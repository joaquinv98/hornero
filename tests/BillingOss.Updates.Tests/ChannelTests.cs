using System.Text;
using static BillingOss.Updates.Tests.ChannelKit;

namespace BillingOss.Updates.Tests;

/// <summary>Formato del canal y sus reglas de verificación (sección 6 de D-20).</summary>
[TestClass]
public sealed class ChannelTests
{
    [TestMethod]
    public void UnCanalFirmadoConLaVigente_Verifica_YSubeElPiso()
    {
        using var current = new SigningKey();
        using var next = new SigningKey();
        var bytes = Bytes(Channel(serial: 42, releases: Release("0.1.2")));

        var verified = ChannelVerifier.Verify(bytes, current.Sign(bytes), [current.PublicPem, next.PublicPem], ChannelTrust.Empty, Now);

        Assert.IsTrue(verified.FormatKnown);
        Assert.AreEqual(42, verified.Trust.Serial);
        Assert.AreEqual(ChannelVerifier.KeyId(current.PublicPem), verified.Key);
        Assert.AreEqual("0.1.2", verified.Channel.Releases.Single().ReleaseVersion);
        Assert.IsEmpty(verified.Trust.RevokedKeys);
    }

    [TestMethod]
    public void UnByteCambiado_NoVerifica()
    {
        using var key = new SigningKey();
        var bytes = Bytes(Channel(releases: Release("0.1.2")));
        var signature = key.Sign(bytes);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("0.1.2", "0.1.3", StringComparison.Ordinal));

        var ex = Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(tampered, signature, [key.PublicPem], ChannelTrust.Empty, Now));
        StringAssert.Contains(ex.Message, "no verifica");
    }

    [TestMethod]
    public void FirmadoConOtraLlave_NoVerifica()
    {
        using var key = new SigningKey();
        using var other = new SigningKey();
        var bytes = Bytes(Channel());

        Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(bytes, other.Sign(bytes), [key.PublicPem], ChannelTrust.Empty, Now));
        Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(bytes, "no es base64!", [key.PublicPem], ChannelTrust.Empty, Now));
    }

    [TestMethod]
    public void UnSerialMenorAlUltimoVisto_EsUnaCopiaVieja()
    {
        using var key = new SigningKey();
        var bytes = Bytes(Channel(serial: 41));
        var trust = new ChannelTrust(42, Now.AddHours(-2), []);

        var ex = Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(bytes, key.Sign(bytes), [key.PublicPem], trust, Now));
        StringAssert.Contains(ex.Message, "serial 41");

        // El mismo serial se acepta: es el mismo canal bajado otra vez.
        var same = Bytes(Channel(serial: 42));
        Assert.AreEqual(42, ChannelVerifier.Verify(same, key.Sign(same), [key.PublicPem], trust, Now).Trust.Serial);
    }

    [TestMethod]
    public void UnCanalVencido_SeRechaza()
    {
        using var key = new SigningKey();
        var bytes = Bytes(Channel(issued: Now.AddDays(-8), validFor: TimeSpan.FromDays(7)));

        var ex = Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(bytes, key.Sign(bytes), [key.PublicPem], ChannelTrust.Empty, Now));
        StringAssert.Contains(ex.Message, "venció");
    }

    [TestMethod]
    public void ConElRelojAtrasado_Frena()
    {
        using var key = new SigningKey();
        var bytes = Bytes(Channel(issued: Now.AddDays(-1)));

        // Antes del último issued visto: con un reloj atrasado un canal vencido parecería vigente.
        var trust = new ChannelTrust(40, Now.AddMinutes(30), []);
        var ex = Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(bytes, key.Sign(bytes), [key.PublicPem], trust, Now));
        StringAssert.Contains(ex.Message, "reloj");

        // Un canal emitido en el futuro del reloj local dice lo mismo.
        var future = Bytes(Channel(issued: Now.AddHours(2)));
        Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(future, key.Sign(future), [key.PublicPem], ChannelTrust.Empty, Now));
    }

    [TestMethod]
    public void AlRotar_LaVigenteQuedaAfueraParaSiempre_YElSerialNuevoEsElPiso()
    {
        using var current = new SigningKey();
        using var next = new SigningKey();
        string[] keys = [current.PublicPem, next.PublicPem];

        // Quien robó la vigente firmó un serial enorme: el canal legítimo, firmado con la siguiente, igual entra.
        var stolen = new ChannelTrust(1_000_000, Now.AddHours(-3), []);
        var legit = Bytes(Channel(serial: 43));
        var rotated = ChannelVerifier.Verify(legit, next.Sign(legit), keys, stolen, Now);
        Assert.AreEqual(43, rotated.Trust.Serial);
        CollectionAssert.Contains(rotated.Trust.RevokedKeys.ToList(), ChannelVerifier.KeyId(current.PublicPem));

        // Después, la vigente ya no sirve aunque el serial sea mayor.
        var later = Bytes(Channel(serial: 44));
        Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(later, current.Sign(later), keys, rotated.Trust, Now));
        Assert.AreEqual(44, ChannelVerifier.Verify(later, next.Sign(later), keys, rotated.Trust, Now).Trust.Serial);
    }

    [TestMethod]
    public void UnFormatoNuevo_SoloDejaLeerElActualizador()
    {
        using var key = new SigningKey();
        var json = $$$"""
            {"format":2,"serial":50,"issued":"{{{Now.AddHours(-1):O}}}","expires":"{{{Now.AddDays(6):O}}}",
             "updater":{"version":"0.5.0","url":"https://canal.example/0.5.0/actualizador-linux-x64","sha256":"{{{new string('a', 64)}}}"},
             "releases":{"otra":"forma"}}
            """;
        var bytes = Encoding.UTF8.GetBytes(json);

        var verified = ChannelVerifier.Verify(bytes, key.Sign(bytes), [key.PublicPem], ChannelTrust.Empty, Now);

        Assert.IsFalse(verified.FormatKnown);
        Assert.AreEqual("0.5.0", verified.Channel.Updater!.Version);
        Assert.IsEmpty(verified.Channel.Releases);
    }

    [TestMethod]
    public void UnActualizadorMalArmado_InvalidaElCanal_TambienConUnFormatoNuevo()
    {
        using var key = new SigningKey();
        var https = new Uri("https://canal.example/0.2.0/actualizador-linux-x64");
        var sha = new string('a', 64);
        foreach (var (bad, reason) in (IEnumerable<(UpdaterBinary, string)>)[
            (new UpdaterBinary("0.2.0", null!, sha), "HTTPS"),
            (new UpdaterBinary("0.2.0", new Uri("http://canal.example/x"), sha), "HTTPS"),
            (new UpdaterBinary("0.2", https, sha), "X.Y.Z"),
            (new UpdaterBinary("0.2.0", https, "abc"), "SHA-256"),
            (new UpdaterBinary("0.2.0", https, new string('g', 64)), "SHA-256"),
        ])
        {
            var bytes = Bytes(Channel() with { Updater = bad });
            var ex = Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(bytes, key.Sign(bytes), [key.PublicPem], ChannelTrust.Empty, Now));
            StringAssert.Contains(ex.Message, reason, bad.ToString());
        }

        // Con un formato que no conoce el actualizador es lo único que se lee: sin URL, cada vuelta se caería.
        var json = $$$"""
            {"format":2,"serial":50,"issued":"{{{Now.AddHours(-1):O}}}","expires":"{{{Now.AddDays(6):O}}}",
             "updater":{"version":"0.5.0","sha256":"{{{sha}}}"},"releases":{"otra":"forma"}}
            """;
        var partial = Encoding.UTF8.GetBytes(json);
        StringAssert.Contains(Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(partial, key.Sign(partial), [key.PublicPem], ChannelTrust.Empty, Now)).Message, "HTTPS");

        // Un binario en loopback vale para probar, y sin actualizador el canal vale igual.
        ChannelVerifier.Validate(Channel() with { Updater = new UpdaterBinary("0.2.0", new Uri("http://127.0.0.1:8080/actualizador"), sha) });
        ChannelVerifier.Validate(Channel() with { Updater = null });
    }

    [TestMethod]
    public void UnaImagenSinDigest_InvalidaElCanalEntero()
    {
        using var key = new SigningKey();
        var bad = Release("0.1.2") with { Images = Release("0.1.2").Images with { Gotenberg = "gotenberg/gotenberg:8" } };
        var bytes = Bytes(Channel(releases: [Release("0.1.1"), bad]));

        var ex = Assert.ThrowsExactly<UpdateException>(() => ChannelVerifier.Verify(bytes, key.Sign(bytes), [key.PublicPem], ChannelTrust.Empty, Now));
        StringAssert.Contains(ex.Message, "gotenberg/gotenberg:8");
    }

    [TestMethod]
    public void LasImagenesValenSoloPorDigest_YSinCaracteresRaros()
    {
        Assert.IsTrue(ImageRefs.IsDigest(Digest("billing-oss", 1)));
        Assert.IsTrue(ImageRefs.IsDigest("gotenberg/gotenberg:8.25-chromium@sha256:" + new string('b', 64)));
        Assert.IsFalse(ImageRefs.IsDigest("gotenberg/gotenberg:8"));
        Assert.IsFalse(ImageRefs.IsDigest("billing-oss@sha256:" + new string('b', 63)));
        Assert.IsFalse(ImageRefs.IsDigest("billing oss@sha256:" + new string('b', 64)));
        Assert.IsFalse(ImageRefs.IsDigest("x\n  privileged: true\n  image: y@sha256:" + new string('b', 64)));
        Assert.IsFalse(ImageRefs.IsDigest("--privileged@sha256:" + new string('b', 64)));
    }

    [TestMethod]
    public void LasVersionesSonXYZSinNadaMas()
    {
        Assert.IsTrue(Versions.TryParse("0.4.2", out var v));
        Assert.AreEqual(new Version(0, 4, 2), v);
        foreach (var bad in (string[])["0.4", "0.4.2.1", "v0.4.2", "0.04.2", "0.4.2-rc.1", " 0.4.2", "../0.4.2", "0.4.2\n"])
        {
            Assert.IsFalse(Versions.TryParse(bad, out _), bad);
        }
    }

    [TestMethod]
    public void ElCanalSeLeeYSeEscribeConLosNombresDelFormato()
    {
        var json = Channel(releases: Release("0.4.2", from: "0.3.0", security: true, cves: "CVE-2026-0001")).ToJson();

        foreach (var field in (string[])["\"release-version\"", "\"cve-list\"", "\"cve-id\"", "\"dotnet-runtime\"", "\"requires-window\"", "\"rollout-percent\"", "\"latest-release\"", "\"support-phase\"", "\"arca\""])
        {
            StringAssert.Contains(json, field);
        }

        var back = ReleaseChannel.Parse(json);
        Assert.AreEqual(Digest("billing-oss-arca", 42), back.Releases.Single().Images.For("arca"));
        Assert.AreEqual(Digest("billing-oss", 42), back.Releases.Single().Images.For("worker"));
        Assert.AreEqual(Digest("postgres18", 42), back.Releases.Single().Images.PostgresFor(18));
        Assert.IsNull(back.Releases.Single().Images.For("proxmox"));
    }
}
