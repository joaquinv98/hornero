using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using BillingOss.Domain.Usage;

namespace BillingOss.Domain.Tests.Usage;

[TestClass]
public sealed class UsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Contador_LaPrimeraLecturaEsBase_DespuesLaDiferencia_YSiBajaVolvioACero()
    {
        Assert.AreEqual(0m, UsageMath.CounterUse(null, 5000m), "Un contador viejo no se cobra entero.");
        Assert.AreEqual(12.5m, UsageMath.CounterUse(100m, 112.5m));
        Assert.AreEqual(0m, UsageMath.CounterUse(100m, 100m));
        Assert.AreEqual(7m, UsageMath.CounterUse(100m, 7m), "La VM se reinició: lo que marca es lo usado desde entonces.");
    }

    [TestMethod]
    public void Resumen_MaximoPromedioYSuma()
    {
        decimal[] values = [10m, 30m, 20m];

        Assert.AreEqual(30m, UsageMath.Measure(UsageAggregation.Max, values));
        Assert.AreEqual(20m, UsageMath.Measure(UsageAggregation.Average, values));
        Assert.AreEqual(60m, UsageMath.Measure(UsageAggregation.Sum, values));
        Assert.AreEqual(3.333333m, UsageMath.Measure(UsageAggregation.Average, [3m, 3m, 4m]));
        Assert.AreEqual(0m, UsageMath.Measure(UsageAggregation.Max, []));
    }

    [TestMethod]
    [DataRow("120", "100", "1", "20")]
    [DataRow("120.2", "100", "1", "21")]
    [DataRow("120.2", "100", "0.5", "20.5")]
    [DataRow("120.2", "100", "10", "30")]
    [DataRow("100", "100", "1", "0")]
    [DataRow("80", "100", "1", "0")]
    [DataRow("0.0003", "0", "0.0001", "0.0003")]
    public void Excedente_LoQuePasaDeLoIncluido_RedondeadoHaciaArribaAlPaso(string measured, string included, string step, string expected) =>
        Assert.AreEqual(Dec(expected), UsageMath.Excess(Dec(measured), Dec(included), Dec(step)));

    [TestMethod]
    public void Umbral_ElMayorAlcanzado_YSinIncluidoNinguno()
    {
        int[] percents = [80, 100];

        Assert.IsNull(UsageMath.Threshold(79.9m, 100m, percents));
        Assert.AreEqual(80, UsageMath.Threshold(80m, 100m, percents));
        Assert.AreEqual(100, UsageMath.Threshold(150m, 100m, percents), "Si saltó los dos, avisa el mayor.");
        Assert.IsNull(UsageMath.Threshold(1000m, 0m, percents));
    }

    [TestMethod]
    public void Metrica_UnContadorSeSuma_UnMedidorNo_YLasCantidadesTienenTope()
    {
        var counter = new UsageMetric(1, "transfer_gb", UsageKind.Counter, Now);
        var gauge = new UsageMetric(1, "disk_gb", UsageKind.Gauge, Now);

        Assert.AreEqual(UsageAggregation.Sum, counter.Aggregation);
        Assert.AreEqual(UsageAggregation.Max, gauge.Aggregation);
        Assert.ThrowsExactly<ArgumentException>(() => counter.Configure("Transferencia", "GB", UsageAggregation.Max, 100m, 1m, UsageCycle.Renewal, [80], true, Now));
        Assert.ThrowsExactly<ArgumentException>(() => gauge.Configure("Disco", "GB", UsageAggregation.Sum, 10m, 1m, UsageCycle.Renewal, [80], true, Now));
        Assert.ThrowsExactly<ArgumentException>(() => gauge.Configure("Disco", "GB", UsageAggregation.Max, 10m, 0m, UsageCycle.Renewal, [80], true, Now));
        Assert.ThrowsExactly<ArgumentException>(() => gauge.Configure("Disco", "GB", UsageAggregation.Max, 10.12345m, 1m, UsageCycle.Renewal, [80], true, Now));
        Assert.ThrowsExactly<ArgumentException>(() => gauge.Configure("Disco", "GB", UsageAggregation.Max, -1m, 1m, UsageCycle.Renewal, [80], true, Now));
        Assert.ThrowsExactly<ArgumentException>(() => gauge.Configure(" ", "GB", UsageAggregation.Max, 10m, 1m, UsageCycle.Renewal, [80], true, Now));
        Assert.ThrowsExactly<ArgumentException>(() => gauge.Configure("Disco", "GB", UsageAggregation.Max, 10m, 1m, UsageCycle.Renewal, [0], true, Now));

        gauge.Configure(" Disco ", "GB", UsageAggregation.Average, 10m, 0.5m, UsageCycle.CalendarMonth, [100, 80, 80], true, Now);

        Assert.AreEqual("Disco", gauge.Name);
        CollectionAssert.AreEqual(new[] { 80, 100 }, gauge.AlertPercents);
    }

    [TestMethod]
    public void Metrica_AlVolverAEstarActivaGuardaDesdeCuando_YLosOtrosCambiosNoLoTocan()
    {
        var metric = new UsageMetric(1, "transfer_gb", UsageKind.Counter, Now);
        Assert.AreEqual(Now, metric.ActiveSince);

        metric.Configure("Transferencia", "GB", UsageAggregation.Sum, 100m, 1m, UsageCycle.Renewal, [80], true, Now.AddDays(1));
        metric.Configure("Transferencia", "GB", UsageAggregation.Sum, 100m, 1m, UsageCycle.Renewal, [80], false, Now.AddDays(2));
        Assert.AreEqual(Now, metric.ActiveSince);

        metric.Configure("Transferencia", "GB", UsageAggregation.Sum, 100m, 1m, UsageCycle.Renewal, [80], true, Now.AddDays(9));
        Assert.AreEqual(Now.AddDays(9), metric.ActiveSince);
    }

    [TestMethod]
    public void Incluido_LoQueSeSumaEsPorMesDeLaVentana_ConLaRenovacionLosDelPlan_YUnNivelValeIgual()
    {
        var transfer = new UsageMetric(1, "transfer_gb", UsageKind.Counter, Now);
        transfer.Configure("Transferencia", "GB", UsageAggregation.Sum, 1000m, 1m, UsageCycle.Renewal, [80], true, Now);
        var disk = new UsageMetric(1, "disk_gb", UsageKind.Gauge, Now);
        disk.Configure("Disco", "GB", UsageAggregation.Max, 10m, 1m, UsageCycle.Renewal, [80], true, Now);
        var nov24 = new DateOnly(2026, 11, 24);

        // Una renovación mensual corta un mes de 28 a 31 días. La primera, más corta, no se prorratea.
        Assert.AreEqual(1000m, transfer.IncludedFor(BillingCycle.Monthly, nov24, new DateOnly(2026, 12, 25)));
        Assert.AreEqual(1000m, transfer.IncludedFor(BillingCycle.Monthly, new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 1)));
        Assert.AreEqual(1000m, transfer.IncludedFor(BillingCycle.Monthly, new DateOnly(2026, 11, 20), nov24));
        Assert.AreEqual(1000m, transfer.IncludedFor(BillingCycle.Monthly, nov24, new DateOnly(2027, 1, 8)), "45 días son un mes.");

        // El trimestral y el anual incluyen sus meses aunque la ventana sea más corta.
        Assert.AreEqual(3000m, transfer.IncludedFor(new BillingCycle(CycleUnit.Month, 3), nov24, new DateOnly(2027, 2, 20)));
        Assert.AreEqual(12000m, transfer.IncludedFor(BillingCycle.Annually, new DateOnly(2027, 6, 1), new DateOnly(2027, 10, 26)));

        // Una ventana que quedó abierta dos meses, sin precio o esperando un pago, incluye dos.
        Assert.AreEqual(2000m, transfer.IncludedFor(BillingCycle.Monthly, nov24, new DateOnly(2027, 1, 9)), "46 días son dos meses.");
        Assert.AreEqual(2000m, transfer.IncludedFor(BillingCycle.Monthly, new DateOnly(2026, 11, 2), new DateOnly(2027, 1, 1)));
        Assert.AreEqual(14000m, transfer.IncludedFor(BillingCycle.Annually, new DateOnly(2026, 11, 2), new DateOnly(2028, 1, 1)));
        Assert.AreEqual(1000m, transfer.IncludedFor(BillingCycle.OneTime, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)), "Sin renovación lo corta el cierre del mes.");
        Assert.AreEqual(1000m, transfer.IncludedFor(BillingCycle.Monthly, new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 2)), "Una ventana al revés vale un mes.");
        Assert.AreEqual(10m, disk.IncludedFor(BillingCycle.Annually, new DateOnly(2026, 11, 2), new DateOnly(2027, 1, 1)));

        transfer.Configure("Transferencia", "GB", UsageAggregation.Sum, 1000m, 1m, UsageCycle.CalendarMonth, [80], true, Now);
        Assert.AreEqual(1000m, transfer.IncludedFor(BillingCycle.Annually, new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1)), "El cierre del mes corta un mes.");
        Assert.AreEqual(2000m, transfer.IncludedFor(BillingCycle.Annually, new DateOnly(2026, 11, 2), new DateOnly(2027, 1, 1)));
    }

    [TestMethod]
    public void Precio_ElVigenteEnLaMonedaAlEmpezarLaVentana()
    {
        UsageMetricPrice[] prices =
        [
            new(1, Currency.Ars, 500m, new DateOnly(2026, 1, 1)),
            new(1, Currency.Ars, 650m, new DateOnly(2026, 12, 1)),
            new(1, Currency.Usd, 0.5m, new DateOnly(2026, 1, 1)),
        ];

        Assert.AreEqual(500m, UsageMetricPrice.On(prices, 1, Currency.Ars, new DateOnly(2026, 11, 30))!.UnitPrice);
        Assert.AreEqual(650m, UsageMetricPrice.On(prices, 1, Currency.Ars, new DateOnly(2026, 12, 1))!.UnitPrice);
        Assert.AreEqual(0.5m, UsageMetricPrice.On(prices, 1, Currency.Usd, new DateOnly(2026, 12, 1))!.UnitPrice);
        Assert.IsNull(UsageMetricPrice.On(prices, 1, Currency.Ars, new DateOnly(2025, 12, 31)));
        Assert.ThrowsExactly<ArgumentException>(() => new UsageMetricPrice(1, Currency.Ars, -1m, new DateOnly(2026, 1, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => new UsageMetricPrice(1, Currency.Ars, 0.0000001m, new DateOnly(2026, 1, 1)));
    }

    [TestMethod]
    public void Muestra_SeFacturaUnaSolaVez()
    {
        var sample = new UsageSample(1, "disk_gb", new DateOnly(2026, 11, 1), 10m, 10m, Now);
        var charge = new UsageCharge(1, "disk_gb", 1, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1), 10m, 5m, 5m, 100m, Currency.Ars, Now);

        sample.BilledBy(charge);

        Assert.ThrowsExactly<InvalidOperationException>(() => sample.BilledBy(charge));
        Assert.ThrowsExactly<ArgumentException>(() => new UsageCharge(1, "disk_gb", 1, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 1), 0m, 0m, 0m, null, Currency.Ars, Now));
    }

    private static decimal Dec(string text) => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
}
