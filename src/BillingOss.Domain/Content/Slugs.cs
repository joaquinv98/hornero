using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BillingOss.Domain.Content;

/// <summary>Las direcciones cortas de artículos y categorías (sección 2.6 de diseno/contenidos.md).</summary>
public static partial class Slugs
{
    public const int MaxLength = 100;

    /// <summary>Sin tildes, en minúscula y con guiones. Un texto solo de dígitos se prefija con "n-" para no confundirse con un ID.</summary>
    public static string From(string? text, int max = 80)
    {
        var builder = new StringBuilder();
        foreach (var c in (text ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > max)
        {
            slug = slug[..max].Trim('-');
        }

        return slug.Length == 0 ? "item" : slug.All(char.IsAsciiDigit) ? "n-" + slug : slug;
    }

    public static bool IsValid(string? slug) => slug is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(slug) && !slug.All(char.IsAsciiDigit);

    /// <summary>El mismo slug con un número, para cuando el primero ya está tomado: "ayuda", "ayuda-2", "ayuda-3".</summary>
    public static string Numbered(string slug, int n)
    {
        ArgumentNullException.ThrowIfNull(slug);
        if (n <= 1)
        {
            return slug;
        }

        var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
        return (slug.Length + suffix.Length > MaxLength ? slug[..(MaxLength - suffix.Length)].TrimEnd('-') : slug) + suffix;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();
}
