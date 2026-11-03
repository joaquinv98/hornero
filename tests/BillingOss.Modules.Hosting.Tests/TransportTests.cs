using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.Modules.Hosting.Tests;

/// <summary>
/// El cliente HTTP de los paneles contra un Kestrel con HTTPS y un certificado autofirmado generado en la prueba:
/// validación del certificado, huella, conexión rechazada, redirecciones y cortes.
/// </summary>
[TestClass]
public sealed class TransportTests
{
    [TestMethod]
    public async Task CertificadoNoConfiable_SeRechaza_SalvoQueCoincidaLaHuella()
    {
        using var certificate = Certificate();
        await using var server = await TlsServer.Start(certificate);

        var refused = await Assert.ThrowsExactlyAsync<ModuleException>(() => Transport(string.Empty).Read("prueba", () => Get(server, "/ok")));
        Assert.AreEqual(ErrorCodes.InvalidConfig, refused.Code);
        Assert.AreEqual("""["tls_fingerprint"]""", refused.Details!["fields"]!.ToJsonString());

        var other = await Assert.ThrowsExactlyAsync<ModuleException>(() => Transport(new string('A', 64)).Read("prueba", () => Get(server, "/ok")));
        Assert.AreEqual(ErrorCodes.InvalidConfig, other.Code);

        var reply = await Transport(Fingerprint(certificate)).Read("prueba", () => Get(server, "/ok"));
        Assert.AreEqual(200, reply.Status);
        Assert.AreEqual("hola", reply.Body);
        Assert.AreEqual(1, server.Hits);
    }

    [TestMethod]
    public async Task UnaRedireccion_NoSeSigue()
    {
        using var certificate = Certificate();
        await using var server = await TlsServer.Start(certificate);
        var transport = Transport(Fingerprint(certificate));

        var cut = await Assert.ThrowsExactlyAsync<ModuleException>(() => transport.Read("prueba", () => Get(server, "/redireccion")));
        var lost = await transport.Mutate("prueba", () => Get(server, "/redireccion"));

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, cut.Code);
        Assert.IsNull(lost, "Sin seguir la redirección no se sabe si el cambio corrió.");
        Assert.AreEqual(0, server.Strays, "Las credenciales no tienen que viajar al destino de la redirección.");
    }

    [TestMethod]
    public async Task UnCorteDespuesDeMandar_EsSinRespuestaEnUnCambio_YNoDisponibleEnUnaLectura()
    {
        using var certificate = Certificate();
        await using var server = await TlsServer.Start(certificate);
        var transport = Transport(Fingerprint(certificate));

        Assert.IsNull(await transport.Mutate("prueba", () => Get(server, "/corte")));
        var read = await Assert.ThrowsExactlyAsync<ModuleException>(() => transport.Read("prueba", () => Get(server, "/corte")));
        Assert.AreEqual("no_response", read.Details!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ConexionRechazada_EsNoDisponible_AunEnUnCambio()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var transport = Transport(string.Empty);
        var url = new Uri($"https://127.0.0.1:{port}/ok");

        var read = await Assert.ThrowsExactlyAsync<ModuleException>(() => transport.Read("prueba", () => new HttpRequestMessage(HttpMethod.Get, url)));
        var change = await Assert.ThrowsExactlyAsync<ModuleException>(() => transport.Mutate("prueba", () => new HttpRequestMessage(HttpMethod.Post, url)));

        Assert.AreEqual("no_connection", read.Details!["reason"]!.GetValue<string>());
        Assert.AreEqual("no_connection", change.Details!["reason"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinTiempoParaUnCambio_NoSale()
    {
        using var certificate = Certificate();
        await using var server = await TlsServer.Start(certificate);
        var transport = new PanelTransport(PanelHttp.Build(Fingerprint(certificate)), new Budget(TimeProvider.System, TimeSpan.FromSeconds(15)), NullLogger.Instance, "Prueba");

        await Assert.ThrowsExactlyAsync<OutOfTimeException>(() => transport.Mutate("prueba", () => Get(server, "/ok")));
        Assert.AreEqual(0, server.Hits);
    }

    private static PanelTransport Transport(string fingerprint) =>
        new(PanelHttp.Build(fingerprint), new Budget(TimeProvider.System, TimeSpan.FromSeconds(60)), NullLogger.Instance, "Prueba");

    private static HttpRequestMessage Get(TlsServer server, string path) => new(HttpMethod.Get, new Uri($"https://127.0.0.1:{server.Port}{path}"));

    /// <summary>Un certificado autofirmado para 127.0.0.1, generado en el momento. Nunca se guarda.</summary>
    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=panel-prueba", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // En Windows, SChannel no acepta la llave efímera de CreateSelfSigned: se pasa por PKCS#12 en memoria.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>La huella como la normaliza la configuración: hexadecimal en mayúsculas y sin dos puntos.</summary>
    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private int hits;
        private int strays;

        private TlsServer(WebApplication app) => this.app = app;

        public int Port { get; private set; }

        public int Hits => hits;

        /// <summary>Pedidos que llegaron siguiendo una redirección.</summary>
        public int Strays => strays;

        public static async Task<TlsServer> Start(X509Certificate2 certificate)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
            var app = builder.Build();
            var server = new TlsServer(app);
            app.MapGet("/ok", () =>
            {
                Interlocked.Increment(ref server.hits);
                return Results.Text("hola");
            });
            app.MapGet("/redireccion", (HttpContext http) =>
            {
                http.Response.StatusCode = 302;
                http.Response.Headers.Location = "/robado";
            });
            app.MapGet("/robado", () =>
            {
                Interlocked.Increment(ref server.strays);
                return Results.Text("OK");
            });
            app.MapGet("/corte", (HttpContext http) => http.Abort());
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            server.Port = new Uri(address).Port;
            return server;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
