using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Usage;

/// <summary>Cómo informa el módulo una métrica (sección 1.1 de diseno/uso-medido.md).</summary>
public enum UsageKind
{
    /// <summary>Un nivel: disco ocupado, casillas, dispositivos. El uso del día es la lectura.</summary>
    Gauge,

    /// <summary>Un acumulado que solo sube y puede volver a cero. El uso del día es lo que subió desde la lectura anterior.</summary>
    Counter,
}

/// <summary>Cómo se resume una ventana de muestras.</summary>
public enum UsageAggregation
{
    Max,
    Average,
    Sum,
}

/// <summary>Cuándo se cobra el excedente.</summary>
public enum UsageCycle
{
    /// <summary>Con la renovación del servicio, en su misma factura.</summary>
    Renewal,

    /// <summary>Al cerrar cada mes calendario, en una factura de uso.</summary>
    CalendarMonth,
}

/// <summary>
/// Lo que se mide de los servicios de un producto para cobrar el excedente: lo incluido por ventana, el paso de
/// facturación, el momento de cobro y los umbrales de aviso. El código es el de la métrica en el módulo.
/// </summary>
public sealed class UsageMetric
{
    private UsageMetric()
    {
    }

    public UsageMetric(long productId, string code, UsageKind kind, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ProductId = productId;
        Code = code;
        Name = code;
        Kind = kind;
        Aggregation = kind == UsageKind.Counter ? UsageAggregation.Sum : UsageAggregation.Max;
        CreatedAt = now;
        UpdatedAt = now;
        ActiveSince = now;
    }

    public long Id { get; private set; }

    public long ProductId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Unit { get; private set; } = string.Empty;

    public UsageKind Kind { get; private set; }

    public UsageAggregation Aggregation { get; private set; }

    /// <summary>Lo incluido en cada ventana, en la unidad de la métrica.</summary>
    public decimal Included { get; private set; }

    /// <summary>El excedente se cobra en múltiplos de este paso, redondeado hacia arriba.</summary>
    public decimal Step { get; private set; } = 1m;

    public UsageCycle Cycle { get; private set; }

    /// <summary>Porcentajes de lo incluido que avisan al cliente, de menor a mayor.</summary>
    public int[] AlertPercents { get; private set; } = [80, 100];

    public bool Active { get; private set; } = true;

    /// <summary>
    /// Desde cuándo está activa sin cortes. Un contador no se encadena con una lectura de antes: lo que pasó mientras
    /// estaba inactiva no se cobra.
    /// </summary>
    public DateTimeOffset ActiveSince { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Lo incluido en la ventana [start, end) de un servicio con ese ciclo. Lo que se suma (transferencia, CPU) se
    /// incluye por mes: uno por cada mes que dura la ventana, redondeado y como mínimo uno, y con la renovación nunca
    /// menos que los meses del plan, así la de un plan anual incluye doce. Una ventana que quedó abierta más de lo
    /// normal (sin precio, o una renovación que esperó un pago) incluye también esos meses y no cobra lo incluido como
    /// excedente. Un nivel (disco, casillas) vale igual en cualquier ventana.
    /// </summary>
    public decimal IncludedFor(BillingCycle cycle, DateOnly start, DateOnly end)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (Aggregation != UsageAggregation.Sum)
        {
            return Included;
        }

        var months = Math.Max(1, (int)Math.Round((end.DayNumber - start.DayNumber) / DaysPerMonth, MidpointRounding.AwayFromZero));
        return Included * (Cycle == UsageCycle.Renewal ? Math.Max(cycle.Months, months) : months);
    }

    /// <summary>Un mes promedio: una ventana de 28 a 45 días es un mes y una de 46 a 76, dos.</summary>
    private const decimal DaysPerMonth = 365.25m / 12m;

    /// <summary>Cambia la configuración. Una regla que no se cumple da <see cref="ArgumentException"/> con el motivo.</summary>
    public void Configure(string name, string unit, UsageAggregation aggregation, decimal included, decimal step, UsageCycle cycle, IEnumerable<int> alertPercents, bool active, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alertPercents);
        var percents = alertPercents.Distinct().Order().ToArray();
        var problem = name is not { Length: > 0 and <= 100 } || string.IsNullOrWhiteSpace(name) ? "El nombre va con 1 a 100 caracteres."
            : unit is not { Length: <= 16 } ? "La unidad va con hasta 16 caracteres."
            : Kind == UsageKind.Counter && aggregation != UsageAggregation.Sum ? "Un contador se resume con la suma."
            : Kind == UsageKind.Gauge && aggregation == UsageAggregation.Sum ? "Un medidor se resume con el máximo o el promedio."
            : included < 0m || included > 999_999_999_999m || !Rounding.HasAtMostDecimals(included, 4) ? "Lo incluido va de 0 en adelante, con 4 decimales como máximo."
            : step <= 0m || step > 999_999_999m || !Rounding.HasAtMostDecimals(step, 4) ? "El paso va mayor que 0, con 4 decimales como máximo."
            : percents.Any(p => p is < 1 or > 1000) || percents.Length > 10 ? "Los umbrales van de 1 a 1000 %, hasta 10."
            : null;
        if (problem is not null)
        {
            throw new ArgumentException(problem);
        }

        if (active && !Active)
        {
            ActiveSince = now;
        }

        Name = name.Trim();
        Unit = unit.Trim();
        Aggregation = aggregation;
        Included = included;
        Step = step;
        Cycle = cycle;
        AlertPercents = percents;
        Active = active;
        UpdatedAt = now;
    }
}

/// <summary>Precio de una unidad de excedente en una moneda, con vigencia. Se agrega, no se edita.</summary>
public sealed class UsageMetricPrice
{
    private UsageMetricPrice()
    {
    }

    public UsageMetricPrice(long metricId, Currency currency, decimal unitPrice, DateOnly validFrom)
    {
        if (unitPrice < 0m || unitPrice > 999_999_999_999m || !Rounding.HasAtMostDecimals(unitPrice, 6))
        {
            throw new ArgumentException("El precio va de 0 en adelante, con 6 decimales como máximo.");
        }

        MetricId = metricId;
        Currency = currency;
        UnitPrice = unitPrice;
        ValidFrom = validFrom;
    }

    public long Id { get; private set; }

    public long MetricId { get; private set; }

    public Currency Currency { get; private set; }

    public decimal UnitPrice { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    /// <summary>El vigente en la moneda para una fecha, o null.</summary>
    public static UsageMetricPrice? On(IEnumerable<UsageMetricPrice> prices, long metricId, Currency currency, DateOnly date) =>
        prices.Where(p => p.MetricId == metricId && p.Currency == currency && p.ValidFrom <= date).MaxBy(p => p.ValidFrom);
}

/// <summary>
/// Lectura diaria de una métrica de un servicio: la lectura cruda del módulo y el uso que se le atribuye al día. Una
/// por servicio, métrica y día. Queda facturada cuando un cargo la toma.
/// </summary>
public sealed class UsageSample
{
    private UsageSample()
    {
    }

    public UsageSample(long serviceId, string code, DateOnly date, decimal reading, decimal value, DateTimeOffset readAt)
    {
        ServiceId = serviceId;
        Code = code;
        Date = date;
        Reading = reading;
        Value = value;
        ReadAt = readAt;
    }

    public long Id { get; private set; }

    public long ServiceId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>El día que mide la muestra, en la zona del proveedor.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Lo que devolvió el módulo, tal cual.</summary>
    public decimal Reading { get; private set; }

    /// <summary>El uso del día: la lectura en un medidor, lo que subió en un contador.</summary>
    public decimal Value { get; private set; }

    public DateTimeOffset ReadAt { get; private set; }

    public long? ChargeId { get; private set; }

    public UsageCharge? Charge { get; private set; }

    public void BilledBy(UsageCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);
        if (ChargeId is not null || Charge is not null)
        {
            throw new InvalidOperationException($"La muestra {Id} ya está facturada.");
        }

        Charge = charge;
    }
}

/// <summary>
/// El cierre de una ventana [Start, End) de una métrica de un servicio: lo medido, lo incluido y el excedente, con el
/// precio y el renglón que lo cobra. Sin excedente no tiene renglón. Único por servicio, métrica y corte.
/// </summary>
public sealed class UsageCharge
{
    private UsageCharge()
    {
    }

    public UsageCharge(long serviceId, string code, long metricId, DateOnly start, DateOnly end, decimal measured, decimal included, decimal excess, decimal? unitPrice, Currency currency, DateTimeOffset now)
    {
        if (end <= start)
        {
            throw new ArgumentException($"Ventana vacía: {start} a {end}.", nameof(end));
        }

        ServiceId = serviceId;
        Code = code;
        MetricId = metricId;
        Start = start;
        End = end;
        Measured = measured;
        Included = included;
        Excess = excess;
        UnitPrice = unitPrice;
        Currency = currency;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    public long ServiceId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public long MetricId { get; private set; }

    public DateOnly Start { get; private set; }

    /// <summary>El corte: la ventana tiene las muestras de antes de este día.</summary>
    public DateOnly End { get; private set; }

    public decimal Measured { get; private set; }

    public decimal Included { get; private set; }

    public decimal Excess { get; private set; }

    public decimal? UnitPrice { get; private set; }

    public Currency Currency { get; private set; }

    public long? DocumentLineId { get; private set; }

    public DocumentLine? Line { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void BillOn(DocumentLine line) => Line = line ?? throw new ArgumentNullException(nameof(line));
}

/// <summary>Las cuentas del uso medido, sin base (secciones 1.1 y 4 de diseno/uso-medido.md).</summary>
public static class UsageMath
{
    /// <summary>
    /// Uso del día de un contador: lo que subió desde la lectura anterior. Si bajó, volvió a cero y vale la lectura
    /// entera. Sin lectura anterior es la base y vale cero, así un contador viejo no se cobra entero.
    /// </summary>
    public static decimal CounterUse(decimal? previous, decimal reading) =>
        previous is not { } before ? 0m : reading >= before ? reading - before : reading;

    /// <summary>El resumen de la ventana. Sin muestras, cero.</summary>
    public static decimal Measure(UsageAggregation aggregation, IReadOnlyCollection<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count == 0 ? 0m : aggregation switch
        {
            UsageAggregation.Max => values.Max(),
            UsageAggregation.Average => Math.Round(values.Average(), 6, MidpointRounding.AwayFromZero),
            _ => values.Sum(),
        };
    }

    /// <summary>Lo medido menos lo incluido, si es positivo, redondeado hacia arriba a un múltiplo del paso.</summary>
    public static decimal Excess(decimal measured, decimal included, decimal step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        var over = measured - included;
        return over <= 0m ? 0m : Math.Ceiling(over / step) * step;
    }

    /// <summary>El mayor umbral alcanzado, en porcentaje de lo incluido. Sin incluido no hay umbrales.</summary>
    public static int? Threshold(decimal used, decimal included, IEnumerable<int> percents)
    {
        ArgumentNullException.ThrowIfNull(percents);
        return included <= 0m ? null : percents.Where(p => used * 100m >= included * p).Select(p => (int?)p).Max();
    }
}
