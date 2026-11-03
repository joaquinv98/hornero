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
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>Un pedido que el módulo mandó al Stripe falso, con el formulario ya leído.</summary>
internal sealed record Sent(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, Dictionary<string, StringValues> Form)
{
    /// <summary>La ruta que usan los tests para dar respuestas: método y path, sin la query.</summary>
    public string Route => $"{Method} {Url.AbsolutePath}";

    public StringValues Query(string name) => QueryHelpers.ParseQuery(Url.Query)[name];

    public string? Field(string name) => Form.TryGetValue(name, out var value) ? value.ToString() : null;
}

/// <summary>Stripe falso: contesta con las respuestas sintéticas que arma cada test y anota lo que recibe.</summary>
internal sealed class FakeStripe : HttpMessageHandler
{
    private readonly Dictionary<string, Func<Sent, HttpResponseMessage>> routes = [];
    private readonly List<Sent> requests = [];

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

    public FakeStripe On(string route, Func<Sent, HttpResponseMessage> respond)
    {
        routes[route] = respond;
        return this;
    }

    public FakeStripe On(string route, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(route, _ => Samples.Json(json, status));

    /// <summary>Los listados que la prueba de la instancia pide para comprobar los permisos de lectura: todos vacíos.</summary>
    public FakeStripe WithReads()
    {
        foreach (var (path, _) in StripeOperations.Reads)
        {
            On($"GET {path}", Samples.EmptyList);
        }

        return this;
    }

    public int Count(string route) => Requests.Count(r => r.Route == route);

    public Sent Single(string route) => Requests.Single(r => r.Route == route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new Sent(request.Method, request.RequestUri!, headers, QueryHelpers.ParseQuery(text));
        lock (requests)
        {
            requests.Add(sent);
        }

        return routes.TryGetValue(sent.Route, out var respond)
            ? respond(sent)
            : throw new InvalidOperationException($"El Stripe falso no tiene respuesta para {sent.Route}.");
    }
}

/// <summary>Reloj fijo para el módulo.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>El módulo corriendo en un TestServer, con el Stripe falso del otro lado. Firma los pedidos como el núcleo.</summary>
internal sealed class StripeHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-prueba");

    /// <summary>Valores inventados con la forma de las de Stripe: ninguno sirve contra Stripe.</summary>
    public const string FakeApiKey = "rk_test_inventadaParaPruebas000";

    public const string FakeWebhookSecret = "whsec_inventadoParaPruebas000";

    public static readonly DateTimeOffset Now = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);

    private readonly WebApplication app;
    private readonly string db;

    private StripeHost(WebApplication app, string db, FakeStripe stripe)
    {
        this.app = app;
        this.db = db;
        Stripe = stripe;
        Client = app.GetTestClient();
    }

    public FakeStripe Stripe { get; }

    public HttpClient Client { get; }

    public static async Task<StripeHost> Start(FakeStripe stripe, HttpClient? http = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"stripe-idem-{Guid.NewGuid():N}.db");
        app.MapModule(
            StripeModule.Create(http ?? new HttpClient(stripe), NullLogger.Instance, new FixedClock(Now)),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new StripeHost(app, db, stripe);
    }

    public static JsonObject Config() => new() { ["api_key"] = FakeApiKey, ["webhook_secret"] = FakeWebhookSecret };

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject input, string? key = null, JsonObject? config = null, JsonObject? context = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = (config ?? Config()).DeepClone() },
            ["context"] = context?.DeepClone() ?? new JsonObject(),
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
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    /// <summary>GET firmado, para el manifiesto.</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Secret, timestamp, "GET", path, requestId, null, []));
        using var response = await Client.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
    }

    public static string? Code(JsonNode? body) => body?["error"]?["code"]?.GetValue<string>();

    public static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    /// <summary>Firma un aviso como Stripe: HMAC-SHA256 de "t.cuerpo" con el secreto del destino.</summary>
    public static string Sign(string body, long t, string secret = FakeWebhookSecret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}

/// <summary>Respuestas sintéticas con la forma de la referencia de la API de Stripe.</summary>
internal static class Samples
{
    public const string SessionUrl = "https://checkout.stripe.com/c/pay/cs_test_a1B2c3D4#fidkdWxOYHwnPyd1blpxYHZxWjA0";

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static string Session(string url = SessionUrl) =>
        $$"""{ "id": "cs_test_a1B2c3D4", "object": "checkout.session", "url": "{{url}}", "status": "open", "payment_status": "unpaid" }""";

    public const string Customer = """{ "id": "cus_Prueba123", "object": "customer", "metadata": { "account_id": "77" } }""";

    /// <summary>
    /// Un PaymentIntent con su cargo y su movimiento de saldo expandidos. Lleva datos del pagador a propósito: el
    /// módulo no los tiene que devolver.
    /// </summary>
    public static string Intent(
        string id = "pi_3Prueba",
        string status = "succeeded",
        long amount = 1210000,
        long received = 1210000,
        long refunded = 0,
        string currency = "ars",
        string reference = "att-1042",
        string? setupFutureUsage = null,
        bool withFee = true,
        string feeCurrency = "ars",
        string? declineCode = null,
        bool offSession = false,
        bool livemode = false)
    {
        var fee = withFee ? $$"""{ "id": "txn_1", "object": "balance_transaction", "fee": 43560, "currency": "{{feeCurrency}}" }""" : "null";
        var error = declineCode is null ? "null" : $$"""{ "code": "card_declined", "decline_code": "{{declineCode}}", "message": "Your card was declined." }""";
        var future = setupFutureUsage is null ? "null" : $"\"{setupFutureUsage}\"";
        var charge = received > 0
            ? $$"""{ "id": "ch_3Prueba", "object": "charge", "amount_refunded": {{refunded}}, "created": 1793199670, "balance_transaction": {{fee}}, "billing_details": { "email": "pagador@example.com", "name": "Pagador de Prueba" } }"""
            : "null";
        return $$"""
            {
              "id": "{{id}}",
              "object": "payment_intent",
              "amount": {{amount}},
              "amount_received": {{received}},
              "currency": "{{currency}}",
              "status": "{{status}}",
              "created": 1793199600,
              "customer": "cus_Prueba123",
              "setup_future_usage": {{future}},
              "livemode": {{(livemode ? "true" : "false")}},
              "metadata": { "attempt_id": "{{reference}}"{{(offSession ? ", \"off_session\": \"true\"" : string.Empty)}} },
              "last_payment_error": {{error}},
              "latest_charge": {{charge}},
              "payment_method": {
                "id": "pm_Prueba123",
                "object": "payment_method",
                "type": "card",
                "customer": "cus_Prueba123",
                "card": { "brand": "visa", "last4": "4242", "exp_month": 12, "exp_year": 2030, "fingerprint": "Xt5EWLLDS7FJjR1c" },
                "billing_details": { "email": "pagador@example.com" }
              }
            }
            """;
    }

    public static string Disputes(params string[] statuses) =>
        $$"""{ "object": "list", "has_more": false, "data": [{{string.Join(",", statuses.Select(s => $$"""{ "id": "du_1", "object": "dispute", "status": "{{s}}" }"""))}}] }""";

    public static string Refund(string status = "succeeded", long amount = 500000, string? failure = null) =>
        $$"""{ "id": "re_3Prueba", "object": "refund", "amount": {{amount}}, "currency": "ars", "payment_intent": "pi_3Prueba", "status": "{{status}}", "failure_reason": {{(failure is null ? "null" : $"\"{failure}\"")}} }""";

    /// <summary>Un error de Stripe con su forma: type, code y el resto de los campos que vengan.</summary>
    public static string Error(string type, string? code = null, string message = "Error de prueba", string? param = null, string? declineCode = null, string? adviceCode = null, string? intent = null)
    {
        var error = new JsonObject { ["type"] = type, ["message"] = message };
        if (code is not null)
        {
            error["code"] = code;
        }

        if (param is not null)
        {
            error["param"] = param;
        }

        if (declineCode is not null)
        {
            error["decline_code"] = declineCode;
        }

        if (adviceCode is not null)
        {
            error["advice_code"] = adviceCode;
        }

        if (intent is not null)
        {
            error["payment_intent"] = new JsonObject { ["id"] = intent, ["status"] = "requires_payment_method" };
        }

        return new JsonObject { ["error"] = error }.ToJsonString();
    }

    public const string EmptyList = """{ "object": "list", "data": [], "has_more": false }""";

    public static string Balance(bool live = false) =>
        $$"""{ "object": "balance", "livemode": {{(live ? "true" : "false")}}, "available": [{ "amount": 0, "currency": "usd" }], "pending": [] }""";

    public static string Endpoints(string url, string status = "enabled", params string[] events) =>
        new JsonObject
        {
            ["object"] = "list",
            ["data"] = new JsonArray(new JsonObject
            {
                ["id"] = "we_1",
                ["object"] = "webhook_endpoint",
                ["url"] = url,
                ["status"] = status,
                ["enabled_events"] = new JsonArray([.. (events.Length == 0 ? StripeOperations.WebhookEvents : events).Select(e => (JsonNode)e)]),
            }),
        }.ToJsonString();

    public static string Event(string type, JsonObject data, string id = "evt_1Prueba") =>
        new JsonObject { ["id"] = id, ["object"] = "event", ["type"] = type, ["data"] = new JsonObject { ["object"] = data } }.ToJsonString();
}
