using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>payment.create_checkout: la orden de PayPal con intent CAPTURE y la URL para aprobarla.</summary>
[TestClass]
public sealed class CheckoutTests
{
    private const string Route = "POST /v2/checkout/orders";
    private const string Checkout = "/v0/payment/create_checkout";

    [TestMethod]
    public async Task CreaLaOrden_YDevuelveLaUrlParaAprobarla()
    {
        var pp = new FakePayPal().On(Route, Samples.OrderCreated, HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Checkout, Input(), "checkout-17");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(Samples.ApproveUrl, body!["result"]!["redirect_url"]!.GetValue<string>());
        Assert.AreEqual(Samples.OrderId, body["result"]!["checkout_external_id"]!.GetValue<string>());

        var sent = pp.Single(Route);
        Assert.AreEqual("https://api-m.sandbox.paypal.com/v2/checkout/orders", sent.Url.ToString());
        Assert.AreEqual($"Bearer {pp.AccessToken}", sent.Headers["Authorization"]);

        // El PayPal-Request-Id sale del intento: una repetición devuelve la misma orden.
        Assert.AreEqual("checkout-att-1042", sent.Headers["PayPal-Request-Id"]);
        Assert.AreEqual("return=representation", sent.Headers["Prefer"]);

        var order = sent.Body!;
        Assert.AreEqual("CAPTURE", order["intent"]!.GetValue<string>());
        var unit = order["purchase_units"]![0]!;
        Assert.AreEqual("billing-oss", unit["reference_id"]!.GetValue<string>());
        Assert.AreEqual("att-1042", unit["custom_id"]!.GetValue<string>());
        Assert.AreEqual("att-1042", unit["invoice_id"]!.GetValue<string>());
        Assert.AreEqual("Factura A-0001-00000042, Factura A-0001-00000043", unit["description"]!.GetValue<string>());
        Assert.AreEqual("USD", unit["amount"]!["currency_code"]!.GetValue<string>());
        Assert.AreEqual("30.50", unit["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("30.50", unit["amount"]!["breakdown"]!["item_total"]!["value"]!.GetValue<string>());

        var items = unit["items"]!.AsArray();
        Assert.HasCount(2, items);
        Assert.AreEqual("Factura A-0001-00000042", items[0]!["name"]!.GetValue<string>());
        Assert.AreEqual("doc-42", items[0]!["sku"]!.GetValue<string>());
        Assert.AreEqual("1", items[0]!["quantity"]!.GetValue<string>());
        Assert.AreEqual("20.00", items[0]!["unit_amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("DIGITAL_GOODS", items[0]!["category"]!.GetValue<string>());

        var context = order["payment_source"]!["paypal"]!["experience_context"]!;
        Assert.AreEqual("https://facturacion.example.com/pay/return/att-1042", context["return_url"]!.GetValue<string>());
        Assert.AreEqual("https://facturacion.example.com/pay/return/att-1042?cancel=1", context["cancel_url"]!.GetValue<string>());
        Assert.AreEqual("PAY_NOW", context["user_action"]!.GetValue<string>());
        Assert.AreEqual("NO_SHIPPING", context["shipping_preference"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinUrlDeFalla_CancelarVuelveALaDeExito()
    {
        var pp = new FakePayPal().On(Route, Samples.OrderCreated, HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);
        var input = Input();
        input["return_urls"]!.AsObject().Remove("failure");

        await host.Send(Checkout, input, "k");

        var context = pp.Single(Route).Body!["payment_source"]!["paypal"]!["experience_context"]!;
        Assert.AreEqual(context["return_url"]!.GetValue<string>(), context["cancel_url"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EnYenes_LosImportesVanSinDecimales()
    {
        var pp = new FakePayPal().On(Route, Samples.OrderCreated, HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);
        var input = Input("JPY", ("1500", 1), ("500", 2));
        input["amount"]!["value"] = "2500";

        var (status, _) = await host.Send(Checkout, input, "k");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var unit = pp.Single(Route).Body!["purchase_units"]![0]!;
        Assert.AreEqual("2500", unit["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("500", unit["items"]![1]!["unit_amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("2", unit["items"]![1]!["quantity"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("ARS", "30.50", "amount.currency")]
    [DataRow("usd", "30.50", "amount.currency")]
    [DataRow("USD", "0", "amount.value")]
    [DataRow("USD", "30.505", "amount.value")]
    [DataRow("JPY", "30.50", "amount.value")]
    public async Task MonedaOMontoQuePayPalNoAcepta_EsInvalidInputSinLlamar(string currency, string value, string field)
    {
        // PayPal no cobra en pesos: una factura en ARS no se puede pagar por acá.
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);
        var input = Input();
        input["amount"] = new JsonObject { ["value"] = value, ["currency"] = currency };

        var (status, body) = await host.Send(Checkout, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        CollectionAssert.Contains(PpHost.Strings(body!["error"]!["details"]!["fields"]), field);
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    public async Task SumaDeItemsDistintaDelMonto_EsInvalidInput()
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);
        var input = Input();
        input["amount"]!["value"] = "31.00";

        var (status, body) = await host.Send(Checkout, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        Assert.Contains("30.50", body!["error"]!["message"]!.GetValue<string>());
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    [DataRow("attempt_id", "con espacio")]
    [DataRow("attempt_id", "")]
    [DataRow("return_urls", null)]
    public async Task EntradaInvalida_NoLlamaAPayPal(string field, string? value)
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);
        var input = Input();
        input[field] = value;

        var (status, body) = await host.Send(Checkout, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    public async Task MismaClave_CreaUnaSolaOrden()
    {
        var pp = new FakePayPal().On(Route, Samples.OrderCreated, HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var first = await host.Send(Checkout, Input(), "checkout-17");
        var second = await host.Send(Checkout, Input(), "checkout-17");

        Assert.AreEqual(1, pp.Count(Route));
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task SinUrlHttpsParaAprobar_EsIncierto()
    {
        // La orden pudo haberse creado, pero no hay a dónde mandar al cliente.
        var pp = new FakePayPal().On(Route, Samples.OrderCreated.Replace("https://www.sandbox", "http://www.sandbox", StringComparison.Ordinal), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Checkout, Input(), "k");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PpHost.Code(body));
    }

    [TestMethod]
    public async Task IntentoYaPagado_EsRejectedConElCodigoDePayPal()
    {
        // invoice_id es el intento y PayPal no deja cobrar dos veces el mismo en la cuenta.
        var pp = new FakePayPal().On(
            Route,
            Samples.Error("UNPROCESSABLE_ENTITY", "The requested action could not be performed.", ("DUPLICATE_INVOICE_ID", "Duplicate Invoice ID detected.")),
            HttpStatusCode.UnprocessableEntity);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Checkout, Input(), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(body));
        Assert.AreEqual("DUPLICATE_INVOICE_ID", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual("90bff07028f7f", body["error"]!["details"]!["debug_id"]!.GetValue<string>());
    }

    /// <summary>Lo que manda el núcleo: dos facturas en dólares.</summary>
    private static JsonObject Input() => Input("USD", ("20.00", 1), ("10.50", 1));

    private static JsonObject Input(string currency, params (string Price, int Quantity)[] items)
    {
        var total = items.Sum(i => decimal.Parse(i.Price, System.Globalization.CultureInfo.InvariantCulture) * i.Quantity);
        return new JsonObject
        {
            ["attempt_id"] = "att-1042",
            ["amount"] = new JsonObject { ["value"] = total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), ["currency"] = currency },
            ["items"] = new JsonArray(items.Select((item, i) => (JsonNode?)new JsonObject
            {
                ["id"] = $"doc-{42 + i}",
                ["title"] = $"Factura A-0001-000000{42 + i}",
                ["quantity"] = item.Quantity,
                ["unit_price"] = item.Price,
            }).ToArray()),
            ["return_urls"] = new JsonObject
            {
                ["success"] = "https://facturacion.example.com/pay/return/att-1042",
                ["pending"] = "https://facturacion.example.com/pay/return/att-1042",
                ["failure"] = "https://facturacion.example.com/pay/return/att-1042?cancel=1",
            },
            ["notification_url"] = "https://facturacion.example.com/webhooks/7",
        };
    }
}
