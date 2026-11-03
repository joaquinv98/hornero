using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Lo que el núcleo le debe a los módulos: las credenciales por acción, la etiqueta que informa el alta, los errores
/// definitivos como tarea de revisión del staff y las operaciones largas que no son de la intención en curso.
/// </summary>
[TestClass]
public sealed class ModuleHardeningTests
{
    [TestMethod]
    public async Task AccionDelManifiesto_LlevaLasCredencialesQueDeclara_YLaQueRotaSeGeneraDeNuevo()
    {
        var calls = new ConcurrentQueue<(string Op, JsonObject Input, string? Key)>();
        var manifest = new JsonObject
        {
            ["id"] = "fake",
            ["credentials"] = new JsonArray("root_password"),
            ["actions"] = new JsonArray(
                new JsonObject { ["id"] = "reinstall", ["audience"] = new JsonArray("staff", "client"), ["credentials"] = new JsonArray("root_password") },
                new JsonObject { ["id"] = "change_password", ["audience"] = new JsonArray("staff"), ["rotates_credentials"] = new JsonArray("root_password") },
                new JsonObject { ["id"] = "console", ["audience"] = new JsonArray("client") }),
        };
        var module = new ModuleDefinition(manifest)
            .Operation("provisioning", "create", call =>
            {
                calls.Enqueue(("create", call.Request.Input.DeepClone().AsObject(), call.IdempotencyKey));
                return Task.FromResult(ModuleResponse.Completed(new JsonObject
                {
                    ["observed_state"] = "active",
                    ["label"] = "vm-1.example.net",
                    ["external_ids"] = new JsonObject { ["vmid"] = "1" },
                }));
            })
            .Operation("provisioning", "run_action", call =>
            {
                calls.Enqueue(("run_action", call.Request.Input.DeepClone().AsObject(), call.IdempotencyKey));
                return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["external_ids"] = new JsonObject { ["node"] = "pve2" } }));
            });
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);

        // La etiqueta que informó el alta queda en el servicio, porque el pedido no trajo una.
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual("vm-1.example.net", service.Label);
        var first = await Secret(h, box, serviceId, "root_password");
        Assert.AreEqual(first, calls.Single(c => c.Op == "create").Input["credentials"]!["root_password"]!.GetValue<string>());

        // reinstall usa la credencial que ya tiene el servicio y lleva los parámetros del pedido, con su propia clave.
        await Run(h, serviceId, "reinstall", new JsonObject { ["template"] = "debian-12" });
        await h.RunJobs();
        var reinstall = calls.Single(c => c.Op == "run_action");
        Assert.AreEqual("reinstall", reinstall.Input["action"]!.GetValue<string>());
        Assert.AreEqual("debian-12", reinstall.Input["params"]!["template"]!.GetValue<string>());
        Assert.AreEqual(first, reinstall.Input["credentials"]!["root_password"]!.GetValue<string>());
        StringAssert.StartsWith(reinstall.Key, $"svc-{serviceId}-run_action-reinstall-");
        service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual("pve2", JsonNode.Parse(service.ExternalIds)!["node"]!.GetValue<string>());
        Assert.AreEqual("1", JsonNode.Parse(service.ExternalIds)!["vmid"]!.GetValue<string>());

        // change_password rota la credencial: se genera y se guarda cifrada antes de llamar, y el módulo recibe la nueva.
        await Run(h, serviceId, "change_password", null);
        var rotated = await Secret(h, box, serviceId, "root_password");
        Assert.AreNotEqual(first, rotated);
        await h.RunJobs();
        var change = calls.Last(c => c.Op == "run_action");
        Assert.AreEqual("change_password", change.Input["action"]!.GetValue<string>());
        Assert.AreEqual(rotated, change.Input["credentials"]!["root_password"]!.GetValue<string>());

        // Una acción que no es para el staff, o que no existe, no se encola.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run(h, serviceId, "console", null));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => Run(h, serviceId, "nope", null));
        await h.RunJobs();
        Assert.AreEqual(2, calls.Count(c => c.Op == "run_action"));
    }

    [TestMethod]
    public async Task ManualReviewEnLaBaja_NoSeReintenta_AbreLaRevision_YElReintentoDelStaffLlevaOtraClave()
    {
        var keys = new ConcurrentQueue<string?>();
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })))
            .Operation("provisioning", "terminate", call =>
            {
                keys.Enqueue(call.IdempotencyKey);
                return keys.Count == 1
                    ? throw new ModuleException(ErrorCodes.ManualReview, "La VM vm1 no tiene la marca del servicio", details: new JsonObject { ["vmid"] = "1" })
                    : Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "terminated" }));
            });
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);
        var today = await h.Use<BillingCore, DateOnly>(c => Task.FromResult(c.Today()));

        await Request(h, serviceId, "terminate", null);
        await h.RunJobs();

        // Sin reintentos: el trabajo murió al primer intento, el servicio sigue activo pero marcado para la baja, y el
        // staff ve la revisión con el código y los detalles del módulo.
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ServiceOperation && j.Status == JobStatus.Dead));
        StringAssert.Contains(job.LastError, ErrorCodes.ManualReview);
        Assert.AreEqual(1, job.Attempts);
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual(today, service.CancelAt);
        var task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open));
        Assert.AreEqual(StaffTask.Review, task.Kind);
        Assert.AreEqual("terminate", task.Operation);
        var details = JsonNode.Parse(task.Details)!;
        Assert.AreEqual(ErrorCodes.ManualReview, details["code"]!.GetValue<string>());
        Assert.AreEqual("1", details["details"]!["vmid"]!.GetValue<string>());
        Assert.AreEqual(serviceId, details["service"]!["id"]!.GetValue<long>());

        // Marcado para la baja, la corrida de facturación no lo renueva más: no hay comprobantes de más.
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync()));

        // El staff revisa la VM y vuelve a pedir la baja: es otra intención, va con otra clave, sale y cierra la revisión.
        await Request(h, serviceId, "terminate", null);
        await h.RunJobs();
        Assert.HasCount(2, keys);
        Assert.AreNotEqual(keys.First(), keys.Last());
        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
        task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id));
        Assert.AreEqual(StaffTaskStatus.Completed, task.Status);
        Assert.IsNotNull(task.ClosedAt);
    }

    [TestMethod]
    public async Task BajaTrabadaPorInvalidInput_QuedaParaElStaff_QueLaFuerzaConElInput()
    {
        var inputs = new ConcurrentQueue<JsonObject>();
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })))
            .Operation("provisioning", "terminate", call =>
            {
                inputs.Enqueue(call.Request.Input.DeepClone().AsObject());
                return call.Request.Input["force"]?.GetValue<bool>() == true
                    ? Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "terminated" }))
                    : throw new ModuleException(ErrorCodes.InvalidInput, "Hay 3 agentes instalados", details: new JsonObject { ["fields"] = new JsonArray("force"), ["agents"] = 3 });
            });
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);

        await Request(h, serviceId, "terminate", null);
        await h.RunJobs();

        // Sin reintentos y con la revisión abierta: el staff ve qué pide el módulo (force) y el servicio queda marcado para la baja.
        Assert.AreEqual(1, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ServiceOperation && j.Status == JobStatus.Dead))).Attempts);
        var task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open));
        var details = JsonNode.Parse(task.Details)!;
        Assert.AreEqual(ErrorCodes.InvalidInput, details["code"]!.GetValue<string>());
        Assert.AreEqual("force", details["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsNotNull((await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).CancelAt);

        // El staff cierra la revisión a mano y pide la baja forzada: el módulo recibe input.force.
        await h.Use<StaffTaskService, bool>(async t =>
        {
            await t.Complete(task.Id, new StaffTaskClose());
            return true;
        });
        Assert.AreEqual(StaffTaskStatus.Completed, (await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id))).Status);
        await Request(h, serviceId, "terminate", new JsonObject { ["force"] = true });
        await h.RunJobs();

        Assert.HasCount(2, inputs);
        Assert.IsTrue(inputs.Last()["force"]!.GetValue<bool>());
        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
    }

    [TestMethod]
    public async Task ManualReviewEnElAlta_DejaElServicioFallido_YElStaffReintentaElAlta()
    {
        var creates = 0;
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Interlocked.Increment(ref creates) == 1
                ? throw new ModuleException(ErrorCodes.ManualReview, "Ya hay una VM vm1 sin la marca del servicio")
                : Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })));
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);

        Assert.AreEqual(ServiceStatus.Failed, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
        var task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open));
        Assert.AreEqual("create", task.Operation);

        await Request(h, serviceId, "create", null);
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
        Assert.AreEqual(StaffTaskStatus.Completed, (await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id))).Status);
        Assert.AreEqual(2, creates);

        // El alta solo se reintenta sobre un servicio fallido.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Request(h, serviceId, "create", null));
    }

    [TestMethod]
    public async Task UnaOperacionLargaDeOtraIntencion_NoSeRetoma_YLaPropiaSi()
    {
        var calls = new ConcurrentQueue<string>();
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })))
            .Operation("provisioning", "unsuspend", _ => Done(calls, "unsuspend", "active"))
            .Operation("provisioning", "suspend", _ => Done(calls, "suspend", "suspended"))
            .Poll((_, id) =>
            {
                calls.Enqueue("poll:" + id);
                return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "suspended" }));
            });
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);

        // Quedó en curso una baja de una intención anterior sobre el servicio suspendido: reactivar no la consulta, pide
        // la reactivación entera.
        await h.Db(async db =>
        {
            (await db.Services.SingleAsync(s => s.Id == serviceId)).MoveTo(ServiceStatus.Suspended, h.Clock.Now);
            await db.SaveChangesAsync();
        });
        await SetPending(h, serviceId, "terminate:t-1");
        await Request(h, serviceId, "unsuspend", null);
        await h.RunJobs();
        Assert.Contains("unsuspend", calls);
        Assert.IsFalse(calls.Any(c => c.StartsWith("poll:", StringComparison.Ordinal)));
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.IsNull(service.PendingOperation);
        Assert.AreEqual(ServiceStatus.Active, service.Status);

        // La propia sí se sigue consultando hasta que termina.
        await SetPending(h, serviceId, "suspend:s-7");
        await Request(h, serviceId, "suspend", null);
        await h.RunJobs();
        Assert.Contains("poll:s-7", calls);
        Assert.IsFalse(calls.Contains("suspend"));
        service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.IsNull(service.PendingOperation);
        Assert.AreEqual(ServiceStatus.Suspended, service.Status);
    }

    [TestMethod]
    public async Task AutoRetryEnFalse_EsDefinitivo_YUnaAccionNoCierraLaRevisionDeOtraOperacion()
    {
        var suspends = 0;
        var manifest = new JsonObject
        {
            ["id"] = "fake",
            ["actions"] = new JsonArray(new JsonObject { ["id"] = "reboot", ["audience"] = new JsonArray("staff") }),
        };
        var module = new ModuleDefinition(manifest)
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })))
            .Operation("provisioning", "suspend", _ => Interlocked.Increment(ref suspends) == 1
                ? throw new ModuleException(ErrorCodes.OutcomeUnknown, "El adaptador quedó en un estado que tiene que mirar una persona", details: new JsonObject { ["reason"] = "manual_review_required", ["auto_retry"] = false })
                : Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "suspended" })))
            .Operation("provisioning", "run_action", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject())));
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);

        // outcome_unknown se reintenta, salvo que el módulo pida con auto_retry en false que lo mire una persona.
        await Request(h, serviceId, "suspend", null);
        await h.RunJobs();
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ServiceOperation && j.Status == JobStatus.Dead));
        Assert.AreEqual(1, job.Attempts);
        StringAssert.Contains(job.LastError, ErrorCodes.OutcomeUnknown);
        var task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open));
        Assert.AreEqual("suspend", task.Operation);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, JsonNode.Parse(task.Details)!["code"]!.GetValue<string>());
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);

        // Una acción que sale bien no dice nada de la suspensión trabada: la revisión sigue abierta.
        await Run(h, serviceId, "reboot", null);
        await h.RunJobs();
        Assert.AreEqual(StaffTaskStatus.Open, (await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id))).Status);

        // El staff la vuelve a pedir: sale y cierra la revisión.
        await Request(h, serviceId, "suspend", null);
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
        Assert.AreEqual(StaffTaskStatus.Completed, (await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id))).Status);
    }

    [TestMethod]
    public async Task BajaPorMoraTrabada_NoRenueva_LaMoraLaRepite_YSeLevantaSiElClientePaga()
    {
        var module = new ModuleDefinition(TestModule.Manifest("fake"))
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })))
            .Operation("provisioning", "suspend", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "suspended" })))
            .Operation("provisioning", "unsuspend", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })))
            .Operation("provisioning", "terminate", _ => throw new ModuleException(ErrorCodes.InvalidInput, "Hay 3 agentes instalados", details: new JsonObject { ["fields"] = new JsonArray("force") }));
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box, configure: o => o.TerminationDays = 10);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);

        // La renovación queda sin pagar: la mora suspende pasada la gracia y pide la baja pasado el plazo, y el módulo no la hace.
        h.Clock.Now = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = new DateTimeOffset(2026, 12, 6, 15, 0, 0, TimeSpan.Zero);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
        h.Clock.Now = new DateTimeOffset(2026, 12, 13, 15, 0, 0, TimeSpan.Zero);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();

        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Suspended, service.Status);
        Assert.AreEqual(new DateOnly(2026, 12, 13), service.CancelAt);
        var task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open));
        Assert.AreEqual("terminate", task.Operation);
        Assert.IsTrue(JsonNode.Parse(task.Details)!["dunning"]!.GetValue<bool>());

        // Al día siguiente la fecha de baja la vuelve a pedir y sigue trabada: la revisión sigue siendo la de la mora.
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        Assert.AreEqual(2, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ServiceOperation && j.Status == JobStatus.Dead)));
        task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id));
        Assert.AreEqual(StaffTaskStatus.Open, task.Status);
        Assert.IsTrue(JsonNode.Parse(task.Details)!["dunning"]!.GetValue<bool>());

        // Marcado para la baja no renueva: la corrida no emite la factura del período siguiente.
        h.Clock.Now = new DateTimeOffset(2026, 12, 27, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(2, await h.Query(db => db.Documents.CountAsync()));

        // El cliente paga la renovación: la baja por mora se levanta, el servicio se reactiva y vuelve a renovar.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();
        service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.IsNull(service.CancelAt);
        task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id));
        Assert.AreEqual(StaffTaskStatus.Completed, task.Status);
        Assert.AreEqual("paid", JsonNode.Parse(task.Result!)!["resolved"]!.GetValue<string>());
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(3, await h.Query(db => db.Documents.CountAsync()));

        // La mora ya no tiene nada que pedirle.
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        Assert.AreEqual(2, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ServiceOperation && j.Status == JobStatus.Dead)));
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
    }

    [TestMethod]
    public async Task ReconciliacionDiaria_GuardaLoObservado_FundeLosIds_YDejaRevisionSiNoCoincide()
    {
        var observed = "active";
        var statusCalls = 0;
        var manifest = new JsonObject { ["id"] = "fake", ["capabilities"] = new JsonArray("provisioning.core", "provisioning.status") };
        var module = new ModuleDefinition(manifest)
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active", ["external_ids"] = new JsonObject { ["vmid"] = "1" } })))
            .Operation("provisioning", "get_status", _ =>
            {
                Interlocked.Increment(ref statusCalls);
                return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = observed, ["external_ids"] = new JsonObject { ["node"] = "pve3" } }));
            }, mutating: false);
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var fake = await TestModule.Start(module, h, box);
        var serviceId = await Provision(h, fake.InstanceId);

        // Coincide: queda lo observado y los IDs externos fundidos con los del alta, sin revisión ni operaciones.
        await h.Tick();
        await h.RunJobs();
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual("active", service.ObservedStatus);
        Assert.AreEqual("pve3", JsonNode.Parse(service.ExternalIds)!["node"]!.GetValue<string>());
        Assert.AreEqual("1", JsonNode.Parse(service.ExternalIds)!["vmid"]!.GetValue<string>());
        Assert.IsFalse(await h.Query(db => db.StaffTasks.AnyAsync()));
        Assert.AreEqual(1, statusCalls);

        // El mismo día no se repite. Al día siguiente el panel lo ve suspendido: revisión para el staff y ninguna operación sola.
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(1, statusCalls);
        observed = "suspended";
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();
        service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual("suspended", service.ObservedStatus);
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        var task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open));
        Assert.AreEqual("get_status", task.Operation);
        var details = JsonNode.Parse(task.Details)!;
        Assert.AreEqual(ServiceReconcileHandler.StateMismatch, details["code"]!.GetValue<string>());
        Assert.AreEqual("suspended", details["details"]!["observed_state"]!.GetValue<string>());
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ServiceOperation)));

        // Vuelven a coincidir: la revisión se cierra sola.
        observed = "active";
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(StaffTaskStatus.Completed, (await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task.Id))).Status);
        Assert.AreEqual("active", (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).ObservedStatus);

        // Un servicio con una operación larga en curso no se consulta.
        await SetPending(h, serviceId, "suspend:s-1");
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(3, statusCalls);
    }

    [TestMethod]
    public async Task BajaRechazadaPorElStaffEnElModuloManual_NoAbreRevision_NiMarcaLaBaja()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        long instance = 0;
        await h.Db(async db =>
        {
            instance = await ManualModule.AddInstance(db, "servicios-manuales", new JsonObject { ["queue"] = "servicios" }, h.Clock.Now);
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            product.ModuleInstanceId = instance;
            product.ModuleParams = """{"activation":"on_payment","lifecycle":"staff"}""";
            await db.SaveChangesAsync();
        });
        var serviceId = await Provision(h, instance);
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);

        // La baja abre la tarea del staff, y el staff la rechaza: esa ya es la revisión.
        await Request(h, serviceId, "terminate", null);
        await h.RunJobs();
        var task = await h.Query(db => db.StaffTasks.SingleAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open));
        Assert.AreEqual("terminate", task.Operation);
        await h.Use<StaffTaskService, bool>(async s =>
        {
            await s.Reject(task.Id, "El cliente pidió seguir");
            return true;
        });
        await h.RunJobs();

        // El trabajo muere con el rechazo y no queda ninguna otra tarea: el servicio sigue activo, sin fecha de baja, y renueva.
        Assert.IsTrue(await h.Query(db => db.Jobs.AnyAsync(j => j.Kind == JobKinds.ServiceOperation && j.Status == JobStatus.Dead && j.LastError!.Contains(ErrorCodes.Rejected))));
        Assert.AreEqual(1, await h.Query(db => db.StaffTasks.CountAsync(t => t.Subject == $"service:{serviceId}")));
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.IsNull(service.CancelAt);
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(2, await h.Query(db => db.Documents.CountAsync()));

        // Y la próxima baja vuelve a abrir una tarea: nada la traba.
        await Request(h, serviceId, "terminate", null);
        await h.RunJobs();
        Assert.AreEqual(1, await h.Query(db => db.StaffTasks.CountAsync(t => t.Subject == $"service:{serviceId}" && t.Status == StaffTaskStatus.Open)));
    }

    private static Task<ModuleResponse> Done(ConcurrentQueue<string> calls, string operation, string observed)
    {
        calls.Enqueue(operation);
        return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = observed }));
    }

    private static Task SetPending(CoreHarness h, long serviceId, string pending) => h.Db(async db =>
    {
        (await db.Services.SingleAsync(s => s.Id == serviceId)).PendingOperation = pending;
        await db.SaveChangesAsync();
    });

    private static Task Request(CoreHarness h, long serviceId, string operation, JsonObject? input) => h.Use<ServiceAdmin, bool>(async r =>
    {
        await r.Request(serviceId, operation, input);
        return true;
    });

    private static Task Run(CoreHarness h, long serviceId, string action, JsonObject? parameters) => h.Use<ServiceAdmin, bool>(async r =>
    {
        await r.RunAction(serviceId, action, parameters, "staff");
        return true;
    });

    private static async Task<string> Secret(CoreHarness h, SecretBox box, long serviceId, string name)
    {
        var stored = await h.Query(db => db.ServiceSecrets.SingleAsync(s => s.ServiceId == serviceId && s.Name == name));
        return box.Decrypt(stored.Value, "services.service_secrets", name, serviceId);
    }

    /// <summary>Servicio del producto con módulo, pedido y pago: queda como lo deje el alta del módulo.</summary>
    private static async Task<long> Provision(CoreHarness h, long instanceId)
    {
        await h.Db(async db =>
        {
            (await db.Products.SingleAsync(p => p.Name == "VPS 2 GB")).ModuleInstanceId = instanceId;
            await db.SaveChangesAsync();
        });
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();
        return placed.ServiceIds.Single();
    }
}
