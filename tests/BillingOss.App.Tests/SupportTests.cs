using System.Text.Json;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using BillingOss.App.UseCases;
using BillingOss.Domain.Shared;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Soporte (sección 9 del modelo de dominio): tickets, mensajes, adjuntos, estados, asignación y avisos.</summary>
[TestClass]
public sealed partial class SupportTests
{
    public static Author Customer { get; } = new(MessageAuthor.Customer, "Cliente de prueba", "cliente@example.com", MessageSource.Portal);

    public static Author StaffAuthor(long id) => new(MessageAuthor.Staff, "Ana del staff", "ana@hosting.example", MessageSource.Panel, StaffId: id);

    internal static Task<CoreHarness> Start(Action<SupportOptions>? support = null, SecretBox? secrets = null, Action<NotificationOptions>? notifications = null) =>
        CoreHarness.Start(secrets, notifications: notifications, services: s => s.Configure<SupportOptions>(o => support?.Invoke(o)));

    internal static Task<long> Department(CoreHarness h, string name = "Soporte técnico", Action<Department>? configure = null) => h.Query(async db =>
    {
        var department = new Department { Name = name, Email = "soporte@hosting.example", StaffAlertEmail = "equipo@hosting.example", CreatedAt = h.Clock.Now };
        configure?.Invoke(department);
        db.Add(department);
        await db.SaveChangesAsync();
        return department.Id;
    });

    internal static Task<long> Staff(CoreHarness h, string email = "ana@hosting.example") => h.Query(async db =>
    {
        var staff = new StaffUser { Email = email, NormalizedEmail = email.ToUpperInvariant(), Name = "Ana del staff", Permissions = [StaffPermissions.SupportTickets], CreatedAt = h.Clock.Now };
        db.Add(staff);
        await db.SaveChangesAsync();
        return staff.Id;
    });

    internal static Task<OpenedTicket> Open(CoreHarness h, long department, IReadOnlyList<NewAttachment>? files = null, IReadOnlyList<string>? cc = null, string? reference = null) =>
        h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, "No anda el VPS", "Se cayó a las 10.", h.AccountId, Attachments: files, Cc: cc, ClientReference: reference), Customer));

    internal static Task<long> Reply(CoreHarness h, long ticket, ReplyDraft draft, Author author) =>
        h.Use<TicketService, long>(t => t.Reply(ticket, draft, author));

    internal static Task<Ticket> Get(CoreHarness h, long id) => h.Query(db => db.Set<Ticket>().AsNoTracking().SingleAsync(t => t.Id == id));

    internal static Task<List<OutboundEmail>> Emails(CoreHarness h, string? template = null) =>
        h.Query(db => db.Set<OutboundEmail>().AsNoTracking().Where(e => template == null || e.Template == template).OrderBy(e => e.Id).ToListAsync());

    /// <summary>El HTML del aviso con las entidades resueltas: Fluid codifica los acentos de lo que recibe.</summary>
    internal static string Text(OutboundEmail email) => System.Net.WebUtility.HtmlDecode(email.Html);

    private static Dictionary<string, string> Headers(OutboundEmail email) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(email.Headers ?? "{}")!;

    [TestMethod]
    public async Task AbrirDesdeElPortal_QuedaAbiertoConNumeroPublicoYAvisaAlClienteYAlEquipo()
    {
        await using var h = await Start();
        var department = await Department(h);

        var opened = await Open(h, department, [new NewAttachment("captura.png", [1, 2, 3])]);

        var ticket = await Get(h, opened.Id);
        Assert.AreEqual(TicketStatus.Open, ticket.Status);
        Assert.AreEqual(h.AccountId, ticket.AccountId);
        Assert.AreEqual("cliente@example.com", ticket.RequesterEmail);
        StringAssert.Matches(ticket.Number, new Regex("^[0-9]{6}$"));
        Assert.AreEqual(20, ticket.ReplyToken.Length);
        var file = await h.Query(db => db.Set<TicketAttachment>().SingleAsync());
        Assert.AreEqual(("captura.png", "image/png", 3L), (file.FileName, file.ContentType, file.Size));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await h.Query(db => db.Set<AttachmentContent>().Select(c => c.Content).SingleAsync()));

        await h.RunJobs();

        var emails = await Emails(h);
        var confirmation = emails.Single(e => e.Template == TemplateKeys.TicketOpened);
        Assert.AreEqual("cliente@example.com", confirmation.Recipient);
        Assert.Contains($"[#{ticket.Number}]", confirmation.Subject);
        Assert.Contains("Se cayó a las 10.", Text(confirmation));
        Assert.Contains("captura.png", confirmation.Html);
        var headers = Headers(confirmation);
        Assert.AreEqual("soporte@hosting.example", headers["Reply-To"]);
        Assert.AreEqual("auto-replied", headers["Auto-Submitted"]);
        Assert.Contains($"ticket.{ticket.ReplyToken}@", headers["In-Reply-To"]);
        Assert.AreEqual(headers["In-Reply-To"], headers["References"]);
        var alert = emails.Single(e => e.Template == TemplateKeys.TicketStaffAlert);
        Assert.AreEqual("equipo@hosting.example", alert.Recipient);
        Assert.Contains($"/admin/soporte/{ticket.Id}", alert.Html);
        Assert.HasCount(2, emails);
    }

    [TestMethod]
    public async Task AbreElStaffSinAvisar_NoSaleCorreo_YLaProximaRespuestaSi()
    {
        await using var h = await Start();
        var department = await Department(h);
        var staff = await Staff(h);

        var opened = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, "Llamó por teléfono", "Anotamos el pedido.", h.AccountId, Notify: false), StaffAuthor(staff)));
        await h.RunJobs();
        Assert.IsEmpty(await Emails(h, TemplateKeys.TicketReply), "Abierto sin avisar: el cliente no recibe nada todavía.");

        await Reply(h, opened.Id, new ReplyDraft("Ya está resuelto."), StaffAuthor(staff));
        await h.RunJobs();
        Assert.AreEqual("cliente@example.com", (await Emails(h, TemplateKeys.TicketReply)).Single().Recipient);
    }

    [TestMethod]
    public async Task RespondeElStaff_EsperaAlClienteSeLoAsignaYAvisaAlSolicitanteYALasCopias()
    {
        await using var h = await Start();
        var department = await Department(h, configure: d => d.AutoReply = false);
        var staff = await Staff(h);
        var opened = await Open(h, department, cc: ["Socio@Example.com", "cliente@example.com", "no es un correo"]);
        await h.RunJobs();

        await Reply(h, opened.Id, new ReplyDraft("Ya lo reiniciamos."), StaffAuthor(staff));
        await h.RunJobs();

        var ticket = await Get(h, opened.Id);
        Assert.AreEqual(TicketStatus.AwaitingCustomer, ticket.Status);
        Assert.AreEqual(staff, ticket.AssignedStaffId);
        CollectionAssert.AreEqual(new[] { "socio@example.com" }, ticket.Cc, "Sin repetir al solicitante ni casillas inválidas.");
        var replies = await Emails(h, TemplateKeys.TicketReply);
        CollectionAssert.AreEquivalent(new[] { "cliente@example.com", "socio@example.com" }, replies.Select(e => e.Recipient).ToArray());
        Assert.IsTrue(replies.All(e => Text(e).Contains("Ya lo reiniciamos.", StringComparison.Ordinal) && Text(e).Contains("respondió tu consulta", StringComparison.Ordinal)));
        Assert.IsTrue(replies.All(e => Headers(e)["In-Reply-To"].Contains(ticket.ReplyToken, StringComparison.Ordinal) && !Headers(e).ContainsKey("Auto-Submitted")));
        Assert.IsEmpty(await Emails(h, TemplateKeys.TicketOpened), "El departamento no confirma por correo.");
    }

    [TestMethod]
    public async Task NotaInterna_NoAvisaNiMueveElUltimoMensaje_YElClienteNoPuedeEscribirUna()
    {
        await using var h = await Start();
        var department = await Department(h);
        var staff = await Staff(h);
        var opened = await Open(h, department);
        await h.RunJobs();
        var before = (await Emails(h)).Count;
        var last = (await Get(h, opened.Id)).LastMessageAt;

        h.Clock.Now = h.Clock.Now.AddHours(1);
        var note = await Reply(h, opened.Id, new ReplyDraft("El cliente debe dos meses.", Internal: true), StaffAuthor(staff));
        var sneaky = await Reply(h, opened.Id, new ReplyDraft("Esto no es interno", Internal: true), Customer);
        await h.RunJobs();

        var messages = await h.Query(db => db.Set<TicketMessage>().Where(m => m.Id == note || m.Id == sneaky).OrderBy(m => m.Id).ToListAsync());
        Assert.IsTrue(messages[0].Internal);
        Assert.IsFalse(messages[1].Internal);
        var ticket = await Get(h, opened.Id);
        Assert.AreEqual(h.Clock.Now, ticket.LastMessageAt, "Lo movió el mensaje del cliente, no la nota.");
        Assert.AreNotEqual(last, ticket.LastMessageAt);
        Assert.IsFalse((await Emails(h)).Skip(before).Any(e => Text(e).Contains("dos meses", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ClienteEscribeEnUnoCerrado_SeReabreYElAvisoVaAlAsignado()
    {
        await using var h = await Start();
        var department = await Department(h);
        var staff = await Staff(h);
        var opened = await Open(h, department);
        await Reply(h, opened.Id, new ReplyDraft("Listo."), StaffAuthor(staff));
        await h.Use<TicketService, bool>(async t =>
        {
            await t.Close(opened.Id);
            return true;
        });
        Assert.IsNotNull((await Get(h, opened.Id)).ClosedAt);
        await h.RunJobs();

        var reply = await Reply(h, opened.Id, new ReplyDraft("Volvió a fallar."), Customer);
        await h.RunJobs();

        var ticket = await Get(h, opened.Id);
        Assert.AreEqual(TicketStatus.Open, ticket.Status);
        Assert.IsNull(ticket.ClosedAt);
        var alert = (await Emails(h, TemplateKeys.TicketStaffAlert)).Last();
        Assert.AreEqual("ana@hosting.example", alert.Recipient, "Con asignado, el aviso le llega a esa persona y no a la casilla del equipo.");
        Assert.Contains("Volvió a fallar.", Text(alert));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "support.ticket.close" && a.Entity == $"ticket:{opened.Id}")));
        Assert.IsGreaterThan(0, reply);
    }

    [TestMethod]
    public async Task Adjuntos_SoloLosTiposPermitidosYHastaElTope()
    {
        await using var h = await Start(o =>
        {
            o.MaxAttachmentBytes = 1024;
            o.MaxAttachments = 2;
            o.AllowedExtensions = "pdf, .PNG,txt";
        });
        var department = await Department(h);
        var options = new SupportOptions { MaxAttachmentBytes = 1024, MaxAttachments = 2, AllowedExtensions = "pdf, .PNG,txt" };

        Assert.AreEqual(AttachmentProblem.None, TicketService.Check(options, "Factura.PDF", 1024));
        Assert.AreEqual(AttachmentProblem.Type, TicketService.Check(options, "virus.exe", 10));
        Assert.AreEqual(AttachmentProblem.Type, TicketService.Check(options, "sin-extension", 10));
        Assert.AreEqual(AttachmentProblem.Size, TicketService.Check(options, "grande.pdf", 1025));
        Assert.AreEqual(AttachmentProblem.Size, TicketService.Check(options, "vacio.pdf", 0));
        Assert.AreEqual(AttachmentProblem.Count, TicketService.Check(options, "tercero.pdf", 10, index: 2));
        Assert.AreEqual("passwd.txt", TicketService.SafeName("..\\..\\etc/passwd.txt"));
        Assert.AreEqual("adjunto", TicketService.SafeName("..."));
        Assert.AreEqual("ab.txt", TicketService.SafeName("a\u0000<b>.txt"));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Open(h, department, [new NewAttachment("virus.exe", [1])]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Open(h, department, [new NewAttachment("grande.pdf", new byte[1025])]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Open(h, department, [new("a.pdf", [1]), new("b.pdf", [1]), new("c.pdf", [1])]));
        Assert.AreEqual(0, await h.Query(db => db.Set<Ticket>().CountAsync()), "Un adjunto que no pasa no deja nada a medias.");
    }

    [TestMethod]
    public async Task MismaReferencia_DevuelveElMismoTicketAunqueLleguenALaVez()
    {
        await using var h = await Start();
        var department = await Department(h);

        var nodes = Enumerable.Range(0, 3).Select(_ => h.Node()).ToList();
        try
        {
            var opened = await Task.WhenAll(nodes.SelectMany(n => Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var scope = n.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<TicketService>()
                    .Open(new TicketDraft(department, "Llamada al IVR", "Pide que lo llamen.", h.AccountId, ClientReference: "ivr-4711"), Customer);
            })));

            Assert.AreEqual(1, opened.Select(o => o.Id).Distinct().Count());
            Assert.AreEqual(1, await h.Query(db => db.Set<Ticket>().CountAsync()));
            Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.SupportNotify)), "El aviso de apertura sale una sola vez.");
        }
        finally
        {
            foreach (var n in nodes)
            {
                await n.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task NumerosPublicos_NoSeRepitenEntreNodos()
    {
        await using var h = await Start();
        var department = await Department(h);
        var nodes = Enumerable.Range(0, 2).Select(_ => h.Node()).ToList();
        try
        {
            var opened = await Task.WhenAll(nodes.SelectMany(n => Enumerable.Range(0, 10).Select(async i =>
            {
                await using var scope = n.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<TicketService>().Open(new TicketDraft(department, $"Consulta {i}", "Hola", h.AccountId), Customer);
            })));

            Assert.AreEqual(20, opened.Select(o => o.Number).Distinct().Count());
            Assert.IsTrue(opened.All(o => o.Number != o.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            foreach (var n in nodes)
            {
                await n.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task CambiosDelStaff_QuedanEnLaAuditoria()
    {
        await using var h = await Start();
        var department = await Department(h);
        var billing = await Department(h, "Facturación");
        var staff = await Staff(h);
        var opened = await Open(h, department);

        await h.Use<TicketService, bool>(async t =>
        {
            await t.Update(opened.Id, new TicketChange(TicketStatus.OnHold, TicketPriority.High, billing, staff));
            return true;
        });

        var ticket = await Get(h, opened.Id);
        Assert.AreEqual((TicketStatus.OnHold, TicketPriority.High, billing, (long?)staff), (ticket.Status, ticket.Priority, ticket.DepartmentId, ticket.AssignedStaffId));
        var audit = await h.Query(db => db.Set<AuditEvent>().SingleAsync(a => a.Action == "support.ticket.update"));
        Assert.AreEqual($"ticket:{opened.Id}", audit.Entity);
        Assert.Contains("OnHold", audit.Details!);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TicketService, bool>(async t =>
        {
            await t.Update(opened.Id, new TicketChange(AssignedStaffId: 999_999));
            return true;
        }));
        await h.Use<TicketService, bool>(async t =>
        {
            await t.Update(opened.Id, new TicketChange(Unassign: true));
            return true;
        });
        Assert.IsNull((await Get(h, opened.Id)).AssignedStaffId);
    }

    [TestMethod]
    public async Task CierreAutomatico_CierraSoloLosQueEsperanAlCliente()
    {
        await using var h = await Start(o => o.AutoCloseAfter = TimeSpan.FromDays(5));
        var department = await Department(h);
        var staff = await Staff(h);
        var answered = await Open(h, department);
        await Reply(h, answered.Id, new ReplyDraft("¿Probaste reiniciar?"), StaffAuthor(staff));
        var waiting = await Open(h, department);
        await h.RunJobs();

        h.Clock.Now = h.Clock.Now.AddDays(6);
        await h.Tick();
        await h.RunJobs();

        Assert.AreEqual(TicketStatus.Closed, (await Get(h, answered.Id)).Status);
        Assert.AreEqual(TicketStatus.Open, (await Get(h, waiting.Id)).Status);
        var mark = await h.Query(db => db.Set<TicketMessage>().Where(m => m.TicketId == answered.Id).OrderBy(m => m.Id).LastAsync());
        Assert.AreEqual((MessageAuthor.System, TicketService.AutoClosedText), (mark.Author, mark.Body));

        await Reply(h, answered.Id, new ReplyDraft("Sigue igual."), Customer);
        Assert.AreEqual(TicketStatus.Open, (await Get(h, answered.Id)).Status);
    }

    [TestMethod]
    public async Task SinCierreAutomatico_NingunoSeCierraSolo()
    {
        await using var h = await Start();
        var department = await Department(h);
        var staff = await Staff(h);
        var answered = await Open(h, department);
        await Reply(h, answered.Id, new ReplyDraft("¿Probaste reiniciar?"), StaffAuthor(staff));

        h.Clock.Now = h.Clock.Now.AddDays(400);
        await h.Tick();
        await h.RunJobs();

        Assert.AreEqual(TicketStatus.AwaitingCustomer, (await Get(h, answered.Id)).Status);
    }

    [TestMethod]
    public async Task DepartamentoOcultoYServicioAjeno_SeRechazanDesdeElPortal()
    {
        await using var h = await Start();
        var hidden = await Department(h, "Abuso", d => d.Hidden = true);
        var staff = await Staff(h);
        var other = await h.Use<AccountService, long>(a => a.Create(new AccountInput("Otra cuenta", "otra@example.com", Currency.Ars)));
        var foreign = (await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(other, [new OrderItem(h.PlanId)]))).ServiceIds[0];
        var own = (await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]))).ServiceIds[0];
        var visible = await Department(h);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(hidden, "Hola", "Hola", h.AccountId), Customer)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(visible, "Hola", "Hola", h.AccountId, ServiceId: foreign), Customer)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(visible, "  ", "Hola", h.AccountId), Customer)));

        var byStaff = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(hidden, "Aviso de abuso", "Recibimos una denuncia.", h.AccountId, ServiceId: own), StaffAuthor(staff)));
        var ticket = await Get(h, byStaff.Id);
        Assert.AreEqual((TicketStatus.AwaitingCustomer, (long?)staff, (long?)own, "cliente@example.com"), (ticket.Status, ticket.AssignedStaffId, ticket.ServiceId, ticket.RequesterEmail));
        await h.RunJobs();
        var notice = (await Emails(h, TemplateKeys.TicketReply)).Single();
        Assert.Contains("abrió la consulta", Text(notice));
    }

    [TestMethod]
    public async Task CorreoDelSolicitanteYCopias_QuedanComoCasillasSueltas_YUnoInvalidoNoAbre()
    {
        await using var h = await Start();
        var department = await Department(h);
        var staff = StaffAuthor(await Staff(h));

        var opened = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, "Alta", "Hola.", h.AccountId,
            RequesterEmail: "Juan Pérez <Juan@Example.com>", Cc: ["Ana <ANA@example.com>", "no-es-correo", "juan@example.com"]), staff));

        var ticket = await Get(h, opened.Id);
        Assert.AreEqual("Juan@Example.com", ticket.RequesterEmail);
        CollectionAssert.AreEqual(new[] { "ana@example.com" }, ticket.Cc);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, "Alta", "Hola.", h.AccountId, RequesterEmail: "no-es-correo"), staff)));
    }

    [TestMethod]
    public async Task AvisoDeTicket_SaleConElHiloYLaCasillaDeRespuesta()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await Start(notifications: o =>
        {
            o.Send = true;
            o.ProductionEnabled = true;
            o.Host = "127.0.0.1";
            o.Port = smtp.Port;
            o.Security = SecureSocketOptions.None;
            o.From = "avisos@hosting.example";
        });
        var department = await Department(h, configure: d => d.StaffAlertEmail = null);
        var opened = await Open(h, department);

        await h.RunJobs();

        var message = smtp.Messages.Single();
        var ticket = await Get(h, opened.Id);
        Assert.AreEqual("soporte@hosting.example", message.ReplyTo.Mailboxes.Single().Address);
        Assert.AreEqual($"ticket.{ticket.ReplyToken}@hosting.example", message.InReplyTo);
        CollectionAssert.AreEqual(new[] { $"ticket.{ticket.ReplyToken}@hosting.example" }, message.References.ToArray());
        Assert.AreEqual("auto-replied", message.Headers["Auto-Submitted"]);
        Assert.Contains("Reply above this line", message.HtmlBody ?? string.Empty);
    }

    [TestMethod]
    public async Task EncabezadosDeMas_SoloLosDeLaListaYSinSaltosDeLinea()
    {
        await using var h = await Start();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => h.Use<Notifier, bool>(n => n.Queue(TemplateKeys.TicketReply, "x@example.com", "es", new Dictionary<string, object?>(), "k1",
            headers: new Dictionary<string, string> { ["Bcc"] = "espia@example.com" })));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => h.Use<Notifier, bool>(n => n.Queue(TemplateKeys.TicketReply, "x@example.com", "es", new Dictionary<string, object?>(), "k2",
            headers: new Dictionary<string, string> { ["Reply-To"] = "a@example.com\r\nBcc: espia@example.com" })));
        Assert.IsEmpty(await Emails(h));
    }
}
