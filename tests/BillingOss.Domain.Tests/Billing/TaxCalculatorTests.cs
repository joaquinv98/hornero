using BillingOss.Domain.Billing;

namespace BillingOss.Domain.Tests.Billing;

[TestClass]
public sealed class TaxCalculatorTests
{
    private static TaxableLine Taxed(string key, decimal amount, decimal rate = 0.21m) =>
        new(key, amount, TaxTreatment.Taxed, rate);

    [TestMethod]
    public void IvaIncluido_UnaLineaDe5()
    {
        var r = TaxCalculator.Compute(PriceMode.TaxIncluded, [Taxed("a", 5.00m)]);

        Assert.AreEqual(4.13m, r.NetTaxed);
        Assert.AreEqual(0.87m, r.Tax);
        Assert.AreEqual(5.00m, r.Total);
    }

    [TestMethod]
    public void IvaIncluido_DoceLineasDe118_DaLoQueArcaYaAcepta()
    {
        var lines = Enumerable.Range(1, 12).Select(i => Taxed($"l{i}", 1.18m)).ToList();

        var r = TaxCalculator.Compute(PriceMode.TaxIncluded, lines);

        Assert.AreEqual(11.70m, r.NetTaxed);
        Assert.AreEqual(2.46m, r.Tax);
        Assert.AreEqual(14.16m, r.Total);
        Assert.AreEqual(r.NetTaxed, r.Lines.Sum(l => l.Net));
        Assert.AreEqual(r.Tax, r.Lines.Sum(l => l.Tax));
        Assert.IsTrue(r.Lines.All(l => l.Gross == 1.18m));
    }

    [TestMethod]
    public void IvaAparte_DosAlicuotas()
    {
        var r = TaxCalculator.Compute(PriceMode.TaxExcluded,
        [
            Taxed("vps", 100.00m),
            Taxed("vps2", 100.00m),
            Taxed("libro", 50.00m, 0.105m),
        ]);

        Assert.HasCount(2, r.Aliquots);
        Assert.AreEqual(new AliquotTotal(0.105m, 50.00m, 5.25m), r.Aliquots[0]);
        Assert.AreEqual(new AliquotTotal(0.21m, 200.00m, 42.00m), r.Aliquots[1]);
        Assert.AreEqual(297.25m, r.Total);
    }

    [TestMethod]
    public void ExentoYNoGravado_NoSumanIva()
    {
        var r = TaxCalculator.Compute(PriceMode.TaxIncluded,
        [
            Taxed("vps", 12.10m),
            new TaxableLine("export", 40.00m, TaxTreatment.Exempt),
            new TaxableLine("reintegro", 3.00m, TaxTreatment.NotTaxed),
        ]);

        Assert.AreEqual(10.00m, r.NetTaxed);
        Assert.AreEqual(2.10m, r.Tax);
        Assert.AreEqual(40.00m, r.Exempt);
        Assert.AreEqual(3.00m, r.NotTaxed);
        Assert.AreEqual(55.10m, r.Total);
    }

    [TestMethod]
    public void MedioCentavoExacto_VaHaciaArriba_YLaDiferenciaConArcaEntraEnLaTolerancia()
    {
        // 0,05 sin IVA: el IVA exacto es 0,0105, redondeado comercial 0,01. Con 12,50 el IVA exacto es 2,625: la mitad
        // hacia arriba da 2,63 y Round Half Even, que ARCA declara, 2,62. Un centavo, dentro de la tolerancia.
        var r = TaxCalculator.Compute(PriceMode.TaxExcluded, [Taxed("a", 12.50m)]);

        Assert.AreEqual(2.63m, r.Tax);
        Assert.AreEqual(2.62m, Math.Round(12.50m * 0.21m, 2, MidpointRounding.ToEven));
        Assert.IsTrue(TaxCalculator.WithinArcaTolerance(r.Aliquots[0]));
        Assert.IsTrue(TaxCalculator.WithinArcaTolerance(new AliquotTotal(0.21m, 12.50m, 2.62m)));
    }

    [TestMethod]
    public void RechazaMasDeDosDecimales() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            TaxCalculator.Compute(PriceMode.TaxIncluded, [Taxed("a", 1.005m)]));

    [TestMethod]
    public void RechazaClavesRepetidas() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            TaxCalculator.Compute(PriceMode.TaxIncluded, [Taxed("a", 1m), Taxed("a", 2m)]));

    [TestMethod]
    public void RechazaAlicuotaEnLineaExenta() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            TaxCalculator.Compute(PriceMode.TaxIncluded, [new TaxableLine("a", 1m, TaxTreatment.Exempt, 0.21m)]));

    [TestMethod]
    [DataRow(PriceMode.TaxIncluded)]
    [DataRow(PriceMode.TaxExcluded)]
    public void Simulacion_CumpleLaToleranciaDeArcaYLasLineasSumanExacto(PriceMode mode)
    {
        // La simulación de la revisión fiscal: precios de 1 a 30, de 1 a 15 líneas, dos alícuotas.
        var random = new Random(20261026);
        decimal[] rates = [0.21m, 0.105m];

        for (var invoice = 0; invoice < 5000; invoice++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 16))
                .Select(i => Taxed($"l{i}", random.Next(100, 3001) / 100m, rates[random.Next(rates.Length)]))
                .ToList();

            var r = TaxCalculator.Compute(mode, lines);

            Assert.IsTrue(r.Aliquots.All(TaxCalculator.WithinArcaTolerance), $"Factura {invoice} fuera de tolerancia.");
            Assert.AreEqual(r.NetTaxed, r.Lines.Sum(l => l.Net));
            Assert.AreEqual(r.Tax, r.Lines.Sum(l => l.Tax));
            var expectedTotal = mode == PriceMode.TaxIncluded ? lines.Sum(l => l.Amount) : lines.Sum(l => l.Amount) + r.Tax;
            Assert.AreEqual(expectedTotal, r.Total);
        }
    }
}
