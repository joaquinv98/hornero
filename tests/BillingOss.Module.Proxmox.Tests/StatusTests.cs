using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>get_status, la vista del portal y la consola por sso.</summary>
[TestClass]
public sealed class StatusTests
{
    [TestMethod]
    public async Task Estado_ActivoApagadoPorElCliente_SigueActivo()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "stop" }, context, "a1");

        var (status, body) = await host.Send("/v0/provisioning/get_status", [], context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var result = ModuleHost.Result(body);
        Assert.AreEqual("active", result["observed_state"]!.GetValue<string>());
        Assert.AreEqual("stopped", result["details"]!["power"]!.GetValue<string>());
        Assert.IsTrue(result["details"]!["onboot"]!.GetValue<bool>());
        Assert.IsEmpty(result["details"]!["drift"]!.AsArray());
        Assert.AreEqual("10.20.0.11", result["external_ids"]!["private_ip"]!.GetValue<string>());
        Assert.AreEqual("vmbr2", result["external_ids"]!["bridge"]!.GetValue<string>());
        Assert.AreEqual("1360", result["external_ids"]!["mtu"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Estado_SuspendidoYPrendidoSinOnboot()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        await host.Send("/v0/provisioning/suspend", [], context, "s1");
        context["service"]!["status"] = "suspended";

        var (_, suspended) = await host.Send("/v0/provisioning/get_status", [], context);
        Assert.AreEqual("suspended", ModuleHost.Result(suspended)["observed_state"]!.GetValue<string>());

        // onboot en 0 y prendida: no se adivina, lo mira el staff.
        host.Pve.Vm(7001)!.Status = "running";
        var (_, odd) = await host.Send("/v0/provisioning/get_status", [], context);
        Assert.AreEqual("unknown", ModuleHost.Result(odd)["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Estado_VmBorradaAMano_EsNotFound_YMigrada_ActualizaElNodo()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);

        host.Pve.Vm(7001)!.Node = "pve2";
        var (_, migrated) = await host.Send("/v0/provisioning/get_status", [], context);
        Assert.AreEqual("pve2", ModuleHost.Result(migrated)["external_ids"]!["node"]!.GetValue<string>());

        host.Pve.Vms.Remove(7001);
        var (_, gone) = await host.Send("/v0/provisioning/get_status", [], context);
        Assert.AreEqual("not_found", ModuleHost.Result(gone)["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Estado_SinVmAudit_NoDiceNotFound()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.VmAudit = false;

        var (_, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(ids: new JsonObject { ["vmid"] = "7001" }));

        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task Estado_VmDeOtroDueno_EsUnknownPorOwnership()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(150, "vm9999");

        var (_, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(7400, new JsonObject { ["vmid"] = "150" }));

        Assert.AreEqual("unknown", ModuleHost.Result(body)["observed_state"]!.GetValue<string>());
        Assert.AreEqual("ownership", ModuleHost.Result(body)["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Estado_ImportadoSinMarca_SeReconocePorElNombre_YCompletaLaRed()
    {
        await using var host = await ModuleHost.Start();
        var legacy = host.Pve.AddVm(121, "vm7301", mac: "bc:24:11:aa:00:02");
        legacy.Config["net0"] = "virtio=BC:24:11:AA:00:02,bridge=vmbr2,firewall=1,mtu=1360,rate=1.25";
        legacy.IpSets["ipfilter-net0"] = ["10.20.0.12"];
        legacy.Firewall["enable"] = "1";
        legacy.Firewall["ipfilter"] = "1";

        var (_, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(7301, new JsonObject { ["vmid"] = "121", ["public_ip"] = "203.0.113.12" }));

        var result = ModuleHost.Result(body);
        Assert.AreEqual("active", result["observed_state"]!.GetValue<string>());
        Assert.AreEqual("bc:24:11:aa:00:02", result["external_ids"]!["mac"]!.GetValue<string>());
        Assert.AreEqual("10.20.0.12", result["external_ids"]!["private_ip"]!.GetValue<string>());
        Assert.AreEqual("pve1", result["external_ids"]!["node"]!.GetValue<string>());

        // Le falta el grupo de aislamiento: queda como desvío para el informe, sin tocar la VM.
        CollectionAssert.Contains(result["details"]!["drift"]!.AsArray().Select(d => d!.GetValue<string>()).ToList(), "isolation_missing");
        Assert.IsFalse(host.Pve.Calls.Any(c => !c.StartsWith("GET ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Vista_ParaElClienteYParaElStaff()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);

        var (status, client) = await host.Send("/v0/provisioning/view", [], context);
        var (_, staff) = await host.Send("/v0/provisioning/view", new JsonObject { ["audience"] = "staff" }, context);

        Assert.AreEqual(HttpStatusCode.OK, status, client?.ToJsonString());
        var blocks = ModuleHost.Result(client)["blocks"]!.AsArray();
        Assert.AreEqual("ok", blocks[0]!["level"]!.GetValue<string>());
        Assert.AreEqual("Encendido", blocks[0]!["text"]!.GetValue<string>());
        var fields = Labels(blocks);
        Assert.AreEqual("203.0.113.11", fields["IP pública"]);
        Assert.AreEqual("client42", fields["Usuario"]);
        Assert.AreEqual("debian-13", fields["Sistema operativo"]);
        Assert.AreEqual("512 / 1024 MB", fields["RAM"]);
        Assert.IsFalse(fields.ContainsKey("VMID"));
        CollectionAssert.AreEqual(
            new[] { "start", "stop", "reboot", "reinstall" },
            blocks.Single(b => b!["type"]!.GetValue<string>() == "actions")!["items"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray());
        Assert.DoesNotContain("<", client!.ToJsonString());

        var staffFields = Labels(ModuleHost.Result(staff)["blocks"]!.AsArray());
        Assert.AreEqual("7001", staffFields["VMID"]);
        Assert.AreEqual("pve1", staffFields["Nodo"]);
        Assert.AreEqual("presente", staffFields["Marca"]);
    }

    [TestMethod]
    public async Task Vista_DeUnServicioSuspendido_NoOfreceAcciones()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        context["service"]!["status"] = "suspended";

        var (status, body) = await host.Send("/v0/provisioning/view", [], context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsFalse(ModuleHost.Result(body)["blocks"]!.AsArray().Any(b => b!["type"]!.GetValue<string>() == "actions"));
    }

    [TestMethod]
    public async Task Estado_SinFirewallEnLaNicNiIpfilterConLaIpPrivadaDelPool_SonDesvios()
    {
        // Un importado anterior al anti-spoofing: net0 sin firewall=1 y sin ipset, con la IP privada en el pool.
        await using var host = await ModuleHost.Start();
        var legacy = host.Pve.AddVm(121, "vm7301", mac: "bc:24:11:aa:00:01");
        legacy.Config["net0"] = "virtio=BC:24:11:AA:00:01,bridge=vmbr2";
        legacy.Firewall["enable"] = "1";

        var (_, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(7301, new JsonObject { ["vmid"] = "121" }));

        var drift = ModuleHost.Result(body)["details"]!["drift"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
        CollectionAssert.Contains(drift, "nic_firewall_off");
        CollectionAssert.Contains(drift, "ipfilter_missing");
    }

    [TestMethod]
    public async Task Vista_ConLaVmApagada_MuestraElAviso()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "stop" }, context, "a1");

        var (_, body) = await host.Send("/v0/provisioning/view", [], context);

        var status = ModuleHost.Result(body)["blocks"]!.AsArray()[0]!;
        Assert.AreEqual("warning", status["level"]!.GetValue<string>());
        Assert.AreEqual("Apagado", status["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Vista_ConProxmoxCaido_CargaIgualConUnAviso()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Hook = async (ctx, route) =>
        {
            await FakePve.Reply(ctx, 503, "Service Unavailable", null);
            return true;
        };

        var (status, body) = await host.Send("/v0/provisioning/view", [], context);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var blocks = ModuleHost.Result(body)["blocks"]!.AsArray();
        Assert.AreEqual("error", blocks[0]!["level"]!.GetValue<string>());
        Assert.DoesNotContain("503", blocks.ToJsonString());
    }

    [TestMethod]
    public async Task Consola_DevuelveUnaUrlConUnGrantQueAceptaElGateway()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);

        var (status, body) = await host.Send("/v0/provisioning/sso", [], context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var url = ModuleHost.Result(body)["url"]!.GetValue<string>();
        StringAssert.StartsWith(url, host.Pve.Origin.GetLeftPart(UriPartial.Authority) + "/#");
        var grant = Grants.Verify(url.Split('#')[1], host.Pve.ConsoleSecret, "proxmox-console");
        Assert.IsNotNull(grant);
        Assert.AreEqual(7001, grant["sub"]!.GetValue<long>());
        Assert.AreEqual(7001, grant["vmid"]!.GetValue<int>());
        Assert.AreEqual("pve1", grant["node"]!.GetValue<string>());
        Assert.AreEqual("6", grant["server"]!.GetValue<string>());
        Assert.AreEqual(60, grant["exp"]!.GetValue<long>() - grant["iat"]!.GetValue<long>());
        Assert.HasCount(48, grant["jti"]!.GetValue<string>());

        // Con otro secreto o para la revocación, el mismo grant no vale.
        Assert.IsNull(Grants.Verify(url.Split('#')[1], "otro-secreto-de-al-menos-32-bytes-xx", "proxmox-console"));
        Assert.IsNull(Grants.Verify(url.Split('#')[1], host.Pve.ConsoleSecret, "proxmox-console-revoke"));
    }

    /// <summary>
    /// El grant contra el verifyGrant del gateway desplegado, en Node. Corre a mano con CONSOLE_GATEWAY_DIR apuntando a
    /// la carpeta console-gateway, y si falta queda inconclusa.
    /// </summary>
    [TestMethod]
    [TestCategory("GatewayReal")]
    public async Task Consola_ElGrantLoAceptaElVerifyGrantDelGateway()
    {
        var dir = Environment.GetEnvironmentVariable("CONSOLE_GATEWAY_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "server.mjs")))
        {
            Assert.Inconclusive("Falta CONSOLE_GATEWAY_DIR con el server.mjs del gateway de consola.");
        }

        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        var (_, body) = await host.Send("/v0/provisioning/sso", [], context);
        var grant = ModuleHost.Result(body)["url"]!.GetValue<string>().Split('#')[1];

        var module = new Uri(Path.Combine(Path.GetFullPath(dir), "server.mjs")).AbsoluteUri;
        var script = $"import {{ verifyGrant }} from '{module}'; const d = verifyGrant(process.env.GRANT, process.env.SECRET, Math.floor(Date.now() / 1000), 0, 'proxmox-console'); console.log(d.sub, d.vmid, d.node);";
        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.Environment["GRANT"] = grant;
        start.Environment["SECRET"] = host.Pve.ConsoleSecret;
        using var node = System.Diagnostics.Process.Start(start)!;
        var output = await node.StandardOutput.ReadToEndAsync();
        var error = await node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync();

        Assert.AreEqual(0, node.ExitCode, error);
        Assert.AreEqual("7001 7001 pve1", output.Trim());
    }

    [TestMethod]
    public async Task Consola_ConLaVmApagadaEsConflict_YSinConsolaOSuspendidoNoHayUrl()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "stop" }, context, "a1");

        var (status, stopped) = await host.Send("/v0/provisioning/sso", [], context);
        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual("vm_stopped", ModuleHost.Reason(stopped));

        var disabled = (JsonObject)context.DeepClone();
        disabled["service"]!["params"]!["console_enabled"] = false;
        var (_, off) = await host.Send("/v0/provisioning/sso", [], disabled);
        Assert.AreEqual("console_disabled", ModuleHost.Reason(off));

        var suspended = (JsonObject)context.DeepClone();
        suspended["service"]!["status"] = "suspended";
        var (_, notActive) = await host.Send("/v0/provisioning/sso", [], suspended);
        Assert.AreEqual("service_not_active", ModuleHost.Reason(notActive));
    }

    private static Dictionary<string, string> Labels(JsonArray blocks) =>
        blocks.Single(b => b!["type"]!.GetValue<string>() == "fields")!["items"]!.AsArray()
            .ToDictionary(f => f!["label"]!.GetValue<string>(), f => f!["value"]!.GetValue<string>());
}
