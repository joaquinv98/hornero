using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;

namespace BillingOss.Domain.Tests.Services;

[TestClass]
public sealed class BillingScheduleTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public void Anclaje31_UsaElUltimoDiaYVuelveAl31SinDeriva()
    {
        var enero = BillingSchedule.PeriodStartingAt(D("2027-01-31"), BillingCycle.Monthly, 31);
        var febrero = BillingSchedule.PeriodStartingAt(enero.End, BillingCycle.Monthly, 31);
        var marzo = BillingSchedule.PeriodStartingAt(febrero.End, BillingCycle.Monthly, 31);

        Assert.AreEqual(D("2027-02-28"), enero.End);
        Assert.AreEqual(D("2027-03-31"), febrero.End);
        Assert.AreEqual(D("2027-04-30"), marzo.End);
    }

    [TestMethod]
    public void AnioBisiesto() =>
        Assert.AreEqual(D("2028-02-29"), BillingSchedule.PeriodStartingAt(D("2028-01-30"), BillingCycle.Monthly, 30).End);

    [TestMethod]
    [DataRow("2026-11-15", 3, "2027-02-15")]
    [DataRow("2026-11-15", 12, "2027-11-15")]
    public void CiclosDeVariosMeses(string start, int months, string expectedEnd)
    {
        var cycle = months == 12 ? BillingCycle.Annually : new BillingCycle(CycleUnit.Month, months);

        Assert.AreEqual(D(expectedEnd), BillingSchedule.PeriodStartingAt(D(start), cycle, 15).End);
    }

    [TestMethod]
    public void PagoUnicoNoTienePeriodos() =>
        Assert.ThrowsExactly<ArgumentException>(() => BillingSchedule.PeriodStartingAt(D("2026-11-01"), BillingCycle.OneTime, 1));

    [TestMethod]
    public void Prorrateo_PorDiasQueQuedan()
    {
        var noviembre = BillingSchedule.PeriodStartingAt(D("2026-11-01"), BillingCycle.Monthly, 1);

        Assert.AreEqual(30, noviembre.Days);
        Assert.AreEqual(0.5m, BillingSchedule.RemainingFraction(noviembre, D("2026-11-16")));
        Assert.AreEqual(3.33m, BillingSchedule.Prorate(10m, noviembre, D("2026-11-21")));
    }

    [TestMethod]
    public void Prorrateo_FueraDelPeriodo() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            BillingSchedule.RemainingFraction(new Period(D("2026-11-01"), D("2026-12-01")), D("2026-12-01")));
}
