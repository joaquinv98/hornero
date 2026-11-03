using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Services;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.UseCases;

/// <summary>Resultado de pedirle una operación a un módulo.</summary>
public sealed record ModuleOutcome(bool Completed, string? ObservedStatus = null, string? ExternalIds = null, TimeSpan? PollAfter = null);

/// <summary>Quien habla con el módulo de un servicio. La implementación real usa el protocolo v0 (D-08).</summary>
public interface IServiceModules
{
    Task<ModuleOutcome> Execute(Service service, ServiceOperation op, CancellationToken ct);
}

/// <summary>
/// Trabajo de operación de un servicio: crear, suspender, reactivar, dar de baja o correr una acción del módulo. Los
/// productos sin módulo se resuelven en el núcleo, y los que tienen módulo y no tienen instancia fallan a la vista
/// del staff. Con módulo, el estado pasa recién cuando el módulo confirma (sección 5.1 del modelo). El módulo manual
/// también confirma, cuando el staff cierra la tarea.
/// </summary>
public sealed class ServiceOperationHandler(BillingDbContext db, IServiceModules modules, BillingCore core, JobQueue jobs, StaffTaskService tasks, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.ServiceOperation;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var op = JsonSerializer.Deserialize<ServiceOperation>(job.Payload)!;
        Service service;
        await using (var check = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            // Con el servicio bloqueado, como el pago que avanza lo pagado: la revisión ve el pago ya confirmado, y
            // uno que llega después lo ve el cierre de la operación.
            await Sql.LockRows(db, "services.services", [op.ServiceId], ct).ConfigureAwait(false);
            service = await db.Services.SingleAsync(s => s.Id == op.ServiceId, ct).ConfigureAwait(false);
            if (service.Generation != op.Generation)
            {
                return JobResult.Done; // La intención cambió mientras esperaba: este pedido ya no vale.
            }

            // Lo que pidió la mora se revisa antes de mandarlo al módulo: si el cliente pagó mientras esperaba, ya no
            // va. Si un intento anterior de suspender pudo haber llegado al módulo (resultado incierto o proceso
            // caído), se pide reactivar por las dudas, salvo que mientras tanto el staff haya suspendido con motivo
            // propio. Una baja no se deshace, así que solo se deja de mandar.
            if (op.Dunning && service.PendingOperation is null && await Due(service, ct).ConfigureAwait(false) is var due && due != op.Operation)
            {
                if (due is null && op.Operation == "suspend" && job.Attempts > 1 && service.SuspendReason is null)
                {
                    await core.EnqueueOperation(service, "unsuspend", ct).ConfigureAwait(false);
                }

                await check.CommitAsync(ct).ConfigureAwait(false);
                return JobResult.Done;
            }

            // Una operación que el estado no admite (suspender lo que todavía no se dio de alta) muere a la vista
            // antes de llegar al módulo y libera la cola del servicio: reintentarla durante horas frenaba el alta
            // que esperaba detrás con la misma clave de entidad.
            if (!ServiceStates.Machine.CanMove(service.Status, Target(op.Operation, service.Status)))
            {
                throw new PermanentFailureException($"El servicio {service.Id} está en {service.Status} y no admite {op.Operation}.");
            }

            await check.CommitAsync(ct).ConfigureAwait(false);
        }

        ModuleOutcome outcome;
        try
        {
            if (service.ModuleInstanceId is null)
            {
                // Sin instancia solo se resuelve en el núcleo lo que no tiene módulo. Un producto con módulo sin
                // instancia (por ejemplo, importado sin vincular) no se da por hecho: el servidor no se enteraría.
                var kind = await db.Products.Where(p => p.Id == service.ProductId).Select(p => p.Kind).SingleAsync(ct).ConfigureAwait(false);
                outcome = kind == ProductKind.Provisioned
                    ? throw new PermanentFailureException($"El servicio {service.Id} es de un producto con módulo y está sin instancia: {op.Operation} no se da por hecho.")
                    : new ModuleOutcome(true);
            }
            else
            {
                outcome = await modules.Execute(service, op, ct).ConfigureAwait(false);
            }
        }
        catch (PermanentFailureException ex)
        {
            // Error definitivo (el módulo lo dejó como tarea de revisión): al crear, el servicio queda fallido y el staff
            // decide (sección 5.1 del modelo). Una baja que el módulo no pudo hacer deja el servicio marcado para la
            // baja, así no renueva más y la mora la vuelve a pedir cada día hasta que el staff la destrabe. Una que el
            // staff rechazó en el módulo manual no: esa ya es su decisión.
            if (op.Operation == "create")
            {
                service.MoveTo(ServiceStatus.Failed, time.GetUtcNow());
                service.PendingOperation = null;
            }
            else if (op.Operation == "terminate" && service.ModuleInstanceId is not null && !ex.Reviewed)
            {
                service.CancelAt ??= core.Today();
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw;
        }
        catch (RetryLaterException)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (!outcome.Completed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return JobResult.PollLater(outcome.PollAfter ?? TimeSpan.FromSeconds(15));
        }

        // Se cierra con el servicio bloqueado, como el pago que avanza lo pagado: así uno de los dos ve al otro. Una
        // suspensión que el staff hizo propia mientras tanto (SuspendReason) no se levanta por el pago.
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", [service.Id], ct).ConfigureAwait(false);
        var current = await db.Services.AsNoTracking().SingleAsync(s => s.Id == service.Id, ct).ConfigureAwait(false);
        var paidMeanwhile = op.Dunning && op.Operation == "suspend" && current.SuspendReason is null && await Due(current, ct).ConfigureAwait(false) is null;

        var now = time.GetUtcNow();
        var target = Target(op.Operation, service.Status);
        if (target != service.Status)
        {
            service.MoveTo(target, now);
        }

        if (target == ServiceStatus.Active)
        {
            // Activo otra vez: el motivo de una suspensión del staff ya no vale.
            service.SuspendReason = null;
        }

        service.ObservedStatus = outcome.ObservedStatus ?? service.ObservedStatus;
        service.ExternalIds = outcome.ExternalIds ?? service.ExternalIds;
        service.NextGeneration();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (op.Operation == "create")
        {
            await core.ServiceProvisioned(service, ct).ConfigureAwait(false);
        }

        // Una operación que terminó bien cierra la revisión que dejó la que había fallado.
        await tasks.ResolveReviews($"service:{service.Id}", op.Operation, ct).ConfigureAwait(false);

        // El cliente pagó mientras el módulo suspendía: se reactiva, y la suspensión no se le avisa.
        if (paidMeanwhile)
        {
            await core.EnqueueOperation(service, "unsuspend", ct).ConfigureAwait(false);
        }

        // Si el padre se da de baja, sus complementos también (sección 5 del modelo). Los que no llegaron a aprovisionarse
        // se cancelan.
        if (op.Operation == "terminate")
        {
            foreach (var child in await db.Services.Where(s => s.ParentServiceId == service.Id && s.Status != ServiceStatus.Terminated && s.Status != ServiceStatus.Cancelled).ToListAsync(ct).ConfigureAwait(false))
            {
                if (child.Status is ServiceStatus.Pending or ServiceStatus.Failed)
                {
                    child.MoveTo(child.Status == ServiceStatus.Pending ? ServiceStatus.Cancelled : child.Status, now);
                }
                else
                {
                    await core.EnqueueOperation(child, "terminate", ct).ConfigureAwait(false);
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // El evento va en la misma transacción que el cambio. La generación nueva impide publicarlo dos veces.
        var evt = op.Operation switch
        {
            "create" => Events.ServiceActivated,
            "unsuspend" => Events.ServiceReactivated,
            "suspend" when !paidMeanwhile => Events.ServiceSuspended,
            "terminate" => Events.ServiceTerminated,
            _ => null,
        };
        if (evt is not null)
        {
            await jobs.Publish(evt, new { service_id = service.Id, generation = service.Generation }, ct: ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    /// <summary>El estado al que lleva cada operación (sección 5.1 del modelo). Una acción propia del módulo no lo cambia.</summary>
    public static ServiceStatus Target(string operation, ServiceStatus current) => operation switch
    {
        "create" or "unsuspend" => ServiceStatus.Active,
        "suspend" => ServiceStatus.Suspended,
        "terminate" => ServiceStatus.Terminated,
        _ => current,
    };

    /// <summary>Lo que le toca hoy por mora, con la misma regla con que la mora lo encoló.</summary>
    private async Task<string?> Due(Service service, CancellationToken ct)
    {
        var days = await db.Products.Where(p => p.Id == service.ProductId).Select(p => p.TerminationDays).SingleAsync(ct).ConfigureAwait(false);
        var balance = await db.CreditBalances.Where(b => b.AccountId == service.AccountId && b.Currency == service.Currency).SumAsync(b => b.Balance, ct).ConfigureAwait(false);
        return DunningService.DueOperation(service, core.Today(), core.Options.GraceDays, days ?? core.Options.TerminationDays, balance, await core.PeriodTotal(service, ct).ConfigureAwait(false));
    }
}

/// <summary>
/// Cuando una factura queda pagada: aprovisiona lo pendiente, reactiva lo suspendido por mora y pide al registrar
/// las renovaciones de dominio pagas. Lo pagado de cada servicio ya avanzó con el pago (<see cref="BillingCore.InvoicePaid"/>).
/// </summary>
public sealed class ActivateServicesOfInvoiceHandler(BillingDbContext db, BillingCore core, JobQueue jobs, StaffTaskService tasks) : IJobHandler
{
    public string Kind => JobKinds.ActivateServicesOfInvoice;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var documentId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("document_id").GetInt64();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Si antes de correr se revirtió el pago (reembolso o contracargo), la factura ya no está pagada.
        if (await db.Documents.AnyAsync(d => d.Id == documentId && d.Total - d.Credited - d.Allocated > 0, ct).ConfigureAwait(false))
        {
            return JobResult.Done;
        }

        // Pagar el excedente de uso no aprovisiona ni reactiva: solo la renovación pagada pone al día al servicio.
        var lines = await db.DocumentLines.Where(l => l.DocumentId == documentId && l.ServiceId != null && l.Kind != LineKind.Usage)
            .Select(l => new { ServiceId = l.ServiceId!.Value, l.Kind }).ToListAsync(ct).ConfigureAwait(false);
        var serviceIds = lines.Select(l => l.ServiceId).Distinct().ToList();

        // Un producto que acepta el staff no se aprovisiona con el pago: espera que acepte el pedido.
        var manual = await db.Services.Where(s => serviceIds.Contains(s.Id))
            .Join(db.Products, s => s.ProductId, p => p.Id, (s, p) => new { s.Id, p.Activation })
            .Where(x => x.Activation == ProductActivation.Manual).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var service in await db.Services.Where(s => serviceIds.Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false))
        {
            // La baja por mora que el módulo no pudo hacer se levanta con el pago: vuelve a renovar y la revisión se cierra.
            if (service.CancelAt is not null && await tasks.LiftBlockedTermination($"service:{service.Id}", ct).ConfigureAwait(false))
            {
                service.CancelAt = null;
            }

            if (service.Status == ServiceStatus.Pending && !manual.Contains(service.Id))
            {
                await core.StartProvisioning(service, ct).ConfigureAwait(false);
            }
            else if (service.Status == ServiceStatus.Suspended && service.SuspendReason is null)
            {
                // Solo la suspensión por mora se levanta con el pago: una con motivo propio la levanta el staff.
                await core.EnqueueOperation(service, "unsuspend", ct).ConfigureAwait(false);
            }
        }

        // Un dominio pedido que se pagó va al registrar. Cada renovación de dominio paga avanza lo pago y se le pide al registrar,
        // también en uno vencido que pagó tarde: vuelve a activo si el registro todavía la acepta. Una opción contratada se aplica.
        // La renovación que una nota de crédito sacó de la factura no la pagó nadie: no avanza ni se pide.
        // Las filas se bloquean como en la corrida: este trabajo no lleva clave de entidad y otro nodo puede estar tocando el dominio.
        var credited = await DomainJobs.CreditedDomains(db, documentId, ct).ConfigureAwait(false);
        var domainLines = await db.DocumentLines.Where(l => l.DocumentId == documentId && l.DomainId != null)
            .Select(l => new { DomainId = l.DomainId!.Value, l.Kind, l.DedupKey }).ToListAsync(ct).ConfigureAwait(false);
        domainLines.RemoveAll(l => l.Kind == LineKind.Renewal && credited.Contains(l.DomainId));
        var domainIds = domainLines.Select(l => l.DomainId).Distinct().ToList();
        await Sql.LockRows(db, "domains.domains", domainIds, ct).ConfigureAwait(false);
        foreach (var domain in await db.Set<RegisteredDomain>().Where(d => domainIds.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false))
        {
            foreach (var contract in domainLines.Where(l => l.DomainId == domain.Id).Select(l => DomainJobs.OptionOperation(l.DedupKey)).OfType<long>())
            {
                await DomainJobs.Enqueue(jobs, domain.Id, contract, ct).ConfigureAwait(false);
            }

            if (domain.Status == DomainStatus.Pending)
            {
                await DomainJobs.ReleaseWaiting(db, jobs, domain.Id, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var _ in domainLines.Where(l => l.DomainId == domain.Id && l.Kind == LineKind.Renewal))
            {
                if (domain.RenewalPaid(core.Today()) is { } renewed)
                {
                    await DomainJobs.RequestRenewal(db, jobs, domain, renewed, core.Now(), ct).ConfigureAwait(false);
                }
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>
/// Reconciliación diaria de una instancia (sección 5.1 del modelo): pregunta get_status por cada servicio activo o
/// suspendido sin una operación larga en curso, guarda lo observado y funde los IDs externos. Si lo observado
/// contradice al estado del núcleo deja una revisión para el staff y no manda operaciones sola: lo que el panel ve
/// puede ser una decisión de una persona. Consulta de a uno y una vez por día, para no saturar al sistema externo.
/// </summary>
public sealed class ServiceReconcileHandler(BillingDbContext db, ProtocolServiceModules modules, StaffTaskService tasks, ILogger<ServiceReconcileHandler> logger) : IJobHandler
{
    /// <summary>Código de la revisión que deja un estado observado distinto del que tiene el núcleo.</summary>
    public const string StateMismatch = "state_mismatch";

    public string Kind => JobKinds.ServiceReconcile;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var instanceId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("instance_id").GetInt64();
        var ids = await db.Services.AsNoTracking()
            .Where(s => s.ModuleInstanceId == instanceId && (s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended) && s.PendingOperation == null)
            .OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        var errors = await Batch.Each(ids, id => Reconcile(instanceId, id, ct), logger, "la reconciliación del servicio").ConfigureAwait(false);
        if (errors.Count > 0)
        {
            // Lo que falló se vuelve a preguntar en una hora, sin insistir: la consulta no cambia nada en el sistema externo.
            throw new RetryLaterException(string.Create(CultureInfo.InvariantCulture, $"La reconciliación de la instancia {instanceId} terminó con {errors.Count} fallas."), TimeSpan.FromHours(1));
        }

        return JobResult.Done;
    }

    private async Task Reconcile(long instanceId, long serviceId, CancellationToken ct)
    {
        var snapshot = await db.Services.AsNoTracking().SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
        var result = await modules.Query(snapshot, "get_status", ct).ConfigureAwait(false);
        var observed = result["observed_state"]?.GetValue<string>() ?? "unknown";

        // Se aplica con el servicio bloqueado y releído: si una operación lo cambió mientras se consultaba, lo observado ya no dice nada.
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", [serviceId], ct).ConfigureAwait(false);
        var service = await db.Services.SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
        if (service.PendingOperation is not null || service.Status is not (ServiceStatus.Active or ServiceStatus.Suspended))
        {
            return;
        }

        service.ObservedStatus = observed;
        if (result["external_ids"] is JsonObject ids)
        {
            service.ExternalIds = ProtocolServiceModules.Merge(service.ExternalIds, ids);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var expected = service.Status == ServiceStatus.Active ? "active" : "suspended";
        var subject = string.Create(CultureInfo.InvariantCulture, $"service:{service.Id}");
        if (observed == expected)
        {
            await tasks.ResolveReviews(subject, "get_status", ct).ConfigureAwait(false);
        }
        else if (observed != "unknown")
        {
            var error = new ModuleError(StateMismatch, $"El módulo ve el servicio {observed} y el núcleo lo tiene {expected}.", false, Details: new JsonObject { ["observed_state"] = observed, ["status"] = expected });
            var key = string.Create(CultureInfo.InvariantCulture, $"svc-{service.Id}-get_status-{service.Generation}");
            await tasks.OpenReview(instanceId, "get_status", key, service, [], error, false, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Lo que el staff, las API y el portal le piden a un servicio por fuera de la mora: suspender con motivo propio,
/// reactivar, dar de baja ya o a fin de período, eximirlo de la suspensión automática, reintentar un alta fallida y
/// correr las acciones del manifiesto del módulo. Cada cambio se decide con el servicio bloqueado, como las
/// operaciones de la mora, y la operación al módulo se encola en la misma transacción. Cada pedido es una intención
/// nueva (sección 6 del protocolo): si el servicio no tiene un trabajo en curso, sube la generación, así un reintento
/// después de un error definitivo no repite la clave que el módulo ya rechazó.
/// </summary>
public sealed class ServiceAdmin(BillingDbContext db, BillingCore core, JobQueue jobs, TimeProvider time, SecretBox? box = null)
{
    /// <summary>Motivo que queda cuando el staff suspende sin dar uno.</summary>
    public const string ManualSuspension = "Suspensión manual";

    /// <summary>
    /// Pide una operación del ciclo de vida por su nombre, como la API: suspend con su motivo, unsuspend, terminate y
    /// create, que solo reintenta un alta fallida. <paramref name="input"/> va en el input del sobre, por ejemplo
    /// <c>{"force": true}</c> en una baja que el módulo no hace sola.
    /// </summary>
    public Task Request(long serviceId, string operation, JsonObject? input = null, string? reason = null, CancellationToken ct = default) => operation switch
    {
        "suspend" => Suspend(serviceId, reason, input, ct),
        "unsuspend" => Unsuspend(serviceId, input, ct),
        "terminate" => Terminate(serviceId, input, ct),
        "create" => RetryCreate(serviceId, input, ct),
        _ => throw new KeyNotFoundException($"Operación desconocida: {operation}."),
    };

    /// <summary>
    /// Suspensión propia: queda el motivo y un pago no la levanta, porque solo la de mora se levanta sola. Un servicio
    /// ya suspendido por mora pasa a suspensión propia sin volver a llamar al módulo.
    /// </summary>
    public async Task Suspend(long serviceId, string? reason, JsonObject? input = null, CancellationToken ct = default)
    {
        var kept = string.IsNullOrWhiteSpace(reason) ? ManualSuspension : reason.Trim();
        if (kept.Length > 255)
        {
            throw new ValidationException("El motivo de la suspensión tiene como máximo 255 caracteres.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var service = await Lock(serviceId, "suspender", [ServiceStatus.Active, ServiceStatus.Suspended], ct).ConfigureAwait(false);
        service.SuspendReason = kept;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (service.Status == ServiceStatus.Active)
        {
            await Enqueue(service, "suspend", input, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Levanta la suspensión, propia o por mora. Si la mora sigue, la vuelve a pedir en su próxima corrida.</summary>
    public async Task Unsuspend(long serviceId, JsonObject? input = null, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var service = await Lock(serviceId, "reactivar", [ServiceStatus.Suspended], ct).ConfigureAwait(false);
        service.SuspendReason = null;
        await Enqueue(service, "unsuspend", input, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Baja inmediata.</summary>
    public async Task Terminate(long serviceId, JsonObject? input = null, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var service = await Lock(serviceId, "dar de baja", [ServiceStatus.Active, ServiceStatus.Suspended], ct).ConfigureAwait(false);
        await Enqueue(service, "terminate", input, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Aprovisiona ya un servicio pendiente, aunque no esté pago, o reintenta uno fallido como <see cref="RetryCreate"/>.
    /// Sobre cualquier otro estado no hace nada y lo dice.
    /// </summary>
    public async Task Provision(long serviceId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var service = await Lock(serviceId, "aprovisionar", [ServiceStatus.Pending, ServiceStatus.Failed], ct).ConfigureAwait(false);
        if (service.Status == ServiceStatus.Pending)
        {
            await core.StartProvisioning(service, ct).ConfigureAwait(false);
        }
        else
        {
            service.MoveTo(ServiceStatus.Provisioning, time.GetUtcNow());
            await Enqueue(service, "create", null, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>El nombre que ve el cliente, por ejemplo el dominio o el hostname. Vacío lo borra.</summary>
    public async Task Rename(long serviceId, string? label, CancellationToken ct = default)
    {
        var value = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (value is { Length: > 255 })
        {
            throw new ValidationException("El nombre del servicio va hasta 255 caracteres.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", [serviceId], ct).ConfigureAwait(false);
        var service = await db.Services.SingleOrDefaultAsync(s => s.Id == serviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el servicio {serviceId}.");
        service.Label = value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Vuelve a pedir el alta de un servicio fallido. Sobre cualquier otro estado no se reintenta.</summary>
    public async Task RetryCreate(long serviceId, JsonObject? input = null, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var service = await Lock(serviceId, "reintentar el alta", [ServiceStatus.Failed], ct).ConfigureAwait(false);
        service.MoveTo(ServiceStatus.Provisioning, time.GetUtcNow());
        await Enqueue(service, "create", input, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pide una acción del manifiesto aceptado (sección 9.1 del protocolo): la acción tiene que existir y
    /// <paramref name="audience"/> (staff o client) estar en su audience. Las credenciales que la acción rota se
    /// generan acá y se guardan cifradas antes de llamar (sección 6): un reintento manda las mismas.
    /// </summary>
    public async Task RunAction(long serviceId, string action, JsonObject? parameters, string audience, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", [serviceId], ct).ConfigureAwait(false);
        var service = await db.Services.SingleOrDefaultAsync(s => s.Id == serviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el servicio {serviceId}.");
        var manifest = await Manifest(service, ct).ConfigureAwait(false);
        var declared = ProtocolServiceModules.Action(manifest, action)
            ?? throw new KeyNotFoundException($"El módulo del servicio {serviceId} no tiene la acción {action}.");
        if (!(declared["audience"] as JsonArray ?? []).Any(a => a?.GetValue<string>() == audience))
        {
            throw new ValidationException($"La acción {action} no está disponible para {audience}.");
        }

        foreach (var name in (declared["rotates_credentials"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>())
        {
            await ProtocolServiceModules.StoreSecret(db, box ?? throw new ValidationException("No hay llave para guardar secretos."), service.Id, name, Passwords.New(), ct).ConfigureAwait(false);
        }

        var input = new JsonObject { ["action"] = action, ["params"] = parameters?.DeepClone() ?? new JsonObject() };
        await Enqueue(service, "run_action", input, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Baja a fin de período: desde esa fecha no se factura y ese día la mora lo da de baja. Sin fecha es el próximo
    /// vencimiento, u hoy si el servicio no renueva. Devuelve la fecha que quedó.
    /// </summary>
    public async Task<DateOnly> RequestCancellation(long serviceId, DateOnly? at, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var service = await Lock(serviceId, "dar de baja", [ServiceStatus.Active, ServiceStatus.Suspended], ct).ConfigureAwait(false);
        var today = core.Today();
        var date = at ?? service.NextDueDate ?? today;
        if (date < today)
        {
            throw new ValidationException("La fecha de baja no puede ser anterior a hoy.");
        }

        service.CancelAt = date;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return date;
    }

    /// <summary>
    /// Anula la baja pedida a fin de período: el servicio vuelve a renovar. Si la fecha ya llegó y la mora encoló la
    /// baja, la generación nueva la deja sin efecto.
    /// </summary>
    public async Task KeepService(long serviceId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var service = await Lock(serviceId, "conservar", [ServiceStatus.Active, ServiceStatus.Suspended], ct).ConfigureAwait(false);
        if (service.CancelAt is { } cancelAt)
        {
            if (cancelAt <= core.Today())
            {
                service.NextGeneration();
            }

            service.CancelAt = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Exime al servicio de la suspensión automática por mora, o lo vuelve a incluir. La baja por mora sigue corriendo.</summary>
    public async Task SetNoAutoSuspend(long serviceId, bool exempt, CancellationToken ct = default)
    {
        var service = await db.Services.SingleOrDefaultAsync(s => s.Id == serviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el servicio {serviceId}.");
        service.NoAutoSuspend = exempt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>El servicio bloqueado, si está en alguno de los estados que admiten la acción.</summary>
    private async Task<Service> Lock(long serviceId, string action, ServiceStatus[] allowed, CancellationToken ct)
    {
        await Sql.LockRows(db, "services.services", [serviceId], ct).ConfigureAwait(false);
        var service = await db.Services.SingleOrDefaultAsync(s => s.Id == serviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el servicio {serviceId}.");
        return allowed.Contains(service.Status)
            ? service
            : throw new InvalidOperationException($"El servicio {serviceId} está en estado {service.Status}: no se puede {action}.");
    }

    private async Task<JsonObject> Manifest(Service service, CancellationToken ct)
    {
        var manifest = service.ModuleInstanceId is { } instanceId
            ? await db.ModuleInstances.Where(i => i.Id == instanceId)
                .Join(db.ModuleInstallations, i => i.InstallationId, m => m.Id, (_, m) => m.Manifest)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        return manifest is null
            ? throw new KeyNotFoundException($"El servicio {service.Id} no tiene módulo.")
            : JsonNode.Parse(manifest) as JsonObject ?? [];
    }

    /// <summary>Sin un trabajo del servicio en curso, el pedido es una intención nueva y sube la generación de la clave.</summary>
    private async Task Enqueue(Service service, string operation, JsonObject? input, CancellationToken ct)
    {
        var subject = string.Create(CultureInfo.InvariantCulture, $"service:{service.Id}");
        var live = await db.Jobs.AnyAsync(j => j.EntityKey == subject && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct).ConfigureAwait(false);
        if (!live)
        {
            service.NextGeneration();
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var what = operation == "run_action" ? $"{operation}:{input!["action"]!.GetValue<string>()}" : operation;
        await jobs.Enqueue(
            JobKinds.ServiceOperation,
            new ServiceOperation(service.Id, operation, service.Generation, Input: input?.ToJsonString()),
            subject,
            string.Create(CultureInfo.InvariantCulture, $"{subject}:{what}:{service.Generation}"),
            ct: ct).ConfigureAwait(false);
    }
}

/// <summary>Sin módulos registrados todavía: cualquier servicio con módulo queda esperando.</summary>
public sealed class NoServiceModules : IServiceModules
{
    public Task<ModuleOutcome> Execute(Service service, ServiceOperation op, CancellationToken ct) =>
        throw new InvalidOperationException("No hay cliente de módulos configurado.");
}
