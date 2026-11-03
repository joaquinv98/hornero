using System.Globalization;

namespace BillingOss.Domain.Catalog;

public enum CycleUnit
{
    Month,
    Year,
    OneTime,
    Free,
}

/// <summary>Ciclo de cobro de un plan: unidad y cantidad (sección 3 del modelo de dominio).</summary>
public sealed record BillingCycle
{
    public BillingCycle(CycleUnit unit, int count = 1)
    {
        if (count < 1 || (unit is CycleUnit.OneTime or CycleUnit.Free && count != 1))
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"Cantidad inválida para {unit}.");
        }

        Unit = unit;
        Count = count;
    }

    public CycleUnit Unit { get; }

    public int Count { get; }

    public bool IsRecurring => Unit is CycleUnit.Month or CycleUnit.Year;

    public int Months => Unit switch
    {
        CycleUnit.Month => Count,
        CycleUnit.Year => 12 * Count,
        _ => 0,
    };

    /// <summary>
    /// Código estable del ciclo, como duración ISO 8601 en los recurrentes ("P1M", "P3M", "P1Y"), y "onetime" o
    /// "free" en los demás. Es lo que guardan los cupones y lo que muestra la API.
    /// </summary>
    public string Code => Unit switch
    {
        CycleUnit.Month => string.Create(CultureInfo.InvariantCulture, $"P{Count}M"),
        CycleUnit.Year => string.Create(CultureInfo.InvariantCulture, $"P{Count}Y"),
        CycleUnit.OneTime => "onetime",
        _ => "free",
    };

    public static BillingCycle Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var upper = code.Trim().ToUpperInvariant();
        return upper switch
        {
            "ONETIME" => OneTime,
            "FREE" => Free,
            _ when upper.Length >= 3 && upper[0] == 'P' && upper[^1] is 'M' or 'Y'
                && int.TryParse(upper.AsSpan(1, upper.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 =>
                new BillingCycle(upper[^1] == 'M' ? CycleUnit.Month : CycleUnit.Year, count),
            _ => throw new ArgumentException($"Ciclo desconocido: {code}."),
        };
    }

    public static BillingCycle Monthly { get; } = new(CycleUnit.Month);

    public static BillingCycle Annually { get; } = new(CycleUnit.Year);

    public static BillingCycle OneTime { get; } = new(CycleUnit.OneTime);

    public static BillingCycle Free { get; } = new(CycleUnit.Free);
}
