using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Arca.Tests;

/// <summary>
/// Lo que usa el alta guiada del núcleo (sección 2 del diseño fiscal): la traba de tres posiciones, el login nuevo de
/// instance/test, los puntos de venta y tributos de parameters, los tributos de authorize y el padrón.
/// </summary>
[TestClass]
public sealed class SetupTests
{
    [TestMethod]
    public void TrabaDeProduccion_SeLeeDeLaConfiguracion_YLaClaveVieja_EnTrue_ValeComoOn()
    {
        Assert.AreEqual(ProductionMode.Off, ProductionModes.Parse(null));
        Assert.AreEqual(ProductionMode.Read, ProductionModes.Parse(" Read "));
        Assert.AreEqual(ProductionMode.On, ProductionModes.Parse("on"));
        Assert.AreEqual(ProductionMode.On, ProductionModes.Parse(null, legacyAllow: true));
        Assert.AreEqual(ProductionMode.Off, ProductionModes.Parse("off", legacyAllow: true), "Arca:Production manda sobre la clave vieja.");
        Assert.ThrowsExactly<InvalidOperationException>(() => ProductionModes.Parse("si"));
    }

    [TestMethod]
    public void LaListaDeConsultas_EsCerrada()
    {
        foreach (var allowed in new[] { "loginCms", "FEDummy", "FEParamGetTiposIva", "FEParamGetPtosVenta", "FECompUltimoAutorizado", "FECompConsultar", "FEXGetPARAM_PtoVenta", "FEXGetLast_CMP", "FEXGetLast_ID", "FEXGetCMP", "getPersona_v2" })
        {
            Assert.IsTrue(ReadOnlyOperations.Allows(allowed), allowed);
        }

        foreach (var denied in new[] { "FECAESolicitar", "FEXAuthorize", "FECAEARegInformativo", "FECAEASolicitar", "FECAEASinMovimientoInformar", "" })
        {
            Assert.IsFalse(ReadOnlyOperations.Allows(denied), denied);
        }
    }

    [TestMethod]
    public async Task Produccion_EnModoRead_DejaLasConsultas_YRechazaAuthorizeAntesDelLogin()
    {
        var arca = new FakeArca()
            .WithParameters()
            .On("FEDummy", Samples.Dummy)
            .On("FECAESolicitar", Samples.Authorized())
            .On("FECompUltimoAutorizado", Samples.LastAuthorized(1487))
            .On("FECompConsultar", Samples.Found(1487));
        await using var host = await ArcaHost.StartWithLogin(arca, ProductionMode.Read);
        var production = ArcaHost.Config("production");

        var (test, body) = await host.Send("/v0/instance/test", [], config: production);
        Assert.AreEqual(HttpStatusCode.OK, test);
        Assert.IsFalse(body!["result"]!["authorize_enabled"]!.GetValue<bool>(), "En read la instancia de producción no puede emitir, y el panel lo muestra sin adivinar.");
        Assert.AreEqual("read", body["result"]!["production_mode"]!.GetValue<string>());
        var state = body["instance_state"];

        var (parameters, _) = await host.Send("/v0/fiscal/parameters", [], state: state, config: production);
        var (last, _) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 }, state: state, config: production);
        var (find, _) = await host.Send("/v0/fiscal/find", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6, ["number"] = 1487 }, state: state, config: production);
        Assert.AreEqual(HttpStatusCode.OK, parameters);
        Assert.AreEqual(HttpStatusCode.OK, last);
        Assert.AreEqual(HttpStatusCode.OK, find);
        var logins = arca.Count("loginCms");

        var (authorize, error) = await host.Send("/v0/fiscal/authorize", Docs.Invoice(), key: "emision-1-1488", state: state, config: production);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, authorize);
        Assert.AreEqual(ErrorCodes.InvalidConfig, error!["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Arca:Production=on", error["error"]!["message"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FECAESolicitar"));
        Assert.AreEqual(logins, arca.Count("loginCms"), "La emisión se rechaza antes de pedir un ticket.");
        Assert.IsTrue(arca.Requests.All(r => r.Url.Host is "wsaa.afip.gov.ar" or "servicios1.afip.gov.ar"), "En read se habla con producción de verdad, solo para consultar.");
    }

    [TestMethod]
    public async Task Produccion_EnModoOn_PuedeEmitir_YHomologacionSiempre()
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var on = await ArcaHost.StartWithLogin(arca, ProductionMode.On);
        var (_, production) = await on.Send("/v0/instance/test", [], config: ArcaHost.Config("production"));
        var (_, homologation) = await on.Send("/v0/instance/test", []);

        Assert.IsTrue(production!["result"]!["authorize_enabled"]!.GetValue<bool>());
        Assert.IsTrue(homologation!["result"]!["authorize_enabled"]!.GetValue<bool>());

        await using var read = await ArcaHost.StartWithLogin(new FakeArca().On("FEDummy", Samples.Dummy), ProductionMode.Read);
        var (_, homologationInRead) = await read.Send("/v0/instance/test", []);
        Assert.IsTrue(homologationInRead!["result"]!["authorize_enabled"]!.GetValue<bool>(), "La traba es solo para producción.");
    }

    [TestMethod]
    public async Task Test_ConFreshLogin_PideOtroTicketAunqueHayaUnoVigente()
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.StartWithLogin(arca);
        var (_, first) = await host.Send("/v0/instance/test", []);
        var state = first!["instance_state"];
        Assert.AreEqual(1, arca.Count("loginCms"));

        _ = await host.Send("/v0/instance/test", [], state: state);
        Assert.AreEqual(1, arca.Count("loginCms"), "Con un ticket vigente no se pide otro.");

        // WSAA entrega otro ticket: el estado nuevo lo lleva, para que el núcleo lo guarde con la credencial nueva.
        arca.WithLogin(host.Clock, "TOKEN-2", "SIGN-2");
        var (status, body) = await host.Send("/v0/instance/test", new JsonObject { ["fresh_login"] = true }, state: state);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(2, arca.Count("loginCms"), "Con fresh_login se ignora el guardado y el que el módulo tiene en memoria.");
        Assert.AreEqual("TOKEN-2", body!["instance_state"]!["tickets"]!["wsfe"]!["token"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Test_ConFreshLogin_SiWsaaDiceQueYaHayTicket_NoUsaElViejo_YPideEsperar()
    {
        // Renovar el certificado: el ticket que queda puede ser del certificado anterior, así que no vale.
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.StartWithLogin(arca);
        var (_, first) = await host.Send("/v0/instance/test", []);
        arca.On("loginCms", _ => Samples.Fault("ns1:coe.alreadyAuthenticated", "El CEE ya posee un TA valido para el acceso al WSN solicitado"));

        var (status, body) = await host.Send("/v0/instance/test", new JsonObject { ["fresh_login"] = true }, state: first!["instance_state"]);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(660, body["error"]!["retry_after"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Parameters_TraePuntosDeVentaYTributos_YLosDeExportacionSiSePide()
    {
        var arca = new FakeArca().WithParameters().On("FEXGetPARAM_PtoVenta", Samples.ExportPointsOfSale((12, "N")));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/parameters", new JsonObject { ["export"] = true });

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        var points = result["points_of_sale"]!.AsArray();
        Assert.HasCount(2, points);
        Assert.AreEqual(3, points[0]!["id"]!.GetValue<int>());
        Assert.AreEqual("CAE", points[0]!["emission_type"]!.GetValue<string>());
        Assert.IsFalse(points[0]!["blocked"]!.GetValue<bool>());
        Assert.IsNull(points[0]!["retired_on"]);
        Assert.IsTrue(points[1]!["blocked"]!.GetValue<bool>());
        Assert.AreEqual("2025-01-01", points[1]!["retired_on"]!.GetValue<string>());
        Assert.AreEqual(13, result["tax_types"]![0]!["id"]!.GetValue<int>());
        Assert.AreEqual(12, result["export_points_of_sale"]![0]!["id"]!.GetValue<int>());

        // Un login por servicio: wsfe para las tablas y wsfex para los puntos de exportación.
        Assert.AreEqual(2, arca.Count("loginCms"));
    }

    [TestMethod]
    public async Task Parameters_SinPuntosDeVenta_DevuelveLaListaVacia_YSinExportNoLlamaAWsfex()
    {
        var arca = new FakeArca().WithParameters().On("FEParamGetPtosVenta", Samples.ErrorsOnly("FEParamGetPtosVenta", (602, "Sin Resultados")));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/parameters", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsEmpty(body!["result"]!["points_of_sale"]!.AsArray());
        Assert.IsFalse(body["result"]!.AsObject().ContainsKey("export_points_of_sale"));
        Assert.AreEqual(0, arca.Count("FEXGetPARAM_PtoVenta"));
    }

    [TestMethod]
    public async Task Authorize_ConTributos_InformaImpTribYElArray_YElPeriodoAsociadoDeLaNota()
    {
        var arca = new FakeArca().On("FECAESolicitar", Samples.Authorized());
        await using var host = await ArcaHost.StartWithLogin(arca);
        var input = Docs.Invoice();
        var amounts = input["document"]!["amounts"]!;
        amounts["other_taxes"] = "1270.50";
        amounts["total"] = "13370.50";
        amounts["taxes"] = new JsonArray(new JsonObject { ["id"] = 13, ["description"] = "Percepción de IVA RG 2126", ["base"] = "12100.00", ["rate"] = "10.5", ["amount"] = "1270.50" });
        input["document"]!["period_assoc"] = new JsonObject { ["from"] = "2026-10-01", ["to"] = "2026-10-31" };

        var (status, _) = await host.Send("/v0/fiscal/authorize", input, key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var detail = arca.Single("FECAESolicitar").Body.Descendants().Single(e => e.Name.LocalName == "FECAEDetRequest");
        var tributo = detail.Descendants().Single(e => e.Name.LocalName == "Tributo");
        var period = detail.Elements().Single(e => e.Name.LocalName == "PeriodoAsoc");
        static string Text(System.Xml.Linq.XElement parent, string name) => parent.Elements().Single(e => e.Name.LocalName == name).Value;
        Assert.AreEqual("1270.50", Text(detail, "ImpTrib"));
        Assert.AreEqual("13", Text(tributo, "Id"));
        Assert.AreEqual("Percepción de IVA RG 2126", Text(tributo, "Desc"));
        Assert.AreEqual("12100.00", Text(tributo, "BaseImp"));
        Assert.AreEqual("10.50", Text(tributo, "Alic"));
        Assert.AreEqual("1270.50", Text(tributo, "Importe"));
        Assert.AreEqual("20261001", Text(period, "FchDesde"));
        Assert.AreEqual("20261031", Text(period, "FchHasta"));

        // El orden del manual: los tributos antes del IVA y el período asociado después de los opcionales.
        var names = detail.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.IsLessThan(names.IndexOf("Iva"), names.IndexOf("Tributos"));
        Assert.AreEqual("PeriodoAsoc", names[^1]);
    }

    [TestMethod]
    public void Validate_ElTotalDeOtrosTributos_TieneQueSerLaSumaDelDetalle()
    {
        var input = Docs.Invoice();
        var amounts = input["document"]!["amounts"]!;
        amounts["other_taxes"] = "100.00";
        amounts["taxes"] = new JsonArray(
            new JsonObject { ["id"] = 13, ["description"] = "Percepción", ["base"] = "12100.00", ["rate"] = "10.5", ["amount"] = "70.00" },
            new JsonObject { ["id"] = 0, ["description"] = "", ["base"] = "-1", ["rate"] = "10.555", ["amount"] = "30.001" });
        input["document"]!["period_assoc"] = new JsonObject { ["from"] = "2026-10-31", ["to"] = "2026-10-01" };

        var bad = Wsfe.Validate(Docs.Parse(input).Document!);

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "document.amounts.other_taxes",
                "document.amounts.taxes[1].id",
                "document.amounts.taxes[1].description",
                "document.amounts.taxes[1].base",
                "document.amounts.taxes[1].rate",
                "document.amounts.taxes[1].amount",
                "document.period_assoc",
            },
            bad);
        Assert.IsFalse(bad.Any(b => b.StartsWith("document.amounts.taxes[0]", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Find_DevuelveLosTributosDelComprobante()
    {
        var found = Samples.Found().Replace("<Iva>", "<Tributos><Tributo><Id>13</Id><Desc>Percepción de IVA RG 2126</Desc><BaseImp>12100</BaseImp><Alic>10.5</Alic><Importe>1270.5</Importe></Tributo></Tributos><Iva>", StringComparison.Ordinal);
        var arca = new FakeArca().On("FECompConsultar", found);
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/find", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6, ["number"] = 1488 });

        Assert.AreEqual(HttpStatusCode.OK, status);
        var tax = body!["result"]!["document"]!["amounts"]!["taxes"]![0]!;
        Assert.AreEqual(13, tax["id"]!.GetValue<int>());
        Assert.AreEqual("12100.00", tax["base"]!.GetValue<string>());
        Assert.AreEqual("10.5", tax["rate"]!.GetValue<string>());
        Assert.AreEqual("1270.50", tax["amount"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Taxpayer_TraeLaConstancia_ConLaCondicionSugerida_YUsaSuPropioServicioDeWsaa()
    {
        var arca = new FakeArca().On("getPersona_v2", Samples.Persona());
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/taxpayer", new JsonObject { ["tax_id"] = "30712345678" });

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("PRUEBA SA", result["name"]!.GetValue<string>());
        Assert.AreEqual("JURIDICA", result["person_type"]!.GetValue<string>());
        Assert.AreEqual("AV SIEMPREVIVA 742", result["address"]!["street"]!.GetValue<string>());
        Assert.AreEqual("1425", result["address"]!["postal_code"]!.GetValue<string>());
        Assert.AreEqual(30, result["taxes"]![0]!["id"]!.GetValue<int>());
        Assert.AreEqual(1, result["suggested_vat_condition"]!.GetValue<int>());
        Assert.IsFalse(result["blocked"]!.GetValue<bool>());
        Assert.IsNotNull(body["instance_state"]);

        var sent = arca.Single("getPersona_v2");
        Assert.AreEqual("https://awshomo.afip.gov.ar/sr-padron/webservices/personaServiceA5", sent.Url.ToString());
        Assert.AreEqual("20000000001", sent.Body.Elements().Single(e => e.Name.LocalName == "cuitRepresentada").Value);
        Assert.AreEqual("30712345678", sent.Body.Elements().Single(e => e.Name.LocalName == "idPersona").Value);
        Assert.AreEqual(1, arca.Count("loginCms"));
        var tra = arca.Single("loginCms");
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(tra.Body.Elements().Single().Value)), "ws_sr_constancia_inscripcion");
    }

    [TestMethod]
    public async Task Taxpayer_Monotributista_SugiereMonotributo_YLaConstanciaBloqueadaTraeSusNotas()
    {
        var monotax = "<datosMonotributo><impuesto><descripcionImpuesto>MONOTRIBUTO</descripcionImpuesto><idImpuesto>20</idImpuesto><periodo>202001</periodo></impuesto><categoriaMonotributo><descripcionCategoria>D</descripcionCategoria><idCategoria>4</idCategoria></categoriaMonotributo></datosMonotributo>";
        var arca = new FakeArca().On("getPersona_v2", Samples.Persona("20111111112", company: null, taxes: string.Empty, extra: monotax));
        await using var host = await ArcaHost.StartWithLogin(arca);
        var (_, body) = await host.Send("/v0/fiscal/taxpayer", new JsonObject { ["tax_id"] = "20111111112" });
        var result = body!["result"]!;
        Assert.AreEqual("PEREZ JUAN", result["name"]!.GetValue<string>());
        Assert.AreEqual(6, result["suggested_vat_condition"]!.GetValue<int>());
        Assert.AreEqual("D", result["monotax_category"]!.GetValue<string>());

        var blocked = "<errorConstancia><apellido>PEREZ</apellido><nombre>JUAN</nombre><error>El contribuyente no tiene domicilio fiscal electronico constituido</error></errorConstancia>";
        arca.On("getPersona_v2", Samples.Persona("20111111112", company: null, taxes: string.Empty, extra: blocked));
        (_, body) = await host.Send("/v0/fiscal/taxpayer", new JsonObject { ["tax_id"] = "20111111112" }, state: body["instance_state"]);
        result = body!["result"]!;
        Assert.IsTrue(result["blocked"]!.GetValue<bool>());
        Assert.IsNull(result["suggested_vat_condition"]);
        StringAssert.Contains(result["notes"]![0]!.GetValue<string>(), "domicilio fiscal electronico");
    }

    [TestMethod]
    public async Task Taxpayer_CuitInexistente_EsNotFound_YUnaCuitMalFormadaNoLlamaAArca()
    {
        var arca = new FakeArca().On("getPersona_v2", _ => Samples.Fault("ns0:Server", "No existe persona con ese Id"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (missing, body) = await host.Send("/v0/fiscal/taxpayer", new JsonObject { ["tax_id"] = "20111111112" });
        var (bad, _) = await host.Send("/v0/fiscal/taxpayer", new JsonObject { ["tax_id"] = "20-11111111-2" });

        Assert.AreEqual(HttpStatusCode.NotFound, missing);
        Assert.AreEqual(ErrorCodes.NotFound, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad);
        Assert.AreEqual(1, arca.Count("getPersona_v2"));
    }
}
