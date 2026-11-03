using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Hosting.Testing;
using BillingOss.Modules.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.CPanel.Tests;

/// <summary>El módulo de cPanel contra un WHM falso: manifiesto, configuración, la forma de cada función y sus errores.</summary>
[TestClass]
public sealed class CPanelTests
{
    private static readonly string Username = Usernames.For("c", ModuleHost.Uuid);

    private static JsonObject Config() => new() { ["host"] = FakeWhm.Host, ["api_token"] = FakeWhm.Token };

    /// <summary>
    /// El plazo de las operaciones se prueba en la biblioteca con un reloj a mano. Acá el reloj no avanza, así una PC
    /// cargada no vence el plazo de una prueba que no es de plazos.
    /// </summary>
    private static Task<ModuleHost> Start(FakeWhm whm, ILogger? logger = null) =>
        ModuleHost.Start(CPanelModule.Create(_ => new HttpClient(whm, disposeHandler: false), logger ?? NullLogger.Instance, new StepClock()), Config());

    [TestMethod]
    public async Task Manifiesto_IdentidadConfiguracionYSecretos()
    {
        await using var host = await Start(new FakeWhm());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("cpanel", manifest!["id"]!.GetValue<string>());
        Assert.AreEqual("0.1.0", manifest["version"]!.GetValue<string>());
        Assert.AreEqual("Apache-2.0", manifest["license"]!.GetValue<string>());
        var config = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "host", "api_token" }, ModuleHost.Strings(config["required"]));
        Assert.AreEqual(2087, config["properties"]!["port"]!["default"]!.GetValue<int>());
        Assert.AreEqual("root", config["properties"]!["api_user"]!["default"]!.GetValue<string>());
        foreach (var (name, property) in config["properties"]!.AsObject())
        {
            Assert.AreEqual(name == "api_token", property!["x-secret"] is not null, name);
        }

        Assert.Contains("provisioning.sso", manifest["capabilities"]!.ToJsonString());
        Assert.Contains("provisioning.usage", manifest["capabilities"]!.ToJsonString());
    }

    [TestMethod]
    public async Task Test_LlamaALaApi1ConElToken_YDevuelveLosPaquetes()
    {
        var whm = new FakeWhm();
        await using var host = await Start(whm);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(FakeWhm.Packages, ModuleHost.Strings(body!["result"]!["plans"]));
        var call = whm.Calls.Single();
        Assert.AreEqual("POST", call.Method);
        Assert.AreEqual("listpkgs", call.Function);
        Assert.AreEqual("?api.version=1", call.Query);
        Assert.AreEqual($"whm root:{FakeWhm.Token}", call.Authorization);
    }

    [TestMethod]
    [DataRow("host", "https://whm.example.test")]
    [DataRow("port", 70000)]
    [DataRow("tls_fingerprint", "no-es-una-huella")]
    [DataRow("api_user", "root:otro")]
    [DataRow("api_token", "con espacio")]
    [DataRow("api_token", "")]
    [DataRow("api_token", "tókenconacento")]
    [DataRow("username_prefix", "test")]
    [DataRow("username_prefix", "Mayu")]
    [DataRow("username_prefix", "largo")]
    public async Task ConfiguracionQueNoSirve_EsInvalidConfig_SinSalirALaRed(string field, object value)
    {
        var whm = new FakeWhm().With("alice");
        await using var host = await Start(whm);
        var config = Config();
        config[field] = JsonValue.Create(value);

        var (status, body) = await host.Op("get_status", ModuleHost.Existing("alice"), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        CollectionAssert.Contains(ModuleHost.Strings(body!["error"]!["details"]!["fields"]), field);
        Assert.IsEmpty(whm.Calls);
    }

    [TestMethod]
    [DataRow(403, """{"cpanelresult":{"error":"Access denied","data":{"result":"0"}}}""")]
    [DataRow(200, """{"metadata":{"result":0,"reason":"You do not have permission to read the account list."}}""")]
    public async Task TokenOPrivilegioRechazado_EsInvalidConfigLegible(int status, string text)
    {
        var whm = new FakeWhm().On("listpkgs", status, text);
        await using var host = await Start(whm);

        var (code, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, code);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.Contains("privilegios", body!["error"]!["message"]!.GetValue<string>());
        Assert.IsNotNull(body["error"]!["details"]!["upstream"]);
    }

    [TestMethod]
    public async Task CicloCompleto_ConLasFuncionesDeWhm()
    {
        var whm = new FakeWhm();
        await using var host = await Start(whm);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Username, ModuleHost.Username(body));
        var create = whm.Last("createacct");
        Assert.AreEqual(Username, create.Arg("username"));
        Assert.AreEqual("nuevo.example.test", create.Arg("domain"));
        Assert.AreEqual("basico", create.Arg("plan"));
        Assert.AreEqual(ModuleHost.Password, create.Arg("password"));
        Assert.AreEqual("cliente@example.com", create.Arg("contactemail"));
        Assert.DoesNotContain(ModuleHost.Password, create.Query, "La contraseña va en el cuerpo, nunca en la URL.");
        CollectionAssert.AreEqual(new[] { "listaccts", "getdomainowner", "createacct", "listaccts" }, whm.Functions);
        Assert.AreEqual("exact", whm.Calls[0].Arg("searchmethod"));

        var context = ModuleHost.Existing(Username, "nuevo.example.test");
        (_, body) = await host.Op("suspend", context);
        Assert.AreEqual("suspended", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual(Username, whm.Last("suspendacct").Arg("user"));

        (_, body) = await host.Op("unsuspend", context);
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));

        (_, body) = await host.Op("change_plan", ModuleHost.Existing(Username, "nuevo.example.test", plan: "Plan Empresa"));
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("Plan Empresa", whm.Last("changepackage").Arg("pkg"));
        Assert.AreEqual("Plan Empresa", whm.Accounts[Username].Plan);

        (status, _) = await host.Op("run_action", context, ModuleHost.ChangePassword("Nueva-Contrasena_99"));
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("Nueva-Contrasena_99", whm.Accounts[Username].Password);

        (_, body) = await host.Op("get_status", context);
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("Plan Empresa", body!["result"]!["details"]!["plan"]!.GetValue<string>());

        (_, body) = await host.Op("view", context);
        Assert.AreEqual("https://whm.example.test:2083/", body!["result"]!["blocks"]![2]!["url"]!.GetValue<string>());

        (status, body) = await host.Op("terminate", context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Username, whm.Last("removeacct").Arg("username"));
        (_, body) = await host.Op("get_status", context);
        Assert.AreEqual("not_found", ModuleHost.Result(body, "observed_state"));

        CollectionAssert.AreEqual(new[] { "createacct", "suspendacct", "unsuspendacct", "changepackage", "passwd", "removeacct" }, whm.Mutations);
    }

    [TestMethod]
    public async Task ConsultaPrevia_ElDominioEnOtraCuenta_NoLlegaACreateacct()
    {
        var whm = new FakeWhm().With("otro", "nuevo.example.test");
        await using var host = await Start(whm);

        var (_, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual("domain_taken", ModuleHost.Reason(body));
        Assert.AreEqual("nuevo.example.test", whm.Last("getdomainowner").Arg("domain"));
        Assert.IsEmpty(whm.Mutations);
    }

    [TestMethod]
    public async Task CreateacctRechazado_EsRejectedConElTextoDeWhm_SinLaContrasena()
    {
        var whm = new FakeWhm().On("createacct", 200, FakeWhm.Result("createacct", 0, $"La contraseña {ModuleHost.Password} es débil"));
        await using var host = await Start(whm);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("upstream_rejected", ModuleHost.Reason(body));
        Assert.AreEqual("La contraseña *** es débil", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(200, "<html>Mantenimiento</html>")]
    [DataRow(502, "")]
    [DataRow(200, """{"metadata":{"result":1},"data":{"acct":"no-es-lista"}}""")]
    public async Task UnaLecturaQueNoEsDeWhm_EsUpstreamUnavailable(int status, string text)
    {
        var whm = new FakeWhm().With("alice").On("listaccts", status, text);
        await using var host = await Start(whm);

        var (code, body) = await host.Op("get_status", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, code, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task Un5xxEnLaSuspension_EsSinRespuesta_YQuedaEnCursoConLaVentana()
    {
        var whm = new FakeWhm().With("alice").On("suspendacct", 502, "Bad Gateway");
        await using var host = await Start(whm);

        var (status, body) = await host.Op("suspend", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(ModuleResponse.PendingStatus, body!["status"]!.GetValue<string>());
        Assert.Contains("-sent-", body["operation_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnCorteDespuesDeBorrar_LaRelecturaConfirmaLaBaja()
    {
        var whm = new FakeWhm().With("alice").CutAfter("removeacct", apply: true);
        await using var host = await Start(whm);

        var (status, body) = await host.Op("terminate", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task AccesoDirecto_UsaCreateUserSession_DeCpanel()
    {
        var whm = new FakeWhm().With("alice");
        await using var host = await Start(whm);

        var (status, body) = await host.Op("sso", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.StartsWith("https://whm.example.test:2083/cpsess0123456789/login/", ModuleHost.Result(body, "url")!);
        Assert.AreEqual("2026-10-29T00:00:00Z", ModuleHost.Result(body, "expires_at"));
        Assert.AreEqual("cpaneld", whm.Last("create_user_session").Arg("service"));
        Assert.IsEmpty(whm.Mutations);
    }

    [TestMethod]
    public async Task Uso_DiscoDeListaccts_YTransferenciaDelMesDeShowbw_EnGB()
    {
        var whm = new FakeWhm().With("alice").With("alice2");
        whm.Accounts["alice"].DiskUsed = "1536M";
        whm.Accounts["alice"].BandwidthBytes = 3L * 1024 * 1024 * 1024;
        await using var host = await Start(whm);

        var (status, body) = await host.Op("usage", ModuleHost.Existing("alice"), ModuleHost.Metrics());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("""{"disk_gb":"1.5","transfer_gb":"3"}""", body!["result"]!["metrics"]!.ToJsonString());
        Assert.AreEqual("alice", whm.Last("showbw").Arg("search"));
        Assert.IsEmpty(whm.Mutations);
    }

    [TestMethod]
    public async Task Uso_ConUnaTransferenciaQueNoSeLee_SoloVaElDisco_YConShowbwCaido_NoHayLectura()
    {
        var whm = new FakeWhm().With("alice")
            .On("showbw", 200, FakeWhm.Result("showbw", 1, "OK", new JsonObject { ["acct"] = new JsonArray(new JsonObject { ["user"] = "alice", ["totalbytes"] = "mucho" }) }));
        await using var host = await Start(whm);

        // Una medida que no se puede leer no va: el núcleo no guarda muestra de ese día.
        var (status, body) = await host.Op("usage", ModuleHost.Existing("alice"), ModuleHost.Metrics());
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("""{"disk_gb":"0.117188"}""", body!["result"]!["metrics"]!.ToJsonString());

        // Un error de WHM no es una lectura: el núcleo reintenta.
        whm.On("showbw", 200, FakeWhm.Result("showbw", 0, "Unable to read bandwidth data"));
        (status, body) = await host.Op("usage", ModuleHost.Existing("alice"), ModuleHost.Metrics());
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status, body?.ToJsonString());
        Assert.AreEqual("upstream_error", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task SecretosTestigo_NoAparecenEnRespuestasNiLogs()
    {
        var echo = $"Error con {ModuleHost.Password} y {FakeWhm.Token}";
        var whm = new FakeWhm().With("alice")
            .On("createacct", 200, FakeWhm.Result("createacct", 0, echo))
            .On("passwd", 200, FakeWhm.Result("passwd", 0, echo));
        var logger = new ListLogger();
        await using var host = await Start(whm, logger);

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
            Assert.DoesNotContain(FakeWhm.Token, body.ToJsonString());
        }

        Assert.DoesNotContain(ModuleHost.Password, logger.Text);
        Assert.DoesNotContain(FakeWhm.Token, logger.Text);
        Assert.DoesNotContain("SESIONINVENTADA", logger.Text);
        Assert.Contains("createacct", logger.Text);
    }
}
