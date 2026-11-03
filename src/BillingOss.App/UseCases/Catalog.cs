using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.UseCases;

public sealed record ProductGroupInput(string Name, string? Headline = null, bool Visible = true, int SortOrder = 0);

/// <summary>Precio de un ciclo ("P1M", "P1Y", "onetime" o "free") en una moneda, con su cargo de alta.</summary>
public sealed record PriceInput(string Cycle, string Currency, decimal Amount, decimal SetupFee = 0m);

/// <summary>
/// Un producto con su tabla de precios y sus grupos de opciones. Los ciclos que no vienen en <paramref name="Prices"/>
/// dejan de venderse, y un precio distinto del vigente abre una vigencia nueva desde hoy: los servicios existentes no
/// cambian. Sin <paramref name="Prices"/> o sin <paramref name="OptionGroupIds"/> quedan como están.
/// </summary>
public sealed record ProductInput(
    string Name,
    ProductKind Kind = ProductKind.Manual,
    long? GroupId = null,
    string? Description = null,
    bool Visible = true,
    int SortOrder = 0,
    string? TaxCategory = null,
    bool AllowQuantity = false,
    int? Stock = null,
    int? TrialDays = null,
    bool Addon = false,
    IReadOnlyList<long>? ParentProductIds = null,
    IReadOnlyList<long>? UpgradeProductIds = null,
    bool OptionChanges = false,
    long? ModuleInstanceId = null,
    string? ModuleParams = null,
    int? TerminationDays = null,
    IReadOnlyList<PriceInput>? Prices = null,
    IReadOnlyList<long>? OptionGroupIds = null,
    ProductActivation Activation = ProductActivation.OnPayment);

/// <summary>Un valor de una opción. Con <paramref name="Prices"/> se reemplazan todos sus precios.</summary>
public sealed record OptionValueInput(long? Id, string Name, string? ModuleValue = null, int SortOrder = 0, bool Hidden = false, IReadOnlyList<PriceInput>? Prices = null);

public sealed record OptionInput(long? Id, string Name, OptionKind Kind = OptionKind.Select, string? ModuleKey = null, int MinQuantity = 0, int MaxQuantity = 0, int SortOrder = 0, bool Hidden = false, IReadOnlyList<OptionValueInput>? Values = null);

/// <summary>
/// Un grupo de opciones con sus opciones y valores. Lo que no viene no se borra: un valor que ya no se ofrece se
/// oculta o se queda sin precios, porque los servicios lo siguen teniendo. Con <paramref name="ProductIds"/> se
/// reemplazan los productos que lo ofrecen.
/// </summary>
public sealed record OptionGroupInput(string Name, string? Description = null, IReadOnlyList<OptionInput>? Options = null, IReadOnlyList<long>? ProductIds = null);

public sealed record CouponInput(
    string Code,
    DiscountKind Kind,
    decimal Value,
    string? Currency = null,
    bool Recurring = false,
    int? RecurringCycles = null,
    IReadOnlyList<long>? ProductIds = null,
    IReadOnlyList<string>? Cycles = null,
    DateOnly? StartsOn = null,
    DateOnly? ExpiresOn = null,
    int? MaxUses = null,
    bool OncePerAccount = false,
    bool NewAccountsOnly = false,
    bool ExistingAccountsOnly = false,
    bool OncePerOrder = false,
    bool Active = true,
    string? Notes = null);

/// <summary>Un plan del paquete y cuántas unidades lleva.</summary>
public sealed record BundleItemInput(long PlanId, int Quantity = 1);

/// <summary>Un paquete de productos: sus planes y el descuento de cada línea, por <paramref name="DiscountCycles"/> períodos o para siempre.</summary>
public sealed record BundleInput(string Name, IReadOnlyList<BundleItemInput> Items, decimal DiscountPercent = 0m, int? DiscountCycles = null, string? Description = null, bool Active = true);

/// <summary>
/// Edición del catálogo (diseno/catalogo.md): grupos, productos con sus precios, opciones, cupones y paquetes. La usan
/// la API propia y el panel del staff. Cada cambio queda en la auditoría.
/// </summary>
public sealed class CatalogService(BillingDbContext db, BillingCore core, AuditLog audit)
{
    /// <summary>Tope de unidades de un paquete: cada una es una línea de servicio.</summary>
    public const int MaxBundleUnits = 50;

    public async Task<long> SaveGroup(long? id, ProductGroupInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var group = id is { } existing
            ? await db.ProductGroups.SingleOrDefaultAsync(g => g.Id == existing, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el grupo {existing}.")
            : db.ProductGroups.Add(new ProductGroup()).Entity;
        group.Name = Required(input.Name, "El grupo");
        group.Headline = Fit(input.Headline, 500, "La bajada del grupo");
        group.Visible = input.Visible;
        group.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("catalog.group.saved", $"product_group:{group.Id}", ct: ct).ConfigureAwait(false);
        return group.Id;
    }

    public async Task<long> SaveProduct(long? id, ProductInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var product = id is { } existing
            ? await db.Products.SingleOrDefaultAsync(p => p.Id == existing, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el producto {existing}.")
            : db.Products.Add(new Product()).Entity;
        if (input.GroupId is { } groupId && !await db.ProductGroups.AnyAsync(g => g.Id == groupId, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe el grupo {groupId}.");
        }

        if (input.ModuleInstanceId is { } instanceId && !await db.ModuleInstances.AnyAsync(i => i.Id == instanceId, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe la instancia de módulo {instanceId}.");
        }

        var related = (input.ParentProductIds ?? []).Concat(input.UpgradeProductIds ?? []).Distinct().ToList();
        if (related.Except(await db.Products.Where(p => related.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault() is var missing and > 0)
        {
            throw new ValidationException($"No existe el producto {missing}.");
        }

        // La API acepta un número en lugar del nombre: uno que no es de la lista no se guarda.
        if (!Enum.IsDefined(input.Kind) || !Enum.IsDefined(input.Activation))
        {
            throw new ValidationException("El tipo o el alta del producto no existen.");
        }

        // Sin categoría queda la que tenía, o la de servicios digitales y hosting en un producto nuevo.
        if (Blank(input.TaxCategory) is { } category && !await db.TaxCategories.AnyAsync(c => c.Code == category, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe la categoría de impuesto {category}.");
        }

        if (input.Stock is < 0 || input.TrialDays is < 0 || input.TerminationDays is < 0)
        {
            throw new ValidationException("El stock y los días de prueba y de baja no pueden ser negativos.");
        }

        product.Name = Required(input.Name, "El producto");
        product.Kind = input.Kind;
        product.GroupId = input.GroupId;
        product.Description = Fit(input.Description, 4000, "La descripción del producto");
        product.Visible = input.Visible;
        product.SortOrder = input.SortOrder;
        product.TaxCategory = Blank(input.TaxCategory) ?? product.TaxCategory;
        product.AllowQuantity = input.AllowQuantity;
        product.Stock = input.Stock;
        product.TrialDays = input.TrialDays is > 0 ? input.TrialDays : null;
        product.Addon = input.Addon;
        product.ParentProductIds = input.Addon ? [.. input.ParentProductIds ?? []] : [];
        product.UpgradeProductIds = [.. input.UpgradeProductIds ?? []];
        product.OptionChanges = input.OptionChanges;
        product.ModuleInstanceId = input.ModuleInstanceId;
        product.ModuleParams = JsonObjectText(input.ModuleParams, "Los parámetros del módulo");
        product.TerminationDays = input.TerminationDays;
        product.Activation = input.Activation;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (input.Prices is { } prices)
        {
            await SavePrices(product.Id, prices, ct).ConfigureAwait(false);
        }

        if (input.OptionGroupIds is { } groups)
        {
            var known = await db.OptionGroups.Where(g => groups.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct).ConfigureAwait(false);
            if (groups.Except(known).FirstOrDefault() is var unknown and > 0)
            {
                throw new ValidationException($"No existe el grupo de opciones {unknown}.");
            }

            await db.ProductOptionGroups.Where(l => l.ProductId == product.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.ProductOptionGroups.AddRange(known.Distinct().Select(g => new ProductOptionGroup(product.Id, g)));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await audit.Record("catalog.product.saved", $"product:{product.Id}", ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return product.Id;
    }

    /// <summary>
    /// Un producto nuevo igual a otro, con sus precios de hoy, sus grupos de opciones y su módulo, oculto hasta que el staff
    /// lo revise y lo muestre. Lo contratado del original no se toca.
    /// </summary>
    public async Task<long> CopyProduct(long id, string? name = null, CancellationToken ct = default)
    {
        var p = (await CatalogQueries.Products(db, core.Today(), [id], ct).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new KeyNotFoundException($"No existe el producto {id}.");
        var copy = await SaveProduct(null, new ProductInput(
            Name: CopyName(name, p.Name),
            Kind: p.Kind,
            GroupId: p.GroupId,
            Description: p.Description,
            Visible: false,
            SortOrder: p.SortOrder,
            TaxCategory: p.TaxCategory,
            AllowQuantity: p.AllowQuantity,
            Stock: p.Stock,
            TrialDays: p.TrialDays,
            Addon: p.Addon,
            ParentProductIds: p.ParentProductIds,
            UpgradeProductIds: p.UpgradeProductIds,
            OptionChanges: p.OptionChanges,
            ModuleInstanceId: p.ModuleInstanceId,
            ModuleParams: p.ModuleParams.ToJsonString(),
            TerminationDays: p.TerminationDays,
            Prices: [.. p.Prices.Select(x => new PriceInput(x.Cycle, x.Currency, x.Amount, x.SetupFee))],
            OptionGroupIds: p.OptionGroupIds,
            Activation: p.Activation), ct).ConfigureAwait(false);
        await audit.Record("catalog.product.copied", $"product:{copy}", new { from_product_id = id }, ct).ConfigureAwait(false);
        return copy;
    }

    /// <summary>Un grupo de opciones nuevo igual a otro, con sus opciones, valores y precios, sin productos: se asocian aparte.</summary>
    public async Task<long> CopyOptionGroup(long id, string? name = null, CancellationToken ct = default)
    {
        var g = (await CatalogQueries.OptionGroups(db, [id], ct).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new KeyNotFoundException($"No existe el grupo de opciones {id}.");
        var copy = await SaveOptionGroup(null, new OptionGroupInput(
            CopyName(name, g.Name),
            g.Description,
            [.. g.Options.Select(o => new OptionInput(null, o.Name, o.Kind, o.ModuleKey, o.MinQuantity, o.MaxQuantity, o.SortOrder, o.Hidden,
                [.. o.Values.Select(v => new OptionValueInput(null, v.Name, v.ModuleValue, v.SortOrder, v.Hidden, [.. v.Prices.Select(x => new PriceInput(x.Cycle, x.Currency, x.Amount, x.SetupFee))]))]))]), ct).ConfigureAwait(false);
        await audit.Record("catalog.option_group.copied", $"option_group:{copy}", new { from_option_group_id = id }, ct).ConfigureAwait(false);
        return copy;
    }

    public async Task<long> SaveOptionGroup(long? id, OptionGroupInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var group = id is { } existing
            ? await db.OptionGroups.SingleOrDefaultAsync(g => g.Id == existing, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el grupo de opciones {existing}.")
            : db.OptionGroups.Add(new OptionGroup()).Entity;
        group.Name = Required(input.Name, "El grupo de opciones");
        group.Description = Fit(input.Description, 1000, "La descripción del grupo de opciones");
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var o in input.Options ?? [])
        {
            await SaveOption(group.Id, o, ct).ConfigureAwait(false);
        }

        if (input.ProductIds is { } productIds)
        {
            var known = await db.Products.Where(p => productIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct).ConfigureAwait(false);
            if (productIds.Except(known).FirstOrDefault() is var unknown and > 0)
            {
                throw new ValidationException($"No existe el producto {unknown}.");
            }

            await db.ProductOptionGroups.Where(l => l.OptionGroupId == group.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.ProductOptionGroups.AddRange(known.Distinct().Select(p => new ProductOptionGroup(p, group.Id)));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await audit.Record("catalog.option_group.saved", $"option_group:{group.Id}", ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return group.Id;
    }

    public async Task<long> SaveCoupon(long? id, CouponInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var coupon = id is { } existing
            ? await db.Coupons.SingleOrDefaultAsync(c => c.Id == existing, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el cupón {existing}.")
            : db.Coupons.Add(new Coupon()).Entity;
        var code = Required(input.Code, "El cupón");
        if (code.Length > 100 || code.Any(char.IsWhiteSpace))
        {
            throw new ValidationException("El código del cupón va sin espacios y con hasta 100 caracteres.");
        }

        if (!Enum.IsDefined(input.Kind) || input.Value < 0m || (input.Kind == DiscountKind.Percent && input.Value > 100m))
        {
            throw new ValidationException("El descuento va de 0 a 100 % o es un monto positivo.");
        }

        if (input.NewAccountsOnly && input.ExistingAccountsOnly)
        {
            throw new ValidationException("Un cupón no puede ser solo para cuentas nuevas y solo para las que ya compraron.");
        }

        if (input.MaxUses is < 0 || input.RecurringCycles is < 1 || (input.StartsOn is { } start && input.ExpiresOn is { } end && end < start))
        {
            throw new ValidationException("Los usos, los ciclos y las fechas del cupón no son coherentes.");
        }

        // Lo usado no vuelve atrás: el tope nuevo no puede quedar abajo de los usos que ya tiene.
        if (input.MaxUses is > 0 and var max && max < coupon.Uses)
        {
            throw new ValidationException($"El cupón ya se usó {coupon.Uses} veces: el tope no puede ser menor.");
        }

        coupon.Code = code;
        coupon.Kind = input.Kind;
        coupon.Value = input.Value;
        coupon.Currency = input.Kind == DiscountKind.Fixed && Blank(input.Currency) is { } currency ? Money(currency) : null;
        coupon.Recurring = input.Recurring;
        coupon.RecurringCycles = input.Recurring ? input.RecurringCycles : null;
        coupon.ProductIds = [.. (input.ProductIds ?? []).Distinct()];
        coupon.Cycles = [.. (input.Cycles ?? []).Select(c => Cycle(c).Code).Distinct()];
        coupon.StartsOn = input.StartsOn;
        coupon.ExpiresOn = input.ExpiresOn;
        coupon.MaxUses = input.MaxUses is > 0 ? input.MaxUses : null;
        coupon.OncePerAccount = input.OncePerAccount;
        coupon.NewAccountsOnly = input.NewAccountsOnly;
        coupon.ExistingAccountsOnly = input.ExistingAccountsOnly;
        coupon.OncePerOrder = input.OncePerOrder;
        coupon.Active = input.Active;
        coupon.Notes = Fit(input.Notes, 1000, "Las notas del cupón");
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException($"Ya hay un cupón con el código {code}.");
        }

        await audit.Record("catalog.coupon.saved", $"coupon:{coupon.Id}", ct: ct).ConfigureAwait(false);
        return coupon.Id;
    }

    public async Task<long> SaveBundle(long? id, BundleInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bundle = id is { } existing
            ? await db.Bundles.SingleOrDefaultAsync(b => b.Id == existing, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el paquete {existing}.")
            : db.Bundles.Add(new Bundle()).Entity;
        var items = input.Items ?? [];
        if (items.Count == 0 || items.Any(i => i.Quantity < 1) || items.Sum(i => (long)i.Quantity) > MaxBundleUnits)
        {
            throw new ValidationException($"Un paquete lleva al menos un plan, cada uno con una unidad o más y hasta {MaxBundleUnits} en total.");
        }

        var planIds = items.Select(i => i.PlanId).Distinct().ToList();
        var active = await db.Plans.Where(p => planIds.Contains(p.Id) && p.Active).Select(p => p.Id).ToListAsync(ct).ConfigureAwait(false);
        if (planIds.Except(active).FirstOrDefault() is var missing and > 0)
        {
            throw new ValidationException($"No existe el plan {missing} o no está activo.");
        }

        if (input.DiscountPercent is < 0m or > 100m || input.DiscountCycles is < 1)
        {
            throw new ValidationException("El descuento del paquete va de 0 a 100 %, por uno o más períodos o para siempre.");
        }

        bundle.Name = Required(input.Name, "El paquete");
        bundle.Description = Fit(input.Description, 4000, "La descripción del paquete");
        bundle.PlanIds = [.. items.SelectMany(i => Enumerable.Repeat(i.PlanId, i.Quantity))];
        bundle.DiscountPercent = input.DiscountPercent;
        bundle.DiscountCycles = input.DiscountCycles;
        bundle.Active = input.Active;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("catalog.bundle.saved", $"bundle:{bundle.Id}", ct: ct).ConfigureAwait(false);
        return bundle.Id;
    }

    /// <summary>
    /// La tabla de precios de un producto: un plan por ciclo y un precio por moneda. Un precio igual al vigente no cambia
    /// nada. Uno distinto abre una vigencia desde hoy, o corrige la de hoy si ya existía. Un ciclo que no viene deja de
    /// venderse, y una moneda que no viene para un ciclo cierra su vigencia hoy.
    /// </summary>
    private async Task SavePrices(long productId, IReadOnlyList<PriceInput> prices, CancellationToken ct)
    {
        var today = core.Today();
        var plans = await db.Plans.Where(p => p.ProductId == productId).ToListAsync(ct).ConfigureAwait(false);
        var parsed = prices.Select(p => (Cycle: Cycle(p.Cycle), Currency: Money(p.Currency), p.Amount, p.SetupFee)).ToList();
        if (parsed.GroupBy(p => (p.Cycle, p.Currency)).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new ValidationException($"El ciclo {repeated.Key.Cycle.Code} en {repeated.Key.Currency} viene más de una vez.");
        }

        foreach (var plan in plans)
        {
            plan.Active = parsed.Any(p => p.Cycle == plan.Cycle);
        }

        foreach (var cycle in parsed.Select(p => p.Cycle).Distinct().Where(c => plans.All(p => p.Cycle != c)))
        {
            plans.Add(db.Plans.Add(new Plan(productId, cycle)).Entity);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var cycles = plans.ToDictionary(p => p.Id, p => p.Cycle);
        var planIds = cycles.Keys.ToList();
        foreach (var dropped in (await db.PlanPrices.Where(p => planIds.Contains(p.PlanId) && p.ValidFrom <= today).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => (p.PlanId, p.Currency))
            .Select(g => g.MaxBy(p => p.ValidFrom)!)
            .Where(p => p.OpenOn(today) && !parsed.Any(x => x.Cycle == cycles[p.PlanId] && x.Currency == p.Currency)))
        {
            dropped.Close(today);
        }

        foreach (var (cycle, currency, amount, setupFee) in parsed)
        {
            if (amount < 0m || setupFee < 0m || (cycle.Unit == CycleUnit.Free && (amount != 0m || setupFee != 0m)))
            {
                throw new ValidationException($"El precio de {cycle.Code} en {currency} no puede ser negativo, y el ciclo gratis va en cero.");
            }

            // La última vigencia, aunque esté cerrada: una moneda que se vuelve a cargar el día que se sacó corrige esa.
            var plan = plans.Single(p => p.Cycle == cycle);
            var current = await CatalogQueries.LatestPrice(db, plan.Id, currency, today, ct).ConfigureAwait(false);
            if (current is not null && current.OpenOn(today) && current.Amount == amount && current.SetupFee == setupFee)
            {
                continue;
            }

            if (current?.ValidFrom == today)
            {
                current.Change(amount, setupFee);
            }
            else
            {
                // Una vigencia nueva sigue el modo de carga de la anterior. Un precio nuevo toma el de su moneda (sección Impuestos).
                var mode = current?.PriceMode ?? (await core.Settings(ct).ConfigureAwait(false)).DefaultPriceMode(currency);
                db.PlanPrices.Add(new PlanPrice(plan.Id, currency, amount, setupFee, today, mode));
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SaveOption(long groupId, OptionInput input, CancellationToken ct)
    {
        if (!Enum.IsDefined(input.Kind))
        {
            throw new ValidationException($"La opción {input.Name} tiene un tipo que no existe.");
        }

        var option = input.Id is { } id
            ? await db.Options.SingleOrDefaultAsync(o => o.Id == id && o.GroupId == groupId, ct).ConfigureAwait(false) ?? throw new ValidationException($"La opción {id} no es de este grupo.")
            : db.Options.Add(new Option { GroupId = groupId }).Entity;
        var key = Fit(input.ModuleKey, 200, "La clave de módulo");
        if (key is not null && !Option.IsValidModuleKey(key))
        {
            throw new ValidationException($"La clave de módulo {key} va con letras, números y guiones bajos, y puntos para anidar.");
        }

        if (input.MinQuantity < 0 || input.MaxQuantity < 0 || (input.MaxQuantity > 0 && input.MinQuantity > input.MaxQuantity))
        {
            throw new ValidationException($"La cantidad de {input.Name} va de un mínimo a un máximo positivos, con cero como sin tope.");
        }

        option.Name = Required(input.Name, "La opción");
        option.Kind = input.Kind;
        option.ModuleKey = key;
        option.MinQuantity = input.Kind == OptionKind.Quantity ? input.MinQuantity : 0;
        option.MaxQuantity = input.Kind == OptionKind.Quantity ? input.MaxQuantity : 0;
        option.SortOrder = input.SortOrder;
        option.Hidden = input.Hidden;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var v in input.Values ?? [])
        {
            var value = v.Id is { } valueId
                ? await db.OptionValues.SingleOrDefaultAsync(x => x.Id == valueId && x.OptionId == option.Id, ct).ConfigureAwait(false) ?? throw new ValidationException($"El valor {valueId} no es de la opción {option.Name}.")
                : db.OptionValues.Add(new OptionValue { OptionId = option.Id }).Entity;
            value.Name = Required(v.Name, "El valor");
            value.ModuleValue = Fit(v.ModuleValue, 200, "El valor de módulo");
            value.SortOrder = v.SortOrder;
            value.Hidden = v.Hidden;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (v.Prices is { } prices)
            {
                var parsed = prices.Select(p => (Cycle: Cycle(p.Cycle), Currency: Money(p.Currency), p.Amount, p.SetupFee)).ToList();
                if (parsed.GroupBy(p => (p.Cycle, p.Currency)).Any(g => g.Count() > 1) || parsed.Any(p => p.Amount < 0m || p.SetupFee < 0m))
                {
                    throw new ValidationException($"Los precios de {option.Name}: {value.Name} van una vez por ciclo y moneda, y nunca negativos.");
                }

                await db.OptionPrices.Where(p => p.ValueId == value.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                db.OptionPrices.AddRange(parsed.Select(p => new OptionPrice(value.Id, p.Currency, p.Cycle, p.Amount, p.SetupFee)));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        // Una opción de sí o no o de cantidad tiene un solo valor, que lleva el precio. Una lista tiene al menos uno.
        var count = await db.OptionValues.CountAsync(v => v.OptionId == option.Id, ct).ConfigureAwait(false);
        if (option.SingleValue ? count != 1 : count == 0)
        {
            throw new ValidationException(option.SingleValue
                ? $"La opción {option.Name} va con un solo valor, que lleva el precio."
                : $"La opción {option.Name} necesita al menos un valor.");
        }
    }

    /// <summary>El nombre, que va con hasta 200 caracteres como su columna: más largo, la base rechazaría todo sin decir por qué.</summary>
    private static string Required(string? text, string what) =>
        Blank(text) is { Length: > 200 }
            ? throw new ValidationException($"{what} va con un nombre de hasta 200 caracteres.")
            : Blank(text) ?? throw new ValidationException($"{what} necesita un nombre.");

    /// <summary>Un texto opcional con el largo de su columna.</summary>
    private static string? Fit(string? text, int max, string what) =>
        Blank(text) is { } value && value.Length > max ? throw new ValidationException($"{what} va con hasta {max} caracteres.") : Blank(text);

    /// <summary>El nombre de una copia: el pedido, o el del original con "(copia)", dentro del largo de su columna.</summary>
    private static string CopyName(string? name, string original) =>
        Blank(name) ?? (original.Length > 192 ? original[..192] : original) + " (copia)";

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static BillingCycle Cycle(string code)
    {
        try
        {
            return BillingCycle.Parse(code);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }

    private static Currency Money(string code)
    {
        try
        {
            return new Currency(code.Trim().ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            throw new ValidationException($"Moneda inválida: {code}.");
        }
    }

    private static string JsonObjectText(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "{}";
        }

        try
        {
            return JsonNode.Parse(text) is JsonObject json ? json.ToJsonString() : throw new ValidationException($"{what} van como un objeto JSON.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ValidationException($"{what} van como un objeto JSON.");
        }
    }
}
