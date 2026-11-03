using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>Manifiesto, rutas, firma y prueba de la instancia: los escenarios 1, 2, 5, 6 y 7 de la conformidad.</summary>
[TestClass]
public sealed class ModuleTests
{
    [TestMethod]
    public async Task Manifiesto_DeclaraTipoCapacidadesEsquemasYLimites()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("pterodactyl", manifest!["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "0" }, PteroHost.Strings(manifest["protocol"]));
        CollectionAssert.AreEqual(new[] { "provisioning" }, PteroHost.Strings(manifest["kinds"]));
        CollectionAssert.AreEquivalent(
            new[] { "provisioning.core", "provisioning.change_plan", "provisioning.status", "provisioning.view", "provisioning.actions" },
            PteroHost.Strings(manifest["capabilities"]));

        var config = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "panel_url", "application_key" }, PteroHost.Strings(config["required"]));
        Assert.IsTrue(config["properties"]!["application_key"]!["x-secret"]!.GetValue<bool>());
        Assert.AreEqual("^https://", config["properties"]!["panel_url"]!["pattern"]!.GetValue<string>());

        var product = manifest["product_schema"]!;
        CollectionAssert.AreEquivalent(
            new[] { "nest_id", "egg_id", "location_id", "memory_mb", "disk_mb", "cpu_percent" },
            PteroHost.Strings(product["required"]));
        Assert.AreEqual(500, product["properties"]!["io_weight"]!["default"]!.GetValue<int>());
        Assert.IsNotNull(product["properties"]!["extra_ports"]);
        Assert.IsNotNull(product["properties"]!["order_fields"]);
        Assert.AreEqual(1024, manifest["order_schema"]!["properties"]!["variables"]!["additionalProperties"]!["maxLength"]!.GetValue<int>());

        // Sin credenciales en el alta: el panel le manda al cliente el enlace para definir la contraseña.
        Assert.IsEmpty(manifest["credentials"]!.AsArray());
        var action = manifest["actions"]![0]!;
        Assert.AreEqual("set_panel_password", action["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "staff" }, PteroHost.Strings(action["audience"]));
        Assert.IsTrue(action["confirm"]!.GetValue<bool>());
        CollectionAssert.AreEqual(new[] { "panel_password" }, PteroHost.Strings(action["credentials"]));

        CollectionAssert.AreEqual(new[] { "account.contact" }, PteroHost.Strings(manifest["data_scopes"]));
        Assert.AreEqual(120, manifest["timeouts"]!["create"]!.GetValue<int>());
        Assert.AreEqual(60, manifest["timeouts"]!["change_plan"]!.GetValue<int>());
        Assert.AreEqual(60, manifest["timeouts"]!["terminate"]!.GetValue<int>());
        Assert.AreEqual(2, manifest["max_in_flight"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Esquemas_SonJsonSchemaConTiposValidos()
    {
        var manifest = PterodactylModule.Manifest();
        var types = new HashSet<string> { "object", "array", "string", "integer", "number", "boolean" };

        // Cada "type" del esquema es un tipo de JSON Schema, o una lista de ellos.
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, value) in obj)
                    {
                        if (name == "type")
                        {
                            var listed = value is JsonArray list ? list.Select(t => t!.GetValue<string>()) : [value!.GetValue<string>()];
                            Assert.IsTrue(listed.All(types.Contains), value!.ToJsonString());
                        }

                        Walk(value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }

        foreach (var schema in new[] { "config_schema", "product_schema", "order_schema" })
        {
            Assert.AreEqual("object", manifest[schema]!["type"]!.GetValue<string>(), schema);
            Walk(manifest[schema]);
        }

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CadaOperacionDeclarada_EstaPublicada()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var context = Samples.Context();
        ((JsonObject)context["service"]!)["params"] = new JsonObject();

        // Sin parámetros contestan invalid_input y no not_supported. Ninguna llega al panel.
        foreach (var operation in new[] { "validate", "create", "change_plan" })
        {
            var (status, body) = await host.Call(operation, context, key: $"k-{operation}");
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, operation);
            Assert.AreEqual(ErrorCodes.InvalidInput, PteroHost.Code(body), operation);
        }

        var (action, actionBody) = await host.Call("run_action", context, key: "k-action", input: new JsonObject { ["action"] = "reboot" });
        Assert.AreEqual(HttpStatusCode.NotImplemented, action);
        Assert.AreEqual(ErrorCodes.NotSupported, PteroHost.Code(actionBody));
        Assert.IsEmpty(host.Panel.Requests);

        var (unknown, unknownBody) = await host.Call("sso", context, key: "k-sso");
        Assert.AreEqual(HttpStatusCode.NotImplemented, unknown);
        Assert.AreEqual(ErrorCodes.NotSupported, PteroHost.Code(unknownBody));
    }

    [TestMethod]
    public async Task OperacionConEfecto_SinIdempotencyKey_EsInvalidInput()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Call("suspend", Samples.Context());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PteroHost.Code(body));
        Assert.IsEmpty(host.Panel.Requests);
    }

    [TestMethod]
    public async Task FirmaInvalida_Da401_YNoLlegaAlPanel()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Get("/v0/manifest", Encoding.UTF8.GetBytes("otro-secreto"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, status);
        Assert.AreEqual(ErrorCodes.InvalidSignature, PteroHost.Code(body));
        Assert.IsEmpty(host.Panel.Requests);
    }

    [TestMethod]
    public async Task CamposDesconocidos_SeIgnoran()
    {
        var panel = FakePanel.Standard();
        panel.AddServer(Samples.ServiceId, Samples.Paper);
        await using var host = await PteroHost.Start(panel);
        var context = Samples.Context();
        context["campo_nuevo"] = "x";
        ((JsonObject)context["service"]!)["otro"] = new JsonObject { ["a"] = 1 };
        ((JsonObject)context["service"]!["params"]!)["parametro_futuro"] = true;

        var (status, body) = await host.Send(
            "/v0/provisioning/get_status", context, new JsonObject { ["algo"] = 1 }, extra: new JsonObject { ["version_futura"] = "1" });

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", body!["result"]!["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Test_SoloLee_YDevuelveLaCantidadDeNodos()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(2, body!["result"]!["nodes"]!.GetValue<int>());
        Assert.AreEqual(0, host.Panel.Writes);
        CollectionAssert.IsSubsetOf(
            new[] { "GET nodes", "GET nodes/1/allocations", "GET users", "GET locations", "GET nests", "GET nests/1/eggs", "GET servers" },
            host.Panel.Requests.Select(r => r.Route).ToArray());
        var sent = host.Panel.Requests[0];
        Assert.AreEqual($"Bearer {FakePanel.DefaultKey}", sent.Headers["Authorization"]);
        Assert.AreEqual("application/vnd.pterodactyl.v1+json", sent.Headers["Accept"]);
        Assert.AreEqual("https://panel.example.test/api/application/nodes?per_page=1", sent.Url.ToString());
    }

    [TestMethod]
    public async Task Test_SinPermisoDeAllocations_EsInvalidConfigConLoQueFalta()
    {
        var panel = FakePanel.Standard();
        panel.Denied.Add("allocations");
        panel.Denied.Add("eggs");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PteroHost.Code(body));
        CollectionAssert.AreEquivalent(new[] { "allocations", "eggs" }, PteroHost.Strings(body!["error"]!["details"]!["missing"]));
        Assert.AreEqual(0, host.Panel.Writes);
    }

    [TestMethod]
    public async Task Test_ConLaClaveRechazada_EsInvalidConfig()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: PteroHost.Config("ptla_otra-clave"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PteroHost.Code(body));
        Assert.AreEqual("application_key", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.AreEqual(1, host.Panel.Requests.Count);
    }

    [TestMethod]
    [DataRow("http://panel.example.test", "panel_url")]
    [DataRow("https://usuario:clave@panel.example.test", "panel_url")]
    [DataRow("https://panel.example.test/?x=1", "panel_url")]
    [DataRow("no es una url", "panel_url")]
    public async Task ConfiguracionInvalida_NoLlamaAlPanel(string url, string field)
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());
        var config = PteroHost.Config();
        config["panel_url"] = url;

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PteroHost.Code(body));
        Assert.AreEqual(field, body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsEmpty(host.Panel.Requests);
    }

    [TestMethod]
    public async Task ClaveConEspacios_EsInvalidConfig()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: PteroHost.Config("ptla_ con espacio"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("application_key", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsEmpty(host.Panel.Requests);
    }

    [TestMethod]
    public async Task Secretos_NoAparecenEnRespuestasErroresNiLogs()
    {
        const string witnessKey = "ptla_TESTIGO-clave-9f8e7d6c5b4a";
        const string witnessPassword = "TESTIGO-ark-admin-1z2y3x";
        const string witnessEmail = "testigo.correo@example.com";
        var panel = FakePanel.Standard(witnessKey);

        // El panel repite la clave en un mensaje de error: el módulo la tapa.
        panel.On("POST servers", HttpStatusCode.BadRequest, Samples.Error(HttpStatusCode.BadRequest, "DisplayException", $"clave {witnessKey} sin nodo").Content.ReadAsStringAsync().Result, times: 1);
        var logs = new LogCapture();
        await using var host = await PteroHost.Start(panel, logs: logs);
        var plan = Samples.Minecraft();
        plan["nest_id"] = 2;
        plan["egg_id"] = 8;
        plan["order_fields"] = new JsonArray(new JsonObject { ["key"] = "ARK_ADMIN_PASSWORD", ["secret"] = true, ["required"] = true });
        var context = Samples.Context(plan: plan, order: new JsonObject { ["variables"] = new JsonObject { ["ARK_ADMIN_PASSWORD"] = witnessPassword } }, account: Samples.Account(email: witnessEmail));
        var config = PteroHost.Config(witnessKey);

        var responses = new List<JsonNode?>
        {
            (await host.Send("/v0/instance/test", [], [], config: config)).Body,
            (await host.Call("create", context, "svc-1042-create-1", config: config)).Body,
            (await host.Call("create", context, "svc-1042-create-2", config: config)).Body,
            (await host.Call("get_status", context, config: config)).Body,
            (await host.Call("view", context, config: config)).Body,
            (await host.Send("/v0/instance/test", [], [], config: PteroHost.Config(witnessKey + "x"))).Body,
        };

        // El alta llegó al panel con la contraseña: el módulo la usa, pero no la devuelve ni la registra.
        Assert.AreEqual(witnessPassword, panel.Server(Samples.ServiceId).Env["ARK_ADMIN_PASSWORD"]);
        Assert.AreEqual(ErrorCodes.Rejected, PteroHost.Code(responses[1]));
        foreach (var witness in new[] { witnessKey, witnessPassword, witnessEmail })
        {
            foreach (var response in responses)
            {
                Assert.DoesNotContain(witness, response?.ToJsonString() ?? string.Empty);
            }

            Assert.DoesNotContain(witness, logs.All);
        }

        Assert.Contains("Panel POST servers 400", logs.All);
    }
}
