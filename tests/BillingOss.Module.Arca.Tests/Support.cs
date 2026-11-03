using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Module.Arca.Tests;

/// <summary>Reloj que el test mueve a mano.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Certificado autofirmado de prueba. Se genera una vez por corrida y nunca sale del proceso.</summary>
internal static class Pki
{
    private static readonly Lazy<(string Certificate, string Key)> Pair = new(Generate);

    public static string CertificatePem => Pair.Value.Certificate;

    public static string PrivateKeyPem => Pair.Value.Key;

    /// <summary>Un certificado con su clave, para verificar firmas.</summary>
    public static X509Certificate2 Load() => X509Certificate2.CreateFromPem(CertificatePem, PrivateKeyPem);

    private static (string, string) Generate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=arca-prueba, SERIALNUMBER=CUIT 20000000001, O=Prueba SA, C=AR",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }
}

/// <summary>Logger que guarda lo que se escribe, para comprobar que no filtra secretos.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<string> lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines)
            {
                return [.. lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (lines)
        {
            lines.Add(formatter(state, exception) + exception);
        }
    }
}

/// <summary>Un pedido SOAP que el módulo mandó a la ARCA falsa.</summary>
internal sealed record Sent(Uri Url, string Action, XElement Body);

/// <summary>ARCA falsa: contesta con las respuestas sintéticas que arma cada test y anota lo que recibe.</summary>
internal sealed class FakeArca : HttpMessageHandler
{
    private readonly Dictionary<string, Func<Sent, HttpResponseMessage>> routes = [];
    private readonly List<Sent> requests = [];

    public IReadOnlyList<Sent> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>La respuesta a una operación, por el nombre del elemento del pedido (loginCms, FECAESolicitar...).</summary>
    public FakeArca On(string operation, Func<Sent, HttpResponseMessage> respond)
    {
        routes[operation] = respond;
        return this;
    }

    public FakeArca On(string operation, string soapBody) => On(operation, _ => Samples.Soap(soapBody));

    /// <summary>Responde loginCms con un ticket que vence en 12 horas desde el reloj dado.</summary>
    public FakeArca WithLogin(TestClock clock, string token = "TOKEN-1", string sign = "SIGN-1") =>
        On("loginCms", _ => Samples.Login(clock.Now.AddHours(12), token, sign));

    public int Count(string operation) => Requests.Count(r => r.Body.Name.LocalName == operation);

    public Sent Single(string operation) => Requests.Single(r => r.Body.Name.LocalName == operation);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
        var body = Xml.Parse(raw).Root!.Child("Body")!.Elements().First();
        var sent = new Sent(request.RequestUri!, request.Headers.GetValues("SOAPAction").Single(), body);
        lock (requests)
        {
            requests.Add(sent);
        }

        return routes.TryGetValue(body.Name.LocalName, out var respond)
            ? respond(sent)
            : throw new InvalidOperationException($"La ARCA falsa no tiene respuesta para {body.Name.LocalName}.");
    }
}

/// <summary>El módulo corriendo en un TestServer, con la ARCA falsa del otro lado. Firma los pedidos como el núcleo.</summary>
internal sealed class ArcaHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-prueba");

    private readonly WebApplication app;
    private readonly string db;

    private ArcaHost(WebApplication app, string db, FakeArca arca, TestClock clock)
    {
        this.app = app;
        this.db = db;
        Arca = arca;
        Clock = clock;
        Client = app.GetTestClient();
    }

    public FakeArca Arca { get; }

    public TestClock Clock { get; }

    public HttpClient Client { get; }

    /// <summary><paramref name="production"/> es la traba del proceso (Arca:Production), en off salvo que la prueba la mueva.</summary>
    public static async Task<ArcaHost> Start(FakeArca arca, TestClock? clock = null, HttpClient? http = null, ILogger? logger = null, ProductionMode production = ProductionMode.Off)
    {
        clock ??= new TestClock(new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.Zero));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"arca-idem-{Guid.NewGuid():N}.db");
        app.MapModule(
            ArcaModule.Create(http ?? new HttpClient(arca), clock, logger ?? NullLogger.Instance, production),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new ArcaHost(app, db, arca, clock);
    }

    /// <summary>Arranca el módulo y deja que la ARCA falsa responda los logins de WSAA.</summary>
    public static async Task<ArcaHost> StartWithLogin(FakeArca arca, ProductionMode production = ProductionMode.Off)
    {
        var host = await Start(arca, production: production);
        arca.WithLogin(host.Clock);
        return host;
    }

    public static JsonObject Config(string environment = "homologation") => new()
    {
        ["cuit"] = "20000000001",
        ["environment"] = environment,
        ["certificate_pem"] = Pki.CertificatePem,
        ["private_key_pem"] = Pki.PrivateKeyPem,
    };

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(
        string path,
        JsonObject input,
        string? key = null,
        JsonNode? state = null,
        JsonObject? config = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = (config ?? Config()).DeepClone(), ["state"] = state?.DeepClone() },
            ["context"] = new JsonObject(),
            ["input"] = input.DeepClone(),
        };
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());

        // El reloj de la firma es el real, no el de TestClock: la SDK compara contra su propio reloj.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new ByteArrayContent(bytes) };
        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        if (key is not null)
        {
            request.Headers.Add(ModuleHeaders.IdempotencyKey, key);
        }

        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Secret, timestamp, "POST", path, requestId, key, bytes));
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    /// <summary>GET firmado, para el manifiesto.</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Secret, timestamp, "GET", path, requestId, null, []));
        using var response = await Client.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        File.Delete(db);
    }
}

/// <summary>Respuestas SOAP sintéticas, escritas siguiendo los manuales de ARCA (WSAA 1.2.2, WSFEv1 4.7 y WSFEX 3.1.1).</summary>
internal static class Samples
{
    private static readonly XNamespace WsaaNs = "http://wsaa.view.sua.dvadac.desein.afip.gov";

    public static HttpResponseMessage Soap(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(
            $"""<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>{body}</soap:Body></soap:Envelope>""",
            Encoding.UTF8,
            "text/xml"),
    };

    public static HttpResponseMessage Fault(string code, string text) => Soap(
        $"<soap:Fault><faultcode>{code}</faultcode><faultstring>{text}</faultstring></soap:Fault>",
        HttpStatusCode.InternalServerError);

    /// <summary>loginCmsResponse: el loginTicketResponse va como texto dentro de loginCmsReturn.</summary>
    public static HttpResponseMessage Login(DateTimeOffset expires, string token, string sign)
    {
        var ticket = new XElement(
            "loginTicketResponse",
            new XAttribute("version", "1.0"),
            new XElement(
                "header",
                new XElement("source", "CN=wsaahomo, O=AFIP, C=AR, SERIALNUMBER=CUIT 33693450239"),
                new XElement("destination", "SERIALNUMBER=CUIT 20000000001, CN=arca-prueba"),
                new XElement("uniqueId", 3866895167),
                new XElement("generationTime", expires.AddHours(-12).ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)),
                new XElement("expirationTime", expires.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture))),
            new XElement("credentials", new XElement("token", token), new XElement("sign", sign)));
        var text = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + ticket.ToString(SaveOptions.DisableFormatting);
        return Soap(new XElement(WsaaNs + "loginCmsResponse", new XElement(WsaaNs + "loginCmsReturn", text)).ToString(SaveOptions.DisableFormatting));
    }

    // ---- WSFEv1 ----

    public const string FeNs = "http://ar.gov.afip.dif.FEV1/";

    public const string Dummy = $"""<FEDummyResponse xmlns="{FeNs}"><FEDummyResult><AppServer>OK</AppServer><DbServer>OK</DbServer><AuthServer>OK</AuthServer></FEDummyResult></FEDummyResponse>""";

    public static string LastAuthorized(long number) =>
        $"""<FECompUltimoAutorizadoResponse xmlns="{FeNs}"><FECompUltimoAutorizadoResult><PtoVta>3</PtoVta><CbteTipo>6</CbteTipo><CbteNro>{number}</CbteNro></FECompUltimoAutorizadoResult></FECompUltimoAutorizadoResponse>""";

    /// <summary>FECAESolicitarResponse con un comprobante autorizado, como el ejemplo 1 del manual.</summary>
    public static string Authorized(long number = 1488, string cae = "71234567890123", string observations = "") =>
        $"""
        <FECAESolicitarResponse xmlns="{FeNs}"><FECAESolicitarResult>
        <FeCabResp><Cuit>20000000001</Cuit><PtoVta>3</PtoVta><CbteTipo>6</CbteTipo><FchProceso>20261026120000</FchProceso><CantReg>1</CantReg><Resultado>A</Resultado><Reproceso>N</Reproceso></FeCabResp>
        <FeDetResp><FECAEDetResponse><Concepto>2</Concepto><DocTipo>96</DocTipo><DocNro>30111222</DocNro><CbteDesde>{number}</CbteDesde><CbteHasta>{number}</CbteHasta><CbteFch>20261025</CbteFch><Resultado>A</Resultado><CAE>{cae}</CAE><CAEFchVto>20261105</CAEFchVto>{observations}</FECAEDetResponse></FeDetResp>
        </FECAESolicitarResult></FECAESolicitarResponse>
        """;

    /// <summary>Comprobante rechazado con observaciones, como el ejemplo 2 del manual.</summary>
    public static string RejectedWith(params (int Code, string Message)[] observations) =>
        $"""
        <FECAESolicitarResponse xmlns="{FeNs}"><FECAESolicitarResult>
        <FeCabResp><Cuit>20000000001</Cuit><PtoVta>3</PtoVta><CbteTipo>6</CbteTipo><FchProceso>20261026120000</FchProceso><CantReg>1</CantReg><Resultado>R</Resultado><Reproceso>N</Reproceso></FeCabResp>
        <FeDetResp><FECAEDetResponse><Concepto>2</Concepto><DocTipo>96</DocTipo><DocNro>30111222</DocNro><CbteDesde>1488</CbteDesde><CbteHasta>1488</CbteHasta><CbteFch>20261025</CbteFch><Resultado>R</Resultado><CAE></CAE><CAEFchVto></CAEFchVto>
        <Observaciones>{string.Concat(observations.Select(o => $"<Obs><Code>{o.Code}</Code><Msg>{o.Message}</Msg></Obs>"))}</Observaciones></FECAEDetResponse></FeDetResp>
        </FECAESolicitarResult></FECAESolicitarResponse>
        """;

    /// <summary>Errores generales, sin detalle: ejemplo 3 del manual o los de infraestructura.</summary>
    public static string ErrorsOnly(string operation, params (int Code, string Message)[] errors) =>
        $"""<{operation}Response xmlns="{FeNs}"><{operation}Result><Errors>{string.Concat(errors.Select(e => $"<Err><Code>{e.Code}</Code><Msg>{e.Message}</Msg></Err>"))}</Errors></{operation}Result></{operation}Response>""";

    public static string Table(string operation, string item, params (string Id, string Description, string From, string To)[] rows) =>
        $"""<{operation}Response xmlns="{FeNs}"><{operation}Result><ResultGet>{string.Concat(rows.Select(r => $"<{item}><Id>{r.Id}</Id><Desc>{r.Description}</Desc><FchDesde>{r.From}</FchDesde><FchHasta>{r.To}</FchHasta></{item}>"))}</ResultGet></{operation}Result></{operation}Response>""";

    public const string VatConditions =
        $"""<FEParamGetCondicionIvaReceptorResponse xmlns="{FeNs}"><FEParamGetCondicionIvaReceptorResult><ResultGet><CondicionIvaReceptor><Id>1</Id><Desc>IVA Responsable Inscripto</Desc><Cmp_Clase>A</Cmp_Clase></CondicionIvaReceptor><CondicionIvaReceptor><Id>5</Id><Desc>Consumidor Final</Desc><Cmp_Clase>B</Cmp_Clase></CondicionIvaReceptor></ResultGet></FEParamGetCondicionIvaReceptorResult></FEParamGetCondicionIvaReceptorResponse>""";

    /// <summary>FEParamGetPtosVenta (manual 4.7): número, tipo de emisión, bloqueado y fecha de baja.</summary>
    public static string PointsOfSale(params (int Number, string Emission, string Blocked, string RetiredOn)[] rows) =>
        $"""<FEParamGetPtosVentaResponse xmlns="{FeNs}"><FEParamGetPtosVentaResult><ResultGet>{string.Concat(rows.Select(r => $"<PtoVenta><Nro>{r.Number}</Nro><EmisionTipo>{r.Emission}</EmisionTipo><Bloqueado>{r.Blocked}</Bloqueado><FchBaja>{r.RetiredOn}</FchBaja></PtoVenta>"))}</ResultGet></FEParamGetPtosVentaResult></FEParamGetPtosVentaResponse>""";

    public static string ExportPointsOfSale(params (int Number, string Blocked)[] rows) =>
        $"""<FEXGetPARAM_PtoVentaResponse xmlns="{FexNs}"><FEXGetPARAM_PtoVentaResult><FEXResultGet>{string.Concat(rows.Select(r => $"<ClsFEXResponse_PtoVenta><Pve_Nro>{r.Number}</Pve_Nro><Pve_Bloqueado>{r.Blocked}</Pve_Bloqueado><Pve_FchBaja></Pve_FchBaja></ClsFEXResponse_PtoVenta>"))}</FEXResultGet><FEXErr><ErrCode>0</ErrCode><ErrMsg>OK</ErrMsg></FEXErr></FEXGetPARAM_PtoVentaResult></FEXGetPARAM_PtoVentaResponse>""";

    /// <summary>Las cinco tablas de siempre más tributos y puntos de venta, para una llamada a parameters que las pida todas.</summary>
    public static FakeArca WithParameters(this FakeArca arca) => arca
        .On("FEParamGetTiposCbte", Table("FEParamGetTiposCbte", "CbteTipo", ("1", "Factura A", "20100917", "NULL"), ("6", "Factura B", "20100917", "NULL")))
        .On("FEParamGetTiposDoc", Table("FEParamGetTiposDoc", "DocTipo", ("80", "CUIT", "20080725", "NULL"), ("96", "DNI", "20080725", "NULL")))
        .On("FEParamGetTiposIva", Table("FEParamGetTiposIva", "IvaTipo", ("5", "21%", "20090220", "NULL"), ("6", "27%", "20090220", "NULL")))
        .On("FEParamGetTiposMonedas", Table("FEParamGetTiposMonedas", "Moneda", ("PES", "Pesos Argentinos", "20090403", "NULL")))
        .On("FEParamGetCondicionIvaReceptor", VatConditions)
        .On("FEParamGetTiposTributos", Table("FEParamGetTiposTributos", "TributoTipo", ("13", "Percepción de IVA No Categorizado según la RG 2126/2006", "20100917", "NULL")))
        .On("FEParamGetPtosVenta", PointsOfSale((3, "CAE", "N", "NULL"), (9, "CAEA", "S", "20250101")));

    // ---- Padrón ----

    public const string PadronNs = "http://a5.soap.ws.server.puc.sr/";

    /// <summary>getPersona_v2 de la constancia de inscripción: un responsable inscripto (impuesto 30) con domicilio fiscal.</summary>
    public static string Persona(string taxId = "30712345678", string? company = "PRUEBA SA", string taxes = "<impuesto><descripcionImpuesto>IVA</descripcionImpuesto><idImpuesto>30</idImpuesto><periodo>201311</periodo></impuesto>", string extra = "") =>
        $"""
        <ns2:getPersona_v2Response xmlns:ns2="{PadronNs}"><personaReturn>
        <datosGenerales>{(company is null ? "<apellido>PEREZ</apellido><nombre>JUAN</nombre>" : $"<razonSocial>{company}</razonSocial>")}<domicilioFiscal><codPostal>1425</codPostal><descripcionProvincia>CIUDAD AUTONOMA BUENOS AIRES</descripcionProvincia><direccion>AV SIEMPREVIVA 742</direccion><idProvincia>0</idProvincia><localidad>CIUDAD AUTONOMA BUENOS AIRES</localidad><tipoDomicilio>FISCAL</tipoDomicilio></domicilioFiscal><estadoClave>ACTIVO</estadoClave><idPersona>{taxId}</idPersona><tipoClave>CUIT</tipoClave><tipoPersona>{(company is null ? "FISICA" : "JURIDICA")}</tipoPersona></datosGenerales>
        <datosRegimenGeneral><actividad><descripcionActividad>SERVICIOS DE CONSULTORES EN INFORMATICA</descripcionActividad><idActividad>620100</idActividad><nomenclador>883</nomenclador><orden>1</orden><periodo>201311</periodo></actividad>{taxes}</datosRegimenGeneral>{extra}
        <metadata><fechaHora>2026-10-29T10:00:00.000-03:00</fechaHora><servidor>setiwsh2</servidor></metadata>
        </personaReturn></ns2:getPersona_v2Response>
        """;

    public static string Exchange(string currency, string rate, string date) =>
        $"""<FEParamGetCotizacionResponse xmlns="{FeNs}"><FEParamGetCotizacionResult><ResultGet><MonId>{currency}</MonId><MonCotiz>{rate}</MonCotiz><FchCotiz>{date}</FchCotiz></ResultGet></FEParamGetCotizacionResult></FEParamGetCotizacionResponse>""";

    /// <summary>FECompConsultar, con los datos del ejemplo del manual adaptados a un comprobante de servicios.</summary>
    public static string Found(long number = 1488, string date = "20261025", string total = "12100", string docNumber = "30111222") =>
        $"""
        <FECompConsultarResponse xmlns="{FeNs}"><FECompConsultarResult><ResultGet>
        <Concepto>2</Concepto><DocTipo>96</DocTipo><DocNro>{docNumber}</DocNro><CbteDesde>{number}</CbteDesde><CbteHasta>{number}</CbteHasta><CbteFch>{date}</CbteFch>
        <CondicionIvaReceptorId>5</CondicionIvaReceptorId><ImpTotal>{total}</ImpTotal><ImpTotConc>0</ImpTotConc><ImpNeto>10000</ImpNeto><ImpOpEx>0</ImpOpEx><ImpTrib>0</ImpTrib><ImpIVA>2100</ImpIVA>
        <FchServDesde>20261001</FchServDesde><FchServHasta>20261031</FchServHasta><FchVtoPago>20261105</FchVtoPago><MonId>PES</MonId><MonCotiz>1</MonCotiz>
        <Iva><AlicIva><Id>5</Id><BaseImp>10000</BaseImp><Importe>2100</Importe></AlicIva></Iva>
        <Resultado>A</Resultado><CodAutorizacion>71234567890123</CodAutorizacion><EmisionTipo>CAE</EmisionTipo><FchVto>20261105</FchVto><FchProceso>20261025101500</FchProceso><PtoVta>3</PtoVta><CbteTipo>6</CbteTipo>
        </ResultGet></FECompConsultarResult></FECompConsultarResponse>
        """;

    // ---- WSFEX ----

    public const string FexNs = "http://ar.gov.afip.dif.fexv1/";

    /// <summary>FEXAuthorizeResponse. Reproceso S es el mismo pedido ya autorizado antes.</summary>
    public static string ExportAuthorized(long number = 12, string reprocess = "N", string result = "A", string reasons = "", int type = 19) =>
        $"""
        <FEXAuthorizeResponse xmlns="{FexNs}"><FEXAuthorizeResult>
        <FEXResultAuth><Id>4501</Id><Cuit>20000000001</Cuit><Cae>71999999999999</Cae><Cbte_Tipo>{type}</Cbte_Tipo><Punto_vta>4</Punto_vta><Cbte_nro>{number}</Cbte_nro><Fch_venc_Cae>20261105</Fch_venc_Cae><Fch_cbte>20261025</Fch_cbte><Resultado>{result}</Resultado><Reproceso>{reprocess}</Reproceso><Motivos_Obs>{reasons}</Motivos_Obs></FEXResultAuth>
        <FEXErr><ErrCode>0</ErrCode><ErrMsg>OK</ErrMsg></FEXErr><FEXEvents><EventCode>0</EventCode><EventMsg>OK</EventMsg></FEXEvents>
        </FEXAuthorizeResult></FEXAuthorizeResponse>
        """;

    public static string ExportError(string operation, int code, string message) =>
        $"""<{operation}Response xmlns="{FexNs}"><{operation}Result><FEXErr><ErrCode>{code}</ErrCode><ErrMsg>{message}</ErrMsg></FEXErr><FEXEvents><EventCode>0</EventCode><EventMsg>OK</EventMsg></FEXEvents></{operation}Result></{operation}Response>""";

    public static string ExportLast(long number, string date) =>
        $"""<FEXGetLast_CMPResponse xmlns="{FexNs}"><FEXGetLast_CMPResult><FEXResult_LastCMP><Cbte_nro>{number}</Cbte_nro><Cbte_fecha>{date}</Cbte_fecha></FEXResult_LastCMP><FEXErr><ErrCode>0</ErrCode><ErrMsg>OK</ErrMsg></FEXErr></FEXGetLast_CMPResult></FEXGetLast_CMPResponse>""";

    /// <summary>FEXGetLast_IDResponse, con la forma del manual 3.1.1 (sección 2.3.3).</summary>
    public static string ExportLastId(long id) =>
        $"""<FEXGetLast_IDResponse xmlns="{FexNs}"><FEXGetLast_IDResult><FEXResultGet><Id>{id}</Id></FEXResultGet><FEXErr><ErrCode>0</ErrCode><ErrMsg>OK</ErrMsg></FEXErr><FEXEvents><EventCode>0</EventCode><EventMsg>OK</EventMsg></FEXEvents></FEXGetLast_IDResult></FEXGetLast_IDResponse>""";

    /// <summary>FEXGetPARAM_CtzResponse, con la forma del manual 3.1.1 (sección 2.13.3): Mon_ctz y Mon_fecha.</summary>
    public static string ExportExchange(string rate, string date) =>
        $"""<FEXGetPARAM_CtzResponse xmlns="{FexNs}"><FEXGetPARAM_CtzResult><FEXResultGet><Mon_ctz>{rate}</Mon_ctz><Mon_fecha>{date}</Mon_fecha></FEXResultGet><FEXErr><ErrCode>0</ErrCode><ErrMsg>OK</ErrMsg></FEXErr><FEXEvents><EventCode>0</EventCode><EventMsg>OK</EventMsg></FEXEvents></FEXGetPARAM_CtzResult></FEXGetPARAM_CtzResponse>""";

    public static string ExportFound(long number = 12) =>
        $"""
        <FEXGetCMPResponse xmlns="{FexNs}"><FEXGetCMPResult><FEXResultGet>
        <Id>4501</Id><Fecha_cbte>20261025</Fecha_cbte><Cbte_Tipo>19</Cbte_Tipo><Punto_vta>4</Punto_vta><Cbte_nro>{number}</Cbte_nro><Tipo_expo>2</Tipo_expo><Permiso_existente></Permiso_existente>
        <Dst_cmp>203</Dst_cmp><Cliente>Joao Da Silva</Cliente><Cuit_pais_cliente>50000000016</Cuit_pais_cliente><Domicilio_cliente>Rua 76 km 34.5 Alagoas</Domicilio_cliente><Id_impositivo>PJ54482221-l</Id_impositivo>
        <Moneda_Id>DOL</Moneda_Id><Moneda_ctz>1450.25</Moneda_ctz><Imp_total>120.00</Imp_total><Forma_pago>Transferencia</Forma_pago><Idioma_cbte>2</Idioma_cbte>
        <Fecha_cbte_cae>20261025</Fecha_cbte_cae><Fch_venc_Cae>20261105</Fch_venc_Cae><Cae>71999999999999</Cae><Resultado>A</Resultado><Motivos_Obs></Motivos_Obs><Fecha_pago>20261105</Fecha_pago>
        </FEXResultGet><FEXErr><ErrCode>0</ErrCode><ErrMsg>OK</ErrMsg></FEXErr></FEXGetCMPResult></FEXGetCMPResponse>
        """;
}

/// <summary>Documentos de ejemplo para authorize.</summary>
internal static class Docs
{
    /// <summary>El ejemplo de la sección 9.4 del protocolo, con la condición de IVA de un consumidor final.</summary>
    public static JsonObject Invoice() => (JsonObject)JsonNode.Parse("""
        {
          "document": {
            "point_of_sale": 3,
            "type_code": 6,
            "number": 1488,
            "date": "2026-10-25",
            "concept": "services",
            "service_period": { "from": "2026-10-01", "to": "2026-10-31" },
            "payment_due": "2026-11-05",
            "receiver": { "doc_type": 96, "doc_number": "30111222", "vat_condition": 5, "name": "Cliente de ejemplo" },
            "currency": "PES",
            "exchange_rate": "1",
            "amounts": {
              "net_taxed": "10000.00",
              "net_untaxed": "0.00",
              "exempt": "0.00",
              "vat": [{ "rate_id": 5, "base": "10000.00", "amount": "2100.00" }],
              "other_taxes": "0.00",
              "total": "12100.00"
            },
            "associated": [],
            "optional": []
          }
        }
        """)!;

    /// <summary>Factura de exportación de servicios en dólares, con el ID de pedido de WSFEX.</summary>
    public static JsonObject Export() => (JsonObject)JsonNode.Parse("""
        {
          "wsfex_id": 4501,
          "document": {
            "point_of_sale": 4,
            "type_code": 19,
            "number": 12,
            "date": "2026-10-25",
            "concept": "services",
            "payment_due": "2026-11-05",
            "receiver": { "name": "Joao Da Silva", "address": "Rua 76 km 34.5 Alagoas", "country_code": 203, "country_tax_id": 50000000016, "tax_id": "PJ54482221-l" },
            "currency": "DOL",
            "exchange_rate": "1450.25",
            "same_currency_payment": false,
            "payment_terms": "Transferencia",
            "language": "en",
            "amounts": { "total": "120.00" },
            "items": [
              { "code": "HOST1", "description": "Hosting mensual", "quantity": "1", "unit_code": 7, "unit_price": "100.00", "total": "100.00" },
              { "description": "Soporte", "quantity": "1", "unit_code": 7, "unit_price": "20.00", "total": "20.00" }
            ]
          }
        }
        """)!;

    /// <summary>
    /// Nota de débito (20) o de crédito (21) de exportación de servicios sobre la factura 4-12 del ejemplo. Trae payment_due y
    /// same_currency_payment como los manda el núcleo para todos los tipos: el módulo no los tiene que enviar.
    /// </summary>
    public static JsonObject ExportNote(int type = 21)
    {
        var input = Export();
        input["wsfex_id"] = 4502;
        var document = (JsonObject)input["document"]!;
        document["type_code"] = type;
        document["number"] = 3;
        document["date"] = "2026-10-26";
        document["same_currency_payment"] = true;
        document["amounts"] = new JsonObject { ["total"] = "20.00" };
        document["items"] = new JsonArray(new JsonObject { ["description"] = "Soporte", ["quantity"] = "1", ["unit_code"] = 7, ["unit_price"] = "20.00", ["total"] = "20.00" });
        document["associated"] = new JsonArray(new JsonObject { ["type_code"] = 19, ["point_of_sale"] = 4, ["number"] = 12, ["tax_id"] = "20000000001", ["date"] = "2026-10-25" });
        return input;
    }

    public static AuthorizeInput Parse(JsonObject input) => input.Deserialize<AuthorizeInput>(ModuleJson.Options)!;
}
