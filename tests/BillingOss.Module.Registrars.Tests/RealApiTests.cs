using System.Net;
using System.Text.Json.Nodes;

namespace BillingOss.Module.Registrars.Tests;

/// <summary>
/// Pruebas contra los ambientes de prueba reales de cada registrar. Solo corren con sus credenciales en variables de
/// entorno, y si faltan quedan inconclusas. Siempre van contra el sandbox (sandbox: true): no registran nada real ni
/// mueven plata. Con REGISTRAR_TEST_REGISTER=1 además registran un nombre al azar en el sandbox y lo renuevan.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("RegistrarReal")]
    [DataRow("internetbs", "INTERNETBS_TEST_API_KEY:api_key", "INTERNETBS_TEST_PASSWORD:password")]
    [DataRow("resellerclub", "RESELLERCLUB_TEST_RESELLER_ID:reseller_id", "RESELLERCLUB_TEST_API_KEY:api_key", "RESELLERCLUB_TEST_CUSTOMER_ID:customer_id")]
    [DataRow("openprovider", "OPENPROVIDER_TEST_USERNAME:username", "OPENPROVIDER_TEST_PASSWORD:password")]
    [DataRow("namecheap", "NAMECHEAP_TEST_API_USER:api_user", "NAMECHEAP_TEST_API_KEY:api_key", "NAMECHEAP_TEST_USERNAME:username", "NAMECHEAP_TEST_CLIENT_IP:client_ip")]
    public async Task Sandbox_ProbarConsultarYPrecios(string registrar, params string[] variables)
    {
        var config = new JsonObject { ["sandbox"] = true };
        foreach (var pair in variables.Select(v => v.Split(':')))
        {
            var value = Environment.GetEnvironmentVariable(pair[0]);
            if (string.IsNullOrWhiteSpace(value))
            {
                Assert.Inconclusive($"Falta {pair[0]}: no hay credenciales del sandbox de {registrar}.");
            }

            config[pair[1]] = value;
        }

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(9) };
        await using var host = await RegistrarHost.Start(registrar, http: http);

        var (status, body) = await host.Send("/v0/instance/test", config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        var name = $"billing-oss-{Guid.NewGuid():N}"[..28] + ".com";
        (status, body) = await host.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray(name, "google.com") }, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsFalse(body!["result"]!["results"]![1]!["available"]!.GetValue<bool>());

        (status, body) = await host.Send("/v0/registrar/get_prices", new JsonObject { ["currency"] = "USD" }, config: config);
        Assert.IsTrue(status == HttpStatusCode.OK || RegistrarHost.Code(body) == "not_supported", body?.ToJsonString());

        if (Environment.GetEnvironmentVariable("REGISTRAR_TEST_REGISTER") != "1")
        {
            return;
        }

        var context = RegistrarHost.Context(name);
        var input = new JsonObject { ["name"] = name, ["years"] = 1, ["nameservers"] = new JsonArray("ns1.digitalocean.com", "ns2.digitalocean.com") };
        (status, body) = await host.Send("/v0/registrar/register", input, $"prueba-{name}", config, context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        (status, body) = await host.Send("/v0/registrar/get_domain", config: config, context: context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var expires = body!["result"]!["expires_on"]!.ToString();
        (status, body) = await host.Send("/v0/registrar/renew", new JsonObject { ["years"] = 1, ["current_expiry"] = expires }, $"renovar-{name}", config, context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        // La zona y la protección: lo que se manda es lo que se lee después.
        var zone = new JsonObject { ["records"] = new JsonArray(new JsonObject { ["name"] = "www", ["type"] = "A", ["value"] = "203.0.113.10", ["ttl"] = 3600 }) };
        (status, body) = await host.Send("/v0/registrar/set_dns", zone, $"zona-{name}", config, context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        (status, body) = await host.Send("/v0/registrar/get_dns", config: config, context: context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsTrue(body!["result"]!["records"]!.AsArray().Any(r => r!["name"]!.ToString() == "www" && r["value"]!.ToString() == "203.0.113.10"), body.ToJsonString());
        (status, body) = await host.Send("/v0/registrar/set_privacy", new JsonObject { ["enabled"] = true }, $"privacidad-{name}", config, context);
        Assert.IsTrue(status == HttpStatusCode.OK || body?["error"]?["details"]?["upstream"]?["reason"]?.ToString() == "no_privacy_subscription", body?.ToJsonString());
    }
}
