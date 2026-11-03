using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>payment.refund: primero la moneda del pago, después el reembolso con la clave del núcleo.</summary>
[TestClass]
public sealed class RefundTests
{
    private const string IntentRoute = "GET /v1/payment_intents/pi_3Prueba";
    private const string Route = "POST /v1/refunds";

    [TestMethod]
    public async Task Parcial_EnLaUnidadMenorYConLaClaveDelNucleo()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent()).On(Route, Samples.Refund());
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/refund", Input("5000.00"), "refund-12");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var sent = stripe.Single(Route);
        Assert.AreEqual("pi_3Prueba", sent.Field("payment_intent"));
        Assert.AreEqual("500000", sent.Field("amount"));
        Assert.AreEqual("refund-12", sent.Headers["Idempotency-Key"]);
        var result = body!["result"]!;
        Assert.AreEqual("re_3Prueba", result["refund_id"]!.GetValue<string>());
        Assert.AreEqual("approved", result["status"]!.GetValue<string>());
        Assert.AreEqual("5000.00", result["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("ARS", result["amount"]!["currency"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinMonto_EsTotal()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent()).On(Route, Samples.Refund(amount: 1210000));
        await using var host = await StripeHost.Start(stripe);

        var (status, _) = await host.Send("/v0/payment/refund", new JsonObject { ["payment_external_id"] = "pi_3Prueba" }, "refund-13");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsNull(stripe.Single(Route).Field("amount"));
    }

    [TestMethod]
    public async Task EnOtraMoneda_NoReembolsa()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent());
        await using var host = await StripeHost.Start(stripe);
        var input = Input("10.00");
        input["amount"]!["currency"] = "USD";

        var (status, body) = await host.Send("/v0/payment/refund", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, StripeHost.Code(body));
        Assert.AreEqual(0, stripe.Count(Route));
    }

    [TestMethod]
    [DataRow("pending", "pending")]
    [DataRow("requires_action", "pending")]
    public async Task EnCurso_EsPendiente(string gatewayStatus, string expected)
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent()).On(Route, Samples.Refund(gatewayStatus));
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/refund", Input("5000.00"), "k");

        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("failed")]
    [DataRow("canceled")]
    public async Task FallidoOCancelado_EsRechazoConElMotivo(string gatewayStatus)
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent()).On(Route, Samples.Refund(gatewayStatus, failure: "expired_or_canceled_card"));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/refund", Input("5000.00"), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, StripeHost.Code(body));
        Assert.AreEqual("expired_or_canceled_card", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task YaReembolsado_EsRechazo()
    {
        var stripe = new FakeStripe()
            .On(IntentRoute, Samples.Intent())
            .On(Route, Samples.Error("invalid_request_error", "charge_already_refunded", "Charge ch_3Prueba has already been refunded."), HttpStatusCode.BadRequest);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/refund", Input("5000.00"), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, StripeHost.Code(body));
        Assert.AreEqual("charge_already_refunded", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MismaClave_UnSoloReembolso()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent()).On(Route, Samples.Refund());
        await using var host = await StripeHost.Start(stripe);

        await host.Send("/v0/payment/refund", Input("5000.00"), "refund-12");
        await host.Send("/v0/payment/refund", Input("5000.00"), "refund-12");

        Assert.AreEqual(1, stripe.Count(Route));
    }

    private static JsonObject Input(string value) => new()
    {
        ["payment_external_id"] = "pi_3Prueba",
        ["amount"] = new JsonObject { ["value"] = value, ["currency"] = "ARS" },
    };
}
