using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>API de actualizaciones (D-20, sección 7.2): lo mismo que el panel, con sus permisos, y el pedido que valida contra el canal.</summary>
[TestClass]
public sealed class UpdatesApiTests
{
    [TestMethod]
    public async Task LaApiMuestraElEstadoYLasVersiones_YElPedidoPideSuPermiso()
    {
        await using var w = await WebHarness.Start();
        var reader = await w.Key("updates:read");
        var writer = await w.Key("updates:read", "updates:write");

        // El reloj de WebHarness está fijo en 2026-11-02 15:00 UTC: el chequeo y la señal del nodo, un minuto antes.
        var channel = UpdatesTests.Channel(UpdatesTests.Release("0.1.1", security: true), UpdatesTests.Release("0.2.0", from: "0.1.0"));
        await w.Query(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE updates.state SET channel = {0}::jsonb, checked_at = '2026-11-02T14:59:00Z', check_attempt_at = '2026-11-02T14:59:00Z'", channel.ToJson());
            return await db.Database.ExecuteSqlRawAsync("INSERT INTO updates.nodes (name, version, updater_version, seen_at) VALUES ('nodo1', '0.1.0', '0.1.0', '2026-11-02T14:59:00Z')");
        });

        using (var anonymous = await w.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/updates", null)))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var overview = await Json(await w.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/updates", reader)));
        Assert.AreEqual("urgent", overview["state"]!.GetValue<string>());
        Assert.AreEqual("Security", overview["reason"]!.GetValue<string>());
        Assert.AreEqual("0.1.0", overview["installed"]!.GetValue<string>());
        Assert.AreEqual("CVE-2026-0001", overview["cves"]![0]!.GetValue<string>());
        var offered = overview["offered"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "0.2.0", "0.1.1" }, offered.Select(o => o!["version"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "0.1.1" }, offered[1]!["path"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray());
        Assert.AreEqual("nodo1", overview["nodes"]![0]!["name"]!.GetValue<string>());

        using (var forbidden = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/updates/requests", reader, new { version = "0.1.1" })))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var unknown = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/updates/requests", writer, new { version = "0.9.9" })))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, unknown.StatusCode);
            StringAssert.Contains((await Json(unknown))["title"]!.GetValue<string>(), "no está en el canal");
        }

        var accepted = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/updates/requests", writer, new { version = "0.1.1" }));
        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
        var location = accepted.Headers.Location!.OriginalString;
        var id = (await Json(accepted))["id"]!.GetValue<long>();
        Assert.AreEqual($"/api/v1/updates/requests/{id}", location);

        var request = await Json(await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/updates/requests/{id}", reader)));
        Assert.AreEqual("0.1.1", request["version"]!.GetValue<string>());
        Assert.AreEqual("pending", request["status"]!.GetValue<string>());
        Assert.AreEqual("web", request["origin"]!.GetValue<string>());
        StringAssert.StartsWith(request["requested_by"]!.GetValue<string>(), "apikey:");
        Assert.IsEmpty(request["progress"]!.AsArray());

        using var missing = await w.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/updates/requests/999999", reader));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        using (response)
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
    }
}
