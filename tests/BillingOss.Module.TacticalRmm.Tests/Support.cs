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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.TacticalRmm.Tests;

/// <summary>Un pedido que el módulo mandó al Tactical falso.</summary>
internal sealed record Sent(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)
{
    /// <summary>Método y path, sin la query, como "GET /clients/".</summary>
    public string Route => $"{Method} {Url.AbsolutePath}";

    public string Query(string name) => QueryHelpers.ParseQuery(Url.Query)[name].ToString();
}

/// <summary>
/// Tactical RMM falso con estado: clientes con sitios, agentes y deployments, con las formas de la API REST y las
/// reglas que el módulo necesita respetar (nombre de cliente único, borrado de un cliente con agentes que falla
/// sin move_to_site, X-API-KEY obligatorio). Las formas y los textos salen de la API pública de Tactical RMM.
/// Cada test puede reemplazar la respuesta de una ruta o perder la respuesta después de aplicar el efecto.
/// </summary>
internal sealed class FakeTactical : HttpMessageHandler
{
    public static readonly Uri Origin = new("https://rmm.example.test/");

    private readonly object gate = new();
    private readonly List<Sent> requests = [];
    private readonly Dictionary<string, Func<Sent, HttpResponseMessage>> overrides = [];
    private readonly HashSet<string> lose = [];
    private readonly List<FakeClient> clients = [];
    private readonly List<FakeAgent> agents = [];
    private readonly List<FakeDeployment> deployments = [];
    private long nextId = 100;

    /// <summary>Generada en cada corrida: ningún valor sirve contra un Tactical real.</summary>
    public string ApiKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    /// <summary>Si el servidor ignora el filtro de agentes por cliente y devuelve todos.</summary>
    public bool IgnoreAgentFilter { get; set; }

    public IReadOnlyList<Sent> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    public int Count(string route) => Requests.Count(r => r.Route == route);

    /// <summary>Reemplaza la respuesta de una ruta, como "POST /clients/". La función puede tirar una excepción de red.</summary>
    public FakeTactical On(string route, Func<Sent, HttpResponseMessage> respond)
    {
        lock (gate)
        {
            overrides[route] = respond;
        }

        return this;
    }

    public FakeTactical On(string route, string text, HttpStatusCode status, string mediaType = "application/json") =>
        On(route, _ => Respond(text, status, mediaType));

    /// <summary>Vuelve a la respuesta normal de la ruta.</summary>
    public void Off(string route)
    {
        lock (gate)
        {
            overrides.Remove(route);
        }
    }

    /// <summary>La próxima vez aplica el efecto de la ruta y después corta la conexión sin respuesta.</summary>
    public FakeTactical LoseResponseOnce(string route)
    {
        lock (gate)
        {
            lose.Add(route);
        }

        return this;
    }

    public FakeClient AddClient(string name, string site = "OficinaPrincipal")
    {
        lock (gate)
        {
            var client = new FakeClient(nextId++, name, [new FakeSite(nextId++, site)]);
            clients.Add(client);
            return client;
        }
    }

    public void AddAgents(FakeClient client, int count, bool maintenance = false)
    {
        lock (gate)
        {
            for (var i = 0; i < count; i++)
            {
                agents.Add(new FakeAgent($"agente-{nextId++}", client.Id, client.Sites[0].Id) { Maintenance = maintenance });
            }
        }
    }

    public void AddDeployment(long siteId, DateTimeOffset expiry, string monType = "workstation", string? uid = null)
    {
        lock (gate)
        {
            deployments.Add(new FakeDeployment(nextId++, uid ?? Guid.NewGuid().ToString(), siteId, monType, expiry));
        }
    }

    public FakeClient? Client(string name)
    {
        lock (gate)
        {
            return clients.SingleOrDefault(c => c.Name == name);
        }
    }

    public int ClientCount
    {
        get
        {
            lock (gate)
            {
                return clients.Count;
            }
        }
    }

    public List<FakeAgent> AgentsOf(FakeClient client)
    {
        lock (gate)
        {
            return agents.Where(a => a.ClientId == client.Id).ToList();
        }
    }

    public List<FakeDeployment> Deployments
    {
        get
        {
            lock (gate)
            {
                return [.. deployments];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new Sent(request.Method, request.RequestUri!, headers, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
        Func<Sent, HttpResponseMessage>? respond;
        bool cut;
        lock (gate)
        {
            requests.Add(sent);
            overrides.TryGetValue(sent.Route, out respond);
            cut = lose.Remove(sent.Route);
        }

        if (respond is not null)
        {
            return respond(sent);
        }

        if (headers.GetValueOrDefault("X-API-KEY") != ApiKey)
        {
            return Respond("""{"detail":"Invalid token."}""", HttpStatusCode.Unauthorized);
        }

        HttpResponseMessage response;
        lock (gate)
        {
            response = Handle(sent);
        }

        if (cut)
        {
            response.Dispose();
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó después de enviar");
        }

        return response;
    }

    private HttpResponseMessage Handle(Sent sent)
    {
        var path = sent.Url.AbsolutePath;
        var segments = path.Trim('/').Split('/');
        switch (sent.Method.Method, path)
        {
            case ("GET", "/clients/"):
                return Json(new JsonArray(clients.Select(c => (JsonNode?)ClientJson(c)).ToArray()));
            case ("POST", "/clients/"):
                return AddClient(sent.Body!);
            case ("GET", "/agents/"):
                var filter = sent.Query("client");
                return Json(new JsonArray(agents
                    .Where(a => IgnoreAgentFilter || a.ClientId.ToString(CultureInfo.InvariantCulture) == filter)
                    .Select(a => (JsonNode?)AgentJson(a))
                    .ToArray()));
            case ("POST", "/agents/maintenance/bulk/"):
                var id = sent.Body!["id"]!.GetValue<long>();
                var on = sent.Body["action"]!.GetValue<bool>();
                var changed = agents.Where(a => a.ClientId == id).ToList();
                changed.ForEach(a => a.Maintenance = on);
                return Json(JsonValue.Create(changed.Count > 0
                    ? $"Maintenance mode has been {(on ? "enabled" : "disabled")} on {changed.Count} agents"
                    : "No agents have been put in maintenance mode. You might not have permissions to the resources.")!);
            case ("GET", "/clients/deployments/"):
                return Json(new JsonArray(deployments.Select(d => (JsonNode?)DeploymentJson(d)).ToArray()));
            case ("POST", "/clients/deployments/"):
                var site = sent.Body!["site"]!.GetValue<long>();
                if (!clients.Any(c => c.Sites.Any(s => s.Id == site)))
                {
                    return Respond("""{"detail":"Not found."}""", HttpStatusCode.NotFound);
                }

                // Como el strptime con %z del servidor: el desplazamiento va con o sin dos puntos.
                var raw = sent.Body["expires"]!.GetValue<string>();
                var expires = DateTimeOffset.ParseExact(raw[..^5] + raw[^5..^2] + ":" + raw[^2..], "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                deployments.Add(new FakeDeployment(nextId++, Guid.NewGuid().ToString(), site, sent.Body["agenttype"]!.GetValue<string>(), expires));
                return Json(JsonValue.Create("The deployment was added successfully")!);
        }

        if (segments is ["clients", var idText] && long.TryParse(idText, CultureInfo.InvariantCulture, out var clientId))
        {
            var client = clients.SingleOrDefault(c => c.Id == clientId);
            if (client is null)
            {
                return Respond("""{"detail":"No Client matches the given query."}""", HttpStatusCode.NotFound);
            }

            if (sent.Method == HttpMethod.Get)
            {
                return Json(ClientJson(client));
            }

            if (sent.Method == HttpMethod.Delete)
            {
                // Tactical exige move_to_site cuando el cliente tiene agentes, y contesta 400 con un texto.
                if (agents.Any(a => a.ClientId == clientId) && string.IsNullOrEmpty(sent.Query("move_to_site")))
                {
                    return Respond(
                        "\"Agents exist under this client. There needs to be a site specified to move existing agents to\"",
                        HttpStatusCode.BadRequest);
                }

                clients.Remove(client);
                deployments.RemoveAll(d => client.Sites.Any(s => s.Id == d.SiteId));
                return Json(JsonValue.Create($"{client.Name} was deleted!")!);
            }
        }

        if (segments is ["clients", "deployments", var deploymentText] && sent.Method == HttpMethod.Delete)
        {
            var removed = long.TryParse(deploymentText, CultureInfo.InvariantCulture, out var deploymentId)
                && deployments.RemoveAll(d => d.Id == deploymentId) > 0;
            return removed
                ? Json(JsonValue.Create("The deployment was deleted")!)
                : Respond("""{"detail":"No Deployment matches the given query."}""", HttpStatusCode.NotFound);
        }

        if (segments is ["agents", var agentId] && sent.Method == HttpMethod.Delete)
        {
            // Tactical saca el agente de su base en el momento y le manda la orden de desinstalarse.
            var agent = agents.SingleOrDefault(a => a.AgentId == agentId);
            if (agent is null)
            {
                return Respond("""{"detail":"No Agent matches the given query."}""", HttpStatusCode.NotFound);
            }

            agents.Remove(agent);
            return Json(JsonValue.Create($"PC-{agentId} will now be uninstalled.")!);
        }

        return Respond("<h1>Not Found</h1>", HttpStatusCode.NotFound, "text/html");
    }

    private HttpResponseMessage AddClient(JsonNode body)
    {
        var name = body["client"]!["name"]!.GetValue<string>();
        if (clients.Any(c => c.Name == name))
        {
            return Respond("""{"name":["client with this name already exists."]}""", HttpStatusCode.BadRequest);
        }

        var client = new FakeClient(nextId++, name, [new FakeSite(nextId++, body["site"]!["name"]!.GetValue<string>())]);
        clients.Add(client);
        return Json(JsonValue.Create($"{name} was added")!);
    }

    private JsonObject ClientJson(FakeClient c) => new()
    {
        ["id"] = c.Id,
        ["name"] = c.Name,
        ["server_policy"] = null,
        ["workstation_policy"] = null,
        ["alert_template"] = null,
        ["block_policy_inheritance"] = false,
        ["sites"] = new JsonArray(c.Sites.Select(s => (JsonNode?)new JsonObject
        {
            ["id"] = s.Id,
            ["name"] = s.Name,
            ["client"] = c.Id,
            ["client_name"] = c.Name,
            ["custom_fields"] = new JsonArray(),
            ["agent_count"] = agents.Count(a => a.SiteId == s.Id),
        }).ToArray()),
        ["custom_fields"] = new JsonArray(),
        ["agent_count"] = agents.Count(a => a.ClientId == c.Id),
    };

    private JsonObject AgentJson(FakeAgent a)
    {
        var client = clients.Single(c => c.Id == a.ClientId);
        return new JsonObject
        {
            ["agent_id"] = a.AgentId,
            ["hostname"] = $"PC-{a.AgentId}",
            ["client_name"] = client.Name,
            ["site_name"] = client.Sites.Single(s => s.Id == a.SiteId).Name,
            ["monitoring_type"] = "workstation",
            ["maintenance_mode"] = a.Maintenance,
            ["status"] = "online",
        };
    }

    private JsonObject DeploymentJson(FakeDeployment d)
    {
        var client = clients.First(c => c.Sites.Any(s => s.Id == d.SiteId));
        return new JsonObject
        {
            ["id"] = d.Id,
            ["uid"] = d.Uid,
            ["client_id"] = client.Id,
            ["site_id"] = d.SiteId,
            ["client_name"] = client.Name,
            ["site_name"] = client.Sites.Single(s => s.Id == d.SiteId).Name,
            ["mon_type"] = d.MonType,
            ["goarch"] = "amd64",
            ["expiry"] = d.Expiry.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["install_flags"] = new JsonObject { ["power"] = false, ["ping"] = false, ["rdp"] = false },
            ["created"] = "2026-10-27T10:00:00Z",
        };
    }

    private static HttpResponseMessage Json(JsonNode node) => Respond(node.ToJsonString(), HttpStatusCode.OK);

    public static HttpResponseMessage Respond(string text, HttpStatusCode status, string mediaType = "application/json") => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, mediaType),
    };
}

internal sealed record FakeSite(long Id, string Name);

internal sealed record FakeClient(long Id, string Name, List<FakeSite> Sites);

internal sealed record FakeAgent(string AgentId, long ClientId, long SiteId)
{
    public bool Maintenance { get; set; }
}

internal sealed record FakeDeployment(long Id, string Uid, long SiteId, string MonType, DateTimeOffset Expiry);

/// <summary>Junta los logs del módulo y de la SDK, para comprobar que no llevan secretos.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly List<string> lines = [];

    public string Text
    {
        get
        {
            lock (lines)
            {
                return string.Join("\n", lines);
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.lines)
            {
                owner.lines.Add($"{formatter(state, exception)} {exception}");
            }
        }
    }
}

/// <summary>
/// Una réplica del módulo corriendo en un TestServer, con el Tactical falso del otro lado. Firma los pedidos como
/// el núcleo. Cada réplica tiene su propio registro de idempotencia, como dos nodos detrás de un balanceador.
/// </summary>
internal sealed class TrmmHost : IAsyncDisposable
{
    /// <summary>Generado en cada corrida, como el de una instalación.</summary>
    public static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    private readonly WebApplication app;
    private readonly string db;

    private TrmmHost(WebApplication app, string db, FakeTactical fake, LogCapture logs)
    {
        this.app = app;
        this.db = db;
        Fake = fake;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public FakeTactical Fake { get; }

    public LogCapture Logs { get; }

    public HttpClient Client { get; }

    /// <summary>Con stepBudget en cero, cada paso de la baja forzada desinstala un solo agente.</summary>
    public static async Task<TrmmHost> Start(FakeTactical fake, HttpClient? http = null, TimeSpan? stepBudget = null)
    {
        var logs = new LogCapture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"trmm-idem-{Guid.NewGuid():N}.db");
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BillingOss.Module.TacticalRmm");
        app.MapModule(
            TacticalRmmModule.Create(http ?? new HttpClient(fake), logger, stepBudget),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new TrmmHost(app, db, fake, logs);
    }

    public JsonObject Config() => new()
    {
        ["api_url"] = FakeTactical.Origin.AbsoluteUri,
        ["api_key"] = Fake.ApiKey,
        ["client_name_prefix"] = "BOSS-",
    };

    /// <summary>El contexto de un servicio, como lo arma el núcleo.</summary>
    public static JsonObject Service(
        long id = 1042,
        FakeClient? client = null,
        int? agentLimit = 5,
        string status = "active",
        string language = "es")
    {
        var external = new JsonObject();
        if (client is not null)
        {
            external["client_id"] = client.Id.ToString(CultureInfo.InvariantCulture);
            external["site_id"] = client.Sites[0].Id.ToString(CultureInfo.InvariantCulture);
        }

        var parameters = new JsonObject();
        if (agentLimit is { } limit)
        {
            parameters["agent_limit"] = limit;
        }

        return new JsonObject
        {
            ["service"] = new JsonObject
            {
                ["id"] = id,
                ["status"] = status,
                ["label"] = null,
                ["external_ids"] = external,
                ["params"] = parameters,
                ["order"] = new JsonObject(),
                ["plan"] = new JsonObject { ["unit"] = "month", ["count"] = 1 },
            },
            ["account"] = new JsonObject { ["id"] = 77, ["language"] = language },
        };
    }

    public Task<(HttpStatusCode Status, JsonNode? Body)> Op(string operation, JsonObject context, string? key = null, JsonObject? input = null, JsonObject? config = null) =>
        Send($"/v0/provisioning/{operation}", context, input ?? [], key, config);

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject context, JsonObject input, string? key = null, JsonObject? config = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = (config ?? Config()).DeepClone(), ["state"] = null },
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

    public static string? Cause(JsonNode? body) => body?["error"]?["details"]?["cause"]?.GetValue<string>();

    public static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}
