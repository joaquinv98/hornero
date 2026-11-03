using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.PayPal;

// Entradas de las operaciones. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta,
// en vez de dejar que el deserializador falle sin decir qué campo era.

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class PayPalConfig
{
    public string? Environment { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? WebhookId { get; init; }
}

/// <summary>Lo necesario para hablar con PayPal: la URL del entorno y las credenciales de la app, ya validadas.</summary>
internal sealed record PayPalAccess(Uri BaseUrl, string ClientId, string ClientSecret)
{
    /// <summary>Clave del token guardado en memoria. Es un hash para que el secreto no quede como clave de un diccionario.</summary>
    public string CacheKey { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{BaseUrl}\n{ClientId}\n{ClientSecret}")));
}

/// <summary>Dinero del protocolo: texto decimal y moneda ISO.</summary>
internal sealed class MoneyInput
{
    public decimal? Value { get; init; }

    public string? Currency { get; init; }
}

/// <summary>Entrada de create_checkout. AttemptId viaja como custom_id e invoice_id, y el total tiene que ser la suma de los ítems.</summary>
internal sealed class CheckoutInput
{
    public string? AttemptId { get; init; }

    public MoneyInput? Amount { get; init; }

    public List<CheckoutItem>? Items { get; init; }

    /// <summary>URLs a las que vuelve el cliente, por resultado: success, pending y failure.</summary>
    public Dictionary<string, string>? ReturnUrls { get; init; }
}

internal sealed class CheckoutItem
{
    public string? Id { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public int? Quantity { get; init; }

    public decimal? UnitPrice { get; init; }
}

/// <summary>Entrada de get_payment.</summary>
internal sealed class PaymentRef
{
    public string? PaymentExternalId { get; init; }
}

/// <summary>Entrada de refund. Sin Amount es un reembolso total.</summary>
internal sealed class RefundInput
{
    public string? PaymentExternalId { get; init; }

    public MoneyInput? Amount { get; init; }
}

/// <summary>Entrada de list_payments.</summary>
internal sealed class ListInput
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int? Offset { get; init; }

    public string? By { get; init; }
}

/// <summary>Entrada de verify_webhook: lo que llegó al núcleo, sin tocar.</summary>
internal sealed class WebhookInput
{
    public JsonNode? Headers { get; init; }

    public string? BodyBase64 { get; init; }
}

/// <summary>Código y mensaje de PayPal, tal cual vienen en el error.</summary>
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

    /// <summary>PayPal no respondió y el pedido seguro no se ejecutó, o fue una consulta: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, int retryAfter = 30) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    /// <summary>Resultado incierto: el pedido pudo haberse ejecutado, así que se repite con la misma clave.</summary>
    public static ModuleException Unknown(string message, string reason) => new(
        ErrorCodes.OutcomeUnknown,
        message,
        retryAfter: 30,
        details: new JsonObject { ["reason"] = reason });

    /// <summary>details.upstream trae los códigos de PayPal, como pide la sección 10 del protocolo. debug_id es lo que pide el soporte de PayPal.</summary>
    public static JsonObject Codes(int httpStatus, IEnumerable<Upstream> codes, string? debugId) => new()
    {
        ["http_status"] = httpStatus,
        ["debug_id"] = debugId,
        ["upstream"] = new JsonArray(
            codes.Select(c => (JsonNode?)new JsonObject { ["code"] = c.Code, ["message"] = c.Message }).ToArray()),
    };

    /// <summary>Si el error de PayPal trae ese código (el issue de sus details).</summary>
    public static bool Has(ModuleException ex, string code) =>
        ex.Details?["upstream"] is JsonArray codes && codes.Any(c => c.Field("code").Text() == code);

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
}

/// <summary>Lectura tolerante de JSON que no controlamos: lo que no es del tipo esperado da null en vez de una excepción.</summary>
internal static class JsonRead
{
    public static JsonNode? Field(this JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    /// <summary>El texto de un valor. Los números y los booleanos salen tal cual vinieron.</summary>
    public static string? Text(this JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    public static decimal? Dec(this JsonNode? node) =>
        decimal.TryParse(node.Text(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static IEnumerable<JsonNode> Items(this JsonNode? node) => node is JsonArray list ? list.OfType<JsonNode>() : [];
}

/// <summary>Formatos del protocolo: dinero como texto decimal y fechas en UTC.</summary>
internal static class Fmt
{
    /// <summary>Al menos 2 decimales y sin perder los que haya, hasta 6.</summary>
    public static string Amount(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    public static JsonObject Money(decimal value, string currency) => new() { ["value"] = Amount(value), ["currency"] = currency };

    public static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>La fecha de un campo de PayPal (RFC 3339 con zona), o null si falta.</summary>
    public static string? Instant(JsonNode? node) =>
        DateTimeOffset.TryParse(node.Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? Instant(value) : null;

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
