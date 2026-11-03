using System.Security.Cryptography;
using System.Text;
using BillingOss.Releases;
using static BillingOss.Updates.Tests.ChannelKit;

namespace BillingOss.Updates.Tests;

/// <summary>
/// La canalización de versiones (D-20, pasos A5 y A6) sin llaves reales: el manifiesto que adjunta publicar.yml, la
/// entrada que suma un mantenedor al canal y la renovación que precede a la firma. Una llave de prueba firma como cosign
/// y el actualizador verifica con las mismas reglas de siempre.
/// </summary>
[TestClass]
public sealed class ManifestTests
{
    private static readonly string Hex = new('a', 64);

    [TestMethod]
    public void LaImagenSaleDeLaMetadataDeBuildx_ConSuDigest()
    {
        using var dir = new TempDir();
        var meta = dir.Write("buildx-nucleo.json", $$"""{"buildx.build.ref": "x", "containerimage.digest": "sha256:{{Hex}}", "image.name": "registro.example/hornero/billing-oss:0.4.2"}""");

        Assert.AreEqual($"registro.example/hornero/billing-oss:0.4.2@sha256:{Hex}", Manifests.Image(meta));
        Assert.AreEqual(Digest("billing-oss", 1), Manifests.Image(Digest("billing-oss", 1)));
        var sinDigest = dir.Write("buildx-roto.json", """{"image.name": "registro.example/hornero/billing-oss:0.4.2"}""");
        Assert.ThrowsExactly<UpdateException>(() => Manifests.Image(sinDigest));
    }

    [TestMethod]
    public void GotenbergYPostgresDeLaReceta_TienenQueIrPorDigest()
    {
        using var dir = Recipe(digests: true);
        var (gotenberg, major, postgres) = Manifests.FromRecipe(dir.File("compose"));
        Assert.AreEqual(($"gotenberg/gotenberg:8.20.1@sha256:{Hex}", "18", $"postgres:18.1@sha256:{Hex}"), (gotenberg, major, postgres));

        // La receta del repo va por etiqueta hasta que Renovate fije los digests: así no se publica nada.
        var ex = Assert.ThrowsExactly<UpdateException>(() => Manifests.FromRecipe(Repo.File("deploy/compose")));
        StringAssert.Contains(ex.Message, "fijale el digest");
    }

    [TestMethod]
    public async Task DePublicarAlActualizador_ElManifiestoEntraAlCanal_SeRenueva_SeFirma_YVerifica()
    {
        using var dir = Recipe(digests: true);
        var updater = Encoding.ASCII.GetBytes("actualizador de prueba");
        await File.WriteAllBytesAsync(dir.File("billing-oss-actualizador-linux-x64"), updater);
        var images = new Dictionary<string, string>
        {
            ["nucleo"] = dir.Write("buildx-nucleo.json", $$"""{"containerimage.digest": "sha256:{{Hex}}", "image.name": "registro.example/hornero/billing-oss:0.4.2"}"""),
            ["arca"] = Digest("billing-oss-arca", 42),
            ["mercadopago"] = Digest("billing-oss-mercadopago", 42),
        };

        var manifest = Manifests.Build("0.4.2", images, dir.File("compose"), Repo.File("deploy/Dockerfile"), dir.File("billing-oss-actualizador-linux-x64"), new Uri("https://git.example/hornero/billing-oss/releases/download/v0.4.2/billing-oss-actualizador-linux-x64"), null, new DateOnly(2026, 11, 12));
        var parsed = ReleaseManifest.Parse(manifest.ToJson());
        Assert.AreEqual("10.0.14", parsed.Release.DotnetRuntime);
        Assert.AreEqual(Digest("billing-oss-arca", 42), parsed.Release.Images.For("arca"));
        Assert.AreEqual($"postgres:18.1@sha256:{Hex}", parsed.Release.Images.PostgresFor(18));
        CollectionAssert.AreEqual(new[] { "arca", "mercadopago" }, parsed.Release.Modules.Select(m => m.Id).ToArray());
        Assert.IsFalse(parsed.Release.Security, "Lo de seguridad lo pone quien abre el PR del canal.");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(updater)), parsed.Updater!.Sha256);

        var previous = Channel(releases: Release("0.4.1"));
        var channel = Manifests.Add(previous, parsed, security: true, "critical", [Manifests.ParseCve("CVE-2026-1234:dotnet")], from: "0.3.0", requiresWindow: false, rolloutPercent: 100);
        Assert.AreEqual("0.4.2", channel.LatestRelease);
        Assert.AreEqual("0.4.2", channel.Updater!.Version);
        Assert.AreEqual(("critical", "CVE-2026-1234", "dotnet"), (channel.Find(new Version(0, 4, 2))!.Severity, channel.Find(new Version(0, 4, 2))!.CveList[0].CveId, channel.Find(new Version(0, 4, 2))!.CveList[0].Origin));
        Assert.ThrowsExactly<UpdateException>(() => Manifests.Add(channel, parsed, false, null, [], null, false, 100));

        var renewed = Manifests.Renew(channel, Now);
        Assert.AreEqual((Now.ToUnixTimeSeconds(), Now, Now.AddDays(7)), (renewed.Serial, renewed.Issued, renewed.Expires));
        Assert.IsGreaterThan(renewed.Serial, Manifests.Renew(renewed, Now).Serial, "Dos firmas en el mismo segundo igual suben el serial.");

        using var key = new SigningKey();
        var bytes = Bytes(renewed);
        var verified = ChannelVerifier.Verify(bytes, key.Sign(bytes), [key.PublicPem], new ChannelTrust(41, Now.AddDays(-1), []), Now.AddMinutes(1));
        CollectionAssert.AreEqual(new[] { "0.4.2" }, UpdatePlanner.Plan(verified.Channel, new Version(0, 4, 1), new Version(0, 4, 2)).Select(r => r.ReleaseVersion).ToArray());
        Assert.AreEqual(UpdateState.Urgent, UpdateStatus.Summarize(new ChannelSnapshot(verified.Channel, Now, Now, null, false, Now), new Version(0, 4, 1), Now.AddMinutes(1)).State);
    }

    [TestMethod]
    public void RetirarUnaVersion_DejaDeOfrecerse_YLaUltimaVuelveALaAnterior()
    {
        var channel = Channel(releases: [Release("0.4.1"), Release("0.4.2")]) with { LatestRelease = "0.4.2" };

        var withdrawn = Manifests.Withdraw(channel, new Version(0, 4, 2));

        Assert.IsTrue(withdrawn.Find(new Version(0, 4, 2))!.Withdrawn);
        Assert.AreEqual("0.4.1", withdrawn.LatestRelease);
        Assert.IsEmpty(UpdatePlanner.Offered(withdrawn).Where(r => r.ReleaseVersion == "0.4.2"));
        Assert.ThrowsExactly<UpdateException>(() => Manifests.Withdraw(channel, new Version(0, 9, 9)));
        Assert.ThrowsExactly<UpdateException>(() => Manifests.ParseCve("CVE-26-1"));
    }

    [TestMethod]
    public async Task LaLineaDeComandos_ArmaElManifiesto_LaEntradaYElCanalRenovado()
    {
        using var dir = Recipe(digests: true);
        dir.Write("buildx-nucleo.json", $$"""{"containerimage.digest": "sha256:{{Hex}}", "image.name": "registro.example/hornero/billing-oss:0.5.0"}""");
        var clock = new FixedClock(Now);
        using var http = new HttpClient();
        using var output = new StringWriter();

        Assert.AreEqual(0, await Cli.Run(["manifiesto", "--version", "0.5.0", "--imagen", $"nucleo={dir.File("buildx-nucleo.json")}", "--imagen", $"arca={Digest("billing-oss-arca", 50)}", "--receta", dir.File("compose"), "--dockerfile", Repo.File("deploy/Dockerfile"), "--salida", dir.File("manifiesto.json")], http, output, clock), output.ToString());
        Assert.AreEqual(0, await Cli.Run(["entrada", "--canal", dir.File("estable.json"), "--manifiesto", dir.File("manifiesto.json"), "--seguridad", "--cve", "CVE-2026-5555"], http, output, clock), output.ToString());
        Assert.AreEqual(0, await Cli.Run(["renovar", "--canal", dir.File("estable.json"), "--salida", dir.File("publicado.json")], http, output, clock), output.ToString());
        Assert.AreEqual(1, await Cli.Run(["entrada", "--canal", dir.File("estable.json"), "--manifiesto", dir.File("manifiesto.json")], http, output, clock), "La misma versión dos veces no entra.");
        Assert.AreEqual(2, await Cli.Run(["firmar"], http, output, clock), "No hay comando para firmar: firma canal.yml.");

        var published = ReleaseChannel.Parse(await File.ReadAllTextAsync(dir.File("publicado.json")));
        Assert.AreEqual(("estable", "0.5.0", Now.ToUnixTimeSeconds(), Now.AddDays(7)), (published.Name, published.LatestRelease, published.Serial, published.Expires));
        Assert.IsTrue(published.Releases.Single().Security);
        StringAssert.Contains(output.ToString(), "Abrí el PR con tu cuenta");
    }

    /// <summary>Una receta con Gotenberg y PostgreSQL en sus valores por defecto, por digest o por etiqueta.</summary>
    private static TempDir Recipe(bool digests)
    {
        var dir = new TempDir();
        var suffix = digests ? $"@sha256:{Hex}" : string.Empty;
        dir.Write("compose/compose.yaml", $"services:\n  gotenberg:\n    image: ${{GOTENBERG_IMAGE:-gotenberg/gotenberg:8.20.1{suffix}}}\n");
        dir.Write("compose/compose.postgres.yaml", $"services:\n  postgres:\n    image: ${{POSTGRES_IMAGE:-postgres:18.1{suffix}}}\n");
        return dir;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
