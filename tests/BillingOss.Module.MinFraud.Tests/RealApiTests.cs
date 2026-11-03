using System.Net;
using System.Text.Json.Nodes;

namespace BillingOss.Module.MinFraud.Tests;

/// <summary>
/// Prueba contra el minFraud real. Solo corre a mano con MAXMIND_ACCOUNT_ID y MAXMIND_LICENSE_KEY de una cuenta de
/// MaxMind, y MAXMIND_SANDBOX=1 para el sandbox. Si faltan, queda inconclusa. Hace dos consultas, que se cobran.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("MinFraudReal")]
    public async Task ApiReal_ProbarLaInstancia_YEvaluarUnPedido()
    {
        var account = Environment.GetEnvironmentVariable("MAXMIND_ACCOUNT_ID");
        var license = Environment.GetEnvironmentVariable("MAXMIND_LICENSE_KEY");
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(license))
        {
            Assert.Inconclusive("Faltan MAXMIND_ACCOUNT_ID y MAXMIND_LICENSE_KEY: no hay credenciales de MaxMind para probar.");
        }

        var config = new JsonObject
        {
            ["account_id"] = account,
            ["license_key"] = license,
            ["service"] = "factors",
            ["environment"] = Environment.GetEnvironmentVariable("MAXMIND_SANDBOX") == "1" ? "sandbox" : "production",
        };

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        await using var host = await MinFraudHost.Start(handler);

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body.ToJsonString());

        (status, body) = await host.Evaluate(Samples.Context(contact: true, address: true), key: $"prueba-{Guid.NewGuid():N}", config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body.ToJsonString());
        var score = decimal.Parse(body["result"]!["score"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(score is >= 0m and <= 100m);
        Assert.IsFalse(string.IsNullOrEmpty(body["result"]!["external_id"]!.GetValue<string>()));

        // Una license key que no es de nadie: invalid_config.
        config["license_key"] = "licencia-que-no-existe";
        (_, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual("invalid_config", body["error"]!["code"]!.GetValue<string>());
    }
}
