using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Pantallas de cobros de punta a punta (secciones 4.3 a 4.6 del diseño de cobros): pagar con pasarela o por
/// transferencia, medios guardados, condiciones del débito y el asistente y las transferencias del admin.
/// </summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base, como UiTests.
public sealed class PaymentUiTests
{
    private const string Titular = "titular@example.com";

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    /// <summary>La pasarela falsa instalada en el host de la interfaz y activada en ARS con débito automático.</summary>
    private static async Task<(TestModule Module, FakeCollector Gateway)> Gateway(UiHarness h)
    {
        var gateway = new FakeCollector();
        var module = await TestModule.Serve(gateway.Definition());
        var id = await h.Use<ModuleRegistry, long>(r => module.Register(r, gateway.Definition(), instanceSecrets: """{"api_key":"k"}"""));
        await h.Query(async db =>
        {
            (await db.ModuleInstances.SingleAsync(i => i.Id == id)).BrowserHosts = """["pay.example.com"]""";
            db.GatewaySetups.Add(new GatewaySetup(id, "Tarjeta de prueba", DateTimeOffset.UtcNow) { Enabled = true, AutoCharge = true, Currencies = ["ARS"], Environment = "test", LastTestOk = true, LastTestAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return true;
        });
        return (module, gateway);
    }

    private static Task<long> BankAccount(UiHarness h) =>
        h.Use<UseCases.TransferService, long>(t => t.SaveAccount(null, new UseCases.TransferAccountInput("Banco de prueba", "Ejemplo SRL", "30712345678", "0170099220000012345678", "ejemplo.cobros", "ARS")));

    [TestMethod]
    public async Task Pagar_OfreceLasPasarelasYLaTransferencia_YGuardarElMedioDejaLaConstancia()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser(Titular, h.AccountA);
        var (module, _) = await Gateway(h);
        await using var __ = module;
        await BankAccount(h);
        using var c = h.NewClient();
        (await c.Login(Titular)).Dispose();

        var page = await c.Html($"/pagar?factura={h.InvoiceA}");
        StringAssert.Contains(page, "Tarjeta de prueba");
        StringAssert.Contains(page, "Transferencia bancaria");
        StringAssert.Contains(page, "Autorizo a billing-oss a cobrar automáticamente con este medio");
        StringAssert.Contains(await c.Html($"/facturas/{h.InvoiceA}"), $"/pagar?factura={h.InvoiceA}");
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get($"/pagar?factura={h.InvoiceB}")).StatusCode);

        using var pay = await c.Submit($"/pagar?factura={h.InvoiceA}", $"/pagar?factura={h.InvoiceA}", new() { ["method"] = $"g{module.InstanceId}", ["save"] = "true" });

        Assert.AreEqual(HttpStatusCode.Redirect, pay.StatusCode);
        StringAssert.StartsWith(Location(pay), "https://pay.example.com/checkout?ref=");
        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.IsTrue(attempt.SaveMethod);
        var consent = JsonNode.Parse(attempt.Consent!)!;
        StringAssert.StartsWith(consent["text"]!.GetValue<string>(), "Autorizo a billing-oss");
        Assert.AreEqual("es-AR", consent["culture"]!.GetValue<string>());
        Assert.AreEqual(64, consent["text_sha256"]!.GetValue<string>().Length);
    }

    [TestMethod]
    public async Task Pagar_PorTransferencia_MuestraLosDatos_YYaTransferiDejaElAvisoConElComprobante()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser(Titular, h.AccountA);
        await BankAccount(h);
        using var c = h.NewClient();
        (await c.Login(Titular)).Dispose();

        using (var choose = await c.Submit($"/pagar?factura={h.InvoiceA}", $"/pagar?factura={h.InvoiceA}", new() { ["method"] = "transferencia" }))
        {
            Assert.AreEqual($"/pagar/transferencia?factura={h.InvoiceA}", Location(choose));
        }

        var page = await c.Html($"/pagar/transferencia?factura={h.InvoiceA}");
        StringAssert.Contains(page, "0170099220000012345678");
        StringAssert.Contains(page, "30-71234567-8");
        StringAssert.Contains(page, "12.100,00 ARS");

        var pdf = "%PDF-1.7\n1 0 obj\n"u8.ToArray();
        using var sent = await c.SubmitFiles($"/pagar/transferencia?factura={h.InvoiceA}", $"/pagar/transferencia?factura={h.InvoiceA}",
            new() { ["transferredOn"] = DateTime.UtcNow.AddHours(-3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["reference"] = "op 991" }, ("receipt", "comprobante.pdf", pdf));

        Assert.AreEqual(HttpStatusCode.OK, sent.StatusCode);
        StringAssert.Contains(await sent.Content.ReadAsStringAsync(), "Recibimos tu aviso");
        var notice = await h.Query(db => db.TransferNotices.SingleAsync());
        Assert.AreEqual(h.AccountA, notice.AccountId);
        Assert.AreEqual("op 991", notice.Reference);
        Assert.AreEqual("application/pdf", notice.ReceiptType);
        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()), "El aviso no es un pago.");
    }

    [TestMethod]
    public async Task MediosDePago_ApagarPrenderYQuitar_YLoAjenoDa404()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser(Titular, h.AccountA);
        var (module, _) = await Gateway(h);
        await using var __ = module;
        var (mine, foreign) = await h.Query(async db =>
        {
            var a = new SavedPaymentMethod(h.AccountA, module.InstanceId, "cus_a", "pm_a", "{}", DateTimeOffset.UtcNow);
            a.Describe("card", "visa", "4242", 12, 2030, DateTimeOffset.UtcNow);
            a.UseForAutoCharge(true, DateTimeOffset.UtcNow);
            var b = new SavedPaymentMethod(h.AccountB, module.InstanceId, "cus_b", "pm_b", "{}", DateTimeOffset.UtcNow);
            b.Describe("card", "mastercard", "5555", 12, 2030, DateTimeOffset.UtcNow);
            db.SavedPaymentMethods.AddRange(a, b);
            await db.SaveChangesAsync();
            return (a.Id, b.Id);
        });
        using var c = h.NewClient();
        (await c.Login(Titular)).Dispose();
        const string page = "/cuenta/medios-de-pago";

        var html = await c.Html(page);
        StringAssert.Contains(html, "Visa •••• 4242");
        Assert.IsFalse(html.Contains("5555", StringComparison.Ordinal));

        (await c.Submit(page, $"{page}?handler=AutoCharge", new() { ["id"] = $"{mine}", ["on"] = "false" })).Dispose();
        Assert.IsFalse((await h.Query(db => db.SavedPaymentMethods.SingleAsync(m => m.Id == mine))).AutoCharge);

        using (var noConsent = await c.Submit(page, $"{page}?handler=AutoCharge", new() { ["id"] = $"{mine}", ["on"] = "true" }))
        {
            StringAssert.Contains(await noConsent.Content.ReadAsStringAsync(), "tildá la casilla de autorización");
        }

        (await c.Submit(page, $"{page}?handler=AutoCharge", new() { ["id"] = $"{mine}", ["on"] = "true", ["consent"] = "true" })).Dispose();
        Assert.IsTrue((await h.Query(db => db.SavedPaymentMethods.SingleAsync(m => m.Id == mine))).AutoCharge);
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "payments.autocharge_on")));

        using (var other = await c.Submit(page, $"{page}?handler=Remove", new() { ["id"] = $"{foreign}" }))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
        }

        using (var removed = await c.Submit(page, $"{page}?handler=Remove", new() { ["id"] = $"{mine}" }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, removed.StatusCode);
        }

        var method = await h.Query(db => db.SavedPaymentMethods.SingleAsync(m => m.Id == mine));
        Assert.AreEqual(SavedMethodStatus.Deleted, method.Status);
        Assert.IsFalse(method.AutoCharge);
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.MethodDelete)));
        Assert.AreEqual(SavedMethodStatus.Active, (await h.Query(db => db.SavedPaymentMethods.SingleAsync(m => m.Id == foreign))).Status);
    }

    [TestMethod]
    public async Task MediosDePago_ConLasSuscripcionesApagadas_UnaVigenteSeVeYSeDesactiva()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser(Titular, h.AccountA);
        var (module, _) = await Gateway(h);
        await using var __ = module;
        var subscription = await h.Query(async db =>
        {
            var s = new GatewaySubscription(h.AccountA, module.InstanceId, Currency.Ars, 12100m, "ref-sub", "{}", DateTimeOffset.UtcNow);
            s.Created("sub-9", "https://pay.example.com/sub", DateTimeOffset.UtcNow);
            s.Observe(SubscriptionStatus.Active, DateTimeOffset.UtcNow);
            db.GatewaySubscriptions.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        using var c = h.NewClient();
        (await c.Login(Titular)).Dispose();
        const string page = "/cuenta/medios-de-pago";

        // Payments:SubscriptionsEnabled está apagado: no se ofrecen, pero la que ya cobra se ve y se puede desactivar.
        StringAssert.Contains(await c.Html(page), "Desactivar el débito");
        using (var off = await c.Submit(page, $"{page}?handler=Unsubscribe", new() { ["id"] = $"{subscription}" }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, off.StatusCode);
        }

        Assert.AreEqual(SubscriptionStatus.Cancelled, (await h.Query(db => db.GatewaySubscriptions.SingleAsync())).Status);
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.SubscriptionCancel)));
    }

    [TestMethod]
    public async Task VueltaDeLaPasarela_MuestraElEstadoSinSesionYNoCambiaNada()
    {
        await using var h = await UiHarness.Start();
        var (module, _) = await Gateway(h);
        await using var __ = module;
        await h.Query(async db =>
        {
            db.PaymentAttempts.Add(new PaymentAttempt(h.AccountA, module.InstanceId, Currency.Ars, 12100m, [h.InvoiceA], "ref-vuelta", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return true;
        });
        using var c = h.NewClient();

        using (var waiting = await c.Get("/pay/return/ref-vuelta"))
        {
            Assert.AreEqual(HttpStatusCode.OK, waiting.StatusCode);
            StringAssert.Contains(await waiting.Content.ReadAsStringAsync(), "Estamos esperando la confirmación");
            Assert.AreEqual("15", waiting.Headers.GetValues("Refresh").Single());
            Assert.AreEqual("no-referrer", waiting.Headers.GetValues("Referrer-Policy").Single());
        }

        StringAssert.Contains(await c.Html("/pay/return/ref-vuelta?cancelado"), "Cancelaste el pago");
        await h.Query(async db =>
        {
            (await db.PaymentAttempts.SingleAsync()).Fail("rejected:card_declined", "later", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return true;
        });
        var failed = await c.Html("/pay/return/ref-vuelta");
        StringAssert.Contains(failed, "El pago no se aprobó");
        StringAssert.Contains(failed, $"/pagar?factura={h.InvoiceA}");
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get("/pay/return/no-existe")).StatusCode);
    }

    [TestMethod]
    public async Task CondicionesDelDebito_ALaVistaSinIniciarSesion()
    {
        await using var h = await UiHarness.Start();
        using var c = h.NewClient();

        var html = await c.Html("/debito-automatico");

        StringAssert.Contains(html, "El día del vencimiento");
        StringAssert.Contains(html, "5 días después");
    }

    [TestMethod]
    public async Task Asistente_SinPermisoNoEntra_YConElPermisoCreaConfiguraPruebaYActiva()
    {
        await using var h = await UiHarness.Start();
        var gateway = new FakeCollector();
        await using var module = await TestModule.Serve(gateway.Definition());
        var installation = await h.Use<ModuleRegistry, long>(async r => (await r.Install("cobros", module.Url, module.Signing, gateway.Manifest)).Id);
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("cobros@example.com", StaffPermissions.PaymentsManage);

        using (var reader = await UiTests.StaffSession(h, "lector@example.com"))
        {
            using var denied = await reader.Get("/admin/cobros");
            StringAssert.Contains(Location(denied), "/estado/403");
        }

        using var c = await UiTests.StaffSession(h, "cobros@example.com");
        StringAssert.Contains(await c.Html("/admin/cobros"), "Cobros de prueba");
        using var created = await c.Submit("/admin/cobros", "/admin/cobros?handler=Create", new() { ["installation"] = $"{installation}", ["environment"] = "test" });
        var wizard = Location(created);
        StringAssert.StartsWith(wizard, "/admin/cobros/");
        var id = long.Parse(wizard["/admin/cobros/".Length..], System.Globalization.CultureInfo.InvariantCulture);

        var html = await c.Html(wizard);
        StringAssert.Contains(html, "api_key");
        StringAssert.Contains(html, $"https://billing.example.com/webhooks/{id}");

        (await c.Submit(wizard, $"{wizard}?handler=Credentials", new() { ["api_key"] = "sk_test_ui", ["account_name"] = "Ejemplo" })).Dispose();
        Assert.IsFalse((await c.Html(wizard)).Contains("sk_test_ui", StringComparison.Ordinal), "Un secreto guardado no se vuelve a mostrar.");
        (await c.Submit(wizard, $"{wizard}?handler=Test", [])).Dispose();
        (await c.Submit(wizard, $"{wizard}?handler=Activate", new() { ["enabled"] = "true", ["displayName"] = "Tarjeta", ["position"] = "1", ["currency"] = "ARS", ["autoCharge"] = "true" })).Dispose();

        var setup = await h.Query(db => db.GatewaySetups.SingleAsync(s => s.ModuleInstanceId == id));
        Assert.IsTrue(setup.LastTestOk);
        Assert.IsTrue(setup.Offers(Currency.Ars));
        Assert.IsTrue(setup.AutoCharge);
        Assert.AreEqual($"https://billing.example.com/webhooks/{id}", gateway.Of("test").Last().Input["webhook_url"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Transferencias_ImportaElExtracto_YElStaffImputaDesdeLaBandeja()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("cobros@example.com", StaffPermissions.PaymentsManage);
        using var c = await UiTests.StaffSession(h, "cobros@example.com");
        const string page = "/admin/cobros/transferencias";
        const string accounts = "/admin/cobros/cuentas-para-transferir";
        StringAssert.Contains(await c.Html(page), accounts, "Sin cuentas, la bandeja manda a cargar una.");

        using (var saved = await c.Submit(accounts, accounts, new()
        {
            ["bank"] = "Banco de prueba", ["holder"] = "Ejemplo SRL", ["cuit"] = "30-71234567-8", ["cbu"] = "0170099220000012345678",
            ["alias"] = "ejemplo.cobros", ["currency"] = "ARS", ["active"] = "true", ["csvMap"] = "{}",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode);
        }

        StringAssert.Contains(await c.Html(accounts), "0170099220000012345678");
        var bank = await h.Query(db => db.TransferAccounts.Select(a => a.Id).SingleAsync());
        var csv = Encoding.UTF8.GetBytes("fecha,importe,referencia,cuit,ordenante,concepto\n02/11/2026,\"12.100,00\",TRF-77,27999888777,Otra persona,FAC\n");
        using (var imported = await c.SubmitFiles(page, $"{page}?handler=Import", new() { ["account"] = $"{bank}" }, ("statement", "extracto.csv", csv)))
        {
            StringAssert.Contains(await imported.Content.ReadAsStringAsync(), "1 movimientos nuevos");
        }

        var movement = await h.Query(db => db.BankMovements.SingleAsync());
        StringAssert.Contains(await c.Html(page), "TRF-77");

        (await c.Submit(page, $"{page}?handler=Apply", new() { ["movement"] = $"{movement.Id}", ["account"] = $"{h.AccountA}", ["mode"] = "viejas" })).Dispose();

        Assert.AreEqual(MovementStatus.Applied, (await h.Query(db => db.BankMovements.SingleAsync())).Status);
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == h.InvoiceA))).Pending);
        Assert.IsFalse((await c.Html(page)).Contains("TRF-77", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CobrosAutomaticos_ListaLosDebitosConSuEstado_FiltraYExporta_YElMenuLosCuelgaDeFacturacion()
    {
        await using var h = await UiHarness.Start();
        var (module, _) = await Gateway(h);
        await using var __ = module;
        await h.Query(async db =>
        {
            var card = new SavedPaymentMethod(h.AccountA, module.InstanceId, "cus_a", "pm_a", "{}", DateTimeOffset.UtcNow);
            card.Describe("card", "visa", "4242", 12, 2030, DateTimeOffset.UtcNow);
            card.UseForAutoCharge(true, DateTimeOffset.UtcNow);
            db.SavedPaymentMethods.Add(card);
            await db.SaveChangesAsync();
            var declined = PaymentAttempt.Automatic(h.AccountA, module.InstanceId, Currency.Ars, 12100m, h.InvoiceA, card.Id, "ref-rechazado", DateTimeOffset.UtcNow.AddDays(-1));
            declined.Fail("rejected:card_declined", "later", DateTimeOffset.UtcNow);
            var uncertain = PaymentAttempt.Automatic(h.AccountA, module.InstanceId, Currency.Ars, 12100m, h.InvoiceA, card.Id, "ref-incierto", DateTimeOffset.UtcNow);
            uncertain.Observe("uncertain:outcome_unknown", DateTimeOffset.UtcNow);
            db.PaymentAttempts.AddRange(declined, uncertain);
            await db.SaveChangesAsync();
            return true;
        });
        await h.Use<AuditLog, bool>(async a =>
        {
            await a.Record("payments.autocharge_stopped", $"account:{h.AccountA}", new { reason = "dispute" });
            return true;
        });
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("cobros@example.com", StaffPermissions.PaymentsManage);
        const string page = "/admin/cobros/automaticos";

        using (var reader = await UiTests.StaffSession(h, "lector@example.com"))
        {
            Assert.IsFalse((await reader.Html("/admin")).Contains(page, StringComparison.Ordinal), "Sin el permiso, el menú no muestra los cobros.");
            using var denied = await reader.Get(page);
            StringAssert.Contains(Location(denied), "/estado/403");
        }

        using var c = await UiTests.StaffSession(h, "cobros@example.com");
        var html = await c.Html(page);
        StringAssert.Contains(html, "<summary>Facturación</summary>");
        StringAssert.Contains(html, $"href=\"{page}\" aria-current=\"page\"");
        StringAssert.Contains(html, "href=\"/admin/cobros\"");
        StringAssert.Contains(html, "Visa •••• 4242 · Tarjeta de prueba");
        StringAssert.Contains(html, "card_declined");
        StringAssert.Contains(html, "Sin respuesta cierta");
        StringAssert.Contains(html, "Débitos apagados por una disputa");

        var uncertainOnly = await c.Html($"{page}?estado=inciertos");
        StringAssert.Contains(uncertainOnly, "Sin respuesta cierta");
        Assert.IsFalse(uncertainOnly.Contains("card_declined", StringComparison.Ordinal));

        using var csv = await c.Get($"{page}?estado=rechazados&csv=true");
        Assert.AreEqual("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var lines = (await csv.Content.ReadAsStringAsync()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length, "El encabezado y el rechazado.");
        StringAssert.StartsWith(lines[0], "id,fecha,cuenta");
        StringAssert.Contains(lines[1], "card_declined");

        await h.PortalUser(Titular, h.AccountA);
        using var portal = h.NewClient();
        (await portal.Login(Titular)).Dispose();
        var home = await portal.Html("/");
        var billing = home.IndexOf("<summary>Facturación</summary>", StringComparison.Ordinal);
        Assert.IsTrue(billing > 0 && home.IndexOf("/cuenta/medios-de-pago", billing, StringComparison.Ordinal) > billing, "Medios de pago cuelga de Facturación.");
    }

    [TestMethod]
    public async Task RegistroDePasarelas_MuestraLosAvisosTalComoLlegaron_FiltraYExplicaLosInvalidos()
    {
        await using var h = await UiHarness.Start();
        var (module, _) = await Gateway(h);
        await using var __ = module;
        var (verified, invalid) = await h.Query(async db =>
        {
            var ok = new WebhookReceipt
            {
                ModuleInstanceId = module.InstanceId, Headers = """{"x-test-signature":"ok"}""", Query = "{}",
                Body = Encoding.UTF8.GetBytes("""{"id":"evt-77","kind":"payment","ref":"pay-77"}"""), Status = ReceiptStatus.Verified, ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            };
            var bad = new WebhookReceipt
            {
                ModuleInstanceId = module.InstanceId, Headers = """{"x-test-signature":"otra"}""", Query = """{"topic":"payment"}""",
                Body = Encoding.UTF8.GetBytes("""{"id":"evt-78"}"""), Status = ReceiptStatus.Invalid, ReceivedAt = DateTimeOffset.UtcNow,
            };
            db.WebhookReceipts.AddRange(ok, bad);
            await db.SaveChangesAsync();
            db.GatewayEvents.Add(new GatewayEvent { ModuleInstanceId = module.InstanceId, EventId = "evt-77", Type = "payment.updated", PaymentExternalId = "pay-77", ReceiptId = ok.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return (ok.Id, bad.Id);
        });
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("cobros@example.com", StaffPermissions.PaymentsManage);
        const string page = "/admin/registro-modulos";

        using (var reader = await UiTests.StaffSession(h, "lector@example.com"))
        {
            Assert.IsFalse((await reader.Html("/admin")).Contains(page, StringComparison.Ordinal), "Sin el permiso, el menú no muestra el registro.");
            using var denied = await reader.Get(page);
            StringAssert.Contains(Location(denied), "/estado/403");
        }

        using var c = await UiTests.StaffSession(h, "cobros@example.com");
        var html = await c.Html(page);

        // En Facturación, como en el menú de la sección 10.2: Registro de pasarelas.
        var item = html.IndexOf($"href=\"{page}?tipo=pasarelas\" aria-current=\"page\"", StringComparison.Ordinal);
        Assert.IsTrue(item > html.IndexOf("<summary>Facturación</summary>", StringComparison.Ordinal), "El registro cuelga de Facturación.");
        Assert.IsTrue(html.IndexOf("<summary>Configuración</summary>", StringComparison.Ordinal) > item, "Antes de Configuración.");
        StringAssert.Contains(html, "Registro de pasarelas");
        StringAssert.Contains(html, "Tarjeta de prueba");
        StringAssert.Contains(html, "Verificado");
        StringAssert.Contains(html, "Inválido");
        StringAssert.Contains(html, "payment.updated");

        var invalidOnly = await c.Html($"{page}?estado=invalidos");
        StringAssert.Contains(invalidOnly, $"aviso={invalid}");
        Assert.IsFalse(invalidOnly.Contains($"aviso={verified}", StringComparison.Ordinal));

        var detail = await c.Html($"{page}?aviso={invalid}");
        StringAssert.Contains(detail, "el secreto de los avisos de la instancia");
        StringAssert.Contains(detail, "&quot;topic&quot;: &quot;payment&quot;");
        StringAssert.Contains(detail, "evt-78");
        StringAssert.Contains(await c.Html($"{page}?aviso={verified}"), "pay-77");

        using var missing = await c.Get($"{page}?aviso={invalid + 1000}");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
