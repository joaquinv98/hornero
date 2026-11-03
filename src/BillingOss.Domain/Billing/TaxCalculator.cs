using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Billing;

public enum PriceMode
{
    /// <summary>El importe de cada línea es el bruto (consumidor final).</summary>
    TaxIncluded,

    /// <summary>El importe de cada línea es el neto (Factura A).</summary>
    TaxExcluded,
}

public enum TaxTreatment
{
    Taxed,
    Exempt,
    NotTaxed,
}

/// <summary>Una línea del cálculo. <paramref name="Amount"/> tiene dos decimales y es bruto o neto según el modo.</summary>
public sealed record TaxableLine(string Key, decimal Amount, TaxTreatment Treatment, decimal Rate = 0m);

public sealed record AliquotTotal(decimal Rate, decimal Net, decimal Tax);

public sealed record LineSplit(string Key, decimal Net, decimal Tax)
{
    public decimal Gross => Net + Tax;
}

public sealed record TaxBreakdown(
    IReadOnlyList<AliquotTotal> Aliquots,
    IReadOnlyList<LineSplit> Lines,
    decimal Exempt,
    decimal NotTaxed)
{
    public decimal NetTaxed => Aliquots.Sum(a => a.Net);

    public decimal Tax => Aliquots.Sum(a => a.Tax);

    public decimal Total => NetTaxed + Tax + Exempt + NotTaxed;
}

/// <summary>
/// IVA por factura y por alícuota (sección 6.2 del modelo de dominio). Los importes que valen salen de la suma
/// de las líneas de cada alícuota. Las líneas solo muestran su parte, y el residuo de redondeo va a la mayor.
/// </summary>
public static class TaxCalculator
{
    /// <summary>Tolerancia por alícuota del manual de WSFEv1, a confirmar en homologación.</summary>
    public const decimal ArcaTolerance = 0.01m;

    public static TaxBreakdown Compute(PriceMode mode, IReadOnlyList<TaxableLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Validate(lines);

        var splits = lines.ToDictionary(l => l.Key, l => new LineSplit(l.Key, l.Amount, 0m));
        var aliquots = new List<AliquotTotal>();

        foreach (var group in lines.Where(l => l.Treatment == TaxTreatment.Taxed).GroupBy(l => l.Rate).OrderBy(g => g.Key))
        {
            var rate = group.Key;
            var sum = group.Sum(l => l.Amount);
            if (sum < 0m)
            {
                throw new ArgumentException($"La alícuota {rate} suma un importe negativo.", nameof(lines));
            }

            AliquotTotal total;
            if (mode == PriceMode.TaxIncluded)
            {
                var net = Rounding.Commercial(sum / (1m + rate));
                total = new AliquotTotal(rate, net, sum - net);
                foreach (var l in group)
                {
                    var lineNet = Rounding.Commercial(l.Amount / (1m + rate));
                    splits[l.Key] = new LineSplit(l.Key, lineNet, l.Amount - lineNet);
                }

                var residue = total.Net - group.Sum(l => splits[l.Key].Net);
                var largest = Largest(group);
                var s = splits[largest.Key];
                splits[largest.Key] = s with { Net = s.Net + residue, Tax = s.Tax - residue };
            }
            else
            {
                total = new AliquotTotal(rate, sum, Rounding.Commercial(sum * rate));
                foreach (var l in group)
                {
                    splits[l.Key] = new LineSplit(l.Key, l.Amount, Rounding.Commercial(l.Amount * rate));
                }

                var residue = total.Tax - group.Sum(l => splits[l.Key].Tax);
                var largest = Largest(group);
                var s = splits[largest.Key];
                splits[largest.Key] = s with { Tax = s.Tax + residue };
            }

            aliquots.Add(total);
        }

        return new TaxBreakdown(
            aliquots,
            [.. lines.Select(l => splits[l.Key])],
            lines.Where(l => l.Treatment == TaxTreatment.Exempt).Sum(l => l.Amount),
            lines.Where(l => l.Treatment == TaxTreatment.NotTaxed).Sum(l => l.Amount));
    }

    public static bool WithinArcaTolerance(AliquotTotal aliquot)
    {
        ArgumentNullException.ThrowIfNull(aliquot);
        return Math.Abs(aliquot.Tax - (aliquot.Net * aliquot.Rate)) <= ArcaTolerance;
    }

    private static TaxableLine Largest(IEnumerable<TaxableLine> group) =>
        group.OrderByDescending(l => Math.Abs(l.Amount)).First();

    private static void Validate(IReadOnlyList<TaxableLine> lines)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines)
        {
            if (!keys.Add(l.Key))
            {
                throw new ArgumentException($"Clave de línea repetida: '{l.Key}'.", nameof(lines));
            }

            if (!Rounding.HasAtMostDecimals(l.Amount))
            {
                throw new ArgumentException($"La línea '{l.Key}' tiene más de dos decimales.", nameof(lines));
            }

            if (l.Treatment == TaxTreatment.Taxed ? l.Rate < 0m : l.Rate != 0m)
            {
                throw new ArgumentException($"La línea '{l.Key}' tiene una alícuota inválida.", nameof(lines));
            }
        }
    }
}
