using System.Globalization;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>
/// Lo que se pide cambiar de un servicio: otro producto, otro ciclo u otras opciones. Lo que no viene queda como está,
/// y las opciones pedidas pisan solo las suyas. Sin prorrateo el cambio vale desde la próxima renovación.
/// </summary>
public sealed record ChangeRequest(long? ProductId = null, string? Cycle = null, IReadOnlyList<OptionChoice>? Options = null, bool Prorate = true);

/// <summary>
/// Un cambio calculado: los precios unitarios de antes y de después (sin descuento) y el prorrateo hasta lo ya
/// facturado. Positivo se cobra con una factura, negativo se acredita con notas de crédito. <paramref name="DiscountEnds"/>
/// dice que el descuento del servicio (de su cupón o de su paquete) no vale para el producto o el ciclo nuevo: el cambio
/// lo termina.
/// </summary>
public sealed record ChangeQuote(long ServiceId, long FromProductId, long ToProductId, string Cycle, string Currency, decimal FromUnitPrice, decimal ToUnitPrice, decimal Amount, DateOnly From, DateOnly? Until, IReadOnlyList<OptionChoice> Options, bool DiscountEnds = false);

/// <summary>El cambio pedido: pendiente del pago de su factura o ya aplicado, con las notas de crédito que emitió.</summary>
public sealed record ChangeResult(long ChangeId, ServiceChangeStatus Status, decimal Amount, long? InvoiceId, IReadOnlyList<long> CreditNoteIds);

/// <summary>
/// Cambios de plan y de opciones con prorrateo (diseno/catalogo.md). Subir cobra la diferencia hasta lo ya facturado con
/// una factura, y el cambio se aplica cuando se paga. Bajar acredita la diferencia con una nota de crédito contra cada
/// factura que la cobró, y se aplica enseguida. El ciclo se cambia solo y vale desde la próxima renovación.
/// </summary>
public sealed class ServiceChangeService(BillingDbContext db, BillingCore core, CreditService credit, AuditLog audit, TimeProvider time)
{
    /// <summary>Motivo de las notas de crédito de una baja de plan.</summary>
    public const string CreditReason = "Cambio de plan";

    public async Task<ChangeQuote> Preview(long serviceId, ChangeRequest request, bool byCustomer = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el servicio {serviceId}.");
        return (await Quote(service, request, byCustomer, ct).ConfigureAwait(false)).Quote;
    }

    public async Task<ChangeResult> Request(long serviceId, ChangeRequest request, bool byCustomer = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var accountId = await db.Services.Where(s => s.Id == serviceId).Select(s => (long?)s.AccountId).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el servicio {serviceId}.");

        // Los locks van en el orden de la sección 13 del modelo: la cuenta (como la renovación, así el precio y lo
        // facturado no cambian mientras se calcula), las facturas que se acreditan, el saldo y el servicio.
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        var snapshot = await db.Services.AsNoTracking().SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
        if (await db.ServiceChanges.AnyAsync(c => c.ServiceId == serviceId && c.Status == ServiceChangeStatus.Pending, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"El servicio {serviceId} ya tiene un cambio esperando el pago de su factura.");
        }

        var (quote, product) = await Quote(snapshot, request, byCustomer, ct).ConfigureAwait(false);
        var credits = quote.Amount < 0m ? await CreditTargets(snapshot, quote, ct).ConfigureAwait(false) : [];
        await Sql.LockRows(db, "billing.documents", credits.Select(c => c.InvoiceId), ct).ConfigureAwait(false);
        await Sql.LockCreditBalance(db, accountId, snapshot.Currency.Code, ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", [serviceId], ct).ConfigureAwait(false);
        var service = await db.Services.SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
        if (service.Status != ServiceStatus.Active)
        {
            throw new InvalidOperationException($"El servicio {serviceId} no está activo: solo se cambia un servicio activo.");
        }

        var now = time.GetUtcNow();
        var change = new ServiceChange(serviceId, snapshot.ProductId, product.Id, BillingCycle.Parse(quote.Cycle), snapshot.UnitPrice, quote.ToUnitPrice, JsonSerializer.Serialize(quote.Options), quote.Amount, now)
        {
            BilledUntil = quote.Until,
            EndsDiscount = quote.DiscountEnds,
        };
        db.ServiceChanges.Add(change);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        long? invoiceId = null;
        var notes = new List<long>();
        if (quote.Amount > 0m)
        {
            invoiceId = await core.IssueChangeInvoice(service, product, Describe(product.Name, quote.From, quote.Until), quote.Amount, $"change:{change.Id}", ct).ConfigureAwait(false);
            change.DocumentId = invoiceId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Como toda factura, el saldo a favor la paga si alcanza: entonces el cambio se aplica ya, con el pago.
            var invoice = await db.Documents.SingleAsync(d => d.Id == invoiceId, ct).ConfigureAwait(false);
            await credit.ApplyTo(invoice, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var (target, amount, treatment, rate) in credits)
            {
                // Nunca más de lo que a la factura le queda por acreditar: una baja anterior pudo haber acreditado parte.
                var left = await db.Documents.Where(d => d.Id == target).Select(d => d.Total - d.Credited).SingleAsync(ct).ConfigureAwait(false);
                if (Math.Min(amount, left) > 0m
                    && await credit.Credit(target, [new CreditLine(Describe(product.Name, quote.From, quote.Until), 1, Math.Min(amount, left), treatment, rate, serviceId)], CreditReason, ct).ConfigureAwait(false) is { } note)
                {
                    notes.Add(note.Note.Id);
                }
            }

            change.DocumentId = notes.Count > 0 ? notes[0] : null;
            await core.ApplyChange(change, service, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("service.change.requested", $"service:{serviceId}", new { change_id = change.Id, by_customer = byCustomer, amount = change.Amount, to_product_id = change.ToProductId }, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return new ChangeResult(change.Id, change.Status, change.Amount, invoiceId, notes);
    }

    /// <summary>
    /// Cancela un cambio que espera el pago: anula su factura impaga con una nota de crédito, y la anulación cancela el
    /// cambio. Si la factura ya tiene algo pagado no se anula sola: la resuelve el staff con una nota de crédito.
    /// </summary>
    public async Task Cancel(long changeId, CancellationToken ct = default)
    {
        var change = await db.ServiceChanges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == changeId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el cambio {changeId}.");
        if (change.Status != ServiceChangeStatus.Pending)
        {
            throw new InvalidOperationException($"El cambio {changeId} ya está {change.Status}.");
        }

        if (await credit.CancelIfUnpaid(change.DocumentId!.Value, "Cambio de plan cancelado", ct).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"La factura del cambio {changeId} tiene pagos: se resuelve con una nota de crédito.");
        }

        await audit.TryRecord("service.change.cancelled", $"service:{change.ServiceId}", new { change_id = changeId }).ConfigureAwait(false);
    }

    /// <summary>
    /// Factura la diferencia de los períodos que se facturaron con el precio viejo mientras el cambio esperaba el pago.
    /// La clave de la línea impide facturarla dos veces.
    /// </summary>
    public async Task BillGap(long changeId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var change = await db.ServiceChanges.AsNoTracking().SingleAsync(c => c.Id == changeId, ct).ConfigureAwait(false);
        var accountId = await db.Services.Where(s => s.Id == change.ServiceId).Select(s => s.AccountId).SingleAsync(ct).ConfigureAwait(false);
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        var service = await db.Services.AsNoTracking().SingleAsync(s => s.Id == change.ServiceId, ct).ConfigureAwait(false);
        var key = $"change:{change.Id}:gap";
        if (change.BilledUntil is not { } from || service.NextDueDate is not { } until || until <= from || !service.Cycle.IsRecurring
            || await db.DocumentLines.AnyAsync(l => l.DedupKey == key, ct).ConfigureAwait(false))
        {
            return;
        }

        // Cada período se facturó con el precio viejo y el descuento que tenía entonces, que el cambio pudo haber terminado:
        // la diferencia sale de lo que cobró su renglón. Uno sin renglón (lo facturó el sistema anterior) toma el precio
        // viejo con el descuento del servicio.
        var billed = (await db.BilledPeriods.Where(p => p.ServiceId == service.Id && p.Start >= from && p.Start < until)
                .Select(p => new { p.Id, p.Start, p.Line!.UnitPrice }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => p.Start)
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.Id)!.UnitPrice);
        var amount = Rounding.Commercial(BillingSchedule.ProratePeriods(
            p => (service.DiscountedIn(p, change.ToUnitPrice) - (billed.TryGetValue(p.Start, out var paid) ? paid : service.DiscountedIn(p, change.FromUnitPrice))) * service.Quantity,
            from, until, service.Cycle, service.AnchorDay).Sum(p => p.Amount));
        if (amount <= 0m)
        {
            return;
        }

        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == change.ToProductId, ct).ConfigureAwait(false);
        var invoiceId = await core.IssueChangeInvoice(service, product, Describe(product.Name, from, until), amount, key, ct).ConfigureAwait(false);
        await credit.ApplyTo(await db.Documents.SingleAsync(d => d.Id == invoiceId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
    }

    private static string Describe(string productName, DateOnly from, DateOnly? until) =>
        until is { } end && end > from
            ? string.Create(CultureInfo.InvariantCulture, $"Cambio a {productName} ({from:dd/MM/yyyy} al {end.AddDays(-1):dd/MM/yyyy})")
            : $"Cambio a {productName}";

    private async Task<(ChangeQuote Quote, Product Product)> Quote(Service service, ChangeRequest request, bool byCustomer, CancellationToken ct)
    {
        if (service.Status != ServiceStatus.Active)
        {
            throw new InvalidOperationException($"El servicio {service.Id} no está activo: solo se cambia un servicio activo.");
        }

        var today = core.Today();
        var current = await db.Products.AsNoTracking().SingleAsync(p => p.Id == service.ProductId, ct).ConfigureAwait(false);
        var product = request.ProductId is { } productId && productId != current.Id
            ? await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct).ConfigureAwait(false) ?? throw new ValidationException($"No existe el producto {productId}.")
            : current;
        if (product.Id != current.Id)
        {
            if (byCustomer && !current.UpgradeProductIds.Contains(product.Id))
            {
                throw new ValidationException($"{current.Name} no se puede pasar a {product.Name}.");
            }

            // El cambio se hace sobre el mismo recurso: un producto de otra instancia de módulo es una baja y un alta.
            if (product.ModuleInstanceId != current.ModuleInstanceId || product.Addon != current.Addon)
            {
                throw new ValidationException($"{product.Name} se aprovisiona en otro lado: el cambio va como baja y alta.");
            }
        }

        BillingCycle cycle;
        try
        {
            cycle = request.Cycle is null ? service.Cycle : BillingCycle.Parse(request.Cycle);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message);
        }

        if (cycle.IsRecurring != service.Cycle.IsRecurring)
        {
            throw new ValidationException("Un servicio recurrente no pasa a un ciclo sin renovación, ni al revés.");
        }

        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.ProductId == product.Id && p.CycleUnit == cycle.Unit && p.CycleCount == cycle.Count && p.Active, ct).ConfigureAwait(false)
            ?? throw new ValidationException($"{product.Name} no se vende con el ciclo {cycle.Code}.");
        var price = await CatalogQueries.Price(db, plan.Id, service.Currency, today, ct).ConfigureAwait(false)
            ?? throw new ValidationException($"{product.Name} no tiene precio en {service.Currency}.");

        // Lo pedido pisa lo que el servicio tiene. Lo que tiene y no es del producto nuevo queda afuera.
        var catalog = await CatalogQueries.Options(db, product.Id, ct).ConfigureAwait(false);
        var had = await CatalogQueries.Chosen(db, service.Id, ct).ConfigureAwait(false);
        var asked = request.Options ?? [];
        var merged = had.Select(c => c.Choice)
            .Where(c => catalog.Options.Any(o => o.Id == c.OptionId) && asked.All(a => a.OptionId != c.OptionId))
            .Concat(asked)
            .ToList();
        IReadOnlyList<ChosenOption> chosen;
        try
        {
            chosen = catalog.Resolve(merged, service.Currency, cycle);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message);
        }

        // El cliente no elige lo oculto, salvo el valor que su servicio ya tiene.
        if (byCustomer && OptionCatalog.HiddenPick(chosen, asked, had.Select(h => h.Value.Id)) is { } hidden)
        {
            throw new ValidationException($"{hidden.Option.Name}: {hidden.Value.Name} no se ofrece.");
        }

        // Pasar a otro producto permitido cambia sus opciones: el permiso de cambiar opciones es para el mismo producto.
        var optionsChanged = !Keys(chosen).SetEquals(Keys(had));
        if (byCustomer && optionsChanged && product.Id == current.Id && !product.OptionChanges)
        {
            throw new ValidationException($"Las opciones de {product.Name} las cambia el staff.");
        }

        var cycleChange = cycle != service.Cycle;
        if (cycleChange && (product.Id != current.Id || optionsChanged))
        {
            throw new ValidationException("El ciclo se cambia solo, sin cambiar de plan ni de opciones.");
        }

        if (!cycleChange && product.Id == current.Id && !optionsChanged)
        {
            throw new ValidationException("No hay nada que cambiar.");
        }

        // Se prorratea lo que ya está facturado: desde hoy, o desde el fin de la prueba gratis, hasta el próximo período
        // a facturar, con el descuento que tuvo cada período.
        // En el mismo producto y el mismo ciclo cambia solo el precio de las opciones, sobre lo que el servicio ya paga: un
        // precio viejo o importado, con su promoción adentro, no se pierde por cambiar una opción. Otro producto u otro
        // ciclo toman el precio de lista.
        var toUnitPrice = product.Id == current.Id && !cycleChange
            ? Math.Max(0m, service.UnitPrice - catalog.Cost(had.Select(h => h.Choice), service.Currency, cycle) + chosen.Sum(c => c.Amount))
            : price.Amount + chosen.Sum(c => c.Amount);
        var from = service.TrialEndsOn is { } trialEnd && trialEnd > today ? trialEnd : today;

        // El descuento del servicio sigue con sus opciones en el mismo producto y ciclo, y en otro solo si su cupón vale ahí:
        // el de un paquete es de sus planes. Si no sigue, lo que queda del período va a precio lleno y el cambio lo termina.
        var keeps = (product.Id == current.Id && !cycleChange)
            || (service.CouponId is { } couponId
                && await db.Coupons.AsNoTracking().SingleOrDefaultAsync(c => c.Id == couponId, ct).ConfigureAwait(false) is { } coupon
                && coupon.AppliesTo(product.Id, cycle, service.Currency));
        var discountEnds = !keeps && service.Discounting(from);
        var amount = request.Prorate && !cycleChange && service.Cycle.IsRecurring && service.NextDueDate is { } until && until > from
            ? Rounding.Commercial(Difference(service, service.UnitPrice, toUnitPrice, !discountEnds, from, until).Sum(p => p.Amount))
            : 0m;
        return (new ChangeQuote(service.Id, current.Id, product.Id, cycle.Code, service.Currency.Code, service.UnitPrice, toUnitPrice, amount, from, service.NextDueDate, [.. chosen.Select(c => c.Choice)], discountEnds), product);
    }

    /// <summary>
    /// Lo que cambia cada período ya facturado entre <paramref name="since"/> y <paramref name="until"/>, del precio viejo al
    /// nuevo por la cantidad del servicio y en proporción a lo que le queda. El viejo lleva el descuento que tuvo ese
    /// período, y el nuevo también si el descuento sigue (<paramref name="keepsDiscount"/>): si no, va a precio lleno.
    /// </summary>
    private static IReadOnlyList<(Period Period, decimal Amount)> Difference(Service service, decimal fromPrice, decimal toPrice, bool keepsDiscount, DateOnly since, DateOnly until) =>
        BillingSchedule.ProratePeriods(p => ((keepsDiscount ? service.DiscountedIn(p, toPrice) : toPrice) - service.DiscountedIn(p, fromPrice)) * service.Quantity, since, until, service.Cycle, service.AnchorDay);

    /// <summary>Lo elegido como conjunto comparable: en las listas la cantidad no cuenta.</summary>
    private static HashSet<(long, long, int)> Keys(IEnumerable<ChosenOption> chosen) =>
        [.. chosen.Select(c => (c.Option.Id, c.Value.Id, c.Option.SingleValue ? c.Quantity : 1))];

    /// <summary>
    /// Contra qué factura va la parte de cada período ya facturado: la del período reservado o, en un servicio importado,
    /// su última factura de renovación. Un período sin factura (gratis o anulado) no tiene nada que acreditar.
    /// </summary>
    private async Task<List<(long InvoiceId, decimal Amount, TaxTreatment Treatment, decimal Rate)>> CreditTargets(Service service, ChangeQuote quote, CancellationToken ct)
    {
        var targets = new List<(long InvoiceId, decimal Amount, TaxTreatment Treatment, decimal Rate)>();
        foreach (var (period, amount) in Difference(service, service.UnitPrice, quote.ToUnitPrice, !quote.DiscountEnds, quote.From, quote.Until!.Value))
        {
            var line = await db.BilledPeriods.Where(p => p.ServiceId == service.Id && p.Start == period.Start && !p.Released).Select(p => p.Line).FirstOrDefaultAsync(ct).ConfigureAwait(false)
                ?? await db.DocumentLines
                    .Where(l => l.ServiceId == service.Id && l.Kind == LineKind.Renewal && db.Documents.Any(d => d.Id == l.DocumentId && d.Imported && d.Kind == DocumentKind.Invoice && d.Total > d.Credited))
                    .OrderByDescending(l => l.DocumentId)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (line is not null)
            {
                targets.Add((line.DocumentId, -amount, line.Treatment, line.Rate));
            }
        }

        return [.. targets.GroupBy(t => t.InvoiceId)
            .Select(g => (g.Key, Rounding.Commercial(g.Sum(t => t.Amount)), g.First().Treatment, g.First().Rate))
            .Where(t => t.Item2 > 0m)];
    }
}

/// <summary>Factura aparte la diferencia de un cambio de plan pagado después de otra renovación (<see cref="ServiceChangeService.BillGap"/>).</summary>
public sealed class ChangeGapHandler(ServiceChangeService changes) : Platform.IJobHandler
{
    public string Kind => Platform.JobKinds.ChangeGap;

    public async Task<Platform.JobResult> Handle(Platform.ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await changes.BillGap(JsonDocument.Parse(job.Payload).RootElement.GetProperty("change_id").GetInt64(), ct).ConfigureAwait(false);
        return Platform.JobResult.Done;
    }
}
