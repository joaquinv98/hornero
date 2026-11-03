using System.Net;
using System.Net.Sockets;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Hestia.Tests;

/// <summary>
/// El cliente HTTP de verdad contra una Hestia falsa detrás de Kestrel con HTTPS: validación del certificado, huella,
/// conexión rechazada y redirecciones.
/// </summary>
[TestClass]
public sealed class TransportTests
{
    /// <summary>El cliente que usa el módulo en producción, sin el caché, para que cada test arme el suyo.</summary>
    private static readonly Func<string?, HttpClient> RealHttp = fingerprint => HestiaHttp.Build(fingerprint ?? string.Empty);

    [TestMethod]
    public async Task CertificadoNoConfiable_SeRechaza_SalvoQueCoincidaLaHuella()
    {
        using var certificate = TlsHestia.Certificate();
        var hestia = new FakeHestia().WithAccount();
        await using var server = await TlsHestia.Start(hestia, certificate);
        await using var host = await HestiaHost.Start(hestia, RealHttp);
        var config = HestiaHost.Config("127.0.0.1", server.Port);

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(body));
        CollectionAssert.AreEqual(new[] { "tls_fingerprint" }, HestiaHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(hestia.Calls, "Sin un certificado válido no sale ningún pedido con la access key.");

        // Con otra huella tampoco.
        config["tls_fingerprint"] = new string('A', 64);
        (_, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual(ErrorCodes.InvalidConfig, HestiaHost.Code(body));

        // Con la huella del certificado, sí. Se acepta con dos puntos entre los bytes, como la muestra openssl.
        config["tls_fingerprint"] = TlsHestia.Fingerprint(certificate);
        (status, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());

        (status, body) = await host.Op("suspend", HestiaHost.Existing(), config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task ConexionRechazada_EsUpstreamUnavailable_AunEnUnMutador()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var host = await HestiaHost.Start(new FakeHestia(), RealHttp);
        var config = HestiaHost.Config("127.0.0.1", port);

        foreach (var operation in new[] { "get_status", "suspend", "terminate" })
        {
            var (status, body) = await host.Op(operation, HestiaHost.Existing(), config: config);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status, operation);
            Assert.AreEqual(ErrorCodes.UpstreamUnavailable, HestiaHost.Code(body), operation);
            Assert.AreEqual("no_connection", HestiaHost.Reason(body), operation);
        }
    }

    [TestMethod]
    public async Task UnaRedireccion_NoSeSigue()
    {
        using var certificate = TlsHestia.Certificate();
        var hestia = new FakeHestia().WithAccount().On("v-list-users", 302, string.Empty);
        await using var server = await TlsHestia.Start(hestia, certificate);
        await using var host = await HestiaHost.Start(hestia, RealHttp);
        var config = HestiaHost.Config("127.0.0.1", server.Port);
        config["tls_fingerprint"] = TlsHestia.Fingerprint(certificate);

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, HestiaHost.Code(body));
        Assert.AreEqual(0, server.Strays, "La access key no tiene que viajar al destino de la redirección.");
    }
}
