using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

public sealed record SubscriptionStarted(long Id, string RedirectUrl);

/// <summary>
/// Débito automático por suscripción de la pasarela (payment.subscriptions, sección 4.5 del diseño de cobros). Cubre
/// los servicios mensuales de la cuenta en su moneda: el cliente da su consentimiento, la aprueba en la pasarela y la
/// pasarela cobra cada mes. Cada cobro entra como pago de la suscripción y va primero a las facturas de esos servicios
/// (GatewayPayments). La fecha la pone la pasarela, así que si el cliente pagó por otro medio el débito queda como saldo
/// a favor para la factura siguiente. Payments:SubscriptionsEnabled corta ofrecerlas y cambiarlas en la pasarela, no
/// registrar lo que una vigente ya cobró.
/// </summary>
public sealed class SubscriptionService(
    BillingDbContext db,
    ModuleRegistry registry,
    GatewayPayments gateways,
    JobQueue jobs,
    AuditLog audit,
    Notifier notifier,
    BillingCore core,
    IOptions<PaymentsOptions> options,
    TimeProvider time,
    ILogger<SubscriptionService> logger)
{
    public bool Enabled => options.Value.SubscriptionsEnabled;

    /// <summary>Instancias que ofrecen el débito por suscripción en la moneda: con la capacidad y activas en el asistente.</summary>
    public async Task<IReadOnlyList<(long Id, string Name)>> Offered(Currency currency, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return [];
        }

        var setups = await db.GatewaySetups.AsNoTracking().Where(s => s.Enabled && s.AutoCharge).OrderBy(s => s.Position).ThenBy(s => s.ModuleInstanceId).ToListAsync(ct).ConfigureAwait(false);
        var offered = new List<(long, string)>();
        foreach (var setup in setups.Where(s => s.Offers(currency)))
        {
            if (await GatewayPayments.Declares(db, setup.ModuleInstanceId, "payment.subscriptions", ct).ConfigureAwait(false))
            {
                offered.Add((setup.ModuleInstanceId, setup.DisplayName));
            }
        }

        return offered;
    }

    /// <summary>
    /// Lo que la suscripción cobra cada mes: los servicios mensuales de la cuenta en la moneda que van a renovar
    /// (también los recién contratados), con su descuento, su IVA y los tributos de comprobante que suman al total (las
    /// percepciones), calculados como la factura de renovación: una por modo de carga del precio, que es como agrupa la
    /// corrida, y con la fecha fiscal de hoy.
    /// </summary>
    public async Task<decimal> MonthlyTotal(long accountId, Currency currency, CancellationToken ct = default)
    {
        var services = await Monthly(accountId, currency).ToListAsync(ct).ConfigureAwait(false);
        if (services.Count == 0)
        {
            return 0m;
        }

        var productIds = services.Select(s => s.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);
        var receiver = await core.Receiver(accountId, ct).ConfigureAwait(false);
        var taxes = await core.Taxes(ct).ConfigureAwait(false);
        var today = core.Today();
        var total = 0m;
        foreach (var group in services.GroupBy(s => s.PriceMode))
        {
            var draft = new Document(DocumentKind.Invoice, accountId, currency, group.Key, today, time.GetUtcNow());
            foreach (var service in group)
            {
                var product = products[service.ProductId];
                var tax = taxes.Line(product, receiver, accountId, today);
                draft.AddLine(LineKind.Renewal, product.Name, service.Quantity, service.DiscountedUnitPrice, tax.Treatment, tax.Rate, service.Id, category: product.TaxCategory, reason: tax.Reason, legend: tax.Legend);
            }

            total += draft.Compute().Total + taxes.DocumentTaxes(draft, receiver, today).Where(t => t.AddsToTotal).Sum(t => t.Amount);
        }

        return total;
    }

    /// <summary>Servicios de la cuenta en la moneda que van a seguir renovando: ni dados de baja ni con la baja pedida.</summary>
    private IQueryable<Service> Renewing(long accountId, Currency currency) =>
        db.Services.AsNoTracking().Where(s => s.AccountId == accountId && s.Currency == currency && s.CancelAt == null
            && (s.Status == ServiceStatus.Pending || s.Status == ServiceStatus.Provisioning || s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended));

    /// <summary>Los que cubre la suscripción: los mensuales de esos. El monto y la fecha de inicio salen de los mismos.</summary>
    private IQueryable<Service> Monthly(long accountId, Currency currency) =>
        Renewing(accountId, currency).Where(s => s.CycleUnit == CycleUnit.Month && s.CycleCount == 1);

    /// <summary>
    /// A qué va el cobro de una suscripción: primero las facturas impagas de la cuenta en la moneda con un servicio
    /// mensual (las que la suscripción paga), después las demás, las más viejas primero. Si fuera a las más viejas sin
    /// más, una factura anual anterior se llevaría la cuota y la renovación mensual quedaría impaga, con el servicio
    /// en mora aunque la pasarela debite todos los meses. Lo que sobra queda como saldo a favor.
    /// </summary>
    public static Task<List<long>> ChargeTargets(BillingDbContext db, long accountId, Currency currency, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var monthly = from l in db.DocumentLines
                      join s in db.Services on l.ServiceId equals (long?)s.Id
                      where s.AccountId == accountId && s.CycleUnit == CycleUnit.Month && s.CycleCount == 1
                      select l.DocumentId;
        return db.Documents.AsNoTracking()
            .Where(d => d.AccountId == accountId && d.Currency == currency && d.Kind != DocumentKind.CreditNote && d.IssueDate != null && d.Total - d.Credited - d.Allocated > 0)
            .OrderBy(d => monthly.Contains(d.Id) ? 0 : 1).ThenBy(d => d.DueDate).ThenBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Crea la suscripción por el total mensual, con inicio en el próximo vencimiento, y devuelve la URL donde el cliente
    /// la aprueba. Preparar la suscripción no cobra nada: se llama directo, como un checkout.
    /// </summary>
    public async Task<SubscriptionStarted> Start(long accountId, long instanceId, ConsentInput consent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        var currency = account.Currency;
        if (!(await Offered(currency, ct).ConfigureAwait(false)).Any(o => o.Id == instanceId))
        {
            throw new ValidationException("Esta pasarela no ofrece el débito automático por suscripción.");
        }

        GatewaySubscription subscription;
        DateOnly start;
        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            // Dos pedidos a la vez (un doble clic, o dos nodos) no crean dos suscripciones: el segundo espera al primero
            // y la ve.
            await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
            if (await db.GatewaySubscriptions.AnyAsync(s => s.AccountId == accountId && s.Status != SubscriptionStatus.Cancelled, ct).ConfigureAwait(false))
            {
                throw new ValidationException("La cuenta ya tiene un débito automático por suscripción.");
            }

            // Uno de los dos: con la suscripción y el medio guardado a la vez, cada factura se cobraría dos veces.
            if (await db.SavedPaymentMethods.AnyAsync(m => m.AccountId == accountId && m.AutoCharge, ct).ConfigureAwait(false))
            {
                throw new ValidationException("La cuenta ya tiene el débito automático con un medio guardado: hay que apagarlo antes de prender la suscripción.");
            }

            var amount = await MonthlyTotal(accountId, currency, ct).ConfigureAwait(false);
            if (amount <= 0m)
            {
                throw new ValidationException("La cuenta no tiene servicios mensuales para debitar.");
            }

            // El inicio es el próximo vencimiento de los mismos servicios que suma el monto: con el de cualquier servicio
            // de la cuenta, uno anual que vence antes adelantaría el primer débito del total mensual a una fecha en la que
            // no hay nada mensual que pagar.
            var today = core.Today();
            start = await Monthly(accountId, currency).Where(s => s.NextDueDate != null && s.NextDueDate >= today)
                .MinAsync(s => s.NextDueDate, ct).ConfigureAwait(false) ?? today.AddDays(1);
            var now = time.GetUtcNow();
            subscription = new GatewaySubscription(accountId, instanceId, currency, amount, GatewayPayments.NewReference(), consent.Record(now), now);
            db.GatewaySubscriptions.Add(subscription);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.Record("payments.consent", SavedMethodService.Account(accountId), new { subscription = subscription.Id, instance = instanceId, kind = "subscription" }, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
            var publicUrl = core.Options.PublicUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Falta Billing:PublicUrl para la vuelta de la pasarela.");
            var result = await gateways.Call(module, "create_subscription", new JsonObject
            {
                ["subscription_id"] = subscription.Reference,
                ["amount"] = GatewayPayments.Money(subscription.Amount, currency),
                ["interval"] = "month",
                ["interval_count"] = 1,
                ["start_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["reason"] = account.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Automatic debit of your services" : "Débito automático de tus servicios",
                ["back_url"] = $"{publicUrl}/cuenta/medios-de-pago",
            }, string.Create(CultureInfo.InvariantCulture, $"subscription-{subscription.Id}"), ct, await gateways.Context(accountId, module, contact: true, ct).ConfigureAwait(false)).ConfigureAwait(false);
            var redirect = GatewayPayments.Redirect(module, result);
            subscription.Created(result["subscription_external_id"]!.GetValue<string>(), redirect, time.GetUtcNow());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new SubscriptionStarted(subscription.Id, redirect);
        }
        catch
        {
            // Sea cual sea la falla (la pasarela, una respuesta sin URL o un corte), el cliente nunca vio la página para
            // aprobarla, así que no cobra aunque la pasarela la haya creado. Se da de baja acá para que pueda volver a
            // intentar.
            subscription.Observe(SubscriptionStatus.Cancelled, time.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// "Desactivar": en el núcleo vale en el momento y un trabajo la da de baja en la pasarela. Si después entra un
    /// cobro, igual se registra: la plata ya está.
    /// </summary>
    public async Task Cancel(long subscriptionId, IReadOnlyCollection<long> accountIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var subscription = await db.GatewaySubscriptions.SingleOrDefaultAsync(s => s.Id == subscriptionId && accountIds.Contains(s.AccountId), ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la suscripción {subscriptionId}.");
        if (!subscription.Observe(SubscriptionStatus.Cancelled, time.GetUtcNow()))
        {
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.subscription_cancelled", SavedMethodService.Account(subscription.AccountId), new { subscription = subscription.Id }, ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.SubscriptionCancel, new { subscription_id = subscription.Id }, GatewayPayments.SubscriptionEntity(subscription.Id), string.Create(CultureInfo.InvariantCulture, $"{JobKinds.SubscriptionCancel}:{subscription.Id}"), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Trabajo: la da de baja en la pasarela. Repetirlo sobre una ya cancelada da lo mismo.</summary>
    public async Task SendCancel(long subscriptionId, CancellationToken ct = default)
    {
        var subscription = await db.GatewaySubscriptions.SingleAsync(s => s.Id == subscriptionId, ct).ConfigureAwait(false);
        if (subscription.ExternalId is { } external)
        {
            var module = await registry.Resolve(subscription.ModuleInstanceId, ct).ConfigureAwait(false);
            await gateways.Call(module, "cancel_subscription", new JsonObject { ["subscription_external_id"] = external }, string.Create(CultureInfo.InvariantCulture, $"subscription-cancel-{subscription.Id}"), ct).ConfigureAwait(false);
        }

        if (subscription.Observe(SubscriptionStatus.Cancelled, time.GetUtcNow()))
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Trabajo: la pasarela avisó un cambio. Se consulta y se guarda el estado. Si la pasarela la canceló sola (por
    /// ejemplo después de varias cuotas rechazadas), queda en la auditoría y se le avisa al cliente. Corre aunque el
    /// débito por suscripción esté apagado: no cambia nada en la pasarela y el estado de una vigente tiene que ser el
    /// real (decide si la cuenta puede usar el débito con un medio guardado).
    /// </summary>
    public async Task Sync(long subscriptionId, CancellationToken ct = default)
    {
        var subscription = await db.GatewaySubscriptions.SingleAsync(s => s.Id == subscriptionId, ct).ConfigureAwait(false);
        if (subscription.ExternalId is not { } external)
        {
            return;
        }

        var module = await registry.Resolve(subscription.ModuleInstanceId, ct).ConfigureAwait(false);
        var result = await gateways.Call(module, "get_subscription", new JsonObject { ["subscription_external_id"] = external }, null, ct).ConfigureAwait(false);
        var status = result["status"]?.GetValue<string>() switch
        {
            "pending" => SubscriptionStatus.Pending,
            "active" => SubscriptionStatus.Active,
            "paused" => SubscriptionStatus.Paused,
            "cancelled" => SubscriptionStatus.Cancelled,
            var other => throw new RetryLaterException($"Estado de suscripción desconocido: {other}."),
        };
        if (!subscription.Observe(status, time.GetUtcNow()))
        {
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (status != SubscriptionStatus.Cancelled)
        {
            return;
        }

        logger.LogWarning("La pasarela dio de baja la suscripción {Id} de la cuenta {AccountId}.", subscription.Id, subscription.AccountId);
        await audit.Record("payments.subscription_cancelled", SavedMethodService.Account(subscription.AccountId), new { subscription = subscription.Id, by = "gateway" }, ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == subscription.AccountId, ct).ConfigureAwait(false);
        var name = await db.GatewaySetups.AsNoTracking().Where(s => s.ModuleInstanceId == subscription.ModuleInstanceId).Select(s => s.DisplayName).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var portal = core.Options.PublicUrl?.TrimEnd('/');
        await notifier.Queue(TemplateKeys.SubscriptionCancelled, account.Email, account.Language, new Dictionary<string, object?>
        {
            ["account_name"] = account.Name,
            ["gateway"] = name ?? module.Instance.Name,
            ["methods_url"] = portal is null ? null : $"{portal}/cuenta/medios-de-pago",
        }, string.Create(CultureInfo.InvariantCulture, $"{TemplateKeys.SubscriptionCancelled}:{subscription.Id}"), account.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Trabajo diario: encola, con la clave de entidad de cada suscripción, el cambio de monto de las que ya no cobran el
    /// total mensual de su cuenta (un servicio nuevo, un precio, una baja). Devuelve cuántas encoló. Con el débito por
    /// suscripción apagado no toca la pasarela, pero si hay suscripciones vigentes lo deja dicho: siguen cobrando el
    /// monto viejo (y sus cobros se registran igual).
    /// </summary>
    public async Task<int> ScanAmounts(CancellationToken ct = default)
    {
        var live = await db.GatewaySubscriptions.AsNoTracking()
            .Where(s => s.ExternalId != null && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Pending))
            .OrderBy(s => s.Id).Select(s => new { s.Id, s.AccountId, s.Currency, s.Amount }).ToListAsync(ct).ConfigureAwait(false);
        if (!Enabled)
        {
            if (live.Count > 0)
            {
                logger.LogWarning("Hay {Count} suscripciones vigentes con Payments:SubscriptionsEnabled apagado: sus cobros se registran, pero el monto no se actualiza en la pasarela ni se ofrecen nuevas. Prenderlo o darlas de baja.", live.Count);
            }

            return 0;
        }

        var today = core.Today();
        var queued = 0;
        foreach (var s in live)
        {
            var total = await MonthlyTotal(s.AccountId, s.Currency, ct).ConfigureAwait(false);
            if (total != s.Amount)
            {
                // Uno por día y suscripción: con el monto en la clave, un total que vuelve a un valor de antes quedaba
                // absorbido por el trabajo ya hecho y la pasarela seguía cobrando el monto viejo.
                var dedup = string.Create(CultureInfo.InvariantCulture, $"{JobKinds.SubscriptionUpdate}:{s.Id}:{today:yyyy-MM-dd}");
                await jobs.Enqueue(JobKinds.SubscriptionUpdate, new { subscription_id = s.Id }, GatewayPayments.SubscriptionEntity(s.Id), dedup, ct: ct).ConfigureAwait(false);
                queued++;
            }
        }

        return queued;
    }

    /// <summary>
    /// Trabajo: lleva la suscripción al total mensual de hoy, antes del próximo cobro, con payment.subscription_update.
    /// Sin esa capacidad lo deja en el log para el staff. Sin servicios que renueven (todos dados de baja) la da de baja:
    /// la baja de los servicios corta también el débito.
    /// </summary>
    public async Task UpdateAmount(long subscriptionId, CancellationToken ct = default)
    {
        var subscription = await db.GatewaySubscriptions.SingleAsync(s => s.Id == subscriptionId, ct).ConfigureAwait(false);
        if (!Enabled || subscription.ExternalId is null || subscription.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Pending))
        {
            return;
        }

        var total = await MonthlyTotal(subscription.AccountId, subscription.Currency, ct).ConfigureAwait(false);
        if (total == subscription.Amount)
        {
            return;
        }

        if (total <= 0m)
        {
            await audit.Record("payments.subscription_cancelled", SavedMethodService.Account(subscription.AccountId), new { subscription = subscription.Id, by = "core", reason = "no_services" }, ct).ConfigureAwait(false);
            await SendCancel(subscription.Id, ct).ConfigureAwait(false);
            return;
        }

        if (!await GatewayPayments.Declares(db, subscription.ModuleInstanceId, "payment.subscription_update", ct).ConfigureAwait(false))
        {
            logger.LogWarning("El total de la suscripción {Id} cambió a {Total} y la pasarela no deja cambiar el monto: hace falta otra suscripción.", subscription.Id, total);
            return;
        }

        // La clave cambia con cada cambio de la suscripción y se repite solo en el reintento de este: con el monto solo,
        // volver a un total de antes repetía una clave ya usada y el módulo devolvía la respuesta vieja sin llamar.
        var module = await registry.Resolve(subscription.ModuleInstanceId, ct).ConfigureAwait(false);
        await gateways.Call(module, "update_subscription", new JsonObject
        {
            ["subscription_external_id"] = subscription.ExternalId,
            ["amount"] = GatewayPayments.Money(total, subscription.Currency),
        }, string.Create(CultureInfo.InvariantCulture, $"subscription-{subscription.Id}-amount-{subscription.UpdatedAt.ToUnixTimeMilliseconds()}-{total:0.00}"), ct).ConfigureAwait(false);
        subscription.ChangeAmount(total, time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.subscription_amount", SavedMethodService.Account(subscription.AccountId), new { subscription = subscription.Id, amount = total }, ct).ConfigureAwait(false);
    }
}

public sealed class SubscriptionSyncHandler(SubscriptionService subscriptions) : IJobHandler
{
    public string Kind => JobKinds.SubscriptionSync;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await subscriptions.Sync(JsonDocument.Parse(job.Payload).RootElement.GetProperty("subscription_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class SubscriptionCancelHandler(SubscriptionService subscriptions) : IJobHandler
{
    public string Kind => JobKinds.SubscriptionCancel;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await subscriptions.SendCancel(JsonDocument.Parse(job.Payload).RootElement.GetProperty("subscription_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class SubscriptionAmountsHandler(SubscriptionService subscriptions) : IJobHandler
{
    public string Kind => JobKinds.SubscriptionAmounts;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        await subscriptions.ScanAmounts(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class SubscriptionUpdateHandler(SubscriptionService subscriptions) : IJobHandler
{
    public string Kind => JobKinds.SubscriptionUpdate;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await subscriptions.UpdateAmount(JsonDocument.Parse(job.Payload).RootElement.GetProperty("subscription_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}
