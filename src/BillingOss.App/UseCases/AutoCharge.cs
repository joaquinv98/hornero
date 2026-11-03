using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

/// <summary>
/// Medios de pago guardados (payment.saved_methods, sección 4.5 del diseño de cobros): lo que el cliente hace desde
/// Medios de pago y lo que avisa la pasarela. La revocación es inmediata en el núcleo (Ley 24.240, art. 10 ter) y el
/// borrado en la pasarela lo hace un trabajo.
/// </summary>
public sealed class SavedMethodService(BillingDbContext db, ModuleRegistry registry, GatewayPayments gateways, JobQueue jobs, AuditLog audit, BillingCore core, TimeProvider time)
{
    /// <summary>"Quitar": el débito se apaga en el momento y un trabajo borra el medio en la pasarela.</summary>
    public async Task Remove(long methodId, IReadOnlyCollection<long> accountIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var method = await Own(methodId, accountIds, ct).ConfigureAwait(false);
        if (method.Status == SavedMethodStatus.Deleted)
        {
            return;
        }

        method.Delete(time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.method_removed", Account(method.AccountId), new { method = method.Id }, ct).ConfigureAwait(false);
        await jobs.Enqueue(
            JobKinds.MethodDelete,
            new { method_id = method.Id },
            GatewayPayments.MethodEntity(method.ModuleInstanceId, method.MethodExternalId),
            string.Create(CultureInfo.InvariantCulture, $"{JobKinds.MethodDelete}:{method.Id}"),
            ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prende el débito automático con este medio, con una constancia nueva del consentimiento, o lo apaga (sin
    /// consentimiento). Apagarlo no borra el medio.
    /// </summary>
    public async Task SetAutoCharge(long methodId, IReadOnlyCollection<long> accountIds, ConsentInput? consent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var method = await Own(methodId, accountIds, ct).ConfigureAwait(false);
        await Sql.LockAccount(db, method.AccountId, ct).ConfigureAwait(false);
        if (consent is null)
        {
            method.UseForAutoCharge(false, now);
        }
        else
        {
            var setup = await db.GatewaySetups.AsNoTracking().SingleOrDefaultAsync(s => s.ModuleInstanceId == method.ModuleInstanceId, ct).ConfigureAwait(false);
            if (method.Status != SavedMethodStatus.Active || method.ExpiredOn(core.Today()) || setup is not { Enabled: true, AutoCharge: true })
            {
                throw new ValidationException("Este medio no se puede usar para el débito automático.");
            }

            // Uno de los dos: con la suscripción y el medio guardado a la vez, cada factura se cobraría dos veces.
            if (await AutoChargeService.HasSubscription(db, method.AccountId, ct).ConfigureAwait(false))
            {
                throw new ValidationException("La cuenta ya tiene el débito automático por suscripción: hay que desactivarlo antes.");
            }

            await db.SavedPaymentMethods.Where(m => m.AccountId == method.AccountId && m.AutoCharge && m.Id != method.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.AutoCharge, false).SetProperty(m => m.UpdatedAt, now), ct).ConfigureAwait(false);
            method.UseForAutoCharge(true, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record(consent is null ? "payments.autocharge_off" : "payments.autocharge_on", Account(method.AccountId), new { method = method.Id, consent = consent?.Record(now) is { } c ? JsonNode.Parse(c) : null }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Trabajo: borra el medio en la pasarela. Repetirlo, o pedirlo sobre uno ya borrado, da lo mismo.</summary>
    public async Task SendDelete(long methodId, CancellationToken ct = default)
    {
        var method = await db.SavedPaymentMethods.AsNoTracking().SingleAsync(m => m.Id == methodId, ct).ConfigureAwait(false);
        var module = await registry.Resolve(method.ModuleInstanceId, ct).ConfigureAwait(false);
        await gateways.Call(module, "delete_method", new JsonObject
        {
            ["customer_external_id"] = method.CustomerExternalId,
            ["method_external_id"] = method.MethodExternalId,
        }, string.Create(CultureInfo.InvariantCulture, $"method-delete-{method.Id}"), ct).ConfigureAwait(false);
    }

    /// <summary>Trabajo: la pasarela avisó que el medio cambió (la red actualizó la tarjeta) o que se borró.</summary>
    public async Task Sync(long instanceId, string methodExternalId, CancellationToken ct = default)
    {
        var method = await db.SavedPaymentMethods.SingleOrDefaultAsync(m => m.ModuleInstanceId == instanceId && m.MethodExternalId == methodExternalId, ct).ConfigureAwait(false);
        if (method is null || method.Status == SavedMethodStatus.Deleted)
        {
            return;
        }

        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var result = await gateways.Call(module, "get_method", new JsonObject
        {
            ["customer_external_id"] = method.CustomerExternalId,
            ["method_external_id"] = method.MethodExternalId,
        }, null, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var wasAutoCharge = method.AutoCharge;
        if (result["status"]?.GetValue<string>() == "deleted")
        {
            method.Delete(now);
        }
        else
        {
            method.Describe(Text(result, "type"), Text(result, "brand"), Text(result, "last4"), Int(result, "exp_month"), Int(result, "exp_year"), now);
            if (method.ExpiredOn(core.Today()))
            {
                method.Expire(now);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (wasAutoCharge && !method.AutoCharge)
        {
            await audit.Record("payments.autocharge_off", Account(method.AccountId), new { method = method.Id, reason = method.Status.ToString() }, ct).ConfigureAwait(false);
        }
    }

    internal static string Account(long accountId) => string.Create(CultureInfo.InvariantCulture, $"account:{accountId}");

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>Un medio de las cuentas que puede ver quien pide. Lo ajeno da lo mismo que lo que no existe.</summary>
    private async Task<SavedPaymentMethod> Own(long methodId, IReadOnlyCollection<long> accountIds, CancellationToken ct) =>
        await db.SavedPaymentMethods.SingleOrDefaultAsync(m => m.Id == methodId && accountIds.Contains(m.AccountId), ct).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"No existe el medio {methodId}.");
}

/// <summary>
/// Cobro automático de renovaciones con el medio guardado (sección 4.5 del diseño de cobros y D-07). El planificador
/// encola un barrido por día y el barrido, un trabajo por factura con clave única. Ese trabajo comprueba la factura
/// en una transacción corta, abre el intento y encola el cobro, que llama a charge_saved con la clave
/// autocharge-{intento} fuera de toda transacción. Nunca se cobra con otra clave a ciegas.
/// </summary>
public sealed class AutoChargeService(
    BillingDbContext db,
    ModuleRegistry registry,
    GatewayPayments gateways,
    JobQueue jobs,
    AuditLog audit,
    Notifier notifier,
    BillingCore core,
    IOptions<PaymentsOptions> options,
    TimeProvider time,
    ILogger<AutoChargeService> logger)
{
    /// <summary>
    /// Plazo para repetir un débito con su misma clave. Tiene que ser menor que lo que el módulo busca hacia atrás el
    /// cobro de un intento (Stripe, 3 días), porque la pasarela puede olvidar la clave antes (Stripe, desde las 24
    /// horas). Pasado el plazo, repetir podría cobrar dos veces.
    /// </summary>
    public static TimeSpan ResendWindow { get; } = TimeSpan.FromDays(2);

    /// <summary>Clave de entidad de los trabajos del débito de una factura: corren de a uno.</summary>
    public static string Entity(long documentId) => string.Create(CultureInfo.InvariantCulture, $"autocharge:{documentId}");

    /// <summary>
    /// La cuenta tiene un débito por suscripción vigente (o esperando que el cliente lo apruebe). Con el medio guardado
    /// es uno de los dos: la suscripción ya cobra las facturas y el débito las cobraría otra vez.
    /// </summary>
    public static Task<bool> HasSubscription(BillingDbContext db, long accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.GatewaySubscriptions.AnyAsync(s => s.AccountId == accountId && s.Status != SubscriptionStatus.Cancelled, ct);
    }

    /// <summary>
    /// Facturas con un servicio dado de baja, o con un período que empieza desde la baja pedida: la baja corta el débito
    /// (Disposición 954/2025, sección 4.5 del diseño de cobros). No se debitan solas y siguen el camino de siempre.
    /// </summary>
    public static IQueryable<long> WithCancelledService(BillingDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return from l in db.DocumentLines
               join s in db.Services on l.ServiceId equals (long?)s.Id
               where s.Status == ServiceStatus.Cancelled || s.Status == ServiceStatus.Terminated
                     || (s.CancelAt != null && db.BilledPeriods.Any(p => p.DocumentLineId == l.Id && !p.Released && p.Start >= s.CancelAt))
               select l.DocumentId;
    }

    /// <summary>Cuánto falta para la hora del débito en la zona del proveedor, o null si ya pasó hoy.</summary>
    public TimeSpan? UntilHour()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(core.Options.TimeZone);
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        var at = new DateTimeOffset(local.Date, local.Offset).AddHours(options.Value.AutoCharge.Hour);
        return local < at ? at - local : null;
    }

    /// <summary>
    /// Lo que dice el correo de la factura antes de cada débito: cuándo y con qué medio se va a debitar, o null si la
    /// factura no se debita sola.
    /// </summary>
    public static async Task<(DateOnly Date, SavedPaymentMethod Method)?> Preview(BillingDbContext db, Document doc, AutoChargeOptions policy, DateOnly today, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(policy);
        if (!doc.IsIssued || doc.Kind == DocumentKind.CreditNote || doc.Pending <= 0m || (policy.MaxAmount.TryGetValue(doc.Currency.Code, out var max) && doc.Pending > max)
            || await WithCancelledService(db).ContainsAsync(doc.Id, ct).ConfigureAwait(false)
            || await HasSubscription(db, doc.AccountId, ct).ConfigureAwait(false))
        {
            return null;
        }

        var method = await db.SavedPaymentMethods.AsNoTracking()
            .SingleOrDefaultAsync(m => m.AccountId == doc.AccountId && m.AutoCharge && m.Status == SavedMethodStatus.Active, ct).ConfigureAwait(false);
        var setup = method is null ? null : await db.GatewaySetups.AsNoTracking().SingleOrDefaultAsync(s => s.ModuleInstanceId == method.ModuleInstanceId, ct).ConfigureAwait(false);
        if (method is null || setup is not { AutoCharge: true } || !setup.Offers(doc.Currency))
        {
            return null;
        }

        var next = policy.EffectiveDays.Select(d => doc.DueDate.AddDays(d)).Where(d => d >= today).Order().FirstOrDefault();
        return next == default || method.ExpiredOn(next) ? null : (next, method);
    }

    /// <summary>
    /// Encola el débito de las facturas que vencen en un día de la política, de cuentas con un medio activo para el
    /// débito en una pasarela que lo ofrece. También vence los medios cuya tarjeta venció. Devuelve cuántas encoló.
    /// </summary>
    public async Task<int> Scan(CancellationToken ct = default)
    {
        var today = core.Today();
        var now = time.GetUtcNow();
        var policy = options.Value.AutoCharge;
        var dueDates = policy.EffectiveDays.Select(d => today.AddDays(-d)).ToList();

        foreach (var expired in await db.SavedPaymentMethods.Where(m => m.Status == SavedMethodStatus.Active && m.ExpYear != null).ToListAsync(ct).ConfigureAwait(false))
        {
            if (expired.ExpiredOn(today))
            {
                expired.Expire(now);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await NoticeExpiring(today, ct).ConfigureAwait(false);

        // Los débitos que siguen abiertos (sin respuesta cierta, pendientes en la pasarela o con su trabajo muerto) se
        // repiten con la misma clave, que es lo seguro (sección 6 del protocolo): el módulo devuelve lo que pasó o lo
        // hace una sola vez. Nunca con otra clave. Pasado ResendWindow el trabajo los cierra sin pedir nada.
        var hourAgo = now.AddHours(-1);
        var open = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.Initiator == AttemptInitiator.Merchant && a.Status == AttemptStatus.Open && a.UpdatedAt < hourAgo)
            .OrderBy(a => a.Id).Select(a => new { a.Id, a.DocumentIds }).ToListAsync(ct).ConfigureAwait(false);
        foreach (var u in open)
        {
            await jobs.Enqueue(JobKinds.AutoCharge, new { attempt_id = u.Id }, Entity(u.DocumentIds[0]), string.Create(CultureInfo.InvariantCulture, $"{JobKinds.AutoCharge}:{u.Id}:resolve:{today:yyyy-MM-dd}"), ct: ct).ConfigureAwait(false);
        }

        var candidates = await (
            from d in db.Documents
            join m in db.SavedPaymentMethods on d.AccountId equals m.AccountId
            join s in db.GatewaySetups on m.ModuleInstanceId equals s.ModuleInstanceId
            where m.AutoCharge && m.Status == SavedMethodStatus.Active && s.Enabled && s.AutoCharge
                  && d.Kind != DocumentKind.CreditNote && d.IssueDate != null && d.Total - d.Credited - d.Allocated > 0
                  && dueDates.Contains(d.DueDate) && !WithCancelledService(db).Contains(d.Id)
                  && !db.GatewaySubscriptions.Any(x => x.AccountId == d.AccountId && x.Status != SubscriptionStatus.Cancelled)
            orderby d.Id
            select new { d.Id, d.Currency, Pending = d.Total - d.Credited - d.Allocated, s.Currencies })
            .ToListAsync(ct).ConfigureAwait(false);

        var queued = 0;
        foreach (var c in candidates)
        {
            if (!c.Currencies.Contains(c.Currency.Code, StringComparer.Ordinal) || (policy.MaxAmount.TryGetValue(c.Currency.Code, out var max) && c.Pending > max))
            {
                continue;
            }

            await jobs.Enqueue(JobKinds.AutoChargePrepare, new { document_id = c.Id }, Entity(c.Id), string.Create(CultureInfo.InvariantCulture, $"{JobKinds.AutoChargePrepare}:{c.Id}:{today:yyyy-MM-dd}"), ct: ct).ConfigureAwait(false);
            queued++;
        }

        return queued;
    }

    /// <summary>
    /// En una transacción corta, con la factura bloqueada: comprueba que siga con saldo, que el débito siga activo y que
    /// no haya otro intento automático abierto, abre el intento por el saldo y encola el cobro. Devuelve el intento, o
    /// null si no corresponde cobrar.
    /// </summary>
    public async Task<long?> Prepare(long documentId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var policy = options.Value.AutoCharge;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", [documentId], ct).ConfigureAwait(false);
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        if (!doc.IsIssued || doc.Kind == DocumentKind.CreditNote || doc.Pending <= 0m || await WithCancelledService(db).ContainsAsync(documentId, ct).ConfigureAwait(false)
            || await HasSubscription(db, doc.AccountId, ct).ConfigureAwait(false))
        {
            return null;
        }

        var method = await db.SavedPaymentMethods.AsNoTracking()
            .SingleOrDefaultAsync(m => m.AccountId == doc.AccountId && m.AutoCharge && m.Status == SavedMethodStatus.Active, ct).ConfigureAwait(false);
        var setup = method is null ? null : await db.GatewaySetups.AsNoTracking().SingleOrDefaultAsync(s => s.ModuleInstanceId == method.ModuleInstanceId, ct).ConfigureAwait(false);
        if (method is null || method.ExpiredOn(core.Today()) || setup is not { AutoCharge: true } || !setup.Offers(doc.Currency)
            || (policy.MaxAmount.TryGetValue(doc.Currency.Code, out var max) && doc.Pending > max))
        {
            return null;
        }

        // Los intentos del comercio sobre esta factura, con el medio que fuera: lo que se cobra es la factura. Ninguno
        // nuevo mientras haya uno abierto (en curso, pendiente en la pasarela o sin respuesta cierta), aunque el cliente
        // haya cambiado la tarjeta: el pedido anterior igual pudo haber cobrado, y lo resuelven el aviso, la conciliación
        // o el plazo. Uno vencido sin respuesta cierta saca la factura del débito. Agotados los intentos, o con un
        // rechazo que no admite reintento con este mismo medio, la factura sigue la mora.
        var previous = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.Initiator == AttemptInitiator.Merchant && a.DocumentIds.Contains(documentId))
            .ToListAsync(ct).ConfigureAwait(false);
        if (previous.Any(a => a.Status == AttemptStatus.Open || a.Outcome == "uncertain_expired") || previous.Count >= policy.EffectiveDays.Count
            || previous.Any(a => a.SavedMethodId == method.Id && a.Status == AttemptStatus.Failed && a.Retry is "never" or "customer_action"))
        {
            return null;
        }

        // Un pago del cliente en curso no se cruza con el débito.
        var recent = now.AddHours(-1);
        if (await db.PaymentAttempts.AnyAsync(a => a.Initiator == AttemptInitiator.Customer && a.Status == AttemptStatus.Open && a.CreatedAt > recent && a.DocumentIds.Contains(documentId), ct).ConfigureAwait(false))
        {
            return null;
        }

        var attempt = PaymentAttempt.Automatic(doc.AccountId, method.ModuleInstanceId, doc.Currency, doc.Pending, doc.Id, method.Id, GatewayPayments.NewReference(), now);
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.AutoCharge, new { attempt_id = attempt.Id }, Entity(documentId), string.Create(CultureInfo.InvariantCulture, $"{JobKinds.AutoCharge}:{attempt.Id}"), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return attempt.Id;
    }

    /// <summary>
    /// Cobra el intento con charge_saved. Con approved el pago sigue el camino de siempre y con pending espera el aviso
    /// o la conciliación. Con rejected mira details.retry: later deja que reintente el próximo día de la política,
    /// never vence el medio y customer_action le manda al cliente el enlace para resolverlo. Si ningún pedido del intento
    /// salió (sin conexión o frenado por la pasarela) y el trabajo se agota, el intento falla con later. Si alguno pudo
    /// haber salido sin respuesta cierta, queda incierto y el barrido lo repite con la misma clave.
    /// </summary>
    public async Task Charge(long attemptId, bool lastTry, CancellationToken ct = default)
    {
        var attempt = await db.PaymentAttempts.SingleAsync(a => a.Id == attemptId, ct).ConfigureAwait(false);
        if (attempt.Status != AttemptStatus.Open)
        {
            return;
        }

        var now = time.GetUtcNow();
        var documentId = attempt.DocumentIds.Single();
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        var method = await db.SavedPaymentMethods.AsNoTracking().SingleAsync(m => m.Id == attempt.SavedMethodId, ct).ConfigureAwait(false);

        // Pasado el plazo, repetir con la misma clave ya no es seguro. Si el primer pedido cobró, lo registran el aviso o
        // la conciliación. La factura no se vuelve a debitar sola y el portal la vuelve a ofrecer.
        if (now - attempt.CreatedAt > ResendWindow)
        {
            attempt.Fail("uncertain_expired", "never", now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogWarning("El débito del intento {AttemptId} siguió sin respuesta cierta pasado el plazo: no se repite. Revisar en la pasarela si cobró.", attempt.Id);
            await audit.Record("payments.autocharge_uncertain", SavedMethodService.Account(doc.AccountId), new { attempt = attempt.Id, document = doc.Id }, ct).ConfigureAwait(false);
            return;
        }

        // Antes de cada pedido, también de cada reintento: la factura sigue con ese saldo, sin servicios dados de baja, y
        // el débito sigue prendido con este medio. Si no, no se pide nada: uno que no llegó a salir no se hace después
        // de otro pago o de una revocación (Ley 24.240, art. 10 ter). Pero si un pedido anterior de este intento pudo
        // llegar a la pasarela (Outcome: sin respuesta cierta, o el pago quedó pendiente), tampoco se cierra: cerrarlo
        // dejaría abrir otro intento sobre la misma factura con el medio nuevo y cobrarla dos veces. Queda abierto, lo
        // que cobró lo registran el aviso o la conciliación, y pasado el plazo vence.
        if (doc.Pending != attempt.Amount || !method.AutoCharge || method.Status != SavedMethodStatus.Active
            || await WithCancelledService(db).ContainsAsync(documentId, ct).ConfigureAwait(false))
        {
            if (attempt.Outcome is not null)
            {
                logger.LogWarning("El débito del intento {AttemptId} pudo haber salido y la factura o el medio cambiaron: no se repite ni se cierra hasta el aviso, la conciliación o el plazo.", attempt.Id);
                return;
            }

            attempt.Fail(doc.Pending <= 0m ? "not_needed" : "changed", null, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var module = await registry.Resolve(attempt.ModuleInstanceId, ct).ConfigureAwait(false);
        var context = await gateways.Context(doc.AccountId, module, contact: false, ct).ConfigureAwait(false);

        // Desde acá el pedido pudo llegar a la pasarela aunque la respuesta no vuelva (un corte, o un nodo que se apaga en
        // el medio del pedido): queda anotado antes de pedir, así un reintento después de un cambio no lo cierra como si
        // nunca hubiera salido. Si el módulo contesta que este pedido no salió, se deshace.
        var fresh = attempt.Outcome is null;
        attempt.Sending(now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var result = await gateways.Send(module, "/v0/payment/charge_saved", "charge_saved", new JsonObject
        {
            ["attempt_id"] = attempt.Reference,
            ["amount"] = GatewayPayments.Money(attempt.Amount, attempt.Currency),
            ["description"] = string.Create(CultureInfo.InvariantCulture, $"Factura {doc.Number}"),
            ["customer_external_id"] = method.CustomerExternalId,
            ["method_external_id"] = method.MethodExternalId,
        }, string.Create(CultureInfo.InvariantCulture, $"autocharge-{attempt.Id}"), context, ct).ConfigureAwait(false);

        if (result.Error is not { } error)
        {
            await gateways.Record(attempt.ModuleInstanceId, result.Response?.Result ?? new JsonObject(), ct).ConfigureAwait(false);

            // Si quedó pendiente en la pasarela, el intento lo recuerda (también si antes estaba sin respuesta cierta: es lo
            // último que dijo la pasarela): ya hay un cobro en curso por esta factura. Record pudo limpiar el contexto, así
            // que se vuelve a leer.
            var open = await db.PaymentAttempts.SingleAsync(a => a.Id == attempt.Id, ct).ConfigureAwait(false);
            if (open.Status == AttemptStatus.Open && open.Outcome != "pending")
            {
                open.Observe("pending", time.GetUtcNow());
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return;
        }

        if (error.Code == ErrorCodes.Rejected)
        {
            await Rejected(attempt, method, doc, error, ct).ConfigureAwait(false);
            return;
        }

        if (NotSent(error.Code))
        {
            // Este pedido no salió. Si el intento no tenía nada antes de pedirlo, vuelve a no tenerlo.
            if (fresh)
            {
                attempt.NotSent(now);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            // Pudo llegar (sin respuesta a tiempo, otro en curso con la misma clave o una falla del módulo): queda incierto,
            // y se repite con la misma clave en los reintentos del trabajo y después en el barrido.
            attempt.Observe($"uncertain:{error.Code}", now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (ErrorCodes.IsRetryable(error.Code) && !lastTry)
        {
            throw GatewayPayments.Failure(error);
        }

        // Si este pedido o uno anterior del intento pudo haber cobrado, un error de ahora no dice nada de aquel: el intento
        // sigue abierto hasta el aviso, la conciliación o el plazo, y la factura no se debita con otro intento.
        if (attempt.Outcome is not null)
        {
            logger.LogWarning("El débito del intento {AttemptId} quedó sin respuesta cierta ({Code}): lo resuelve la conciliación.", attempt.Id, error.Code);
            return;
        }

        // Ningún pedido llegó a la pasarela: sin conexión todo el trabajo, o un error definitivo antes de cobrar (por
        // ejemplo la configuración de la instancia). No traba la factura: vuelve otro día de la política con otro
        // intento, o el cliente la paga por otro medio en el portal.
        attempt.Fail(error.Code, "later", now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogWarning("El débito del intento {AttemptId} no llegó a la pasarela: {Code} {Message}. Vuelve otro día de la política.", attempt.Id, error.Code, error.Message);
    }

    /// <summary>
    /// Errores que dicen que el pedido no llegó a cobrar: sin conexión con el módulo, frenado por la pasarela o rechazado
    /// antes de pedirle nada (firma, configuración, datos, una operación que no tiene o un medio que no existe). Con
    /// cualquier otro, el pedido pudo haber cobrado.
    /// </summary>
    private static bool NotSent(string code) =>
        code is ErrorCodes.UpstreamUnavailable or ErrorCodes.RateLimited or ErrorCodes.InvalidSignature or ErrorCodes.InvalidConfig
            or ErrorCodes.InvalidInput or ErrorCodes.NotSupported or ErrorCodes.NotFound;

    private async Task Rejected(PaymentAttempt attempt, SavedPaymentMethod method, Document doc, ModuleError error, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var details = error.Details;
        var retry = details?["retry"]?.GetValue<string>() switch
        {
            "never" => "never",
            "customer_action" => "customer_action",
            _ => "later",
        };
        var code = details?["decline_code"]?.GetValue<string>() ?? details?["upstream"]?[0]?["code"]?.GetValue<string>() ?? "declined";
        attempt.Fail($"rejected:{code}", retry, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.autocharge_rejected", SavedMethodService.Account(doc.AccountId), new { attempt = attempt.Id, document = doc.Id, retry }, ct).ConfigureAwait(false);
        if (retry == "later")
        {
            return;
        }

        if (retry == "never")
        {
            // La tarjeta no sirve más (vencida, perdida, robada): deja de usarse y el cliente elige otro medio.
            await db.SavedPaymentMethods.Where(m => m.Id == method.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, SavedMethodStatus.Expired).SetProperty(m => m.AutoCharge, false).SetProperty(m => m.UpdatedAt, now), ct).ConfigureAwait(false);
        }

        // Al cliente, un rechazo genérico: el motivo de un fraude o de una tarjeta robada no se muestra. El enlace de la
        // pasarela va solo si es https y a un host de la instancia, como cualquier URL que sigue el navegador (sección 9.1
        // del protocolo). Si no, el de la factura en el portal.
        var action = details?["customer_action_url"]?.GetValue<string>();
        var hosts = JsonSerializer.Deserialize<string[]>(await db.ModuleInstances.AsNoTracking().Where(i => i.Id == attempt.ModuleInstanceId).Select(i => i.BrowserHosts).SingleAsync(ct).ConfigureAwait(false)) ?? [];
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == doc.AccountId, ct).ConfigureAwait(false);
        var culture = TemplateRenderer.CultureFor(account.Language);
        var portal = core.Options.PublicUrl?.TrimEnd('/');
        await notifier.Queue(TemplateKeys.AutoChargeNotice, account.Email, account.Language, new Dictionary<string, object?>
        {
            ["account_name"] = account.Name,
            ["reason"] = retry,
            ["number"] = doc.Number?.ToString(CultureInfo.InvariantCulture),
            ["pending"] = Notifier.Money(doc.Pending, doc.Currency, culture),
            ["method"] = Describe(method),
            ["pay_url"] = Uri.TryCreate(action, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase)
                ? action
                : portal is null ? null : string.Create(CultureInfo.InvariantCulture, $"{portal}/facturas/{doc.Id}"),
        }, string.Create(CultureInfo.InvariantCulture, $"{TemplateKeys.AutoChargeNotice}:attempt:{attempt.Id}"), account.Id, ct).ConfigureAwait(false);
    }

    /// <summary>"Visa •••• 4242", sin nada más de la tarjeta.</summary>
    public static string Describe(SavedPaymentMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var brand = method.Brand switch
        {
            null => method.Type,
            "amex" => "American Express",
            "diners" => "Diners Club",
            "jcb" => "JCB",
            var b => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(b.Replace('_', ' ')),
        };
        return method.Last4 is null ? brand ?? "medio guardado" : $"{brand} •••• {method.Last4}";
    }

    /// <summary>
    /// Aviso 30 días antes de que venza la tarjeta del débito (sección 4.5 del diseño de cobros): uno por tarjeta y
    /// vencimiento, aunque el barrido corra todos los días.
    /// </summary>
    private async Task NoticeExpiring(DateOnly today, CancellationToken ct)
    {
        var portal = core.Options.PublicUrl?.TrimEnd('/');
        var methods = await db.SavedPaymentMethods.AsNoTracking()
            .Where(m => m.AutoCharge && m.Status == SavedMethodStatus.Active && m.ExpYear != null && m.ExpMonth != null)
            .OrderBy(m => m.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var method in methods)
        {
            var lastDay = new DateOnly(method.ExpYear!.Value, method.ExpMonth!.Value, 1).AddMonths(1).AddDays(-1);
            if (lastDay < today || lastDay > today.AddDays(30))
            {
                continue;
            }

            var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == method.AccountId, ct).ConfigureAwait(false);
            await notifier.Queue(TemplateKeys.CardExpiring, account.Email, account.Language, new Dictionary<string, object?>
            {
                ["account_name"] = account.Name,
                ["method"] = Describe(method),
                ["expires"] = Notifier.Date(lastDay, TemplateRenderer.CultureFor(account.Language)),
                ["methods_url"] = portal is null ? null : $"{portal}/cuenta/medios-de-pago",
            }, string.Create(CultureInfo.InvariantCulture, $"{TemplateKeys.CardExpiring}:{method.Id}:{lastDay:yyyy-MM}"), account.Id, ct).ConfigureAwait(false);
        }
    }
}

public sealed class MethodSyncHandler(SavedMethodService methods) : IJobHandler
{
    public string Kind => JobKinds.MethodSync;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var p = JsonDocument.Parse(job.Payload).RootElement;
        await methods.Sync(p.GetProperty("instance_id").GetInt64(), p.GetProperty("method_external_id").GetString()!, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class MethodDeleteHandler(SavedMethodService methods) : IJobHandler
{
    public string Kind => JobKinds.MethodDelete;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await methods.SendDelete(JsonDocument.Parse(job.Payload).RootElement.GetProperty("method_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class AutoChargeScanHandler(AutoChargeService autoCharge) : IJobHandler
{
    public string Kind => JobKinds.AutoChargeScan;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        if (autoCharge.UntilHour() is { } wait)
        {
            return JobResult.PollLater(wait);
        }

        await autoCharge.Scan(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class AutoChargePrepareHandler(AutoChargeService autoCharge) : IJobHandler
{
    public string Kind => JobKinds.AutoChargePrepare;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await autoCharge.Prepare(JsonDocument.Parse(job.Payload).RootElement.GetProperty("document_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class AutoChargeHandler(AutoChargeService autoCharge) : IJobHandler
{
    public string Kind => JobKinds.AutoCharge;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await autoCharge.Charge(JsonDocument.Parse(job.Payload).RootElement.GetProperty("attempt_id").GetInt64(), job.Attempts >= job.MaxAttempts, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}
