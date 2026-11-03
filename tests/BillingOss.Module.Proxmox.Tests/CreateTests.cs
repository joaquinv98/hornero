using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>El alta: pasos, idempotencia, operación larga, VMID, asignación de red y fallas.</summary>
[TestClass]
public sealed class CreateTests
{
    [TestMethod]
    public async Task Alta_DejaLaVmArmadaYDevuelveIdsYEtiqueta()
    {
        await using var host = await ModuleHost.Start();

        var (status, body) = await host.Create(p: ModuleHost.Params(disk: 20));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var result = ModuleHost.Result(body);
        Assert.AreEqual("active", result["observed_state"]!.GetValue<string>());
        Assert.AreEqual("Host_203-0-113-11.vps.example.com", result["label"]!.GetValue<string>());
        var ids = result["external_ids"]!;
        Assert.AreEqual("7001", ids["vmid"]!.GetValue<string>());
        Assert.AreEqual("pve1", ids["node"]!.GetValue<string>());
        Assert.AreEqual("vm7001", ids["name"]!.GetValue<string>());
        Assert.AreEqual("bc:24:11:aa:00:01", ids["mac"]!.GetValue<string>());
        Assert.AreEqual("203.0.113.11", ids["public_ip"]!.GetValue<string>());
        Assert.AreEqual("10.20.0.11", ids["private_ip"]!.GetValue<string>());
        Assert.AreEqual("vmbr2", ids["bridge"]!.GetValue<string>());
        Assert.AreEqual("1360", ids["mtu"]!.GetValue<string>());
        Assert.AreEqual("client42", ids["username"]!.GetValue<string>());
        StringAssert.Matches(ids["marker"]!.GetValue<string>(), new System.Text.RegularExpressions.Regex("^billing-oss:7001:[0-9a-f]{48}$"));

        var vm = host.Pve.Vm(7001)!;
        Assert.AreEqual("running", vm.Status);
        Assert.AreEqual("1", vm.Config["onboot"]);
        Assert.AreEqual(ids["marker"]!.GetValue<string>(), vm.Config["description"]);
        Assert.AreEqual("vm7001", vm.Name);
        Assert.AreEqual("vps-console", vm.Pool);
        Assert.AreEqual(20m, vm.DiskGb);
        Assert.AreEqual("1", vm.Config["cores"]);
        Assert.AreEqual("1024", vm.Config["memory"]);
        Assert.AreEqual("client42", vm.Config["ciuser"]);
        Assert.AreEqual(host.Password, vm.CiPassword);
        Assert.AreEqual("1", vm.Config["agent"]);
        Assert.AreEqual("virtio=BC:24:11:AA:00:01,bridge=vmbr2,firewall=1,mtu=1360,rate=1.25", vm.Config["net0"]);
        Assert.AreEqual("1", vm.Firewall["enable"]);
        Assert.AreEqual("1", vm.Firewall["macfilter"]);
        Assert.AreEqual("1", vm.Firewall["ipfilter"]);
        Assert.AreEqual("ACCEPT", vm.Firewall["policy_in"]);
        Assert.AreEqual("ACCEPT", vm.Firewall["policy_out"]);
        Assert.AreEqual("0", vm.Firewall["radv"]);
        CollectionAssert.AreEqual(new[] { "10.20.0.11" }, vm.IpSets["ipfilter-net0"]);
        Assert.AreEqual("vps-vmbr2", vm.Rules.Single()["action"]);

        // El orden importa: onboot en 0 apenas termina el clon, ipset antes que ipfilter, arranque y onboot en 1 al final.
        var calls = host.Pve.Calls.ToList();
        Assert.IsLessThan(calls.IndexOf("PUT /nodes/pve1/qemu/7001/resize"), calls.IndexOf("PUT /nodes/pve1/qemu/7001/config"));
        Assert.IsLessThan(calls.IndexOf("PUT /nodes/pve1/qemu/7001/firewall/options"), calls.IndexOf("POST /nodes/pve1/qemu/7001/firewall/ipset/ipfilter-net0"));
        Assert.IsLessThan(calls.IndexOf("POST /nodes/pve1/qemu/7001/status/start"), calls.IndexOf("PUT /nodes/pve1/qemu/7001/firewall/options"));
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
    }

    [TestMethod]
    public async Task MismaClave_NoClonaDosVeces_YDevuelveLoMismo()
    {
        await using var host = await ModuleHost.Start();

        var first = await host.Create();
        var second = await host.Create();

        Assert.AreEqual(HttpStatusCode.OK, second.Status);
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
    }

    [TestMethod]
    public async Task ClonLento_ContestaConflictSinPerderLaContrasena_YLaRepeticionTerminaSinClonarOtraVez()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.Durations["qmclone"] = TimeSpan.FromHours(1);

        var (status, body) = await host.Create();

        // Antes de cargar la contraseña no hay pending: la consulta no la trae y el núcleo tiene que repetir create.
        Assert.AreEqual(HttpStatusCode.Conflict, status, body?.ToJsonString());
        Assert.AreEqual("in_progress", ModuleHost.Reason(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual("clone", host.Pve.Vm(7001)!.Config["lock"]);

        // La repetición, con el clon todavía en curso, espera: no elige otro VMID ni clona de nuevo.
        (_, body) = await host.Create();
        Assert.AreEqual("in_progress", ModuleHost.Reason(body));

        host.Pve.ReleaseTasks();
        (status, body) = await host.Create();

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("7001", ModuleHost.Result(body)["external_ids"]!["vmid"]!.GetValue<string>());
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
        Assert.AreEqual(host.Password, host.Pve.Vm(7001)!.CiPassword);
    }

    [TestMethod]
    public async Task ClonConLaMarcaYElInventarioSinNombre_LaRepeticionReconoceSuClon_SinClonarOtraVez()
    {
        // El clon ya escribió la marca y pvestatd todavía no publicó el nombre: la repetición decide con la configuración.
        await using var host = await ModuleHost.Start();
        host.Pve.Durations["qmclone"] = TimeSpan.FromHours(1);
        host.Pve.CloneConfigEarly = true;

        var (_, body) = await host.Create();
        Assert.AreEqual("in_progress", ModuleHost.Reason(body));
        host.Pve.StaleInventory = true;

        (_, body) = await host.Create();
        Assert.AreEqual("in_progress", ModuleHost.Reason(body));
        Assert.IsNull(host.Pve.Vm(1007001));

        host.Pve.ReleaseTasks();
        var (status, done) = await host.Create();

        Assert.AreEqual(HttpStatusCode.OK, status, done?.ToJsonString());
        Assert.AreEqual("7001", ModuleHost.Result(done)["external_ids"]!["vmid"]!.GetValue<string>());
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
    }

    [TestMethod]
    public async Task ConElUuidDelServicio_LaMarcaLoLleva_YLaRepeticionLaReconoce()
    {
        await using var host = await ModuleHost.Start();
        const string uuid = "0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e11";
        var context = ModuleHost.Context();
        context["service"]!["uuid"] = uuid;

        var (status, body) = await host.Send("/v0/provisioning/create", host.Credentials(), context, "svc-7001-create-1");
        var (again, repeated) = await host.Send("/v0/provisioning/create", host.Credentials(), context.DeepClone().AsObject(), "svc-7001-create-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(HttpStatusCode.OK, again, repeated?.ToJsonString());
        var marker = ModuleHost.Result(body)["external_ids"]!["marker"]!.GetValue<string>();
        StringAssert.StartsWith(marker, $"billing-oss:7001:{uuid}:");
        Assert.AreEqual(marker, host.Pve.Vm(7001)!.Config["description"]);
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
    }

    [TestMethod]
    public async Task UnUuidInvalido_EsInvalidInput_SinLlamarAProxmox()
    {
        await using var host = await ModuleHost.Start();
        var context = ModuleHost.Context();
        context["service"]!["uuid"] = "no-es-un-uuid";

        var (status, body) = await host.Send("/v0/provisioning/create", host.Credentials(), context, "svc-7001-create-1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.IsNotNull(body!["error"]!["details"]!["fields"]!["service.uuid"]);
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task ConElInventarioSinNombres_ValidaLaPlantillaConSuConfiguracion()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.StaleInventory = true;

        var (status, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = ModuleHost.Params() }, ModuleHost.Context());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
    }

    [TestMethod]
    public async Task ArranqueLento_ContestaPending_YOtraReplicaTerminaLaConsulta()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.Durations["qmstart"] = TimeSpan.FromHours(1);

        var (status, body) = await host.Create();

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        var operation = body["operation_id"]!.GetValue<string>();
        StringAssert.StartsWith(operation, "create-7001-");

        // Otra réplica, con su propio registro de idempotencia y nada en memoria, sigue la operación.
        await using var other = await ModuleHost.Start(host.Pve);
        host.Pve.ReleaseTasks();
        (status, body) = await other.PollUntilDone(operation, ModuleHost.Context());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
        Assert.AreEqual("1", host.Pve.Vm(7001)!.Config["onboot"]);
        Assert.AreEqual(1, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
    }

    [TestMethod]
    public async Task OperacionDesconocida_EsNotFound()
    {
        await using var host = await ModuleHost.Start();

        var (status, body) = await host.Poll("create-7001-xyz", ModuleHost.Context());

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task UnaVmPreexistenteConElNombreDelServicio_EsManualReview_YNoSeClona()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(7001, "vm7001", description: "otra cosa");

        var (status, body) = await host.Create();

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(0, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
        Assert.AreEqual("otra cosa", host.Pve.Vm(7001)!.Config["description"]);
    }

    [TestMethod]
    public async Task UnServicioQueChocaConUnaPlantilla_UsaElVmidAlternativo()
    {
        await using var host = await ModuleHost.Start();

        var (status, body) = await host.Create(service: 9002, p: ModuleHost.Params(9002, disk: 40, cores: 2, memory: 4096));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var ids = ModuleHost.Result(body)["external_ids"]!;
        Assert.AreEqual("1009002", ids["vmid"]!.GetValue<string>());
        Assert.AreEqual("vm9002", ids["name"]!.GetValue<string>());
        Assert.AreEqual("Administrador", ids["username"]!.GetValue<string>());
        Assert.IsTrue(host.Pve.Vm(9002)!.Template);
    }

    [TestMethod]
    public async Task UnVmidOcupadoPorOtraVm_UsaElAlternativo_SinTocarLaOtra()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(7001, "otra-vm", description: "de otro sistema");

        var (status, body) = await host.Create();

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("1007001", ModuleHost.Result(body)["external_ids"]!["vmid"]!.GetValue<string>());
        Assert.AreEqual("de otro sistema", host.Pve.Vm(7001)!.Config["description"]);
    }

    [TestMethod]
    public async Task LaPrimeraEntradaLibre_SaltaLasMacsDelCluster()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(500, "ajena", mac: "bc:24:11:aa:00:01");

        var (_, body) = await host.Create();

        Assert.AreEqual("bc:24:11:aa:00:02", ModuleHost.Result(body)["external_ids"]!["mac"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DosAltasALaVezConUnaSolaEntradaLibre_UnaTerminaYLaOtraAgotaElPool()
    {
        await using var host = await ModuleHost.Start();
        var config = host.Config(ModuleHost.Pool(1));

        async Task<(HttpStatusCode Status, JsonNode? Body)> Run(long service)
        {
            for (var i = 0; ; i++)
            {
                var result = await host.Send("/v0/provisioning/create", host.Credentials(), ModuleHost.Context(service), $"svc-{service}-create-1", config);
                if (ModuleHost.Code(result.Body) != ErrorCodes.Conflict || i > 20)
                {
                    return result;
                }
            }
        }

        var results = await Task.WhenAll(Run(7101), Run(7102));

        var done = results.Where(r => r.Status == HttpStatusCode.OK).ToList();
        var exhausted = results.Where(r => ModuleHost.Code(r.Body) == ErrorCodes.InvalidConfig).ToList();
        Assert.HasCount(1, done, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        Assert.HasCount(1, exhausted);
        Assert.AreEqual("pool_exhausted", ModuleHost.Reason(exhausted[0].Body));
        Assert.HasCount(1, host.Pve.Vms.Values.Where(v => v.Mac == "bc:24:11:aa:00:01"));
    }

    [TestMethod]
    public async Task PoolAgotado_EsInvalidConfig_AntesDeClonar()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(500, "ajena", mac: "bc:24:11:aa:00:01");

        var (status, body) = await host.Send("/v0/provisioning/create", host.Credentials(), ModuleHost.Context(), "k", host.Config(ModuleHost.Pool(1)));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("pool_exhausted", ModuleHost.Reason(body));
        Assert.AreEqual(0, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
    }

    [TestMethod]
    public async Task UnInventarioConUnaVmIlegible_AbortaSinAsignar()
    {
        await using var host = await ModuleHost.Start();
        var odd = host.Pve.AddVm(500, "rara");
        odd.Config["net0"] = "virtio,bridge=vmbr0";

        var (status, body) = await host.Create();

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status, body?.ToJsonString());
        Assert.AreEqual(0, host.Pve.Count("POST /nodes/pve1/qemu/9001/clone"));
    }

    [TestMethod]
    public async Task UnaFallaDelIpset_DejaLaVmApagadaYConOnbootEn0()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.FailIpSetEntry = true;

        var (status, body) = await host.Create();

        Assert.AreNotEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var vm = host.Pve.Vm(7001)!;
        Assert.AreEqual("stopped", vm.Status);
        Assert.AreEqual("0", vm.Config["onboot"]);
        Assert.IsFalse(vm.Firewall.ContainsKey("ipfilter"));
        Assert.AreEqual(0, host.Pve.Count("POST /nodes/pve1/qemu/7001/status/start"));
    }

    [TestMethod]
    public async Task UnClonQueFalla_EsRejected_YNoQuedaVm()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.FailNextTask = "qmclone";

        var (status, body) = await host.Create();

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, ModuleHost.Code(body));
        Assert.AreEqual("task_failed", ModuleHost.Reason(body));
        Assert.IsNull(host.Pve.Vm(7001));
    }

    [TestMethod]
    public async Task SinContrasena_EsInvalidInput_SinLlamarAProxmox()
    {
        await using var host = await ModuleHost.Start();

        var (_, body) = await host.Send("/v0/provisioning/create", [], ModuleHost.Context(), "k");

        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task UnaPoliticaIncumplida_EsInvalidInputConElCampo()
    {
        await using var host = await ModuleHost.Start();

        var (_, body) = await host.Create(p: ModuleHost.Params(9002, disk: 40, cores: 1, memory: 2048));

        var fields = body!["error"]!["details"]!["fields"]!;
        Assert.AreEqual(2, fields["cores"]!["min"]!.GetValue<int>());
        Assert.AreEqual(3072, fields["memory_mb"]!["min"]!.GetValue<int>());
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task UnNodoInvalidoEnLosParametros_EsInvalidInput_SinArmarLaRuta()
    {
        await using var host = await ModuleHost.Start();
        var p = ModuleHost.Params();
        p["node"] = "../pve1";

        var (_, body) = await host.Create(p: p);

        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        Assert.IsEmpty(host.Pve.Calls);
    }
}
