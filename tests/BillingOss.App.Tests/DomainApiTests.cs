using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.Domain.Domains;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Dominios y precios por TLD en la API propia, contra el registrar falso.</summary>
[TestClass]
public sealed class DomainApiTests
{
    private sealed record Setup(WebHarness W, TestModule Module, FakeRegistrarModule Fake) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Module.DisposeAsync();
            await W.DisposeAsync();
        }
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Send(WebHarness w, HttpMethod method, string path, string token, object? body = null)
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

    private static async Task<Setup> Start()
    {
        var w = await WebHarness.Start();
        var fake = new FakeRegistrarModule();
        var module = await TestModule.Start(fake.Definition(), async work =>
        {
            await using var scope = w.Services.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<ModuleRegistry>());
        });
        await w.Db(db => FakeRegistrarModule.Seed(db, module.InstanceId, w.AccountId));
        return new Setup(w, module, fake);
    }

    [TestMethod]
    public async Task Dominio_BuscarRegistrarPagarYOperar()
    {
        await using var s = await Start();
        var (w, fake) = (s.W, s.Fake);
        var token = await w.Key("domains:read", "domains:write", "payments:write");
        var reader = await w.Key("domains:read");

        var check = await Send(w, HttpMethod.Post, "/api/v1/domains/check", token, new { names = new[] { "libre.com", "tomado.com" } });
        var badCurrency = await Send(w, HttpMethod.Post, "/api/v1/domains/check", token, new { names = new[] { "libre.com" }, currency = "PESOS" });
        var badPriceCurrency = await Send(w, HttpMethod.Get, "/api/v1/tld-prices?currency=XX", reader);
        var forbidden = await Send(w, HttpMethod.Post, "/api/v1/domains/register", reader, new { account_id = w.AccountId, name = "nuevo.com" });
        var invalid = await Send(w, HttpMethod.Post, "/api/v1/domains/register", token, new { account_id = w.AccountId, name = "no es un dominio" });
        var placed = await Send(w, HttpMethod.Post, "/api/v1/domains/register", token, new { account_id = w.AccountId, name = "nuevo.com", nameservers = new[] { "ns1.proveedor.ar", "ns2.proveedor.ar" } });

        Assert.AreEqual(HttpStatusCode.OK, check.Status);
        Assert.AreEqual((true, 15000m), (check.Body![0]!["available"]!.GetValue<bool>(), check.Body[0]!["price"]!["register"]!.GetValue<decimal>()));
        Assert.IsFalse(check.Body[1]!["available"]!.GetValue<bool>());
        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (badCurrency.Status, badPriceCurrency.Status), "Una moneda mal formada es un error del pedido, nunca un 500.");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.AreEqual(HttpStatusCode.Created, placed.Status, placed.Body?.ToJsonString());
        var id = placed.Body!["domain_id"]!.GetValue<long>();

        await Send(w, HttpMethod.Post, "/api/v1/payments", token, new { account_id = w.AccountId, method = "transferencia", external_id = "T-1", currency = "ARS", amount = 15000m });
        await w.RunJobs();
        var nameservers = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/nameservers", token, new { nameservers = new[] { "ns1.otro.net", "ns2.otro.net" } });
        var locked = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/lock", token, new { locked = true });
        var noRenew = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/auto-renew", token, new { auto_renew = false });
        await w.RunJobs();
        var renew = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/renew", token);
        var detail = await Send(w, HttpMethod.Get, $"/api/v1/domains/{id}", reader);
        var missing = await Send(w, HttpMethod.Get, "/api/v1/domains/999999", reader);
        var byAccount = await Send(w, HttpMethod.Get, $"/api/v1/domains?account_id={w.AccountId}&status=active", reader);
        var otherAccount = await Send(w, HttpMethod.Get, "/api/v1/domains?account_id=999999", reader);
        Assert.AreEqual((1, 0), (byAccount.Body!.AsArray().Count, otherAccount.Body!.AsArray().Count));

        Assert.AreEqual((HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.NoContent), (nameservers.Status, locked.Status, noRenew.Status));
        Assert.AreEqual(HttpStatusCode.OK, renew.Status);
        Assert.IsNotNull(renew.Body!["invoice_id"]!.GetValue<long?>());
        var d = detail.Body!;
        Assert.AreEqual(("active", "2027-11-02", true, false, false), (d["status"]!.ToString(), d["expires_on"]!.ToString(), d["locked"]!.GetValue<bool>(), d["auto_renew"]!.GetValue<bool>(), d["has_auth_code"]!.GetValue<bool>()));
        CollectionAssert.AreEqual(new[] { "ns1.otro.net", "ns2.otro.net" }, d["nameservers"]!.AsArray().Select(n => n!.ToString()).ToArray());
        Assert.IsTrue(d["operations"]!.AsArray().All(o => o!["status"]!.ToString() == "completed"));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.Status);
        Assert.AreEqual(DomainStatus.Active, (await w.Query(db => db.Domains.SingleAsync())).Status);
        Assert.IsTrue(fake.Domains.ContainsKey("nuevo.com"));
    }

    [TestMethod]
    public async Task ZonaReenviosYProteccion_SeLeenEnElMomentoYSeCambianPorLaCola()
    {
        await using var s = await Start();
        var (w, fake) = (s.W, s.Fake);
        var token = await w.Key("domains:read", "domains:write");
        var reader = await w.Key("domains:read");
        fake.Domains["ejemplo.com"] = new FakeRegistrarModule.FakeEntry { Expires = new DateOnly(2027, 6, 1), Records = [new("@", "A", "198.51.100.1")] };
        var id = await w.Query(async db =>
        {
            var domain = new RegisteredDomain(w.AccountId, "ejemplo.com", s.Module.InstanceId, BillingOss.Domain.Shared.Currency.Ars, 16000m, DateTimeOffset.UtcNow);
            domain.Start(new DateOnly(2027, 6, 1));
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            return domain.Id;
        });

        var zone = await Send(w, HttpMethod.Get, $"/api/v1/domains/{id}/dns", reader);
        var setZone = await Send(w, HttpMethod.Put, $"/api/v1/domains/{id}/dns", token, new { records = new[] { new { name = "www", type = "A", value = "203.0.113.10", ttl = 600 } } });
        var badZone = await Send(w, HttpMethod.Put, $"/api/v1/domains/{id}/dns", token, new { records = new[] { new { name = "@", type = "CNAME", value = "otro.example.net", ttl = 3600 } } });
        var noWrite = await Send(w, HttpMethod.Put, $"/api/v1/domains/{id}/dns", reader, new { records = Array.Empty<object>() });
        var forwards = await Send(w, HttpMethod.Put, $"/api/v1/domains/{id}/email-forwards", token, new { forwards = new[] { new { mailbox = "info", to = "ana@example.net" } } });
        var privacy = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/privacy", token, new { enabled = true });
        await w.RunJobs();
        var listed = await Send(w, HttpMethod.Get, $"/api/v1/domains/{id}/email-forwards", reader);

        Assert.AreEqual(HttpStatusCode.OK, zone.Status, zone.Body?.ToJsonString());
        Assert.AreEqual("198.51.100.1", zone.Body!["records"]![0]!["value"]!.ToString());
        Assert.AreEqual((HttpStatusCode.Accepted, HttpStatusCode.UnprocessableEntity, HttpStatusCode.Forbidden), (setZone.Status, badZone.Status, noWrite.Status));
        Assert.AreEqual((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (forwards.Status, privacy.Status));
        Assert.AreEqual("www A 203.0.113.10 600", string.Join(";", fake.Domains["ejemplo.com"].Records.Select(r => $"{r.Name} {r.Type} {r.Value} {r.Ttl}")));
        Assert.AreEqual("ana@example.net", listed.Body!["forwards"]![0]!["to"]!.ToString());
        Assert.IsTrue((await w.Query(db => db.Domains.SingleAsync(d => d.Id == id))).IdProtection);
    }

    [TestMethod]
    public async Task Opciones_PrecioContratacionYPedido_PorLaApi()
    {
        await using var s = await Start();
        var w = s.W;
        var token = await w.Key("domains:read", "domains:write", "pricing:write", "payments:write");
        var reader = await w.Key("domains:read");
        s.Fake.Domains["ejemplo.com"] = new FakeRegistrarModule.FakeEntry { Expires = new DateOnly(2027, 6, 1) };
        var id = await w.Query(async db =>
        {
            var domain = new RegisteredDomain(w.AccountId, "ejemplo.com", s.Module.InstanceId, BillingOss.Domain.Shared.Currency.Ars, 16000m, DateTimeOffset.UtcNow);
            domain.Start(new DateOnly(2027, 6, 1));
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            return domain.Id;
        });

        var price = await Send(w, HttpMethod.Put, "/api/v1/domain-option-prices", token, new { option = "dns", currency = "ARS", price = 1590m, valid_from = "2026-01-01" });
        var noPricing = await Send(w, HttpMethod.Put, "/api/v1/domain-option-prices", reader, new { option = "dns", currency = "ARS", price = 1m, valid_from = "2026-01-01" });
        var prices = await Send(w, HttpMethod.Get, "/api/v1/domain-option-prices?currency=ARS", reader);
        var locked = await Send(w, HttpMethod.Get, $"/api/v1/domains/{id}/dns", reader);
        var unknown = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/options", token, new { option = "hosting", enabled = true });
        var contract = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/options", token, new { option = "dns", enabled = true });
        var forwarding = await Send(w, HttpMethod.Post, $"/api/v1/domains/{id}/options", token, new { option = "email_forwarding", enabled = true });

        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.Forbidden), (price.Status, noPricing.Status));
        Assert.AreEqual((1590m, 0m), (prices.Body!["dns"]!.GetValue<decimal>(), prices.Body["privacy"]!.GetValue<decimal>()));
        Assert.AreEqual((HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity), (locked.Status, unknown.Status));
        Assert.AreEqual((HttpStatusCode.OK, HttpStatusCode.Accepted), (contract.Status, forwarding.Status), "La gestión de DNS tiene precio y el reenvío es gratis.");
        var invoiceId = contract.Body!["invoice_id"]!.GetValue<long>();
        var total = await w.Query(db => db.Documents.Where(d => d.Id == invoiceId).Select(d => d.Total).SingleAsync());
        await Send(w, HttpMethod.Post, "/api/v1/payments", token, new { account_id = w.AccountId, method = "transferencia", external_id = "T-40", currency = "ARS", amount = total });
        await w.RunJobs();

        var detail = (await Send(w, HttpMethod.Get, $"/api/v1/domains/{id}", reader)).Body!;
        CollectionAssert.AreEqual(new[] { "dns", "email_forwarding" }, detail["options"]!.AsArray().Select(o => o!.ToString()).ToArray());
        Assert.AreEqual((1590m, 17590m), (detail["option_prices"]!["dns"]!.GetValue<decimal>(), detail["recurring_amount"]!.GetValue<decimal>()));
        Assert.AreEqual(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, $"/api/v1/domains/{id}/dns", reader)).Status);

        var placed = await Send(w, HttpMethod.Post, "/api/v1/domains/register", token, new { account_id = w.AccountId, name = "nuevo.com", nameservers = new[] { "ns1.proveedor.ar", "ns2.proveedor.ar" }, dns_management = true });
        Assert.AreEqual(HttpStatusCode.Created, placed.Status, placed.Body?.ToJsonString());
        var orderInvoice = placed.Body!["invoice_id"]!.GetValue<long>();
        Assert.AreEqual(15000m + 1590m, await w.Query(db => db.Documents.Where(d => d.Id == orderInvoice).Select(d => d.Total).SingleAsync()));

        // Por extensión: .com deja de ofrecer el reenvío. No se pide más, y el dominio que ya lo tiene lo conserva.
        var hide = await Send(w, HttpMethod.Put, "/api/v1/tld-options", token, new { tld = "COM", offered = new[] { "dns", "privacy" } });
        var hideReader = await Send(w, HttpMethod.Put, "/api/v1/tld-options", reader, new { tld = "com", offered = Array.Empty<string>() });
        var byTld = await Send(w, HttpMethod.Get, "/api/v1/tld-options", reader);
        var hidden = await Send(w, HttpMethod.Post, "/api/v1/domains/register", token, new { account_id = w.AccountId, name = "otro.com", nameservers = new[] { "ns1.proveedor.ar", "ns2.proveedor.ar" }, email_forwarding = true });
        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.Forbidden, HttpStatusCode.UnprocessableEntity), (hide.Status, hideReader.Status, hidden.Status), hidden.Body?.ToJsonString());
        Assert.AreEqual(".com", byTld.Body![0]!["tld"]!.ToString());
        CollectionAssert.AreEqual(new[] { "dns", "privacy" }, byTld.Body[0]!["offered"]!.AsArray().Select(o => o!.ToString()).ToArray());
        CollectionAssert.AreEqual(new[] { "dns", "email_forwarding" }, (await Send(w, HttpMethod.Get, $"/api/v1/domains/{id}", reader)).Body!["options"]!.AsArray().Select(o => o!.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Precios_CargarListarEImportarDelRegistrar()
    {
        await using var s = await Start();
        var (w, module) = (s.W, s.Module);
        var token = await w.Key("domains:read", "pricing:write");
        var reader = await w.Key("domains:read");

        var set = await Send(w, HttpMethod.Put, "/api/v1/tld-prices", token, new { tld = "NET", instance_id = module.InstanceId, currency = "ARS", years = 1, register = 18000m, renew = 19000m, valid_from = "2026-10-01" });
        var notAllowed = await Send(w, HttpMethod.Put, "/api/v1/tld-prices", reader, new { tld = ".org", instance_id = module.InstanceId, currency = "ARS", years = 1, register = 1m, valid_from = "2026-10-01" });
        var preview = await Send(w, HttpMethod.Post, "/api/v1/tld-prices/import", token, new { instance_id = module.InstanceId, currency = "ARS", exchange_rate = 1000m, margin_percent = 30m, tax_percent = 21m, round_to = 100m });
        var csv = await Send(w, HttpMethod.Post, "/api/v1/tld-prices/csv", token, new { instance_id = module.InstanceId, currency = "ARS", csv = "tld,years,register,renew,transfer\n.com.ar,1,9000,9000,", apply = true });
        var badCsv = await Send(w, HttpMethod.Post, "/api/v1/tld-prices/csv", token, new { instance_id = module.InstanceId, currency = "ARS", csv = "tld,years\n.com.ar,1" });
        var list = await Send(w, HttpMethod.Get, "/api/v1/tld-prices?currency=ARS", reader);
        Assert.AreEqual((HttpStatusCode.OK, ".com.ar", HttpStatusCode.UnprocessableEntity), (csv.Status, csv.Body![0]!["tld"]!.ToString(), badCsv.Status));
        Assert.AreEqual(9000m, list.Body!.AsArray().Single(p => p!["tld"]!.ToString() == ".com.ar")!["register"]!.GetValue<decimal>());

        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.Forbidden), (set.Status, notAllowed.Status));
        var com = preview.Body!.AsArray().Single(r => r!["tld"]!.ToString() == ".com")!;
        Assert.AreEqual((9.50m, 15000m), (com["cost_register"]!.GetValue<decimal>(), com["register"]!.GetValue<decimal>()));
        var prices = list.Body!.AsArray();
        Assert.AreEqual(18000m, prices.Single(p => p!["tld"]!.ToString() == ".net")!["register"]!.GetValue<decimal>());
        Assert.AreEqual(15000m, prices.Single(p => p!["tld"]!.ToString() == ".com" && p["years"]!.GetValue<int>() == 1)!["register"]!.GetValue<decimal>(), "La vista previa no guarda nada.");
    }
}
