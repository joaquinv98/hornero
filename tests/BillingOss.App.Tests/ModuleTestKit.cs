using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Tests;

/// <summary>Un módulo real en 127.0.0.1, registrado en el núcleo con su secreto de firma cifrado.</summary>
internal sealed class TestModule : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string db;

    private TestModule(WebApplication app, string url, string db, string signing)
    {
        this.app = app;
        this.db = db;
        Url = url;
        Signing = signing;
    }

    public string Url { get; }

    /// <summary>El secreto de firma del módulo, el mismo que guarda el núcleo.</summary>
    public string Signing { get; }

    /// <summary>El mismo secreto, con el nombre del campo del panel de extensiones, para registrarlo a mano.</summary>
    public string SigningSecret => Signing;

    public long InstallationId { get; private set; }

    public long InstanceId { get; private set; }

    /// <summary>Levanta el módulo sin registrarlo en ningún núcleo.</summary>
    public static async Task<TestModule> Serve(ModuleDefinition module)
    {
        var signing = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"idem-{Guid.NewGuid():N}.db");
        app.MapModule(module, new ModuleSdkOptions { SigningSecrets = [Encoding.UTF8.GetBytes(signing)], IdempotencyStorePath = db });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
        return new TestModule(app, url, db, signing);
    }

    public static Task<TestModule> Start(ModuleDefinition module, CoreHarness h, SecretBox box, string instanceConfig = "{}", string? instanceSecrets = null) =>
        Start(module, work => h.Use<ModuleRegistry, long>(work), instanceConfig, instanceSecrets);

    /// <summary>Sobre cualquier núcleo con llaves, por ejemplo el host web completo de <see cref="WebHarness"/>.</summary>
    public static Task<TestModule> Start(ModuleDefinition module, IServiceProvider services, string? instanceConfig = "{}", string? instanceSecrets = null) =>
        Start(module, async work =>
        {
            await using var scope = services.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<ModuleRegistry>());
        }, instanceConfig, instanceSecrets);

    /// <summary>
    /// Registra la instalación con el registro que entrega <paramref name="register"/> (el del núcleo de prueba o el
    /// del host web). Con <paramref name="instanceConfig"/> nulo queda solo la instalación, para que el asistente de
    /// ARCA cree la instancia.
    /// </summary>
    public static async Task<TestModule> Start(ModuleDefinition module, Func<Func<ModuleRegistry, Task<long>>, Task<long>> register, string? instanceConfig = "{}", string? instanceSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(register);
        var test = await Serve(module);
        await register(registry => test.Register(registry, module, instanceConfig, instanceSecrets));
        return test;
    }

    /// <summary>
    /// Lo instala en el núcleo de ese registro, por ejemplo el del host de la interfaz, con una instancia. Con
    /// <paramref name="instanceConfig"/> nulo queda solo la instalación.
    /// </summary>
    public async Task<long> Register(ModuleRegistry registry, ModuleDefinition module, string? instanceConfig = "{}", string? instanceSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(module);
        var installation = await registry.Install($"m-{Guid.NewGuid():N}", Url, Signing, module.Manifest);
        InstallationId = installation.Id;
        InstanceId = instanceConfig is null ? 0 : (await registry.AddInstance(installation.Id, "instancia", instanceConfig, instanceSecrets)).Id;
        return InstanceId;
    }

    public static SecretBox NewBox() => new(new Dictionary<int, byte[]> { [1] = RandomNumberGenerator.GetBytes(32) }, 1);

    public static JsonObject Manifest(string id) => new() { ["id"] = id };

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        try
        {
            File.Delete(db);
        }
        catch (IOException)
        {
            // Con la PC cargada, el antivirus o un manejador que todavía corre lo tienen abierto: el temporal queda.
        }
    }
}

/// <summary>
/// Módulo de fraude falso (tipo fraud, sección 9.6 del protocolo): devuelve el puntaje que fija la prueba, o el error
/// que fija, y anota cada llamada con su clave y su sobre.
/// </summary>
internal sealed class FakeFraudModule
{
    public decimal Score { get; set; } = 10m;

    /// <summary>Si tiene valor, la evaluación falla con ese código del protocolo.</summary>
    public string? ErrorCode { get; set; }

    public System.Collections.Concurrent.ConcurrentQueue<(string? Key, JsonObject Input, JsonObject Context)> Calls { get; } = new();

    public ModuleDefinition Definition(params string[] scopes) => new ModuleDefinition(new JsonObject
    {
        ["id"] = "fraude-falso",
        ["kinds"] = new JsonArray("fraud"),
        ["capabilities"] = new JsonArray("fraud.core"),
        ["data_scopes"] = new JsonArray([.. scopes.Select(s => (JsonNode)s)]),
    }).Operation("fraud", "evaluate", call =>
    {
        Calls.Enqueue((call.IdempotencyKey, call.Request.Input, call.Request.Context));
        if (ErrorCode is { } code)
        {
            throw new ModuleException(code, "Falla de prueba del módulo de fraude.");
        }

        return Task.FromResult(BillingOss.Modules.Protocol.ModuleResponse.Completed(new JsonObject
        {
            ["score"] = Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["reasons"] = new JsonArray(new JsonObject { ["code"] = "ip.proxy", ["message"] = "La IP es de un proxy" }),
            ["external_id"] = "ev-1",
        }));
    });
}
