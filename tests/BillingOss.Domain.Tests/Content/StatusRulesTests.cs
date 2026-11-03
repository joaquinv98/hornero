using BillingOss.Domain.Content;

namespace BillingOss.Domain.Tests.Content;

/// <summary>Estados de la red y reglas de descargas, sin base (secciones 5.2 y 6 de diseno/contenidos.md).</summary>
[TestClass]
public sealed class StatusRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private static StatusEvent Maintenance(TimeSpan from, TimeSpan to, StatusEventState state = StatusEventState.Scheduled) =>
        new() { Kind = StatusEventKind.Maintenance, State = state, StartsAt = Now + from, EndsAt = Now + to };

    [TestMethod]
    public void Gravedad_ElOrdenDelEnumEsLaGravedad()
    {
        Assert.AreEqual(ComponentStatus.MajorOutage, StatusRules.Worst([ComponentStatus.Degraded, ComponentStatus.MajorOutage, ComponentStatus.Maintenance]));
        Assert.AreEqual(ComponentStatus.Degraded, StatusRules.Worst([ComponentStatus.Maintenance, ComponentStatus.Degraded]));
        Assert.AreEqual(ComponentStatus.Operational, StatusRules.Worst([]));
    }

    [TestMethod]
    public void Incidente_SeMueveLibrementeMientrasEstaAbiertoYAlResolverseNoVuelve()
    {
        var machine = StatusRules.IncidentStates;

        Assert.IsTrue(machine.CanMove(StatusEventState.Investigating, StatusEventState.Identified));
        Assert.IsTrue(machine.CanMove(StatusEventState.Monitoring, StatusEventState.Investigating));
        Assert.IsTrue(machine.CanMove(StatusEventState.Investigating, StatusEventState.Resolved));
        Assert.IsTrue(machine.CanMove(StatusEventState.Resolved, StatusEventState.Resolved));
        Assert.IsFalse(machine.CanMove(StatusEventState.Resolved, StatusEventState.Investigating));
        Assert.IsFalse(machine.CanMove(StatusEventState.Investigating, StatusEventState.Scheduled));
    }

    [TestMethod]
    public void Mantenimiento_SoloSeTerminaOSeCancelaDesdeProgramado()
    {
        var machine = StatusRules.MaintenanceStates;

        Assert.IsTrue(machine.CanMove(StatusEventState.Scheduled, StatusEventState.Completed));
        Assert.IsTrue(machine.CanMove(StatusEventState.Scheduled, StatusEventState.Cancelled));
        Assert.IsFalse(machine.CanMove(StatusEventState.Completed, StatusEventState.Scheduled));
        Assert.IsFalse(machine.CanMove(StatusEventState.Cancelled, StatusEventState.Completed));
    }

    [TestMethod]
    public void Mantenimiento_LaFaseSeDeduceConLaHora()
    {
        Assert.AreEqual(MaintenancePhase.Upcoming, StatusRules.Phase(Maintenance(TimeSpan.FromHours(1), TimeSpan.FromHours(2)), Now));
        Assert.AreEqual(MaintenancePhase.InProgress, StatusRules.Phase(Maintenance(-TimeSpan.FromHours(1), TimeSpan.FromHours(1)), Now));
        Assert.AreEqual(MaintenancePhase.Completed, StatusRules.Phase(Maintenance(-TimeSpan.FromHours(2), -TimeSpan.FromHours(1)), Now), "Pasado el hasta, terminó sola.");
        Assert.AreEqual(MaintenancePhase.Completed, StatusRules.Phase(Maintenance(-TimeSpan.FromHours(1), TimeSpan.FromHours(1), StatusEventState.Completed), Now));
        Assert.AreEqual(MaintenancePhase.Cancelled, StatusRules.Phase(Maintenance(-TimeSpan.FromHours(1), TimeSpan.FromHours(1), StatusEventState.Cancelled), Now));
        Assert.AreEqual(MaintenancePhase.InProgress, StatusRules.Phase(Maintenance(TimeSpan.Zero, TimeSpan.FromHours(1)), Now), "El desde ya empezó.");
        Assert.AreEqual(MaintenancePhase.Completed, StatusRules.Phase(Maintenance(-TimeSpan.FromHours(1), TimeSpan.Zero), Now), "El hasta ya pasó.");
    }

    [TestMethod]
    public void Afecta_UnIncidenteAbiertoOUnMantenimientoEnCurso()
    {
        var open = new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Monitoring, StartsAt = Now };
        var resolved = new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Resolved, StartsAt = Now, EndsAt = Now };

        Assert.IsTrue(StatusRules.Affects(open, Now));
        Assert.IsFalse(StatusRules.Affects(resolved, Now));
        Assert.IsFalse(StatusRules.Affects(Maintenance(TimeSpan.FromHours(1), TimeSpan.FromHours(2)), Now));
        Assert.IsTrue(StatusRules.Affects(Maintenance(-TimeSpan.FromHours(1), TimeSpan.FromHours(1)), Now));
        Assert.IsFalse(StatusRules.Affects(Maintenance(-TimeSpan.FromHours(2), -TimeSpan.FromHours(1)), Now));
        Assert.IsTrue(StatusRules.IsOpen(open));
        Assert.IsFalse(StatusRules.IsOpen(resolved));
        Assert.IsTrue(StatusRules.IsOpen(Maintenance(TimeSpan.FromHours(1), TimeSpan.FromHours(2))));
    }

    [TestMethod]
    public void Impacto_UnIncidenteMarcaDegradadoOInterrupcion_UnMantenimientoMarcaMantenimiento()
    {
        Assert.IsTrue(StatusRules.ValidImpact(StatusEventKind.Incident, ComponentStatus.Degraded));
        Assert.IsTrue(StatusRules.ValidImpact(StatusEventKind.Incident, ComponentStatus.MajorOutage));
        Assert.IsFalse(StatusRules.ValidImpact(StatusEventKind.Incident, ComponentStatus.Maintenance));
        Assert.IsFalse(StatusRules.ValidImpact(StatusEventKind.Incident, ComponentStatus.Operational));
        Assert.IsTrue(StatusRules.ValidImpact(StatusEventKind.Maintenance, ComponentStatus.Maintenance));
        Assert.IsFalse(StatusRules.ValidImpact(StatusEventKind.Maintenance, ComponentStatus.Degraded));
    }

    [TestMethod]
    public void Evento_ValidacionesDeVentanaYFin()
    {
        Assert.IsNull(StatusRules.Error(Maintenance(TimeSpan.FromHours(1), TimeSpan.FromHours(2))));
        Assert.IsNotNull(StatusRules.Error(Maintenance(TimeSpan.FromHours(2), TimeSpan.FromHours(1))));
        Assert.IsNotNull(StatusRules.Error(new StatusEvent { Kind = StatusEventKind.Maintenance, State = StatusEventState.Scheduled, StartsAt = Now }));
        Assert.IsNotNull(StatusRules.Error(new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Scheduled, StartsAt = Now }));
        Assert.IsNull(StatusRules.Error(new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Investigating, StartsAt = Now }));
        Assert.IsNotNull(StatusRules.Error(new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Investigating, StartsAt = Now, EndsAt = Now }));
        Assert.IsNotNull(StatusRules.Error(new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Resolved, StartsAt = Now }));
        Assert.IsNull(StatusRules.Error(new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Resolved, StartsAt = Now, EndsAt = Now.AddMinutes(5) }));
    }

    [TestMethod]
    public void Novedad_LaFaseDeUnaNovedadSeDeduceConLaHora()
    {
        var published = new Announcement { Status = PublishState.Published, PublishAt = Now.AddHours(-1), ExpiresAt = Now.AddHours(1) };

        Assert.AreEqual(AnnouncementPhase.Live, published.Phase(Now));
        Assert.AreEqual(AnnouncementPhase.Scheduled, published.Phase(Now.AddHours(-2)));
        Assert.AreEqual(AnnouncementPhase.Expired, published.Phase(Now.AddHours(1)), "El vencimiento ya pasó.");
        Assert.AreEqual(AnnouncementPhase.Draft, new Announcement { Status = PublishState.Draft, PublishAt = Now }.Phase(Now));
        Assert.AreEqual(AnnouncementPhase.Archived, new Announcement { Status = PublishState.Archived, PublishAt = Now }.Phase(Now));
        Assert.IsNotNull(new Announcement { PublishAt = Now, ExpiresAt = Now }.DateError());
        Assert.IsNull(new Announcement { PublishAt = Now, ExpiresAt = Now.AddSeconds(1) }.DateError());
        Assert.IsNull(new Announcement().DateError());
    }

    [TestMethod]
    [DataRow("Mi agente (v1).zip", "Mi_agente_v1_.zip")]
    [DataRow("..\\..\\windows\\system32\\agente.zip", "agente.zip")]
    [DataRow("/etc/passwd.txt", "passwd.txt")]
    [DataRow("  espacios  .pdf", "espacios_.pdf")]
    [DataRow("Instalación ñandú.exe", "Instalacion_nandu.exe")]
    [DataRow("....", "")]
    [DataRow("", "")]
    public void Descargas_ElNombreSeLimpiaSinRutasNiCaracteresRaros(string name, string expected)
    {
        Assert.AreEqual(expected, DownloadRules.CleanName(name));
    }

    [TestMethod]
    public void Descargas_ElNombreLargoConservaLaExtension()
    {
        var clean = DownloadRules.CleanName(new string('a', 300) + ".zip");

        Assert.AreEqual(DownloadRules.MaxNameLength, clean.Length);
        Assert.IsTrue(clean.EndsWith(".zip", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Descargas_ExtensionYTipo()
    {
        Assert.AreEqual("gz", DownloadRules.Extension("agente.tar.gz"));
        Assert.AreEqual("zip", DownloadRules.Extension("AGENTE.ZIP"));
        Assert.AreEqual(string.Empty, DownloadRules.Extension("sin-extension"));
        Assert.AreEqual(string.Empty, DownloadRules.Extension("termina-en-punto."));
        Assert.AreEqual("application/zip", DownloadRules.ContentType("zip"));
        Assert.AreEqual("application/pdf", DownloadRules.ContentType("pdf"));
        Assert.AreEqual("application/octet-stream", DownloadRules.ContentType("exe"));
        Assert.AreEqual("application/octet-stream", DownloadRules.ContentType("desconocida"));
    }

    [TestMethod]
    [DataRow("html")]
    [DataRow("htm")]
    [DataRow("svg")]
    [DataRow("js")]
    [DataRow("mjs")]
    [DataRow("php")]
    [DataRow("xhtml")]
    public void Descargas_LosTiposQueUnNavegadorEjecutaNoEstanDeFabrica(string extension)
    {
        CollectionAssert.DoesNotContain(DownloadRules.DefaultExtensions.ToList(), extension);
    }

    [TestMethod]
    public void Descargas_LosTiposQueNoSeInterpretanSalenConUnTipoFijo()
    {
        foreach (var extension in DownloadRules.DefaultExtensions)
        {
            var type = DownloadRules.ContentType(extension);
            Assert.IsFalse(type.StartsWith("text/html", StringComparison.Ordinal) || type.Contains("svg", StringComparison.Ordinal) || type.Contains("javascript", StringComparison.Ordinal), $"{extension}: {type}");
        }
    }
}
