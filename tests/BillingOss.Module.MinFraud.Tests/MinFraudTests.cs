using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.MinFraud.Tests;

/// <summary>Un pedido que el módulo mandó al MaxMind falso.</summary>
internal sealed record Sent(Uri Url, string? Authorization, JsonNode Body);

/// <summary>MaxMind falso: contesta lo que fija cada prueba y anota lo que recibe. Nunca sale a la red.</summary>
internal sealed class FakeMaxMind : HttpMessageHandler
{
    private readonly List<Sent> requests = [];

    public Func<Sent, HttpResponseMessage> Respond { get; set; } = _ => Json(Samples.Factors);

    public IReadOnlyList<Sent> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = new Sent(request.RequestUri!, request.Headers.Authorization?.ToString(), JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
        lock (requests)
        {
            requests.Add(sent);
        }

        return Respond(sent);
    }
}

/// <summary>El módulo en un TestServer, firmado como lo firma el núcleo.</summary>
internal sealed class MinFraudHost : IAsyncDisposable
{
    public const string FakeLicenseKey = "licencia-inventada-de-prueba";

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-prueba");

    private readonly WebApplication app;
    private readonly string db;

    private MinFraudHost(WebApplication app, string db, HttpClient client)
    {
        this.app = app;
        this.db = db;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<MinFraudHost> Start(HttpMessageHandler maxmind, ILogger? logger = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"minfraud-idem-{Guid.NewGuid():N}.db");
        app.MapModule(MinFraudModule.Create(new HttpClient(maxmind), logger ?? NullLogger.Instance), new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new MinFraudHost(app, db, app.GetTestClient());
    }

    public static JsonObject Config(string service = "factors") => new()
    {
        ["account_id"] = "123456",
        ["license_key"] = FakeLicenseKey,
        ["service"] = service,
    };

    public async Task<(HttpStatusCode Status, JsonNode Body)> Send(string path, JsonObject input, JsonObject? context = null, string? key = null, JsonObject? config = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = (config ?? Config()).DeepClone() },
            ["context"] = (context ?? []).DeepClone(),
            ["input"] = input.DeepClone(),
        };
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new ByteArrayContent(bytes) };
        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        if (key is not null)
        {
            request.Headers.Add(ModuleHeaders.IdempotencyKey, key);
        }

        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Secret, timestamp, "POST", path, requestId, key, bytes));
        using var response = await Client.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    public Task<(HttpStatusCode Status, JsonNode Body)> Evaluate(JsonObject? context = null, string key = "order-1042-evaluate", JsonObject? config = null) =>
        Send("/v0/fraud/evaluate", Samples.Input(), context, key, config);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}

/// <summary>Respuestas sintéticas con la forma de la documentación pública de minFraud v2.0.</summary>
internal static class Samples
{
    public const string Factors = """
        {
          "id": "5bc5d6c2-b2c8-40af-87f4-6d61af86b6ae",
          "risk_score": 37.42,
          "funds_remaining": 25.00,
          "queries_remaining": 5000,
          "ip_address": {
            "risk": 12.5,
            "risk_reasons": [{ "code": "ANONYMOUS_IP", "reason": "The IP address belongs to an anonymous network." }],
            "traits": { "is_anonymous": true }
          },
          "email": { "is_disposable": true, "is_high_risk": false, "is_free": true },
          "billing_address": { "is_in_ip_country": false },
          "risk_score_reasons": [
            { "multiplier": 4.5, "reasons": [{ "code": "EMAIL_DOMAIN_NEW", "reason": "Riskiness of newly-sighted email domain" }] },
            { "multiplier": 1.2, "reasons": [{ "code": "ANONYMOUS_IP", "reason": "Risk due to IP being an Anonymous IP" }] }
          ],
          "warnings": [{ "code": "BILLING_CITY_NOT_FOUND", "warning": "City not found", "input_pointer": "/billing/city" }]
        }
        """;

    public const string Score = """
        { "id": "11111111-2222-3333-4444-555555555555", "risk_score": 0.85, "funds_remaining": 10.00, "queries_remaining": 1000, "ip_address": { "risk": 31.4 } }
        """;

    public static JsonObject Input() => new()
    {
        ["order"] = new JsonObject
        {
            ["id"] = "1042",
            ["placed_at"] = "2026-10-29T15:00:00.0000000+00:00",
            ["amount"] = new JsonObject { ["value"] = "12100.00", ["currency"] = "ARS" },
            ["items"] = new JsonArray(new JsonObject { ["product_id"] = "7", ["name"] = "VPS 2 GB", ["quantity"] = 2 }),
        },
        ["request"] = new JsonObject { ["ip"] = "203.0.113.7", ["user_agent"] = "Mozilla/5.0 prueba" },
    };

    public static JsonObject Context(bool contact = false, bool address = false)
    {
        var context = new JsonObject { ["account"] = new JsonObject { ["id"] = "5", ["country"] = "AR", ["language"] = "es", ["created_at"] = "2025-01-02T03:04:05Z" } };
        if (contact)
        {
            context["contact"] = new JsonObject { ["name"] = "Cliente de prueba", ["email"] = " Cliente@Example.com ", ["phone"] = "+54 11 5555 0000" };
        }

        if (address)
        {
            context["address"] = new JsonObject { ["street"] = "Calle Falsa 123", ["city"] = "La Plata", ["province"] = "Buenos Aires", ["postal_code"] = "1900", ["country"] = "AR" };
        }

        return context;
    }
}

[TestClass]
public sealed class MinFraudTests
{
    [TestMethod]
    public void Manifiesto_DeclaraElTipoFraudYLaLicenciaComoSecreto()
    {
        var manifest = MinFraudModule.Manifest();

        CollectionAssert.AreEqual(new[] { "fraud" }, manifest["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "fraud.core" }, manifest["capabilities"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray());
        Assert.IsTrue(manifest["config_schema"]!["properties"]!["license_key"]!["x-secret"]!.GetValue<bool>());
        Assert.AreEqual(15, manifest["timeouts"]!["evaluate"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Evaluar_ConFactors_DevuelvePuntajeYMotivos()
    {
        var maxmind = new FakeMaxMind();
        await using var host = await MinFraudHost.Start(maxmind);

        var (status, body) = await host.Evaluate(Samples.Context());

        Assert.AreEqual(HttpStatusCode.OK, status, body.ToJsonString());
        var result = body["result"]!;
        Assert.AreEqual("37.42", result["score"]!.GetValue<string>());
        Assert.AreEqual("5bc5d6c2-b2c8-40af-87f4-6d61af86b6ae", result["external_id"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { "minfraud.email_domain_new", "minfraud.anonymous_ip", "ip.anonymous_ip", "ip.anonymous", "email.disposable", "billing.not_in_ip_country" },
            result["reasons"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()).ToArray());

        var sent = maxmind.Requests.Single();
        Assert.AreEqual("https://minfraud.maxmind.com/minfraud/v2.0/factors", sent.Url.ToString());
        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("123456:" + MinFraudHost.FakeLicenseKey)), sent.Authorization);
        Assert.AreEqual("203.0.113.7", sent.Body["device"]!["ip_address"]!.GetValue<string>());
        Assert.AreEqual("1042", sent.Body["event"]!["transaction_id"]!.GetValue<string>());
        Assert.AreEqual("purchase", sent.Body["event"]!["type"]!.GetValue<string>());
        Assert.AreEqual(12100.00m, sent.Body["order"]!["amount"]!.GetValue<decimal>());
        Assert.AreEqual("ARS", sent.Body["order"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("7", sent.Body["shopping_cart"]![0]!["item_id"]!.GetValue<string>());
        Assert.AreEqual("AR", sent.Body["billing"]!["country"]!.GetValue<string>());
        Assert.IsNull(sent.Body["email"], "Sin el permiso account.contact no viaja el correo.");
    }

    [TestMethod]
    public async Task Evaluar_ConPermisos_MandaElCorreoComoMd5YElDomicilio()
    {
        var maxmind = new FakeMaxMind();
        await using var host = await MinFraudHost.Start(maxmind);

        var (status, _) = await host.Evaluate(Samples.Context(contact: true, address: true));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var body = maxmind.Requests.Single().Body;
        Assert.AreEqual(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("cliente@example.com"))), body["email"]!["address"]!.GetValue<string>());
        Assert.AreEqual("example.com", body["email"]!["domain"]!.GetValue<string>());
        Assert.IsFalse(body.ToJsonString().Contains("Cliente@Example.com", StringComparison.OrdinalIgnoreCase), "El correo nunca sale en claro.");
        Assert.AreEqual("Calle Falsa 123", body["billing"]!["address"]!.GetValue<string>());
        Assert.AreEqual("1900", body["billing"]!["postal"]!.GetValue<string>());
        Assert.AreEqual("+54 11 5555 0000", body["billing"]!["phone_number"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Evaluar_ConScore_SoloTraeElRiesgoDeLaIp_YElSandboxUsaSuHost()
    {
        var maxmind = new FakeMaxMind { Respond = _ => FakeMaxMind.Json(Samples.Score) };
        await using var host = await MinFraudHost.Start(maxmind);
        var config = MinFraudHost.Config("score");
        config["environment"] = "sandbox";

        var (status, body) = await host.Evaluate(Samples.Context(), config: config);

        Assert.AreEqual(HttpStatusCode.OK, status, body.ToJsonString());
        Assert.AreEqual("0.85", body["result"]!["score"]!.GetValue<string>());
        Assert.AreEqual("ip.risk", body["result"]!["reasons"]![0]!["code"]!.GetValue<string>());
        Assert.AreEqual("https://sandbox.maxmind.com/minfraud/v2.0/score", maxmind.Requests.Single().Url.ToString());
    }

    [TestMethod]
    public async Task Evaluar_LaMismaClaveDosVeces_ConsultaUnaSolaVez()
    {
        var maxmind = new FakeMaxMind();
        await using var host = await MinFraudHost.Start(maxmind);

        var first = await host.Evaluate(Samples.Context());
        var second = await host.Evaluate(Samples.Context());

        Assert.AreEqual(HttpStatusCode.OK, second.Status);
        Assert.AreEqual(first.Body.ToJsonString(), second.Body.ToJsonString());
        Assert.HasCount(1, maxmind.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, """{"code":"AUTHORIZATION_INVALID","error":"Invalid license key"}""", "invalid_config", false)]
    [DataRow(HttpStatusCode.Forbidden, """{"code":"PERMISSION_REQUIRED","error":"No permission"}""", "invalid_config", false)]
    [DataRow(HttpStatusCode.PaymentRequired, """{"code":"INSUFFICIENT_FUNDS","error":"Out of funds"}""", "rejected", false)]
    [DataRow(HttpStatusCode.BadRequest, """{"code":"IP_ADDRESS_INVALID","error":"Bad IP"}""", "invalid_input", false)]
    [DataRow(HttpStatusCode.TooManyRequests, "{}", "rate_limited", true)]
    [DataRow(HttpStatusCode.InternalServerError, "no es json", "upstream_unavailable", true)]
    public async Task Errores_DeMaxMind_SeTraducenAlProtocolo(HttpStatusCode upstream, string json, string code, bool retryable)
    {
        await using var host = await MinFraudHost.Start(new FakeMaxMind { Respond = _ => FakeMaxMind.Json(json, upstream) });

        var (_, body) = await host.Evaluate(Samples.Context());

        Assert.AreEqual(code, body["error"]!["code"]!.GetValue<string>(), body.ToJsonString());
        Assert.AreEqual(retryable, body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task SinRespuesta_EsReintentable_YLaLicenciaNoAparece()
    {
        var logs = new ListLogger();
        var maxmind = new FakeMaxMind { Respond = _ => throw new HttpRequestException("se cortó la conexión con " + MinFraudHost.FakeLicenseKey) };
        await using var host = await MinFraudHost.Start(maxmind, logs);

        var (status, body) = await host.Evaluate(Samples.Context());
        maxmind.Respond = _ => FakeMaxMind.Json($$"""{"code":"AUTHORIZATION_INVALID","error":"key {{MinFraudHost.FakeLicenseKey}} is invalid"}""", HttpStatusCode.Unauthorized);
        var (_, rejected) = await host.Evaluate(Samples.Context(), key: "order-1043-evaluate");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual("upstream_unavailable", body["error"]!["code"]!.GetValue<string>());
        Assert.IsFalse(body.ToJsonString().Contains(MinFraudHost.FakeLicenseKey, StringComparison.Ordinal));
        Assert.IsFalse(rejected.ToJsonString().Contains(MinFraudHost.FakeLicenseKey, StringComparison.Ordinal));
        Assert.IsFalse(logs.Lines.Any(l => l.Contains(MinFraudHost.FakeLicenseKey, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Configuracion_Invalida_EsInvalidConfigConLosCampos()
    {
        var maxmind = new FakeMaxMind();
        await using var host = await MinFraudHost.Start(maxmind);
        var config = new JsonObject { ["account_id"] = "abc", ["license_key"] = "con espacio", ["service"] = "otro" };

        var (status, body) = await host.Evaluate(Samples.Context(), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("invalid_config", body["error"]!["code"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(new[] { "account_id", "license_key", "service" }, body["error"]!["details"]!["fields"]!.AsArray().Select(f => f!.GetValue<string>()).ToArray());
        Assert.IsEmpty(maxmind.Requests);
    }

    [TestMethod]
    public async Task ProbarLaInstancia_HaceUnaConsultaDeScore()
    {
        var maxmind = new FakeMaxMind { Respond = _ => FakeMaxMind.Json(Samples.Score) };
        await using var host = await MinFraudHost.Start(maxmind);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status, body.ToJsonString());
        Assert.AreEqual(1000, body["result"]!["queries_remaining"]!.GetValue<int>());
        Assert.AreEqual(MinFraudModule.TestIp, maxmind.Requests.Single().Body["device"]!["ip_address"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EvaluarSinClave_SeRechaza()
    {
        await using var host = await MinFraudHost.Start(new FakeMaxMind());

        var (_, body) = await host.Send("/v0/fraud/evaluate", Samples.Input(), Samples.Context());

        Assert.AreEqual("invalid_input", body["error"]!["code"]!.GetValue<string>());
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception) + exception);
            }
        }
    }
}
