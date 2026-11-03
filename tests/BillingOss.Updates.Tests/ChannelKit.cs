using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BillingOss.Updates.Tests;

/// <summary>Un par de llaves de firma como el de cosign (ECDSA P-256), creado para la prueba.</summary>
internal sealed class SigningKey : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicPem => key.ExportSubjectPublicKeyInfoPem();

    /// <summary>Lo mismo que cosign sign-blob --output-signature: DER sobre SHA-256, en base64.</summary>
    public string Sign(byte[] document) => Convert.ToBase64String(key.SignData(document, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public void Dispose() => key.Dispose();
}

/// <summary>Canales de prueba con valores inventados.</summary>
internal static class ChannelKit
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 18, 0, 0, TimeSpan.Zero);

    public static string Digest(string name, int n) => $"registro.example/hornero/{name}@sha256:{n.ToString("x", CultureInfo.InvariantCulture).PadLeft(64, '0')}";

    public static Release Release(string version, string? from = null, bool security = false, bool withdrawn = false, string? severity = null, params string[] cves)
    {
        var n = int.Parse(version.Replace(".", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return new Release
        {
            ReleaseVersion = version,
            ReleaseDate = new DateOnly(2026, 11, 12),
            From = from,
            Security = security,
            Severity = severity ?? (security ? "high" : null),
            CveList = [.. cves.Select(c => new Cve(c, "dotnet"))],
            Withdrawn = withdrawn,
            DotnetRuntime = "10.0.14",
            Images = new ReleaseImages
            {
                Nucleo = Digest("billing-oss", n),
                Gotenberg = Digest("gotenberg", n),
                Postgres = new Dictionary<string, string> { ["17"] = Digest("postgres17", n), ["18"] = Digest("postgres18", n) },
                Modules = new() { ["arca"] = System.Text.Json.JsonSerializer.SerializeToElement(Digest("billing-oss-arca", n)) },
            },
        };
    }

    public static ReleaseChannel Channel(long serial = 42, DateTimeOffset? issued = null, TimeSpan? validFor = null, params Release[] releases) => new()
    {
        Format = 1,
        Name = "estable",
        Serial = serial,
        Issued = issued ?? Now.AddHours(-1),
        Expires = (issued ?? Now.AddHours(-1)) + (validFor ?? TimeSpan.FromDays(7)),
        SupportPhase = "active",
        LatestRelease = releases.Length == 0 ? null : releases.Max(r => r.Version)!.Text(),
        Updater = new UpdaterBinary("0.1.0", new Uri("https://canal.example/0.1.0/actualizador-linux-x64"), new string('0', 64)),
        Releases = releases,
    };

    public static byte[] Bytes(ReleaseChannel channel) => Encoding.UTF8.GetBytes(channel.ToJson());
}
