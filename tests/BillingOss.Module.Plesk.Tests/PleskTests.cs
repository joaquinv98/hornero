using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Hosting.Testing;
using BillingOss.Modules.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.Plesk.Tests;

/// <summary>El módulo de Plesk contra un Plesk falso: manifiesto, configuración, la forma de cada paquete y sus errores.</summary>
[TestClass]
public sealed class PleskTests
{
    private static readonly string Username = Usernames.For("c", ModuleHost.Uuid);

    private static JsonObject Config() => new() { ["host"] = FakePlesk.Host, ["api_key"] = FakePlesk.Key, ["ip_address"] = FakePlesk.Ip };

    /// <summary>
    /// El plazo de las operaciones se prueba en la biblioteca con un reloj a mano. Acá el reloj no avanza, así una PC
    /// cargada no vence el plazo de una prueba que no es de plazos.
    /// </summary>
    private static Task<ModuleHost> Start(FakePlesk plesk, ILogger? logger = null) =>
        ModuleHost.Start(PleskModule.Create(_ => new HttpClient(plesk, disposeHandler: false), logger ?? NullLogger.Instance, new StepClock()), Config());

    /// <summary>La IP del navegador que manda el núcleo con el acceso directo.</summary>
    private static JsonObject Browser() => new() { ["client_ip"] = "203.0.113.7" };

    [TestMethod]
    public async Task Manifiesto_IdentidadConfiguracionYSecretos()
    {
        await using var host = await Start(new FakePlesk());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("plesk", manifest!["id"]!.GetValue<string>());
        Assert.AreEqual("0.1.0", manifest["version"]!.GetValue<string>());
        Assert.AreEqual("Apache-2.0", manifest["license"]!.GetValue<string>());
        var config = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "host", "api_key", "ip_address" }, ModuleHost.Strings(config["required"]));
        Assert.AreEqual(8443, config["properties"]!["port"]!["default"]!.GetValue<int>());
        foreach (var (name, property) in config["properties"]!.AsObject())
        {
            Assert.AreEqual(name == "api_key", property!["x-secret"] is not null, name);
        }

        Assert.Contains("provisioning.sso", manifest["capabilities"]!.ToJsonString());
        Assert.Contains("provisioning.usage", manifest["capabilities"]!.ToJsonString());
        Assert.AreEqual("Plan de servicio de Plesk", manifest["product_schema"]!["properties"]!["plan"]!["description"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Test_MandaUnPaqueteConLaClave_YDevuelveLosPlanes()
    {
        var plesk = new FakePlesk();
        await using var host = await Start(plesk);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(FakePlesk.Plans, ModuleHost.Strings(body!["result"]!["plans"]));
        var call = plesk.Calls.Single();
        Assert.AreEqual("POST", call.Method);
        Assert.AreEqual("/enterprise/control/agent.php", call.Path);
        Assert.AreEqual("text/xml", call.ContentType);
        Assert.AreEqual(FakePlesk.Key, call.Key);
        CollectionAssert.AreEqual(new[] { "service-plan/get" }, call.Operations);
    }

    [TestMethod]
    [DataRow("host", "https://plesk.example.test")]
    [DataRow("port", 70000)]
    [DataRow("tls_fingerprint", "no-es-una-huella")]
    [DataRow("api_key", "con espacio")]
    [DataRow("api_key", "")]
    [DataRow("api_key", "clavéconacento")]
    [DataRow("ip_address", "no-es-una-ip")]
    [DataRow("ip_address", "2001:db8::10")]
    [DataRow("username_prefix", "Mayu")]
    public async Task ConfiguracionQueNoSirve_EsInvalidConfig_SinSalirALaRed(string field, object value)
    {
        var plesk = new FakePlesk().With("alice");
        await using var host = await Start(plesk);
        var config = Config();
        config[field] = JsonValue.Create(value);

        var (status, body) = await host.Op("get_status", ModuleHost.Existing("alice"), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        CollectionAssert.Contains(ModuleHost.Strings(body!["error"]!["details"]!["fields"]), field);
        Assert.IsEmpty(plesk.Calls);
    }

    [TestMethod]
    public async Task ClaveDeOtraInstalacion_EsInvalidConfigLegible()
    {
        var plesk = new FakePlesk();
        await using var host = await Start(plesk);
        var config = Config();
        config["api_key"] = "OTRACLAVEINVENTADA";

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.Contains("IP de salida", body!["error"]!["message"]!.GetValue<string>());
        Assert.Contains("Authentication failed", body["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(401, "")]
    [DataRow(200, "permiso")]
    public async Task UnRechazoHttpODePermiso_EsInvalidConfig(int status, string kind)
    {
        var plesk = new FakePlesk().On(
            "service-plan/get",
            status,
            kind == "permiso" ? FakePlesk.Reply(("service-plan/get", FakePlesk.Error(1006, "Permission denied"))) : string.Empty);
        await using var host = await Start(plesk);

        var (code, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, code);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        CollectionAssert.Contains(ModuleHost.Strings(body!["error"]!["details"]!["fields"]), "api_key");
    }

    [TestMethod]
    public async Task CicloCompleto_ConLosPaquetesDePlesk()
    {
        var plesk = new FakePlesk();
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Username, ModuleHost.Username(body));
        var add = plesk.Last("customer/add");
        CollectionAssert.AreEqual(new[] { "customer/add", "webspace/add" }, add.Operations);
        var customer = add.Operation("customer/add").Element("gen_info")!;
        Assert.AreEqual("Cliente de Prueba S.A.", customer.Element("pname")!.Value);
        Assert.AreEqual(Username, customer.Element("login")!.Value);
        Assert.AreEqual(ModuleHost.Password, customer.Element("passwd")!.Value);
        Assert.AreEqual("cliente@example.com", customer.Element("email")!.Value);
        Assert.AreEqual(ModuleHost.Uuid, customer.Element("external-id")!.Value);
        var setup = add.Operation("webspace/add").Element("gen_setup")!;
        Assert.AreEqual("nuevo.example.test", setup.Element("name")!.Value);
        Assert.AreEqual(Username, setup.Element("owner-login")!.Value);
        Assert.AreEqual("basico", add.Operation("webspace/add").Element("plan-name")!.Value);
        Assert.AreEqual(Username, plesk.Webspaces["nuevo.example.test"].FtpLogin);
        Assert.AreEqual(ModuleHost.Password, plesk.Webspaces["nuevo.example.test"].FtpPassword);
        CollectionAssert.AreEqual(
            new[]
            {
                "customer/get", "webspace/get", "service-plan/get", "webspace/get", "site/get", "customer/get", "webspace/get", "service-plan/get",
                "customer/add", "webspace/add", "customer/get", "webspace/get", "service-plan/get",
            },
            plesk.Operations);
        Assert.AreEqual(Username, plesk.Calls[0].Operation("webspace/get").Element("filter")!.Element("owner-login")!.Value);

        var context = ModuleHost.Existing(Username, "nuevo.example.test");
        (_, body) = await host.Op("suspend", context);
        Assert.AreEqual("suspended", ModuleHost.Result(body, "observed_state"));
        var suspend = plesk.Last("webspace/set").Operation("webspace/set");
        Assert.AreEqual("nuevo.example.test", suspend.Element("filter")!.Element("name")!.Value);
        Assert.AreEqual("16", suspend.Element("values")!.Element("gen_setup")!.Element("status")!.Value);

        (_, body) = await host.Op("unsuspend", context);
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual(0, plesk.Webspaces["nuevo.example.test"].Status);

        (_, body) = await host.Op("change_plan", ModuleHost.Existing(Username, "nuevo.example.test", plan: "Plan Empresa"));
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        var change = plesk.Last("webspace/switch-subscription").Operation("webspace/switch-subscription");
        Assert.AreEqual(FakePlesk.Guid("Plan Empresa"), change.Element("plan-guid")!.Value);

        (status, _) = await host.Op("run_action", context, ModuleHost.ChangePassword("Nueva-Contrasena_99"));
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("Nueva-Contrasena_99", plesk.Customers[Username].Password);
        Assert.AreEqual("Nueva-Contrasena_99", plesk.Webspaces["nuevo.example.test"].FtpPassword);

        (_, body) = await host.Op("get_status", context);
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("Plan Empresa", body!["result"]!["details"]!["plan"]!.GetValue<string>());

        (_, body) = await host.Op("view", context);
        Assert.AreEqual("https://plesk.example.test:8443/", body!["result"]!["blocks"]![2]!["url"]!.GetValue<string>());

        (status, body) = await host.Op("terminate", context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Username, plesk.Last("customer/del").Operation("customer/del").Element("filter")!.Element("login")!.Value);
        Assert.IsEmpty(plesk.Customers);
        Assert.IsEmpty(plesk.Webspaces);
        (_, body) = await host.Op("get_status", context);
        Assert.AreEqual("not_found", ModuleHost.Result(body, "observed_state"));

        CollectionAssert.AreEqual(
            new[] { "customer/add", "webspace/add", "webspace/set", "webspace/set", "webspace/switch-subscription", "customer/set", "webspace/set", "customer/del" },
            plesk.Mutations);
    }

    [TestMethod]
    public async Task ConsultaPrevia_ElDominioEnLaSuscripcionDeOtroCliente_NoLlegaAlAlta()
    {
        var plesk = new FakePlesk().With("otro", "nuevo.example.test");
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("domain_taken", ModuleHost.Reason(body));
        Assert.AreEqual("nuevo.example.test", plesk.Last("site/get").Operation("webspace/get").Element("filter")!.Element("name")!.Value);
        Assert.IsEmpty(plesk.Mutations);
        Assert.IsFalse(plesk.Customers.ContainsKey(Username));
    }

    [TestMethod]
    [DataRow("sitio")]
    [DataRow("admin")]
    public async Task ConsultaPrevia_UnSitioAdicionalOUnaSuscripcionDelAdministrador_TambienFrenan(string kind)
    {
        var plesk = new FakePlesk();
        if (kind == "sitio")
        {
            plesk.Sites.Add("nuevo.example.test");
        }
        else
        {
            plesk.Subscription("admin", "nuevo.example.test");
        }

        await using var host = await Start(plesk);

        var (_, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual("domain_taken", ModuleHost.Reason(body));
        Assert.IsEmpty(plesk.Mutations);
    }

    [TestMethod]
    public async Task ConsultaPrevia_LaSuscripcionDeUnClienteConLaMarcaDelServicio_LaAdopta()
    {
        // Un alta anterior del mismo servicio con otro prefijo de usuario.
        var plesk = new FakePlesk().With("xyz12345", "nuevo.example.test", marker: ModuleHost.Uuid);
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("xyz12345", ModuleHost.Username(body));
        Assert.IsEmpty(plesk.Mutations);
    }

    [TestMethod]
    public async Task AltaAMedias_ElClienteConLaMarcaYSinSuscripcion_SeCompletaSoloConLaSuscripcion()
    {
        var plesk = new FakePlesk().With(Username, string.Empty, marker: ModuleHost.Uuid);
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(new[] { "webspace/add" }, plesk.Mutations);
        Assert.AreEqual(Username, plesk.Webspaces["nuevo.example.test"].Owner);
    }

    [TestMethod]
    public async Task UnaSuscripcionRechazada_DejaElClienteConLaMarca_YElReintentoLaCompleta()
    {
        // Plesk crea el cliente y rechaza la suscripción porque el usuario de sistema ya existe en otra.
        var plesk = new FakePlesk().With("otro", "otro.example.test");
        plesk.Webspaces["otro.example.test"].FtpLogin = Username;
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("upstream_rejected", ModuleHost.Reason(body));
        Assert.Contains("already exists", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
        Assert.AreEqual(ModuleHost.Uuid, plesk.Customers[Username].ExternalId);

        plesk.Webspaces["otro.example.test"].FtpLogin = "otro";
        (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(new[] { "customer/add", "webspace/add", "webspace/add" }, plesk.Mutations);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(ModuleHost.Uuid)]
    public async Task ElClienteDelUsuarioConOtraSuscripcion_EsUserTaken_SinTocarlo(string? marker)
    {
        // Con la marca del servicio es un alta anterior con otro dominio: lo resuelve el staff.
        var plesk = new FakePlesk().With(Username, "otro.example.test", marker: marker);
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("user_taken", ModuleHost.Reason(body));
        Assert.IsEmpty(plesk.Mutations);
    }

    [TestMethod]
    public async Task UnPlanQueNoExiste_EsInvalidConfig_SinCrearNada()
    {
        var plesk = new FakePlesk();
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(plan: "inexistente"), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        CollectionAssert.Contains(ModuleHost.Strings(body!["error"]!["details"]!["fields"]), "plan");
        Assert.IsEmpty(plesk.Mutations);
    }

    [TestMethod]
    public async Task BajaDeUnClienteConOtrasSuscripciones_BorraSoloLaDelDominio()
    {
        var plesk = new FakePlesk().With("alice").Subscription("alice", "otro.example.test");
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("terminate", ModuleHost.Existing("alice"));
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body, "observed_state"));

        CollectionAssert.AreEqual(new[] { "webspace/del" }, plesk.Mutations);
        Assert.IsTrue(plesk.Customers.ContainsKey("alice"));
        CollectionAssert.AreEqual(new[] { "otro.example.test" }, plesk.Webspaces.Keys.ToArray());
        (_, body) = await host.Op("get_status", ModuleHost.Existing("alice"));
        Assert.AreEqual("not_found", ModuleHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task ClienteImportadoConElExternalIdDelSistemaAnterior_SeOperaPorElDominio()
    {
        // El sistema anterior pudo dejar su propio ID en el external-id del cliente: no es una marca de billing-oss.
        var plesk = new FakePlesk().With("alice", marker: "4321");
        await using var host = await Start(plesk);
        var context = ModuleHost.Existing("alice");

        var (status, body) = await host.Op("suspend", context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(16, plesk.Webspaces["example.test"].Status);

        (_, body) = await host.Op("get_status", context);
        Assert.AreEqual("suspended", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("4321", plesk.Customers["alice"].ExternalId);
    }

    [TestMethod]
    public async Task BajaDeUnAltaAMedias_BorraElClienteConLaMarca()
    {
        var plesk = new FakePlesk().With(Username, string.Empty, marker: ModuleHost.Uuid);
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("terminate", ModuleHost.Existing(Username, "nuevo.example.test"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(new[] { "customer/del" }, plesk.Mutations);
        Assert.IsEmpty(plesk.Customers);
    }

    [TestMethod]
    public async Task UnRechazoDePlesk_EsRejectedConSuTexto_SinLaContrasena()
    {
        var plesk = new FakePlesk().On(
            "webspace/add",
            200,
            FakePlesk.Reply(("customer/add", FakePlesk.Ok()), ("webspace/add", FakePlesk.Error(1019, $"La contraseña {ModuleHost.Password} es débil"))));
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("upstream_rejected", ModuleHost.Reason(body));
        Assert.AreEqual("La contraseña *** es débil (1019)", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(200, "<html><body>Mantenimiento</body></html>")]
    [DataRow(502, "")]
    [DataRow(200, "<packet><customer><get><result><status>rara</status></result></get></customer></packet>")]
    [DataRow(200, "<!DOCTYPE packet [<!ENTITY x \"y\">]><packet>&x;</packet>")]
    [DataRow(200, "<packet><customer><get><result><status>error</status><errcode>1023</errcode><errtext>Operation failed</errtext></result></get></customer></packet>")]
    public async Task UnaLecturaQueNoEsDePlesk_EsUpstreamUnavailable(int status, string text)
    {
        var plesk = new FakePlesk().With("alice").On("customer/get", status, text);
        await using var host = await Start(plesk);

        var (code, body) = await host.Op("get_status", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, code, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task Un5xxEnLaSuspension_EsSinRespuesta_YQuedaEnCursoConLaVentana()
    {
        var plesk = new FakePlesk().With("alice").On("webspace/set", 502, "Bad Gateway");
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("suspend", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(ModuleResponse.PendingStatus, body!["status"]!.GetValue<string>());
        Assert.Contains("-sent-", body["operation_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnCorteDespuesDeBorrar_LaRelecturaConfirmaLaBaja()
    {
        var plesk = new FakePlesk().With("alice").CutAfter("customer/del", apply: true);
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("terminate", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task AccesoDirecto_CreaUnaSesionAtadaALaIpDelNavegador()
    {
        var plesk = new FakePlesk().With("alice");
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("sso", ModuleHost.Existing("alice"), Browser());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("https://plesk.example.test:8443/enterprise/rsession_init.php?PLESKSESSID=SESIONINVENTADA0123456789abcdef", ModuleHost.Result(body, "url"));
        Assert.IsNull(body!["result"]!["expires_at"]);
        Assert.AreEqual("203.0.113.7", plesk.LastUserIp);
        Assert.AreEqual("alice", plesk.Last("server/create_session").Operation("server/create_session").Element("login")!.Value);
        Assert.IsEmpty(plesk.Mutations);

        (status, body) = await host.Op("sso", ModuleHost.Existing("alice"));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        CollectionAssert.Contains(ModuleHost.Strings(body!["error"]!["details"]!["fields"]), "client_ip");
    }

    [TestMethod]
    public async Task Uso_DiscoYTransferenciaDelMes_DeBytesAGB()
    {
        var plesk = new FakePlesk().With("alice").With("alice2", "otro.example.test");
        plesk.Webspaces["example.test"].RealSize = 512L * 1024 * 1024;
        plesk.Webspaces["example.test"].Traffic = 1280L * 1024 * 1024;
        await using var host = await Start(plesk);

        var (status, body) = await host.Op("usage", ModuleHost.Existing("alice"), ModuleHost.Metrics());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("""{"disk_gb":"0.5","transfer_gb":"1.25"}""", body!["result"]!["metrics"]!.ToJsonString());
        var dataset = plesk.Calls[^1].Operation("webspace/get").Element("dataset")!.Elements().Select(e => e.Name.LocalName).ToArray();
        CollectionAssert.AreEqual(new[] { "gen_info", "stat" }, dataset);
        Assert.IsEmpty(plesk.Mutations);
    }

    [TestMethod]
    public async Task SecretosTestigo_NoAparecenEnRespuestasNiLogs()
    {
        var echo = $"Error con {ModuleHost.Password} y {FakePlesk.Key}";
        var plesk = new FakePlesk().With("alice")
            .On("webspace/add", 200, FakePlesk.Reply(("customer/add", FakePlesk.Ok()), ("webspace/add", FakePlesk.Error(1019, echo))))
            .On("customer/set", 200, FakePlesk.Reply(("customer/set", FakePlesk.Error(1019, echo)), ("webspace/set", FakePlesk.Error(1019, echo))));
        var logger = new ListLogger();
        await using var host = await Start(plesk, logger);

        var responses = new[]
        {
            await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials()),
            await host.Op("run_action", ModuleHost.Existing("alice"), ModuleHost.ChangePassword()),
            await host.Op("sso", ModuleHost.Existing("alice"), Browser()),
            await host.Send("/v0/instance/test", []),
        };

        foreach (var (_, body) in responses)
        {
            Assert.DoesNotContain(ModuleHost.Password, body!.ToJsonString());
            Assert.DoesNotContain(FakePlesk.Key, body.ToJsonString());
        }

        Assert.Contains("***", responses[0].Body!["error"]!["details"]!["upstream"]!.GetValue<string>());
        Assert.DoesNotContain(ModuleHost.Password, logger.Text);
        Assert.DoesNotContain(FakePlesk.Key, logger.Text);
        Assert.DoesNotContain("SESIONINVENTADA", logger.Text);
        Assert.Contains("customer/add", logger.Text);
    }
}
