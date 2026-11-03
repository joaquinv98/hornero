using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>
/// payment.verify_webhook: la firma la verifica PayPal (POST /v1/notifications/verify-webhook-signature) con el evento
/// tal cual llegó, y el módulo normaliza los eventos que tocan pagos.
/// </summary>
[TestClass]
public sealed class WebhookTests
{
    private const string VerifyRoute = "POST /v1/notifications/verify-webhook-signature";
    private const string Verify = "/v0/payment/verify_webhook";

    /// <summary>Firma inventada, con los caracteres de base64. La verifica PayPal: el módulo solo la pasa.</summary>
    private const string Signature = "ZmlybWEtZGUtcHJ1ZWJh+/==";

    private const string Success = """{ "verification_status": "SUCCESS" }""";

    // Espacios y orden de campos a propósito: PayPal tiene que recibir exactamente estos bytes.
    private const string CaptureCompleted = """
        {"id":"WH-58D329510W468432D-8HN650336L201105X","create_time":"2026-10-25T19:01:13.000Z",  "resource_type":"capture",
          "event_type":"PAYMENT.CAPTURE.COMPLETED","summary":"Payment completed for $ 100.0 USD",
          "resource":{"id":"3C679366HH908993F","status":"COMPLETED","amount":{"currency_code":"USD","value":"100.00"},"custom_id":"att-1042"}}
        """;

    [TestMethod]
    public async Task FirmaValida_PayPalRecibeElEventoExactoYSaleLaCaptura()
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Verify, Input(CaptureCompleted));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.IsTrue(result["valid"]!.GetValue<bool>());
        var events = result["events"]!.AsArray();
        Assert.HasCount(1, events);
        Assert.AreEqual("WH-58D329510W468432D-8HN650336L201105X", events[0]!["event_id"]!.GetValue<string>());
        Assert.AreEqual("PAYMENT.CAPTURE.COMPLETED", events[0]!["type"]!.GetValue<string>());
        Assert.AreEqual(Samples.CaptureId, events[0]!["payment_external_id"]!.GetValue<string>());

        // El evento va pegado tal cual, sin volver a serializarlo, y los encabezados en sus campos.
        var sent = pp.Single(VerifyRoute);
        Assert.EndsWith($",\"webhook_event\":{CaptureCompleted}}}", sent.Text!);
        var verification = sent.Body!;
        Assert.AreEqual("SHA256withRSA", verification["auth_algo"]!.GetValue<string>());
        Assert.AreEqual("https://api.sandbox.paypal.com/v1/notifications/certs/CERT-360caa42-fca2a594-a5cafa77", verification["cert_url"]!.GetValue<string>());
        Assert.AreEqual("69cd13f0-d67a-11e5-baa3-778b53f4ae55", verification["transmission_id"]!.GetValue<string>());
        Assert.AreEqual(Signature, verification["transmission_sig"]!.GetValue<string>());
        Assert.AreEqual("2026-10-25T19:01:13Z", verification["transmission_time"]!.GetValue<string>());
        Assert.AreEqual(PpHost.WebhookId, verification["webhook_id"]!.GetValue<string>());
        Assert.AreEqual("PAYMENT.CAPTURE.COMPLETED", verification["webhook_event"]!["event_type"]!.GetValue<string>());
        Assert.AreEqual($"Bearer {pp.AccessToken}", sent.Headers["Authorization"]);
    }

    [TestMethod]
    public async Task FirmaQuePayPalNoReconoce_NoEsValida()
    {
        var pp = new FakePayPal().On(VerifyRoute, """{ "verification_status": "FAILURE" }""");
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Verify, Input(CaptureCompleted));

        Assert.AreEqual(HttpStatusCode.OK, status);
        AssertRejected(body);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest, "INVALID_RESOURCE_ID")]
    [DataRow(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE_ENTITY")]
    public async Task UnRechazoDePayPal_EsUnError_NoUnaFirmaFalsa(HttpStatusCode upstream, string issue)
    {
        // Los formatos ya se comprobaron: un 4xx es el webhook_id, la app o el pedido. Como error, el núcleo deja el
        // aviso sin procesar y el trabajo fallido a la vista. Como valid false, lo descartaría para siempre.
        var pp = new FakePayPal().On(VerifyRoute, Samples.Error("VALIDATION_ERROR", "Invalid request - see details", (issue, "webhook_id")), upstream);
        var logger = new ListLogger();
        await using var host = await PpHost.Start(pp, logger: logger);

        var (status, body) = await host.Send(Verify, Input(CaptureCompleted));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(body));
        Assert.AreEqual(issue, body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());

        // Mientras dure, todos los avisos quedan sin procesar: es un error para el staff, no un aviso más.
        Assert.IsTrue(logger.Lines.Any(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error && l.Text.Contains("webhook_id")));
    }

    [TestMethod]
    [DataRow("paypal-cert-url", "http://api.sandbox.paypal.com/v1/notifications/certs/CERT-1")]
    [DataRow("paypal-cert-url", "certs/CERT-1")]
    [DataRow("paypal-transmission-time", "ayer")]
    [DataRow("paypal-transmission-id", "1234567890")]
    [DataRow("paypal-transmission-id", "-69cd13f0")]
    [DataRow("paypal-transmission-sig", "+firma")]
    [DataRow("paypal-transmission-sig", "x")]
    [DataRow("paypal-auth-algo", "SHA256-RSA")]
    public async Task EncabezadoConUnFormatoQuePayPalNoAcepta_NoEsValidaYNoLlamaAPayPal(string header, string value)
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);
        var input = Input(CaptureCompleted);
        input["headers"]![header] = value;

        var (status, body) = await host.Send(Verify, input);

        Assert.AreEqual(HttpStatusCode.OK, status);
        AssertRejected(body);
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    [DataRow("paypal-transmission-sig")]
    [DataRow("paypal-transmission-id")]
    [DataRow("paypal-cert-url")]
    [DataRow("paypal-auth-algo")]
    [DataRow("paypal-transmission-time")]
    public async Task SinUnEncabezado_NoEsValidaYNoLlamaAPayPal(string header)
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);
        var input = Input(CaptureCompleted);
        input["headers"]!.AsObject().Remove(header);

        var (_, body) = await host.Send(Verify, input);

        AssertRejected(body);
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    [DataRow("no es json")]
    [DataRow("[1, 2]")]
    [DataRow("""{"id":"WH-1"}, "webhook_id": "OTRO" """)]
    public async Task CuerpoQueNoEsUnObjetoJson_NoEsValidaYNoLlamaAPayPal(string raw)
    {
        // El último intentaría escaparse del campo webhook_event para cambiar el webhook_id.
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(Verify, Input(raw));

        AssertRejected(body);
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    public async Task EncabezadosConOtrasMayusculasYEnLista_SeLeenIgual()
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);
        var input = Input(CaptureCompleted);
        var headers = new JsonObject();
        foreach (var (name, value) in input["headers"]!.AsObject())
        {
            headers[name.ToUpperInvariant()] = new JsonArray(value!.GetValue<string>());
        }

        input["headers"] = headers;

        var (_, body) = await host.Send(Verify, input);

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task OrdenAprobadaDeEsteModulo_DaElEventoParaCapturarla()
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(Verify, Input(Event("WH-1", "CHECKOUT.ORDER.APPROVED", OrderResource("billing-oss"))));

        var events = body!["result"]!["events"]!.AsArray();
        Assert.HasCount(1, events);
        Assert.AreEqual($"order:{Samples.OrderId}", events[0]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("CHECKOUT.ORDER.APPROVED", events[0]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task OrdenAprobadaDeOtroSistema_NoDaEventos()
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(Verify, Input(Event("WH-1", "CHECKOUT.ORDER.APPROVED", OrderResource("default"))));

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }

    [TestMethod]
    [DataRow("PAYMENT.CAPTURE.REFUNDED")]
    [DataRow("PAYMENT.CAPTURE.REVERSED")]
    public async Task ReembolsoOReversion_DaLaCapturaDeSuEnlaceUp(string type)
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(Verify, Input(Event("WH-2", type, Samples.Refund("1JU08902781691411", "40.00"))));

        var events = body!["result"]!["events"]!.AsArray();
        Assert.HasCount(1, events);
        Assert.AreEqual(Samples.CaptureId, events[0]!["payment_external_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ReembolsoSinEnlaceALaCaptura_NoDaEventos()
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);
        var refund = Samples.Refund("1JU08902781691411", "40.00");
        refund.Remove("links");

        var (_, body) = await host.Send(Verify, Input(Event("WH-2", "PAYMENT.CAPTURE.REFUNDED", refund)));

        Assert.IsEmpty(body!["result"]!["events"]!.AsArray());
    }

    [TestMethod]
    public async Task Disputa_DaUnEventoPorCadaCapturaReclamada()
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);
        var dispute = new JsonObject
        {
            ["dispute_id"] = "PP-D-27803",
            ["status"] = "OPEN",
            ["disputed_transactions"] = new JsonArray(
                new JsonObject { ["seller_transaction_id"] = Samples.CaptureId },
                new JsonObject { ["seller_transaction_id"] = "7AB12345CD6789012" }),
        };

        var (_, body) = await host.Send(Verify, Input(Event("WH-3", "CUSTOMER.DISPUTE.CREATED", dispute)));

        var events = body!["result"]!["events"]!.AsArray();
        Assert.HasCount(2, events);
        Assert.AreEqual($"WH-3:{Samples.CaptureId}", events[0]!["event_id"]!.GetValue<string>());
        Assert.AreEqual("7AB12345CD6789012", events[1]!["payment_external_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EventoQueNoTocaPagos_EsValidoSinEventos()
    {
        var pp = new FakePayPal().On(VerifyRoute, Success);
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(Verify, Input(Event("WH-4", "BILLING.PLAN.CREATED", new JsonObject { ["id"] = "P-7GL4271244454362WXNWU5NQ" })));

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }

    [TestMethod]
    public async Task PayPalCaido_EsUpstreamUnavailable_ParaQueElNucleoReintente()
    {
        var pp = new FakePayPal().On(VerifyRoute, "{}", HttpStatusCode.ServiceUnavailable);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Verify, Input(CaptureCompleted));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code(body));
    }

    [TestMethod]
    public async Task SinWebhookIdEnLaConfiguracion_EsInvalidConfig()
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);
        var config = PpHost.Config();
        config.Remove("webhook_id");

        var (status, body) = await host.Send(Verify, Input(CaptureCompleted), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
        Assert.IsEmpty(pp.Requests);
    }

    private static JsonObject OrderResource(string reference) => new()
    {
        ["id"] = Samples.OrderId,
        ["intent"] = "CAPTURE",
        ["status"] = "APPROVED",
        ["purchase_units"] = new JsonArray(new JsonObject { ["reference_id"] = reference, ["custom_id"] = "att-1042" }),
    };

    private static string Event(string id, string type, JsonObject resource) => new JsonObject
    {
        ["id"] = id,
        ["event_version"] = "1.0",
        ["create_time"] = "2026-10-25T19:01:13.000Z",
        ["event_type"] = type,
        ["resource"] = resource,
    }.ToJsonString();

    /// <summary>Lo que el núcleo guardó del pedido de PayPal: los encabezados de transmisión y el cuerpo en base64.</summary>
    private static JsonObject Input(string raw) => new()
    {
        ["headers"] = new JsonObject
        {
            ["paypal-auth-algo"] = "SHA256withRSA",
            ["paypal-cert-url"] = "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-360caa42-fca2a594-a5cafa77",
            ["paypal-transmission-id"] = "69cd13f0-d67a-11e5-baa3-778b53f4ae55",
            ["paypal-transmission-sig"] = Signature,
            ["paypal-transmission-time"] = "2026-10-25T19:01:13Z",
            ["content-type"] = "application/json",
        },
        ["query"] = new JsonObject(),
        ["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
    };

    private static void AssertRejected(JsonNode? body)
    {
        Assert.IsFalse(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }
}
