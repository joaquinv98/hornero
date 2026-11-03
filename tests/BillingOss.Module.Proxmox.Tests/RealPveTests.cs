using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>
/// Prueba contra un Proxmox de laboratorio. Solo corre a mano con estas variables, y si faltan queda inconclusa:
/// PVE_TEST_API_URL (origen https), PVE_TEST_TOKEN_ID, PVE_TEST_TOKEN_SECRET, PVE_TEST_NODE y PVE_TEST_TEMPLATE, más
/// PVE_TEST_CA_FILE si el certificado es de un CA propio. Por defecto solo lee: instance/test y validate. Con
/// PVE_TEST_MUTATE=1, PVE_TEST_POOL (el network_pool en JSON) y PVE_TEST_SERVICE_ID (un VMID libre de laboratorio)
/// hace el ciclo completo: alta, suspensión, reactivación, reinstalación y baja.
/// </summary>
[TestClass]
public sealed class RealPveTests
{
    [TestMethod]
    [TestCategory("ProxmoxReal")]
    public async Task ProxmoxDeLaboratorio_PruebaValidaYCicloCompleto()
    {
        var url = Environment.GetEnvironmentVariable("PVE_TEST_API_URL");
        var tokenId = Environment.GetEnvironmentVariable("PVE_TEST_TOKEN_ID");
        var tokenSecret = Environment.GetEnvironmentVariable("PVE_TEST_TOKEN_SECRET");
        var node = Environment.GetEnvironmentVariable("PVE_TEST_NODE");
        var template = Environment.GetEnvironmentVariable("PVE_TEST_TEMPLATE");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(tokenSecret)
            || string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(template))
        {
            Assert.Inconclusive("Faltan las variables PVE_TEST_*: no hay un Proxmox de laboratorio para probar.");
        }

        await using var host = await ModuleHost.Start(options: ProxmoxOptions.Default);
        var config = new JsonObject
        {
            ["api_url"] = url,
            ["token_id"] = tokenId,
            ["token_secret"] = tokenSecret,
            ["network_pool"] = JsonNode.Parse(Environment.GetEnvironmentVariable("PVE_TEST_POOL") ?? "[]"),
        };
        if (Environment.GetEnvironmentVariable("PVE_TEST_CA_FILE") is { Length: > 0 } caFile)
        {
            config["ca_pem"] = await File.ReadAllTextAsync(caFile);
        }

        var p = new JsonObject
        {
            ["template_id"] = int.Parse(template, CultureInfo.InvariantCulture),
            ["node"] = node,
            ["cores"] = 1,
            ["memory_mb"] = 1024,
            ["disk_gb"] = int.Parse(Environment.GetEnvironmentVariable("PVE_TEST_DISK_GB") ?? "10", CultureInfo.InvariantCulture),
            ["rate_mb_s"] = "1.25",
        };

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        (status, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = p }, [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        if (Environment.GetEnvironmentVariable("PVE_TEST_MUTATE") != "1"
            || !long.TryParse(Environment.GetEnvironmentVariable("PVE_TEST_SERVICE_ID"), out var service))
        {
            return;
        }

        var context = ModuleHost.Context(service, p: p);
        var run = Guid.NewGuid().ToString("N");
        (status, body) = await Until(host, "/v0/provisioning/create", host.Credentials(), context, $"lab-{run}-create", config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        context = ModuleHost.Context(service, (JsonObject)ModuleHost.Result(body)["external_ids"]!.DeepClone(), p);

        (status, body) = await Until(host, "/v0/provisioning/suspend", [], context, $"lab-{run}-suspend", config);
        Assert.AreEqual("suspended", ModuleHost.Result(body)["observed_state"]!.GetValue<string>(), body?.ToJsonString());
        (status, body) = await Until(host, "/v0/provisioning/unsuspend", [], context, $"lab-{run}-unsuspend", config);
        Assert.AreEqual("active", ModuleHost.Result(body)["observed_state"]!.GetValue<string>(), body?.ToJsonString());

        var reinstall = host.Credentials();
        reinstall["action"] = "reinstall";
        (status, body) = await Until(host, "/v0/provisioning/run_action", reinstall, context, $"lab-{run}-reinstall", config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        (status, body) = await Until(host, "/v0/provisioning/terminate", [], context, $"lab-{run}-terminate", config);
        Assert.AreEqual("terminated", ModuleHost.Result(body)["observed_state"]!.GetValue<string>(), body?.ToJsonString());
    }

    /// <summary>Repite como el núcleo: misma clave mientras haya conflict y consultas mientras haya pending.</summary>
    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Until(ModuleHost host, string path, JsonObject input, JsonObject context, string key, JsonObject config)
    {
        for (var i = 0; i < 40; i++)
        {
            var (status, body) = await host.Send(path, input, context, key, config);
            if (body?["status"]?.GetValue<string>() == "pending")
            {
                var operation = body["operation_id"]!.GetValue<string>();
                for (var j = 0; j < 40 && body?["status"]?.GetValue<string>() == "pending"; j++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    (status, body) = await host.Send($"/v0/operations/{operation}", [], context, config: config);
                }

                return (status, body);
            }

            if (ModuleHost.Code(body) != "conflict")
            {
                return (status, body);
            }

            await Task.Delay(TimeSpan.FromSeconds(body!["error"]!["retry_after"]?.GetValue<int>() ?? 15));
        }

        throw new AssertFailedException($"{path} no terminó.");
    }
}
