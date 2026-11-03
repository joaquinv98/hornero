using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.MercadoPago;

// Entradas de las operaciones. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta,
// en vez de dejar que el deserializador falle sin decir qué campo era.

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class MercadoPagoConfig
{
    public string? AccessToken { get; init; }

    public string? WebhookSecret { get; init; }
}

/// <summary>Dinero del protocolo: texto decimal y moneda ISO.</summary>
internal sealed class MoneyInput
{
    public decimal? Value { get; init; }

    public string? Currency { get; init; }
}

/// <summary>Entrada de create_checkout. AttemptId viaja como external_reference y el total tiene que ser la suma de los ítems.</summary>
internal sealed class CheckoutInput
{
    public string? AttemptId { get; init; }

    public MoneyInput? Amount { get; init; }

    public List<CheckoutItem>? Items { get; init; }

    /// <summary>URLs a las que vuelve el cliente, por resultado: success, pending y failure.</summary>
    public Dictionary<string, string>? ReturnUrls { get; init; }

    public string? NotificationUrl { get; init; }
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

/// <summary>Entrada de list_payments. By dice qué fecha filtra: created (por defecto), approved o updated.</summary>
internal sealed class ListInput
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int? Offset { get; init; }

    public string? By { get; init; }
}

/// <summary>Entrada de verify_webhook: lo que llegó al núcleo, sin tocar, y cuándo llegó.</summary>
internal sealed class WebhookInput
{
    public JsonNode? Headers { get; init; }

    public JsonNode? Query { get; init; }

    public string? BodyBase64 { get; init; }

    /// <summary>Cuándo recibió el núcleo el aviso. Un núcleo anterior no lo manda, y entonces no hay ventana de tiempo.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }
}

/// <summary>
/// Entrada de create_subscription: débito recurrente que administra Mercado Pago (payment.subscriptions). El
/// cliente lo aprueba en Mercado Pago y Mercado Pago cobra solo cada período.
/// </summary>
internal sealed class SubscriptionInput
{
    public string? SubscriptionId { get; init; }

    public MoneyInput? Amount { get; init; }

    /// <summary>month o day.</summary>
    public string? Interval { get; init; }

    public int? IntervalCount { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? Reason { get; init; }

    public string? BackUrl { get; init; }
}

/// <summary>Entrada de get_subscription y de cancel_subscription.</summary>
internal sealed class SubscriptionRef
{
    public string? SubscriptionExternalId { get; init; }
}

/// <summary>Entrada de update_subscription (payment.subscription_update): el monto nuevo de cada cobro.</summary>
internal sealed class SubscriptionUpdate
{
    public string? SubscriptionExternalId { get; init; }

    public MoneyInput? Amount { get; init; }
}

/// <summary>Contexto de create_subscription. El correo del pagador va por el permiso account.contact.</summary>
internal sealed class SubscriptionContext
{
    public ContextAccount? Account { get; init; }
}

internal sealed class ContextAccount
{
    public string? Id { get; init; }

    public ContextContact? Contact { get; init; }
}

internal sealed class ContextContact
{
    public string? Email { get; init; }
}

/// <summary>Código y mensaje de Mercado Pago, tal cual vienen en el error.</summary>
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

    /// <summary>Mercado Pago no respondió y el pedido seguro no se ejecutó, o fue una consulta: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, int retryAfter = 30) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    /// <summary>Resultado incierto: el pedido pudo haberse ejecutado, así que se repite con la misma clave.</summary>
    public static ModuleException Unknown(string message, string reason) => new(
        ErrorCodes.OutcomeUnknown,
        message,
        retryAfter: 30,
        details: new JsonObject { ["reason"] = reason });

    /// <summary>Con detalle de Mercado Pago: details.upstream trae sus códigos, como pide la sección 10 del protocolo.</summary>
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
    /// <summary>El contexto que manda el núcleo, si vino y se entiende. Si no, null.</summary>
    public static T? Context<T>(ModuleCall call)
        where T : class
    {
        try
        {
            return call.Context<T>();
        }
        catch (Exception ex) when (ex is JsonException or ModuleException)
        {
            return null;
        }
    }

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
}

/// <summary>Formatos del protocolo: dinero como texto decimal y fechas en UTC.</summary>
internal static class Fmt
{
    /// <summary>Al menos 2 decimales y sin perder los que haya, hasta 6.</summary>
    public static string Amount(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    public static JsonObject Money(decimal value, string currency) => new() { ["value"] = Amount(value), ["currency"] = currency };

    public static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>La fecha de un campo de Mercado Pago (ISO 8601 con zona), o null si falta.</summary>
    public static string? Instant(JsonNode? node) =>
        DateTimeOffset.TryParse(node.Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? Instant(value) : null;
}
