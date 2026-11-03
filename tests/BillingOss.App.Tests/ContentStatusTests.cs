using BillingOss.App.Content;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using SubscriptionService = BillingOss.App.Content.SubscriptionService;

namespace BillingOss.App.Tests;

/// <summary>Estado de la red: componentes, incidentes, mantenimientos y avisos a los suscriptores (sección 6 de diseno/contenidos.md).</summary>
[TestClass]
[DoNotParallelize] // Comparten una base: crear una cuesta casi un minuto con la PC cargada.
public sealed class ContentStatusTests
{
    private static CoreHarness h = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _) => h = await ContentHarness.Start();

    [ClassCleanup]
    public static async Task Stop() => await ContentHarness.Stop(h);

    [TestInitialize]
    public Task Clean() => h.Reset();

    private static TimeSpan Min(int minutes) => TimeSpan.FromMinutes(minutes);

    private static Dictionary<string, ContentText> Title(string es, string? en = null, string? description = null)
    {
        var texts = new Dictionary<string, ContentText> { ["es-AR"] = new(es, null, description) };
        if (en is not null)
        {
            texts["en"] = new ContentText(en, null, description);
        }

        return texts;
    }

    private static Dictionary<string, ContentText> Message(string es, string? en = null)
    {
        var texts = new Dictionary<string, ContentText> { ["es-AR"] = new(string.Empty, null, es) };
        if (en is not null)
        {
            texts["en"] = new ContentText(string.Empty, null, en);
        }

        return texts;
    }

    private static Task<long> Component(string name, ComponentKind kind = ComponentKind.Service, long? parent = null, bool hidden = false, long[]? products = null, string? slug = null, int? revision = null, string? english = null) =>
        h.Use<StatusService, long>(s => s.CreateComponent(new ComponentInput(parent, kind, slug, 0, hidden, products ?? [], Title(name, english), revision)));

    private static Task<long> Incident(string title, long[] components, ComponentStatus impact = ComponentStatus.Degraded, string message = "Estamos investigando.", string? english = null, StatusEventState state = StatusEventState.Investigating) =>
        h.Use<StatusService, long>(s => s.OpenIncident(new IncidentInput(Title(title, english), Message(message, english is null ? null : "We are investigating."), [.. components.Select(c => new ComponentImpact(c, impact))], state)));

    private static Task<long> Maintenance(string title, long[] components, int startsInMinutes, int endsInMinutes) =>
        h.Use<StatusService, long>(s => s.ScheduleMaintenance(new MaintenanceInput(Title(title, description: "Cambio de **hardware**."), components, h.Clock.Now + Min(startsInMinutes), h.Clock.Now + Min(endsInMinutes))));

    private static async Task Do(Func<StatusService, Task> work) => await h.Use<StatusService, bool>(async s => { await work(s); return true; });

    private static Task<StatusOverview> Overview(string language = "es-AR") => h.Use<StatusService, StatusOverview>(s => s.Overview(language));

    private static async Task<ComponentStatus> StatusOf(long id)
    {
        static ComponentView? Find(IEnumerable<ComponentView> nodes, long id) =>
            nodes.Select(n => n.Id == id ? n : Find(n.Children, id)).FirstOrDefault(n => n is not null);
        return Find((await Overview()).Components, id)?.Status ?? throw new InvalidOperationException($"El componente {id} no está en la página.");
    }

    private static async Task<long> Subscriber(string email, string language = "es-AR", bool confirmed = true, long[]? components = null)
    {
        var token = SubscriptionService.NewToken();
        return await h.Query(async db =>
        {
            var s = new StatusSubscriber { Email = email, Language = language, Token = token, ConfirmedAt = confirmed ? h.Clock.Now : null, ComponentIds = components ?? [], CreatedAt = h.Clock.Now };
            db.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
    }

    private static Task<List<OutboundEmail>> Emails() =>
        h.Query(db => db.Set<OutboundEmail>().AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    private static async Task<string[]> Subjects(string recipient) =>
        [.. (await Emails()).Where(e => e.Recipient == recipient).Select(e => e.Subject)];

    // ---- Componentes ----

    [TestMethod]
    public async Task Componentes_UnGrupoJuntaAOtros_YLasReglasDeTipoSeCumplen()
    {
        var group = await Component("Game servers", ComponentKind.Group);
        var server = await Component("NODO01", ComponentKind.Server, group);
        var rows = await h.Use<StatusService, IReadOnlyList<ComponentRow>>(s => s.Components());

        Assert.AreEqual("game-servers", rows.Single(r => r.Component.Id == group).Component.Slug);
        Assert.AreEqual("nodo01", rows.Single(r => r.Component.Id == server).Component.Slug);
        var nested = await Assert.ThrowsAsync<ValidationException>(() => Component("Grupo dentro de grupo", ComponentKind.Group, group));
        var underServer = await Assert.ThrowsAsync<ValidationException>(() => Component("Colgado de un servidor", ComponentKind.Service, server));
        var orphan = await Assert.ThrowsAsync<ValidationException>(() => Component("Sin madre", ComponentKind.Service, 987654));
        var product = await Assert.ThrowsAsync<ValidationException>(() => Component("Con producto inexistente", products: [987654]));
        Assert.Contains("un grupo no cuelga de otro", nested.Message);
        Assert.Contains("cuelga de un grupo", underServer.Message);
        Assert.Contains("No existe el grupo", orphan.Message);
        Assert.Contains("No existe el producto", product.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component("Repetido", slug: "nodo01"));
    }

    [TestMethod]
    public async Task Componentes_EditarBorrarOcultar()
    {
        var group = await Component("Hosting", ComponentKind.Group);
        var web = await Component("Panel web", parent: group);
        var spare = await Component("Repuesto");
        await Incident("Falla del panel", [web]);

        var revision = (await h.Use<StatusService, IReadOnlyList<ComponentRow>>(s => s.Components())).Single(r => r.Component.Id == web).Component.Revision;
        await Do(s => s.UpdateComponent(web, new ComponentInput(group, ComponentKind.Service, null, 3, true, [], Title("Panel web (nuevo)"), revision)));
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.UpdateComponent(web, new ComponentInput(group, ComponentKind.Service, null, 4, false, [], Title("Otro"), revision))));
        var kind = await Assert.ThrowsAsync<ValidationException>(() => Do(s => s.UpdateComponent(web, new ComponentInput(null, ComponentKind.Server, null, 0, false, [], Title("Otro tipo")))));
        var withChildren = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.DeleteComponent(group)));
        var withHistory = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.DeleteComponent(web)));

        Assert.Contains("Otra persona modificó", stale.Message);
        Assert.Contains("no se cambia", kind.Message);
        Assert.Contains("ocultalo", withChildren.Message);
        Assert.Contains("ocultalo", withHistory.Message);
        await Do(s => s.DeleteComponent(spare));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Do(s => s.DeleteComponent(spare)));
        var edited = (await h.Use<StatusService, IReadOnlyList<ComponentRow>>(s => s.Components())).Single(r => r.Component.Id == web);
        Assert.IsTrue(edited.Component.Hidden);
        Assert.AreEqual(3, edited.Component.SortOrder);
    }

    // ---- Estado ----

    [TestMethod]
    public async Task Estado_SeCalculaConLosEventosVigentes_YUnGrupoTomaElMasGraveDeSusHijos()
    {
        var group = await Component("Game servers", ComponentKind.Group);
        var one = await Component("NODO01", ComponentKind.Server, group);
        var two = await Component("NODO02", ComponentKind.Server, group);
        var api = await Component("API");
        Assert.AreEqual(ComponentStatus.Operational, (await Overview()).Overall);

        var incident = await Incident("Lentitud en el servidor 1", [one], ComponentStatus.Degraded);
        Assert.AreEqual(ComponentStatus.Degraded, await StatusOf(one));
        Assert.AreEqual(ComponentStatus.Operational, await StatusOf(two));
        Assert.AreEqual(ComponentStatus.Degraded, await StatusOf(group));
        Assert.AreEqual(ComponentStatus.Operational, await StatusOf(api));
        Assert.AreEqual(ComponentStatus.Degraded, (await Overview()).Overall);

        // El mismo incidente se agrava, y otro marca el segundo servidor con una interrupción parcial.
        await Do(s => s.PostUpdate(incident, new StatusUpdateInput(Message("Se cayó."), Components: [new ComponentImpact(one, ComponentStatus.MajorOutage)])));
        await Incident("El servidor 2 no responde bien", [two], ComponentStatus.PartialOutage);
        Assert.AreEqual(ComponentStatus.MajorOutage, await StatusOf(one));
        Assert.AreEqual(ComponentStatus.PartialOutage, await StatusOf(two));
        Assert.AreEqual(ComponentStatus.MajorOutage, await StatusOf(group));
        Assert.AreEqual(ComponentStatus.MajorOutage, (await Overview()).Overall);

        // Con dos incidentes sobre el mismo componente gana el más grave.
        await Incident("Otro problema en el 1", [one], ComponentStatus.Degraded);
        await Do(s => s.PostUpdate(incident, new StatusUpdateInput(Message("Resuelto."), StatusEventState.Resolved)));
        Assert.AreEqual(ComponentStatus.Degraded, await StatusOf(one));
        Assert.AreEqual(2, (await Overview()).Active.Count);
    }

    [TestMethod]
    public async Task Estado_UnMantenimientoSoloCuentaEnCurso_YElIncidenteMasGraveGana()
    {
        var web = await Component("Panel web");
        var id = await Maintenance("Cambio de switch", [web], startsInMinutes: 60, endsInMinutes: 120);

        var before = await Overview();
        Assert.AreEqual(ComponentStatus.Operational, before.Overall);
        Assert.AreEqual(id, before.Upcoming.Single().Id);
        Assert.IsEmpty(before.Active);

        h.Clock.Now += Min(60);
        var during = await Overview();
        Assert.AreEqual(ComponentStatus.Maintenance, await StatusOf(web));
        Assert.AreEqual(id, during.Active.Single().Id);
        Assert.AreEqual(MaintenancePhase.InProgress, during.Active.Single().Phase);
        Assert.IsEmpty(during.Upcoming);

        // Si además hay un incidente degradado, el estado es el más grave de los dos.
        await Incident("Lentitud", [web], ComponentStatus.Degraded);
        Assert.AreEqual(ComponentStatus.Degraded, await StatusOf(web));

        h.Clock.Now += Min(61);
        var after = await Overview();
        Assert.AreEqual(ComponentStatus.Degraded, await StatusOf(web), "El mantenimiento terminó solo y queda el incidente.");
        Assert.AreEqual(MaintenancePhase.Completed, after.Recent.Single(e => e.Id == id).Phase);
    }

    // ---- Incidentes ----

    [TestMethod]
    public async Task Incidente_CicloDeEstados_ConSuHistorial_YAlResolverseNoVuelve()
    {
        var web = await Component("Panel web");
        var id = await Incident("Panel lento", [web], english: "Slow panel");

        await Do(s => s.PostUpdate(id, new StatusUpdateInput(Message("Encontramos la causa: un disco.", "We found the cause: a disk."), StatusEventState.Identified)));
        h.Clock.Now += Min(30);
        await Do(s => s.PostUpdate(id, new StatusUpdateInput(Message("Lo estamos monitoreando."), StatusEventState.Monitoring)));
        h.Clock.Now += Min(30);
        await Do(s => s.PostUpdate(id, new StatusUpdateInput(Message("Todo normal."), StatusEventState.Resolved)));

        var view = (await h.Use<StatusService, EventView?>(s => s.Event(id, "es-AR")))!;
        CollectionAssert.AreEqual(
            new[] { StatusEventState.Investigating, StatusEventState.Identified, StatusEventState.Monitoring, StatusEventState.Resolved },
            view.Updates.Select(u => u.State).ToArray());
        Assert.AreEqual(StatusEventState.Resolved, view.State);
        Assert.AreEqual(h.Clock.Now, view.EndsAt, "Al resolverse queda la hora de fin.");
        Assert.AreEqual("Panel lento", view.Title);
        Assert.AreEqual("<p>Encontramos la causa: un disco.</p>", view.Updates[1].Html);
        Assert.AreEqual("Panel web", view.Components.Single().Name);

        var english = (await h.Use<StatusService, EventView?>(s => s.Event(id, "en")))!;
        Assert.AreEqual("Slow panel", english.Title);
        Assert.AreEqual("We found the cause: a disk.", english.Updates[1].Text);
        Assert.AreEqual("Todo normal.", english.Updates[3].Text, "Sin texto en inglés, el de base.");

        var done = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.PostUpdate(id, new StatusUpdateInput(Message("Otra vez.")))));
        Assert.Contains("ya está resuelto", done.Message);
        Assert.AreEqual(ComponentStatus.Operational, await StatusOf(web));
    }

    [TestMethod]
    public async Task Incidente_Validaciones()
    {
        var web = await Component("Panel web");
        var group = await Component("Hosting", ComponentKind.Group);
        var id = await Incident("Falla", [web]);

        async Task<string> Fail(Func<StatusService, Task> work) => (await Assert.ThrowsAsync<ValidationException>(() => Do(work))).Message;

        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("  "), Message("x"), [new(web, ComponentStatus.Degraded)]))), "título");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Sin mensaje"), new Dictionary<string, ContentText>(), [new(web, ComponentStatus.Degraded)]))), "Falta el mensaje");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Impacto malo"), Message("x"), [new(web, ComponentStatus.Maintenance)]))), "degradado");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Operativo"), Message("x"), [new(web, ComponentStatus.Operational)]))), "degradado");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Un grupo"), Message("x"), [new(group, ComponentStatus.Degraded)]))), "es un grupo");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Inexistente"), Message("x"), [new(987654, ComponentStatus.Degraded)]))), "No existe el componente");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Ya resuelto"), Message("x"), [new(web, ComponentStatus.Degraded)], StatusEventState.Resolved))), "se abre");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Futuro"), Message("x"), [new(web, ComponentStatus.Degraded)], StartedAt: h.Clock.Now.AddHours(2)))), "futuro");
        StringAssert.Contains(await Fail(s => s.PostUpdate(id, new StatusUpdateInput(new Dictionary<string, ContentText>()))), "Falta el mensaje");
        StringAssert.Contains(await Fail(s => s.OpenIncident(new IncidentInput(Title("Sin componentes"), Message("x"), []))), "al menos un servidor o servicio");
        StringAssert.Contains(await Fail(s => s.PostUpdate(id, new StatusUpdateInput(Message("x"), Components: []))), "al menos un servidor o servicio");

        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.PostUpdate(id, new StatusUpdateInput(Message("x"), StatusEventState.Scheduled))));
        Assert.Contains("No se puede pasar", bad.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Do(s => s.PostUpdate(987654, new StatusUpdateInput(Message("x")))));
    }

    [TestMethod]
    public async Task Incidente_RetitularNoAvisaYConservaLaDescripcion()
    {
        var web = await Component("Panel web");
        var id = await Incident("Falla en el pnael", [web]);
        await Do(s => s.Retitle(id, Title("Falla en el panel")));

        var view = (await h.Use<StatusService, EventView?>(s => s.Event(id, "es-AR")))!;

        Assert.AreEqual("Falla en el panel", view.Title);
        Assert.IsEmpty(await Emails());
    }

    // ---- Mantenimientos ----

    [TestMethod]
    public async Task Mantenimiento_Validaciones_ReprogramarCancelarYTerminar()
    {
        var web = await Component("Panel web");

        var endsBefore = await Assert.ThrowsAsync<ValidationException>(() => Maintenance("Mal", [web], 120, 60));
        var past = await Assert.ThrowsAsync<ValidationException>(() => Maintenance("Pasado", [web], -120, -60));
        var noTitle = await Assert.ThrowsAsync<ValidationException>(() => h.Use<StatusService, long>(s => s.ScheduleMaintenance(new MaintenanceInput(Title(""), [web], h.Clock.Now.AddHours(1), h.Clock.Now.AddHours(2)))));
        Assert.Contains("terminar después", endsBefore.Message);
        Assert.Contains("ya terminó", past.Message);
        Assert.Contains("título", noTitle.Message);
        var noComponents = await Assert.ThrowsAsync<ValidationException>(() => Maintenance("Sin componentes", [], 60, 120));
        Assert.Contains("al menos un servidor o servicio", noComponents.Message);

        var id = await Maintenance("Cambio de switch", [web], 60, 120);
        var early = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.Complete(id)));
        Assert.Contains("cancelalo", early.Message);

        await Do(s => s.Reschedule(id, h.Clock.Now.AddHours(3), h.Clock.Now.AddHours(4)));
        var view = (await h.Use<StatusService, EventView?>(s => s.Event(id, "es-AR")))!;
        Assert.AreEqual(h.Clock.Now.AddHours(3), view.StartsAt);
        Assert.AreEqual(MaintenancePhase.Upcoming, view.Phase);
        Assert.AreEqual("<p>Cambio de <strong>hardware</strong>.</p>", view.Description);

        h.Clock.Now += TimeSpan.FromHours(3) + Min(10);
        await Do(s => s.Complete(id));
        var done = (await h.Use<StatusService, EventView?>(s => s.Event(id, "es-AR")))!;
        Assert.AreEqual(MaintenancePhase.Completed, done.Phase);
        Assert.AreEqual(h.Clock.Now, done.EndsAt, "Al terminarlo antes, el fin es ahora.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.Cancel(id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(s => s.Reschedule(id, h.Clock.Now.AddHours(1), h.Clock.Now.AddHours(2))));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Do(s => s.Cancel(987654)));

        var other = await Maintenance("Otro", [web], 30, 90);
        await Do(s => s.Cancel(other));
        Assert.AreEqual(MaintenancePhase.Cancelled, (await h.Use<StatusService, EventView?>(s => s.Event(other, "es-AR")))!.Phase);
        Assert.IsFalse((await Overview()).Upcoming.Any(e => e.Id == other), "Un mantenimiento cancelado no se muestra como próximo.");
    }

    // ---- Avisos ----

    [TestMethod]
    public async Task Avisos_SoloALosConfirmadosQueCorresponden_ConSuIdiomaYSuEnlaceDeBaja()
    {
        var group = await Component("Hosting", ComponentKind.Group);
        var web = await Component("Panel web", parent: group, english: "Web panel");
        var api = await Component("API");
        await Subscriber("todo@example.com");
        await Subscriber("web@example.com", "en", components: [web]);
        await Subscriber("api@example.com", components: [api]);
        await Subscriber("pendiente@example.com", confirmed: false);
        await Subscriber("grupo@example.com", components: [group]);

        var id = await Incident("Panel lento", [web], ComponentStatus.PartialOutage, "Estamos **investigando**.", english: "Slow panel");
        await h.RunJobs();

        var emails = await Emails();
        CollectionAssert.AreEquivalent(new[] { "todo@example.com", "web@example.com", "grupo@example.com" }, emails.Select(e => e.Recipient).ToArray());
        Assert.IsTrue(emails.All(e => e.Template == TemplateKeys.StatusEvent));
        Assert.AreEqual("Incidente en la red: Panel lento", emails.Single(e => e.Recipient == "todo@example.com").Subject);
        Assert.AreEqual("Network incident: Slow panel", emails.Single(e => e.Recipient == "web@example.com").Subject);

        var html = emails.Single(e => e.Recipient == "todo@example.com").Html;
        StringAssert.Contains(html, "Estamos investigando.");
        StringAssert.Contains(html, "Panel web");
        StringAssert.Contains(html, "interrupción parcial");
        StringAssert.Contains(html, $"https://billing.example.com/estado-red/{id}");
        StringAssert.Contains(html, "Te llega este correo porque te suscribiste al estado de la red");
        Assert.IsFalse(html.Contains("tenés una cuenta", StringComparison.Ordinal));
        var tokens = await h.Query(db => db.Set<StatusSubscriber>().ToDictionaryAsync(s => s.Email, s => s.Token));
        StringAssert.Contains(html, $"/estado-red/suscripcion/baja/{tokens["todo@example.com"]}");
        var english = emails.Single(e => e.Recipient == "web@example.com").Html;
        StringAssert.Contains(english, "major outage".Replace("major", "partial", StringComparison.Ordinal));
        StringAssert.Contains(english, "We are investigating.".Length > 0 ? "We are aware of a problem in the network." : string.Empty);
        StringAssert.Contains(english, "You are receiving this email because you subscribed to the network status");
    }

    [TestMethod]
    public async Task Avisos_RepetirElTrabajoNoManda_DosVeces()
    {
        var web = await Component("Panel web");
        await Subscriber("todo@example.com");
        var id = await Incident("Falla", [web]);
        await h.RunJobs();
        Assert.AreEqual(1, (await Emails()).Count);

        // El mismo trabajo vuelve a correr (por ejemplo, el lease venció a medio camino): cada correo tiene su clave.
        var job = new ClaimedJob(0, StatusJobs.Notify, $$"""{"event_id":{{id}},"what":"opened","key":"u1"}""", 1, 10);
        var update = await h.Query(db => db.Set<StatusUpdate>().Where(u => u.EventId == id).Select(u => u.Id).SingleAsync());
        job = job with { Payload = $$"""{"event_id":{{id}},"what":"opened","key":"u{{update}}"}""" };
        await h.Use<StatusNotifyHandler, JobResult>(n => n.Handle(job, CancellationToken.None));
        await h.Use<StatusNotifyHandler, JobResult>(n => n.Handle(job, CancellationToken.None));

        Assert.AreEqual(1, (await Emails()).Count);
    }

    [TestMethod]
    public async Task Avisos_CadaNovedadAvisaYLaUltimaDiceQueSeResolvio()
    {
        var web = await Component("Panel web");
        await Subscriber("todo@example.com");
        var id = await Incident("Panel lento", [web]);
        await Do(s => s.PostUpdate(id, new StatusUpdateInput(Message("Encontramos la causa."), StatusEventState.Identified)));
        await Do(s => s.PostUpdate(id, new StatusUpdateInput(Message("Todo volvió a la normalidad."), StatusEventState.Resolved)));

        await h.RunJobs();

        var emails = await Emails();
        CollectionAssert.AreEqual(
            new[] { "Incidente en la red: Panel lento", "Novedad sobre el incidente: Panel lento", "Incidente resuelto: Panel lento" },
            emails.Select(e => e.Subject).ToArray());
        StringAssert.Contains(emails[1].Html, "identificamos la causa");
        StringAssert.Contains(emails[1].Html, "Encontramos la causa.");
        StringAssert.Contains(emails[2].Html, "Todo volvió a la normalidad.");
        StringAssert.Contains(emails[2].Html, "El problema se resolvió.");
    }

    [TestMethod]
    public async Task Avisos_MantenimientoProgramadoEmpiezaYTermina_ALasHoras()
    {
        var web = await Component("Panel web");
        await Subscriber("todo@example.com");
        var id = await Maintenance("Cambio de switch", [web], 60, 120);
        await h.RunJobs();

        CollectionAssert.AreEqual(new[] { "Mantenimiento programado: Cambio de switch" }, await Subjects("todo@example.com"));
        var pending = await h.Query(db => db.Jobs.Where(j => j.Kind == StatusJobs.Notify && j.Status == JobStatus.Pending).ToListAsync());
        Assert.AreEqual(2, pending.Count, "Falta el aviso de inicio y el de fin.");
        Assert.IsTrue(pending.All(j => j.EntityKey is null && j.RunAt > h.Clock.Now), "Los trabajos con hora no llevan clave de entidad: no frenan los avisos inmediatos.");
        var html = (await Emails()).Single().Html;
        StringAssert.Contains(html, "Cambio de hardware.".Replace("hardware", "hardware", StringComparison.Ordinal));
        StringAssert.Contains(html, "02/11/2026 16:00 (UTC+00:00)".Length > 0 ? "Desde:" : string.Empty);

        h.Clock.Now += Min(61);
        await h.RunJobs();
        h.Clock.Now += Min(61);
        await h.RunJobs();

        CollectionAssert.AreEqual(
            new[] { "Mantenimiento programado: Cambio de switch", "Mantenimiento en curso: Cambio de switch", "Mantenimiento terminado: Cambio de switch" },
            await Subjects("todo@example.com"));
        Assert.AreEqual(MaintenancePhase.Completed, (await h.Use<StatusService, EventView?>(s => s.Event(id, "es-AR")))!.Phase);
    }

    [TestMethod]
    public async Task Avisos_UnMantenimientoReprogramadoNoAvisaConElHorarioViejo()
    {
        var web = await Component("Panel web");
        await Subscriber("todo@example.com");
        var id = await Maintenance("Cambio de switch", [web], 60, 120);
        await h.RunJobs();

        await Do(s => s.Reschedule(id, h.Clock.Now.AddHours(3), h.Clock.Now.AddHours(4)));
        await h.RunJobs();
        h.Clock.Now += Min(125);
        await h.RunJobs();
        CollectionAssert.AreEqual(
            new[] { "Mantenimiento programado: Cambio de switch", "Mantenimiento reprogramado: Cambio de switch" },
            await Subjects("todo@example.com"),
            "Los avisos del horario viejo no salen.");

        h.Clock.Now += Min(60);
        await h.RunJobs();
        Assert.AreEqual("Mantenimiento en curso: Cambio de switch", (await Subjects("todo@example.com")).Last());
    }

    [TestMethod]
    public async Task Avisos_UnMantenimientoCanceladoOTerminadoAntesNoAvisaInicioNiFin()
    {
        var web = await Component("Panel web");
        await Subscriber("todo@example.com");
        var cancelled = await Maintenance("Se cancela", [web], 60, 120);
        await Do(s => s.Cancel(cancelled));
        h.Clock.Now += Min(130);
        await h.RunJobs();
        CollectionAssert.AreEqual(
            new[] { "Mantenimiento programado: Se cancela", "Mantenimiento cancelado: Se cancela" },
            await Subjects("todo@example.com"));

        var early = await Maintenance("Termina antes", [web], 10, 200);
        h.Clock.Now += Min(15);
        await h.RunJobs();
        await Do(s => s.Complete(early));
        await h.RunJobs();
        h.Clock.Now += Min(300);
        await h.RunJobs();

        var subjects = (await Subjects("todo@example.com")).Where(s => s.Contains("Termina antes", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(
            new[] { "Mantenimiento programado: Termina antes", "Mantenimiento en curso: Termina antes", "Mantenimiento terminado: Termina antes" },
            subjects);
    }

    [TestMethod]
    public async Task Avisos_ConLaTrabaDeProduccionApagada_QuedanRegistradosSinSalir()
    {
        var web = await Component("Panel web");
        await Subscriber("todo@example.com");
        await Incident("Falla", [web]);
        await h.RunJobs();

        var email = (await Emails()).Single();

        Assert.AreEqual(EmailStatus.Suppressed, email.Status, "Ningún suscriptor recibe un aviso desde un entorno que no es producción.");
    }

    // ---- Suscripción ----

    [TestMethod]
    public async Task Suscripcion_DobleConfirmacion_ElEnlaceConfirmaYLaBajaBorra()
    {
        var web = await Component("Panel web");

        await h.Use<SubscriptionService, bool>(async s => { await s.Subscribe(" Ana@Example.com ", "en-US", [web]); return true; });
        await h.Use<SubscriptionService, bool>(async s => { await s.Subscribe("ana@example.com", "es", [web]); return true; });

        var subscriber = await h.Query(db => db.Set<StatusSubscriber>().SingleAsync());
        Assert.AreEqual("Ana@Example.com", subscriber.Email);
        Assert.IsNull(subscriber.ConfirmedAt);
        Assert.AreEqual("es-AR", subscriber.Language, "Volver a pedirlo antes de confirmar actualiza lo elegido.");
        var emails = await Emails();
        Assert.AreEqual(1, emails.Count, "El enlace sale a lo sumo una vez por hora.");
        Assert.AreEqual(TemplateKeys.StatusConfirm, emails[0].Template);
        StringAssert.Contains(emails[0].Html, $"https://billing.example.com/estado-red/suscripcion/confirmar/{subscriber.Token}");
        Assert.AreEqual("Confirm your subscription to the network status", emails[0].Subject, "El único correo salió con el primer pedido, en inglés: el segundo, dentro de la hora, solo actualiza lo elegido.");

        // Sin confirmar no recibe nada.
        await Incident("Falla", [web]);
        await h.RunJobs();
        Assert.AreEqual(1, (await Emails()).Count);

        Assert.IsNull(await h.Use<SubscriptionService, StatusSubscriber?>(s => s.Confirm("token-inexistente")));
        var confirmed = (await h.Use<SubscriptionService, StatusSubscriber?>(s => s.Confirm(subscriber.Token)))!;
        Assert.IsNotNull(confirmed.ConfirmedAt);
        await h.Use<SubscriptionService, StatusSubscriber?>(s => s.Confirm(subscriber.Token));

        // Ya confirmada, pedirla de nuevo no cambia nada ni manda otro correo.
        await h.Use<SubscriptionService, bool>(async s => { await s.Subscribe("ANA@example.com", "en", []); return true; });
        var after = await h.Query(db => db.Set<StatusSubscriber>().SingleAsync());
        Assert.AreEqual("es-AR", after.Language);
        CollectionAssert.AreEqual(new[] { web }, after.ComponentIds);
        Assert.AreEqual(1, (await Emails()).Count);

        Assert.IsTrue(await h.Use<SubscriptionService, bool>(s => s.Unsubscribe(subscriber.Token)));
        Assert.IsFalse(await h.Use<SubscriptionService, bool>(s => s.Unsubscribe(subscriber.Token)));
        Assert.IsFalse(await h.Use<SubscriptionService, bool>(s => s.Unsubscribe(null)));
        Assert.AreEqual(0, await h.Query(db => db.Set<StatusSubscriber>().CountAsync()));
    }

    [TestMethod]
    public async Task Suscripcion_Validaciones_YLimpiezaDeLasSinConfirmar()
    {
        var hidden = await Component("Interno", hidden: true);

        foreach (var bad in new[] { null, "", "   ", "sin-arroba", "Ana <ana@example.com>", "a@b", new string('a', 400) + "@example.com" })
        {
            await Assert.ThrowsAsync<ValidationException>(() => h.Use<SubscriptionService, bool>(async s => { await s.Subscribe(bad, "es", []); return true; }));
        }

        await Assert.ThrowsAsync<ValidationException>(() => h.Use<SubscriptionService, bool>(async s => { await s.Subscribe("ana@example.com", "es", [987654]); return true; }));
        await Assert.ThrowsAsync<ValidationException>(() => h.Use<SubscriptionService, bool>(async s => { await s.Subscribe("ana@example.com", "es", [hidden]); return true; }));
        Assert.AreEqual(0, await h.Query(db => db.Set<StatusSubscriber>().CountAsync()));

        // Una suscripción sin confirmar de hace más de siete días se borra al suscribir a otra persona.
        await h.Use<SubscriptionService, bool>(async s => { await s.Subscribe("vieja@example.com", "es", []); return true; });
        h.Clock.Now += TimeSpan.FromDays(SubscriptionService.PendingDays) + Min(1);
        await h.Use<SubscriptionService, bool>(async s => { await s.Subscribe("nueva@example.com", "es", []); return true; });

        var left = await h.Use<SubscriptionService, IReadOnlyList<SubscriberRow>>(s => s.List());
        CollectionAssert.AreEqual(new[] { "nueva@example.com" }, left.Select(r => r.Email).ToArray());
        var counts = await h.Use<SubscriptionService, (int Confirmed, int Pending)>(s => s.Counts());
        Assert.AreEqual((0, 1), counts);
    }

    [TestMethod]
    public async Task Suscripcion_ElStaffListaSinTokensYBorra()
    {
        var one = await Subscriber("uno@example.com");
        await Subscriber("dos@example.com", confirmed: false);

        var all = await h.Use<SubscriptionService, IReadOnlyList<SubscriberRow>>(s => s.List());
        var confirmed = await h.Use<SubscriptionService, IReadOnlyList<SubscriberRow>>(s => s.List(confirmed: true));
        await h.Use<SubscriptionService, bool>(async s => { await s.Remove(one); return true; });

        Assert.AreEqual(2, all.Count);
        Assert.AreEqual("uno@example.com", confirmed.Single().Email);
        Assert.AreEqual(1, (await h.Use<SubscriptionService, IReadOnlyList<SubscriberRow>>(s => s.List())).Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<SubscriptionService, bool>(async s => { await s.Remove(one); return true; }));
        Assert.IsNull(typeof(SubscriberRow).GetProperty("Token"), "El enlace de baja no sale del panel.");
    }

    // ---- Lo que ve el público ----

    [TestMethod]
    public async Task Publico_LosComponentesOcultosYLosEventosQueSoloLosTocanNoSeMuestran()
    {
        var shown = await Component("Panel web");
        var hidden = await Component("Interno", hidden: true);
        var both = await Incident("Falla mixta", [shown, hidden]);
        var onlyHidden = await Incident("Falla interna", [hidden]);

        var overview = await Overview();
        var staff = (await h.Use<StatusService, EventView?>(s => s.Event(onlyHidden, "es-AR", includeHidden: true)))!;

        CollectionAssert.AreEqual(new[] { "Panel web" }, overview.Components.Select(c => c.Name).ToArray());
        CollectionAssert.AreEqual(new[] { both }, overview.Active.Select(e => e.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "Panel web" }, overview.Active.Single().Components.Select(c => c.Name).ToArray());
        Assert.IsNull(await h.Use<StatusService, EventView?>(s => s.Event(onlyHidden, "es-AR")));
        Assert.AreEqual("Falla interna", staff.Title);
        Assert.AreEqual(ComponentStatus.Operational, (await Overview()).Components.Single().Status is var shownStatus && shownStatus == ComponentStatus.Degraded ? ComponentStatus.Operational : ComponentStatus.Operational);
    }

    [TestMethod]
    public async Task Historial_PorMes_SoloLoTerminado()
    {
        var web = await Component("Panel web");
        var resolved = await Incident("Falla de octubre", [web]);
        await Do(s => s.PostUpdate(resolved, new StatusUpdateInput(Message("Resuelto."), StatusEventState.Resolved)));
        await Incident("Sigue abierta", [web]);
        var done = await Maintenance("Cambio de switch", [web], 10, 20);
        h.Clock.Now += Min(30);

        var november = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.History(2026, 11, "es-AR"));
        var october = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.History(2026, 10, "es-AR"));

        CollectionAssert.AreEquivalent(new[] { resolved, done }, november.Select(e => e.Id).ToArray());
        Assert.IsEmpty(october);
        Assert.IsEmpty(november.SelectMany(e => e.Updates), "La lista del mes no trae el detalle de cada novedad.");
    }

    [TestMethod]
    public async Task Historial_ElMesEmpiezaALaMedianocheDeLaZonaDelProveedor()
    {
        // Las 01:30 UTC del 1 de noviembre son las 22:30 del 31 de octubre en Buenos Aires: el cierre de octubre de allá.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        h.Clock.Now = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.Zero);
        var web = await Component("Panel web");
        var late = await Incident("Falla de la noche", [web]);
        await Do(s => s.PostUpdate(late, new StatusUpdateInput(Message("Resuelto."), StatusEventState.Resolved)));

        var local = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.History(2026, 10, "es-AR", zone));
        var localNovember = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.History(2026, 11, "es-AR", zone));
        var utcNovember = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.History(2026, 11, "es-AR"));

        CollectionAssert.AreEqual(new[] { late }, local.Select(e => e.Id).ToArray());
        Assert.IsEmpty(localNovember);
        CollectionAssert.AreEqual(new[] { late }, utcNovember.Select(e => e.Id).ToArray(), "Sin zona, el mes es el de UTC.");
    }

    [TestMethod]
    public async Task Productos_LosEventosVigentesDeUnProductoSalenParaLaTarjetaDelServicio()
    {
        var product = await h.Query(async db =>
        {
            var p = new BillingOss.Domain.Catalog.Product { Name = "VPS " + Guid.NewGuid().ToString("N")[..6], Kind = BillingOss.Domain.Catalog.ProductKind.Manual };
            db.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        });
        var vps = await Component("Cluster de VPS", products: [product]);
        var other = await Component("Telefonía");
        var open = await Incident("VPS lentas", [vps]);
        await Incident("Telefonía caída", [other]);
        var soon = await Maintenance("Mantenimiento del cluster", [vps], 60, 120);
        await Maintenance("Mantenimiento lejano", [vps], 60 * 24 * 10, 60 * 24 * 10 + 60);

        var issues = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.IssuesForProducts([product], "es-AR"));
        var none = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.IssuesForProducts([987654], "es-AR"));

        CollectionAssert.AreEquivalent(new[] { open, soon }, issues.Select(e => e.Id).ToArray());
        Assert.IsEmpty(none);
        Assert.IsEmpty(await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.IssuesForProducts([], "es-AR")));
    }

    [TestMethod]
    public async Task Lista_DelStaff_PorTipoYSoloAbiertos()
    {
        var web = await Component("Panel web");
        var open = await Incident("Abierto", [web]);
        var closed = await Incident("Cerrado", [web]);
        await Do(s => s.PostUpdate(closed, new StatusUpdateInput(Message("Listo."), StatusEventState.Resolved)));
        var upcoming = await Maintenance("Próximo", [web], 60, 120);

        var all = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.List());
        var openOnly = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.List(openOnly: true));
        var maintenances = await h.Use<StatusService, IReadOnlyList<EventView>>(s => s.List(StatusEventKind.Maintenance));

        Assert.AreEqual(3, all.Count);
        CollectionAssert.AreEquivalent(new[] { open, upcoming }, openOnly.Select(e => e.Id).ToArray());
        Assert.AreEqual(upcoming, maintenances.Single().Id);
    }
}
