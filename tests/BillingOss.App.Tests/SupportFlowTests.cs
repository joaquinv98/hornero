using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using BillingOss.App.UseCases;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit.Utils;

namespace BillingOss.App.Tests;

/// <summary>Unión de tickets, encuesta de satisfacción y reglas de escalamiento (sección 9 del modelo de dominio).</summary>
[TestClass]
public sealed class SupportFlowTests
{
    private static Task Merge(CoreHarness h, long source, long target) => h.Use<TicketService, bool>(async t =>
    {
        await t.Merge(source, target);
        return true;
    });

    private static Task<List<TicketMessage>> Messages(CoreHarness h, long ticketId) =>
        h.Query(db => db.Set<TicketMessage>().AsNoTracking().Where(m => m.TicketId == ticketId).OrderBy(m => m.Id).ToListAsync());

    private static Task<long> Rule(CoreHarness h, Action<EscalationRule> configure) => h.Query(async db =>
    {
        var rule = new EscalationRule { Name = "Sin respuesta en una hora", AfterMinutes = 60 };
        configure(rule);
        db.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    });

    private static Task<int> Escalate(CoreHarness h) => h.Use<TicketService, int>(t => t.Escalate());

    [TestMethod]
    public async Task Unir_PasaMensajesYAdjuntos_ElUnidoQuedaCerradoYSuCorreoLlegaAlQueLoRecibio()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var target = await SupportTests.Open(h, department);
        var source = await SupportTests.Open(h, department, [new NewAttachment("log.txt", [7, 7])], cc: ["socio@example.com"]);
        var guest = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, "Desde el celular", "Soy yo desde otra casilla.", RequesterEmail: "personal@example.org"),
            new Author(MessageAuthor.Customer, "Cliente", "personal@example.org", MessageSource.Email)));
        var sourceToken = (await SupportTests.Get(h, source.Id)).ReplyToken;

        await Merge(h, guest.Id, source.Id);
        await Merge(h, source.Id, target.Id);

        var merged = await SupportTests.Get(h, source.Id);
        Assert.AreEqual((TicketStatus.Closed, (long?)target.Id), (merged.Status, merged.MergedIntoId));
        Assert.AreEqual((long?)target.Id, (await SupportTests.Get(h, guest.Id)).MergedIntoId, "Lo que se había unido al primero pasa a apuntar al segundo.");
        Assert.IsEmpty(await Messages(h, source.Id));
        var thread = await Messages(h, target.Id);
        Assert.HasCount(5, thread, "Los dos mensajes originales, el del invitado y las dos marcas de unión.");
        Assert.AreEqual(TicketService.MergedText(merged.Number, merged.Subject), thread[^1].Body);
        Assert.AreEqual(MessageAuthor.System, thread[^1].Author);
        var ids = thread.Select(m => m.Id).ToList();
        Assert.AreEqual(1, await h.Query(db => db.Set<TicketAttachment>().CountAsync(a => ids.Contains(a.MessageId))), "El adjunto sigue a su mensaje.");
        var kept = await SupportTests.Get(h, target.Id);
        CollectionAssert.AreEquivalent(new[] { "socio@example.com", "personal@example.org" }, kept.Cc);
        Assert.AreEqual(TicketStatus.Open, kept.Status);
        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "support.ticket.merge")));

        var reply = await SupportMailTests.Deliver(h, department, SupportMailTests.Mail("cliente@example.com", "RE: No anda el VPS", "Sigue igual.", m =>
        {
            m.InReplyTo = MimeUtils.GenerateMessageId("hosting.example");
            m.References.Add(SupportNotifyHandler.ThreadId(sourceToken, "avisos@hosting.example"));
        }));
        Assert.AreEqual((InboundOutcome.Replied, (long?)target.Id), (reply!.Outcome, reply.TicketId));
        var byNumber = await SupportMailTests.Deliver(h, department, SupportMailTests.Mail("socio@example.com", $"Otra cosa [#{merged.Number}]", "Agrego un dato."));
        Assert.AreEqual((InboundOutcome.Replied, (long?)target.Id), (byNumber!.Outcome, byNumber.TicketId), "El número del unido también lleva al que lo recibió.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => SupportTests.Reply(h, source.Id, new ReplyDraft("Hola"), SupportTests.Customer));
    }

    [TestMethod]
    public async Task Unir_SoloLaMismaCuentaOUnoSinCuenta()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var other = await h.Use<AccountService, long>(a => a.Create(new AccountInput("Otra cuenta", "otra@example.com", Currency.Ars)));
        var mine = await SupportTests.Open(h, department);
        var foreign = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(department, "Hola", "De otra cuenta", other), SupportTests.Customer));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Merge(h, foreign.Id, mine.Id));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Merge(h, mine.Id, mine.Id));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Merge(h, mine.Id, 999_999));
        Assert.IsNull((await SupportTests.Get(h, foreign.Id)).MergedIntoId);
        Assert.HasCount(1, await Messages(h, mine.Id), "Un intento rechazado no mueve nada.");
    }

    [TestMethod]
    public async Task Encuesta_SaleUnaVezAlCerrar_YSeRespondeUnaVezConLaFicha()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h, configure: d => d.RequestFeedback = true);
        var staff = await SupportTests.Staff(h);
        var opened = await SupportTests.Open(h, department);
        await SupportTests.Reply(h, opened.Id, new ReplyDraft("Listo, quedó andando.", Status: TicketStatus.Closed), SupportTests.StaffAuthor(staff));
        await h.RunJobs();

        var survey = (await SupportTests.Emails(h, TemplateKeys.TicketFeedback)).Single();
        var ticket = await SupportTests.Get(h, opened.Id);
        Assert.AreEqual("cliente@example.com", survey.Recipient);
        Assert.Contains($"https://billing.example.com/soporte/encuesta/{ticket.FeedbackToken}", survey.Html);
        Assert.Contains(ticket.ReplyToken, survey.Headers!, "Responder la encuesta por correo reabre la consulta.");

        Assert.IsTrue(await h.Use<TicketService, bool>(t => t.Rate(ticket.FeedbackToken!, 9, "  Rápido y claro.  ")));
        Assert.IsFalse(await h.Use<TicketService, bool>(t => t.Rate(ticket.FeedbackToken!, 2, null)), "Una sola respuesta por ticket.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TicketService, bool>(t => t.Rate(ticket.FeedbackToken!, 11, null)));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => h.Use<TicketService, bool>(t => t.Rate("no-existe", 5, null)));
        var feedback = await h.Query(db => db.Set<TicketFeedback>().SingleAsync());
        Assert.AreEqual((opened.Id, 9, "Rápido y claro.", (long?)staff), (feedback.TicketId, feedback.Rating, feedback.Comment, feedback.StaffId));

        await SupportTests.Reply(h, opened.Id, new ReplyDraft("Volvió a pasar."), SupportTests.Customer);
        await h.Use<TicketService, bool>(async t =>
        {
            await t.Close(opened.Id);
            return true;
        });
        await h.RunJobs();
        Assert.HasCount(1, await SupportTests.Emails(h, TemplateKeys.TicketFeedback), "Reabrir y cerrar no manda otra encuesta.");
    }

    [TestMethod]
    public async Task Encuesta_SinPedidoDelDepartamentoOReabiertoAntesDelAviso_NoSale()
    {
        await using var h = await SupportTests.Start(o => o.AutoCloseAfter = TimeSpan.FromDays(3));
        var quiet = await SupportTests.Department(h);
        var asks = await SupportTests.Department(h, "Ventas", d => d.RequestFeedback = true);
        var staff = await SupportTests.Staff(h);
        var silent = await SupportTests.Open(h, quiet);
        await SupportTests.Reply(h, silent.Id, new ReplyDraft("Resuelto.", Status: TicketStatus.Closed), SupportTests.StaffAuthor(staff));
        var reopened = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(asks, "Precio", "¿Cuánto sale?", h.AccountId), SupportTests.Customer));
        await SupportTests.Reply(h, reopened.Id, new ReplyDraft("Sale 10.", Status: TicketStatus.Closed), SupportTests.StaffAuthor(staff));
        await SupportTests.Reply(h, reopened.Id, new ReplyDraft("¿Y con IVA?"), SupportTests.Customer);
        await h.RunJobs();

        Assert.IsEmpty(await SupportTests.Emails(h, TemplateKeys.TicketFeedback));
        Assert.IsNull((await SupportTests.Get(h, reopened.Id)).FeedbackToken);

        // El cierre automático también la pide.
        await SupportTests.Reply(h, reopened.Id, new ReplyDraft("Con IVA, 12,10."), SupportTests.StaffAuthor(staff));
        h.Clock.Now = h.Clock.Now.AddDays(4);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual("cliente@example.com", (await SupportTests.Emails(h, TemplateKeys.TicketFeedback)).Single().Recipient);
    }

    [TestMethod]
    public async Task Encuesta_NoSaleAQuienEstaBloqueadoEnElControlDeSpam()
    {
        await using var h = await SupportTests.Start();
        var asks = await SupportTests.Department(h, "Ventas", d => d.RequestFeedback = true);
        var sender = new Author(MessageAuthor.Customer, "Ofertas", "ofertas@otro.example", MessageSource.Email);
        var spam = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(asks, "Oferta", "Compre ya.", RequesterEmail: "ofertas@otro.example"), sender));
        var real = await h.Use<TicketService, OpenedTicket>(t => t.Open(new TicketDraft(asks, "Precio", "¿Cuánto sale?", h.AccountId), SupportTests.Customer));
        await h.Query(async db =>
        {
            db.Add(new MailFilter { Kind = MailFilterKind.Sender, Value = "Ofertas@Otro.example" });
            return await db.SaveChangesAsync();
        });

        foreach (var id in new[] { spam.Id, real.Id })
        {
            await h.Use<TicketService, bool>(async t =>
            {
                await t.Close(id);
                return true;
            });
        }

        await h.RunJobs();
        Assert.AreEqual("cliente@example.com", (await SupportTests.Emails(h, TemplateKeys.TicketFeedback)).Single().Recipient, "Al bloqueado no le pide la encuesta.");
    }

    [TestMethod]
    public async Task Escalamiento_AplicaUnaVezPorEspera_AvisaYDejaLaNota_YUnMensajeNuevoLaHabilita()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var billing = await SupportTests.Department(h, "Facturación");
        var staff = await SupportTests.Staff(h);
        var urgent = await Rule(h, r =>
        {
            r.DepartmentIds = [department];
            r.Priorities = [TicketPriority.Normal];
            r.NewPriority = TicketPriority.High;
            r.AssignStaffId = staff;
            r.Notify = true;
            r.Note = "Pasó una hora sin respuesta.";
        });
        var reminder = await Rule(h, r =>
        {
            r.Name = "Recordatorio";
            r.AfterMinutes = 90;
            r.NewDepartmentId = billing;
        });
        var opened = await SupportTests.Open(h, department);
        await h.RunJobs();
        var start = h.Clock.Now;

        h.Clock.Now = start.AddMinutes(30);
        Assert.AreEqual(0, await Escalate(h));
        h.Clock.Now = start.AddMinutes(61);
        Assert.AreEqual(1, await Escalate(h));
        Assert.AreEqual(0, await Escalate(h), "La misma espera no se escala dos veces.");
        await h.RunJobs();

        var ticket = await SupportTests.Get(h, opened.Id);
        Assert.AreEqual((TicketPriority.High, (long?)staff, department), (ticket.Priority, ticket.AssignedStaffId, ticket.DepartmentId));
        var note = (await Messages(h, opened.Id)).Last();
        Assert.AreEqual((true, MessageAuthor.System, "Pasó una hora sin respuesta."), (note.Internal, note.Author, note.Body));
        Assert.AreEqual(start, ticket.LastMessageAt, "La nota interna no cuenta como mensaje nuevo.");
        var alert = (await SupportTests.Emails(h, TemplateKeys.TicketEscalated)).Single();
        Assert.AreEqual("ana@hosting.example", alert.Recipient, "Avisa al asignado que dejó la regla.");
        Assert.Contains("Sin respuesta en una hora", SupportTests.Text(alert));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "support.ticket.escalate")));

        h.Clock.Now = start.AddMinutes(91);
        Assert.AreEqual(1, await Escalate(h));
        Assert.AreEqual(billing, (await SupportTests.Get(h, opened.Id)).DepartmentId);

        // Un mensaje nuevo del cliente abre otra espera: la regla sin condición de prioridad vuelve a valer.
        await SupportTests.Reply(h, opened.Id, new ReplyDraft("¿Novedades?"), SupportTests.Customer);
        h.Clock.Now = start.AddMinutes(150);
        Assert.AreEqual(0, await Escalate(h), "La de prioridad normal ya no aplica a un ticket en alta, y la otra espera sus 90 minutos.");
        h.Clock.Now = start.AddMinutes(200);
        Assert.AreEqual(1, await Escalate(h));
        Assert.AreEqual(3, await h.Query(db => db.Set<TicketEscalation>().CountAsync()));
        Assert.AreEqual(2, await h.Query(db => db.Set<TicketEscalation>().CountAsync(e => e.RuleId == reminder)));
        Assert.AreEqual(1, await h.Query(db => db.Set<TicketEscalation>().CountAsync(e => e.RuleId == urgent)));
    }

    [TestMethod]
    public async Task Escalamiento_RespondeAlClienteYAvisaALasPersonasElegidas_SinMoverLaEspera()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var ana = await SupportTests.Staff(h);
        var beto = await SupportTests.Staff(h, "beto@hosting.example");
        await Rule(h, r =>
        {
            r.Notify = true;
            r.NotifyStaffIds = [ana, beto];
            r.Reply = "Seguimos con tu caso: lo pasamos a un especialista.";
        });
        var opened = await SupportTests.Open(h, department, cc: ["socio@example.com"]);
        await h.RunJobs();
        var start = h.Clock.Now;

        h.Clock.Now = start.AddMinutes(61);
        Assert.AreEqual(1, await Escalate(h));
        await h.RunJobs();

        var reply = (await Messages(h, opened.Id)).Last();
        Assert.AreEqual((false, MessageAuthor.System, "Soporte técnico", "Seguimos con tu caso: lo pasamos a un especialista."), (reply.Internal, reply.Author, reply.AuthorName, reply.Body));
        var ticket = await SupportTests.Get(h, opened.Id);
        Assert.AreEqual((TicketStatus.Open, start), (ticket.Status, ticket.LastMessageAt), "La respuesta de la regla no mueve la espera.");
        var replies = await SupportTests.Emails(h, TemplateKeys.TicketReply);
        CollectionAssert.AreEquivalent(new[] { "cliente@example.com", "socio@example.com" }, replies.Select(e => e.Recipient).ToList());
        Assert.Contains("lo pasamos a un especialista", SupportTests.Text(replies[0]));
        var alerts = await SupportTests.Emails(h, TemplateKeys.TicketEscalated);
        CollectionAssert.AreEquivalent(new[] { "equipo@hosting.example", "ana@hosting.example", "beto@hosting.example" }, alerts.Select(e => e.Recipient).ToList(),
            "Sin asignado, la casilla del equipo, y además cada persona que nombra la regla.");

        h.Clock.Now = start.AddMinutes(300);
        Assert.AreEqual(0, await Escalate(h), "Su propia respuesta no abre otra espera.");
        await h.RunJobs();
        Assert.HasCount(2, await SupportTests.Emails(h, TemplateKeys.TicketReply), "Los reintentos no repiten el aviso.");
    }

    [TestMethod]
    public async Task Escalamiento_DosNodosALaVez_UnaSolaVez_YElPlanificadorLoEncola()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        await Rule(h, r => r.NewPriority = TicketPriority.Urgent);
        var opened = await SupportTests.Open(h, department);
        h.Clock.Now = h.Clock.Now.AddHours(2);

        var nodes = Enumerable.Range(0, 3).Select(_ => h.Node()).ToList();
        try
        {
            var applied = await Task.WhenAll(nodes.Select(async n =>
            {
                await using var scope = n.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<TicketService>().Escalate();
            }));

            Assert.AreEqual(1, applied.Sum());
        }
        finally
        {
            foreach (var n in nodes)
            {
                await n.DisposeAsync();
            }
        }

        Assert.AreEqual(TicketPriority.Urgent, (await SupportTests.Get(h, opened.Id)).Priority);
        await h.Tick();
        await h.Tick();
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.SupportEscalate)), "Un trabajo por intervalo de cinco minutos.");
    }
}
