using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Hosting.Testing;
using BillingOss.Modules.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.DirectAdmin.Tests;

/// <summary>El módulo de DirectAdmin contra un DirectAdmin falso: manifiesto, configuración, la forma de cada comando y sus errores.</summary>
[TestClass]
public sealed class DirectAdminTests
{
    private static readonly string Username = Usernames.For("c", ModuleHost.Uuid);

    private static JsonObject Config() => new() { ["host"] = FakeDirectAdmin.Host, ["api_key"] = FakeDirectAdmin.Key, ["ip_address"] = FakeDirectAdmin.Ip };

    /// <summary>
    /// El plazo de las operaciones se prueba en la biblioteca con un reloj a mano. Acá el reloj no avanza, así una PC
    /// cargada no vence el plazo de una prueba que no es de plazos.
    /// </summary>
    private static Task<ModuleHost> Start(FakeDirectAdmin directAdmin, ILogger? logger = null) =>
        ModuleHost.Start(DirectAdminModule.Create(_ => new HttpClient(directAdmin, disposeHandler: false), logger ?? NullLogger.Instance, new StepClock()), Config());

    [TestMethod]
    public async Task Manifiesto_IdentidadConfiguracionYSecretos()
    {
        await using var host = await Start(new FakeDirectAdmin());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("directadmin", manifest!["id"]!.GetValue<string>());
        Assert.AreEqual("0.1.0", manifest["version"]!.GetValue<string>());
        Assert.AreEqual("Apache-2.0", manifest["license"]!.GetValue<string>());
        var config = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "host", "api_key", "ip_address" }, ModuleHost.Strings(config["required"]));
        Assert.AreEqual(2222, config["properties"]!["port"]!["default"]!.GetValue<int>());
        Assert.AreEqual("admin", config["properties"]!["api_user"]!["default"]!.GetValue<string>());
        foreach (var (name, property) in config["properties"]!.AsObject())
        {
            Assert.AreEqual(name == "api_key", property!["x-secret"] is not null, name);
        }

        Assert.Contains("provisioning.sso", manifest["capabilities"]!.ToJsonString());
        Assert.Contains("provisioning.usage", manifest["capabilities"]!.ToJsonString());
    }

    [TestMethod]
    public async Task Test_LeeLosPaquetesPorGet_ConAutenticacionBasica()
    {
        var directAdmin = new FakeDirectAdmin();
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(FakeDirectAdmin.Packages, ModuleHost.Strings(body!["result"]!["plans"]));
        var call = directAdmin.Calls.Single();
        Assert.AreEqual("GET", call.Method);
        Assert.AreEqual("CMD_API_PACKAGES_USER", call.Command);
        Assert.AreEqual("admin", call.User);
        Assert.AreEqual(FakeDirectAdmin.Key, call.Key);
    }

    [TestMethod]
    [DataRow("host", "https://da.example.test")]
    [DataRow("port", 70000)]
    [DataRow("tls_fingerprint", "no-es-una-huella")]
    [DataRow("api_user", "admin|otro")]
    [DataRow("api_user", "ad:min")]
    [DataRow("api_key", "con espacio")]
    [DataRow("api_key", "")]
    [DataRow("api_key", "clavéconacento")]
    [DataRow("ip_address", "no-es-una-ip")]
    [DataRow("username_prefix", "Mayu")]
    public async Task ConfiguracionQueNoSirve_EsInvalidConfig_SinSalirALaRed(string field, object value)
    {
        var directAdmin = new FakeDirectAdmin().With("alice");
        await using var host = await Start(directAdmin);
        var config = Config();
        config[field] = JsonValue.Create(value);

        var (status, body) = await host.Op("get_status", ModuleHost.Existing("alice"), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        CollectionAssert.Contains(ModuleHost.Strings(body!["error"]!["details"]!["fields"]), field);
        Assert.IsEmpty(directAdmin.Calls);
    }

    [TestMethod]
    public async Task ClaveRechazada_EsInvalidConfigLegible()
    {
        var directAdmin = new FakeDirectAdmin();
        await using var host = await Start(directAdmin);
        var config = Config();
        config["api_key"] = "OTRACLAVEINVENTADA";

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.Contains("clave de acceso", body!["error"]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnComandoQueLaClaveNoPuedeCorrer_EsInvalidConfig_TambienEnUnCambio()
    {
        var directAdmin = new FakeDirectAdmin().With("alice")
            .On("CMD_API_SELECT_USERS", 200, FakeDirectAdmin.Error("Cannot Execute Your Request", "You do not have access to CMD_API_SELECT_USERS"));
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("suspend", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.Contains("do not have access", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CicloCompleto_ConLosComandosDeDirectAdmin()
    {
        var directAdmin = new FakeDirectAdmin();
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Username, ModuleHost.Username(body));
        var create = directAdmin.Last("CMD_API_ACCOUNT_USER");
        Assert.AreEqual("POST", create.Method);
        Assert.AreEqual(Username, create.Arg("username"));
        Assert.AreEqual("nuevo.example.test", create.Arg("domain"));
        Assert.AreEqual("basico", create.Arg("package"));
        Assert.AreEqual(ModuleHost.Password, create.Arg("passwd"));
        Assert.AreEqual(ModuleHost.Password, create.Arg("passwd2"));
        Assert.AreEqual("cliente@example.com", create.Arg("email"));
        Assert.AreEqual(FakeDirectAdmin.Ip, create.Arg("ip"));
        Assert.AreEqual("no", create.Arg("notify"));
        Assert.DoesNotContain(ModuleHost.Password, create.Query, "La contraseña va en el cuerpo, nunca en la URL.");
        CollectionAssert.AreEqual(
            new[] { "CMD_API_SHOW_ALL_USERS", "CMD_API_DOMAIN_OWNERS", "CMD_API_ACCOUNT_USER", "CMD_API_SHOW_ALL_USERS", "CMD_API_SHOW_USER_CONFIG" },
            directAdmin.Commands);
        Assert.AreEqual(Username, directAdmin.Last("CMD_API_SHOW_USER_CONFIG").Arg("user"));

        var context = ModuleHost.Existing(Username, "nuevo.example.test");
        (_, body) = await host.Op("suspend", context);
        Assert.AreEqual("suspended", ModuleHost.Result(body, "observed_state"));
        var suspend = directAdmin.Last("CMD_API_SELECT_USERS");
        Assert.AreEqual("Suspend", suspend.Arg("suspend"));
        Assert.AreEqual("CMD_SELECT_USERS", suspend.Arg("location"));
        Assert.AreEqual(Username, suspend.Arg("select0"));

        (_, body) = await host.Op("unsuspend", context);
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("Unsuspend", directAdmin.Last("CMD_API_SELECT_USERS").Arg("suspend"));

        (_, body) = await host.Op("change_plan", ModuleHost.Existing(Username, "nuevo.example.test", plan: "Plan Empresa"));
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("package", directAdmin.Last("CMD_API_MODIFY_USER").Arg("action"));
        Assert.AreEqual("Plan Empresa", directAdmin.Users[Username].Package);

        (status, _) = await host.Op("run_action", context, ModuleHost.ChangePassword("Nueva-Contrasena_99"));
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("Nueva-Contrasena_99", directAdmin.Users[Username].Password);

        (_, body) = await host.Op("get_status", context);
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("Plan Empresa", body!["result"]!["details"]!["plan"]!.GetValue<string>());

        (_, body) = await host.Op("view", context);
        Assert.AreEqual("https://da.example.test:2222/", body!["result"]!["blocks"]![2]!["url"]!.GetValue<string>());

        (status, body) = await host.Op("terminate", context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var delete = directAdmin.Last("CMD_API_SELECT_USERS");
        Assert.AreEqual("yes", delete.Arg("delete"));
        Assert.AreEqual("Confirm", delete.Arg("confirmed"));
        Assert.IsEmpty(directAdmin.Users);
        (_, body) = await host.Op("get_status", context);
        Assert.AreEqual("not_found", ModuleHost.Result(body, "observed_state"));

        CollectionAssert.AreEqual(
            new[] { "CMD_API_ACCOUNT_USER", "CMD_API_SELECT_USERS", "CMD_API_SELECT_USERS", "CMD_API_MODIFY_USER", "CMD_API_USER_PASSWD", "CMD_API_SELECT_USERS" },
            directAdmin.Mutations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConsultaPrevia_ElDominioPrincipalOAdicionalDeOtroUsuario_NoLlegaAlAlta(bool extra)
    {
        var directAdmin = new FakeDirectAdmin();
        if (extra)
        {
            directAdmin.With("otro").Users["otro"].ExtraDomains.Add("nuevo.example.test");
        }
        else
        {
            directAdmin.With("otro", "nuevo.example.test");
        }

        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("domain_taken", ModuleHost.Reason(body));
        Assert.IsEmpty(directAdmin.Mutations);
    }

    [TestMethod]
    public async Task UsuarioConOtroDominio_EsUserTaken_SinTocarlo()
    {
        var directAdmin = new FakeDirectAdmin().With(Username, "ajeno.example.test");
        await using var host = await Start(directAdmin);

        var (_, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual("user_taken", ModuleHost.Reason(body));
        Assert.IsEmpty(directAdmin.Mutations);
    }

    [TestMethod]
    public async Task UnAltaRechazada_EsRejectedConElTextoDeDirectAdmin_SinLaContrasena()
    {
        var directAdmin = new FakeDirectAdmin()
            .On("CMD_API_ACCOUNT_USER", 200, FakeDirectAdmin.Error("Unable to Create User", $"La contraseña {ModuleHost.Password} es débil"));
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("upstream_rejected", ModuleHost.Reason(body));
        Assert.AreEqual("Unable to Create User: La contraseña *** es débil", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("CMD_API_SHOW_ALL_USERS", 200, "<html><head><title>DirectAdmin Login</title></head></html>")]
    [DataRow("CMD_API_SHOW_ALL_USERS", 502, "")]
    [DataRow("CMD_API_SHOW_ALL_USERS", 200, "Bad Gateway")]
    [DataRow("CMD_API_SHOW_ALL_USERS", 200, "error=1&text=Cannot%20Execute%20Your%20Request&details=Busy")]
    [DataRow("CMD_API_SHOW_USER_CONFIG", 200, "domain=example.test&package=basico")]
    public async Task UnaLecturaQueNoEsDeDirectAdmin_EsUpstreamUnavailable(string command, int status, string text)
    {
        var directAdmin = new FakeDirectAdmin().With("alice").On(command, status, text);
        await using var host = await Start(directAdmin);

        var (code, body) = await host.Op("get_status", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, code, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
    }

    [TestMethod]
    [DataRow("Bad Gateway")]
    [DataRow("nuevo.example.test=con%20espacio")]
    public async Task UnaListaDeDominiosQueNoSeLee_NoDejaCrear(string text)
    {
        var directAdmin = new FakeDirectAdmin().On("CMD_API_DOMAIN_OWNERS", 200, text);
        await using var host = await Start(directAdmin);

        var (code, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, code, body?.ToJsonString());
        Assert.IsEmpty(directAdmin.Mutations);
    }

    [TestMethod]
    public async Task Un5xxEnLaSuspension_EsSinRespuesta_YQuedaEnCursoConLaVentana()
    {
        var directAdmin = new FakeDirectAdmin().With("alice").On("CMD_API_SELECT_USERS", 502, "Bad Gateway");
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("suspend", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(ModuleResponse.PendingStatus, body!["status"]!.GetValue<string>());
        Assert.Contains("-sent-", body["operation_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnCorteDespuesDeBorrar_LaRelecturaConfirmaLaBaja()
    {
        var directAdmin = new FakeDirectAdmin().With("alice").CutAfter("CMD_API_SELECT_USERS", apply: true);
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("terminate", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task AccesoDirecto_UnaUrlDeUnSoloUso_PedidaEntrandoComoElUsuario()
    {
        var directAdmin = new FakeDirectAdmin().With("alice");
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("sso", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("https://da.example.test:2222/api/login/url?key=CLAVEUNICAINVENTADAalice", ModuleHost.Result(body, "url"));
        var call = directAdmin.Last("CMD_API_LOGIN_KEYS");
        Assert.AreEqual("POST", call.Method);
        Assert.AreEqual("admin|alice", call.User);
        Assert.AreEqual("one_time_url", call.Arg("type"));
        Assert.IsEmpty(directAdmin.Mutations);
    }

    [TestMethod]
    public async Task Uso_DiscoYTransferenciaDelMes_DeMBConDecimalesAGB()
    {
        var directAdmin = new FakeDirectAdmin().With("alice").With("alice2");
        directAdmin.Users["alice"].DiskUsed = "2048";
        directAdmin.Users["alice"].BandwidthUsed = "512.5";
        await using var host = await Start(directAdmin);

        var (status, body) = await host.Op("usage", ModuleHost.Existing("alice"), ModuleHost.Metrics());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("""{"disk_gb":"2","transfer_gb":"0.500488"}""", body!["result"]!["metrics"]!.ToJsonString());
        Assert.AreEqual("alice", directAdmin.Last("CMD_API_SHOW_USER_USAGE").Arg("user"));
        Assert.IsEmpty(directAdmin.Mutations);
    }

    [TestMethod]
    public async Task SecretosTestigo_NoAparecenEnRespuestasNiLogs()
    {
        var echo = $"Error con {ModuleHost.Password} y {FakeDirectAdmin.Key}";
        var directAdmin = new FakeDirectAdmin().With("alice")
            .On("CMD_API_ACCOUNT_USER", 200, FakeDirectAdmin.Error("Unable to Create User", echo))
            .On("CMD_API_USER_PASSWD", 200, FakeDirectAdmin.Error("Cannot Execute Your Request", echo));
        var logger = new ListLogger();
        await using var host = await Start(directAdmin, logger);

        var responses = new[]
        {
            await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials()),
            await host.Op("run_action", ModuleHost.Existing("alice"), ModuleHost.ChangePassword()),
            await host.Op("sso", ModuleHost.Existing("alice")),
            await host.Send("/v0/instance/test", []),
        };

        foreach (var (_, body) in responses)
        {
            Assert.DoesNotContain(ModuleHost.Password, body!.ToJsonString());
            Assert.DoesNotContain(FakeDirectAdmin.Key, body.ToJsonString());
        }

        Assert.Contains("***", responses[1].Body!["error"]!["details"]!["upstream"]!.GetValue<string>());
        Assert.DoesNotContain(ModuleHost.Password, logger.Text);
        Assert.DoesNotContain(FakeDirectAdmin.Key, logger.Text);
        Assert.DoesNotContain("CLAVEUNICAINVENTADA", logger.Text);
        Assert.Contains("CMD_API_ACCOUNT_USER", logger.Text);
    }
}
