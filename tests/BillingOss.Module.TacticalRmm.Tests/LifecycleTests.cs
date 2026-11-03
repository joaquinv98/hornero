using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.TacticalRmm.Tests;

/// <summary>Alta, validación, suspensión, estado observado, cambio de plan y baja contra el Tactical falso.</summary>
[TestClass]
public sealed class LifecycleTests
{
    [TestMethod]
    public async Task Create_CreaClienteYSitioConLaMarca_YDevuelveLosIdsComoTexto()
    {
        var fake = new FakeTactical();
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("create", TrmmHost.Service(id: 1042), key: "svc-1042-create-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var client = fake.Client("BOSS-1042")!;
        Assert.IsNotNull(client);
        Assert.AreEqual("OficinaPrincipal", client.Sites[0].Name);
        var result = body!["result"]!;
        Assert.AreEqual("active", result["observed_state"]!.GetValue<string>());
        Assert.AreEqual(client.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), result["external_ids"]!["client_id"]!.GetValue<string>());
        Assert.AreEqual(client.Sites[0].Id.ToString(System.Globalization.CultureInfo.InvariantCulture), result["external_ids"]!["site_id"]!.GetValue<string>());
        Assert.AreEqual("BOSS-1042", result["external_ids"]!["client_name"]!.GetValue<string>());

        // El nombre no lleva datos personales y el alta no crea usuarios ni roles.
        var post = fake.Requests.Single(r => r.Route == "POST /clients/");
        Assert.AreEqual("BOSS-1042", post.Body!["client"]!["name"]!.GetValue<string>());
        Assert.IsFalse(fake.Requests.Any(r => r.Url.AbsolutePath.StartsWith("/accounts/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Create_ConPrefijoYSitioDeLaInstancia_LosUsa()
    {
        var fake = new FakeTactical();
        await using var host = await TrmmHost.Start(fake);
        var config = host.Config();
        config["client_name_prefix"] = "PRV_";
        config["site_name"] = "Casa Central";

        var (status, _) = await host.Op("create", TrmmHost.Service(id: 7), key: "svc-7-create-1", config: config);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("Casa Central", fake.Client("PRV_7")!.Sites[0].Name);
    }

    [TestMethod]
    public async Task Create_RepetidoConLaMismaClave_DejaUnSoloClienteYLaMismaRespuesta()
    {
        var fake = new FakeTactical();
        await using var host = await TrmmHost.Start(fake);

        var first = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");
        var second = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(1, fake.ClientCount);
        Assert.AreEqual(1, fake.Count("POST /clients/"));
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task Create_ConLaRespuestaPerdida_EsOutcomeUnknown_YElReintentoEncuentraLaMarca()
    {
        var fake = new FakeTactical().LoseResponseOnce("POST /clients/");
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, TrmmHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());

        (status, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1, fake.ClientCount);
        Assert.AreEqual(1, fake.Count("POST /clients/"));
        Assert.AreEqual(fake.Client("BOSS-1042")!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), body!["result"]!["external_ids"]!["client_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Create_EnDosReplicasConRegistrosSeparados_DejaUnSoloCliente()
    {
        var fake = new FakeTactical();
        await using var a = await TrmmHost.Start(fake);
        await using var b = await TrmmHost.Start(fake);

        // A la vez, como un reintento del núcleo que cae en la otra réplica mientras la primera sigue.
        var results = await Task.WhenAll(
            a.Op("create", TrmmHost.Service(), key: "svc-1042-create-1"),
            b.Op("create", TrmmHost.Service(), key: "svc-1042-create-1"));

        Assert.AreEqual(1, fake.ClientCount);
        Assert.IsTrue(results.All(r => r.Status == HttpStatusCode.OK), string.Join(" ", results.Select(r => r.Body?.ToJsonString())));
        Assert.AreEqual(results[0].Body!["result"]!.ToJsonString(), results[1].Body!["result"]!.ToJsonString());

        // No es determinística: según el orden, la segunda encuentra el cliente o recibe el 400 por nombre repetido.
        // El caso del 400 lo fija Create_ConElNombreTomadoEntreLaBusquedaYElAlta_ReutilizaElCliente.
        Assert.IsTrue(fake.Count("POST /clients/") is >= 1 and <= 2);
    }

    [TestMethod]
    public async Task Create_ConElNombreTomadoEntreLaBusquedaYElAlta_ReutilizaElCliente()
    {
        // La otra réplica creó el cliente entre la búsqueda y el alta: la búsqueda no lo ve y el POST da 400.
        var fake = new FakeTactical();
        var other = fake.AddClient("BOSS-1042");
        fake.On("GET /clients/", _ =>
        {
            fake.Off("GET /clients/");
            return FakeTactical.Respond("[]", HttpStatusCode.OK);
        });
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(1, fake.ClientCount);
        Assert.AreEqual(1, fake.Count("POST /clients/"));
        Assert.AreEqual(other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), body!["result"]!["external_ids"]!["client_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Create_UnClienteCargadoAMano_NoSeReutiliza()
    {
        var fake = new FakeTactical();
        var manual = fake.AddClient("BOSS-1042 Empresa de ejemplo");
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(2, fake.ClientCount);
        Assert.AreNotEqual(manual.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), body!["result"]!["external_ids"]!["client_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Create_ConUnRechazoDeTactical_EsRejectedConLosCodigos()
    {
        var fake = new FakeTactical().On("POST /clients/", """{"name":["Client name cannot contain the | character"]}""", HttpStatusCode.BadRequest);
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("create", TrmmHost.Service(), key: "svc-1042-create-1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, TrmmHost.Code(body));
        var upstream = body!["error"]!["details"]!["upstream"]![0]!;
        Assert.AreEqual("name", upstream["code"]!.GetValue<string>());
        Assert.Contains("cannot contain", upstream["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Create_SinLimiteDeAgentes_EsInvalidInputSinLlamarATactical()
    {
        var fake = new FakeTactical();
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("create", TrmmHost.Service(agentLimit: null), key: "svc-1042-create-1");

        Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body));
        CollectionAssert.AreEqual(new[] { "params.agent_limit" }, TrmmHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(fake.Requests);
    }

    [TestMethod]
    public async Task Validate_ConElNombreLibre_EsValido_YConElNombreTomado_EsInvalidConfig()
    {
        var fake = new FakeTactical();
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("validate", TrmmHost.Service());
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());

        var other = fake.AddClient("BOSS-1042");
        (status, body) = await host.Op("validate", TrmmHost.Service());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("name_taken", TrmmHost.Cause(body));

        // El ID del cliente encontrado va en los detalles, para que el staff lo vincule si es del servicio.
        Assert.AreEqual(other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), body!["error"]!["details"]!["client_id"]!.GetValue<string>());

        // El mismo cliente que ya tiene el servicio no es un choque.
        (status, _) = await host.Op("validate", TrmmHost.Service(client: other));
        Assert.AreEqual(HttpStatusCode.OK, status);

        // validate no cambia nada.
        Assert.IsTrue(fake.Requests.All(r => r.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task SuspendYUnsuspend_PrendenYApaganElMantenimiento_YGetStatusLoVe()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        var otro = fake.AddClient("BOSS-9");
        fake.AddAgents(client, 2);
        fake.AddAgents(otro, 1);
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);

        var (status, body) = await host.Op("suspend", service, key: "svc-1042-suspend-1");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("suspended", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.IsTrue(fake.AgentsOf(client).All(a => a.Maintenance));
        Assert.IsFalse(fake.AgentsOf(otro).Any(a => a.Maintenance));
        var bulk = fake.Requests.Single(r => r.Route == "POST /agents/maintenance/bulk/").Body!;
        Assert.AreEqual("Client", bulk["type"]!.GetValue<string>());
        Assert.AreEqual(client.Id, bulk["id"]!.GetValue<long>());
        Assert.IsTrue(bulk["action"]!.GetValue<bool>());

        (_, body) = await host.Op("get_status", service);
        Assert.AreEqual("suspended", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.AreEqual(2, body["result"]!["details"]!["agents_in_maintenance"]!.GetValue<int>());

        // Repetirla con otra clave no cambia nada.
        (status, _) = await host.Op("suspend", service, key: "svc-1042-suspend-2");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsTrue(fake.AgentsOf(client).All(a => a.Maintenance));

        (_, body) = await host.Op("unsuspend", service, key: "svc-1042-unsuspend-1");
        Assert.AreEqual("active", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.IsFalse(fake.AgentsOf(client).Any(a => a.Maintenance));
        (_, body) = await host.Op("get_status", service);
        Assert.AreEqual("active", body!["result"]!["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Suspend_ConUnClienteQueNoExiste_EsNotFound_SinTocarAgentes()
    {
        var fake = new FakeTactical();
        var otro = fake.AddClient("BOSS-9");
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service();
        service["service"]!["external_ids"] = new JsonObject { ["client_id"] = "999", ["site_id"] = "998" };

        var (status, body) = await host.Op("suspend", service, key: "svc-1042-suspend-1");

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, TrmmHost.Code(body));
        Assert.AreEqual(0, fake.Count("POST /agents/maintenance/bulk/"));
        Assert.IsNotNull(otro);
    }

    [TestMethod]
    [DataRow(2, 2, "suspended")]
    [DataRow(2, 1, "active")]
    [DataRow(0, 0, "active")]
    public async Task GetStatus_SegunLosAgentesEnMantenimiento(int agents, int inMaintenance, string expected)
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, inMaintenance, maintenance: true);
        fake.AddAgents(client, agents - inMaintenance);
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("get_status", TrmmHost.Service(client: client));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(expected, body!["result"]!["observed_state"]!.GetValue<string>());
        var details = body["result"]!["details"]!;
        Assert.AreEqual(agents, details["agents"]!.GetValue<int>());
        Assert.AreEqual(inMaintenance, details["agents_in_maintenance"]!.GetValue<int>());
        Assert.AreEqual(5, details["agent_limit"]!.GetValue<int>());
        Assert.IsFalse(details["agent_limit_exceeded"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task GetStatus_ConElClienteBorrado_EsNotFound()
    {
        var fake = new FakeTactical();
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service();
        service["service"]!["external_ids"] = new JsonObject { ["client_id"] = "999" };

        var (status, body) = await host.Op("get_status", service);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("not_found", body!["result"]!["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetStatus_ConUnServidorQueNoFiltraLosAgentes_CuentaSoloLosDelCliente()
    {
        var fake = new FakeTactical { IgnoreAgentFilter = true };
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 1);
        fake.AddAgents(fake.AddClient("BOSS-9"), 7);
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Op("get_status", TrmmHost.Service(client: client));

        Assert.AreEqual(1, body!["result"]!["details"]!["agents"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ChangePlan_DebajoDeLosAgentesInstalados_EsInvalidInput_YArribaEsCompleted()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 7);
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client, agentLimit: 10);

        var (status, body) = await host.Op("change_plan", service, key: "svc-1042-change_plan-2", input: new JsonObject { ["params"] = new JsonObject { ["agent_limit"] = 5 } });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body));
        Assert.AreEqual(7, body!["error"]!["details"]!["agents"]!.GetValue<int>());
        CollectionAssert.AreEqual(new[] { "params.agent_limit" }, TrmmHost.Strings(body["error"]!["details"]!["fields"]));

        // El rechazo es definitivo: la misma clave da lo mismo.
        var again = await host.Op("change_plan", service, key: "svc-1042-change_plan-2", input: new JsonObject { ["params"] = new JsonObject { ["agent_limit"] = 5 } });
        Assert.AreEqual(body.ToJsonString(), again.Body!.ToJsonString());

        (status, body) = await host.Op("change_plan", service, key: "svc-1042-change_plan-3", input: new JsonObject { ["params"] = new JsonObject { ["agent_limit"] = 10 } });
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(7, body!["result"]!["agents"]!.GetValue<int>());
        Assert.AreEqual(10, body["result"]!["agent_limit"]!.GetValue<int>());

        // El cambio de plan no toca Tactical: no tiene cupo de agentes por cliente.
        Assert.IsTrue(fake.Requests.All(r => r.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task Terminate_SinAgentes_BorraElCliente_YRepetidaEsCompleted()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);

        var (status, body) = await host.Op("terminate", service, key: "svc-1042-terminate-1");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.IsNull(fake.Client("BOSS-1042"));

        // Otra réplica, sin el registro de la primera: el cliente ya no existe y la baja está hecha.
        await using var other = await TrmmHost.Start(fake);
        (status, body) = await other.Op("terminate", service, key: "svc-1042-terminate-1");
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>());

        (_, body) = await host.Op("get_status", service);
        Assert.AreEqual("not_found", body!["result"]!["observed_state"]!.GetValue<string>());

        // Nunca toca usuarios ni roles, aunque el servicio migrado traiga role_id y user_id.
        Assert.IsFalse(fake.Requests.Any(r => r.Url.AbsolutePath.StartsWith("/accounts/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Terminate_ConAgentesYSinForce_EsInvalidInput_YNoBorraNada()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 3);
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("terminate", TrmmHost.Service(client: client), key: "svc-1042-terminate-1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body));
        Assert.AreEqual(3, body!["error"]!["details"]!["agents"]!.GetValue<int>());
        CollectionAssert.AreEqual(new[] { "force" }, TrmmHost.Strings(body["error"]!["details"]!["fields"]));
        Assert.IsNotNull(fake.Client("BOSS-1042"));
        Assert.IsTrue(fake.Requests.All(r => r.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task Terminate_ConForce_DesinstalaLosAgentesYBorraElCliente()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        var otro = fake.AddClient("BOSS-9");
        fake.AddAgents(client, 3);
        fake.AddAgents(otro, 2);
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Op("terminate", TrmmHost.Service(client: client), key: "svc-1042-terminate-2", input: new JsonObject { ["force"] = true });

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.IsNull(fake.Client("BOSS-1042"));
        Assert.AreEqual(3, fake.Requests.Count(r => r.Method == HttpMethod.Delete && r.Url.AbsolutePath.StartsWith("/agents/", StringComparison.Ordinal)));
        Assert.HasCount(2, fake.AgentsOf(otro));
    }

    [TestMethod]
    public async Task Terminate_ConForceEnUnServidorQueNoFiltra_SoloDesinstalaLosDelCliente()
    {
        var fake = new FakeTactical { IgnoreAgentFilter = true };
        var client = fake.AddClient("BOSS-1042");
        var otro = fake.AddClient("BOSS-9");
        fake.AddAgents(client, 1);
        fake.AddAgents(otro, 4);
        await using var host = await TrmmHost.Start(fake);

        var (status, _) = await host.Op("terminate", TrmmHost.Service(client: client), key: "svc-1042-terminate-2", input: new JsonObject { ["force"] = true });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.HasCount(4, fake.AgentsOf(otro));
    }

    /// <summary>Escenario 4 de conformidad: operación larga completa, consultada desde otra réplica y perdida.</summary>
    [TestMethod]
    public async Task Terminate_ConForceYMuchosAgentes_QuedaEnCurso_YLaConsultaLaTermina()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 4);
        await using var a = await TrmmHost.Start(fake, stepBudget: TimeSpan.Zero);
        await using var b = await TrmmHost.Start(fake, stepBudget: TimeSpan.Zero);
        var service = TrmmHost.Service(client: client);
        var force = new JsonObject { ["force"] = true };

        var (status, body) = await a.Op("terminate", service, key: "svc-1042-terminate-2", input: force);
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        var operation = body["operation_id"]!.GetValue<string>();
        Assert.IsGreaterThan(0, body["retry_after"]!.GetValue<int>());
        Assert.HasCount(3, fake.AgentsOf(client));

        // La misma clave devuelve la misma operación, sin desinstalar otro.
        var again = await a.Op("terminate", service, key: "svc-1042-terminate-2", input: force);
        Assert.AreEqual(body.ToJsonString(), again.Body!.ToJsonString());
        Assert.HasCount(3, fake.AgentsOf(client));

        // La consulta llega a la otra réplica, que no empezó la baja, con el sobre completo y la entrada vacía.
        (_, body) = await b.Send($"/v0/operations/{operation}", service, []);
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.AreEqual(operation, body["operation_id"]!.GetValue<string>());
        (_, body) = await b.Send($"/v0/operations/{operation}", service, []);
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        (status, body) = await a.Send($"/v0/operations/{operation}", service, []);
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual("terminated", body["result"]!["observed_state"]!.GetValue<string>());
        Assert.IsNull(fake.Client("BOSS-1042"));

        // Consultar una operación terminada sigue dando terminated.
        (_, body) = await b.Send($"/v0/operations/{operation}", service, []);
        Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Consulta_DeUnaOperacionDeOtroServicio_EsNotFound_YRepetirLaBajaLaRetoma()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 2);
        await using var host = await TrmmHost.Start(fake, stepBudget: TimeSpan.Zero);
        var service = TrmmHost.Service(client: client);

        var (status, body) = await host.Send("/v0/operations/terminate-1", service, []);
        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, TrmmHost.Code(body));
        Assert.HasCount(2, fake.AgentsOf(client));

        // El núcleo repite la baja con la misma clave en una réplica sin registro: avanza desde donde quedó.
        await using var other = await TrmmHost.Start(fake, stepBudget: TimeSpan.Zero);
        (_, body) = await other.Op("terminate", service, key: "svc-1042-terminate-2", input: new JsonObject { ["force"] = true });
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.HasCount(1, fake.AgentsOf(client));
    }

    [TestMethod]
    public async Task Consulta_DeUnaBajaDeHaceMasDeUnaHora_EsNotFound_SinDesinstalar()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        fake.AddAgents(client, 2);
        await using var host = await TrmmHost.Start(fake);
        var started = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();

        var (status, body) = await host.Send($"/v0/operations/terminate-{client.Id}-{started}", TrmmHost.Service(client: client), []);

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, TrmmHost.Code(body));
        Assert.HasCount(2, fake.AgentsOf(client));
        Assert.IsEmpty(fake.Requests);
    }

    [TestMethod]
    public async Task ConUnClientIdDeOtroCliente_SuspendYBajaForzada_SonInvalidInput_SinTocarNada()
    {
        // Un client_id viejo o mal cargado que apunta al cliente de otra empresa.
        var fake = new FakeTactical();
        var ajeno = fake.AddClient("Empresa de ejemplo");
        fake.AddAgents(ajeno, 2);
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: ajeno);

        foreach (var (operation, input) in new[] { ("suspend", new JsonObject()), ("terminate", new JsonObject { ["force"] = true }) })
        {
            var (status, body) = await host.Op(operation, service, key: $"svc-1042-{operation}-1", input: input);

            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, operation);
            Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body), operation);
            CollectionAssert.AreEqual(new[] { "external_ids.client_id" }, TrmmHost.Strings(body!["error"]!["details"]!["fields"]), operation);
        }

        Assert.IsTrue(fake.Requests.All(r => r.Method == HttpMethod.Get));
        Assert.HasCount(2, fake.AgentsOf(ajeno));
        Assert.IsFalse(fake.AgentsOf(ajeno).Any(a => a.Maintenance));

        // Un cliente migrado se reconoce por el nombre que carga el importador en external_ids.client_name.
        service["service"]!["external_ids"]!["client_name"] = "Empresa de ejemplo";
        var (_, suspended) = await host.Op("suspend", service, key: "svc-1042-suspend-2");
        Assert.AreEqual("suspended", suspended!["result"]!["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Suspend_BorraLosLinksDeInstalacionDelCliente_YNoLosDeOtro()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        var otro = fake.AddClient("BOSS-9");
        fake.AddDeployment(client.Sites[0].Id, DateTimeOffset.UtcNow.AddDays(3));
        fake.AddDeployment(client.Sites[0].Id, DateTimeOffset.UtcNow.AddDays(5), "server");
        fake.AddDeployment(otro.Sites[0].Id, DateTimeOffset.UtcNow.AddDays(3));
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);

        var (status, _) = await host.Op("suspend", service, key: "svc-1042-suspend-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(otro.Sites[0].Id, fake.Deployments.Single().SiteId);

        // Sin permiso de deployments la suspensión igual termina.
        fake.On("GET /clients/deployments/", """{"detail":"You do not have permission to perform this action."}""", HttpStatusCode.Forbidden);
        (status, _) = await host.Op("suspend", service, key: "svc-1042-suspend-2");
        Assert.AreEqual(HttpStatusCode.OK, status);
    }

    [TestMethod]
    public async Task Terminate_ConForceYUnAgenteNuevoDuranteLaBaja_SigueEnCurso()
    {
        var fake = new FakeTactical();
        var client = fake.AddClient("BOSS-1042");
        var route = $"DELETE /clients/{client.Id}/";
        fake.On(route, _ =>
        {
            fake.Off(route);
            return FakeTactical.Respond("\"Agents exist under this client. There needs to be a site specified to move existing agents to\"", HttpStatusCode.BadRequest);
        });
        await using var host = await TrmmHost.Start(fake);
        var service = TrmmHost.Service(client: client);

        var (_, body) = await host.Op("terminate", service, key: "svc-1042-terminate-2", input: new JsonObject { ["force"] = true });
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        Assert.IsNotNull(fake.Client("BOSS-1042"));

        (_, body) = await host.Send($"/v0/operations/{body["operation_id"]!.GetValue<string>()}", service, []);
        Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.IsNull(fake.Client("BOSS-1042"));
    }

    [TestMethod]
    public async Task OperacionesSobreUnServicio_SinClientId_SonInvalidInput()
    {
        await using var host = await TrmmHost.Start(new FakeTactical());

        foreach (var operation in new[] { "suspend", "unsuspend", "terminate", "change_plan", "get_status", "view" })
        {
            var (_, body) = await host.Op(operation, TrmmHost.Service(), key: $"svc-1042-{operation}-1");
            Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body), operation);
            CollectionAssert.AreEqual(new[] { "external_ids.client_id" }, TrmmHost.Strings(body!["error"]!["details"]!["fields"]), operation);
        }

        Assert.IsEmpty(host.Fake.Requests);
    }
}
