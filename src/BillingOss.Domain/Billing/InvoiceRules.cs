namespace BillingOss.Domain.Billing;

public enum InvoiceStatus
{
    Draft,
    Issued,
    PartiallyPaid,
    Paid,
    Cancelled,
}

/// <summary>Estado y saldo de una factura, derivados de sus importes (sección 6.1 del modelo de dominio).</summary>
public static class InvoiceRules
{
    public static decimal Pending(decimal total, decimal credited, decimal allocated) => total - credited - allocated;

    public static InvoiceStatus Status(bool issued, decimal total, decimal credited, decimal allocated)
    {
        if (!issued)
        {
            return InvoiceStatus.Draft;
        }

        // Sin nada que cobrar está paga. Solo llegan así los importados: el núcleo no emite documentos de total cero.
        if (total == 0m)
        {
            return InvoiceStatus.Paid;
        }

        if (credited >= total)
        {
            return InvoiceStatus.Cancelled;
        }

        if (allocated <= 0m)
        {
            return InvoiceStatus.Issued;
        }

        return Pending(total, credited, allocated) <= 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
    }
}

/// <summary>Topes de una nota de crédito contra lo que falta acreditar del original, alícuota por alícuota.</summary>
public static class CreditNoteRules
{
    /// <summary>
    /// Devuelve los importes finales de la nota. Por alícuota, si la nota completa el neto o el bruto que falta toma
    /// exactamente el neto y el IVA restantes, así la última cierra sin residuos de redondeo (con precios IVA
    /// incluido el bruto cierra y el neto difiere en un centavo). Si el bruto es menor, el IVA se topea en lo que
    /// falta y la diferencia pasa al neto, siempre dentro de la tolerancia del fisco.
    /// </summary>
    public static TaxBreakdown Apply(TaxBreakdown original, IReadOnlyList<TaxBreakdown> alreadyCredited, TaxBreakdown requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var left = Remaining(original, alreadyCredited);
        var aliquots = new List<AliquotTotal>();
        foreach (var r in requested.Aliquots)
        {
            var o = left.Aliquots.SingleOrDefault(a => a.Rate == r.Rate)
                ?? throw new InvalidOperationException($"El original no tiene la alícuota {r.Rate}.");
            var gross = r.Net + r.Tax;

            if (r.Net == o.Net || gross == o.Net + o.Tax)
            {
                aliquots.Add(r with { Net = o.Net, Tax = o.Tax });
            }
            else if (gross < o.Net + o.Tax)
            {
                var tax = Math.Min(r.Tax, o.Tax);
                var net = gross - tax;
                if (net > o.Net)
                {
                    (net, tax) = (o.Net, gross - o.Net);
                }

                var capped = r with { Net = net, Tax = tax };
                if (!TaxCalculator.WithinArcaTolerance(capped))
                {
                    throw new InvalidOperationException($"La nota no cierra con la alícuota {r.Rate}: el IVA que queda no corresponde al neto.");
                }

                aliquots.Add(capped);
            }
            else
            {
                throw new InvalidOperationException($"La nota supera lo que falta acreditar en la alícuota {r.Rate}.");
            }
        }

        var exempt = Capped(requested.Exempt, left.Exempt, "exento");
        var notTaxed = Capped(requested.NotTaxed, left.NotTaxed, "no gravado");
        return new TaxBreakdown(aliquots, requested.Lines, exempt, notTaxed);
    }

    /// <summary>
    /// Lo que falta acreditar del original: una entrada por cada alícuota del original, más exento y no gravado. Es
    /// también la nota que lo anula por completo.
    /// </summary>
    public static TaxBreakdown Remaining(TaxBreakdown original, IReadOnlyList<TaxBreakdown> alreadyCredited)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(alreadyCredited);

        var credited = alreadyCredited.SelectMany(c => c.Aliquots).ToList();
        return new TaxBreakdown(
            [.. original.Aliquots.Select(o => new AliquotTotal(
                o.Rate,
                o.Net - credited.Where(a => a.Rate == o.Rate).Sum(a => a.Net),
                o.Tax - credited.Where(a => a.Rate == o.Rate).Sum(a => a.Tax)))],
            [],
            original.Exempt - alreadyCredited.Sum(c => c.Exempt),
            original.NotTaxed - alreadyCredited.Sum(c => c.NotTaxed));
    }

    private static decimal Capped(decimal requested, decimal remaining, string what) =>
        requested <= remaining
            ? requested
            : throw new InvalidOperationException($"La nota supera lo que falta acreditar de {what}.");
}
