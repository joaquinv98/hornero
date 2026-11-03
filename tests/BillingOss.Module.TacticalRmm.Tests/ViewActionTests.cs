using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.TacticalRmm.Tests;

/// <summary>La vista del portal y la acción installer_link.</summary>
[TestClass]
public sealed class ViewActionTests
{
    private static JsonObject Action(string agentType = "workstation") =>
        new() { ["action"] = "installer_link", ["params"] = new JsonObject { ["agent_type"] = agentType } };

    private static JsonNode Block(JsonNode? body, string type) =>
        body!["result"]!["blocks"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == type)!;

    private static bool Has(JsonNode? body, string type) =>
        body!["result"]!["blocks"]!.AsArray().Any(b => b!["type"]!.GetValue<string>() == type);

    [TestMethod]
    public async Task View_DentroDelLimite_MuestraAgentesYSitio_YOfreceLaAccion()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 3);
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("view", TrmmHost.Service(client: client));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("ok", Block(body, "status")["level"]!.GetValue<string>());
        var fields = Block(body, "fields")["items"]!.AsArray();
        Assert.AreEqual("Agentes instalados", fields[0]!["label"]!.GetValue<string>());
        Assert.AreEqual("3 de 5", fields[0]!["value"]!.GetValue<string>());
        Assert.AreEqual("OficinaPrincipal", fields[1]!["value"]!.GetValue<string>());
        Assert.IsFalse(Has(body, "notice"));
        CollectionAssert.AreEqual(new[] { "installer_link" }, TrmmHost.Strings(Block(body, "actions")["items"]));

        // Los IDs de Tactical no se le muestran al cliente.
        Assert.DoesNotContain(client.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), body!.ToJsonString());
        Assert.IsLessThanOrEqualTo(50, body["result"]!["blocks"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task View_PasadoDelLimite_AvisaConNotice_EnElIdiomaDeLaCuenta()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 6);
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("view", TrmmHost.Service(client: client, language: "en"));

        Assert.AreEqual("warning", Block(body, "status")["level"]!.GetValue<string>());
        Assert.AreEqual("6 of 5", Block(body, "fields")["items"]![0]!["value"]!.GetValue<string>());
        Assert.Contains("6 agents", Block(body, "notice")["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task View_ConUnLinkVigente_LoMuestraHttps_YSuspendidoNo()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        var uid = Guid.NewGuid().ToString();
        fake.AddDeployment(client.Sites[0].Id, DateTimeOffset.UtcNow.AddDays(3), uid: uid);
        fake.AddDeployment(client.Sites[0].Id, DateTimeOffset.UtcNow.AddMinutes(20));
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("view", TrmmHost.Service(client: client));
        var link = Block(body, "link");
        Assert.AreEqual($"https://rmm.example.test/clients/{uid}/deploy/", link["url"]!.GetValue<string>());
        Assert.IsFalse(Has(body, "actions"));

        (_, body) = await host.Op("view", TrmmHost.Service(client: client, status: "suspended"));
        Assert.AreEqual("warning", Block(body, "status")["level"]!.GetValue<string>());
        Assert.IsFalse(Has(body, "link"));
        Assert.IsFalse(Has(body, "actions"));
    }

    [TestMethod]
    public async Task View_SinPermisoDeDeployments_SaleSinLinkNiAccion()
    {
        // La acción fallaría siempre con invalid_config: el cliente vería un botón que nunca anda.
        var fake = new FakeTactical().On("GET /clients/deployments/", """{"detail":"You do not have permission to perform this action."}""", HttpStatusCode.Forbidden);
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("view", TrmmHost.Service(client: client));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsFalse(Has(body, "link"));
        Assert.IsFalse(Has(body, "actions"));
        Assert.Contains("sin link de instalación", host.Logs.Text);
    }

    [TestMethod]
    public async Task VistaEInstallerLink_ConElSitioDeOtroCliente_SonInvalidInput_SinMostrarNiCrearLinks()
    {
        // Un site_id mal importado: el link sería de un sitio de otra empresa.
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        var ajeno = fake.AddClient("BOSS-9");
        fake.AddDeployment(ajeno.Sites[0].Id, DateTimeOffset.UtcNow.AddDays(3));
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);
        service["service"]!["external_ids"]!["site_id"] = ajeno.Sites[0].Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var (_, view) = await host.Op("view", service);
        var (_, action) = await host.Op("run_action", service, key: "svc-1042-installer_link-1", input: Action());

        foreach (var body in new[] { view, action })
        {
            Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body));
            Assert.AreEqual("site_mismatch", body!["error"]!["details"]!["reason"]!.GetValue<string>());
            CollectionAssert.AreEqual(new[] { "external_ids.site_id" }, TrmmHost.Strings(body["error"]!["details"]!["fields"]));
            Assert.DoesNotContain(fake.Deployments.Single().Uid, body.ToJsonString());
        }

        Assert.AreEqual(0, fake.Count("POST /clients/deployments/"));
    }

    [TestMethod]
    public async Task View_ConUnUidQueNoEsUuid_LoDescarta()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddDeployment(client.Sites[0].Id, DateTimeOffset.UtcNow.AddDays(3), uid: "../../evil");
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("view", TrmmHost.Service(client: client));

        Assert.IsFalse(Has(body, "link"));
        Assert.Contains("uid inválido", host.Logs.Text);
    }

    [TestMethod]
    public async Task InstallerLink_CreaUnDeploymentDelSitio_YRepetirloDevuelveElMismoLink()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);

        var (status, body) = await host.Op("run_action", service, key: "svc-1042-installer_link-1", input: Action("server"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var deployment = fake.Deployments.Single();
        Assert.AreEqual(client.Sites[0].Id, deployment.SiteId);
        Assert.AreEqual("server", deployment.MonType);
        var url = body!["result"]!["url"]!.GetValue<string>();
        Assert.AreEqual($"https://rmm.example.test/clients/{deployment.Uid}/deploy/", url);

        // Vence a los días de la instancia (7 por defecto).
        var expires = DateTimeOffset.Parse(body["result"]!["expires_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsLessThan(TimeSpan.FromMinutes(5), (expires - DateTimeOffset.UtcNow.AddDays(7)).Duration());
        var sent = fake.Requests.Single(r => r.Route == "POST /clients/deployments/").Body!;
        Assert.EndsWith("+0000", sent["expires"]!.GetValue<string>());
        Assert.AreEqual("amd64", sent["goarch"]!.GetValue<string>());

        // La misma clave y otra réplica sin registro devuelven el mismo link sin crear otro.
        var again = await host.Op("run_action", service, key: "svc-1042-installer_link-1", input: Action("server"));
        Assert.AreEqual(body.ToJsonString(), again.Body!.ToJsonString());
        await using var other = await TrmmHost.Start(fake);
        (_, body) = await other.Op("run_action", service, key: "svc-1042-installer_link-1", input: Action("server"));
        Assert.AreEqual(url, body!["result"]!["url"]!.GetValue<string>());
        Assert.HasCount(1, fake.Deployments);

        // Otro tipo de agente es otro link.
        (_, body) = await host.Op("run_action", service, key: "svc-1042-installer_link-2", input: Action());
        Assert.AreNotEqual(url, body!["result"]!["url"]!.GetValue<string>());
        Assert.HasCount(2, fake.Deployments);
    }

    [TestMethod]
    public async Task InstallerLink_ConLaRespuestaPerdida_ElReintentoEncuentraElDeployment()
    {
        var fake = new FakeTactical().LoseResponseOnce("POST /clients/deployments/");
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);

        var (_, body) = await host.Op("run_action", service, key: "svc-1042-installer_link-1", input: Action());
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, TrmmHost.Code(body));

        (_, body) = await host.Op("run_action", service, key: "svc-1042-installer_link-1", input: Action());
        Assert.AreEqual($"https://rmm.example.test/clients/{fake.Deployments.Single().Uid}/deploy/", body!["result"]!["url"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("otra_accion", "workstation", "action")]
    [DataRow("installer_link", "mac", "params.agent_type")]
    public async Task RunAction_ConEntradaInvalida_EsInvalidInput(string action, string agentType, string field)
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);
        var input = Action(agentType);
        input["action"] = action;

        var (_, body) = await host.Op("run_action", TrmmHost.Service(client: client), key: "k-1", input: input);

        Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body));
        CollectionAssert.AreEqual(new[] { field }, TrmmHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(fake.Requests);
    }

    [TestMethod]
    public async Task InstallerLink_ConElServicioSuspendido_EsInvalidInput()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("run_action", TrmmHost.Service(client: client, status: "suspended"), key: "k-1", input: Action());

        Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body));
        Assert.IsEmpty(fake.Deployments);
    }
}
