using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Stripe;

// Entradas de las operaciones. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta,
// en vez de dejar que el deserializador falle sin decir qué campo era.

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class StripeConfig
{
    public string? ApiKey { get; init; }

    public string? WebhookSecret { get; init; }
}

/// <summary>Dinero del protocolo: texto decimal y moneda ISO.</summary>
internal sealed class MoneyInput
{
    public decimal? Value { get; init; }

    public string? Currency { get; init; }
}

/// <summary>Contexto que manda el núcleo a los cobros: la cuenta, sin datos personales.</summary>
internal sealed class PaymentContext
{
    public AccountRef? Account { get; init; }
}

internal sealed class AccountRef
{
    public string? Id { get; init; }

    public string? Language { get; init; }
}

/// <summary>
/// Entrada de create_checkout. AttemptId viaja como client_reference_id y en la metadata del PaymentIntent. Con
/// SaveMethod el medio queda guardado para cobros sin el cliente presente (payment.saved_methods).
/// </summary>
internal sealed class CheckoutInput
{
    public string? AttemptId { get; init; }

    public MoneyInput? Amount { get; init; }

    public List<CheckoutItem>? Items { get; init; }

    public Dictionary<string, string>? ReturnUrls { get; init; }

    public string? NotificationUrl { get; init; }

    public string? Language { get; init; }

    public bool? SaveMethod { get; init; }

    public string? CustomerExternalId { get; init; }
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

/// <summary>Entrada de list_payments. Cursor sigue la página anterior: es el next_cursor que devolvió.</summary>
internal sealed class ListInput
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Cursor { get; init; }
}

/// <summary>Entrada de verify_webhook: lo que llegó al núcleo, sin tocar, y cuándo llegó.</summary>
internal sealed class WebhookInput
{
    public JsonNode? Headers { get; init; }

    public string? BodyBase64 { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }
}

/// <summary>Entrada de charge_saved: un cobro sin el cliente presente, con un medio guardado.</summary>
internal sealed class ChargeInput
{
    public string? AttemptId { get; init; }

    public MoneyInput? Amount { get; init; }

    public string? Description { get; init; }

    public string? CustomerExternalId { get; init; }

    public string? MethodExternalId { get; init; }
}

/// <summary>Entrada de get_method y de delete_method.</summary>
internal sealed class MethodRef
{
    public string? MethodExternalId { get; init; }
}

/// <summary>Entrada de instance/test: la URL de avisos de la instancia, para comprobar el destino en Stripe.</summary>
internal sealed class TestInput
{
    public string? WebhookUrl { get; init; }
}

/// <summary>Un código de error de Stripe con su mensaje, tal cual vienen.</summary>
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

    /// <summary>Stripe no respondió y el pedido seguro no se ejecutó, o fue una consulta: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, int retryAfter = 30) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    /// <summary>Resultado incierto: el pedido pudo haberse ejecutado, así que se repite con la misma clave.</summary>
    public static ModuleException Unknown(string message, string reason) => new(
        ErrorCodes.OutcomeUnknown,
        message,
        retryAfter: 30,
        details: new JsonObject { ["reason"] = reason });

    public static JsonArray Strings(IEnumerable<string> values) =>
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

    /// <summary>El contexto, que es opcional: si no viene o no se entiende, vale vacío.</summary>
    public static PaymentContext Context(ModuleCall call)
    {
        try
        {
            return call.Context<PaymentContext>();
        }
        catch (Exception ex) when (ex is JsonException or ModuleException)
        {
            return new PaymentContext();
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

    public static long? Long(this JsonNode? node) =>
        long.TryParse(node.Text(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static bool? Bool(this JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>El ID de un campo que Stripe devuelve como texto o, si se expandió, como objeto con su id.</summary>
    public static string? Id(this JsonNode? node) => node is JsonObject obj ? obj["id"].Text() : node.Text();
}

/// <summary>Formatos del protocolo: dinero como texto decimal y fechas en UTC.</summary>
internal static class Fmt
{
    /// <summary>Al menos 2 decimales y sin perder los que haya, hasta 6.</summary>
    public static string Amount(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    public static JsonObject Money(decimal value, string currency) => new() { ["value"] = Amount(value), ["currency"] = currency };

    public static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Una marca de tiempo Unix de Stripe en UTC, o null si falta.</summary>
    public static string? Unix(JsonNode? node) => node.Long() is { } seconds ? Instant(DateTimeOffset.FromUnixTimeSeconds(seconds)) : null;
}
