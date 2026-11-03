using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.MercadoPago;

/// <summary>Del pago de Mercado Pago (GET /v1/payments) al resultado de get_payment y de list_payments.</summary>
internal static class PaymentMapper
{
    public static JsonObject ToResult(JsonNode? payment)
    {
        var id = payment.Field("id").Text() ?? throw Missing("id");
        var currency = payment.Field("currency_id").Text() ?? throw Missing("currency_id");
        var amount = payment.Field("transaction_amount").Dec() ?? throw Missing("transaction_amount");
        var refunded = payment.Field("transaction_amount_refunded").Dec() ?? 0m;
        var gatewayStatus = payment.Field("status").Text() ?? throw Missing("status");
        var detail = payment.Field("status_detail").Text();

        // La comisión es lo que paga el vendedor. Lo que paga el comprador, como el financiamiento, no cuenta.
        var fee = payment.Field("fee_details") is JsonArray fees
            ? fees.Where(f => f.Field("fee_payer").Text() == "collector").Sum(f => f.Field("amount").Dec() ?? 0m)
            : 0m;

        // Un pago con credenciales de prueba: en una instancia de producción el núcleo no lo toma como plata.
        bool? live = payment.Field("live_mode") is JsonValue mode && mode.TryGetValue<bool>(out var b) ? b : null;

        return new JsonObject
        {
            ["payment_external_id"] = id,
            ["status"] = Status(gatewayStatus, detail, amount, refunded),
            ["livemode"] = live,
            ["amount"] = Fmt.Money(amount, currency),
            ["refunded"] = Fmt.Money(refunded, currency),
            ["fee"] = Fmt.Money(fee, currency),
            ["created_at"] = Fmt.Instant(payment.Field("date_created")),
            ["approved_at"] = Fmt.Instant(payment.Field("date_approved")),
            ["external_reference"] = payment.Field("external_reference").Text() is { Length: > 0 } reference ? reference : null,
            ["gateway_status"] = gatewayStatus,
            ["gateway_status_detail"] = detail,

            // Un cobro de una suscripción: Mercado Pago lo marca en point_of_interaction, y en algunos pagos también en
            // la metadata. Con eso el núcleo lo imputa aunque no tenga un intento propio.
            ["subscription_external_id"] = payment.Field("point_of_interaction").Field("transaction_data").Field("subscription_id").Text()
                ?? payment.Field("metadata").Field("preapproval_id").Text(),
        };
    }

    /// <summary>
    /// Estados de Mercado Pago a los del núcleo (sección 8.1 del modelo de dominio). Un reembolso parcial deja el pago
    /// en approved con el detalle partially_refunded. Un estado que no conocemos no se adivina: el núcleo se entera.
    /// </summary>
    public static string Status(string status, string? detail, decimal amount, decimal refunded) => status switch
    {
        "approved" when refunded > 0m && refunded >= amount => "refunded",
        "approved" when refunded > 0m || detail == "partially_refunded" => "partially_refunded",
        "approved" => "approved",
        "pending" or "in_process" or "authorized" => "pending",
        "rejected" or "cancelled" => "rejected",
        "in_mediation" => "disputed",
        "charged_back" => "charged_back",
        "refunded" => "refunded",
        _ => throw Fail.Unavailable($"Mercado Pago devolvió un estado de pago que el módulo no conoce: {status}."),
    };

    private static ModuleException Missing(string field) =>
        Fail.Unavailable($"Mercado Pago devolvió un pago sin {field}.");
}
