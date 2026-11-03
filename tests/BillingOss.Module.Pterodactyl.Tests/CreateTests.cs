using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>Alta (PTR-06) y su convergencia.</summary>
[TestClass]
public sealed class CreateTests
{
    private const string Key = "svc-1042-create-1";

    private const string Uuid = "0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e11";

    [TestMethod]
    public async Task Alta_TerminaApenasExisteElServer_ConLosSeisIds_YLaInstalacionSeVeEnLaVista()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        // Sin operación larga: el panel sigue instalando, pero el alta ya terminó (PTR-06).
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        var server = host.Panel.Server(Samples.ServiceId);
        Assert.AreEqual("installing", server.Status);
        var result = body["result"]!;
        Assert.AreEqual("active", result["observed_state"]!.GetValue<string>());
        var ids = result["external_ids"]!.AsObject();
        CollectionAssert.AreEquivalent(
            new[] { "server_id", "identifier", "user_id", "node_id", "allocation_id", "address" },
            ids.Select(i => i.Key).ToArray());
        Assert.AreEqual(server.Id.ToString(), ids["server_id"]!.GetValue<string>());
        Assert.AreEqual(server.Identifier, ids["identifier"]!.GetValue<string>());

        // La IP del nodo es 0.0.0.0: la dirección es el alias con el puerto.
        Assert.AreEqual("mc.example.test:25565", ids["address"]!.GetValue<string>());

        var (_, view) = await host.Call("view", Samples.Context());
        Assert.AreEqual("Instalando", view!["result"]!["blocks"]![0]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ServerBorradoEnElPanelMientrasInstalaba_OtraGeneracionLoVuelveACrear()
    {
        // Antes el alta quedaba en curso con create-{id} guardado para la clave: si el staff borraba el server, la
        // consulta daba not_found, el núcleo repetía con la misma clave y la SDK devolvía la misma operación muerta.
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var (_, first) = await host.Call("create", Samples.Context(), Key);
        var deleted = host.Panel.Server(Samples.ServiceId);
        host.Panel.Servers.Remove(deleted);
        foreach (var allocation in host.Panel.Allocations.Where(a => a.Server == deleted.Id))
        {
            allocation.Server = null;
        }

        var (_, again) = await host.Call("create", Samples.Context(), "svc-1042-create-2");

        Assert.AreEqual("completed", first!["status"]!.GetValue<string>());
        Assert.AreEqual("completed", again!["status"]!.GetValue<string>(), again.ToJsonString());
        Assert.AreNotEqual(deleted.Id, host.Panel.Server(Samples.ServiceId).Id);
        Assert.AreEqual(2, host.Panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task SinLaOperacionLarga_LaConsultaNoEstaSoportada()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Send("/v0/operations/create-100", Samples.Context(), []);

        Assert.AreEqual(HttpStatusCode.NotImplemented, status);
        Assert.AreEqual(ErrorCodes.NotSupported, PteroHost.Code(body));
    }

    [TestMethod]
    public async Task ClaveSinPermisoDeAllocations_ElAltaNoTerminaSinDireccion()
    {
        // El incidente del 2026-06-30: el panel devolvía null_resource en vez de las allocations, sin error.
        var panel = FakePanel.Standard();
        panel.Denied.Add("allocations");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.InvalidConfig, PteroHost.Code(body));
        CollectionAssert.AreEqual(new[] { "allocations" }, PteroHost.Strings(body!["error"]!["details"]!["missing"]));
    }

    [TestMethod]
    public async Task ConElUuidDelServicio_LaMarcaEsElUuid_YUnaRepeticionSinRegistroNoCreaOtro()
    {
        // Otra clave, como después de perder el registro de idempotencia: la consulta previa encuentra el server.
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var context = Samples.Context();
        context["service"]!["uuid"] = Uuid;

        var (status, body) = await host.Call("create", context, Key);
        var (_, again) = await host.Call("create", context, "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Uuid, host.Panel.Last("POST servers").Body!["external_id"]!.GetValue<string>());
        Assert.AreEqual("completed", again!["status"]!.GetValue<string>(), again.ToJsonString());
        Assert.AreEqual(1, host.Panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task ConElUuid_UnServerImportadoConElIdNumerico_SeEncuentra_YNoSeCreaOtro()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        host.Panel.AddServer(Samples.ServiceId, Samples.Paper);
        var context = Samples.Context();
        context["service"]!["uuid"] = Uuid;

        var (_, status) = await host.Call("get_status", context);
        var (_, created) = await host.Call("create", context, Key);

        Assert.AreNotEqual("not_found", status!["result"]!["observed_state"]!.GetValue<string>(), status.ToJsonString());
        Assert.AreEqual("completed", created!["status"]!.GetValue<string>(), created.ToJsonString());
        Assert.AreEqual(0, host.Panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task UnUuidInvalido_EsInvalidInput_SinLlamarAlPanel()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var context = Samples.Context();
        context["service"]!["uuid"] = "no-es-un-uuid";

        var (status, body) = await host.Call("create", context, Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(0, host.Panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task Alta_MandaElPaqueteDelProducto_YLaMarcaExterna()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        await host.Call("create", Samples.Context(), Key);

        var body = host.Panel.Last("POST servers").Body!;
        Assert.AreEqual("1042", body["external_id"]!.GetValue<string>());
        Assert.AreEqual("Servicio 1042", body["name"]!.GetValue<string>());
        Assert.AreEqual(5, body["egg"]!.GetValue<int>());
        Assert.AreEqual("ghcr.io/pterodactyl/yolks:java_21", body["docker_image"]!.GetValue<string>());
        Assert.AreEqual(Samples.Paper.Startup, body["startup"]!.GetValue<string>());
        Assert.AreEqual(1024, body["limits"]!["memory"]!.GetValue<int>());
        Assert.AreEqual(0, body["limits"]!["swap"]!.GetValue<int>());
        Assert.AreEqual(5120, body["limits"]!["disk"]!.GetValue<int>());
        Assert.AreEqual(500, body["limits"]!["io"]!.GetValue<int>());
        Assert.AreEqual(100, body["limits"]!["cpu"]!.GetValue<int>());
        Assert.IsTrue(body["oom_disabled"]!.GetValue<bool>());
        Assert.IsTrue(body["start_on_completion"]!.GetValue<bool>());

        // Los opcionales vacíos van como 0, nunca null: databases null en el panel es sin límite.
        Assert.AreEqual(0, body["feature_limits"]!["databases"]!.GetValue<int>());
        Assert.AreEqual(0, body["feature_limits"]!["allocations"]!.GetValue<int>());
        Assert.AreEqual(0, body["feature_limits"]!["backups"]!.GetValue<int>());
        CollectionAssert.AreEqual(new[] { 3 }, body["deploy"]!["locations"]!.AsArray().Select(l => l!.GetValue<int>()).ToArray());
        Assert.IsFalse(body["deploy"]!["dedicated_ip"]!.GetValue<bool>());

        // MC_VERSION sale de la variable por nombre visible y el resto, del egg.
        var environment = body["environment"]!;
        Assert.AreEqual("1.21.1", environment["MC_VERSION"]!.GetValue<string>());
        Assert.AreEqual("server.jar", environment["SERVER_JARFILE"]!.GetValue<string>());
        Assert.AreEqual("latest", environment["BUILD_NUMBER"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CuentaNueva_CreaElUsuarioSinContrasena_ConNombreYApellido()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        await host.Call("create", Samples.Context(), Key);

        var user = host.Panel.Last("POST users").Body!;
        Assert.AreEqual("77", user["external_id"]!.GetValue<string>());
        Assert.AreEqual("cliente77@example.com", user["email"]!.GetValue<string>());
        Assert.AreEqual("Ana", user["first_name"]!.GetValue<string>());
        Assert.AreEqual("María Pérez", user["last_name"]!.GetValue<string>());
        Assert.AreEqual(8, user["username"]!.GetValue<string>().Length);

        // Sin contraseña: el panel le manda al cliente el correo para definirla.
        Assert.IsNull(user["password"]);
        Assert.AreEqual(host.Panel.Users.Single().Id, host.Panel.Server(Samples.ServiceId).User);
    }

    [TestMethod]
    public async Task NombreDeUnaPalabra_ElApellidoLaRepite()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        await host.Call("create", Samples.Context(account: Samples.Account(name: "Cher")), Key);

        var user = host.Panel.Last("POST users").Body!;
        Assert.AreEqual("Cher", user["first_name"]!.GetValue<string>());
        Assert.AreEqual("Cher", user["last_name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CuentaConUsuario_LoReutiliza_PorExternalIdOPorCorreoExacto()
    {
        var panel = FakePanel.Standard();
        var byExternal = panel.AddUser("77", "otro@example.com");

        // Un correo parecido que el filtro parcial también trae, y el exacto en otra mayúscula.
        panel.AddUser(null, "xcliente88@example.com");
        var byEmail = panel.AddUser(null, "Cliente88@Example.com");
        await using var host = await PteroHost.Start(panel);

        await host.Call("create", Samples.Context(), Key);
        await host.Call("create", Samples.Context(service: 2000, account: Samples.Account(id: 88, email: "cliente88@example.com")), "svc-2000-create-1");

        Assert.AreEqual(0, panel.Count("POST users"));
        Assert.AreEqual(byExternal.Id, panel.Server(Samples.ServiceId).User);
        Assert.AreEqual(byEmail.Id, panel.Server(2000).User);
    }

    [TestMethod]
    public async Task CorreoQueTieneUnUsuarioDeOtraCuenta_NoSeLeDaElServer()
    {
        // La cuenta 55 tenía el correo que hoy usa la 77 y su usuario del panel lo conserva.
        var panel = FakePanel.Standard();
        panel.AddUser("55", "cliente77@example.com");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.Rejected, PteroHost.Code(body));
        Assert.AreEqual("email_of_other_account", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(0, panel.Writes);
        Assert.IsEmpty(panel.Servers);
    }

    [TestMethod]
    public async Task ServerConLaMarcaDelServicioPeroDeOtroCliente_NoSeAdopta()
    {
        // Un ID de servicio que WHMCS ya usó para otro cliente durante el paralelo: el server existe con esa marca.
        var panel = FakePanel.Standard();
        var other = panel.AddUser("55", "otro55@example.com");
        panel.AddServer(Samples.ServiceId, Samples.Paper, user: other.Id);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("external_id_taken", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.DoesNotContain("address", body.ToJsonString());
        Assert.AreEqual(0, panel.Writes);
    }

    [TestMethod]
    public async Task ElServerQueAparecioConLaMismaMarca_EsDeOtroCliente_NoSeAdopta()
    {
        // La búsqueda da 404 y el POST choca con el external_id único de un server de otro cliente.
        var panel = FakePanel.Standard();
        var other = panel.AddUser("55", "otro55@example.com");
        panel.On("POST servers", (_, next) =>
        {
            panel.AddServer(Samples.ServiceId, Samples.Paper, port: 25580, user: other.Id);
            return next();
        }, times: 1);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("external_id_taken", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(other.Id, panel.Server(Samples.ServiceId).User);
    }

    [TestMethod]
    public async Task RepetirConLaMismaClave_NoCreaOtroServer_YDevuelveLoMismo()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var first = await host.Call("create", Samples.Context(), Key);
        var second = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
        Assert.AreEqual(1, host.Panel.Count("POST servers"));
        Assert.AreEqual(1, host.Panel.Servers.Count);
    }

    [TestMethod]
    public async Task OtraGeneracionConElServerYaCreado_ConvergeSinCrear()
    {
        var panel = FakePanel.Standard();
        var existing = panel.AddServer(Samples.ServiceId, Samples.Paper, port: 25570);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual(existing.Id.ToString(), body["result"]!["external_ids"]!["server_id"]!.GetValue<string>());

        // Antes un alta repetida no completaba la dirección: ahora sí.
        Assert.AreEqual("mc.example.test:25570", body["result"]!["external_ids"]!["address"]!.GetValue<string>());
        Assert.AreEqual(0, panel.Writes);
    }

    [TestMethod]
    public async Task OtroRegistroDeIdempotencia_ConvergePorLaMarcaDelPanel()
    {
        // Dos réplicas con registros distintos: la segunda no conoce la clave y encuentra el server por external_id.
        var panel = FakePanel.Standard();
        await using (var replica = await PteroHost.Start(panel))
        {
            await replica.Call("create", Samples.Context(), Key);
        }

        await using var other = await PteroHost.Start(panel);
        panel.Server(Samples.ServiceId).Status = null;
        var (_, body) = await other.Call("create", Samples.Context(), Key);

        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual(1, panel.Servers.Count);
        Assert.AreEqual(1, panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task CorteDespuesDelPost_EsIncierto_YElReintentoConverge()
    {
        var panel = FakePanel.Standard().CutAfter("POST servers");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, PteroHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(1, panel.Servers.Count);

        // El núcleo repite con la misma clave: el error incierto no quedó guardado y el server se encuentra.
        (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual(1, panel.Servers.Count);
        Assert.AreEqual(1, panel.Count("POST servers"));
    }

    [TestMethod]
    public async Task OtraReplicaLoCreoAlMismoTiempo_ElExternalIdUnicoLoResuelve()
    {
        // La búsqueda da 404, pero cuando llega el POST otra réplica ya creó el server.
        var panel = FakePanel.Standard();
        panel.On("POST servers", (_, next) =>
        {
            panel.AddServer(Samples.ServiceId, Samples.Paper, port: 25580);
            return next();
        }, times: 1);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(1, panel.Servers.Count);
        Assert.AreEqual("mc.example.test:25580", body!["result"]!["external_ids"]!["address"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UsuarioCreadoPorOtraAlta_ElCorreoUnicoLoResuelve()
    {
        var panel = FakePanel.Standard();
        FakeUser? raced = null;
        panel.On("POST users", (_, next) =>
        {
            raced = panel.AddUser("77", "cliente77@example.com");
            return next();
        }, times: 1);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(raced!.Id, panel.Server(Samples.ServiceId).User);
        Assert.AreEqual(1, panel.Users.Count);
    }

    [TestMethod]
    public async Task SinNodoConLugar_EsRejected()
    {
        var panel = FakePanel.Standard();
        foreach (var allocation in panel.Allocations)
        {
            allocation.Server = 1;
        }

        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PteroHost.Code(body));
        Assert.AreEqual("no_deployable_node", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.IsFalse(body["error"]!["retryable"]!.GetValue<bool>());
        Assert.IsEmpty(panel.Servers);
    }

    [TestMethod]
    public async Task ValidacionDelPanel_EsInvalidInputConLosCampos()
    {
        var panel = FakePanel.Standard();
        panel.On("POST servers", (_, _) => Samples.Validation("environment.MC_VERSION", "max"));
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PteroHost.Code(body));
        CollectionAssert.AreEqual(new[] { "environment.MC_VERSION" }, PteroHost.Strings(body!["error"]!["details"]!["fields"]));
    }

    [TestMethod]
    public async Task InstalacionFallida_LaVeElStaff()
    {
        // Un alta repetida que encuentra el server con la instalación fallida no la da por buena.
        var panel = FakePanel.Standard();
        panel.AddServer(Samples.ServiceId, Samples.Paper, status: "install_failed");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("create", Samples.Context(), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, PteroHost.Code(body));
        Assert.AreEqual("install_failed", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DatosDelPedido_SoloLosDeclarados_YDentroDeSusOpciones()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var plan = Samples.Minecraft();
        plan["order_fields"] = new JsonArray(
            new JsonObject { ["key"] = "egg_id", ["required"] = true, ["options"] = new JsonArray("5", "6") },
            new JsonObject { ["key"] = "Server Jar File" });
        var order = new JsonObject
        {
            ["variables"] = new JsonObject { ["egg_id"] = "6", ["Server Jar File"] = "propio.jar", ["FABRIC_VERSION"] = "999" },
        };

        var (status, body) = await host.Call("create", Samples.Context(plan: plan, order: order), Key);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var sent = host.Panel.Last("POST servers").Body!;
        Assert.AreEqual(6, sent["egg"]!.GetValue<int>());
        Assert.AreEqual("propio.jar", sent["environment"]!["SERVER_JARFILE"]!.GetValue<string>());

        // FABRIC_VERSION no lo declara el producto: no se aplica aunque venga en el pedido.
        Assert.AreEqual("latest", sent["environment"]!["FABRIC_VERSION"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EggFueraDeLasOpciones_OSinUnObligatorio_NoLlegaAlPanel()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var plan = Samples.Minecraft();
        plan["order_fields"] = new JsonArray(
            new JsonObject { ["key"] = "egg_id", ["options"] = new JsonArray("5", "6") },
            new JsonObject { ["key"] = "MC_VERSION", ["required"] = true });
        var order = new JsonObject { ["variables"] = new JsonObject { ["egg_id"] = "8" } };

        var (status, body) = await host.Call("create", Samples.Context(plan: plan, order: order), Key);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEquivalent(
            new[] { "order.variables.egg_id", "order.variables.MC_VERSION" },
            PteroHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.AreEqual(0, host.Panel.Writes);
    }

    [TestMethod]
    public async Task AltaRepetida_ConUnValorQueYaNoSeOfrece_Converge()
    {
        // El server ya existe: los datos del pedido ya se aplicaron y no se vuelven a controlar contra el producto.
        var panel = FakePanel.Standard();
        var existing = panel.AddServer(Samples.ServiceId, Samples.Paper);
        await using var host = await PteroHost.Start(panel);
        var plan = Samples.Minecraft();
        plan["order_fields"] = new JsonArray(new JsonObject { ["key"] = "SERVER_JARFILE", ["required"] = true, ["options"] = new JsonArray("a.jar") });
        var order = new JsonObject { ["variables"] = new JsonObject { ["SERVER_JARFILE"] = "el-que-se-ofrecia.jar" } };

        var (status, body) = await host.Call("create", Samples.Context(plan: plan, order: order), "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(existing.Id.ToString(), body!["result"]!["external_ids"]!["server_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Validate_ControlaQueLasVariablesExistanEnElEgg_SinEscribir()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var plan = Samples.Minecraft();
        plan["order_fields"] = new JsonArray(new JsonObject { ["key"] = "MC_VERSION" }, new JsonObject { ["key"] = "NO_EXISTE" });

        var (bad, badBody) = await host.Call("validate", Samples.Context(plan: plan));
        plan["order_fields"] = new JsonArray(new JsonObject { ["key"] = "Minecraft Version" });
        var (ok, _) = await host.Call("validate", Samples.Context(plan: plan));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad);
        CollectionAssert.AreEqual(new[] { "params.order_fields.NO_EXISTE" }, PteroHost.Strings(badBody!["error"]!["details"]!["fields"]));
        Assert.AreEqual(HttpStatusCode.OK, ok);
        Assert.AreEqual(0, host.Panel.Writes);
    }

    [TestMethod]
    public async Task ParametrosInvalidos_ListanLosCampos()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var plan = Samples.Minecraft();
        plan.Remove("egg_id");
        plan["io_weight"] = 5;
        plan["memory_mb"] = -1;

        var (status, body) = await host.Call("validate", Samples.Context(plan: plan));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEquivalent(
            new[] { "params.egg_id", "params.io_weight", "params.memory_mb" },
            PteroHost.Strings(body!["error"]!["details"]!["fields"]));
    }
}
