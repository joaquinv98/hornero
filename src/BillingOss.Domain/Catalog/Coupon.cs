using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Catalog;

public enum DiscountKind
{
    Percent,
    Fixed,
}

/// <summary>
/// Cupón (sección 3 del modelo de dominio y diseno/catalogo.md): descuento porcentual o fijo por unidad sobre el precio
/// del período, para ciertos productos y ciclos, con ventana de validez y usos máximos. El servicio guarda el
/// descuento y los ciclos que le quedan, y el contador baja en cada factura.
/// </summary>
public sealed class Coupon
{
    public long Id { get; private set; }

    /// <summary>Lo que escribe el cliente. Único sin distinguir mayúsculas.</summary>
    public string Code { get; set; } = string.Empty;

    public DiscountKind Kind { get; set; }

    /// <summary>Porcentaje (de 0 a 100) o monto fijo por unidad.</summary>
    public decimal Value { get; set; }

    /// <summary>Moneda del monto fijo. Sin moneda vale el mismo número en cualquiera, como en el sistema anterior.</summary>
    public Currency? Currency { get; set; }

    /// <summary>Sigue en las renovaciones. Si no, descuenta solo la primera factura.</summary>
    public bool Recurring { get; set; }

    /// <summary>Períodos con descuento de un cupón recurrente, contando el primero. Nulo es para siempre.</summary>
    public int? RecurringCycles { get; set; }

    /// <summary>Productos donde vale. Vacío es en todos.</summary>
    public long[] ProductIds { get; set; } = [];

    /// <summary>Ciclos donde vale, con <see cref="BillingCycle.Code"/>. Vacío es en todos.</summary>
    public string[] Cycles { get; set; } = [];

    public DateOnly? StartsOn { get; set; }

    /// <summary>Último día en que vale.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Pedidos que lo pueden usar. Nulo es sin tope.</summary>
    public int? MaxUses { get; set; }

    public int Uses { get; set; }

    public bool OncePerAccount { get; set; }

    public bool NewAccountsOnly { get; set; }

    public bool ExistingAccountsOnly { get; set; }

    /// <summary>En un pedido con varias líneas, descuenta solo la primera donde vale.</summary>
    public bool OncePerOrder { get; set; }

    public bool Active { get; set; } = true;

    public string? Notes { get; set; }

    public void SetImportedId(long id) => Id = id;

    /// <summary>Por qué no se puede usar hoy en un pedido de la cuenta, o nulo si se puede.</summary>
    public string? Rejection(DateOnly today, bool accountHasOrders, bool accountUsedIt)
    {
        if (!Active || (StartsOn is { } start && today < start) || (ExpiresOn is { } end && today > end))
        {
            return $"El cupón {Code} no está vigente.";
        }

        if (MaxUses is { } max && Uses >= max)
        {
            return $"El cupón {Code} ya se usó todas las veces que permite.";
        }

        if (NewAccountsOnly && accountHasOrders)
        {
            return $"El cupón {Code} es solo para cuentas nuevas.";
        }

        if (ExistingAccountsOnly && !accountHasOrders)
        {
            return $"El cupón {Code} es solo para cuentas que ya compraron.";
        }

        return OncePerAccount && accountUsedIt ? $"El cupón {Code} ya se usó en esta cuenta." : null;
    }

    public bool AppliesTo(long productId, BillingCycle cycle, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return cycle.Unit != CycleUnit.Free
            && (ProductIds.Length == 0 || ProductIds.Contains(productId))
            && (Cycles.Length == 0 || Cycles.Contains(cycle.Code, StringComparer.OrdinalIgnoreCase))
            && (Kind == DiscountKind.Percent || Currency is null || Currency == currency);
    }

    /// <summary>Ciclos de descuento que le quedan al servicio que lo usa: uno si no es recurrente.</summary>
    public int? CyclesFor(BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return Recurring && cycle.IsRecurring ? RecurringCycles : 1;
    }
}
