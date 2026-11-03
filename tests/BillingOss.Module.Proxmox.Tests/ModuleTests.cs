using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>Manifiesto, configuración de la instancia, instance/test y validate.</summary>
[TestClass]
public sealed class ModuleTests
{
    [TestMethod]
    public async Task Manifiesto_DeclaraCapacidadesAccionesCredencialesYSecretos()
    {
        await using var host = await ModuleHost.Start();

        var manifest = await host.Manifest();

        Assert.AreEqual("proxmox", manifest["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "0" }, Strings(manifest["protocol"]));
        CollectionAssert.AreEqual(new[] { "provisioning" }, Strings(manifest["kinds"]));
        CollectionAssert.AreEqual(
            new[] { "provisioning.core", "provisioning.status", "provisioning.actions", "provisioning.view", "provisioning.sso" },
            Strings(manifest["capabilities"]));
        CollectionAssert.AreEqual(new[] { "guest_password" }, Strings(manifest["credentials"]));
        Assert.IsEmpty(manifest["data_scopes"]!.AsArray());
        Assert.AreEqual(4, manifest["max_in_flight"]!.GetValue<int>());
        Assert.AreEqual(120, manifest["timeouts"]!["create"]!.GetValue<int>());
        Assert.IsTrue(manifest["timeouts"]!.AsObject().All(t => t.Value!.GetValue<int>() <= 120));

        var config = manifest["config_schema"]!;
        Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", config["$schema"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(new[] { "api_url", "token_id", "token_secret" }, Strings(config["required"]));
        var secrets = config["properties"]!.AsObject().Where(p => p.Value!["x-secret"]?.GetValue<bool>() == true).Select(p => p.Key).ToArray();
        CollectionAssert.AreEquivalent(new[] { "token_secret", "console_secret" }, secrets);

        var product = manifest["product_schema"]!["properties"]!.AsObject();
        var options = product.Where(p => p.Value!["x-client-option"]?.GetValue<bool>() == true).Select(p => p.Key).ToArray();
        CollectionAssert.AreEquivalent(new[] { "template_id", "cores", "memory_mb", "disk_gb", "rate_mb_s" }, options);

        var actions = manifest["actions"]!.AsArray().ToDictionary(a => a!["id"]!.GetValue<string>(), a => a!);
        CollectionAssert.AreEquivalent(new[] { "start", "stop", "reboot", "reinstall", "revoke_console" }, actions.Keys.ToArray());
        foreach (var id in new[] { "start", "stop", "reboot", "reinstall" })
        {
            Assert.IsTrue(actions[id]["confirm"]!.GetValue<bool>(), id);
            CollectionAssert.AreEquivalent(new[] { "staff", "client" }, Strings(actions[id]["audience"]), id);
            Assert.IsFalse(string.IsNullOrEmpty(actions[id]["label"]!["es"]!.GetValue<string>()), id);
            Assert.IsFalse(string.IsNullOrEmpty(actions[id]["label"]!["en"]!.GetValue<string>()), id);
        }

        CollectionAssert.AreEqual(new[] { "guest_password" }, Strings(actions["reinstall"]["credentials"]));
        CollectionAssert.AreEqual(new[] { "staff" }, Strings(actions["revoke_console"]["audience"]));
    }

    [TestMethod]
    public async Task CadaOperacionDeclarada_EstaPublicada()
    {
        await using var host = await ModuleHost.Start();
        var context = ModuleHost.Context();
        context["service"]!["params"] = new JsonObject();

        // Con los parámetros vacíos contestan invalid_input y no not_supported.
        foreach (var operation in new[] { "validate", "create", "get_status", "view", "sso", "run_action", "suspend", "unsuspend", "terminate" })
        {
            var (status, body) = await host.Send($"/v0/provisioning/{operation}", [], context, key: $"k-{operation}");
            Assert.AreNotEqual(HttpStatusCode.NotImplemented, status, operation);
            Assert.AreNotEqual(ErrorCodes.NotSupported, ModuleHost.Code(body), operation);
        }
    }

    [TestMethod]
    public async Task Test_ConTokenValido_EsCompleted_YNoDevuelveSecretos()
    {
        await using var host = await ModuleHost.Start();

        var (status, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.IsGreaterThanOrEqualTo(2, ModuleHost.Result(body)["vms"]!.GetValue<int>());
        Assert.IsEmpty(ModuleHost.Result(body)["missing_privileges"]!.AsArray());
        CollectionAssert.Contains(host.Pve.Calls.ToList(), "GET /cluster/resources");
        Assert.DoesNotContain(host.Pve.Secret, body.ToJsonString());
    }

    [TestMethod]
    public async Task Test_SinVmAuditEnVms_EsInvalidConfig()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.VmAudit = false;

        var (status, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.AreEqual("vm_audit_missing", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task Test_ConLimpiezaHeredadaYSinPermisoParaLasAcl_EsInvalidConfig()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AclAllowed = false;

        var (status, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("legacy_acl_permissions", ModuleHost.Reason(body));

        var config = host.Config();
        config["legacy_acl_cleanup"] = false;
        (status, body) = await host.Send("/v0/instance/test", [], [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
    }

    [TestMethod]
    public async Task Test_TokenRechazado_EsInvalidConfig()
    {
        await using var host = await ModuleHost.Start();
        var config = host.Config();
        config["token_secret"] = "otro-secreto-que-no-sirve";

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.AreEqual("token_rejected", ModuleHost.Reason(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Test_CertificadoNoConfiable_SeRechaza()
    {
        await using var host = await ModuleHost.Start();
        var config = host.Config();
        config.Remove("ca_pem");

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("tls_untrusted", ModuleHost.Reason(body));
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task Test_ConUnCaQueNoEsElDelServidor_SeRechaza()
    {
        await using var host = await ModuleHost.Start();
        await using var other = await FakePve.Start();
        var config = host.Config();
        config["ca_pem"] = other.CaPem;

        var (_, body) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual("tls_untrusted", ModuleHost.Reason(body));
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    [DataRow("mac", "bc:24:11:zz:00:01")]
    [DataRow("public_ip", "203.0.113")]
    [DataRow("bridge", "vmbr 2")]
    [DataRow("mtu", "9500")]
    [DataRow("private_ip", "10.20.0")]
    public async Task UnaLineaInvalidaDelPool_EsInvalidConfig_SinLlamarAProxmox(string field, string value)
    {
        await using var host = await ModuleHost.Start();
        var pool = ModuleHost.Pool();
        pool[1]![field] = field == "mtu" ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value;

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: host.Config(pool));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        CollectionAssert.Contains(body!["error"]!["details"]!["fields"]!.AsArray().Select(f => f!.GetValue<string>()).ToList(), "network_pool[1]");
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task UnaMacRepetidaEnElPool_EsInvalidConfig()
    {
        await using var host = await ModuleHost.Start();
        var pool = ModuleHost.Pool();
        pool[2]!["mac"] = pool[0]!["mac"]!.GetValue<string>().ToUpperInvariant();

        var (_, body) = await host.Send("/v0/instance/test", [], [], config: host.Config(pool));

        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:8006")]
    [DataRow("https://127.0.0.1:8006/api2/json")]
    [DataRow("https://usuario@127.0.0.1:8006")]
    [DataRow("https://127.0.0.1:8006/?x=1")]
    public async Task ApiUrlQueNoEsUnOrigenHttps_EsInvalidConfig(string url)
    {
        await using var host = await ModuleHost.Start();
        var config = host.Config();
        config["api_url"] = url;

        var (_, body) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task Validate_WindowsCon10GB_DaInvalidInputConElMinimo()
    {
        await using var host = await ModuleHost.Start();

        var (status, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = ModuleHost.Params(9002, disk: 10, cores: 2, memory: 3072) }, []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        var fields = body!["error"]!["details"]!["fields"]!;
        Assert.AreEqual("os_minimum", fields["disk_gb"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(40, fields["disk_gb"]!["min"]!.GetValue<int>());
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task Validate_WindowsCon2NucleosY3072MB_Pasa()
    {
        await using var host = await ModuleHost.Start();

        var (status, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = ModuleHost.Params(9002, disk: 40, cores: 2, memory: 3072) }, []);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsTrue(ModuleHost.Result(body)["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Validate_DiscoMenorQueElDeLaPlantilla_DaElMinimoDeLaPlantilla()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddTemplate(9005, "pve1", "rocky-10", 20);

        var (_, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = ModuleHost.Params(9005, disk: 10) }, []);

        Assert.AreEqual("template_disk", body!["error"]!["details"]!["fields"]!["disk_gb"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(20, body["error"]!["details"]!["fields"]!["disk_gb"]!["min"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Validate_UnaVmQueNoEsPlantilla_EsInvalidInput()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(9010, "no-plantilla");

        var (_, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = ModuleHost.Params(9010) }, []);

        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        Assert.AreEqual("not_template", body!["error"]!["details"]!["fields"]!["template_id"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Validate_CambioDePlanQueCambiaLaPlantilla_SeRechaza()
    {
        await using var host = await ModuleHost.Start();
        var input = new JsonObject { ["params"] = ModuleHost.Params(9001, disk: 20), ["current_params"] = ModuleHost.Params(9002, disk: 40, cores: 2, memory: 3072) };

        var (_, body) = await host.Send("/v0/provisioning/validate", input, []);

        Assert.AreEqual("reinstall_required", body!["error"]!["details"]!["fields"]!["template_id"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Validate_BridgeDelPoolInexistenteOGrupoFaltante_EsInvalidConfig()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.Groups.Remove("vps-vmbr2");

        var (_, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = ModuleHost.Params() }, []);

        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.AreEqual("isolation_group_missing", ModuleHost.Reason(body));

        host.Pve.Bridges["pve1"].Remove("vmbr2");
        (_, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = ModuleHost.Params() }, []);
        Assert.AreEqual("bridge_missing", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task CamposDesconocidos_SeIgnoran()
    {
        await using var host = await ModuleHost.Start();
        var config = host.Config();
        config["campo_nuevo"] = "x";
        var p = ModuleHost.Params(9002, disk: 40, cores: 2, memory: 3072);
        p["otra_cosa"] = 1;

        var (status, body) = await host.Send("/v0/provisioning/validate", new JsonObject { ["params"] = p, ["extra"] = true }, [], config: config);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
    }

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
}
