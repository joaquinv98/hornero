using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BillingOss.App.UseCases;

/// <summary>
/// Una línea del pedido: el plan, la cantidad y las opciones elegidas. Un complemento cuelga de un servicio vivo de la
/// cuenta (<paramref name="ParentServiceId"/>) o de otra línea del mismo pedido (<paramref name="ParentItem"/>, su índice).
/// <paramref name="Bundled"/> dice que la línea es del paquete del pedido.
/// </summary>
public sealed record OrderItem(long PlanId, int Quantity = 1, string? Label = null, IReadOnlyList<OptionChoice>? Options = null, string OrderData = "{}", long? ParentServiceId = null, int? ParentItem = null, bool Bundled = false);

/// <summary>InvoiceId es la primera factura del pedido. Un pedido que mezcla modos de carga sale en una factura por modo: InvoiceIds las lista todas.</summary>
public sealed record PlacedOrder(long OrderId, long? InvoiceId, IReadOnlyList<long> ServiceIds, IReadOnlyList<long>? InvoiceIds = null);

/// <summary>
/// Lo pactado en un presupuesto aceptado: el precio de cada línea del pedido, que sigue en las renovaciones y reemplaza
/// el del catálogo y su cargo de alta, y renglones sueltos que van en la misma factura. Solo lo arma el núcleo: ni la API
/// ni el portal lo reciben.
/// </summary>
public sealed record QuotedTerms(IReadOnlyList<decimal> UnitPrices, IReadOnlyList<LineInput> Extras);

/// <summary>
/// Una línea del pedido calculada: lo que el servicio va a cobrar por unidad y período con sus opciones, con y sin el
/// descuento del cupón o del paquete, el cargo de alta y el fin de la prueba gratis si le toca.
/// </summary>
public sealed record OrderQuoteLine(
    long PlanId,
    long ProductId,
    string Description,
    string Cycle,
    int Quantity,
    decimal UnitPrice,
    decimal DiscountedUnitPrice,
    decimal SetupFee,
    decimal? DiscountPercent,
    decimal? DiscountAmount,
    int? DiscountCycles,
    DateOnly? TrialEndsOn,
    int? ParentItem,
    long? ParentServiceId,
    bool Bundled);

/// <summary>
/// Un pedido calculado sin confirmarlo: sus líneas y la factura que saldría hoy, con el neto, el impuesto y el total. Total
/// cero es un pedido que se acepta sin factura.
/// </summary>
public sealed record OrderQuote(string Currency, IReadOnlyList<OrderQuoteLine> Lines, decimal Net, decimal Tax, decimal Total);

/// <summary>Confirmar un pedido: un solo caso de uso para el portal, la API propia y la API compatible.</summary>
public sealed class OrderService(BillingDbContext db, JobQueue jobs, BillingCore core, CreditService credit, ExchangeRateService rates, OrderValidation validation, FraudScreening screening, TimeProvider time)
{
    /// <summary>
    /// Calcula el pedido con las mismas reglas que <see cref="PlaceOrder"/> sin tocar nada: ni stock, ni usos del cupón,
    /// ni servicios, ni factura. Lo usan el carrito del portal y la API antes de confirmar.
    /// </summary>
    public async Task<OrderQuote> Quote(long accountId, IReadOnlyList<OrderItem> items, string? couponCode = null, long? bundleId = null, CancellationToken ct = default, bool byCustomer = false)
    {
        Validate(items, bundleId);
        var today = core.Today();
        var now = time.GetUtcNow();
        var resolved = await Resolve(accountId, items, couponCode, bundleId, today, now, byCustomer, ct).ConfigureAwait(false);

        // Las líneas se leen antes de armar la factura: tomar el primer período consume un ciclo de descuento.
        var lines = resolved.Lines.Select(p => new OrderQuoteLine(
            p.Plan.Id,
            p.Product.Id,
            CatalogQueries.WithOptions(p.Product.Name, p.Chosen),
            p.Plan.Cycle.Code,
            p.Item.Quantity,
            p.Service.UnitPrice,
            p.Service.DiscountedUnitPrice,
            p.SetupFee,
            p.Service.DiscountPercent,
            p.Service.DiscountAmount,
            p.Service.DiscountCyclesLeft,
            p.Service.TrialEndsOn,
            p.Item.ParentItem,
            p.Item.ParentServiceId,
            p.Item.Bundled)).ToList();
        var invoices = new Dictionary<PriceMode, Document>();
        foreach (var p in resolved.Lines)
        {
            AddFirstLines(InvoiceFor(invoices, p.Service.PriceMode, accountId, resolved.Account.Currency, today, now), p, resolved.Receiver, resolved.Taxes, accountId, today, setupKey: null);
        }

        var breakdowns = invoices.Values.Where(i => i.Lines.Count > 0).Select(i => i.Compute()).ToList();
        var total = breakdowns.Sum(b => b.Total);
        var tax = breakdowns.Sum(b => b.Tax);
        return new OrderQuote(resolved.Account.Currency.Code, lines, total - tax, tax, total);
    }

    /// <summary>
    /// Con <paramref name="clientReference"/> (la Idempotency-Key) el mismo pedido mandado dos veces se confirma una
    /// sola: la clave vale por API key y cuenta, y guarda la huella del pedido, así la misma clave con otro pedido se
    /// rechaza en vez de devolver el viejo. Con <paramref name="quoted"/> es la conversión de un presupuesto: sus precios
    /// pactados y sus renglones sueltos, dentro de la transacción del presupuesto.
    /// </summary>
    /// <param name="byCustomer">Lo pide el cliente desde el portal: no elige opciones ni valores ocultos.</param>
    /// <param name="fraud">
    /// Quién compra, para la evaluación de fraude. Sin él se toma el de la sesión del portal, y los pedidos del staff o
    /// de la API sin IP del cliente no se evalúan (diseno/fraude-y-acceso.md, sección 3.2).
    /// </param>
    public async Task<PlacedOrder> PlaceOrder(long accountId, IReadOnlyList<OrderItem> items, CancellationToken ct = default, string? clientReference = null, string? paymentMethod = null, string? couponCode = null, long? bundleId = null, bool byCustomer = false, long? apiKeyId = null, FraudContext? fraud = null, QuotedTerms? quoted = null)
    {
        Validate(items, bundleId);
        if (quoted is not null && quoted.UnitPrices.Count != items.Count)
        {
            throw new ArgumentException("Hace falta un precio pactado por línea del pedido.", nameof(quoted));
        }

        if (clientReference is { Length: > 200 })
        {
            throw new ValidationException("La Idempotency-Key tiene como máximo 200 caracteres.");
        }

        var fingerprint = clientReference is null ? null : Fingerprint(accountId, items, paymentMethod, couponCode, bundleId);
        if (clientReference is not null && await Existing(accountId, clientReference, apiKeyId, fingerprint, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        // Las extensiones que validan pedidos, antes de abrir la transacción: nunca se llama a un módulo con una abierta.
        // Un presupuesto llega dentro de la suya: QuoteService lo validó antes de abrirla, con los mismos planes.
        if (quoted is null)
        {
            await validation.Check(accountId, items, ct).ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        var today = core.Today();

        // Dentro de la transacción del que llama, si la hay: el presupuesto que se convierte y su pedido van juntos.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;

        // La cuenta primero, como la facturación: dos pedidos de la misma cuenta a la vez no se cruzan con el cupón de un
        // uso por cuenta ni con la prueba gratis.
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);

        // Con la cuenta bloqueada se mira otra vez: de dos envíos a la vez, el segundo esperó al primero y ve su pedido.
        if (clientReference is not null && await Existing(accountId, clientReference, apiKeyId, fingerprint, ct).ConfigureAwait(false) is { } concurrent)
        {
            return concurrent;
        }

        var (account, coupon, bundle, receiver, taxes, priced) = await Resolve(accountId, items, couponCode, bundleId, today, now, byCustomer, ct, quoted).ConfigureAwait(false);
        var order = new Order(accountId, account.Currency, now) { ClientReference = clientReference, ApiKeyId = apiKeyId, RequestFingerprint = fingerprint, CouponId = coupon?.Id, BundleId = bundle?.Id };
        db.Orders.Add(order);
        var lines = new List<OrderLine>();
        foreach (var p in priced)
        {
            await TakeStock(p.Product, p.Item.Quantity, ct).ConfigureAwait(false);
            p.Service.PaymentMethod = paymentMethod;
            lines.Add(order.AddLine(p.Product.Id, p.Plan.Id, p.Item.Quantity, p.Service.UnitPrice, p.SetupFee, JsonSerializer.Serialize(p.Chosen.Select(c => c.Choice)), p.Item.OrderData));
            db.Services.Add(p.Service);
        }

        if (coupon is not null)
        {
            await UseCoupon(coupon, ct).ConfigureAwait(false);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (tx is not null && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_orders_api_key_id_account_id_client_reference" })
        {
            // El mismo pedido dos veces al mismo tiempo: el índice único decide y el que pierde vuelve a buscar la clave,
            // así devuelve el que quedó, o lo rechaza si era otro pedido. Dentro de la transacción de un presupuesto no
            // pasa: el presupuesto bloqueado se convierte una sola vez.
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return await PlaceOrder(accountId, items, ct, clientReference, paymentMethod, couponCode, bundleId, byCustomer, apiKeyId, fraud, quoted).ConfigureAwait(false);
        }

        // Antes de aprovisionar nada: si va a revisión, el pedido queda retenido desde esta misma transacción.
        await screening.Start(order, account, fraud, ct).ConfigureAwait(false);

        // Una factura por modo de carga: casi siempre es una sola, porque cada moneda suele tener un solo modo.
        var invoices = new Dictionary<PriceMode, Document>();
        var periods = new List<(Document Invoice, BilledPeriod Period, Service Service)>();
        for (var i = 0; i < priced.Count; i++)
        {
            var (item, service, chosen) = (priced[i].Item, priced[i].Service, priced[i].Chosen);
            lines[i].ServiceId = service.Id;
            service.OrderLineId = lines[i].Id;
            if (item.ParentItem is { } parent)
            {
                service.ParentServiceId = priced[parent].Service.Id;
            }

            db.ServiceOptions.AddRange(chosen.Select(c => new ServiceOption(service.Id, c.Option.Id, c.Value.Id, c.Quantity)));
            var invoice = InvoiceFor(invoices, service.PriceMode, accountId, account.Currency, today, now);
            if (AddFirstLines(invoice, priced[i], receiver, taxes, accountId, today, $"setup:{lines[i].Id}") is { } period)
            {
                periods.Add((invoice, period, service));
            }
        }

        // Los renglones sueltos de un presupuesto van en la factura del modo de carga de la moneda, como sus precios.
        if (quoted is { Extras.Count: > 0 })
        {
            InvoiceService.AddTaxed(InvoiceFor(invoices, taxes.Settings.DefaultPriceMode(account.Currency), accountId, account.Currency, today, now), quoted.Extras, LineKind.Manual, taxes, receiver, today);
        }

        var services = priced.Select(p => p.Service).ToList();
        var issued = new List<long>();
        var billed = new HashSet<long>();

        // Un pedido que no cierra (una bonificación mayor que lo gravado) es un error del pedido, no una falla interna.
        foreach (var invoice in invoices.Values.Where(i => i.Lines.Count > 0 && InvoiceService.Total(i) > 0m))
        {
            db.Documents.Add(invoice);
            await core.IssueDraft(invoice, receiver, ct).ConfigureAwait(false);

            // Como en la renovación, el saldo a favor de la cuenta paga la factura nueva.
            await credit.ApplyTo(invoice, ct).ConfigureAwait(false);
            issued.Add(invoice.Id);
            billed.UnionWith(invoice.Lines.Where(l => l.ServiceId != null).Select(l => l.ServiceId!.Value));
        }

        // Un período queda facturado solo en una factura emitida. El de una factura que quedó en cero (un descuento total)
        // no tiene nada que cobrar: corre igual y queda pago, como en la renovación, así la mora no lo toma por impago.
        foreach (var (invoice, period, service) in periods)
        {
            if (invoice.IsIssued)
            {
                db.BilledPeriods.Add(period);
            }
            else
            {
                service.RenewalPaid();
            }
        }

        if (issued.Count > 0)
        {
            order.InvoiceId = issued[0];
        }
        else if (priced.All(p => p.Product.Activation != ProductActivation.Manual))
        {
            // Total cero: se acepta sin factura. Lo que espera al staff deja el pedido pendiente hasta que lo acepte.
            order.Status = OrderStatus.Accepted;
        }

        // Al pedir se aprovisiona enseguida. Al pagar espera su factura, salvo que no tenga nada que pagar ahora (una prueba
        // gratis o un plan gratis): no espera el pago del resto. Lo que acepta el staff no lo aprovisiona ni el pago.
        foreach (var p in priced.Where(p => p.Product.Activation switch
        {
            ProductActivation.OnOrder => true,
            ProductActivation.Manual => false,
            _ => !billed.Contains(p.Service.Id),
        }))
        {
            await core.StartProvisioning(p.Service, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await jobs.Publish(Events.OrderPlaced, new { order_id = order.Id }, ct: ct).ConfigureAwait(false);
        if (tx is not null)
        {
            await core.Commit(tx, ct).ConfigureAwait(false);
        }

        return new PlacedOrder(order.Id, order.InvoiceId, [.. services.Select(s => s.Id)], issued);
    }

    /// <summary>
    /// Acepta el pedido a mano y aprovisiona lo pendiente, aunque no esté pagado. Un pedido retenido por la evaluación de
    /// fraude no se acepta así: se decide en la revisión.
    /// </summary>
    public async Task Accept(long orderId, bool provision, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var order = await db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el pedido {orderId}.");
        if (await db.Set<FraudCheck>().AnyAsync(f => f.OrderId == orderId && (f.Status == ScreeningStatus.Pending || f.Status == ScreeningStatus.Review), ct).ConfigureAwait(false))
        {
            throw new ValidationException("El pedido está retenido por la evaluación de fraude: se aprueba o se rechaza en la revisión.");
        }

        order.Status = OrderStatus.Accepted;
        if (provision)
        {
            var serviceIds = order.Lines.Where(l => l.ServiceId != null).Select(l => l.ServiceId!.Value).ToList();
            foreach (var service in await db.Services.Where(s => serviceIds.Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false))
            {
                await core.StartProvisioning(service, ct).ConfigureAwait(false);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Con Billing:ExchangeRates:IndexPrices, el precio del plan en la primera de las monedas cotizadas que lo tenga, y la
    /// cotización de hoy para convertirlo a la de la cuenta. Sin cotización vigente el pedido falla con el motivo.
    /// </summary>
    private async Task<(PlanPrice Price, Conversion Rate)?> IndexedPrice(long planId, Currency currency, DateOnly today, CancellationToken ct)
    {
        if (!core.Options.ExchangeRates.IndexPrices)
        {
            return null;
        }

        foreach (var code in core.Options.ExchangeRates.Currencies)
        {
            var other = InvoiceService.ParseCurrency(code);
            if (other != currency && await CatalogQueries.Price(db, planId, other, today, ct).ConfigureAwait(false) is { } price)
            {
                return (price, await rates.Require(other, currency, ct).ConfigureAwait(false));
            }
        }

        return null;
    }

    /// <summary>Motivo de la nota de crédito que anula la factura de un pedido cancelado.</summary>
    public const string CancelReason = "Pedido cancelado";

    /// <summary>
    /// Cancela un pedido que nadie pagó ni empezó a aprovisionar: anula sus facturas (una por modo de carga del precio)
    /// con una nota de crédito por todo lo que falta acreditar, cancela sus servicios y devuelve el stock y el uso del
    /// cupón que tomó. Con algo pagado no se cancela, se reembolsa, y un servicio que ya salió hacia el módulo se da de
    /// baja. Repetirlo con el pedido ya cancelado no hace nada. Toma los locks como el pedido, el catálogo antes que el
    /// saldo, y después los de la sección 13: la factura, el saldo y los servicios.
    /// </summary>
    public async Task Cancel(long orderId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var order = await db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el pedido {orderId}.");
        if (order.Status == OrderStatus.Cancelled)
        {
            return;
        }

        var serviceIds = order.Lines.Where(l => l.ServiceId != null).Select(l => l.ServiceId!.Value).ToList();
        await EnsurePending(serviceIds, ct).ConfigureAwait(false);
        foreach (var product in order.Lines.GroupBy(l => l.ProductId))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE catalog.products SET stock = stock + {product.Sum(l => l.Quantity)} WHERE id = {product.Key} AND stock IS NOT NULL", ct).ConfigureAwait(false);
        }

        if (order.CouponId is { } couponId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog.coupons SET uses = uses - 1 WHERE id = {couponId} AND uses > 0", ct).ConfigureAwait(false);
        }

        foreach (var invoiceId in await OrderInvoices(order, serviceIds, ct).ConfigureAwait(false))
        {
            await credit.Credit(invoiceId, null, CancelReason, ct).ConfigureAwait(false);
            if (await db.Documents.Where(d => d.Id == invoiceId).Select(d => d.Allocated).SingleAsync(ct).ConfigureAwait(false) != 0m)
            {
                throw new InvalidOperationException($"La factura del pedido {orderId} tiene pagos: no se cancela, se reembolsa desde el admin.");
            }
        }

        await Sql.LockRows(db, "services.services", serviceIds, ct).ConfigureAwait(false);
        await EnsurePending(serviceIds, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        foreach (var service in await db.Services.Where(s => serviceIds.Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false))
        {
            service.MoveTo(ServiceStatus.Cancelled, now);
        }

        order.Status = OrderStatus.Cancelled;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
    }

    /// <summary>Antes de anular nada y otra vez con los servicios bloqueados: un pago puede haber arrancado el alta.</summary>
    private async Task EnsurePending(IReadOnlyCollection<long> serviceIds, CancellationToken ct)
    {
        if (await db.Services.AsNoTracking().Where(s => serviceIds.Contains(s.Id) && s.Status != ServiceStatus.Pending)
                .Select(s => (long?)s.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } started)
        {
            throw new InvalidOperationException($"El servicio {started} ya no está pendiente: se da de baja, el pedido no se cancela.");
        }
    }

    /// <summary>
    /// El pedido que ya se confirmó con la clave, como lo devolvió la primera vez. La misma clave con otro pedido se
    /// rechaza.
    /// </summary>
    private async Task<PlacedOrder?> Existing(long accountId, string clientReference, long? apiKeyId, string? fingerprint, CancellationToken ct)
    {
        var existing = await db.Orders.Include(o => o.Lines).AsNoTracking()
            .SingleOrDefaultAsync(o => o.ApiKeyId == apiKeyId && o.AccountId == accountId && o.ClientReference == clientReference, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (existing.RequestFingerprint != fingerprint)
        {
            throw new ValidationException("La Idempotency-Key ya se usó con otro pedido: repetí el mismo cuerpo o usá otra clave.");
        }

        var serviceIds = existing.Lines.Where(l => l.ServiceId != null).Select(l => l.ServiceId!.Value).ToList();
        return new PlacedOrder(existing.Id, existing.InvoiceId, serviceIds, await OrderInvoices(existing, serviceIds, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Las facturas que emitió el pedido, como las devolvió la primera vez: una por modo de carga del precio, que es la
    /// primera factura de cada uno de sus servicios. Sin factura (total cero), ninguna.
    /// </summary>
    private async Task<IReadOnlyList<long>> OrderInvoices(Order order, List<long> serviceIds, CancellationToken ct) =>
        order.InvoiceId is null
            ? []
            : await db.DocumentLines.Where(l => l.ServiceId != null && serviceIds.Contains(l.ServiceId.Value))
                .GroupBy(l => l.ServiceId).Select(g => g.Min(l => l.DocumentId)).Distinct().OrderBy(id => id)
                .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Huella SHA-256 del pedido ya leído, con su cupón y su paquete: no depende del orden de las propiedades ni del
    /// formato del JSON que mandó el cliente.
    /// </summary>
    public static string Fingerprint(long accountId, IReadOnlyList<OrderItem> items, string? paymentMethod, string? couponCode = null, long? bundleId = null) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { accountId, items, paymentMethod, couponCode, bundleId })));

    /// <summary>El cupón por su código, sin distinguir mayúsculas, si se puede usar hoy en un pedido de la cuenta.</summary>
    private async Task<Coupon> Coupon(long accountId, string code, DateOnly today, CancellationToken ct)
    {
        var normalized = code.Trim().ToLowerInvariant();
        var coupon = await db.Coupons.SingleOrDefaultAsync(c => c.Code.ToLower() == normalized, ct).ConfigureAwait(false)
            ?? throw new ValidationException($"No existe el cupón {code.Trim()}.");
        var hasOrders = await db.Orders.AnyAsync(o => o.AccountId == accountId && o.Status != OrderStatus.Cancelled, ct).ConfigureAwait(false);
        var usedIt = await db.Orders.AnyAsync(o => o.AccountId == accountId && o.CouponId == coupon.Id && o.Status != OrderStatus.Cancelled, ct).ConfigureAwait(false);
        return coupon.Rejection(today, hasOrders, usedIt) is { } rejection ? throw new ValidationException(rejection) : coupon;
    }

    /// <summary>El paquete, si se puede pedir y las líneas marcadas son exactamente sus planes con sus unidades.</summary>
    private async Task<Bundle> Bundle(long bundleId, IReadOnlyList<OrderItem> items, CancellationToken ct)
    {
        var bundle = await db.Bundles.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bundleId, ct).ConfigureAwait(false)
            ?? throw new ValidationException($"No existe el paquete {bundleId}.");
        if (!bundle.Active)
        {
            throw new ValidationException($"El paquete {bundle.Name} no está disponible.");
        }

        if (!bundle.Matches(items.Where(i => i.Bundled).Select(i => (i.PlanId, i.Quantity))))
        {
            var plans = await (from p in db.Plans
                               join product in db.Products on p.ProductId equals product.Id
                               where bundle.PlanIds.Contains(p.Id)
                               select new { p.Id, product.Name, p.CycleUnit, p.CycleCount }).ToListAsync(ct).ConfigureAwait(false);
            var expected = bundle.PlanIds.GroupBy(id => id).Select(g => plans.FirstOrDefault(p => p.Id == g.Key) is { } p
                ? string.Create(CultureInfo.InvariantCulture, $"{p.Name} ({new BillingCycle(p.CycleUnit, p.CycleCount).Code}): {g.Count()}")
                : string.Create(CultureInfo.InvariantCulture, $"plan {g.Key}: {g.Count()}"));
            throw new ValidationException($"Las líneas del paquete {bundle.Name} tienen que ser sus planes con sus unidades: {string.Join(", ", expected)}.");
        }

        return bundle;
    }

    /// <summary>Suma un uso con un UPDATE condicionado: dos pedidos a la vez nunca pasan el tope.</summary>
    private async Task UseCoupon(Coupon coupon, CancellationToken ct)
    {
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.coupons SET uses = uses + 1 WHERE id = {coupon.Id} AND (max_uses IS NULL OR uses < max_uses)", ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new ValidationException($"El cupón {coupon.Code} ya se usó todas las veces que permite.");
        }
    }

    /// <summary>Descuenta el stock con un UPDATE condicionado: nunca queda negativo, aunque lleguen dos pedidos a la vez.</summary>
    private async Task TakeStock(Product product, int quantity, CancellationToken ct)
    {
        if (product.Stock is null)
        {
            return;
        }

        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.products SET stock = stock - {quantity} WHERE id = {product.Id} AND stock >= {quantity}", ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new ValidationException($"No queda stock de {product.Name}.");
        }
    }

    /// <summary>
    /// Un complemento cuelga de un servicio vivo de la misma cuenta o de otra línea del pedido, de un producto que lo
    /// admite. Un producto que no es complemento no cuelga de nada.
    /// </summary>
    private async Task CheckParent(long accountId, Product product, OrderItem item, IReadOnlyList<OrderItem> items, CancellationToken ct)
    {
        if (!product.Addon)
        {
            if (item.ParentServiceId is not null || item.ParentItem is not null)
            {
                throw new ValidationException($"{product.Name} no es un complemento: no cuelga de otro servicio.");
            }

            return;
        }

        long parentProduct;
        if (item.ParentItem is { } index)
        {
            var parentPlan = items[index].PlanId;
            parentProduct = await db.Plans.Where(p => p.Id == parentPlan).Select(p => p.ProductId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }
        else if (item.ParentServiceId is { } parentId)
        {
            var parent = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == parentId && s.AccountId == accountId, ct).ConfigureAwait(false);
            if (parent is null || parent.Status is ServiceStatus.Terminated or ServiceStatus.Cancelled or ServiceStatus.Failed)
            {
                throw new ValidationException($"El complemento {product.Name} necesita un servicio vivo de la cuenta.");
            }

            parentProduct = parent.ProductId;
        }
        else
        {
            throw new ValidationException($"{product.Name} es un complemento: va colgado de un servicio.");
        }

        if (product.ParentProductIds.Length > 0 && !product.ParentProductIds.Contains(parentProduct))
        {
            throw new ValidationException($"El complemento {product.Name} no se puede agregar a ese servicio.");
        }
    }

    /// <summary>Lo que se valida antes de tocar la base.</summary>
    private static void Validate(IReadOnlyList<OrderItem> items, long? bundleId)
    {
        if (items is null || items.Count == 0)
        {
            // Sin el nombre del parámetro: el texto sale tal cual al integrador (D-11).
            throw new ArgumentException("Un pedido sin líneas no se confirma.");
        }

        // Una línea con 0 o negativa bajaría el total y aprovisionaría igual.
        if (items.Any(i => i.Quantity < 1))
        {
            throw new ValidationException("La cantidad de cada línea tiene que ser 1 o más.");
        }

        // El nombre del servicio tiene el largo de su columna: más largo, la base rechazaría el pedido entero sin decir por qué.
        if (items.Any(i => i.Label is { Length: > 255 }))
        {
            throw new ValidationException("El nombre o dominio de cada línea va con hasta 255 caracteres.");
        }

        if (items.Select((item, index) => (item, index)).Any(x => x.item.ParentItem is { } p && (p < 0 || p >= items.Count || p == x.index || x.item.ParentServiceId is not null)))
        {
            throw new ValidationException("Un complemento cuelga de un servicio de la cuenta o de otra línea del pedido, no de las dos.");
        }

        if (bundleId is null && items.Any(i => i.Bundled))
        {
            throw new ValidationException("Una línea del paquete necesita el paquete del pedido.");
        }
    }

    /// <summary>
    /// Resuelve cada línea sin escribir nada: el plan activo con su precio en la moneda de la cuenta (o, con
    /// Billing:ExchangeRates:IndexPrices, en otra convertido con la cotización de hoy), el padre del complemento, las
    /// opciones, el stock que alcanza, la prueba gratis y el descuento del paquete o del cupón. Con
    /// <paramref name="quoted"/>, el precio pactado en el presupuesto. Los servicios quedan armados en memoria, todavía
    /// sin guardar.
    /// </summary>
    private async Task<Resolved> Resolve(long accountId, IReadOnlyList<OrderItem> items, string? couponCode, long? bundleId, DateOnly today, DateTimeOffset now, bool byCustomer, CancellationToken ct, QuotedTerms? quoted = null)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        var coupon = couponCode is null ? null : await Coupon(accountId, couponCode, today, ct).ConfigureAwait(false);
        var bundle = bundleId is { } wanted ? await Bundle(wanted, items, ct).ConfigureAwait(false) : null;
        var receiver = await core.Receiver(accountId, ct).ConfigureAwait(false);
        var taxes = await core.Taxes(ct).ConfigureAwait(false);
        var priced = new List<Priced>();
        var units = new Dictionary<long, int>();
        foreach (var item in items)
        {
            var plan = await db.Plans.SingleOrDefaultAsync(p => p.Id == item.PlanId && p.Active, ct).ConfigureAwait(false)
                ?? throw new ValidationException($"No existe el plan {item.PlanId} o no está activo.");
            var product = await db.Products.SingleAsync(p => p.Id == plan.ProductId, ct).ConfigureAwait(false);
            if (item.Quantity > 1 && !product.AllowQuantity)
            {
                // Cada unidad de un producto que no se pide por cantidad es su propio servicio: va en su propia línea.
                throw new ValidationException($"{product.Name} se pide de a una unidad por línea.");
            }

            // Un presupuesto no necesita precio del plan en la moneda: trae el suyo. Sin él, con IndexPrices, vale el de otra
            // moneda cotizada.
            var price = await CatalogQueries.Price(db, plan.Id, account.Currency, today, ct).ConfigureAwait(false);
            Conversion? indexed = null;
            if (price is null && quoted is null && await IndexedPrice(plan.Id, account.Currency, today, ct).ConfigureAwait(false) is { } found)
            {
                (price, indexed) = found;
            }

            if (price is null && quoted is null)
            {
                throw new InvalidOperationException($"El plan {plan.Id} no tiene precio en {account.Currency}.");
            }

            await CheckParent(accountId, product, item, items, ct).ConfigureAwait(false);
            IReadOnlyList<ChosenOption> chosen;
            try
            {
                // Las opciones van en la moneda del precio del plan: con uno en otra moneda se convierten con él.
                chosen = (await CatalogQueries.Options(db, product.Id, ct).ConfigureAwait(false)).Resolve(item.Options, price?.Currency ?? account.Currency, plan.Cycle);
            }
            catch (ArgumentException ex)
            {
                throw new ValidationException(ex.Message);
            }

            // Lo oculto lo elige el staff o la API: el cliente que lo pide a mano, fuera de lo que le muestra el portal, no.
            if (byCustomer && OptionCatalog.HiddenPick(chosen, item.Options) is { } hidden)
            {
                throw new ValidationException($"{hidden.Option.Name}: {hidden.Value.Name} no se ofrece.");
            }

            // Lo que pide todo el pedido de cada producto. Al confirmar, el UPDATE condicionado es el que manda.
            units[product.Id] = units.GetValueOrDefault(product.Id) + item.Quantity;
            if (product.Stock is { } stock && stock < units[product.Id])
            {
                throw new ValidationException($"No queda stock de {product.Name}.");
            }

            var trial = quoted is null && product.TrialDays is > 0 && plan.Cycle.IsRecurring && priced.All(p => p.Product.Id != product.Id || p.Service.TrialEndsOn is null)
                && !await db.Services.AnyAsync(s => s.AccountId == accountId && s.ProductId == product.Id && s.TrialEndsOn != null, ct).ConfigureAwait(false);

            // El precio unitario es el del plan más el de las opciones. El cargo de alta va una vez por línea, y con prueba
            // gratis no se cobra: probar no cuesta nada. El de un presupuesto reemplaza a los dos, en el modo de carga por
            // defecto de la moneda, como lo cargó el staff. Uno en otra moneda se factura convertido con la cotización de
            // hoy, también el cargo de alta, y conserva su modo.
            var listPrice = price is null ? 0m : price.Amount + chosen.Sum(c => c.Amount);
            var listSetup = trial || price is null ? 0m : price.SetupFee + chosen.Sum(c => c.SetupFee);
            var (unitPrice, setupFee, mode) = quoted is not null
                ? (quoted.UnitPrices[priced.Count], 0m, taxes.Settings.DefaultPriceMode(account.Currency))
                : (indexed?.Apply(listPrice) ?? listPrice, indexed?.Apply(listSetup) ?? listSetup, price!.PriceMode);

            // El servicio congela precio y modo de carga: las opciones siguen el modo del precio del plan.
            var service = new Service(accountId, product.Id, plan.Cycle, account.Currency, unitPrice, item.Quantity, now, mode)
            {
                Label = item.Label,
                ModuleInstanceId = product.ModuleInstanceId,
                ModuleParams = OptionCatalog.ModuleParams(product.ModuleParams, chosen),
                ParentServiceId = item.ParentServiceId,
                TrialEndsOn = trial ? today.AddDays(product.TrialDays!.Value) : null,
            };
            if (indexed is not null)
            {
                service.IndexTo(price!.Currency, listPrice);
            }

            // Las líneas del paquete llevan su descuento y no toman cupón: el paquete ya es su promoción. El cupón con "una
            // vez por pedido" descuenta solo la primera línea donde vale.
            if (item.Bundled)
            {
                if (bundle!.DiscountPercent > 0m)
                {
                    service.DiscountPercent = bundle.DiscountPercent;
                    service.DiscountCyclesLeft = bundle.CyclesFor(plan.Cycle);
                }
            }
            else if (coupon is not null && coupon.AppliesTo(product.Id, plan.Cycle, account.Currency) && !(coupon.OncePerOrder && priced.Any(p => p.Service.CouponId is not null)))
            {
                service.CouponId = coupon.Id;
                service.DiscountPercent = coupon.Kind == DiscountKind.Percent ? coupon.Value : null;
                service.DiscountAmount = coupon.Kind == DiscountKind.Fixed ? coupon.Value : null;
                service.DiscountCyclesLeft = coupon.CyclesFor(plan.Cycle);
            }

            priced.Add(new Priced(item, plan, product, service, chosen, setupFee, indexed));
        }

        if (coupon is not null && priced.All(p => p.Service.CouponId is null))
        {
            throw new ValidationException($"El cupón {coupon.Code} no vale para nada de este pedido.");
        }

        return new Resolved(account, coupon, bundle, receiver, taxes, priced);
    }

    /// <summary>
    /// Lo que la línea suma a la primera factura: el primer período de un plan recurrente, el pago único y el cargo de
    /// alta. Una prueba gratis todavía no factura: su primer período arranca cuando termina, y la renovación lo factura
    /// antes. Devuelve el período que reservó, si reservó uno.
    /// </summary>
    private static BilledPeriod? AddFirstLines(Document invoice, Priced p, ReceiverSnapshot receiver, TaxContext taxes, long accountId, DateOnly today, string? setupKey)
    {
        var service = p.Service;
        if (service.TrialEndsOn is { } trialEnd)
        {
            service.StartBilling(trialEnd);
            return null;
        }

        // La fecha fiscal es la de emisión: hoy.
        var tax = taxes.Line(p.Product, receiver, accountId, today);
        var name = CatalogQueries.WithOptions(p.Product.Name, p.Chosen);
        BilledPeriod? reserved = null;
        if (p.Plan.Cycle.IsRecurring)
        {
            service.StartBilling(today);

            // El precio se lee antes de tomar el período: si tomarlo consume un ciclo de descuento, ese ciclo se aplica acá.
            var unitPrice = service.DiscountedUnitPrice;
            var period = service.TakeNextPeriod();
            var renewal = invoice.AddLine(LineKind.Renewal, BillingCore.Describe(name, period, service, p.Indexed), p.Item.Quantity, unitPrice, tax.Treatment, tax.Rate, service.Id, category: p.Product.TaxCategory, reason: tax.Reason, legend: tax.Legend);
            reserved = new BilledPeriod(service.Id, period.Start, period.End, renewal);
        }
        else if (p.Plan.Cycle.Unit == CycleUnit.OneTime)
        {
            invoice.AddLine(LineKind.OneTime, name, p.Item.Quantity, service.DiscountedUnitPrice, tax.Treatment, tax.Rate, service.Id, category: p.Product.TaxCategory, reason: tax.Reason, legend: tax.Legend);
        }

        if (p.SetupFee > 0m)
        {
            // El cargo de alta lleva la categoría del servicio al que acompaña (Ley de IVA, art. 10).
            invoice.AddLine(LineKind.Setup, $"Alta de {p.Product.Name}", 1, p.SetupFee, tax.Treatment, tax.Rate, service.Id, setupKey, p.Product.TaxCategory, tax.Reason, tax.Legend);
        }

        return reserved;
    }

    private sealed record Priced(OrderItem Item, Plan Plan, Product Product, Service Service, IReadOnlyList<ChosenOption> Chosen, decimal SetupFee, Conversion? Indexed);

    /// <summary>La factura de un modo de carga: un pedido que mezcla modos sale en una factura por modo.</summary>
    private Document InvoiceFor(Dictionary<PriceMode, Document> invoices, PriceMode mode, long accountId, Currency currency, DateOnly today, DateTimeOffset now)
    {
        if (!invoices.TryGetValue(mode, out var invoice))
        {
            invoice = new Document(DocumentKind.Invoice, accountId, currency, mode, today.AddDays(core.Options.InvoiceDueDays), now);
            invoices[mode] = invoice;
        }

        return invoice;
    }

    private sealed record Resolved(Account Account, Coupon? Coupon, Bundle? Bundle, ReceiverSnapshot Receiver, TaxContext Taxes, List<Priced> Lines);
}

/// <summary>Piezas que comparten los casos de uso de facturación.</summary>
public sealed class BillingCore(BillingDbContext db, JobQueue jobs, Microsoft.Extensions.Options.IOptions<BillingOptions> options, TimeProvider time)
{
    /// <summary>Los renglones se escriben en castellano: así salen en el comprobante, en el portal y en el PDF.</summary>
    private static readonly CultureInfo LineCulture = CultureInfo.GetCultureInfo("es-AR");

    public BillingOptions Options { get; } = options.Value;

    public DateOnly Today() => Options.Today(time);

    public DateTimeOffset Now() => time.GetUtcNow();

    /// <summary>La identidad fiscal vigente, o consumidor final del país por defecto.</summary>
    public async Task<ReceiverSnapshot> Receiver(long accountId, CancellationToken ct)
    {
        var identity = await db.TaxIdentities.SingleOrDefaultAsync(t => t.AccountId == accountId && t.ValidTo == null, ct).ConfigureAwait(false);
        return identity is null
            ? new ReceiverSnapshot(TaxDocumentType.None, string.Empty, string.Empty, Options.DefaultVatCondition, Options.DefaultCountry, null)
            : new ReceiverSnapshot(identity.DocumentType, identity.DocumentNumber, identity.LegalName, identity.VatCondition, identity.Country, identity.Province, Address(identity), identity.VatNumber, identity.ValidatedBusiness);
    }

    /// <summary>
    /// El receptor de una nota sobre un documento: el que ese documento congeló al emitirse, porque la nota corrige ese
    /// comprobante. Un importado no lo trae (el importador guarda "{}", sin país ni condición): va el de la cuenta hoy,
    /// que es el mejor dato que hay, y así la nota se numera, se avisa y se emite con receptor.
    /// </summary>
    public async Task<ReceiverSnapshot> ReceiverOf(Document doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var frozen = string.IsNullOrEmpty(doc.Receiver) ? null : JsonSerializer.Deserialize<ReceiverSnapshot>(doc.Receiver);
        return frozen is { Country.Length: > 0 } ? frozen : await Receiver(doc.AccountId, ct).ConfigureAwait(false);
    }

    /// <summary>Los ajustes impositivos, una sola fila. Si la base no la tiene (una importación vieja), se crea con los valores de fábrica.</summary>
    public async Task<TaxSettings> Settings(CancellationToken ct)
    {
        var settings = await db.TaxSettings.SingleOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null)
        {
            settings = new TaxSettings();
            db.TaxSettings.Add(settings);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return settings;
    }

    /// <summary>Categorías, impuestos, reglas, ajustes y la condición del emisor, cargados una vez por operación.</summary>
    public async Task<TaxContext> Taxes(CancellationToken ct)
    {
        var categories = await db.TaxCategories.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var taxes = await db.Taxes.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var rules = await db.TaxRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var issuer = await db.IssuerProfiles.AsNoTracking().Where(i => i.Active).Select(i => (int?)i.VatCondition).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new TaxContext(categories, taxes, rules, await Settings(ct).ConfigureAwait(false), issuer ?? VatConditions.ResponsableInscripto);
    }

    private static string? Address(TaxIdentity identity)
    {
        var parts = new[] { identity.Street, identity.City, identity.Province, identity.PostalCode, identity.Country }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(", ", parts) is { Length: > 0 } address ? address : null;
    }

    public static string Describe(string productName, Period period) =>
        string.Create(CultureInfo.InvariantCulture, $"{productName} ({period.Start:dd/MM/yyyy} al {period.End.AddDays(-1):dd/MM/yyyy})");

    /// <summary>
    /// La renovación de un servicio con precio pactado en otra moneda dice ese precio y la cotización que lo convirtió,
    /// con su fuente y su fecha, por ejemplo "VPS (01/11/2026 al 30/11/2026), USD 10,00 a 1.465,50 (Banco Nación del 30/10/2026)".
    /// </summary>
    public static string Describe(string productName, Period period, Service service, Conversion? indexed)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (indexed is null || service.IndexCurrency is not { } currency)
        {
            return Describe(productName, period);
        }

        var source = indexed.Rate.Source == ExchangeRate.Bna ? "Banco Nación" : "cargada por el staff";
        return string.Create(LineCulture, $"{Describe(productName, period)}, {currency.Code} {service.IndexedUnitPrice:N2} a {indexed.Factor:N2} ({source} del {indexed.Rate.Date:dd/MM/yyyy})");
    }

    /// <summary>
    /// Lo que cuesta hoy un período del servicio con su IVA y en su modo de carga del precio, como lo factura la
    /// renovación: lo que la reemisión de un período anulado le facturaría, y con lo que la mora mide si el saldo a
    /// favor lo cubre.
    /// </summary>
    public async Task<decimal> PeriodTotal(Service service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        var product = await db.Products.SingleAsync(p => p.Id == service.ProductId, ct).ConfigureAwait(false);
        var receiver = await Receiver(service.AccountId, ct).ConfigureAwait(false);
        var tax = (await Taxes(ct).ConfigureAwait(false)).Line(product, receiver, service.AccountId, Today());
        return TaxCalculator.Compute(service.PriceMode, [new TaxableLine("0", service.RecurringAmount, tax.Treatment, tax.Rate)]).Total;
    }

    /// <summary>
    /// Cierre en dos pasos (la base no deja tocar líneas de un documento emitido) y evento, con la clave de la serie
    /// fiscal del documento. Los tributos de comprobante se calculan acá para las facturas, y una nota pasa los suyos
    /// topeados contra el original. El número comercial lo asigna <see cref="Commit"/>: la transacción que emite termina
    /// ahí.
    /// </summary>
    public async Task IssueDraft(Document draft, ReceiverSnapshot receiver, CancellationToken ct, TaxBreakdown? capped = null, IReadOnlyList<DocumentTaxAmount>? taxes = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var breakdown = draft.PrepareIssue(capped);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        taxes ??= (await Taxes(ct).ConfigureAwait(false)).DocumentTaxes(draft, receiver, Today());
        draft.Issue(Today(), JsonSerializer.Serialize(receiver), time.GetUtcNow(), breakdown, taxes);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var key = await FiscalEmissionHandler.EntityKey(db, draft, ct).ConfigureAwait(false);
        var issued = draft.Kind switch
        {
            DocumentKind.Invoice => Events.InvoiceIssued,
            DocumentKind.DebitNote => Events.DebitNoteIssued,
            _ => Events.CreditNoteIssued,
        };
        await jobs.Publish(issued, new { document_id = draft.Id }, key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Numera lo emitido en la transacción y la confirma. El número sale del contador justo antes del commit, así la
    /// fila del contador, que pone en fila a todos los que emiten ese tipo, queda bloqueada lo menos posible. Un
    /// documento emitido no se confirma sin número por otro camino (<see cref="NumberingGuard"/>).
    /// </summary>
    public async Task Commit(IDbContextTransaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tx);
        var unnumbered = db.ChangeTracker.Entries<Document>().Select(e => e.Entity).Where(d => d.IsIssued && d.Number is null).OrderBy(d => d.Id).ToList();
        foreach (var doc in unnumbered)
        {
            doc.AssignNumber(await Sql.NextNumber(db, doc.Kind, ct).ConfigureAwait(false));
        }

        if (unnumbered.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pasa el servicio a aprovisionando, deja aceptado su pedido y encola la creación en su módulo. Si su pedido está
    /// retenido por la evaluación de fraude, queda pendiente: lo aprovisiona la liberación (diseno/fraude-y-acceso.md,
    /// sección 3.2).
    /// </summary>
    public async Task StartProvisioning(Service service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.Status != ServiceStatus.Pending || await OrderHeld(service, ct).ConfigureAwait(false))
        {
            return;
        }

        service.MoveTo(ServiceStatus.Provisioning, time.GetUtcNow());

        // Un pedido con una línea que espera al staff sigue pendiente aunque el resto se aprovisione: lo acepta el staff.
        if (await OrderOf(service, ct).ConfigureAwait(false) is { Status: OrderStatus.Pending } order && !await WaitsForStaff(order, service.Id, ct).ConfigureAwait(false))
        {
            order.Status = OrderStatus.Accepted;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EnqueueOperation(service, "create", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// El módulo confirmó la creación: cuando todos los servicios del pedido están activos, el pedido queda completo,
    /// como los que trae el importador. Uno que falló lo deja aceptado, a la vista del staff.
    /// </summary>
    public async Task ServiceProvisioned(Service service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (await OrderOf(service, ct).ConfigureAwait(false) is not { } order)
        {
            return;
        }

        var siblings = order.Lines.Where(l => l.ServiceId != null && l.ServiceId != service.Id).Select(l => l.ServiceId!.Value).ToList();
        if (await db.Services.Where(s => siblings.Contains(s.Id)).AllAsync(s => s.Status == ServiceStatus.Active, ct).ConfigureAwait(false))
        {
            order.Status = OrderStatus.Completed;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private Task<Order?> OrderOf(Service service, CancellationToken ct) =>
        service.OrderLineId is { } lineId
            ? db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Lines.Any(l => l.Id == lineId), ct)
            : Task.FromResult<Order?>(null);

    /// <summary>
    /// Con el pedido bloqueado, el mismo lock que toma la liberación: o la liberación ve el pago, o este pago ve la
    /// evaluación ya liberada. Nunca queda un servicio pago sin aprovisionar. El estado del servicio se vuelve a leer
    /// después del lock: quien lo leyó antes puede tenerlo pendiente cuando un rechazo ya lo canceló. Un pedido rechazado
    /// nunca aprovisiona nada.
    /// </summary>
    private async Task<bool> OrderHeld(Service service, CancellationToken ct)
    {
        if (service.OrderLineId is not { } lineId)
        {
            return false;
        }

        var orderId = await db.Set<OrderLine>().Where(l => l.Id == lineId).Select(l => l.OrderId).SingleAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "orders.orders", [orderId], ct, "NO KEY UPDATE").ConfigureAwait(false);
        return await db.Services.AnyAsync(s => s.Id == service.Id && s.Status != ServiceStatus.Pending, ct).ConfigureAwait(false)
            || await db.Set<FraudCheck>().AnyAsync(f => f.OrderId == orderId && (f.Status == ScreeningStatus.Pending || f.Status == ScreeningStatus.Review || f.Status == ScreeningStatus.Rejected), ct).ConfigureAwait(false);
    }

    /// <summary>Otra línea del pedido es de un producto que acepta el staff y todavía no se aprovisionó.</summary>
    private Task<bool> WaitsForStaff(Order order, long serviceId, CancellationToken ct)
    {
        var others = order.Lines.Where(l => l.ServiceId != null && l.ServiceId != serviceId).Select(l => l.ServiceId!.Value).ToList();
        return db.Services.Where(s => others.Contains(s.Id) && s.Status == ServiceStatus.Pending)
            .Join(db.Products, s => s.ProductId, p => p.Id, (s, p) => p.Activation)
            .AnyAsync(a => a == ProductActivation.Manual, ct);
    }

    /// <summary>Encola una operación con la generación actual. Las de un mismo servicio corren en orden y de a una.</summary>
    public Task EnqueueOperation(Service service, string operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        return jobs.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(service.Id, operation, service.Generation), $"service:{service.Id}", $"service:{service.Id}:{operation}:{service.Generation}", ct: ct);
    }

    /// <summary>
    /// La factura quedó paga: lo pagado de cada servicio avanza un ciclo por renovación y se publica el evento, en la
    /// misma transacción que la imputación. Así la mora nunca ve un pago confirmado sin su efecto (sección 5.2 del
    /// modelo). Los servicios se bloquean después de la factura y del saldo (sección 13). El que llama guarda.
    /// </summary>
    public async Task InvoicePaid(Document doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var renewals = await db.DocumentLines.Where(l => l.DocumentId == doc.Id && l.Kind == LineKind.Renewal && l.ServiceId != null)
            .Select(l => l.ServiceId!.Value).ToListAsync(ct).ConfigureAwait(false);

        // Un cambio de plan que esperaba esta factura se aplica con el pago, en la misma transacción.
        var changes = await db.ServiceChanges.Where(c => c.DocumentId == doc.Id && c.Status == ServiceChangeStatus.Pending).ToListAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", renewals.Concat(changes.Select(c => c.ServiceId)), ct).ConfigureAwait(false);
        foreach (var service in await db.Services.Where(s => renewals.Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false))
        {
            foreach (var _ in renewals.Where(id => id == service.Id))
            {
                service.RenewalPaid();
            }
        }

        foreach (var change in changes)
        {
            var service = await db.Services.SingleAsync(s => s.Id == change.ServiceId, ct).ConfigureAwait(false);
            await ApplyChange(change, service, ct).ConfigureAwait(false);

            // Mientras esperaba el pago se pudo haber facturado otro período con el precio viejo: esa diferencia la
            // factura un trabajo aparte, fuera de la transacción del pago.
            if (change.BilledUntil is { } until && service.NextDueDate > until)
            {
                await jobs.Enqueue(JobKinds.ChangeGap, new { change_id = change.Id }, $"account:{service.AccountId}", $"{JobKinds.ChangeGap}:{change.Id}", ct: ct).ConfigureAwait(false);
            }
        }

        await jobs.Publish(Events.InvoicePaid, new { document_id = doc.Id }, $"document:{doc.Id}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Aplica un cambio de plan o de opciones: producto, ciclo, precio y opciones nuevos, los parámetros del módulo
    /// recalculados y la operación change_plan encolada. El que llama tiene el servicio bloqueado. Lo que se sacó del
    /// catálogo después de pedir el cambio queda afuera: un pago nunca falla por una edición del catálogo.
    /// </summary>
    public async Task ApplyChange(ServiceChange change, Service service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(service);
        var product = await db.Products.SingleAsync(p => p.Id == change.ToProductId, ct).ConfigureAwait(false);
        var choices = JsonSerializer.Deserialize<List<OptionChoice>>(change.Options) ?? [];
        var optionIds = choices.Select(c => c.OptionId).ToList();
        var valueIds = choices.Where(c => c.ValueId is not null).Select(c => c.ValueId!.Value).ToList();
        var options = await db.Options.Where(o => optionIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct).ConfigureAwait(false);
        var values = await db.OptionValues.Where(v => valueIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct).ConfigureAwait(false);
        var chosen = choices
            .Where(c => options.ContainsKey(c.OptionId) && c.ValueId is { } v && values.ContainsKey(v))
            .Select(c => new ChosenOption(options[c.OptionId], values[c.ValueId!.Value], c.Quantity ?? 1, 0m, 0m))
            .ToList();

        service.ChangePlan(product.Id, change.Cycle, change.ToUnitPrice);
        if (change.EndsDiscount)
        {
            service.EndDiscount();
        }

        await db.ServiceOptions.Where(o => o.ServiceId == service.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.ServiceOptions.AddRange(chosen.Select(c => new ServiceOption(service.Id, c.Option.Id, c.Value.Id, c.Quantity)));
        service.ModuleParams = OptionCatalog.ModuleParams(product.ModuleParams, chosen);
        change.Apply(time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (service.Status is ServiceStatus.Active or ServiceStatus.Suspended)
        {
            await jobs.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(service.Id, "change_plan", service.Generation), $"service:{service.Id}", $"service:{service.Id}:change_plan:{change.Id}", ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Emite la factura de un cambio de plan con una línea de cambio de plan y su clave contra duplicados.</summary>
    public async Task<long> IssueChangeInvoice(Service service, Product product, string description, decimal amount, string dedupKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(product);
        var receiver = await Receiver(service.AccountId, ct).ConfigureAwait(false);
        var tax = (await Taxes(ct).ConfigureAwait(false)).Line(product, receiver, service.AccountId, Today());
        var invoice = new Document(DocumentKind.Invoice, service.AccountId, service.Currency, service.PriceMode, Today().AddDays(Options.InvoiceDueDays), time.GetUtcNow(), reason: "plan-change");
        invoice.AddLine(LineKind.PlanChange, description, 1, amount, tax.Treatment, tax.Rate, service.Id, dedupKey, product.TaxCategory, tax.Reason, tax.Legend);
        db.Documents.Add(invoice);
        await IssueDraft(invoice, receiver, ct).ConfigureAwait(false);
        return invoice.Id;
    }
}

/// <summary>
/// Receptor congelado al emitir. Address es el domicilio en una línea, que pide la factura de exportación.
/// ValidatedBusiness es la evidencia de empresa del exterior: el número fiscal validado en VIES o HMRC.
/// </summary>
public sealed record ReceiverSnapshot(TaxDocumentType DocumentType, string DocumentNumber, string LegalName, int VatCondition, string Country, string? Province, string? Address = null, string? VatNumber = null, bool ValidatedBusiness = false);

/// <summary>
/// Operación de un servicio. Dunning dice que la pidió la mora: antes de mandarla se revisa que siga correspondiendo.
/// Input es lo que va en el input del sobre además de las credenciales (JSON): action y params de run_action, o
/// force en una baja forzada.
/// </summary>
public sealed record ServiceOperation(long ServiceId, string Operation, int Generation, bool Dunning = false, string? Input = null);
