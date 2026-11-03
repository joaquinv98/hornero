using System.Globalization;
using System.Net;
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

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>Un pedido que el módulo mandó a la Mercado Pago falsa.</summary>
internal sealed record Sent(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)
{
    /// <summary>La ruta que usan los tests para dar respuestas: método y path, sin la query.</summary>
    public string Route => $"{Method} {Url.AbsolutePath}";

    public string Query(string name) => QueryHelpers.ParseQuery(Url.Query)[name].ToString();
}

/// <summary>Mercado Pago falsa: contesta con las respuestas sintéticas que arma cada test y anota lo que recibe.</summary>
internal sealed class FakeMercadoPago : HttpMessageHandler
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

    /// <summary>La respuesta a una ruta, como "GET /v1/payments/123".</summary>
    public FakeMercadoPago On(string route, Func<Sent, HttpResponseMessage> respond)
    {
        routes[route] = respond;
        return this;
    }

    public FakeMercadoPago On(string route, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(route, _ => Samples.Json(json, status));

    public int Count(string route) => Requests.Count(r => r.Route == route);

    public Sent Single(string route) => Requests.Single(r => r.Route == route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new Sent(request.Method, request.RequestUri!, headers, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
        lock (requests)
        {
            requests.Add(sent);
        }

        return routes.TryGetValue(sent.Route, out var respond)
            ? respond(sent)
            : throw new InvalidOperationException($"La Mercado Pago falsa no tiene respuesta para {sent.Route}.");
    }
}

/// <summary>El módulo corriendo en un TestServer, con la Mercado Pago falsa del otro lado. Firma los pedidos como el núcleo.</summary>
internal sealed class MpHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-prueba");

    /// <summary>Valores inventados: ninguno sirve contra Mercado Pago.</summary>
    public const string FakeAccessToken = "TEST-access-inventado-para-pruebas";

    public const string FakeWebhookSecret = "clave-de-webhooks-de-prueba";

    private readonly WebApplication app;
    private readonly string db;

    private MpHost(WebApplication app, string db, FakeMercadoPago mp)
    {
        this.app = app;
        this.db = db;
        Mp = mp;
        Client = app.GetTestClient();
    }

    public FakeMercadoPago Mp { get; }

    public HttpClient Client { get; }

    public static async Task<MpHost> Start(FakeMercadoPago mp, HttpClient? http = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"mp-idem-{Guid.NewGuid():N}.db");
        app.MapModule(
            MercadoPagoModule.Create(http ?? new HttpClient(mp), NullLogger.Instance),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new MpHost(app, db, mp);
    }

    public static JsonObject Config() => new()
    {
        ["access_token"] = FakeAccessToken,
        ["webhook_secret"] = FakeWebhookSecret,
    };

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

    /// <summary>El código del error de una respuesta del módulo.</summary>
    public static string? Code(JsonNode? body) => body?["error"]?["code"]?.GetValue<string>();

    public static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}

/// <summary>Respuestas sintéticas con la forma de la documentación de Mercado Pago (preferencias, pagos y búsqueda de pagos).</summary>
internal static class Samples
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public const string InitPoint = "https://www.mercadopago.com.ar/checkout/v1/redirect?pref_id=1234567890-5c0b6a2f-0000-0000-0000-aaaabbbbcccc";

    /// <summary>Respuesta de POST /checkout/preferences.</summary>
    public const string PreferenceCreated = $$"""
        {
          "id": "1234567890-5c0b6a2f-0000-0000-0000-aaaabbbbcccc",
          "init_point": "{{InitPoint}}",
          "sandbox_init_point": "https://sandbox.mercadopago.com.ar/checkout/v1/redirect?pref_id=1234567890-5c0b6a2f-0000-0000-0000-aaaabbbbcccc",
          "external_reference": "att-1042",
          "date_created": "2026-10-25T15:00:00.000-04:00"
        }
        """;

    /// <summary>
    /// Respuesta de GET /v1/payments/{id}. La comisión del vendedor es 435.6 y hay otra de 120.5 que paga el comprador.
    /// Lleva datos del pagador a propósito: el módulo no los tiene que devolver.
    /// </summary>
    public static string Payment(
        long id = 123456789,
        string status = "approved",
        string detail = "accredited",
        string amount = "12100",
        string refunded = "0",
        string reference = "att-1042",
        string currency = "ARS",
        string? approvedAt = "2026-10-25T15:01:10.000-04:00",
        bool live = true)
    {
        var approved = approvedAt is null ? "null" : $"\"{approvedAt}\"";
        return $$"""
            {
              "id": {{id}},
              "live_mode": {{(live ? "true" : "false")}},
              "date_created": "2026-10-25T15:00:00.000-04:00",
              "date_approved": {{approved}},
              "date_last_updated": "2026-10-25T15:01:10.000-04:00",
              "currency_id": "{{currency}}",
              "external_reference": "{{reference}}",
              "status": "{{status}}",
              "status_detail": "{{detail}}",
              "transaction_amount": {{amount}},
              "transaction_amount_refunded": {{refunded}},
              "fee_details": [
                { "type": "mercadopago_fee", "amount": 435.6, "fee_payer": "collector" },
                { "type": "financing_fee", "amount": 120.5, "fee_payer": "payer" }
              ],
              "payer": { "email": "comprador.de.prueba@example.com", "identification": { "type": "DNI", "number": "11111111" } }
            }
            """;
    }

    /// <summary>Respuesta de GET /v1/payments/search.</summary>
    public static string Search(int total, int offset, params string[] payments) =>
        $$"""{ "paging": { "total": {{total}}, "limit": 100, "offset": {{offset}} }, "results": [{{string.Join(",", payments)}}] }""";

    /// <summary>Respuesta de POST /v1/payments/{id}/refunds.</summary>
    public static string Refund(long id = 5550001, long paymentId = 123456789, string amount = "5000", string status = "approved") =>
        $$"""
        {
          "id": {{id}},
          "payment_id": {{paymentId}},
          "amount": {{amount}},
          "source": { "id": "987654321", "name": "Vendedor de prueba", "type": "collector" },
          "date_created": "2026-10-26T10:00:00.000-04:00",
          "refund_mode": "standard",
          "status": "{{status}}"
        }
        """;

    /// <summary>Lo que contesta la API de pagos a un access token que no existe. La forma se vio con un token inventado.</summary>
    public const string Unauthorized =
        """{"message":"Must provide your access_token to proceed","error":"unauthorized","status":401,"cause":[{"code":5,"description":"Must provide your access_token to proceed","data":"27-10-2026T02:13:50UTC;20797790-acc2-468e-91f7-35714482ec01"}]}""";

    /// <summary>Lo que contesta el filtro de políticas de Mercado Pago, en otras rutas, al mismo token. Trae code y message en la raíz.</summary>
    public const string PolicyForbidden =
        """{"message":"At least one policy returned UNAUTHORIZED.","blocked_by":"PolicyAgent","status":403,"code":"PA_UNAUTHORIZED_RESULT_FROM_POLICIES"}""";

    /// <summary>Error de Mercado Pago con el formato de la API de pagos: message, error, status y cause.</summary>
    public static string Error(int status, string error, string message, params (string Code, string Description)[] causes) =>
        new JsonObject
        {
            ["message"] = message,
            ["error"] = error,
            ["status"] = status,
            ["cause"] = new JsonArray(causes.Select(c => (JsonNode?)new JsonObject { ["code"] = c.Code, ["description"] = c.Description }).ToArray()),
        }.ToJsonString();
}
