using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>
/// El token de la app y cómo los errores de PayPal salen como errores del protocolo (sección 10). Las lecturas usan
/// get_payment y los pedidos que cambian algo usan create_checkout.
/// </summary>
[TestClass]
public sealed class ErrorTests
{
    private const string Read = $"GET /v2/payments/captures/{Samples.CaptureId}";
    private const string Write = "POST /v2/checkout/orders";

    [TestMethod]
    public async Task ElTokenSeReusaHastaPocoAntesDeVencer()
    {
        var clock = new ManualClock();
        var pp = new FakePayPal().On(Read, Samples.Capture());
        await using var host = await PpHost.Start(pp, time: clock);

        await GetPayment(host);
        await GetPayment(host);
        Assert.AreEqual(1, pp.Count(FakePayPal.TokenRoute));

        // El token del falso dura 9 horas y se renueva 5 minutos antes.
        clock.Now = clock.Now.AddHours(9).AddMinutes(-4);
        await GetPayment(host);
        Assert.AreEqual(2, pp.Count(FakePayPal.TokenRoute));
        var reads = pp.Requests.Where(r => r.Route == Read).ToList();
        Assert.AreNotEqual(reads[0].Headers["Authorization"], reads[2].Headers["Authorization"]);
    }

    [TestMethod]
    public async Task CadaAppTieneSuToken()
    {
        var pp = new FakePayPal().On(Read, Samples.Capture());
        await using var host = await PpHost.Start(pp);
        var other = PpHost.Config();
        other["client_id"] = "otra-app";

        await GetPayment(host);
        await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = Samples.CaptureId }, config: other);

        Assert.AreEqual(2, pp.Count(FakePayPal.TokenRoute));
    }

    [TestMethod]
    public async Task TokenRevocado_SePideOtroYSeRepiteUnaVez()
    {
        var calls = 0;
        var pp = new FakePayPal().On(Read, _ => ++calls == 1
            ? Samples.Json(Samples.Error("AUTHENTICATION_FAILURE", "Authentication failed due to invalid authentication credentials."), HttpStatusCode.Unauthorized)
            : Samples.Json(Samples.Capture()));
        await using var host = await PpHost.Start(pp);

        var (status, _) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(2, pp.Count(FakePayPal.TokenRoute));
        Assert.AreEqual(2, pp.Count(Read));
    }

    [TestMethod]
    public async Task TokenRechazadoDosVeces_EsInvalidConfig_YNoSeReintenta()
    {
        var pp = new FakePayPal().On(Read, Samples.Error("AUTHENTICATION_FAILURE", "Authentication failed."), HttpStatusCode.Unauthorized);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(2, pp.Count(Read));
    }

    [TestMethod]
    public async Task SinPermisoSobreElRecurso_EsRejected_NoUnaConfiguracionMala()
    {
        // Así contesta PayPal un recurso de otra app de la cuenta: NOT_AUTHORIZED con el issue PERMISSION_DENIED.
        var pp = new FakePayPal().On(
            Read,
            Samples.Error(
                "NOT_AUTHORIZED",
                "Authorization failed due to insufficient permissions.",
                ("PERMISSION_DENIED", "You do not have permission to access or perform operations on this resource.")),
            HttpStatusCode.Forbidden);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(body));
        Assert.AreEqual("PERMISSION_DENIED", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(42, 42)]
    [DataRow(0, 30)]
    public async Task LimiteDePedidos_EsRateLimitedConElRetryAfterDePayPal(int header, int expected)
    {
        var pp = new FakePayPal().On(Read, _ =>
        {
            var response = Samples.Json(Samples.Error("RATE_LIMIT_REACHED", "Too many requests."), HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(header));
            return response;
        });
        await using var host = await PpHost.Start(pp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
        Assert.AreEqual(ErrorCodes.RateLimited, PpHost.Code(body));
        Assert.AreEqual(expected, body!["error"]!["retry_after"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task ErroresDelServidor_SonUpstreamUnavailable_Reintentables(HttpStatusCode upstream)
    {
        var pp = new FakePayPal().On(Read, "<html>Service Unavailable</html>", upstream).On(Write, "{}", upstream);
        await using var host = await PpHost.Start(pp);

        foreach (var (status, body) in new[] { await GetPayment(host), await Checkout(host) })
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code(body));
            Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        }
    }

    [TestMethod]
    public async Task SinConexionAntesDeEnviar_EsUpstreamUnavailable_EnLecturasYEscrituras()
    {
        Func<Sent, HttpResponseMessage> noConnection = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No se resolvió el host");
        var pp = new FakePayPal().On(Read, noConnection).On(Write, noConnection);
        await using var host = await PpHost.Start(pp);

        foreach (var (status, body) in new[] { await GetPayment(host), await Checkout(host) })
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code(body));
        }
    }

    [TestMethod]
    public async Task SinConexionAlPedirElToken_EsUpstreamUnavailable()
    {
        var pp = new FakePayPal().On(FakePayPal.TokenRoute, _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "No se pudo conectar"));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await Checkout(host);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code(body));
    }

    [TestMethod]
    public async Task CorteDespuesDeEnviar_EsIncierto_SoloSiElPedidoCambiaAlgo()
    {
        Func<Sent, HttpResponseMessage> cut = _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
        var pp = new FakePayPal().On(Read, cut).On(Write, cut);
        await using var host = await PpHost.Start(pp);

        var read = await GetPayment(host);
        var write = await Checkout(host);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, read.Status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code(read.Body));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, write.Status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PpHost.Code(write.Body));
        Assert.AreEqual("no_response", write.Body!["error"]!["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PlazoVencido_SeTrataComoUnCorteDespuesDeEnviar()
    {
        Func<Sent, HttpResponseMessage> timeout = _ => throw new TaskCanceledException("Se agotó el tiempo", new TimeoutException());
        var pp = new FakePayPal().On(Read, timeout).On(Write, timeout);
        await using var host = await PpHost.Start(pp);

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code((await GetPayment(host)).Body));
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PpHost.Code((await Checkout(host)).Body));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, "<html>no es json</html>")]
    [DataRow(HttpStatusCode.Found, "")]
    public async Task RespuestaQueNoSePuedeLeer_EsIncierta_SoloSiElPedidoCambiaAlgo(HttpStatusCode upstream, string text)
    {
        var pp = new FakePayPal().On(Read, text, upstream).On(Write, text, upstream);
        await using var host = await PpHost.Start(pp);

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PpHost.Code((await GetPayment(host)).Body));
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PpHost.Code((await Checkout(host)).Body));
    }

    [TestMethod]
    public async Task ElSecretYElTokenNoSeFiltran_AunquePayPalLosRepitaEnElError()
    {
        var pp = new FakePayPal();
        pp.On(Read, _ =>
        {
            var echo = $"el token {pp.AccessToken} y el secret {PpHost.ClientSecret} no sirven";
            return Samples.Json(Samples.Error("INVALID_REQUEST", echo, ("INVALID_PARAMETER_VALUE", echo)), HttpStatusCode.BadRequest);
        });
        await using var host = await PpHost.Start(pp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.DoesNotContain(pp.AccessToken, body!.ToJsonString());
        Assert.DoesNotContain(PpHost.ClientSecret, body.ToJsonString());
        Assert.Contains("***", body.ToJsonString());
    }

    [TestMethod]
    public async Task ErrorSinCuerpoUtil_EsRejectedConElCodigoHttp()
    {
        var pp = new FakePayPal().On(Read, string.Empty, HttpStatusCode.BadRequest);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PpHost.Code(body));
        Assert.AreEqual("400", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ConfiguracionConUnTipoMalo_EsInvalidConfig()
    {
        await using var host = await PpHost.Start(new FakePayPal());
        var config = PpHost.Config();
        config["client_secret"] = 12345;

        var (status, body) = await GetPayment(host, config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> GetPayment(PpHost host, JsonObject? config = null) =>
        host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = Samples.CaptureId }, config: config);

    private static Task<(HttpStatusCode Status, JsonNode? Body)> Checkout(PpHost host) =>
        host.Send(
            "/v0/payment/create_checkout",
            new JsonObject
            {
                ["attempt_id"] = "att-1",
                ["amount"] = new JsonObject { ["value"] = "10.00", ["currency"] = "USD" },
                ["items"] = new JsonArray(new JsonObject { ["title"] = "Servicio", ["quantity"] = 1, ["unit_price"] = "10.00" }),
                ["return_urls"] = new JsonObject { ["success"] = "https://facturacion.example.com/pay/return/att-1" },
            },
            Guid.NewGuid().ToString());
}
