using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using BillingOss.Web.Pages.Admin.Tickets;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Las pantallas de soporte del portal, del panel y la encuesta, de punta a punta sobre el host web.</summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base, como las de UiTests.
public sealed class SupportUiTests
{
    private static string Location(HttpResponseMessage response) => UiTests.Location(response);

    private static Task<long> Department(UiHarness h, string name = "Soporte técnico", Action<Department>? configure = null) => h.Query(async db =>
    {
        var department = new Department { Name = name, CreatedAt = DateTimeOffset.UtcNow };
        configure?.Invoke(department);
        db.Add(department);
        await db.SaveChangesAsync();
        return department.Id;
    });

    private static Task<OpenedTicket> Open(UiHarness h, long department, string subject, long? account = null) =>
        h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, subject, "Hola, tengo un problema.", account ?? h.AccountA),
            new Author(MessageAuthor.Customer, "Cliente", "cliente@example.com", MessageSource.Portal)));

    private static Task<Ticket> Get(UiHarness h, long id) => h.Query(db => db.Set<Ticket>().AsNoTracking().SingleAsync(t => t.Id == id));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Tickets directo en la base, sin mensajes ni avisos, para llenar la cola rápido. El número es 500000 más su lugar y
    /// cada uno tuvo movimiento un minuto antes que el anterior.
    /// </summary>
    private static Task<List<long>> Seed(UiHarness h, params (long Department, string Subject, long? Account, TicketStatus Status, long? Staff)[] rows) => h.Query(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var tickets = rows.Select((r, i) => new Ticket
        {
            Number = Text(500_000 + i),
            AccountId = r.Account,
            RequesterName = r.Account is null ? "Visita" : "Cliente",
            RequesterEmail = r.Account is null ? "visita@example.org" : "cliente@example.com",
            DepartmentId = r.Department,
            Subject = r.Subject,
            Status = r.Status,
            AssignedStaffId = r.Staff,
            ReplyToken = Guid.NewGuid().ToString("N")[..20],
            CreatedAt = now.AddMinutes(-i),
            LastMessageAt = now.AddMinutes(-i),
            UpdatedAt = now,
        }).ToList();
        db.AddRange(tickets);
        await db.SaveChangesAsync();
        return tickets.Select(t => t.Id).ToList();
    });

    /// <summary>Los elegidos de la lista, como los manda el formulario de las acciones masivas.</summary>
    private static Dictionary<string, string> Chosen(string action, params long[] ids)
    {
        var fields = ids.Select((id, i) => (Key: string.Create(CultureInfo.InvariantCulture, $"ids[{i}]"), Value: Text(id))).ToDictionary(f => f.Key, f => f.Value);
        fields["accion"] = action;
        return fields;
    }

    [TestMethod]
    public async Task Portal_AbreConAdjunto_RespondeYCierra_SinNotasInternas_LoAjenoDa404()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        var staff = await h.Staff("ana@example.com", StaffPermissions.SupportTickets);
        var department = await Department(h);
        var foreign = await Open(h, department, "Consulta ajena", h.AccountB);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();

        using var created = await c.SubmitFiles("/soporte/nuevo", "/soporte/nuevo",
            new() { ["DepartmentId"] = department.ToString(CultureInfo.InvariantCulture), ["Subject"] = "No anda el VPS", ["Message"] = "Se cayó a las 10.", ["Priority"] = "High" },
            ("Files", "captura.png", [1, 2, 3]));
        Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode);
        var ticket = await h.Query(db => db.Set<Ticket>().AsNoTracking().SingleAsync(t => t.AccountId == h.AccountA));
        Assert.AreEqual($"/soporte/{ticket.Id}?creada=1", Location(created));
        Assert.AreEqual((TicketPriority.High, "titular@example.com"), (ticket.Priority, ticket.RequesterEmail));
        var page = await c.Html(Location(created));
        StringAssert.Contains(page, "Se cayó a las 10.");
        StringAssert.Contains(page, "captura.png");
        StringAssert.Contains(page, "Mis tickets", "El menú clásico del portal lleva Soporte con sus ítems.");

        await h.Use<TicketService, long>(t => t.Reply(ticket.Id, new ReplyDraft("Nota secreta del staff", Internal: true), new Author(MessageAuthor.Staff, "Ana", null, MessageSource.Panel, StaffId: staff.Id)));
        Assert.IsFalse((await c.Html($"/soporte/{ticket.Id}")).Contains("Nota secreta", StringComparison.Ordinal));
        var file = await h.Query(db => db.Set<TicketAttachment>().Select(a => a.Id).SingleAsync());
        using (var download = await c.Get($"/soporte/{ticket.Id}?handler=File&file={file}"))
        {
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
        }

        using (var reply = await c.Submit($"/soporte/{ticket.Id}", $"/soporte/{ticket.Id}?handler=Reply", new() { ["Message"] = "Sigue caído." }))
        {
            Assert.AreEqual($"/soporte/{ticket.Id}#respuesta", Location(reply));
        }

        (await c.Submit($"/soporte/{ticket.Id}", $"/soporte/{ticket.Id}?handler=Close", [])).Dispose();
        Assert.AreEqual(TicketStatus.Closed, (await Get(h, ticket.Id)).Status);

        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get($"/soporte/{foreign.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get($"/soporte/{foreign.Id}?handler=File&file={file}")).StatusCode);
        var list = await c.Html("/soporte?ver=todas");
        StringAssert.Contains(list, "No anda el VPS");
        Assert.IsFalse(list.Contains("Consulta ajena", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Panel_NotaInterna_UnirPorNumero_YElMenuSoloConLoPermitido()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("soporte@example.com", StaffPermissions.SupportTickets);
        var department = await Department(h);
        var first = await Open(h, department, "Primero");
        var second = await Open(h, department, "Segundo");
        using var c = await UiTests.StaffSession(h, "soporte@example.com");

        var queue = await c.Html("/admin/soporte");
        StringAssert.Contains(queue, "Primero");
        StringAssert.Contains(queue, "Segundo");
        StringAssert.Contains(queue, "href=\"/admin/soporte/nuevo\"");
        Assert.IsFalse(queue.Contains("/admin/soporte/escalamiento", StringComparison.Ordinal), "Sin el permiso de configuración no se ofrece.");

        using (var note = await c.Submit($"/admin/soporte/{first.Id}", $"/admin/soporte/{first.Id}?handler=Reply", new() { ["Message"] = "Revisar el nodo.", ["Internal"] = "true" }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, note.StatusCode);
        }

        var last = await h.Query(db => db.Set<TicketMessage>().AsNoTracking().Where(m => m.TicketId == first.Id).OrderBy(m => m.Id).LastAsync());
        Assert.AreEqual((true, "Revisar el nodo."), (last.Internal, last.Body));

        var number = (await Get(h, first.Id)).Number;
        using (var merge = await c.Submit($"/admin/soporte/{second.Id}", $"/admin/soporte/{second.Id}?handler=Merge", new() { ["MergeInto"] = "#" + number }))
        {
            Assert.AreEqual($"/admin/soporte/{first.Id}", Location(merge));
        }

        using (var old = await c.Get($"/admin/soporte/{second.Id}"))
        {
            Assert.AreEqual($"/admin/soporte/{first.Id}", Location(old), "El unido lleva al que lo recibió.");
        }

        StringAssert.Contains(await c.Html($"/admin/soporte/{first.Id}"), "Segundo");
        Assert.IsFalse((await c.Html("/admin/soporte?estado=todos")).Contains("Segundo", StringComparison.Ordinal), "La cola no muestra los unidos.");
        StringAssert.Contains(await c.Html("/admin/soporte?q=%23" + (await Get(h, second.Id)).Number), "Segundo", "Buscado por su número, el unido aparece.");
        using (var unknown = await c.Submit($"/admin/soporte/{first.Id}", $"/admin/soporte/{first.Id}?handler=Merge", new() { ["MergeInto"] = "123" }))
        {
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "No hay un ticket con el número 123");
        }

        using var forbidden = await c.Get("/admin/soporte/escalamiento");
        StringAssert.Contains(Location(forbidden), "/estado/403");
    }

    [TestMethod]
    public async Task Encuesta_SinIniciarSesion_UnaRespuesta_YElReporteLaMuestra()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("jefe@example.com", StaffPermissions.SupportTickets, StaffPermissions.SupportSettings, ReportPermissions.Satisfaction);
        var department = await Department(h, configure: d => d.RequestFeedback = true);
        var opened = await Open(h, department, "Consulta de facturación");
        const string token = "fichadeprueba234567abcdef";
        await h.Query(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE support.tickets SET feedback_token = {token}, status = 'Closed' WHERE id = {opened.Id}"));
        const string url = "/soporte/encuesta/" + token;
        using var c = h.NewClient();

        StringAssert.Contains(await c.Html(url), "qué tan conforme quedaste");
        using (var empty = await c.Submit(url, url, new() { ["Comment"] = "Sin número" }))
        {
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "Elegí un número del 0 al 10");
        }

        using (var sent = await c.Submit(url, url, new() { ["Rating"] = "10", ["Comment"] = "Excelente atención" }))
        {
            StringAssert.Contains(await sent.Content.ReadAsStringAsync(), "Ya registramos tu respuesta");
        }

        StringAssert.Contains(await c.Html(url), "Ya respondiste esta encuesta");

        // Respondida, la página ya no tiene formulario: el token sale de otra, como un envío repetido del navegador.
        using (var again = await c.Submit("/ingresar", url, new() { ["Rating"] = "1" }))
        {
            StringAssert.Contains(await again.Content.ReadAsStringAsync(), "Ya respondiste esta encuesta");
        }

        Assert.AreEqual(10, await h.Query(db => db.Set<TicketFeedback>().Select(f => f.Rating).SingleAsync()));
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get("/soporte/encuesta/otraficha")).StatusCode);

        using var staff = await UiTests.StaffSession(h, "jefe@example.com");
        // La encuesta se ve en el reporte de satisfacción, en el grupo Soporte de Reportes.
        var report = await staff.Html("/admin/reportes/satisfaccion");
        StringAssert.Contains(report, "Excelente atención");
        StringAssert.Contains(report, "<data value=\"10\">10</data>");
        StringAssert.Contains(report, "href=\"/admin/reportes/satisfaccion\"", "El reporte cuelga de Reportes en el menú.");
        StringAssert.Contains(await staff.Html($"/admin/soporte/{opened.Id}"), "10 de 10");
    }

    [TestMethod]
    public async Task Configuracion_DepartamentoYReglaDesdeElPanel_LasCasillasSinMarcarQuedanEnFalso()
    {
        await using var h = await UiHarness.Start();
        var boss = await h.Staff("jefe@example.com", StaffPermissions.SupportTickets, StaffPermissions.SupportSettings);
        using var c = await UiTests.StaffSession(h, "jefe@example.com");
        StringAssert.Contains(await c.Html("/admin/soporte"), "/admin/soporte/escalamiento");

        var fields = new Dictionary<string, string> { ["Name"] = "Ventas", ["RequestFeedback"] = "true", ["ImapPort"] = "993", ["ImapSecurity"] = "SslOnConnect", ["SortOrder"] = "0" };
        using (var saved = await c.Submit("/admin/soporte/departamentos/0", "/admin/soporte/departamentos/0", fields))
        {
            Assert.AreEqual("/admin/soporte/departamentos", Location(saved));
        }

        var department = await h.Query(db => db.Set<Department>().AsNoTracking().SingleAsync(d => d.Name == "Ventas"));
        Assert.AreEqual((true, false), (department.RequestFeedback, department.AutoReply), "La confirmación sin marcar queda apagada.");

        using (var none = await c.Submit("/admin/soporte/escalamiento/0", "/admin/soporte/escalamiento/0", new() { ["Name"] = "Nada", ["AfterMinutes"] = "30" }))
        {
            StringAssert.Contains(await none.Content.ReadAsStringAsync(), "Elegí al menos una acción");
        }

        var rule = new Dictionary<string, string>
        {
            ["Name"] = "Urgentes",
            ["Enabled"] = "true",
            ["AfterMinutes"] = "30",
            ["Priorities"] = "Urgent",
            ["NewDepartmentId"] = department.Id.ToString(CultureInfo.InvariantCulture),
            ["Notify"] = "true",
            ["NotifyStaff"] = Text(boss.Id),
            ["Reply"] = "Seguimos con tu caso.",
        };
        using (var ok = await c.Submit("/admin/soporte/escalamiento/0", "/admin/soporte/escalamiento/0", rule))
        {
            Assert.AreEqual("/admin/soporte/escalamiento", Location(ok), $"{(int)ok.StatusCode} {Alert(await ok.Content.ReadAsStringAsync())}");
        }

        var saved2 = await h.Query(db => db.Set<EscalationRule>().AsNoTracking().SingleAsync());
        Assert.AreEqual((30, (long?)department.Id, true, true, "Seguimos con tu caso."), (saved2.AfterMinutes, saved2.NewDepartmentId, saved2.Notify, saved2.Enabled, saved2.Reply));
        CollectionAssert.AreEqual(new[] { TicketPriority.Urgent }, saved2.Priorities);
        CollectionAssert.AreEqual(new[] { boss.Id }, saved2.NotifyStaffIds);
        StringAssert.Contains(await c.Html("/admin/soporte/escalamiento"), "Urgentes");
        StringAssert.Contains(await c.Html($"/admin/soporte/escalamiento/{saved2.Id}"), "Seguimos con tu caso.");
        rule["NotifyStaff"] = "999999";
        using (var unknown = await c.Submit($"/admin/soporte/escalamiento/{saved2.Id}", $"/admin/soporte/escalamiento/{saved2.Id}", rule))
        {
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "ya no existen");
        }
    }

    /// <summary>El aviso de error de una página, para el mensaje de una prueba que falla.</summary>
    private static string Alert(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, "role=\"alert\">(.*?)</div>", System.Text.RegularExpressions.RegexOptions.Singleline) is { Success: true } m ? m.Groups[1].Value : html[..Math.Min(html.Length, 2000)];

    /// <summary>Los dos textos están en la página y el primero va antes.</summary>
    private static void Before(string html, string first, string second)
    {
        var (a, b) = (html.IndexOf(first, StringComparison.Ordinal), html.IndexOf(second, StringComparison.Ordinal));
        Assert.IsTrue(a >= 0 && b >= 0 && a < b, $"{first} tendría que ir antes que {second}.");
    }

    [TestMethod]
    public async Task Cola_FiltraOrdenaPaginaYExporta_YLaSolapaDeLaCuenta()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("soporte@example.com", StaffPermissions.SupportTickets, StaffPermissions.AccountsRead);
        var tecnica = await Department(h, "Técnica");
        var ventas = await Department(h, "Ventas");
        var rows = Enumerable.Range(0, QueueModel.Size + 1)
            .Select(i => (tecnica, string.Create(CultureInfo.InvariantCulture, $"Consulta {i:D2}"), (long?)h.AccountA, TicketStatus.Open, (long?)null)).ToList();
        rows.Add((ventas, "=HYPERLINK(\"x\")", null, TicketStatus.Open, null));
        rows.Add((tecnica, "Ya resuelta", h.AccountB, TicketStatus.Closed, null));
        await Seed(h, [.. rows]);
        using var c = await UiTests.StaffSession(h, "soporte@example.com");

        // Primero lo de movimiento más reciente, de a cincuenta, y Abiertos, el filtro de entrada, sin los cerrados.
        var first = await c.Html("/admin/soporte");
        StringAssert.Contains(first, "Mostrando 1 a 50 de 52");
        Before(first, "Consulta 00", "Consulta 01");
        Assert.IsFalse(first.Contains("Consulta 50", StringComparison.Ordinal) || first.Contains("Ya resuelta", StringComparison.Ordinal));
        StringAssert.Contains(first, "href=\"/admin/soporte?pagina=2\"");
        var second = await c.Html("/admin/soporte?pagina=2");
        StringAssert.Contains(second, "Mostrando 51 a 52 de 52");
        StringAssert.Contains(second, "Consulta 50");

        var descending = await c.Html(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte?departamento={tecnica}&orden=asunto&dir=desc"));
        Before(descending, "Consulta 50", "Consulta 49");
        Assert.IsFalse(descending.Contains("HYPERLINK", StringComparison.Ordinal), "El filtro de departamento deja afuera a Ventas.");
        StringAssert.Contains(descending, "aria-sort=\"descending\"><a class=\"bo-sort\" href=\"/admin/soporte?departamento=");
        var ascending = await c.Html(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte?departamento={tecnica}&orden=asunto"));
        Before(ascending, "Consulta 00", "Consulta 01");
        StringAssert.Contains(await c.Html(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte?departamento={ventas}")), "HYPERLINK");

        var closed = await c.Html("/admin/soporte?estado=cerrados");
        StringAssert.Contains(closed, "Ya resuelta");
        Assert.IsFalse(closed.Contains("Consulta 00", StringComparison.Ordinal));
        StringAssert.Contains(await c.Html("/admin/soporte?q=Consulta%2007"), "Mostrando 1 a 1 de 1");
        StringAssert.Contains(await c.Html("/admin/soporte?q=%23500003"), "Consulta 03");
        StringAssert.Contains(await c.Html("/admin/soporte?q=500052"), "Ya resuelta", "Un número busca en todos los estados.");
        StringAssert.Contains(await c.Html("/admin/soporte?asignado=yo"), "No hay tickets para mostrar.");

        // La exportación lleva todo lo filtrado, sin el paginado, y una celda que parece fórmula va con apóstrofo.
        using (var csv = await c.Get("/admin/soporte?formato=csv"))
        {
            Assert.AreEqual("text/csv", csv.Content.Headers.ContentType!.MediaType);
            var bytes = await csv.Content.ReadAsByteArrayAsync();
            CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), bytes[..3], "Con BOM, para que la planilla lea los acentos.");
            var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.HasCount(53, lines);
            StringAssert.StartsWith(lines[0], "numero,asunto,cuenta_numero,cuenta,solicitante,correo,area,prioridad,estado");
            Assert.IsTrue(lines.Any(l => l.StartsWith("500051,\"'=HYPERLINK(\"\"x\"\")\",,,Visita,visita@example.org,Ventas,Normal,Para responder,,", StringComparison.Ordinal)), lines[^1]);
        }

        // La solapa Tickets de la ficha del cliente es la misma lista con la cuenta fija.
        var tab = await c.Html(string.Create(CultureInfo.InvariantCulture, $"/admin/cuentas/{h.AccountA}/tickets"));
        StringAssert.Contains(tab, "Tickets de Cliente de prueba");
        StringAssert.Contains(tab, "Mostrando 1 a 50 de 51");
        Assert.IsFalse(tab.Contains("HYPERLINK", StringComparison.Ordinal));
        StringAssert.Contains(tab, string.Create(CultureInfo.InvariantCulture, $"href=\"/admin/cuentas/{h.AccountA}/tickets?pagina=2\""), "El paginado sigue en la solapa.");
        StringAssert.Contains(await c.Html(string.Create(CultureInfo.InvariantCulture, $"/admin/cuentas/{h.AccountB}/tickets?estado=todos")), "Ya resuelta");
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get(string.Create(CultureInfo.InvariantCulture, $"/admin/cuentas/{h.AccountB + 1000}/tickets"))).StatusCode);
        StringAssert.Contains(await c.Html(string.Create(CultureInfo.InvariantCulture, $"/admin/cuentas/{h.AccountA}")), string.Create(CultureInfo.InvariantCulture, $"href=\"/admin/cuentas/{h.AccountA}/tickets\""));
    }

    [TestMethod]
    public async Task Cola_AccionesMasivas_PidenConfirmar_YSoloCambianLoQueCorresponde()
    {
        await using var h = await UiHarness.Start();
        var me = await h.Staff("soporte@example.com", StaffPermissions.SupportTickets);
        var department = await Department(h);
        var ids = await Seed(h, (department, "Ya es mío", h.AccountA, TicketStatus.Open, me.Id), (department, "Sin asignar", h.AccountA, TicketStatus.Open, null),
            (department, "Ya cerrado", h.AccountA, TicketStatus.Closed, null));
        using var c = await UiTests.StaffSession(h, "soporte@example.com");
        const string List = "/admin/soporte?estado=todos";

        using (var confirm = await c.Submit(List, List + "&handler=Bulk", Chosen(QueueModel.AssignToMe, [.. ids])))
        {
            var html = await confirm.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "¿Asignarte 2 tickets?");
            StringAssert.Contains(html, $"type=\"hidden\" name=\"ids\" value=\"{ids[1]}\"");
            Assert.IsFalse(html.Contains($"type=\"hidden\" name=\"ids\" value=\"{ids[0]}\"", StringComparison.Ordinal), "El que ya es mío no cambia.");
        }

        Assert.IsNull((await Get(h, ids[1])).AssignedStaffId, "El primer paso no cambia nada.");
        using (var done = await c.Submit(List, List + "&handler=BulkConfirm", Chosen(QueueModel.AssignToMe, [.. ids])))
        {
            Assert.AreEqual("/admin/soporte?estado=todos&hechos=2", Location(done));
            StringAssert.Contains(await c.Html(Location(done)), "Listo: cambiaron 2 tickets.");
        }

        Assert.IsTrue((await h.Query(db => db.Set<Ticket>().AsNoTracking().ToListAsync())).All(t => t.AssignedStaffId == me.Id));
        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "support.ticket.update")), "Cada cambio queda en la auditoría.");

        using (var close = await c.Submit(List, List + "&handler=BulkConfirm", Chosen(QueueModel.CloseThem, [.. ids])))
        {
            Assert.AreEqual("/admin/soporte?estado=todos&hechos=2", Location(close));
        }

        Assert.IsTrue((await h.Query(db => db.Set<Ticket>().AsNoTracking().ToListAsync())).All(t => t.Status == TicketStatus.Closed));
        Assert.AreEqual(2, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.SupportNotify)), "Cada cierre deja su aviso, el que pide la encuesta.");
        using (var nothing = await c.Submit(List, List + "&handler=Bulk", Chosen(QueueModel.CloseThem, [.. ids])))
        {
            StringAssert.Contains(await nothing.Content.ReadAsStringAsync(), "A lo elegido no le cambia nada esa acción.");
        }

        using var unknown = await c.Submit(List, List + "&handler=BulkConfirm", Chosen("borrar", ids[0]));
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [TestMethod]
    public async Task Cola_NuevosYConRespuestaDelCliente_PrioridadYPersona_YLasAccionesDeSpamYUnion()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("soporte@example.com", StaffPermissions.SupportTickets, StaffPermissions.SupportSettings);
        await h.Staff("agente@example.com", StaffPermissions.SupportTickets);
        var other = await h.Staff("otra@example.com", StaffPermissions.SupportTickets);
        var department = await Department(h);
        var ids = await Seed(h, (department, "Nuevo de verdad", h.AccountA, TicketStatus.Open, null), (department, "Volvió a escribir", h.AccountA, TicketStatus.Open, other.Id),
            (department, "Spam uno", null, TicketStatus.Open, null), (department, "Spam dos", null, TicketStatus.Open, null));
        await h.Query(async db =>
        {
            // Una nota interna no cuenta como respuesta: el cliente no la vio.
            db.Add(new TicketMessage { TicketId = ids[0], Author = MessageAuthor.Staff, AuthorName = "Ana", Body = "Revisar.", Internal = true, Source = MessageSource.Panel, CreatedAt = DateTimeOffset.UtcNow });
            db.Add(new TicketMessage { TicketId = ids[1], Author = MessageAuthor.Staff, AuthorName = "Ana", Body = "Probá ahora.", Source = MessageSource.Panel, CreatedAt = DateTimeOffset.UtcNow });
            await db.Set<Ticket>().Where(t => t.Id == ids[1]).ExecuteUpdateAsync(u => u.SetProperty(t => t.Priority, TicketPriority.Urgent));
            return await db.SaveChangesAsync();
        });
        using var c = await UiTests.StaffSession(h, "soporte@example.com");

        var fresh = await c.Html("/admin/soporte?estado=nuevos");
        StringAssert.Contains(fresh, "Nuevo de verdad");
        Assert.IsFalse(fresh.Contains("Volvió a escribir", StringComparison.Ordinal));
        var replied = await c.Html("/admin/soporte?estado=respuesta-del-cliente");
        StringAssert.Contains(replied, "Volvió a escribir");
        Assert.IsFalse(replied.Contains("Nuevo de verdad", StringComparison.Ordinal));
        StringAssert.Contains(replied, "respuesta del cliente");
        foreach (var only in new[] { $"/admin/soporte?asignado={other.Id}", "/admin/soporte?prioridad=Urgent" })
        {
            var html = await c.Html(only);
            StringAssert.Contains(html, "Volvió a escribir");
            Assert.IsFalse(html.Contains("Nuevo de verdad", StringComparison.Ordinal), only);
        }

        // Spam: bloquea a quien escribió sin cuenta y cierra. Los de una cuenta quedan afuera.
        const string List = "/admin/soporte?estado=todos";
        using (var confirm = await c.Submit(List, List + "&handler=Bulk", Chosen(QueueModel.SpamThem, ids[0], ids[2], ids[3])))
        {
            var html = await confirm.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "¿Bloquear al remitente y cerrar 2 tickets?");
            StringAssert.Contains(html, "visita@example.org");
            Assert.IsFalse(html.Contains($"type=\"hidden\" name=\"ids\" value=\"{ids[0]}\"", StringComparison.Ordinal), "El de una cuenta no se bloquea.");
        }

        using (var done = await c.Submit(List, List + "&handler=BulkConfirm", Chosen(QueueModel.SpamThem, ids[0], ids[2], ids[3])))
        {
            Assert.AreEqual("/admin/soporte?estado=todos&hechos=2", Location(done));
        }

        var filter = await h.Query(db => db.Set<MailFilter>().AsNoTracking().SingleAsync());
        Assert.AreEqual((MailFilterKind.Sender, "visita@example.org"), (filter.Kind, filter.Value));
        Assert.AreEqual((TicketStatus.Open, TicketStatus.Closed, TicketStatus.Closed), ((await Get(h, ids[0])).Status, (await Get(h, ids[2])).Status, (await Get(h, ids[3])).Status));

        // Unir: al más antiguo de lo elegido, que en la semilla es el segundo.
        using (var confirm = await c.Submit(List, List + "&handler=Bulk", Chosen(QueueModel.MergeThem, ids[0], ids[1])))
        {
            StringAssert.Contains(await confirm.Content.ReadAsStringAsync(), "¿Unir 1 tickets al N.º 500001?");
        }

        using (var merged = await c.Submit(List, List + "&handler=BulkConfirm", Chosen(QueueModel.MergeThem, ids[0], ids[1])))
        {
            Assert.AreEqual("/admin/soporte?estado=todos&hechos=1", Location(merged));
        }

        Assert.AreEqual((long?)ids[1], (await Get(h, ids[0])).MergedIntoId);
        using (var alone = await c.Submit(List, List + "&handler=Bulk", Chosen(QueueModel.MergeThem, ids[1])))
        {
            StringAssert.Contains(await alone.Content.ReadAsStringAsync(), "A lo elegido no le cambia nada esa acción.", "Para unir hacen falta dos.");
        }

        // Bloquear remitentes es del control de spam: sin permiso de configuración ni se ofrece ni se aplica.
        using var agent = await UiTests.StaffSession(h, "agente@example.com");
        Assert.IsFalse((await agent.Html(List)).Contains($"value=\"{QueueModel.SpamThem}\"", StringComparison.Ordinal));
        using var denied = await agent.Submit(List, List + "&handler=BulkConfirm", Chosen(QueueModel.SpamThem, ids[0]));
        Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
        StringAssert.Contains(Location(denied), "/estado/403");
        Assert.AreEqual(1, await h.Query(db => db.Set<MailFilter>().CountAsync()), "No sumó otro filtro.");
    }

    [TestMethod]
    public async Task Resumen_CuentaCadaVistaDeLaCola_LoQueMasEspera_YPorDepartamentoYPersona()
    {
        await using var h = await UiHarness.Start();
        var me = await h.Staff("soporte@example.com", StaffPermissions.SupportTickets);
        await h.Staff("otro@example.com", StaffPermissions.AccountsRead);
        var department = await Department(h, "Técnica");
        var ids = await Seed(h, (department, "Nuevo sin leer", h.AccountA, TicketStatus.Open, null), (department, "Volvió a escribir", h.AccountA, TicketStatus.Open, me.Id),
            (department, "Lo estoy viendo", h.AccountA, TicketStatus.InProgress, me.Id), (department, "Respondido", h.AccountA, TicketStatus.AwaitingCustomer, null),
            (department, "Espera al proveedor", h.AccountA, TicketStatus.OnHold, null), (department, "Ya cerrado", h.AccountA, TicketStatus.Closed, null));
        await h.Query(async db =>
        {
            db.Add(new TicketMessage { TicketId = ids[1], Author = MessageAuthor.Staff, AuthorName = "Ana", Body = "Probá ahora.", Source = MessageSource.Panel, CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        using var c = await UiTests.StaffSession(h, "soporte@example.com");

        var html = await c.Html("/admin/soporte/resumen");

        foreach (var (view, count) in new[] { ("estado=nuevos", 1), ("estado=respuesta-del-cliente", 1), ("estado=en-curso", 1), ("estado=esperando-al-cliente", 1), ("estado=en-pausa", 1), ("asignado=yo", 2) })
        {
            StringAssert.Matches(html, new Regex($"href=\"/admin/soporte\\?{view}\"><strong data-tone=\"[a-z]+\">{count}</strong>"), view);
        }

        // Lo que más espera: lo que espera al staff, del último mensaje más viejo al más nuevo.
        var waiting = html[html.IndexOf("Lo que más espera", StringComparison.Ordinal)..html.IndexOf("Tickets por departamento", StringComparison.Ordinal)];
        Assert.IsTrue(waiting.IndexOf("Lo estoy viendo", StringComparison.Ordinal) < waiting.IndexOf("Nuevo sin leer", StringComparison.Ordinal));
        Assert.IsFalse(waiting.Contains("Respondido", StringComparison.Ordinal), "Lo que espera al cliente no espera al staff.");
        StringAssert.Contains(html, $"href=\"/admin/soporte?departamento={department}\">Técnica</a>");
        StringAssert.Contains(html, $"href=\"/admin/soporte?asignado={me.Id}\">");
        Assert.IsFalse(html.Contains("/admin/reportes/soporte", StringComparison.Ordinal), "Sin el permiso del reporte no ofrece el enlace.");

        using var other = await UiTests.StaffSession(h, "otro@example.com");
        using var denied = await other.Get("/admin/soporte/resumen");
        Assert.AreNotEqual(HttpStatusCode.OK, denied.StatusCode);
    }

    [TestMethod]
    public async Task Ajustes_SeGuardanEnLaBase_ValenEnSeguida_YDosALaVezNoSePisan()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("jefe@example.com", StaffPermissions.SupportTickets, StaffPermissions.SupportSettings);
        var department = await Department(h);
        using var c = await UiTests.StaffSession(h, "jefe@example.com");
        const string Page = "/admin/soporte/ajustes";
        StringAssert.Contains(await c.Html(Page), "pdf, png, jpg", "Sin guardar, los de la configuración.");
        StringAssert.Contains(await c.Html("/admin/soporte"), Page, "Cuelga del menú Configuración.");

        var fields = new Dictionary<string, string>
        {
            ["Version"] = "0",
            ["AllowedExtensions"] = "txt, LOG",
            ["MaxAttachmentMb"] = "2",
            ["MaxAttachments"] = "3",
            ["AutoCloseHours"] = "120",
            ["MaxEmailMb"] = "10",
            ["MaxEmailsPerSenderPerHour"] = "20",
        };
        using (var bad = await c.Submit(Page, Page, new(fields) { ["MaxAttachments"] = "20", ["AllowedExtensions"] = "p d f!" }))
        {
            var html = await bad.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "Los adjuntos por mensaje van de 1 a 10");
            StringAssert.Contains(html, "Escribí entre 1 y 30 extensiones");
        }

        using (var ok = await c.Submit(Page, Page, new(fields)))
        {
            Assert.AreEqual(Page + "?guardado=true", Location(ok));
        }

        var saved = await h.Use<SupportSettings, SupportOptions>(s => s.Get());
        Assert.AreEqual((".txt,.log", 2L * 1024 * 1024, 3, (TimeSpan?)TimeSpan.FromHours(120), 10L * 1024 * 1024, 20, false),
            (saved.AllowedExtensions, saved.MaxAttachmentBytes, saved.MaxAttachments, saved.AutoCloseAfter, saved.MaxEmailBytes, saved.MaxEmailsPerSenderPerHour, saved.EmailCc),
            "La casilla de las copias sin marcar queda en falso.");
        StringAssert.Contains(await c.Html(Page), "value=\"120\"");

        // Valen en seguida para el núcleo: un PDF ya no se acepta.
        var customer = new Author(MessageAuthor.Customer, "Cliente", "cliente@example.com", MessageSource.Portal);
        var ex = await Assert.ThrowsAsync<BillingOss.App.UseCases.ValidationException>(() =>
            h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, "Con PDF", "Hola.", h.AccountA, Attachments: [new NewAttachment("factura.pdf", [1])]), customer)));
        StringAssert.Contains(ex.Message, ".txt, .log");

        // Quien editaba la versión vieja no pisa lo guardado.
        using (var stale = await c.Submit(Page, Page, new(fields) { ["MaxAttachments"] = "1" }))
        {
            StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "Otra persona cambió los ajustes");
        }

        Assert.AreEqual(3, (await h.Use<SupportSettings, SupportOptions>(s => s.Get())).MaxAttachments);
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "support.settings")));
    }

    [TestMethod]
    public async Task RespuestasPredefinidas_SeGuardanEditanUsanYBorran()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("jefe@example.com", StaffPermissions.SupportTickets, StaffPermissions.SupportSettings);
        await h.Staff("agente@example.com", StaffPermissions.SupportTickets);
        var department = await Department(h);
        var ticket = await Open(h, department, "Consulta");
        using var c = await UiTests.StaffSession(h, "jefe@example.com");
        const string Page = "/admin/soporte/respuestas";

        using (var empty = await c.Submit(Page, Page + "?id=0", new() { ["Name"] = " ", ["Body"] = string.Empty }))
        {
            var html = await empty.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "Escribí el nombre, de hasta 200 caracteres.");
            StringAssert.Contains(html, "Escribí el mensaje.");
        }

        using (var saved = await c.Submit(Page, Page + "?id=0", new() { ["Name"] = "Saludo", ["Category"] = "General", ["Body"] = "Hola, gracias por escribir." }))
        {
            Assert.AreEqual(Page, Location(saved));
        }

        var id = await h.Query(db => db.Set<SavedReply>().Select(r => r.Id).SingleAsync());
        var edit = string.Create(CultureInfo.InvariantCulture, $"{Page}?id={id}");
        StringAssert.Contains(await c.Html(edit), "Hola, gracias por escribir.</textarea>");
        (await c.Submit(edit, edit, new() { ["Name"] = "Saludo", ["Category"] = "General", ["Body"] = "Hola, ya lo estamos mirando." })).Dispose();
        var reply = await h.Query(db => db.Set<SavedReply>().AsNoTracking().SingleAsync());
        Assert.AreEqual(("Saludo", "General", "Hola, ya lo estamos mirando."), (reply.Name, reply.Category, reply.Body));

        // Quien atiende la usa al responder, pero no la edita: eso es de configuración.
        using var agent = await UiTests.StaffSession(h, "agente@example.com");
        var page = await agent.Html($"/admin/soporte/{ticket.Id}");
        StringAssert.Contains(page, "General · Saludo");
        Assert.IsFalse(page.Contains("href=\"/admin/soporte/respuestas\"", StringComparison.Ordinal));
        StringAssert.Contains(await agent.Html(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/{ticket.Id}?respuesta={id}")), "Hola, ya lo estamos mirando.</textarea>");
        using (var forbidden = await agent.Get(Page))
        {
            StringAssert.Contains(Location(forbidden), "/estado/403");
        }

        using (var removed = await c.Submit(Page, Page + "?handler=Delete", new() { ["remove"] = Text(id) }))
        {
            Assert.AreEqual(Page, Location(removed));
        }

        Assert.AreEqual(0, await h.Query(db => db.Set<SavedReply>().CountAsync()));
        StringAssert.Contains(await c.Html(Page), "Todavía no hay respuestas predefinidas.");
    }

    [TestMethod]
    public async Task AbrirDesdeElPanel_PorUnaCuentaOPorQuienNoEsCliente_YCambiarlo()
    {
        await using var h = await UiHarness.Start();
        var me = await h.Staff("soporte@example.com", StaffPermissions.SupportTickets);
        var department = await Department(h);
        var ventas = await Department(h, "Ventas");
        using var c = await UiTests.StaffSession(h, "soporte@example.com");
        var page = string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/nuevo?cuenta={h.AccountA}");
        StringAssert.Contains(await c.Html(page), "Cliente de prueba");

        using (var created = await c.Submit(page, page, new() { ["DepartmentId"] = Text(department), ["Subject"] = "Llamó por el VPS", ["Message"] = "Pide que revisemos el disco.", ["Priority"] = "High" }))
        {
            var opened = await h.Query(db => db.Set<Ticket>().AsNoTracking().SingleAsync());
            Assert.AreEqual($"/admin/soporte/{opened.Id}", Location(created));
            Assert.AreEqual((h.AccountA, "cliente@example.com", TicketStatus.AwaitingCustomer, me.Id, TicketPriority.High),
                (opened.AccountId!.Value, opened.RequesterEmail, opened.Status, opened.AssignedStaffId!.Value, opened.Priority), "Abierto por el staff, espera al cliente y es de quien lo abrió.");
        }

        using (var noEmail = await c.Submit("/admin/soporte/nuevo", "/admin/soporte/nuevo", new() { ["DepartmentId"] = Text(department), ["Subject"] = "Consulta", ["Message"] = "Hola." }))
        {
            StringAssert.Contains(await noEmail.Content.ReadAsStringAsync(), "Sin cuenta, escribí un correo válido para responderle.");
        }

        using (var guest = await c.Submit("/admin/soporte/nuevo", "/admin/soporte/nuevo",
            new() { ["DepartmentId"] = Text(department), ["RequesterName"] = "Visita", ["RequesterEmail"] = "visita@example.org", ["Subject"] = "Quiere contratar", ["Message"] = "Llamó al 0800." }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, guest.StatusCode);
        }

        var noAccount = await h.Query(db => db.Set<Ticket>().AsNoTracking().SingleAsync(t => t.AccountId == null));
        Assert.AreEqual(("Visita", "visita@example.org"), (noAccount.RequesterName, noAccount.RequesterEmail));

        // La cuenta también se encuentra por su correo, y el staff suma copias y elige no avisar ahora.
        const string ByEmail = "/admin/soporte/nuevo?cuenta=Cliente@Example.com";
        StringAssert.Contains(await c.Html(ByEmail), "Cliente de prueba");
        using (var bad = await c.Submit(ByEmail, ByEmail, new() { ["DepartmentId"] = Text(department), ["Subject"] = "Copias", ["Message"] = "Hola.", ["Cc"] = "socio@example.com, no-es-un-correo" }))
        {
            StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "Revisá las copias");
        }

        using (var quiet = await c.Submit(ByEmail, ByEmail,
            new() { ["DepartmentId"] = Text(department), ["Subject"] = "Sin aviso", ["Message"] = "Lo dejamos anotado.", ["Cc"] = "socio@example.com; contador@example.com", ["Quiet"] = "true" }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, quiet.StatusCode);
        }

        var silent = await h.Query(db => db.Set<Ticket>().AsNoTracking().SingleAsync(t => t.Subject == "Sin aviso"));
        Assert.AreEqual((long?)h.AccountA, silent.AccountId);
        CollectionAssert.AreEqual(new[] { "socio@example.com", "contador@example.com" }, silent.Cc);
        var payload = await h.Query(db => db.Jobs.AsNoTracking().Where(j => j.Kind == JobKinds.SupportNotify && j.EntityKey == $"ticket:{silent.Id}").Select(j => j.Payload).SingleAsync());
        using (var json = System.Text.Json.JsonDocument.Parse(payload))
        {
            Assert.IsTrue(json.RootElement.GetProperty("quiet").GetBoolean(), "El aviso sabe que no tiene que salir.");
        }

        using (var unknown = await c.Submit("/admin/soporte/nuevo?cuenta=nadie@example.com", "/admin/soporte/nuevo?cuenta=nadie@example.com",
            new() { ["DepartmentId"] = Text(department), ["Subject"] = "X", ["Message"] = "Y." }))
        {
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "No encontramos una sola cuenta");
        }

        var ticket = string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/{noAccount.Id}");
        using (var changed = await c.Submit(ticket, ticket + "?handler=Update",
            new() { ["Status"] = "OnHold", ["Priority"] = "Urgent", ["DepartmentId"] = Text(ventas), ["AssignedStaffId"] = "0" }))
        {
            Assert.AreEqual(ticket, Location(changed));
        }

        var after = await Get(h, noAccount.Id);
        Assert.AreEqual((TicketStatus.OnHold, TicketPriority.Urgent, ventas, (long?)null), (after.Status, after.Priority, after.DepartmentId, after.AssignedStaffId));
    }

    [TestMethod]
    public async Task CorreoEntrante_ElRegistroConBusquedaYPaginas_YElControlDeSpamDesdeElPanel()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("jefe@example.com", StaffPermissions.SupportTickets, StaffPermissions.SupportSettings);
        var department = await Department(h);
        await h.Query(async db =>
        {
            db.Add(new InboundEmail
            {
                DepartmentId = department,
                MessageKey = "uno@robot.example.org",
                Sender = "robot@example.org",
                Subject = "Oferta imperdible",
                Outcome = InboundOutcome.Rejected,
                Reason = "Filtro de remitente: example.org",
                ReceivedAt = DateTimeOffset.UtcNow,
            });

            // Más de una página de correos abiertos, del más nuevo al más viejo.
            for (var i = 1; i <= 55; i++)
            {
                db.Add(new InboundEmail { DepartmentId = department, MessageKey = $"m{i}@cliente.example", Sender = "cliente@example.com", Subject = $"Consulta {i:00}", Outcome = InboundOutcome.Opened, ReceivedAt = DateTimeOffset.UtcNow });
            }

            return await db.SaveChangesAsync();
        });
        using var c = await UiTests.StaffSession(h, "jefe@example.com");
        const string Log = "/admin/registros/correo-entrante";

        var first = await c.Html(Log);
        StringAssert.Contains(first, "Consulta 55");
        Assert.IsFalse(first.Contains("Oferta imperdible", StringComparison.Ordinal), "El más viejo queda en la página siguiente.");
        StringAssert.Contains(first, "1 a 50 de 56");
        StringAssert.Contains(await c.Html(Log + "?pagina=2"), "Oferta imperdible");
        var rejected = await c.Html(Log + "?resultado=Rejected");
        StringAssert.Contains(rejected, "Filtro de remitente: example.org");
        Assert.IsFalse(rejected.Contains("Consulta 01", StringComparison.Ordinal));
        var search = await c.Html(Log + "?q=robot");
        StringAssert.Contains(search, "Oferta imperdible");
        Assert.IsFalse(search.Contains("Consulta 01", StringComparison.Ordinal));

        // Bloquear al remitente desde el registro suma un filtro del control de spam, una sola vez.
        using (var blocked = await c.Submit(Log + "?q=robot", Log + "?q=robot&handler=Block", new() { ["sender"] = "Robot@Example.org" }))
        {
            Assert.AreEqual(Log + "?hechos=1&q=robot", Location(blocked));
        }

        using (var again = await c.Submit(Log, Log + "?handler=Block", new() { ["sender"] = "robot@example.org" }))
        {
            Assert.AreEqual(Log + "?hechos=0", Location(again));
        }

        var block = await h.Query(db => db.Set<MailFilter>().AsNoTracking().SingleAsync());
        Assert.AreEqual((MailFilterKind.Sender, "robot@example.org"), (block.Kind, block.Value));
        Assert.IsFalse((await c.Html(Log + "?q=robot")).Contains("Bloquear remitente", StringComparison.Ordinal), "Ya bloqueado no se ofrece de nuevo.");

        const string Spam = "/admin/soporte/correo";
        StringAssert.Contains(await c.Html(Spam), "robot@example.org");
        using (var empty = await c.Submit(Spam, Spam + "?handler=Add", new() { ["Kind"] = "Sender", ["Value"] = " " }))
        {
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "Escribí el valor del filtro.");
        }

        using (var added = await c.Submit(Spam, Spam + "?handler=Add", new() { ["Kind"] = "Subject", ["Value"] = " Oferta " }))
        {
            Assert.AreEqual(Spam, Location(added));
        }

        var filter = await h.Query(db => db.Set<MailFilter>().AsNoTracking().SingleAsync(f => f.Kind == MailFilterKind.Subject));
        Assert.AreEqual("Oferta", filter.Value);
        (await c.Submit(Spam, Spam + "?handler=Delete", new() { ["remove"] = Text(filter.Id) })).Dispose();
        Assert.AreEqual(1, await h.Query(db => db.Set<MailFilter>().CountAsync()));
        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "support.mail_filter.add")));
    }

    [TestMethod]
    public async Task Casilla_LaClaveNoVuelveAlNavegador_ProbarNoGuarda_YOtroServidorLaPide()
    {
        await using var imap = new FakeImapServer("soporte@hosting.example", "clave-de-la-casilla-1");
        await using var h = await UiHarness.Start();
        await h.Staff("jefe@example.com", StaffPermissions.SupportTickets, StaffPermissions.SupportSettings);
        using var c = await UiTests.StaffSession(h, "jefe@example.com");
        const string New = "/admin/soporte/departamentos/0";
        var fields = new Dictionary<string, string>
        {
            ["Name"] = "Técnica",
            ["MailEnabled"] = "true",
            ["ImapHost"] = "127.0.0.1",
            ["ImapPort"] = Text(imap.Port),
            ["ImapSecurity"] = "None",
            ["ImapUser"] = imap.User,
            ["ImapPassword"] = imap.Password,
            ["SortOrder"] = "0",
        };

        using (var test = await c.Submit(New, New + "?handler=Test", new(fields)))
        {
            StringAssert.Contains(await test.Content.ReadAsStringAsync(), "La casilla responde: tiene 0 correos.");
        }

        Assert.AreEqual(0, await h.Query(db => db.Set<Department>().CountAsync()), "Probar no guarda nada.");
        (await c.Submit(New, New, new(fields))).Dispose();
        var saved = await h.Query(db => db.Set<Department>().AsNoTracking().SingleAsync());
        Assert.IsNotNull(saved.ImapPassword);
        Assert.IsFalse(Encoding.UTF8.GetString(saved.ImapPassword).Contains(imap.Password, StringComparison.Ordinal), "La contraseña va cifrada.");
        var edit = string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/departamentos/{saved.Id}");
        Assert.IsFalse((await c.Html(edit)).Contains(imap.Password, StringComparison.Ordinal), "La contraseña nunca vuelve al navegador.");

        // Vacía, probar usa la guardada mientras el servidor (host, puerto y cifrado) sea el mismo. Si no, hay que cargarla.
        fields["ImapPassword"] = string.Empty;
        using (var again = await c.Submit(edit, edit + "?handler=Test", new(fields)))
        {
            StringAssert.Contains(await again.Content.ReadAsStringAsync(), "La casilla responde: tiene 0 correos.");
        }

        fields["ImapPort"] = Text(imap.Port + 1);
        using (var otherPort = await c.Submit(edit, edit + "?handler=Test", new(fields)))
        {
            StringAssert.Contains(await otherPort.Content.ReadAsStringAsync(), "Para probar hacen falta el servidor, el usuario y la contraseña.");
        }

        fields["ImapPort"] = Text(imap.Port);
        fields["ImapHost"] = "localhost";
        using (var other = await c.Submit(edit, edit, new(fields)))
        {
            StringAssert.Contains(await other.Content.ReadAsStringAsync(), "Cargá la contraseña de la casilla: es nueva o cambió el servidor.");
        }

        Assert.AreEqual("127.0.0.1", (await h.Query(db => db.Set<Department>().AsNoTracking().SingleAsync())).ImapHost);
    }

    [TestMethod]
    public async Task ReporteDeSoporte_PorDepartamento_ConLaPrimeraRespuesta()
    {
        await using var h = await UiHarness.Start();
        var staff = await h.Staff("jefe@example.com", StaffPermissions.SupportTickets, ReportPermissions.Support);
        var department = await Department(h, "Técnica");
        var answered = await Open(h, department, "Con respuesta");
        await Open(h, department, "Sin respuesta");
        await h.Use<TicketService, long>(t => t.Reply(answered.Id, new ReplyDraft("Solo para el equipo.", Internal: true), new Author(MessageAuthor.Staff, "Ana", null, MessageSource.Panel, StaffId: staff.Id)));
        await h.Use<TicketService, long>(t => t.Reply(answered.Id, new ReplyDraft("Ya está."), new Author(MessageAuthor.Staff, "Ana", null, MessageSource.Panel, StaffId: staff.Id)));
        using var c = await UiTests.StaffSession(h, "jefe@example.com");

        // El reporte de Reportes lee los tickets del soporte: abiertos, con la primera respuesta que ve el cliente y sin ella.
        var report = Regex.Replace(await c.Html("/admin/reportes/soporte"), @"\s+", " ");
        StringAssert.Contains(report, "<td>Técnica</td> <td class=\"num\"><data value=\"2\">2</data></td> <td class=\"num\"><data value=\"1\">1</data></td> <td class=\"num\"><data value=\"1\">1</data></td>");
        StringAssert.Contains(await c.Html("/admin/soporte/resumen"), "Ver el reporte de soporte", "El resumen enlaza al reporte.");
    }
}
