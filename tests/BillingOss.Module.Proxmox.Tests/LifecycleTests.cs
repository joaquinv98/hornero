using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>Suspensión, reactivación, baja, acciones y reinstalación sobre una VM ya creada.</summary>
[TestClass]
public sealed class LifecycleTests
{
    [TestMethod]
    public async Task Suspension_RevocaApagaQuitaLaAclYDejaOnbootEn0_YSePuedeRepetir()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.Users.Add("client42@pve");

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "svc-7001-suspend-1");
        var again = await host.Send("/v0/provisioning/suspend", [], context, "svc-7001-suspend-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(HttpStatusCode.OK, again.Status, again.Body?.ToJsonString());
        Assert.AreEqual("suspended", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
        var vm = host.Pve.Vm(7001)!;
        Assert.AreEqual("stopped", vm.Status);
        Assert.AreEqual("0", vm.Config["onboot"]);
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/7001/status/stop"));
        CollectionAssert.Contains(host.Pve.Acl, "delete /vms/7001 client42@pve PVEVMUser");
        Assert.HasCount(2, host.Pve.Revoked);
        Assert.AreEqual(7001, host.Pve.Revoked[0]["sub"]!.GetValue<long>());
        Assert.AreEqual("6", host.Pve.Revoked[0]["server"]!.GetValue<string>());
        Assert.IsFalse(host.Pve.Calls.Any(c => c.StartsWith("POST /access", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Suspension_ConElGatewayCaido_NoTocaLaVm_YEsReintentable()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.GatewayDown = true;

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "svc-7001-suspend-1");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual("running", host.Pve.Vm(7001)!.Status);
        Assert.AreEqual("1", host.Pve.Vm(7001)!.Config["onboot"]);
    }

    [TestMethod]
    public async Task SinGatewayConfigurado_LaSuspensionNoRevoca()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        var config = host.Config();
        config.Remove("console_origin");
        config.Remove("console_secret");

        var (status, _) = await host.Send("/v0/provisioning/suspend", [], context, "k", config);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsEmpty(host.Pve.Revoked);
    }

    [TestMethod]
    public async Task SinLimpiezaHeredada_NoHayLlamadasAAccess()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.Users.Add("client42@pve");
        var config = host.Config();
        config["legacy_acl_cleanup"] = false;

        await host.Send("/v0/provisioning/suspend", [], context, "k", config);
        await host.Send("/v0/provisioning/terminate", [], context, "k2", config);

        Assert.IsFalse(host.Pve.Calls.Any(c => c.Contains("/access/users", StringComparison.Ordinal) || c.Contains("/access/acl", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Reactivacion_PrendeYPoneOnbootEn1()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        await host.Send("/v0/provisioning/suspend", [], context, "svc-7001-suspend-1");

        var (status, body) = await host.Send("/v0/provisioning/unsuspend", [], context, "svc-7001-unsuspend-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
        Assert.AreEqual("running", host.Pve.Vm(7001)!.Status);
        Assert.AreEqual("1", host.Pve.Vm(7001)!.Config["onboot"]);
    }

    [TestMethod]
    public async Task Reactivacion_SinAltaCompleta_EsConflict()
    {
        await using var host = await ModuleHost.Start();

        var (status, body) = await host.Send("/v0/provisioning/unsuspend", [], ModuleHost.Context(), "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual("create_incomplete", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task Baja_BorraLaVmYLiberaLaEntradaDelPool_YRepetirlaEsCompleted()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], context, "svc-7001-terminate-1");
        var again = await host.Send("/v0/provisioning/terminate", [], context, "svc-7001-terminate-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
        Assert.AreEqual("terminated", ModuleHost.Result(again.Body)["observed_state"]!.GetValue<string>());
        Assert.IsNull(host.Pve.Vm(7001));
        Assert.HasCount(1, host.Pve.Revoked);

        // La entrada vuelve a estar libre para el alta siguiente.
        var (_, next) = await host.Create(7002);
        Assert.AreEqual("bc:24:11:aa:00:01", ModuleHost.Result(next)["external_ids"]!["mac"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Baja_DeUnaVmConOtraMarca_EsManualReview_YNoSeBorra()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.Vm(7001)!.Config["description"] = "la tocaron a mano";

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], context, "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(body));
        Assert.IsNotNull(host.Pve.Vm(7001));
        Assert.AreEqual(0, host.Pve.Count("DELETE /nodes/pve1/qemu/7001"));
    }

    [TestMethod]
    public async Task Baja_SinVmAuditNoDaPorBorradaUnaVmQueNoVe()
    {
        await using var host = await ModuleHost.Start();
        var context = ModuleHost.Context(ids: new JsonObject { ["vmid"] = "7001" });
        host.Pve.VmAudit = false;

        var (_, body) = await host.Send("/v0/provisioning/terminate", [], context, "k");

        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.AreEqual("vm_audit_missing", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task Baja_DeUnAltaQueNoTermino_EncuentraLaVmPorNombreYMarca()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.FailIpSetEntry = true;
        await host.Create();
        Assert.IsNotNull(host.Pve.Vm(7001));

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], ModuleHost.Context(), "k");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsNull(host.Pve.Vm(7001));
    }

    [TestMethod]
    public async Task Baja_SinVmidConUnClonEnCurso_EsPending_YNoDaPorBorrada()
    {
        // La configuración temporal del clon no tiene nombre ni marca: la baja espera a que termine.
        await using var host = await ModuleHost.Start();
        host.Pve.Durations["qmclone"] = TimeSpan.FromHours(1);
        var (_, created) = await host.Create();
        Assert.AreEqual("in_progress", ModuleHost.Reason(created), created?.ToJsonString());

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], ModuleHost.Context(), "k");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>(), body.ToJsonString());
        Assert.IsNotNull(host.Pve.Vm(7001));

        host.Pve.ReleaseTasks();
        (status, body) = await host.PollUntilDone(body["operation_id"]!.GetValue<string>(), ModuleHost.Context());
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsNull(host.Pve.Vm(7001));
    }

    [TestMethod]
    public async Task ConElInventarioSinNombres_UnImportadoSinMarcaSeSuspende()
    {
        // pvestatd trabado: el inventario no trae el nombre, la configuración sí.
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(121, "vm7301", mac: "bc:24:11:aa:00:02");
        host.Pve.StaleInventory = true;

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], ModuleHost.Context(7301, new JsonObject { ["vmid"] = "121" }), "k");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("stopped", host.Pve.Vm(121)!.Status);
    }

    [TestMethod]
    public async Task UnaReinstalacionQueFallaDespuesDeClonar_NoDejaElServicioSinSalida()
    {
        // La VM queda con la marca nueva y external_ids con la anterior: sigue siendo del servicio.
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.FailNextTask = "qmstart";

        var (status, body) = await Reinstall(host, context, "r1");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        var newMarker = host.Pve.Vm(7001)!.Config["description"];
        Assert.AreNotEqual(context["service"]!["external_ids"]!["marker"]!.GetValue<string>(), newMarker);

        (status, body) = await host.Send("/v0/provisioning/get_status", [], context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(newMarker, ModuleHost.Result(body)["external_ids"]!["marker"]!.GetValue<string>());

        (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "s1");
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("suspended", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinPermisoParaQuitarLaAcl_LaSuspensionFallaSinApagarLaVm()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.Users.Add("client42@pve");
        host.Pve.AclAllowed = false;

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "s1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("running", host.Pve.Vm(7001)!.Status);
        Assert.AreEqual("1", host.Pve.Vm(7001)!.Config["onboot"]);
    }

    [TestMethod]
    public async Task LaBaja_NoQuitaLaAclHeredada_PorqueElDeleteLaBorra()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.Users.Add("client42@pve");
        host.Pve.AclAllowed = false;

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], context, "t1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsNull(host.Pve.Vm(7001));
    }

    [TestMethod]
    public async Task UnClonTerminadoQueNoSePuedeConfigurar_QuedaConOnbootEn0()
    {
        // El núcleo dejó de repetir create durante el clon y la consulta no trae la contraseña.
        await using var host = await ModuleHost.Start();
        host.Pve.Durations["qmclone"] = TimeSpan.FromHours(1);
        await host.Create();
        host.Pve.ReleaseTasks();
        var marker = host.Pve.Vm(7001)!.Config["description"];
        Assert.AreEqual("1", host.Pve.Vm(7001)!.Config["onboot"]);

        var (status, body) = await host.Poll($"create-7001-{marker[(marker.LastIndexOf(':') + 1)..]}", ModuleHost.Context());

        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(body), body?.ToJsonString());
        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual("0", host.Pve.Vm(7001)!.Config["onboot"]);
    }

    [TestMethod]
    public async Task Reinstalacion_SinIpPrivadaEnExternalIds_LaTomaDelPoolConLaMismaMacEIp()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        var ids = context["service"]!["external_ids"]!.AsObject();
        ids.Remove("private_ip");
        ids.Remove("mtu");

        var (status, body) = await Reinstall(host, context, "r1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("10.20.0.11", ModuleHost.Result(body)["external_ids"]!["private_ip"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "10.20.0.11" }, host.Pve.Vm(7001)!.IpSets["ipfilter-net0"]);
    }

    [TestMethod]
    public async Task Reinstalacion_ReservaLaMacMientrasClona_YUnAltaNoLaToma()
    {
        // La entrada del servicio es la primera del pool: sin la reserva, un alta en esa ventana la tomaba siempre.
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        var mac = context["service"]!["external_ids"]!["mac"]!.GetValue<string>();
        host.Pve.Durations["qmclone"] = TimeSpan.FromHours(1);

        var (_, body) = await Reinstall(host, context, "r1");
        Assert.AreEqual("in_progress", ModuleHost.Reason(body), body?.ToJsonString());
        var reserve = host.Pve.Vm(2007001)!;
        Assert.AreEqual(mac, reserve.Mac);
        Assert.AreEqual("0", reserve.Config["onboot"]);
        StringAssert.StartsWith(reserve.Config["description"], "billing-oss-reserva:7001");

        host.Pve.Durations.Remove("qmclone");
        var (status, other) = await host.Create(7100);
        Assert.AreEqual(HttpStatusCode.OK, status, other?.ToJsonString());
        Assert.AreNotEqual(mac, ModuleHost.Result(other)["external_ids"]!["mac"]!.GetValue<string>());

        host.Pve.ReleaseTasks();
        (status, body) = await Reinstall(host, context, "r1");
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(mac, host.Pve.Vm(7001)!.Mac);
        Assert.IsNull(host.Pve.Vm(2007001));
    }

    [TestMethod]
    public async Task LaBaja_BorraLaReservaQueDejoUnaReinstalacionSinTerminar()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.FailNextTask = "qmclone";
        await Reinstall(host, context, "r1");
        Assert.IsNull(host.Pve.Vm(7001));
        Assert.IsNotNull(host.Pve.Vm(2007001));

        var (status, body) = await host.Send("/v0/provisioning/terminate", [], context, "t1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsNull(host.Pve.Vm(2007001));
    }

    [TestMethod]
    public async Task BorradoLento_EsPendingYLaConsultaTermina()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.Durations["qmdestroy"] = TimeSpan.FromHours(1);

        var (_, body) = await host.Send("/v0/provisioning/terminate", [], context, "k");

        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.AreEqual("terminate-7001", body["operation_id"]!.GetValue<string>());
        host.Pve.ReleaseTasks();
        var (status, done) = await host.PollUntilDone("terminate-7001", context);
        Assert.AreEqual(HttpStatusCode.OK, status, done?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(done)["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Acciones_StopStartYRebootSobreElNodoActual()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);

        var (_, stop) = await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "stop" }, context, "a1");
        Assert.AreEqual("stopped", host.Pve.Vm(7001)!.Status, stop?.ToJsonString());

        // Stop sobre una VM apagada no hace nada.
        await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "stop" }, context, "a2");
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/7001/status/stop"));

        await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "start" }, context, "a3");
        Assert.AreEqual("running", host.Pve.Vm(7001)!.Status);
        Assert.AreEqual(2, host.Pve.Count("POST /nodes/pve1/qemu/7001/status/start"));

        // La migran a mano a otro nodo: el reinicio va al nodo nuevo sin tocar external_ids.
        host.Pve.Vm(7001)!.Node = "pve2";
        var (status, reboot) = await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "reboot" }, context, "a4");
        Assert.AreEqual(HttpStatusCode.OK, status, reboot?.ToJsonString());
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve2/qemu/7001/status/reboot"));
        Assert.AreEqual("pve2", ModuleHost.Result(reboot)["external_ids"]!["node"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Acciones_EnUnServicioSuspendido_SeRechazan()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        context["service"]!["status"] = "suspended";

        var (status, body) = await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "start" }, context, "a1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("service_not_active", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task Acciones_UnaAccionDesconocida_EsInvalidInput()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);

        var (_, body) = await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "format_disk" }, context, "a1");

        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task Reinstalacion_ConservaVmidMacEIp_ConMarcaNueva()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        var before = context["service"]!["external_ids"]!.DeepClone();

        var (status, body) = await Reinstall(host, context, "r1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var ids = ModuleHost.Result(body)["external_ids"]!;
        Assert.AreEqual(before["vmid"]!.GetValue<string>(), ids["vmid"]!.GetValue<string>());
        Assert.AreEqual(before["mac"]!.GetValue<string>(), ids["mac"]!.GetValue<string>());
        Assert.AreEqual(before["public_ip"]!.GetValue<string>(), ids["public_ip"]!.GetValue<string>());
        Assert.AreNotEqual(before["marker"]!.GetValue<string>(), ids["marker"]!.GetValue<string>());
        Assert.IsNotNull(ids["last_reinstall_at"]);
        var vm = host.Pve.Vm(7001)!;
        Assert.AreEqual(ids["marker"]!.GetValue<string>(), vm.Config["description"]);
        Assert.AreEqual("bc:24:11:aa:00:01", vm.Mac);
        Assert.AreEqual("running", vm.Status);
        Assert.AreEqual("1", vm.Config["onboot"]);
        Assert.AreEqual(1, host.Pve.Count("DELETE /nodes/pve1/qemu/7001"));
        Assert.AreEqual(2, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
        Assert.HasCount(1, host.Pve.Revoked);
    }

    [TestMethod]
    public async Task Reinstalacion_AntesDe30Minutos_EsConflictConRetryAfter()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        var (_, first) = await Reinstall(host, context, "r1");
        context["service"]!["external_ids"] = ModuleHost.Result(first)["external_ids"]!.DeepClone();

        var (status, body) = await Reinstall(host, context, "r2");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual("reinstall_wait", ModuleHost.Reason(body));
        Assert.IsGreaterThan(1700, body!["error"]!["retry_after"]!.GetValue<int>());
        Assert.AreEqual(1, host.Pve.Count("DELETE /nodes/pve1/qemu/7001"));
    }

    [TestMethod]
    public async Task Reinstalacion_DeUnVpsImportadoConIpFueraDelPool_ConservaSuRed()
    {
        await using var host = await ModuleHost.Start();

        // Un VPS que vino del sistema anterior: VMID viejo, sin marca, IP de otra red y bridge que no está en el pool.
        var legacy = host.Pve.AddVm(120, "vm7300", mac: "bc:24:11:bb:00:07");
        legacy.Config["net0"] = "virtio=BC:24:11:BB:00:07,bridge=vmbr1,firewall=1,rate=3.13";
        var ids = new JsonObject
        {
            ["vmid"] = "120",
            ["mac"] = "bc:24:11:bb:00:07",
            ["public_ip"] = "198.51.100.7",
            ["bridge"] = "vmbr1",
            ["username"] = "client42",
        };
        var context = ModuleHost.Context(7300, ids);

        var (status, body) = await Reinstall(host, context, "r1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var vm = host.Pve.Vm(120)!;
        Assert.AreEqual("bc:24:11:bb:00:07", vm.Mac);
        StringAssert.Contains(vm.Config["net0"], "bridge=vmbr1");
        Assert.AreEqual("vm7300", vm.Name);
        Assert.AreEqual("198.51.100.7", ModuleHost.Result(body)["external_ids"]!["public_ip"]!.GetValue<string>());
        Assert.IsFalse(vm.IpSets.ContainsKey("ipfilter-net0"));
        Assert.AreEqual("0", vm.Firewall["ipfilter"]);
    }

    [TestMethod]
    public async Task Reinstalacion_ConExternalIdsQueNoCoincidenConLaVm_EsManualReview_YNoBorra()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        context["service"]!["external_ids"]!["mac"] = "bc:24:11:aa:00:09";

        var (_, body) = await Reinstall(host, context, "r1");

        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(body));
        Assert.AreEqual("network_mismatch", ModuleHost.Reason(body));
        Assert.AreEqual(0, host.Pve.Count("DELETE /nodes/pve1/qemu/7001"));
    }

    [TestMethod]
    public async Task Reinstalacion_CortadaEntreElBorradoYElClon_SeRetomaConLaMismaClave()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);
        host.Pve.Durations["qmclone"] = TimeSpan.FromHours(1);

        var (_, body) = await Reinstall(host, context, "r1");
        Assert.AreEqual("in_progress", ModuleHost.Reason(body));
        Assert.AreEqual("clone", host.Pve.Vm(7001)!.Config["lock"]);

        host.Pve.ReleaseTasks();
        var (status, done) = await Reinstall(host, context, "r1");

        Assert.AreEqual(HttpStatusCode.OK, status, done?.ToJsonString());
        Assert.AreEqual(1, host.Pve.Count("DELETE /nodes/pve1/qemu/7001"));
        Assert.AreEqual("bc:24:11:aa:00:01", host.Pve.Vm(7001)!.Mac);
    }

    [TestMethod]
    public async Task Reinstalacion_SinContrasena_EsInvalidInput()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);

        var (_, body) = await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "reinstall" }, context, "r1");

        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        Assert.AreEqual(0, host.Pve.Count("DELETE /nodes/pve1/qemu/7001"));
    }

    [TestMethod]
    public async Task RevokeConsole_RevocaLasSesionesDelServicio()
    {
        await using var host = await ModuleHost.Start();
        var context = await Provisioned(host);

        var (status, _) = await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "revoke_console" }, context, "rc1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.HasCount(1, host.Pve.Revoked);
    }

    internal static async Task<JsonObject> Provisioned(ModuleHost host, long service = 7001)
    {
        var (status, body) = await host.Create(service);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        return ModuleHost.Context(service, (JsonObject)ModuleHost.Result(body)["external_ids"]!.DeepClone());
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> Reinstall(ModuleHost host, JsonObject context, string key)
    {
        var input = host.Credentials();
        input["action"] = "reinstall";
        return host.Send("/v0/provisioning/run_action", input, context, key);
    }
}
