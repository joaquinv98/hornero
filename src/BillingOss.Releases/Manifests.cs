using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BillingOss.Updates;

namespace BillingOss.Releases;

/// <summary>
/// Lo que publicar.yml adjunta a la versión de Forgejo (sección 3.4, paso 2): la entrada del canal sin lo de seguridad,
/// con cada imagen por digest y el runtime de .NET que trae, y el binario del actualizador con su SHA-256.
/// </summary>
public sealed record ReleaseManifest(Release Release, UpdaterBinary? Updater)
{
    public string ToJson() => JsonSerializer.Serialize(this, ReleaseChannel.Json);

    public static ReleaseManifest Parse(string json) =>
        JsonSerializer.Deserialize<ReleaseManifest>(json, ReleaseChannel.Json) ?? throw new UpdateException("El manifiesto está vacío.");
}

/// <summary>El manifiesto de una versión y los cambios del canal: sumar, retirar y renovar antes de firmar (sección 6).</summary>
public static partial class Manifests
{
    /// <summary>Lo que vale un canal firmado: canal.yml lo vuelve a firmar cada día.</summary>
    public static TimeSpan Validity { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// La imagen por digest: la referencia tal cual si ya va por digest, o la que sale del archivo de metadata de BuildKit
    /// (--metadata-file de buildctl o de buildx, con las mismas claves), con su etiqueta y el digest de lo que se empujó.
    /// </summary>
    public static string Image(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ImageRefs.IsDigest(value))
        {
            return value;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(value));
        var name = Watch.Text(doc.RootElement, "image.name")?.Split(',')[0].Trim();
        var digest = Watch.Text(doc.RootElement, "containerimage.digest");
        var image = $"{name}@{digest}";
        return ImageRefs.IsDigest(image) ? image : throw new UpdateException($"{value} no trae una imagen empujada por digest.");
    }

    /// <summary>
    /// Gotenberg y PostgreSQL salen de los valores por defecto de la receta de Compose, y tienen que ir por digest: el
    /// proveedor recibe digests, nunca etiquetas (sección 5). PostgreSQL va con la major de su etiqueta.
    /// </summary>
    public static (string Gotenberg, string PostgresMajor, string Postgres) FromRecipe(string composeDirectory)
    {
        var gotenberg = Default(Path.Combine(composeDirectory, "compose.yaml"), "GOTENBERG_IMAGE");
        var postgres = Default(Path.Combine(composeDirectory, "compose.postgres.yaml"), "POSTGRES_IMAGE");
        var major = PostgresMajor().Match(postgres);
        return major.Success
            ? (gotenberg, major.Groups[1].Value, postgres)
            : throw new UpdateException($"No se sabe la major de PostgreSQL de «{postgres}»: la etiqueta tiene que empezar con ella (postgres:18.N).");
    }

    public static ReleaseManifest Build(
        string version,
        IReadOnlyDictionary<string, string> images,
        string composeDirectory,
        string dockerfile,
        string? updaterFile,
        Uri? updaterUrl,
        Uri? notes,
        DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(images);
        var v = Versions.Parse(version);
        if (!images.TryGetValue("nucleo", out var nucleo))
        {
            throw new UpdateException("Falta la imagen del núcleo (--imagen nucleo=...).");
        }

        var (gotenberg, major, postgres) = FromRecipe(composeDirectory);
        var modules = images.Where(i => i.Key != "nucleo").OrderBy(i => i.Key, StringComparer.Ordinal).ToList();
        var release = new Release
        {
            ReleaseVersion = v.Text(),
            ReleaseDate = date,
            DotnetRuntime = Watch.Runtime(File.ReadAllText(dockerfile)).ToString(3),
            Notes = notes,
            Images = new ReleaseImages
            {
                Nucleo = Image(nucleo),
                Gotenberg = gotenberg,
                Postgres = new Dictionary<string, string>(StringComparer.Ordinal) { [major] = postgres },
                Modules = modules.ToDictionary(m => m.Key, m => JsonSerializer.SerializeToElement(Image(m.Value)), StringComparer.Ordinal),
            },

            // En v0 los módulos propios salen en la misma versión que el núcleo y hablan la versión 0 del protocolo.
            Modules = [.. modules.Select(m => new ReleaseModule(m.Key, v.Text(), ["0"]))],
        };

        // Las mismas reglas que aplica el actualizador: una imagen sin digest invalida la versión.
        ChannelVerifier.Validate(new ReleaseChannel { Format = ChannelVerifier.Format, Releases = [release] });
        UpdaterBinary? updater = null;
        if (updaterFile is not null)
        {
            using var file = File.OpenRead(updaterFile);
            updater = new UpdaterBinary(v.Text(), updaterUrl ?? throw new UpdateException("Falta --actualizador-url."), Convert.ToHexStringLower(SHA256.HashData(file)));
        }

        return new ReleaseManifest(release, updater);
    }

    /// <summary>
    /// Suma la versión al canal: es el PR que abre un mantenedor con su cuenta y aprueba otro (sección 3.4, paso 3). Lo de
    /// seguridad lo decide quien abre el PR: el manifiesto no lo trae.
    /// </summary>
    public static ReleaseChannel Add(ReleaseChannel channel, ReleaseManifest manifest, bool security, string? severity, IReadOnlyList<Cve> cves, string? from, bool requiresWindow, int rolloutPercent)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(manifest);
        if (channel.Find(manifest.Release.Version) is not null)
        {
            throw new UpdateException($"La versión {manifest.Release.ReleaseVersion} ya está en el canal.");
        }

        if (from is not null)
        {
            _ = Versions.Parse(from);
        }

        var release = manifest.Release with
        {
            Security = security,
            Severity = severity ?? (security ? "high" : null),
            CveList = cves,
            From = from,
            RequiresWindow = requiresWindow,
            RolloutPercent = rolloutPercent,
        };
        var updater = manifest.Updater is { } offered && (channel.Updater is null || Versions.Parse(offered.Version) > Versions.Parse(channel.Updater.Version))
            ? offered
            : channel.Updater;
        return Checked(channel with { Releases = [.. channel.Releases.Append(release).OrderByDescending(r => r.Version)], Updater = updater });
    }

    /// <summary>Retira una versión mala: deja de ofrecerse y a quien ya la tiene el panel le dice "actualizá ya".</summary>
    public static ReleaseChannel Withdraw(ReleaseChannel channel, Version version)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Find(version) is null)
        {
            throw new UpdateException($"La versión {version?.Text()} no está en el canal.");
        }

        return Checked(channel with { Releases = [.. channel.Releases.Select(r => r.Version == version ? r with { Withdrawn = true } : r)] });
    }

    /// <summary>
    /// Lo que hace canal.yml antes de firmar, al fusionar y cada día: emitido ahora, vence a los 7 días y un serial nuevo.
    /// El serial es la hora de emisión en segundos, así crece de una corrida a otra sin guardar estado.
    /// </summary>
    public static ReleaseChannel Renew(ReleaseChannel channel, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var issued = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        return Checked(channel with
        {
            Format = ChannelVerifier.Format,
            Serial = Math.Max(issued.ToUnixTimeSeconds(), channel.Serial + 1),
            Issued = issued,
            Expires = issued + Validity,
        });
    }

    /// <summary>Un canal nuevo, vacío, con el nombre del archivo (estable o lts).</summary>
    public static ReleaseChannel Empty(string name) => new() { Format = ChannelVerifier.Format, Name = name, SupportPhase = "active" };

    /// <summary>CVE-2026-1234 o CVE-2026-1234:dotnet, con el origen como en el cve-list de .NET.</summary>
    public static Cve ParseCve(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split(':', 2);
        return CveId().IsMatch(parts[0]) ? new Cve(parts[0], parts.Length == 2 ? parts[1] : null) : throw new UpdateException($"«{text}» no es una CVE (CVE-AAAA-NNNN).");
    }

    private static ReleaseChannel Checked(ReleaseChannel channel)
    {
        var offered = channel.Releases.Where(r => !r.Withdrawn).Select(r => r.Version).DefaultIfEmpty().Max();
        var result = channel with { LatestRelease = offered?.Text() };
        ChannelVerifier.Validate(result);
        return result;
    }

    private static string Default(string file, string variable)
    {
        var match = Regex.Match(File.ReadAllText(file), $@"\$\{{{variable}:-([^}}\s]+)\}}", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new UpdateException($"{Path.GetFileName(file)} no tiene un valor por defecto para {variable}.");
        }

        return ImageRefs.IsDigest(match.Groups[1].Value)
            ? match.Groups[1].Value
            : throw new UpdateException($"{variable} en {Path.GetFileName(file)} va por etiqueta ({match.Groups[1].Value}): fijale el digest antes de publicar.");
    }

    [GeneratedRegex(@"^(?:[^/@]+/)*postgres:(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PostgresMajor();

    [GeneratedRegex(@"^CVE-\d{4}-\d{4,}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CveId();
}
