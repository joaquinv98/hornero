using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BillingOss.Updates;

/// <summary>Un error de actualización con un texto que se puede mostrar: canal, camino o pedido.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>Versiones X.Y.Z, sin prerelease ni ceros a la izquierda: lo único que publica el canal.</summary>
public static partial class Versions
{
    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out Version? version)
    {
        version = text is not null && Shape().IsMatch(text) ? Version.Parse(text) : null;
        return version is not null;
    }

    public static Version Parse(string? text) =>
        TryParse(text, out var version) ? version : throw new UpdateException($"«{text}» no es una versión X.Y.Z.");

    /// <summary>Major y minor: las versiones de una misma línea solo difieren en el parche.</summary>
    public static (int Major, int Minor) Line(this Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return (version.Major, version.Minor);
    }

    /// <summary>La versión de un ensamblado, sin los metadatos del build (lo que va después de "+").</summary>
    public static Version Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return TryParse(informational.Split('+')[0], out var version) ? version : new Version(0, 0, 0);
    }

    public static string Text(this Version version) => version?.ToString(3) ?? string.Empty;

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

/// <summary>Una imagen de contenedor por digest, nunca por etiqueta sola (sección 5 del diseño).</summary>
public static partial class ImageRefs
{
    /// <summary>
    /// Nombre (con registro y etiqueta opcionales) y digest sha256. El juego de caracteres es cerrado: el valor va a un
    /// YAML y a la línea de argumentos de Docker, y así no puede traer espacios, comillas ni saltos de línea.
    /// </summary>
    public static bool IsDigest([NotNullWhen(true)] string? image) => image is not null && Digest().IsMatch(image);

    [GeneratedRegex(@"^[a-z0-9][A-Za-z0-9._\-/:]{0,254}@sha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
}

/// <summary>
/// Canal de versiones (sección 6 del diseño): un JSON estático por canal, con la forma del releases.json de .NET, firmado
/// con cosign sign-blob. Los campos format, serial, issued, expires y updater no cambian nunca de forma, así cualquier
/// actualizador viejo los lee.
/// </summary>
public sealed record ReleaseChannel
{
    public int Format { get; init; }

    [JsonPropertyName("channel")]
    public string Name { get; init; } = string.Empty;

    public long Serial { get; init; }

    public DateTimeOffset Issued { get; init; }

    public DateTimeOffset Expires { get; init; }

    /// <summary>active, maintenance o eol.</summary>
    public string? SupportPhase { get; init; }

    public DateOnly? EolDate { get; init; }

    public string? LatestRelease { get; init; }

    public UpdaterBinary? Updater { get; init; }

    public Minimums? Minimums { get; init; }

    public IReadOnlyList<Release> Releases { get; init; } = [];

    /// <summary>Kebab-case como el releases.json de .NET, y fechas ISO.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static ReleaseChannel Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReleaseChannel>(json, Json) ?? throw new UpdateException("El canal está vacío.");
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"El canal no es un JSON válido: {ex.Message}");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public Release? Find(Version version) => Releases.FirstOrDefault(r => Versions.TryParse(r.ReleaseVersion, out var v) && v == version);
}

/// <summary>El binario del actualizador: se actualiza antes que nada (sección 7.3).</summary>
public sealed record UpdaterBinary(string Version, Uri Url, string Sha256);

/// <summary>Versiones mínimas recomendadas de PostgreSQL por major ("18": "18.1") y del resto de la receta de HA.</summary>
public sealed record Minimums
{
    public IReadOnlyDictionary<string, string>? Postgres { get; init; }

    public string? Patroni { get; init; }

    public string? Etcd { get; init; }
}

public sealed record Cve(string CveId, string? Origin = null);

public sealed record ReleaseModule(string Id, string Version, IReadOnlyList<string> Protocol, string? MinCore = null);

public sealed record Release
{
    public string ReleaseVersion { get; init; } = string.Empty;

    public DateOnly? ReleaseDate { get; init; }

    public bool Security { get; init; }

    /// <summary>low, medium, high o critical.</summary>
    public string? Severity { get; init; }

    public IReadOnlyList<Cve> CveList { get; init; } = [];

    /// <summary>Runtime de .NET que trae la versión: lo compara el vigía con la metadata oficial.</summary>
    public string? DotnetRuntime { get; init; }

    /// <summary>Primera versión desde la que se llega directo (sección 7.4).</summary>
    public string? From { get; init; }

    public bool RequiresWindow { get; init; }

    public int RolloutPercent { get; init; } = 100;

    public bool Withdrawn { get; init; }

    public Uri? Notes { get; init; }

    public ReleaseImages Images { get; init; } = new();

    public IReadOnlyList<ReleaseModule> Modules { get; init; } = [];

    [JsonIgnore]
    public Version Version => Versions.Parse(ReleaseVersion);

    [JsonIgnore]
    public Version FromVersion => From is null ? new Version(0, 0, 0) : Versions.Parse(From);
}

/// <summary>
/// Imágenes de la versión, cada una por digest. El núcleo sirve a web, worker y migrate. PostgreSQL va una por major y el
/// actualizador nunca cambia de major. El resto (arca, mercadopago y cada módulo nuevo) va con el nombre de su servicio.
/// </summary>
public sealed record ReleaseImages
{
    public string? Nucleo { get; init; }

    public string? Gotenberg { get; init; }

    public IReadOnlyDictionary<string, string>? Postgres { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Modules { get; init; }

    /// <summary>La imagen de un servicio de la receta, o null si la versión no la trae.</summary>
    public string? For(string service) => service switch
    {
        "web" or "worker" or "migrate" => Nucleo,
        "gotenberg" => Gotenberg,
        "postgres" => null,
        _ => Modules is not null && Modules.TryGetValue(service, out var image) && image.ValueKind == JsonValueKind.String ? image.GetString() : null,
    };

    public string? PostgresFor(int major) => Postgres is not null && Postgres.TryGetValue(major.ToString(CultureInfo.InvariantCulture), out var image) ? image : null;

    public IEnumerable<string> All() =>
        new[] { Nucleo, Gotenberg }
            .Concat(Postgres?.Values ?? [])
            .Concat(Modules?.Values.Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() : "no es texto") ?? [])
            .OfType<string>();
}

/// <summary>
/// Lo que el actualizador recuerda entre chequeos, en su esquema: el último serial, el último issued y las llaves que dejó
/// de aceptar al rotar.
/// </summary>
public sealed record ChannelTrust(long Serial, DateTimeOffset? Issued, IReadOnlyList<string> RevokedKeys)
{
    public static ChannelTrust Empty { get; } = new(0, null, []);
}

/// <summary>Un canal que pasó la firma, el piso de serial, el reloj y el vencimiento.</summary>
public sealed record VerifiedChannel(ReleaseChannel Channel, ChannelTrust Trust, string Key, bool FormatKnown);

/// <summary>
/// Reglas del actualizador sobre el canal (sección 6), que cubren las amenazas principales de TUF sin implementarlo entero:
/// firma ECDSA P-256 de cosign sign-blob, rotación a la llave siguiente, vuelta atrás, congelamiento y reloj atrasado.
/// </summary>
public static class ChannelVerifier
{
    /// <summary>El formato que entiende esta versión del actualizador.</summary>
    public const int Format = 1;

    /// <summary>Un canal emitido más adelante que esto respecto del reloj local delata un reloj atrasado.</summary>
    public static TimeSpan ClockTolerance { get; } = TimeSpan.FromMinutes(5);

    /// <param name="document">Los bytes del canal tal como se bajaron: la firma es sobre ellos.</param>
    /// <param name="signature">La firma de cosign sign-blob, en base64 (DER sobre SHA-256).</param>
    /// <param name="publicKeys">PEM de las llaves públicas, en orden: la vigente y la siguiente.</param>
    public static VerifiedChannel Verify(byte[] document, string signature, IReadOnlyList<string> publicKeys, ChannelTrust trust, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentNullException.ThrowIfNull(trust);
        byte[] sig;
        try
        {
            sig = Convert.FromBase64String((signature ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            throw new UpdateException("La firma del canal no es base64.");
        }

        var keys = publicKeys.Select(pem => (Pem: pem, Id: KeyId(pem))).ToList();
        var used = keys.FindIndex(k => !trust.RevokedKeys.Contains(k.Id) && Verifies(k.Pem, document, sig));
        if (used < 0)
        {
            throw new UpdateException("La firma del canal no verifica con ninguna de las llaves aceptadas.");
        }

        // Los campos que no cambian de forma, leídos aparte: un formato nuevo no impide leerlos.
        Header header;
        try
        {
            header = JsonSerializer.Deserialize<Header>(document, ReleaseChannel.Json) ?? throw new UpdateException("El canal está vacío.");
        }
        catch (JsonException ex)
        {
            throw new UpdateException($"El canal no es un JSON válido: {ex.Message}");
        }

        // Rotación: la primera vez que firma la siguiente, la vigente queda afuera para siempre y el serial de este canal
        // pasa a ser el piso, aunque sea menor: si no, quien robó la vigente deja afuera a todos con un serial enorme.
        var rotated = used > 0 && keys.Take(used).Any(k => !trust.RevokedKeys.Contains(k.Id));
        var revoked = rotated ? trust.RevokedKeys.Concat(keys.Take(used).Select(k => k.Id)).Distinct().ToList() : trust.RevokedKeys;
        if (!rotated && header.Serial < trust.Serial)
        {
            throw new UpdateException($"El canal trae el serial {header.Serial}, menor que el último visto ({trust.Serial}): es una copia vieja.");
        }

        if (trust.Issued is { } lastIssued && now < lastIssued || header.Issued > now + ClockTolerance)
        {
            throw new UpdateException("El reloj de este nodo está atrasado respecto del canal: sincronizalo antes de actualizar.");
        }

        if (now > header.Expires)
        {
            throw new UpdateException(string.Create(CultureInfo.InvariantCulture, $"El canal venció el {header.Expires:yyyy-MM-dd HH:mm} UTC: quien lo sirve no lo está renovando."));
        }

        var next = new ChannelTrust(header.Serial, trust.Issued is { } issued && issued > header.Issued ? issued : header.Issued, revoked);
        ValidateUpdater(header.Updater);
        if (header.Format != Format)
        {
            var partial = new ReleaseChannel { Format = header.Format, Serial = header.Serial, Issued = header.Issued, Expires = header.Expires, Updater = header.Updater };
            return new VerifiedChannel(partial, next, keys[used].Id, FormatKnown: false);
        }

        var channel = JsonSerializer.Deserialize<ReleaseChannel>(document, ReleaseChannel.Json)!;
        Validate(channel);
        return new VerifiedChannel(channel, next, keys[used].Id, FormatKnown: true);
    }

    /// <summary>Huella de una llave pública: SHA-256 de su SubjectPublicKeyInfo.</summary>
    public static string KeyId(string pem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Una entrada mal armada invalida el canal entero: nunca se aplica una versión a medias.</summary>
    public static void Validate(ReleaseChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ValidateUpdater(channel.Updater);
        foreach (var release in channel.Releases)
        {
            var version = Versions.Parse(release.ReleaseVersion);
            _ = release.FromVersion;
            if (release.RolloutPercent is < 0 or > 100)
            {
                throw new UpdateException($"La versión {version.Text()} tiene un rollout-percent fuera de 0 a 100.");
            }

            if (release.Severity is not (null or "low" or "medium" or "high" or "critical"))
            {
                throw new UpdateException($"La versión {version.Text()} tiene una severidad desconocida.");
            }

            if (release.Images.Nucleo is null)
            {
                throw new UpdateException($"La versión {version.Text()} no trae la imagen del núcleo.");
            }

            if (release.Images.All().FirstOrDefault(i => !ImageRefs.IsDigest(i)) is { } bad)
            {
                throw new UpdateException($"La versión {version.Text()} trae una imagen sin digest: «{bad}».");
            }
        }

        if (channel.Releases.Select(r => r.ReleaseVersion).Distinct(StringComparer.Ordinal).Count() != channel.Releases.Count)
        {
            throw new UpdateException("El canal repite una versión.");
        }
    }

    /// <summary>
    /// El binario del actualizador, también con un formato que no se conoce: es lo único que se lee de él, y un campo que
    /// falta tumbaría a todos los actualizadores en la próxima vuelta.
    /// </summary>
    public static void ValidateUpdater(UpdaterBinary? updater)
    {
        if (updater is null)
        {
            return;
        }

        if (!Versions.TryParse(updater.Version, out _))
        {
            throw new UpdateException("El actualizador del canal no tiene una versión X.Y.Z.");
        }

        if (updater.Url is null || !updater.Url.IsAbsoluteUri || !(updater.Url.Scheme == Uri.UriSchemeHttps || (updater.Url.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(updater.Url.Host, out var ip) && IPAddress.IsLoopback(ip))))
        {
            throw new UpdateException("El actualizador del canal se baja solo por HTTPS.");
        }

        if (updater.Sha256 is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit))
        {
            throw new UpdateException("El actualizador del canal no trae un SHA-256 de 64 hexadecimales.");
        }
    }

    private static bool Verifies(string pem, byte[] document, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.VerifyData(document, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record Header(int Format, long Serial, DateTimeOffset Issued, DateTimeOffset Expires, UpdaterBinary? Updater);
}
