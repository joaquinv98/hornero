using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Pruebas de contrato de los comandos que la API compatible sumó después de v0 (sección 3.1 de
/// diseno/api-compatible.md): mandan lo que manda una integración y miran los campos que lee.
/// </summary>
[TestClass]
public sealed class CompatCommandsTests
{
    private static readonly string[] All =
    [
        "compat:AddClient", "compat:AddOrder", "compat:AddInvoicePayment", "compat:AcceptOrder", "compat:GetInvoice", "compat:GetClientsDetails",
        "compat:GetClientsProducts", "compat:GetClients", "compat:GetProducts", "compat:GetInvoices", "compat:DeleteClient",
        "compat:UpdateClientProduct", "compat:ModuleCreate", "compat:ModuleTerminate", "compat:CancelOrder", "compat:GetEmailTemplates",
        "compat:ModuleSuspend", "compat:ModuleUnsuspend",
    ];

    [TestMethod]
    public async Task GetClients_BuscaFiltraPorEstadoYPagina()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        await AddClient(w, token, "Juan", "Gómez", "juan@example.com", "Hosting Sur SA");
        await AddClient(w, token, "Ana", "Pérez", "ana_p@example.com");

        var byCompany = await w.Compat(token, new() { ["action"] = "GetClients", ["search"] = "sur sa" });
        var byEmail = await w.Compat(token, new() { ["action"] = "GetClients", ["search"] = "ana_p@" });
        var wildcard = await w.Compat(token, new() { ["action"] = "GetClients", ["search"] = "_" });
        var paged = await w.Compat(token, new() { ["action"] = "GetClients", ["limitstart"] = "1", ["limitnum"] = "1" });
        var closed = await w.Compat(token, new() { ["action"] = "GetClients", ["status"] = "Closed" });
        var numeric = await w.Compat(token, new() { ["action"] = "GetClients", ["status"] = "1" });

        var juan = Single(byCompany, "clients", "client");
        Assert.AreEqual("Juan", juan["firstname"]!.GetValue<string>());
        Assert.AreEqual("Gómez", juan["lastname"]!.GetValue<string>());
        Assert.AreEqual("Hosting Sur SA", juan["companyname"]!.GetValue<string>());
        Assert.AreEqual("juan@example.com", juan["email"]!.GetValue<string>());
        Assert.AreEqual("Active", juan["status"]!.GetValue<string>());
        Assert.AreEqual("ana_p@example.com", Single(byEmail, "clients", "client")["email"]!.GetValue<string>());

        // El guion bajo se busca tal cual, no como comodín.
        Assert.AreEqual(1, wildcard["totalresults"]!.GetValue<int>());
        Assert.AreEqual(3, paged["totalresults"]!.GetValue<int>());
        Assert.AreEqual(1, paged["startnumber"]!.GetValue<int>());
        Assert.AreEqual(1, paged["numreturned"]!.GetValue<int>());
        Assert.AreEqual(0, closed["totalresults"]!.GetValue<int>());
        Assert.AreEqual("error", numeric["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetProducts_ConElPrecioVigentePorMonedaYCiclo()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync());
        await w.Query(async db =>
        {
            // Un precio futuro todavía no vale, y una suba ya vigente reemplaza al de enero.
            db.PlanPrices.Add(new PlanPrice(planId, Currency.Ars, 13000m, 500m, new DateOnly(2026, 10, 1)));
            db.PlanPrices.Add(new PlanPrice(planId, Currency.Ars, 99999m, 0m, new DateOnly(2027, 1, 1)));
            return await db.SaveChangesAsync();
        });

        var all = await w.Compat(token, new() { ["action"] = "GetProducts" });
        var one = await w.Compat(token, new() { ["action"] = "GetProducts", ["pid"] = w.ProductId.ToString(CultureInfo.InvariantCulture) });

        Assert.AreEqual(2, all["totalresults"]!.GetValue<int>());
        var vps = Single(one, "products", "product");
        Assert.AreEqual(w.ProductId, vps["pid"]!.GetValue<long>());
        Assert.AreEqual("VPS 2 GB", vps["name"]!.GetValue<string>());
        Assert.IsFalse(vps["hidden"]!.GetValue<bool>());
        Assert.AreEqual("13000.00", vps["pricing"]!["ARS"]!["monthly"]!.GetValue<string>());
        Assert.AreEqual("500.00", vps["pricing"]!["ARS"]!["msetupfee"]!.GetValue<string>());
        Assert.AreEqual("-1.00", vps["pricing"]!["ARS"]!["annually"]!.GetValue<string>(), "un ciclo que no se vende");

        // El plan gratis, como el de pago único, va en la primera columna.
        var trial = all["products"]!["product"]!.AsArray().Single(p => p!["pid"]!.GetValue<long>() != w.ProductId)!;
        Assert.AreEqual("0.00", trial["pricing"]!["ARS"]!["monthly"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetInvoices_CadaFiltroDeEstadoCoincideConGetInvoice()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        var staff = await w.Key("payments:refund");
        var unpaid = (await Order(w, token)).InvoiceId;
        var paid = (await Order(w, token)).InvoiceId;
        await Pay(w, token, paid, "ML-1");
        var partial = (await Order(w, token)).InvoiceId;
        await Pay(w, token, partial, "ML-2", "100.00");
        var cancelled = await Order(w, token);
        Assert.AreEqual("success", (await w.Compat(token, new() { ["action"] = "CancelOrder", ["orderid"] = cancelled.OrderId.ToString(CultureInfo.InvariantCulture) }))["result"]!.GetValue<string>());
        await w.Query(async db =>
        {
            var zero = new TaxBreakdown([], [], 0m, 0m);
            db.Documents.AddRange(
                new Document(DocumentKind.Invoice, w.AccountId, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 11, 10), DateTimeOffset.UtcNow),
                Document.FromImport(9001, DocumentKind.Invoice, w.AccountId, Currency.Ars, 9001, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), zero, 0m, 0m, "{}", DateTimeOffset.UtcNow, sourceStatus: "Paid"),
                Document.FromImport(9002, DocumentKind.Invoice, w.AccountId, Currency.Ars, 9002, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), zero, 0m, 0m, "{}", DateTimeOffset.UtcNow, sourceStatus: "Cancelled"),
                Document.FromImport(9003, DocumentKind.Invoice, w.AccountId, Currency.Ars, 9003, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), new TaxBreakdown([], [], 0m, 500m), 0m, 500m, "{}", DateTimeOffset.UtcNow, sourceStatus: "Refunded"));
            return await db.SaveChangesAsync();
        });

        // El reembolso al final: deja saldo a favor, que pagaría las facturas de los pedidos siguientes.
        var refunded = (await Order(w, token)).InvoiceId;
        await Pay(w, token, refunded, "ML-3");
        var paymentId = await w.Query(db => db.Payments.Where(p => p.ExternalId == "ML-3").Select(p => p.Id).SingleAsync());
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/payments/{paymentId}/refunds", UriKind.Relative)))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staff);
            request.Content = JsonContent.Create(new { invoice_id = refunded, reason = "Arrepentimiento", lines = new[] { new { description = "Devolución", quantity = 1, unit_price = 12100m, treatment = "taxed", rate = 0.21m } } });
            using var response = await w.Client.SendAsync(request);
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        var ids = await w.Query(db => db.Documents.Where(d => d.Kind == DocumentKind.Invoice).Select(d => d.Id).ToListAsync());
        var expected = new Dictionary<long, string>();
        foreach (var id in ids)
        {
            expected[id] = (await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = id.ToString(CultureInfo.InvariantCulture) }))["status"]!.GetValue<string>();
        }

        Assert.AreEqual("Unpaid", expected[unpaid]);
        Assert.AreEqual("Unpaid", expected[partial]);
        Assert.AreEqual("Paid", expected[paid]);
        Assert.AreEqual("Cancelled", expected[cancelled.InvoiceId]);
        Assert.AreEqual("Refunded", expected[refunded]);
        foreach (var status in new[] { "Draft", "Unpaid", "Paid", "Cancelled", "Refunded" })
        {
            var list = await w.Compat(token, new() { ["action"] = "GetInvoices", ["status"] = status, ["limitnum"] = "250" });
            var found = list["invoices"]!["invoice"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Order().ToList();
            CollectionAssert.AreEqual(expected.Where(e => e.Value == status).Select(e => e.Key).Order().ToList(), found, status);
            Assert.IsTrue(list["invoices"]!["invoice"]!.AsArray().All(i => i!["status"]!.GetValue<string>() == status), status);
        }

        var paidRow = (await w.Compat(token, new() { ["action"] = "GetInvoices", ["userid"] = w.AccountId.ToString(CultureInfo.InvariantCulture), ["status"] = "Paid" }))["invoices"]!["invoice"]!
            .AsArray().Single(i => i!["id"]!.GetValue<long>() == paid)!;
        Assert.AreEqual("12100.00", paidRow["total"]!.GetValue<string>());
        Assert.AreEqual(w.AccountId, paidRow["userid"]!.GetValue<long>());
        Assert.AreNotEqual(string.Empty, paidRow["datepaid"]!.GetValue<string>());
        Assert.AreEqual(0, (await w.Compat(token, new() { ["action"] = "GetInvoices", ["clientid"] = "999999" }))["totalresults"]!.GetValue<int>());
        Assert.AreEqual("error", (await w.Compat(token, new() { ["action"] = "GetInvoices", ["status"] = "Overdue" }))["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DeleteClient_SoloLaCuentaSinHistoria()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        var mistaken = await AddClient(w, token, "Cuenta", "Por error", "error@example.com");
        await Order(w, token);

        var deleted = await w.Compat(token, new() { ["action"] = "DeleteClient", ["clientid"] = mistaken.ToString(CultureInfo.InvariantCulture) });
        var kept = await w.Compat(token, new() { ["action"] = "DeleteClient", ["clientid"] = w.AccountId.ToString(CultureInfo.InvariantCulture) });
        var missing = await w.Compat(token, new() { ["action"] = "DeleteClient", ["clientid"] = mistaken.ToString(CultureInfo.InvariantCulture) });

        Assert.AreEqual("success", deleted["result"]!.GetValue<string>(), deleted.ToJsonString());
        Assert.AreEqual("error", kept["result"]!.GetValue<string>());
        StringAssert.Contains(kept["message"]!.GetValue<string>(), "se cierra desde el admin");
        Assert.AreEqual("error", missing["result"]!.GetValue<string>());
        Assert.IsFalse(await w.Query(db => db.Accounts.AnyAsync(a => a.Id == mistaken)));
        Assert.IsTrue(await w.Query(db => db.Accounts.AnyAsync(a => a.Id == w.AccountId)));
    }

    [TestMethod]
    public async Task ModuleCreateYModuleTerminate_SiguenElCicloDelServicio()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        var serviceId = (await Order(w, token)).ServiceId;
        var service = new Dictionary<string, string> { ["serviceid"] = serviceId.ToString(CultureInfo.InvariantCulture) };

        // Un producto con módulo y sin instancia falla al crear: el servicio queda fallido y se reintenta con otra clave.
        await w.Query(db => db.Products.Where(p => p.Id == w.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Kind, ProductKind.Provisioned)));
        Assert.AreEqual("success", (await Command(w, token, "ModuleCreate", service))["result"]!.GetValue<string>());
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Failed, await Status(w, serviceId));

        await w.Query(db => db.Products.Where(p => p.Id == w.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Kind, ProductKind.Manual)));
        Assert.AreEqual("success", (await Command(w, token, "ModuleCreate", service))["result"]!.GetValue<string>());
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, await Status(w, serviceId));
        Assert.AreEqual("error", (await Command(w, token, "ModuleCreate", service))["result"]!.GetValue<string>());

        // Dos pedidos de baja encolan una sola.
        Assert.AreEqual("success", (await Command(w, token, "ModuleTerminate", service))["result"]!.GetValue<string>());
        Assert.AreEqual("success", (await Command(w, token, "ModuleTerminate", service))["result"]!.GetValue<string>());
        Assert.AreEqual(1, await w.Query(db => db.Jobs.CountAsync(j => j.DedupKey!.Contains(":terminate:"))));
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Terminated, await Status(w, serviceId));
        var again = await Command(w, token, "ModuleTerminate", service);
        Assert.AreEqual("error", again["result"]?.GetValue<string>(), again.ToJsonString());
        Assert.AreEqual("error", (await Command(w, token, "ModuleTerminate", new() { ["serviceid"] = "999999" }))["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ModuleSuspendYModuleUnsuspend_ConMotivoQueElPagoNoLevanta()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        await w.Query(db => db.Products.Where(p => p.Id == w.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Kind, ProductKind.Manual)));
        var (_, invoiceId, serviceId) = await Order(w, token);
        var service = new Dictionary<string, string> { ["serviceid"] = serviceId.ToString(CultureInfo.InvariantCulture) };

        Assert.AreEqual("error", (await Command(w, token, "ModuleSuspend", service))["result"]!.GetValue<string>(), "Uno pendiente no se suspende.");
        await Command(w, token, "ModuleCreate", service);
        await w.RunJobs();
        Assert.AreEqual("error", (await Command(w, token, "ModuleUnsuspend", service))["result"]!.GetValue<string>(), "Uno activo no se reactiva.");

        // Dos pedidos encolan una sola suspensión, y queda con motivo: es del staff, no de la mora.
        Assert.AreEqual("success", (await Command(w, token, "ModuleSuspend", service))["result"]!.GetValue<string>());
        Assert.AreEqual("success", (await Command(w, token, "ModuleSuspend", service))["result"]!.GetValue<string>());
        Assert.AreEqual(1, await w.Query(db => db.Jobs.CountAsync(j => j.DedupKey!.Contains(":suspend:"))));
        await w.RunJobs();
        var suspended = await w.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Suspended, suspended.Status);
        Assert.AreEqual(UseCases.ServiceAdmin.ManualSuspension, suspended.SuspendReason);
        await Pay(w, token, invoiceId, "ML-S");
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, await Status(w, serviceId), "El pago levanta solo la suspensión por mora.");

        Assert.AreEqual("success", (await Command(w, token, "ModuleUnsuspend", service))["result"]!.GetValue<string>());
        await w.RunJobs();
        var active = await w.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Active, active.Status);
        Assert.IsNull(active.SuspendReason, "Reactivado, el motivo ya no vale.");
        Assert.AreEqual("error", (await Command(w, token, "ModuleSuspend", new() { ["serviceid"] = "999999" }))["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UpdateClientProduct_CambiaElNombreYRechazaLoQueNoSoporta()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        var serviceId = (await Order(w, token)).ServiceId.ToString(CultureInfo.InvariantCulture);

        var renamed = await w.Compat(token, new() { ["action"] = "UpdateClientProduct", ["serviceid"] = serviceId, ["domain"] = "vps1.example.com" });
        var unsupported = await w.Compat(token, new() { ["action"] = "UpdateClientProduct", ["serviceid"] = serviceId, ["domain"] = "otro.example.com", ["status"] = "Active" });
        var nothing = await w.Compat(token, new() { ["action"] = "UpdateClientProduct", ["serviceid"] = serviceId });

        Assert.AreEqual("success", renamed["result"]!.GetValue<string>(), renamed.ToJsonString());
        Assert.AreEqual("error", unsupported["result"]!.GetValue<string>());
        StringAssert.Contains(unsupported["message"]!.GetValue<string>(), "status");
        Assert.AreEqual("error", nothing["result"]!.GetValue<string>());
        var product = await w.Compat(token, new() { ["action"] = "GetClientsProducts", ["serviceid"] = serviceId });
        Assert.AreEqual("vps1.example.com", Single(product, "products", "product")["domain"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CancelOrder_AnulaLaFacturaYCancelaLosServicios_SoloSiNadaSePagoNiArranco()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        var open = await Order(w, token);
        var paid = await Order(w, token);
        await Pay(w, token, paid.InvoiceId, "ML-9");
        var started = await Order(w, token);
        await w.Compat(token, new() { ["action"] = "AcceptOrder", ["orderid"] = started.OrderId.ToString(CultureInfo.InvariantCulture), ["autosetup"] = "true" });

        for (var i = 0; i < 2; i++)
        {
            var cancel = await w.Compat(token, new() { ["action"] = "CancelOrder", ["orderid"] = open.OrderId.ToString(CultureInfo.InvariantCulture) });
            Assert.AreEqual("success", cancel["result"]!.GetValue<string>(), cancel.ToJsonString());
        }

        var paidCancel = await w.Compat(token, new() { ["action"] = "CancelOrder", ["orderid"] = paid.OrderId.ToString(CultureInfo.InvariantCulture) });
        var startedCancel = await w.Compat(token, new() { ["action"] = "CancelOrder", ["orderid"] = started.OrderId.ToString(CultureInfo.InvariantCulture) });

        Assert.AreEqual("Cancelled", (await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = open.InvoiceId.ToString(CultureInfo.InvariantCulture) }))["status"]!.GetValue<string>());
        Assert.AreEqual(ServiceStatus.Cancelled, await Status(w, open.ServiceId));
        Assert.AreEqual(OrderStatus.Cancelled, await w.Query(db => db.Orders.Where(o => o.Id == open.OrderId).Select(o => o.Status).SingleAsync()));
        Assert.AreEqual(1, await w.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote)), "Repetir no emite otra nota.");

        Assert.AreEqual("error", paidCancel["result"]!.GetValue<string>());
        StringAssert.Contains(paidCancel["message"]!.GetValue<string>(), "reembolsa");
        Assert.AreEqual("error", startedCancel["result"]!.GetValue<string>());
        Assert.AreEqual("Paid", (await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = paid.InvoiceId.ToString(CultureInfo.InvariantCulture) }))["status"]!.GetValue<string>());
        Assert.AreEqual("Unpaid", (await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = started.InvoiceId.ToString(CultureInfo.InvariantCulture) }))["status"]!.GetValue<string>());
        Assert.AreEqual(ServiceStatus.Provisioning, await Status(w, started.ServiceId));
    }

    [TestMethod]
    public async Task GetEmailTemplates_ListaCadaAvisoEnCadaIdioma()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key(All);
        await w.Query(async db =>
        {
            db.Add(new Template { Key = TemplateKeys.InvoiceIssued, Culture = "es-AR", Subject = "Tu factura de Ejemplo", Body = "{{ number }}", UpdatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });

        var list = await w.Compat(token, new() { ["action"] = "GetEmailTemplates" });

        var templates = list["emailtemplates"]!["emailtemplate"]!.AsArray();
        Assert.AreEqual((TemplateRenderer.Keys.Count - 1) * TemplateRenderer.Cultures.Count, list["totalresults"]!.GetValue<int>());
        Assert.IsFalse(templates.Any(t => t!["name"]!.GetValue<string>() == TemplateRenderer.Layout));
        var edited = templates.Single(t => t!["name"]!.GetValue<string>() == TemplateKeys.InvoiceIssued && t["language"]!.GetValue<string>() == "es-AR")!;
        Assert.AreEqual("Tu factura de Ejemplo", edited["subject"]!.GetValue<string>());
        Assert.IsTrue(edited["custom"]!.GetValue<bool>());
        Assert.IsFalse(templates.Single(t => t!["name"]!.GetValue<string>() == TemplateKeys.InvoiceIssued && t["language"]!.GetValue<string>() == "en")!["custom"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ComandosNuevos_ExigenSuPermisoYQuedanEnLaAuditoria()
    {
        await using var w = await WebHarness.Start(compat: true);
        var reader = await w.Key("compat:GetClients");
        var writer = await w.Key("compat:AddClient", "compat:DeleteClient");
        var id = await AddClient(w, writer, "Cuenta", "Auditada", "auditada@example.com");

        var denied = await w.Compat(reader, new() { ["action"] = "GetInvoices" });
        var deleted = await w.Compat(writer, new() { ["action"] = "DeleteClient", ["clientid"] = id.ToString(CultureInfo.InvariantCulture) });

        Assert.AreEqual("error", denied["result"]!.GetValue<string>());
        Assert.AreEqual("success", deleted["result"]!.GetValue<string>());
        var audit = await w.Query(db => db.Set<Platform.AuditEvent>().Where(a => a.Action == "compat DeleteClient").ToListAsync());
        Assert.HasCount(1, audit);
        StringAssert.Contains(audit[0].Details, id.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<long> AddClient(WebHarness w, string token, string first, string last, string email, string? company = null)
    {
        var fields = new Dictionary<string, string> { ["action"] = "AddClient", ["firstname"] = first, ["lastname"] = last, ["email"] = email, ["currency"] = "1" };
        if (company is not null)
        {
            fields["companyname"] = company;
        }

        var added = await w.Compat(token, fields);
        Assert.AreEqual("success", added["result"]!.GetValue<string>(), added.ToJsonString());
        return added["clientid"]!.GetValue<long>();
    }

    private static async Task<(long OrderId, long InvoiceId, long ServiceId)> Order(WebHarness w, string token)
    {
        var order = await w.Compat(token, new() { ["action"] = "AddOrder", ["clientid"] = w.AccountId.ToString(CultureInfo.InvariantCulture), ["pid[0]"] = w.ProductId.ToString(CultureInfo.InvariantCulture), ["billingcycle[0]"] = "monthly" });
        Assert.AreEqual("success", order["result"]!.GetValue<string>(), order.ToJsonString());
        return (order["orderid"]!.GetValue<long>(), order["invoiceid"]!.GetValue<long>(), long.Parse(order["productids"]!.GetValue<string>(), CultureInfo.InvariantCulture));
    }

    private static async Task Pay(WebHarness w, string token, long invoiceId, string transactionId, string? amount = null)
    {
        var fields = new Dictionary<string, string> { ["action"] = "AddInvoicePayment", ["invoiceid"] = invoiceId.ToString(CultureInfo.InvariantCulture), ["transid"] = transactionId, ["gateway"] = "mercadolibre" };
        if (amount is not null)
        {
            fields["amount"] = amount;
        }

        var paid = await w.Compat(token, fields);
        Assert.AreEqual("success", paid["result"]!.GetValue<string>(), paid.ToJsonString());
    }

    private static Task<JsonNode> Command(WebHarness w, string token, string action, Dictionary<string, string> fields) =>
        w.Compat(token, new Dictionary<string, string>(fields) { ["action"] = action });

    private static Task<ServiceStatus> Status(WebHarness w, long serviceId) =>
        w.Query(db => db.Services.Where(s => s.Id == serviceId).Select(s => s.Status).SingleAsync());

    private static JsonNode Single(JsonNode body, string plural, string singular)
    {
        Assert.AreEqual("success", body["result"]!.GetValue<string>(), body.ToJsonString());
        var items = body[plural]![singular]!.AsArray();
        Assert.HasCount(1, items, body.ToJsonString());
        return items[0]!;
    }
}
