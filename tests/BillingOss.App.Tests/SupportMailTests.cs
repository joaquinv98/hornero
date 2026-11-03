using System.Security.Cryptography;
using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Utils;

namespace BillingOss.App.Tests;

/// <summary>
/// Correo entrante (sección 9 del modelo de dominio): qué abre un ticket, qué se suma a uno, qué se descarta sin
/// contestar, y la lectura de la casilla por IMAP contra un servidor falso.
/// </summary>
[TestClass]
public sealed class SupportMailTests
{
    private const string ImapUser = "soporte@hosting.example";
    private const string ImapPassword = "clave-de-la-casilla-1";

    internal static MimeMessage Mail(string from, string subject, string body, Action<MimeMessage>? configure = null)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("soporte@hosting.example"));
        message.Subject = subject;
        message.MessageId = MimeUtils.GenerateMessageId("cliente.example");
        message.Body = new TextPart("plain") { Text = body };
        configure?.Invoke(message);
        return message;
    }

    internal static async Task<InboundEmail?> Deliver(CoreHarness h, long departmentId, MimeMessage message)
    {
        var department = await h.Query(db => db.Set<Department>().AsNoTracking().SingleAsync(d => d.Id == departmentId));
        return await h.Use<MailIntake, InboundEmail?>(intake => intake.Process(department, message, CancellationToken.None));
    }

    private static Task<List<Ticket>> Tickets(CoreHarness h) => h.Query(db => db.Set<Ticket>().AsNoTracking().OrderBy(t => t.Id).ToListAsync());

    private static Task<List<TicketMessage>> Messages(CoreHarness h, long ticketId) =>
        h.Query(db => db.Set<TicketMessage>().AsNoTracking().Where(m => m.TicketId == ticketId).OrderBy(m => m.Id).ToListAsync());

    [TestMethod]
    public async Task CorreoNuevo_AbreUnTicketEnLaCuentaDelRemitenteConLasCopias()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var mail = Mail("Cliente Apurado <Cliente@Example.com>", "Se cayó el correo", "No me llegan los mensajes desde ayer.", m => m.Cc.Add(MailboxAddress.Parse("colega@example.com")));

        var record = await Deliver(h, department, mail);

        Assert.AreEqual(InboundOutcome.Opened, record!.Outcome);
        var ticket = (await Tickets(h)).Single();
        Assert.AreEqual(record.TicketId, ticket.Id);
        Assert.AreEqual((h.AccountId, "Cliente Apurado", "cliente@example.com"), (ticket.AccountId!.Value, ticket.RequesterName, ticket.RequesterEmail));
        CollectionAssert.AreEqual(new[] { "colega@example.com" }, ticket.Cc, "La casilla del departamento no va en copia.");
        var message = (await Messages(h, ticket.Id)).Single();
        Assert.AreEqual((MessageAuthor.Customer, MessageSource.Email, "No me llegan los mensajes desde ayer."), (message.Author, message.Source, message.Body));
        Assert.AreEqual(TicketService.MailKey(mail.MessageId!), message.EmailMessageId);
        await h.RunJobs();
        Assert.AreEqual(1, await h.Query(db => db.Set<OutboundEmail>().CountAsync(e => e.Template == TemplateKeys.TicketOpened && e.Recipient == "cliente@example.com")));
    }

    [TestMethod]
    public async Task RespuestaConLaFichaDelHilo_SeSumaAunqueEscribaDesdeOtraCasilla()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var opened = await SupportTests.Open(h, department);
        var token = (await SupportTests.Get(h, opened.Id)).ReplyToken;
        var thread = SupportNotifyHandler.ThreadId(token, "avisos@hosting.example");

        var record = await Deliver(h, department, Mail("otra@empresa.example", "RE: tu consulta", "Te respondo desde el celular.", m =>
        {
            m.InReplyTo = MimeUtils.GenerateMessageId("hosting.example");
            m.References.Add(thread);
            m.References.Add(m.InReplyTo);
        }));

        Assert.AreEqual((InboundOutcome.Replied, (long?)opened.Id), (record!.Outcome, record.TicketId));
        var last = (await Messages(h, opened.Id)).Last();
        Assert.AreEqual(("otra@empresa.example", "Te respondo desde el celular."), (last.AuthorEmail, last.Body));
        Assert.HasCount(1, await Tickets(h));
    }

    [TestMethod]
    public async Task NumeroEnElAsunto_SoloSumaSiQuienEscribeParticipa()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var opened = await SupportTests.Open(h, department);
        var subject = $"Re: [#{opened.Number}] No anda el VPS";

        var own = await Deliver(h, department, Mail("cliente@example.com", subject, "Sigue sin andar."));
        var stranger = await Deliver(h, department, Mail("extrano@otro.example", subject, "Quiero ver ese ticket."));

        Assert.AreEqual((InboundOutcome.Replied, (long?)opened.Id), (own!.Outcome, own.TicketId));
        Assert.AreEqual(InboundOutcome.Opened, stranger!.Outcome);
        Assert.AreNotEqual(opened.Id, stranger.TicketId);
        Assert.IsNull((await Tickets(h)).Single(t => t.Id == stranger.TicketId).AccountId, "Quien no es de una cuenta abre un ticket sin cuenta.");
    }

    [TestMethod]
    public async Task NumeroConOtroRotulo_ComoLosAsuntosDelSistemaAnterior_EncuentraElTicket()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var opened = await SupportTests.Open(h, department);

        var labeled = await Deliver(h, department, Mail("cliente@example.com", $"RE: [Consulta N.º: {opened.Number}] No anda el VPS", "Respondo un correo viejo."));
        var dated = await Deliver(h, department, Mail("cliente@example.com", $"Re: [2026 {opened.Number}] otra cosa", "Esto es otro tema."));
        var stranger = await Deliver(h, department, Mail("extrano@otro.example", $"RE: [Consulta: {opened.Number}] No anda el VPS", "Quiero ver ese ticket."));

        Assert.AreEqual((InboundOutcome.Replied, (long?)opened.Id), (labeled!.Outcome, labeled.TicketId));
        Assert.AreEqual(InboundOutcome.Opened, dated!.Outcome, "Un rótulo con cifras no es el del número.");
        Assert.AreEqual(InboundOutcome.Opened, stranger!.Outcome, "Igual hace falta participar del ticket.");
    }

    [TestMethod]
    public async Task RespuestaConLaFicha_SumaLasOtrasCasillasALasCopias_SalvoQueElAjusteLoApague()
    {
        foreach (var on in new[] { true, false })
        {
            await using var h = await SupportTests.Start(o => o.EmailCc = on);
            var department = await SupportTests.Department(h);
            var opened = await SupportTests.Open(h, department, cc: ["primera@example.com"]);
            var thread = SupportNotifyHandler.ThreadId((await SupportTests.Get(h, opened.Id)).ReplyToken, "avisos@hosting.example");

            var record = await Deliver(h, department, Mail("cliente@example.com", "Re: No anda el VPS", "Sumo a mi socio.", m =>
            {
                m.References.Add(thread);
                m.To.Add(MailboxAddress.Parse("Socio <Socio@Example.com>"));
                m.Cc.Add(MailboxAddress.Parse("cliente@example.com"));
            }));

            Assert.AreEqual(InboundOutcome.Replied, record!.Outcome);
            var expected = on ? new[] { "primera@example.com", "socio@example.com" } : new[] { "primera@example.com" };
            CollectionAssert.AreEqual(expected, (await SupportTests.Get(h, opened.Id)).Cc, "Ni la casilla del departamento ni quien consulta van en copia.");
        }
    }

    [TestMethod]
    public async Task RespuestaPorElNumeroDelAsunto_NoSumaCopias_UnRemitenteFalsoNoSeSumaALasRespuestas()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var opened = await SupportTests.Open(h, department);

        // Cualquiera que sepa el número puede poner la casilla del cliente en el remitente: sin la ficha no suma copias.
        var record = await Deliver(h, department, Mail("cliente@example.com", $"Re: [#{opened.Number}] No anda el VPS", "Mandame todo a esta otra.", m => m.Cc.Add(MailboxAddress.Parse("intruso@otro.example"))));

        Assert.AreEqual((InboundOutcome.Replied, (long?)opened.Id), (record!.Outcome, record.TicketId));
        Assert.IsEmpty((await SupportTests.Get(h, opened.Id)).Cc);
    }

    [TestMethod]
    public async Task AutomaticosRebotesYPropios_SeIgnoranSinContestar()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);

        var outcomes = new List<InboundOutcome>();
        foreach (var mail in new[]
        {
            Mail("cliente@example.com", "Fuera de la oficina", "Vuelvo el lunes.", m => m.Headers.Add("Auto-Submitted", "auto-replied")),
            Mail("lista@example.com", "Novedades", "Boletín", m => m.Headers.Add(HeaderId.Precedence, "bulk")),
            Mail("MAILER-DAEMON@mx.example.com", "Undelivered", "No se pudo entregar."),
            Mail("soporte@hosting.example", "Bucle", "Me escribo a mí mismo."),
            Mail("cliente@example.com", "Auto-Submitted: no", "Esto lo escribió una persona.", m => m.Headers.Add("Auto-Submitted", "no")),
        })
        {
            outcomes.Add((await Deliver(h, department, mail))!.Outcome);
        }

        CollectionAssert.AreEqual(new[] { InboundOutcome.Ignored, InboundOutcome.Ignored, InboundOutcome.Ignored, InboundOutcome.Ignored, InboundOutcome.Opened }, outcomes);
        Assert.HasCount(1, await Tickets(h));
        await h.RunJobs();
        Assert.AreEqual(1, await h.Query(db => db.Set<OutboundEmail>().CountAsync(e => e.Template == TemplateKeys.TicketOpened)), "Solo contesta a la persona.");
    }

    [TestMethod]
    public async Task FiltrosYTopePorRemitente_RechazanSinAvisar()
    {
        await using var h = await SupportTests.Start(o => o.MaxEmailsPerSenderPerHour = 2);
        var department = await SupportTests.Department(h);
        await h.Query(async db =>
        {
            db.AddRange(new MailFilter { Kind = MailFilterKind.Sender, Value = "spam.example" }, new MailFilter { Kind = MailFilterKind.Subject, Value = "Oferta" });
            return await db.SaveChangesAsync();
        });

        var spam = await Deliver(h, department, Mail("ventas@spam.example", "Hola", "Compre ya"));
        var offer = await Deliver(h, department, Mail("alguien@example.org", "Gran OFERTA", "Compre ya"));
        var outcomes = new List<InboundOutcome>();
        for (var i = 0; i < 3; i++)
        {
            outcomes.Add((await Deliver(h, department, Mail("insistente@example.org", $"Consulta {i}", "Hola")))!.Outcome);
        }

        Assert.AreEqual(InboundOutcome.Rejected, spam!.Outcome);
        Assert.Contains("spam.example", spam.Reason!);
        Assert.AreEqual(InboundOutcome.Rejected, offer!.Outcome);
        CollectionAssert.AreEqual(new[] { InboundOutcome.Opened, InboundOutcome.Opened, InboundOutcome.Rejected }, outcomes);
        Assert.HasCount(2, await Tickets(h));
    }

    [TestMethod]
    public async Task DepartamentoSoloClientes_RechazaAQuienNoEsDeUnaCuenta()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h, configure: d => d.ClientsOnly = true);

        var stranger = await Deliver(h, department, Mail("nadie@otro.example", "Hola", "¿Venden dominios?"));
        var customer = await Deliver(h, department, Mail("cliente@example.com", "Hola", "Necesito ayuda."));

        Assert.AreEqual(InboundOutcome.Rejected, stranger!.Outcome);
        Assert.AreEqual(InboundOutcome.Opened, customer!.Outcome);
    }

    [TestMethod]
    public async Task AdjuntosDelCorreo_LosQueNoPasanQuedanNombradosEnElMensaje()
    {
        await using var h = await SupportTests.Start(o => o.MaxAttachmentBytes = 100);
        var department = await SupportTests.Department(h);
        var mail = Mail("cliente@example.com", "Adjunto la factura", "Va la captura.", m =>
        {
            var body = new BodyBuilder { TextBody = "Va la captura." };
            body.Attachments.Add("captura.png", new byte[10]);
            body.Attachments.Add("virus.exe", new byte[10]);
            body.Attachments.Add("enorme.pdf", new byte[500]);
            m.Body = body.ToMessageBody();
        });

        var record = await Deliver(h, department, mail);

        var message = (await Messages(h, record!.TicketId!.Value)).Single();
        StringAssert.StartsWith(message.Body, "Va la captura.");
        Assert.Contains("[Adjunto omitido: virus.exe, tipo no permitido]", message.Body);
        Assert.Contains("[Adjunto omitido: enorme.pdf, supera el tope o está vacío]", message.Body);
        var stored = await h.Query(db => db.Set<TicketAttachment>().Where(a => a.MessageId == message.Id).Select(a => a.FileName).ToListAsync());
        CollectionAssert.AreEqual(new[] { "captura.png" }, stored);
    }

    [TestMethod]
    public async Task MismoCorreoDosVeces_SeProcesaUnaSolaVez()
    {
        await using var h = await SupportTests.Start();
        var department = await SupportTests.Department(h);
        var mail = Mail("cliente@example.com", "Hola", "Una sola vez.");

        Assert.IsNotNull(await Deliver(h, department, mail));
        Assert.IsNull(await Deliver(h, department, mail));

        Assert.HasCount(1, await Tickets(h));
        Assert.AreEqual(1, await h.Query(db => db.Set<InboundEmail>().CountAsync()));
    }

    [TestMethod]
    public void TextoDelCorreo_SinLoCitadoYConElHtmlPasadoATexto()
    {
        var html = new MimeMessage { Body = new TextPart("html") { Text = "<html><head><title>x</title><style>p{color:red}</style></head><body><p>Hola</p><div>Segunda   línea</div><script>alert(1)</script></body></html>" } };
        var quoted = "Gracias, ya funciona.\n\nEl lun, 2 nov 2026 a las 10:00, Soporte <soporte@hosting.example> escribió:\n> Probá reiniciar.\n> Saludos";
        var marked = $"Listo, gracias.\n\n{MailText.ReplyMarker}\nHola, Cliente:\nTe respondimos...";
        var inline = "> ¿Probaste reiniciar?\nSí, dos veces.\n\nOn Mon, Nov 2, 2026 at 10:00 AM Soporte wrote:\n> Hola";

        Assert.AreEqual("Hola\nSegunda línea", MailText.Body(html));
        Assert.AreEqual("Gracias, ya funciona.", MailText.StripQuoted(quoted));
        Assert.AreEqual("Listo, gracias.", MailText.StripQuoted(marked));
        Assert.AreEqual("> ¿Probaste reiniciar?\nSí, dos veces.", MailText.StripQuoted(inline), "Lo citado en el medio puede ser la respuesta intercalada.");
    }

    private static SecretBox Box() => new(new Dictionary<int, byte[]> { [1] = RandomNumberGenerator.GetBytes(32) }, 1);

    /// <summary>Un departamento con la casilla del servidor falso y la contraseña cifrada atada a la fila y al host.</summary>
    private static Task<long> Mailbox(CoreHarness h, SecretBox box, FakeImapServer imap, string password = ImapPassword) => h.Query(async db =>
    {
        var department = new Department
        {
            Name = "Soporte",
            Email = "soporte@hosting.example",
            MailEnabled = true,
            ImapHost = "127.0.0.1",
            ImapPort = imap.Port,
            ImapSecurity = MailSecurity.None,
            ImapUser = ImapUser,
            CreatedAt = h.Clock.Now,
        };
        db.Add(department);
        await db.SaveChangesAsync();
        department.ImapPassword = MailPollHandler.Protect(box, department, password);
        await db.SaveChangesAsync();
        return department.Id;
    });

    private static Task<Department> Read(CoreHarness h, long id) => h.Query(db => db.Set<Department>().AsNoTracking().SingleAsync(d => d.Id == id));

    /// <summary>Un turno del planificador, dos minutos después del anterior: encola la lectura y la corre.</summary>
    private static async Task Poll(CoreHarness h)
    {
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.Tick();
        await h.RunJobs();
    }

    [TestMethod]
    public async Task Casilla_LeeEnSoloLecturaAvanzaElCursorYNoRepite()
    {
        await using var imap = new FakeImapServer(ImapUser, ImapPassword);
        for (var i = 1; i <= 3; i++)
        {
            imap.Add(Mail($"cliente{i}@example.org", $"Consulta {i}", $"Mensaje {i}"));
        }

        var box = Box();
        await using var h = await SupportTests.Start(secrets: box);
        var department = await Mailbox(h, box, imap);

        await Poll(h);

        var state = await Read(h, department);
        Assert.AreEqual((1L, 3L, (string?)null), (state.ImapUidValidity, state.ImapLastUid, state.MailError));
        Assert.IsNotNull(state.MailCheckedAt);
        Assert.HasCount(3, await Tickets(h));
        Assert.Contains("EXAMINE", imap.Commands);
        Assert.IsFalse(imap.Commands.Any(c => c is "SELECT" or "STORE" or "UID STORE" or "EXPUNGE" or "UID EXPUNGE" or "UID MOVE" or "UID COPY"), string.Join(", ", imap.Commands));

        imap.Add(Mail("cliente4@example.org", "Consulta 4", "Mensaje 4"));
        await Poll(h);
        await Poll(h);

        Assert.HasCount(4, await Tickets(h));
        Assert.AreEqual(4L, (await Read(h, department)).ImapLastUid);
        Assert.AreEqual(4, await h.Query(db => db.Set<InboundEmail>().CountAsync()));
    }

    [TestMethod]
    public async Task Casilla_UnaLecturaPorCasillaYPorTurno()
    {
        await using var imap = new FakeImapServer(ImapUser, ImapPassword);
        var box = Box();
        await using var h = await SupportTests.Start(secrets: box);
        var department = await Mailbox(h, box, imap);
        await h.Query(async db =>
        {
            db.Add(new Department { Name = "Sin correo", CreatedAt = h.Clock.Now });
            return await db.SaveChangesAsync();
        });

        await h.Tick();
        await h.Tick();

        var jobs = await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.SupportMail).ToListAsync());
        Assert.HasCount(1, jobs, "Dos nodos en el mismo turno encolan una sola lectura.");
        Assert.AreEqual(MailPollHandler.Entity(department), jobs[0].EntityKey);
    }

    [TestMethod]
    public async Task Casilla_CambioDeUidValidity_ReleeSinDuplicar()
    {
        await using var imap = new FakeImapServer(ImapUser, ImapPassword);
        imap.Add(Mail("uno@example.org", "Uno", "Primero"));
        imap.Add(Mail("dos@example.org", "Dos", "Segundo"));
        var box = Box();
        await using var h = await SupportTests.Start(secrets: box);
        var department = await Mailbox(h, box, imap);
        await Poll(h);

        imap.Renumber(7);
        imap.Add(Mail("tres@example.org", "Tres", "Tercero"));
        await Poll(h);

        Assert.HasCount(3, await Tickets(h));
        var state = await Read(h, department);
        Assert.AreEqual((7L, 3L), (state.ImapUidValidity, state.ImapLastUid));
    }

    [TestMethod]
    public async Task Casilla_CorreoQuePasaElTope_QuedaRechazadoSinBajarlo()
    {
        await using var imap = new FakeImapServer(ImapUser, ImapPassword);
        imap.Add(Mail("pesado@example.org", "Video", "Te mando el video", m =>
        {
            var body = new BodyBuilder { TextBody = "Te mando el video" };
            body.Attachments.Add("video.pdf", new byte[8000]);
            m.Body = body.ToMessageBody();
        }));
        imap.Add(Mail("liviano@example.org", "Hola", "Corto"));
        var box = Box();
        await using var h = await SupportTests.Start(o => o.MaxEmailBytes = 4000, secrets: box);
        var department = await Mailbox(h, box, imap);

        await Poll(h);

        var log = await h.Query(db => db.Set<InboundEmail>().OrderBy(e => e.Id).ToListAsync());
        Assert.AreEqual((InboundOutcome.Rejected, "pesado@example.org"), (log[0].Outcome, log[0].Sender));
        Assert.Contains("tope", log[0].Reason!);
        Assert.AreEqual(InboundOutcome.Opened, log[1].Outcome);
        Assert.HasCount(1, await Tickets(h));
        Assert.AreEqual(2L, (await Read(h, department)).ImapLastUid, "Un correo que no se procesa no traba la casilla.");
    }

    [TestMethod]
    public async Task Casilla_ClaveMalaOServidorCambiado_QuedaElErrorParaElStaff()
    {
        await using var imap = new FakeImapServer(ImapUser, ImapPassword);
        imap.Add(Mail("uno@example.org", "Uno", "Primero"));
        var box = Box();
        await using var h = await SupportTests.Start(secrets: box);
        var wrong = await Mailbox(h, box, imap, "otra-clave");

        await Poll(h);

        var state = await Read(h, wrong);
        Assert.IsNotNull(state.MailError);
        Assert.AreEqual(0L, state.ImapLastUid);
        Assert.IsEmpty(await Tickets(h));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.SupportMail && j.Status == JobStatus.Dead)), "La falla queda en el departamento y el próximo turno reintenta.");

        // La clave quedó atada al servidor: apuntar la casilla a otro host obliga a cargarla de nuevo.
        var moved = await Mailbox(h, box, imap);
        await h.Query(db => db.Set<Department>().Where(d => d.Id == moved).ExecuteUpdateAsync(s => s.SetProperty(d => d.ImapHost, "localhost")));
        var logins = imap.Commands.Count(c => c == "LOGIN");
        await Poll(h);

        Assert.IsNotNull((await Read(h, moved)).MailError);
        Assert.AreEqual(logins + 1, imap.Commands.Count(c => c == "LOGIN"), "Solo la otra casilla volvió a probar: la movida ni se conectó.");
    }

    /// <summary>
    /// Contra una casilla real, solo si están las variables (BILLINGOSS_TEST_IMAP_HOST, _PORT, _USER, _PASSWORD y
    /// _SECURITY con SslOnConnect o StartTls). Abre en solo lectura y no marca nada: los correos quedan como estaban.
    /// </summary>
    [TestMethod]
    public async Task Casilla_Real_LeeSinErrores()
    {
        var host = Environment.GetEnvironmentVariable("BILLINGOSS_TEST_IMAP_HOST");
        var user = Environment.GetEnvironmentVariable("BILLINGOSS_TEST_IMAP_USER");
        var password = Environment.GetEnvironmentVariable("BILLINGOSS_TEST_IMAP_PASSWORD");
        if (host is null || user is null || password is null)
        {
            Assert.Inconclusive("Sin BILLINGOSS_TEST_IMAP_HOST, _USER y _PASSWORD no se prueba contra una casilla real.");
        }

        var port = int.TryParse(Environment.GetEnvironmentVariable("BILLINGOSS_TEST_IMAP_PORT"), out var p) ? p : 993;
        var security = Enum.TryParse<MailSecurity>(Environment.GetEnvironmentVariable("BILLINGOSS_TEST_IMAP_SECURITY"), out var s) ? s : MailSecurity.SslOnConnect;
        var box = Box();
        await using var h = await SupportTests.Start(o => o.MaxEmailsPerSenderPerHour = 1000, secrets: box);
        var department = await h.Query(async db =>
        {
            var d = new Department { Name = "Casilla real", MailEnabled = true, ImapHost = host, ImapPort = port, ImapSecurity = security, ImapUser = user, CreatedAt = h.Clock.Now };
            db.Add(d);
            await db.SaveChangesAsync();
            d.ImapPassword = MailPollHandler.Protect(box, d, password);
            await db.SaveChangesAsync();
            return d.Id;
        });

        await Poll(h);

        var state = await Read(h, department);
        Assert.IsNull(state.MailError, state.MailError);
        Assert.IsNotNull(state.MailCheckedAt);
    }
}
