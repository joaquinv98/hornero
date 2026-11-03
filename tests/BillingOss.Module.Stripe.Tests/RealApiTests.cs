using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>
/// Prueba contra el sandbox de Stripe. Solo corre con una clave de prueba a mano: STRIPE_TEST_API_KEY, restringida o
/// secreta pero siempre de prueba (rk_test_ o sk_test_). Si falta, o si es de producción, queda inconclusa. Crea una
/// sesión de Checkout, que no mueve plata, y lista los movimientos del último día.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("StripeReal")]
    public async Task ApiReal_ProbarLaInstancia_CrearUnCheckout_YConciliar()
    {
        var apiKey = Environment.GetEnvironmentVariable("STRIPE_TEST_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Inconclusive("Falta STRIPE_TEST_API_KEY: no hay una clave de prueba de Stripe para probar.");
        }

        if (!apiKey.StartsWith("rk_test_", StringComparison.Ordinal) && !apiKey.StartsWith("sk_test_", StringComparison.Ordinal))
        {
            Assert.Inconclusive("STRIPE_TEST_API_KEY no es una clave de prueba: esta prueba nunca corre contra producción.");
        }

        var currency = Environment.GetEnvironmentVariable("STRIPE_TEST_CURRENCY") is { Length: > 0 } value ? value : "USD";
        var config = new JsonObject { ["api_key"] = apiKey, ["webhook_secret"] = StripeHost.FakeWebhookSecret };
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        await using var host = await StripeHost.Start(new FakeStripe(), http);

        // 1. La clave vale y es de prueba.
        var (status, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.IsFalse(body!["result"]!["livemode"]!.GetValue<bool>());

        // 2. Una sesión de Checkout con medio guardado: crea el cliente y devuelve una URL https.
        var checkout = new JsonObject
        {
            ["attempt_id"] = $"prueba-{Guid.NewGuid():N}",
            ["amount"] = new JsonObject { ["value"] = "10.00", ["currency"] = currency },
            ["items"] = new JsonArray(new JsonObject { ["title"] = "Prueba del módulo", ["quantity"] = 1, ["unit_price"] = "10.00" }),
            ["return_urls"] = new JsonObject { ["success"] = "https://example.com/pay/return/prueba" },
            ["save_method"] = true,
        };
        (status, body) = await host.Send("/v0/payment/create_checkout", checkout, Guid.NewGuid().ToString(), config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.StartsWith("https://checkout.stripe.com/", body!["result"]!["redirect_url"]!.GetValue<string>());
        Assert.StartsWith("cus_", body["result"]!["external_ids"]!["customer"]!.GetValue<string>());

        // 3. Los movimientos del último día: cada pago con la forma de get_payment.
        var range = new JsonObject { ["from"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), ["to"] = DateTimeOffset.UtcNow.ToString("O") };
        (status, body) = await host.Send("/v0/payment/list_payments", range, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));

        // 4. Una clave inventada la rechaza la API: invalid_config.
        var invalid = new JsonObject { ["api_key"] = "rk_test_noExisteNingunaClaveAsi000", ["webhook_secret"] = StripeHost.FakeWebhookSecret };
        (status, body) = await host.Send("/v0/instance/test", [], config: invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, Failure(body));
        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));
    }

    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
