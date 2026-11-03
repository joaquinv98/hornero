using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.Plesk.Tests;

/// <summary>
/// Ciclo completo contra un Plesk de laboratorio, nunca contra uno productivo. Solo corre con PLESK_TEST_HOST,
/// PLESK_TEST_KEY y PLESK_TEST_IP. Opcionales: PLESK_TEST_PORT (8443), PLESK_TEST_FINGERPRINT, PLESK_TEST_PLAN
/// (Default Domain) y PLESK_TEST_PLAN_2 para probar el cambio de plan. Sin las obligatorias queda inconclusa. Crea un
/// cliente con una suscripción de un dominio .test inventado y lo borra al final.
/// </summary>
[TestClass]
public sealed class RealPleskTests
{
    [TestMethod]
    [TestCategory("PleskReal")]
    public async Task PleskDeLaboratorio_AltaSuspensionReactivacionCambiosAccesoUsoYBaja()
    {
        var hostName = Environment.GetEnvironmentVariable("PLESK_TEST_HOST");
        var key = Environment.GetEnvironmentVariable("PLESK_TEST_KEY");
        var ip = Environment.GetEnvironmentVariable("PLESK_TEST_IP");
        if (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(ip))
        {
            Assert.Inconclusive("Faltan PLESK_TEST_HOST, PLESK_TEST_KEY o PLESK_TEST_IP: no hay un Plesk de laboratorio para probar.");
        }

        var config = new JsonObject
        {
            ["host"] = hostName,
            ["port"] = int.Parse(Environment.GetEnvironmentVariable("PLESK_TEST_PORT") ?? "8443", CultureInfo.InvariantCulture),
            ["api_key"] = key,
            ["ip_address"] = ip,
            ["username_prefix"] = "bo",
        };
        if (Environment.GetEnvironmentVariable("PLESK_TEST_FINGERPRINT") is { Length: > 0 } fingerprint)
        {
            config["tls_fingerprint"] = fingerprint;
        }

        var plan = Environment.GetEnvironmentVariable("PLESK_TEST_PLAN") is { Length: > 0 } p ? p : "Default Domain";
        var uuid = Guid.NewGuid().ToString();
        var domain = $"bo-{uuid[..8]}.test";
        var username = Usernames.For("bo", uuid);

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        await using var host = await ModuleHost.Start(PleskModule.Create(PanelHttp.For, NullLogger.Instance), config);
        var context = ModuleHost.NewService(domain, plan, uuid);
        var existing = ModuleHost.Existing(username, domain, plan, uuid);

        var (status, body) = await host.Send("/v0/instance/test", []);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        (status, body) = await host.Op("validate", context);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));

        var password = $"Lab-{Guid.NewGuid():N}"[..20];
        (status, body) = await host.Op("create", context, ModuleHost.Credentials(password), key: $"svc-{uuid}-create-1");
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.AreEqual(username, ModuleHost.Username(body));

        try
        {
            foreach (var (operation, state) in new[] { ("suspend", "suspended"), ("suspend", "suspended"), ("unsuspend", "active"), ("unsuspend", "active") })
            {
                (status, body) = await host.Op(operation, existing);
                Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
                Assert.AreEqual(state, ModuleHost.Result(body, "observed_state"));
            }

            (status, body) = await host.Op("run_action", existing, ModuleHost.ChangePassword($"Lab-{Guid.NewGuid():N}"[..20]));
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            foreach (var operation in new[] { "get_status", "view", "usage" })
            {
                (status, body) = await host.Op(operation, existing, operation == "usage" ? ModuleHost.Metrics() : null);
                Assert.AreEqual(HttpStatusCode.OK, status, $"{operation}: {Failure(body)}");
            }

            // La última fue usage: las dos lecturas, como texto decimal.
            Assert.AreEqual(2, body!["result"]!["metrics"]!.AsObject().Count, body.ToJsonString());

            // Una IP de documentación: la sesión queda atada a ella y nadie la usa.
            (status, body) = await host.Op("sso", existing, new JsonObject { ["client_ip"] = "192.0.2.1" });
            Assert.AreEqual(HttpStatusCode.OK, status, $"sso: {Failure(body)}");

            if (Environment.GetEnvironmentVariable("PLESK_TEST_PLAN_2") is { Length: > 0 } other)
            {
                (status, body) = await host.Op("change_plan", ModuleHost.Existing(username, domain, other, uuid));
                Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            }
        }
        finally
        {
            (status, body) = await host.Op("terminate", existing);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            (_, body) = await host.Op("get_status", existing);
            Assert.AreEqual("not_found", ModuleHost.Result(body, "observed_state"));
        }
    }

    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
