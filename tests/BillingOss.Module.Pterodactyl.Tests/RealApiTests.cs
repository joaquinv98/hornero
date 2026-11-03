using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>
/// Prueba contra un panel Pterodactyl de laboratorio, nunca contra el de producción. Sin las variables queda
/// inconclusa:
/// - PTERODACTYL_TEST_URL y PTERODACTYL_TEST_KEY: URL del panel y clave de la Application API. Con solo estas dos
///   corre la prueba de la instancia, que solo lee.
/// - PTERODACTYL_TEST_PLAN y PTERODACTYL_TEST_EMAIL: parámetros del producto en JSON (product_schema) y un correo de
///   prueba propio, al que el panel manda el enlace del usuario. Con estas dos además crea un server, lo suspende,
///   lo reactiva, consulta su estado y su vista, y lo borra.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("PterodactylReal")]
    [Timeout(15 * 60 * 1000, CooperativeCancellation = true)]
    public async Task PanelReal_ProbarLaInstancia_YElCicloDeVidaCompleto()
    {
        var url = Environment.GetEnvironmentVariable("PTERODACTYL_TEST_URL");
        var key = Environment.GetEnvironmentVariable("PTERODACTYL_TEST_KEY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            Assert.Inconclusive("Faltan PTERODACTYL_TEST_URL y PTERODACTYL_TEST_KEY: no hay un panel de laboratorio para probar.");
        }

        var config = new JsonObject { ["panel_url"] = url, ["application_key"] = key };

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        await using var host = await PteroHost.Start(new FakePanel(), http);

        // 1. La prueba de la instancia solo lee.
        var (status, body) = await host.Send("/v0/instance/test", [], [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));

        var plan = Environment.GetEnvironmentVariable("PTERODACTYL_TEST_PLAN");
        var email = Environment.GetEnvironmentVariable("PTERODACTYL_TEST_EMAIL");
        if (string.IsNullOrWhiteSpace(plan) || string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        // Un ID de servicio alto y al azar: es el external_id del server y no choca con los de verdad.
        var service = 900_000_000L + Random.Shared.Next(1, 99_999_999);
        var context = Samples.Context(
            service: service,
            plan: JsonNode.Parse(plan)!.AsObject(),
            account: Samples.Account(id: 900_000_001, name: "Prueba Billing", email: email));
        string Key(string operation) => string.Create(CultureInfo.InvariantCulture, $"svc-{service}-{operation}-1");

        try
        {
            // 2. Alta: termina apenas existe el server. La instalación se espera en la vista, hasta 10 minutos.
            (status, body) = await host.Call("create", context, Key("create"), config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
            Assert.AreEqual("active", body["result"]!["observed_state"]!.GetValue<string>());
            Assert.HasCount(6, body["result"]!["external_ids"]!.AsObject());
            for (var i = 0; i < 60; i++)
            {
                var (_, view) = await host.Call("view", context, config: config);
                if (view!["result"]!["blocks"]![0]!["text"]!.GetValue<string>() != "Instalando")
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            // 3. Repetir el alta con otra clave converge al mismo server.
            var (_, again) = await host.Call("create", context, Key("create-again"), config: config);
            Assert.AreEqual(body.ToJsonString(), again!.ToJsonString());

            // 4. Suspender, reactivar, estado y vista.
            (status, body) = await host.Call("suspend", context, Key("suspend"), config: config);
            Assert.AreEqual("suspended", body!["result"]!["observed_state"]!.GetValue<string>(), Failure(body));
            (status, body) = await host.Call("unsuspend", context, Key("unsuspend"), config: config);
            Assert.AreEqual("active", body!["result"]!["observed_state"]!.GetValue<string>(), Failure(body));
            (status, body) = await host.Call("get_status", context, config: config);
            Assert.IsTrue(body!["result"]!["package_matches"]!.GetValue<bool>(), Failure(body));
            (status, body) = await host.Call("view", context, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        }
        finally
        {
            // 5. Baja, y el estado pasa a not_found.
            (status, body) = await host.Call("terminate", context, Key("terminate"), config: config);
            Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>(), Failure(body));
        }

        (_, body) = await host.Call("get_status", context, config: config);
        Assert.AreEqual("not_found", body!["result"]!["observed_state"]!.GetValue<string>());
    }

    /// <summary>El error de la respuesta, para el mensaje del test.</summary>
    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
