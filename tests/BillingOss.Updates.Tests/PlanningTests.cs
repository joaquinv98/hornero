using static BillingOss.Updates.Tests.ChannelKit;

namespace BillingOss.Updates.Tests;

/// <summary>Camino de actualización y estado del panel (secciones 7.2 y 7.4 de D-20).</summary>
[TestClass]
public sealed class PlanningTests
{
    private static readonly ReleaseChannel Lines = Channel(releases:
    [
        Release("0.2.7"),
        Release("0.3.0", from: "0.2.0"),
        Release("0.3.5", from: "0.2.0"),
        Release("0.4.0", from: "0.3.0"),
        Release("0.4.2", from: "0.3.0", security: true, cves: "CVE-2026-1111"),
    ]);

    [TestMethod]
    public void LosParchesSeSalteanYLasMinorsVanDeAUna()
    {
        var path = UpdatePlanner.Plan(Lines, new Version(0, 2, 7), new Version(0, 4, 2));

        CollectionAssert.AreEqual(new[] { "0.3.5", "0.4.2" }, path.Select(r => r.ReleaseVersion).ToArray());
    }

    [TestMethod]
    public void DentroDeLaMismaMinor_VaDirectoAlPedido()
    {
        var path = UpdatePlanner.Plan(Lines, new Version(0, 4, 0), new Version(0, 4, 2));

        CollectionAssert.AreEqual(new[] { "0.4.2" }, path.Select(r => r.ReleaseVersion).ToArray());
    }

    [TestMethod]
    public void SiLaMinorSiguientePideUnParcheAntes_PasaPorEl()
    {
        var channel = Channel(releases: [Release("0.2.5"), Release("0.2.7"), Release("0.3.1", from: "0.2.7")]);

        var path = UpdatePlanner.Plan(channel, new Version(0, 2, 3), new Version(0, 3, 1));

        CollectionAssert.AreEqual(new[] { "0.2.7", "0.3.1" }, path.Select(r => r.ReleaseVersion).ToArray());
    }

    [TestMethod]
    public void SinCamino_RetiradaOBloqueada_NoHayPlan()
    {
        var withdrawn = Channel(releases: [Release("0.4.0"), Release("0.4.1", withdrawn: true)]);
        Assert.ThrowsExactly<UpdateException>(() => UpdatePlanner.Plan(withdrawn, new Version(0, 4, 0), new Version(0, 4, 1)));
        Assert.ThrowsExactly<UpdateException>(() => UpdatePlanner.Plan(Lines, new Version(0, 4, 0), new Version(0, 4, 2), blocked: ["0.4.2"]));
        Assert.ThrowsExactly<UpdateException>(() => UpdatePlanner.Plan(Channel(releases: Release("0.3.0", from: "0.2.0")), new Version(0, 1, 0), new Version(0, 3, 0)));
        Assert.ThrowsExactly<UpdateException>(() => UpdatePlanner.Plan(Lines, new Version(0, 4, 2), new Version(0, 4, 0)));
        Assert.ThrowsExactly<UpdateException>(() => UpdatePlanner.Plan(Lines, new Version(0, 4, 0), new Version(0, 9, 0)));
    }

    [TestMethod]
    public void ElPisoDeSeguridadEsElUltimoParcheDeSeguridadDeLaLinea()
    {
        Assert.AreEqual(new Version(0, 4, 2), UpdatePlanner.SecurityFloor(Lines, new Version(0, 4, 0)));
        Assert.IsNull(UpdatePlanner.SecurityFloor(Lines, new Version(0, 3, 0)));
    }

    [TestMethod]
    public void Estado_SinChequeo_ActualizadorParado_ViejoORechazado_EsSinDatos()
    {
        var installed = new Version(0, 4, 2);
        Assert.AreEqual("NoCheck", Summary(null, installed).Reason);
        Assert.AreEqual(UpdateState.NoData, Summary(null, installed).State);
        Assert.AreEqual("UpdaterDown", Summary(Lines, installed, seen: Now.AddMinutes(-30)).Reason);
        Assert.AreEqual("Stale", Summary(Lines with { Expires = Now.AddDays(10) }, installed, checkedAt: Now.AddHours(-49)).Reason);
        Assert.AreEqual("Expired", Summary(Lines with { Expires = Now.AddMinutes(-1) }, installed).Reason);
        Assert.AreEqual("Format", Summary(Lines with { Format = 2, Releases = [] }, installed).Reason);
        Assert.AreEqual("Rejected", UpdateStatus.Summarize(new ChannelSnapshot(Lines, Now.AddHours(-2), Now.AddMinutes(-5), "firma inválida", true, Now), installed, Now).Reason);

        // Una falla de red con datos recientes no tapa lo que se sabe.
        Assert.AreEqual(UpdateState.UpToDate, UpdateStatus.Summarize(new ChannelSnapshot(Lines, Now.AddHours(-2), Now.AddMinutes(-5), "sin red", false, Now), installed, Now).State);
    }

    [TestMethod]
    public void Estado_FaltaUnParcheDeSeguridad_EsActualizaYa_ConSusCve()
    {
        var summary = Summary(Lines, new Version(0, 4, 0));

        Assert.AreEqual(UpdateState.Urgent, summary.State);
        Assert.AreEqual("Security", summary.Reason);
        Assert.AreEqual("CVE-2026-1111", summary.Cves.Single().CveId);
        Assert.AreEqual("0.4.2", summary.Newer[0].ReleaseVersion);
    }

    [TestMethod]
    public void Estado_RetiradaOSinSoporte_EsActualizaYa()
    {
        var withdrawn = Channel(releases: [Release("0.4.0", withdrawn: true), Release("0.4.1")]);
        Assert.AreEqual("Withdrawn", Summary(withdrawn, new Version(0, 4, 0)).Reason);
        Assert.AreEqual("EndOfLife", Summary(Channel(releases: [Release("0.5.0")]), new Version(0, 3, 0)).Reason);
        Assert.AreEqual("EndOfLife", Summary(Lines with { EolDate = new DateOnly(2026, 11, 1) }, new Version(0, 4, 2)).Reason);
    }

    [TestMethod]
    public void Estado_HayVersionOAlDia()
    {
        var sinSeguridad = Channel(releases: [Release("0.4.0"), Release("0.4.1")]);
        Assert.AreEqual(UpdateState.Available, Summary(sinSeguridad, new Version(0, 4, 0)).State);
        Assert.AreEqual(UpdateState.UpToDate, Summary(sinSeguridad, new Version(0, 4, 1)).State);
    }

    [TestMethod]
    public void PostgresDebajoDeLaMinima_SeAvisa()
    {
        var channel = Lines with { Minimums = new Minimums { Postgres = new Dictionary<string, string> { ["18"] = "18.2" } } };

        Assert.IsTrue(UpdateStatus.PostgresBehind(channel, 180001));
        Assert.IsFalse(UpdateStatus.PostgresBehind(channel, 180002));
        Assert.IsFalse(UpdateStatus.PostgresBehind(channel, 170009));
        Assert.IsTrue(Summary(channel, new Version(0, 4, 2), serverVersionNum: 180001).PostgresBehind);
    }

    private static UpdateSummary Summary(ReleaseChannel? channel, Version installed, DateTimeOffset? checkedAt = null, DateTimeOffset? seen = null, int? serverVersionNum = null) =>
        UpdateStatus.Summarize(new ChannelSnapshot(channel, channel is null ? null : checkedAt ?? Now.AddHours(-1), null, null, false, seen ?? Now.AddMinutes(-1)), installed, Now, serverVersionNum);
}
