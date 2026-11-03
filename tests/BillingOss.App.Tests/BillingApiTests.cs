using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Facturación avanzada en la API propia: permisos, códigos y la forma de las respuestas.</summary>
[TestClass]
public sealed class BillingApiTests
{
    internal static async Task<(HttpStatusCode Status, JsonNode? Body)> Send(WebHarness w, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await w.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [TestMethod]
    public async Task FacturaManual_PorLaApi_BorradorEmitidoYDescartado()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("documents:write", "documents:read");
        var reader = await w.Key("documents:read");
        var lines = new[] { new { description = "Horas de soporte", quantity = 3, unit_price = 1210 }, new { description = "Cable", quantity = 1, unit_price = 100 } };

        var denied = await Send(w, HttpMethod.Post, "/api/v1/invoices", reader, new { account_id = w.AccountId, lines });
        var created = await Send(w, HttpMethod.Post, "/api/v1/invoices", token, new { account_id = w.AccountId, lines, draft = true });
        var invalid = await Send(w, HttpMethod.Post, "/api/v1/invoices", token, new { account_id = w.AccountId, lines = Array.Empty<object>() });
        var missing = await Send(w, HttpMethod.Post, "/api/v1/invoices", token, new { account_id = 999999, lines });

        Assert.AreEqual(HttpStatusCode.Forbidden, denied.Status);
        Assert.AreEqual(HttpStatusCode.Created, created.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.Status);
        var id = created.Body!["id"]!.GetValue<long>();
        var draft = await Send(w, HttpMethod.Get, $"/api/v1/documents/{id}", token);
        Assert.AreEqual("draft", draft.Body!["status"]!.GetValue<string>());
        Assert.AreEqual(2, draft.Body!["lines"]!.AsArray().Count);

        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/invoices/{id}/issue", token)).Status);
        var issued = await Send(w, HttpMethod.Get, $"/api/v1/documents/{id}", token);
        Assert.AreEqual("issued", issued.Body!["status"]!.GetValue<string>());
        Assert.AreEqual(3730m, issued.Body!["total"]!.GetValue<decimal>());
        Assert.AreEqual(2, issued.Body!["lines"]!.AsArray().Count);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Delete, $"/api/v1/invoices/{id}", token)).Status);

        var other = (await Send(w, HttpMethod.Post, "/api/v1/invoices", token, new { account_id = w.AccountId, lines, draft = true })).Body!["id"]!.GetValue<long>();
        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Delete, $"/api/v1/invoices/{other}", token)).Status);
        Assert.IsFalse(await w.Query(db => db.Documents.AnyAsync(d => d.Id == other)));
    }

    [TestMethod]
    public async Task ItemsFacturables_PorLaApi_CargaListaCancelaYFactura()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("documents:write", "documents:read");

        var first = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/billable-items", token, new { description = "Horas extra", quantity = 2, unit_price = 500 });
        var second = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/billable-items", token, new { description = "Reinstalación", quantity = 1, unit_price = 900, invoice_on = "2026-12-01" });
        var invalid = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/billable-items", token, new { description = "Descuento", quantity = 1, unit_price = -5 });
        Assert.AreEqual(HttpStatusCode.Created, first.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.Status);
        var secondId = second.Body!["id"]!.GetValue<long>();

        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/billable-items/{secondId}/cancel", token)).Status);
        var invoiced = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/billable-items/invoice", token);
        Assert.AreEqual(HttpStatusCode.OK, invoiced.Status);
        var documentId = invoiced.Body!["document_ids"]![0]!.GetValue<long>();
        Assert.AreEqual(1000m, await w.Query(db => db.Documents.Where(d => d.Id == documentId).Select(d => d.Total).SingleAsync()));

        var list = (await Send(w, HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}/billable-items", token)).Body!.AsArray();
        CollectionAssert.AreEqual(new[] { "cancelled", "invoiced" }, list.Select(i => i!["status"]!.GetValue<string>()).ToArray());
        Assert.AreEqual(documentId, list[1]!["document_id"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task SaldoAFavor_PorLaApi_AjusteConPermisoPropio_LibroYAplicacion()
    {
        await using var w = await WebHarness.Start();
        var payments = await w.Key("payments:write", "payments:read", "documents:write");
        var credit = await w.Key("credit:write");
        var invoice = (await Send(w, HttpMethod.Post, "/api/v1/invoices", payments, new { account_id = w.AccountId, lines = new[] { new { description = "Horas", quantity = 1, unit_price = 1000 } } })).Body!["id"]!.GetValue<long>();

        var denied = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/credit/adjustments", payments, new { currency = "ARS", amount = 600, reason = "Compensación" });
        var adjusted = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/credit/adjustments", credit, new { currency = "ARS", amount = 600, reason = "Compensación" });
        var negative = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/credit/adjustments", credit, new { currency = "ARS", amount = -900, reason = "Error" });

        Assert.AreEqual(HttpStatusCode.Forbidden, denied.Status);
        Assert.AreEqual(HttpStatusCode.Created, adjusted.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, negative.Status);
        StringAssert.Contains(negative.Body!["title"]!.GetValue<string>(), "no alcanza");

        var applied = await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/credit/apply", payments, new { document_ids = new[] { invoice } });
        Assert.AreEqual(HttpStatusCode.OK, applied.Status);
        Assert.AreEqual(600m, applied.Body![0]!["amount"]!.GetValue<decimal>());
        Assert.AreEqual(400m, await w.Query(db => db.Documents.Where(d => d.Id == invoice).Select(d => d.Total - d.Credited - d.Allocated).SingleAsync()));

        var ledger = await Send(w, HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}/credit", payments);
        Assert.AreEqual(0m, ledger.Body!["balances"]![0]!["balance"]!.GetValue<decimal>());
        var entries = ledger.Body!["entries"]!.AsArray();
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("Compensación", entries[1]!["reason"]!.GetValue<string>());
        StringAssert.StartsWith(entries[1]!["actor"]!.GetValue<string>(), "apikey:");
        Assert.AreEqual(InvoiceStatus.PartiallyPaid, (await w.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Status);
    }

    [TestMethod]
    public async Task Presupuestos_PorLaApi_SeArmanCambianMandanYConviertenUnaVez()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("documents:write", "documents:read");
        var reader = await w.Key("documents:read");
        var lines = new object[] { new { description = "Migración de sitios", quantity = 1, unit_price = 30000 } };

        var denied = await Send(w, HttpMethod.Post, "/api/v1/quotes", reader, new { account_id = w.AccountId, subject = "Mudanza", lines });
        var created = await Send(w, HttpMethod.Post, "/api/v1/quotes", token, new { account_id = w.AccountId, subject = "Mudanza", lines });
        var invalid = await Send(w, HttpMethod.Post, "/api/v1/quotes", token, new { account_id = w.AccountId, subject = " ", lines });
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.Status);
        Assert.AreEqual(HttpStatusCode.Created, created.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.Status);
        var id = created.Body!["id"]!.GetValue<long>();

        var changed = new object[] { lines[0], new { description = "Disco", quantity = 1, unit_price = 500, treatment = "exempt" } };
        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Put, $"/api/v1/quotes/{id}", token, new { account_id = w.AccountId, subject = "Mudanza", lines = changed })).Status);
        var quote = (await Send(w, HttpMethod.Get, $"/api/v1/quotes/{id}", reader)).Body!;
        Assert.AreEqual("draft", quote["status"]!.GetValue<string>());
        Assert.AreEqual(30500m, quote["total"]!.GetValue<decimal>());
        Assert.AreEqual("exempt", quote["lines"]![1]!["treatment"]!.GetValue<string>());

        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/quotes/{id}/send", token)).Status);
        var list = (await Send(w, HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}/quotes", reader)).Body!.AsArray();
        Assert.AreEqual("sent", list.Single()!["status"]!.GetValue<string>());

        var converted = await Send(w, HttpMethod.Post, $"/api/v1/quotes/{id}/convert", token, new { to = "invoice" });
        Assert.AreEqual(HttpStatusCode.OK, converted.Status);
        Assert.AreEqual("invoiced", converted.Body!["status"]!.GetValue<string>());
        var invoice = converted.Body!["document_id"]!.GetValue<long>();
        Assert.AreEqual(30500m, await w.Query(db => db.Documents.Where(d => d.Id == invoice).Select(d => d.Total).SingleAsync()));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Post, $"/api/v1/quotes/{id}/convert", token, new { to = "order" })).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Delete, $"/api/v1/quotes/{id}", token)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Get, "/api/v1/quotes/999999", reader)).Status);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Post, $"/api/v1/quotes/{id}/duplicate", reader)).Status);
        var copy = await Send(w, HttpMethod.Post, $"/api/v1/quotes/{id}/duplicate", token);
        Assert.AreEqual(HttpStatusCode.Created, copy.Status);
        var duplicated = (await Send(w, HttpMethod.Get, $"/api/v1/quotes/{copy.Body!["id"]!.GetValue<long>()}", reader)).Body!;
        Assert.AreEqual("draft", duplicated["status"]!.GetValue<string>());
        Assert.AreEqual(30500m, duplicated["total"]!.GetValue<decimal>());
        Assert.AreEqual(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Post, "/api/v1/quotes/999999/duplicate", token)).Status);
    }

    [TestMethod]
    public async Task RecargoAPedido_YExencionesDeLaCuenta_PorLaApi()
    {
        await using var w = await WebHarness.Start(settings: new() { ["Billing:LateFeePercent"] = "10" });
        var token = await w.Key("documents:write", "documents:read", "accounts:write");
        var hours = new[] { new { description = "Horas", quantity = 1, unit_price = 1000 } };
        var first = (await Send(w, HttpMethod.Post, "/api/v1/invoices", token, new { account_id = w.AccountId, lines = hours })).Body!["id"]!.GetValue<long>();

        // Antes del vencimiento no hay mora: el staff lo pide sin esperar a la corrida, pero con la factura vencida.
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Post, $"/api/v1/invoices/{first}/late-fee", token)).Status);
        await w.Query(db => TestDatabase.MakeOverdue(db, first, 10));
        var fee = await Send(w, HttpMethod.Post, $"/api/v1/invoices/{first}/late-fee", token);
        Assert.AreEqual(HttpStatusCode.Created, fee.Status);
        var noteId = fee.Body!["id"]!.GetValue<long>();
        Assert.AreEqual(100m, await w.Query(db => db.Documents.Where(d => d.Id == noteId).Select(d => d.Total).SingleAsync()));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Post, $"/api/v1/invoices/{first}/late-fee", token)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Post, "/api/v1/invoices/999999/late-fee", token)).Status);

        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/accounts/{w.AccountId}/billing-exemptions", token, new { no_late_fees = true, no_reminders = true })).Status);
        var account = await w.Query(db => db.Accounts.SingleAsync(a => a.Id == w.AccountId));
        Assert.IsTrue(account.NoLateFees && account.NoReminders);
        var second = (await Send(w, HttpMethod.Post, "/api/v1/invoices", token, new { account_id = w.AccountId, lines = hours })).Body!["id"]!.GetValue<long>();
        await w.Query(db => TestDatabase.MakeOverdue(db, second, 10));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Post, $"/api/v1/invoices/{second}/late-fee", token)).Status);
    }

    [TestMethod]
    public async Task Cotizaciones_PorLaApi_CualquierLlaveLasLee_YCargarPideSuPermiso()
    {
        await using var w = await WebHarness.Start();
        var reader = await w.Key("documents:read");
        var writer = await w.Key("rates:write");

        var denied = await Send(w, HttpMethod.Post, "/api/v1/exchange-rates", reader, new { currency = "USD", rate = 1500 });
        var created = await Send(w, HttpMethod.Post, "/api/v1/exchange-rates", writer, new { currency = "USD", rate = 1500.5m });
        var invalid = await Send(w, HttpMethod.Post, "/api/v1/exchange-rates", writer, new { currency = "USD", rate = -1 });
        var refresh = await Send(w, HttpMethod.Post, "/api/v1/exchange-rates/refresh", writer);
        var read = await Send(w, HttpMethod.Get, "/api/v1/exchange-rates?currency=USD", reader);

        Assert.AreEqual(HttpStatusCode.Forbidden, denied.Status);
        Assert.AreEqual(HttpStatusCode.Created, created.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.AreEqual(HttpStatusCode.Accepted, refresh.Status);
        Assert.AreEqual(1500.5m, read.Body!["current"]!["rate"]!.GetValue<decimal>());
        Assert.AreEqual("manual", read.Body!["current"]!["source"]!.GetValue<string>());
        Assert.AreEqual(1, read.Body!["recent"]!.AsArray().Count);
        Assert.IsTrue(await w.Query(db => db.Jobs.AnyAsync(j => j.Kind == App.Platform.JobKinds.ExchangeRates)), "La del banco la trae un trabajo, nunca el pedido.");
    }

    [TestMethod]
    public async Task CargaDeSaldo_PorLaApi_DevuelveLaUrlDeLaPasarela_DentroDeLosLimites()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"billing-oss-test-{Guid.NewGuid():N}.keys");
        await File.WriteAllTextAsync(keyFile, App.Platform.SecretBox.NewKeyLine(1));
        try
        {
            await using var w = await WebHarness.Start(settings: new()
            {
                ["Secrets:KeyFile"] = keyFile,
                ["Billing:PublicUrl"] = "https://billing.example.com",
                ["Billing:TopUp:ARS:Min"] = "1000",
                ["Billing:TopUp:ARS:Max"] = "50000",
            });
            var gateway = new FakeGateway { Currencies = ["ARS"] };
            await using var module = await TestModule.Start(gateway.Definition(), async work =>
            {
                await using var scope = w.Services.CreateAsyncScope();
                return await work(scope.ServiceProvider.GetRequiredService<App.Platform.ModuleRegistry>());
            });
            await w.Query(async db =>
            {
                (await db.ModuleInstances.SingleAsync()).BrowserHosts = """["pay.example.com"]""";
                return await db.SaveChangesAsync();
            });
            var token = await w.Key("payments:write");
            var reader = await w.Key("payments:read");
            var path = $"/api/v1/accounts/{w.AccountId}/credit/top-up";

            var denied = await Send(w, HttpMethod.Post, path, reader, new { currency = "ARS", amount = 5000, instance_id = module.InstanceId });
            var low = await Send(w, HttpMethod.Post, path, token, new { currency = "ARS", amount = 999, instance_id = module.InstanceId });
            var dollars = await Send(w, HttpMethod.Post, path, token, new { currency = "USD", amount = 50, instance_id = module.InstanceId });
            var started = await Send(w, HttpMethod.Post, path, token, new { currency = "ARS", amount = 5000, instance_id = module.InstanceId });

            Assert.AreEqual(HttpStatusCode.Forbidden, denied.Status);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, low.Status);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, dollars.Status, "Sin límites en dólares no se carga en dólares.");
            Assert.AreEqual(HttpStatusCode.OK, started.Status);
            StringAssert.StartsWith(started.Body!["redirect_url"]!.GetValue<string>(), "https://pay.example.com/checkout?ref=");
            var attempt = await w.Query(db => db.PaymentAttempts.SingleAsync());
            Assert.AreEqual(5000m, attempt.Amount);
            Assert.IsEmpty(attempt.DocumentIds);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }
}
