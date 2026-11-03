using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Billing;

[TestClass]
public sealed class DocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Due = new(2026, 11, 9);

    private static Document Issued(DocumentKind kind, long number, TaxBreakdown? capped = null, params decimal[] grossLines)
    {
        var doc = new Document(kind, 1, Currency.Ars, PriceMode.TaxIncluded, Due, Now, kind == DocumentKind.Invoice ? null : 1);
        foreach (var gross in grossLines)
        {
            doc.AddLine(LineKind.Manual, "Servicio", 1m, gross, TaxTreatment.Taxed, 0.21m);
        }

        doc.Issue(Due, "{}", Now, doc.PrepareIssue(capped));
        doc.AssignNumber(number);
        return doc;
    }

    [TestMethod]
    public void LaNotaTopeadaAjustaElNetoYElIvaDeSuLinea()
    {
        // La segunda nota de 10,00 cierra la factura de 10,00 más 10,00 con IVA incluido: sus importes son los que faltan
        // (8,27 y 1,73), no los de su propio cálculo (8,26 y 1,74), y la línea tiene que mostrar lo mismo.
        var invoice = Issued(DocumentKind.Invoice, 1, null, 10m, 10m);
        var request = new Document(DocumentKind.CreditNote, 1, Currency.Ars, PriceMode.TaxIncluded, Due, Now, 1);
        request.AddLine(LineKind.Manual, "Servicio", 1m, 10m, TaxTreatment.Taxed, 0.21m);

        var first = Issued(DocumentKind.CreditNote, 1, CreditNoteRules.Apply(invoice.IssuedBreakdown(), [], request.Compute()), 10m);
        var second = Issued(DocumentKind.CreditNote, 2, CreditNoteRules.Apply(invoice.IssuedBreakdown(), [first.IssuedBreakdown()], request.Compute()), 10m);

        Assert.AreEqual(8.27m, second.NetTaxed);
        Assert.AreEqual(1.73m, second.Tax);
        Assert.AreEqual(8.27m, second.Lines[0].Net);
        Assert.AreEqual(1.73m, second.Lines[0].LineTax);
        Assert.AreEqual(invoice.NetTaxed, first.NetTaxed + second.NetTaxed);
    }
}
