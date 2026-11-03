using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>
/// Cómo los errores de Mercado Pago salen como errores del protocolo (sección 10). Las lecturas usan get_payment y
/// los pedidos que cambian algo usan create_checkout.
/// </summary>
[TestClass]
public sealed class ErrorTests
{
    private const string Read = "GET /v1/payments/123";
    private const string Write = "POST /checkout/preferences";

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task TokenRechazado_EsInvalidConfig_YNoSeReintenta(HttpStatusCode upstream)
    {
        var mp = new FakeMercadoPago().On(Read, upstream == HttpStatusCode.Unauthorized ? Samples.Unauthorized : Samples.PolicyForbidden, upstream);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, MpHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual("access_token", body["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(42, 42)]
    [DataRow(0, 30)]
    public async Task LimiteDePedidos_EsRateLimitedConElRetryAfterDeMercadoPago(int header, int expected)
    {
        var mp = new FakeMercadoPago().On(Read, _ =>
        {
            var response = Samples.Json(Samples.Error(429, "too_many_requests", "Too many requests"), HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(header));
            return response;
        });
        await using var host = await MpHost.Start(mp);

        var (status, body) = await GetPayment(host);

        // Un Retry-After de cero no sirve de nada: se espera lo de siempre.
        Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
        Assert.AreEqual(ErrorCodes.RateLimited, MpHost.Code(body));
        Assert.AreEqual(expected, body!["error"]!["retry_after"]!.GetValue<int>());
        Assert.IsTrue(body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task LimiteDePedidos_SinRetryAfter_EspeleraUnRatoPorDefecto()
    {
        var mp = new FakeMercadoPago().On(Read, """{ "message": "Too many requests" }""", HttpStatusCode.TooManyRequests);
        await using var host = await MpHost.Start(mp);

        var (_, body) = await GetPayment(host);

        Assert.AreEqual(30, body!["error"]!["retry_after"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.GatewayTimeout)]
    public async Task ErroresDelServidor_SonUpstreamUnavailable_Reintentables(HttpStatusCode upstream)
    {
        var mp = new FakeMercadoPago().On(Read, "<html>Service Unavailable</html>", upstream).On(Write, "{}", upstream);
        await using var host = await MpHost.Start(mp);

        foreach (var (status, body) in new[] { await GetPayment(host), await Checkout(host) })
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(body));
            Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        }
    }

    [TestMethod]
    public async Task SinConexionAntesDeEnviar_EsUpstreamUnavailable_EnLecturasYEscrituras()
    {
        Func<Sent, HttpResponseMessage> noConnection = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No se resolvió el host");
        var mp = new FakeMercadoPago().On(Read, noConnection).On(Write, noConnection);
        await using var host = await MpHost.Start(mp);

        foreach (var (status, body) in new[] { await GetPayment(host), await Checkout(host) })
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(body));
        }
    }

    [TestMethod]
    public async Task CorteDespuesDeEnviar_EsIncierto_SoloSiElPedidoCambiaAlgo()
    {
        Func<Sent, HttpResponseMessage> cut = _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
        var mp = new FakeMercadoPago().On(Read, cut).On(Write, cut);
        await using var host = await MpHost.Start(mp);

        var read = await GetPayment(host);
        var write = await Checkout(host);

        // Una consulta se repite sin riesgo. Un pedido que crea algo pudo haberse ejecutado.
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, read.Status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(read.Body));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, write.Status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, MpHost.Code(write.Body));
        Assert.AreEqual("no_response", write.Body!["error"]!["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PlazoVencido_SeTrataComoUnCorteDespuesDeEnviar()
    {
        Func<Sent, HttpResponseMessage> timeout = _ => throw new TaskCanceledException("Se agotó el tiempo", new TimeoutException());
        var mp = new FakeMercadoPago().On(Read, timeout).On(Write, timeout);
        await using var host = await MpHost.Start(mp);

        var read = await GetPayment(host);
        var write = await Checkout(host);

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(read.Body));
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, MpHost.Code(write.Body));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, "<html>no es json</html>")]
    [DataRow(HttpStatusCode.OK, "")]
    [DataRow(HttpStatusCode.Found, "")]
    public async Task RespuestaQueNoSePuedeLeer_EsIncierta_SoloSiElPedidoCambiaAlgo(HttpStatusCode upstream, string text)
    {
        var mp = new FakeMercadoPago().On(Read, text, upstream).On(Write, text, upstream);
        await using var host = await MpHost.Start(mp);

        var read = await GetPayment(host);
        var write = await Checkout(host);

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(read.Body));
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, MpHost.Code(write.Body));
    }

    [TestMethod]
    public async Task ErrorConElCodigoEnLaRaiz_SeLeeEseCodigo()
    {
        var mp = new FakeMercadoPago().On(Read, """{ "message": "parametro invalido", "status": 400, "code": "invalid_param" }""", HttpStatusCode.BadRequest);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, MpHost.Code(body));
        Assert.AreEqual("invalid_param", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual("parametro invalido", body["error"]!["details"]!["upstream"]![0]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ErrorSinCuerpoUtil_EsRejectedConElCodigoHttp()
    {
        var mp = new FakeMercadoPago().On(Read, string.Empty, HttpStatusCode.BadRequest);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, MpHost.Code(body));
        Assert.AreEqual("400", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ElAccessTokenNoSeFiltra_AunqueMercadoPagoLoRepitaEnElError()
    {
        var echo = $"el token {MpHost.FakeAccessToken} no sirve";
        var mp = new FakeMercadoPago()
            .On(Read, Samples.Error(400, "bad_request", echo, ("1", echo)), HttpStatusCode.BadRequest)
            .On(Write, Samples.Error(401, "unauthorized", echo), HttpStatusCode.Unauthorized);
        await using var host = await MpHost.Start(mp);

        var rejected = await GetPayment(host);
        var unauthorized = await Checkout(host);

        foreach (var (status, body) in new[] { rejected, unauthorized })
        {
            Assert.AreNotEqual(HttpStatusCode.OK, status);
            Assert.DoesNotContain(MpHost.FakeAccessToken, body!.ToJsonString());
            Assert.DoesNotContain(MpHost.FakeWebhookSecret, body.ToJsonString());
        }

        Assert.Contains("***", rejected.Body!.ToJsonString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("con espacio")]
    [DataRow("con\nsalto")]
    public async Task AccessTokenQueNoSirve_EsInvalidConfig_SinSalirALaRed(string token)
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);
        var config = MpHost.Config();
        config["access_token"] = token;

        var (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "123" }, config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, MpHost.Code(body));
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task ConfiguracionConUnTipoMalo_EsInvalidConfig()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var config = MpHost.Config();
        config["access_token"] = 12345;

        var (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "123" }, config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, MpHost.Code(body));
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> GetPayment(MpHost host) =>
        host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "123" });

    private static Task<(HttpStatusCode Status, JsonNode? Body)> Checkout(MpHost host) =>
        host.Send(
            "/v0/payment/create_checkout",
            new JsonObject
            {
                ["attempt_id"] = "att-1",
                ["amount"] = new JsonObject { ["value"] = "100.00", ["currency"] = "ARS" },
                ["items"] = new JsonArray(new JsonObject { ["title"] = "Servicio", ["quantity"] = 1, ["unit_price"] = "100.00" }),
            },
            Guid.NewGuid().ToString());
}
