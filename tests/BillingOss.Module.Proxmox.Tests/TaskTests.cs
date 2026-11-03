using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>
/// Tareas de Proxmox que siguen corriendo cuando vence el plazo o cuando llega una repetición: se siguen y nunca se
/// lanza otra igual, que en Proxmox esperaría el lock de la VM y fallaría.
/// </summary>
[TestClass]
public sealed class TaskTests
{
    private static readonly JsonObject Reboot = new() { ["action"] = "reboot" };

    [TestMethod]
    public async Task UnReinicioLento_EsPendingConSuTarea_YLaRepeticionNoReiniciaOtraVez()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Durations["qmreboot"] = TimeSpan.FromHours(1);

        var (status, body) = await host.Send("/v0/provisioning/run_action", Reboot.DeepClone().AsObject(), context, "r1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        var operation = body["operation_id"]!.GetValue<string>();
        StringAssert.StartsWith(operation, "reboot-7001-");

        // La misma clave devuelve el pending guardado y la consulta espera la misma tarea.
        (_, body) = await host.Send("/v0/provisioning/run_action", Reboot.DeepClone().AsObject(), context, "r1");
        Assert.AreEqual(operation, body!["operation_id"]!.GetValue<string>());
        host.Pve.ReleaseTasks();
        (status, body) = await host.PollUntilDone(operation, context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("running", ModuleHost.Result(body)["power"]!.GetValue<string>());
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/7001/status/reboot"));
    }

    [TestMethod]
    public async Task UnReinicioQueSigueCorriendo_SeSigueAunqueSeHayaPerdidoLaClave()
    {
        // Un corte después del POST con la clave olvidada: la repetición encuentra la tarea en curso y no lanza otra.
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Durations["qmreboot"] = TimeSpan.FromHours(1);
        await host.Send("/v0/provisioning/run_action", Reboot.DeepClone().AsObject(), context, "r1");

        var (status, body) = await host.Send("/v0/provisioning/run_action", Reboot.DeepClone().AsObject(), context, "r2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/7001/status/reboot"));
    }

    [TestMethod]
    public async Task ReiniciarUnaVmApagada_EsRejected_NoUnConflictQueSeReintenta()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Vm(7001)!.Status = "stopped";

        var (status, body) = await host.Send("/v0/provisioning/run_action", Reboot.DeepClone().AsObject(), context, "r1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.Rejected, ModuleHost.Code(body));
        Assert.AreEqual("vm_stopped", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task OtraTareaDeLaVmEnCurso_EsConflict_SinLanzarNada()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Durations["qmreboot"] = TimeSpan.FromHours(1);
        await host.Send("/v0/provisioning/run_action", Reboot.DeepClone().AsObject(), context, "r1");

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "s1");

        Assert.AreEqual(HttpStatusCode.Conflict, status, body?.ToJsonString());
        Assert.AreEqual("task_running", ModuleHost.Reason(body));
        Assert.AreEqual(0, host.Pve.Count("POST /nodes/pve1/qemu/7001/status/stop"));
    }

    [TestMethod]
    public async Task UnaBajaRepetidaConElBorradoEnCurso_NoLanzaOtroDelete()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Durations["qmdestroy"] = TimeSpan.FromHours(1);
        await host.Send("/v0/provisioning/terminate", [], context, "t1");

        // Otra clave (la anterior se perdió): la consulta y la repetición siguen el mismo borrado.
        var (status, body) = await host.Send("/v0/provisioning/terminate", [], context, "t2");
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        (_, body) = await host.Poll("terminate-7001", context);
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());

        host.Pve.ReleaseTasks();
        (status, body) = await host.PollUntilDone("terminate-7001", context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
        Assert.AreEqual(1, host.Pve.Count("DELETE /nodes/pve1/qemu/7001"));
    }

    [TestMethod]
    public async Task UnBorradoQueFallaConLaVmYaBorrada_EsUnaBajaHecha()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.FailNextTask = "qmdestroy";
        host.Pve.FailNextTaskAfterEffect = true;
        host.Pve.FailNextTaskExit = "Configuration file 'nodes/pve1/qemu-server/7001.conf' does not exist";

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], context, "t1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnBorradoConAvisos_CompletaLaBaja_YDiceQueVolumenesRevisar()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.DestroyWarnings = true;

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], context, "t1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var result = ModuleHost.Result(body);
        Assert.AreEqual("terminated", result["observed_state"]!.GetValue<string>());
        Assert.AreEqual("destroy_warnings", result["details"]!["reason"]!.GetValue<string>());
        CollectionAssert.Contains(result["details"]!["volumes"]!.AsArray().Select(v => v!.GetValue<string>()).ToList(), "local-zfs:vm-7001-disk-0");
    }

    [TestMethod]
    public async Task UnaConsultaDeReinicioConOtraTarea_EsNotFound()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        var upid = "UPID:pve1:00000001:00000001:00000001:qmstop:7001:billing@pve!modulo:";
        var hex = Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(upid));

        var (status, body) = await host.Poll($"reboot-7001-{hex}", context);

        Assert.AreEqual(HttpStatusCode.NotFound, status, body?.ToJsonString());
    }
}
