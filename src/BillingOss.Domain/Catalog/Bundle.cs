namespace BillingOss.Domain.Catalog;

/// <summary>
/// Paquete de productos (diseno/catalogo.md): planes que se venden juntos con un descuento porcentual en cada uno. El
/// pedido trae las líneas del paquete marcadas, con sus opciones como cualquier otra, y tienen que ser exactamente sus
/// planes con sus unidades.
/// </summary>
public sealed class Bundle
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Texto para la tienda y la API, sin HTML.</summary>
    public string? Description { get; set; }

    /// <summary>Los planes del paquete, uno por unidad: un plan que aparece dos veces lleva dos unidades.</summary>
    public long[] PlanIds { get; set; } = [];

    /// <summary>De 0 a 100, sobre el precio de cada línea del paquete con sus opciones.</summary>
    public decimal DiscountPercent { get; set; }

    /// <summary>Períodos con descuento de un plan recurrente, contando el primero. Nulo es para siempre.</summary>
    public int? DiscountCycles { get; set; }

    /// <summary>Apagado no se ofrece ni se puede pedir.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Las líneas son el paquete: los mismos planes con las mismas unidades, ni más ni menos.</summary>
    public bool Matches(IEnumerable<(long PlanId, int Quantity)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var asked = lines.GroupBy(l => l.PlanId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var needed = PlanIds.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        return asked.Count == needed.Count && needed.All(n => asked.GetValueOrDefault(n.Key) == n.Value);
    }

    /// <summary>Períodos de descuento que le quedan al servicio de una línea del paquete: uno si no renueva.</summary>
    public int? CyclesFor(BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return cycle.IsRecurring ? DiscountCycles : 1;
    }
}
