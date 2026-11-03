using BillingOss.Domain.Catalog;

namespace BillingOss.Domain.Services;

/// <summary>El valor o la cantidad que el servicio tiene de una opción configurable.</summary>
public sealed class ServiceOption
{
    private ServiceOption()
    {
    }

    public ServiceOption(long serviceId, long optionId, long valueId, int quantity)
    {
        ServiceId = serviceId;
        OptionId = optionId;
        ValueId = valueId;
        Quantity = quantity;
    }

    public long Id { get; private set; }

    public long ServiceId { get; private set; }

    public long OptionId { get; private set; }

    public long ValueId { get; private set; }

    /// <summary>La cantidad de una opción de cantidad, 0 o 1 en una de sí o no y 1 en una lista.</summary>
    public int Quantity { get; private set; }

    public OptionChoice Choice => new(OptionId, ValueId, Quantity);
}

public enum ServiceChangeStatus
{
    /// <summary>Espera el pago de su factura.</summary>
    Pending,

    Applied,

    Cancelled,
}

/// <summary>
/// Cambio de plan o de opciones de un servicio (diseno/catalogo.md). Uno que cobra espera el pago de su factura. Uno
/// que acredita o que no mueve plata se aplica enseguida. Un servicio tiene a lo sumo uno pendiente.
/// </summary>
public sealed class ServiceChange
{
    private ServiceChange()
    {
    }

    public ServiceChange(long serviceId, long fromProductId, long toProductId, BillingCycle cycle, decimal fromUnitPrice, decimal toUnitPrice, string options, decimal amount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ServiceId = serviceId;
        FromProductId = fromProductId;
        ToProductId = toProductId;
        CycleUnit = cycle.Unit;
        CycleCount = cycle.Count;
        FromUnitPrice = fromUnitPrice;
        ToUnitPrice = toUnitPrice;
        Options = options;
        Amount = amount;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    public long ServiceId { get; private set; }

    public ServiceChangeStatus Status { get; private set; }

    public long FromProductId { get; private set; }

    public long ToProductId { get; private set; }

    /// <summary>Ciclo del servicio después del cambio.</summary>
    public CycleUnit CycleUnit { get; private set; }

    public int CycleCount { get; private set; }

    public decimal FromUnitPrice { get; private set; }

    public decimal ToUnitPrice { get; private set; }

    /// <summary>Opciones después del cambio, como lista de <see cref="OptionChoice"/>. JSON.</summary>
    public string Options { get; private set; } = "[]";

    /// <summary>Prorrateo: positivo se cobra con una factura, negativo se acredita con una nota de crédito.</summary>
    public decimal Amount { get; private set; }

    /// <summary>
    /// Hasta dónde estaba facturado el servicio al pedirlo: el prorrateo llega hasta acá. Si al pagarse ya se facturó un
    /// período más con el precio viejo, la diferencia de ese período se factura aparte.
    /// </summary>
    public DateOnly? BilledUntil { get; set; }

    /// <summary>El descuento del servicio no vale para el producto o el ciclo nuevo: al aplicarse, el cambio lo termina.</summary>
    public bool EndsDiscount { get; set; }

    /// <summary>La factura que lo cobra o la nota que lo acredita.</summary>
    public long? DocumentId { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    public BillingCycle Cycle => new(CycleUnit, CycleCount);

    public void Apply(DateTimeOffset now)
    {
        if (Status != ServiceChangeStatus.Pending)
        {
            throw new InvalidOperationException($"El cambio {Id} ya está {Status}.");
        }

        Status = ServiceChangeStatus.Applied;
        AppliedAt = now;
    }

    public void Cancel()
    {
        if (Status != ServiceChangeStatus.Pending)
        {
            throw new InvalidOperationException($"El cambio {Id} ya está {Status}.");
        }

        Status = ServiceChangeStatus.Cancelled;
    }
}
