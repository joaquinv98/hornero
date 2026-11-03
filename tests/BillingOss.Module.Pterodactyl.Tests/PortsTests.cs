using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>Puertos adicionales en el alta (PTR-07): un solo camino para DayZ y los multipuerto genéricos.</summary>
[TestClass]
public sealed class PortsTests
{
    private const string Key = "svc-1042-create-1";

    [TestMethod]
    public async Task Contiguos_EligeUnBloque_YLasVariablesApuntanASusPuertos()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), Key);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var sent = host.Panel.Last("POST servers").Body!;
        Assert.IsNull(sent["deploy"]);
        var server = host.Panel.Server(Samples.ServiceId);
        var ports = Owned(host.Panel, server);
        var primary = host.Panel.Allocations.Single(a => a.Id == server.Allocation).Port;
        CollectionAssert.AreEqual(new[] { primary, primary + 1, primary + 2, primary + 3 }, ports);
        Assert.AreEqual((primary + 1).ToString(), server.Env["QUERY_PORT"]);
        Assert.AreEqual((primary + 3).ToString(), server.Env["RCON_PORT"]);
        Assert.AreEqual("cambiar", server.Env["RCON_PASSWORD"]);

        // El tope de allocations cubre los cuatro del alta, como hoy en DayZ.
        Assert.AreEqual(4, sent["feature_limits"]!["allocations"]!.GetValue<int>());
        Assert.AreEqual(0, host.Panel.Count("PATCH servers/" + server.Id + "/build"));
    }

    [TestMethod]
    public async Task NoContiguos_UsanLosLibresDeLaMismaIp_EnOrden()
    {
        var panel = FakePanel.Standard();

        // Huecos en el nodo: ningún bloque contiguo de 3.
        foreach (var allocation in panel.Allocations.Where(a => a.Node == 2 && a.Port % 2 == 1))
        {
            allocation.Server = 1;
        }

        await using var host = await PteroHost.Start(panel);
        var plan = Samples.DayZPlan(contiguous: false);
        plan["extra_ports"] = new JsonObject { ["count"] = 2, ["variables"] = new JsonObject { ["QUERY_PORT"] = 1, ["RCON_PORT"] = 2 } };

        var (status, body) = await host.Call("create", Samples.Context(plan: plan), Key);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var server = panel.Server(Samples.ServiceId);
        var ports = Owned(panel, server);
        Assert.HasCount(3, ports);
        Assert.AreEqual(ports[1].ToString(), server.Env["QUERY_PORT"]);
        Assert.AreEqual(ports[2].ToString(), server.Env["RCON_PORT"]);
        Assert.AreEqual(ports[0], panel.Allocations.Single(a => a.Id == server.Allocation).Port);
    }

    [TestMethod]
    public async Task SinPuertosLibres_EsRejected_YNoCreaNada()
    {
        var panel = FakePanel.Standard();
        foreach (var allocation in panel.Allocations.Where(a => a.Node == 2 && a.Port % 3 == 0))
        {
            allocation.Server = 1;
        }

        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PteroHost.Code(body));
        Assert.AreEqual("no_free_ports", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(0, panel.Count("POST servers"));
        Assert.IsEmpty(panel.Servers);
    }

    [TestMethod]
    public async Task PuertoTomadoEntreLaLecturaYElAlta_SeVuelveAElegir()
    {
        var panel = FakePanel.Standard();
        panel.On("POST servers", (_, _) => Samples.Validation("allocation.additional.0", "exists"), times: 1);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), Key);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(2, panel.Count("POST servers"));
        Assert.HasCount(4, Owned(panel, panel.Server(Samples.ServiceId)));
    }

    [TestMethod]
    public async Task AltaRepetida_CompletaLosPuertosQueFaltan()
    {
        // Un server que quedó con un solo puerto, como el camino genérico de hoy cuando fallaba.
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.DayZ, node: 2, port: 2310);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(new[] { 2310, 2311, 2312, 2313 }, Owned(panel, server));
        Assert.AreEqual("2311", server.Env["QUERY_PORT"]);
        Assert.AreEqual("2313", server.Env["RCON_PORT"]);
        Assert.AreEqual(4, server.AllocationLimit);
        Assert.AreEqual(0, panel.Count("POST servers"));

        // Repetir ya no escribe nada.
        await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), "svc-1042-create-3");
        Assert.AreEqual(1, panel.Count($"PATCH servers/{server.Id}/build"));
        Assert.AreEqual(1, panel.Count($"PATCH servers/{server.Id}/startup"));
    }

    [TestMethod]
    public async Task AltaRepetida_ConElPuertoContiguoOcupado_EsRejected()
    {
        var panel = FakePanel.Standard();
        panel.AddServer(Samples.ServiceId, Samples.DayZ, node: 2, port: 2310);
        panel.Allocations.Single(a => a.Node == 2 && a.Port == 2312).Server = 1;
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("no_free_ports", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(0, panel.Writes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ServiciosDistintos_EligenElMismoBloqueOBloquesSinPuertosEnComun(bool contiguous)
    {
        // Antes los bloques se solapaban a medias: 1042 elegía 2320 a 2323 y 1077, 2321 a 2324.
        var free = Enumerable.Range(0, 41).Select(i => new Alloc(i + 1, "10.20.30.5", null, 2300 + i, false)).ToList();
        var plan = new PortsPlan(3, contiguous, new Dictionary<string, int> { ["QUERY_PORT"] = 1 });

        var first = Ports.Pick(free, plan, 1042)!;
        Assert.HasCount(4, first);
        Assert.AreEqual(3, first[^1].Port - first[0].Port);
        var ports = first.Select(a => a.Port).ToArray();
        foreach (var seed in Enumerable.Range(1000, 300))
        {
            var other = Ports.Pick(free, plan, seed)!.Select(a => a.Port).ToArray();
            Assert.IsTrue(other.SequenceEqual(ports) || !other.Intersect(ports).Any(), $"{seed}: {string.Join(",", other)} contra {string.Join(",", ports)}");
        }

        // La elección es estable, y un servicio vecino elige otro bloque.
        CollectionAssert.AreEqual(first.ToArray(), Ports.Pick(free, plan, 1042)!.ToArray());
        Assert.IsFalse(Ports.Pick(free, plan, 1043)!.Select(a => a.Port).Intersect(ports).Any());
    }

    [TestMethod]
    public async Task SinNodoConLugarEnLaLocation_EsNoDeployableNode()
    {
        // nodes/deployable contesta 400 NoViableNodeException cuando ningún nodo de la location tiene lugar.
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var plan = Samples.DayZPlan();
        plan["location_id"] = 9;

        var (status, body) = await host.Call("create", Samples.Context(plan: plan), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("no_deployable_node", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(0, host.Panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task PuertosTomadosEnTresIntentos_EsConflictReintentable()
    {
        var panel = FakePanel.Standard();
        panel.On("POST servers", (_, _) => Samples.Validation("allocation.additional.0", "exists"));
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), Key);

        Assert.AreEqual(HttpStatusCode.Conflict, status, body?.ToJsonString());
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual("ports_taken", body["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(3, panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task PuertoTomadoAntesDelPatchBuild_LaVariableNoApuntaAUnPuertoAjeno()
    {
        // El panel saltea sin error una allocation que otro server tomó entre la lectura de libres y el PATCH build.
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.DayZ, node: 2, port: 2310);
        var taken = panel.Allocations.Single(a => a.Node == 2 && a.Port == 2311);
        panel.On($"PATCH servers/{server.Id}/build", (_, next) =>
        {
            taken.Server = 999;
            return next();
        }, times: 1);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan()), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.Conflict, status, body?.ToJsonString());
        Assert.AreEqual("ports_taken", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(string.Empty, server.Env["QUERY_PORT"]);
        Assert.AreEqual(0, panel.Count($"PATCH servers/{server.Id}/startup"));
    }

    [TestMethod]
    public async Task PuertoTomadoAntesDelPatchBuild_ElReintentoEligeOtro()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.DayZ, node: 2, port: 2310);
        panel.On($"PATCH servers/{server.Id}/build", (s, next) =>
        {
            var first = s.Body!["add_allocations"]![0]!.GetValue<int>();
            panel.Allocations.Single(a => a.Id == first).Server = 999;
            return next();
        }, times: 1);
        await using var host = await PteroHost.Start(panel);

        var (conflict, _) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan(contiguous: false)), "svc-1042-create-2");
        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan(contiguous: false)), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.Conflict, conflict);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var owned = Owned(panel, server);
        Assert.HasCount(4, owned);
        CollectionAssert.Contains(owned, int.Parse(server.Env["QUERY_PORT"]));
        CollectionAssert.Contains(owned, int.Parse(server.Env["RCON_PORT"]));
    }

    [TestMethod]
    public async Task PuertoQueOtraAltaSeQuedoDespuesDelPost_SeReemplazaAntesDeTerminar()
    {
        // El panel valida que las allocations estén libres antes de su transacción: otra alta simultánea puede
        // quedarse con una después. El alta relee el server y la reemplaza.
        var panel = FakePanel.Standard();
        FakeAllocation? lost = null;
        panel.On("POST servers", (_, next) =>
        {
            var response = next();
            var created = panel.Server(Samples.ServiceId);
            lost = panel.Allocations.Where(a => a.Server == created.Id && a.Id != created.Allocation).OrderBy(a => a.Port).First();
            lost.Server = 999;
            return response;
        }, times: 1);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(plan: Samples.DayZPlan(contiguous: false)), Key);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var server = panel.Server(Samples.ServiceId);
        var owned = Owned(panel, server);
        Assert.HasCount(4, owned);
        CollectionAssert.DoesNotContain(owned, lost!.Port);
        CollectionAssert.Contains(owned, int.Parse(server.Env["QUERY_PORT"]));
        CollectionAssert.Contains(owned, int.Parse(server.Env["RCON_PORT"]));
    }

    /// <summary>Los puertos del server, ordenados.</summary>
    private static int[] Owned(FakePanel panel, FakeServer server) =>
        panel.Allocations.Where(a => a.Server == server.Id).Select(a => a.Port).Order().ToArray();
}
