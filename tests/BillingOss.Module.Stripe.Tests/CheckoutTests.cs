using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>payment.create_checkout: la sesión de Checkout y la URL a la que se redirige al cliente.</summary>
[TestClass]
public sealed class CheckoutTests
{
    private const string Route = "POST /v1/checkout/sessions";

    [TestMethod]
    public async Task ArmaLaSesion_ConElIntentoComoReferenciaYLosImportesEnLaUnidadMenor()
    {
        var stripe = new FakeStripe().On(Route, Samples.Session());
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/create_checkout", Input(), "checkout-7", context: Context("es"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(Samples.SessionUrl, body!["result"]!["redirect_url"]!.GetValue<string>());
        Assert.IsNull(body["result"]!["checkout_external_id"], "get_payment no acepta la sesión: el barrido de checkouts no tiene qué preguntar.");
        Assert.IsNull(body["result"]!["external_ids"]);

        var sent = stripe.Single(Route);
        Assert.AreEqual("https://api.stripe.com/v1/checkout/sessions", sent.Url.ToString());
        Assert.AreEqual($"Bearer {StripeHost.FakeApiKey}", sent.Headers["Authorization"]);
        Assert.AreEqual(StripeApi.Version, sent.Headers["Stripe-Version"]);
        Assert.AreEqual("checkout-7", sent.Headers["Idempotency-Key"]);
        Assert.AreEqual("payment", sent.Field("mode"));
        Assert.AreEqual("att-1042", sent.Field("client_reference_id"));
        Assert.AreEqual("att-1042", sent.Field("metadata[attempt_id]"));
        Assert.AreEqual("att-1042", sent.Field("payment_intent_data[metadata][attempt_id]"));
        Assert.AreEqual("https://facturacion.example.com/pay/return/att-1042", sent.Field("success_url"));
        Assert.AreEqual("https://facturacion.example.com/pay/return/att-1042?cancelado", sent.Field("cancel_url"));
        Assert.AreEqual("es-419", sent.Field("locale"));
        Assert.AreEqual("false", sent.Field("adaptive_pricing[enabled]"));
        Assert.AreEqual("ars", sent.Field("line_items[0][price_data][currency]"));
        Assert.AreEqual("1000000", sent.Field("line_items[0][price_data][unit_amount]"));
        Assert.AreEqual("VPS Básico, octubre 2026", sent.Field("line_items[0][price_data][product_data][name]"));
        Assert.AreEqual("1", sent.Field("line_items[0][quantity]"));
        Assert.AreEqual("210000", sent.Field("line_items[1][price_data][unit_amount]"));

        // Sin medio guardado no hay cliente ni uso futuro, y los avisos de Stripe son de la cuenta: notification_url no viaja.
        Assert.IsNull(sent.Field("customer"));
        Assert.IsNull(sent.Field("payment_intent_data[setup_future_usage]"));
        Assert.AreEqual(0, stripe.Count("POST /v1/customers"));
        Assert.IsFalse(sent.Form.Values.Any(v => v.ToString().Contains("webhooks", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConMedioGuardadoYSinCliente_CreaElClienteSinDatosPersonales()
    {
        var stripe = new FakeStripe().On(Route, Samples.Session()).On("POST /v1/customers", Samples.Customer);
        await using var host = await StripeHost.Start(stripe);
        var input = Input();
        input["save_method"] = true;

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "checkout-8", context: Context("en"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var customer = stripe.Single("POST /v1/customers");
        Assert.AreEqual("77", customer.Field("metadata[account_id]"));
        Assert.AreEqual("checkout-8:customer", customer.Headers["Idempotency-Key"]);
        Assert.HasCount(1, customer.Form, "Solo el ID de la cuenta: ni nombre ni correo.");

        var session = stripe.Single(Route);
        Assert.AreEqual("cus_Prueba123", session.Field("customer"));
        Assert.AreEqual("off_session", session.Field("payment_intent_data[setup_future_usage]"));
        Assert.AreEqual("card", session.Field("payment_method_types[0]"));
        Assert.AreEqual("en", session.Field("locale"));
        Assert.AreEqual("cus_Prueba123", body!["result"]!["external_ids"]!["customer"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ConMedioGuardadoYClienteDelNucleo_NoCreaOtro()
    {
        var stripe = new FakeStripe().On(Route, Samples.Session());
        await using var host = await StripeHost.Start(stripe);
        var input = Input();
        input["save_method"] = true;
        input["customer_external_id"] = "cus_YaExiste";

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(0, stripe.Count("POST /v1/customers"));
        Assert.AreEqual("cus_YaExiste", stripe.Single(Route).Field("customer"));
        Assert.AreEqual("cus_YaExiste", body!["result"]!["external_ids"]!["customer"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MismaClave_UnaSolaSesion_YLaMismaRespuesta()
    {
        var stripe = new FakeStripe().On(Route, Samples.Session());
        await using var host = await StripeHost.Start(stripe);

        var first = await host.Send("/v0/payment/create_checkout", Input(), "checkout-7");
        var second = await host.Send("/v0/payment/create_checkout", Input(), "checkout-7");

        Assert.AreEqual(1, stripe.Count(Route));
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task SiLosItemsNoSumanElMonto_NoCobraOtraCosa()
    {
        var stripe = new FakeStripe();
        await using var host = await StripeHost.Start(stripe);
        var input = Input();
        input["amount"]!["value"] = "12000.00";

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, StripeHost.Code(body));
        Assert.IsEmpty(stripe.Requests);
    }

    [TestMethod]
    public async Task MonedaSinDecimales_VaTalCual_YConDecimalesEsInvalida()
    {
        var stripe = new FakeStripe().On(Route, Samples.Session());
        await using var host = await StripeHost.Start(stripe);
        var input = Single("JPY", "1500");

        var (status, _) = await host.Send("/v0/payment/create_checkout", input, "k1");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("1500", stripe.Single(Route).Field("line_items[0][price_data][unit_amount]"));
        Assert.AreEqual("jpy", stripe.Single(Route).Field("line_items[0][price_data][currency]"));

        var (bad, body) = await host.Send("/v0/payment/create_checkout", Single("JPY", "10.50"), "k2");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad);
        CollectionAssert.Contains(StripeHost.Strings(body!["error"]!["details"]!["fields"]), "amount.value");
        Assert.AreEqual(1, stripe.Count(Route));
    }

    [TestMethod]
    public async Task ConMasDeCienLineas_VaUnaSolaPorElTotal()
    {
        var stripe = new FakeStripe().On(Route, Samples.Session());
        await using var host = await StripeHost.Start(stripe);
        var items = new JsonArray();
        for (var i = 0; i < 101; i++)
        {
            items.Add(new JsonObject { ["title"] = $"Línea {i}", ["quantity"] = 1, ["unit_price"] = "1.00" });
        }

        var input = Single("USD", "101.00");
        input["items"] = items;

        var (status, _) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var sent = stripe.Single(Route);
        Assert.AreEqual("10100", sent.Field("line_items[0][price_data][unit_amount]"));
        Assert.AreEqual("Total", sent.Field("line_items[0][price_data][product_data][name]"));
        Assert.IsNull(sent.Field("line_items[1][quantity]"));
    }

    [TestMethod]
    public async Task ConDatosInvalidos_ListaLosCamposYNoLlamaAStripe()
    {
        var stripe = new FakeStripe();
        await using var host = await StripeHost.Start(stripe);
        var input = (JsonObject)JsonNode.Parse("""
            {
              "attempt_id": " ",
              "amount": { "value": "10.123", "currency": "usd" },
              "items": [ { "quantity": 0, "unit_price": "-5" } ],
              "return_urls": { "success": "javascript:alert(1)", "failure": "ftp://x" },
              "customer_external_id": "no-es-un-cliente"
            }
            """)!;

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEquivalent(
            new[] { "attempt_id", "amount.currency", "amount.value", "items[0].title", "items[0].quantity", "items[0].unit_price", "return_urls.success", "return_urls.failure" },
            StripeHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(stripe.Requests);
    }

    [TestMethod]
    public async Task SiStripeDevuelveUnaUrlQueNoEsHttps_NoSeLaDaAlNucleo()
    {
        var stripe = new FakeStripe().On(Route, Samples.Session("http://checkout.stripe.com/c/pay/cs_test"));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/create_checkout", Input(), "k");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, StripeHost.Code(body));
    }

    internal static JsonObject Input() => (JsonObject)JsonNode.Parse("""
        {
          "attempt_id": "att-1042",
          "amount": { "value": "12100.00", "currency": "ARS" },
          "items": [
            { "id": "doc-77", "title": "VPS Básico, octubre 2026", "quantity": 1, "unit_price": "10000.00" },
            { "title": "IVA 21 %", "quantity": 1, "unit_price": "2100.00" }
          ],
          "return_urls": {
            "success": "https://facturacion.example.com/pay/return/att-1042",
            "failure": "https://facturacion.example.com/pay/return/att-1042?cancelado"
          },
          "notification_url": "https://facturacion.example.com/webhooks/3"
        }
        """)!;

    private static JsonObject Single(string currency, string value) => new()
    {
        ["attempt_id"] = "att-9",
        ["amount"] = new JsonObject { ["value"] = value, ["currency"] = currency },
        ["items"] = new JsonArray(new JsonObject { ["title"] = "Servicio", ["quantity"] = 1, ["unit_price"] = value }),
        ["return_urls"] = new JsonObject { ["success"] = "https://facturacion.example.com/pay/return/att-9" },
    };

    private static JsonObject Context(string language) => new() { ["account"] = new JsonObject { ["id"] = "77", ["language"] = language } };
}
