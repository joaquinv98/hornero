using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Tests;

[TestClass]
public sealed class ModuleFlowTests
{
    [TestMethod]
    public async Task AltaPorModulo_ConOperacionLarga_CredencialesDelNucleo_YSuspensionPorMora()
    {
        var calls = new ConcurrentQueue<(string Op, JsonObject Input, JsonObject Config, JsonObject Context)>();
        var signing = RandomNumberGenerator.GetBytes(32);
        var manifest = new JsonObject { ["id"] = "fake", ["credentials"] = new JsonArray("root_password") };
        var module = new ModuleDefinition(manifest)
            .Operation("provisioning", "create", call =>
            {
                calls.Enqueue(("create", call.Request.Input, call.Request.Instance.Config, call.Request.Context));
                return Task.FromResult(ModuleResponse.Pending("op-1", retryAfter: 0));
            })
            .Operation("provisioning", "suspend", call =>
            {
                calls.Enqueue(("suspend", call.Request.Input, call.Request.Instance.Config, call.Request.Context));
                return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "suspended" }));
            })
            .Poll((_, id) => Task.FromResult(ModuleResponse.Completed(new JsonObject
            {
                ["external_ids"] = new JsonObject { ["vmid"] = "4102" },
                ["observed_state"] = "active",
                ["operation"] = id,
            })));

        await using var fake = await StartModule(module, signing);
        var box = new SecretBox(new Dictionary<int, byte[]> { [1] = RandomNumberGenerator.GetBytes(32) }, 1);
        await using var h = await CoreHarness.Start(box);
        var instanceId = await h.Use<ModuleRegistry, long>(async registry =>
        {
            var installation = await registry.Install("fake", fake.Url, Encoding.UTF8.GetString(signing), manifest);
            return (await registry.AddInstance(installation.Id, "cluster", """{"api_url":"https://pve.example.net"}""", """{"token":"secreto"}""")).Id;
        });
        await h.Db(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            product.ModuleInstanceId = instanceId;
            await db.SaveChangesAsync();
        });

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();

        var service = await h.Query(db => db.Services.SingleAsync());
        var jobs = await h.Query(db => db.Jobs.Select(j => j.Kind + ":" + j.Status + ":" + j.LastError).ToListAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status, string.Join(" | ", jobs));
        Assert.AreEqual("active", service.ObservedStatus);
        Assert.AreEqual("4102", JsonNode.Parse(service.ExternalIds)!["vmid"]!.GetValue<string>());
        var stored = await h.Query(db => db.ServiceSecrets.SingleAsync(s => s.ServiceId == service.Id && s.Name == "root_password"));
        var create = calls.Single(c => c.Op == "create");
        Assert.AreEqual(box.Decrypt(stored.Value, "services.service_secrets", "root_password", service.Id), create.Input["credentials"]!["root_password"]!.GetValue<string>());
        Assert.AreEqual("secreto", create.Config["token"]!.GetValue<string>());
        Assert.AreEqual("https://pve.example.net", create.Config["api_url"]!.GetValue<string>());

        // El uuid del servicio va en el contexto para la marca externa, y no cambia entre llamadas.
        Assert.AreNotEqual(Guid.Empty, service.Uuid);
        Assert.AreEqual(service.Uuid.ToString(), create.Context["service"]!["uuid"]!.GetValue<string>());

        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = h.Clock.Now.AddDays(12);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.AreEqual(1, calls.Count(c => c.Op == "suspend"));
        Assert.AreEqual(service.Uuid.ToString(), calls.Single(c => c.Op == "suspend").Context["service"]!["uuid"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SuspensionPorMoraQueSeReintenta_NoSuspendeSiElClientePagoMientras()
    {
        var calls = new ConcurrentQueue<string>();
        var failed = 0;
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Done(calls, "create", "active"))
            .Operation("provisioning", "suspend", _ => Interlocked.Exchange(ref failed, 1) == 0
                ? throw new ModuleException(ErrorCodes.UpstreamUnavailable, "El hipervisor no respondió", retryAfter: 60)
                : Done(calls, "suspend", "suspended"))
            .Operation("provisioning", "unsuspend", _ => Done(calls, "unsuspend", "active"));
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        await Overdue(h, fake.InstanceId);

        // El módulo no responde a la suspensión y el trabajo queda para dentro de un minuto. Mientras, el cliente paga.
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        await Pay(h, "T-2");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.IsFalse(calls.Contains("suspend"));
    }

    [TestMethod]
    public async Task SuspensionLargaYPagoMientrasTanto_ElServicioVuelveAEstarActivo()
    {
        var calls = new ConcurrentQueue<string>();
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Done(calls, "create", "active"))
            .Operation("provisioning", "suspend", _ =>
            {
                calls.Enqueue("suspend");
                return Task.FromResult(ModuleResponse.Pending("op-s", retryAfter: 60));
            })
            .Operation("provisioning", "unsuspend", _ => Done(calls, "unsuspend", "active"))
            .Poll((_, _) => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "suspended" })));
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        await Overdue(h, fake.InstanceId);

        // La suspensión queda en curso en el módulo y el cliente paga antes de que termine.
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        await Pay(h, "T-2");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.Contains("unsuspend", calls);
    }

    [TestMethod]
    public async Task SuspensionLargaQueElStaffHacePropiaMientrasTanto_ElPagoNoLaLevanta()
    {
        var calls = new ConcurrentQueue<string>();
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Done(calls, "create", "active"))
            .Operation("provisioning", "suspend", _ =>
            {
                calls.Enqueue("suspend");
                return Task.FromResult(ModuleResponse.Pending("op-s", retryAfter: 60));
            })
            .Operation("provisioning", "unsuspend", _ => Done(calls, "unsuspend", "active"))
            .Poll((_, _) => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "suspended" })));
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        await Overdue(h, fake.InstanceId);

        // La suspensión por mora queda en curso en el módulo. Mientras, el staff la hace propia y el cliente paga.
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        var serviceId = (await h.Query(db => db.Services.SingleAsync())).Id;
        await h.Use<ServiceAdmin, bool>(async s =>
        {
            await s.Suspend(serviceId, "Abuso de recursos");
            return true;
        });
        await Pay(h, "T-2");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.RunJobs();

        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Suspended, service.Status);
        Assert.AreEqual("Abuso de recursos", service.SuspendReason);
        Assert.IsFalse(calls.Contains("unsuspend"));
    }

    private static Task<ModuleResponse> Done(ConcurrentQueue<string> calls, string operation, string observed)
    {
        calls.Enqueue(operation);
        return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = observed }));
    }

    private static async Task Pay(CoreHarness h, string id)
    {
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", id, Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();
    }

    /// <summary>Servicio con módulo, activo y con la renovación vencida más allá de la gracia por defecto.</summary>
    private static async Task Overdue(CoreHarness h, long instanceId)
    {
        await h.Db(async db =>
        {
            (await db.Products.SingleAsync(p => p.Name == "VPS 2 GB")).ModuleInstanceId = instanceId;
            await db.SaveChangesAsync();
        });
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await Pay(h, "T-1");
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        h.Clock.Now = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = new DateTimeOffset(2026, 12, 6, 15, 0, 0, TimeSpan.Zero);
    }

    private static async Task<FakeModule> StartModule(ModuleDefinition module, byte[] signing)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"idem-{Guid.NewGuid():N}.db");
        app.MapModule(module, new ModuleSdkOptions { SigningSecrets = [Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signing))], IdempotencyStorePath = db });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeModule(app, url + "/", db);
    }

    private sealed class FakeModule(WebApplication app, string url, string db) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            File.Delete(db);
        }
    }
}

/// <summary>Los secretos de un módulo solo viajan a su destino: https, o http por loopback (sección 4 del protocolo).</summary>
[TestClass]
public sealed class ModuleDestinationTests
{
    private const string ManifestJson = """{"id":"fake"}""";

    [TestMethod]
    public void ElClienteNoArmaUnDestinoPorHttpFueraDeLoopback()
    {
        Assert.ThrowsExactly<PermanentFailureException>(() => new ModuleEndpoint(new Uri("http://10.20.30.1:8080/"), [], null));
        Assert.ThrowsExactly<PermanentFailureException>(() => new ModuleEndpoint(new Uri("http://modulo.example.net/"), [], null));
        Assert.AreEqual("http://127.0.0.1:9/", new ModuleEndpoint(new Uri("http://127.0.0.1:9/"), [], null).BaseUrl.ToString());
        Assert.AreEqual("https://modulo.example.net/", new ModuleEndpoint(new Uri("https://modulo.example.net/"), [], null).BaseUrl.ToString());
    }

    [TestMethod]
    public async Task CargarUnaInstalacion_RechazaHttpFueraDeLoopback()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ModuleRegistry, ModuleInstallation>(r => r.Install("m", "http://10.20.30.1:8080/", "firma", Manifest())));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ModuleRegistry, ModuleInstallation>(r => r.Install("m", "no-es-url", "firma", Manifest())));
    }

    [TestMethod]
    public async Task Resolve_RechazaUnaUrlQueNoEsHttpsNiLoopback_AntesDeDescifrar()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var instanceId = await Install(h, "http://127.0.0.1:9/");
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE platform.module_installations SET base_url = 'http://10.20.30.1:9/'"));

        var ex = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => h.Use<ModuleRegistry, ResolvedInstance>(r => r.Resolve(instanceId, CancellationToken.None)));

        StringAssert.Contains(ex.Message, ModuleEndpoint.BaseUrlRule);
    }

    [TestMethod]
    public async Task Resolve_SiCambioLaUrlPorAfuera_PideVolverACargarLosSecretos()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var instanceId = await Install(h, "https://modulo.example.net/");
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE platform.module_installations SET base_url = 'https://otro.example.net/'"));

        var ex = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => h.Use<ModuleRegistry, ResolvedInstance>(r => r.Resolve(instanceId, CancellationToken.None)));

        StringAssert.Contains(ex.Message, "volver a cargar");
    }

    [TestMethod]
    public async Task CambiarLaUrl_ExigeLosSecretosDeCadaInstancia_YDescartaElEstado()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var instanceId = await Install(h, "https://modulo.example.net/");
        var installationId = await h.Query(db => db.ModuleInstallations.Select(i => i.Id).SingleAsync());
        await h.Use<ModuleRegistry, ResolvedInstance>(async r => await r.SaveState(await r.Resolve(instanceId, CancellationToken.None), new JsonObject { ["ticket"] = "viejo" }, CancellationToken.None));

        var sinSecretos = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ModuleRegistry, bool>(async r =>
        {
            await r.ChangeBaseUrl(installationId, "https://otro.example.net/", "firma-nueva", new Dictionary<long, string>());
            return true;
        }));
        StringAssert.Contains(sinSecretos.Message, instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await h.Use<ModuleRegistry, bool>(async r =>
        {
            await r.ChangeBaseUrl(installationId, "https://otro.example.net/", "firma-nueva", new Dictionary<long, string> { [instanceId] = """{"token":"nuevo"}""" });
            return true;
        });

        var resolved = await h.Use<ModuleRegistry, ResolvedInstance>(r => r.Resolve(instanceId, CancellationToken.None));
        Assert.AreEqual("https://otro.example.net/", resolved.Endpoint.BaseUrl.ToString());
        Assert.AreEqual("firma-nueva", Encoding.UTF8.GetString(resolved.Endpoint.SigningSecret));
        Assert.AreEqual("nuevo", resolved.Data.Config["token"]!.GetValue<string>());
        Assert.AreEqual("https://pve.example.net", resolved.Data.Config["api_url"]!.GetValue<string>());
        Assert.IsNull(resolved.Data.State);
    }

    private static JsonObject Manifest() => JsonNode.Parse(ManifestJson)!.AsObject();

    private static Task<long> Install(CoreHarness h, string url) => h.Use<ModuleRegistry, long>(async r =>
    {
        var installation = await r.Install("fake", url, "firma", Manifest());
        return (await r.AddInstance(installation.Id, "cluster", """{"api_url":"https://pve.example.net"}""", """{"token":"secreto"}""")).Id;
    });
}
