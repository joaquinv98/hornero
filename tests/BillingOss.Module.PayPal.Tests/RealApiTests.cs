using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>
/// Prueba contra el sandbox de PayPal. Solo corre con credenciales de una app de sandbox a mano: PAYPAL_TEST_CLIENT_ID y
/// PAYPAL_TEST_CLIENT_SECRET. Si faltan, queda inconclusa. Con PAYPAL_TEST_WEBHOOK_ID prueba además el webhook de la
/// app. Crea una orden que nadie aprueba, así que no mueve plata. Nunca usa el entorno live.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("PayPalReal")]
    public async Task Sandbox_ProbarLaInstancia_CrearUnCheckout_ListarYConsultarPagos()
    {
        var clientId = Environment.GetEnvironmentVariable("PAYPAL_TEST_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("PAYPAL_TEST_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Assert.Inconclusive("Faltan PAYPAL_TEST_CLIENT_ID y PAYPAL_TEST_CLIENT_SECRET: no hay credenciales de sandbox de PayPal para probar.");
        }

        var webhookId = Environment.GetEnvironmentVariable("PAYPAL_TEST_WEBHOOK_ID");
        var config = new JsonObject
        {
            ["environment"] = "sandbox",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["webhook_id"] = string.IsNullOrWhiteSpace(webhookId) ? PpHost.WebhookId : webhookId,
        };

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        await using var host = await PpHost.Start(new FakePayPal(), http);

        // 1. El webhook de la app, con los eventos que usa el módulo.
        HttpStatusCode status;
        JsonNode? body;
        if (!string.IsNullOrWhiteSpace(webhookId))
        {
            (status, body) = await host.Send("/v0/instance/test", [], config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        }

        // 2. Una orden de un dólar: tiene que volver una URL https de PayPal a la que redirigir al cliente.
        var attempt = $"prueba-{Guid.NewGuid():N}";
        var checkout = new JsonObject
        {
            ["attempt_id"] = attempt,
            ["amount"] = new JsonObject { ["value"] = "1.00", ["currency"] = "USD" },
            ["items"] = new JsonArray(new JsonObject { ["title"] = "Prueba del módulo", ["quantity"] = 1, ["unit_price"] = "1.00" }),
            ["return_urls"] = new JsonObject { ["success"] = $"https://example.com/pay/return/{attempt}" },
        };
        (status, body) = await host.Send("/v0/payment/create_checkout", checkout, Guid.NewGuid().ToString(), config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.StartsWith("https://www.sandbox.paypal.com/", body!["result"]!["redirect_url"]!.GetValue<string>());

        // 3. La orden todavía no está aprobada: no tiene un pago y no se captura.
        var order = body["result"]!["checkout_external_id"]!.GetValue<string>();
        (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = $"order:{order}" }, config: config);
        Assert.AreEqual(HttpStatusCode.NotFound, status, Failure(body));

        // 4. Las capturas de los últimos 3 días, y la primera por su ID, que tiene que dar lo mismo. Necesita
        // Transaction search activado en la app.
        var range = new JsonObject
        {
            ["from"] = DateTimeOffset.UtcNow.AddDays(-3).ToString("O"),
            ["to"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        (status, body) = await host.Send("/v0/payment/list_payments", range, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        var payments = body!["result"]!["payments"]!.AsArray();
        if (payments.Count > 0)
        {
            var id = payments[0]!["payment_external_id"]!.GetValue<string>();
            var listed = payments[0]!["status"]!.GetValue<string>();
            (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = id }, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            Assert.AreEqual(listed, body!["result"]!["status"]!.GetValue<string>());
        }

        // 5. Un secret que no es de nadie lo rechaza PayPal: invalid_config.
        var invalid = config.DeepClone().AsObject();
        invalid["client_secret"] = $"no-existe-{Guid.NewGuid():N}";
        (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "3C679366HH908993F" }, config: invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, Failure(body));
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
    }

    /// <summary>El error de la respuesta, para el mensaje del test.</summary>
    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
