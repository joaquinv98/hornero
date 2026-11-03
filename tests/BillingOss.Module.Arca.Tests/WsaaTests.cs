using System.Net;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Arca.Tests;

[TestClass]
public sealed class WsaaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Tra_SigueLaEspecificacionDeArca()
    {
        var root = XElement.Parse(Wsaa.BuildTra("wsfe", T0));

        Assert.AreEqual("loginTicketRequest", root.Name.LocalName);
        Assert.AreEqual("wsfe", root.Element("service")!.Value);
        var header = root.Element("header")!;

        // Hora de Argentina, con generationTime diez minutos atrás y vencimiento diez minutos adelante.
        Assert.AreEqual("2026-10-26T11:50:00-03:00", header.Element("generationTime")!.Value);
        Assert.AreEqual("2026-10-26T12:10:00-03:00", header.Element("expirationTime")!.Value);
        Assert.IsTrue(uint.TryParse(header.Element("uniqueId")!.Value, out _));

        // El manual recomienda no mandar source ni destination.
        Assert.IsNull(header.Element("source"));
        Assert.IsNull(header.Element("destination"));
    }

    [TestMethod]
    public void Cms_EsUnSignedDataConElTraIncluidoYFirmadoConSha256()
    {
        using var certificate = Pki.Load();
        var tra = Wsaa.BuildTra("wsfex", T0);

        var cms = new SignedCms();
        cms.Decode(Convert.FromBase64String(Wsaa.Sign(tra, certificate)));

        cms.CheckSignature(verifySignatureOnly: true);
        Assert.IsFalse(cms.Detached);
        Assert.AreEqual(tra, Encoding.UTF8.GetString(cms.ContentInfo.Content));
        Assert.HasCount(1, cms.SignerInfos);
        var signer = cms.SignerInfos[0];
        Assert.AreEqual("2.16.840.1.101.3.4.2.1", signer.DigestAlgorithm.Value);
        Assert.AreEqual(certificate.Thumbprint, signer.Certificate!.Thumbprint);
        Assert.HasCount(1, cms.Certificates);
    }

    [TestMethod]
    public async Task Login_ArmaElPedidoYGuardaElTicketEnElEstado()
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.Start(arca);
        arca.WithLogin(host.Clock);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var login = arca.Single("loginCms");
        Assert.AreEqual("https://wsaahomo.afip.gov.ar/ws/services/LoginCms", login.Url.ToString());
        Assert.AreEqual("\"\"", login.Action);

        // Lo que viaja en in0 es el TRA firmado, para el servicio wsfe.
        var cms = new SignedCms();
        cms.Decode(Convert.FromBase64String(login.Body.Descendants().Single(e => e.Name.LocalName == "in0").Value));
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.AreEqual("wsfe", XElement.Parse(Encoding.UTF8.GetString(cms.ContentInfo.Content)).Element("service")!.Value);

        var ticket = body!["instance_state"]!["tickets"]!["wsfe"]!;
        Assert.AreEqual("TOKEN-1", ticket["token"]!.GetValue<string>());
        Assert.AreEqual("SIGN-1", ticket["sign"]!.GetValue<string>());
        Assert.AreEqual("20000000001", ticket["cuit"]!.GetValue<string>());
        Assert.AreEqual("homologation", ticket["environment"]!.GetValue<string>());
        Assert.AreEqual(host.Clock.Now.AddHours(12), ticket["expires_at"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task Login_Produccion_VaALaUrlDeProduccion()
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.Start(arca, production: ProductionMode.On);
        arca.WithLogin(host.Clock);

        await host.Send("/v0/instance/test", [], config: ArcaHost.Config("production"));

        Assert.AreEqual("https://wsaa.afip.gov.ar/ws/services/LoginCms", arca.Single("loginCms").Url.ToString());
        Assert.AreEqual("https://servicios1.afip.gov.ar/wsfev1/service.asmx", arca.Single("FEDummy").Url.ToString());
    }

    [TestMethod]
    public async Task Login_NoSePideOtroTicketMientrasElDelEstadoSirva()
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.Start(arca);
        arca.WithLogin(host.Clock);
        var (_, first) = await host.Send("/v0/instance/test", []);

        var (status, second) = await host.Send("/v0/instance/test", [], state: first!["instance_state"]);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1, arca.Count("loginCms"));

        // Sin login nuevo, el estado no cambia y no se devuelve.
        Assert.IsNull(second!["instance_state"]);
    }

    [TestMethod]
    public async Task Login_ConElTicketVencidoOPorVencer_PideUnoNuevo()
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.Start(arca);
        arca.WithLogin(host.Clock);
        var (_, first) = await host.Send("/v0/instance/test", []);

        // Faltan 30 segundos: menos que el margen de renovación.
        host.Clock.Now = host.Clock.Now.AddHours(12).AddSeconds(-30);
        arca.WithLogin(host.Clock, token: "TOKEN-2");
        var (_, second) = await host.Send("/v0/instance/test", [], state: first!["instance_state"]);

        Assert.AreEqual(2, arca.Count("loginCms"));
        Assert.AreEqual("TOKEN-2", second!["instance_state"]!["tickets"]!["wsfe"]!["token"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Login_ConElTicketPorVencerYWsaaQueNoDejaPedirOtro_UsaElQueQueda()
    {
        var arca = new FakeArca()
            .On("FEDummy", Samples.Dummy)
            .On("loginCms", _ => Samples.Fault("ns1:coe.alreadyAuthenticated", "El CEE ya posee un TA valido para el acceso al WSN solicitado"));
        await using var host = await ArcaHost.Start(arca);
        var state = StateWith(host.Clock.Now.AddSeconds(30));

        var (status, body) = await host.Send("/v0/instance/test", [], state: state);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1, arca.Count("loginCms"));
        Assert.IsNull(body!["instance_state"]);
    }

    [TestMethod]
    [DataRow("production", "20000000001")]
    [DataRow("homologation", "30000000007")]
    public async Task Login_ElEstadoDeOtroAmbienteOCuit_NoSeUsa(string environment, string cuit)
    {
        var arca = new FakeArca().On("FEDummy", Samples.Dummy);
        await using var host = await ArcaHost.Start(arca);
        arca.WithLogin(host.Clock);
        var foreign = StateWith(host.Clock.Now.AddHours(6), environment: environment, cuit: cuit);

        await host.Send("/v0/instance/test", [], state: foreign);

        Assert.AreEqual(1, arca.Count("loginCms"));
    }

    [TestMethod]
    public async Task Login_ExportacionPideElServicioWsfex()
    {
        var arca = new FakeArca()
            .On("FEXGetLast_CMP", Samples.ExportLast(11, "20261020"));
        await using var host = await ArcaHost.Start(arca);
        arca.WithLogin(host.Clock);

        await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 4, ["type_code"] = 19 });

        var cms = new SignedCms();
        cms.Decode(Convert.FromBase64String(arca.Single("loginCms").Body.Descendants().Single(e => e.Name.LocalName == "in0").Value));
        Assert.AreEqual("wsfex", XElement.Parse(Encoding.UTF8.GetString(cms.ContentInfo.Content)).Element("service")!.Value);
    }

    [TestMethod]
    public async Task Login_YaHayTicketVigenteYElEstadoSePerdio_EsReintentableYDiceQueHacer()
    {
        var arca = new FakeArca()
            .On("loginCms", _ => Samples.Fault("ns1:coe.alreadyAuthenticated", "El CEE ya posee un TA valido para el acceso al WSN solicitado"));
        await using var host = await ArcaHost.Start(arca, production: ProductionMode.On);

        var (status, body) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 });
        var (_, production) = await host.Send("/v0/fiscal/last_number", new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6 }, config: ArcaHost.Config("production"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        var error = body!["error"]!;
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, error["code"]!.GetValue<string>());
        Assert.IsTrue(error["retryable"]!.GetValue<bool>());
        Assert.Contains("ticket de acceso vigente", error["message"]!.GetValue<string>());
        Assert.Contains("estado de la instancia", error["message"]!.GetValue<string>());

        // Homologación espera 10 minutos entre tickets y producción 2.
        Assert.AreEqual(660, error["retry_after"]!.GetValue<int>());
        Assert.AreEqual(150, production!["error"]!["retry_after"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow("ns1:cms.cert.untrusted", "Certificado no emitido por AC de confianza", HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow("ns1:coe.notAuthorized", "Computador no autorizado a acceder al servicio", HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow("ns1:cms.sign.invalid", "Firma inválida o algoritmo no soportado", HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow("ns1:cms.cert.expired", "Certificado expirado", HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidConfig)]
    [DataRow("ns1:xml.generationTime.invalid", "El tiempo de generación es posterior a la hora actual", HttpStatusCode.InternalServerError, ErrorCodes.Internal)]
    [DataRow("ns1:wsaa.unavailable", "El servicio de autenticación/autorización se encuentra momentáneamente fuera de servicio", HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    [DataRow("ns1:wsn.unavailable", "El servicio al que se desea acceder se encuentra momentáneamente fuera de servicio", HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]

    // Si el faultcode no trae el código, el texto de la tabla del manual igual alcanza.
    [DataRow("soapenv:Server", "El CEE ya posee un TA valido para el acceso al WSN solicitado", HttpStatusCode.ServiceUnavailable, ErrorCodes.UpstreamUnavailable)]
    public async Task Login_MapeaLosErroresDeWsaa(string faultCode, string faultText, HttpStatusCode expectedStatus, string expectedCode)
    {
        var arca = new FakeArca()
            .On("FEDummy", Samples.Dummy)
            .On("loginCms", _ => Samples.Fault(faultCode, faultText));
        await using var host = await ArcaHost.Start(arca);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(expectedStatus, status);
        Assert.AreEqual(expectedCode, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Login_ConUnTicketIlegibleOYaVencido_NoSeUsa()
    {
        var unreadable = "<loginCmsResponse xmlns=\"http://wsaa.view.sua.dvadac.desein.afip.gov\"><loginCmsReturn>no es xml</loginCmsReturn></loginCmsResponse>";
        var arca = new FakeArca().On("FEDummy", Samples.Dummy).On("loginCms", unreadable);
        await using var host = await ArcaHost.Start(arca);

        var (status, body) = await host.Send("/v0/instance/test", []);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());

        // Un ticket que ya venció al llegar delata un reloj corrido.
        arca.On("loginCms", _ => Samples.Login(host.Clock.Now.AddMinutes(-5), "T", "S"));
        (status, body) = await host.Send("/v0/instance/test", []);
        Assert.AreEqual(HttpStatusCode.InternalServerError, status);
        Assert.AreEqual(ErrorCodes.Internal, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Login_SinConexionConWsaa_NoEsDeConfiguracion()
    {
        var arca = new FakeArca().On("loginCms", _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));
        await using var host = await ArcaHost.Start(arca);

        var (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL" });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Login_UnTicketQueSeObtuvoYLaLlamadaFallaDespues_NoSePierde()
    {
        // Un error del protocolo no puede llevar instance_state: el ticket queda en memoria hasta que el núcleo lo guarde.
        var arca = new FakeArca().On("FECompConsultar", Samples.ErrorsOnly("FECompConsultar", (602, "No existen datos en nuestros registros")));
        await using var host = await ArcaHost.Start(arca);
        arca.WithLogin(host.Clock);
        var input = new JsonObject { ["point_of_sale"] = 3, ["type_code"] = 6, ["number"] = 99 };

        var (first, _) = await host.Send("/v0/fiscal/find", input);
        var (second, _) = await host.Send("/v0/fiscal/find", input);

        Assert.AreEqual(HttpStatusCode.NotFound, first);
        Assert.AreEqual(HttpStatusCode.NotFound, second);
        Assert.AreEqual(1, arca.Count("loginCms"));
    }

    /// <summary>Un instance_state con un ticket de wsfe que vence en la fecha dada.</summary>
    private static JsonObject StateWith(DateTimeOffset expires, string environment = "homologation", string cuit = "20000000001") => new()
    {
        ["tickets"] = new JsonObject
        {
            ["wsfe"] = new JsonObject
            {
                ["token"] = "TOKEN-VIEJO",
                ["sign"] = "SIGN-VIEJO",
                ["expires_at"] = expires.ToString("O"),
                ["cuit"] = cuit,
                ["environment"] = environment,
            },
        },
    };
}
