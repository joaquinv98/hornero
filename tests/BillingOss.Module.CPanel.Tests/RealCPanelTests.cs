using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.CPanel.Tests;

/// <summary>
/// Ciclo completo contra un WHM de laboratorio, nunca contra uno productivo. Solo corre con CPANEL_TEST_HOST y
/// CPANEL_TEST_TOKEN. Opcionales: CPANEL_TEST_USER (root), CPANEL_TEST_PORT (2087), CPANEL_TEST_FINGERPRINT,
/// CPANEL_TEST_PLAN (default) y CPANEL_TEST_PLAN_2 para probar el cambio de plan. Sin las obligatorias queda
/// inconclusa. Crea una cuenta con un dominio .test inventado y la borra al final.
/// </summary>
[TestClass]
public sealed class RealCPanelTests
{
    [TestMethod]
    [TestCategory("CPanelReal")]
    public async Task WhmDeLaboratorio_AltaSuspensionReactivacionCambiosAccesoUsoYBaja()
    {
        var hostName = Environment.GetEnvironmentVariable("CPANEL_TEST_HOST");
        var token = Environment.GetEnvironmentVariable("CPANEL_TEST_TOKEN");
        if (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(token))
        {
            Assert.Inconclusive("Faltan CPANEL_TEST_HOST o CPANEL_TEST_TOKEN: no hay un WHM de laboratorio para probar.");
        }

        var config = new JsonObject
        {
            ["host"] = hostName,
            ["port"] = int.Parse(Environment.GetEnvironmentVariable("CPANEL_TEST_PORT") ?? "2087", CultureInfo.InvariantCulture),
            ["api_user"] = Environment.GetEnvironmentVariable("CPANEL_TEST_USER") ?? "root",
            ["api_token"] = token,
            ["username_prefix"] = "bo",
        };
        if (Environment.GetEnvironmentVariable("CPANEL_TEST_FINGERPRINT") is { Length: > 0 } fingerprint)
        {
            config["tls_fingerprint"] = fingerprint;
        }

        var plan = Environment.GetEnvironmentVariable("CPANEL_TEST_PLAN") is { Length: > 0 } p ? p : "default";
        var uuid = Guid.NewGuid().ToString();
        var domain = $"bo-{uuid[..8]}.test";
        var username = Usernames.For("bo", uuid);

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        await using var host = await ModuleHost.Start(CPanelModule.Create(PanelHttp.For, NullLogger.Instance), config);
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

            if (Environment.GetEnvironmentVariable("CPANEL_TEST_PLAN_2") is { Length: > 0 } other)
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
