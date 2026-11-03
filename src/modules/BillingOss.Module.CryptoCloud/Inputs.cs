using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.CryptoCloud;

// Entradas de las operaciones. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta,
// en vez de dejar que el deserializador falle sin decir qué campo era. Los campos que el núcleo manda y este
// módulo no usa (ítems y URLs de create_checkout) se ignoran.

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class CryptoCloudConfig
{
    public string? ShopId { get; init; }

    public string? ApiKey { get; init; }

    public string? SecretKey { get; init; }

    public int? InvoiceHours { get; init; }

    public bool? AllowTestInvoices { get; init; }
}

/// <summary>Dinero del protocolo: texto decimal y moneda ISO.</summary>
internal sealed class MoneyInput
{
    public decimal? Value { get; init; }

    public string? Currency { get; init; }
}

/// <summary>Entrada de create_checkout. AttemptId viaja como order_id.</summary>
internal sealed class CheckoutInput
{
    public string? AttemptId { get; init; }

    public MoneyInput? Amount { get; init; }
}

/// <summary>Entrada de get_payment: el uuid de la factura de CryptoCloud, con o sin el prefijo INV-.</summary>
internal sealed class PaymentRef
{
    public string? PaymentExternalId { get; init; }
}

/// <summary>Entrada de list_payments. By dice qué fecha filtra: created (por defecto), approved o updated.</summary>
internal sealed class ListInput
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int? Offset { get; init; }

    public string? By { get; init; }
}

/// <summary>Entrada de verify_webhook. De lo que llegó al núcleo solo hace falta el cuerpo: la firma va adentro.</summary>
internal sealed class WebhookInput
{
    public string? BodyBase64 { get; init; }
}

/// <summary>Un error de CryptoCloud: el campo o la clave que vino y su mensaje.</summary>
internal sealed record Upstream(string Code, string Message);

/// <summary>Armado de los errores del protocolo con los detalles que usa este módulo.</summary>
internal static class Fail
{
    public static ModuleException InvalidInput(IReadOnlyCollection<string> fields, string? message = null) => new(
        ErrorCodes.InvalidInput,
        message ?? $"Faltan datos o son inválidos: {string.Join(", ", fields)}.",
        details: new JsonObject { ["fields"] = Strings(fields) });

    public static ModuleException InvalidConfig(IReadOnlyCollection<string> fields, string? message = null) => new(
        ErrorCodes.InvalidConfig,
        message ?? $"La configuración de la instancia no sirve: {string.Join(", ", fields)}.",
        details: new JsonObject { ["fields"] = Strings(fields) });

    /// <summary>CryptoCloud no respondió y el pedido seguro no se ejecutó, o fue una consulta: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, int retryAfter = 30) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    /// <summary>Resultado incierto: el pedido pudo haberse ejecutado, así que se repite con la misma clave.</summary>
    public static ModuleException Unknown(string message, string reason) => new(
        ErrorCodes.OutcomeUnknown,
        message,
        retryAfter: 30,
        details: new JsonObject { ["reason"] = reason });

    /// <summary>details.upstream con los errores de CryptoCloud, como pide la sección 10 del protocolo.</summary>
    public static JsonObject Codes(int httpStatus, IEnumerable<Upstream> codes) => new()
    {
        ["http_status"] = httpStatus,
        ["upstream"] = new JsonArray(
            codes.Select(c => (JsonNode?)new JsonObject { ["code"] = c.Code, ["message"] = c.Message }).ToArray()),
    };

    public static string Describe(IEnumerable<Upstream> codes)
    {
        var text = string.Join(" ", codes.Select(c => $"[{c.Code}] {c.Message}".Trim()));
        return text.Length <= 600 ? text : text[..600] + "...";
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}

internal static class Inputs
{
    /// <summary>Lee la entrada de la operación. Un tipo mal puesto sale como invalid_input con la ruta, nunca con el valor.</summary>
    public static T Read<T>(ModuleCall call)
    {
        try
        {
            return call.Input<T>();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidInput([ex.Path ?? "input"]);
        }
    }

    /// <summary>
    /// El uuid de una factura con su prefijo, como lo devuelve CryptoCloud al crearla (INV-89UX09KA). El aviso de
    /// pago lo manda sin el prefijo. Null si no tiene esa forma.
    /// </summary>
    public static string? InvoiceId(string? id)
    {
        var code = id is not null && id.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) ? id[4..] : id;
        return code is { Length: > 0 and <= 32 } && code.All(char.IsAsciiLetterOrDigit) ? "INV-" + code : null;
    }
}

/// <summary>Lectura tolerante de JSON que no controlamos: lo que no es del tipo esperado da null en vez de una excepción.</summary>
internal static class JsonRead
{
    public static JsonNode? Field(this JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    /// <summary>
    /// El texto de un valor. Los números y los booleanos salen tal cual vinieron. CryptoCloud manda "None" en
    /// algunos campos vacíos, y eso también es null.
    /// </summary>
    public static string? Text(this JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text is "" or "None" ? null : text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    public static decimal? Dec(this JsonNode? node) =>
        decimal.TryParse(node.Text(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Formatos del protocolo: dinero como texto decimal y fechas en UTC.</summary>
internal static class Fmt
{
    /// <summary>Al menos 2 decimales y sin perder los que haya, hasta 6.</summary>
    public static string Amount(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    public static JsonObject Money(decimal value, string currency) => new() { ["value"] = Amount(value), ["currency"] = currency };

    /// <summary>Una fecha de CryptoCloud ("2026-01-27 09:03:58.958133", siempre en UTC), o null si falta.</summary>
    public static string? Instant(JsonNode? node) =>
        DateTimeOffset.TryParse(node.Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;
}
