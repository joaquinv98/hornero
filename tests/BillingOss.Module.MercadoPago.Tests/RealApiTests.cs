using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>
/// Prueba contra la API real de Mercado Pago. Solo corre con credenciales de prueba a mano: la variable
/// MP_TEST_ACCESS_TOKEN con el access token de prueba de una cuenta vendedor de prueba. Si falta, queda inconclusa.
/// Opcional: MP_TEST_CURRENCY (ARS por defecto) para cuentas de otro país. Crea una preferencia en esa cuenta y no
/// mueve plata.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("MercadoPagoReal")]
    public async Task ApiReal_ProbarLaInstancia_CrearUnCheckout_ListarYConsultarPagos()
    {
        var accessToken = Environment.GetEnvironmentVariable("MP_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Assert.Inconclusive("Falta MP_TEST_ACCESS_TOKEN: no hay credenciales de prueba de Mercado Pago para probar.");
        }

        var currency = Environment.GetEnvironmentVariable("MP_TEST_CURRENCY") is { Length: > 0 } value ? value : "ARS";
        var config = new JsonObject { ["access_token"] = accessToken, ["webhook_secret"] = MpHost.FakeWebhookSecret };

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        await using var host = await MpHost.Start(new FakeMercadoPago(), http);

        // 1. El token se valida contra la API.
        var (status, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.IsFalse(string.IsNullOrEmpty(body!["result"]!["account_id"]!.GetValue<string>()));

        // 2. Una preferencia de Checkout Pro: tiene que volver una URL https a la que redirigir al cliente.
        var checkout = new JsonObject
        {
            ["attempt_id"] = $"prueba-{Guid.NewGuid():N}",
            ["amount"] = new JsonObject { ["value"] = "100.00", ["currency"] = currency },
            ["items"] = new JsonArray(new JsonObject { ["title"] = "Prueba del módulo", ["quantity"] = 1, ["unit_price"] = "100.00" }),
            ["return_urls"] = new JsonObject { ["success"] = "https://example.com/pay/return/prueba" },
        };
        (status, body) = await host.Send("/v0/payment/create_checkout", checkout, Guid.NewGuid().ToString(), config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.StartsWith("https://", body!["result"]!["redirect_url"]!.GetValue<string>());

        // 3. Los pagos de las últimas 24 horas, y el primero de ellos por su ID, que tiene que dar lo mismo.
        var range = new JsonObject
        {
            ["from"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
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

        // 4. Un token que no es de nadie lo rechaza la API: invalid_config.
        var invalid = new JsonObject { ["access_token"] = "TEST-token-que-no-existe", ["webhook_secret"] = MpHost.FakeWebhookSecret };
        (status, body) = await host.Send("/v0/instance/test", [], config: invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, Failure(body));
        Assert.AreEqual(ErrorCodes.InvalidConfig, MpHost.Code(body));
    }

    /// <summary>
    /// Suscripción contra la API real: la crea pendiente de aprobación (no cobra nada hasta que el pagador la aprueba),
    /// la consulta y la cancela. Además de MP_TEST_ACCESS_TOKEN pide MP_TEST_PAYER_EMAIL, el correo de un usuario
    /// comprador de prueba de la misma cuenta.
    /// </summary>
    [TestMethod]
    [TestCategory("MercadoPagoReal")]
    public async Task ApiReal_CrearConsultarYCancelarUnaSuscripcion()
    {
        var accessToken = Environment.GetEnvironmentVariable("MP_TEST_ACCESS_TOKEN");
        var payer = Environment.GetEnvironmentVariable("MP_TEST_PAYER_EMAIL");
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(payer))
        {
            Assert.Inconclusive("Faltan MP_TEST_ACCESS_TOKEN y MP_TEST_PAYER_EMAIL: no hay credenciales de prueba para la suscripción.");
        }

        var currency = Environment.GetEnvironmentVariable("MP_TEST_CURRENCY") is { Length: > 0 } value ? value : "ARS";
        var config = new JsonObject { ["access_token"] = accessToken, ["webhook_secret"] = MpHost.FakeWebhookSecret };
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        await using var host = await MpHost.Start(new FakeMercadoPago(), http);
        var context = new JsonObject { ["account"] = new JsonObject { ["id"] = "prueba", ["contact"] = new JsonObject { ["email"] = payer } } };
        var input = new JsonObject
        {
            ["subscription_id"] = $"prueba-{Guid.NewGuid():N}",
            ["amount"] = new JsonObject { ["value"] = "100.00", ["currency"] = currency },
            ["interval"] = "month",
            ["interval_count"] = 1,
            ["start_date"] = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["reason"] = "Prueba del módulo",
            ["back_url"] = "https://example.com/cuenta/medios-de-pago",
        };

        var (status, body) = await host.Send("/v0/payment/create_subscription", input, Guid.NewGuid().ToString(), config, context);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        var id = body!["result"]!["subscription_external_id"]!.GetValue<string>();
        Assert.StartsWith("https://", body["result"]!["redirect_url"]!.GetValue<string>());

        (status, body) = await host.Send("/v0/payment/get_subscription", new JsonObject { ["subscription_external_id"] = id }, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.AreEqual("pending", body!["result"]!["status"]!.GetValue<string>());

        (status, body) = await host.Send("/v0/payment/cancel_subscription", new JsonObject { ["subscription_external_id"] = id }, Guid.NewGuid().ToString(), config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.AreEqual("cancelled", body!["result"]!["status"]!.GetValue<string>());
    }

    /// <summary>El error de la respuesta, para el mensaje del test.</summary>
    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
