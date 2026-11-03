using BillingOss.Domain.Accounts;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Billing;

/// <summary>Motivos estables de cada renglón y de cada tributo. La factura, el simulador y la API los muestran en castellano llano.</summary>
public static class TaxReasons
{
    public const string Taxed = "gravado";
    public const string Export = "exportacion";
    public const string ExemptByLaw = "exento_por_ley";
    public const string Untaxed = "no_gravado";
    public const string ReverseCharge = "inversion_sujeto_pasivo";
    public const string NoRegistration = "sin_registro";
    public const string AccountRule = "regla_de_cuenta";
    public const string Monotax = "monotributo";
    public const string Perception = "percepcion";
    public const string Informational = "solo_se_informa";
}

/// <summary>Resultado del IVA de un renglón: tratamiento, alícuota, motivo y la leyenda que corresponda.</summary>
public sealed record LineTax(TaxTreatment Treatment, decimal Rate, string Reason, string? Legend = null);

/// <summary>
/// Un tributo de comprobante ya calculado y congelado en el documento. <paramref name="Categories"/> lleva las
/// categorías de los renglones que lo componen, separadas por coma, y <paramref name="BaseKind"/> sobre qué se
/// calculó: con eso una nota de crédito parcial reparte el tributo por renglón y categoría. Un documento congelado
/// antes de que existieran los dos campos se reparte por documento, como hasta entonces.
/// </summary>
public sealed record DocumentTaxAmount(string Code, string Name, int? FiscalCode, decimal Base, decimal Rate, decimal Amount, bool AddsToTotal, string Reason, string? Legend = null, string? Categories = null, TaxBase BaseKind = TaxBase.NetPlusLineTaxes)
{
    /// <summary>Si un renglón de esa categoría entra en este tributo.</summary>
    public bool Covers(string category) => Categories is null || Categories.Split(',').Contains(category, StringComparer.Ordinal);
}

/// <summary>Un renglón con su categoría y el tratamiento de su IVA, para los tributos de comprobante que se calculan por alícuota.</summary>
public sealed record TaxedLine(string Category, LineSplit Split, TaxTreatment Treatment = TaxTreatment.Taxed)
{
    /// <summary>
    /// La base del renglón para un tributo. Uno que se calcula sobre neto más IVA alcanza solo las operaciones
    /// gravadas (RG 2126, arts. 1 y 5): un renglón exento o no gravado no entra en él.
    /// </summary>
    public decimal? BaseFor(TaxBase kind) => kind switch
    {
        TaxBase.Net => Split.Net,
        TaxBase.NetPlusLineTaxes => Treatment == TaxTreatment.Taxed ? Split.Net + Split.Tax : null,
        _ => Split.Gross,
    };
}

/// <summary>
/// Resuelve el impuesto de cada renglón y los tributos del comprobante (sección 3.2 del diseño fiscal). El cálculo del
/// IVA por comprobante y alícuota sigue en <see cref="TaxCalculator"/>: acá se decide qué alícuota y por qué.
/// </summary>
public static class TaxEngine
{
    /// <summary>
    /// El IVA de un renglón. Una categoría exenta o no gravada no pasa por las reglas. Un emisor monotributista o exento
    /// no cobra IVA, con el motivo monotributo. Lo demás sale de la regla más específica, y sin ninguna el cálculo falla.
    /// </summary>
    public static LineTax ResolveLine(TaxCategory category, TaxReceiver receiver, IEnumerable<TaxRule> rules, DateOnly on, int issuerVatCondition = VatConditions.ResponsableInscripto)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(receiver);
        if (category.Treatment != TaxTreatment.Taxed)
        {
            return new LineTax(category.Treatment, 0m, category.Reason ?? (category.Treatment == TaxTreatment.Exempt ? TaxReasons.ExemptByLaw : TaxReasons.Untaxed));
        }

        if (VatConditions.IssuesClassC(issuerVatCondition))
        {
            return new LineTax(TaxTreatment.NotTaxed, 0m, TaxReasons.Monotax);
        }

        var rule = TaxRule.Resolve(rules, receiver, category.Code, on);
        var reason = rule.Reason ?? (rule.IsAccountRule ? TaxReasons.AccountRule : rule.Outcome switch
        {
            TaxTreatment.Taxed => TaxReasons.Taxed,
            TaxTreatment.Exempt => rule.Country is null ? TaxReasons.Export : TaxReasons.ExemptByLaw,
            _ => TaxReasons.Untaxed,
        });
        return rule.Outcome == TaxTreatment.Taxed
            ? new LineTax(TaxTreatment.Taxed, rule.Rate, reason, rule.Legend)
            : new LineTax(rule.Outcome, 0m, reason, rule.Legend);
    }

    /// <summary>
    /// Los tributos de comprobante que corresponden, calculados sobre los renglones ya repartidos. Cada tributo se
    /// resuelve por renglón con su categoría y sale un importe por alícuota, como pide la RG 2126 (13,5 % para las
    /// telecomunicaciones y 10,5 % para el resto). Sin regla, o con una regla de cuenta que lo excluye, no corresponde.
    /// Un renglón exento o no gravado no entra en un tributo que se calcula sobre neto más IVA.
    /// </summary>
    public static IReadOnlyList<DocumentTaxAmount> ComputeDocumentTaxes(IEnumerable<Tax> taxes, IEnumerable<TaxRule> rules, TaxReceiver receiver, IReadOnlyList<TaxedLine> lines, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(taxes);
        ArgumentNullException.ThrowIfNull(lines);
        var ruleList = rules as IReadOnlyList<TaxRule> ?? [.. rules];
        var result = new List<DocumentTaxAmount>();
        foreach (var tax in taxes.Where(t => t.Level == TaxLevel.Document && t.IsValidOn(on)).OrderBy(t => t.Code, StringComparer.Ordinal))
        {
            var byRate = new Dictionary<(decimal Rate, string? Legend, string Reason), (decimal Base, SortedSet<string> Categories)>();
            foreach (var line in lines)
            {
                if (line.BaseFor(tax.Base) is not { } baseAmount)
                {
                    continue;
                }

                var rule = TaxRule.TryResolve(ruleList, receiver, line.Category, on, tax.Code);
                if (rule is null || rule.Outcome != TaxTreatment.Taxed)
                {
                    continue;
                }

                var reason = rule.Reason ?? (rule.IsAccountRule ? TaxReasons.AccountRule : tax.AddsToTotal ? TaxReasons.Perception : TaxReasons.Informational);
                var key = (rule.Rate, rule.Legend, reason);
                var (sum, categories) = byRate.GetValueOrDefault(key, (0m, new SortedSet<string>(StringComparer.Ordinal)));
                categories.Add(line.Category);
                byRate[key] = (sum + baseAmount, categories);
            }

            foreach (var ((rate, legend, reason), (baseAmount, categories)) in byRate.OrderBy(k => k.Key.Rate))
            {
                var amount = tax.AddsToTotal ? Rounding.Commercial(baseAmount * rate) : 0m;
                result.Add(new DocumentTaxAmount(tax.Code, tax.Name, tax.FiscalCode, baseAmount, rate, amount, tax.AddsToTotal, reason, legend, string.Join(',', categories), tax.Base));
            }
        }

        return result;
    }
}

/// <summary>Topes de los tributos de una nota de crédito contra lo que falta revertir del original, tributo por tributo.</summary>
public static class DocumentTaxRules
{
    /// <summary>
    /// Los tributos de la nota: cada uno se recalcula sobre la base de la nota (los renglones de sus categorías, con la
    /// misma base que el original) con la alícuota del original, y se topea en lo que falta revertir. Si la nota
    /// completa el original, toma exactamente el resto, así cierra sin residuos.
    /// </summary>
    public static IReadOnlyList<DocumentTaxAmount> ForNote(IReadOnlyList<DocumentTaxAmount> original, IReadOnlyList<IReadOnlyList<DocumentTaxAmount>> alreadyCredited, IReadOnlyList<TaxedLine> noteLines, bool completesOriginal)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(alreadyCredited);
        ArgumentNullException.ThrowIfNull(noteLines);
        var result = new List<DocumentTaxAmount>();
        foreach (var tax in original)
        {
            var credited = alreadyCredited.SelectMany(c => c).Where(c => c.Code == tax.Code && c.Rate == tax.Rate).ToList();
            var remaining = tax.Amount - credited.Sum(c => c.Amount);
            if (remaining <= 0m)
            {
                continue;
            }

            decimal amount;
            decimal baseAmount;
            var remainingBase = tax.Base - credited.Sum(c => c.Base);
            if (completesOriginal)
            {
                amount = remaining;
                baseAmount = remainingBase;
            }
            else
            {
                baseAmount = Math.Min(noteLines.Where(l => tax.Covers(l.Category)).Sum(l => l.BaseFor(tax.BaseKind) ?? 0m), remainingBase);
                if (baseAmount <= 0m)
                {
                    continue;
                }

                amount = Math.Min(Rounding.Commercial(baseAmount * tax.Rate), remaining);
            }

            result.Add(tax with { Base = baseAmount, Amount = amount });
        }

        return result;
    }
}
