using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

// Este archivo se comparte con las pruebas de cada módulo de panel (Compile Include con Link).
namespace BillingOss.Modules.Hosting.Testing;

/// <summary>
/// Un módulo corriendo en un TestServer, con un panel falso del otro lado. Firma los pedidos como el núcleo. Cada host
/// tiene su propio registro de idempotencia, como una réplica.
/// </summary>
internal sealed class ModuleHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-firma-de-prueba");

    /// <summary>La contraseña que manda el núcleo en las pruebas. Inventada, con el alfabeto de Passwords.New.</summary>
    public const string Password = "Pr8eba-Inventada_x2.Q";

    /// <summary>El uuid del servicio de las pruebas.</summary>
    public const string Uuid = "0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e11";

    private static readonly string[] ReadOnly = ["validate", "get_status", "view", "sso", "usage"];

    private readonly WebApplication app;
    private readonly string db;
    private readonly JsonObject config;

    private ModuleHost(WebApplication app, string db, JsonObject config)
    {
        this.app = app;
        this.db = db;
        this.config = config;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    /// <param name="config">La configuración de instancia que va en cada pedido si el test no pasa otra.</param>
    public static async Task<ModuleHost> Start(ModuleDefinition module, JsonObject config)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"panel-idem-{Guid.NewGuid():N}.db");
        app.MapModule(module, new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new ModuleHost(app, db, config);
    }

    /// <summary>Contexto de un servicio que ya tiene cuenta en el panel, como uno importado.</summary>
    public static JsonObject Existing(string username, string domain = "example.test", string plan = "basico", string uuid = Uuid, string language = "es") =>
        Context(uuid, new JsonObject { ["username"] = username, ["domain"] = domain }, plan, new JsonObject(), language);

    /// <summary>Contexto de un alta: el dominio viene del pedido.</summary>
    public static JsonObject NewService(string domain = "nuevo.example.test", string plan = "basico", string uuid = Uuid, string language = "es-AR") =>
        Context(uuid, new JsonObject(), plan, new JsonObject { ["domain"] = domain }, language);

    private static JsonObject Context(string uuid, JsonObject externalIds, string plan, JsonObject order, string language) => new()
    {
        ["service"] = new JsonObject
        {
            ["id"] = 1042,
            ["uuid"] = uuid,
            ["status"] = "active",
            ["external_ids"] = externalIds,
            ["params"] = new JsonObject { ["plan"] = plan },
            ["order"] = order,
            ["plan"] = new JsonObject { ["unit"] = "month", ["count"] = 1 },
        },
        ["account"] = new JsonObject
        {
            ["id"] = 77,
            ["language"] = language,
            ["name"] = "Cliente de Prueba <S.A.>",
            ["email"] = "cliente@example.com",
        },
    };

    public static JsonObject Credentials(string password = Password) =>
        new() { ["credentials"] = new JsonObject { ["password"] = password } };

    public static JsonObject ChangePassword(string password = Password) =>
        new() { ["action"] = "change_password", ["credentials"] = new JsonObject { ["password"] = password } };

    /// <summary>La entrada de usage con las dos métricas que declaran los módulos de paneles.</summary>
    public static JsonObject Metrics() => new() { ["metrics"] = new JsonArray(HostingModule.DiskMetric, HostingModule.TransferMetric) };

    /// <summary>Una entrada que sirve a cualquier operación: la acción change_password y las métricas de usage.</summary>
    public static JsonObject AnyInput()
    {
        var input = ChangePassword();
        input["metrics"] = Metrics()["metrics"]!.DeepClone();
        return input;
    }

    /// <summary>Una operación de aprovisionamiento. Las que cambian algo llevan una clave nueva si no se pasa una.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Op(string operation, JsonObject context, JsonObject? input = null, string? key = null, JsonObject? config = null) =>
        Send($"/v0/provisioning/{operation}", input ?? [], ReadOnly.Contains(operation) ? null : key ?? Guid.NewGuid().ToString(), context, config);

    /// <summary>La consulta de una operación larga, con el sobre completo.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Poll(string operationId, JsonObject context, JsonObject? config = null) =>
        Send($"/v0/operations/{operationId}", [], null, context, config);

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject input, string? key = null, JsonObject? context = null, JsonObject? config = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-panel", ["config"] = (config ?? this.config).DeepClone() },
            ["context"] = (context ?? new JsonObject()).DeepClone(),
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

    public static string? Reason(JsonNode? body) => body?["error"]?["details"]?["reason"]?.GetValue<string>();

    public static string? Result(JsonNode? body, string field) => body?["result"]?[field]?.GetValue<string>();

    public static string? Username(JsonNode? body) => body?["result"]?["external_ids"]?["username"]?.GetValue<string>();

    public static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}

/// <summary>Reloj que solo avanza a mano, también la hora: arranca en la hora real de su creación.</summary>
internal sealed class StepClock : TimeProvider
{
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Read(ref ticks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}

/// <summary>Logger que guarda lo que se escribe, para comprobar que no filtra secretos.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<string> lines = [];

    public string Text
    {
        get
        {
            lock (lines)
            {
                return string.Join('\n', lines);
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
            lines.Add(formatter(state, exception) + exception);
        }
    }
}
