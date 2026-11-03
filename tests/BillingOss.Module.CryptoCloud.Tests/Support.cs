using System.Buffers.Text;
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

namespace BillingOss.Module.CryptoCloud.Tests;

/// <summary>Un pedido que el módulo mandó a la CryptoCloud falsa.</summary>
internal sealed record Sent(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)
{
    /// <summary>La ruta que usan los tests para dar respuestas: método y path.</summary>
    public string Route => $"{Method} {Url.AbsolutePath}";
}

/// <summary>CryptoCloud falsa: contesta con las respuestas sintéticas que arma cada test y anota lo que recibe.</summary>
internal sealed class FakeCryptoCloud : HttpMessageHandler
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

    /// <summary>La respuesta a una ruta, como "POST /v2/invoice/create".</summary>
    public FakeCryptoCloud On(string route, Func<Sent, HttpResponseMessage> respond)
    {
        routes[route] = respond;
        return this;
    }

    public FakeCryptoCloud On(string route, string json, HttpStatusCode status = HttpStatusCode.OK) =>
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
            : throw new InvalidOperationException($"La CryptoCloud falsa no tiene respuesta para {sent.Route}.");
    }
}

/// <summary>El módulo corriendo en un TestServer, con la CryptoCloud falsa del otro lado. Firma los pedidos como el núcleo.</summary>
internal sealed class CcHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-prueba");

    /// <summary>Valores inventados: ninguno sirve contra CryptoCloud.</summary>
    public const string FakeShopId = "TiendaDePrueba1";

    public const string FakeApiKey = "api-key-inventada-para-pruebas";

    public const string FakeSecretKey = "clave-secreta-de-prueba";

    private readonly WebApplication app;
    private readonly string db;

    private CcHost(WebApplication app, string db, FakeCryptoCloud cc)
    {
        this.app = app;
        this.db = db;
        Cc = cc;
        Client = app.GetTestClient();
    }

    public FakeCryptoCloud Cc { get; }

    public HttpClient Client { get; }

    public static async Task<CcHost> Start(FakeCryptoCloud cc, HttpClient? http = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"cc-idem-{Guid.NewGuid():N}.db");
        app.MapModule(
            CryptoCloudModule.Create(http ?? new HttpClient(cc), NullLogger.Instance),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new CcHost(app, db, cc);
    }

    public static JsonObject Config() => new()
    {
        ["shop_id"] = FakeShopId,
        ["api_key"] = FakeApiKey,
        ["secret_key"] = FakeSecretKey,
    };

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject input, string? key = null, JsonObject? config = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = (config ?? Config()).DeepClone() },
            ["context"] = new JsonObject(),
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

/// <summary>JWT HS256 armados a mano en la prueba, sin el código del módulo.</summary>
internal static class Jwt
{
    public static string Sign(string secret, string header = """{"typ":"JWT","alg":"HS256"}""", string payload = """{"id":13,"exp":1631786426}""")
    {
        var signed = $"{Part(header)}.{Part(payload)}";
        return $"{signed}.{Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed)))}";
    }

    public static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    /// <summary>Claims como las del ejemplo oficial con un exp a tantos segundos de ahora.</summary>
    public static string Claims(long expFromNow = 300) =>
        $$"""{"id":13,"exp":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expFromNow}}}""";
}

/// <summary>Respuestas sintéticas con la forma de la documentación de la API v2 de CryptoCloud (Trybit).</summary>
internal static class Samples
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public const string Link = "https://pay.trybit.com/89UX09KA";

    /// <summary>Respuesta de POST /v2/invoice/create, con los campos de la documentación.</summary>
    public const string InvoiceCreated = $$"""
        {
          "status": "success",
          "result": {
            "uuid": "INV-89UX09KA",
            "created": "2026-10-25 19:00:00.958133",
            "address": "",
            "expiry_date": "2026-10-26 19:00:00.493361",
            "side_commission": "client",
            "side_commission_service": "merchant",
            "type_payments": "crypto",
            "amount": 34.0,
            "amount_usd": 34.0,
            "amount_in_fiat": 34.0,
            "fee": 0.0,
            "fee_usd": 0.0,
            "service_fee": 0.48,
            "service_fee_usd": 0.48,
            "fiat_currency": "USD",
            "status": "created",
            "is_email_required": false,
            "link": "{{Link}}",
            "invoice_id": null,
            "currency": { "id": 4, "code": "USDT_TRC20" },
            "project": { "id": 1, "name": "Proyecto de prueba", "fail": "", "success": "", "logo": "" },
            "test_mode": false
          }
        }
        """;

    /// <summary>
    /// Una factura como la devuelven invoice/merchant/info e invoice/merchant/list. La comisión del servicio la paga
    /// el comercio y la de la red el cliente. Lleva correo y teléfono a propósito: el módulo no los tiene que devolver.
    /// </summary>
    public static string Invoice(
        string uuid = "INV-89UX09KA",
        string status = "paid",
        string amountInFiat = "34.0",
        string? fiatCurrency = "USD",
        string orderId = "att-1042",
        string? dateFinished = "2026-10-25 19:12:10.258735",
        string sideCommission = "client",
        string sideService = "merchant",
        bool testMode = false)
    {
        var fiat = fiatCurrency is null ? string.Empty : $"\"fiat_currency\": \"{fiatCurrency}\",";
        var finished = dateFinished is null ? "null" : $"\"{dateFinished}\"";
        return $$"""
            {
              "uuid": "{{uuid}}",
              "created": "2026-10-25 19:00:00.958133",
              "address": "TXyzDireccionDePrueba",
              "currency": { "id": 4, "code": "USDT_TRC20", "fullcode": "USDT_TRC20", "name": "Tether" },
              "date_finished": {{finished}},
              "expiry_date": "2026-10-26 19:00:00.493361",
              "side_commission": "{{sideCommission}}",
              "side_commission_cc": "{{sideService}}",
              "type_payments": "crypto",
              "status": "{{status}}",
              "invoice_status": "success",
              "is_email_required": false,
              "project": { "id": 1, "name": "Proyecto de prueba", "fail": "None", "success": "None", "logo": null },
              "tx_list": ["0x4d52dc2d2fa8555ab10211bd6c9a7aed13e6f9e1901090b03e7bf74603178c15"],
              "test_mode": {{(testMode ? "true" : "false")}},
              "type": "up",
              "user_email": "comprador.de.prueba@example.com",
              "pay_url": "None",
              "phone": "1100000000",
              "order_id": "{{orderId}}",
              {{fiat}}
              "amount_in_crypto": null,
              "amount_in_fiat": {{amountInFiat}},
              "amount": 34.0,
              "amount_usd": 34.0,
              "amount_to_pay": 34.0,
              "amount_to_pay_usd": 34.0,
              "amount_paid": 34.0,
              "amount_paid_usd": 34.0,
              "fee": 1.2,
              "fee_usd": 1.2,
              "service_fee": 0.479,
              "service_fee_usd": 0.479,
              "received": 33.52,
              "received_usd": 33.52,
              "to_surcharge": 0.0,
              "to_surcharge_usd": 0.0
            }
            """;
    }

    /// <summary>Respuesta de invoice/merchant/info.</summary>
    public static string Info(params string[] invoices) => $$"""{ "status": "success", "result": [{{string.Join(",", invoices)}}] }""";

    /// <summary>Respuesta de invoice/merchant/list.</summary>
    public static string List(int total, params string[] invoices) =>
        $$"""{ "status": "success", "result": [{{string.Join(",", invoices)}}], "all_count": {{total}} }""";

    /// <summary>Respuesta de merchant/wallet/balance/all.</summary>
    public const string Balance = """
        {
          "status": "success",
          "result": [
            { "currency": { "id": 4, "code": "USDT_TRC20" }, "balance_crypto": 812.5, "balance_usd": 812.5, "available_balance": 812.5, "available_balance_usd": 812.5 }
          ]
        }
        """;

    /// <summary>El error de autorización con la forma de la documentación.</summary>
    public const string Unauthorized = """{ "status": "error", "result": { "authorization": "Unauthorized request." } }""";

    /// <summary>Un error de CryptoCloud: status error y en result cada campo con su mensaje.</summary>
    public static string Error(string field, string message) =>
        new JsonObject { ["status"] = "error", ["result"] = new JsonObject { [field] = new JsonArray(message) } }.ToJsonString();
}
