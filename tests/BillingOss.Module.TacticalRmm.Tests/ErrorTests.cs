using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.TacticalRmm.Tests;

/// <summary>Errores del protocolo (sección 10) y escenarios 2, 3, 5, 6 y 7 del kit de conformidad.</summary>
[TestClass]
public sealed class ErrorTests
{
    [TestMethod]
    [DataRow("GET /clients/", 500, "upstream_unavailable", 503)]
    [DataRow("GET /clients/", 502, "upstream_unavailable", 503)]
    [DataRow("GET /clients/", 404, "invalid_config", 422)]
    [DataRow("POST /clients/", 500, "outcome_unknown", 504)]
    [DataRow("POST /clients/", 429, "rate_limited", 429)]
    public async Task RespuestaHttpDeTactical_SeTraduceAlCodigoDelProtocolo(string route, int upstream, string code, int http)
    {
        var fake = new FakeTactical().On(route, "<html>error</html>", (HttpStatusCode)upstream, "text/html");
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(http, (int)status, body?.ToJsonString());
        Assert.AreEqual(code, TrmmHost.Code(body));
        Assert.AreEqual(ErrorCodes.IsRetryable(code), body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.DoesNotContain("<html>", body.ToJsonString());
    }

    [TestMethod]
    public async Task RateLimited_UsaElRetryAfterDeTactical()
    {
        var fake = new FakeTactical().On("GET /clients/", _ =>
        {
            var response = FakeTactical.Respond("""{"detail":"Request was throttled."}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(ErrorCodes.RateLimited, TrmmHost.Code(body));
        Assert.AreEqual(42, body!["error"]!["retry_after"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow(HttpRequestError.ConnectionError, "create", "upstream_unavailable")]
    [DataRow(HttpRequestError.NameResolutionError, "create", "upstream_unavailable")]
    [DataRow(HttpRequestError.ResponseEnded, "create", "outcome_unknown")]
    [DataRow(HttpRequestError.ResponseEnded, "get_status", "upstream_unavailable")]
    public async Task SinRespuesta_SegunSiElPedidoPudoSalir(HttpRequestError error, string operation, string code)
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1");
        var route = operation == "create" ? "POST /clients/" : $"GET /clients/{client.Id}/";
        fake.On(route, _ => throw new HttpRequestException(error, "sin respuesta"));
        await using var host = await TrmmHost.Start(fake);
        var service = operation == "create" ? TrmmHost.Service() : TrmmHost.Service(client: client);

        var (_, body) = await host.Op(operation, service, key: $"svc-1042-{operation}-1");

        Assert.AreEqual(code, TrmmHost.Code(body));
    }

    [TestMethod]
    public async Task PlazoVencido_EnUnPedidoQueCambiaAlgo_EsOutcomeUnknown()
    {
        var fake = new FakeTactical().On("POST /clients/", _ => throw new TaskCanceledException("plazo", new TimeoutException()));
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, TrmmHost.Code(body));
    }

    [TestMethod]
    public async Task UnOkSinJson_NoSeTomaComoExito()
    {
        var fake = new FakeTactical().On("POST /clients/", "<html>portal</html>", HttpStatusCode.OK, "text/html");
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, TrmmHost.Code(body));
    }

    [TestMethod]
    public async Task UnaRedireccion_NoSeSigue()
    {
        var fake = new FakeTactical().On("GET /clients/", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://otro.example.test/clients/");
            return response;
        });
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, TrmmHost.Code(body));
        Assert.HasCount(1, fake.Requests);
    }

    [TestMethod]
    public async Task UnRechazoLargo_NoPasaDe500Caracteres_NiArrastraLaKey()
    {
        var fake = new FakeTactical();
        var text = new string('x', 3000);
        fake.On("POST /clients/", $$"""{"name":["{{text}}"],"key":["{{fake.ApiKey}}"]}""", HttpStatusCode.BadRequest);
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(ErrorCodes.Rejected, TrmmHost.Code(body));
        Assert.IsLessThanOrEqualTo(500, body!["error"]!["message"]!.GetValue<string>().Length);
        Assert.DoesNotContain(fake.ApiKey, body.ToJsonString());
    }

    /// <summary>Escenario 7: un valor testigo en la key y en credenciales no aparece en respuestas, errores ni logs.</summary>
    [TestMethod]
    public async Task ValoresTestigo_NoAparecenEnRespuestasErroresNiLogs()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 2);
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);
        const string Testigo = "testigo-de-credencial-que-no-debe-salir";
        var responses = new StringBuilder();

        // El manifiesto no pide credenciales: si el núcleo igual las manda, se ignoran.
        var withCredentials = new JsonObject { ["credentials"] = new JsonObject { ["root_password"] = Testigo } };
        foreach (var operation in new[] { "validate", "suspend", "unsuspend", "get_status", "view", "terminate" })
        {
            var (_, body) = await host.Op(operation, service, key: $"svc-1042-{operation}-1", input: withCredentials.DeepClone().AsObject());
            responses.Append(body?.ToJsonString());
        }

        responses.Append((await host.Send("/v0/instance/test", [], [])).Body?.ToJsonString());
        fake.On("GET /clients/", """{"detail":"Invalid token."}""", HttpStatusCode.Unauthorized);
        responses.Append((await host.Send("/v0/instance/test", [], [])).Body?.ToJsonString());

        var all = responses + host.Logs.Text;
        Assert.DoesNotContain(fake.ApiKey, all);
        Assert.DoesNotContain(Testigo, all);
        Assert.IsFalse(fake.Requests.Any(r => r.Body?.ToJsonString().Contains(Testigo, StringComparison.Ordinal) == true));
        Assert.IsFalse(fake.Requests.Any(r => r.Url.AbsolutePath.StartsWith("/accounts/", StringComparison.Ordinal)));
    }

    /// <summary>Escenario 2: firma inválida y reloj corrido.</summary>
    [TestMethod]
    public async Task FirmaInvalidaORelojCorrido_Es401_SinLlamarATactical()
    {
        await using var host = await TrmmHost.Start(new FakeTactical());
        var body = Encoding.UTF8.GetBytes("""{"instance":{"id":"i","config":{}},"context":{},"input":{}}""");

        foreach (var (secret, skew) in new[] { (new byte[] { 1, 2, 3 }, 0L), (TrmmHost.Secret, 600L) })
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - skew;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v0/provisioning/create") { Content = new ByteArrayContent(body) };
            request.Headers.Add(ModuleHeaders.RequestId, "r-1");
            request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(ModuleHeaders.IdempotencyKey, "k-1");
            request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(secret, timestamp, "POST", "/v0/provisioning/create", "r-1", "k-1", body));

            using var response = await host.Client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.AreEqual(ErrorCodes.InvalidSignature, TrmmHost.Code(JsonNode.Parse(await response.Content.ReadAsStringAsync())));
        }

        Assert.IsEmpty(host.Fake.Requests);
    }

    /// <summary>Escenario 3: misma clave con otra entrada. Que el registro sobreviva a un reinicio lo prueba la SDK.</summary>
    [TestMethod]
    public async Task MismaClaveConOtraEntrada_EsIdempotencyConflict()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);

        var (status, _) = await host.Op("run_action", service, key: "k-1", input: new JsonObject { ["action"] = "installer_link" });
        Assert.AreEqual(HttpStatusCode.OK, status);

        var (conflict, body) = await host.Op("run_action", service, key: "k-1", input: new JsonObject { ["action"] = "installer_link", ["params"] = new JsonObject { ["agent_type"] = "server" } });
        Assert.AreEqual(HttpStatusCode.Conflict, conflict);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, TrmmHost.Code(body));
        Assert.HasCount(1, fake.Deployments);
    }

    [TestMethod]
    public async Task OperacionQueCambiaAlgo_SinIdempotencyKey_EsInvalidInput()
    {
        var fake = new FakeTactical();
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("create", TrmmHost.Service());

        Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body));
        Assert.IsEmpty(fake.Requests);
    }

    /// <summary>Escenario 6: campos desconocidos en el sobre, la configuración, el contexto y las respuestas de Tactical.</summary>
    [TestMethod]
    public async Task CamposDesconocidos_SeIgnoran()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);
        var config = host.Config();
        config["campo_nuevo"] = "x";
        var service = TrmmHost.Service(client: client);
        service["otro"] = new JsonObject { ["a"] = 1 };
        service["service"]!["external_ids"]!["role_id"] = "12";
        service["service"]!["external_ids"]!["user_id"] = "13";

        var (status, body) = await host.Op("get_status", service, config: config);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
    }

    [TestMethod]
    public async Task OperacionNoDeclarada_EsNotSupported()
    {
        await using var host = await TrmmHost.Start(new FakeTactical());

        var (status, body) = await host.Op("sso", TrmmHost.Service(), key: "k-1");

        Assert.AreEqual(HttpStatusCode.NotImplemented, status);
        Assert.AreEqual(ErrorCodes.NotSupported, TrmmHost.Code(body));
    }
}
