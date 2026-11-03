using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>payment.get_payment: la captura de PayPal traducida al pago del núcleo, y la captura de una orden aprobada.</summary>
[TestClass]
public sealed class PaymentTests
{
    private const string CaptureRoute = $"GET /v2/payments/captures/{Samples.CaptureId}";
    private const string OrderRoute = $"GET /v2/checkout/orders/{Samples.OrderId}";
    private const string CaptureOrderRoute = $"POST /v2/checkout/orders/{Samples.OrderId}/capture";
    private const string GetPayment = "/v0/payment/get_payment";

    [TestMethod]
    public async Task Captura_DevuelveEstadoMontoComisionFechasYReferencia()
    {
        var pp = new FakePayPal().On(CaptureRoute, Samples.Capture());
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual(Samples.CaptureId, result["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", result["status"]!.GetValue<string>());
        Assert.AreEqual("100.00", result["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("USD", result["amount"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("0.00", result["refunded"]!["value"]!.GetValue<string>());
        Assert.AreEqual("3.98", result["fee"]!["value"]!.GetValue<string>());
        Assert.AreEqual("USD", result["fee"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("2026-10-25T19:01:10Z", result["created_at"]!.GetValue<string>());
        Assert.AreEqual("2026-10-25T19:01:10Z", result["approved_at"]!.GetValue<string>());
        Assert.AreEqual("att-1042", result["external_reference"]!.GetValue<string>());
        Assert.AreEqual("COMPLETED", result["gateway_status"]!.GetValue<string>());

        // Ni el vendedor ni el comprador hacen falta.
        Assert.DoesNotContain("vendedor@example.com", body.ToJsonString());
        Assert.AreEqual("https://api-m.sandbox.paypal.com/v2/payments/captures/3C679366HH908993F", pp.Single(CaptureRoute).Url.ToString());
    }

    [TestMethod]
    [DataRow("COMPLETED", null, "approved")]
    [DataRow("PENDING", "PENDING_REVIEW", "pending")]
    [DataRow("PENDING", "ECHECK", "pending")]
    [DataRow("PENDING", "BUYER_COMPLAINT", "disputed")]
    [DataRow("PENDING", "CHARGEBACK", "disputed")]
    [DataRow("DECLINED", null, "rejected")]
    [DataRow("FAILED", null, "rejected")]
    [DataRow("REFUNDED", null, "refunded")]
    [DataRow("REVERSED", null, "charged_back")]
    public async Task TraduceLosEstadosDeLaCapturaALosDelNucleo(string gateway, string? reason, string expected)
    {
        var pp = new FakePayPal().On(CaptureRoute, Samples.Capture(status: gateway, reason: reason));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(gateway, body["result"]!["gateway_status"]!.GetValue<string>());
        Assert.AreEqual(reason, body["result"]!["gateway_status_detail"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task UnEstadoQueNoConoce_NoSeAdivina()
    {
        var pp = new FakePayPal().On(CaptureRoute, Samples.Capture(status: "ON_HOLD"));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code(body));
        Assert.Contains("ON_HOLD", body!["error"]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Pendiente_NoTieneComisionNiFechaDeAprobacion()
    {
        // PayPal no informa el desglose mientras la captura está pendiente.
        var pp = new FakePayPal().On(CaptureRoute, Samples.Capture(status: "PENDING", reason: "PENDING_REVIEW", fee: null));
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.IsNull(body!["result"]!["fee"]);
        Assert.IsNull(body["result"]!["approved_at"]);
    }

    [TestMethod]
    public async Task ComisionEnOtraMoneda_NoSeInforma()
    {
        // Una comisión en otra moneda no se puede restar del pago: mejor null que un número 1000 veces más grande.
        var pp = new FakePayPal().On(CaptureRoute, Samples.Capture(fee: "5000", feeCurrency: "JPY"));
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.IsNull(body!["result"]!["fee"]);
    }

    [TestMethod]
    public async Task Reembolsada_LoReembolsadoEsElTotal()
    {
        var pp = new FakePayPal().On(CaptureRoute, Samples.Capture(status: "REFUNDED"));
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual("100.00", body!["result"]!["refunded"]!["value"]!.GetValue<string>());
        Assert.AreEqual(0, pp.Count(OrderRoute));
    }

    [TestMethod]
    public async Task ReembolsoParcial_SumaLosReembolsosCompletosDeEstaCapturaEnSuOrden()
    {
        var pp = new FakePayPal()
            .On(CaptureRoute, Samples.Capture(status: "PARTIALLY_REFUNDED"))
            .On(OrderRoute, Samples.Order(
                capture: Samples.Capture(status: "PARTIALLY_REFUNDED"),
                refunds:
                [
                    Samples.Refund("1JU08902781691411", "10.00"),
                    Samples.Refund("2JU08902781691411", "5.50"),
                    Samples.Refund("3JU08902781691411", "7.00", status: "FAILED"),
                    Samples.Refund("4JU08902781691411", "9.00", captureId: "9ZZ00000000000000"),
                ]));
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual("partially_refunded", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("15.50", body["result"]!["refunded"]!["value"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ReembolsoParcialConUnaOrdenQueEstaAppNoPuedeLeer_SaleSinLoReembolsado()
    {
        var pp = new FakePayPal()
            .On(CaptureRoute, Samples.Capture(status: "PARTIALLY_REFUNDED"))
            .On(
                OrderRoute,
                Samples.Error("NOT_AUTHORIZED", "Authorization failed due to insufficient permissions.", ("PERMISSION_DENIED", "You do not have permission to access or perform operations on this resource.")),
                HttpStatusCode.Forbidden);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("partially_refunded", body!["result"]!["status"]!.GetValue<string>());
        Assert.IsNull(body["result"]!["refunded"]);
    }

    [TestMethod]
    [DataRow("COMPLETED", "WAITING_FOR_SELLER_RESPONSE")]
    [DataRow("COMPLETED", "OPEN")]
    [DataRow("PARTIALLY_REFUNDED", "UNDER_REVIEW")]
    public async Task CapturaConUnaDisputaAbierta_EsDisputed(string capture, string dispute)
    {
        // La captura sola no lo muestra: con la disputa abierta puede seguir COMPLETED.
        var pp = new FakePayPal()
            .On(CaptureRoute, Samples.Capture(status: capture))
            .On(OrderRoute, Samples.Order(capture: Samples.Capture(status: capture)))
            .On(FakePayPal.DisputesRoute, Samples.Disputes(("PP-D-27803", "RESOLVED"), ("PP-D-27804", dispute)));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("disputed", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(capture, body["result"]!["gateway_status"]!.GetValue<string>());
        Assert.AreEqual(Samples.CaptureId, pp.Single(FakePayPal.DisputesRoute).Query("disputed_transaction_id"));
    }

    [TestMethod]
    [DataRow("REFUNDED", "RESOLVED_BUYER_FAVOUR", "charged_back")]
    [DataRow("COMPLETED", "RESOLVED_SELLER_FAVOUR", "approved")]
    [DataRow("COMPLETED", "CANCELED_BY_BUYER", "approved")]
    [DataRow("REFUNDED", "RESOLVED_WITH_PAYOUT", "refunded")]
    public async Task UnaDisputaResuelta_DaChargedBackSoloSiLaGanoElComprador(string capture, string outcome, string expected)
    {
        // Un contracargo perdido deja la captura REFUNDED, igual que un reembolso: el resultado está en la disputa.
        const string DisputeId = "PP-D-27803";
        var pp = new FakePayPal()
            .On(CaptureRoute, Samples.Capture(status: capture))
            .On(FakePayPal.DisputesRoute, Samples.Disputes((DisputeId, "RESOLVED")))
            .On($"GET /v1/customer/disputes/{DisputeId}", Samples.Dispute(DisputeId, outcome));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinLaFuncionDeDisputas_SeInformaLaCapturaConSuEstado()
    {
        var pp = new FakePayPal()
            .On(CaptureRoute, Samples.Capture())
            .On(FakePayPal.DisputesRoute, """{ "name": "NOT_AUTHORIZED", "message": "Authorization failed due to insufficient permissions." }""", HttpStatusCode.Forbidden);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("approved", body!["result"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("DECLINED")]
    [DataRow("FAILED")]
    public async Task UnaCapturaQueNoCobro_NoBuscaDisputas(string capture)
    {
        var pp = new FakePayPal().On(CaptureRoute, Samples.Capture(status: capture, fee: null));
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(GetPayment, Input(Samples.CaptureId));

        Assert.AreEqual("rejected", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(0, pp.Count(FakePayPal.DisputesRoute));
    }

    [TestMethod]
    public async Task OrdenAprobada_SeCapturaConUnPedidoRepetible_YDevuelveLaCaptura()
    {
        var pp = new FakePayPal()
            .On(OrderRoute, Samples.Order(status: "APPROVED"))
            .On(CaptureOrderRoute, Samples.Order(capture: Samples.Capture()), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual(Samples.CaptureId, result["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", result["status"]!.GetValue<string>());
        Assert.AreEqual("100.00", result["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("3.98", result["fee"]!["value"]!.GetValue<string>());

        // Dentro de la orden la captura no trae custom_id: la referencia sale de la unidad de compra.
        Assert.AreEqual("att-1042", result["external_reference"]!.GetValue<string>());

        var capture = pp.Single(CaptureOrderRoute);
        Assert.AreEqual($"capture-{Samples.OrderId}", capture.Headers["PayPal-Request-Id"]);
        Assert.AreEqual("return=representation", capture.Headers["Prefer"]);
        Assert.AreEqual("{}", capture.Text);
    }

    [TestMethod]
    public async Task OrdenYaCapturada_SeLeeSinCapturarDeNuevo()
    {
        var pp = new FakePayPal().On(OrderRoute, Samples.Order(capture: Samples.Capture()));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(Samples.CaptureId, body!["result"]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual(0, pp.Count(CaptureOrderRoute));
    }

    [TestMethod]
    public async Task OtraReplicaLaCapturoAntes_SeLeeLaOrden()
    {
        // Entre leer la orden y capturarla, otra réplica la capturó: PayPal contesta ORDER_ALREADY_CAPTURED.
        var reads = 0;
        var pp = new FakePayPal()
            .On(OrderRoute, _ => Samples.Json(++reads == 1 ? Samples.Order(status: "APPROVED") : Samples.Order(capture: Samples.Capture())))
            .On(
                CaptureOrderRoute,
                Samples.Error("UNPROCESSABLE_ENTITY", "The requested action could not be performed.", ("ORDER_ALREADY_CAPTURED", "Order already captured.")),
                HttpStatusCode.UnprocessableEntity);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(Samples.CaptureId, body!["result"]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual(2, pp.Count(OrderRoute));
    }

    [TestMethod]
    public async Task OrdenDeOtroSistema_NoSeCaptura()
    {
        // La cuenta de PayPal puede ser la misma que usa otro sistema: sus órdenes no son de este módulo.
        var pp = new FakePayPal().On(OrderRoute, Samples.Order(status: "APPROVED", reference: "default"));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, PpHost.Code(body));
        Assert.AreEqual(0, pp.Count(CaptureOrderRoute));
    }

    [TestMethod]
    [DataRow("CREATED")]
    [DataRow("PAYER_ACTION_REQUIRED")]
    [DataRow("VOIDED")]
    public async Task OrdenSinAprobar_NoTienePago(string orderStatus)
    {
        var pp = new FakePayPal().On(OrderRoute, Samples.Order(status: orderStatus));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, PpHost.Code(body));
        Assert.AreEqual(0, pp.Count(CaptureOrderRoute));
    }

    [TestMethod]
    public async Task CapturaRechazada_EsRejected()
    {
        var pp = new FakePayPal()
            .On(OrderRoute, Samples.Order(status: "APPROVED"))
            .On(
                CaptureOrderRoute,
                Samples.Error("UNPROCESSABLE_ENTITY", "The requested action could not be performed.", ("INSTRUMENT_DECLINED", "The instrument presented was either declined by the processor or bank.")),
                HttpStatusCode.UnprocessableEntity);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(body));
        Assert.AreEqual("INSTRUMENT_DECLINED", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CapturaDenegadaPorPayPal_EsUnPagoRechazado()
    {
        // PayPal crea la captura pero la deniega: es un pago, con su ID, en estado rejected.
        var pp = new FakePayPal()
            .On(OrderRoute, Samples.Order(status: "APPROVED"))
            .On(CaptureOrderRoute, Samples.Order(capture: Samples.Capture(status: "DECLINED", fee: null)), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual("rejected", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(Samples.CaptureId, body["result"]!["payment_external_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CorteAlCapturar_EsIncierto_YElReintentoRepiteElMismoPedido()
    {
        var attempts = 0;
        var pp = new FakePayPal()
            .On(OrderRoute, Samples.Order(status: "APPROVED"))
            .On(CaptureOrderRoute, _ => ++attempts == 1
                ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó")
                : Samples.Json(Samples.Order(capture: Samples.Capture()), HttpStatusCode.OK));
        await using var host = await PpHost.Start(pp);

        var first = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));
        var retry = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PpHost.Code(first.Body));
        Assert.AreEqual(HttpStatusCode.OK, retry.Status);

        // El mismo PayPal-Request-Id: si el primero se ejecutó, PayPal devuelve esa captura y no cobra dos veces.
        var captures = pp.Requests.Where(r => r.Route == CaptureOrderRoute).ToList();
        Assert.HasCount(2, captures);
        Assert.IsTrue(captures.All(c => c.Headers["PayPal-Request-Id"] == $"capture-{Samples.OrderId}"));
    }

    [TestMethod]
    public async Task CapturaEnDiferido_SeVuelveAPedir()
    {
        // Un 202 todavía no trae la captura: no hay pago que registrar y el núcleo reintenta.
        var pp = new FakePayPal()
            .On(OrderRoute, Samples.Order(status: "APPROVED"))
            .On(CaptureOrderRoute, $$"""{ "id": "{{Samples.OrderId}}", "status": "APPROVED" }""", HttpStatusCode.Accepted);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input($"order:{Samples.OrderId}"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code(body));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("3C679366HH908993F/../x")]
    [DataRow("order:")]
    [DataRow("order:5O19 0127")]
    public async Task IdInvalido_EsInvalidInputSinLlamar(string id)
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(GetPayment, Input(id));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        Assert.IsEmpty(pp.Requests);
    }

    private static JsonObject Input(string id) => new() { ["payment_external_id"] = id };
}
