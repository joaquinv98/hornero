using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>payment.refund: reembolso total o parcial de una captura, con la clave de idempotencia del núcleo.</summary>
[TestClass]
public sealed class RefundTests
{
    private const string Route = $"POST /v2/payments/captures/{Samples.CaptureId}/refund";
    private const string Refund = "/v0/payment/refund";
    private const string RefundId = "1JU08902781691411";

    [TestMethod]
    public async Task Parcial_ReembolsaEnLaMonedaDelPedido_YPasaLaClaveAPayPal()
    {
        var pp = new FakePayPal().On(Route, Samples.Refund(RefundId, "30.00").ToJsonString(), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, Partial("30.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual(RefundId, result["refund_id"]!.GetValue<string>());
        Assert.AreEqual(Samples.CaptureId, result["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", result["status"]!.GetValue<string>());
        Assert.AreEqual("30.00", result["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("USD", result["amount"]!["currency"]!.GetValue<string>());

        // La parte de la comisión que PayPal devuelve, y lo reembolsado en total de la captura.
        Assert.AreEqual("0.12", result["fee_refunded"]!["value"]!.GetValue<string>());
        Assert.AreEqual("30.00", result["total_refunded"]!["value"]!.GetValue<string>());
        Assert.AreEqual("COMPLETED", result["gateway_status"]!.GetValue<string>());

        var sent = pp.Single(Route);
        Assert.AreEqual($"https://api-m.sandbox.paypal.com/v2/payments/captures/{Samples.CaptureId}/refund", sent.Url.ToString());
        Assert.AreEqual($"refund-{Samples.CaptureId}-pay-77-refund-1", sent.Headers["PayPal-Request-Id"]);
        Assert.AreEqual("return=representation", sent.Headers["Prefer"]);
        Assert.AreEqual("""{"amount":{"currency_code":"USD","value":"30.00"}}""", sent.Text);
    }

    [TestMethod]
    public async Task Total_MandaElCuerpoVacio_YDevuelveLoQueReembolsoPayPal()
    {
        var pp = new FakePayPal().On(Route, Samples.Refund(RefundId, "100.00", total: "100.00").ToJsonString(), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, new JsonObject { ["payment_external_id"] = Samples.CaptureId }, "k");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("{}", pp.Single(Route).Text);
        Assert.AreEqual("100.00", body!["result"]!["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("USD", body["result"]!["amount"]!["currency"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ReembolsoPendiente_EsPending()
    {
        var pp = new FakePayPal().On(Route, Samples.Refund(RefundId, "30.00", status: "PENDING").ToJsonString(), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (_, body) = await host.Send(Refund, Partial("30.00"), "k");

        Assert.AreEqual("pending", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("PENDING", body["result"]!["gateway_status"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("FAILED")]
    [DataRow("CANCELLED")]
    public async Task ReembolsoQuePayPalNoCompleto_EsRejected_ComoUn422(string gateway)
    {
        // No se movió plata: sale como cualquier rechazo, y la misma clave devuelve el mismo error sin volver a pedir.
        var pp = new FakePayPal().On(Route, Samples.Refund(RefundId, "30.00", status: gateway).ToJsonString(), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, Partial("30.00"), "k");
        var again = await host.Send(Refund, Partial("30.00"), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(gateway, body["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual(RefundId, body["error"]!["details"]!["refund_id"]!.GetValue<string>());
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(again.Body));
        Assert.AreEqual(1, pp.Count(Route));
    }

    [TestMethod]
    public async Task MismaClave_ReembolsaUnaSolaVez()
    {
        var pp = new FakePayPal().On(Route, Samples.Refund(RefundId, "30.00").ToJsonString(), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        var first = await host.Send(Refund, Partial("30.00"), "pay-77-refund-1");
        var second = await host.Send(Refund, Partial("30.00"), "pay-77-refund-1");

        Assert.AreEqual(1, pp.Count(Route));
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task MismaClaveConOtroMonto_EsIdempotencyConflict_YNoReembolsaDeMas()
    {
        var pp = new FakePayPal().On(Route, Samples.Refund(RefundId, "30.00").ToJsonString(), HttpStatusCode.Created);
        await using var host = await PpHost.Start(pp);

        await host.Send(Refund, Partial("30.00"), "pay-77-refund-1");
        var (status, body) = await host.Send(Refund, Partial("40.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, PpHost.Code(body));
        Assert.AreEqual(1, pp.Count(Route));
    }

    [TestMethod]
    public async Task SinClaveDeIdempotencia_NoReembolsa()
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, Partial("30.00"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    [DataRow("0", "USD")]
    [DataRow("-5.00", "USD")]
    [DataRow("10.123", "USD")]
    [DataRow("10.50", "JPY")]
    [DataRow("1000.00", "ARS")]
    public async Task MontoOMonedaInvalidos_NoLlamaAPayPal(string value, string currency)
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);
        var input = Partial(value);
        input["amount"]!["currency"] = currency;

        var (status, body) = await host.Send(Refund, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    public async Task UnaOrdenNoSeReembolsa_SoloUnaCaptura()
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, new JsonObject { ["payment_external_id"] = $"order:{Samples.OrderId}" }, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        Assert.IsEmpty(pp.Requests);
    }

    [TestMethod]
    [DataRow("REFUND_AMOUNT_EXCEEDED")]
    [DataRow("REFUND_TIME_LIMIT_EXCEEDED")]
    [DataRow("CAPTURE_FULLY_REFUNDED")]
    [DataRow("TRANSACTION_DISPUTED")]
    [DataRow("CURRENCY_MISMATCH")]
    public async Task RechazoDePayPal_EsRejectedConSuCodigo(string issue)
    {
        // En todos estos casos la plata no se movió: el núcleo deja el importe como saldo a favor.
        var pp = new FakePayPal().On(
            Route,
            Samples.Error("UNPROCESSABLE_ENTITY", "The requested action could not be performed.", (issue, "No se puede reembolsar.")),
            HttpStatusCode.UnprocessableEntity);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, Partial("30.00"), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(issue, body["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task OtroPedidoEnCurso_EsConflictReintentable()
    {
        var pp = new FakePayPal().On(
            Route,
            Samples.Error("RESOURCE_CONFLICT", "The server has detected a conflict while processing this request.", ("PREVIOUS_REQUEST_IN_PROGRESS", "A previous request on this resource is currently in progress.")),
            HttpStatusCode.Conflict);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, Partial("30.00"), "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.Conflict, PpHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task CorteDespuesDeEnviar_EsIncierto_YElReintentoVuelveAUsarLaMismaClave()
    {
        var attempts = 0;
        var pp = new FakePayPal().On(Route, _ => ++attempts == 1
            ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó")
            : Samples.Json(Samples.Refund(RefundId, "30.00").ToJsonString(), HttpStatusCode.OK));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(Refund, Partial("30.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PpHost.Code(body));

        // El núcleo repite con la misma clave y PayPal recibe el mismo PayPal-Request-Id: no reembolsa dos veces.
        var retry = await host.Send(Refund, Partial("30.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.OK, retry.Status);
        var posts = pp.Requests.Where(r => r.Route == Route).ToList();
        Assert.HasCount(2, posts);
        Assert.AreEqual(posts[0].Headers["PayPal-Request-Id"], posts[1].Headers["PayPal-Request-Id"]);
    }

    private static JsonObject Partial(string value) => new()
    {
        ["payment_external_id"] = Samples.CaptureId,
        ["amount"] = new JsonObject { ["value"] = value, ["currency"] = "USD" },
    };
}
