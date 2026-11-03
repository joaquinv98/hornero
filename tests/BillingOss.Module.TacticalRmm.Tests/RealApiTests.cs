using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.TacticalRmm.Tests;

/// <summary>
/// Prueba contra un Tactical RMM de prueba, nunca contra el de producción. Solo corre con las variables
/// TRMM_TEST_API_URL (por ejemplo https://api.tactical-prueba.example) y TRMM_TEST_API_KEY, la key de un usuario con
/// el rol del módulo. Si faltan, queda inconclusa. Crea un cliente con el prefijo PRUEBA-, prueba suspensión,
/// estado, vista y link de instalación, y lo da de baja. No instala agentes.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("TacticalRmmReal")]
    public async Task ApiReal_AltaSuspensionEstadoVistaLinkYBaja()
    {
        var url = Environment.GetEnvironmentVariable("TRMM_TEST_API_URL");
        var key = Environment.GetEnvironmentVariable("TRMM_TEST_API_KEY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            Assert.Inconclusive("Faltan TRMM_TEST_API_URL y TRMM_TEST_API_KEY: no hay un Tactical RMM de prueba para probar.");
        }

        var config = new JsonObject { ["api_url"] = url, ["api_key"] = key, ["client_name_prefix"] = "PRUEBA-" };

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        await using var host = await TrmmHost.Start(new FakeTactical(), http);
        var serviceId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var run = Guid.NewGuid().ToString("N");

        // 1. La configuración contra la API.
        var (status, body) = await host.Send("/v0/instance/test", [], [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));

        // 2. Alta, y repetirla con la misma clave no duplica.
        var service = TrmmHost.Service(id: serviceId);
        (status, body) = await host.Op("create", service, key: $"create-{run}", config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        var ids = body!["result"]!["external_ids"]!.AsObject();
        (_, var again) = await host.Op("create", service, key: $"create-{run}", config: config);
        Assert.AreEqual(body.ToJsonString(), again!.ToJsonString());
        service["service"]!["external_ids"] = ids.DeepClone();

        try
        {
            // 3. Suspensión y reactivación sin agentes, estado y vista.
            (status, body) = await host.Op("suspend", service, key: $"suspend-{run}", config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            (status, body) = await host.Op("unsuspend", service, key: $"unsuspend-{run}", config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            (status, body) = await host.Op("get_status", service, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            Assert.AreEqual("active", body!["result"]!["observed_state"]!.GetValue<string>());
            Assert.AreEqual(0, body["result"]!["details"]!["agents"]!.GetValue<int>());
            (status, body) = await host.Op("view", service, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));

            // 4. El link de instalación, si el rol tiene permiso de deployments.
            (status, body) = await host.Op("run_action", service, key: $"link-{run}", input: new JsonObject { ["action"] = "installer_link" }, config: config);
            if (TrmmHost.Code(body) != ErrorCodes.InvalidConfig)
            {
                Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
                Assert.StartsWith(new Uri(url!).GetLeftPart(UriPartial.Authority), body!["result"]!["url"]!.GetValue<string>());
            }
        }
        finally
        {
            // 5. Baja: borra el cliente y su sitio.
            (status, body) = await host.Op("terminate", service, key: $"terminate-{run}", config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        }

        (_, body) = await host.Op("get_status", service, config: config);
        Assert.AreEqual("not_found", body!["result"]!["observed_state"]!.GetValue<string>());

        // 6. Una key inventada da invalid_config.
        var invalid = config.DeepClone().AsObject();
        invalid["api_key"] = "KEYQUENOEXISTE";
        (status, body) = await host.Send("/v0/instance/test", [], [], config: invalid);
        Assert.AreEqual(ErrorCodes.InvalidConfig, TrmmHost.Code(body), Failure(body));
    }

    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
