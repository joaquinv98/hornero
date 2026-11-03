using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Tests;

/// <summary>
/// El host web de la interfaz sobre una base de prueba (<see cref="UiHarness"/>), con acceso a sus servicios para sembrar
/// contenidos y con los pedidos de formularios y de archivos que usan las pantallas del panel y del sitio.
/// </summary>
internal sealed partial class ContentSite : IAsyncDisposable
{
    private readonly ProviderBox box;

    private ContentSite(UiHarness ui, ProviderBox box)
    {
        Ui = ui;
        this.box = box;
    }

    public UiHarness Ui { get; }

    /// <summary>Los errores que registró la aplicación, con su excepción: sirven para entender un 500 en una prueba.</summary>
    public IReadOnlyCollection<string> Errors => box.Errors;

    /// <summary>Arranca el host. Con <paramref name="services"/> se suman o reemplazan servicios, como las opciones de los contenidos.</summary>
    public static async Task<ContentSite> Start(Action<IServiceCollection>? services = null)
    {
        var box = new ProviderBox();
        var ui = await UiHarness.Start(services: s =>
        {
            s.AddLogging(b => b.AddProvider(new ErrorLog(box.Errors)));
            s.AddSingleton<IHostedService>(sp => new Capture(sp, box));
            services?.Invoke(s);
        });
        return new ContentSite(ui, box);
    }

    public Client Visitor() => Ui.NewClient();

    public async Task<TResult> Use<TService, TResult>(Func<TService, Task<TResult>> work)
        where TService : notnull
    {
        await using var scope = box.Services!.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public Task Use<TService>(Func<TService, Task> work)
        where TService : notnull => Use<TService, bool>(async s =>
        {
            await work(s);
            return true;
        });

    /// <summary>Un cliente titular de la cuenta, para entrar después con <see cref="Login"/>. Una cuenta tiene un solo titular.</summary>
    public Task<BillingOss.App.Identity.PortalUser> AddCustomer(string email, long account) => Ui.PortalUser(email, account);

    /// <summary>Una sesión nueva del portal para un cliente que ya existe.</summary>
    public async Task<Client> Login(string email)
    {
        var c = Ui.NewClient();
        using var login = await c.Login(email);
        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode, "El cliente de prueba no pudo ingresar.");
        return c;
    }

    /// <summary>Un usuario del staff con esos permisos, con su segundo factor ya enrolado y la sesión abierta.</summary>
    public async Task<Client> Staff(string email, params string[] permissions)
    {
        await Ui.Staff(email, permissions);
        var c = Ui.NewClient();
        (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = email, ["Password"] = UiHarness.Password })).Dispose();
        var key = Key(await c.Html("/admin/ingresar/codigo"));
        (await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
        return c;
    }

    public ValueTask DisposeAsync() => Ui.DisposeAsync();

    private static string Key(string html) =>
        KeyPattern().Match(html) is { Success: true } m ? m.Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal) : throw new AssertFailedException("La página no muestra la clave de TOTP.");

    [GeneratedRegex("<code class=\"key\"[^>]*>([A-Z2-7 ]+)</code>")]
    private static partial Regex KeyPattern();

    private sealed class ProviderBox
    {
        public IServiceProvider? Services { get; set; }

        public ConcurrentQueue<string> Errors { get; } = new();
    }

    private sealed class ErrorLog(ConcurrentQueue<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(errors, categoryName);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> errors, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    errors.Enqueue($"{category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }

    private sealed class Capture : IHostedService
    {
        public Capture(IServiceProvider services, ProviderBox box) => box.Services = services;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>Formularios con campos repetidos, subida de archivos y lectura de la página, sobre el navegador de las pruebas.</summary>
internal static partial class Forms
{
    /// <summary>Abre <paramref name="page"/>, toma su token antiforgery y manda los campos a <paramref name="action"/>. Un nombre puede repetirse.</summary>
    public static async Task<HttpResponseMessage> Post(this Client c, string page, string action, params (string Name, string Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(c);
        var token = await Token(c, page);
        using var content = new FormUrlEncodedContent(fields.Append((Name: "__RequestVerificationToken", Value: token)).Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        return await c.Http.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    /// <summary>Lo mismo con un archivo: el campo del archivo, su nombre y sus bytes.</summary>
    public static async Task<HttpResponseMessage> Upload(this Client c, string page, string action, string field, string fileName, byte[] bytes, params (string Name, string Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(c);
        var token = await Token(c, page);
        using var content = new MultipartFormDataContent();
        foreach (var (name, value) in fields.Append((Name: "__RequestVerificationToken", Value: token)))
        {
            content.Add(new StringContent(value), name);
        }

        content.Add(new ByteArrayContent(bytes), field, fileName);
        return await c.Http.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    public static string Location(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.Location?.OriginalString ?? string.Empty;
    }

    /// <summary>La página como texto, sin importar el código que devuelva.</summary>
    public static async Task<(HttpStatusCode Status, string Html)> Read(this Client c, string path)
    {
        ArgumentNullException.ThrowIfNull(c);
        using var response = await c.Get(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> Token(Client c, string page)
    {
        var html = await c.Html(page);
        return TokenPattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : throw new AssertFailedException($"{page} no tiene token antiforgery.");
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
