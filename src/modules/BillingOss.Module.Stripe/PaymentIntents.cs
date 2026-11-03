using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Stripe;

/// <summary>Del PaymentIntent de Stripe, con su cargo y sus disputas, al resultado de get_payment y de list_payments.</summary>
internal static class PaymentIntents
{
    /// <summary>Disputas abiertas: la plata ya salió del saldo, pero el resultado no está decidido.</summary>
    private static readonly HashSet<string> OpenDispute = new(StringComparer.Ordinal)
    {
        "needs_response", "under_review", "warning_needs_response", "warning_under_review",
    };

    /// <param name="intent">El PaymentIntent, con latest_charge.balance_transaction y payment_method expandidos si se pudo.</param>
    /// <param name="disputes">Los estados de sus disputas, si se consultaron.</param>
    public static JsonObject ToResult(JsonNode? intent, IEnumerable<string> disputes)
    {
        var id = intent.Field("id").Text() ?? throw Missing("id");
        var currency = Currencies.Upper(intent.Field("currency").Text() ?? throw Missing("currency"));
        var gatewayStatus = intent.Field("status").Text() ?? throw Missing("status");
        var charge = intent.Field("latest_charge") as JsonObject;
        var received = intent.Field("amount_received").Long() ?? 0;
        var amount = received > 0 ? received : intent.Field("amount").Long() ?? throw Missing("amount");
        var refunded = charge.Field("amount_refunded").Long() ?? 0;

        // La comisión solo si el movimiento de saldo ya existe y está en la moneda del pago, como en PayPal.
        var balance = charge.Field("balance_transaction") as JsonObject;
        var feeCurrency = Currencies.Upper(balance.Field("currency").Text());
        var fee = balance is not null && feeCurrency == currency ? balance.Field("fee").Long() : null;

        // Un cobro sin el cliente presente que se rechazó no tiene sesión donde reintentar: es un rechazo.
        var declinedOffSession = intent.Field("metadata").Field("off_session").Text() == "true" && intent.Field("last_payment_error") is JsonObject;
        var result = new JsonObject
        {
            ["payment_external_id"] = id,
            ["status"] = Status(gatewayStatus, received, refunded, disputes, declinedOffSession),

            // Un pago del ambiente de prueba: en una instancia de producción el núcleo no lo toma como plata.
            ["livemode"] = intent.Field("livemode").Bool(),
            ["amount"] = Fmt.Money(Currencies.FromMinor(amount, currency), currency),
            ["refunded"] = Fmt.Money(Currencies.FromMinor(refunded, currency), currency),
            ["fee"] = fee is { } f ? Fmt.Money(Currencies.FromMinor(f, currency), currency) : null,
            ["created_at"] = Fmt.Unix(intent.Field("created")),
            ["approved_at"] = gatewayStatus == "succeeded" ? Fmt.Unix(charge.Field("created")) : null,
            ["external_reference"] = intent.Field("metadata").Field("attempt_id").Text() is { Length: > 0 } reference ? reference : null,
            ["gateway_status"] = gatewayStatus,
            ["gateway_status_detail"] = intent.Field("last_payment_error").Field("decline_code").Text()
                ?? intent.Field("last_payment_error").Field("code").Text(),
        };

        if (SavedMethod(intent) is { } saved)
        {
            result["saved_method"] = saved;
        }

        return result;
    }

    /// <summary>
    /// El medio que dejó el pago para cobros sin el cliente presente: solo si se pidió guardarlo y quedó en un
    /// cliente. Nunca el número completo ni el código de seguridad.
    /// </summary>
    public static JsonObject? SavedMethod(JsonNode? intent)
    {
        if (intent.Field("setup_future_usage").Text() != "off_session" || intent.Field("payment_method") is not JsonObject method)
        {
            return null;
        }

        var customer = method.Field("customer").Id() ?? intent.Field("customer").Id();
        return customer is null ? null : Method(method, customer);
    }

    /// <summary>Un PaymentMethod de Stripe con la forma del protocolo.</summary>
    public static JsonObject Method(JsonNode method, string? customer)
    {
        var card = method.Field("card");
        return new JsonObject
        {
            ["method_external_id"] = method.Field("id").Text(),
            ["customer_external_id"] = customer,
            ["type"] = method.Field("type").Text(),
            ["brand"] = card.Field("brand").Text(),
            ["last4"] = card.Field("last4").Text(),
            ["exp_month"] = card.Field("exp_month").Long(),
            ["exp_year"] = card.Field("exp_year").Long(),
        };
    }

    /// <summary>
    /// Estados de Stripe a los del núcleo (sección 8.1 del modelo de dominio). Una disputa perdida es contracargo,
    /// una abierta es disputa y una ganada o cerrada no cambia nada. requires_payment_method es pendiente porque el
    /// cliente puede reintentar mientras la sesión está abierta, salvo en un cobro sin el cliente que ya se rechazó
    /// (<paramref name="declinedOffSession"/>): ese no tiene sesión y es un rechazo. Un estado que no conocemos no se
    /// adivina.
    /// </summary>
    public static string Status(string status, long received, long refunded, IEnumerable<string> disputes, bool declinedOffSession = false)
    {
        var list = disputes.ToList();
        if (list.Contains("lost"))
        {
            return "charged_back";
        }

        if (list.Any(OpenDispute.Contains))
        {
            return "disputed";
        }

        return status switch
        {
            "succeeded" when refunded > 0 && refunded >= received => "refunded",
            "succeeded" when refunded > 0 => "partially_refunded",
            "succeeded" => "approved",
            "requires_payment_method" when declinedOffSession => "rejected",
            "processing" or "requires_action" or "requires_confirmation" or "requires_payment_method" or "requires_capture" => "pending",
            "canceled" => "rejected",
            _ => throw Fail.Unavailable($"Stripe devolvió un estado de pago que el módulo no conoce: {status}."),
        };
    }

    private static ModuleException Missing(string field) => Fail.Unavailable($"Stripe devolvió un PaymentIntent sin {field}.");
}
