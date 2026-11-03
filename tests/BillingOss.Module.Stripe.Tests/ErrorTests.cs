using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>Errores de Stripe y de la configuración, en el formato y con los códigos de la sección 10 del protocolo.</summary>
[TestClass]
public sealed class ErrorTests
{
    private const string IntentRoute = "GET /v1/payment_intents/pi_3Prueba";

    [TestMethod]
    [DataRow(401, ErrorCodes.InvalidConfig)]
    [DataRow(403, ErrorCodes.InvalidConfig)]
    [DataRow(404, ErrorCodes.NotFound)]
    [DataRow(409, ErrorCodes.Conflict)]
    [DataRow(429, ErrorCodes.RateLimited)]
    [DataRow(500, ErrorCodes.UpstreamUnavailable)]
    [DataRow(503, ErrorCodes.UpstreamUnavailable)]
    public async Task ErroresHttp_EnUnaConsulta(int http, string code)
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Error("invalid_request_error", "x", "Mensaje"), (HttpStatusCode)http);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "pi_3Prueba" });

        Assert.AreEqual(code, StripeHost.Code(body));
        Assert.AreEqual(ErrorCodes.HttpStatus(code), (int)status);
    }

    [TestMethod]
    public async Task Error500AlMoverPlata_EsIncierto()
    {
        var stripe = new FakeStripe()
            .On(IntentRoute, Samples.Intent())
            .On("POST /v1/refunds", Samples.Error("api_error"), HttpStatusCode.InternalServerError);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/refund", new JsonObject { ["payment_external_id"] = "pi_3Prueba" }, "refund-1");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, StripeHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Limite_TomaElRetryAfterDeStripe()
    {
        var stripe = new FakeStripe().On(IntentRoute, _ =>
        {
            var response = Samples.Json(Samples.Error("rate_limit_error"), HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "pi_3Prueba" });

        Assert.AreEqual(ErrorCodes.RateLimited, StripeHost.Code(body));
        Assert.AreEqual(7, body!["error"]!["retry_after"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ParametroInvalido_EsInvalidInputConElCampo_YLaClaveDeOtroPedidoEsConflicto()
    {
        var stripe = new FakeStripe()
            .On(IntentRoute, Samples.Intent())
            .On("POST /v1/refunds", Samples.Error("invalid_request_error", "parameter_invalid_integer", "Invalid integer", param: "amount"), HttpStatusCode.BadRequest);
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/refund", new JsonObject { ["payment_external_id"] = "pi_3Prueba" }, "refund-1");
        Assert.AreEqual(ErrorCodes.InvalidInput, StripeHost.Code(body));
        CollectionAssert.AreEqual(new[] { "amount" }, StripeHost.Strings(body!["error"]!["details"]!["fields"]));

        stripe.On("POST /v1/refunds", Samples.Error("idempotency_error", message: "Keys for idempotent requests can only be used with the same parameters"), HttpStatusCode.BadRequest);
        (_, body) = await host.Send("/v0/payment/refund", new JsonObject { ["payment_external_id"] = "pi_3Prueba" }, "refund-2");
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, StripeHost.Code(body));
    }

    [TestMethod]
    public async Task ElPermisoQueFaltaVaEnElMensaje_YLaClaveNuncaSale()
    {
        var message = $"The provided key '{StripeHost.FakeApiKey}' does not have the required permissions for this endpoint. Having the 'rak_disputes_read' permission would allow this request.";
        var stripe = new FakeStripe().On(IntentRoute, Samples.Error("invalid_request_error", message: message), HttpStatusCode.Forbidden);
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "pi_3Prueba" });

        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));
        StringAssert.Contains(body!["error"]!["message"]!.GetValue<string>(), "rak_disputes_read");
        Assert.DoesNotContain(StripeHost.FakeApiKey, body.ToJsonString());
    }

    [TestMethod]
    [DataRow("pk_test_publicable", "api_key")]
    [DataRow("rk_test_con espacio", "api_key")]
    [DataRow("", "api_key")]
    public async Task ClaveConFormaEquivocada_NoSaleALaRed(string key, string field)
    {
        var stripe = new FakeStripe();
        await using var host = await StripeHost.Start(stripe);
        var config = StripeHost.Config();
        config["api_key"] = key;

        var (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "pi_3Prueba" }, config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));
        CollectionAssert.AreEqual(new[] { field }, StripeHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(stripe.Requests);
    }

    [TestMethod]
    public async Task SecretoDeAvisosQueNoEsWhsec_EsConfiguracionInvalida()
    {
        await using var host = await StripeHost.Start(new FakeStripe());
        var config = StripeHost.Config();
        config["webhook_secret"] = "secreto";

        var (_, body) = await host.Send("/v0/payment/verify_webhook", new JsonObject { ["headers"] = new JsonObject(), ["body_base64"] = "e30=" }, config: config);

        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));
        CollectionAssert.AreEqual(new[] { "webhook_secret" }, StripeHost.Strings(body!["error"]!["details"]!["fields"]));
    }

    [TestMethod]
    public async Task RespuestaQueNoEsJsonDespuesDeEnviar_EsIncierta()
    {
        var stripe = new FakeStripe()
            .On("GET /v1/payment_intents", """{ "object": "list", "data": [] }""")
            .On("POST /v1/payment_intents", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy</html>") });
        await using var host = await StripeHost.Start(stripe);
        var input = new JsonObject
        {
            ["attempt_id"] = "att-1",
            ["amount"] = new JsonObject { ["value"] = "10.00", ["currency"] = "USD" },
            ["customer_external_id"] = "cus_1",
            ["method_external_id"] = "pm_1",
        };

        var (_, body) = await host.Send("/v0/payment/charge_saved", input, "autocharge-1");

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, StripeHost.Code(body));
    }
}
