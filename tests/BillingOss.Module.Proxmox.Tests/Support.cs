using System.Collections.Concurrent;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>
/// El módulo en un TestServer, del otro lado el Proxmox falso por HTTPS. Firma los pedidos como el núcleo y guarda los
/// logs para buscar secretos filtrados.
/// </summary>
internal sealed class ModuleHost : IAsyncDisposable
{
    /// <summary>Esperas cortas para que las pruebas no tarden: en producción son de 1, 90 y 40 segundos.</summary>
    public static readonly ProxmoxOptions Fast = new(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

    private static readonly byte[] Signing = RandomNumberGenerator.GetBytes(32);

    private readonly WebApplication app;
    private readonly string db;
    private readonly HttpClient gateway;

    private readonly bool ownsPve;

    private ModuleHost(WebApplication app, string db, HttpClient gateway, FakePve pve, LogSink logs, bool ownsPve)
    {
        this.app = app;
        this.ownsPve = ownsPve;
        this.db = db;
        this.gateway = gateway;
        Pve = pve;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public FakePve Pve { get; }

    public LogSink Logs { get; }

    public HttpClient Client { get; }

    /// <summary>Contraseña del invitado que manda el núcleo. Inventada en cada prueba.</summary>
    public string Password { get; } = "pw-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    /// <summary>Sin Proxmox falso arma uno propio. Con uno, es otra réplica del módulo contra el mismo cluster.</summary>
    public static async Task<ModuleHost> Start(FakePve? pve = null, ProxmoxOptions? options = null)
    {
        var owns = pve is null;
        pve ??= await FakePve.Start();
        var logs = new LogSink();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(logs);
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"pve-idem-{Guid.NewGuid():N}.db");

        // El gateway de pruebas usa el mismo certificado que el Proxmox falso.
        var gateway = new HttpClient(Transport.Handler(pve.Ca)) { Timeout = TimeSpan.FromSeconds(10) };
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Proxmox");
        app.MapModule(
            ProxmoxModule.Create(Transport.Handler, gateway, TimeProvider.System, logger, options ?? Fast),
            new ModuleSdkOptions { SigningSecrets = [Signing], IdempotencyStorePath = db });
        await app.StartAsync();
        return new ModuleHost(app, db, gateway, pve, logs, owns);
    }

    /// <summary>Pool con IPs de documentación (TEST-NET-3) y una red privada inventada.</summary>
    public static JsonArray Pool(int count = 3) => new(Enumerable.Range(1, count).Select(i => (JsonNode?)new JsonObject
    {
        ["mac"] = $"bc:24:11:aa:00:{i:x2}",
        ["public_ip"] = $"203.0.113.{10 + i}",
        ["bridge"] = "vmbr2",
        ["mtu"] = 1360,
        ["private_ip"] = $"10.20.0.{10 + i}",
    }).ToArray());

    public JsonObject Config(JsonArray? pool = null) => new()
    {
        ["api_url"] = Pve.Origin.ToString(),
        ["token_id"] = FakePve.TokenId,
        ["token_secret"] = Pve.Secret,
        ["ca_pem"] = Pve.CaPem,
        ["network_pool"] = pool ?? Pool(),
        ["console_origin"] = Pve.Origin.ToString(),
        ["console_secret"] = Pve.ConsoleSecret,
        ["console_server_key"] = "6",
        ["legacy_acl_cleanup"] = true,
        ["reserved_vmids"] = new JsonArray(new JsonObject { ["from"] = 9000, ["to"] = 9019 }),
    };

    public static JsonObject Params(int template = 9001, int disk = 10, int cores = 1, int memory = 1024) => new()
    {
        ["template_id"] = template,
        ["node"] = "pve1",
        ["cores"] = cores,
        ["memory_mb"] = memory,
        ["disk_gb"] = disk,
        ["rate_mb_s"] = "1.25",
        ["console_enabled"] = true,
        ["hostname_suffix"] = ".vps.example.com",
        ["isolation_group_prefix"] = "vps-",
        ["os_minimum_policy"] = """{"9002":{"cpu":2,"ramMB":3072,"diskGB":40}}""",
        ["console_pool"] = "vps-console",
    };

    public static JsonObject Context(long service = 7001, JsonObject? ids = null, JsonObject? p = null, string status = "active", long account = 42) => new()
    {
        ["service"] = new JsonObject
        {
            ["id"] = service,
            ["status"] = status,
            ["external_ids"] = ids ?? [],
            ["params"] = p ?? Params(),
            ["order"] = new JsonObject(),
            ["plan"] = new JsonObject { ["unit"] = "month", ["count"] = 1 },
        },
        ["account"] = new JsonObject { ["id"] = account, ["language"] = "es" },
    };

    public JsonObject Credentials() => new() { ["credentials"] = new JsonObject { ["guest_password"] = Password } };

    /// <summary>create con su clave, como lo llama el núcleo.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Create(long service = 7001, string? key = null, JsonObject? p = null, JsonObject? config = null) =>
        Send("/v0/provisioning/create", Credentials(), Context(service, p: p), key ?? $"svc-{service}-create-1", config);

    public Task<(HttpStatusCode Status, JsonNode? Body)> Poll(string operationId, JsonObject context) =>
        Send($"/v0/operations/{operationId}", [], context);

    /// <summary>Repite create con la misma clave mientras conteste conflict, como el núcleo, y sigue las consultas.</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> CreateUntilDone(long service = 7001, JsonObject? p = null)
    {
        for (var i = 0; i < 30; i++)
        {
            var (status, body) = await Create(service, p: p);
            if (body?["status"]?.GetValue<string>() == "pending")
            {
                return await PollUntilDone(body["operation_id"]!.GetValue<string>(), Context(service, p: p));
            }

            if (Code(body) != ErrorCodes.Conflict)
            {
                return (status, body);
            }
        }

        throw new AssertFailedException("create no terminó.");
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body)> PollUntilDone(string operationId, JsonObject context)
    {
        for (var i = 0; i < 30; i++)
        {
            var (status, body) = await Poll(operationId, context);
            if (body?["status"]?.GetValue<string>() != "pending" && Code(body) != ErrorCodes.Conflict)
            {
                return (status, body);
            }
        }

        throw new AssertFailedException("La operación no terminó.");
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject input, JsonObject context, string? key = null, JsonObject? config = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-pve", ["config"] = (config ?? Config()).DeepClone() },
            ["context"] = context.DeepClone(),
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

        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Signing, timestamp, "POST", path, requestId, key, bytes));
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Responses.Add(text);
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    /// <summary>GET firmado, para el manifiesto.</summary>
    public async Task<JsonNode> Manifest()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v0/manifest", UriKind.Relative));
        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Signing, timestamp, "GET", "/v0/manifest", requestId, null, []));
        using var response = await Client.SendAsync(request);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>Todas las respuestas del módulo, para buscar secretos filtrados.</summary>
    public ConcurrentBag<string> Responses { get; } = [];

    public static string? Code(JsonNode? body) => body?["error"]?["code"]?.GetValue<string>();

    public static string? Reason(JsonNode? body) => body?["error"]?["details"]?["reason"]?.GetValue<string>();

    public static JsonNode Result(JsonNode? body) => body?["result"] ?? throw new AssertFailedException($"Sin result: {body?.ToJsonString()}");

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        gateway.Dispose();
        await app.DisposeAsync();
        if (ownsPve)
        {
            await Pve.DisposeAsync();
        }

        File.Delete(db);
    }
}

/// <summary>Junta los logs del módulo y de la SDK.</summary>
internal sealed class LogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    public string All => string.Join('\n', lines);

    public ILogger CreateLogger(string categoryName) => new Sink(lines);

    public void Dispose()
    {
    }

    private sealed class Sink(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
