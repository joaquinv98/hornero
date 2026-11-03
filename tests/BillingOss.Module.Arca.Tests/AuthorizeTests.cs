using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Arca.Tests;

/// <summary>
/// Resultado de authorize de punta a punta: qué error del protocolo sale para cada respuesta o corte de ARCA.
/// La regla central es que un resultado dudoso nunca se informe como rechazo.
/// </summary>
[TestClass]
public sealed class AuthorizeTests
{
    private const string Path = "/v0/fiscal/authorize";

    private static Task<ArcaHost> Start(Func<Sent, HttpResponseMessage> authorize) =>
        ArcaHost.StartWithLogin(new FakeArca().On("FECAESolicitar", authorize));

    private static HttpResponseMessage Body(string body) => Samples.Soap(body);

    private static async Task<(ArcaHost Host, FakeArca Arca)> StartCore(HttpResponseMessage authorized)
    {
        var arca = new FakeArca().On("FECAESolicitar", _ => authorized);
        return (await ArcaHost.StartWithLogin(arca), arca);
    }

    [TestMethod]
    public async Task Autorizado_DevuelveElCaeYGuardaElTicket()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var arca = host.Arca;

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("completed", body!["status"]!.GetValue<string>());
        Assert.AreEqual("71234567890123", body["result"]!["authorization_code"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05", body["result"]!["authorization_due"]!.GetValue<string>());
        Assert.AreEqual("TOKEN-1", body["instance_state"]!["tickets"]!["wsfe"]!["token"]!.GetValue<string>());

        // El pedido a ARCA llevó el ticket del login y el SOAPAction de WSFEv1.
        var sent = arca.Single("FECAESolicitar");
        Assert.AreEqual("https://wswhomo.afip.gob.ar/wsfev1/service.asmx", sent.Url.ToString());
        Assert.AreEqual("\"http://ar.gov.afip.dif.FEV1/FECAESolicitar\"", sent.Action);
        Assert.AreEqual("TOKEN-1", sent.Body.Descendants().Single(e => e.Name.LocalName == "Token").Value);
    }

    [TestMethod]
    public async Task Autorizado_SinClaveDeIdempotencia_NoSeEjecuta()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var arca = host.Arca;

        var (status, body) = await host.Send(Path, Docs.Invoice());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FECAESolicitar"));
    }

    [TestMethod]
    public async Task Autorizado_ConLaMismaClave_PideUnaSolaVezYDevuelveLoMismo()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var arca = host.Arca;

        var (_, first) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");
        var (_, second) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(1, arca.Count("FECAESolicitar"));
        Assert.AreEqual(first!["result"]!.ToJsonString(), second!["result"]!.ToJsonString());

        // La repetición sale del registro de idempotencia, que no guarda el ticket de WSAA.
        Assert.IsNotNull(first["instance_state"]);
        Assert.IsNull(second["instance_state"]);
    }

    [TestMethod]
    public async Task Autorizado_ConLaMismaClaveYOtroDocumento_ConflictoDeIdempotencia()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");
        var other = Docs.Invoice();
        other["document"]!["number"] = 1489;

        var (status, body) = await host.Send(Path, other, key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task NumeroFueraDeSecuencia_EnLasObservaciones_EsIncierto_NuncaRechazo()
    {
        await using var host = await Start(_ => Body(Samples.RejectedWith((10016, "comp. 1488 no coincide con el próximo a autorizar"))));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        var error = body!["error"]!;
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, error["code"]!.GetValue<string>());
        Assert.IsTrue(error["retryable"]!.GetValue<bool>());
        Assert.AreEqual("number_out_of_sequence", error["details"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(10016, error["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
        Assert.Contains("consultarlo", error["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task NumeroFueraDeSecuencia_EnLosErroresGenerales_TambienEsIncierto()
    {
        await using var host = await Start(_ => Body(Samples.ErrorsOnly("FECAESolicitar", (10016, "El numero o fecha del comprobante no se corresponde con el proximo a autorizar"))));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnResultadoIncierto_NoSeGuarda_YElReintentoVuelveAARCA()
    {
        await using var host = await Start(_ => Body(Samples.RejectedWith((10016, "fuera de secuencia"))));
        var arca = host.Arca;

        await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");
        await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(2, arca.Count("FECAESolicitar"));
    }

    [TestMethod]
    public async Task Rechazo_DevuelveRejectedConLosCodigosDeArca()
    {
        await using var host = await Start(_ => Body(Samples.RejectedWith(
            (10048, "El campo Importe Total debe ser igual a la suma de los importes"),
            (10063, "Otro motivo"))));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        var error = body!["error"]!;
        Assert.AreEqual(ErrorCodes.Rejected, error["code"]!.GetValue<string>());
        Assert.IsFalse(error["retryable"]!.GetValue<bool>());
        Assert.AreEqual(10048, error["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
        Assert.AreEqual(10063, error["details"]!["upstream"]![1]!["code"]!.GetValue<int>());
        Assert.Contains("10048", error["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Rechazo_SeGuarda_YLaMismaClaveDaLoMismoSinVolverAARCA()
    {
        await using var host = await Start(_ => Body(Samples.RejectedWith((10048, "mal el total"))));
        var arca = host.Arca;

        var (_, first) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");
        var (_, second) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(1, arca.Count("FECAESolicitar"));

        // El ticket del login viaja en el primer error para que el núcleo lo guarde, y la repetición guardada sale sin
        // él, como toda respuesta repetida del registro de idempotencia.
        Assert.AreEqual(first!["error"]!.ToJsonString(), second!["error"]!.ToJsonString());
        Assert.IsNotNull(first["instance_state"]);
        Assert.IsNull(second["instance_state"]);
    }

    [TestMethod]
    public async Task ErroresGeneralesDeValidacion_SonRechazo()
    {
        await using var host = await Start(_ => Body(Samples.ErrorsOnly(
            "FECAESolicitar",
            (10002, "No coincide la cantidad de registros"),
            (1005, "El punto de venta no se encuentra empadronado"))));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, body!["error"]!["code"]!.GetValue<string>());
        Assert.HasCount(2, body["error"]!["details"]!["upstream"]!.AsArray());
    }

    [TestMethod]
    [DataRow(500)]
    [DataRow(501)]
    [DataRow(502)]
    public async Task ErrorInternoDeArca_EsIncierto(int code)
    {
        await using var host = await Start(_ => Body(Samples.ErrorsOnly("FECAESolicitar", (code, "Error interno"))));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("upstream_internal_error", body["error"]!["details"]!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(600)]
    [DataRow(601)]
    public async Task TokenOCuitNoAceptados_SonDeConfiguracion(int code)
    {
        await using var host = await Start(_ => Body(Samples.ErrorsOnly("FECAESolicitar", (code, "No se corresponden token y firma"))));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ResultadoRechazadoSinMotivo_EsRechazo()
    {
        await using var host = await Start(_ => Body(Samples.RejectedWith()));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AprobadoSinCae_OUnaRespuestaRara_SonInciertos()
    {
        var withoutCae = Samples.Authorized(cae: "").Replace("<CAE></CAE>", string.Empty, StringComparison.Ordinal);
        var partial = Samples.Authorized().Replace("<Resultado>A</Resultado>", "<Resultado>P</Resultado>", StringComparison.Ordinal);
        foreach (var response in new[] { withoutCae, partial, "<FECAESolicitarResponse xmlns=\"" + Samples.FeNs + "\"><FECAESolicitarResult/></FECAESolicitarResponse>" })
        {
            await using var host = await Start(_ => Body(response));

            var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

            Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
            Assert.AreEqual(ErrorCodes.OutcomeUnknown, body!["error"]!["code"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task CorteDespuesDeEnviar_EsIncierto()
    {
        // Vence el plazo, se corta la respuesta o contesta un intermediario con HTML: el pedido ya salió.
        Func<Sent, HttpResponseMessage>[] cuts =
        [
            _ => throw new TaskCanceledException("timeout", new TimeoutException()),
            _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html><body><h1>502 Bad Gateway</h1><br></body></html>", Encoding.UTF8, "text/html") },
            _ => Samples.Fault("soap:Server", "Server was unable to process request."),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><FECAESolicitarRes", Encoding.UTF8, "text/xml") },
        ];
        foreach (var cut in cuts)
        {
            await using var host = await Start(cut);

            var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

            Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
            var error = body!["error"]!;
            Assert.AreEqual(ErrorCodes.OutcomeUnknown, error["code"]!.GetValue<string>());
            Assert.AreEqual("no_response", error["details"]!["reason"]!.GetValue<string>());
        }
    }

    [TestMethod]
    [DataRow(HttpRequestError.ConnectionError)]
    [DataRow(HttpRequestError.NameResolutionError)]
    [DataRow(HttpRequestError.SecureConnectionError)]
    public async Task FallaAntesDeEnviar_NoLlegoAARCA_EsUpstreamUnavailable(HttpRequestError error)
    {
        await using var host = await Start(_ => throw new HttpRequestException(error, "no se pudo conectar"));

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.IsTrue(body["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task SiElLoginFalla_NoSeEnvioNada_Y_EsUpstreamUnavailable()
    {
        var arca = new FakeArca()
            .On("loginCms", _ => Samples.Fault("ns1:coe.alreadyAuthenticated", "El CEE ya posee un TA valido para el acceso al WSN solicitado"))
            .On("FECAESolicitar", _ => Body(Samples.Authorized()));
        await using var host = await ArcaHost.Start(arca);

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FECAESolicitar"));
    }

    [TestMethod]
    public async Task EntradaInvalida_ListaLosCamposYNoLlamaAARCA()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var arca = host.Arca;
        var input = Docs.Invoice();
        input["document"]!["receiver"]!["vat_condition"] = null;
        input["document"]!["amounts"]!["other_taxes"] = "10.00";

        var (status, body) = await host.Send(Path, input, key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        var error = body!["error"]!;
        Assert.AreEqual(ErrorCodes.InvalidInput, error["code"]!.GetValue<string>());
        var fields = error["details"]!["fields"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        CollectionAssert.IsSubsetOf(new[] { "document.receiver.vat_condition", "document.amounts.other_taxes" }, fields);
        Assert.AreEqual(0, arca.Count("loginCms"));
        Assert.AreEqual(0, arca.Count("FECAESolicitar"));
    }

    [TestMethod]
    public async Task UnTipoMalPuestoEnLaEntrada_EsInvalidInputConLaRuta_SinElValor()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var input = Docs.Invoice();
        input["document"]!["number"] = "valor-secreto";

        var (status, body) = await host.Send(Path, input, key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        var json = body!.ToJsonString();
        Assert.Contains("document.number", json);
        Assert.DoesNotContain("valor-secreto", json);
    }

    [TestMethod]
    public async Task AceptaElDocumentoTalComoLoArmaElNucleo()
    {
        // Con la forma que arma FiscalPayload del núcleo: nota de crédito con asociado, renglones y same_currency_payment.
        var (host, arca) = await StartCore(Body(Samples.Authorized()));
        await using var _ = host;
        var input = Docs.Invoice();
        var document = input["document"]!;
        document["type_code"] = 8;
        document["same_currency_payment"] = false;
        document["receiver"] = new JsonObject { ["doc_type"] = 99, ["doc_number"] = "0", ["vat_condition"] = 5, ["name"] = "Consumidor Final" };
        document["associated"] = new JsonArray(new JsonObject { ["type_code"] = 6, ["point_of_sale"] = 3, ["number"] = 1400, ["date"] = "2026-10-01", ["tax_id"] = "20000000001" });
        document["items"] = new JsonArray(new JsonObject { ["description"] = "Hosting", ["quantity"] = "1", ["unit_price"] = "10000", ["total"] = "10000.00" });

        var (status, _) = await host.Send(Path, input, key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var detail = arca.Single("FECAESolicitar").Body.Descendants().Single(e => e.Name.LocalName == "FECAEDetRequest");
        Assert.AreEqual("0", detail.Elements().Single(e => e.Name.LocalName == "DocNro").Value);
        Assert.AreEqual("20000000001", detail.Descendants().Single(e => e.Name.LocalName == "CbteAsoc").Elements().Single(e => e.Name.LocalName == "Cuit").Value);
    }

    [TestMethod]
    public async Task LosCamposQueElModuloNoConoce_SeIgnoran()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var input = Docs.Invoice();
        input["campo_nuevo"] = 1;
        input["document"]!["otro_campo"] = new JsonObject { ["x"] = true };
        input["document"]!["receiver"]!["apodo"] = "x";

        var (status, _) = await host.Send(Path, input, key: "emision-1-1488");

        Assert.AreEqual(HttpStatusCode.OK, status);
    }

    [TestMethod]
    public async Task NadaSecretoSaleEnLosLogsNiEnLasRespuestasDeError()
    {
        var logger = new ListLogger();
        var arca = new FakeArca().On("FECAESolicitar", _ => Body(Samples.Authorized()));
        await using var host = await ArcaHost.Start(arca, logger: logger);
        var (token, sign) = ($"TOKEN-{Guid.NewGuid():N}", $"SIGN-{Guid.NewGuid():N}");
        arca.WithLogin(host.Clock, token: token, sign: sign);

        var (_, ok) = await host.Send(Path, Docs.Invoice(), key: "k1");
        arca.On("FECAESolicitar", _ => throw new TaskCanceledException("timeout", new TimeoutException()));
        var other = Docs.Invoice();
        other["document"]!["number"] = 1489;
        var (_, cut) = await host.Send(Path, other, key: "k2", state: ok!["instance_state"]);
        // Otro CUIT no tiene ticket en memoria: obliga a un login, que falla.
        arca.On("loginCms", _ => Samples.Fault("ns1:cms.cert.untrusted", "Certificado no emitido por AC de confianza"));
        var otherCuit = ArcaHost.Config();
        otherCuit["cuit"] = "20000000002";
        var (_, login) = await host.Send(Path, other, key: "k3", config: otherCuit);
        Assert.AreEqual(ErrorCodes.InvalidConfig, login!["error"]!["code"]!.GetValue<string>());

        string[] secrets = [token, sign, Pki.PrivateKeyPem[40..80], Pki.CertificatePem[40..80]];
        Assert.IsNotEmpty(logger.Lines);
        foreach (var secret in secrets)
        {
            Assert.IsEmpty(logger.Lines.Where(l => l.Contains(secret, StringComparison.Ordinal)), $"Un log filtró {secret[..6]}...");
            Assert.DoesNotContain(secret, cut!.ToJsonString());
            Assert.DoesNotContain(secret, login!.ToJsonString());
            Assert.DoesNotContain(secret, ok["result"]!.ToJsonString());
        }
    }

    [TestMethod]
    public async Task ConfiguracionInvalida_NoFiltraLaClavePrivada()
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var arca = host.Arca;
        var config = ArcaHost.Config();
        var pem = PemEncoding.WriteString("PRIVATE KEY", "TESTIGO-CLAVE-PRIVADA"u8);
        config["private_key_pem"] = pem;

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488", config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, body!["error"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain(pem.Split('\n')[1], body.ToJsonString());
        Assert.DoesNotContain(Pki.PrivateKeyPem[40..80], body.ToJsonString());
        Assert.AreEqual(0, arca.Requests.Count);
    }

    [TestMethod]
    [DataRow("cuit", "123")]
    [DataRow("environment", "staging")]
    public async Task ConfiguracionConValoresQueNoSirven_DiceCualCampo(string field, string value)
    {
        await using var host = await Start(_ => Body(Samples.Authorized()));
        var config = ArcaHost.Config();
        config[field] = value;

        var (status, body) = await host.Send(Path, Docs.Invoice(), key: "emision-1-1488", config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(field, body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    // ---- Factura de exportación (WSFEX) ----

    private static Task<ArcaHost> StartExport(Func<Sent, HttpResponseMessage> authorize) =>
        ArcaHost.StartWithLogin(new FakeArca().On("FEXAuthorize", authorize));

    [TestMethod]
    public async Task Exportacion_Autorizada_UsaWsfexConSuPropioTicket()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized()));
        var arca = host.Arca;

        var (status, body) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("71999999999999", body!["result"]!["authorization_code"]!.GetValue<string>());
        Assert.AreEqual(12, body["result"]!["number"]!.GetValue<long>());
        Assert.IsFalse(body["result"]!["reprocessed"]!.GetValue<bool>());
        Assert.IsNotNull(body["instance_state"]!["tickets"]!["wsfex"]);
        var sent = arca.Single("FEXAuthorize");
        Assert.AreEqual("https://wswhomo.afip.gob.ar/wsfexv1/service.asmx", sent.Url.ToString());
        Assert.AreEqual("\"http://ar.gov.afip.dif.fexv1/FEXAuthorize\"", sent.Action);
    }

    [TestMethod]
    public async Task Exportacion_ConElMismoIdDespuesDeUnCorte_ARCADevuelveElMismoCae()
    {
        // Primer intento: se corta la respuesta. Segundo: ARCA ya lo tenía y contesta Reproceso = S con el mismo CAE.
        var calls = 0;
        await using var host = await StartExport(_ => ++calls == 1
            ? throw new TaskCanceledException("timeout", new TimeoutException())
            : Body(Samples.ExportAuthorized(reprocess: "S")));
        var arca = host.Arca;

        var (firstStatus, first) = await host.Send(Path, Docs.Export(), key: "emision-2-12");
        var (secondStatus, second) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, firstStatus);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, first!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.OK, secondStatus);
        Assert.AreEqual("71999999999999", second!["result"]!["authorization_code"]!.GetValue<string>());
        Assert.IsTrue(second["result"]!["reprocessed"]!.GetValue<bool>());

        // Los dos pedidos llevaron el mismo ID de pedido.
        Assert.IsTrue(arca.Requests.Where(r => r.Body.Name.LocalName == "FEXAuthorize").All(r => r.Body.Descendants().Single(e => e.Name.LocalName == "Id" && e.Parent!.Name.LocalName == "Cmp").Value == "4501"));
    }

    [TestMethod]
    public async Task Exportacion_FueraDeSecuencia_1535_EsIncierto_NuncaRechazo()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportError("FEXAuthorize", 1535, "Verifica que el comprobante ingresado corresponde en secuencia al próximo inmediato a autorizar")));

        var (status, body) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        var error = body!["error"]!;
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, error["code"]!.GetValue<string>());
        Assert.AreEqual("number_out_of_sequence", error["details"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(1535, error["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Exportacion_Rechazada_DevuelveLosCodigos()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportError("FEXAuthorize", 1650, "Campo Cliente obligatorio, no podrá estar vacío")));

        var (status, body) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1650, body["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Exportacion_ResultadoRechazadoConMotivo_EsRechazo()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized(result: "R", reasons: "21").Replace("<Cae>71999999999999</Cae>", "<Cae></Cae>", StringComparison.Ordinal)));

        var (status, body) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(21, body!["error"]!["details"]!["upstream"]![0]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Exportacion_AutorizadaConObservaciones_LasDevuelve()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized(reasons: "17")));

        var (_, body) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(17, body!["result"]!["observations"]![0]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Exportacion_SiARCADevuelveOtroComprobante_ElIdYaEstabaUsado()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized(number: 7, reprocess: "S")));

        var (status, body) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, body!["error"]!["code"]!.GetValue<string>());
        Assert.Contains("ID de pedido 4501", body["error"]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(1000)]
    [DataRow(1001)]
    public async Task Exportacion_TokenOCuitNoAceptados_SonDeConfiguracion(int code)
    {
        await using var host = await StartExport(_ => Body(Samples.ExportError("FEXAuthorize", code, "Usuario no autorizado a realizar esta operación")));

        var (status, body) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(20)]
    [DataRow(21)]
    public async Task Exportacion_UnaNota_EnviaElAsociadoYNoLaFechaDePagoNiCanMisMonExt(int type)
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized(number: 3, type: type)));
        var arca = host.Arca;

        // El núcleo manda payment_due y same_currency_payment en todos los tipos.
        var (status, body) = await host.Send(Path, Docs.ExportNote(type), key: "emision-3-3");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("71999999999999", body!["result"]!["authorization_code"]!.GetValue<string>());
        Assert.AreEqual(type, body["result"]!["type_code"]!.GetValue<int>());
        Assert.AreEqual(3, body["result"]!["number"]!.GetValue<long>());
        Assert.IsNotNull(body["instance_state"]!["tickets"]!["wsfex"]);

        // Va por WSFEX y con Cmps_asoc justo entre Imp_total y Forma_pago.
        var sent = arca.Single("FEXAuthorize");
        Assert.AreEqual("https://wswhomo.afip.gob.ar/wsfexv1/service.asmx", sent.Url.ToString());
        Assert.AreEqual("\"http://ar.gov.afip.dif.fexv1/FEXAuthorize\"", sent.Action);
        var cmp = sent.Body.Element(Wsfex.Ns + "Cmp")!;
        var names = cmp.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.AreEqual(names.IndexOf("Imp_total") + 1, names.IndexOf("Cmps_asoc"));
        Assert.AreEqual(names.IndexOf("Cmps_asoc") + 1, names.IndexOf("Forma_pago"));
        Assert.DoesNotContain("Fecha_pago", names);
        Assert.DoesNotContain("CanMisMonExt", names);
        Assert.AreEqual(type.ToString(System.Globalization.CultureInfo.InvariantCulture), cmp.Element(Wsfex.Ns + "Cbte_Tipo")!.Value);
        var associated = cmp.Element(Wsfex.Ns + "Cmps_asoc")!.Elements().Single();
        CollectionAssert.AreEqual(new[] { "Cbte_tipo", "Cbte_punto_vta", "Cbte_nro", "Cbte_cuit" }, associated.Elements().Select(e => e.Name.LocalName).ToArray());
        CollectionAssert.AreEqual(new[] { "19", "4", "12", "20000000001" }, associated.Elements().Select(e => e.Value).ToArray());
    }

    [TestMethod]
    public async Task Exportacion_UnaNotaSinAsociado_EsInvalidInput_YNoLlamaAARCA()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized(type: 21)));
        var arca = host.Arca;
        var input = Docs.ExportNote();
        ((JsonObject)input["document"]!).Remove("associated");

        var (status, body) = await host.Send(Path, input, key: "emision-3-3");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual("document.associated", body["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("loginCms"));
        Assert.AreEqual(0, arca.Count("FEXAuthorize"));
    }

    [TestMethod]
    public async Task Exportacion_UnaFactura_SigueEnviandoFechaDePagoYCanMisMonExt_YNingunAsociado()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized()));
        var arca = host.Arca;

        var (status, _) = await host.Send(Path, Docs.Export(), key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var cmp = arca.Single("FEXAuthorize").Body.Element(Wsfex.Ns + "Cmp")!;
        Assert.AreEqual("20261105", cmp.Element(Wsfex.Ns + "Fecha_pago")!.Value);
        Assert.AreEqual("N", cmp.Element(Wsfex.Ns + "CanMisMonExt")!.Value);
        Assert.IsNull(cmp.Element(Wsfex.Ns + "Cmps_asoc"));
    }

    [TestMethod]
    public async Task Exportacion_UnaFacturaConAsociado_EsInvalidInput_YNoLlamaAARCA()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized()));
        var arca = host.Arca;
        var input = Docs.Export();
        input["document"]!["associated"] = new JsonArray(new JsonObject { ["type_code"] = 19, ["point_of_sale"] = 4, ["number"] = 11 });

        var (status, body) = await host.Send(Path, input, key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("document.associated", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FEXAuthorize"));
    }

    [TestMethod]
    public async Task Exportacion_SinIdDePedido_EsInvalidInput()
    {
        await using var host = await StartExport(_ => Body(Samples.ExportAuthorized()));
        var arca = host.Arca;
        var input = Docs.Export();
        input.Remove("wsfex_id");

        var (status, body) = await host.Send(Path, input, key: "emision-2-12");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("wsfex_id", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.AreEqual(0, arca.Count("FEXAuthorize"));
    }
}
