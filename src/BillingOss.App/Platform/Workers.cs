using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Platform;

public sealed record JobResult(bool Finished, TimeSpan? After = null)
{
    public static JobResult Done { get; } = new(true);

    public static JobResult PollLater(TimeSpan after) => new(false, after);
}

public interface IJobHandler
{
    string Kind { get; }

    Task<JobResult> Handle(ClaimedJob job, CancellationToken ct);
}

/// <summary>Configuración de los trabajadores (sección Workers). Ver docs/alta-disponibilidad/configuracion.md.</summary>
public sealed class WorkerOptions
{
    /// <summary>
    /// Lease de un trabajo tomado, medido con el reloj de la base. El latido lo renueva cada tercio, y si pasan dos
    /// tercios sin poder renovarlo el trabajo se corta. Es lo que tarda otro nodo en retomar lo de un nodo muerto.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Cuánto se guardan los trabajos hechos. Los muertos quedan hasta que los resuelva el staff.</summary>
    public TimeSpan KeepDone { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Cuánto se guarda el cuerpo de los avisos de pasarela ya procesados. Después la fila queda, con su estado y sus
    /// fechas, y el cuerpo se vacía: es lo que crece sin tope. Los avisos sin procesar conservan el cuerpo.
    /// </summary>
    public TimeSpan KeepWebhookBodies { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Cuánto se guardan los eventos verificados de las pasarelas, que son la unicidad de los avisos. Un aviso que
    /// se repita después de este plazo se vuelve a procesar, y el pago único por canal e ID externo lo absorbe.
    /// </summary>
    public TimeSpan KeepGatewayEvents { get; set; } = TimeSpan.FromDays(400);

    /// <summary>
    /// Al apagar, cuánto pueden seguir los trabajos en curso antes de cortarlos y devolverlos a la cola. Va por debajo
    /// de Shutdown:Timeout, que a su vez va por debajo del plazo del orquestador para matar el proceso.
    /// </summary>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Corre los trabajos de la cola, cada uno fuera de cualquier transacción y con su propio scope. Mientras corre, un
/// latido extiende el lease. Una falla pasajera de la base (una conmutación) devuelve el trabajo a la cola sin gastar
/// un intento. Al apagar no toma nada nuevo, da ShutdownGrace a lo que corre y devuelve lo que tuvo que cortar.
/// </summary>
public sealed class JobRunner(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, WriteGate gate, ILogger<JobRunner> logger) : BackgroundService
{
    /// <summary>Espera antes de reintentar un trabajo que falló por una conmutación de la base.</summary>
    public static TimeSpan TransientRetry { get; } = TimeSpan.FromSeconds(5);

    private readonly string worker = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Los tipos que este binario sabe correr. Un nodo con una versión vieja no toma tipos nuevos.</summary>
    private string[]? kinds;

    /// <summary>Corre trabajos hasta que la cola queda vacía. Devuelve cuántos corrió.</summary>
    public async Task<int> RunUntilIdle(CancellationToken ct)
    {
        var count = 0;
        while (await RunOne(ct, ct).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var abort = new CancellationTokenSource();
        await using var onStop = stoppingToken.Register(() => abort.CancelAfter(options.Value.ShutdownGrace));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOne(stoppingToken, abort.Token).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (Failover.IsTransient(ex))
                {
                    Failover.Recover(ex);
                    logger.LogWarning("La base no responde o está conmutando: {Error}", ex.Message);
                }
                else
                {
                    logger.LogError(ex, "Falla del corredor de trabajos");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> RunOne(CancellationToken stopping, CancellationToken abort)
    {
        // Con el freno cerrado (sin réplica sincrónica) no se toma nada: lo que se escribiera quedaría confirmado solo en la primaria.
        if (stopping.IsCancellationRequested || !gate.Open)
        {
            return false;
        }

        ClaimedJob? job;
        await using (var claim = scopes.CreateAsyncScope())
        {
            kinds ??= [.. claim.ServiceProvider.GetServices<IJobHandler>().Select(h => h.Kind).Distinct()];

            // Sin token: un UPDATE cortado después del commit dejaría el trabajo tomado sin que nadie lo sepa hasta que venza.
            job = await claim.ServiceProvider.GetRequiredService<JobQueue>().Claim(worker, kinds, CancellationToken.None).ConfigureAwait(false);
        }

        if (job is null)
        {
            return false;
        }

        // Un span por trabajo, hijo de la traza que lo encoló, la duración por tipo y resultado (D-15), y el actor que
        // lo pidió, así lo que el trabajo haga en los módulos queda auditado a su nombre.
        ActivityContext.TryParse(job.TraceParent, null, out var parent);
        using var activity = Telemetry.Source.StartActivity($"job {job.Kind}", ActivityKind.Consumer, parent);
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("job.kind", job.Kind);
        activity?.SetTag("job.attempt", job.Attempts);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["JobKind"] = job.Kind });
        Actor.Current = job.Actor ?? Actor.Node;
        var started = Stopwatch.GetTimestamp();
        var outcome = "cut";
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(abort);
        using var beat = new CancellationTokenSource();
        var heartbeat = Heartbeat(job, lost, beat.Token);
        Func<JobQueue, Task<bool>> close;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetServices<IJobHandler>().SingleOrDefault(h => h.Kind == job.Kind)
                ?? throw new InvalidOperationException($"No hay manejador para {job.Kind}.");
            var result = await JobFence.Run(job, () => handler.Handle(job, lost.Token)).ConfigureAwait(false);
            close = result.Finished ? q => q.Complete(job) : q => q.Reschedule(job, result.After ?? TimeSpan.FromSeconds(15));
            outcome = result.Finished ? "done" : "later";
        }
        catch (Exception ex) when (lost.IsCancellationRequested && ex is OperationCanceledException || Failover.Find<LeaseLostException>(ex) is not null)
        {
            // Apagado o lease perdido: vuelve a la cola ya y sin gastar el intento. Si el lease es de otro, la cerca no
            // dejó guardar nada y el cierre tampoco toca la fila.
            logger.LogInformation("Trabajo {Id} ({Kind}) cortado: vuelve a la cola.", job.Id, job.Kind);
            close = q => q.Release(job);
        }
        catch (Exception ex) when (Failover.IsTransient(ex))
        {
            // Conmutación de la base: no es culpa del trabajo. Vuelve en unos segundos sin gastar un intento, y repetirlo
            // es seguro porque cada manejador es idempotente (claves únicas, relectura bajo lock y consulta al fisco).
            Failover.Recover(ex);
            logger.LogWarning("Trabajo {Id} ({Kind}) cortado por la base: {Error}", job.Id, job.Kind, ex.Message);
            close = q => q.Reschedule(job, TransientRetry, $"Falla pasajera de la base: {ex.Message}");
        }
        catch (Exception ex)
        {
            outcome = ex is PermanentFailureException || job.Attempts >= job.MaxAttempts ? "dead" : "retry";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning("Trabajo {Id} ({Kind}) falló: {Error}", job.Id, job.Kind, ex.Message);
            close = q => q.Fail(job, ex.Message, (ex as RetryLaterException)?.After, permanent: ex is PermanentFailureException);
        }
        finally
        {
            await beat.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        await Close(job, close).ConfigureAwait(false);
        Telemetry.JobDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new("job.kind", job.Kind), new("outcome", outcome));
        return true;
    }

    /// <summary>
    /// Guarda el cierre sin token, aunque el proceso se esté apagando. Si la base está conmutando, reintenta unos
    /// segundos: el cierre lleva la cerca del lease, así que repetirlo no pisa a nadie. Si igual no llega, el lease vence
    /// y otro nodo retoma el trabajo.
    /// </summary>
    private async Task Close(ClaimedJob job, Func<JobQueue, Task<bool>> close)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (!await close(scope.ServiceProvider.GetRequiredService<JobQueue>()).ConfigureAwait(false))
                {
                    logger.LogWarning("Trabajo {Id} ({Kind}): ya no es de esta toma, no se cierra.", job.Id, job.Kind);
                }

                return;
            }
            catch (Exception ex) when (retry < 4 && Failover.IsTransient(ex))
            {
                Failover.Recover(ex);
                await Task.Delay(TimeSpan.FromSeconds(1 << retry)).ConfigureAwait(false);
            }
        }
    }

    private async Task Heartbeat(ClaimedJob job, CancellationTokenSource lost, CancellationToken stop)
    {
        var lease = options.Value.Lease;
        var renewed = Stopwatch.GetTimestamp();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(lease / 3, stop).ConfigureAwait(false);

                // Con la base colgada el latido no espera más que un tercio del lease.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(lease / 3);
                await using var scope = scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<JobQueue>().Extend(job, timeout.Token).ConfigureAwait(false))
                {
                    // Lo tomó otro proceso: se corta el manejador para que no corra dos veces a la vez.
                    logger.LogWarning("Trabajo {Id} ({Kind}): se perdió el lease.", job.Id, job.Kind);
                    await lost.CancelAsync().ConfigureAwait(false);
                    return;
                }

                renewed = Stopwatch.GetTimestamp();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Base caída o conmutando: se reintenta en el próximo latido mientras el lease tenga margen. Si no se
                // pudo renovar en dos tercios del lease, otro nodo puede tomarlo pronto y se corta antes.
                Failover.Recover(ex);
                logger.LogWarning("Latido del trabajo {Id} falló: {Error}", job.Id, ex.Message);
                if (Stopwatch.GetElapsedTime(renewed) >= lease * 2 / 3)
                {
                    await lost.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
    }
}

/// <summary>
/// Planificador sin líder (D-07): cada nodo intenta en intervalos cortos, toma un lock de transacción y solo
/// encola los trabajos del día con clave de deduplicación. Dos nodos a la vez no duplican nada.
/// </summary>
public sealed class Scheduler(IServiceScopeFactory scopes, ILogger<Scheduler> logger) : BackgroundService
{
    /// <summary>Clave de entidad de la corrida de facturación y de la mora.</summary>
    public const string BillingEntity = "billing";

    public static async Task Tick(BillingDbContext db, JobQueue jobs, BillingCore core, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(core);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = await Sql.Command(db, "SELECT pg_try_advisory_xact_lock(4242001)", ct).ConfigureAwait(false);
        if (!(bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
        {
            return;
        }

        // Mientras el actualizador aplica una versión no arranca ninguna corrida (sección 7.4 de D-20). La pausa vence sola.
        if (await SchemaGate.SchedulerPaused(db, ct).ConfigureAwait(false))
        {
            return;
        }

        var today = core.Today();
        // Corrida y mora van de a una entre todos los nodos: la misma clave de entidad.
        await jobs.Enqueue(JobKinds.BillingRun, new { date = today }, BillingEntity, $"{JobKinds.BillingRun}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.Dunning, new { date = today }, BillingEntity, $"{JobKinds.Dunning}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        await jobs.Enqueue(RetentionHandler.JobKind, new { date = today }, dedupKey: $"{RetentionHandler.JobKind}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.InvoiceReminders, new { date = today }, dedupKey: $"{JobKinds.InvoiceReminders}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        await Support.SupportServices.Schedule(db, jobs, core.Now(), ct).ConfigureAwait(false);

        // Uso medido: la lectura del día que terminó y el cierre del mes, solo si hay métricas activas.
        await UsageJobs.Schedule(db, jobs, today, core.Now(), ct).ConfigureAwait(false);

        // Afiliados: libera las comisiones que cumplieron la espera y, el día 1, el resumen del mes.
        await jobs.Enqueue(AffiliateJobs.Release, new { date = today }, dedupKey: $"{AffiliateJobs.Release}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        if (today.Day == 1)
        {
            await jobs.Enqueue(AffiliateJobs.Monthly, new { date = today }, dedupKey: $"{AffiliateJobs.Monthly}:{today:yyyy-MM}", ct: ct).ConfigureAwait(false);
        }

        // Débito automático con medio guardado y montos de las suscripciones (sección 4.5 del diseño de cobros). El
        // barrido espera solo la hora de la política: corre una vez por día aunque el planificador pase antes.
        await jobs.Enqueue(JobKinds.AutoChargeScan, new { date = today }, dedupKey: $"{JobKinds.AutoChargeScan}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.SubscriptionAmounts, new { date = today }, dedupKey: $"{JobKinds.SubscriptionAmounts}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.DomainSyncDay, new { date = today }, dedupKey: $"{JobKinds.DomainSyncDay}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.DomainNotices, new { date = today }, dedupKey: $"{JobKinds.DomainNotices}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);

        // Barrido fiscal diario (sección 7.2 del modelo de dominio), solo cuando hay un emisor que emite por módulo.
        if (await db.IssuerProfiles.AnyAsync(i => i.Active && i.ModuleInstanceId != null, ct).ConfigureAwait(false))
        {
            await jobs.Enqueue(JobKinds.FiscalSweep, new { date = today }, Subscriptions.FiscalEntity, $"{JobKinds.FiscalSweep}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        }

        // Vencimiento de los certificados fiscales: avisa a los 30, 15, 7 y 1 día (sección 2.8 del diseño fiscal).
        if (await db.FiscalCredentials.AnyAsync(c => c.Status == Domain.Fiscal.CredentialStatus.Active || c.Status == Domain.Fiscal.CredentialStatus.Ready, ct).ConfigureAwait(false))
        {
            await jobs.Enqueue(JobKinds.FiscalCertificates, new { date = today }, Subscriptions.FiscalEntity, $"{JobKinds.FiscalCertificates}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        }

        // Conciliación de cada pasarela que concilia, una vez por hora. Una instancia que el asistente creó y nunca probó
        // todavía no tiene credenciales: no se le pide nada.
        var hour = core.Now().ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        var gateways = await db.Database.SqlQueryRaw<long>("""
            SELECT i.id AS "Value" FROM platform.module_instances i
            JOIN platform.module_installations m ON m.id = i.installation_id
            LEFT JOIN payments.gateways g ON g.module_instance_id = i.id
            WHERE m.manifest -> 'kinds' @> '["payment"]'::jsonb
              AND m.manifest -> 'capabilities' @> '["payment.reconcile"]'::jsonb
              AND (g.module_instance_id IS NULL OR g.last_test_at IS NOT NULL)
            """).ToListAsync(ct).ConfigureAwait(false);
        foreach (var instance in gateways)
        {
            await jobs.Enqueue(JobKinds.PaymentReconcile, new { instance_id = instance }, $"gateway:{instance}", $"{JobKinds.PaymentReconcile}:{instance}:{hour}", ct: ct).ConfigureAwait(false);
        }

        // Cotizaciones del Banco Nación, una vez por hora: el último valor de cada día queda como su cierre.
        if (core.Options.ExchangeRates.Bna)
        {
            await jobs.Enqueue(JobKinds.ExchangeRates, new { hour }, ExchangeRateService.Entity, $"{JobKinds.ExchangeRates}:{hour}", maxAttempts: 3, ct: ct).ConfigureAwait(false);
        }

        // Los intentos abiertos con checkout, cada quince minutos: por si el aviso de la pasarela no llegó a tiempo.
        if (gateways.Count > 0)
        {
            var slot = core.Now().ToUnixTimeSeconds() / (long)CheckoutSweepHandler.Every.TotalSeconds;
            await jobs.Enqueue(JobKinds.CheckoutSweep, new { }, JobKinds.CheckoutSweep, $"{JobKinds.CheckoutSweep}:{slot.ToString(CultureInfo.InvariantCulture)}", ct: ct).ConfigureAwait(false);
        }

        // Reconciliación diaria del estado de los servicios (sección 5.1 del modelo) con cada instancia que lo informa.
        // El módulo manual no: contesta lo que confirmó el staff y no mira ningún sistema externo.
        var provisioning = await db.Database.SqlQuery<long>($"""
            SELECT i.id AS "Value" FROM platform.module_instances i
            JOIN platform.module_installations m ON m.id = i.installation_id
            WHERE m.manifest -> 'capabilities' @> '["provisioning.status"]'::jsonb AND m.base_url <> {ManualModule.Address}
            """).ToListAsync(ct).ConfigureAwait(false);
        foreach (var instance in provisioning)
        {
            await jobs.Enqueue(JobKinds.ServiceReconcile, new { instance_id = instance }, $"module:{instance}", $"{JobKinds.ServiceReconcile}:{instance}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        }

        // Línea de tiempo de la base: una nueva es una promoción o una restauración, y lo que el fisco, las pasarelas y
        // los módulos hicieron desde la última marca puede faltar en la base. La conciliación corre sola desde esa
        // hora, con la clave fiscal para no cruzarse con una emisión (D-14).
        var now = core.Now();
        var timeline = await UseCases.ReconcileService.CurrentTimeline(db, ct).ConfigureAwait(false);
        var mark = await db.Timeline.AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (mark is not null && mark.TimelineId != timeline)
        {
            await jobs.Enqueue(JobKinds.Reconcile, new { since = mark.SeenAt, timeline }, Subscriptions.FiscalEntity, $"{JobKinds.Reconcile}:{timeline}", ct: ct).ConfigureAwait(false);
        }

        await UseCases.ReconcileService.MarkTimeline(db, timeline, now, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                await Tick(sp.GetRequiredService<BillingDbContext>(), sp.GetRequiredService<JobQueue>(), sp.GetRequiredService<BillingCore>(), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falla del planificador");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }
}

public sealed class BillingRunHandler(BillingRunService run, AuditLog audit) : IJobHandler
{
    public string Kind => JobKinds.BillingRun;

    /// <summary>
    /// Reparte la facturación del día en un trabajo por cuenta y queda en la auditoría con su duración: de ahí la
    /// leen las métricas y Zabbix.
    /// </summary>
    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var started = Stopwatch.GetTimestamp();
        var accounts = await run.Distribute(ct).ConfigureAwait(false);
        await audit.Record(JobKinds.BillingRun, $"job:{job.Id}", new { seconds = Stopwatch.GetElapsedTime(started).TotalSeconds, accounts }, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>La facturación de una cuenta, repartida por billing.run. Si falla, reintenta y queda muerta a la vista del staff.</summary>
public sealed class BillingAccountHandler(BillingRunService run) : IJobHandler
{
    public string Kind => JobKinds.BillingAccount;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var accountId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("account_id").GetInt64();
        await run.RunAccount(accountId, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class DunningHandler(DunningService dunning) : IJobHandler
{
    public string Kind => JobKinds.Dunning;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        await dunning.Run(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}
