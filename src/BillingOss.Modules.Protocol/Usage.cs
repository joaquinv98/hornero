using System.Globalization;
using System.Text.Json.Nodes;

namespace BillingOss.Modules.Protocol;

/// <summary>Una métrica que declara el manifiesto en usage_metrics: código, tipo, unidad y etiqueta por idioma.</summary>
public sealed record UsageMetricDeclaration(string Id, string Kind, string Unit, IReadOnlyDictionary<string, string> Label);

/// <summary>
/// Uso medido (sección 9.1 del protocolo): la operación usage lee el valor actual de las métricas de un servicio. Es
/// una lectura sin efecto y sin Idempotency-Key. Las lecturas viajan como texto decimal, igual que el dinero.
/// </summary>
public static class UsageProtocol
{
    public const string Capability = "provisioning.usage";

    public const string Operation = "usage";

    public const string Path = "/v0/provisioning/usage";

    /// <summary>Un nivel: disco ocupado, casillas, dispositivos.</summary>
    public const string Gauge = "gauge";

    /// <summary>Un acumulado que solo sube y puede volver a cero: bytes transferidos, segundos de CPU.</summary>
    public const string Counter = "counter";

    /// <summary>Si el manifiesto declara la capacidad.</summary>
    public static bool Declares(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest["capabilities"] is JsonArray capabilities
            && capabilities.Any(c => c is JsonValue v && v.TryGetValue<string>(out var s) && s == Capability);
    }

    /// <summary>Las métricas del manifiesto por código. Una declaración mal formada no cuenta.</summary>
    public static IReadOnlyDictionary<string, UsageMetricDeclaration> Declared(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var result = new Dictionary<string, UsageMetricDeclaration>(StringComparer.Ordinal);
        foreach (var node in manifest["usage_metrics"] as JsonArray ?? [])
        {
            var kind = Text(node?["kind"]);
            if (Text(node?["id"]) is not { Length: > 0 } id || kind is not (Gauge or Counter))
            {
                continue;
            }

            var label = (node?["label"] as JsonObject ?? [])
                .Where(p => Text(p.Value) is not null)
                .ToDictionary(p => p.Key, p => Text(p.Value)!, StringComparer.Ordinal);
            result[id] = new UsageMetricDeclaration(id, kind, Text(node?["unit"]) ?? string.Empty, label);
        }

        return result;
    }

    /// <summary>
    /// Una lectura válida: decimal sin signo, sin exponente ni separador de miles, con 12 enteros y 6 decimales como
    /// máximo, que es lo que entra en la base del núcleo.
    /// </summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrEmpty(text) || text.Length > 19
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var integers = dot < 0 ? text.Length : dot;
        var decimals = dot < 0 ? 0 : text.Length - dot - 1;
        if (integers is < 1 or > 12 || decimals > 6)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>La lectura como texto, con hasta 6 decimales y sin ceros de más.</summary>
    public static string Format(decimal value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture);

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
