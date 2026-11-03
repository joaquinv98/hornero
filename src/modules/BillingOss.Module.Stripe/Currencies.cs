using System.Globalization;

namespace BillingOss.Module.Stripe;

/// <summary>
/// Importes en la unidad menor, como los pide Stripe: 10.99 USD va como 1099. La tabla vive en el módulo y sale de la
/// documentación de monedas de Stripe.
/// </summary>
internal static class Currencies
{
    /// <summary>Sin decimales: el importe va tal cual.</summary>
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.Ordinal)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    /// <summary>Tres decimales: el último dígito de la unidad menor tiene que ser cero.</summary>
    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.Ordinal) { "BHD", "JOD", "KWD", "OMR", "TND" };

    /// <summary>Van con dos decimales en la API pero no admiten fracciones: los decimales tienen que ser cero.</summary>
    private static readonly HashSet<string> WholeOnly = new(StringComparer.Ordinal) { "ISK", "UGX" };

    public static bool IsCode(string? code) => code is { Length: 3 } && code.All(char.IsAsciiLetterUpper);

    /// <summary>La unidad menor del importe, o null si tiene más decimales de los que admite la moneda.</summary>
    public static long? ToMinor(decimal value, string currency)
    {
        if (value < 0m || value >= 1_000_000_000_000m)
        {
            return null;
        }

        var exponent = ZeroDecimal.Contains(currency) ? 0 : ThreeDecimal.Contains(currency) ? 3 : 2;
        if (WholeOnly.Contains(currency) && decimal.Truncate(value) != value)
        {
            return null;
        }

        var minor = value * Pow(exponent);
        return decimal.Truncate(minor) == minor ? (long)minor : null;
    }

    /// <summary>Vuelve de la unidad menor, con la moneda en mayúsculas.</summary>
    public static decimal FromMinor(long minor, string currency)
    {
        var code = currency.ToUpperInvariant();
        return minor / Pow(ZeroDecimal.Contains(code) ? 0 : ThreeDecimal.Contains(code) ? 3 : 2);
    }

    public static string Lower(string currency) => currency.ToLowerInvariant();

    public static string Upper(string? currency) => (currency ?? string.Empty).ToUpper(CultureInfo.InvariantCulture);

    private static decimal Pow(int exponent) => exponent switch { 0 => 1m, 3 => 1000m, _ => 100m };
}
