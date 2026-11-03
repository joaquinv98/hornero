using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Shared;
using BillingOss.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Tests;

/// <summary>Correos de acceso guardados en memoria, para leer el código o el enlace en la prueba.</summary>
internal sealed class CapturedMail : IAccountMail
{
    public ConcurrentQueue<(string To, string Kind, string Value)> Sent { get; } = new();

    public Task SendPasswordReset(string email, string language, Uri link, CancellationToken ct)
    {
        Sent.Enqueue((email, "reset", link.ToString()));
        return Task.CompletedTask;
    }

    public Task SendConsumerCode(string email, string language, ConsumerRequestKind kind, string code, CancellationToken ct)
    {
        Sent.Enqueue((email, "code", code));
        return Task.CompletedTask;
    }

    public Task SendConsumerReceipt(string email, string language, ConsumerRequestKind kind, string receipt, DateTimeOffset at, CancellationToken ct)
    {
        Sent.Enqueue((email, "receipt", receipt));
        return Task.CompletedTask;
    }

    public Task SendEmailVerification(string email, string language, Uri link, CancellationToken ct)
    {
        Sent.Enqueue((email, "verify", link.ToString()));
        return Task.CompletedTask;
    }

    public Task SendNewLogin(string email, string language, DateTimeOffset at, string ip, string device, Uri? link, CancellationToken ct)
    {
        Sent.Enqueue((email, "new-login", $"{ip}|{device}|{link}"));
        return Task.CompletedTask;
    }

    public Task SendExternalLink(string email, string language, string provider, Uri link, CancellationToken ct)
    {
        Sent.Enqueue((email, "external-link", link.ToString()));
        return Task.CompletedTask;
    }

    public string Last(string kind) => Sent.Last(m => m.Kind == kind).Value;
}

/// <summary>Navegador mínimo: guarda cookies respetando dominio, ruta y Secure, y no sigue redirecciones.</summary>
internal sealed class Browser(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public CookieContainer Cookies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var header = Cookies.GetCookieHeader(request.RequestUri!);
        if (header.Length > 0)
        {
            request.Headers.Add("Cookie", header);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                Cookies.SetCookies(request.RequestUri!, value);
            }
        }

        return response;
    }
}

/// <summary>
/// El proxy que termina TLS: el navegador le habla por HTTPS y él reenvía por HTTP con X-Forwarded-Proto y
/// X-Forwarded-For, como NPM o HAProxy delante de los nodos web.
/// </summary>
internal sealed class TlsProxy(HttpMessageHandler inner, IPAddress? client = null) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.RequestUri!;
        request.RequestUri = new UriBuilder(original) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri;
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", (client ?? UiHarness.ClientIp).ToString());
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            request.RequestUri = original;
        }
    }
}

/// <summary>
/// El host web con portal y admin sobre una base de prueba, por https para que viajen las cookies Secure. Siembra dos
/// cuentas con una factura cada una: la A tiene un usuario titular y la B es "ajena".
/// </summary>
internal sealed partial class UiHarness : IAsyncDisposable
{
    public const string Password = "una-clave-larga-1";

    /// <summary>El proxy de confianza de <see cref="Start(bool)"/> con proxy, y el cliente que está detrás.</summary>
    public static readonly IPAddress ProxyIp = IPAddress.Parse("10.20.30.40");

    public static readonly IPAddress ClientIp = IPAddress.Parse("203.0.113.7");

    private readonly WebApplication app;
    private readonly Func<Task> drop;
    private readonly string keyFile;
    private readonly bool behindProxy;

    private UiHarness(WebApplication app, Func<Task> drop, string keyFile, CapturedMail mail, bool behindProxy)
    {
        this.app = app;
        this.drop = drop;
        this.keyFile = keyFile;
        this.behindProxy = behindProxy;
        Mail = mail;
    }

    /// <summary>Los correos que mandó la aplicación.</summary>
    public CapturedMail Mail { get; }

    public long AccountA { get; private set; }

    public long AccountB { get; private set; }

    public long InvoiceA { get; private set; }

    public long InvoiceB { get; private set; }

    /// <summary>El VPS mensual del catálogo de prueba.</summary>
    public long PlanId { get; private set; }

    /// <summary>
    /// Con <paramref name="behindProxy"/> la aplicación recibe todo por HTTP desde <see cref="ProxyIp"/>, declarado en
    /// ForwardedHeaders:KnownProxies, y los clientes pasan por <see cref="TlsProxy"/>. Con <paramref name="services"/>
    /// se suman servicios, como un conversor de PDF de mentira.
    /// </summary>
    /// <param name="settings">Configuración de más, por ejemplo los límites de la carga de saldo.</param>
    public static async Task<UiHarness> Start(bool behindProxy = false, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var (connectionString, drop) = await TestDatabase.Create(template: TestDatabase.Template);
        var keyFile = Path.Combine(Path.GetTempPath(), $"billing-oss-test-{Guid.NewGuid():N}.keys");
        await File.WriteAllTextAsync(keyFile, SecretBox.NewKeyLine(1));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri("https://localhost/"));
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = connectionString,
            ["Role"] = "web",
            ["Secrets:KeyFile"] = keyFile,
            ["Billing:PublicUrl"] = "https://billing.example.com",
            ["Billing:DomainNameservers:0"] = "ns1.proveedor.ar",
            ["Billing:DomainNameservers:1"] = "ns2.proveedor.ar",

            // Ninguna prueba consulta registros reales: el RDAP queda apagado y, si se prende, va al falso.
            ["Billing:DomainLookup:Enabled"] = "false",
            ["Billing:DomainLookup:BootstrapUrl"] = FakeRdap.Bootstrap,
            ["Billing:DomainLookup:DnsResolvers:0"] = "127.0.0.1:9",
        });
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.Services.AddSingleton(new RdapHandler(new FakeRdap()));
        if (behindProxy)
        {
            // TestServer no trae IP remota: todo llega desde el proxy, antes que cualquier middleware de la aplicación.
            builder.Configuration.AddInMemoryCollection([new("ForwardedHeaders:KnownProxies:0", ProxyIp.ToString())]);
            builder.Services.AddTransient<IStartupFilter>(_ => new FromProxy());
        }

        var mail = new CapturedMail();
        builder.Services.AddSingleton<IAccountMail>(mail);
        services?.Invoke(builder.Services);
        var app = AppHost.Build(builder);
        await AppHost.Migrate(app);
        await app.StartAsync();
        var harness = new UiHarness(app, drop, keyFile, mail, behindProxy);
        await harness.Seed();
        return harness;
    }

    /// <summary>Un navegador. Detrás del proxy, <paramref name="ip"/> es la IP del cliente que ve la aplicación.</summary>
    /// <param name="plainHttp">Por HTTP y sin pasar por el proxy, como quien abre un nodo directo sin TLS.</param>
    public Client NewClient(IPAddress? ip = null, bool plainHttp = false)
    {
        var server = app.GetTestServer().CreateHandler();
        var scheme = plainHttp ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        return new(new HttpClient(new Browser(behindProxy && !plainHttp ? new TlsProxy(server, ip) : server)) { BaseAddress = new Uri($"{scheme}://localhost/") });
    }

    /// <summary>Guarda ajustes de seguridad sobre los de fábrica.</summary>
    public Task Settings(Func<SecuritySettings, SecuritySettings> change) => Use<SecuritySettingsStore, bool>(async store =>
    {
        var (current, version) = await store.Current();
        await store.Save(change(current), version);
        return true;
    });

    public async Task<PortalUser> PortalUser(string email, long accountId, bool owner = true, string[]? capabilities = null, string? importedHash = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PortalUser>>();
        var user = new PortalUser { Email = email, CreatedAt = DateTimeOffset.UtcNow };
        var result = importedHash is null ? await users.CreateAsync(user, Password) : await users.CreateAsync(user);
        Assert.IsTrue(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        if (importedHash is not null)
        {
            user.PasswordHash = importedHash;
            await users.UpdateAsync(user);
        }

        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        db.Set<AccountUser>().Add(new AccountUser { AccountId = accountId, UserId = user.Id, IsOwner = owner, Capabilities = capabilities ?? [], CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<StaffUser> Staff(string email, params string[] permissions)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var user = new StaffUser { Email = email, Name = "Staff de prueba", Permissions = permissions, CreatedAt = DateTimeOffset.UtcNow };
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>().CreateAsync(user, Password);
        Assert.IsTrue(result.Succeeded);
        return user;
    }

    public Task<T> Query<T>(Func<BillingDbContext, Task<T>> work) => Use(work);

    public Task Db(Func<BillingDbContext, Task> work) => Query(async db =>
    {
        await work(db);
        return true;
    });

    /// <summary>Un servicio de la aplicación, en su propio alcance: para preparar datos con los mismos casos de uso.</summary>
    public async Task<T> Use<TService, T>(Func<TService, Task<T>> work)
        where TService : notnull
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>El rol web no corre trabajos: la prueba los corre cuando quiere ver el efecto.</summary>
    public Task<int> RunJobs() => app.Services.GetRequiredService<JobRunner>().RunUntilIdle(CancellationToken.None);

    /// <summary>Código TOTP de ahora (RFC 6238, SHA-1, 30 segundos, 6 dígitos) para una clave en Base32.</summary>
    public static string Totp(string base32)
    {
        var key = Base32(base32);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0xf;
        var code = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7fffffff) % 1_000_000;
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        await drop();
        File.Delete(keyFile);
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var bytes = new List<byte>();
        foreach (var c in text.ToUpperInvariant().Where(c => c != '=' && c != ' '))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
            }
        }

        return [.. bytes];
    }

    private sealed class FromProxy : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, following) =>
            {
                http.Connection.RemoteIpAddress = ProxyIp;
                return following(http);
            });
            next(app);
        };
    }

    private async Task Seed()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<BillingDbContext>();
        var (planId, _, accountA) = await CoreHarness.SeedCatalog(db, DateTimeOffset.UtcNow);
        var accountB = await services.GetRequiredService<AccountService>().Create(new AccountInput("Cuenta ajena", "ajena@example.com", Currency.Ars));
        var orders = services.GetRequiredService<OrderService>();
        AccountA = accountA;
        AccountB = accountB;
        PlanId = planId;
        InvoiceA = (await orders.PlaceOrder(accountA, [new OrderItem(planId)])).InvoiceId!.Value;
        InvoiceB = (await orders.PlaceOrder(accountB, [new OrderItem(planId)])).InvoiceId!.Value;
    }
}

/// <summary>Un navegador con sus cookies: arma los formularios con el token antiforgery de la página.</summary>
internal sealed partial class Client(HttpClient http) : IDisposable
{
    public HttpClient Http { get; } = http;

    public Task<HttpResponseMessage> Get(string path) => Http.GetAsync(new Uri(path, UriKind.Relative));

    public async Task<string> Html(string path)
    {
        using var response = await Get(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{path} {response.Headers.Location}");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Abre la página, toma su token antiforgery y manda el formulario a <paramref name="action"/>.</summary>
    public async Task<HttpResponseMessage> Submit(string page, string action, Dictionary<string, string> fields)
    {
        var html = await Html(page);
        fields["__RequestVerificationToken"] = Token().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : throw new AssertFailedException($"{page} no tiene token antiforgery: {html[..Math.Min(html.Length, 4000)]}");
        using var content = new FormUrlEncodedContent(fields);
        return await Http.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    /// <summary>Como <see cref="Submit"/>, pero en multipart y con archivos, como un formulario con adjuntos.</summary>
    public async Task<HttpResponseMessage> SubmitFiles(string page, string action, Dictionary<string, string> fields, params (string Field, string Name, byte[] Content)[] files)
    {
        var html = await Html(page);
        using var content = new MultipartFormDataContent
        {
            { new StringContent(Token().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : throw new AssertFailedException($"{page} no tiene token antiforgery: {html[..Math.Min(html.Length, 4000)]}")), "__RequestVerificationToken" },
        };
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value), name);
        }

        foreach (var (field, name, bytes) in files)
        {
            content.Add(new ByteArrayContent(bytes), field, name);
        }

        return await Http.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    public async Task<HttpResponseMessage> Login(string email, string password = UiHarness.Password) =>
        await Submit("/ingresar", "/ingresar", new() { ["Email"] = email, ["Password"] = password });

    public void Dispose() => Http.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
