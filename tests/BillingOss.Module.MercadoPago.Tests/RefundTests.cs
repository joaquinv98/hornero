using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>payment.refund: reembolso total o parcial con la clave de idempotencia del núcleo.</summary>
[TestClass]
public sealed class RefundTests
{
    private const string GetRoute = "GET /v1/payments/123456789";
    private const string PostRoute = "POST /v1/payments/123456789/refunds";
    private const string RefundRoute = "/v0/payment/refund";

    [TestMethod]
    public async Task Parcial_ReembolsaElMontoYPasaLaClaveDelNucleoAMercadoPago()
    {
        var mp = new FakeMercadoPago().On(GetRoute, Samples.Payment()).On(PostRoute, Samples.Refund(amount: "5000"), HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial("5000.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("5550001", result["refund_id"]!.GetValue<string>());
        Assert.AreEqual("123456789", result["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", result["status"]!.GetValue<string>());
        Assert.AreEqual("5000.00", result["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("ARS", result["amount"]!["currency"]!.GetValue<string>());

        var sent = mp.Single(PostRoute);
        Assert.AreEqual("https://api.mercadopago.com/v1/payments/123456789/refunds", sent.Url.ToString());
        Assert.AreEqual("pay-77-refund-1", sent.Headers["X-Idempotency-Key"]);
        Assert.AreEqual($"Bearer {MpHost.FakeAccessToken}", sent.Headers["Authorization"]);
        Assert.AreEqual(JsonValueKind.Number, sent.Body!["amount"]!.GetValueKind());
        Assert.AreEqual(5000m, sent.Body["amount"]!.GetValue<decimal>());
    }

    [TestMethod]
    public async Task Total_NoManda_Monto_YDevuelveLoQueReembolsoMercadoPago()
    {
        var mp = new FakeMercadoPago().On(GetRoute, Samples.Payment()).On(PostRoute, Samples.Refund(amount: "12100"), HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, new JsonObject { ["payment_external_id"] = "123456789" }, "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("{}", mp.Single(PostRoute).Body!.ToJsonString());
        Assert.AreEqual("12100.00", body!["result"]!["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("ARS", body["result"]!["amount"]!["currency"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnReembolsoQueNoEstaAprobadoTodavia_QuedaPendiente()
    {
        var mp = new FakeMercadoPago().On(GetRoute, Samples.Payment()).On(PostRoute, Samples.Refund(status: "in_process"), HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var (_, body) = await host.Send(RefundRoute, Partial("5000.00"), "k");

        Assert.AreEqual("pending", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("in_process", body["result"]!["gateway_status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MismaClave_ReembolsaUnaSolaVez()
    {
        var mp = new FakeMercadoPago().On(GetRoute, Samples.Payment()).On(PostRoute, Samples.Refund(), HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var first = await host.Send(RefundRoute, Partial("5000.00"), "pay-77-refund-1");
        var second = await host.Send(RefundRoute, Partial("5000.00"), "pay-77-refund-1");

        Assert.AreEqual(1, mp.Count(PostRoute));
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task MismaClaveConOtroMonto_EsIdempotencyConflict_YNoReembolsaDeMas()
    {
        var mp = new FakeMercadoPago().On(GetRoute, Samples.Payment()).On(PostRoute, Samples.Refund(), HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        await host.Send(RefundRoute, Partial("5000.00"), "pay-77-refund-1");
        var (status, body) = await host.Send(RefundRoute, Partial("6000.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, MpHost.Code(body));
        Assert.AreEqual(1, mp.Count(PostRoute));
    }

    [TestMethod]
    public async Task SinClaveDeIdempotencia_NoReembolsa()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial("5000.00"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task EnOtraMonedaQueElPago_NoReembolsa()
    {
        var mp = new FakeMercadoPago().On(GetRoute, Samples.Payment());
        await using var host = await MpHost.Start(mp);
        var input = Partial("50.00");
        input["amount"]!["currency"] = "USD";

        var (status, body) = await host.Send(RefundRoute, input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.AreEqual(0, mp.Count(PostRoute));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-5.00")]
    [DataRow("10.123")]
    public async Task ConUnMontoInvalido_NoLlamaAMercadoPago(string value)
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial(value), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task ConUnPagoInexistente_EsNotFoundYNoReembolsa()
    {
        var mp = new FakeMercadoPago().On(GetRoute, Samples.Error(404, "not_found", "Payment not found"), HttpStatusCode.NotFound);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial("5000.00"), "k");

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, MpHost.Code(body));
        Assert.AreEqual(0, mp.Count(PostRoute));
    }

    [TestMethod]
    public async Task RechazoDeMercadoPago_EsRejectedConSusCodigos()
    {
        var mp = new FakeMercadoPago()
            .On(GetRoute, Samples.Payment())
            .On(PostRoute, Samples.Error(400, "bad_request", "invalid refund", ("2004", "Refund amount exceeds the payment amount")), HttpStatusCode.BadRequest);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial("99999.00"), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, MpHost.Code(body));
        Assert.AreEqual("2004", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SiMercadoPagoYaUsoLaClaveConOtroCuerpo_EsIdempotencyConflict()
    {
        // Mercado Pago a veces responde con errors[] en lugar de cause[]: se leen los dos.
        var mp = new FakeMercadoPago()
            .On(GetRoute, Samples.Payment())
            .On(PostRoute, """{ "errors": [{ "code": "idempotency_key_already_used", "message": "The value sent in X-Idempotency-Key was already used with a different body" }] }""", HttpStatusCode.Conflict);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial("5000.00"), "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, MpHost.Code(body));
        Assert.AreEqual("idempotency_key_already_used", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CorteDespuesDeEnviar_EsIncierto_YElReintentoVuelveAUsarLaMismaClave()
    {
        var attempts = 0;
        var mp = new FakeMercadoPago()
            .On(GetRoute, Samples.Payment())
            .On(PostRoute, _ => ++attempts == 1
                ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó")
                : Samples.Json(Samples.Refund(), HttpStatusCode.Created));
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial("5000.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, MpHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());

        // El núcleo repite con la misma clave y Mercado Pago recibe la misma X-Idempotency-Key: si el primer pedido
        // llegó a ejecutarse, no se reembolsa dos veces.
        var retry = await host.Send(RefundRoute, Partial("5000.00"), "pay-77-refund-1");

        Assert.AreEqual(HttpStatusCode.OK, retry.Status);
        var posts = mp.Requests.Where(r => r.Route == PostRoute).ToList();
        Assert.HasCount(2, posts);
        Assert.AreEqual("pay-77-refund-1", posts[0].Headers["X-Idempotency-Key"]);
        Assert.AreEqual("pay-77-refund-1", posts[1].Headers["X-Idempotency-Key"]);
    }

    [TestMethod]
    public async Task SinConexionAntesDeEnviar_NoHayIncertidumbre()
    {
        var mp = new FakeMercadoPago()
            .On(GetRoute, Samples.Payment())
            .On(PostRoute, _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "No se pudo conectar"));
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(RefundRoute, Partial("5000.00"), "k");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(body));
    }

    private static JsonObject Partial(string value) => new()
    {
        ["payment_external_id"] = "123456789",
        ["amount"] = new JsonObject { ["value"] = value, ["currency"] = "ARS" },
    };
}
