using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Hestia.Tests;

/// <summary>validate, change_plan, change_password, get_status, view y la consulta de operaciones largas.</summary>
[TestClass]
public sealed class OperationTests
{
    private static readonly JsonObject ChangePassword = new()
    {
        ["action"] = "change_password",
        ["credentials"] = new JsonObject { ["password"] = "Otra-Clave_Inventada.9" },
    };

    // ---- validate ----

    [TestMethod]
    public async Task Validar_UnPedidoBueno_CompletaSinCambiarNada()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());

        var (status, _) = await host.Op("validate", HestiaHost.NewService());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsEmpty(host.Hestia.Mutations);
        CollectionAssert.AreEqual(new[] { "v-list-user-packages", "v-search-domain-owner" }, host.Hestia.Commands);
    }

    [TestMethod]
    public async Task Validar_UnDominioYaCargado_EsInvalidInputDelDominio()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount(domain: "nuevo.example.test"));

        var (status, body) = await host.Op("validate", HestiaHost.NewService(domain: "Nuevo.Example.Test"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, HestiaHost.Code(body));
        Assert.AreEqual("domain_taken", HestiaHost.Reason(body));
        CollectionAssert.AreEqual(new[] { "domain" }, HestiaHost.Strings(body!["error"]!["details"]!["fields"]));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Error: nuevo.example.test doesn't exist")]
    public async Task Validar_UnDominioLibre_Con404SinTextoOConElTextoViejo_EsValido(string text)
    {
        // v-search-domain-owner sin dueño termina con E_NOTEXIST sin imprimir nada, y la API lo pasa a 404. La
        // Hestia falsa contesta así por defecto. Algunas versiones mandan además un texto.
        await using var host = await HestiaHost.Start(new FakeHestia().On("v-search-domain-owner", 404, text));

        var (status, body) = await host.Op("validate", HestiaHost.NewService());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
    }

    [TestMethod]
    public async Task Validar_UnPaqueteInexistente_EsInvalidConfig()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (_, body) = await host.Op("validate", HestiaHost.NewService(package: "inexistente"));

        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(body));
        CollectionAssert.AreEqual(new[] { "package" }, HestiaHost.Strings(body!["error"]!["details"]!["fields"]));
    }

    [TestMethod]
    [DataRow("sin-punto")]
    [DataRow("con espacio.com")]
    [DataRow("-guion.example.test")]
    [DataRow("")]
    public async Task Validar_UnDominioMalFormado_EsInvalidInput_SinSalirAHestia(string domain)
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (_, body) = await host.Op("validate", HestiaHost.NewService(domain: domain));

        Assert.AreEqual(ErrorCodes.InvalidInput, HestiaHost.Code(body));
        Assert.IsEmpty(host.Hestia.Calls);
    }

    [TestMethod]
    public async Task Validar_UnDominioConEnie_SeMandaEnPunycode()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (status, _) = await host.Op("validate", HestiaHost.NewService(domain: "señal.example.test"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        CollectionAssert.AreEqual(new[] { "xn--seal-hqa.example.test" }, host.Hestia.Calls.Single(c => c.Command == "v-search-domain-owner").Args);
    }

    // ---- change_plan ----

    [TestMethod]
    public async Task CambioDePlan_DejaElPaqueteNuevo_YRepetirloNoMuta()
    {
        var hestia = new FakeHestia().WithAccount(package: "freetier");
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("change_plan", HestiaHost.Existing(package: "gainsboro"));
        var again = await host.Op("change_plan", HestiaHost.Existing(package: "gainsboro"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("active", HestiaHost.Result(body, "observed_state"));
        Assert.AreEqual(HttpStatusCode.OK, again.Status);
        Assert.AreEqual("gainsboro", hestia.Users["alice"].Package);
        Assert.AreEqual(1, hestia.Count("v-change-user-package"));
        CollectionAssert.AreEqual(new[] { "alice", "gainsboro" }, hestia.Calls.Single(c => c.Command == "v-change-user-package").Args);

        // Hestia reaplica el shell del paquete: el resultado lo avisa.
        Assert.AreEqual("shell_changed", body!["result"]!["warnings"]![0]!["code"]!.GetValue<string>());
        Assert.IsNull(again.Body!["result"]!["warnings"]);
    }

    [TestMethod]
    public async Task CambioDePlan_QueHestiaRechaza_EsRejected_YNoDejaElServicioAMedias()
    {
        var hestia = new FakeHestia().WithAccount(package: "gainsboro").On("v-change-user-package", 400, "Error: disk usage exceeds the package quota");
        await using var host = await HestiaHost.Start(hestia);

        var (_, body) = await host.Op("change_plan", HestiaHost.Existing(package: "freetier"));

        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(body));
        Assert.AreEqual("Error: disk usage exceeds the package quota", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
        Assert.AreEqual("gainsboro", hestia.Users["alice"].Package);
    }

    // ---- change_password ----

    [TestMethod]
    public async Task CambioDeContrasena_ConOk_Completa()
    {
        var hestia = new FakeHestia().WithAccount();
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("run_action", HestiaHost.Existing(), ChangePassword);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("change_password", HestiaHost.Result(body, "action"));
        Assert.AreEqual("Otra-Clave_Inventada.9", hestia.Users["alice"].Password);
        Assert.DoesNotContain("Otra-Clave_Inventada.9", body!.ToJsonString());
    }

    [TestMethod]
    public async Task CambioDeContrasena_ConCorte_EsOutcomeUnknown_YRepetirConLaMismaEsSeguro()
    {
        var hestia = new FakeHestia().WithAccount().CutAfter("v-change-user-password", apply: true);
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("run_action", HestiaHost.Existing(), ChangePassword, key: "svc-1042-change_password-1");
        hestia.On("v-change-user-password", (_, run) => run());
        var again = await host.Op("run_action", HestiaHost.Existing(), ChangePassword, key: "svc-1042-change_password-1");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, HestiaHost.Code(body));
        Assert.AreEqual(HttpStatusCode.OK, again.Status);
        Assert.AreEqual("Otra-Clave_Inventada.9", hestia.Users["alice"].Password);
    }

    [TestMethod]
    public async Task CambioDeContrasena_Rechazada_EsRejected()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount().On("v-change-user-password", 400, "Error: invalid password format"));

        var (_, body) = await host.Op("run_action", HestiaHost.Existing(), ChangePassword);

        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(body));
        Assert.AreEqual("password_rejected", HestiaHost.Reason(body));
    }

    [TestMethod]
    public async Task CambioDeContrasena_DeUnaCuentaSinElDominioDelServicio_NoSeHace()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount(domain: "otro.example.test"));

        var (_, body) = await host.Op("run_action", HestiaHost.Existing(), ChangePassword);

        Assert.AreEqual(ErrorCodes.InvalidInput, HestiaHost.Code(body));
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    // ---- get_status ----

    [TestMethod]
    public async Task Estado_ActivoSuspendidoYAusente()
    {
        var hestia = new FakeHestia().WithAccount().WithAccount(username: "bob", domain: "bob.example.test", suspended: true);
        await using var host = await HestiaHost.Start(hestia);

        var active = await host.Op("get_status", HestiaHost.Existing());
        var suspended = await host.Op("get_status", HestiaHost.Existing(username: "bob", domain: "bob.example.test"));
        var absent = await host.Op("get_status", HestiaHost.Existing(username: "carla", domain: "carla.example.test"));

        Assert.AreEqual("active", HestiaHost.Result(active.Body, "observed_state"));
        Assert.AreEqual("suspended", HestiaHost.Result(suspended.Body, "observed_state"));
        Assert.AreEqual("not_found", HestiaHost.Result(absent.Body, "observed_state"));

        var details = active.Body!["result"]!["details"]!;
        Assert.AreEqual("gainsboro", details["package"]!.GetValue<string>());
        Assert.IsFalse(details["suspended"]!.GetValue<bool>());
        Assert.AreEqual("120", details["disk"]!["used_mb"]!.GetValue<string>());
        Assert.AreEqual("unlimited", details["bandwidth"]!["limit_mb"]!.GetValue<string>());
        Assert.AreEqual(1, details["resources"]!["cron"]!["count"]!.GetValue<int>());
        Assert.IsEmpty(hestia.Mutations);
    }

    [TestMethod]
    public async Task Estado_ParcialODominioAjeno_EsUnknownConLaRazon()
    {
        var hestia = new FakeHestia().WithAccount().WithAccount(username: "bob", domain: "bob.example.test");
        hestia.Users["alice"].Resources["databases"]["alice_db"] = true;
        await using var host = await HestiaHost.Start(hestia);

        var partial = await host.Op("get_status", HestiaHost.Existing());
        var mismatch = await host.Op("get_status", HestiaHost.Existing(username: "bob", domain: "example.test"));

        Assert.AreEqual("unknown", HestiaHost.Result(partial.Body, "observed_state"));
        Assert.AreEqual("partial", partial.Body!["result"]!["details"]!["reason"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "databases" }, HestiaHost.Strings(partial.Body["result"]!["details"]!["mixed"]));
        Assert.AreEqual("unknown", HestiaHost.Result(mismatch.Body, "observed_state"));
        Assert.AreEqual("domain_mismatch", mismatch.Body!["result"]!["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Estado_SinPoderProbarLaAusencia_EsUnknown_NoNotFound()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().On("v-list-users", 200, "{}"));

        var (_, body) = await host.Op("get_status", HestiaHost.Existing());

        Assert.AreEqual("unknown", HestiaHost.Result(body, "observed_state"));
        Assert.AreEqual("absence_unproven", body!["result"]!["details"]!["reason"]!.GetValue<string>());
    }

    // ---- view ----

    [TestMethod]
    public async Task Vista_MuestraLosCincoCamposYElEnlaceAlPanel_SinContrasenas()
    {
        var hestia = new FakeHestia().WithAccount();
        hestia.Users["alice"].Password = HestiaHost.Password;
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("view", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        var blocks = body!["result"]!["blocks"]!.AsArray();
        Assert.AreEqual("status", blocks[0]!["type"]!.GetValue<string>());
        // La misma forma que los demás módulos y el módulo manual del núcleo: el color en level y las listas en items.
        Assert.AreEqual("ok", blocks[0]!["level"]!.GetValue<string>());
        var actions = blocks.Single(b => b!["type"]!.GetValue<string>() == "actions")!["items"]!.AsArray();
        Assert.AreEqual("change_password", actions.Single()!.GetValue<string>());
        var fields = blocks.Single(b => b!["type"]!.GetValue<string>() == "fields")!["items"]!.AsArray();
        Assert.HasCount(5, fields);
        Assert.AreEqual("alice", fields[0]!["value"]!.GetValue<string>());
        Assert.AreEqual("120 MB de 1000 MB", fields[3]!["value"]!.GetValue<string>());
        Assert.AreEqual("35 MB de ilimitado", fields[4]!["value"]!.GetValue<string>());
        var link = blocks.Single(b => b!["type"]!.GetValue<string>() == "link")!;
        Assert.AreEqual("https://panel.example.test:8083/login/", link["url"]!.GetValue<string>());
        Assert.AreEqual("Entrar al panel", link["label"]!.GetValue<string>());

        var text = body.ToJsonString();
        Assert.DoesNotContain(HestiaHost.Password, text);
        Assert.DoesNotContain(FakeHestia.SecretKey, text);
        Assert.DoesNotContain("webmail", text, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Vista_DeUnaCuentaParcial_LlevaUnAviso_YEnIngles()
    {
        var hestia = new FakeHestia().WithAccount(suspended: true);
        hestia.Users["alice"].Resources["cron"]["1"] = false;
        await using var host = await HestiaHost.Start(hestia);

        var (_, body) = await host.Op("view", HestiaHost.Existing(language: "en"));

        var blocks = body!["result"]!["blocks"]!.AsArray();
        Assert.AreEqual("Under review", blocks[0]!["text"]!.GetValue<string>());
        Assert.AreEqual("notice", blocks[^1]!["type"]!.GetValue<string>());
        Assert.IsFalse(blocks.Any(b => b!["type"]!.GetValue<string>() == "actions"));
    }

    // ---- Operaciones largas ----

    [TestMethod]
    public async Task SinTiempoParaElMutador_LaSuspensionQuedaPending_YLaConsultaLaTermina()
    {
        // Cada comando tarda 10 segundos y la suspensión tiene 80: después de leer el estado no queda tiempo para mutar.
        var clock = new StepClock();
        var hestia = new FakeHestia { Clock = clock, Step = TimeSpan.FromSeconds(10) }.WithAccount();
        await using var slow = await HestiaHost.Start(hestia, time: clock);
        await using var other = await HestiaHost.Start(hestia);

        var (status, body) = await slow.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.AreEqual("suspend-svc1042", body["operation_id"]!.GetValue<string>());
        Assert.IsEmpty(hestia.Mutations);

        // La misma clave en la réplica lenta devuelve la misma operación en curso.
        var repeated = await slow.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");
        Assert.AreEqual(body.ToJsonString(), repeated.Body!.ToJsonString());

        hestia.Step = TimeSpan.Zero;
        (status, body) = await other.Send("/v0/operations/suspend-svc1042", [], context: HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
        Assert.AreEqual(1, hestia.Count("v-suspend-user"));
    }

    [TestMethod]
    [DataRow("suspend-svc9999")]
    [DataRow("reboot-svc1042")]
    [DataRow("op-77f2")]
    public async Task ConsultaDeUnaOperacionQueNoEsDelServicio_EsNotFound(string id)
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());

        var (status, body) = await host.Send($"/v0/operations/{id}", [], context: HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, HestiaHost.Code(body));
        Assert.IsEmpty(host.Hestia.Calls);
    }
}
