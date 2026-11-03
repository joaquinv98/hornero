using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>
/// Cómo cada respuesta del panel sale como error del protocolo (PTR-14 y sección 10). Las lecturas usan get_status
/// y las escrituras, suspend sobre un server existente.
/// </summary>
[TestClass]
public sealed class ErrorTests
{
    private const string Read = "GET servers/external/1042";

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task ClaveRechazada_EsInvalidConfig_NoReintentable(HttpStatusCode upstream)
    {
        var panel = FakePanel.Standard();
        panel.On(Read, upstream, "{}");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PteroHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual("application_key", body["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(42, 42)]
    [DataRow(0, 60)]
    public async Task LimiteDePedidos_EsRateLimited_ConElRetryAfterDelPanel(int header, int expected)
    {
        var panel = FakePanel.Standard();
        panel.On(Read, (_, _) =>
        {
            var response = Samples.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(header));
            return response;
        });
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
        Assert.AreEqual(ErrorCodes.RateLimited, PteroHost.Code(body));
        Assert.AreEqual(expected, body!["error"]!["retry_after"]!.GetValue<int>());
        Assert.IsTrue(body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.GatewayTimeout)]
    public async Task ErrorDelServidorEnUnaLectura_EsUpstreamUnavailable(HttpStatusCode upstream)
    {
        var panel = FakePanel.Standard();
        panel.On(Read, upstream, "<html>error</html>");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(body));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadGateway, ErrorCodes.OutcomeUnknown)]
    [DataRow(HttpStatusCode.GatewayTimeout, ErrorCodes.OutcomeUnknown)]
    [DataRow(HttpStatusCode.InternalServerError, ErrorCodes.UpstreamUnavailable)]
    [DataRow(HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    public async Task ErrorDelServidorEnUnaEscritura_EsInciertoSiLoDioElProxy(HttpStatusCode upstream, string expected)
    {
        var (status, body) = await Suspend((panel, id) => panel.On($"POST servers/{id}/suspend", upstream, "<html>error</html>"));

        Assert.AreEqual(expected, PteroHost.Code(body));
        Assert.AreEqual(ErrorCodes.HttpStatus(expected), (int)status);
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.GatewayTimeout)]
    public async Task WingsCaido_ElPanelNoAplico_EsUpstreamUnavailable(HttpStatusCode upstream)
    {
        // Con Wings caído el panel contesta DaemonConnectionException, 504 por defecto, y revierte la suspensión.
        var (status, body) = await Suspend((panel, id) => panel.On(
            $"POST servers/{id}/suspend", upstream,
            Samples.Error(upstream, "DaemonConnectionException", "Could not establish a connection to the machine running this server. Please try again.").Content.ReadAsStringAsync().Result));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(body));
    }

    [TestMethod]
    [DataRow("<html><body>404 Not Found</body></html>")]
    [DataRow("{}")]
    public async Task Un404QueNoEsDelPanel_NuncaEsAusencia(string notFound)
    {
        // El proxy delante del panel, un vhost por defecto o la panel_url con otro prefijo contestan 404 sin el error del panel.
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        panel.On(Read, HttpStatusCode.NotFound, notFound);
        await using var host = await PteroHost.Start(panel);

        var terminate = await host.Call("terminate", Samples.Context(), "svc-1042-terminate-1");
        var status = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(terminate.Body), terminate.Body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(status.Body));
        Assert.AreEqual(0, panel.Count($"DELETE servers/{server.Id}"));
        Assert.HasCount(1, panel.Servers);
    }

    [TestMethod]
    public async Task SinConexionAntesDeEnviar_EsUpstreamUnavailable_AunEnUnaEscritura()
    {
        var (_, body) = await Suspend((panel, id) => panel.On(
            $"POST servers/{id}/suspend", (_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "sin DNS")));

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(body));
    }

    [TestMethod]
    public async Task CorteOPlazoVencidoDespuesDeEnviar_EsOutcomeUnknownEnEscrituras()
    {
        var (_, cut) = await Suspend((panel, id) => panel.CutAfter($"POST servers/{id}/suspend"));
        var (_, timeout) = await Suspend((panel, id) => panel.On(
            $"POST servers/{id}/suspend", (_, _) => throw new TaskCanceledException("plazo", new TimeoutException())));

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PteroHost.Code(cut));
        Assert.AreEqual("no_response", cut!["error"]!["details"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PteroHost.Code(timeout));
    }

    [TestMethod]
    public async Task CorteEnUnaLectura_EsUpstreamUnavailable()
    {
        var panel = FakePanel.Standard();
        panel.On(Read, (_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded, "cortada"));
        await using var host = await PteroHost.Start(panel);

        var (_, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(body));
    }

    [TestMethod]
    public async Task RespuestaQueNoEsJson_EsInternal_Reintentable()
    {
        var panel = FakePanel.Standard();
        panel.On(Read, HttpStatusCode.OK, "<html>mantenimiento</html>");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(HttpStatusCode.InternalServerError, status);
        Assert.AreEqual(ErrorCodes.Internal, PteroHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Redireccion_EsInvalidConfigDeLaUrl_YNoSeSigue()
    {
        var panel = FakePanel.Standard();
        panel.On(Read, (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://otro.example.test/login");
            return response;
        });
        await using var host = await PteroHost.Start(panel);

        var (_, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(ErrorCodes.InvalidConfig, PteroHost.Code(body));
        Assert.AreEqual("panel_url", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.HasCount(1, panel.Requests);
    }

    [TestMethod]
    public async Task ConflictoDelPanel_EsConflictReintentable()
    {
        var (status, body) = await Suspend((panel, id) => panel.On(
            $"POST servers/{id}/suspend", HttpStatusCode.Conflict,
            Samples.Error(HttpStatusCode.Conflict, "ConflictHttpException", "Cannot toggle suspension status on a server that is currently being transferred.").Content.ReadAsStringAsync().Result));

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.Conflict, PteroHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ErrorDefinitivo_SeGuardaConLaClave_YElIncierto_No()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        panel.On($"POST servers/{server.Id}/suspend", HttpStatusCode.BadRequest, "{}", times: 1);
        panel.On($"POST servers/{server.Id}/unsuspend", HttpStatusCode.BadGateway, "{}", times: 1);
        await using var host = await PteroHost.Start(panel);

        var rejected = await host.Call("suspend", Samples.Context(), "k-suspend");
        var again = await host.Call("suspend", Samples.Context(), "k-suspend");
        server.Status = "suspended";
        await host.Call("unsuspend", Samples.Context(), "k-unsuspend");
        var retried = await host.Call("unsuspend", Samples.Context(), "k-unsuspend");

        // El rechazo se repite igual sin volver al panel. El incierto se vuelve a ejecutar y converge.
        Assert.AreEqual(ErrorCodes.Rejected, PteroHost.Code(rejected.Body));
        Assert.AreEqual(rejected.Body!.ToJsonString(), again.Body!.ToJsonString());
        Assert.AreEqual(1, panel.Count($"POST servers/{server.Id}/suspend"));
        Assert.AreEqual("active", retried.Body!["result"]!["observed_state"]!.GetValue<string>());
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Suspend(Action<FakePanel, int> setup)
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        setup(panel, server.Id);
        await using var host = await PteroHost.Start(panel);
        return await host.Call("suspend", Samples.Context(), "svc-1042-suspend-1");
    }
}
