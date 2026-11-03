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

namespace BillingOss.Module.PayPal.Tests;

/// <summary>Un pedido que el módulo mandó al PayPal falso. Text es el cuerpo tal cual salió.</summary>
internal sealed record Sent(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Text)
{
    /// <summary>La ruta que usan los tests para dar respuestas: método y path, sin la query.</summary>
    public string Route => $"{Method} {Url.AbsolutePath}";

    public JsonNode? Body => string.IsNullOrEmpty(Text) ? null : JsonNode.Parse(Text);

    public string Query(string name) => QueryHelpers.ParseQuery(Url.Query)[name].ToString();
}

/// <summary>
/// PayPal falso: contesta con las respuestas sintéticas que arma cada test y anota lo que recibe. El pedido de token
/// tiene respuesta por defecto, con un token que se genera al crearlo, y la búsqueda de disputas no encuentra ninguna.
/// </summary>
internal sealed class FakePayPal : HttpMessageHandler
{
    public const string TokenRoute = "POST /v1/oauth2/token";

    public const string DisputesRoute = "GET /v1/customer/disputes";

    private readonly Dictionary<string, Func<Sent, HttpResponseMessage>> routes = [];
    private readonly List<Sent> requests = [];
    private int issued;

    public FakePayPal()
    {
        On(TokenRoute, _ => Samples.Json(Samples.Token(NextToken())));
        On(DisputesRoute, Samples.Disputes());
    }

    /// <summary>El último token que entregó el pedido de token.</summary>
    public string AccessToken { get; private set; } = string.Empty;

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

    /// <summary>Los pedidos a la API, sin los de token.</summary>
    public IReadOnlyList<Sent> ApiRequests => [.. Requests.Where(r => r.Route != TokenRoute)];

    /// <summary>La respuesta a una ruta, como "GET /v2/payments/captures/X".</summary>
    public FakePayPal On(string route, Func<Sent, HttpResponseMessage> respond)
    {
        routes[route] = respond;
        return this;
    }

    public FakePayPal On(string route, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(route, _ => Samples.Json(json, status));

    public int Count(string route) => Requests.Count(r => r.Route == route);

    public Sent Single(string route) => Requests.Single(r => r.Route == route);

    /// <summary>Un token nuevo, distinto en cada pedido. Se arma al correr: no hay valores fijos con forma de credencial.</summary>
    public string NextToken()
    {
        AccessToken = string.Create(CultureInfo.InvariantCulture, $"A21AA{Interlocked.Increment(ref issued)}x{Guid.NewGuid():N}");
        return AccessToken;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new Sent(request.Method, request.RequestUri!, headers, text);
        lock (requests)
        {
            requests.Add(sent);
        }

        return routes.TryGetValue(sent.Route, out var respond)
            ? respond(sent)
            : throw new InvalidOperationException($"El PayPal falso no tiene respuesta para {sent.Route}.");
    }
}

/// <summary>Reloj que mueve el test.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>El módulo corriendo en un TestServer, con el PayPal falso del otro lado. Firma los pedidos como el núcleo.</summary>
internal sealed class PpHost : IAsyncDisposable
{
    public const string ClientId = "client-id-de-prueba";

    public const string WebhookId = "8PT597110X687430LKGECATA";

    /// <summary>Se genera al correr: no hay secretos escritos en el código.</summary>
    public static readonly string ClientSecret = $"cs{Guid.NewGuid():N}";

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"));

    private readonly WebApplication app;
    private readonly string db;

    private PpHost(WebApplication app, string db, FakePayPal pp)
    {
        this.app = app;
        this.db = db;
        Pp = pp;
        Client = app.GetTestClient();
    }

    public FakePayPal Pp { get; }

    public HttpClient Client { get; }

    public static async Task<PpHost> Start(FakePayPal pp, HttpClient? http = null, TimeProvider? time = null, ILogger? logger = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"pp-idem-{Guid.NewGuid():N}.db");
        app.MapModule(
            PayPalModule.Create(http ?? new HttpClient(pp), logger ?? NullLogger.Instance, time ?? TimeProvider.System),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new PpHost(app, db, pp);
    }

    public static JsonObject Config() => new()
    {
        ["environment"] = "sandbox",
        ["client_id"] = ClientId,
        ["client_secret"] = ClientSecret,
        ["webhook_id"] = WebhookId,
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

/// <summary>
/// Respuestas sintéticas con la forma de la documentación pública de PayPal (Orders v2, Payments v2, Webhooks v1 y
/// Transaction search v1). Los IDs son inventados, con el formato de PayPal.
/// </summary>
internal static class Samples
{
    public const string OrderId = "5O190127TN364715T";

    public const string CaptureId = "3C679366HH908993F";

    public const string ApproveUrl = $"https://www.sandbox.paypal.com/checkoutnow?token={OrderId}";

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>Respuesta de POST /v1/oauth2/token. Se arma con JsonObject para no escribir el token en un literal.</summary>
    public static string Token(string accessToken, int expiresIn = 32400) => new JsonObject
    {
        ["scope"] = "https://uri.paypal.com/services/payments/payment https://uri.paypal.com/services/reporting/search/read",
        ["access_token"] = accessToken,
        ["token_type"] = "Bearer",
        ["app_id"] = "APP-80W284485P519543T",
        ["expires_in"] = expiresIn,
    }.ToJsonString();

    /// <summary>Respuesta mínima de POST /v2/checkout/orders con la billetera de PayPal como medio.</summary>
    public const string OrderCreated = $$"""
        {
          "id": "{{OrderId}}",
          "status": "PAYER_ACTION_REQUIRED",
          "payment_source": { "paypal": {} },
          "links": [
            { "href": "https://api-m.sandbox.paypal.com/v2/checkout/orders/{{OrderId}}", "rel": "self", "method": "GET" },
            { "href": "{{ApproveUrl}}", "rel": "payer-action", "method": "GET" }
          ]
        }
        """;

    /// <summary>
    /// Una captura (GET /v2/payments/captures/{id}). La comisión es 3.98 sobre 100.00. Lleva el correo del vendedor a
    /// propósito: el módulo no lo tiene que devolver.
    /// </summary>
    public static string Capture(
        string id = CaptureId,
        string status = "COMPLETED",
        string amount = "100.00",
        string currency = "USD",
        string? reason = null,
        string? fee = "3.98",
        string? feeCurrency = null,
        string? customId = "att-1042",
        string orderId = OrderId)
    {
        var capture = new JsonObject
        {
            ["id"] = id,
            ["amount"] = new JsonObject { ["currency_code"] = currency, ["value"] = amount },
            ["final_capture"] = true,
            ["status"] = status,
            ["invoice_id"] = customId,
            ["custom_id"] = customId,
            ["supplementary_data"] = new JsonObject { ["related_ids"] = new JsonObject { ["order_id"] = orderId } },
            ["payee"] = new JsonObject { ["email_address"] = "vendedor@example.com", ["merchant_id"] = "YXZY75W2GKDQE" },
            ["create_time"] = "2026-10-25T19:01:10Z",
            ["update_time"] = "2026-10-25T19:01:12Z",
            ["links"] = new JsonArray(
                new JsonObject { ["href"] = $"https://api-m.sandbox.paypal.com/v2/payments/captures/{id}", ["rel"] = "self", ["method"] = "GET" },
                new JsonObject { ["href"] = $"https://api-m.sandbox.paypal.com/v2/checkout/orders/{orderId}", ["rel"] = "up", ["method"] = "GET" }),
        };
        if (reason is not null)
        {
            capture["status_details"] = new JsonObject { ["reason"] = reason };
        }

        if (fee is not null)
        {
            capture["seller_receivable_breakdown"] = new JsonObject
            {
                ["gross_amount"] = new JsonObject { ["currency_code"] = currency, ["value"] = amount },
                ["paypal_fee"] = new JsonObject { ["currency_code"] = feeCurrency ?? currency, ["value"] = fee },
                ["net_amount"] = new JsonObject { ["currency_code"] = currency, ["value"] = "96.02" },
            };
        }

        return capture.ToJsonString();
    }

    /// <summary>Un reembolso, con su enlace up a la captura.</summary>
    public static JsonObject Refund(string id, string amount, string status = "COMPLETED", string captureId = CaptureId, string fee = "0.12", string total = "30.00") => new()
    {
        ["id"] = id,
        ["amount"] = new JsonObject { ["currency_code"] = "USD", ["value"] = amount },
        ["seller_payable_breakdown"] = new JsonObject
        {
            ["gross_amount"] = new JsonObject { ["currency_code"] = "USD", ["value"] = amount },
            ["paypal_fee"] = new JsonObject { ["currency_code"] = "USD", ["value"] = fee },
            ["net_amount"] = new JsonObject { ["currency_code"] = "USD", ["value"] = amount },
            ["total_refunded_amount"] = new JsonObject { ["currency_code"] = "USD", ["value"] = total },
        },
        ["status"] = status,
        ["create_time"] = "2026-10-26T10:00:00-07:00",
        ["links"] = new JsonArray(
            new JsonObject { ["href"] = $"https://api.paypal.com/v2/payments/refunds/{id}", ["rel"] = "self", ["method"] = "GET" },
            new JsonObject { ["href"] = $"https://api.paypal.com/v2/payments/captures/{captureId}", ["rel"] = "up", ["method"] = "GET" }),
    };

    /// <summary>Una orden (GET /v2/checkout/orders/{id} o la respuesta de la captura). captures y refunds van en la unidad de compra.</summary>
    public static string Order(string status = "COMPLETED", string reference = "billing-oss", string? capture = null, params JsonObject[] refunds)
    {
        var payments = new JsonObject();
        if (capture is not null)
        {
            var parsed = JsonNode.Parse(capture)!.AsObject();

            // Dentro de la orden la captura no trae custom_id: está en la unidad de compra.
            parsed.Remove("custom_id");
            parsed.Remove("supplementary_data");
            payments["captures"] = new JsonArray(parsed);
        }

        if (refunds.Length > 0)
        {
            payments["refunds"] = new JsonArray([.. refunds]);
        }

        return new JsonObject
        {
            ["id"] = OrderId,
            ["intent"] = "CAPTURE",
            ["status"] = status,
            ["purchase_units"] = new JsonArray(new JsonObject
            {
                ["reference_id"] = reference,
                ["custom_id"] = "att-1042",
                ["invoice_id"] = "att-1042",
                ["amount"] = new JsonObject { ["currency_code"] = "USD", ["value"] = "100.00" },
                ["payments"] = payments,
            }),
            ["payer"] = new JsonObject { ["email_address"] = "comprador@example.com", ["payer_id"] = "QYR5Z8XDVJNXQ" },
        }.ToJsonString();
    }

    /// <summary>Una transacción de la búsqueda (GET /v1/reporting/transactions).</summary>
    public static JsonObject Transaction(string id, string code, string amount = "100.00", string? reference = null, string status = "S") => new()
    {
        ["transaction_info"] = new JsonObject
        {
            ["transaction_id"] = id,
            ["paypal_reference_id"] = reference,
            ["paypal_reference_id_type"] = reference is null ? null : "TXN",
            ["transaction_event_code"] = code,
            ["transaction_initiation_date"] = "2026-10-25T19:01:10+0000",
            ["transaction_amount"] = new JsonObject { ["currency_code"] = "USD", ["value"] = amount },
            ["transaction_status"] = status,
            ["custom_field"] = "att-1042",
        },
    };

    public static string Search(int totalItems, int page, int totalPages, params JsonObject[] transactions) => new JsonObject
    {
        ["transaction_details"] = new JsonArray([.. transactions]),
        ["account_number"] = "XZXSPECPDZHZU",
        ["page"] = page,
        ["total_items"] = totalItems,
        ["total_pages"] = totalPages,
    }.ToJsonString();

    /// <summary>Respuesta de GET /v1/customer/disputes: el resumen de cada disputa, con su ID y su estado.</summary>
    public static string Disputes(params (string Id, string Status)[] disputes) => new JsonObject
    {
        ["items"] = new JsonArray(disputes.Select(d => (JsonNode?)new JsonObject
        {
            ["dispute_id"] = d.Id,
            ["reason"] = "MERCHANDISE_OR_SERVICE_NOT_RECEIVED",
            ["status"] = d.Status,
            ["dispute_amount"] = new JsonObject { ["currency_code"] = "USD", ["value"] = "100.00" },
            ["dispute_life_cycle_stage"] = "CHARGEBACK",
            ["create_time"] = "2026-10-26T10:00:00.000Z",
            ["update_time"] = "2026-10-26T10:00:00.000Z",
        }).ToArray()),
    }.ToJsonString();

    /// <summary>Una disputa completa (GET /v1/customer/disputes/{id}), ya resuelta con ese resultado.</summary>
    public static string Dispute(string id, string outcome) => new JsonObject
    {
        ["dispute_id"] = id,
        ["status"] = "RESOLVED",
        ["disputed_transactions"] = new JsonArray(new JsonObject { ["seller_transaction_id"] = CaptureId }),
        ["dispute_outcome"] = new JsonObject
        {
            ["outcome_code"] = outcome,
            ["amount_refunded"] = new JsonObject { ["currency_code"] = "USD", ["value"] = "100.00" },
        },
        ["dispute_life_cycle_stage"] = "CHARGEBACK",
    }.ToJsonString();

    /// <summary>Error de la API REST de PayPal: name, message, debug_id y details con su issue.</summary>
    public static string Error(string name, string message, params (string Issue, string Description)[] details) => new JsonObject
    {
        ["name"] = name,
        ["message"] = message,
        ["debug_id"] = "90bff07028f7f",
        ["details"] = new JsonArray(details.Select(d => (JsonNode?)new JsonObject { ["issue"] = d.Issue, ["description"] = d.Description }).ToArray()),
    }.ToJsonString();
}

/// <summary>Logger que guarda el nivel y el texto de lo que se escribe.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<(LogLevel Level, string Text)> lines = [];

    public IReadOnlyList<(LogLevel Level, string Text)> Lines
    {
        get
        {
            lock (lines)
            {
                return [.. lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (lines)
        {
            lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
