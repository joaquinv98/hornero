using System.Net;
using System.Text.Json;
using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Platform;

/// <summary>
/// Un trabajo tomado de la cola, con su lease. LockedBy es la cerca: cambia en cada toma, y solo quien tomó este
/// intento puede cerrarlo, extenderlo o devolverlo. TraceParent es la traza de quien lo encoló y Actor quién lo pidió
/// (D-15): el trabajo los continúa.
/// </summary>
public sealed record ClaimedJob(long Id, string Kind, string Payload, int Attempts, int MaxAttempts, string LockedBy = "", string? TraceParent = null, Actor? Actor = null);

/// <summary>
/// Cola de trabajos en PostgreSQL (D-07). Encolar va en la misma transacción que el cambio. Tomar usa
/// SKIP LOCKED con un lease, y los trabajos de una misma entidad salen en orden y de a uno. El lease se mide con el
/// reloj de la base: un nodo con el reloj corrido no retoma antes de tiempo ni retiene trabajos ajenos.
/// </summary>
public sealed class JobQueue(BillingDbContext db, TimeProvider time, IOptions<WorkerOptions> options, ILogger<JobQueue> logger)
{
    /// <summary>
    /// Encola un trabajo. Con clave de deduplicación, encolar dos veces lo mismo no duplica mientras el primero esté
    /// pendiente, corriendo o hecho. Si murió, vuelve a la cola con los intentos en cero: una suspensión o una baja que
    /// falló no queda trabada para siempre. La deduplicación dura lo que la retención de los hechos (Workers:KeepDone).
    /// Con más intentos, la espera creciente aguanta caídas largas: 30 intentos son casi un día. Va en la transacción
    /// del contexto, y si no hay abre una propia: así pasa por la cerca del trabajo y por la guarda del COMMIT.
    /// </summary>
    public async Task Enqueue(string kind, object payload, string? entityKey = null, string? dedupKey = null, DateTimeOffset? runAt = null, int maxAttempts = 10, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var json = JsonSerializer.Serialize(payload);
        var at = runAt ?? now;
        var trace = Telemetry.CurrentTraceParent();
        var actor = Actor.Current;
        var actorKind = actor.Kind.ToString();
        var actorIp = actor.Ip?.ToString();
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await using (own)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO platform.jobs (kind, payload, entity_key, dedup_key, status, run_at, attempts, max_attempts, created_at, updated_at, trace_parent, actor_kind, actor, actor_ip)
                VALUES ({kind}, {json}::jsonb, {entityKey}, {dedupKey}, 'Pending', {at}, 0, {maxAttempts}, {now}, {now}, {trace}, {actorKind}, {actor.Id}, {actorIp}::inet)
                ON CONFLICT (dedup_key) WHERE dedup_key IS NOT NULL DO UPDATE SET status = 'Pending', payload = EXCLUDED.payload,
                  run_at = EXCLUDED.run_at, attempts = 0, max_attempts = EXCLUDED.max_attempts, locked_until = NULL, locked_by = NULL, last_error = NULL,
                  updated_at = EXCLUDED.updated_at, trace_parent = EXCLUDED.trace_parent, actor_kind = EXCLUDED.actor_kind, actor = EXCLUDED.actor,
                  actor_ip = EXCLUDED.actor_ip
                  WHERE platform.jobs.status = 'Dead'
                """, ct).ConfigureAwait(false);
            if (own is not null)
            {
                await own.CommitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Evento de dominio: un trabajo por suscriptor, en la misma transacción que el cambio (o en una propia si no
    /// hay). El trabajo lleva el nombre del evento en "event" y, si el suscriptor la usa, la clave de entidad del que
    /// publica. Los webhooks y las extensiones suscriptas reciben su entrega en la misma transacción
    /// (<see cref="PublicEvents.FanOut"/>).
    /// </summary>
    public async Task Publish(string eventName, object payload, string? entityKey = null, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToNode(payload)!.AsObject();
        body["event"] = eventName;
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await using (own)
        {
            foreach (var (kind, keyed) in Subscriptions.For(eventName))
            {
                await Enqueue(kind, body, keyed ? entityKey : null, ct: ct).ConfigureAwait(false);
            }

            await PublicEvents.FanOut(db, eventName, body, time.GetUtcNow(), Telemetry.CurrentTraceParent(), Actor.Current, ct).ConfigureAwait(false);
            if (own is not null)
            {
                await own.CommitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Toma el próximo trabajo. <paramref name="worker"/> identifica al proceso y cada toma le suma un sufijo propio. Con
    /// <paramref name="kinds"/> solo toma esos tipos: un nodo viejo en un despliegue rodante no mata trabajos nuevos.
    /// Un trabajo tomado que no se puede leer vuelve a la cola como intento fallido y se sigue con el próximo.
    /// </summary>
    public async Task<ClaimedJob?> Claim(string worker, IReadOnlyCollection<string>? kinds = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        while (true)
        {
            var (job, unreadable) = await ClaimOne(worker, kinds, ct).ConfigureAwait(false);
            if (unreadable is null)
            {
                return job;
            }

            // El UPDATE ya lo dejó tomado: si quedara así, se retomaría al vencer cada lease con los intentos subiendo y
            // sin verse nunca como falla. Vuelve a la cola con su error y su espera, y muere visible al agotar los intentos.
            logger.LogError(unreadable, "Trabajo {Id} ({Kind}): no se pudo leer al tomarlo, vuelve a la cola.", job!.Id, job.Kind);
            await Fail(job, $"No se pudo leer el trabajo tomado: {unreadable.Message}", ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Una toma. Si el trabajo se tomó pero su fila no se pudo leer, lo devuelve con lo mínimo para cerrarlo y el error.</summary>
    private async Task<(ClaimedJob? Job, Exception? Unreadable)> ClaimOne(string worker, IReadOnlyCollection<string>? kinds, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lockedBy = $"{(worker.Length > 60 ? worker[..60] : worker)}/{Guid.NewGuid():N}";

        // El predicado de lo vivo (status IN Pending, Running) es el de los índices parciales: la toma no recorre el historial.
        var byKind = kinds is null ? string.Empty : "AND j.kind = ANY(@kinds)";
        var args = new List<(string, object?)> { ("lease", options.Value.Lease), ("worker", lockedBy), ("now", now) };
        if (kinds is not null)
        {
            args.Add(("kinds", kinds.ToArray()));
        }

        // actor_ip se lee como inet, con su tipo: convertido a texto PostgreSQL le agrega la máscara (127.0.0.1/32) y
        // eso no es una dirección.
        await using var cmd = await Sql.Command(db, $"""
            UPDATE platform.jobs SET status = 'Running', locked_until = now() + @lease, locked_by = @worker,
                   attempts = attempts + 1, updated_at = @now
            WHERE id = (
              SELECT j.id FROM platform.jobs j
              WHERE j.status IN ('Pending', 'Running') AND (j.status = 'Pending' OR j.locked_until < now())
                AND j.run_at <= @now {byKind}
                AND (j.entity_key IS NULL OR NOT EXISTS (
                      SELECT 1 FROM platform.jobs e
                      WHERE e.entity_key = j.entity_key AND e.id < j.id AND e.status IN ('Pending', 'Running')))
              ORDER BY j.run_at, j.id
              FOR UPDATE SKIP LOCKED
              LIMIT 1)
            RETURNING id, kind, payload::text, attempts, max_attempts, trace_parent, actor_kind, actor, actor_ip
            """, ct, [.. args]).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (null, null);
        }

        var taken = new ClaimedJob(reader.GetInt64(0), reader.GetString(1), "{}", reader.GetInt32(3), reader.GetInt32(4), lockedBy);
        try
        {
            var actor = reader.IsDBNull(7) ? null : new Actor(
                Enum.Parse<ActorKind>(reader.GetString(6)),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<IPAddress>(8));
            return (taken with { Payload = reader.GetString(2), TraceParent = reader.IsDBNull(5) ? null : reader.GetString(5), Actor = actor }, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Failover.IsTransient(ex))
        {
            return (taken, ex);
        }
    }

    /// <summary>Terminado. Como todos los cierres, da false si el trabajo ya no es de esta toma: venció el lease y lo tomó otro.</summary>
    public async Task<bool> Complete(ClaimedJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var now = time.GetUtcNow();
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.jobs SET status = 'Done', locked_until = NULL, updated_at = {now}
            WHERE id = {job.Id} AND status = 'Running' AND locked_by = {job.LockedBy}
            """, ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Falla: vuelve a la cola con espera creciente, o queda muerto y visible para el staff.</summary>
    public async Task<bool> Fail(ClaimedJob job, string error, TimeSpan? retryAfter = null, bool permanent = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(error);
        var now = time.GetUtcNow();
        var dead = permanent || job.Attempts >= job.MaxAttempts;
        var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Min(3600, 15 * Math.Pow(2, job.Attempts - 1)));
        var status = dead ? "Dead" : "Pending";
        var runAt = now + delay;
        var message = error.Length > 2000 ? error[..2000] : error;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.jobs SET status = {status}, run_at = {runAt}, locked_until = NULL,
                   last_error = {message}, updated_at = {now}
            WHERE id = {job.Id} AND status = 'Running' AND locked_by = {job.LockedBy}
            """, ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Vuelve a la cola sin contar como falla: para consultar una operación larga o, con <paramref name="error"/>,
    /// después de una falla pasajera de la base que no es culpa del trabajo.
    /// </summary>
    public async Task<bool> Reschedule(ClaimedJob job, TimeSpan after, string? error = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var now = time.GetUtcNow();
        var runAt = now + after;
        var message = error is { Length: > 2000 } ? error[..2000] : error;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.jobs SET status = 'Pending', run_at = {runAt}, locked_until = NULL,
                   attempts = GREATEST(attempts - 1, 0), last_error = COALESCE({message}, last_error), updated_at = {now}
            WHERE id = {job.Id} AND status = 'Running' AND locked_by = {job.LockedBy}
            """, ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Latido: el trabajo sigue vivo y el lease se corre otro período.</summary>
    public async Task<bool> Extend(ClaimedJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var now = time.GetUtcNow();
        var lease = options.Value.Lease;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.jobs SET locked_until = now() + {lease}, updated_at = {now}
            WHERE id = {job.Id} AND status = 'Running' AND locked_by = {job.LockedBy}
            """, ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Apagado ordenado: el trabajo vuelve a la cola ya, sin contar el intento, para que lo tome otro nodo.</summary>
    public Task<bool> Release(ClaimedJob job, CancellationToken ct = default) => Reschedule(job, TimeSpan.Zero, ct: ct);
}

/// <summary>
/// Quién escucha cada evento. Cada suscriptor es un tipo de trabajo, y dice si toma la clave de entidad del que
/// publica (Keyed) o corre sin clave.
/// </summary>
public static class Subscriptions
{
    /// <summary>
    /// Clave de la emisión de un documento sin serie resuelta (sin emisor o sin tipo de comprobante): corre de a una
    /// con las demás que tampoco la tienen. Con serie, la clave es la de <see cref="FiscalSeries"/>.
    /// </summary>
    public const string FiscalEntity = "fiscal";

    /// <summary>
    /// Clave de entidad de una serie fiscal (emisor, punto de venta y tipo): sus emisiones y su control diario van de a
    /// uno y en orden, y dos series no se esperan entre sí (sección 9.4 del protocolo).
    /// </summary>
    public static string FiscalSeries(long issuerId, int pointOfSale, int typeCode) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{FiscalEntity}:{issuerId}:{pointOfSale}:{typeCode}");

    private static readonly Dictionary<string, (string Kind, bool Keyed)[]> Map = new(StringComparer.Ordinal)
    {
        [Events.InvoicePaid] = [(JobKinds.ActivateServicesOfInvoice, true), (UseCases.AffiliateJobs.Paid, true)],
        [Events.InvoiceIssued] = [(JobKinds.FiscalEmit, true)],
        // La revisión de la comisión no toma la clave de la serie fiscal: no espera a que salga el comprobante. Corre sin
        // clave y bloquea la factura.
        [Events.CreditNoteIssued] = [(JobKinds.FiscalEmit, true), (UseCases.AffiliateJobs.Review, false)],
        [Events.DebitNoteIssued] = [(JobKinds.FiscalEmit, true)],
        [Events.PaymentAllocated] = [(JobKinds.FiscalEmit, true)],

        // Los avisos de un ticket salen en orden: la clave de entidad es la del ticket, la que pasa quien publica.
        [Events.TicketOpened] = [(JobKinds.SupportNotify, true)],
        [Events.TicketReplied] = [(JobKinds.SupportNotify, true)],
        [Events.TicketClosed] = [(JobKinds.SupportNotify, true)],
        [Events.TicketEscalated] = [(JobKinds.SupportNotify, true)],

        // Afiliados: el pedido atribuye, el pago libera la espera y la reversa revisa la comisión.
        [Events.OrderPlaced] = [(UseCases.AffiliateJobs.Order, true)],
        [Events.AllocationReversed] = [(UseCases.AffiliateJobs.Review, true)],
    };

    /// <summary>
    /// Eventos que avisan al cliente por correo (D-13). El suscriptor no tiene clave de entidad: el aviso de factura
    /// espera al comprobante fiscal por su cuenta.
    /// </summary>
    private static readonly HashSet<string> Notified = new(StringComparer.Ordinal)
    {
        Events.InvoiceIssued,
        Events.DebitNoteIssued,
        Events.PaymentReceived,
        Events.ServiceActivated,
        Events.ServiceReactivated,
        Events.ServiceSuspended,
        Events.ServiceTerminated,
        Events.FiscalDocumentAuthorized,
    };

    public static IReadOnlyList<(string Kind, bool Keyed)> For(string eventName)
    {
        var subscribers = Map.TryGetValue(eventName, out var s) ? s : [];
        return Notified.Contains(eventName) ? [.. subscribers, (JobKinds.NotifyEvent, false)] : subscribers;
    }
}

public static class Events
{
    public const string OrderPlaced = "OrderPlaced";
    public const string InvoiceIssued = "InvoiceIssued";
    public const string PaymentAllocated = "PaymentAllocated";
    public const string InvoicePaid = "InvoicePaid";
    public const string CreditNoteIssued = "CreditNoteIssued";
    public const string DebitNoteIssued = "DebitNoteIssued";
    public const string PaymentReceived = "PaymentReceived";
    public const string ServiceActivated = "ServiceActivated";
    public const string ServiceReactivated = "ServiceReactivated";
    public const string ServiceSuspended = "ServiceSuspended";
    public const string ServiceTerminated = "ServiceTerminated";
    public const string FiscalDocumentAuthorized = "FiscalDocumentAuthorized";

    /// <summary>
    /// Los publica el soporte. Los webhooks y las extensiones reparten la apertura y las respuestas (<see cref="PublicEvents"/>).
    /// </summary>
    public const string TicketOpened = "TicketOpened";
    public const string TicketReplied = "TicketReplied";
    public const string TicketClosed = "TicketClosed";
    public const string TicketEscalated = "TicketEscalated";

    /// <summary>Un reembolso o un contracargo sacó una imputación de una factura, que vuelve a deber.</summary>
    public const string AllocationReversed = "AllocationReversed";
}

public static class JobKinds
{
    public const string BillingRun = "billing.run";
    public const string BillingAccount = "billing.account";
    public const string Dunning = "billing.dunning";
    public const string ReissueReleased = "billing.reissue";
    public const string ExchangeRates = "billing.exchange-rates";
    public const string ChangeGap = "billing.change-gap";
    public const string ActivateServicesOfInvoice = "services.activate-invoice";
    public const string ServiceOperation = "services.operation";
    public const string ServiceReconcile = "services.reconcile";
    public const string DomainOperation = "domains.operation";
    public const string DomainSyncDay = "domains.sync-day";
    public const string DomainSync = "domains.sync";
    public const string DomainNotices = "domains.notices";
    public const string FiscalEmit = "fiscal.emit";
    public const string FiscalSweep = "fiscal.sweep";
    public const string FiscalCertificates = "fiscal.certificates";
    public const string FiscalCheck = "fiscal.check";
    public const string FiscalRate = "fiscal.rate";
    public const string WebhookVerify = "payments.webhook";
    public const string PaymentSync = "payments.sync";
    public const string PaymentReconcile = "payments.reconcile";
    public const string CheckoutSweep = "payments.checkouts";
    public const string PaymentRefund = "payments.refund";
    public const string MethodSync = "payments.method-sync";
    public const string MethodDelete = "payments.method-delete";
    public const string AutoChargeScan = "payments.autocharge-scan";
    public const string AutoChargePrepare = "payments.autocharge-prepare";
    public const string AutoCharge = "payments.autocharge";
    public const string SubscriptionSync = "payments.subscription-sync";
    public const string SubscriptionCancel = "payments.subscription-cancel";
    public const string SubscriptionAmounts = "payments.subscription-amounts";
    public const string SubscriptionUpdate = "payments.subscription-update";
    public const string NotifyEvent = "notifications.event";
    public const string SendEmail = "notifications.send";
    public const string InvoiceReminders = "notifications.reminders";
    public const string Reconcile = "platform.reconcile";
    public const string WebhookDeliver = "webhooks.deliver";
    public const string ExtensionEvent = "extensions.event";
    public const string FraudScreen = "fraud.screen";
    public const string SupportNotify = "support.notify";
    public const string SupportMail = "support.mail";
    public const string SupportAutoClose = "support.autoclose";
    public const string SupportEscalate = "support.escalate";
}
