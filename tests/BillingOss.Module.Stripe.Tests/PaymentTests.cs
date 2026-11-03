using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>payment.get_payment y payment.list_payments: el PaymentIntent con sus disputas, en la forma del protocolo.</summary>
[TestClass]
public sealed class PaymentTests
{
    private const string IntentRoute = "GET /v1/payment_intents/pi_3Prueba";
    private const string DisputesRoute = "GET /v1/disputes";

    [TestMethod]
    public async Task Aprobado_ConComisionYReferencia_SinDatosDelPagador()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent()).On(DisputesRoute, Samples.Disputes());
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/get_payment", Ref());

        Assert.AreEqual(HttpStatusCode.OK, status);
        var p = body!["result"]!;
        Assert.AreEqual("pi_3Prueba", p["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", p["status"]!.GetValue<string>());
        Assert.AreEqual("12100.00", p["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("ARS", p["amount"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("0.00", p["refunded"]!["value"]!.GetValue<string>());
        Assert.AreEqual("435.60", p["fee"]!["value"]!.GetValue<string>());
        Assert.AreEqual("att-1042", p["external_reference"]!.GetValue<string>());
        Assert.AreEqual("2026-10-28T15:01:10Z", p["approved_at"]!.GetValue<string>());
        Assert.AreEqual("succeeded", p["gateway_status"]!.GetValue<string>());
        Assert.IsFalse(p["livemode"]!.GetValue<bool>(), "El núcleo sabe que es un pago del ambiente de prueba.");
        Assert.IsNull(p["saved_method"], "Sin setup_future_usage el medio no quedó guardado.");
        Assert.DoesNotContain("pagador@example.com", body.ToJsonString());

        var sent = stripe.Single(IntentRoute);
        CollectionAssert.AreEquivalent(new[] { "latest_charge.balance_transaction", "payment_method" }, sent.Query("expand[]").ToArray());
        Assert.AreEqual("pi_3Prueba", stripe.Single(DisputesRoute).Query("payment_intent").ToString());
    }

    [TestMethod]
    [DataRow("succeeded", 1210000L, 0L, "approved")]
    [DataRow("succeeded", 1210000L, 500000L, "partially_refunded")]
    [DataRow("succeeded", 1210000L, 1210000L, "refunded")]
    [DataRow("processing", 0L, 0L, "pending")]
    [DataRow("requires_payment_method", 0L, 0L, "pending")]
    [DataRow("requires_action", 0L, 0L, "pending")]
    [DataRow("canceled", 0L, 0L, "rejected")]
    public async Task EstadosDeStripe_AlNucleo(string gatewayStatus, long received, long refunded, string expected)
    {
        var stripe = new FakeStripe()
            .On(IntentRoute, Samples.Intent(status: gatewayStatus, received: received, refunded: refunded))
            .On(DisputesRoute, Samples.Disputes());
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_payment", Ref());

        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CobroSinElClienteRechazado_EsRechazado_YElDelCheckoutSiguePendiente()
    {
        var stripe = new FakeStripe()
            .On(IntentRoute, Samples.Intent(status: "requires_payment_method", received: 0, declineCode: "insufficient_funds", offSession: true))
            .On(DisputesRoute, Samples.Disputes());
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_payment", Ref());
        Assert.AreEqual("rejected", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("insufficient_funds", body["result"]!["gateway_status_detail"]!.GetValue<string>());

        // En el checkout el cliente puede reintentar con otra tarjeta mientras la sesión siga abierta.
        stripe.On(IntentRoute, Samples.Intent(status: "requires_payment_method", received: 0, declineCode: "insufficient_funds"));
        (_, body) = await host.Send("/v0/payment/get_payment", Ref());
        Assert.AreEqual("pending", body!["result"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("needs_response", "disputed")]
    [DataRow("under_review", "disputed")]
    [DataRow("warning_needs_response", "disputed")]
    [DataRow("lost", "charged_back")]
    [DataRow("won", "approved")]
    [DataRow("warning_closed", "approved")]
    [DataRow("prevented", "approved")]
    public async Task Disputas_DisputaContracargoONada(string dispute, string expected)
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent()).On(DisputesRoute, Samples.Disputes(dispute));
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_payment", Ref());

        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ComisionEnOtraMonedaOSinMovimiento_EsNula()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent(feeCurrency: "usd")).On(DisputesRoute, Samples.Disputes());
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_payment", Ref());
        Assert.IsNull(body!["result"]!["fee"]);

        stripe.On(IntentRoute, Samples.Intent(withFee: false));
        (_, body) = await host.Send("/v0/payment/get_payment", Ref());
        Assert.IsNull(body!["result"]!["fee"]);
    }

    [TestMethod]
    public async Task PagoQueGuardoElMedio_DevuelveElMedioSinElNumeroCompleto()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent(setupFutureUsage: "off_session")).On(DisputesRoute, Samples.Disputes());
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/payment/get_payment", Ref());

        var saved = body!["result"]!["saved_method"]!;
        Assert.AreEqual("pm_Prueba123", saved["method_external_id"]!.GetValue<string>());
        Assert.AreEqual("cus_Prueba123", saved["customer_external_id"]!.GetValue<string>());
        Assert.AreEqual("visa", saved["brand"]!.GetValue<string>());
        Assert.AreEqual("4242", saved["last4"]!.GetValue<string>());
        Assert.AreEqual(12, saved["exp_month"]!.GetValue<long>());
        Assert.AreEqual(2030, saved["exp_year"]!.GetValue<long>());
        Assert.DoesNotContain("fingerprint", body.ToJsonString());
    }

    [TestMethod]
    public async Task EstadoDesconocido_NoSeAdivina()
    {
        var stripe = new FakeStripe().On(IntentRoute, Samples.Intent(status: "inventado")).On(DisputesRoute, Samples.Disputes());
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/get_payment", Ref());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, StripeHost.Code(body));
    }

    [TestMethod]
    [DataRow("123")]
    [DataRow("pi_../../v1/charges")]
    [DataRow("ch_3Prueba")]
    public async Task IdQueNoEsDeUnPaymentIntent_NoSaleALaRed(string id)
    {
        var stripe = new FakeStripe();
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = id });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, StripeHost.Code(body));
        Assert.IsEmpty(stripe.Requests);
    }

    [TestMethod]
    public async Task Conciliacion_RecorreLosMovimientosYDevuelveCadaPagoUnaVez()
    {
        var page = """
            {
              "object": "list",
              "has_more": true,
              "data": [
                { "id": "txn_1", "type": "charge", "source": { "id": "ch_1", "object": "charge", "payment_intent": "pi_3Prueba" } },
                { "id": "txn_2", "type": "refund", "source": { "id": "re_1", "object": "refund", "payment_intent": "pi_3Prueba" } },
                { "id": "txn_3", "type": "adjustment", "source": { "id": "du_1", "object": "dispute", "payment_intent": "pi_3Otro" } },
                { "id": "txn_4", "type": "payout", "source": { "id": "po_1", "object": "payout" } }
              ]
            }
            """;
        var stripe = new FakeStripe()
            .On("GET /v1/balance_transactions", page)
            .On(IntentRoute, Samples.Intent())
            .On("GET /v1/payment_intents/pi_3Otro", Samples.Intent(id: "pi_3Otro"))
            .On(DisputesRoute, Samples.Disputes());
        await using var host = await StripeHost.Start(stripe);
        var input = new JsonObject { ["from"] = "2026-10-26T00:00:00Z", ["to"] = "2026-10-28T00:00:00Z", ["cursor"] = "txn_0" };

        var (status, body) = await host.Send("/v0/payment/list_payments", input);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var ids = body!["result"]!["payments"]!.AsArray().Select(p => p!["payment_external_id"]!.GetValue<string>()).ToArray();
        CollectionAssert.AreEqual(new[] { "pi_3Prueba", "pi_3Otro" }, ids);
        Assert.AreEqual("txn_4", body["result"]!["next_cursor"]!.GetValue<string>());

        var sent = stripe.Single("GET /v1/balance_transactions");
        Assert.AreEqual("1792972800", sent.Query("created[gte]").ToString());
        Assert.AreEqual("1793145600", sent.Query("created[lt]").ToString());
        Assert.AreEqual("txn_0", sent.Query("starting_after").ToString());
        Assert.AreEqual("data.source", sent.Query("expand[]").ToString());
    }

    [TestMethod]
    public async Task Conciliacion_UltimaPaginaSinCursor_YCursorInvalidoNoSale()
    {
        var stripe = new FakeStripe().On("GET /v1/balance_transactions", """{ "object": "list", "has_more": false, "data": [] }""");
        await using var host = await StripeHost.Start(stripe);
        var range = new JsonObject { ["from"] = "2026-10-26T00:00:00Z", ["to"] = "2026-10-28T00:00:00Z" };

        var (_, body) = await host.Send("/v0/payment/list_payments", range);
        Assert.IsEmpty(body!["result"]!["payments"]!.AsArray());
        Assert.IsNull(body["result"]!["next_cursor"]);

        range["cursor"] = "txn_1&limit=1000";
        var (status, error) = await host.Send("/v0/payment/list_payments", range);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEqual(new[] { "cursor" }, StripeHost.Strings(error!["error"]!["details"]!["fields"]));
        Assert.AreEqual(1, stripe.Requests.Count);
    }

    private static JsonObject Ref() => new() { ["payment_external_id"] = "pi_3Prueba" };
}
