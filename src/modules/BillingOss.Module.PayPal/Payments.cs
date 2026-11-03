using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.PayPal;

/// <summary>Las monedas de PayPal y el formato de sus importes.</summary>
internal static class Currencies
{
    /// <summary>
    /// Las 25 monedas de la API REST de PayPal (Currency Codes de su referencia). ARS no está: PayPal no cobra en pesos.
    /// BRL, CNY y MYR solo valen para cuentas de ese país, y eso lo rechaza PayPal.
    /// </summary>
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "AUD", "BRL", "CAD", "CNY", "CZK", "DKK", "EUR", "HKD", "HUF", "ILS", "JPY", "MYR", "MXN",
        "TWD", "NZD", "NOK", "PHP", "PLN", "GBP", "RUB", "SGD", "SEK", "CHF", "THB", "USD",
    };

    /// <summary>Decimales que acepta PayPal en la moneda, o null si PayPal no la cobra. HUF, JPY y TWD van sin decimales.</summary>
    public static int? Decimals(string? code) =>
        code is null || !Supported.Contains(code) ? null : code is "HUF" or "JPY" or "TWD" ? 0 : 2;

    /// <summary>
    /// Un precio o un monto: mayor que cero, con los decimales de la moneda y con un tope que deja la suma de los ítems
    /// lejos del desborde de decimal.
    /// </summary>
    public static bool IsAmount(decimal? value, int decimals) =>
        value is > 0m and < 1_000_000_000_000m && decimal.Round(value.Value, decimals) == value.Value;

    /// <summary>El money de PayPal: la moneda y el valor con exactamente los decimales que pide.</summary>
    public static JsonObject Money(decimal value, string currency) => new()
    {
        ["currency_code"] = currency,
        ["value"] = value.ToString(Decimals(currency) == 0 ? "0" : "0.00", CultureInfo.InvariantCulture),
    };
}

/// <summary>De la captura de PayPal (GET /v2/payments/captures o la captura dentro de la orden) al resultado de get_payment.</summary>
internal static class PaymentMapper
{
    /// <summary>
    /// Arma el pago. reference es el custom_id de la unidad de compra, para las capturas que vienen dentro de una orden
    /// sin el suyo. refunded es lo reembolsado de una captura PARTIALLY_REFUNDED, que la captura no informa.
    /// </summary>
    public static JsonObject ToResult(JsonNode capture, string? reference, decimal? refunded)
    {
        var id = capture.Field("id").Text() ?? throw Missing("id");
        var currency = capture.Field("amount").Field("currency_code").Text() ?? throw Missing("amount.currency_code");
        var amount = capture.Field("amount").Field("value").Dec() ?? throw Missing("amount.value");
        var gatewayStatus = capture.Field("status").Text() ?? throw Missing("status");
        var detail = capture.Field("status_details").Field("reason").Text();
        var status = Status(gatewayStatus, detail);

        // La comisión es paypal_fee, en la moneda del pago. PayPal no la informa mientras la captura está pendiente, y si
        // llegara en otra moneda no se puede comparar con el pago: en los dos casos va null.
        var paypalFee = capture.Field("seller_receivable_breakdown").Field("paypal_fee");
        var fee = paypalFee.Field("currency_code").Text() == currency ? paypalFee.Field("value").Dec() : null;

        var back = gatewayStatus switch
        {
            "REFUNDED" => amount,
            "PARTIALLY_REFUNDED" => refunded,
            _ => 0m,
        };

        // La hora de la captura. En PayPal la plata se mueve al capturar, que es cuando el cliente vuelve de aprobar.
        var settled = status is "approved" or "partially_refunded" or "refunded";
        return new JsonObject
        {
            ["payment_external_id"] = id,
            ["status"] = status,
            ["amount"] = Fmt.Money(amount, currency),
            ["refunded"] = back is { } r ? Fmt.Money(r, currency) : null,
            ["fee"] = fee is { } f ? Fmt.Money(f, currency) : null,
            ["created_at"] = Fmt.Instant(capture.Field("create_time")),
            ["approved_at"] = settled ? Fmt.Instant(capture.Field("create_time")) : null,
            ["external_reference"] = capture.Field("custom_id").Text() is { Length: > 0 } own ? own : reference,
            ["gateway_status"] = gatewayStatus,
            ["gateway_status_detail"] = detail,
        };
    }

    /// <summary>
    /// Estados de la captura a los del núcleo (sección 8.1 del modelo de dominio). Una captura retenida por un reclamo o un
    /// contracargo queda PENDING con ese motivo. REVERSED no está en la lista de PayPal pero existe el evento
    /// PAYMENT.CAPTURE.REVERSED: si llega, la plata volvió al comprador. Lo que no conocemos no se adivina.
    /// </summary>
    public static string Status(string status, string? detail) => status switch
    {
        "COMPLETED" => "approved",
        "PENDING" when detail is "BUYER_COMPLAINT" or "CHARGEBACK" => "disputed",
        "PENDING" => "pending",
        "DECLINED" or "DENIED" or "FAILED" => "rejected",
        "PARTIALLY_REFUNDED" => "partially_refunded",
        "REFUNDED" => "refunded",
        "REVERSED" => "charged_back",
        _ => throw Fail.Unavailable($"PayPal devolvió un estado de captura que el módulo no conoce: {status}."),
    };

    /// <summary>
    /// Lo reembolsado de una captura según su orden: la suma de los reembolsos completos de la orden. Si un reembolso dice
    /// de qué captura es (su enlace up), solo cuenta si es de esta.
    /// </summary>
    public static decimal Refunded(JsonNode order, string captureId) =>
        order.Field("purchase_units").Items()
            .SelectMany(u => u.Field("payments").Field("refunds").Items())
            .Where(r => r.Field("status").Text() == "COMPLETED")
            .Where(r => Links.CaptureOf(r) is not { } up || up == captureId)
            .Sum(r => r.Field("amount").Field("value").Dec() ?? 0m);

    private static ModuleException Missing(string field) =>
        Fail.Unavailable($"PayPal devolvió una captura sin {field}.");
}

/// <summary>Enlaces HATEOAS de PayPal.</summary>
internal static class Links
{
    public static string? Href(JsonNode? resource, string rel) =>
        resource.Field("links").Items().FirstOrDefault(l => l.Field("rel").Text() == rel).Field("href").Text();

    /// <summary>La captura a la que apunta el enlace up de un reembolso o una reversión, si es un ID válido.</summary>
    public static string? CaptureOf(JsonNode? resource)
    {
        const string prefix = "/v2/payments/captures/";
        return Uri.TryCreate(Href(resource, "up"), UriKind.Absolute, out var up)
               && up.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
               && up.AbsolutePath[prefix.Length..] is var id
               && Ids.IsValid(id)
            ? id
            : null;
    }
}

/// <summary>IDs de PayPal (órdenes, capturas, reembolsos): letras y dígitos. Van en la ruta de la API, así que no se aceptan otros caracteres.</summary>
internal static class Ids
{
    public static bool IsValid(string? id) => id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit);
}
