using System.Text;

namespace BillingOss.Domain.Content;

/// <summary>Una descarga: título y descripción por idioma, audiencia, contador y los datos del archivo (sección 5 de diseno/contenidos.md).</summary>
public sealed class Download : IAudienced
{
    public long Id { get; private set; }

    public long CategoryId { get; set; }

    public PublishState Status { get; set; }

    public ContentAudience Audience { get; set; } = ContentAudience.Customers;

    public long[] ProductIds { get; set; } = [];

    public long[] GroupIds { get; set; } = [];

    public int SortOrder { get; set; }

    /// <summary>Pedidos completos del archivo. Sube con SQL atómico.</summary>
    public long DownloadCount { get; set; }

    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public long? Size { get; set; }

    public string? Sha256 { get; set; }

    public string Texts { get; set; } = "{}";

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool HasFile => Size is not null;

    public void SetImportedId(long id) => Id = id;
}

/// <summary>Los bytes de una descarga, en una tabla aparte para que ninguna lista los lea.</summary>
public sealed class DownloadFile
{
    public long DownloadId { get; set; }

    public byte[] Bytes { get; set; } = [];
}

/// <summary>Nombres, extensiones y tipos de las descargas.</summary>
public static class DownloadRules
{
    /// <summary>Tope de fábrica del archivo: 50 MiB.</summary>
    public const long DefaultMaxBytes = 50L * 1024 * 1024;

    public const int MaxNameLength = 100;

    /// <summary>Extensiones de fábrica. Los tipos que un navegador ejecuta o interpreta (html, svg, js) no están.</summary>
    public static IReadOnlyList<string> DefaultExtensions { get; } =
    [
        "zip", "7z", "gz", "tgz", "tar", "rar", "exe", "msi", "dmg", "pkg", "deb", "rpm", "iso", "pdf", "txt", "md", "csv", "json", "xml",
        "png", "jpg", "jpeg", "gif", "webp", "jar", "apk", "sh", "ps1", "conf", "cfg", "ini",
    ];

    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["zip"] = "application/zip",
        ["7z"] = "application/x-7z-compressed",
        ["gz"] = "application/gzip",
        ["tgz"] = "application/gzip",
        ["tar"] = "application/x-tar",
        ["rar"] = "application/vnd.rar",
        ["pdf"] = "application/pdf",
        ["txt"] = "text/plain; charset=utf-8",
        ["md"] = "text/plain; charset=utf-8",
        ["csv"] = "text/csv; charset=utf-8",
        ["json"] = "application/json",
        ["xml"] = "application/xml",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
    };

    /// <summary>Solo letras, números, punto, guion y guion bajo; sin rutas. Vacío si no queda nada con extensión.</summary>
    public static string CleanName(string? name)
    {
        var last = (name ?? string.Empty).Replace('\\', '/').Split('/')[^1].Trim();
        var builder = new StringBuilder();
        foreach (var c in last.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            {
                builder.Append(c);
            }
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var clean = builder.ToString().Trim('.', '_');
        if (clean.Length > MaxNameLength)
        {
            var extension = Extension(clean);
            clean = extension.Length is > 0 and < 12 ? clean[..(MaxNameLength - extension.Length - 1)].TrimEnd('.', '_') + "." + extension : clean[..MaxNameLength];
        }

        return clean;
    }

    /// <summary>La extensión en minúscula y sin punto, o vacío. En "a.tar.gz" es "gz".</summary>
    public static string Extension(string? fileName)
    {
        var dot = (fileName ?? string.Empty).LastIndexOf('.');
        return dot < 0 || dot == fileName!.Length - 1 ? string.Empty : fileName[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>El tipo fijo que corresponde a la extensión. Lo que no se conoce sale como binario genérico.</summary>
    public static string ContentType(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return Types.GetValueOrDefault(extension, "application/octet-stream");
    }
}
