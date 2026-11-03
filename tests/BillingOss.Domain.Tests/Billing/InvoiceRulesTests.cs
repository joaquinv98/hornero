using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Billing;

[TestClass]
public sealed class InvoiceRulesTests
{
    [TestMethod]
    [DataRow(false, 100, 0, 0, InvoiceStatus.Draft)]
    [DataRow(true, 100, 0, 0, InvoiceStatus.Issued)]
    [DataRow(true, 100, 0, 40, InvoiceStatus.PartiallyPaid)]
    [DataRow(true, 100, 0, 100, InvoiceStatus.Paid)]
    [DataRow(true, 100, 30, 70, InvoiceStatus.Paid)]
    [DataRow(true, 100, 30, 0, InvoiceStatus.Issued)]
    [DataRow(true, 100, 100, 0, InvoiceStatus.Cancelled)]
    [DataRow(true, 100, 100, 100, InvoiceStatus.Cancelled)]
    [DataRow(true, 0, 0, 0, InvoiceStatus.Paid)]
    public void EstadoDerivado(bool issued, int total, int credited, int allocated, InvoiceStatus expected) =>
        Assert.AreEqual(expected, InvoiceRules.Status(issued, total, credited, allocated));

    [TestMethod]
    public void ContracargoVuelveADeber() =>
        Assert.AreEqual(InvoiceStatus.Issued, InvoiceRules.Status(true, 100m, 0m, allocated: 0m));
}

[TestClass]
public sealed class CreditNoteRulesTests
{
    private static TaxBreakdown Doc(decimal net, decimal tax, decimal exempt = 0m) =>
        new([new AliquotTotal(0.21m, net, tax)], [], exempt, 0m);

    [TestMethod]
    public void NotaParcialYDespuesLaQueCierraTomaElRestoExacto()
    {
        var original = Doc(11.70m, 2.46m);
        var primera = CreditNoteRules.Apply(original, [], Doc(5.85m, 1.23m));
        var cierre = CreditNoteRules.Apply(original, [primera], Doc(5.85m, 1.24m));

        Assert.AreEqual(1.23m, cierre.Tax);
        Assert.AreEqual(original.Tax, primera.Tax + cierre.Tax);
    }

    [TestMethod]
    public void ConPreciosIvaIncluido_LaNotaQueCierraSeAceptaPorBruto()
    {
        // Factura de dos líneas de 10,00 al 21 %: neto 16,53 e IVA 3,47. Cada nota de 10,00 calcula 8,26 y 1,74, y la
        // segunda no coincide con lo que falta (8,27 y 1,73) aunque el bruto sea exacto: toma el resto tal cual.
        var invoice = TaxCalculator.Compute(PriceMode.TaxIncluded, [new("0", 10m, TaxTreatment.Taxed, 0.21m), new("1", 10m, TaxTreatment.Taxed, 0.21m)]);
        var note = TaxCalculator.Compute(PriceMode.TaxIncluded, [new("0", 10m, TaxTreatment.Taxed, 0.21m)]);

        var first = CreditNoteRules.Apply(invoice, [], note);
        var second = CreditNoteRules.Apply(invoice, [first], note);

        Assert.AreEqual(8.26m, first.NetTaxed);
        Assert.AreEqual(8.27m, second.NetTaxed);
        Assert.AreEqual(1.73m, second.Tax);
        Assert.AreEqual(invoice.Total, first.Total + second.Total);
    }

    [TestMethod]
    public void UnaNotaMenorConMasIvaDelQueFalta_TopeaElIvaYPasaLaDiferenciaAlNeto()
    {
        // Falta 27,54 más 5,79 (bruto 33,33). Una nota de 27,55 más 5,77 (bruto 33,32) entra por bruto: el neto se
        // topea en 27,54 y el centavo va al IVA, que sigue dentro de la tolerancia del fisco.
        var capped = CreditNoteRules.Apply(Doc(27.54m, 5.79m), [], Doc(27.55m, 5.77m));

        Assert.AreEqual(27.54m, capped.NetTaxed);
        Assert.AreEqual(5.78m, capped.Tax);
        Assert.AreEqual(33.32m, capped.Total);
    }

    [TestMethod]
    public void NoSuperaLoQueFaltaPorBruto() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreditNoteRules.Apply(Doc(27.54m, 5.79m), [], Doc(27.55m, 5.79m)));

    [TestMethod]
    public void NoSuperaLoQueFalta() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreditNoteRules.Apply(Doc(10m, 2.10m), [Doc(6m, 1.26m)], Doc(5m, 1.05m)));

    [TestMethod]
    public void NoAcreditaUnaAlicuotaQueElOriginalNoTiene() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreditNoteRules.Apply(Doc(10m, 2.10m), [], new TaxBreakdown([new AliquotTotal(0.105m, 1m, 0.11m)], [], 0m, 0m)));

    [TestMethod]
    public void AnulacionTotal_TomaLoQueFaltaYLaLineaCierra()
    {
        // Original de 1,00 con IVA incluido y una nota previa de 0,50: faltan 0,42 de neto y 0,08 de IVA.
        var original = TaxCalculator.Compute(PriceMode.TaxIncluded, [new TaxableLine("0", 1.00m, TaxTreatment.Taxed, 0.21m)]);
        var previa = CreditNoteRules.Apply(original, [], TaxCalculator.Compute(PriceMode.TaxIncluded, [new TaxableLine("0", 0.50m, TaxTreatment.Taxed, 0.21m)]));
        var note = new Document(DocumentKind.CreditNote, 1, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 11, 2), DateTimeOffset.UnixEpoch);
        var line = note.AddLine(LineKind.Manual, "Anulación", 1, 0.50m, TaxTreatment.Taxed, 0.21m);

        note.PrepareIssue(CreditNoteRules.Remaining(original, [previa]));

        Assert.AreEqual(0.42m, line.Net);
        Assert.AreEqual(0.08m, line.LineTax);
    }

    [TestMethod]
    public void TopeDeExento() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreditNoteRules.Apply(Doc(0m, 0m, exempt: 40m), [], new TaxBreakdown([], [], 41m, 0m)));
}
