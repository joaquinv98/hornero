using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>payment.saved_methods: cobrar sin el cliente presente, consultar y quitar un medio guardado.</summary>
[TestClass]
public sealed class SavedMethodTests
{
    private const string ListRoute = "GET /v1/payment_intents";
    private const string Route = "POST /v1/payment_intents";
    private const string NoIntents = """{ "object": "list", "has_more": false, "data": [] }""";

    [TestMethod]
    public async Task Cobra_SinElClientePresente_ConLaClaveDelNucleo()
    {
        var stripe = new FakeStripe().On(ListRoute, NoIntents).On(Route, Samples.Intent(reference: "att-55", withFee: false));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("approved", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("att-55", body["result"]!["external_reference"]!.GetValue<string>());

        // Antes de cobrar busca un cobro de este intento entre los del cliente de los últimos días.
        var list = stripe.Single(ListRoute);
        Assert.AreEqual("cus_Prueba123", list.Query("customer").ToString());
        Assert.AreEqual(StripeHost.Now.AddDays(-3).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), list.Query("created[gte]").ToString());

        var sent = stripe.Single(Route);
        Assert.AreEqual("autocharge-55", sent.Headers["Idempotency-Key"]);
        Assert.AreEqual("1210000", sent.Field("amount"));
        Assert.AreEqual("ars", sent.Field("currency"));
        Assert.AreEqual("cus_Prueba123", sent.Field("customer"));
        Assert.AreEqual("pm_Prueba123", sent.Field("payment_method"));
        Assert.AreEqual("true", sent.Field("off_session"));
        Assert.AreEqual("true", sent.Field("confirm"));
        Assert.AreEqual("att-55", sent.Field("metadata[attempt_id]"));
        Assert.AreEqual("true", sent.Field("metadata[off_session]"));
        Assert.AreEqual("Factura 1042", sent.Field("description"));
    }

    [TestMethod]
    public async Task SiYaHabiaUnCobroDelIntento_DevuelveEseYNoCobraOtraVez()
    {
        var existing = $$"""{ "object": "list", "has_more": false, "data": [{{Samples.Intent(reference: "att-55")}}] }""";
        var stripe = new FakeStripe().On(ListRoute, existing);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("pi_3Prueba", body!["result"]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual(0, stripe.Count(Route));
    }

    [TestMethod]
    public async Task SiElCobroAnteriorDelIntentoFueRechazado_DevuelveElRechazo()
    {
        var existing = $$"""{ "object": "list", "has_more": false, "data": [{{Samples.Intent(reference: "att-55", status: "requires_payment_method", received: 0, declineCode: "insufficient_funds")}}] }""";
        var stripe = new FakeStripe().On(ListRoute, existing);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, StripeHost.Code(body));
        Assert.AreEqual("later", body!["error"]!["details"]!["retry"]!.GetValue<string>());
        Assert.AreEqual(0, stripe.Count(Route));
    }

    [TestMethod]
    [DataRow("card_declined", "insufficient_funds", null, "later")]
    [DataRow("card_declined", "generic_decline", null, "later")]
    [DataRow("expired_card", null, null, "never")]
    [DataRow("card_declined", "stolen_card", null, "never")]
    [DataRow("card_declined", "fraudulent", null, "never")]
    [DataRow("authentication_required", "authentication_required", null, "customer_action")]
    [DataRow("card_declined", "insufficient_funds", "do_not_try_again", "never")]
    [DataRow("card_declined", "do_not_honor", "confirm_card_data", "customer_action")]
    [DataRow("card_declined", "stolen_card", "try_again_later", "later")]
    public async Task Rechazo_DiceSiSePuedeReintentar(string code, string? decline, string? advice, string retry)
    {
        var stripe = new FakeStripe()
            .On(ListRoute, NoIntents)
            .On(Route, Samples.Error("card_error", code, "Your card was declined.", declineCode: decline, adviceCode: advice, intent: "pi_3Rechazado"), HttpStatusCode.PaymentRequired);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, StripeHost.Code(body));
        var details = body!["error"]!["details"]!;
        Assert.AreEqual(retry, details["retry"]!.GetValue<string>());
        Assert.AreEqual(code, details["upstream"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual("pi_3Rechazado", details["payment_external_id"]!.GetValue<string>());
        Assert.IsFalse(body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task SinRespuestaCierta_EsIncierto_YElReintentoEncuentraElCobro()
    {
        var stripe = new FakeStripe()
            .On(ListRoute, NoIntents)
            .On(Route, _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "Se cortó la respuesta."));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, StripeHost.Code(body));

        // Stripe sí lo había cobrado: el reintento con la misma clave lo encuentra en el listado y no vuelve a cobrar.
        stripe.On(ListRoute, $$"""{ "object": "list", "has_more": false, "data": [{{Samples.Intent(reference: "att-55")}}] }""");
        (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("approved", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(1, stripe.Count(Route));
    }

    [TestMethod]
    public async Task Error500GuardadoBajoLaClave_SiNoCobro_PideConUnaClaveDerivadaDeLaHora()
    {
        // Stripe guarda el 500 bajo la clave: con la del núcleo devuelve siempre el mismo error.
        var stripe = new FakeStripe()
            .On(ListRoute, NoIntents)
            .On(Route, sent => sent.Headers["Idempotency-Key"] == "autocharge-55"
                ? Samples.Json(Samples.Error("api_error", message: "Error interno"), HttpStatusCode.InternalServerError)
                : Samples.Json(Samples.Intent(reference: "att-55", withFee: false)));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("approved", body!["result"]!["status"]!.GetValue<string>());
        var keys = stripe.Requests.Where(r => r.Route == Route).Select(r => r.Headers["Idempotency-Key"]).ToArray();
        CollectionAssert.AreEqual(new[] { "autocharge-55", "autocharge-55:2026102815" }, keys);

        // Antes de pedir con la clave derivada volvió a buscar el cobro del intento.
        Assert.AreEqual(2, stripe.Count(ListRoute));
    }

    [TestMethod]
    public async Task Error500_SiElCobroSeHizoIgual_LoDevuelveSinPedirOtraVez()
    {
        var lists = 0;
        var charged = $$"""{ "object": "list", "has_more": false, "data": [{{Samples.Intent(reference: "att-55")}}] }""";
        var stripe = new FakeStripe()
            .On(ListRoute, _ => Samples.Json(Interlocked.Increment(ref lists) == 1 ? NoIntents : charged))
            .On(Route, Samples.Error("api_error", message: "Error interno"), HttpStatusCode.InternalServerError);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("pi_3Prueba", body!["result"]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual(1, stripe.Count(Route));
    }

    [TestMethod]
    public async Task Error500TambienConLaClaveDerivada_QuedaIncierto()
    {
        var stripe = new FakeStripe()
            .On(ListRoute, NoIntents)
            .On(Route, Samples.Error("api_error", message: "Error interno"), HttpStatusCode.InternalServerError);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/charge_saved", Input(), "autocharge-55");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, StripeHost.Code(body));
        Assert.AreEqual(2, stripe.Count(Route));
    }

    [TestMethod]
    public async Task ConDatosInvalidos_NoSaleALaRed()
    {
        var stripe = new FakeStripe();
        await using var host = await StripeHost.Start(stripe);
        var input = new JsonObject
        {
            ["attempt_id"] = "",
            ["amount"] = new JsonObject { ["value"] = "1.005", ["currency"] = "ARS" },
            ["customer_external_id"] = "cliente",
            ["method_external_id"] = "pm_../x",
        };

        var (status, body) = await host.Send("/v0/payment/charge_saved", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEquivalent(
            new[] { "attempt_id", "amount", "customer_external_id", "method_external_id" },
            StripeHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(stripe.Requests);
    }

    [TestMethod]
    public async Task ConsultarUnMedio_ActivoOBorrado()
    {
        var method = """{ "id": "pm_Prueba123", "object": "payment_method", "type": "card", "customer": "cus_Prueba123", "card": { "brand": "mastercard", "last4": "4444", "exp_month": 1, "exp_year": 2031 } }""";
        var stripe = new FakeStripe().On("GET /v1/payment_methods/pm_Prueba123", method);
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_method", new JsonObject { ["method_external_id"] = "pm_Prueba123" });
        Assert.AreEqual("active", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("4444", body["result"]!["last4"]!.GetValue<string>());
        Assert.AreEqual(2031, body["result"]!["exp_year"]!.GetValue<long>());

        stripe.On("GET /v1/payment_methods/pm_Prueba123", method.Replace("\"cus_Prueba123\"", "null", StringComparison.Ordinal));
        (_, body) = await host.Send("/v0/payment/get_method", new JsonObject { ["method_external_id"] = "pm_Prueba123" });
        Assert.AreEqual("deleted", body!["result"]!["status"]!.GetValue<string>());

        stripe.On("GET /v1/payment_methods/pm_Prueba123", Samples.Error("invalid_request_error", "resource_missing", "No such PaymentMethod"), HttpStatusCode.NotFound);
        (_, body) = await host.Send("/v0/payment/get_method", new JsonObject { ["method_external_id"] = "pm_Prueba123" });
        Assert.AreEqual("deleted", body!["result"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task QuitarUnMedio_LoDesprende_YSiYaNoEstabaEsExito()
    {
        var attached = """{ "id": "pm_Prueba123", "object": "payment_method", "type": "card", "customer": "cus_Prueba123", "card": {} }""";
        var stripe = new FakeStripe()
            .On("GET /v1/payment_methods/pm_Prueba123", attached)
            .On("POST /v1/payment_methods/pm_Prueba123/detach", attached.Replace("\"cus_Prueba123\"", "null", StringComparison.Ordinal));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/delete_method", new JsonObject { ["method_external_id"] = "pm_Prueba123" }, "method-9-delete");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsTrue(body!["result"]!["deleted"]!.GetValue<bool>());
        Assert.AreEqual("method-9-delete", stripe.Single("POST /v1/payment_methods/pm_Prueba123/detach").Headers["Idempotency-Key"]);

        // Ya desprendido o borrado: no hay nada que hacer y la respuesta es la misma.
        stripe.On("GET /v1/payment_methods/pm_Prueba123", attached.Replace("\"cus_Prueba123\"", "null", StringComparison.Ordinal));
        (status, _) = await host.Send("/v0/payment/delete_method", new JsonObject { ["method_external_id"] = "pm_Prueba123" }, "method-9-delete-2");
        Assert.AreEqual(HttpStatusCode.OK, status);
        stripe.On("GET /v1/payment_methods/pm_Prueba123", Samples.Error("invalid_request_error", "resource_missing"), HttpStatusCode.NotFound);
        (status, _) = await host.Send("/v0/payment/delete_method", new JsonObject { ["method_external_id"] = "pm_Prueba123" }, "method-9-delete-3");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1, stripe.Count("POST /v1/payment_methods/pm_Prueba123/detach"));
    }

    private static JsonObject Input() => new()
    {
        ["attempt_id"] = "att-55",
        ["amount"] = new JsonObject { ["value"] = "12100.00", ["currency"] = "ARS" },
        ["description"] = "Factura 1042",
        ["customer_external_id"] = "cus_Prueba123",
        ["method_external_id"] = "pm_Prueba123",
    };
}
