using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>payment.create_checkout: la preferencia de Checkout Pro y la URL a la que se redirige al cliente.</summary>
[TestClass]
public sealed class CheckoutTests
{
    private const string Route = "POST /checkout/preferences";

    [TestMethod]
    public async Task ArmaLaPreferencia_ConElIntentoComoReferenciaYLasUrlsDelNucleo()
    {
        var mp = new FakeMercadoPago().On(Route, Samples.PreferenceCreated, HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/create_checkout", Input(), "att-1042-checkout-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(Samples.InitPoint, body!["result"]!["redirect_url"]!.GetValue<string>());

        var sent = mp.Single(Route);
        Assert.AreEqual("https://api.mercadopago.com/checkout/preferences", sent.Url.ToString());
        Assert.AreEqual($"Bearer {MpHost.FakeAccessToken}", sent.Headers["Authorization"]);
        var preference = sent.Body!;
        Assert.AreEqual("att-1042", preference["external_reference"]!.GetValue<string>());
        Assert.AreEqual("https://facturacion.example.com/webhooks/inst-3", preference["notification_url"]!.GetValue<string>());
        Assert.AreEqual("https://facturacion.example.com/pay/return/att-1042", preference["back_urls"]!["success"]!.GetValue<string>());
        Assert.AreEqual("https://facturacion.example.com/pay/return/att-1042", preference["back_urls"]!["failure"]!.GetValue<string>());
        Assert.AreEqual("approved", preference["auto_return"]!.GetValue<string>());
        Assert.IsNull(preference["payment_methods"], "No excluye ningún medio de pago: el cliente elige entre todos los de la cuenta.");

        var items = preference["items"]!.AsArray();
        Assert.HasCount(2, items);
        Assert.AreEqual("svc-77", items[0]!["id"]!.GetValue<string>());
        Assert.AreEqual("VPS Básico, octubre 2026", items[0]!["title"]!.GetValue<string>());
        Assert.AreEqual(1, items[0]!["quantity"]!.GetValue<int>());
        Assert.AreEqual("ARS", items[0]!["currency_id"]!.GetValue<string>());

        // El precio viaja como número, que es lo que pide Mercado Pago, y sin perder los decimales.
        Assert.AreEqual(JsonValueKind.Number, items[0]!["unit_price"]!.GetValueKind());
        Assert.AreEqual(10000m, items[0]!["unit_price"]!.GetValue<decimal>());
        Assert.AreEqual(2100m, items[1]!["unit_price"]!.GetValue<decimal>());
        Assert.IsNull(items[1]!["id"]);
    }

    [TestMethod]
    public async Task SinUrlsDeRetornoNiDeNotificacion_NoLasInventa()
    {
        var mp = new FakeMercadoPago().On(Route, Samples.PreferenceCreated, HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);
        var input = Input();
        input.Remove("return_urls");
        input.Remove("notification_url");

        var (status, _) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var preference = mp.Single(Route).Body!;
        Assert.IsNull(preference["back_urls"]);
        Assert.IsNull(preference["auto_return"]);
        Assert.IsNull(preference["notification_url"]);
    }

    [TestMethod]
    public async Task MismaClave_UnaSolaPreferencia_YLaMismaRespuesta()
    {
        var mp = new FakeMercadoPago().On(Route, Samples.PreferenceCreated, HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var first = await host.Send("/v0/payment/create_checkout", Input(), "att-1042-checkout-1");
        var second = await host.Send("/v0/payment/create_checkout", Input(), "att-1042-checkout-1");

        Assert.AreEqual(1, mp.Count(Route));
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task SinClaveDeIdempotencia_NoCreaNada()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/create_checkout", Input());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task SiLosItemsNoSumanElMontoDelIntento_NoCobraOtraCosa()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);
        var input = Input();
        input["amount"]!["value"] = "12000.00";

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.Contains("12100.00", body!["error"]!["message"]!.GetValue<string>());
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task ConDatosInvalidos_ListaLosCamposYNoLlamaAMercadoPago()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);
        var input = (JsonObject)JsonNode.Parse("""
            {
              "attempt_id": " ",
              "amount": { "value": "10.123", "currency": "ars" },
              "items": [
                { "quantity": 0, "unit_price": "-5" },
                { "title": "Bien", "quantity": 1, "unit_price": "10.12" }
              ],
              "return_urls": { "success": "javascript:alert(1)" },
              "notification_url": "no es una url"
            }
            """)!;

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        var fields = MpHost.Strings(body!["error"]!["details"]!["fields"]);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "attempt_id", "amount.currency", "amount.value", "items[0].title", "items[0].quantity", "items[0].unit_price",
                "return_urls.success", "notification_url",
            },
            fields);
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task ConImportesEnormes_NoDesbordaYEsInvalida()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);
        var input = Input();
        input["amount"]!["value"] = "79228162514264337593543950335";
        input["items"] = new JsonArray(new JsonObject { ["title"] = "Enorme", ["quantity"] = int.MaxValue, ["unit_price"] = "79228162514264337593543950335" });

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task SinItems_EsInvalida()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var input = Input();
        input["items"] = new JsonArray();

        var (status, body) = await host.Send("/v0/payment/create_checkout", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEqual(new[] { "items" }, MpHost.Strings(body!["error"]!["details"]!["fields"]));
    }

    [TestMethod]
    public async Task SiMercadoPagoRespondeUnaUrlQueNoEsHttps_NoSeLaDaAlNucleo()
    {
        var mp = new FakeMercadoPago().On(Route, """{ "id": "1", "init_point": "http://www.mercadopago.com.ar/checkout/v1/redirect?pref_id=1" }""", HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/create_checkout", Input(), "k");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(body));
    }

    [TestMethod]
    public async Task RechazoDeNegocio_EsRejectedConLosCodigosDeMercadoPago()
    {
        var mp = new FakeMercadoPago().On(
            Route,
            Samples.Error(400, "bad_request", "invalid parameters", ("2062", "unit_price is invalid for the currency")),
            HttpStatusCode.BadRequest);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/create_checkout", Input(), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, MpHost.Code(body));
        var details = body!["error"]!["details"]!;
        Assert.AreEqual(400, details["http_status"]!.GetValue<int>());
        Assert.AreEqual("2062", details["upstream"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual("unit_price is invalid for the currency", details["upstream"]![0]!["message"]!.GetValue<string>());
        Assert.IsFalse(body["error"]!["retryable"]!.GetValue<bool>());
    }

    private static JsonObject Input() => (JsonObject)JsonNode.Parse("""
        {
          "attempt_id": "att-1042",
          "amount": { "value": "12100.00", "currency": "ARS" },
          "items": [
            { "id": "svc-77", "title": "VPS Básico, octubre 2026", "description": "Período 2026-10-01 al 2026-10-31", "quantity": 1, "unit_price": "10000.00" },
            { "title": "IVA 21 %", "quantity": 1, "unit_price": "2100.00" }
          ],
          "return_urls": {
            "success": "https://facturacion.example.com/pay/return/att-1042",
            "pending": "https://facturacion.example.com/pay/return/att-1042",
            "failure": "https://facturacion.example.com/pay/return/att-1042"
          },
          "notification_url": "https://facturacion.example.com/webhooks/inst-3"
        }
        """)!;

}
