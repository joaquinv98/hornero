using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Hestia.Tests;

/// <summary>Manifiesto, firma, configuración, campos desconocidos y secretos (escenarios 1, 2, 6 y 7 de la conformidad).</summary>
[TestClass]
public sealed class ModuleTests
{
    [TestMethod]
    public async Task Manifiesto_DeclaraCapacidadesEsquemasYSecretos_SinSshNiIp()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("hestia", manifest!["id"]!.GetValue<string>());
        Assert.AreEqual("0.1.0", manifest["version"]!.GetValue<string>());
        Assert.AreEqual("Apache-2.0", manifest["license"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "0" }, HestiaHost.Strings(manifest["protocol"]));
        CollectionAssert.AreEqual(new[] { "provisioning" }, HestiaHost.Strings(manifest["kinds"]));
        CollectionAssert.AreEqual(
            new[] { "provisioning.core", "provisioning.change_plan", "provisioning.status", "provisioning.actions", "provisioning.view" },
            HestiaHost.Strings(manifest["capabilities"]));

        var config = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "host", "access_key_id", "secret_key" }, HestiaHost.Strings(config["required"]));
        Assert.IsTrue(config["properties"]!["secret_key"]!["x-secret"]!.GetValue<bool>());
        Assert.AreEqual(8083, config["properties"]!["port"]!["default"]!.GetValue<int>());
        foreach (var (name, property) in config["properties"]!.AsObject())
        {
            Assert.AreEqual(name == "secret_key", property!["x-secret"] is not null, name);
        }

        CollectionAssert.AreEqual(new[] { "package" }, HestiaHost.Strings(manifest["product_schema"]!["required"]));
        CollectionAssert.AreEqual(new[] { "domain" }, HestiaHost.Strings(manifest["order_schema"]!["required"]));
        CollectionAssert.AreEqual(new[] { "password" }, HestiaHost.Strings(manifest["credentials"]));
        CollectionAssert.AreEqual(new[] { "account.contact" }, HestiaHost.Strings(manifest["data_scopes"]));
        Assert.AreEqual(2, manifest["max_in_flight"]!.GetValue<int>());

        var action = manifest["actions"]![0]!;
        Assert.AreEqual("change_password", action["id"]!.GetValue<string>());
        Assert.IsTrue(action["confirm"]!.GetValue<bool>());
        CollectionAssert.AreEqual(new[] { "staff", "client" }, HestiaHost.Strings(action["audience"]));
        CollectionAssert.AreEqual(new[] { "password" }, HestiaHost.Strings(action["credentials"]));
        Assert.IsNotNull(action["label"]!["es"]);
        Assert.IsNotNull(action["label"]!["en"]);

        // Cada operación declarada tiene su espera, y ninguna pasa el máximo de 120 segundos del protocolo.
        var timeouts = manifest["timeouts"]!.AsObject();
        foreach (var operation in new[] { "validate", "create", "suspend", "unsuspend", "terminate", "change_plan", "get_status", "run_action", "view" })
        {
            Assert.IsTrue(timeouts[operation]!.GetValue<int>() is > 0 and <= 120, operation);
        }

        // Las opciones SSH Access e IP Address del módulo anterior no se portan, y no hay sso.
        var text = manifest.ToJsonString();
        Assert.DoesNotContain("ssh", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ip", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provisioning.sso", text);
    }

    [TestMethod]
    public async Task CadaOperacionDeclarada_EstaPublicada()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        // Sin external_ids contestan invalid_input y no not_supported, y no salen a Hestia.
        var empty = new JsonObject { ["service"] = new JsonObject { ["id"] = 1 }, ["account"] = new JsonObject() };
        foreach (var operation in new[] { "suspend", "unsuspend", "terminate", "change_plan", "get_status", "view" })
        {
            var (status, body) = await host.Op(operation, empty);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, operation);
            Assert.IsTrue(HestiaHost.Code(body) is ErrorCodes.InvalidInput or ErrorCodes.InvalidConfig, operation);
        }

        var (_, create) = await host.Op("create", empty, HestiaHost.Credentials());
        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(create));
        var (_, validate) = await host.Op("validate", empty);
        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(validate));
        var (_, action) = await host.Op("run_action", empty, new JsonObject { ["action"] = "reboot" });
        Assert.AreEqual(ErrorCodes.NotSupported, HestiaHost.Code(action));
        Assert.IsEmpty(host.Hestia.Calls);
    }

    [TestMethod]
    public async Task PedidoSinFirmaValida_EsInvalidSignature()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v0/provisioning/get_status") { Content = new StringContent("{}") };
        request.Headers.Add("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Signature", "v0=00");

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(host.Hestia.Calls);
    }

    [TestMethod]
    public async Task CamposDesconocidos_SeIgnoran()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());
        var context = HestiaHost.Existing();
        context["algo_nuevo"] = new JsonObject { ["x"] = 1 };
        context["service"]!["campo_futuro"] = "valor";
        var config = HestiaHost.Config();
        config["opcion_futura"] = true;

        var (status, body) = await host.Op("get_status", context, new JsonObject { ["extra"] = 1 }, config: config);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("active", HestiaHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task Test_PruebaLaAccessKey_YDevuelveLosPaquetes()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        CollectionAssert.Contains(HestiaHost.Strings(body!["result"]!["packages"]), "freetier");
        CollectionAssert.AreEqual(new[] { "v-list-users", "v-list-user-packages" }, host.Hestia.Commands);

        // Mismo pedido que documenta Hestia: hash ACCESS_KEY:SECRET_KEY, returncode no y el formato json como último argumento.
        var call = host.Hestia.Calls[0];
        Assert.AreEqual($"{FakeHestia.AccessKeyId}:{FakeHestia.SecretKey}", call.Hash);
        Assert.AreEqual("no", call.ReturnCode);
        CollectionAssert.AreEqual(new[] { "json" }, call.Args);
    }

    [TestMethod]
    [DataRow(401, "Error: authentication failed")]
    [DataRow(200, "Error: IP is not allowed to connect with API")]
    [DataRow(403, "Error: permission denied for this access key")]
    public async Task Test_ConLaAccessKeyRechazada_OLaIpNoPermitida_EsInvalidConfigLegible(int status, string text)
    {
        await using var host = await HestiaHost.Start(new FakeHestia().On("v-list-users", status, text));

        var (code, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, code);
        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.Contains("IPs permitidas", body["error"]!["message"]!.GetValue<string>());
        Assert.AreEqual(text, body["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("host", "https://panel.example.test")]
    [DataRow("host", "panel.example.test/api")]
    [DataRow("port", 0)]
    [DataRow("tls_fingerprint", "no-es-una-huella")]
    [DataRow("access_key_id", "con:dos-puntos")]
    [DataRow("secret_key", "con espacio")]
    [DataRow("secret_key", "")]
    [DataRow("default_language", "español")]
    [DataRow("username_prefix", "Con-Mayuscula")]
    public async Task ConfiguracionQueNoSirve_EsInvalidConfig_SinSalirALaRed(string field, object value)
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());
        var config = HestiaHost.Config();
        config[field] = JsonValue.Create(value);

        var (status, body) = await host.Op("get_status", HestiaHost.Existing(), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(body));
        CollectionAssert.Contains(HestiaHost.Strings(body!["error"]!["details"]!["fields"]), field);
        Assert.IsEmpty(host.Hestia.Calls);
    }

    [TestMethod]
    public async Task ConfiguracionConUnTipoMalo_EsInvalidConfig()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());
        var config = HestiaHost.Config();
        config["host"] = 12345;

        var (_, body) = await host.Op("get_status", HestiaHost.Existing(), config: config);

        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(body));
    }

    [TestMethod]
    public async Task SecretosTestigo_NoAparecenEnRespuestasErroresNiLogs()
    {
        // Hestia repite la contraseña y la secret key en sus errores: el módulo los tapa.
        var echo = $"Error: bad {HestiaHost.Password} for {FakeHestia.SecretKey}";
        var hestia = new FakeHestia()
            .On("v-add-user", 400, echo)
            .On("v-change-user-password", 400, echo);
        hestia.WithAccount();
        var logger = new ListLogger();
        await using var host = await HestiaHost.Start(hestia, logger: logger);

        var responses = new[]
        {
            await host.Op("create", HestiaHost.NewService(), HestiaHost.Credentials()),
            await host.Op("run_action", HestiaHost.Existing(), new JsonObject { ["action"] = "change_password", ["credentials"] = new JsonObject { ["password"] = HestiaHost.Password } }),
            await host.Op("view", HestiaHost.Existing()),
            await host.Op("get_status", HestiaHost.Existing()),
            await host.Send("/v0/instance/test", []),
        };

        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(responses[0].Body));
        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(responses[1].Body));
        foreach (var (_, body) in responses)
        {
            var text = body!.ToJsonString();
            Assert.DoesNotContain(HestiaHost.Password, text);
            Assert.DoesNotContain(FakeHestia.SecretKey, text);
        }

        Assert.Contains("***", responses[0].Body!.ToJsonString());
        Assert.DoesNotContain(HestiaHost.Password, logger.Text);
        Assert.DoesNotContain(FakeHestia.SecretKey, logger.Text);
        Assert.Contains("v-add-user", logger.Text);
    }
}
