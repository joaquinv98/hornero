using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.Modules.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Tests;

/// <summary>El cliente del protocolo contra un servidor crudo en 127.0.0.1, sin base ni SDK.</summary>
[TestClass]
public sealed class ModuleClientTests
{
    private const string Path = "/v0/provisioning/create";

    private static async Task<(WebApplication App, Uri Url)> Serve(string body, X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l =>
        {
            if (certificate is not null)
            {
                l.UseHttps(certificate);
            }
        }));
        var app = builder.Build();
        app.MapPost(Path, () => Results.Text(body, "application/json"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address + "/"));
    }

    private static Task<ModuleCallResult> Call(Uri url, string? pin = null) =>
        new ModuleClient(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()).Call(
            new ModuleEndpoint(url, RandomNumberGenerator.GetBytes(32), pin),
            Path,
            new ModuleRequest(new InstanceData("1", []), [], []),
            "svc-1-create-1",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

    private static X509Certificate2 SelfSigned(int fromDays, int toDays)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=modulo", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(fromDays), DateTimeOffset.UtcNow.AddDays(toDays));
        // En Windows el servidor TLS no usa una llave efímera: se carga desde un PFX.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private static string Pin(X509Certificate2 certificate) => Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private const string Completed = """{"status":"completed","result":{"ok":true}}""";

    [TestMethod]
    public async Task RespuestaDeMasDeUnMiB_EsErrorInternoYNoIncierto()
    {
        var (app, url) = await Serve($$$"""{"status":"completed","result":{"relleno":"{{{new string('a', 2 * 1024 * 1024)}}}"}}""");
        await using var _ = app;

        var result = await Call(url);

        Assert.IsTrue(result.Response is null, "El núcleo leyó la respuesta entera.");
        Assert.AreEqual(ErrorCodes.Internal, result.Error?.Code);
        Assert.IsFalse(result.IsUncertain);
    }

    [TestMethod]
    public async Task Huella_ConCertificadoPineadoVigente_Conecta()
    {
        using var certificate = SelfSigned(-1, 30);
        var (app, url) = await Serve(Completed, certificate);
        await using var _ = app;

        var result = await Call(url, Pin(certificate));

        Assert.IsNull(result.Error, result.Error?.Message);
        Assert.IsTrue(result.Response!.Result!["ok"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Huella_ConCertificadoPineadoVencido_NoConecta()
    {
        using var certificate = SelfSigned(-30, -1);
        var (app, url) = await Serve(Completed, certificate);
        await using var _ = app;

        var result = await Call(url, Pin(certificate));

        Assert.IsNull(result.Response);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, result.Error?.Code);
    }

    [TestMethod]
    public void Huella_SoloAceptaElCertificadoDeEsaHuellaYVigente()
    {
        using var pinned = SelfSigned(-1, 30);
        using var other = SelfSigned(-1, 30);
        using var expired = SelfSigned(-30, -1);
        var pin = Pin(pinned);

        Assert.IsTrue(ModuleClient.PinMatches(pin, pinned));
        Assert.IsTrue(ModuleClient.PinMatches(string.Join(':', pin.Chunk(2).Select(c => new string(c))).ToLowerInvariant(), pinned));
        Assert.IsFalse(ModuleClient.PinMatches(pin, other));
        Assert.IsFalse(ModuleClient.PinMatches(Pin(expired), expired));
        Assert.IsFalse(ModuleClient.PinMatches(pin, null));
    }
}
