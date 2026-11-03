using System.Collections.Concurrent;
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

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>Un pedido que el módulo mandó al panel falso.</summary>
internal sealed record Sent(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)
{
    /// <summary>Método y ruta desde la raíz de la Application API, sin la query. Por ejemplo "GET servers/external/1042".</summary>
    public string Route => $"{Method} {Url.AbsolutePath[FakePanel.ApiPath.Length..]}";

    public string Query(string name) => QueryHelpers.ParseQuery(Url.Query).GetValueOrDefault(name).ToString();
}

internal sealed class FakeUser
{
    public int Id { get; init; }

    public string? ExternalId { get; set; }

    public required string Username { get; set; }

    public required string Email { get; set; }

    public string First { get; set; } = "Nombre";

    public string Last { get; set; } = "Apellido";

    public string? Password { get; set; }

    public bool RootAdmin { get; set; }
}

internal sealed record FakeAllocation(int Id, int Node, string Ip, string? Alias, int Port)
{
    public int? Server { get; set; }
}

internal sealed record FakeVariable(string Name, string Env, string Default);

internal sealed record FakeEgg(int Id, int Nest, string Startup, Dictionary<string, string> Images, List<FakeVariable> Variables);

internal sealed class FakeServer
{
    public int Id { get; init; }

    public string? ExternalId { get; set; }

    public string Identifier { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Status { get; set; }

    public int User { get; set; }

    public int Node { get; set; }

    public int Allocation { get; set; }

    public int Nest { get; set; }

    public int Egg { get; set; }

    public int Memory { get; set; }

    public int Swap { get; set; }

    public int Disk { get; set; }

    public int Io { get; set; } = 500;

    public int Cpu { get; set; }

    public bool Oom { get; set; }

    public int? Databases { get; set; }

    public int? AllocationLimit { get; set; }

    public int? Backups { get; set; }

    public string Startup { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public Dictionary<string, string> Env { get; set; } = [];
}

/// <summary>
/// Panel Pterodactyl falso con estado: guarda usuarios, servers y allocations, y contesta con las formas de la
/// Application API pública (objetos con object y attributes, listas con meta.pagination y errores con errors[]).
/// Cada test puede pisar una ruta para simular fallas.
/// </summary>
internal sealed class FakePanel : HttpMessageHandler
{
    public const string Url = "https://panel.example.test/";
    public const string ApiPath = "/api/application/";
    public const string DefaultKey = "ptla_clave-inventada-para-pruebas-0000000";

    private readonly Lock gate = new();
    private readonly List<Sent> requests = [];
    private readonly Dictionary<string, (Func<Sent, Func<HttpResponseMessage>, HttpResponseMessage> Handler, int Times)> overrides = [];
    private int nextServer = 100;
    private int nextUser = 10;
    private int nextAllocation = 1000;

    public string Key { get; init; } = DefaultKey;

    /// <summary>Permisos de lectura que la clave no tiene: nodes, allocations, users, locations, nests, eggs o servers.</summary>
    public HashSet<string> Denied { get; } = [];

    public List<FakeUser> Users { get; } = [];

    public List<(int Id, int Location)> Nodes { get; } = [];

    public List<FakeAllocation> Allocations { get; } = [];

    public List<FakeEgg> Eggs { get; } = [];

    public List<FakeServer> Servers { get; } = [];

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

    /// <summary>El panel de las pruebas: dos nodos, eggs de Minecraft, DayZ y ARK.</summary>
    public static FakePanel Standard(string? key = null)
    {
        var panel = new FakePanel { Key = key ?? DefaultKey };
        panel.Node(1, location: 3, ip: "0.0.0.0", alias: "mc.example.test", from: 25565, to: 25600);
        panel.Node(2, location: 5, ip: "10.20.30.5", alias: null, from: 2300, to: 2340);
        panel.Eggs.Add(Samples.Paper);
        panel.Eggs.Add(Samples.Fabric);
        panel.Eggs.Add(Samples.DayZ);
        panel.Eggs.Add(Samples.Ark);
        return panel;
    }

    public FakePanel Node(int id, int location, string ip, string? alias, int from, int to)
    {
        Nodes.Add((id, location));
        for (var port = from; port <= to; port++)
        {
            Allocations.Add(new FakeAllocation(nextAllocation++, id, ip, alias, port));
        }

        return this;
    }

    /// <summary>Pisa una ruta. El handler recibe next, que ejecuta lo que haría el panel, para simular un corte después.</summary>
    public FakePanel On(string route, Func<Sent, Func<HttpResponseMessage>, HttpResponseMessage> handler, int times = int.MaxValue)
    {
        lock (gate)
        {
            overrides[route] = (handler, times);
        }

        return this;
    }

    public FakePanel On(string route, HttpStatusCode status, string json = "{}", int times = int.MaxValue) =>
        On(route, (_, _) => Samples.Json(json, status), times);

    /// <summary>El panel ejecuta el pedido y la respuesta se pierde.</summary>
    public FakePanel CutAfter(string route, int times = 1) =>
        On(route, (_, next) =>
        {
            next();
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
        }, times);

    public int Count(string route) => Requests.Count(r => r.Route == route);

    public int Writes => Requests.Count(r => r.Method != HttpMethod.Get);

    public Sent Last(string route) => Requests.Last(r => r.Route == route);

    public FakeServer Server(long externalId) => Servers.Single(s => s.ExternalId == externalId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Un server que ya existe en el panel, como los que creó el módulo de hoy. Sin user es del usuario de la cuenta
    /// de las pruebas, con su external_id, como los deja el alta.
    /// </summary>
    public FakeServer AddServer(long externalId, FakeEgg egg, int node = 1, int port = 25565, string? status = null, int? user = null)
    {
        var allocation = Allocations.Single(a => a.Node == node && a.Port == port);
        var account = Samples.AccountId.ToString(CultureInfo.InvariantCulture);
        var owner = user ?? (Users.FirstOrDefault(u => u.ExternalId == account) ?? AddUser(account, $"cliente{account}@example.com")).Id;
        var server = new FakeServer
        {
            Id = nextServer++,
            ExternalId = externalId.ToString(CultureInfo.InvariantCulture),
            Identifier = $"{nextServer:x8}"[..8],
            Name = "Servidor previo",
            Status = status,
            User = owner,
            Node = node,
            Allocation = allocation.Id,
            Nest = egg.Nest,
            Egg = egg.Id,
            Memory = 1024,
            Disk = 5120,
            Cpu = 100,
            Oom = true,
            Databases = 0,
            AllocationLimit = 0,
            Backups = 0,
            Startup = egg.Startup,
            Image = egg.Images.Values.First(),
            Env = egg.Variables.ToDictionary(v => v.Env, v => v.Default),
        };
        allocation.Server = server.Id;
        Servers.Add(server);
        return server;
    }

    public FakeUser AddUser(string? externalId, string email)
    {
        var user = new FakeUser { Id = nextUser++, ExternalId = externalId, Username = $"previo{nextUser}", Email = email };
        Users.Add(user);
        return user;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new Sent(request.Method, request.RequestUri!, headers, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
        lock (gate)
        {
            requests.Add(sent);
            if (!request.RequestUri!.AbsolutePath.StartsWith(ApiPath, StringComparison.Ordinal) || headers.GetValueOrDefault("Authorization") != $"Bearer {Key}")
            {
                return Samples.Error(HttpStatusCode.Unauthorized, "AuthenticationException", "Unauthenticated.");
            }

            if (Denied.Contains(Permission(sent)))
            {
                return Samples.Error(HttpStatusCode.Forbidden, "AccessDeniedHttpException", "This action is unauthorized.");
            }

            if (overrides.TryGetValue(sent.Route, out var entry) && entry.Times > 0)
            {
                overrides[sent.Route] = (entry.Handler, entry.Times - 1);
                return entry.Handler(sent, () => Handle(sent));
            }

            return Handle(sent);
        }
    }

    private static string Permission(Sent sent)
    {
        var parts = sent.Route.Split(' ')[1].Split('/');
        return parts switch
        {
            ["nodes", _, "allocations", ..] => "allocations",
            ["nests", _, "eggs", ..] => "eggs",
            _ => parts[0],
        };
    }

    private HttpResponseMessage Handle(Sent s)
    {
        var parts = s.Route.Split(' ')[1].Split('/');
        var include = s.Query("include").Contains("allocations", StringComparison.Ordinal);
        int Id(string text) => int.Parse(text, CultureInfo.InvariantCulture);
        return (s.Method.Method, parts) switch
        {
            ("GET", ["nodes"]) => List(s, Nodes.Select(n => Item("node", new JsonObject { ["id"] = n.Id, ["location_id"] = n.Location }))),
            ("GET", ["nodes", "deployable"]) => Deployable(s),
            ("GET", ["nodes", var node]) => Nodes.Where(n => n.Id == Id(node)).Select(n => Item("node", new JsonObject { ["id"] = n.Id, ["location_id"] = n.Location }))
                .FirstOrDefault() is { } found ? Samples.Json(found.ToJsonString()) : NotFound(),
            ("GET", ["nodes", var node, "allocations"]) => List(s, Allocations.Where(a => a.Node == Id(node)).Select(AllocationJson)),
            ("GET", ["locations"]) => List(s, Nodes.Select(n => n.Location).Distinct().Select(l => Item("location", new JsonObject { ["id"] = l }))),
            ("GET", ["nests"]) => List(s, Eggs.Select(e => e.Nest).Distinct().Select(n => Item("nest", new JsonObject { ["id"] = n }))),
            ("GET", ["nests", var nest, "eggs"]) => List(s, Eggs.Where(e => e.Nest == Id(nest)).Select(EggJson)),
            ("GET", ["nests", var nest, "eggs", var egg]) => Eggs.FirstOrDefault(e => e.Nest == Id(nest) && e.Id == Id(egg)) is { } found
                ? Samples.Json(EggJson(found).ToJsonString())
                : NotFound(),
            ("GET", ["users"]) => List(s, Users.Where(u => s.Query("filter[email]") is not { Length: > 0 } f || u.Email.Contains(f, StringComparison.OrdinalIgnoreCase)).Select(UserJson)),
            ("GET", ["users", "external", var ext]) => One(Users.FirstOrDefault(u => u.ExternalId == ext), UserJson),
            ("GET", ["users", var id]) => One(Users.FirstOrDefault(u => u.Id == Id(id)), UserJson),
            ("POST", ["users"]) => CreateUser(s.Body!),
            ("PATCH", ["users", var id]) => UpdateUser(Id(id), s.Body!),
            ("GET", ["servers"]) => List(s, Servers.Select(x => ServerJson(x, false))),
            ("GET", ["servers", "external", var ext]) => One(Servers.FirstOrDefault(x => x.ExternalId == ext), x => ServerJson(x, include)),
            ("GET", ["servers", var id]) => One(Servers.FirstOrDefault(x => x.Id == Id(id)), x => ServerJson(x, include)),
            ("POST", ["servers"]) => CreateServer(s.Body!, include),
            ("POST", ["servers", var id, var action]) => Toggle(Id(id), action == "suspend"),
            ("DELETE", ["servers", var id]) => Delete(Id(id)),
            ("PATCH", ["servers", var id, "build"]) => Build(Id(id), s.Body!, include),
            ("PATCH", ["servers", var id, "startup"]) => Startup(Id(id), s.Body!, include),
            _ => throw new InvalidOperationException($"El panel falso no tiene la ruta {s.Route}."),
        };
    }

    /// <summary>Como el panel: sin ningún nodo con lugar en la location no da una lista vacía, da 400 NoViableNodeException.</summary>
    private HttpResponseMessage Deployable(Sent s)
    {
        var nodes = Nodes.Where(n => s.Query("location_ids[]") == n.Location.ToString(CultureInfo.InvariantCulture)).ToList();
        return nodes.Count == 0
            ? Samples.Error(HttpStatusCode.BadRequest, "NoViableNodeException", "No nodes satisfying the requirements specified were found.")
            : List(s, nodes.Select(n => Item("node", new JsonObject { ["id"] = n.Id, ["location_id"] = n.Location })));
    }

    private HttpResponseMessage CreateUser(JsonNode body)
    {
        var email = body["email"]?.GetValue<string>() ?? string.Empty;
        var external = body["external_id"]?.GetValue<string>();
        if (Users.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            return Samples.Validation("email", "unique");
        }

        if (external is not null && Users.Any(u => u.ExternalId == external))
        {
            return Samples.Validation("external_id", "unique");
        }

        var user = new FakeUser
        {
            Id = nextUser++,
            ExternalId = external,
            Email = email,
            Username = body["username"]!.GetValue<string>(),
            First = body["first_name"]!.GetValue<string>(),
            Last = body["last_name"]!.GetValue<string>(),
            Password = body["password"]?.GetValue<string>(),
        };
        Users.Add(user);
        return Samples.Json(UserJson(user).ToJsonString(), HttpStatusCode.Created);
    }

    private HttpResponseMessage UpdateUser(int id, JsonNode body)
    {
        if (Users.FirstOrDefault(u => u.Id == id) is not { } user)
        {
            return NotFound();
        }

        // El panel exige los datos completos del usuario en cada cambio.
        foreach (var field in new[] { "email", "username", "first_name", "last_name" })
        {
            if (body[field]?.GetValue<string>() is not { Length: > 0 })
            {
                return Samples.Validation(field, "required");
            }
        }

        user.Email = body["email"]!.GetValue<string>();
        user.Username = body["username"]!.GetValue<string>();
        user.First = body["first_name"]!.GetValue<string>();
        user.Last = body["last_name"]!.GetValue<string>();
        user.Password = body["password"]?.GetValue<string>() ?? user.Password;
        return Samples.Json(UserJson(user).ToJsonString());
    }

    private HttpResponseMessage CreateServer(JsonNode b, bool include)
    {
        var external = b["external_id"]?.GetValue<string>();
        if (external is not null && Servers.Any(x => x.ExternalId == external))
        {
            return Samples.Validation("external_id", "unique");
        }

        var egg = Eggs.FirstOrDefault(e => e.Id == b["egg"]!.GetValue<int>());
        if (egg is null || Users.All(u => u.Id != b["user"]!.GetValue<int>()))
        {
            return Samples.Validation(egg is null ? "egg" : "user", "exists");
        }

        FakeAllocation primary;
        var additional = new List<FakeAllocation>();
        if (b["allocation"] is JsonObject chosen)
        {
            FakeAllocation? Free(JsonNode? id) => Allocations.FirstOrDefault(a => a.Id == id!.GetValue<int>() && a.Server is null);
            if (Free(chosen["default"]) is not { } first)
            {
                return Samples.Validation("allocation.default", "exists");
            }

            primary = first;

            foreach (var (i, id) in (chosen["additional"]?.AsArray() ?? []).Index())
            {
                if (Free(id) is not { } extra)
                {
                    return Samples.Validation($"allocation.additional.{i}", "exists");
                }

                additional.Add(extra);
            }
        }
        else
        {
            var locations = b["deploy"]!["locations"]!.AsArray().Select(l => l!.GetValue<int>()).ToList();
            var nodes = Nodes.Where(n => locations.Contains(n.Location)).Select(n => n.Id).ToList();
            var free = Allocations.FirstOrDefault(a => nodes.Contains(a.Node) && a.Server is null);
            if (free is null)
            {
                return Samples.Error(HttpStatusCode.BadRequest, "NoViableNodeException", "No nodes satisfying the requirements specified were found.");
            }

            primary = free;
        }

        var limits = b["limits"]!;
        var features = b["feature_limits"]!;
        var server = new FakeServer
        {
            Id = nextServer++,
            ExternalId = external,
            Identifier = $"{nextServer * 7919:x8}"[..8],
            Name = b["name"]!.GetValue<string>(),
            Status = "installing",
            User = b["user"]!.GetValue<int>(),
            Node = primary.Node,
            Allocation = primary.Id,
            Nest = egg.Nest,
            Egg = egg.Id,
            Memory = limits["memory"]!.GetValue<int>(),
            Swap = limits["swap"]!.GetValue<int>(),
            Disk = limits["disk"]!.GetValue<int>(),
            Io = limits["io"]!.GetValue<int>(),
            Cpu = limits["cpu"]!.GetValue<int>(),
            Oom = b["oom_disabled"]?.GetValue<bool>() ?? false,
            Databases = features["databases"]?.GetValue<int>(),
            AllocationLimit = features["allocations"]?.GetValue<int>(),
            Backups = features["backups"]?.GetValue<int>(),
            Startup = b["startup"]!.GetValue<string>().Trim(),
            Image = b["docker_image"]!.GetValue<string>().Trim(),
            Env = egg.Variables.ToDictionary(v => v.Env, v => b["environment"]?[v.Env]?.GetValue<string>().Trim() ?? v.Default),
        };
        primary.Server = server.Id;
        additional.ForEach(a => a.Server = server.Id);
        Servers.Add(server);
        return Samples.Json(ServerJson(server, include).ToJsonString(), HttpStatusCode.Created);
    }

    private HttpResponseMessage Toggle(int id, bool suspend)
    {
        if (Servers.FirstOrDefault(x => x.Id == id) is not { } server)
        {
            return NotFound();
        }

        server.Status = suspend ? "suspended" : server.Status == "suspended" ? null : server.Status;
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Delete(int id)
    {
        if (Servers.FirstOrDefault(x => x.Id == id) is not { } server)
        {
            return NotFound();
        }

        Servers.Remove(server);
        foreach (var allocation in Allocations.Where(a => a.Server == id))
        {
            allocation.Server = null;
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Build(int id, JsonNode b, bool include)
    {
        if (Servers.FirstOrDefault(x => x.Id == id) is not { } server)
        {
            return NotFound();
        }

        if (b["feature_limits"] is null || b["allocation"]?.GetValue<int>() != server.Allocation)
        {
            return Samples.Validation(b["feature_limits"] is null ? "feature_limits" : "allocation", "required");
        }

        var limits = b["limits"]!;
        server.Memory = limits["memory"]!.GetValue<int>();
        server.Swap = limits["swap"]!.GetValue<int>();
        server.Disk = limits["disk"]!.GetValue<int>();
        server.Io = limits["io"]!.GetValue<int>();
        server.Cpu = limits["cpu"]!.GetValue<int>();
        server.Oom = b["oom_disabled"]!.GetValue<bool>();
        server.Databases = b["feature_limits"]!["databases"]?.GetValue<int>();
        server.AllocationLimit = b["feature_limits"]!["allocations"]?.GetValue<int>();
        server.Backups = b["feature_limits"]!["backups"]?.GetValue<int>();
        // Como el panel: suma solo las libres del mismo nodo y saltea las demás sin error.
        foreach (var add in b["add_allocations"]?.AsArray() ?? [])
        {
            var allocation = Allocations.Single(a => a.Id == add!.GetValue<int>());
            if (allocation.Server is null && allocation.Node == server.Node)
            {
                allocation.Server = id;
            }
        }

        return Samples.Json(ServerJson(server, include).ToJsonString());
    }

    private HttpResponseMessage Startup(int id, JsonNode b, bool include)
    {
        if (Servers.FirstOrDefault(x => x.Id == id) is not { } server)
        {
            return NotFound();
        }

        // Como el middleware TrimStrings del panel, que recorta todo texto de entrada.
        var egg = Eggs.Single(e => e.Id == b["egg"]!.GetValue<int>());
        server.Env = egg.Variables.ToDictionary(v => v.Env, v => b["environment"]?[v.Env]?.GetValue<string>().Trim() ?? server.Env.GetValueOrDefault(v.Env) ?? v.Default);
        server.Egg = egg.Id;
        server.Nest = egg.Nest;
        server.Startup = b["startup"]!.GetValue<string>().Trim();
        server.Image = b["image"]!.GetValue<string>().Trim();
        return Samples.Json(ServerJson(server, include).ToJsonString());
    }

    private JsonObject ServerJson(FakeServer x, bool include)
    {
        // El panel suma a environment variables propias que no son del egg.
        var environment = new JsonObject(x.Env.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value)))
        {
            ["STARTUP"] = x.Startup,
            ["P_SERVER_UUID"] = "00000000-0000-0000-0000-000000000000",
        };
        var attributes = new JsonObject
        {
            ["id"] = x.Id,
            ["external_id"] = x.ExternalId,
            ["uuid"] = "00000000-0000-0000-0000-000000000000",
            ["identifier"] = x.Identifier,
            ["name"] = x.Name,
            ["status"] = x.Status,
            ["suspended"] = x.Status == "suspended",
            ["limits"] = new JsonObject
            {
                ["memory"] = x.Memory, ["swap"] = x.Swap, ["disk"] = x.Disk, ["io"] = x.Io, ["cpu"] = x.Cpu, ["threads"] = null, ["oom_disabled"] = x.Oom,
            },
            ["feature_limits"] = new JsonObject { ["databases"] = x.Databases, ["allocations"] = x.AllocationLimit, ["backups"] = x.Backups },
            ["user"] = x.User,
            ["node"] = x.Node,
            ["allocation"] = x.Allocation,
            ["nest"] = x.Nest,
            ["egg"] = x.Egg,
            ["container"] = new JsonObject
            {
                ["startup_command"] = x.Startup,
                ["image"] = x.Image,
                ["installed"] = x.Status == "installing" ? 0 : 1,
                ["environment"] = environment,
            },
        };
        if (include)
        {
            // Sin permiso de lectura de allocations el panel contesta null_resource, sin error.
            attributes["relationships"] = new JsonObject
            {
                ["allocations"] = Denied.Contains("allocations")
                    ? new JsonObject { ["object"] = "null_resource", ["attributes"] = null }
                    : new JsonObject
                    {
                        ["object"] = "list",
                        ["data"] = new JsonArray(Allocations.Where(a => a.Server == x.Id).Select(a => (JsonNode?)AllocationJson(a)).ToArray()),
                    },
            };
        }

        return Item("server", attributes);
    }

    private static JsonObject AllocationJson(FakeAllocation a) => Item("allocation", new JsonObject
    {
        ["id"] = a.Id, ["ip"] = a.Ip, ["alias"] = a.Alias, ["port"] = a.Port, ["notes"] = null, ["assigned"] = a.Server is not null,
    });

    private static JsonObject UserJson(FakeUser u) => Item("user", new JsonObject
    {
        ["id"] = u.Id, ["external_id"] = u.ExternalId, ["username"] = u.Username, ["email"] = u.Email,
        ["first_name"] = u.First, ["last_name"] = u.Last, ["language"] = "en", ["root_admin"] = u.RootAdmin,
    });

    private static JsonObject EggJson(FakeEgg e) => Item("egg", new JsonObject
    {
        ["id"] = e.Id,
        ["nest"] = e.Nest,
        ["docker_images"] = new JsonObject(e.Images.Select(i => KeyValuePair.Create(i.Key, (JsonNode?)i.Value))),
        ["startup"] = e.Startup,
        ["relationships"] = new JsonObject
        {
            ["variables"] = new JsonObject
            {
                ["object"] = "list",
                ["data"] = new JsonArray(e.Variables.Select(v => (JsonNode?)Item("egg_variable", new JsonObject
                {
                    ["name"] = v.Name, ["env_variable"] = v.Env, ["default_value"] = v.Default, ["user_editable"] = true,
                })).ToArray()),
            },
        },
    });

    private static JsonObject Item(string type, JsonObject attributes) => new() { ["object"] = type, ["attributes"] = attributes };

    private static HttpResponseMessage One<T>(T? item, Func<T, JsonObject> json) where T : class =>
        item is null ? NotFound() : Samples.Json(json(item).ToJsonString());

    private static HttpResponseMessage NotFound() =>
        Samples.Error(HttpStatusCode.NotFound, "NotFoundHttpException", "The requested resource could not be found on the server.");

    /// <summary>Una página de un listado, con per_page y page como el panel.</summary>
    private static HttpResponseMessage List(Sent s, IEnumerable<JsonObject> items)
    {
        var all = items.ToList();
        var perPage = int.TryParse(s.Query("per_page"), CultureInfo.InvariantCulture, out var p) ? p : 50;
        var page = int.TryParse(s.Query("page"), CultureInfo.InvariantCulture, out var n) ? n : 1;
        var data = all.Skip((page - 1) * perPage).Take(perPage).ToList();
        var json = new JsonObject
        {
            ["object"] = "list",
            ["data"] = new JsonArray(data.Select(d => (JsonNode?)d).ToArray()),
            ["meta"] = new JsonObject
            {
                ["pagination"] = new JsonObject
                {
                    ["total"] = all.Count,
                    ["count"] = data.Count,
                    ["per_page"] = perPage,
                    ["current_page"] = page,
                    ["total_pages"] = Math.Max(1, (all.Count + perPage - 1) / perPage),
                },
            },
        };
        return Samples.Json(json.ToJsonString());
    }
}

/// <summary>Captura lo que se registra, para comprobar que no se filtran secretos.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public string All => string.Join("\n", Lines);

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{formatter(state, exception)} {exception}");
    }
}

/// <summary>El módulo corriendo en un TestServer con el panel falso del otro lado. Firma los pedidos como el núcleo.</summary>
internal sealed class PteroHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-prueba");

    private readonly WebApplication app;
    private readonly string db;

    private PteroHost(WebApplication app, string db, FakePanel panel)
    {
        this.app = app;
        this.db = db;
        Panel = panel;
        Client = app.GetTestClient();
    }

    public FakePanel Panel { get; }

    public HttpClient Client { get; }

    public static async Task<PteroHost> Start(FakePanel panel, HttpClient? http = null, LogCapture? logs = null, string? db = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
        }

        var app = builder.Build();
        db ??= Path.Combine(Path.GetTempPath(), $"ptero-idem-{Guid.NewGuid():N}.db");
        app.MapModule(
            PterodactylModule.Create(http ?? new HttpClient(panel), app.Logger),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new PteroHost(app, db, panel);
    }

    public static JsonObject Config(string? key = null) => new()
    {
        ["panel_url"] = FakePanel.Url,
        ["application_key"] = key ?? FakePanel.DefaultKey,
    };

    /// <summary>Una operación de provisioning con el sobre del protocolo.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Call(string operation, JsonObject context, string? key = null, JsonObject? input = null, JsonObject? config = null) =>
        Send(operation.StartsWith('/') ? operation : $"/v0/provisioning/{operation}", context, input ?? [], key, config);

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject context, JsonObject input, string? key = null, JsonObject? config = null, JsonObject? extra = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = (config ?? Config()).DeepClone(), ["state"] = null },
            ["context"] = context.DeepClone(),
            ["input"] = input.DeepClone(),
        };
        foreach (var (name, value) in extra ?? [])
        {
            envelope[name] = value?.DeepClone();
        }

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
    public async Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path, byte[]? secret = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(secret ?? Secret, timestamp, "GET", path, requestId, null, []));
        using var response = await Client.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
    }

    public static string? Code(JsonNode? body) => body?["error"]?["code"]?.GetValue<string>();

    public static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}

/// <summary>Contextos, eggs y respuestas sintéticas para las pruebas.</summary>
internal static class Samples
{
    public const long ServiceId = 1042;
    public const long AccountId = 77;

    public static readonly FakeEgg Paper = new(5, 1, "java -Xms128M -jar {{SERVER_JARFILE}}",
        new() { ["Java 21"] = "ghcr.io/pterodactyl/yolks:java_21", ["Java 17"] = "ghcr.io/pterodactyl/yolks:java_17" },
        [new("Server Jar File", "SERVER_JARFILE", "server.jar"), new("Minecraft Version", "MC_VERSION", "latest"), new("Build Number", "BUILD_NUMBER", "latest")]);

    public static readonly FakeEgg Fabric = new(6, 1, "java -jar {{SERVER_JARFILE}} nogui",
        new() { ["Java 21"] = "ghcr.io/pterodactyl/yolks:java_21" },
        [new("Server Jar File", "SERVER_JARFILE", "fabric.jar"), new("Minecraft Version", "MC_VERSION", "latest"), new("Fabric Version", "FABRIC_VERSION", "latest")]);

    public static readonly FakeEgg DayZ = new(7, 2, "./DayZServer -port={{SERVER_PORT}}",
        new() { ["DayZ"] = "ghcr.io/example/dayz:latest" },
        [new("Query Port", "QUERY_PORT", ""), new("RCON Port", "RCON_PORT", ""), new("RCON Password", "RCON_PASSWORD", "cambiar")]);

    public static readonly FakeEgg Ark = new(8, 2, "./ShooterGameServer",
        new() { ["ARK"] = "ghcr.io/example/ark:latest" },
        [new("Server Password", "ARK_PASSWORD", ""), new("Admin Password", "ARK_ADMIN_PASSWORD", ""), new("Server Name", "SERVER_NAME", "ARK"), new("Query Port", "QUERY_PORT", "")]);

    /// <summary>Un producto de Minecraft como los de la copia: 1024 MB, 5120 MB de disco y 100 % de CPU en la location 3.</summary>
    public static JsonObject Minecraft() => new()
    {
        ["nest_id"] = 1,
        ["egg_id"] = 5,
        ["location_id"] = 3,
        ["memory_mb"] = 1024,
        ["disk_mb"] = 5120,
        ["cpu_percent"] = 100,
        ["io_weight"] = 500,
        ["oom_disabled"] = true,
        ["variables"] = new JsonObject { ["Minecraft Version"] = "1.21.1" },
    };

    /// <summary>DayZ: 3 puertos contiguos, QUERY_PORT en base+1 y RCON_PORT en base+3, en la location 5.</summary>
    public static JsonObject DayZPlan(bool contiguous = true) => new()
    {
        ["nest_id"] = 2,
        ["egg_id"] = 7,
        ["location_id"] = 5,
        ["memory_mb"] = 6144,
        ["disk_mb"] = 10240,
        ["cpu_percent"] = 100,
        ["extra_ports"] = new JsonObject
        {
            ["count"] = 3,
            ["contiguous"] = contiguous,
            ["variables"] = new JsonObject { ["QUERY_PORT"] = 1, ["RCON_PORT"] = 3 },
        },
    };

    public static JsonObject Account(long id = AccountId, string? name = "Ana María Pérez", string? email = "cliente77@example.com", string language = "es") =>
        new() { ["id"] = id, ["name"] = name, ["email"] = email, ["language"] = language };

    public static JsonObject Context(long service = ServiceId, JsonObject? plan = null, JsonObject? order = null, JsonObject? externalIds = null, JsonObject? account = null) => new()
    {
        ["service"] = new JsonObject
        {
            ["id"] = service,
            ["status"] = "pending",
            ["label"] = null,
            ["external_ids"] = externalIds?.DeepClone() ?? new JsonObject(),
            ["params"] = plan?.DeepClone() ?? Minecraft(),
            ["order"] = order?.DeepClone() ?? new JsonObject(),
            ["plan"] = new JsonObject { ["unit"] = "month", ["count"] = 1 },
        },
        ["account"] = account?.DeepClone() ?? Account(),
    };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>Un error con la forma del panel: errors[] con code, status y detail.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string code, string detail) => Json(
        new JsonObject
        {
            ["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["status"] = ((int)status).ToString(CultureInfo.InvariantCulture), ["detail"] = detail }),
        }.ToJsonString(),
        status);

    /// <summary>Un 422 de validación: el campo va en meta.source_field.</summary>
    public static HttpResponseMessage Validation(string field, string rule) => Json(
        new JsonObject
        {
            ["errors"] = new JsonArray(new JsonObject
            {
                ["code"] = "ValidationException",
                ["status"] = "422",
                ["detail"] = $"The {field} field is invalid.",
                ["meta"] = new JsonObject { ["source_field"] = field, ["rule"] = rule },
            }),
        }.ToJsonString(),
        HttpStatusCode.UnprocessableEntity);
}
