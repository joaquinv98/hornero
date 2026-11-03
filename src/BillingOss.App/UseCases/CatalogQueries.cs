using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BillingOss.App.Persistence;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>Precio de un ciclo en una moneda. En un producto lleva su plan, que es lo que se pide.</summary>
public sealed record PriceView(string Cycle, string Currency, decimal Amount, decimal SetupFee, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PlanId = null);

public sealed record OptionValueView(long Id, string Name, string? ModuleValue, int SortOrder, bool Hidden, IReadOnlyList<PriceView> Prices);

public sealed record OptionView(long Id, long GroupId, string Name, OptionKind Kind, string? ModuleKey, int MinQuantity, int MaxQuantity, int SortOrder, bool Hidden, IReadOnlyList<OptionValueView> Values);

public sealed record OptionGroupView(long Id, string Name, string? Description, IReadOnlyList<long> ProductIds, IReadOnlyList<OptionView> Options);

/// <summary>Un producto con los precios vigentes de sus planes activos y las opciones que ofrece.</summary>
public sealed record ProductView(
    long Id,
    string Name,
    long? GroupId,
    string? GroupName,
    string? Description,
    ProductKind Kind,
    bool Visible,
    int SortOrder,
    string TaxCategory,
    bool AllowQuantity,
    int? Stock,
    int? TrialDays,
    bool Addon,
    IReadOnlyList<long> ParentProductIds,
    IReadOnlyList<long> UpgradeProductIds,
    bool OptionChanges,
    long? ModuleInstanceId,
    JsonObject ModuleParams,
    int? TerminationDays,
    IReadOnlyList<PriceView> Prices,
    IReadOnlyList<long> OptionGroupIds,
    IReadOnlyList<OptionView> Options,
    ProductActivation Activation);

/// <summary>Un plan del paquete con su producto, su ciclo y sus unidades.</summary>
public sealed record BundleItemView(long PlanId, long ProductId, string Product, string Cycle, int Quantity);

/// <summary>El primer período del paquete en una moneda sin opciones: con el descuento, sin él, y los cargos de alta.</summary>
public sealed record BundlePriceView(string Currency, decimal Amount, decimal FullAmount, decimal SetupFee);

public sealed record BundleView(long Id, string Name, string? Description, decimal DiscountPercent, int? DiscountCycles, bool Active, IReadOnlyList<BundleItemView> Items, IReadOnlyList<BundlePriceView> Prices);

/// <summary>Lecturas del catálogo que comparten el pedido, la renovación, los cambios de plan y las API.</summary>
public static class CatalogQueries
{
    /// <summary>Los productos (todos, o los pedidos) armados para la API, el panel y la API compatible, en pocas consultas.</summary>
    public static async Task<List<ProductView>> Products(BillingDbContext db, DateOnly on, IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.Products.AsNoTracking();
        if (ids is not null)
        {
            query = query.Where(p => ids.Contains(p.Id));
        }

        var products = await query.OrderBy(p => p.GroupId).ThenBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync(ct).ConfigureAwait(false);
        var productIds = products.Select(p => p.Id).ToList();
        var groups = await db.ProductGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, ct).ConfigureAwait(false);
        var plans = await db.Plans.AsNoTracking().Where(p => productIds.Contains(p.ProductId) && p.Active).ToListAsync(ct).ConfigureAwait(false);
        var planIds = plans.Select(p => p.Id).ToList();
        var prices = (await db.PlanPrices.AsNoTracking().Where(p => planIds.Contains(p.PlanId) && p.ValidFrom <= on).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => (p.PlanId, p.Currency))
            .Select(g => g.MaxBy(p => p.ValidFrom)!)
            .Where(p => p.OpenOn(on))
            .ToLookup(p => p.PlanId);
        var links = (await db.ProductOptionGroups.AsNoTracking().Where(l => productIds.Contains(l.ProductId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(l => l.ProductId, l => l.OptionGroupId);
        var options = (await OptionGroups(db, [.. links.SelectMany(l => l).Distinct()], ct).ConfigureAwait(false)).ToDictionary(g => g.Id);
        return [.. products.Select(p => new ProductView(
            p.Id,
            p.Name,
            p.GroupId,
            p.GroupId is { } g ? groups.GetValueOrDefault(g) : null,
            p.Description,
            p.Kind,
            p.Visible,
            p.SortOrder,
            p.TaxCategory,
            p.AllowQuantity,
            p.Stock,
            p.TrialDays,
            p.Addon,
            p.ParentProductIds,
            p.UpgradeProductIds,
            p.OptionChanges,
            p.ModuleInstanceId,
            JsonNode.Parse(p.ModuleParams) as JsonObject ?? [],
            p.TerminationDays,
            [.. plans.Where(x => x.ProductId == p.Id).SelectMany(x => prices[x.Id].Select(price => new PriceView(x.Cycle.Code, price.Currency.Code, price.Amount, price.SetupFee, x.Id))).OrderBy(x => x.Currency, StringComparer.Ordinal)],
            [.. links[p.Id].Order()],
            [.. links[p.Id].Where(options.ContainsKey).SelectMany(id => options[id].Options).OrderBy(o => o.SortOrder).ThenBy(o => o.Id)],
            p.Activation))];
    }

    /// <summary>Los grupos de opciones (todos, o los pedidos) con sus opciones, valores y precios, y los productos que los ofrecen.</summary>
    public static async Task<List<OptionGroupView>> OptionGroups(BillingDbContext db, IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.OptionGroups.AsNoTracking();
        if (ids is not null)
        {
            query = query.Where(g => ids.Contains(g.Id));
        }

        var groups = await query.OrderBy(g => g.Name).ThenBy(g => g.Id).ToListAsync(ct).ConfigureAwait(false);
        var groupIds = groups.Select(g => g.Id).ToList();
        var options = await db.Options.AsNoTracking().Where(o => groupIds.Contains(o.GroupId)).ToListAsync(ct).ConfigureAwait(false);
        var optionIds = options.Select(o => o.Id).ToList();
        var values = await db.OptionValues.AsNoTracking().Where(v => optionIds.Contains(v.OptionId)).ToListAsync(ct).ConfigureAwait(false);
        var valueIds = values.Select(v => v.Id).ToList();
        var prices = (await db.OptionPrices.AsNoTracking().Where(p => valueIds.Contains(p.ValueId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(p => p.ValueId);
        var links = (await db.ProductOptionGroups.AsNoTracking().Where(l => groupIds.Contains(l.OptionGroupId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(l => l.OptionGroupId, l => l.ProductId);
        return [.. groups.Select(g => new OptionGroupView(g.Id, g.Name, g.Description, [.. links[g.Id].Order()], [.. options
            .Where(o => o.GroupId == g.Id)
            .OrderBy(o => o.SortOrder).ThenBy(o => o.Id)
            .Select(o => new OptionView(o.Id, o.GroupId, o.Name, o.Kind, o.ModuleKey, o.MinQuantity, o.MaxQuantity, o.SortOrder, o.Hidden, [.. values
                .Where(v => v.OptionId == o.Id)
                .OrderBy(v => v.SortOrder).ThenBy(v => v.Id)
                .Select(v => new OptionValueView(v.Id, v.Name, v.ModuleValue, v.SortOrder, v.Hidden, [.. prices[v.Id].Select(p => new PriceView(p.Cycle.Code, p.Currency.Code, p.Amount, p.SetupFee)).OrderBy(p => p.Currency, StringComparer.Ordinal)]))]))]))];
    }

    /// <summary>
    /// Los paquetes (todos, o los pedidos) con sus planes y su primer período en cada moneda donde se venden todos sus
    /// planes con sus opciones. Cada unidad suma su plan con lo menos que cobran sus opciones, con el mismo descuento, y
    /// su cargo de alta, porque cada una es su propia línea del pedido.
    /// </summary>
    public static async Task<List<BundleView>> Bundles(BillingDbContext db, DateOnly on, IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.Bundles.AsNoTracking();
        if (ids is not null)
        {
            query = query.Where(b => ids.Contains(b.Id));
        }

        var bundles = await query.OrderBy(b => b.Name).ThenBy(b => b.Id).ToListAsync(ct).ConfigureAwait(false);
        var planIds = bundles.SelectMany(b => b.PlanIds).Distinct().ToList();
        var plans = await (from p in db.Plans.AsNoTracking()
                           join product in db.Products on p.ProductId equals product.Id
                           where planIds.Contains(p.Id)
                           select new { Plan = p, product.Name }).ToDictionaryAsync(x => x.Plan.Id, ct).ConfigureAwait(false);
        var options = await Options(db, [.. plans.Values.Select(p => p.Plan.ProductId).Distinct()], ct).ConfigureAwait(false);

        // Lo que cobra cada plan en cada moneda con sus opciones más baratas: sin eso, el plan no se vende ahí.
        var prices = (await db.PlanPrices.AsNoTracking().Where(p => planIds.Contains(p.PlanId) && p.ValidFrom <= on).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => (p.PlanId, p.Currency))
            .Select(g => g.MaxBy(p => p.ValidFrom)!)
            .Where(p => p.OpenOn(on) && plans.ContainsKey(p.PlanId))
            .Select(p => (Price: p, Options: options[plans[p.PlanId].Plan.ProductId].Cheapest(p.Currency, plans[p.PlanId].Plan.Cycle)))
            .Where(p => p.Options is not null)
            .Select(p => (p.Price.PlanId, p.Price.Currency, Amount: p.Price.Amount + p.Options!.Value.Amount, SetupFee: p.Price.SetupFee + p.Options!.Value.SetupFee))
            .ToList();
        return [.. bundles.Select(b =>
        {
            var units = b.PlanIds.GroupBy(id => id).Select(g => (PlanId: g.Key, Quantity: g.Count())).ToList();
            var totals = prices.Where(p => b.PlanIds.Contains(p.PlanId))
                .GroupBy(p => p.Currency)
                .Where(g => units.All(u => g.Any(p => p.PlanId == u.PlanId)))
                .Select(g =>
                {
                    var full = units.Sum(u => g.Single(p => p.PlanId == u.PlanId).Amount * u.Quantity);
                    var setup = units.Sum(u => g.Single(p => p.PlanId == u.PlanId).SetupFee * u.Quantity);
                    return new BundlePriceView(g.Key.Code, Rounding.Commercial(full * (1m - (b.DiscountPercent / 100m))), full, setup);
                })
                .OrderBy(p => p.Currency, StringComparer.Ordinal);
            return new BundleView(b.Id, b.Name, b.Description, b.DiscountPercent, b.DiscountCycles, b.Active, [.. units
                .Where(u => plans.ContainsKey(u.PlanId))
                .Select(u => new BundleItemView(u.PlanId, plans[u.PlanId].Plan.ProductId, plans[u.PlanId].Name, plans[u.PlanId].Plan.Cycle.Code, u.Quantity))], [.. totals]);
        })];
    }

    /// <summary>El precio vigente del plan en la moneda, o nulo si no se vende en ella.</summary>
    public static async Task<PlanPrice?> Price(BillingDbContext db, long planId, Currency currency, DateOnly on, CancellationToken ct) =>
        await LatestPrice(db, planId, currency, on, ct).ConfigureAwait(false) is { } price && price.OpenOn(on) ? price : null;

    /// <summary>La última vigencia del plan en la moneda que empezó, aunque la tabla de precios la haya cerrado.</summary>
    public static Task<PlanPrice?> LatestPrice(BillingDbContext db, long planId, Currency currency, DateOnly on, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.PlanPrices
            .Where(p => p.PlanId == planId && p.Currency == currency && p.ValidFrom <= on)
            .OrderByDescending(p => p.ValidFrom)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Las opciones que ofrece el producto: las de los grupos que tiene asociados, con valores y precios.</summary>
    public static async Task<OptionCatalog> Options(BillingDbContext db, long productId, CancellationToken ct) =>
        (await Options(db, [productId], ct).ConfigureAwait(false))[productId];

    /// <summary>Las opciones de varios productos en cuatro consultas, como las de <see cref="Options(BillingDbContext, long, CancellationToken)"/>.</summary>
    public static async Task<Dictionary<long, OptionCatalog>> Options(BillingDbContext db, IReadOnlyCollection<long> productIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var links = await db.ProductOptionGroups.AsNoTracking().Where(l => productIds.Contains(l.ProductId)).ToListAsync(ct).ConfigureAwait(false);
        var groupIds = links.Select(l => l.OptionGroupId).Distinct().ToList();
        var options = await db.Options.AsNoTracking().Where(o => groupIds.Contains(o.GroupId)).ToListAsync(ct).ConfigureAwait(false);
        var optionIds = options.Select(o => o.Id).ToList();
        var values = await db.OptionValues.AsNoTracking().Where(v => optionIds.Contains(v.OptionId)).ToListAsync(ct).ConfigureAwait(false);
        var valueIds = values.Select(v => v.Id).ToList();
        var prices = (await db.OptionPrices.AsNoTracking().Where(p => valueIds.Contains(p.ValueId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(p => p.ValueId);
        return productIds.Distinct().ToDictionary(id => id, id =>
        {
            var groups = links.Where(l => l.ProductId == id).Select(l => l.OptionGroupId).ToHashSet();
            var own = options.Where(o => groups.Contains(o.GroupId)).ToList();
            var ownValues = values.Where(v => own.Any(o => o.Id == v.OptionId)).ToList();
            return new OptionCatalog(own, ownValues, ownValues.SelectMany(v => prices[v.Id]));
        });
    }

    /// <summary>Lo que el servicio tiene elegido de cada opción, con sus nombres. Los importes no se recalculan.</summary>
    public static async Task<List<ChosenOption>> Chosen(BillingDbContext db, long serviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from so in db.ServiceOptions
                          where so.ServiceId == serviceId
                          join o in db.Options on so.OptionId equals o.Id
                          join v in db.OptionValues on so.ValueId equals v.Id
                          select new { Option = o, Value = v, so.Quantity }).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.Option.SortOrder).ThenBy(r => r.Option.Id).Select(r => new ChosenOption(r.Option, r.Value, r.Quantity, 0m, 0m))];
    }

    /// <summary>
    /// El nombre del producto con lo elegido que el cliente ve en la factura: "VPS · RAM: 4 GB, IP extra: 2". Las
    /// opciones ocultas y las de cantidad cero no se nombran.
    /// </summary>
    public static string WithOptions(string productName, IEnumerable<ChosenOption> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var parts = chosen.Where(c => !c.Option.Hidden && (!c.Option.SingleValue || c.Quantity > 0))
            .Select(c => c.Option.Kind switch
            {
                OptionKind.Quantity => $"{c.Option.Name}: {c.Quantity}",
                OptionKind.YesNo => c.Option.Name,
                _ => $"{c.Option.Name}: {c.Value.Name}",
            })
            .ToList();
        var text = parts.Count == 0 ? productName : $"{productName} · {string.Join(", ", parts)}";
        return text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 399), "…");
    }
}
