using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Revisión de pedidos y listas de bloqueo por la API propia (diseno/fraude-y-acceso.md, sección 10).</summary>
[TestClass]
public sealed class SecurityApiTests
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

    [TestMethod]
    public async Task PedidoConClientIp_SeEvalua_YSeDecideEnLaRevisionDeLaApi()
    {
        await using var w = await WebHarness.Start();
        var writer = await w.Key("orders:write");
        var reviewer = await w.Key("orders:review");
        var security = await w.Key("security:write");
        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync());

        var block = await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", security, new { kind = "ip", value = "203.0.113.0/24", reason = "Escaneo de puertos" });
        Assert.AreEqual(HttpStatusCode.Created, block.Status);

        var badIp = await Send(w, HttpMethod.Post, "/api/v1/orders", writer, new { account_id = w.AccountId, items = new[] { new { plan_id = planId } }, client_ip = "no-es-una-ip" });
        Assert.AreEqual(HttpStatusCode.BadRequest, badIp.Status);

        var placed = await Send(w, HttpMethod.Post, "/api/v1/orders", writer, new { account_id = w.AccountId, items = new[] { new { plan_id = planId } }, client_ip = "203.0.113.7" });
        Assert.AreEqual(HttpStatusCode.Created, placed.Status);
        var orderId = placed.Body!["order_id"]!.GetValue<long>();

        // Sin client_ip no se evalúa: solo el pedido de arriba queda retenido.
        Assert.AreEqual(HttpStatusCode.Created, (await Send(w, HttpMethod.Post, "/api/v1/orders", writer, new { account_id = w.AccountId, items = new[] { new { plan_id = planId } } })).Status);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/orders/review", writer)).Status);
        var queue = await Send(w, HttpMethod.Get, "/api/v1/orders/review", reviewer);
        Assert.AreEqual(HttpStatusCode.OK, queue.Status);
        var held = queue.Body!.AsArray().Single()!;
        Assert.AreEqual(orderId, held["order_id"]!.GetValue<long>());
        Assert.AreEqual("review", held["status"]!.GetValue<string>());
        Assert.AreEqual("203.0.113.7", held["ip"]!.GetValue<string>());
        Assert.AreEqual("blocklist.ip", held["reasons"]![0]!["code"]!.GetValue<string>());

        Assert.AreEqual(HttpStatusCode.BadRequest, (await Send(w, HttpMethod.Post, $"/api/v1/orders/{orderId}/review", reviewer, new { decision = "quizas" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Post, $"/api/v1/orders/{orderId}/review", writer, new { decision = "approve" })).Status);
        var approved = await Send(w, HttpMethod.Post, $"/api/v1/orders/{orderId}/review", reviewer, new { decision = "approve", note = "Cliente conocido" });
        Assert.AreEqual(HttpStatusCode.OK, approved.Status);
        Assert.AreEqual("approved", approved.Body!["status"]!.GetValue<string>());

        // La contraria a una decisión tomada es un conflicto, y un pedido sin evaluación no existe para la revisión.
        Assert.AreEqual(HttpStatusCode.Conflict, (await Send(w, HttpMethod.Post, $"/api/v1/orders/{orderId}/review", reviewer, new { decision = "reject" })).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Post, $"/api/v1/orders/{orderId + 1}/review", reviewer, new { decision = "reject" })).Status);
        Assert.AreEqual(0, (await Send(w, HttpMethod.Get, "/api/v1/orders/review", reviewer)).Body!.AsArray().Count);

        var check = await w.Query(db => db.Set<FraudCheck>().SingleAsync());
        Assert.AreEqual(ScreeningStatus.Approved, check.Status);
        Assert.AreEqual("Cliente conocido", check.Note);
        StringAssert.StartsWith(check.DecidedBy, "apikey:");
    }

    [TestMethod]
    public async Task ListasDeBloqueo_ConPermisos_NormalizanYVencen()
    {
        await using var w = await WebHarness.Start();
        var reader = await w.Key("security:read");
        var writer = await w.Key("security:write");

        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", reader, new { kind = "email", value = "x@ejemplo.com", reason = "x" })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/security/blocklist", writer)).Status);

        var email = await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", writer, new { kind = "email", value = " Alguien@Ejemplo.com ", reason = "Contracargo" });
        Assert.AreEqual(HttpStatusCode.Created, email.Status);
        var emailId = email.Body!["id"]!.GetValue<long>();
        var network = await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", writer, new { kind = "ip", value = "2001:db8::1/32", reason = "Red de abuso", expires_at = "2026-12-01T00:00:00Z" });
        Assert.AreEqual(HttpStatusCode.Created, network.Status);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", writer, new { kind = "telefono", value = "1", reason = "x" })).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", writer, new { kind = "ip", value = "no-es-ip", reason = "x" })).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", writer, new { kind = "domain", value = "ejemplo.org", reason = "" })).Status);
        // El reloj de la prueba está en el 2 de noviembre de 2026: un vencimiento pasado no se acepta.
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Send(w, HttpMethod.Post, "/api/v1/security/blocklist", writer, new { kind = "domain", value = "ejemplo.org", reason = "x", expires_at = "2026-11-01T00:00:00Z" })).Status);

        var list = (await Send(w, HttpMethod.Get, "/api/v1/security/blocklist", reader)).Body!.AsArray();
        CollectionAssert.AreEquivalent(new[] { "alguien@ejemplo.com", "2001:db8::/32" }, list.Select(e => e!["value"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("api", list.Single(e => e!["kind"]!.GetValue<string>() == "email")!["source"]!.GetValue<string>());

        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Delete, $"/api/v1/security/blocklist/{emailId}", writer)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Delete, $"/api/v1/security/blocklist/{emailId}", writer)).Status);
        Assert.AreEqual(1, (await Send(w, HttpMethod.Get, "/api/v1/security/blocklist", reader)).Body!.AsArray().Count);

        var actions = await w.Query(db => db.Set<AuditEvent>().Where(a => a.Action.StartsWith("security.")).Select(a => a.Action).ToListAsync());
        CollectionAssert.AreEquivalent(new[] { "security.block", "security.block", "security.unblock" }, actions);
        Assert.AreEqual(1, await w.Query(db => db.Set<BlockedEntry>().CountAsync()));
    }
}
