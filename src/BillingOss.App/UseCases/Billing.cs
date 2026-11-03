using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.App.UseCases;

/// <summary>
/// Ciclo de facturación (sección 12.2 del modelo de dominio): renueva cada servicio y cada dominio en su transacción
/// corta, arma un borrador por cuenta y moneda, lo emite y consume el saldo a favor. Dos corridas a la vez no
/// duplican: lo impiden la restricción de exclusión de los períodos facturados y la clave de cada renglón de dominio.
/// </summary>
public sealed class BillingRunService(BillingDbContext db, BillingCore core, CreditService credit, ExchangeRateService rates, JobQueue jobs, UsageBilling usage, TimeProvider time, ILogger<BillingRunService> logger)
{
    /// <summary>Las métricas que se cobran con la renovación (sección 4.1 de diseno/uso-medido.md).</summary>
    private static readonly Func<Domain.Usage.UsageMetric, bool> WithRenewal = m => m.Cycle == Domain.Usage.UsageCycle.Renewal;

    /// <summary>La corrida en un solo proceso, para el paralelo y las pruebas: cada cuenta se factura acá mismo. Devuelve cuántas facturas emitió.</summary>
    public Task<int> Run(CancellationToken ct = default) => Run((accountId, _) => RunAccount(accountId, ct), ct);

    /// <summary>
    /// La corrida de producción (billing.run): reparte un trabajo por cuenta con algo que renovar, con clave de
    /// deduplicación por fecha y cuenta, así varios nodos facturan a la vez y una corrida repetida el mismo día no
    /// vuelve a encolar las cuentas ya repartidas. Devuelve cuántas cuentas repartió.
    /// </summary>
    public Task<int> Distribute(CancellationToken ct = default) => Run(async (accountId, today) =>
    {
        await jobs.Enqueue(JobKinds.BillingAccount, new { date = today, account_id = accountId }, $"account:{accountId}",
            string.Create(CultureInfo.InvariantCulture, $"{JobKinds.BillingAccount}:{today:yyyy-MM-dd}:{accountId}"), ct: ct).ConfigureAwait(false);
        return 1;
    }, ct);

    /// <summary>
    /// Factura una cuenta: renueva sus servicios y sus dominios, que comparten borrador, suma sus ítems facturables que
    /// ya tocan y emite todos sus borradores de renovación, también los que dejó un intento cortado, así el reintento
    /// del trabajo los termina. Devuelve cuántas facturas emitió.
    /// </summary>
    public async Task<int> RunAccount(long accountId, CancellationToken ct = default)
    {
        var today = core.Today();
        var due = await DueServices(today).Where(s => s.AccountId == accountId).OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        var dueDomains = await DueDomains(today).Where(d => d.AccountId == accountId).OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        var errors = await Batch.Each(due, id => RenewOne(accountId, id, today, ct), logger, "la renovación del servicio").ConfigureAwait(false);
        errors.AddRange(await Batch.Each(dueDomains, id => RenewDomain(accountId, id, today, ct), logger, "la renovación del dominio").ConfigureAwait(false));
        errors.AddRange(await Batch.Each([accountId], id => InvoiceItems(id, today, ct), logger, "la facturación de los ítems de la cuenta").ConfigureAwait(false));

        var drafts = await db.Documents
            .Where(d => d.AccountId == accountId && d.Kind == DocumentKind.Invoice && d.IssueDate == null && d.Reason == "renewal")
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        errors.AddRange(await Batch.Each(drafts, id => IssueAndApplyCredit(id, ct), logger, "la emisión del borrador").ConfigureAwait(false));
        if (errors.Count > 0)
        {
            throw new AggregateException($"La facturación de la cuenta {accountId} terminó con fallas.", errors);
        }

        return drafts.Count;
    }

    /// <summary>
    /// Lo común de las dos corridas: el barrido de borradores viejos, una vuelta por cada cuenta con algo que renovar
    /// y los vencimientos de dominios. Devuelve la suma de lo que dio cada cuenta.
    /// </summary>
    private async Task<int> Run(Func<long, DateOnly, Task<int>> perAccount, CancellationToken ct)
    {
        var today = core.Today();
        // Las cuentas con ítems facturables que ya tocan entran aunque no renueven nada hoy.
        var accounts = await DueServices(today).Select(s => s.AccountId).Union(DueDomains(today).Select(d => d.AccountId))
            .Union(db.BillableItems.Where(i => i.Status == BillableItemStatus.Pending && i.InvoiceOn <= today).Select(i => i.AccountId))
            .OrderBy(a => a).ToListAsync(ct).ConfigureAwait(false);

        // Al empezar, los borradores de más de una hora: los dejó una corrida que se cortó antes de emitir y sus
        // servicios ya avanzaron, así que nadie más los toca. Los más nuevos pueden ser de otra corrida que los llena.
        var stale = await db.Documents
            .Where(d => d.Kind == DocumentKind.Invoice && d.IssueDate == null && d.Reason == "renewal" && d.CreatedAt < time.GetUtcNow().AddHours(-1))
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var errors = await Batch.Each(stale, id => IssueAndApplyCredit(id, ct), logger, "la emisión del borrador").ConfigureAwait(false);

        var total = 0;
        errors.AddRange(await Batch.Each(accounts, async id => total += await perAccount(id, today).ConfigureAwait(false), logger, "la facturación de la cuenta").ConfigureAwait(false));
        await ExpireDomains(today, ct).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new AggregateException("La corrida de facturación terminó con fallas.", errors);
        }

        return total;
    }

    /// <summary>Servicios que entran en la corrida de hoy: vivos y con vencimiento dentro de la mayor anticipación.</summary>
    private IQueryable<Service> DueServices(DateOnly today)
    {
        var horizon = today.AddDays(core.Options.RenewalLeadDaysByMonths.Values.Append(core.Options.RenewalLeadDays).Max());
        return db.Services.Where(s => (s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended) && s.NextDueDate != null && s.NextDueDate <= horizon);
    }

    /// <summary>Dominios que se renuevan hoy (ver <see cref="DomainWindow"/>).</summary>
    private IQueryable<RegisteredDomain> DueDomains(DateOnly today)
    {
        var (from, to) = DomainWindow(today);
        return db.Set<RegisteredDomain>().Where(d => (d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace) && d.AutoRenew && d.NextDueDate > from && d.NextDueDate <= to);
    }

    /// <summary>
    /// Vuelve a facturar lo que liberó la anulación automática de impagas y le aplica el saldo a favor: los períodos
    /// de los servicios que siguen sin pagar (con el alta que iba en la misma factura, si era la de un pedido), la
    /// línea del pedido de un servicio por única vez que sigue pendiente y las renovaciones de dominio anuladas. Es
    /// para el cliente que paga después de la anulación: su dinero quedó como
    /// saldo y, sin esto, el servicio llega a la baja, el pedido no se aprovisiona y el dominio vence. Lo dispara un
    /// pago que deja saldo, y entonces solo factura lo que el saldo cubre entero, para no volver a cobrarle a quien
    /// dejó vencer. El staff lo puede pedir sin ese tope. Devuelve cuántas facturas emitió.
    /// </summary>
    public async Task<int> ReissueReleased(long accountId, bool onlyIfCovered, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);

        // También los pendientes: la anulación alcanza a la factura del pedido que nadie pagó.
        var services = await db.Services
            .Where(s => s.AccountId == accountId && (s.Status == ServiceStatus.Pending || s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended) && s.PaidThrough < s.NextDueDate)
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = services.Select(s => s.Id).ToList();
        var released = await db.BilledPeriods.Include(p => p.Line)
            .Where(p => ids.Contains(p.ServiceId) && p.Released
                && db.Documents.Any(n => n.Kind == DocumentKind.CreditNote && n.RelatedDocumentId == p.Line!.DocumentId && n.Reason == DunningService.CancelReason))
            .ToListAsync(ct).ConfigureAwait(false);
        var live = await db.BilledPeriods.Where(p => ids.Contains(p.ServiceId) && !p.Released).ToListAsync(ct).ConfigureAwait(false);

        // Solo lo que falta pagar y no está facturado de nuevo. Un período anulado dos veces vuelve una sola vez.
        var pending = services
            .SelectMany(s => released
                .Where(p => p.ServiceId == s.Id && p.Start >= s.PaidThrough && p.End <= s.NextDueDate
                    && !live.Any(l => l.ServiceId == s.Id && l.Start < p.End && p.Start < l.End))
                .GroupBy(p => (p.Start, p.End))
                .Select(g => (Service: s, Period: g.MaxBy(p => p.Id)!)))
            .ToList();

        // Los servicios por única vez no tienen período: la línea del pedido que nadie pagó vuelve si todas sus copias
        // están anuladas.
        var oneTime = new List<(Service Service, DocumentLine Line)>();
        foreach (var service in await db.Services.Where(s => s.AccountId == accountId && s.Status == ServiceStatus.Pending && s.NextDueDate == null).ToListAsync(ct).ConfigureAwait(false))
        {
            if (await LastCancelled(db.DocumentLines.Where(l => l.ServiceId == service.Id && l.Kind == LineKind.OneTime), ct).ConfigureAwait(false) is { } line)
            {
                oneTime.Add((service, line));
            }
        }

        // Los dominios tampoco: la línea anulada se reconoce por la clave del período que sigue sin pagar.
        var domains = new List<(RegisteredDomain Domain, DocumentLine Line, string Key)>();
        foreach (var domain in await db.Domains
            .Where(d => d.AccountId == accountId && (d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace) && d.PaidThrough < d.NextDueDate)
            .ToListAsync(ct).ConfigureAwait(false))
        {
            var key = DomainKey(domain.Id, domain.PaidThrough!.Value);
            if (await LastCancelled(db.DocumentLines.Where(l => l.DomainId == domain.Id && (l.DedupKey == key || l.DedupKey!.StartsWith(key + ":"))), ct).ConfigureAwait(false) is { } line)
            {
                domains.Add((domain, line, key));
            }
        }

        if (pending.Count == 0 && oneTime.Count == 0 && domains.Count == 0)
        {
            return 0;
        }

        var drafts = new List<Document>();
        // Una factura por moneda, medio de pago y modo de carga del precio, como la renovación.
        var groups = pending.Select(x => (x.Service.Currency, x.Service.PaymentMethod, x.Service.PriceMode))
            .Concat(oneTime.Select(x => (x.Service.Currency, x.Service.PaymentMethod, x.Service.PriceMode)))
            .Concat(domains.Select(x => (x.Domain.Currency, x.Domain.PaymentMethod, x.Domain.PriceMode)))
            .Distinct();
        foreach (var (currency, paymentMethod, priceMode) in groups)
        {
            var draft = new Document(DocumentKind.Invoice, accountId, currency, priceMode, core.Today().AddDays(core.Options.InvoiceDueDays), time.GetUtcNow(), reason: "renewal");
            var periods = new List<BilledPeriod>();
            foreach (var (service, period) in pending.Where(x => x.Service.Currency == currency && x.Service.PaymentMethod == paymentMethod && x.Service.PriceMode == priceMode).OrderBy(x => x.Period.Start))
            {
                var l = period.Line!;
                var line = draft.AddLine(LineKind.Renewal, l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, service.Id, category: l.Category, reason: l.Reason, legend: l.Legend);
                periods.Add(new BilledPeriod(service.Id, period.Start, period.End, line));
                await AddSetup(draft, service, l.DocumentId, ct).ConfigureAwait(false);
            }

            foreach (var (service, l) in oneTime.Where(x => x.Service.Currency == currency && x.Service.PaymentMethod == paymentMethod && x.Service.PriceMode == priceMode))
            {
                draft.AddLine(LineKind.OneTime, l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, service.Id, category: l.Category, reason: l.Reason, legend: l.Legend);
                await AddSetup(draft, service, l.DocumentId, ct).ConfigureAwait(false);
            }

            foreach (var (domain, l, key) in domains.Where(x => x.Domain.Currency == currency && x.Domain.PaymentMethod == paymentMethod && x.Domain.PriceMode == priceMode))
            {
                draft.AddLine(LineKind.Renewal, l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, dedupKey: await Reissued(key, ct).ConfigureAwait(false), category: l.Category, reason: l.Reason, legend: l.Legend, domainId: domain.Id);
            }

            // El saldo se lee sin lock: alcanza para decidir, y lo que se consume lo cuida ApplyTo.
            var balance = await db.CreditBalances.Where(b => b.AccountId == accountId && b.Currency == currency).SumAsync(b => b.Balance, ct).ConfigureAwait(false);
            if (onlyIfCovered && balance < draft.Compute().Total)
            {
                continue;
            }

            db.Documents.Add(draft);
            db.BilledPeriods.AddRange(periods);
            drafts.Add(draft);
        }

        if (drafts.Count == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.UniqueViolation })
        {
            // Otro proceso ya volvió a facturar alguno de esos períodos.
            return 0;
        }

        var receiver = await core.Receiver(accountId, ct).ConfigureAwait(false);
        foreach (var draft in drafts)
        {
            await core.IssueDraft(draft, receiver, ct).ConfigureAwait(false);
            await credit.ApplyTo(draft, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return drafts.Count;
    }

    /// <summary>
    /// La última copia de una línea sin período (la de un dominio o de un servicio por única vez) si todas están en
    /// facturas que anuló la mora. Una viva, en una factura abierta o en un borrador, es que ya se volvió a facturar.
    /// </summary>
    private async Task<DocumentLine?> LastCancelled(IQueryable<DocumentLine> copies, CancellationToken ct)
    {
        var lines = await copies
            .Select(l => new { Line = l, Cancelled = db.Documents.Any(n => n.Kind == DocumentKind.CreditNote && n.RelatedDocumentId == l.DocumentId && n.Reason == DunningService.CancelReason) })
            .ToListAsync(ct).ConfigureAwait(false);
        return lines.Count > 0 && lines.All(c => c.Cancelled) ? lines.MaxBy(c => c.Line.Id)!.Line : null;
    }

    /// <summary>El alta que iba en la misma factura (la de un pedido que nadie pagó) vuelve junto con lo que se reemite.</summary>
    private async Task AddSetup(Document draft, Service service, long documentId, CancellationToken ct)
    {
        foreach (var setup in await db.DocumentLines.Where(x => x.DocumentId == documentId && x.ServiceId == service.Id && x.Kind == LineKind.Setup).OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
        {
            var key = service.OrderLineId is { } orderLineId ? await Reissued($"setup:{orderLineId}", ct).ConfigureAwait(false) : null;
            draft.AddLine(LineKind.Setup, setup.Description, setup.Quantity, setup.UnitPrice, setup.Treatment, setup.Rate, service.Id, key, setup.Category, setup.Reason, setup.Legend);
        }
    }

    /// <summary>Clave de una línea que se vuelve a facturar: la de origen con un contador, porque la clave es única.</summary>
    private async Task<string> Reissued(string key, CancellationToken ct) =>
        $"{key}:{await db.DocumentLines.CountAsync(l => l.DedupKey == key || l.DedupKey!.StartsWith(key + ":"), ct).ConfigureAwait(false)}";

    /// <summary>Clave de la línea de renovación de un dominio: única por dominio y período.</summary>
    private static string DomainKey(long domainId, DateOnly periodStart) =>
        string.Create(CultureInfo.InvariantCulture, $"domain:{domainId}:{periodStart:yyyy-MM-dd}");

    private async Task<long?> RenewOne(long accountId, long serviceId, DateOnly today, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // La cuenta primero: dos renovaciones de la misma cuenta a la vez no arman dos borradores.
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", [serviceId], ct).ConfigureAwait(false);
        var service = await db.Services.SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);

        // Con baja pedida a fin de período no se factura nada desde esa fecha.
        if (service.CancelAt is { } cancelAt && service.NextDueDate >= cancelAt)
        {
            return null;
        }

        if (service.NextDueDate is not { } next || next > today.AddDays(core.Options.RenewalLeadDaysFor(service.Cycle)))
        {
            return null;
        }

        // Sin facturación continua, un servicio con períodos facturados sin pagar no suma facturas.
        if (!core.Options.ContinuousInvoicing && service.PaidThrough is { } paid && paid < next)
        {
            return null;
        }

        // El corte del uso es ayer: la muestra de ayer puede estar llegando y va a la ventana siguiente.
        var usageCut = today.AddDays(-1);

        // Precio pactado en otra moneda: se convierte con la cotización del día de la corrida y queda como el precio del
        // servicio. Sin cotización vigente el servicio no renueva y la corrida termina con el motivo, para reintentar.
        Conversion? indexed = null;
        if (service.IndexCurrency is { } index && service.IndexedUnitPrice is > 0m and var agreed)
        {
            indexed = await rates.Require(index, service.Currency, ct).ConfigureAwait(false);
            service.Reprice(indexed.Apply(agreed));
        }

        // Un período sin nada para cobrar (precio cero o descuento total) no se factura, pero corre igual: queda
        // pago y consume su ciclo de descuento. Si mide uso, el excedente sí se factura, en un borrador solo con eso.
        if (service.RecurringAmount <= 0m)
        {
            var free = service.TakeNextPeriod();
            if (service.PaidThrough == free.Start)
            {
                service.RenewalPaid();
            }

            Document? usageDraft = null;
            await usage.Close(service, WithRenewal, usageCut, async () =>
            {
                usageDraft ??= await Draft(service.AccountId, service.Currency, service.PriceMode, next, service.PaymentMethod, ct).ConfigureAwait(false);
                return usageDraft;
            }, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return usageDraft?.Id;
        }

        var product = await db.Products.SingleAsync(p => p.Id == service.ProductId, ct).ConfigureAwait(false);
        var receiver = await core.Receiver(service.AccountId, ct).ConfigureAwait(false);

        var name = CatalogQueries.WithOptions(product.Name, await CatalogQueries.Chosen(db, service.Id, ct).ConfigureAwait(false));

        // Fecha fiscal única: la regla se resuelve con el día de la corrida, que es el de emisión, y no con el del período.
        var tax = (await core.Taxes(ct).ConfigureAwait(false)).Line(product, receiver, service.AccountId, today);
        var draft = await Draft(service.AccountId, service.Currency, service.PriceMode, next, service.PaymentMethod, ct).ConfigureAwait(false);
        var unitPrice = service.DiscountedUnitPrice;
        var period = service.TakeNextPeriod();
        var line = draft.AddLine(LineKind.Renewal, BillingCore.Describe(name, period, service, indexed), service.Quantity, unitPrice, tax.Treatment, tax.Rate, service.Id, category: product.TaxCategory, reason: tax.Reason, legend: tax.Legend);
        db.BilledPeriods.Add(new BilledPeriod(service.Id, period.Start, period.End, line));

        // El excedente de uso va en la misma factura, en sus propios renglones y en la misma transacción.
        await usage.Close(service, WithRenewal, usageCut, () => Task.FromResult(draft), ct).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return draft.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
        {
            // Otra corrida ya facturó ese período: no se duplica.
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: null })
        {
            // El trigger de líneas congeladas: el borrador se emitió entre que se leyó y se le agregó la línea. Con el
            // lock de la cuenta no debería pasar, queda de resguardo. El servicio no avanzó y se renueva en la próxima.
            return null;
        }
    }

    /// <summary>
    /// Factura los ítems sueltos de la cuenta que ya tocan: van al borrador de renovación de la cuenta en su moneda y su
    /// modo de carga, si lo hay, o a uno nuevo con el plazo de siempre, que la corrida de la cuenta emite con los demás.
    /// El precio de un ítem está en el modo de carga por defecto de su moneda. Con la cuenta tomada, como la renovación,
    /// y cada ítem bloqueado y vuelto a leer: uno cancelado o facturado a la vez por el staff no entra.
    /// </summary>
    private async Task InvoiceItems(long accountId, DateOnly today, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        var ids = await db.BillableItems.Where(i => i.AccountId == accountId && i.Status == BillableItemStatus.Pending && i.InvoiceOn <= today)
            .Select(i => i.Id).ToListAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.billable_items", ids, ct).ConfigureAwait(false);
        var items = await db.BillableItems.Where(i => ids.Contains(i.Id) && i.Status == BillableItemStatus.Pending).OrderBy(i => i.Id).ToListAsync(ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return;
        }

        var open = await db.Documents.Include(d => d.Lines)
            .Where(d => d.AccountId == accountId && d.Kind == DocumentKind.Invoice && d.IssueDate == null && d.Reason == DocumentReasons.Renewal)
            .OrderBy(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        var receiver = await core.Receiver(accountId, ct).ConfigureAwait(false);
        var taxes = await core.Taxes(ct).ConfigureAwait(false);
        var used = new List<(Document Draft, List<BillableItem> Items)>();
        foreach (var group in items.GroupBy(i => i.Currency))
        {
            var mode = taxes.Settings.DefaultPriceMode(group.Key);
            var draft = open.Find(d => d.Currency == group.Key && d.PriceMode == mode);
            if (draft is null)
            {
                draft = new Document(DocumentKind.Invoice, accountId, group.Key, mode, today.AddDays(core.Options.InvoiceDueDays), time.GetUtcNow(), reason: DocumentReasons.Renewal);
                db.Documents.Add(draft);
            }

            BillableItemService.AddTo(draft, group, receiver, taxes, today);
            used.Add((draft, group.ToList()));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var (draft, group) in used)
        {
            group.ForEach(i => i.MarkInvoiced(draft.Id, time.GetUtcNow()));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dominios que se renuevan hoy: vencen dentro de la anticipación propia de los dominios y todavía no pasaron la
    /// gracia, también los que están en gracia sin la renovación facturada, por ejemplo porque se volvió a prender la
    /// renovación automática. Uno vencido hace más que la gracia no se factura: pasa a vencido y lo decide el staff.
    /// </summary>
    private (DateOnly From, DateOnly To) DomainWindow(DateOnly today) =>
        (today.AddDays(-core.Options.DomainGraceDays), today.AddDays(core.Options.DomainRenewalLeadDays));

    /// <summary>
    /// Renueva un dominio en su transacción corta. A precio cero no hay factura: avanza el período y se le pide la
    /// renovación al registrar igual, porque el registro la cobra aunque el cliente no pague.
    /// </summary>
    private async Task<long?> RenewDomain(long accountId, long domainId, DateOnly today, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // La cuenta primero, como en la renovación de servicios: los dos llenan el mismo borrador.
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        await Sql.LockRows(db, "domains.domains", [domainId], ct).ConfigureAwait(false);
        var domain = await db.Set<RegisteredDomain>().SingleAsync(d => d.Id == domainId, ct).ConfigureAwait(false);
        var (from, to) = DomainWindow(today);
        if (domain.Status is not (DomainStatus.Active or DomainStatus.Grace) || !domain.AutoRenew || domain.NextDueDate is not { } next || next <= from || next > to)
        {
            return null;
        }

        // Sin facturación continua, una renovación facturada sin pagar no suma otra. Tampoco una que anuló la mora: esa
        // la vuelve a facturar Renovar ahora, o la reemisión cuando el cliente paga después.
        if (!core.Options.ContinuousInvoicing && domain.PaidThrough is { } paid && paid < next)
        {
            return null;
        }

        if (domain.RecurringAmount <= 0m)
        {
            domain.TakeNextPeriod();
            var renewed = domain.RenewalPaid(today)!.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await DomainJobs.RequestRenewal(db, jobs, domain, renewed, time.GetUtcNow(), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        // Un dominio no tiene producto: va como servicio digital, con la fecha fiscal de la corrida y el modo de carga
        // congelado con su precio, como un servicio. Vence el día del período, y si ese día ya pasó (en gracia, o una
        // anulada que se reemite) a los días de hoy: una factura que nace vencida la anularía la mora antes de poder pagarla.
        var receiver = await core.Receiver(domain.AccountId, ct).ConfigureAwait(false);
        var tax = (await core.Taxes(ct).ConfigureAwait(false)).Line(TaxCategories.Digital, receiver, domain.AccountId, today);
        var due = next < today ? today.AddDays(core.Options.InvoiceDueDays) : next;
        var draft = await Draft(domain.AccountId, domain.Currency, domain.PriceMode, due, domain.PaymentMethod, ct).ConfigureAwait(false);
        var period = domain.TakeNextPeriod();
        var line = await DomainJobs.RenewalLine(db, domain, period, ct).ConfigureAwait(false);
        draft.AddLine(LineKind.Renewal, line.Description, 1, line.Amount, tax.Treatment, tax.Rate,
            dedupKey: line.Key, category: TaxCategories.Digital, reason: tax.Reason, legend: tax.Legend, domainId: domain.Id);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return draft.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Otra corrida ya facturó ese período: no se duplica.
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: null })
        {
            // El trigger de líneas congeladas, de resguardo como en los servicios: el dominio se renueva en la próxima.
            return null;
        }
    }

    /// <summary>
    /// Vencimientos de los dominios según lo pago: pasado lo pago entran en gracia, y pasada la gracia quedan vencidos.
    /// Lo que dice el registro lo trae aparte la sincronización diaria, en el estado del registro, sin tocar este.
    /// </summary>
    private async Task ExpireDomains(DateOnly today, CancellationToken ct)
    {
        var cutoff = today.AddDays(-core.Options.DomainGraceDays);

        // En una transacción, para que pase por la cerca del trabajo y por la guarda del COMMIT.
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Set<RegisteredDomain>()
            .Where(d => (d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace) && d.PaidThrough != null && d.PaidThrough <= cutoff)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DomainStatus.Expired), ct).ConfigureAwait(false);
        await db.Set<RegisteredDomain>()
            .Where(d => d.Status == DomainStatus.Active && d.PaidThrough != null && d.PaidThrough < today)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DomainStatus.Grace), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Un borrador por cuenta, moneda, modo de carga, vencimiento y medio de pago: cada factura tiene un solo
    /// vencimiento. Lo comparten los servicios y los dominios de la cuenta.
    /// </summary>
    private async Task<Document> Draft(long accountId, Currency currency, PriceMode mode, DateOnly due, string? paymentMethod, CancellationToken ct)
    {
        var candidates = await db.Documents.Include(d => d.Lines)
            .Where(d => d.AccountId == accountId && d.Currency == currency && d.PriceMode == mode && d.Kind == DocumentKind.Invoice && d.IssueDate == null && d.Reason == "renewal" && d.DueDate == due)
            .OrderBy(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            var services = candidate.Lines.Where(l => l.ServiceId != null).Select(l => l.ServiceId!.Value).ToList();
            var domains = candidate.Lines.Where(l => l.DomainId != null).Select(l => l.DomainId!.Value).ToList();
            if (await db.Services.Where(s => services.Contains(s.Id)).AllAsync(s => s.PaymentMethod == paymentMethod, ct).ConfigureAwait(false)
                && await db.Set<RegisteredDomain>().Where(d => domains.Contains(d.Id)).AllAsync(d => d.PaymentMethod == paymentMethod, ct).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        var draft = new Document(DocumentKind.Invoice, accountId, currency, mode, due, time.GetUtcNow(), reason: "renewal");
        db.Documents.Add(draft);
        return draft;
    }

    private async Task IssueAndApplyCredit(long draftId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Con la cuenta tomada, como la renovación: así nadie le agrega una línea mientras se emite. La clave foránea
        // de la línea haría esperar a la emisión, pero el trigger de líneas congeladas ya habría pasado y la línea
        // quedaría en un documento emitido sin entrar en sus importes.
        var accountId = await db.Documents.Where(d => d.Id == draftId).Select(d => d.AccountId).SingleAsync(ct).ConfigureAwait(false);
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", [draftId], ct).ConfigureAwait(false);
        var draft = await db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == draftId, ct).ConfigureAwait(false);
        if (draft.IsIssued)
        {
            return;
        }

        await core.IssueDraft(draft, await core.Receiver(draft.AccountId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await credit.ApplyTo(draft, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
    }
}

/// <summary>Un pago dejó saldo: vuelve a facturar lo que liberó la anulación automática, si el saldo lo cubre.</summary>
public sealed class ReissueReleasedHandler(BillingRunService billing) : IJobHandler
{
    public string Kind => JobKinds.ReissueReleased;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var accountId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("account_id").GetInt64();
        await billing.ReissueReleased(accountId, onlyIfCovered: true, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>Saldo a favor: consumo al emitir y notas de crédito (secciones 6 y 6.3 del modelo de dominio).</summary>
public sealed class CreditService(BillingDbContext db, JobQueue jobs, BillingCore core, TimeProvider time)
{
    /// <summary>Consume saldo a favor de la cuenta para pagar el documento. Debe correr dentro de una transacción.</summary>
    public async Task ApplyTo(Document doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);
        await Sql.LockCreditBalance(db, doc.AccountId, doc.Currency.Code, ct).ConfigureAwait(false);
        var balance = await db.CreditBalances.SingleAsync(b => b.AccountId == doc.AccountId && b.Currency == doc.Currency, ct).ConfigureAwait(false);
        await db.Entry(balance).ReloadAsync(ct).ConfigureAwait(false);
        var amount = Math.Min(balance.Balance, doc.Pending);
        if (amount <= 0m)
        {
            return;
        }

        var now = time.GetUtcNow();
        var entry = new CreditEntry(doc.AccountId, doc.Currency, -amount, "Consumo de saldo", now, documentId: doc.Id);
        db.CreditEntries.Add(entry);
        balance.Balance -= amount;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        doc.Allocate(amount);
        db.Allocations.Add(new Allocation(doc.Id, amount, 1m, now, creditEntryId: entry.Id));
        await jobs.Publish(Events.PaymentAllocated, new { document_id = doc.Id }, await FiscalEmissionHandler.EntityKey(db, doc, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (doc.Pending == 0m)
        {
            await core.InvoicePaid(doc, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Nota de crédito contra una factura, topeada por alícuota. Primero baja lo que se debe; si la factura ya
    /// estaba pagada, el resto vuelve como saldo a favor. Si la anula por completo, libera sus períodos, que vuelven a
    /// estar por facturar, y cierra el pedido de dominio que esperaba su pago. La renovación de un dominio vuelve a estar
    /// por facturar también cuando la nota acredita entero su renglón.
    /// </summary>
    public async Task<long> IssueCreditNote(long invoiceId, IReadOnlyList<CreditLine> lines, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (note, _) = await Credit(invoiceId, lines, reason, ct).ConfigureAwait(false) ?? throw new UnreachableException();
        await core.Commit(tx, ct).ConfigureAwait(false);
        return note.Id;
    }

    /// <summary>
    /// Anula una factura impaga por todo lo que le falta acreditar. La nota sale de los importes emitidos menos las
    /// notas previas y no de las líneas, así cierra exacto aunque las líneas no coincidan con la cabecera, como pasa
    /// con algunos importados. Decide con la factura bloqueada: si ya tiene algo imputado no emite y devuelve null,
    /// y si ya no queda nada por acreditar devuelve la nota que otra corrida emitió antes.
    /// </summary>
    public async Task<long?> CancelIfUnpaid(long invoiceId, string reason, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await Credit(invoiceId, null, reason, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return result?.Note.Id;
    }

    /// <summary>
    /// La nota de crédito dentro de la transacción del que llama. Sin líneas es la anulación de una impaga. Devuelve
    /// la nota y lo ya pagado que volvió como saldo a favor, que es lo que un reembolso puede devolver por la pasarela.
    /// </summary>
    internal async Task<(Document Note, decimal Returned)?> Credit(long invoiceId, IReadOnlyList<CreditLine>? lines, string reason, CancellationToken ct)
    {
        // El original es una factura o una nota de débito, por ejemplo la de un recargo por mora.
        await Sql.LockRows(db, "billing.documents", [invoiceId], ct).ConfigureAwait(false);
        var invoice = await db.Documents.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == invoiceId && d.Kind != DocumentKind.CreditNote && d.IssueDate != null, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la factura o nota de débito emitida {invoiceId}.");
        if (lines is null && (invoice.Allocated != 0m || invoice.Pending <= 0m))
        {
            // Con algo pagado no se anula sola. Si ya no queda nada por acreditar, otra corrida la anuló primero y se
            // devuelve esa nota.
            var existing = invoice.Allocated != 0m ? null : await db.Documents
                .Where(d => d.RelatedDocumentId == invoiceId && d.Kind == DocumentKind.CreditNote && d.IssueDate != null)
                .OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return existing is null ? null : (existing, 0m);
        }

        var previous = await db.Documents.Where(d => d.RelatedDocumentId == invoiceId && d.Kind == DocumentKind.CreditNote && d.IssueDate != null)
            .ToListAsync(ct).ConfigureAwait(false);
        var credited = previous.Select(p => p.IssuedBreakdown()).ToList();
        var creditedBefore = invoice.Lines.Any(l => l.Kind == LineKind.Renewal && l.DomainId is not null) ? await DomainJobs.CreditedDomains(db, invoiceId, ct).ConfigureAwait(false) : null;
        var remaining = CreditNoteRules.Remaining(invoice.IssuedBreakdown(), credited);

        var note = new Document(DocumentKind.CreditNote, invoice.AccountId, invoice.Currency, invoice.PriceMode, core.Today(), time.GetUtcNow(), invoice.Id, reason);
        TaxBreakdown capped;
        if (lines is null)
        {
            capped = remaining with { Aliquots = [.. remaining.Aliquots.Where(a => a.Net != 0m || a.Tax != 0m)] };
            var description = string.Create(CultureInfo.InvariantCulture, $"Anulación de la {(invoice.Kind == DocumentKind.DebitNote ? "nota de débito" : "factura")} {invoice.Number}");
            foreach (var a in capped.Aliquots)
            {
                note.AddLine(LineKind.Manual, description, 1, invoice.PriceMode == PriceMode.TaxIncluded ? a.Net + a.Tax : a.Net, TaxTreatment.Taxed, a.Rate);
            }

            if (capped.Exempt > 0m)
            {
                note.AddLine(LineKind.Manual, description, 1, capped.Exempt, TaxTreatment.Exempt, 0m);
            }

            if (capped.NotTaxed > 0m)
            {
                note.AddLine(LineKind.Manual, description, 1, capped.NotTaxed, TaxTreatment.NotTaxed, 0m);
            }
        }
        else
        {
            // Una línea sin categoría (la API no la exige) toma la del renglón de su servicio o su dominio en la factura, o
            // la única que la factura tenga: con ella la nota revierte los tributos de esa categoría, como una percepción
            // de telecomunicaciones. Una sin servicio ni dominio con el texto del renglón de un dominio es ese renglón, y
            // la nota lo lleva con su dominio.
            foreach (var l in lines)
            {
                var domainId = l.DomainId ?? (l.ServiceId is null ? invoice.Lines.FirstOrDefault(x => x.DomainId is not null && x.Description == l.Description)?.DomainId : null);
                var categories = invoice.Lines.Where(x => (l.ServiceId is null || x.ServiceId == l.ServiceId) && (domainId is null || x.DomainId == domainId)).Select(x => x.Category).Distinct().ToList();
                note.AddLine(LineKind.Manual, l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, l.ServiceId, category: l.Category ?? (categories.Count == 1 ? categories[0] : null), domainId: domainId);
            }

            capped = CreditNoteRules.Apply(invoice.IssuedBreakdown(), credited, note.Compute());
        }

        db.Documents.Add(note);

        // Los tributos de la nota se recalculan sobre su base (los renglones de sus categorías) con la alícuota del
        // original, con tope en lo que falta revertir. La que completa el original toma exactamente el resto.
        var completes = lines is null || capped.Total == remaining.Total;
        var noteTaxes = DocumentTaxRules.ForNote(invoice.IssuedTaxes(), [.. previous.Select(p => p.IssuedTaxes())], note.TaxedLines(), completes);

        // La nota lleva el receptor congelado en la factura, no el actual de la cuenta: corrige aquel comprobante. Una
        // importada no lo trae y va con el de la cuenta.
        await core.IssueDraft(note, await core.ReceiverOf(invoice, ct).ConfigureAwait(false), ct, capped, noteTaxes).ConfigureAwait(false);

        // El recargo impago de una factura que esta nota anula se anula con ella y con el mismo motivo, la anule la mora,
        // el staff o un reembolso: la deuda que recargaba ya no existe. Uno cobrado lo resuelve el staff. Va antes de
        // tocar la factura y el saldo, así respeta el orden de locks de la sección 13 (documentos y después saldo).
        if (invoice.Credited + note.Total >= invoice.Total)
        {
            var fees = await db.Documents
                .Where(n => n.RelatedDocumentId == invoiceId && n.Kind == DocumentKind.DebitNote && n.Reason == DunningService.LateFeeReason && n.IssueDate != null && n.Allocated == 0 && n.Total - n.Credited > 0)
                .OrderBy(n => n.Id).Select(n => n.Id).ToListAsync(ct).ConfigureAwait(false);
            foreach (var fee in fees)
            {
                await Credit(fee, null, reason, ct).ConfigureAwait(false);
            }
        }

        var pendingBefore = invoice.Pending;
        var reducesDebt = Math.Min(note.Total, pendingBefore);
        var refundable = note.Total - reducesDebt;
        invoice.AddCredit(note.Total);
        if (refundable > 0m)
        {
            // Lo ya pagado que la nota devuelve sale de las imputaciones, de la más nueva a la más vieja, y queda como
            // saldo a favor con su pago de origen: lo imputado sigue siendo la suma de las imputaciones vigentes, y el
            // pago sigue aplicado porque su plata está en el saldo.
            invoice.ReturnPaid(refundable);
            await Sql.LockCreditBalance(db, invoice.AccountId, invoice.Currency.Code, ct).ConfigureAwait(false);
            var balance = await db.CreditBalances.SingleAsync(b => b.AccountId == invoice.AccountId && b.Currency == invoice.Currency, ct).ConfigureAwait(false);
            await db.Entry(balance).ReloadAsync(ct).ConfigureAwait(false);
            balance.Balance += refundable;
            var now = time.GetUtcNow();
            var left = refundable;
            var allocations = await db.Allocations.Where(a => a.DocumentId == invoice.Id && a.ReversedAt == null).OrderByDescending(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
            foreach (var allocation in allocations)
            {
                var take = Math.Min(left, allocation.Amount);
                if (take <= 0m)
                {
                    break;
                }

                if (allocation.ReverseUpTo(take, now) is { } rest)
                {
                    db.Allocations.Add(rest);
                }

                db.CreditEntries.Add(new CreditEntry(invoice.AccountId, invoice.Currency, take, "Nota de crédito", now, allocation.PaymentId, note.Id));
                left -= take;
            }

            // Un importado puede tener imputaciones que no suman lo imputado: el resto entra al saldo sin pago de origen.
            if (left > 0m)
            {
                db.CreditEntries.Add(new CreditEntry(invoice.AccountId, invoice.Currency, left, "Nota de crédito", now, documentId: note.Id));
            }
        }

        // Una nota parcial que cubre lo que faltaba deja la factura paga, como un pago: avanza lo pagado y reactiva.
        if (pendingBefore > 0m && invoice.Status == InvoiceStatus.Paid)
        {
            await core.InvoicePaid(invoice, ct).ConfigureAwait(false);
        }

        if (invoice.Status == InvoiceStatus.Cancelled)
        {
            var periods = await db.BilledPeriods.Where(p => p.Line!.DocumentId == invoice.Id && !p.Released).ToListAsync(ct).ConfigureAwait(false);
            periods.ForEach(p => p.Release());
            await CloseOrders(invoice, reason, ct).ConfigureAwait(false);

            // Un cambio de plan que esperaba esta factura ya no se va a pagar.
            var changes = await db.ServiceChanges.Where(c => c.DocumentId == invoice.Id && c.Status == ServiceChangeStatus.Pending).ToListAsync(ct).ConfigureAwait(false);
            changes.ForEach(c => c.Cancel());
        }

        if (creditedBefore is not null && reason != DunningService.CancelReason)
        {
            await ReleaseDomainRenewals(invoice, creditedBefore, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (note, refundable);
    }

    /// <summary>
    /// Lo que para los servicios es liberar los períodos facturados: una renovación de dominio que la nota sacó de la
    /// factura (la anuló entera, o acreditó entero su renglón) vuelve a estar por facturar, así la corrida o Renovar ahora
    /// la emiten de nuevo con la clave siguiente del período (<see cref="DomainJobs.RenewalLine"/>). Un renglón sin clave
    /// de período, como los importados o uno hecho a mano, es el del período que sigue a lo pago, salvo que otra factura
    /// abierta lo cubra: entonces es de uno anterior, como una importada ya paga que se reintegra. Solo cuenta la nota que
    /// lo saca: uno que ya estaba acreditado no se vuelve a liberar. Lo que anula la mora no se libera, porque la corrida lo
    /// volvería a emitir y la mora a anular cada día: lo vuelve a facturar Renovar ahora, o
    /// <see cref="BillingRunService.ReissueReleased"/> cuando el cliente paga después y su saldo lo cubre.
    /// </summary>
    private async Task ReleaseDomainRenewals(Document invoice, IReadOnlySet<long> before, CancellationToken ct)
    {
        var credited = invoice.Status == InvoiceStatus.Cancelled ? null : await DomainJobs.CreditedDomains(db, invoice.Id, ct).ConfigureAwait(false);
        var renewals = invoice.Lines.Where(l => l.Kind == LineKind.Renewal && l.DomainId is { } d && !before.Contains(d) && (credited is null || credited.Contains(d))).ToList();
        if (renewals.Count == 0)
        {
            return;
        }

        var ids = renewals.Select(l => l.DomainId!.Value).Distinct().ToList();
        await Sql.LockRows(db, "domains.domains", ids, ct).ConfigureAwait(false);
        var domains = await db.Domains.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct).ConfigureAwait(false);
        foreach (var line in renewals)
        {
            var domain = domains[line.DomainId!.Value];
            var start = DomainJobs.RenewalPeriodStart(line.DedupKey)
                ?? (domain.PaidThrough is { } paid && await DomainJobs.OpenRenewalInvoice(db, domain.Id, paid, ct, except: invoice.Id).ConfigureAwait(false) is null ? paid : null);
            if (start is { } period)
            {
                domain.RenewalCancelled(period);
            }
        }
    }

    /// <summary>
    /// Un pedido de dominio con su factura anulada entera (la mora, o el staff) ya no se paga: se cierra como al
    /// cancelarlo y el nombre queda libre para volver a pedirlo. Uno en curso en el registrar sigue, y si falla lo
    /// cancela el staff.
    /// </summary>
    private async Task CloseOrders(Document invoice, string reason, CancellationToken ct)
    {
        var ids = invoice.Lines.Where(l => l.Kind == LineKind.OneTime && l.DomainId is not null).Select(l => l.DomainId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        await Sql.LockRows(db, "domains.domains", ids, ct).ConfigureAwait(false);
        foreach (var domain in await db.Domains.Where(d => ids.Contains(d.Id) && d.Status == DomainStatus.Pending).ToListAsync(ct).ConfigureAwait(false))
        {
            await DomainJobs.CloseOrder(db, domain, reason, core.Now(), ct).ConfigureAwait(false);
        }
    }
}

public sealed record CreditLine(string Description, decimal Quantity, decimal UnitPrice, TaxTreatment Treatment, decimal Rate, long? ServiceId = null, string? Category = null, long? DomainId = null);

/// <summary>
/// Mora por servicio (sección 5.2 del modelo de dominio), medida desde lo pagado de cada servicio: suspende pasada
/// la gracia, anula las facturas impagas pasado su plazo y da de baja pasado el plazo de baja.
/// </summary>
public sealed class DunningService(BillingDbContext db, JobQueue jobs, BillingCore core, CreditService credit, ILogger<DunningService> logger)
{
    /// <summary>Motivo de la nota de la anulación automática. Marca los períodos que se pueden volver a facturar.</summary>
    public const string CancelReason = "Factura impaga anulada por vencimiento";

    /// <summary>Motivo de las notas de débito del recargo por mora: hay una sola por factura.</summary>
    public const string LateFeeReason = "Recargo por mora";

    /// <summary>
    /// Un recargo que no salió por una caída sale igual dentro de esta ventana, y no más tarde: prender el recargo no
    /// castiga deudas viejas.
    /// </summary>
    public const int LateFeeCatchUpDays = 7;

    /// <summary>Devuelve cuántos servicios mandó a suspender.</summary>
    public async Task<int> Run(CancellationToken ct = default)
    {
        var today = core.Today();
        var options = core.Options;

        var held = await Held(db, ct).ConfigureAwait(false);

        // Bajas pedidas a fin de período: se dan de baja el día programado, sin pasar por la mora.
        var scheduled = await db.Services
            .Where(s => (s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended) && s.CancelAt != null && s.CancelAt <= today)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in scheduled.Where(s => !held.Contains(s.Id)))
        {
            await jobs.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(s.Id, "terminate", s.Generation), $"service:{s.Id}", $"service:{s.Id}:terminate:{s.Generation}:cancel-at", ct: ct).ConfigureAwait(false);
        }

        // Los servicios sin nada para cobrar (precio cero) no entran en mora.
        var overdue = await db.Services
            .Where(s => (s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended) && s.PaidThrough != null && s.PaidThrough < today && s.UnitPrice > 0)
            .Where(s => s.CancelAt == null || s.CancelAt > today)
            .ToListAsync(ct).ConfigureAwait(false);
        var productIds = overdue.Select(s => s.ProductId).Distinct().ToList();
        var termination = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.TerminationDays ?? options.TerminationDays, ct).ConfigureAwait(false);
        var accountIds = overdue.Select(s => s.AccountId).Distinct().ToList();
        var balances = await db.CreditBalances.Where(b => accountIds.Contains(b.AccountId)).ToListAsync(ct).ConfigureAwait(false);
        var suspended = 0;
        foreach (var s in overdue)
        {
            var balance = balances.Where(b => b.AccountId == s.AccountId && b.Currency == s.Currency).Sum(b => b.Balance);
            if (DueOperation(s, today, options.GraceDays, termination[s.ProductId], balance, await core.PeriodTotal(s, ct).ConfigureAwait(false)) is { } operation && !(operation == "terminate" && held.Contains(s.Id)))
            {
                // Lo pagado y el día van en la clave: una operación que un pago dejó sin efecto queda hecha, y la clave
                // de una deuda nueva o de otro día no choca con ella. El manejador la revisa igual antes de mandarla.
                await jobs.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(s.Id, operation, s.Generation, Dunning: true), $"service:{s.Id}", $"service:{s.Id}:{operation}:{s.Generation}:{s.PaidThrough:yyyy-MM-dd}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
                suspended += operation == "suspend" ? 1 : 0;
            }
        }

        // Un recargo que falla no frena la anulación de las impagas: la corrida termina con todas las fallas juntas, las
        // de los recargos y las de las anulaciones, y se reintenta.
        var errors = options.LateFeeAfterDays is { } feeDays
            ? await ChargeLateFees(today.AddDays(-feeDays), options.CancelUnpaidAfterDays is { } c ? today.AddDays(-c) : null, ct).ConfigureAwait(false)
            : [];

        if (options.CancelUnpaidAfterDays is { } cancelDays)
        {
            errors.AddRange(await CancelUnpaid(today.AddDays(-cancelDays), ct).ConfigureAwait(false));
        }

        return errors.Count == 0 ? suspended : throw new AggregateException("La mora terminó con fallas en recargos o anulaciones.", errors);
    }

    /// <summary>
    /// El recargo por mora de una factura vencida e impaga: una nota de débito por el porcentaje de lo que debe más el
    /// fijo de su moneda, repartido como sus alícuotas, así lleva el impuesto de lo que recarga (Ley de IVA, art. 10). Se
    /// decide con la factura bloqueada: una que se pagó mientras tanto no lo recibe. El staff lo pide sin esperar los días
    /// de la corrida, pero nunca antes del vencimiento. La nota consume saldo a favor como cualquier documento. Devuelve
    /// la nota, o null si no corresponde.
    /// </summary>
    public async Task<long?> ChargeLateFee(long invoiceId, CancellationToken ct = default)
    {
        var options = core.Options;
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", [invoiceId], ct).ConfigureAwait(false);
        var invoice = await db.Documents.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == invoiceId && d.Kind == DocumentKind.Invoice, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la factura {invoiceId}.");
        if (!invoice.IsIssued || invoice.Pending <= 0m || invoice.Status == InvoiceStatus.Cancelled || invoice.DueDate >= core.Today()
            || await db.Documents.AnyAsync(d => d.RelatedDocumentId == invoiceId && d.Kind == DocumentKind.DebitNote && d.Reason == LateFeeReason, ct).ConfigureAwait(false)
            || await db.Accounts.AnyAsync(a => a.Id == invoice.AccountId && a.NoLateFees, ct).ConfigureAwait(false))
        {
            return null;
        }

        // El porcentaje va sobre lo que se debe sin los tributos de comprobante de la factura (una percepción) y la nota
        // suma los suyos: así es el porcentaje de todo lo que se debe, y no un recargo sobre la percepción con otra encima.
        var owed = invoice.OtherTaxes == 0m ? invoice.Pending : Rounding.Commercial(invoice.Pending * (invoice.Total - invoice.OtherTaxes) / invoice.Total);
        // La moneda del fijo se compara sin distinguir mayúsculas, como los límites de la carga de saldo: "ars" es "ARS".
        var fixedFee = options.LateFeeFixed.FirstOrDefault(p => string.Equals(p.Key, invoice.Currency.Code, StringComparison.OrdinalIgnoreCase)).Value;
        var fee = Rounding.Commercial(owed * options.LateFeePercent / 100m) + fixedFee;
        if (fee <= 0m)
        {
            return null;
        }

        var note = new Document(DocumentKind.DebitNote, invoice.AccountId, invoice.Currency, invoice.PriceMode, core.Today().AddDays(options.InvoiceDueDays), core.Now(), invoice.Id, LateFeeReason);
        var issued = invoice.IssuedBreakdown();
        var parts = issued.Aliquots.Select(a => (Treatment: TaxTreatment.Taxed, a.Rate, Gross: a.Net + a.Tax))
            .Append((Treatment: TaxTreatment.Exempt, Rate: 0m, Gross: issued.Exempt)).Append((Treatment: TaxTreatment.NotTaxed, Rate: 0m, Gross: issued.NotTaxed))
            .Where(p => p.Gross > 0m).ToList();
        var total = parts.Sum(p => p.Gross);
        var cumulative = 0m;
        var shares = new List<TaxableLine>();
        foreach (var (treatment, rate, gross) in parts)
        {
            var before = Rounding.Commercial(fee * cumulative / total);
            cumulative += gross;
            var share = Rounding.Commercial(fee * cumulative / total) - before;
            if (share > 0m)
            {
                shares.Add(new TaxableLine(shares.Count.ToString(CultureInfo.InvariantCulture), share, treatment, rate));
            }
        }

        // Cada parte es un bruto y la nota cierra en el recargo con cualquier modo de precios: con precios sin IVA el
        // renglón lleva el neto y el IVA es la diferencia, como en un precio con IVA y dentro de la tolerancia del fisco.
        // Calcular el IVA otra vez sobre el neto redondeado corría el total un centavo.
        var amounts = TaxCalculator.Compute(PriceMode.TaxIncluded, shares);
        var description = string.Create(CultureInfo.InvariantCulture, $"Recargo por mora de la factura {invoice.Number}");
        foreach (var (share, split) in shares.Zip(amounts.Lines))
        {
            // Lleva la categoría, el motivo y la leyenda de los renglones de la factura con esa alícuota, si son unos solos.
            var origin = invoice.Lines.Where(l => l.Treatment == share.Treatment && l.Rate == share.Rate).Select(l => (l.Category, l.Reason, l.Legend)).Distinct().ToList();
            var (category, why, legend) = origin.Count == 1 ? origin[0] : default;
            var amount = note.PriceMode == PriceMode.TaxExcluded ? split.Net : share.Amount;
            note.AddLine(LineKind.LateFee, description, 1m, amount, share.Treatment, share.Rate, dedupKey: string.Create(CultureInfo.InvariantCulture, $"late-fee:{invoice.Id}:{note.Lines.Count}"), category: category, reason: why, legend: legend);
        }

        db.Documents.Add(note);
        await core.IssueDraft(note, await core.ReceiverOf(invoice, ct).ConfigureAwait(false), ct, amounts).ConfigureAwait(false);
        await credit.ApplyTo(note, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return note.Id;
    }

    /// <summary>
    /// Recarga las facturas impagas que vencieron hasta la fecha, dentro de la ventana. Una que la misma corrida va a
    /// anular por impaga no se recarga: serían una nota de débito y otra de crédito por nada. Una que falla no frena a
    /// las demás.
    /// </summary>
    private async Task<List<Exception>> ChargeLateFees(DateOnly dueUpTo, DateOnly? cancelUpTo, CancellationToken ct)
    {
        var dueFrom = dueUpTo.AddDays(-LateFeeCatchUpDays + 1);
        var ids = await db.Documents.AsNoTracking()
            .Where(d => d.Kind == DocumentKind.Invoice && d.IssueDate != null && d.DueDate >= dueFrom && d.DueDate <= dueUpTo && d.Total - d.Credited - d.Allocated > 0)
            .Where(d => cancelUpTo == null || d.DueDate > cancelUpTo || d.Allocated != 0)
            .Where(d => !db.Documents.Any(n => n.RelatedDocumentId == d.Id && n.Kind == DocumentKind.DebitNote && n.Reason == LateFeeReason))
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return await Batch.Each(ids, id => ChargeLateFee(id, ct), logger, "el recargo por mora de la factura").ConfigureAwait(false);
    }

    /// <summary>
    /// Bajas retenidas: los servicios con una revisión abierta (la que abre el importador por una baja que el sistema
    /// anterior dejó sin ejecutar). La mora no los da de baja hasta que el staff la cierra.
    /// </summary>
    public static async Task<HashSet<long>> Held(BillingDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var subjects = await db.StaffTasks.AsNoTracking().Where(t => t.Kind == StaffTask.Review && t.Status == StaffTaskStatus.Open)
            .Select(t => t.Subject).ToListAsync(ct).ConfigureAwait(false);
        return [.. subjects.Select(StaffTask.ReviewedService).OfType<long>()];
    }

    /// <summary>
    /// Lo que le toca hoy por mora a un servicio, medido desde lo pagado: la baja, la suspensión o nada. Si toca la
    /// baja va directo, porque una suspensión del mismo día cambiaría la generación y la dejaría sin efecto. La usan la
    /// mora al encolar y el manejador de operaciones antes de mandarlas al módulo. Con saldo a favor que alcanza para
    /// un período con sus impuestos (<paramref name="periodTotal"/>, lo mismo que exige la reemisión) no se da de
    /// baja: el cliente pagó y falta aplicarlo. Las bajas pedidas a fin de período no pasan por acá: las encola la
    /// corrida de mora el día programado.
    /// </summary>
    public static string? DueOperation(Service service, DateOnly today, int graceDays, int? terminationDays, decimal balance, decimal periodTotal)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.Status is not (ServiceStatus.Active or ServiceStatus.Suspended) || service.UnitPrice <= 0m || service.PaidThrough is not { } paid || paid >= today)
        {
            return null;
        }

        if (terminationDays is { } days && paid.AddDays(days) <= today && balance < periodTotal)
        {
            return "terminate";
        }

        // Exento de la suspensión automática (por ejemplo un servicio interno): la baja sigue corriendo.
        return service.Status == ServiceStatus.Active && !service.NoAutoSuspend && paid.AddDays(graceDays) <= today ? "suspend" : null;
    }

    /// <summary>
    /// Anula con una nota de crédito las facturas impagas que vencieron hasta la fecha. Una factura con algo pagado
    /// no se anula sola: la resuelve el staff. Una que falla no frena a las demás y su falla vuelve al que llama.
    /// </summary>
    private async Task<List<Exception>> CancelUnpaid(DateOnly dueUpTo, CancellationToken ct)
    {
        // Las facturas de uso no: el consumo ya se prestó y no hay períodos que liberar (sección 5 de diseno/uso-medido.md).
        var ids = await db.Documents.AsNoTracking()
            .Where(d => d.Kind == DocumentKind.Invoice && d.IssueDate != null && d.DueDate <= dueUpTo && d.Allocated == 0 && d.Total - d.Credited > 0 && d.Reason != UsageBilling.InvoiceReason)
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        // El recargo impago de cada una se anula con ella, en la misma nota de crédito (CreditService.Credit).
        return await Batch.Each(ids, id => credit.CancelIfUnpaid(id, CancelReason, ct), logger, "la anulación de la factura impaga").ConfigureAwait(false);
    }
}

/// <summary>
/// Corre el trabajo de cada ID por separado: si uno falla, queda en el log y sigue con el resto. Devuelve las fallas
/// para que el llamador las relance al final, así el trabajo se reintenta y, si no se resuelve, queda muerto y
/// visible para el staff.
/// </summary>
internal static class Batch
{
    public static async Task<List<Exception>> Each(IEnumerable<long> ids, Func<long, Task> work, ILogger logger, string what)
    {
        List<Exception> errors = [];
        foreach (var id in ids)
        {
            try
            {
                await work(id).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló {What} {Id}", what, id);
                errors.Add(ex);
            }
        }

        return errors;
    }
}
