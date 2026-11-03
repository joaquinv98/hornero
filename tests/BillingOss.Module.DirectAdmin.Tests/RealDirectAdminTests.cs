using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.DirectAdmin.Tests;

/// <summary>
/// Ciclo completo contra un DirectAdmin de laboratorio, nunca contra uno productivo. Solo corre con DIRECTADMIN_TEST_HOST,
/// DIRECTADMIN_TEST_KEY, DIRECTADMIN_TEST_IP y DIRECTADMIN_TEST_PLAN. Opcionales: DIRECTADMIN_TEST_USER (admin),
/// DIRECTADMIN_TEST_PORT (2222), DIRECTADMIN_TEST_FINGERPRINT y DIRECTADMIN_TEST_PLAN_2 para probar el cambio de plan.
/// Sin las obligatorias queda inconclusa. Crea un usuario con un dominio .test inventado y lo borra al final.
/// </summary>
[TestClass]
public sealed class RealDirectAdminTests
{
    [TestMethod]
    [TestCategory("DirectAdminReal")]
    public async Task DirectAdminDeLaboratorio_AltaSuspensionReactivacionCambiosAccesoUsoYBaja()
    {
        var hostName = Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_HOST");
        var key = Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_KEY");
        var ip = Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_IP");
        var plan = Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_PLAN");
        if (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(plan))
        {
            Assert.Inconclusive("Faltan DIRECTADMIN_TEST_HOST, DIRECTADMIN_TEST_KEY, DIRECTADMIN_TEST_IP o DIRECTADMIN_TEST_PLAN: no hay un DirectAdmin de laboratorio para probar.");
        }

        var config = new JsonObject
        {
            ["host"] = hostName,
            ["port"] = int.Parse(Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_PORT") ?? "2222", CultureInfo.InvariantCulture),
            ["api_user"] = Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_USER") ?? "admin",
            ["api_key"] = key,
            ["ip_address"] = ip,
            ["username_prefix"] = "bo",
        };
        if (Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_FINGERPRINT") is { Length: > 0 } fingerprint)
        {
            config["tls_fingerprint"] = fingerprint;
        }

        var uuid = Guid.NewGuid().ToString();
        var domain = $"bo-{uuid[..8]}.test";
        var username = Usernames.For("bo", uuid);

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        await using var host = await ModuleHost.Start(DirectAdminModule.Create(PanelHttp.For, NullLogger.Instance), config);
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
            foreach (var operation in new[] { "get_status", "view", "sso", "usage" })
            {
                (status, body) = await host.Op(operation, existing, operation == "usage" ? ModuleHost.Metrics() : null);
                Assert.AreEqual(HttpStatusCode.OK, status, $"{operation}: {Failure(body)}");
            }

            // La última fue usage: las dos lecturas, como texto decimal.
            Assert.AreEqual(2, body!["result"]!["metrics"]!.AsObject().Count, body.ToJsonString());

            if (Environment.GetEnvironmentVariable("DIRECTADMIN_TEST_PLAN_2") is { Length: > 0 } other)
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
