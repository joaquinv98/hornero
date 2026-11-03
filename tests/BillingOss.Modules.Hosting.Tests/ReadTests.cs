using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting.Testing;
using BillingOss.Modules.Protocol;
using Microsoft.Extensions.Configuration;

namespace BillingOss.Modules.Hosting.Tests;

/// <summary>Las operaciones sin efecto: prueba de instancia, validate, get_status, view, sso y usage.</summary>
[TestClass]
public sealed class ReadTests
{
    [TestMethod]
    public async Task Test_DevuelveLosPlanes()
    {
        var panel = new FakePanel();
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(FakePanel.AllPlans, ModuleHost.Strings(body!["result"]!["plans"]));
    }

    [TestMethod]
    public async Task Validate_SinEfectos_ControlaPlanDominioYCorreo()
    {
        var panel = new FakePanel().With("otro", "ocupado.example.test");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("validate", ModuleHost.NewService());
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        (status, body) = await host.Op("validate", ModuleHost.NewService("ocupado.example.test"));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        Assert.AreEqual("domain_taken", ModuleHost.Reason(body));

        (_, body) = await host.Op("validate", ModuleHost.NewService(plan: "no-existe"));
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        CollectionAssert.AreEqual(new[] { "plan" }, ModuleHost.Strings(body!["error"]!["details"]!["fields"]));

        // El pedido de alguien que todavía no tiene cuenta no trae el correo: se controla solo si viene.
        var context = ModuleHost.NewService();
        context.Remove("account");
        (status, body) = await host.Op("validate", context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        context = ModuleHost.NewService();
        context["account"]!["email"] = "no es un correo";
        (_, body) = await host.Op("validate", context);
        CollectionAssert.AreEqual(new[] { "account.email" }, ModuleHost.Strings(body!["error"]!["details"]!["fields"]));

        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task Estado_DeCadaCaso()
    {
        var panel = new FakePanel { KeepsMarker = true }
            .With("activa", "activa.example.test", plan: "pro")
            .With("suspendida", "suspendida.example.test", suspended: true)
            .With("ajena", "otro.example.test")
            .With("marcada", "marcada.example.test", marker: "11111111-2222-4333-8444-555555555555");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        async Task<JsonNode> Status(string username, string domain)
        {
            var (status, body) = await host.Op("get_status", ModuleHost.Existing(username, domain));
            Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
            return body!["result"]!;
        }

        var active = await Status("activa", "activa.example.test");
        Assert.AreEqual("active", active["observed_state"]!.GetValue<string>());
        Assert.AreEqual("pro", active["details"]!["plan"]!.GetValue<string>());
        Assert.IsFalse(active["details"]!["suspended"]!.GetValue<bool>());

        Assert.AreEqual("suspended", (await Status("suspendida", "suspendida.example.test"))["observed_state"]!.GetValue<string>());

        var absent = await Status("nadie", "nadie.example.test");
        Assert.AreEqual("not_found", absent["observed_state"]!.GetValue<string>());
        Assert.IsEmpty(absent["details"]!.AsObject());

        var foreign = await Status("ajena", "example.test");
        Assert.AreEqual("unknown", foreign["observed_state"]!.GetValue<string>());
        Assert.AreEqual("domain_mismatch", foreign["details"]!["reason"]!.GetValue<string>());

        var marked = await Status("marcada", "marcada.example.test");
        Assert.AreEqual("unknown", marked["observed_state"]!.GetValue<string>());
        Assert.AreEqual("marker_mismatch", marked["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Estado_ConElPanelSinResponder_EsUpstreamUnavailable_NoUnEstado()
    {
        var panel = new FakePanel { FailingReads = -1 }.With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("get_status", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Vista_Activa_ConDatosEnlaceYAccion()
    {
        var panel = new FakePanel().With("alice", plan: "pro");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("view", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var blocks = body!["result"]!["blocks"]!.AsArray();
        // La forma común de los bloques, la del módulo manual del núcleo y los demás módulos: el color en level y las
        // listas de fields y de actions en items.
        Assert.AreEqual("""{"type":"status","level":"ok","text":"Activo"}""", blocks[0]!.ToJsonString());
        var items = blocks[1]!["items"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "Usuario del panel", "Dominio", "Plan" }, items.Select(i => i!["label"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "alice", "example.test", "pro" }, items.Select(i => i!["value"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("link", blocks[2]!["type"]!.GetValue<string>());
        Assert.AreEqual("https://panel.example.test:2083/", blocks[2]!["url"]!.GetValue<string>());
        Assert.AreEqual("""{"type":"actions","items":["change_password"]}""", blocks[3]!.ToJsonString());
        Assert.DoesNotContain(ModuleHost.Password, body.ToJsonString());
    }

    [TestMethod]
    public async Task Vista_EnIngles_Suspendida_SinAcciones()
    {
        var panel = new FakePanel().With("alice", suspended: true);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("view", ModuleHost.Existing("alice", language: "en-US"));

        var blocks = body!["result"]!["blocks"]!.AsArray();
        Assert.AreEqual("""{"type":"status","level":"warning","text":"Suspended"}""", blocks[0]!.ToJsonString());
        Assert.AreEqual("Panel user", blocks[1]!["items"]![0]!["label"]!.GetValue<string>());
        Assert.AreEqual("Open the panel", blocks[2]!["label"]!.GetValue<string>());
        Assert.HasCount(3, blocks);
    }

    [TestMethod]
    public async Task Vista_DeUnaCuentaAjena_SoloEstadoYAviso()
    {
        var panel = new FakePanel().With("alice", "otro.example.test");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("view", ModuleHost.Existing("alice"));

        var blocks = body!["result"]!["blocks"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "status", "notice" }, blocks.Select(b => b!["type"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "warning", "warning" }, blocks.Select(b => b!["level"]!.GetValue<string>()).ToArray());
        Assert.DoesNotContain("otro.example.test", body.ToJsonString());

        (_, body) = await host.Op("view", ModuleHost.Existing("nadie"));
        Assert.AreEqual("La cuenta no está en el panel", body!["result"]!["blocks"]![0]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AccesoDirecto_DevuelveUrlYVencimiento_ConLaIpDelNavegador()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("sso", ModuleHost.Existing("alice"), new JsonObject { ["client_ip"] = "203.0.113.7" });

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("https://panel.example.test:2083/sesion/alice", ModuleHost.Result(body, "url"));
        Assert.AreEqual("2026-10-29T15:00:00Z", ModuleHost.Result(body, "expires_at"));
        Assert.AreEqual("203.0.113.7", panel.LastClientIp);
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task AccesoDirecto_DeUnaCuentaSuspendida_EsConflict()
    {
        var panel = new FakePanel().With("alice", suspended: true);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("sso", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual("suspended", ModuleHost.Reason(body));
        CollectionAssert.DoesNotContain(panel.Calls.ToArray(), "sso:alice");
    }

    [TestMethod]
    [DataRow("http://panel.example.test:2083/sesion")]
    [DataRow("https://otro.example.test/sesion")]
    public async Task AccesoDirecto_AOtroHostOSinHttps_NoSeDevuelve(string url)
    {
        var panel = new FakePanel { SsoUrl = new Uri(url) }.With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("sso", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.DoesNotContain("sesion", body!.ToJsonString());
    }

    [TestMethod]
    public async Task AccesoDirecto_ConUnaIpQueNoEsIp_EsInvalidInput()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("sso", ModuleHost.Existing("alice"), new JsonObject { ["client_ip"] = "no-es-ip" });

        CollectionAssert.AreEqual(new[] { "client_ip" }, ModuleHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(panel.Calls);
    }

    [TestMethod]
    public async Task Uso_DiscoYTransferenciaEnGB_ComoTextoDecimal()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("usage", ModuleHost.Existing("alice"), ModuleHost.Metrics());

        // 1536 MiB son 1,5 GB, y 120,5 MiB se redondean a 6 decimales.
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("""{"disk_gb":"1.5","transfer_gb":"0.117676"}""", body!["result"]!["metrics"]!.ToJsonString());
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task Uso_SoloLasPedidas_YLoQueNoSeDeclaraSeIgnora()
    {
        var panel = new FakePanel().With("alice", suspended: true);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        // Una cuenta suspendida también se lee: su disco sigue ocupado.
        var input = new JsonObject { ["metrics"] = new JsonArray("transfer_gb", "cpu_seconds", "transfer_gb", null) };
        var (status, body) = await host.Op("usage", ModuleHost.Existing("alice"), input);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("""{"transfer_gb":"0.117676"}""", body!["result"]!["metrics"]!.ToJsonString());
    }

    [TestMethod]
    public async Task Uso_SinLaListaDeMetricas_EsInvalidInput_SinLlamarAlPanel()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("usage", ModuleHost.Existing("alice"));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEqual(new[] { "metrics" }, ModuleHost.Strings(body!["error"]!["details"]!["fields"]));

        (_, body) = await host.Op("usage", ModuleHost.Existing("alice"), new JsonObject { ["metrics"] = new JsonArray(1) });
        CollectionAssert.AreEqual(new[] { "input.metrics[0]" }, ModuleHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(panel.Calls);
    }

    [TestMethod]
    public async Task Manifiesto_DeclaraTodasLasOperacionesDelProtocolo_ConSusEsperas()
    {
        await using var host = await ModuleHost.Start(FakePanel.Module(new FakePanel()), []);

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        CollectionAssert.AreEqual(
            new[] { "provisioning.core", "provisioning.change_plan", "provisioning.status", "provisioning.actions", "provisioning.view", "provisioning.sso", "provisioning.usage" },
            ModuleHost.Strings(manifest!["capabilities"]));
        CollectionAssert.AreEqual(new[] { "password" }, ModuleHost.Strings(manifest["credentials"]));
        CollectionAssert.AreEqual(new[] { "account.contact" }, ModuleHost.Strings(manifest["data_scopes"]));
        CollectionAssert.AreEqual(new[] { "domain" }, ModuleHost.Strings(manifest["order_schema"]!["required"]));
        CollectionAssert.AreEqual(new[] { "plan" }, ModuleHost.Strings(manifest["product_schema"]!["required"]));
        var action = manifest["actions"]![0]!;
        Assert.AreEqual("change_password", action["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "password" }, ModuleHost.Strings(action["credentials"]));
        Assert.IsTrue(action["confirm"]!.GetValue<bool>());

        var timeouts = manifest["timeouts"]!.AsObject();
        foreach (var operation in new[] { "validate", "create", "suspend", "unsuspend", "terminate", "change_plan", "get_status", "run_action", "view", "sso", "usage" })
        {
            Assert.IsTrue(timeouts[operation]!.GetValue<int>() is > 0 and <= 120, operation);
        }

        // Las métricas del uso medido, con su tipo, su unidad y la etiqueta en los dos idiomas.
        Assert.AreEqual(
            """[{"id":"disk_gb","kind":"gauge","unit":"GB","label":{"es":"Disco","en":"Disk"}},{"id":"transfer_gb","kind":"counter","unit":"GB","label":{"es":"Transferencia","en":"Transfer"}}]""",
            manifest["usage_metrics"]!.ToJsonString());
    }

    [TestMethod]
    public async Task CamposDesconocidos_SeIgnoran_YSinFirma_EsInvalidSignature()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);
        var context = ModuleHost.Existing("alice");
        context["algo_nuevo"] = new JsonObject { ["x"] = 1 };
        context["service"]!["campo_futuro"] = "valor";

        var (status, body) = await host.Op("get_status", context, new JsonObject { ["extra"] = 1 });
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v0/provisioning/get_status") { Content = new StringContent("{}") };
        request.Headers.Add("X-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Signature", "v0=00");
        using var response = await host.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public void RegistroDeIdempotencia_ConPostgreSql_NoUsaNiCreaElArchivo()
    {
        static IConfiguration Config(params (string Key, string Value)[] pairs) =>
            new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value))).Build();

        var root = Directory.CreateTempSubdirectory("hosting-").FullName;
        try
        {
            // Con PostgreSQL el registro lo toma la SDK: el módulo no escribe en el disco y corre con el sistema de
            // archivos de solo lectura, como dice la guía de alta disponibilidad de los módulos.
            Assert.IsNull(HostingModule.IdempotencyFile(Config(("Module:IdempotencyConnectionString", "Host=pg.interna")), root));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "data")));

            // Sin PostgreSQL, el archivo por defecto va en el directorio del módulo, con su carpeta creada.
            Assert.AreEqual(Path.Combine(root, "data", "idempotency.db"), HostingModule.IdempotencyFile(Config(), root));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "data")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
