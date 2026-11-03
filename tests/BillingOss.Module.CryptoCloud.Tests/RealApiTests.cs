using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.CryptoCloud.Tests;

/// <summary>
/// Prueba contra la API real de CryptoCloud (Trybit). Solo corre con datos de un proyecto de prueba a mano: las
/// variables CRYPTOCLOUD_TEST_SHOP_ID y CRYPTOCLOUD_TEST_API_KEY. Si falta alguna, queda inconclusa. Crea una
/// factura de 1 USD que vence sin pagarse y no mueve plata.
/// </summary>
[TestClass]
public sealed class RealApiTests
{
    [TestMethod]
    [TestCategory("CryptoCloudReal")]
    public async Task ApiReal_ProbarLaInstancia_CrearUnaFactura_ConsultarlaYListar()
    {
        var shopId = Environment.GetEnvironmentVariable("CRYPTOCLOUD_TEST_SHOP_ID");
        var apiKey = Environment.GetEnvironmentVariable("CRYPTOCLOUD_TEST_API_KEY");
        if (string.IsNullOrWhiteSpace(shopId) || string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Inconclusive("Faltan CRYPTOCLOUD_TEST_SHOP_ID o CRYPTOCLOUD_TEST_API_KEY: no hay un proyecto de prueba de CryptoCloud para probar.");
        }

        // La secret_key solo sirve para los avisos de pago, que esta prueba no recibe. Un proyecto nuevo arranca en
        // modo prueba: sin allow_test_invoices sus facturas salen como rejected.
        var config = new JsonObject
        {
            ["shop_id"] = shopId,
            ["api_key"] = apiKey,
            ["secret_key"] = CcHost.FakeSecretKey,
            ["invoice_hours"] = 1,
            ["allow_test_invoices"] = true,
        };

        // Con el mismo cliente HTTP que usa el módulo de verdad.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        await using var host = await CcHost.Start(new FakeCryptoCloud(), http);

        // 1. La api_key se valida contra la API.
        var (status, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));

        // 2. Una factura de 1 USD: tiene que volver un link https y su uuid.
        var attempt = $"prueba-{Guid.NewGuid():N}";
        var checkout = new JsonObject { ["attempt_id"] = attempt, ["amount"] = new JsonObject { ["value"] = "1.00", ["currency"] = "USD" } };
        (status, body) = await host.Send("/v0/payment/create_checkout", checkout, Guid.NewGuid().ToString(), config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.StartsWith("https://", body!["result"]!["redirect_url"]!.GetValue<string>());
        var id = body["result"]!["checkout_external_id"]!.GetValue<string>();

        // 3. La factura recién creada: pendiente, por 1 USD y con el intento como referencia.
        (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = id }, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.AreEqual("pending", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("1.00", body["result"]!["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("USD", body["result"]!["amount"]!["currency"]!.GetValue<string>());
        Assert.AreEqual(attempt, body["result"]!["external_reference"]?.GetValue<string>(), "info no devolvió el order_id");

        // 4. Las facturas de hoy: tiene que estar la recién creada, con el intento como referencia. Sin order_id en la
        // lista, la conciliación del núcleo no encuentra el intento de ningún pago.
        var range = new JsonObject
        {
            ["from"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
            ["to"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
        };
        JsonNode? item = null;
        for (int? offset = 0; offset is { } current && item is null;)
        {
            range["offset"] = current;
            (status, body) = await host.Send("/v0/payment/list_payments", range, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            item = body!["result"]!["payments"]!.AsArray().FirstOrDefault(p => p!["payment_external_id"]!.GetValue<string>() == id);
            offset = body["result"]!["next_offset"]?.GetValue<int>();
        }

        Assert.IsNotNull(item, "La factura creada no apareció en list_payments.");
        Assert.AreEqual(attempt, item["external_reference"]?.GetValue<string>(), "list no devolvió el order_id");

        // 5. Una api_key que no es de nadie la rechaza la API: invalid_config.
        var invalid = config.DeepClone().AsObject();
        invalid["api_key"] = "api-key-que-no-existe";
        (status, body) = await host.Send("/v0/instance/test", [], config: invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, Failure(body));
        Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
    }

    /// <summary>El error de la respuesta, para el mensaje del test.</summary>
    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
