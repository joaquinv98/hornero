using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.CryptoCloud.Tests;

/// <summary>payment.create_checkout: la factura de CryptoCloud y el link al que se redirige al cliente.</summary>
[TestClass]
public sealed class CheckoutTests
{
    private const string Route = "POST /v2/invoice/create";
    private const string Checkout = "/v0/payment/create_checkout";

    [TestMethod]
    public async Task CreaLaFactura_ConElIntentoComoOrderId_YDevuelveElLink()
    {
        var cc = new FakeCryptoCloud().On(Route, Samples.InvoiceCreated);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send(Checkout, Input(), "checkout-77");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(Samples.Link, body!["result"]!["redirect_url"]!.GetValue<string>());
        Assert.AreEqual("INV-89UX09KA", body["result"]!["checkout_external_id"]!.GetValue<string>());

        var sent = cc.Single(Route);
        Assert.AreEqual("https://api.trybit.com/v2/invoice/create", sent.Url.ToString());
        Assert.AreEqual($"Token {CcHost.FakeApiKey}", sent.Headers["Authorization"]);
        var invoice = sent.Body!;
        Assert.AreEqual(CcHost.FakeShopId, invoice["shop_id"]!.GetValue<string>());
        Assert.AreEqual("USD", invoice["currency"]!.GetValue<string>());
        Assert.AreEqual("0f3c2a9be1d84c7a9e5b6d4c3b2a1908", invoice["order_id"]!.GetValue<string>());

        // El monto viaja como número, que es lo que pide CryptoCloud, y sin perder los centavos.
        Assert.AreEqual(JsonValueKind.Number, invoice["amount"]!.GetValueKind());
        Assert.AreEqual(34.5m, invoice["amount"]!.GetValue<decimal>());

        // Sin vida configurada se mandan 24 horas, para no depender del proyecto. Y nada de datos del cliente ni de
        // URLs: son del proyecto.
        Assert.AreEqual(24, invoice["add_fields"]!["time_to_pay"]!["hours"]!.GetValue<int>());
        Assert.AreEqual(0, invoice["add_fields"]!["time_to_pay"]!["minutes"]!.GetValue<int>());
        Assert.IsNull(invoice["email"]);
        Assert.DoesNotContain("facturacion.example.com", invoice.ToJsonString());
    }

    [TestMethod]
    public async Task ConVidaDeFacturaEnLaInstancia_LaMandaEnTimeToPay()
    {
        var cc = new FakeCryptoCloud().On(Route, Samples.InvoiceCreated);
        await using var host = await CcHost.Start(cc);
        var config = CcHost.Config();
        config["invoice_hours"] = 6;

        var (status, _) = await host.Send(Checkout, Input(), "k", config);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var time = cc.Single(Route).Body!["add_fields"]!["time_to_pay"]!;
        Assert.AreEqual(6, time["hours"]!.GetValue<int>());
        Assert.AreEqual(0, time["minutes"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task MismaClave_UnaSolaFactura_YLaMismaRespuesta()
    {
        var cc = new FakeCryptoCloud().On(Route, Samples.InvoiceCreated);
        await using var host = await CcHost.Start(cc);

        var first = await host.Send(Checkout, Input(), "checkout-77");
        var second = await host.Send(Checkout, Input(), "checkout-77");

        Assert.AreEqual(1, cc.Count(Route));
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task MismaClaveConOtroMonto_EsIdempotencyConflict()
    {
        var cc = new FakeCryptoCloud().On(Route, Samples.InvoiceCreated);
        await using var host = await CcHost.Start(cc);
        await host.Send(Checkout, Input(), "checkout-77");
        var other = Input();
        other["amount"]!["value"] = "40.00";

        var (status, body) = await host.Send(Checkout, other, "checkout-77");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, CcHost.Code(body));
        Assert.AreEqual(1, cc.Count(Route));
    }

    [TestMethod]
    public async Task SinClaveDeIdempotencia_NoCreaNada()
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send(Checkout, Input());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, CcHost.Code(body));
        Assert.IsEmpty(cc.Requests);
    }

    [TestMethod]
    [DataRow("ARS")]
    [DataRow("EUR")]
    public async Task EnOtraMonedaQueUsd_EsInvalidInput_YNoCreaNada(string currency)
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);
        var input = Input();
        input["amount"]!["currency"] = currency;

        var (status, body) = await host.Send(Checkout, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, CcHost.Code(body));
        CollectionAssert.AreEqual(new[] { "amount.currency" }, CcHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.Contains(currency, body["error"]!["message"]!.GetValue<string>());
        Assert.IsEmpty(cc.Requests);
    }

    [TestMethod]
    public async Task ConDatosInvalidos_ListaLosCamposYNoLlamaACryptoCloud()
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);
        var input = new JsonObject
        {
            ["attempt_id"] = " ",
            ["amount"] = new JsonObject { ["value"] = "10.123", ["currency"] = "usd" },
        };

        var (status, body) = await host.Send(Checkout, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEquivalent(
            new[] { "attempt_id", "amount.currency", "amount.value" },
            CcHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(cc.Requests);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-5.00")]
    [DataRow("79228162514264337593543950335")]
    public async Task ConUnMontoQueNoSirve_EsInvalidInput(string value)
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);
        var input = Input();
        input["amount"]!["value"] = value;

        var (status, body) = await host.Send(Checkout, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEqual(new[] { "amount.value" }, CcHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(cc.Requests);
    }

    [TestMethod]
    [DataRow("http://pay.trybit.com/89UX09KA")]
    [DataRow("javascript:alert(1)")]
    [DataRow("None")]
    public async Task SiCryptoCloudNoDevuelveUnLinkHttps_NoSeLoDaAlNucleo(string link)
    {
        var created = Samples.InvoiceCreated.Replace(Samples.Link, link, StringComparison.Ordinal);
        var cc = new FakeCryptoCloud().On(Route, created);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send(Checkout, Input(), "k");

        // Con un 2xx la factura pudo haberse creado: el resultado es incierto, no upstream_unavailable.
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, CcHost.Code(body));
        Assert.AreEqual("unexpected_response", body!["error"]!["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task RechazoDeNegocio_EsRejectedConLosErroresDeCryptoCloud()
    {
        var cc = new FakeCryptoCloud().On(Route, Samples.Error("amount", "Minimum amount is 1 USD"), HttpStatusCode.BadRequest);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send(Checkout, Input(), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, CcHost.Code(body));
        var details = body!["error"]!["details"]!;
        Assert.AreEqual(400, details["http_status"]!.GetValue<int>());
        Assert.AreEqual("amount", details["upstream"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual("Minimum amount is 1 USD", details["upstream"]![0]!["message"]!.GetValue<string>());
        Assert.IsFalse(body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task UnShopIdQueNoEsDelProyecto_EsInvalidConfig()
    {
        var cc = new FakeCryptoCloud().On(Route, Samples.Error("shop_id", "Shop not found"), HttpStatusCode.BadRequest);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send(Checkout, Input(), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
        Assert.AreEqual("shop_id", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    /// <summary>Lo que manda el núcleo: los ítems y las URLs no se usan, porque CryptoCloud cobra un monto y las URLs son del proyecto.</summary>
    private static JsonObject Input() => (JsonObject)JsonNode.Parse("""
        {
          "attempt_id": "0f3c2a9be1d84c7a9e5b6d4c3b2a1908",
          "amount": { "value": "34.50", "currency": "USD" },
          "items": [ { "id": "doc-77", "title": "Factura 0003-00001488", "quantity": 1, "unit_price": "34.50" } ],
          "return_urls": {
            "success": "https://facturacion.example.com/pay/return/0f3c2a9be1d84c7a9e5b6d4c3b2a1908",
            "pending": "https://facturacion.example.com/pay/return/0f3c2a9be1d84c7a9e5b6d4c3b2a1908",
            "failure": "https://facturacion.example.com/pay/return/0f3c2a9be1d84c7a9e5b6d4c3b2a1908"
          },
          "notification_url": "https://facturacion.example.com/webhooks/3"
        }
        """)!;
}
