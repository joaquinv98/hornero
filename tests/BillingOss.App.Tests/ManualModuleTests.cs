using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Productos sin módulo con el módulo manual del núcleo: tareas del staff y ciclo de vida completo.</summary>
[TestClass]
public sealed class ManualModuleTests
{
    private const string StaffFields = """[{"id":"tunel","label":{"es":"Túnel","en":"Tunnel"},"client_visible":true},{"id":"psk","label":{"es":"Clave"},"secret":true}]""";

    /// <summary>El núcleo con llave de secretos y el producto mensual de prueba atado a una instancia manual.</summary>
    private static async Task<(CoreHarness H, long Instance)> Start(string activation, string lifecycle, bool free = false)
    {
        var h = await CoreHarness.Start(TestModule.NewBox());
        long instance = 0;
        await h.Db(async db =>
        {
            instance = await ManualModule.AddInstance(db, "servicios-manuales", new JsonObject { ["queue"] = "servicios" }, h.Clock.Now);
            var name = free ? "Prueba gratis" : "VPS 2 GB";
            var product = await db.Products.SingleAsync(p => p.Name == name);
            product.ModuleInstanceId = instance;
            product.ModuleParams = $$"""{"activation":"{{activation}}","lifecycle":"{{lifecycle}}","instructions":"Configurar el túnel","staff_fields":{{StaffFields}}}""";
            await db.SaveChangesAsync();
        });
        return (h, instance);
    }

    private static async Task OrderAndPay(CoreHarness h)
    {
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId, OrderData: """{"ubicacion":"Miami"}""")]));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();
    }

    private static Task Close(CoreHarness h, long taskId, StaffTaskClose close) =>
        h.Use<StaffTaskService, bool>(async s =>
        {
            await s.Complete(taskId, close);
            return true;
        });

    private static Task Reject(CoreHarness h, long taskId, string reason) =>
        h.Use<StaffTaskService, bool>(async s =>
        {
            await s.Reject(taskId, reason);
            return true;
        });

    internal static async Task<ModuleCallResult> Call(CoreHarness h, long instance, string path, JsonObject context, JsonObject? input = null, string? key = null)
    {
        var module = await h.Use<ModuleRegistry, ResolvedInstance>(r => r.Resolve(instance, CancellationToken.None));
        return await h.Use<ManualModule, ModuleCallResult>(m => m.Call(module, path, new ModuleRequest(module.Data, context, input ?? []), key, CancellationToken.None));
    }

    private static JsonObject ServiceContext(long id, string? activation, string? lifecycle) => new()
    {
        ["service"] = new JsonObject
        {
            ["id"] = id,
            ["status"] = "active",
            ["params"] = new JsonObject { ["activation"] = activation, ["lifecycle"] = lifecycle },
        },
        ["account"] = new JsonObject { ["id"] = 1, ["language"] = "es" },
    };

    /// <summary>Escenario 1 de la conformidad: manifiesto y esquemas del módulo manual.</summary>
    [TestMethod]
    public void Manifiesto_DeclaraAmbosTipos_SinSecretosNiCredenciales()
    {
        var manifest = ManualModule.Manifest();

        Assert.AreEqual("manual", manifest["id"]!.GetValue<string>());
        Assert.AreEqual("""["0"]""", manifest["protocol"]!.ToJsonString());
        var capabilities = manifest["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        CollectionAssert.IsSubsetOf(new[] { "provisioning.core", "provisioning.status", "provisioning.view", "registrar.core" }, capabilities);
        Assert.AreEqual("""["activation","lifecycle"]""", manifest["product_schema"]!["required"]!.ToJsonString());
        Assert.AreEqual("[]", manifest["credentials"]!.ToJsonString());
        Assert.IsFalse(manifest.ToJsonString().Contains("x-secret", StringComparison.Ordinal));
        Assert.AreEqual("""["account.contact"]""", manifest["data_scopes"]!.ToJsonString());

        // Una tarea del staff dura días: ninguna operación tiene plazo.
        Assert.IsTrue(manifest["deadlines"]!.AsObject().All(d => d.Value is null));
    }

    [TestMethod]
    public async Task AlPago_QuedaActivoSinTarea()
    {
        var (h, _) = await Start("on_payment", "state_only");
        await using var _h = h;

        await OrderAndPay(h);

        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual("active", service.ObservedStatus);
        Assert.AreEqual(0, await h.Query(db => db.StaffTasks.CountAsync()));
    }

    [TestMethod]
    public async Task ConStaff_EsperaLaTareaDiasSinMorir_YAlCerrarlaQuedaActivoConSusCampos()
    {
        var (h, instance) = await Start("staff", "staff");
        await using var _h = h;

        await OrderAndPay(h);

        var task = await h.Query(db => db.StaffTasks.SingleAsync());
        Assert.AreEqual(StaffTaskStatus.Open, task.Status);
        Assert.AreEqual("create", task.Operation);
        StringAssert.Contains(task.Details, "Configurar el túnel");
        StringAssert.Contains(task.Details, "Miami");
        Assert.AreEqual(ServiceStatus.Provisioning, (await h.Query(db => db.Services.SingleAsync())).Status);

        // Una tarea del staff dura días: el trabajo se consulta cada hora sin gastar intentos ni abrir otra tarea.
        for (var hour = 0; hour < 75; hour++)
        {
            h.Clock.Now = h.Clock.Now.AddHours(1);
            await h.RunJobs();
        }

        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ServiceOperation));
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(1, await h.Query(db => db.StaffTasks.CountAsync()));

        await Close(h, task.Id, new StaffTaskClose("S2S-12", new Dictionary<string, string> { ["tunel"] = "tun-miami-3" }, new Dictionary<string, string> { ["psk"] = "testigo-secreto-psk" }));
        await h.RunJobs();

        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual("S2S-12", JsonNode.Parse(service.ExternalIds)!["staff_ref"]!.GetValue<string>());
        var secret = await h.Query(db => db.ServiceSecrets.SingleAsync(s => s.ServiceId == service.Id && s.Name == "psk"));
        Assert.AreNotEqual(0, secret.Value.Length);

        // El secreto no queda en la tarea ni vuelve en las respuestas del módulo (escenario 7 de la conformidad).
        var closed = await h.Query(db => db.StaffTasks.SingleAsync());
        Assert.IsFalse(closed.Result!.Contains("testigo-secreto-psk", StringComparison.Ordinal));
        var context = new JsonObject
        {
            ["service"] = new JsonObject { ["id"] = service.Id, ["status"] = "active", ["params"] = JsonNode.Parse(service.ModuleParams) },
            ["account"] = new JsonObject { ["id"] = h.AccountId, ["language"] = "es" },
        };
        var view = await Call(h, instance, "/v0/provisioning/view", context);
        var poll = await Call(h, instance, $"/v0/operations/task-{task.Id}", context);
        Assert.IsFalse(view.Response!.Result!.ToJsonString().Contains("testigo", StringComparison.Ordinal));
        Assert.IsFalse(poll.Response!.Result!.ToJsonString().Contains("testigo", StringComparison.Ordinal));
        StringAssert.Contains(view.Response.Result.ToJsonString(), "tun-miami-3");
        var status = await Call(h, instance, "/v0/provisioning/get_status", context);
        Assert.AreEqual("active", status.Response!.Result!["observed_state"]!.GetValue<string>());

        // Con lifecycle staff la suspensión también es una tarea. Rechazada, el trabajo muere y el servicio no cambia.
        var generation = service.Generation;
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(service.Id, "suspend", generation), $"service:{service.Id}", $"service:{service.Id}:suspend:{generation}");
            return true;
        });
        await h.RunJobs();
        var suspend = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Status == StaffTaskStatus.Open));
        Assert.AreEqual("suspend", suspend.Operation);
        await Reject(h, suspend.Id, "El cliente pagó por otro medio");
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.IsTrue(await h.Query(db => db.Jobs.AnyAsync(j => j.Status == JobStatus.Dead && j.LastError!.Contains("rejected"))));
    }

    [TestMethod]
    public async Task RevisionDeBajaAbierta_NoFrenaLasTareasDelServicio_NiSeMezclaConLoQueConfirmoElStaff()
    {
        var (h, instance) = await Start("on_payment", "staff");
        await using var _h = h;
        await OrderAndPay(h);
        var service = await h.Query(db => db.Services.SingleAsync());
        var review = await CoreFlowTests.Review(h, service.Id);

        // Con la revisión abierta, una suspensión del mismo servicio abre su tarea igual.
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(service.Id, "suspend", service.Generation), $"service:{service.Id}", $"service:{service.Id}:suspend:{service.Generation}");
            return true;
        });
        await h.RunJobs();
        var suspend = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Kind == "provisioning" && t.Status == StaffTaskStatus.Open));
        await Close(h, suspend.Id, new StaffTaskClose());
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);

        // El portal no la muestra como configuración en curso, y cerrarla no pisa el estado que confirmó el staff.
        var context = new JsonObject
        {
            ["service"] = new JsonObject { ["id"] = service.Id, ["status"] = "suspended", ["params"] = JsonNode.Parse(service.ModuleParams) },
            ["account"] = new JsonObject { ["id"] = h.AccountId, ["language"] = "es" },
        };
        Assert.DoesNotContain("en curso", (await Call(h, instance, "/v0/provisioning/view", context)).Response!.Result!.ToJsonString());
        await Close(h, review, new StaffTaskClose());
        await h.RunJobs();
        var status = await Call(h, instance, "/v0/provisioning/get_status", context);
        Assert.AreEqual("suspended", status.Response!.Result!["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PedidoDeTotalCero_DeProductoConStaff_NoQuedaActivoSinTarea()
    {
        var (h, _) = await Start("staff", "staff", free: true);
        await using var _h = h;

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.FreePlanId)]));
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Provisioning, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.AreEqual(StaffTaskStatus.Open, (await h.Query(db => db.StaffTasks.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task TareaDeAltaRechazada_DejaElServicioFallido()
    {
        var (h, _) = await Start("staff", "staff");
        await using var _h = h;
        await OrderAndPay(h);

        await Reject(h, (await h.Query(db => db.StaffTasks.SingleAsync())).Id, "Claro no tiene cupo en Miami");
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Failed, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task ServicioDeProductoConModuloSinInstancia_NoSeSuspendeSinModulo()
    {
        await using var h = await CoreHarness.Start();
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();

        // Un producto manual sin instancia sigue completando en el núcleo.
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);

        // Si el producto tiene módulo y el servicio no tiene instancia, la mora no lo da por suspendido.
        await h.Db(async db =>
        {
            (await db.Products.SingleAsync(p => p.Name == "VPS 2 GB")).Kind = ProductKind.Provisioned;
            await db.SaveChangesAsync();
        });
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = h.Clock.Now.AddDays(12);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        var dead = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ServiceOperation && j.Status == JobStatus.Dead));
        StringAssert.Contains(dead.LastError, "está sin instancia");
    }

    /// <summary>Resultado y estado observado por operación y por combinación de activation y lifecycle.</summary>
    [TestMethod]
    [DataRow("on_payment", "state_only", "create", "completed:active")]
    [DataRow("staff", "state_only", "create", "pending")]
    [DataRow("on_payment", "state_only", "suspend", "completed:suspended")]
    [DataRow("on_payment", "state_only", "unsuspend", "completed:active")]
    [DataRow("on_payment", "state_only", "terminate", "completed:terminated")]
    [DataRow("on_payment", "staff", "suspend", "pending")]
    [DataRow("staff", "staff", "unsuspend", "pending")]
    [DataRow("staff", "staff", "terminate", "pending")]
    [DataRow("on_payment", "state_only", "change_plan", "pending")]
    [DataRow("staff", "staff", "validate", "completed:")]
    [DataRow("staff", "staff", "get_status", "completed:unknown")]
    [DataRow(null, "staff", "create", "invalid_input")]
    [DataRow("staff", "otro", "suspend", "invalid_input")]
    [DataRow("staff", "staff", "run_action", "not_supported")]
    [DataRow("staff", "staff", "sso", "not_supported")]
    public async Task TablaDeOperaciones(string? activation, string lifecycle, string operation, string expected)
    {
        var (h, instance) = await Start("staff", "staff");
        await using var _h = h;

        var result = await Call(h, instance, $"/v0/provisioning/{operation}", ServiceContext(4242, activation, lifecycle), key: $"svc-4242-{operation}-1");

        var actual = result.Error is { } e ? e.Code
            : result.Response!.Status == ModuleResponse.PendingStatus ? "pending"
            : $"completed:{result.Response.Result?["observed_state"]?.GetValue<string>()}";
        Assert.AreEqual(expected, actual);
        if (result.Error is { Code: ErrorCodes.InvalidInput } invalid)
        {
            Assert.IsNotNull(invalid.Details?["fields"]);
        }
    }

    /// <summary>Escenarios 3, 4 y 5 de la conformidad sobre el registro de tareas.</summary>
    [TestMethod]
    public async Task Idempotencia_ConflictoYOperacionPerdida()
    {
        var (h, instance) = await Start("staff", "staff");
        await using var _h = h;
        var context = ServiceContext(77, "staff", "staff");

        var first = await Call(h, instance, "/v0/provisioning/create", context, key: "svc-77-create-1");
        var again = await Call(h, instance, "/v0/provisioning/create", context, key: "svc-77-create-1");
        var otherInput = await Call(h, instance, "/v0/provisioning/create", context, new JsonObject { ["x"] = 1 }, "svc-77-create-1");
        var otherKey = await Call(h, instance, "/v0/provisioning/suspend", context, key: "svc-77-suspend-1");
        var lost = await Call(h, instance, "/v0/operations/task-999999", context);
        var noKey = await Call(h, instance, "/v0/provisioning/terminate", ServiceContext(78, "staff", "staff"));

        Assert.AreEqual(first.Response!.OperationId, again.Response!.OperationId);
        Assert.AreEqual(1, await h.Query(db => db.StaffTasks.CountAsync()));
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, otherInput.Error!.Code);
        Assert.AreEqual(ErrorCodes.Conflict, otherKey.Error!.Code);
        Assert.IsTrue(otherKey.Error.Retryable);
        Assert.AreEqual(ErrorCodes.NotFound, lost.Error!.Code);
        Assert.AreEqual(ErrorCodes.InvalidInput, noKey.Error!.Code);
    }
}
