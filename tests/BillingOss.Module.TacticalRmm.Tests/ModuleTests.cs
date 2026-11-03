using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.TacticalRmm.Tests;

/// <summary>El manifiesto, la prueba de configuración (instance/test) y la consulta de operaciones.</summary>
[TestClass]
public sealed class ModuleTests
{
    [TestMethod]
    public async Task Manifiesto_DeclaraCapacidadesEsquemasYAccion_SinDatosPersonalesNiCredenciales()
    {
        await using var host = await TrmmHost.Start(new FakeTactical());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("tacticalrmm", manifest!["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "0" }, TrmmHost.Strings(manifest["protocol"]));
        CollectionAssert.AreEqual(new[] { "provisioning" }, TrmmHost.Strings(manifest["kinds"]));
        CollectionAssert.AreEquivalent(
            new[] { "provisioning.core", "provisioning.status", "provisioning.view", "provisioning.change_plan", "provisioning.actions" },
            TrmmHost.Strings(manifest["capabilities"]));

        // Sin usuarios del panel ni datos de contacto: el nombre del cliente en Tactical es prefijo más ID.
        Assert.IsEmpty(manifest["credentials"]!.AsArray());
        Assert.IsEmpty(manifest["data_scopes"]!.AsArray());

        var config = manifest["config_schema"]!;
        // El prefijo no tiene valor por defecto: cada instalación de billing-oss elige el suyo.
        CollectionAssert.AreEquivalent(new[] { "api_url", "api_key", "client_name_prefix" }, TrmmHost.Strings(config["required"]));
        Assert.IsNull(config["properties"]!["client_name_prefix"]!["default"]);
        Assert.IsTrue(config["properties"]!["api_key"]!["x-secret"]!.GetValue<bool>());
        foreach (var field in new[] { "api_url", "client_name_prefix", "site_name", "deploy_expiry_days" })
        {
            Assert.IsNull(config["properties"]![field]!["x-secret"], field);
        }

        CollectionAssert.AreEqual(new[] { "agent_limit" }, TrmmHost.Strings(manifest["product_schema"]!["required"]));

        var action = manifest["actions"]!.AsArray().Single()!;
        Assert.AreEqual("installer_link", action["id"]!.GetValue<string>());
        Assert.IsFalse(string.IsNullOrEmpty(action["label"]!["es"]!.GetValue<string>()));
        Assert.IsFalse(string.IsNullOrEmpty(action["label"]!["en"]!.GetValue<string>()));
        CollectionAssert.AreEquivalent(new[] { "staff", "client" }, TrmmHost.Strings(action["audience"]));
        Assert.IsTrue(action["confirm"]!.GetValue<bool>());

        // Los plazos declarados entran en el máximo del protocolo.
        foreach (var (operation, seconds) in manifest["timeouts"]!.AsObject())
        {
            Assert.IsTrue(seconds!.GetValue<int>() is > 0 and <= 120, operation);
        }
    }

    [TestMethod]
    public async Task CadaOperacionDeclarada_EstaPublicada()
    {
        await using var host = await TrmmHost.Start(new FakeTactical());
        var context = new JsonObject();

        // Con el contexto vacío contestan invalid_input y no not_supported, y no llaman a Tactical.
        foreach (var operation in new[] { "create", "suspend", "unsuspend", "terminate", "change_plan", "run_action", "validate", "get_status", "view" })
        {
            var (status, body) = await host.Op(operation, context, key: $"k-{operation}");
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, operation);
            Assert.AreEqual(ErrorCodes.InvalidInput, TrmmHost.Code(body), operation);
        }

        Assert.IsEmpty(host.Fake.Requests);
    }

    [TestMethod]
    public async Task Test_ListaClientesYAgentesFiltrados_ConLaKeyEnElEncabezado()
    {
        var fake = new FakeTactical();
        fake.AddClient("Cliente cargado a mano");
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1, body!["result"]!["clients"]!.GetValue<int>());

        // No usa GET agents/ sin filtro: listaría los agentes de todos los clientes.
        CollectionAssert.AreEqual(new[] { "GET /clients/", "GET /agents/" }, fake.Requests.Select(r => r.Route).ToArray());
        Assert.AreEqual("0", fake.Requests[1].Query("client"));
        Assert.AreEqual(fake.ApiKey, fake.Requests[0].Headers["X-API-KEY"]);
        Assert.AreEqual("https://rmm.example.test/clients/", fake.Requests[0].Url.AbsoluteUri);
    }

    [TestMethod]
    public async Task Test_ConUnaRutaBaseEnLaUrl_LaRespeta()
    {
        var fake = new FakeTactical().On("GET /tactical/api/clients/", "[]", HttpStatusCode.OK).On("GET /tactical/api/agents/", "[]", HttpStatusCode.OK);
        await using var host = await TrmmHost.Start(fake);
        var config = host.Config();
        config["api_url"] = "https://rmm.example.test/tactical/api";

        var (status, _) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("https://rmm.example.test/tactical/api/clients/", fake.Requests[0].Url.AbsoluteUri);
    }

    [TestMethod]
    [DataRow(401, """{"detail":"Invalid token."}""", "application/json", "api_key")]
    [DataRow(403, """{"detail":"You do not have permission to perform this action."}""", "application/json", "api_key_o_permisos")]
    [DataRow(403, "<html><body><h1>403 Forbidden</h1></body></html>", "text/html", "ip_no_permitida")]
    [DataRow(403, "", "text/html", "ip_no_permitida")]
    public async Task Test_ConLaKeyOLaIpRechazadas_EsInvalidConfigConLaCausa(int upstream, string text, string mediaType, string cause)
    {
        var fake = new FakeTactical().On("GET /clients/", text, (HttpStatusCode)upstream, mediaType);
        await using var host = await TrmmHost.Start(fake);

        var (status, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, TrmmHost.Code(body));
        Assert.AreEqual(cause, TrmmHost.Cause(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.DoesNotContain(fake.ApiKey, body.ToJsonString());
        Assert.DoesNotContain("<html>", body.ToJsonString());
    }

    [TestMethod]
    public async Task Test_ConElCertificadoInvalido_EsInvalidConfigTls()
    {
        var fake = new FakeTactical().On("GET /clients/", _ => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "certificado"));
        await using var host = await TrmmHost.Start(fake);

        var (_, body) = await host.Send("/v0/instance/test", [], []);

        Assert.AreEqual(ErrorCodes.InvalidConfig, TrmmHost.Code(body));
        Assert.AreEqual("tls", TrmmHost.Cause(body));
    }

    [TestMethod]
    [DataRow("api_url", "http://rmm.example.test")]
    [DataRow("api_url", "https://usuario:clave@rmm.example.test")]
    [DataRow("api_url", "https://rmm.example.test/?x=1")]
    [DataRow("api_url", "rmm.example.test")]
    [DataRow("api_key", "")]
    [DataRow("api_key", "con espacio")]
    [DataRow("client_name_prefix", "con espacio")]
    [DataRow("client_name_prefix", "prefijo-de-mas-de-16")]
    [DataRow("site_name", "Sitio|raro")]
    [DataRow("deploy_expiry_days", "31")]
    public async Task ConfiguracionInvalida_EsInvalidConfigConElCampo_SinLlamarATactical(string field, string value)
    {
        await using var host = await TrmmHost.Start(new FakeTactical());
        var config = host.Config();
        config[field] = field == "deploy_expiry_days" ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value;

        var (status, body) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, TrmmHost.Code(body));
        CollectionAssert.AreEqual(new[] { field }, TrmmHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(host.Fake.Requests);
    }

    [TestMethod]
    public async Task SinPrefijo_EsInvalidConfig_SinLlamarATactical()
    {
        await using var host = await TrmmHost.Start(new FakeTactical());
        var config = host.Config();
        config.Remove("client_name_prefix");

        var (_, body) = await host.Send("/v0/instance/test", [], [], config: config);

        Assert.AreEqual(ErrorCodes.InvalidConfig, TrmmHost.Code(body));
        CollectionAssert.AreEqual(new[] { "client_name_prefix" }, TrmmHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(host.Fake.Requests);
    }

    [TestMethod]
    public async Task ConsultaDeOperacion_QueNoEsUnaBajaDelServicio_EsNotFound()
    {
        await using var host = await TrmmHost.Start(new FakeTactical());

        var (status, body) = await host.Send("/v0/operations/op-perdida", TrmmHost.Service(), []);

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, TrmmHost.Code(body));
    }
}
