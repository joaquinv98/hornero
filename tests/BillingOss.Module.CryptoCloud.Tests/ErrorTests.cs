using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.CryptoCloud.Tests;

/// <summary>
/// Cómo los errores de CryptoCloud salen como errores del protocolo (sección 10). Las lecturas usan get_payment y
/// los pedidos que cambian algo usan create_checkout.
/// </summary>
[TestClass]
public sealed class ErrorTests
{
    private const string Read = "POST /v2/invoice/merchant/info";
    private const string Write = "POST /v2/invoice/create";

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task ApiKeyRechazada_EsInvalidConfig_YNoSeReintenta(HttpStatusCode upstream)
    {
        var cc = new FakeCryptoCloud().On(Read, Samples.Unauthorized, upstream).On(Write, Samples.Unauthorized, upstream);
        await using var host = await CcHost.Start(cc);

        foreach (var (status, body) in new[] { await GetPayment(host), await Checkout(host) })
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
            Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
            Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
            Assert.AreEqual("api_key", body["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        }
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.OK)]
    public async Task ErrorDeAutorizacionConOtroCodigoHttp_EsInvalidConfigDeLaApiKey(HttpStatusCode upstream)
    {
        // La documentación muestra el error de autorización pero no dice con qué código HTTP llega.
        var cc = new FakeCryptoCloud().On(Read, Samples.Unauthorized, upstream).On(Write, Samples.Unauthorized, upstream);
        await using var host = await CcHost.Start(cc);

        foreach (var (status, body) in new[] { await GetPayment(host), await Checkout(host) })
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
            Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
            CollectionAssert.AreEqual(new[] { "api_key" }, CcHost.Strings(body!["error"]!["details"]!["fields"]));
        }
    }

    [TestMethod]
    [DataRow(42, 42)]
    [DataRow(0, 30)]
    public async Task LimiteDePedidos_EsRateLimitedConElRetryAfterDeCryptoCloud(int header, int expected)
    {
        var cc = new FakeCryptoCloud().On(Read, _ =>
        {
            var response = Samples.Json("""{ "detail": "Request was throttled." }""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(header));
            return response;
        });
        await using var host = await CcHost.Start(cc);

        var (status, body) = await GetPayment(host);

        // Un Retry-After de cero no sirve de nada: se espera lo de siempre.
        Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
        Assert.AreEqual(ErrorCodes.RateLimited, CcHost.Code(body));
        Assert.AreEqual(expected, body!["error"]!["retry_after"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task ErroresDelServidor_SonUpstreamUnavailable_EnLecturas_EInciertosAlCrear(HttpStatusCode upstream)
    {
        var cc = new FakeCryptoCloud().On(Read, "<html>Service Unavailable</html>", upstream).On(Write, "{}", upstream);
        await using var host = await CcHost.Start(cc);

        var read = await GetPayment(host);
        var write = await Checkout(host);

        // Un 5xx no prueba que la factura no se creó: puede venir de un proxy.
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, read.Status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code(read.Body));
        Assert.IsTrue(read.Body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, write.Status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, CcHost.Code(write.Body));
        Assert.AreEqual("upstream_5xx", write.Body!["error"]!["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task NotFound_EsNotFound()
    {
        var cc = new FakeCryptoCloud().On(Read, """{ "status": "error", "result": { "uuids": "Invoice not found" } }""", HttpStatusCode.NotFound);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, CcHost.Code(body));
        Assert.AreEqual("uuids", body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinConexionAntesDeEnviar_EsUpstreamUnavailable_EnLecturasYEscrituras()
    {
        Func<Sent, HttpResponseMessage> noConnection = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No se resolvió el host");
        var cc = new FakeCryptoCloud().On(Read, noConnection).On(Write, noConnection);
        await using var host = await CcHost.Start(cc);

        foreach (var (status, body) in new[] { await GetPayment(host), await Checkout(host) })
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code(body));
        }
    }

    [TestMethod]
    public async Task CorteDespuesDeEnviar_EsIncierto_SoloSiElPedidoCambiaAlgo()
    {
        Func<Sent, HttpResponseMessage> cut = _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
        var cc = new FakeCryptoCloud().On(Read, cut).On(Write, cut);
        await using var host = await CcHost.Start(cc);

        var read = await GetPayment(host);
        var write = await Checkout(host);

        // Una consulta se repite sin riesgo. Una factura pudo haberse creado.
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, read.Status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code(read.Body));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, write.Status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, CcHost.Code(write.Body));
        Assert.AreEqual("no_response", write.Body!["error"]!["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PlazoVencido_SeTrataComoUnCorteDespuesDeEnviar()
    {
        Func<Sent, HttpResponseMessage> timeout = _ => throw new TaskCanceledException("Se agotó el tiempo", new TimeoutException());
        var cc = new FakeCryptoCloud().On(Read, timeout).On(Write, timeout);
        await using var host = await CcHost.Start(cc);

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code((await GetPayment(host)).Body));
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, CcHost.Code((await Checkout(host)).Body));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, "<html>no es json</html>")]
    [DataRow(HttpStatusCode.OK, "")]
    [DataRow(HttpStatusCode.Found, "")]
    [DataRow(HttpStatusCode.TemporaryRedirect, "")]
    public async Task RespuestaQueNoSePuedeLeer_EsIncierta_SoloSiElPedidoCambiaAlgo(HttpStatusCode upstream, string text)
    {
        var cc = new FakeCryptoCloud().On(Read, text, upstream).On(Write, text, upstream);
        await using var host = await CcHost.Start(cc);

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code((await GetPayment(host)).Body));
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, CcHost.Code((await Checkout(host)).Body));
    }

    [TestMethod]
    public async Task StatusErrorConHttp200_EsRejected()
    {
        var cc = new FakeCryptoCloud().On(Write, Samples.Error("currency", "Currency is not supported"));
        await using var host = await CcHost.Start(cc);

        var (status, body) = await Checkout(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, CcHost.Code(body));
        Assert.AreEqual(200, body!["error"]!["details"]!["http_status"]!.GetValue<int>());
        Assert.AreEqual("Currency is not supported", body["error"]!["details"]!["upstream"]![0]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("""{ "status": "error", "result": "Bad request" }""", "error", "Bad request")]
    [DataRow("""{ "detail": "Invalid input." }""", "detail", "Invalid input.")]
    [DataRow("", "400", "")]
    public async Task ErroresConOtraForma_SeLeenIgual(string text, string code, string message)
    {
        var cc = new FakeCryptoCloud().On(Read, text, HttpStatusCode.BadRequest);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await GetPayment(host);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, CcHost.Code(body));
        Assert.AreEqual(code, body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual(message, body["error"]!["details"]!["upstream"]![0]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task LasClavesNoSeFiltran_AunqueCryptoCloudLasRepitaEnElError()
    {
        var echo = $"la clave {CcHost.FakeApiKey} no sirve";
        var cc = new FakeCryptoCloud()
            .On(Read, Samples.Error("api_key", echo), HttpStatusCode.BadRequest)
            .On(Write, Samples.Error("authorization", echo), HttpStatusCode.Unauthorized);
        await using var host = await CcHost.Start(cc);

        var rejected = await GetPayment(host);
        var unauthorized = await Checkout(host);

        foreach (var (status, body) in new[] { rejected, unauthorized })
        {
            Assert.AreNotEqual(HttpStatusCode.OK, status);
            Assert.DoesNotContain(CcHost.FakeApiKey, body!.ToJsonString());
            Assert.DoesNotContain(CcHost.FakeSecretKey, body.ToJsonString());
        }

        Assert.Contains("***", rejected.Body!.ToJsonString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("con espacio")]
    [DataRow("con\nsalto")]
    public async Task ApiKeyQueNoSirve_EsInvalidConfig_SinSalirALaRed(string key)
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);
        var config = CcHost.Config();
        config["api_key"] = key;

        var (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "INV-89UX09KA" }, config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
        Assert.IsEmpty(cc.Requests);
    }

    [TestMethod]
    public async Task ConfiguracionConUnTipoMalo_EsInvalidConfig()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var config = CcHost.Config();
        config["invoice_hours"] = "muchas";

        var (status, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "INV-89UX09KA" }, config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> GetPayment(CcHost host) =>
        host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "INV-89UX09KA" });

    private static Task<(HttpStatusCode Status, JsonNode? Body)> Checkout(CcHost host) =>
        host.Send(
            "/v0/payment/create_checkout",
            new JsonObject { ["attempt_id"] = "att-1", ["amount"] = new JsonObject { ["value"] = "10.00", ["currency"] = "USD" } },
            Guid.NewGuid().ToString());
}
