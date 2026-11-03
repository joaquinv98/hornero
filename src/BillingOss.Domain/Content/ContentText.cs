using System.Text.Json;
using System.Text.Json.Serialization;

namespace BillingOss.Domain.Content;

/// <summary>Título, resumen y cuerpo de un contenido en un idioma (sección 2.1 de diseno/contenidos.md).</summary>
public sealed record ContentText(string Title, string? Summary = null, string? Body = null);

/// <summary>
/// Los textos de un contenido por idioma, guardados como JSON. <c>es-AR</c> es el idioma de base: quien lee ve el suyo,
/// si no el de base y si tampoco el primero que haya.
/// </summary>
public static class ContentTexts
{
    public const string DefaultLanguage = "es-AR";

    public static IReadOnlyList<string> Languages { get; } = [DefaultLanguage, "en"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>"es", "es-AR" y "es-ar" son es-AR. "en" y "en-US" son en. Cualquier otro idioma no se soporta.</summary>
    public static string? Canonical(string? language) =>
        language is null ? null
        : language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en"
        : language.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? DefaultLanguage
        : null;

    /// <summary>El idioma de quien lee: el soportado más cercano, o el de base.</summary>
    public static string Reader(string? language) => Canonical(language) ?? DefaultLanguage;

    public static Dictionary<string, ContentText> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, ContentText>>(json, Json) ?? [];
    }

    public static string Serialize(IReadOnlyDictionary<string, ContentText> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return JsonSerializer.Serialize(texts.OrderBy(t => Languages.ToList().IndexOf(t.Key)).ToDictionary(t => t.Key, t => t.Value), Json);
    }

    /// <summary>El texto en el idioma pedido, o el de base, o el primero. Nulo si no hay ninguno.</summary>
    public static ContentText? Pick(IReadOnlyDictionary<string, ContentText> texts, string? language) => PickWithLanguage(texts, language)?.Text;

    public static (ContentText Text, string Language)? PickWithLanguage(IReadOnlyDictionary<string, ContentText> texts, string? language)
    {
        ArgumentNullException.ThrowIfNull(texts);
        foreach (var wanted in new[] { Canonical(language), DefaultLanguage })
        {
            if (wanted is not null && texts.TryGetValue(wanted, out var found))
            {
                return (found, wanted);
            }
        }

        return texts.Count == 0 ? null : (texts.First().Value, texts.First().Key);
    }

    /// <summary>Texto de un JSON de textos en un idioma, o vacío si no hay nada. Para listas y avisos.</summary>
    public static string Title(string? json, string? language) => Pick(Parse(json), language)?.Title ?? string.Empty;

    /// <summary>Qué tiene mal un conjunto de textos, o null si se puede guardar.</summary>
    public static string? Error(IReadOnlyDictionary<string, ContentText> texts, int titleMax = 200, int summaryMax = 2000, bool bodyRequired = false)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return "Falta el texto en al menos un idioma.";
        }

        foreach (var (language, text) in texts)
        {
            if (!Languages.Contains(language))
            {
                return $"Idioma no soportado: {language}. Los válidos son {string.Join(", ", Languages)}.";
            }

            if (string.IsNullOrWhiteSpace(text.Title) || text.Title.Length > titleMax)
            {
                return $"El título en {language} va con 1 a {titleMax} caracteres.";
            }

            if (text.Summary is { } summary && summary.Length > summaryMax)
            {
                return $"El resumen en {language} pasa los {summaryMax} caracteres.";
            }

            if (text.Body is { } body && body.Length > Markdown.MaxLength)
            {
                return $"El cuerpo en {language} pasa los {Markdown.MaxLength} caracteres.";
            }

            if (bodyRequired && string.IsNullOrWhiteSpace(text.Body))
            {
                return $"Falta el cuerpo en {language}.";
            }
        }

        return null;
    }

    /// <summary>Deja los textos prolijos: sin espacios de más y sin resumen ni cuerpo vacíos.</summary>
    public static Dictionary<string, ContentText> Clean(IReadOnlyDictionary<string, ContentText> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var clean = new Dictionary<string, ContentText>();
        foreach (var (language, text) in texts)
        {
            clean[Canonical(language) ?? language] = new ContentText((text.Title ?? string.Empty).Trim(), Blank(text.Summary), Blank(text.Body));
        }

        return clean;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
