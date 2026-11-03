using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>Cambio de plan (PTR-10) y estado observado con la comparación del paquete (PTR-11).</summary>
[TestClass]
public sealed class ChangePlanTests
{
    [TestMethod]
    public async Task CambiarRamCpuYDisco_AplicaLosLimites_YRepetirNoEscribe()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["memory_mb"] = 4096;
        plan["cpu_percent"] = 200;
        plan["disk_mb"] = 20480;

        var (status, body) = await host.Call("change_plan", Samples.Context(plan: plan), "svc-1042-change_plan-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.AreEqual((4096, 200, 20480), (server.Memory, server.Cpu, server.Disk));

        // No reinstala ni reinicia: solo build y startup, sin tocar la allocation principal.
        Assert.AreEqual(server.Allocation, panel.Last($"PATCH servers/{server.Id}/build").Body!["allocation"]!.GetValue<int>());
        Assert.IsFalse(panel.Last($"PATCH servers/{server.Id}/startup").Body!["skip_scripts"]!.GetValue<bool>());
        var writes = panel.Writes;

        await host.Call("change_plan", Samples.Context(plan: plan), "svc-1042-change_plan-2");
        Assert.AreEqual(writes, panel.Writes);
    }

    [TestMethod]
    public async Task DePaperAFabric_PoneElStartupDelEggNuevo_YConservaLaImagenSiLaOfrece()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        server.Env["MC_VERSION"] = "1.20.4";
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["egg_id"] = 6;
        plan.Remove("variables");

        var (status, body) = await host.Call("change_plan", Samples.Context(plan: plan), "svc-1042-change_plan-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(6, server.Egg);
        Assert.AreEqual(Samples.Fabric.Startup, server.Startup);
        Assert.AreEqual("ghcr.io/pterodactyl/yolks:java_21", server.Image);

        // Sin valor en los parámetros queda el del server, y si no lo tiene, el del egg.
        Assert.AreEqual("1.20.4", server.Env["MC_VERSION"]);
        Assert.AreEqual("latest", server.Env["FABRIC_VERSION"]);
    }

    [TestMethod]
    public async Task EggNuevoSinLaImagenActual_UsaLaPorDefectoDelEgg()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        server.Image = "ghcr.io/pterodactyl/yolks:java_17";
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["egg_id"] = 6;

        await host.Call("change_plan", Samples.Context(plan: plan), "svc-1042-change_plan-1");

        Assert.AreEqual("ghcr.io/pterodactyl/yolks:java_21", server.Image);
    }

    [TestMethod]
    public async Task VersionDeLaOpcion_PisaLaDelServer_PeroLosDatosDelPedidoNo()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        server.Env["MC_VERSION"] = "1.19";
        server.Env["SERVER_JARFILE"] = "el-del-cliente.jar";
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["variables"] = new JsonObject { ["MC_VERSION"] = "1.21.1" };
        plan["order_fields"] = new JsonArray(new JsonObject { ["key"] = "SERVER_JARFILE" });
        var order = new JsonObject { ["variables"] = new JsonObject { ["SERVER_JARFILE"] = "el-del-pedido.jar" } };

        await host.Call("change_plan", Samples.Context(plan: plan, order: order), "svc-1042-change_plan-1");

        Assert.AreEqual("1.21.1", server.Env["MC_VERSION"]);
        Assert.AreEqual("el-del-cliente.jar", server.Env["SERVER_JARFILE"]);
    }

    [TestMethod]
    public async Task UnStartupQueElEggRechaza_NoDejaLosLimitesAMedias()
    {
        // El panel valida las variables contra las reglas del egg en el PATCH startup: va primero.
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        panel.On($"PATCH servers/{server.Id}/startup", (_, _) => Samples.Validation("environment.MC_VERSION", "regex"));
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["memory_mb"] = 4096;

        var (status, body) = await host.Call("change_plan", Samples.Context(plan: plan), "svc-1042-change_plan-1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.InvalidInput, PteroHost.Code(body));
        Assert.AreEqual(1024, server.Memory);
        Assert.AreEqual(0, panel.Count($"PATCH servers/{server.Id}/build"));
    }

    [TestMethod]
    public async Task TextosConEspaciosAlFinal_ComoLosRecortaElPanel_NoQuedanComoDiferencia()
    {
        // Un egg importado de un JSON con espacios al final: el panel guarda todo recortado.
        var panel = FakePanel.Standard();
        var egg = new FakeEgg(9, 1, "java -jar {{SERVER_JARFILE}} ",
            new() { ["Java 21"] = "ghcr.io/pterodactyl/yolks:java_21 " },
            [new("Server Jar File", "SERVER_JARFILE", "server.jar"), new("Minecraft Version", "MC_VERSION", "latest"), new("Loader Version", "LOADER_VERSION", "0.15.11\n")]);
        panel.Eggs.Add(egg);
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["egg_id"] = 9;

        var (status, body) = await host.Call("change_plan", Samples.Context(plan: plan), "svc-1042-change_plan-1");
        var (_, state) = await host.Call("get_status", Samples.Context(plan: plan));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(9, server.Egg);
        Assert.AreEqual("0.15.11", server.Env["LOADER_VERSION"]);
        Assert.IsTrue(state!["result"]!["package_matches"]!.GetValue<bool>(), state.ToJsonString());
    }

    [TestMethod]
    public async Task OtraLocation_SePideElTrasladoAlStaff_YElEstadoLaMarca()
    {
        // La opción Protección DDoS cambia la location, y la Application API no traslada servers.
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        server.Env["MC_VERSION"] = "1.21.1";
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["location_id"] = 5;
        plan["memory_mb"] = 2048;

        var (status, body) = await host.Call("change_plan", Samples.Context(plan: plan), "svc-1042-change_plan-1");
        var (_, state) = await host.Call("get_status", Samples.Context(plan: plan));

        // Lo que se puede aplicar queda aplicado. El traslado lo hace el staff.
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("requires_transfer", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "location_id" }, PteroHost.Strings(body["error"]!["details"]!["differences"]));
        Assert.AreEqual(2048, server.Memory);
        Assert.AreEqual(1, server.Node);
        Assert.IsFalse(state!["result"]!["package_matches"]!.GetValue<bool>());
        CollectionAssert.AreEqual(new[] { "location_id" }, PteroHost.Strings(state["result"]!["differences"]));
    }

    [TestMethod]
    public async Task UnValorDelPedidoQueYaNoSeOfrece_NoTrabaElCambioDePlanNiElEstado()
    {
        // El staff sacó la opción que eligió el cliente al contratar: la RAM que pagó igual se aplica.
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        server.Env["MC_VERSION"] = "1.21.1";
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["memory_mb"] = 4096;
        plan["order_fields"] = new JsonArray(new JsonObject { ["key"] = "SERVER_JARFILE", ["options"] = new JsonArray("a.jar", "b.jar") });
        var order = new JsonObject { ["variables"] = new JsonObject { ["SERVER_JARFILE"] = "opcion-que-ya-no-se-ofrece.jar" } };

        var (status, body) = await host.Call("change_plan", Samples.Context(plan: plan, order: order), "svc-1042-change_plan-1");
        var (_, state) = await host.Call("get_status", Samples.Context(plan: plan, order: order));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(4096, server.Memory);
        Assert.IsTrue(state!["result"]!["package_matches"]!.GetValue<bool>(), state.ToJsonString());
    }

    [TestMethod]
    [DataRow("6", true)]
    [DataRow("no-es-un-id", false)]
    public async Task ElEggDelPedido_ValeAunqueYaNoSeOfrezca_YSiNoSirveEsUnaDiferencia(string egg, bool matches)
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Fabric);
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan.Remove("variables");
        plan["order_fields"] = new JsonArray(new JsonObject { ["key"] = "egg_id", ["options"] = new JsonArray("5") });
        var order = new JsonObject { ["variables"] = new JsonObject { ["egg_id"] = egg } };

        var (_, body) = await host.Call("get_status", Samples.Context(plan: plan, order: order));

        var result = body!["result"]!;
        Assert.AreEqual(matches, result["package_matches"]!.GetValue<bool>(), body.ToJsonString());
        if (!matches)
        {
            CollectionAssert.AreEqual(new[] { "egg_id" }, PteroHost.Strings(result["differences"]));
        }

        Assert.AreEqual(6, server.Egg);
    }

    [TestMethod]
    public async Task AltaConDatabasesVacio_NoDaDiferenciaDespues()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        await host.Call("create", Samples.Context(), "svc-1042-create-1");
        host.Panel.Server(Samples.ServiceId).Status = null;
        var writes = host.Panel.Writes;

        var (_, status) = await host.Call("get_status", Samples.Context());
        await host.Call("change_plan", Samples.Context(), "svc-1042-change_plan-1");

        Assert.IsTrue(status!["result"]!["package_matches"]!.GetValue<bool>(), status.ToJsonString());
        Assert.AreEqual(writes, host.Panel.Writes);
    }

    [TestMethod]
    public async Task DatabasesNullDeUnAltaVieja_ElCambioDePlanLoDejaEnCero()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        server.Databases = null;
        server.Env["MC_VERSION"] = "1.21.1";
        await using var host = await PteroHost.Start(panel);

        var (_, before) = await host.Call("get_status", Samples.Context());
        await host.Call("change_plan", Samples.Context(), "svc-1042-change_plan-1");

        CollectionAssert.AreEqual(new[] { "databases" }, PteroHost.Strings(before!["result"]!["differences"]));
        Assert.AreEqual(0, server.Databases);
    }

    [TestMethod]
    public async Task CambioDePlanSinServer_EsNotFound()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Call("change_plan", Samples.Context(), "svc-1042-change_plan-1");

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, PteroHost.Code(body));
    }

    [TestMethod]
    public async Task StartupSinConfirmar_DecideLaRelectura()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        panel.CutAfter($"PATCH servers/{server.Id}/startup");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("change_plan", Samples.Context(), "svc-1042-change_plan-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("1.21.1", server.Env["MC_VERSION"]);
    }

    [TestMethod]
    public async Task EstadoObservado_DiferenciasSoloConNombres()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper, status: "suspended");
        server.Env["MC_VERSION"] = "valor-secreto-del-server";
        server.Memory = 2048;
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("suspended", result["observed_state"]!.GetValue<string>());
        Assert.IsFalse(result["package_matches"]!.GetValue<bool>());
        CollectionAssert.AreEquivalent(new[] { "memory_mb", "variables.MC_VERSION" }, PteroHost.Strings(result["differences"]));
        Assert.DoesNotContain("valor-secreto-del-server", body.ToJsonString());
        Assert.HasCount(6, result["external_ids"]!.AsObject());
        Assert.AreEqual(0, panel.Writes);
    }

    [TestMethod]
    public async Task EstadoDeUnServicioImportado_SinParametros_AdoptaLosIds()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper, node: 2, port: 2320);
        await using var host = await PteroHost.Start(panel);
        var context = Samples.Context(plan: new JsonObject(), externalIds: new JsonObject { ["address"] = "10.20.30.5:2320" });

        var (status, body) = await host.Call("get_status", context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.AreEqual(server.Id.ToString(), body["result"]!["external_ids"]!["server_id"]!.GetValue<string>());
        Assert.AreEqual("10.20.30.5:2320", body["result"]!["external_ids"]!["address"]!.GetValue<string>());
        Assert.IsNull(body["result"]!["package_matches"]);
    }

    [TestMethod]
    public async Task EstadoSinServer_EsNotFound_YNoUnError()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Call("get_status", Samples.Context());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("not_found", body!["result"]!["observed_state"]!.GetValue<string>());
    }
}
