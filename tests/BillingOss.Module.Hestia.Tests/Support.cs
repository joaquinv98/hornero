using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.Hestia.Tests;

/// <summary>Un comando que el módulo mandó a la Hestia falsa.</summary>
internal sealed record HestiaCall(string Command, string[] Args, string? Hash, string? ReturnCode);

/// <summary>Una cuenta de la Hestia falsa. Cada recurso tiene su propio SUSPENDED, como en Hestia.</summary>
internal sealed class FakeUser
{
    public string Contact { get; set; } = "cliente@example.com";

    public string Name { get; set; } = string.Empty;

    public string Package { get; set; } = "gainsboro";

    public string Language { get; set; } = "en";

    public string Shell { get; set; } = "nologin";

    public string Password { get; set; } = string.Empty;

    public bool Suspended { get; set; }

    /// <summary>Tipo de recurso (web, mail, dns, databases, cron), y por cada uno nombre y si está suspendido.</summary>
    public Dictionary<string, Dictionary<string, bool>> Resources { get; } = new(StringComparer.Ordinal)
    {
        ["web"] = [], ["mail"] = [], ["dns"] = [], ["databases"] = [], ["cron"] = [],
    };

    public void SetAll(bool suspended)
    {
        Suspended = suspended;
        foreach (var kind in Resources.Values)
        {
            foreach (var name in kind.Keys.ToList())
            {
                kind[name] = suspended;
            }
        }
    }
}

/// <summary>
/// Hestia falsa: simula los comandos v-* que usa el módulo sobre cuentas en memoria, con los textos de error de Hestia.
/// Cada test puede pisar la respuesta de un comando, y anota todo lo que recibe.
/// </summary>
internal sealed class FakeHestia : HttpMessageHandler
{
    /// <summary>Valores inventados: no sirven contra ningún Hestia.</summary>
    public const string AccessKeyId = "PRUEBAHESTIA0000001";

    public const string SecretKey = "clave-secreta-inventada-de-prueba";

    /// <summary>Paquetes con su shell, como los de un servidor real: el gratuito sin SSH y los pagos con bash.</summary>
    public static readonly IReadOnlyDictionary<string, string> Packages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["freetier"] = "nologin",
        ["gainsboro"] = "bash",
        ["palegreen"] = "bash",
        ["slategrey"] = "bash",
        ["dns-managment"] = "nologin",
        ["DNSZONE"] = "nologin",
    };

    private static readonly string[] KnownLanguages = ["es", "en", "pt", "de"];

    private readonly Dictionary<string, Func<HestiaCall, Func<(int Status, string Text)>, (int Status, string Text)>> overrides = new(StringComparer.Ordinal);
    private readonly List<HestiaCall> calls = [];

    public Dictionary<string, FakeUser> Users { get; } = new(StringComparer.Ordinal);

    /// <summary>Si está, cada comando adelanta este reloj: así los tests controlan el tiempo de las operaciones.</summary>
    public StepClock? Clock { get; set; }

    public TimeSpan Step { get; set; }

    public IReadOnlyList<HestiaCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public string[] Commands => Calls.Select(c => c.Command).ToArray();

    /// <summary>Los comandos que cambian algo.</summary>
    public string[] Mutations => Commands.Where(c => !c.StartsWith("v-list", StringComparison.Ordinal) && c != "v-search-domain-owner").ToArray();

    public int Count(string command) => Commands.Count(c => c == command);

    /// <summary>Una cuenta con un dominio en web, correo y DNS, una base y un cron.</summary>
    public FakeHestia WithAccount(string username = "alice", string domain = "example.test", bool suspended = false, string package = "gainsboro")
    {
        var user = new FakeUser { Package = package, Shell = Packages[package], Language = "es" };
        user.Resources["web"][domain] = suspended;
        user.Resources["mail"][domain] = suspended;
        user.Resources["dns"][domain] = suspended;
        user.Resources["databases"][$"{username}_db"] = suspended;
        user.Resources["cron"]["1"] = suspended;
        user.Suspended = suspended;
        Users[username] = user;
        return this;
    }

    /// <summary>Pisa la respuesta de un comando. La función recibe el pedido y la respuesta normal, por si la quiere usar.</summary>
    public FakeHestia On(string command, Func<HestiaCall, Func<(int Status, string Text)>, (int Status, string Text)> respond)
    {
        overrides[command] = respond;
        return this;
    }

    public FakeHestia On(string command, int status, string text) => On(command, (_, _) => (status, text));

    /// <summary>Un corte de conexión después de mandar el pedido. Con apply, Hestia llegó a ejecutarlo.</summary>
    public FakeHestia CutAfter(string command, bool apply) => On(command, (_, run) =>
    {
        if (apply)
        {
            run();
        }

        throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var form = QueryHelpers.ParseQuery(body).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        var (status, text) = Respond(form);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    }

    public (int Status, string Text) Respond(IReadOnlyDictionary<string, string> form)
    {
        Clock?.Advance(Step);
        var args = new List<string>();
        for (var i = 1; form.TryGetValue(string.Create(CultureInfo.InvariantCulture, $"arg{i}"), out var arg); i++)
        {
            args.Add(arg);
        }

        var call = new HestiaCall(form.GetValueOrDefault("cmd") ?? string.Empty, [.. args], form.GetValueOrDefault("hash"), form.GetValueOrDefault("returncode"));
        lock (calls)
        {
            calls.Add(call);
        }

        return overrides.TryGetValue(call.Command, out var respond) ? respond(call, () => Default(call)) : Default(call);
    }

    private (int Status, string Text) Default(HestiaCall call)
    {
        lock (Users)
        {
            return Run(call);
        }
    }

    private (int Status, string Text) Run(HestiaCall call)
    {
        if (call.Hash != $"{AccessKeyId}:{SecretKey}")
        {
            return (401, "Error: authentication failed");
        }

        var a = call.Args;
        var user = a.Length > 0 ? Users.GetValueOrDefault(a[0]) : null;
        var missing = (400, $"Error: user {(a.Length > 0 ? a[0] : string.Empty)} doesn't exist");
        if (call.Command.StartsWith("v-list", StringComparison.Ordinal) && (a.Length == 0 || a[^1] != "json"))
        {
            return (200, "sin formato json");
        }

        switch (call.Command)
        {
            case "v-list-users":
                var all = new JsonObject { ["admin"] = new JsonObject { ["PACKAGE"] = "system", ["SUSPENDED"] = "no" } };
                foreach (var (name, u) in Users)
                {
                    all[name] = UserJson(u);
                }

                return Ok(all);
            case "v-list-user":
                return user is null ? missing : Ok(new JsonObject { [a[0]] = UserJson(user) });
            case "v-list-user-packages":
                return Ok(new JsonObject(Packages.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonObject { ["SHELL"] = p.Value }))));
            case "v-list-web-domains" or "v-list-mail-domains" or "v-list-dns-domains" or "v-list-databases" or "v-list-cron-jobs":
                if (user is null)
                {
                    return missing;
                }

                var kind = call.Command switch
                {
                    "v-list-web-domains" => "web",
                    "v-list-mail-domains" => "mail",
                    "v-list-dns-domains" => "dns",
                    "v-list-databases" => "databases",
                    _ => "cron",
                };
                return Ok(new JsonObject(user.Resources[kind].Select(r =>
                    KeyValuePair.Create(r.Key, (JsonNode?)new JsonObject { ["SUSPENDED"] = r.Value ? "yes" : "no" }))));
            case "v-search-domain-owner":
                var owner = Users.FirstOrDefault(u => u.Value.Resources["web"].ContainsKey(a[0]) || u.Value.Resources["dns"].ContainsKey(a[0])).Key;
                // Sin dueño, el script termina con E_NOTEXIST sin imprimir nada y la API lo pasa a 404.
                return owner is null ? (404, string.Empty) : (200, owner);
            case "v-add-user":
                if (user is not null)
                {
                    return (409, $"Error: user {a[0]} exists");
                }

                if (!Packages.TryGetValue(a[3], out var shell))
                {
                    return (400, $"Error: package {a[3]} doesn't exist");
                }

                Users[a[0]] = new FakeUser { Password = a[1], Contact = a[2], Package = a[3], Shell = shell, Name = a.Length > 4 ? a[4] : string.Empty };
                return (200, "OK");
            case "v-add-domain":
                if (user is null)
                {
                    return missing;
                }

                if (Users.Values.Any(u => u.Resources["web"].ContainsKey(a[1]) || u.Resources["dns"].ContainsKey(a[1])))
                {
                    return (409, $"Error: domain {a[1]} exists");
                }

                foreach (var type in new[] { "web", "mail", "dns" })
                {
                    user.Resources[type][a[1]] = user.Suspended;
                }

                return (200, "OK");
            case "v-change-user-language":
                if (user is null)
                {
                    return missing;
                }

                if (!KnownLanguages.Contains(a[1]))
                {
                    return (400, $"Error: language {a[1]} doesn't exist");
                }

                user.Language = a[1];
                return (200, "OK");
            case "v-change-user-password":
                if (user is null)
                {
                    return missing;
                }

                user.Password = a[1];
                return (200, "OK");
            case "v-change-user-package":
                if (user is null)
                {
                    return missing;
                }

                if (!Packages.TryGetValue(a[1], out var packageShell))
                {
                    return (400, $"Error: package {a[1]} doesn't exist");
                }

                user.Package = a[1];
                user.Shell = packageShell;
                return (200, "OK");
            case "v-suspend-user" or "v-unsuspend-user":
                if (user is null)
                {
                    return missing;
                }

                user.SetAll(call.Command == "v-suspend-user");
                return (200, "OK");
            case "v-delete-user":
                return user is null ? missing : Users.Remove(a[0]) ? (200, "OK") : missing;
            default:
                return (400, $"Error: command {call.Command} not found");
        }
    }

    private static JsonObject UserJson(FakeUser user) => new()
    {
        ["NAME"] = user.Name,
        ["PACKAGE"] = user.Package,
        ["CONTACT"] = user.Contact,
        ["SHELL"] = user.Shell,
        ["LANGUAGE"] = user.Language,
        ["SUSPENDED"] = user.Suspended ? "yes" : "no",
        ["U_DISK"] = "120",
        ["DISK_QUOTA"] = "1000",
        ["U_BANDWIDTH"] = "35",
        ["BANDWIDTH"] = "unlimited",
    };

    private static (int, string) Ok(JsonNode json) => (200, json.ToJsonString());
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

/// <summary>
/// El módulo corriendo en un TestServer, con la Hestia falsa del otro lado. Firma los pedidos como el núcleo. Cada host
/// tiene su propio registro de idempotencia, como una réplica.
/// </summary>
internal sealed class HestiaHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-firma-de-prueba");

    /// <summary>La contraseña que manda el núcleo en las pruebas. Inventada, con el alfabeto de Passwords.New.</summary>
    public const string Password = "Pr8eba-Inventada_x2.Q";

    private readonly WebApplication app;
    private readonly string db;

    private HestiaHost(WebApplication app, string db, FakeHestia hestia)
    {
        this.app = app;
        this.db = db;
        Hestia = hestia;
        Client = app.GetTestClient();
    }

    public FakeHestia Hestia { get; }

    public HttpClient Client { get; }

    public static async Task<HestiaHost> Start(FakeHestia hestia, Func<string?, HttpClient>? http = null, TimeProvider? time = null, ILogger? logger = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"hestia-idem-{Guid.NewGuid():N}.db");
        var client = new HttpClient(hestia, disposeHandler: false);
        app.MapModule(
            HestiaModule.Create(http ?? (_ => client), logger ?? NullLogger.Instance, time),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new HestiaHost(app, db, hestia);
    }

    public static JsonObject Config(string host = "panel.example.test", int port = 8083) => new()
    {
        ["host"] = host,
        ["port"] = port,
        ["access_key_id"] = FakeHestia.AccessKeyId,
        ["secret_key"] = FakeHestia.SecretKey,
    };

    /// <summary>Contexto de un servicio que ya tiene cuenta en Hestia, como uno importado.</summary>
    public static JsonObject Existing(string username = "alice", string domain = "example.test", string package = "gainsboro", long id = 1042, string language = "es") =>
        Context(id, new JsonObject { ["username"] = username, ["domain"] = domain }, package, new JsonObject(), language);

    /// <summary>Contexto de un alta: el dominio viene del pedido.</summary>
    public static JsonObject NewService(string domain = "nuevo.example.test", string package = "gainsboro", long id = 1042, string language = "es-AR") =>
        Context(id, new JsonObject(), package, new JsonObject { ["domain"] = domain }, language);

    private static JsonObject Context(long id, JsonObject externalIds, string package, JsonObject order, string language) => new()
    {
        ["service"] = new JsonObject
        {
            ["id"] = id,
            ["status"] = "active",
            ["external_ids"] = externalIds,
            ["params"] = new JsonObject { ["package"] = package },
            ["order"] = order,
            ["plan"] = new JsonObject { ["unit"] = "month", ["count"] = 1 },
        },
        ["account"] = new JsonObject
        {
            ["id"] = 77,
            ["language"] = language,
            ["name"] = "Cliente de Prueba",
            ["email"] = "cliente@example.com",
        },
    };

    public static JsonObject Credentials(string password = Password) =>
        new() { ["credentials"] = new JsonObject { ["password"] = password } };

    /// <summary>Una operación de aprovisionamiento. Las que cambian algo llevan una clave nueva si no se pasa una.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Op(string operation, JsonObject context, JsonObject? input = null, string? key = null, JsonObject? config = null)
    {
        var mutating = operation is not ("validate" or "get_status" or "view");
        return Send($"/v0/provisioning/{operation}", input ?? [], mutating ? key ?? Guid.NewGuid().ToString() : null, context, config);
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject input, string? key = null, JsonObject? context = null, JsonObject? config = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-hosting", ["config"] = (config ?? Config()).DeepClone() },
            ["context"] = (context ?? Existing()).DeepClone(),
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

    public static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}

/// <summary>La Hestia falsa detrás de un Kestrel con HTTPS de verdad, con un certificado autofirmado generado en la prueba.</summary>
internal sealed class TlsHestia : IAsyncDisposable
{
    private readonly WebApplication app;
    private int strays;

    private TlsHestia(WebApplication app, int port)
    {
        this.app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Pedidos que llegaron a otra ruta que /api/, por ejemplo siguiendo una redirección.</summary>
    public int Strays => strays;

    public static async Task<TlsHestia> Start(FakeHestia hestia, X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        var app = builder.Build();
        TlsHestia? server = null;
        app.MapPost("/api/", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            var (status, text) = hestia.Respond(form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal));
            http.Response.StatusCode = status;
            if (status is >= 300 and < 400)
            {
                http.Response.Headers.Location = "/robado";
            }

            await http.Response.WriteAsync(text);
        });
        app.MapFallback((HttpContext _) =>
        {
            Interlocked.Increment(ref server!.strays);
            return Results.Text("OK");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new TlsHestia(app, new Uri(address).Port);
        return server;
    }

    /// <summary>Un certificado autofirmado para 127.0.0.1, generado en el momento. Nunca se guarda.</summary>
    public static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=hestia-prueba", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // En Windows, SChannel no acepta la llave efímera de CreateSelfSigned: se pasa por PKCS#12 en memoria.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c)));

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
