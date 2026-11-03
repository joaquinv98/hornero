using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>El catálogo y los cambios de servicios por la API propia, y GetProducts por la API compatible.</summary>
[TestClass]
public sealed class CatalogApiTests
{
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

    private static async Task<long> Created(WebHarness w, string path, string token, object body)
    {
        var (status, json) = await Send(w, HttpMethod.Post, path, token, body);
        Assert.AreEqual(HttpStatusCode.Created, status, json?.ToJsonString());
        return json!["id"]?.GetValue<long>() ?? json["change_id"]!.GetValue<long>();
    }

    [TestMethod]
    public async Task ApiPropia_ArmaElCatalogo_VendeConOpciones_YUnPrecioNuevoNoTocaLoContratado()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("catalog:read", "catalog:write", "orders:write", "services:read");

        var group = await Created(w, "/api/v1/product-groups", token, new { name = "VPS", headline = "Servidores virtuales" });
        var options = await Created(w, "/api/v1/option-groups", token, new
        {
            name = "VPS",
            options = new[]
            {
                new
                {
                    name = "RAM",
                    kind = "select",
                    module_key = "memory_mb",
                    values = new[]
                    {
                        new { name = "1 GB", module_value = "1024", prices = new[] { new { cycle = "P1M", currency = "ARS", amount = 0m, setup_fee = 0m } } },
                        new { name = "2 GB", module_value = "2048", prices = new[] { new { cycle = "P1M", currency = "ARS", amount = 5000m, setup_fee = 1000m } } },
                    },
                },
            },
        });
        var product = await Created(w, "/api/v1/products", token, new
        {
            name = "VPS SSD",
            kind = "manual",
            group_id = group,
            description = "Un VPS",
            prices = new[] { new { cycle = "P1M", currency = "ARS", amount = 10000m, setup_fee = 0m } },
            option_group_ids = new[] { options },
            activation = "on_order",
        });
        Assert.AreEqual("on_order", (await Send(w, HttpMethod.Get, $"/api/v1/products/{product}", token)).Body!["activation"]!.GetValue<string>());
        var (odd, oddBody) = await Send(w, HttpMethod.Post, "/api/v1/products", token, new { name = "Raro", kind = 9 });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, odd, oddBody?.ToJsonString());
        var copy = await Created(w, $"/api/v1/products/{product}/copy", token, new { name = "VPS SSD 2" });
        var copied = (await Send(w, HttpMethod.Get, $"/api/v1/products/{copy}", token)).Body!;
        Assert.AreEqual(("VPS SSD 2", false), (copied["name"]!.GetValue<string>(), copied["visible"]!.GetValue<bool>()));

        var (_, view) = await Send(w, HttpMethod.Get, $"/api/v1/products/{product}", token);
        Assert.AreEqual("VPS", view!["group_name"]!.GetValue<string>());
        Assert.AreEqual(10000m, view["prices"]![0]!["amount"]!.GetValue<decimal>());
        var ram = view["options"]![0]!;
        Assert.AreEqual("select", ram["kind"]!.GetValue<string>());
        var big = ram["values"]![1]!;
        Assert.AreEqual(5000m, big["prices"]![0]!["amount"]!.GetValue<decimal>());

        var (bad, problem) = await Send(w, HttpMethod.Post, "/api/v1/products", token, new { name = "Otro", prices = new[] { new { cycle = "mensual", currency = "ARS", amount = 1m } } });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad);
        StringAssert.Contains(problem!["title"]!.GetValue<string>(), "Ciclo desconocido");

        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == product).Select(p => p.Id).SingleAsync());
        var body = new
        {
            account_id = w.AccountId,
            items = new[] { new { plan_id = planId, options = new[] { new { option_id = ram["id"]!.GetValue<long>(), value_id = big["id"]!.GetValue<long>() } } } },
        };

        // La cotización dice lo mismo que va a facturar el pedido, sin crear nada.
        var (quoted, quote) = await Send(w, HttpMethod.Post, "/api/v1/orders/quote", token, body);
        Assert.AreEqual(HttpStatusCode.OK, quoted, quote?.ToJsonString());
        Assert.AreEqual((15000m, 1000m, 16000m), (quote!["lines"]![0]!["unit_price"]!.GetValue<decimal>(), quote["lines"]![0]!["setup_fee"]!.GetValue<decimal>(), quote["total"]!.GetValue<decimal>()));
        StringAssert.Contains(quote["lines"]![0]!["description"]!.GetValue<string>(), "RAM: 2 GB");
        Assert.AreEqual(0, await w.Query(db => db.Orders.CountAsync()));

        var (ordered, order) = await Send(w, HttpMethod.Post, "/api/v1/orders", token, body);
        Assert.AreEqual(HttpStatusCode.Created, ordered, order?.ToJsonString());
        var serviceId = order!["service_ids"]![0]!.GetValue<long>();
        var invoiceId = order["invoice_id"]!.GetValue<long>();
        Assert.AreEqual(16000m, await w.Query(db => db.Documents.Where(d => d.Id == invoiceId).Select(d => d.Total).SingleAsync()));

        // El precio nuevo abre una vigencia: lo que se vende cambia, lo contratado no.
        var (updated, _) = await Send(w, HttpMethod.Put, $"/api/v1/products/{product}", token, new
        {
            name = "VPS SSD",
            kind = "manual",
            group_id = group,
            prices = new[] { new { cycle = "P1M", currency = "ARS", amount = 11000m, setup_fee = 0m } },
        });
        Assert.AreEqual(HttpStatusCode.OK, updated);
        Assert.AreEqual(11000m, (await Send(w, HttpMethod.Get, $"/api/v1/products/{product}", token)).Body!["prices"]![0]!["amount"]!.GetValue<decimal>());
        var (_, service) = await Send(w, HttpMethod.Get, $"/api/v1/services/{serviceId}", token);
        Assert.AreEqual(15000m, service!["unit_price"]!.GetValue<decimal>());
        Assert.AreEqual("2 GB", service["options"]![0]!["value"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiPropia_Paquete_ConLosPlanesQueDiceElProducto_YElPedidoLoUsa()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("catalog:read", "catalog:write", "orders:write");
        var (_, product) = await Send(w, HttpMethod.Get, $"/api/v1/products/{w.ProductId}", token);
        var planId = product!["prices"]![0]!["plan_id"]!.GetValue<long>();

        var bundle = await Created(w, "/api/v1/bundles", token, new { name = "Dos VPS", items = new[] { new { plan_id = planId, quantity = 2 } }, discount_percent = 15m, description = "Para alta disponibilidad" });
        var (_, view) = await Send(w, HttpMethod.Get, $"/api/v1/bundles/{bundle}", token);
        Assert.AreEqual(2, view!["items"]![0]!["quantity"]!.GetValue<int>());
        Assert.AreEqual(20570m, view["prices"]![0]!["amount"]!.GetValue<decimal>());
        Assert.IsNull(view["discount_cycles"]);
        var (bad, problem) = await Send(w, HttpMethod.Post, "/api/v1/bundles", token, new { name = "Malo", items = new[] { new { plan_id = planId } }, discount_percent = 120m });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad, problem?.ToJsonString());

        var (ordered, order) = await Send(w, HttpMethod.Post, "/api/v1/orders", token, new
        {
            account_id = w.AccountId,
            bundle_id = bundle,
            items = new[] { new { plan_id = planId, bundled = true }, new { plan_id = planId, bundled = true } },
        });
        Assert.AreEqual(HttpStatusCode.Created, ordered, order?.ToJsonString());
        var services = await w.Query(db => db.Services.ToListAsync());
        Assert.AreEqual(2, services.Count);
        Assert.IsTrue(services.All(s => s.DiscountPercent == 15m && s.DiscountCyclesLeft is null));
        Assert.AreEqual(bundle, await w.Query(db => db.Orders.Select(o => o.BundleId).SingleAsync()));
    }

    [TestMethod]
    public async Task ApiPropia_CambioDeOpciones_CalculaPideYCancela()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write", "payments:write", "services:read", "services:write");
        await w.Query(async db =>
        {
            await CatalogSeed.VpsOptions(db, w.ProductId);
            return true;
        });
        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync());
        var (_, order) = await Send(w, HttpMethod.Post, "/api/v1/orders", token, new { account_id = w.AccountId, items = new[] { new { plan_id = planId } } });
        var serviceId = order!["service_ids"]![0]!.GetValue<long>();
        await Send(w, HttpMethod.Post, "/api/v1/payments", token, new { account_id = w.AccountId, method = "transferencia", external_id = "T-1", currency = "ARS", amount = 12100m });
        await w.RunJobs();

        var change = new { options = new[] { new { option_id = CatalogSeed.Ram, value_id = CatalogSeed.Ram2 } } };
        var (previewed, quote) = await Send(w, HttpMethod.Post, $"/api/v1/services/{serviceId}/changes/preview", token, change);
        Assert.AreEqual(HttpStatusCode.OK, previewed, quote?.ToJsonString());
        Assert.AreEqual(17100m, quote!["to_unit_price"]!.GetValue<decimal>());
        Assert.AreEqual(5000m, quote["amount"]!.GetValue<decimal>());

        var changeId = await Created(w, $"/api/v1/services/{serviceId}/changes", token, change);
        Assert.AreEqual(changeId, (await Send(w, HttpMethod.Get, $"/api/v1/services/{serviceId}", token)).Body!["pending_change_id"]!.GetValue<long>());
        var (cancelled, _) = await Send(w, HttpMethod.Post, $"/api/v1/service-changes/{changeId}/cancel", token);
        Assert.AreEqual(HttpStatusCode.NoContent, cancelled);
        Assert.AreEqual("cancelled", (await Send(w, HttpMethod.Get, $"/api/v1/service-changes/{changeId}", token)).Body!["status"]!.GetValue<string>());
        var (again, conflict) = await Send(w, HttpMethod.Post, $"/api/v1/service-changes/{changeId}/cancel", token);
        Assert.AreEqual(HttpStatusCode.Conflict, again, conflict?.ToJsonString());
        Assert.AreEqual(ServiceChangeStatus.Cancelled, await w.Query(db => db.ServiceChanges.Select(c => c.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task ApiCompatible_GetProducts_ConPreciosPorMonedaYCicloYOpciones()
    {
        await using var w = await WebHarness.Start(compat: true);
        await w.Query(async db =>
        {
            await CatalogSeed.VpsOptions(db, w.ProductId);
            var product = await db.Products.SingleAsync(p => p.Id == w.ProductId);
            product.Description = "VPS con SSD";
            await db.SaveChangesAsync();
            return true;
        });
        var token = await w.Key("compat:GetProducts");

        var all = await w.Compat(token, new() { ["action"] = "GetProducts" });
        var one = await w.Compat(token, new() { ["action"] = "GetProducts", ["pid"] = w.ProductId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        Assert.AreEqual("success", all["result"]!.GetValue<string>(), all.ToJsonString());
        Assert.AreEqual(2, all["totalresults"]!.GetValue<int>());
        var vps = one["products"]!["product"]!.AsArray().Single()!;
        Assert.AreEqual(w.ProductId, vps["pid"]!.GetValue<long>());
        Assert.AreEqual("VPS con SSD", vps["description"]!.GetValue<string>());
        Assert.AreEqual("12100.00", vps["pricing"]!["ARS"]!["monthly"]!.GetValue<string>());
        Assert.AreEqual("-1.00", vps["pricing"]!["ARS"]!["quarterly"]!.GetValue<string>());
        var ram = vps["configoptions"]!["configoption"]![0]!;
        Assert.AreEqual("RAM", ram["name"]!.GetValue<string>());
        Assert.AreEqual("1", ram["type"]!.GetValue<string>());
        var big = ram["options"]!["option"]![1]!;
        Assert.AreEqual("2 GB", big["name"]!.GetValue<string>());
        Assert.AreEqual("5000.00", big["pricing"]!["ARS"]!["monthly"]!.GetValue<string>());
        Assert.AreEqual("1000.00", big["pricing"]!["ARS"]!["msetupfee"]!.GetValue<string>());
        Assert.AreEqual("50000.00", big["pricing"]!["ARS"]!["annually"]!.GetValue<string>());
        Assert.AreEqual("4", vps["configoptions"]!["configoption"]![1]!["type"]!.GetValue<string>());
    }
}
