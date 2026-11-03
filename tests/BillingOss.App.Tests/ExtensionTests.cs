using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Module.OneTrial;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Tests;

/// <summary>Lo que escribe un módulo en su log, para ver qué recibió.</summary>
internal sealed class ListLogger : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue(formatter(state, exception));
}

/// <summary>Extensiones: eventos por la cola, validación de pedidos, alta desde el panel y el ejemplo completo de la guía.</summary>
[TestClass]
public sealed class ExtensionTests
{
    private static JsonObject Manifest(params string[] capabilities) => new()
    {
        ["id"] = "prueba",
        ["kinds"] = new JsonArray("extension"),
        ["capabilities"] = new JsonArray([.. capabilities.Select(c => (JsonNode)c)]),
        ["config_schema"] = JsonNode.Parse("""{ "type": "object", "properties": { "api_token": { "type": "string", "x-secret": true } } }"""),
    };

    [TestMethod]
    public async Task Evento_LlegaConSuClaveYUnErrorReintentableSeReintenta()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var calls = new ConcurrentQueue<ModuleCall>();
        var definition = new ModuleDefinition(Manifest(ExtensionCapabilities.Events)).Operation("extension", "on_event", call =>
        {
            calls.Enqueue(call);
            return calls.Count == 1
                ? throw new ModuleException(ErrorCodes.UpstreamUnavailable, "El CRM no responde.", retryAfter: 30)
                : Task.FromResult(ModuleResponse.Completed());
        });
        await using var module = await TestModule.Start(definition, h, TestModule.NewBox());
        await SetHooks(h, module.InstanceId, ["invoice.issued"]);

        var placed = await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.RunJobs();
        Assert.HasCount(1, calls);
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.RunJobs();

        Assert.HasCount(2, calls);
        var (first, second) = (calls.First(), calls.Last());
        Assert.AreEqual(first.IdempotencyKey, second.IdempotencyKey);
        StringAssert.StartsWith(first.IdempotencyKey, "hook-");
        Assert.AreEqual("invoice.issued", second.Request.Input["type"]!.GetValue<string>());
        Assert.AreEqual(placed.InvoiceId, second.Request.Input["data"]!["invoice_id"]!.GetValue<long>());
        Assert.AreEqual("2026-11-02T15:00:00.000Z", second.Request.Input["occurred_at"]!.GetValue<string>());
        Assert.AreEqual(32, second.Request.Input["id"]!.GetValue<string>().Length);
        Assert.AreEqual(0, second.Request.Context.Count, "El evento no lleva contexto.");
        Assert.AreEqual(JobStatus.Done, await JobOf(h, module.InstanceId));
    }

    [TestMethod]
    public async Task EventoConErrorDefinitivo_QuedaMuertoYVisible()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var definition = new ModuleDefinition(Manifest(ExtensionCapabilities.Events))
            .Operation("extension", "on_event", _ => throw new ModuleException(ErrorCodes.InvalidConfig, "Falta el token del CRM."));
        await using var module = await TestModule.Start(definition, h, TestModule.NewBox());
        await SetHooks(h, module.InstanceId, ["order.placed"]);

        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.RunJobs();

        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ExtensionEvent));
        Assert.AreEqual(JobStatus.Dead, job.Status);
        StringAssert.Contains(job.LastError, "Falta el token del CRM.");
    }

    [TestMethod]
    public async Task GanchoQuitadoDespuesDePublicar_NoLlamaAlModulo()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var calls = 0;
        var definition = new ModuleDefinition(Manifest(ExtensionCapabilities.Events)).Operation("extension", "on_event", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ModuleResponse.Completed());
        });
        await using var module = await TestModule.Start(definition, h, TestModule.NewBox());
        await SetHooks(h, module.InstanceId, ["order.placed"]);
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        await SetHooks(h, module.InstanceId, []);
        await h.RunJobs();

        Assert.AreEqual(0, calls);
        Assert.AreEqual(JobStatus.Done, await JobOf(h, module.InstanceId));
    }

    [TestMethod]
    public async Task ValidarPedido_UnRechazoNoDejaNadaYLleguaSoloLoMinimo()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var seen = new ConcurrentQueue<ModuleCall>();
        var definition = new ModuleDefinition(Manifest(ExtensionCapabilities.ValidateOrder)).Operation("extension", "validate_order", call =>
        {
            seen.Enqueue(call);
            return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["allowed"] = false, ["reason"] = "No vendemos VPS los domingos.\n<b>nunca</b>" }));
        }, mutating: false);
        await using var module = await TestModule.Start(definition, h, TestModule.NewBox());
        await SetHooks(h, module.InstanceId, [], validate: true);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId, OrderData: """{"hostname":"vps1"}""")])));

        Assert.AreEqual("El pedido no se puede confirmar: No vendemos VPS los domingos.<b>nunca</b>", ex.Message);
        Assert.AreEqual(0, await h.Query(db => db.Orders.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Services.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync()));
        var call = seen.Single();
        Assert.IsNull(call.IdempotencyKey, "Validar no tiene efecto: va sin clave.");
        var account = call.Request.Context["account"]!;
        Assert.AreEqual(h.AccountId, account["id"]!.GetValue<long>());
        Assert.AreEqual("AR", account["country"]!.GetValue<string>());
        Assert.IsNull(account["email"], "Sin datos de contacto.");
        Assert.IsNull(account["name"], "Sin datos de contacto.");
        var item = call.Request.Input["items"]![0]!;
        Assert.AreEqual(h.PlanId, item["plan_id"]!.GetValue<long>());
        Assert.AreEqual("month", item["cycle"]!["unit"]!.GetValue<string>());
        Assert.AreEqual("vps1", item["order"]!["hostname"]!.GetValue<string>());
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "extensions.order_rejected")));
    }

    [TestMethod]
    public async Task ValidarPedido_SinRespuesta_ObligatoriaRechazaYOpcionalDejaPasar()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var definition = new ModuleDefinition(Manifest(ExtensionCapabilities.ValidateOrder))
            .Operation("extension", "validate_order", _ => throw new InvalidOperationException("se rompió"), mutating: false);
        await using var module = await TestModule.Start(definition, h, TestModule.NewBox());
        await SetHooks(h, module.InstanceId, [], validate: true, required: true);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)])));
        Assert.AreEqual(OrderValidation.Unavailable, ex.Message);
        Assert.AreEqual(0, await h.Query(db => db.Orders.CountAsync()));

        await SetHooks(h, module.InstanceId, [], validate: true, required: false);
        var placed = await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        Assert.IsNotNull(placed.InvoiceId);
        var failures = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action == "extensions.validate_failed").Select(a => a.Details!).ToListAsync());
        Assert.HasCount(2, failures);
        Assert.IsTrue(failures.All(f => f.Contains("internal", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Ganchos_ExigenLasCapacidadesDelManifiesto()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var events = await TestModule.Start(new ModuleDefinition(Manifest(ExtensionCapabilities.Events)), h, TestModule.NewBox());
        await using var provisioning = await TestModule.Start(new ModuleDefinition(new JsonObject { ["id"] = "px", ["kinds"] = new JsonArray("provisioning") }), h, TestModule.NewBox());

        await Assert.ThrowsAsync<ValidationException>(() => SetHooks(h, events.InstanceId, [], validate: true));
        await Assert.ThrowsAsync<ValidationException>(() => SetHooks(h, events.InstanceId, ["InvoicePaid"]));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => SetHooks(h, provisioning.InstanceId, ["invoice.paid"]));
        await SetHooks(h, events.InstanceId, ["invoice.paid", "invoice.paid", "service.suspended"]);

        CollectionAssert.AreEquivalent(new[] { "invoice.paid", "service.suspended" }, await h.Query(db => db.Set<ExtensionHook>().Select(x => x.Event).ToListAsync()));
    }

    [TestMethod]
    public async Task Alta_LeeElManifiestoFirmadoYCifraLosCamposSecretos()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        // La prueba de configuración es obligatoria para todo módulo (sección 9 del protocolo): sin región no sirve.
        var tested = new ConcurrentQueue<JsonObject>();
        var definition = new ModuleDefinition(Manifest(ExtensionCapabilities.Events)).InstanceTest(call =>
        {
            tested.Enqueue(call.Request.Instance.Config);
            return call.Request.Instance.Config["region"] is null
                ? throw new ModuleException(ErrorCodes.InvalidConfig, "Falta region.")
                : Task.FromResult(ModuleResponse.Completed(new JsonObject { ["ok"] = true }));
        });
        await using var module = await TestModule.Serve(definition);
        await using var other = await TestModule.Serve(new ModuleDefinition(new JsonObject { ["id"] = "px", ["kinds"] = new JsonArray("provisioning") }));
        await using var untested = await TestModule.Serve(new ModuleDefinition(Manifest(ExtensionCapabilities.Events)));

        var wrongSecret = new ExtensionInput("CRM", module.Url, "un-secreto-que-no-es-el-del-modulo", "{}");
        var notExtension = new ExtensionInput("Proxmox", other.Url, other.SigningSecret, "{}");
        var badConfig = new ExtensionInput("CRM", module.Url, module.SigningSecret, "[1, 2]");
        var rejected = new ExtensionInput("CRM", module.Url, module.SigningSecret, """{ "api_token": "token-del-crm-123" }""");
        var noTest = new ExtensionInput("Sin prueba", untested.Url, untested.SigningSecret, "{}");
        foreach (var input in new[] { wrongSecret, notExtension, badConfig, rejected, noTest })
        {
            await Assert.ThrowsAsync<ValidationException>(() => h.Use<ExtensionService, long>(s => s.Install(input)), input.Name);
        }

        var refused = await Assert.ThrowsAsync<ValidationException>(() => h.Use<ExtensionService, long>(s => s.Install(rejected)));
        StringAssert.Contains(refused.Message, "Falta region.");
        Assert.AreEqual(0, await h.Query(db => db.ModuleInstallations.CountAsync()), "Lo que la prueba de configuración rechaza no queda instalado.");
        Assert.AreEqual("token-del-crm-123", tested.First()["api_token"]!.GetValue<string>(), "La prueba recibe también los campos secretos.");

        var id = await h.Use<ExtensionService, long>(s => s.Install(new ExtensionInput("CRM", module.Url, module.SigningSecret, """{ "region": "ar", "api_token": "token-del-crm-123" }""")));

        var instance = await h.Query(db => db.ModuleInstances.SingleAsync(i => i.Id == id));
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{ "region": "ar" }"""), JsonNode.Parse(instance.Config)));
        Assert.IsNotNull(instance.Secrets);
        var resolved = await h.Use<ModuleRegistry, ResolvedInstance>(r => r.Resolve(id, CancellationToken.None));
        Assert.AreEqual("token-del-crm-123", resolved.Data.Config["api_token"]!.GetValue<string>());
        Assert.IsFalse((await h.Query(db => db.Set<AuditEvent>().Select(a => a.Details).ToListAsync())).Any(d => d?.Contains("token-del-crm-123", StringComparison.Ordinal) == true));
        await Assert.ThrowsAsync<ValidationException>(() => h.Use<ExtensionService, long>(s => s.Install(new ExtensionInput("CRM", module.Url, module.SigningSecret, "{}"))));

        await h.Use<ExtensionService, bool>(async s =>
        {
            await s.Remove(id);
            return true;
        });
        Assert.AreEqual(0, await h.Query(db => db.ModuleInstallations.CountAsync()));
    }

    [TestMethod]
    public async Task EjemploDeLaGuia_UnaSolaPruebaGratisPorCuenta()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var log = new ListLogger();
        var trialProduct = await h.Query(db => db.Plans.Where(p => p.Id == h.FreePlanId).Select(p => p.ProductId).SingleAsync());
        await using var module = await TestModule.Start(OneTrialModule.Create(log), h, TestModule.NewBox(), instanceConfig: $$"""{ "trial_product_ids": [{{trialProduct}}] }""");
        await SetHooks(h, module.InstanceId, ["service.activated"], validate: true);

        var trial = await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.FreePlanId)]));
        await h.RunJobs();
        var again = await Assert.ThrowsAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.FreePlanId)])));
        var two = await Assert.ThrowsAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.FreePlanId, Quantity: 2)])));
        var paid = await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        Assert.IsNull(trial.InvoiceId, "La prueba no se factura.");
        Assert.AreEqual("El pedido no se puede confirmar: Ya usaste la prueba gratis. Elegí un plan pago.", again.Message);
        Assert.AreEqual("El pedido no se puede confirmar: La prueba gratis es una sola.", two.Message);
        Assert.IsNotNull(paid.InvoiceId);
        var serviceId = trial.ServiceIds.Single();
        Assert.IsTrue(log.Lines.Any(l => l.Contains("service.activated", StringComparison.Ordinal) && l.Contains($"\"service_id\":{serviceId}", StringComparison.Ordinal)), string.Join("\n", log.Lines));
    }

    private static Task SetHooks(CoreHarness h, long instanceId, string[] events, bool validate = false, bool required = true) =>
        h.Use<ExtensionService, bool>(async s =>
        {
            await s.SetHooks(instanceId, events, validate, required);
            return true;
        });

    private static async Task<JobStatus> JobOf(CoreHarness h, long instanceId)
    {
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ExtensionEvent && j.EntityKey!.StartsWith($"extension:{instanceId}:")));
        Assert.IsTrue(job.Status is JobStatus.Done or JobStatus.Dead, $"{job.Status} {job.Attempts} {job.RunAt:O} {job.LastError}");
        return job.Status;
    }
}
