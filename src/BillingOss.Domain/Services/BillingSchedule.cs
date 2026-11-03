using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Services;

/// <summary>Período facturado, cerrado al inicio y abierto al final: [Start, End).</summary>
public readonly record struct Period
{
    public Period(DateOnly start, DateOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException($"Período vacío: {start} a {end}.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    public int Days => End.DayNumber - Start.DayNumber;

    public bool Contains(DateOnly date) => date >= Start && date < End;
}

/// <summary>Períodos con día de anclaje y prorrateo (sección 5 del modelo de dominio).</summary>
public static class BillingSchedule
{
    /// <summary>
    /// El período que empieza en <paramref name="start"/>. Termina en el día de anclaje del mes que corresponde,
    /// o en el último día si ese mes es más corto. El anclaje se guarda aparte, así un 31 vuelve al 31 sin deriva.
    /// </summary>
    public static Period PeriodStartingAt(DateOnly start, BillingCycle cycle, int anchorDay)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (!cycle.IsRecurring)
        {
            throw new ArgumentException($"El ciclo {cycle.Unit} no tiene períodos.", nameof(cycle));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(anchorDay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(anchorDay, 31);

        var month = new DateOnly(start.Year, start.Month, 1).AddMonths(cycle.Months);
        var day = Math.Min(anchorDay, DateTime.DaysInMonth(month.Year, month.Month));
        return new Period(start, new DateOnly(month.Year, month.Month, day));
    }

    /// <summary>El período que termina en <paramref name="end"/>, con el mismo anclaje que <see cref="PeriodStartingAt"/>.</summary>
    public static Period PeriodEndingAt(DateOnly end, BillingCycle cycle, int anchorDay)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (!cycle.IsRecurring)
        {
            throw new ArgumentException($"El ciclo {cycle.Unit} no tiene períodos.", nameof(cycle));
        }

        var month = new DateOnly(end.Year, end.Month, 1).AddMonths(-cycle.Months);
        var start = new DateOnly(month.Year, month.Month, Math.Min(anchorDay, DateTime.DaysInMonth(month.Year, month.Month)));
        return new Period(start < end ? start : end.AddMonths(-cycle.Months), end);
    }

    /// <summary>
    /// Lo que vale un precio por período entre <paramref name="from"/> y <paramref name="until"/>, recorriendo los
    /// períodos que terminan en <paramref name="until"/>: los enteros cuentan completos y el que contiene la fecha, en
    /// proporción a los días que le quedan. Es el prorrateo de un cambio de plan (diseno/catalogo.md).
    /// </summary>
    public static decimal ProrateUntil(decimal pricePerPeriod, DateOnly from, DateOnly until, BillingCycle cycle, int anchorDay) =>
        Rounding.Commercial(ProratePeriods(pricePerPeriod, from, until, cycle, anchorDay).Sum(p => p.Amount));

    /// <summary>
    /// El mismo prorrateo período por período, del más nuevo al más viejo y sin redondear: cada período puede estar en
    /// otra factura, y la nota de crédito de una baja de plan va contra cada una por su parte.
    /// </summary>
    public static IReadOnlyList<(Period Period, decimal Amount)> ProratePeriods(decimal pricePerPeriod, DateOnly from, DateOnly until, BillingCycle cycle, int anchorDay) =>
        ProratePeriods(_ => pricePerPeriod, from, until, cycle, anchorDay);

    /// <summary>El mismo prorrateo con un precio propio de cada período, como el de uno que tuvo un descuento que ya se terminó.</summary>
    public static IReadOnlyList<(Period Period, decimal Amount)> ProratePeriods(Func<Period, decimal> pricePerPeriod, DateOnly from, DateOnly until, BillingCycle cycle, int anchorDay)
    {
        ArgumentNullException.ThrowIfNull(pricePerPeriod);
        var parts = new List<(Period, decimal)>();
        var end = until;
        while (end > from)
        {
            var period = PeriodEndingAt(end, cycle, anchorDay);
            var price = pricePerPeriod(period);
            parts.Add((period, period.Start >= from ? price : price * (end.DayNumber - from.DayNumber) / period.Days));
            end = period.Start;
        }

        return parts;
    }

    /// <summary>Parte del período que queda desde <paramref name="from"/> hasta el final.</summary>
    public static decimal RemainingFraction(Period period, DateOnly from)
    {
        if (!period.Contains(from))
        {
            throw new ArgumentOutOfRangeException(nameof(from), from, "La fecha está fuera del período.");
        }

        return (decimal)(period.End.DayNumber - from.DayNumber) / period.Days;
    }

    /// <summary>Importe proporcional a lo que queda del período, con redondeo comercial.</summary>
    public static decimal Prorate(decimal amount, Period period, DateOnly from) =>
        Rounding.Commercial(amount * RemainingFraction(period, from));
}
