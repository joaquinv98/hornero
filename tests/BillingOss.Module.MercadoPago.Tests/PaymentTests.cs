using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>payment.get_payment y payment.list_payments: el pago de Mercado Pago traducido al del núcleo.</summary>
[TestClass]
public sealed class PaymentTests
{
    [TestMethod]
    public async Task GetPayment_DevuelveEstadoMontoComisionFechaYReferencia()
    {
        var mp = new FakeMercadoPago().On("GET /v1/payments/123456789", Samples.Payment());
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("123456789"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("123456789", result["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", result["status"]!.GetValue<string>());
        Assert.AreEqual("12100.00", result["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("ARS", result["amount"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("0.00", result["refunded"]!["value"]!.GetValue<string>());

        // La comisión es la del vendedor: la de financiación que paga el comprador no cuenta.
        Assert.AreEqual("435.60", result["fee"]!["value"]!.GetValue<string>());
        Assert.AreEqual("ARS", result["fee"]!["currency"]!.GetValue<string>());

        // Las fechas salen en UTC aunque Mercado Pago las mande con su zona.
        Assert.AreEqual("2026-10-25T19:00:00Z", result["created_at"]!.GetValue<string>());
        Assert.AreEqual("2026-10-25T19:01:10Z", result["approved_at"]!.GetValue<string>());
        Assert.AreEqual("att-1042", result["external_reference"]!.GetValue<string>());
        Assert.AreEqual("approved", result["gateway_status"]!.GetValue<string>());
        Assert.AreEqual("accredited", result["gateway_status_detail"]!.GetValue<string>());
        Assert.IsTrue(result["livemode"]!.GetValue<bool>());

        // Los datos del pagador no hacen falta y no se devuelven.
        Assert.DoesNotContain("comprador", body.ToJsonString());
        Assert.DoesNotContain("11111111", body.ToJsonString());

        var sent = mp.Single("GET /v1/payments/123456789");
        Assert.AreEqual("https://api.mercadopago.com/v1/payments/123456789", sent.Url.ToString());
        Assert.AreEqual($"Bearer {MpHost.FakeAccessToken}", sent.Headers["Authorization"]);
    }

    [TestMethod]
    public async Task GetPayment_ConCredencialesDePrueba_LoDice()
    {
        var mp = new FakeMercadoPago().On("GET /v1/payments/123456789", Samples.Payment(live: false));
        await using var host = await MpHost.Start(mp);

        var (_, body) = await host.Send("/v0/payment/get_payment", Input("123456789"));

        Assert.IsFalse(body!["result"]!["livemode"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task GetPayment_SinAprobarTodavia_NoTieneFechaDeAprobacion()
    {
        var mp = new FakeMercadoPago().On("GET /v1/payments/123456789", Samples.Payment(status: "pending", detail: "pending_waiting_payment", approvedAt: null));
        await using var host = await MpHost.Start(mp);

        var (_, body) = await host.Send("/v0/payment/get_payment", Input("123456789"));

        Assert.AreEqual("pending", body!["result"]!["status"]!.GetValue<string>());
        Assert.IsNull(body["result"]!["approved_at"]);
    }

    [TestMethod]
    [DataRow("approved", "accredited", "0", "approved")]
    [DataRow("pending", "pending_waiting_payment", "0", "pending")]
    [DataRow("in_process", "pending_contingency", "0", "pending")]
    [DataRow("authorized", "pending_capture", "0", "pending")]
    [DataRow("rejected", "cc_rejected_other_reason", "0", "rejected")]
    [DataRow("cancelled", "expired", "0", "rejected")]
    [DataRow("in_mediation", "pending", "0", "disputed")]
    [DataRow("charged_back", "settled", "0", "charged_back")]
    [DataRow("refunded", "refunded", "12100", "refunded")]

    // Un reembolso parcial deja el pago en approved con el detalle partially_refunded.
    [DataRow("approved", "partially_refunded", "5000", "partially_refunded")]

    // Aunque el detalle no lo diga, lo reembolsado manda.
    [DataRow("approved", "accredited", "5000", "partially_refunded")]
    [DataRow("approved", "accredited", "12100", "refunded")]
    public async Task GetPayment_TraduceLosEstadosDeMercadoPagoALosDelNucleo(string gateway, string detail, string refunded, string expected)
    {
        var mp = new FakeMercadoPago().On("GET /v1/payments/123456789", Samples.Payment(status: gateway, detail: detail, refunded: refunded));
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("123456789"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(gateway, body["result"]!["gateway_status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetPayment_ConUnEstadoQueNoConoce_NoLoAdivina()
    {
        var mp = new FakeMercadoPago().On("GET /v1/payments/123456789", Samples.Payment(status: "estado_nuevo"));
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("123456789"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(body));
        Assert.Contains("estado_nuevo", body!["error"]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("../v1/users")]
    [DataRow("12 34")]
    [DataRow("1234?x=1")]
    [DataRow("123456789012345678901")]
    public async Task GetPayment_ConUnIdQueNoEsUnNumero_NoLlamaAMercadoPago(string id)
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input(id));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task GetPayment_PagoInexistente_EsNotFound()
    {
        var mp = new FakeMercadoPago().On(
            "GET /v1/payments/999",
            Samples.Error(404, "not_found", "Payment not found"),
            HttpStatusCode.NotFound);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("999"));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, MpHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ListPayments_PidePorRangoOrdenadoPorIdYDaLaPaginaSiguiente()
    {
        var mp = new FakeMercadoPago().On(
            "GET /v1/payments/search",
            Samples.Search(250, 0, Samples.Payment(id: 111), Samples.Payment(id: 112, status: "pending", approvedAt: null)));
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/list_payments", Range());

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual(250, result["total"]!.GetValue<int>());
        Assert.AreEqual(2, result["next_offset"]!.GetValue<int>());
        var payments = result["payments"]!.AsArray();
        Assert.HasCount(2, payments);
        Assert.AreEqual("111", payments[0]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", payments[0]!["status"]!.GetValue<string>());
        Assert.AreEqual("pending", payments[1]!["status"]!.GetValue<string>());

        var sent = mp.Single("GET /v1/payments/search");
        Assert.AreEqual("id", sent.Query("sort"));
        Assert.AreEqual("asc", sent.Query("criteria"));
        Assert.AreEqual("date_created", sent.Query("range"));
        Assert.AreEqual("2026-10-25T00:00:00.000Z", sent.Query("begin_date"));
        Assert.AreEqual("2026-10-26T00:00:00.000Z", sent.Query("end_date"));
        Assert.AreEqual("100", sent.Query("limit"));
        Assert.AreEqual("0", sent.Query("offset"));

        // Los dos puntos van escapados en la URL.
        Assert.Contains("begin_date=2026-10-25T00%3A00%3A00.000Z", sent.Url.Query);
    }

    [TestMethod]
    public async Task ListPayments_EnLaUltimaPagina_NoHayOffsetSiguiente()
    {
        var mp = new FakeMercadoPago().On("GET /v1/payments/search", Samples.Search(102, 100, Samples.Payment(id: 211), Samples.Payment(id: 212)));
        await using var host = await MpHost.Start(mp);
        var input = Range();
        input["offset"] = 100;

        var (_, body) = await host.Send("/v0/payment/list_payments", input);

        Assert.IsNull(body!["result"]!["next_offset"]);
        Assert.AreEqual(100, body["result"]!["offset"]!.GetValue<int>());
        Assert.AreEqual("100", mp.Single("GET /v1/payments/search").Query("offset"));
    }

    [TestMethod]
    [DataRow("created", "date_created")]
    [DataRow("approved", "date_approved")]
    [DataRow("updated", "date_last_updated")]
    public async Task ListPayments_ElegirPorQueFechaSeFiltra(string by, string range)
    {
        var mp = new FakeMercadoPago().On("GET /v1/payments/search", Samples.Search(0, 0));
        await using var host = await MpHost.Start(mp);
        var input = Range();
        input["by"] = by;

        var (status, body) = await host.Send("/v0/payment/list_payments", input);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsEmpty(body!["result"]!["payments"]!.AsArray());
        Assert.AreEqual(range, mp.Single("GET /v1/payments/search").Query("range"));
    }

    [TestMethod]
    public async Task ListPayments_ConRangoInvertidoOFiltroDesconocido_NoLlamaAMercadoPago()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);
        var inverted = Range();
        inverted["from"] = "2026-10-27T00:00:00Z";
        var unknown = Range();
        unknown["by"] = "money_release_date";
        var negative = Range();
        negative["offset"] = -1;

        foreach (var input in new[] { inverted, unknown, negative, new JsonObject() })
        {
            var (status, body) = await host.Send("/v0/payment/list_payments", input);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
            Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        }

        Assert.IsEmpty(mp.Requests);
    }

    private static JsonObject Input(string id) => new() { ["payment_external_id"] = id };

    private static JsonObject Range() => new() { ["from"] = "2026-10-25T00:00:00Z", ["to"] = "2026-10-26T00:00:00Z" };
}
