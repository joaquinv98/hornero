using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Arca.Tests;

/// <summary>Las demás operaciones del módulo, de punta a punta: pedido firmado del núcleo, SOAP a la ARCA falsa y respuesta del protocolo.</summary>
[TestClass]
public sealed class OperationTests
{
    [TestMethod]
    public async Task Manifiesto_DeclaraElTipoLasCapacidadesYLosSecretos()
    {
        await using var host = await ArcaHost.Start(new FakeArca());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("arca", manifest!["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "fiscal" }, manifest["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "fiscal.core", "fiscal.exchange_rate", "fiscal.taxpayer", "instance_state" },
            manifest["capabilities"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray());
        var schema = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(
            new[] { "cuit", "environment", "certificate_pem", "private_key_pem" },
            schema["required"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "homologation", "production" },
            schema["properties"]!["environment"]!["enum"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray());
        Assert.IsTrue(schema["properties"]!["certificate_pem"]!["x-secret"]!.GetValue<bool>());
        Assert.IsTrue(schema["properties"]!["private_key_pem"]!["x-secret"]!.GetValue<bool>());
        Assert.IsNull(schema["properties"]!["cuit"]!["x-secret"]);
        Assert.AreEqual(1, manifest["max_in_flight"]!.GetValue<int>());

        // exchange_rate puede probar hasta 11 fechas seguidas: 120 s es lo máximo que el núcleo respeta.
        Assert.AreEqual(120, manifest["timeouts"]!["exchange_rate"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Test_HaceFeDummyYUnLoginDeWsaa()
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("OK", result["servers"]!["app_server"]!.GetValue<string>());
        Assert.AreEqual("homologation", result["environment"]!.GetValue<string>());
        Assert.AreEqual("2026-10-27T00:00:00-03:00", result["ticket_expires_at"]!.GetValue<string>());
        Assert.IsNotNull(result["certificate_expires_on"]);
        Assert.AreEqual(1, arca.Count("FEDummy"));
        Assert.AreEqual(1, arca.Count("loginCms"));
    }

    [TestMethod]
    public async Task SiFallaDespuesDeUnLoginNuevo_ElTicketViajaEnElError()
    {
        // WSAA dio un ticket y WSFEv1 no respondió: el ticket vuelve en instance_state del error, así el núcleo lo guarda y
        // ninguna réplica vuelve a WSAA, que no entrega otro mientras este siga vigente (punto 13 de la revisión de HA).
        var arca = new FakeArca().On("FECompUltimoAutorizado", _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("TOKEN-1", body["instance_state"]!["tickets"]!["wsfe"]!["token"]!.GetValue<string>());
        Assert.AreEqual(1, arca.Count("loginCms"));
    }

    [TestMethod]
    public async Task Test_SiArcaInformaUnServidorCaido_NoEstaDisponibleYNoPideTicket()
    {
        var down = Samples.Dummy.Replace("<AuthServer>OK</AuthServer>", "<AuthServer>FAIL</AuthServer>", StringComparison.Ordinal);
        var arca = new FakeArca().On("FEDummy", down);
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("loginCms"));
    }

    [TestMethod]
    public async Task Test_ConUnCertificadoQueNoSirve_EsInvalidConfig()
    {
        var arca = new FakeArca();
        await using var host = await ArcaHost.StartWithLogin(arca);
        var config = ArcaHost.Config();
        config["certificate_pem"] = "no es un certificado";

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Requests.Count);
    }

    [TestMethod]
    public async Task Parameters_TraLasCincoTablas()
    {
        var arca = new FakeArca().WithParameters();
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/parameters", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("Factura B", result["voucher_types"]![1]!["description"]!.GetValue<string>());
        Assert.AreEqual(96, result["document_types"]![1]!["id"]!.GetValue<int>());
        Assert.AreEqual("21%", result["vat_rates"]![0]!["description"]!.GetValue<string>());
        Assert.AreEqual("PES", result["currencies"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual("A", result["receiver_vat_conditions"]![0]!["voucher_class"]!.GetValue<string>());

        // Un solo login para las siete consultas, y la condición de IVA se pide sin clase para traer todas.
        Assert.AreEqual(1, arca.Count("loginCms"));
        Assert.IsEmpty(arca.Single("FEParamGetCondicionIvaReceptor").Body.Descendants().Where(e => e.Name.LocalName == "ClaseCmp"));
        Assert.IsNotNull(body["instance_state"]);
    }

    [TestMethod]
    public async Task LastNumber_TraElNumeroYSacaLaFechaDelComprobante()
    {
        var arca = new FakeArca()
            .On("FECompUltimoAutorizado", Samples.LastAuthorized(1487))
            .On("FECompConsultar", Samples.Found(number: 1487, date: "20261024"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1487, body!["result"]!["number"]!.GetValue<long>());
        Assert.AreEqual("2026-10-24", body["result"]!["date"]!.GetValue<string>());

        // WSFEv1 no tiene ID de pedido: la respuesta no cambia y no trae last_request_id.
        Assert.IsFalse(body["result"]!.AsObject().ContainsKey("last_request_id"));

        // La fecha sale de FECompConsultar con el número que devolvió FECompUltimoAutorizado.
        var consult = arca.Single("FECompConsultar").Body.Descendants().Single(e => e.Name.LocalName == "FeCompConsReq");
        Assert.AreEqual("1487", consult.Elements().Single(e => e.Name.LocalName == "CbteNro").Value);
        Assert.AreEqual("6", consult.Elements().Single(e => e.Name.LocalName == "CbteTipo").Value);
        Assert.AreEqual("3", consult.Elements().Single(e => e.Name.LocalName == "PtoVta").Value);
    }

    [TestMethod]
    public async Task LastNumber_SinComprobantes_DevuelveCeroYNoConsulta()
    {
        var arca = new FakeArca().On("FECompUltimoAutorizado", Samples.LastAuthorized(0));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (_, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 });

        Assert.AreEqual(0, body!["result"]!["number"]!.GetValue<long>());
        Assert.IsNull(body["result"]!["date"]);
        Assert.AreEqual(0, arca.Count("FECompConsultar"));
    }

    [TestMethod]
    [DataRow(19)]
    [DataRow(20)]
    [DataRow(21)]
    public async Task LastNumber_DeExportacion_VaPorWsfexYNoNecesitaConsultar_ConElUltimoIdDePedido(int type)
    {
        var arca = new FakeArca()
            .On("FEXGetLast_CMP", Samples.ExportLast(11, "20261020"))
            .On("FEXGetLast_ID", Samples.ExportLastId(4501));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = type });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(11, body!["result"]!["number"]!.GetValue<long>());
        Assert.AreEqual("2026-10-20", body["result"]!["date"]!.GetValue<string>());
        Assert.AreEqual(4501, body["result"]!["last_request_id"]!.GetValue<long>());
        Assert.AreEqual(1, arca.Count("FEXGetLast_CMP"));
        Assert.AreEqual(0, arca.Count("FECompConsultar"));

        // El ID se pide después del número, con el mismo ticket, y solo con Auth. Un solo login para las dos consultas.
        Assert.AreEqual(1, arca.Count("loginCms"));
        var wsfex = arca.Requests.Where(r => r.Body.Name.Namespace == Wsfex.Ns).ToList();
        CollectionAssert.AreEqual(new[] { "FEXGetLast_CMP", "FEXGetLast_ID" }, wsfex.Select(r => r.Body.Name.LocalName).ToArray());
        var id = arca.Single("FEXGetLast_ID");
        Assert.AreEqual("\"http://ar.gov.afip.dif.fexv1/FEXGetLast_ID\"", id.Action);
        CollectionAssert.AreEqual(new[] { "Auth" }, id.Body.Elements().Select(e => e.Name.LocalName).ToArray());
        CollectionAssert.AreEqual(new[] { "Token", "Sign", "Cuit" }, id.Body.Element(Wsfex.Ns + "Auth")!.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("TOKEN-1", id.Body.Descendants().Single(e => e.Name.LocalName == "Token").Value);
    }

    [TestMethod]
    public async Task LastNumber_DeExportacionSinPedidosPrevios_DevuelveCeroEnLosDos()
    {
        var arca = new FakeArca()
            .On("FEXGetLast_CMP", Samples.ExportLast(0, string.Empty))
            .On("FEXGetLast_ID", Samples.ExportLastId(0));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (_, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19 });

        Assert.AreEqual(0, body!["result"]!["number"]!.GetValue<long>());
        Assert.IsNull(body["result"]!["date"]);
        Assert.AreEqual(0, body["result"]!["last_request_id"]!.GetValue<long>());
    }

    [TestMethod]
    [DataRow(1000, HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow(1001, HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow(500, HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    [DataRow(501, HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    [DataRow(502, HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    [DataRow(505, HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    [DataRow(1014, HttpStatusCode.UnprocessableEntity, ErrorCodes.Rejected)]
    public async Task LastNumber_DeExportacion_ElErrorDeFEXGetLast_ID_SeClasificaComoLosDemasDeWsfex(int code, HttpStatusCode expectedStatus, string expectedError)
    {
        var arca = new FakeArca()
            .On("FEXGetLast_CMP", Samples.ExportLast(11, "20261020"))
            .On("FEXGetLast_ID", Samples.ExportError("FEXGetLast_ID", code, "Error de prueba"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19 });

        // Es la misma clasificación que tiene el error de FEXGetLast_CMP, con los códigos de ARCA en los detalles.
        Assert.AreEqual(expectedStatus, status);
        Assert.AreEqual(expectedError, body!["error"]!["code"]!.GetValue<string>());
        if (expectedError != ErrorCodes.InvalidConfig)
        {
            Assert.AreEqual(code, body["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
        }

        arca.On("FEXGetLast_CMP", Samples.ExportError("FEXGetLast_CMP", code, "Error de prueba"));
        var (sameStatus, same) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19 });
        Assert.AreEqual(expectedStatus, sameStatus);
        Assert.AreEqual(expectedError, same!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1, arca.Count("FEXGetLast_ID"));
    }

    [TestMethod]
    public async Task LastNumber_DeExportacion_SiFEXGetLast_IDNoTraElId_NoEstaDisponible()
    {
        var withoutId = Samples.ExportLastId(1).Replace("<Id>1</Id>", string.Empty, StringComparison.Ordinal);
        var arca = new FakeArca()
            .On("FEXGetLast_CMP", Samples.ExportLast(11, "20261020"))
            .On("FEXGetLast_ID", withoutId);
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19 });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.IsTrue(body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task LastNumber_DeExportacion_SiARCANoResponde_NoPidioElIdYEsUpstreamUnavailable()
    {
        var arca = new FakeArca()
            .On("FEXGetLast_CMP", _ => throw new TaskCanceledException("timeout", new TimeoutException()))
            .On("FEXGetLast_ID", Samples.ExportLastId(4501));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19 });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FEXGetLast_ID"));
    }

    [TestMethod]
    public async Task LastNumber_ConUnPuntoDeVentaQueARCAObjeta_EsRejected()
    {
        var arca = new FakeArca().On("FECompUltimoAutorizado", Samples.ErrorsOnly("FECompUltimoAutorizado", (11002, "Debe ser un punto de venta habilitado en este WS")));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 9, ["type_code"] = 6 });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(11002, body["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task EnProduccion_WsfexVaALaUrlDeProduccion()
    {
        var arca = new FakeArca()
            .On("FEXGetLast_CMP", Samples.ExportLast(11, "20261020"))
            .On("FEXGetLast_ID", Samples.ExportLastId(4501));
        await using var host = await ArcaHost.StartWithLogin(arca, production: ProductionMode.On);

        await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19 }, config: ArcaHost.Config("production"));

        Assert.AreEqual("https://servicios1.afip.gov.ar/wsfexv1/service.asmx", arca.Single("FEXGetLast_CMP").Url.ToString());
        Assert.AreEqual("https://servicios1.afip.gov.ar/wsfexv1/service.asmx", arca.Single("FEXGetLast_ID").Url.ToString());
        Assert.AreEqual("https://wsaa.afip.gov.ar/ws/services/LoginCms", arca.Single("loginCms").Url.ToString());
    }

    [TestMethod]
    [DataRow("/v0/fiscal/authorize")]
    [DataRow("/v0/fiscal/last_number")]
    [DataRow("/v0/instance/test")]
    public async Task Produccion_SinHabilitarEnElProceso_SeRechazaSinTocarArca(string path)
    {
        // La traba del módulo: con Arca:Production en off, una instancia de producción no llega ni al login de WSAA.
        var arca = new FakeArca()
            .On("FEDummy", Samples.Dummy)
            .On("FECAESolicitar", Samples.Authorized())
            .On("FECompUltimoAutorizado", Samples.LastAuthorized(1487))
            .On("FECompConsultar", Samples.Found(1487));
        await using var host = await ArcaHost.StartWithLogin(arca);
        var input = path switch
        {
            "/v0/fiscal/authorize" => Docs.Invoice(),
            "/v0/fiscal/last_number" => new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 },
            _ => [],
        };

        var (status, body) = await host.Send(path, input, key: "emision-1-1488", config: ArcaHost.Config("production"));
        var (homologation, _) = await host.Send(path, input, key: "emision-2-1488");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, body!["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Arca:Production", body["error"]!["message"]!.GetValue<string>());
        Assert.AreEqual("environment", body["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsEmpty(arca.Requests.Where(r => r.Url.Host.StartsWith("wsaa.", StringComparison.Ordinal) || r.Url.Host.StartsWith("servicios1.", StringComparison.Ordinal)));
        Assert.AreEqual(HttpStatusCode.OK, homologation);
    }

    [TestMethod]
    public async Task LastNumber_SiARCANoDevuelveElComprobanteQueInformoComoUltimo_NoEsNotFound()
    {
        // Un not_found acá se leería como "la serie no existe": es una inconsistencia pasajera de ARCA.
        var arca = new FakeArca()
            .On("FECompUltimoAutorizado", Samples.LastAuthorized(1487))
            .On("FECompConsultar", Samples.ErrorsOnly("FECompConsultar", (602, "No existen datos en nuestros registros")));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task LastNumber_ConEntradaIncompleta_EsInvalidInput()
    {
        await using var host = await ArcaHost.StartWithLogin(new FakeArca());

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["type_code"] = 6 });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("point_of_sale", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Find_DevuelveLosDatosYElCodigo()
    {
        var arca = new FakeArca().On("FECompConsultar", Samples.Found());
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/find", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6, ["number"] = 1488 });

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("71234567890123", result["authorization_code"]!.GetValue<string>());
        Assert.AreEqual("2026-10-25", result["document"]!["date"]!.GetValue<string>());
        Assert.AreEqual("12100.00", result["document"]!["amounts"]!["total"]!.GetValue<string>());
        Assert.AreEqual("30111222", result["document"]!["receiver"]!["doc_number"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Find_DeUnComprobanteQueNoExiste_EsNotFound()
    {
        var arca = new FakeArca().On("FECompConsultar", Samples.ErrorsOnly("FECompConsultar", (602, "No existen datos en nuestros registros")));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/find", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6, ["number"] = 99 });

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, body!["error"]!["code"]!.GetValue<string>());
        Assert.IsFalse(body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Find_DeExportacion_VaPorWsfex()
    {
        var arca = new FakeArca().On("FEXGetCMP", Samples.ExportFound());
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/find", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19, ["number"] = 12 });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("71999999999999", body!["result"]!["authorization_code"]!.GetValue<string>());
        var request = arca.Single("FEXGetCMP").Body.Descendants().Single(e => e.Name.LocalName == "Cmp");
        Assert.AreEqual("12", request.Elements().Single(e => e.Name.LocalName == "Cbte_nro").Value);

        // El WSDL de WSFEX pide Cbte_tipo con la t en minúscula en este método (el manual dice Cbte_Tipo).
        Assert.AreEqual("19", request.Elements().Single(e => e.Name.LocalName == "Cbte_tipo").Value);
        Assert.AreEqual("Cbte_tipo", request.Elements().Single(e => e.Name.LocalName.StartsWith("Cbte_t", StringComparison.OrdinalIgnoreCase)).Name.LocalName);
    }

    [TestMethod]
    public async Task Find_DeUnComprobanteDeExportacionInexistente_EsNotFound()
    {
        var arca = new FakeArca().On("FEXGetCMP", Samples.ExportError("FEXGetCMP", 1020, "Comprobante inexistente"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, _) = await host.Send("/v0/fiscal/find", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19, ["number"] = 99 });

        Assert.AreEqual(HttpStatusCode.NotFound, status);
    }

    [TestMethod]
    public async Task Find_SiARCANoResponde_EsUpstreamUnavailableYNoIncierto()
    {
        // En una consulta repetir no tiene riesgo: un corte es un simple "no pude".
        var arca = new FakeArca().On("FECompConsultar", _ => throw new TaskCanceledException("timeout", new TimeoutException()));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/find", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6, ["number"] = 1488 });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ExchangeRate_PideLaCotizacionDeLaFecha()
    {
        var arca = new FakeArca().On("FEParamGetCotizacion", Samples.Exchange("DOL", "1450.250000", "20261023"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["date"] = "2026-10-23" });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("1450.25", body!["result"]!["rate"]!.GetValue<string>());
        Assert.AreEqual("2026-10-23", body["result"]!["date"]!.GetValue<string>());
        var request = arca.Single("FEParamGetCotizacion").Body;
        Assert.AreEqual("DOL", request.Descendants().Single(e => e.Name.LocalName == "MonId").Value);
        Assert.AreEqual("20261023", request.Descendants().Single(e => e.Name.LocalName == "FchCotiz").Value);
    }

    [TestMethod]
    public async Task ExchangeRate_SinFecha_NoMandaFchCotiz()
    {
        var arca = new FakeArca().On("FEParamGetCotizacion", Samples.Exchange("DOL", "1450.25", "20261026"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL" });

        Assert.IsEmpty(arca.Single("FEParamGetCotizacion").Body.Descendants().Where(e => e.Name.LocalName == "FchCotiz"));
    }

    [TestMethod]
    [DataRow(19)]
    [DataRow(20)]
    [DataRow(21)]
    public async Task ExchangeRate_ConUnTipoDeExportacion_VaPorFEXGetPARAM_Ctz(int type)
    {
        var arca = new FakeArca()
            .On("FEXGetPARAM_Ctz", Samples.ExportExchange("1450.250000", "20261023"))
            .On("FEParamGetCotizacion", Samples.Exchange("DOL", "1.00", "20261026"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["date"] = "2026-10-23", ["type_code"] = type });

        // La misma forma de respuesta que con WSFEv1.
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("DOL", body!["result"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("1450.25", body["result"]!["rate"]!.GetValue<string>());
        Assert.AreEqual("2026-10-23", body["result"]!["date"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FEParamGetCotizacion"));

        // Mon_id y FchCotiz van al lado de Auth, la fecha con guiones, y el ticket es el de wsfex.
        var sent = arca.Single("FEXGetPARAM_Ctz");
        Assert.AreEqual("https://wswhomo.afip.gob.ar/wsfexv1/service.asmx", sent.Url.ToString());
        Assert.AreEqual("\"http://ar.gov.afip.dif.fexv1/FEXGetPARAM_Ctz\"", sent.Action);
        CollectionAssert.AreEqual(new[] { "Auth", "Mon_id", "FchCotiz" }, sent.Body.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("DOL", sent.Body.Element(Wsfex.Ns + "Mon_id")!.Value);
        Assert.AreEqual("2026-10-23", sent.Body.Element(Wsfex.Ns + "FchCotiz")!.Value);
        Assert.IsNotNull(body["instance_state"]!["tickets"]!["wsfex"]);
    }

    [TestMethod]
    public async Task ExchangeRate_ConUnTipoDeExportacionSinFecha_NoMandaFchCotiz()
    {
        var arca = new FakeArca().On("FEXGetPARAM_Ctz", Samples.ExportExchange("1450.25", "20261026"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["type_code"] = 21 });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("2026-10-26", body!["result"]!["date"]!.GetValue<string>());
        Assert.IsEmpty(arca.Single("FEXGetPARAM_Ctz").Body.Elements().Where(e => e.Name.LocalName == "FchCotiz"));
    }

    [TestMethod]
    [DataRow(6)]
    [DataRow(1)]
    public async Task ExchangeRate_ConUnTipoDeWsfev1_SigueYendoPorFEParamGetCotizacion(int type)
    {
        var arca = new FakeArca()
            .On("FEParamGetCotizacion", Samples.Exchange("DOL", "1450.25", "20261023"))
            .On("FEXGetPARAM_Ctz", Samples.ExportExchange("1.00", "20261026"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (_, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["date"] = "2026-10-23", ["type_code"] = type });

        Assert.AreEqual("1450.25", body!["result"]!["rate"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FEXGetPARAM_Ctz"));

        // La fecha de WSFEv1 sigue siendo yyyymmdd.
        Assert.AreEqual("20261023", arca.Single("FEParamGetCotizacion").Body.Descendants().Single(e => e.Name.LocalName == "FchCotiz").Value);
    }

    [TestMethod]
    public async Task ExchangeRate_ConUnTipoDeExportacionSinFecha_SiARCAObjeta_EsRejected()
    {
        // Sin fecha hay una sola consulta y el rechazo sale tal cual. Con fecha, un rechazo hace probar el día anterior.
        var arca = new FakeArca().On("FEXGetPARAM_Ctz", Samples.ExportError("FEXGetPARAM_Ctz", 1014, "Error no contemplado en el manual"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["type_code"] = 19 });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1014, body["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
        Assert.AreEqual(1, arca.Count("FEXGetPARAM_Ctz"));
    }

    [TestMethod]
    public async Task ExchangeRate_ConUnTipoDeExportacion_SinCotizacion_EsNotFound()
    {
        var arca = new FakeArca().On("FEXGetPARAM_Ctz", Samples.ExportExchange("0", "20261023"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["type_code"] = 19 });

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ExchangeRate_ConUnTipoDeExportacion_SiARCANoResponde_EsUpstreamUnavailable()
    {
        var arca = new FakeArca().On("FEXGetPARAM_Ctz", _ => throw new TaskCanceledException("timeout", new TimeoutException()));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["type_code"] = 19 });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ExchangeRate_ConUnTipoDeComprobanteQueNoEsUnNumero_EsInvalidInputConLaRuta()
    {
        await using var host = await ArcaHost.StartWithLogin(new FakeArca());

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL", ["type_code"] = "factura" });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, body!["error"]!["code"]!.GetValue<string>());
        Assert.Contains("type_code", body.ToJsonString());
    }

    [TestMethod]
    public async Task ExchangeRate_ConUnaMonedaQueARCAObjeta_EsRejected()
    {
        var arca = new FakeArca().On("FEParamGetCotizacion", Samples.ErrorsOnly("FEParamGetCotizacion", (12000, "Campo MonId debe ser alguno de los habilitados")));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "XXX" });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(12000, body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
    }

    // ---- exchange_rate con fecha: si ARCA no tiene cotización, prueba el día anterior ----
    //
    // Las dos fuentes con la misma lógica: export en false es WSFEv1 (FEParamGetCotizacion) y en true WSFEX (FEXGetPARAM_Ctz,
    // con el tipo 19). El 25 de octubre de 2026 es domingo, el 24 sábado y el 23 viernes.

    private static string RateOperation(bool export) => export ? "FEXGetPARAM_Ctz" : "FEParamGetCotizacion";

    private static JsonObject RateInput(bool export, string? date = null)
    {
        var input = new JsonObject { ["currency"] = "DOL" };
        if (date is not null)
        {
            input["date"] = date;
        }

        if (export)
        {
            input["type_code"] = 19;
        }

        return input;
    }

    /// <summary>La respuesta con cotización. La fecha va como la informa ARCA, yyyyMMdd.</summary>
    private static string Quoted(bool export, string day) =>
        export ? Samples.ExportExchange("1450.25", day) : Samples.Exchange("DOL", "1450.25", day);

    /// <summary>
    /// Las dos maneras en que ARCA dice "sin cotización": un rechazo de negocio y el "sin datos". Los códigos 12010 y 1999 son
    /// inventados para la prueba, porque ARCA no documenta cuál devuelve cuando falta la cotización.
    /// </summary>
    private static string NoQuote(bool export, bool rejection) => (export, rejection) switch
    {
        (false, true) => Samples.ErrorsOnly("FEParamGetCotizacion", (12010, "No existe cotización para la fecha")),
        (false, false) => Samples.ErrorsOnly("FEParamGetCotizacion", (602, "Sin Resultados: No existen datos en nuestros registros")),
        (true, true) => Samples.ExportError("FEXGetPARAM_Ctz", 1999, "No existe cotización para la fecha"),
        (true, false) => Samples.ExportExchange("0", string.Empty),
    };

    private static string Failure(bool export, int code) =>
        export ? Samples.ExportError("FEXGetPARAM_Ctz", code, "Error de prueba") : Samples.ErrorsOnly("FEParamGetCotizacion", (code, "Error de prueba"));

    /// <summary>La fecha que pidió el módulo, sin guiones para comparar igual el yyyyMMdd de WSFEv1 y el yyyy-MM-dd de WSFEX.</summary>
    private static string Asked(Sent sent) =>
        sent.Body.Elements().Single(e => e.Name.LocalName == "FchCotiz").Value.Replace("-", string.Empty, StringComparison.Ordinal);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExchangeRate_SinCotizacionEnLaFecha_RetrocedeDiaPorDiaHastaEncontrarla(bool export)
    {
        // Domingo con un rechazo, sábado con "sin datos" y viernes con la cotización: la encuentra al tercer intento.
        var attempts = 0;
        var arca = new FakeArca().On(RateOperation(export), sent => Samples.Soap(++attempts < 3 ? NoQuote(export, rejection: attempts == 1) : Quoted(export, Asked(sent))));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export, "2026-10-25"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("DOL", body!["result"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("1450.25", body["result"]!["rate"]!.GetValue<string>());

        // La fecha de la respuesta es la de la cotización que encontró ARCA, no la pedida.
        Assert.AreEqual("2026-10-23", body["result"]!["date"]!.GetValue<string>());
        var asked = arca.Requests.Where(r => r.Body.Name.LocalName == RateOperation(export)).ToList();
        CollectionAssert.AreEqual(new[] { "20261025", "20261024", "20261023" }, asked.Select(Asked).ToArray());

        // Cada intento usa el formato de fecha de su servicio, y los tres comparten un solo login.
        Assert.AreEqual(export ? "2026-10-25" : "20261025", asked[0].Body.Elements().Single(e => e.Name.LocalName == "FchCotiz").Value);
        Assert.AreEqual(1, arca.Count("loginCms"));
        Assert.IsNotNull(body["instance_state"]!["tickets"]![export ? "wsfex" : "wsfe"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExchangeRate_SiARCARespondeConUnaFechaAnterior_DevuelveLaQueInformaARCA(bool export)
    {
        var arca = new FakeArca().On(RateOperation(export), Quoted(export, "20261023"));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (_, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export, "2026-10-25"));

        Assert.AreEqual("2026-10-23", body!["result"]!["date"]!.GetValue<string>());
        Assert.AreEqual(1, arca.Count(RateOperation(export)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExchangeRate_SinCotizacionEnNingunaDeLasFechas_PruebaOnceFechasYEsNotFound(bool export)
    {
        // Alterna rechazo y "sin datos", y el último intento es un rechazo: su código queda en los detalles.
        var attempts = 0;
        var arca = new FakeArca().On(RateOperation(export), _ => Samples.Soap(NoQuote(export, rejection: ++attempts % 2 == 1)));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export, "2026-10-25"));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        var error = body!["error"]!;
        Assert.AreEqual(ErrorCodes.NotFound, error["code"]!.GetValue<string>());
        Assert.Contains("DOL", error["message"]!.GetValue<string>());
        Assert.Contains("2026-10-25", error["message"]!.GetValue<string>());
        Assert.Contains(export ? "[1999]" : "[12010]", error["message"]!.GetValue<string>());
        Assert.AreEqual(export ? 1999 : 12010, error["details"]!["upstream"]![0]!["code"]!.GetValue<int>());

        // La fecha pedida y los 10 días anteriores, con un solo login.
        var expected = Enumerable.Range(0, 11).Select(back => new DateOnly(2026, 10, 25).AddDays(-back).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        CollectionAssert.AreEqual(expected, arca.Requests.Where(r => r.Body.Name.LocalName == RateOperation(export)).Select(Asked).ToArray());
        Assert.AreEqual(1, arca.Count("loginCms"));
    }

    [TestMethod]
    [DataRow(false, 600, HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow(false, 500, HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    [DataRow(true, 1000, HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow(true, 500, HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    public async Task ExchangeRate_ConUnErrorQueNoEsDeCotizacion_CortaEnElPrimerIntento(bool export, int code, HttpStatusCode expectedStatus, string expectedError)
    {
        var arca = new FakeArca().On(RateOperation(export), Failure(export, code));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export, "2026-10-25"));

        Assert.AreEqual(expectedStatus, status);
        Assert.AreEqual(expectedError, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1, arca.Count(RateOperation(export)));
    }

    [TestMethod]
    [DataRow(false, 600, ErrorCodes.InvalidConfig)]
    [DataRow(false, 500, ErrorCodes.UpstreamUnavailable)]
    [DataRow(true, 1001, ErrorCodes.InvalidConfig)]
    [DataRow(true, 502, ErrorCodes.UpstreamUnavailable)]
    public async Task ExchangeRate_ElErrorQueLlegaDespuesDeRetroceder_TambienCorta(bool export, int code, string expectedError)
    {
        // Primero "sin datos" y después el error: corta en el segundo intento, no sigue hasta el undécimo.
        var attempts = 0;
        var arca = new FakeArca().On(RateOperation(export), _ => Samples.Soap(++attempts == 1 ? NoQuote(export, rejection: false) : Failure(export, code)));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (_, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export, "2026-10-25"));

        Assert.AreEqual(expectedError, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(2, arca.Count(RateOperation(export)));
    }

    [TestMethod]
    public async Task ExchangeRate_SiARCANoResponde_CortaSinRetroceder()
    {
        var arca = new FakeArca().On("FEParamGetCotizacion", _ => throw new TaskCanceledException("timeout", new TimeoutException()));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export: false, "2026-10-25"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1, arca.Count("FEParamGetCotizacion"));
    }

    [TestMethod]
    [DataRow(12000)]
    [DataRow(12001)]
    public async Task ExchangeRate_ConUnRechazoPorLaMoneda_NoRetrocede(int code)
    {
        // Con otro día la moneda seguiría mal: sale el rechazo tal cual, con el código de ARCA.
        var arca = new FakeArca().On("FEParamGetCotizacion", Samples.ErrorsOnly("FEParamGetCotizacion", (code, "Campo MonId inválido")));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export: false, "2026-10-25"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(code, body["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
        Assert.AreEqual(1, arca.Count("FEParamGetCotizacion"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExchangeRate_SinFecha_HaceUnaSolaLlamada_AunSiNoHayCotizacion(bool export)
    {
        var arca = new FakeArca().On(RateOperation(export), NoQuote(export, rejection: false));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1, arca.Count(RateOperation(export)));
        Assert.IsEmpty(arca.Single(RateOperation(export)).Body.Elements().Where(e => e.Name.LocalName == "FchCotiz"));
    }

    [TestMethod]
    public async Task ExchangeRate_ConUnaFechaEnElPrimerDiaDelCalendario_NoSeSaleDelRango()
    {
        // 0001-01-03 solo tiene dos días hacia atrás. No tiene que fallar con una excepción de fechas.
        var attempts = 0;
        var arca = new FakeArca().On("FEParamGetCotizacion", _ => Samples.Soap(NoQuote(export: false, rejection: ++attempts == 1)));
        await using var host = await ArcaHost.StartWithLogin(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", RateInput(export: false, "0001-01-03"));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(3, arca.Count("FEParamGetCotizacion"));
    }

    [TestMethod]
    public async Task UnaOperacionQueElModuloNoTiene_EsNotSupported()
    {
        await using var host = await ArcaHost.StartWithLogin(new FakeArca());

        var (status, body) = await host.Send("/v0/fiscal/numbering", [], key: "k");

        Assert.AreEqual(HttpStatusCode.NotImplemented, status);
        Assert.AreEqual(ErrorCodes.NotSupported, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ConElSecretoDeFirmaEquivocado_ElModuloNoHaceNada()
    {
        var arca = new FakeArca();
        await using var host = await ArcaHost.StartWithLogin(arca);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v0/fiscal/parameters", UriKind.Relative)) { Content = new StringContent("{}") };
        request.Headers.Add(ModuleHeaders.RequestId, Guid.NewGuid().ToString());
        request.Headers.Add(ModuleHeaders.Timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(ModuleHeaders.Signature, "v0=00");

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, arca.Requests.Count);
    }
}
