using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

[TestClass]
public sealed class NotificationTests
{
    private static Task<CoreHarness> Start(FakeSmtpServer? smtp = null, Action<NotificationOptions>? configure = null, SecretBox? box = null) =>
        CoreHarness.Start(box, notifications: o =>
        {
            if (smtp is not null)
            {
                o.Send = true;
                o.ProductionEnabled = true;
                o.Host = "127.0.0.1";
                o.Port = smtp.Port;
                o.Security = SecureSocketOptions.None;
                o.User = "avisos@example.com";
                o.Password = "prueba";
                o.From = "avisos@example.com";
            }

            configure?.Invoke(o);
        });

    private static Task<PlacedOrder> Order(CoreHarness h) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId, Label: "vps1.example.com")]));

    private static Task<long> Pay(CoreHarness h, string externalId = "T-1") =>
        h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", externalId, Currency.Ars, 12100m, PaymentStatus.Approved)));

    private static Task<List<OutboundEmail>> Emails(CoreHarness h) =>
        h.Query(db => db.Set<OutboundEmail>().OrderBy(e => e.Id).ToListAsync());

    private static Dictionary<string, object?> Model(string name) => new()
    {
        ["account_name"] = name,
        ["number"] = "7",
        ["due_date"] = "09/11/2026",
        ["pending"] = "ARS 12.100,00",
        ["lines"] = new List<Dictionary<string, object?>> { new() { ["description"] = "VPS <b>2 GB</b>", ["amount"] = "ARS 12.100,00" } },
    };

    [TestMethod]
    public async Task Catalogo_CadaAvisoTienePlantillaEnCadaIdioma_YCodificaHtml()
    {
        await using var h = await Start();

        foreach (var key in TemplateRenderer.Keys.Where(k => k != TemplateRenderer.Layout))
        {
            foreach (var culture in TemplateRenderer.Cultures)
            {
                var email = await h.Use<TemplateRenderer, RenderedEmail>(r => r.Render(key, culture, Model("<script>x</script>")));
                Assert.IsFalse(string.IsNullOrWhiteSpace(email.Subject), $"{key} {culture} sin asunto");
                Assert.Contains($"lang=\"{culture}\"", email.Html);
                Assert.DoesNotContain("<script>", email.Html);
            }
        }

        var es = await h.Use<TemplateRenderer, RenderedEmail>(r => r.Render(TemplateKeys.InvoiceIssued, "es-AR", Model("Ana & Cía")));
        var en = await h.Use<TemplateRenderer, RenderedEmail>(r => r.Render(TemplateKeys.InvoiceIssued, "en", Model("Ana & Cía")));
        Assert.AreEqual("Factura N.º 7 de", es.Subject);
        Assert.Contains("Hola, Ana &amp; C", es.Html);
        Assert.Contains("VPS &lt;b&gt;2 GB&lt;/b&gt;", es.Html);
        Assert.Contains("Invoice #7", en.Subject);
        Assert.AreEqual("es-AR", TemplateRenderer.CultureFor("es"));
        Assert.AreEqual("en", TemplateRenderer.CultureFor("en-US"));
    }

    [TestMethod]
    public async Task PlantillaEditada_SeUsa_YSiSePasaDeLosTopesVaLaDeFabrica()
    {
        await using var h = await Start();
        async Task<RenderedEmail> Edit(string subject, string body)
        {
            await h.Db(async db =>
            {
                await db.Set<Template>().ExecuteDeleteAsync();
                db.Add(new Template { Key = TemplateKeys.InvoiceReminder, Culture = "es-AR", Subject = subject, Body = body });
                await db.SaveChangesAsync();
            });
            return await h.Use<TemplateRenderer, RenderedEmail>(r => r.Render(TemplateKeys.InvoiceReminder, "es-AR", Model("Ana")));
        }

        var custom = await Edit("Ojo con la {{ number }}", "<p>Debés {{ pending }}</p>");
        Assert.AreEqual("Ojo con la 7", custom.Subject);
        Assert.Contains("<p>Debés ARS 12.100,00</p>", custom.Html);

        // Pasos, tamaño de una variable, tamaño de la salida, sintaxis o archivos: todo cae a la de fábrica.
        string[] bombs =
        [
            "{% for i in (1..1000) %}{% for j in (1..1000) %}x{% endfor %}{% endfor %}",
            "{% assign s = 'xxxxxxxxxxxxxxxx' %}{% for i in (1..20) %}{% assign s = s | append: s %}{% endfor %}",
            "{% for i in (1..5000) %}" + new string('x', 400) + "{% endfor %}",
            "{% if %}",
            "{% include 'appsettings.json' %}",
        ];
        foreach (var bomb in bombs)
        {
            var fallback = await Edit("Ojo", bomb);
            Assert.AreEqual("Recordatorio: la factura N.º 7 vence el 09/11/2026", fallback.Subject, bomb);
        }
    }

    [TestMethod]
    public async Task SinEnvio_LosAvisosQuedanRegistradosYNoSalen()
    {
        await using var h = await Start();
        await Order(h);
        await Pay(h);
        await h.RunJobs();

        var emails = await Emails(h);
        CollectionAssert.AreEquivalent(
            new[] { TemplateKeys.InvoiceIssued, TemplateKeys.PaymentReceived, TemplateKeys.ServiceActivated },
            emails.Select(e => e.Template).ToArray());
        Assert.IsTrue(emails.All(e => e.Status == EmailStatus.Suppressed && e.Recipient == "cliente@example.com" && e.Culture == "es-AR"));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.SendEmail)));
        var activated = emails.Single(e => e.Template == TemplateKeys.ServiceActivated);
        Assert.AreEqual("Tu servicio VPS 2 GB (vps1.example.com) ya está activo", activated.Subject);
    }

    [TestMethod]
    public async Task ConEnvio_CadaAvisoSaleUnaVezPorSmtp()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await Start(smtp);
        await Order(h);
        await Pay(h);
        await h.RunJobs();
        await h.RunJobs();

        var emails = await Emails(h);
        Assert.HasCount(3, emails);
        Assert.IsTrue(emails.All(e => e.Status == EmailStatus.Sent && e.SentAt is not null));
        Assert.HasCount(3, smtp.Messages);
        var payment = smtp.Messages.Single(m => m.Subject == "Recibimos tu pago de ARS 12.100,00");
        Assert.AreEqual("avisos@example.com", payment.From.Mailboxes.Single().Address);
        Assert.AreEqual("cliente@example.com", payment.To.Mailboxes.Single().Address);
        Assert.AreEqual(emails.Single(e => e.Template == TemplateKeys.PaymentReceived).MessageId, payment.MessageId);
        Assert.Contains("Factura N.º 1", payment.HtmlBody!);
        Assert.IsTrue(smtp.Users.All(u => u == "avisos@example.com"));
    }

    [TestMethod]
    public async Task ServidorCaido_ReintentaConEspera_YNoPierdeElAviso()
    {
        await using var smtp = new FakeSmtpServer { Down = true };
        await using var h = await Start(smtp);
        await Order(h);
        await h.RunJobs();

        var pending = (await Emails(h)).Single();
        Assert.AreEqual(EmailStatus.Pending, pending.Status);
        Assert.IsNotNull(pending.LastError);
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.SendEmail));
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(Notifier.SendAttempts, job.MaxAttempts);

        smtp.Down = false;
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.RunJobs();

        Assert.AreEqual(EmailStatus.Sent, (await Emails(h)).Single().Status);
        Assert.HasCount(1, smtp.Messages);
    }

    [TestMethod]
    public async Task CasillaRechazada_QuedaFallida_SinReintentos()
    {
        await using var smtp = new FakeSmtpServer { Reject = "cliente@" };
        await using var h = await Start(smtp);
        await Order(h);
        await h.RunJobs();

        var email = (await Emails(h)).Single();
        Assert.AreEqual(EmailStatus.Failed, email.Status);
        Assert.Contains("550", email.LastError!);
        Assert.AreEqual(JobStatus.Dead, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.SendEmail))).Status);
        Assert.IsEmpty(smtp.Messages);
    }

    [TestMethod]
    public async Task VariosNodosALaVez_MandanCadaCorreoUnaSolaVez()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await Start(smtp);
        for (var i = 0; i < 20; i++)
        {
            var key = $"password-reset:{i}";
            var queued = await h.Use<Notifier, bool>(n => n.Queue(TemplateKeys.PasswordReset, $"usuario{i}@example.com", "en",
                new Dictionary<string, object?> { ["link"] = "https://billing.example.com/reset/abc", ["minutes"] = 30 }, key));
            Assert.IsTrue(queued);
        }

        // Volver a pedir el mismo aviso no lo arma de nuevo.
        Assert.IsFalse(await h.Use<Notifier, bool>(n => n.Queue(TemplateKeys.PasswordReset, "usuario0@example.com", "en", new Dictionary<string, object?>(), "password-reset:0")));

        await Task.WhenAll(h.RunJobs(), h.RunJobs(), h.RunJobs(), h.RunJobs());

        Assert.HasCount(20, smtp.Messages);
        Assert.HasCount(20, smtp.Messages.Select(m => m.MessageId).Distinct().ToList());
        Assert.IsTrue((await Emails(h)).All(e => e.Status == EmailStatus.Sent));
        Assert.Contains("expires in 30 minutes", smtp.Messages.First().HtmlBody!);
    }

    [TestMethod]
    public async Task ListaPermitida_SoloEsasCasillasReciben()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await Start(smtp, o => o.OnlyTo = ["staff@example.com"]);
        await Order(h);
        await h.Use<Notifier, bool>(n => n.Queue(TemplateKeys.PasswordReset, "Staff@example.com", "es", new Dictionary<string, object?>(), "prueba-staff"));
        await h.RunJobs();

        var emails = await Emails(h);
        Assert.AreEqual(EmailStatus.Suppressed, emails.Single(e => e.Template == TemplateKeys.InvoiceIssued).Status);
        Assert.AreEqual(EmailStatus.Sent, emails.Single(e => e.Template == TemplateKeys.PasswordReset).Status);
        Assert.AreEqual("Staff@example.com", smtp.Messages.Single().To.Mailboxes.Single().Address);
    }

    [TestMethod]
    public async Task CorreosDeLaInterfaz_SalenPorLaCola_UnaConstanciaPorCodigo()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await Start(smtp);
        await h.Use<IAccountMail, bool>(async mail =>
        {
            await mail.SendConsumerCode("cliente@example.com", "es", ConsumerRequestKind.Cancellation, "123456", CancellationToken.None);
            await mail.SendConsumerReceipt("cliente@example.com", "en", ConsumerRequestKind.Withdrawal, "ARRE-abc", h.Clock.Now, CancellationToken.None);
            await mail.SendConsumerReceipt("cliente@example.com", "en", ConsumerRequestKind.Withdrawal, "ARRE-abc", h.Clock.Now, CancellationToken.None);
            return true;
        });
        await h.RunJobs();

        Assert.HasCount(2, smtp.Messages);
        var code = smtp.Messages.Single(m => m.Subject == "Código para confirmar tu pedido de baja");
        Assert.Contains("123456", code.HtmlBody!);
        Assert.Contains("30 minutos", code.HtmlBody!);
        Assert.AreEqual(1, smtp.Messages.Count(m => m.Subject == "Withdrawal receipt ARRE-abc"));
    }

    [TestMethod]
    public async Task TrabaDeProduccionApagada_NingunClienteRecibe_NiDesdeUnaBaseRestaurada()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await Start(smtp, o => o.ProductionEnabled = false);
        await Order(h);
        await h.RunJobs();
        var email = (await Emails(h)).Single();
        Assert.AreEqual(EmailStatus.Suppressed, email.Status, "Con la traba apagada no sale aunque el envío esté prendido.");

        // Un correo pendiente en una base de producción restaurada en staging: la traba se vuelve a mirar al mandar.
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE notifications.outbound_emails SET status = 'Pending'"));
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.SendEmail, new { email_id = email.Id });
            return true;
        });
        await h.RunJobs();

        Assert.IsEmpty(smtp.Messages);
        Assert.AreEqual(EmailStatus.Suppressed, (await Emails(h)).Single().Status);
    }

    [TestMethod]
    public async Task Recordatorios_UnoAntesYUnoDespuesDelVencimiento_SinRepetir()
    {
        await using var h = await Start();
        await Order(h); // Vence el 09/11.

        h.Clock.Now = h.Clock.Now.AddDays(5);
        await h.Tick();
        await h.RunJobs();
        Assert.IsEmpty((await Emails(h)).Where(e => e.Template != TemplateKeys.InvoiceIssued));

        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.Use<IEnumerable<IJobHandler>, JobResult>(handlers => handlers.Single(x => x.Kind == JobKinds.InvoiceReminders)
            .Handle(new ClaimedJob(0, JobKinds.InvoiceReminders, "{}", 1, 1), CancellationToken.None));
        var reminder = (await Emails(h)).Single(e => e.Template == TemplateKeys.InvoiceReminder);
        Assert.AreEqual("Recordatorio: la factura N.º 1 vence el 09/11/2026", reminder.Subject);

        h.Clock.Now = h.Clock.Now.AddDays(4);
        await h.Tick();
        await h.RunJobs();
        var overdue = (await Emails(h)).Single(e => e.Template == TemplateKeys.InvoiceOverdue);
        Assert.AreEqual("La factura N.º 1 está vencida", overdue.Subject);
        Assert.HasCount(3, await Emails(h));
    }

    [TestMethod]
    public async Task SuspensionPorMora_AvisaConElMotivo()
    {
        await using var h = await Start();
        await Order(h);
        await Pay(h);
        await h.RunJobs();

        h.Clock.Now = h.Clock.Now.AddDays(34);
        await h.Tick();
        await h.RunJobs();

        var suspended = (await Emails(h)).Single(e => e.Template == TemplateKeys.ServiceSuspended);
        Assert.Contains("por falta de pago", suspended.Html);
    }

    private static async Task<(CoreHarness H, TestModule Module, FakeFisco Fisco)> StartFiscal(EmissionMoment moment, Action<FakeFisco>? setup = null)
    {
        var box = TestModule.NewBox();
        var h = await Start(box: box);
        var fisco = new FakeFisco();
        setup?.Invoke(fisco);
        var module = await TestModule.Start(fisco.Definition(), h, box);
        await h.Db(async db =>
        {
            db.IssuerProfiles.Add(new IssuerProfile
            {
                TaxId = "30712345678",
                LegalName = "Proveedor de prueba",
                ModuleInstanceId = module.InstanceId,
                PointOfSale = 3,
                Moment = moment,
                TypeMap = """{"B":{"Invoice":6,"DebitNote":7,"CreditNote":8}}""",
            });
            await db.SaveChangesAsync();
        });
        return (h, module, fisco);
    }

    [TestMethod]
    public async Task FiscalAlEmitir_LaFacturaSaleUnaSolaVezConElCae()
    {
        var (h, module, _) = await StartFiscal(EmissionMoment.AtIssue);
        await using var _h = h;
        await using var _m = module;
        await Order(h);
        await h.RunJobs();

        var invoice = (await Emails(h)).Single();
        Assert.AreEqual(TemplateKeys.InvoiceIssued, invoice.Template);
        Assert.Contains("Comprobante fiscal 00003-00000001, CAE CAE1", invoice.Html);
    }

    [TestMethod]
    public async Task FiscalQueNoAutoriza_LaFacturaSaleSinCaeAlVencerLaEspera()
    {
        var (h, module, _) = await StartFiscal(EmissionMoment.AtIssue, f => f.Reject = true);
        await using var _h = h;
        await using var _m = module;
        await Order(h);
        await h.RunJobs();
        Assert.IsEmpty(await Emails(h));

        h.Clock.Now = h.Clock.Now.AddMinutes(11);
        await h.RunJobs();

        var invoice = (await Emails(h)).Single();
        Assert.AreEqual(TemplateKeys.InvoiceIssued, invoice.Template);
        Assert.DoesNotContain("CAE", invoice.Html);
    }

    [TestMethod]
    public async Task FiscalAlCobrar_LaFacturaSaleEnseguida_YElComprobanteDespuesDelPago()
    {
        var (h, module, _) = await StartFiscal(EmissionMoment.AtFirstPayment);
        await using var _h = h;
        await using var _m = module;
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(TemplateKeys.InvoiceIssued, (await Emails(h)).Single().Template);

        await Pay(h);
        await h.RunJobs();

        var fiscal = (await Emails(h)).Single(e => e.Template == TemplateKeys.FiscalDocument);
        Assert.AreEqual("Comprobante fiscal 00003-00000001 de Hornero", fiscal.Subject);
        Assert.Contains("CAE: CAE1", fiscal.Html);
        Assert.HasCount(4, await Emails(h));
    }

    [TestMethod]
    public async Task Plantillas_SeEditanPorApi()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("templates:read", "templates:write");
        HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
        {
            var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        const string Path = "/api/v1/notifications/templates/invoice-reminder/es-AR";
        using (var bad = await w.Client.SendAsync(Request(HttpMethod.Put, Path, new { subject = "Hola", body = "{% if %}" })))
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        }

        using (var missing = await w.Client.SendAsync(Request(HttpMethod.Put, "/api/v1/notifications/templates/no-existe/es-AR", new { subject = "a", body = "b" })))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var ok = await w.Client.SendAsync(Request(HttpMethod.Put, Path, new { subject = "Vence la {{ number }}", body = "<p>Pagá</p>" })))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, ok.StatusCode);
        }

        async Task<JsonNode> Reminder()
        {
            using var list = await w.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/notifications/templates"));
            var all = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray();
            Assert.HasCount(TemplateRenderer.Keys.Count * 2, all);
            return all.Single(t => t!["key"]!.GetValue<string>() == "invoice-reminder" && t["culture"]!.GetValue<string>() == "es-AR")!;
        }

        Assert.AreEqual("Vence la {{ number }}", (await Reminder())["subject"]!.GetValue<string>());
        using (var reset = await w.Client.SendAsync(Request(HttpMethod.Delete, Path)))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, reset.StatusCode);
        }

        Assert.IsFalse((await Reminder())["customized"]!.GetValue<bool>());
    }
}
