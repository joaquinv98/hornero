using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Hestia.Tests;

/// <summary>create idempotente por estado, sin vuelta atrás, y su continuación por consulta cuando se acaba el tiempo.</summary>
[TestClass]
public sealed class CreateTests
{
    [TestMethod]
    public async Task Alta_CreaUsuarioDeterministico_IdiomaYDominio_YVerifica()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (status, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", HestiaHost.Result(body, "observed_state"));
        var ids = body!["result"]!["external_ids"]!;
        Assert.AreEqual("c1042", ids["username"]!.GetValue<string>());
        Assert.AreEqual("nuevo.example.test", ids["domain"]!.GetValue<string>());
        Assert.IsNull(body["result"]!["warnings"]);

        CollectionAssert.AreEqual(new[] { "v-add-user", "v-change-user-language", "v-add-domain" }, host.Hestia.Mutations);
        var add = host.Hestia.Calls.Single(c => c.Command == "v-add-user");
        CollectionAssert.AreEqual(new[] { "c1042", HestiaHost.Password, "cliente@example.com", "gainsboro", "Cliente de Prueba" }, add.Args);
        CollectionAssert.AreEqual(new[] { "c1042", "es" }, host.Hestia.Calls.Single(c => c.Command == "v-change-user-language").Args);
        CollectionAssert.AreEqual(new[] { "c1042", "nuevo.example.test" }, host.Hestia.Calls.Single(c => c.Command == "v-add-domain").Args);

        var user = host.Hestia.Users["c1042"];
        Assert.AreEqual("es", user.Language);
        Assert.IsTrue(user.Resources["web"].ContainsKey("nuevo.example.test"));

        // La ausencia previa se probó con el inventario y el alta terminó con una lectura completa.
        CollectionAssert.AreEqual(new[] { "v-list-user", "v-list-users" }, host.Hestia.Commands[..2]);
        Assert.AreEqual("v-list-cron-jobs", host.Hestia.Commands[^1]);
    }

    [TestMethod]
    public async Task UnRecursoEnOtroEstadoAlTerminarElAlta_EsConflictPartial()
    {
        // v-add-domain contesta OK y deja el correo suspendido: el chequeo final del alta lo tiene que ver.
        var hestia = new FakeHestia();
        hestia.On("v-add-domain", (call, run) =>
        {
            var answer = run();
            hestia.Users["c1042"].Resources["mail"][call.Args[1]] = true;
            return answer;
        });
        await using var host = await HestiaHost.Start(hestia);

        var (_, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(ErrorCodes.Conflict, HestiaHost.Code(body), body?.ToJsonString());
        Assert.AreEqual("partial", HestiaHost.Reason(body));
        CollectionAssert.AreEqual(new[] { "mail" }, HestiaHost.Strings(body!["error"]!["details"]!["resources"]));
    }

    [TestMethod]
    public async Task ElPrefijoDelUsuario_SaleDeLaInstancia()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());
        var config = HestiaHost.Config();
        config["username_prefix"] = "h";

        var (_, body) = await host.Op("create", HestiaHost.NewService(id: 7), HestiaHost.Credentials(), config: config);

        Assert.AreEqual("h7", body!["result"]!["external_ids"]!["username"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CorteEntreElUsuarioYElDominio_QuedaEnCurso_YLaConsultaDeOtraReplicaTerminaConUnSoloUsuario()
    {
        var clock = new StepClock();
        var hestia = new FakeHestia().CutAfter("v-add-domain", apply: false);
        await using var first = await HestiaHost.Start(hestia, time: clock);
        await using var second = await HestiaHost.Start(hestia, time: clock);

        var (status, body) = await first.Op("create", HestiaHost.NewService(), HestiaHost.Credentials(), key: "svc-1042-create-1");

        // Hestia puede seguir agregando el dominio: la operación queda en curso con el momento en que salió el mutador.
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        var id = body["operation_id"]!.GetValue<string>();
        Assert.StartsWith("create-svc1042-sent-", id);

        // Dentro de la ventana la consulta solo relee, aunque le conteste otra réplica.
        hestia.On("v-add-domain", (_, run) => run());
        (_, body) = await second.Send($"/v0/operations/{id}", [], context: HestiaHost.NewService());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.AreEqual(id, body["operation_id"]!.GetValue<string>());
        Assert.AreEqual(1, hestia.Count("v-add-domain"));

        // Pasada la ventana, la consulta sigue el alta desde el estado de Hestia y la termina.
        clock.Advance(TimeSpan.FromMinutes(11));
        (status, body) = await second.Send($"/v0/operations/{id}", [], context: HestiaHost.NewService());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual(1, hestia.Count("v-add-user"));
        Assert.AreEqual(2, hestia.Count("v-add-domain"));
        Assert.AreEqual(1, hestia.Count("v-change-user-language"));
        Assert.IsTrue(hestia.Users["c1042"].Resources["dns"].ContainsKey("nuevo.example.test"));
    }

    [TestMethod]
    public async Task ConElUuidDelServicio_ElUsuarioLlevaSuMarca_YLaRepeticionSinRegistroEncuentraLaCuenta()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());
        var context = HestiaHost.NewService();
        context["service"]!["uuid"] = "0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e11";

        var (status, body) = await host.Op("create", context, HestiaHost.Credentials(), key: "svc-1042-create-1");
        var (again, repeated) = await host.Op("create", context, HestiaHost.Credentials(), key: "svc-1042-create-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(HttpStatusCode.OK, again, repeated?.ToJsonString());
        var username = body!["result"]!["external_ids"]!["username"]!.GetValue<string>();
        StringAssert.Matches(username, new System.Text.RegularExpressions.Regex("^c1042[a-z2-7]{4}$"));
        Assert.AreEqual(username, repeated!["result"]!["external_ids"]!["username"]!.GetValue<string>());
        Assert.AreEqual(1, host.Hestia.Mutations.Count(m => m == "v-add-user"));
    }

    [TestMethod]
    public async Task ElMismoIdDeOtraInstalacion_NoChocaConLaCuentaDeOtroUuid()
    {
        // Otra instalación que comparte el panel ya creó la cuenta de su servicio 1042 con otro dominio.
        await using var host = await HestiaHost.Start(new FakeHestia());
        var other = HestiaHost.NewService(domain: "otro.example.test");
        other["service"]!["uuid"] = "7d1f3c2a-1111-4c2e-9f7d-3d2f1c0a9e11";
        var (_, first) = await host.Op("create", other, HestiaHost.Credentials(), key: "a");
        var mine = HestiaHost.NewService();
        mine["service"]!["uuid"] = "0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e11";

        var (status, body) = await host.Op("create", mine, HestiaHost.Credentials(), key: "b");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreNotEqual(
            first!["result"]!["external_ids"]!["username"]!.GetValue<string>(),
            body!["result"]!["external_ids"]!["username"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnUuidInvalido_EsInvalidInput_SinTocarHestia()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());
        var context = HestiaHost.NewService();
        context["service"]!["uuid"] = "no-es-un-uuid";

        var (status, body) = await host.Op("create", context, HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.InvalidInput, HestiaHost.Code(body));
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    public async Task UsuarioExistenteConOtroCorreo_EsRejected_SinTocarNada()
    {
        var hestia = new FakeHestia().WithAccount(username: "c1042", domain: "otro.example.test");
        hestia.Users["c1042"].Contact = "otra.persona@example.com";
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(body));
        Assert.AreEqual("user_taken", HestiaHost.Reason(body));
        Assert.IsEmpty(hestia.Mutations);
    }

    [TestMethod]
    public async Task UsuarioExistenteConElMismoCorreoYOtroDominio_EsRejected_SinTocarNada()
    {
        // Adoptarlo sumaría el dominio a una cuenta con otros sitios, que un terminate de este servicio borraría.
        var hestia = new FakeHestia().WithAccount(username: "c1042", domain: "otro.example.test");
        await using var host = await HestiaHost.Start(hestia);

        var (_, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(body));
        Assert.AreEqual("user_taken", HestiaHost.Reason(body));
        Assert.IsEmpty(hestia.Mutations);
    }

    [TestMethod]
    public async Task CuentaVaciaDeUnAltaAnterior_ConOtroPaquete_SeLeCambiaElPaquete()
    {
        // Lo que deja un alta que falló después de v-add-user, repetida por el staff con otro plan.
        var hestia = new FakeHestia();
        hestia.Users["c1042"] = new FakeUser { Package = "freetier", Shell = "nologin", Language = "es" };
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("create", HestiaHost.NewService(package: "gainsboro"), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("gainsboro", hestia.Users["c1042"].Package);
        CollectionAssert.AreEqual(new[] { "v-change-user-package", "v-add-domain" }, hestia.Mutations);
    }

    [TestMethod]
    public async Task UsuarioExistenteConElDominio_EsUnaRepeticion_AunqueLaCuentaCambieDeCorreo()
    {
        var hestia = new FakeHestia().WithAccount(username: "c1042", domain: "nuevo.example.test");
        hestia.Users["c1042"].Contact = "correo.anterior@example.com";
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsEmpty(hestia.Mutations);
    }

    [TestMethod]
    public async Task IdiomaQueHestiaNoTiene_UsaElDeLaInstancia()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (status, body) = await host.Op("create", HestiaHost.NewService(language: "fr-FR"), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsNull(body!["result"]!["warnings"]);
        Assert.AreEqual("es", host.Hestia.Users["c1042"].Language);
        CollectionAssert.AreEqual(new[] { "fr", "es" }, host.Hestia.Calls.Where(c => c.Command == "v-change-user-language").Select(c => c.Args[1]).ToArray());
    }

    [TestMethod]
    public async Task FallaDeIdioma_DejaElAltaTerminadaConAviso()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().On("v-change-user-language", 500, "Error: algo salió mal"));

        var (status, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("active", HestiaHost.Result(body, "observed_state"));
        Assert.AreEqual("language_not_applied", body!["result"]!["warnings"]![0]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DominioDeOtroUsuario_EsRejectedConElTextoDeHestia_YNoDeshaceElUsuario()
    {
        var hestia = new FakeHestia().WithAccount(username: "bob", domain: "nuevo.example.test");
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(body));
        Assert.AreEqual("domain_rejected", HestiaHost.Reason(body));
        Assert.AreEqual("Error: domain nuevo.example.test exists", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
        Assert.IsTrue(hestia.Users.ContainsKey("c1042"), "Sin vuelta atrás: el staff decide qué hacer con lo creado.");
        Assert.AreEqual(0, hestia.Count("v-delete-user"));
    }

    [TestMethod]
    public async Task PaqueteQueHestiaRechaza_EsRejected()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (_, body) = await host.Op("create", HestiaHost.NewService(package: "inexistente"), HestiaHost.Credentials());

        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(body));
        Assert.AreEqual("Error: package inexistente doesn't exist", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("corta")]
    [DataRow("con espacio adentro")]
    public async Task SinUnaContrasenaQueSirva_EsInvalidInput_SinSalirAHestia(string? password)
    {
        await using var host = await HestiaHost.Start(new FakeHestia());
        var input = password is null ? new JsonObject() : HestiaHost.Credentials(password);

        var (_, body) = await host.Op("create", HestiaHost.NewService(), input);

        Assert.AreEqual(ErrorCodes.InvalidInput, HestiaHost.Code(body));
        CollectionAssert.AreEqual(new[] { "credentials.password" }, HestiaHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(host.Hestia.Calls);
    }

    [TestMethod]
    public async Task SinElCorreoDeLaCuenta_EsInvalidInput()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());
        var context = HestiaHost.NewService();
        context["account"]!.AsObject().Remove("email");

        var (_, body) = await host.Op("create", context, HestiaHost.Credentials());

        Assert.AreEqual(ErrorCodes.InvalidInput, HestiaHost.Code(body));
        Assert.IsEmpty(host.Hestia.Calls);
    }

    [TestMethod]
    public async Task SinTiempoParaElUsuario_EsUpstreamUnavailable_SinMutar()
    {
        // Cada comando tarda 50 segundos: después de probar la ausencia no queda tiempo para v-add-user.
        var clock = new StepClock();
        var hestia = new FakeHestia { Clock = clock, Step = TimeSpan.FromSeconds(50) };
        await using var host = await HestiaHost.Start(hestia, time: clock);

        var (status, body) = await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual("out_of_time", HestiaHost.Reason(body));
        Assert.IsEmpty(hestia.Mutations);
    }

    [TestMethod]
    public async Task SinTiempoDespuesDeCrearElUsuario_EsPending_YLaConsultaDeOtraReplicaTerminaElAlta()
    {
        // Cada comando tarda 10 segundos y el alta tiene 110: alcanza para crear el usuario y verificarlo, no para el resto.
        var clock = new StepClock();
        var hestia = new FakeHestia { Clock = clock, Step = TimeSpan.FromSeconds(10) };
        await using var slow = await HestiaHost.Start(hestia, time: clock);
        await using var other = await HestiaHost.Start(hestia);

        var (status, body) = await slow.Op("create", HestiaHost.NewService(), HestiaHost.Credentials(), key: "svc-1042-create-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.AreEqual("create-svc1042", body["operation_id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "v-add-user" }, hestia.Mutations);

        // La consulta lleva el sobre completo sin credenciales, como la manda el núcleo.
        hestia.Step = TimeSpan.Zero;
        (status, body) = await other.Send("/v0/operations/create-svc1042", [], context: HestiaHost.NewService());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual("c1042", body["result"]!["external_ids"]!["username"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "v-add-user", "v-change-user-language", "v-add-domain" }, hestia.Mutations);
    }

    [TestMethod]
    public async Task ConsultaDeUnAltaSinCuenta_NoPuedeCrearla_YEsIncierta()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (status, body) = await host.Send("/v0/operations/create-svc1042", [], context: HestiaHost.NewService());

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual("user_missing", HestiaHost.Reason(body));
        Assert.IsEmpty(host.Hestia.Mutations);
    }
}
