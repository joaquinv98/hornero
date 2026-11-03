using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Registrars.Tests;

/// <summary>
/// Conformidad del tipo registrar (secciones 9.2 y 11 del protocolo) contra los cuatro registrars, cada uno con su
/// API falsa sobre el mismo registro simulado.
/// </summary>
[TestClass]
public sealed class ScenarioTests
{
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonObject RegisterInput(string name = "nuevo.com", int years = 2) => new()
    {
        ["name"] = name,
        ["years"] = years,
        ["nameservers"] = new JsonArray("ns1.proveedor.ar", "ns2.proveedor.ar"),
    };

    [TestMethod]
    [DataRow("internetbs", "api_key", "password")]
    [DataRow("resellerclub", "api_key")]
    [DataRow("openprovider", "password")]
    [DataRow("namecheap", "api_key")]
    public async Task Manifiesto_TipoRegistrar_ConLosSecretosMarcados(string registrar, params string[] secrets)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (status, manifest) = await h.Send("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(registrar, manifest!["id"]!.ToString());
        Assert.AreEqual("registrar", manifest["kinds"]![0]!.ToString());
        var capabilities = manifest["capabilities"]!.AsArray().Select(c => c!.ToString()).ToList();
        CollectionAssert.Contains(capabilities, "registrar.core");

        // Namecheap no entrega el código EPP por API: no lo declara, y el núcleo no lo ofrece.
        Assert.AreEqual(registrar != "namecheap", capabilities.Contains("registrar.auth_code"));
        foreach (var secret in secrets)
        {
            Assert.IsTrue(manifest["config_schema"]!["properties"]![secret]!["x-secret"]!.GetValue<bool>(), secret);
        }
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task ProbarLaInstancia_CredencialesMalas_SonInvalidConfigSinMostrarlas(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (ok, _) = await h.Send("/v0/instance/test");
        h.State = null;
        var (bad, body) = await h.Send("/v0/instance/test", config: h.Fake.Config("clave-equivocada-xyz"));

        Assert.AreEqual(HttpStatusCode.OK, ok);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad);
        Assert.AreEqual(ErrorCodes.InvalidConfig, RegistrarHost.Code(body));
        Assert.IsFalse(body!.ToJsonString().Contains("clave-equivocada-xyz", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Consultar_LibreYTomado_YUnNombreInvalidoEsInvalidInput(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (status, body) = await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("Libre.COM", "tomado.com") });
        var (invalid, error) = await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("no es un dominio") });

        Assert.AreEqual(HttpStatusCode.OK, status);
        var results = body!["result"]!["results"]!.AsArray();
        Assert.AreEqual("libre.com", results[0]!["name"]!.ToString());
        Assert.IsTrue(results[0]!["available"]!.GetValue<bool>());
        Assert.IsFalse(results[1]!["available"]!.GetValue<bool>());
        Assert.AreEqual(ErrorCodes.InvalidInput, RegistrarHost.Code(error));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid);
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Registrar_LaMismaClaveRegistraUnaSolaVez_YConOtraEntradaEsConflicto(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (first, body) = await h.Send("/v0/registrar/register", RegisterInput(), "dom-1-register-1", context: RegistrarHost.Context("nuevo.com"));
        var (again, repeated) = await h.Send("/v0/registrar/register", RegisterInput(), "dom-1-register-1", context: RegistrarHost.Context("nuevo.com"));
        var (conflict, error) = await h.Send("/v0/registrar/register", RegisterInput(years: 3), "dom-1-register-1", context: RegistrarHost.Context("nuevo.com"));

        Assert.AreEqual(HttpStatusCode.OK, first, body?.ToJsonString());
        var result = body!["result"]!;
        Assert.AreEqual("active", result["status"]!.ToString());
        Assert.AreEqual(Iso(h.Registry.Today.AddYears(2)), result["expires_on"]!.ToString());
        CollectionAssert.AreEqual(new[] { "ns1.proveedor.ar", "ns2.proveedor.ar" }, result["nameservers"]!.AsArray().Select(n => n!.ToString()).ToArray());
        Assert.AreEqual(HttpStatusCode.OK, again);
        Assert.AreEqual(result.ToJsonString(), repeated!["result"]!.ToJsonString());
        Assert.AreEqual(1, h.Registry.Registrations);
        Assert.AreEqual("titular@example.com", h.Registry.Mine["nuevo.com"].RegistrantEmail);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, RegistrarHost.Code(error));
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Registrar_UnNombreTomadoPorOtro_EsRechazoConMotivoTaken(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (status, body) = await h.Send("/v0/registrar/register", RegisterInput("tomado.com"), "dom-1-register-1", context: RegistrarHost.Context("tomado.com"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, RegistrarHost.Code(body));
        Assert.AreEqual("taken", body!["error"]!["details"]!["upstream"]!["reason"]!.ToString());
        Assert.AreEqual(0, h.Registry.Registrations);
    }

    /// <summary>Marca de idempotencia (punto 4 de la sección 6): la respuesta del registro se perdió y el núcleo repite.</summary>
    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Registrar_ElDominioYaEstaEnLaCuenta_SeDaPorRegistradoSinRepetir(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        h.Registry.Add("nuevo.com", h.Registry.Today.AddYears(2));

        var (status, body) = await h.Send("/v0/registrar/register", RegisterInput(), "dom-1-register-9", context: RegistrarHost.Context("nuevo.com"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Iso(h.Registry.Today.AddYears(2)), body!["result"]!["expires_on"]!.ToString());
        Assert.AreEqual(0, h.Registry.Registrations);
    }

    /// <summary>La cuenta del revendedor es de todos sus clientes: un dominio de otro que ya está en ella no es el registro perdido.</summary>
    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Registrar_UnDominioAjenoEnLaMismaCuenta_SigueTomado(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        h.Registry.Add("viejo.com", h.Registry.Today.AddMonths(5));

        var (status, body) = await h.Send("/v0/registrar/register", RegisterInput("viejo.com"), "dom-1-register-1", context: RegistrarHost.Context("viejo.com"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.Rejected, RegistrarHost.Code(body));
        Assert.AreEqual("taken", body!["error"]!["details"]!["upstream"]!["reason"]!.ToString());
        Assert.AreEqual(0, h.Registry.Registrations);
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Registrar_SinDomicilioDelTitular_DiceQueCamposFaltanYNoLlamaAlRegistrar(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var registrant = RegistrarHost.Registrant();
        registrant.Remove("address1");
        registrant["phone"] = "11 5555 1234";

        var (status, body) = await h.Send("/v0/registrar/register", RegisterInput(), "dom-1-register-1", context: RegistrarHost.Context("nuevo.com", registrant: registrant));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, RegistrarHost.Code(body));
        CollectionAssert.Contains(body!["error"]!["details"]!["fields"]!.AsArray().Select(f => f!.ToString()).ToList(), "contacts.registrant.address1");
        Assert.AreEqual(0, h.Fake.Requests);
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Renovar_SumaLosAñosAlVencimiento(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var expires = h.Registry.Today.AddDays(20);
        h.Registry.Add("ejemplo.com", expires);

        var (status, body) = await h.Send("/v0/registrar/renew", new JsonObject { ["years"] = 1, ["current_expiry"] = Iso(expires) }, "dom-1-renew-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Iso(expires.AddYears(1)), body!["result"]!["expires_on"]!.ToString());
        Assert.AreEqual(expires.AddYears(1), h.Registry.Mine["ejemplo.com"].Expires);
    }

    /// <summary>Si se perdió el registro de la clave, la repetición con otra clave ve el vencimiento nuevo y no renueva otra vez.</summary>
    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Renovar_ElMismoVencimientoConOtraClave_NoRenuevaDosVeces(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var expires = h.Registry.Today.AddDays(20);
        h.Registry.Add("ejemplo.com", expires);
        var input = new JsonObject { ["years"] = 1, ["current_expiry"] = Iso(expires) };

        await h.Send("/v0/registrar/renew", input, "dom-1-renew-1");
        var (status, body) = await h.Send("/v0/registrar/renew", input.DeepClone().AsObject(), "dom-1-renew-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Iso(expires.AddYears(1)), body!["result"]!["expires_on"]!.ToString());
        Assert.AreEqual(expires.AddYears(1), h.Registry.Mine["ejemplo.com"].Expires);
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task ConsultarElDominio_DaEstadoVencimientoNameserversYBloqueo_YElAjenoEsNotFound(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var expires = h.Registry.Today.AddMonths(5);
        h.Registry.Add("ejemplo.com", expires).Locked = true;

        var (status, body) = await h.Send("/v0/registrar/get_domain");
        var (_, other) = await h.Send("/v0/registrar/get_domain", context: RegistrarHost.Context("ajeno.com"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var r = body!["result"]!;
        Assert.AreEqual("active", r["status"]!.ToString());
        Assert.AreEqual("registrar", r["source"]!.ToString());
        Assert.AreEqual(Iso(expires), r["expires_on"]!.ToString());
        Assert.AreEqual("ns1.viejo.example", r["nameservers"]![0]!.ToString());
        Assert.IsTrue(r["locked"]!.GetValue<bool>());
        Assert.AreEqual("not_found", other!["result"]!["status"]!.ToString());
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Nameservers_SeCambian_YConUnoSoloEsInvalidInput(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        var (status, body) = await h.Send("/v0/registrar/set_nameservers", new JsonObject { ["nameservers"] = new JsonArray("NS1.Nuevo.example.", "ns2.nuevo.example") }, "dom-1-set_nameservers-1");
        var (invalid, _) = await h.Send("/v0/registrar/set_nameservers", new JsonObject { ["nameservers"] = new JsonArray("ns1.nuevo.example") }, "dom-1-set_nameservers-2");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEqual(new[] { "ns1.nuevo.example", "ns2.nuevo.example" }, h.Registry.Mine["ejemplo.com"].Nameservers);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid);
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Bloqueo_SeSacaYSePone(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var domain = h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        var (off, _) = await h.Send("/v0/registrar/set_lock", new JsonObject { ["locked"] = false }, "dom-1-set_lock-1");
        Assert.IsFalse(domain.Locked);
        var (on, body) = await h.Send("/v0/registrar/set_lock", new JsonObject { ["locked"] = true }, "dom-1-set_lock-2");

        Assert.AreEqual(HttpStatusCode.OK, off);
        Assert.AreEqual(HttpStatusCode.OK, on);
        Assert.IsTrue(body!["result"]!["locked"]!.GetValue<bool>());
        Assert.IsTrue(domain.Locked);
    }

    [TestMethod]
    [DataRow("internetbs", true)]
    [DataRow("resellerclub", true)]
    [DataRow("openprovider", true)]
    [DataRow("namecheap", false)]
    public async Task CodigoDeTransferencia_VaEnSecrets_ONoEstaEnLaApi(string registrar, bool supported)
    {
        await using var h = await RegistrarHost.Start(registrar);
        h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1)).AuthCode = "EPP-XYZ-987";

        var (status, body) = await h.Send("/v0/registrar/get_auth_code");

        if (supported)
        {
            Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
            Assert.AreEqual("EPP-XYZ-987", body!["result"]!["secrets"]!["auth_code"]!.ToString());
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.NotImplemented, status);
            Assert.AreEqual(ErrorCodes.NotSupported, RegistrarHost.Code(body));
        }
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Transferir_QuedaEnCurso_YTerminaCuandoElRegistroLaCompleta(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var context = RegistrarHost.Context("traer.com");
        var input = new JsonObject { ["name"] = "traer.com", ["auth_code"] = FakeRegistry.ValidTransferCode };

        var (status, body) = await h.Send("/v0/registrar/transfer", input, "dom-1-transfer-1", context: context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.ToString());
        var operation = body["operation_id"]!.ToString();
        Assert.AreEqual("transfer-traer_com", operation);

        var (_, waiting) = await h.Send($"/v0/operations/{operation}", context: context);
        Assert.AreEqual("pending", waiting!["status"]!.ToString());

        h.Registry.CompleteTransfers();
        var (_, done) = await h.Send($"/v0/operations/{operation}", context: context);
        Assert.AreEqual("completed", done!["status"]!.ToString(), done.ToJsonString());
        Assert.AreEqual(Iso(h.Registry.Today.AddYears(1)), done["result"]!["expires_on"]!.ToString());
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Transferir_ConUnCodigoEquivocado_EsRechazo(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (status, body) = await h.Send("/v0/registrar/transfer", new JsonObject { ["name"] = "traer.com", ["auth_code"] = "EPP-MALO" }, "dom-1-transfer-1", context: RegistrarHost.Context("traer.com"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, RegistrarHost.Code(body));
        Assert.AreEqual("invalid_auth_code", body!["error"]!["details"]!["upstream"]!["reason"]!.ToString());
        Assert.IsFalse(body.ToJsonString().Contains("EPP-MALO", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Contactos_SeCambianConLosDelContexto(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        var (status, body) = await h.Send("/v0/registrar/set_contacts", key: "dom-1-set_contacts-1", context: RegistrarHost.Context(registrant: RegistrarHost.Registrant("nuevo@example.com")));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("nuevo@example.com", h.Registry.Mine["ejemplo.com"].RegistrantEmail);
    }

    [TestMethod]
    [DataRow("internetbs", true)]
    [DataRow("resellerclub", false)]
    [DataRow("openprovider", true)]
    [DataRow("namecheap", true)]
    public async Task Precios_PorTldConLasTresOperaciones(string registrar, bool supported)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (status, body) = await h.Send("/v0/registrar/get_prices", new JsonObject { ["currency"] = "USD" });

        if (!supported)
        {
            Assert.AreEqual(ErrorCodes.NotSupported, RegistrarHost.Code(body));
            return;
        }

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var com = body!["result"]!["prices"]!.AsArray().Single(p => p!["tld"]!.ToString() == ".com")!;
        Assert.AreEqual(("USD", 1, "9.50", "10.25", "9.75"), (com["currency"]!.ToString(), com["years"]!.GetValue<int>(), com["register"]!.ToString(), com["renew"]!.ToString(), com["transfer"]!.ToString()));
        Assert.IsTrue(body["result"]!["prices"]!.AsArray().Any(p => p!["tld"]!.ToString() == ".com.ar"));
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task SinConexion_EsReintentable_YUnCorteDespuesDeEnviarDejaElRegistroIncierto(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);

        h.Registry.Down = true;
        var (down, unavailable) = await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("libre.com") });
        h.Registry.Down = false;
        h.Registry.Hang = true;
        var (hang, unknown) = await h.Send("/v0/registrar/register", RegisterInput(), "dom-1-register-1", context: RegistrarHost.Context("nuevo.com"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, down);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, RegistrarHost.Code(unavailable));
        Assert.IsTrue(unavailable!["error"]!["retryable"]!.GetValue<bool>());
        if (registrar == "openprovider")
        {
            // El login es una lectura: el corte llega antes de mandar el registro y se puede repetir sin riesgo.
            Assert.AreEqual(ErrorCodes.UpstreamUnavailable, RegistrarHost.Code(unknown));
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.GatewayTimeout, hang);
            Assert.AreEqual(ErrorCodes.OutcomeUnknown, RegistrarHost.Code(unknown));
        }
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("openprovider")]
    public async Task ConsultaDeUnaOperacionQueNoEsDelModulo_EsNotFound(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (status, body) = await h.Send("/v0/operations/otra-cosa-1");

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, RegistrarHost.Code(body));
    }
}
