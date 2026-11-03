using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.CryptoCloud;

/// <summary>De la factura de CryptoCloud (invoice/merchant/info y invoice/merchant/list) al resultado de get_payment y de list_payments.</summary>
internal static class InvoiceMapper
{
    /// <summary>
    /// La factura como pago. Una factura en modo prueba se confirma desde el panel sin que entre plata: sale como
    /// rejected con el detalle test_mode, salvo que la instancia habilite allow_test_invoices.
    /// </summary>
    public static JsonObject ToResult(JsonNode? invoice, bool allowTest)
    {
        var id = Inputs.InvoiceId(invoice.Field("uuid").Text()) ?? throw Missing("uuid");
        var gatewayStatus = invoice.Field("status").Text() ?? throw Missing("status");
        var test = string.Equals(invoice.Field("test_mode").Text(), "true", StringComparison.OrdinalIgnoreCase) && !allowTest;
        var status = test ? "rejected" : Status(gatewayStatus);

        // El monto es el de la moneda en que se creó la factura. fiat_currency está en la tabla de campos pero no en
        // los ejemplos: sin ella, la factura es en USD, la moneda por defecto y la única que crea este módulo.
        var currency = invoice.Field("fiat_currency").Text() is { Length: 3 } fiat && fiat.All(char.IsAsciiLetterUpper) ? fiat : "USD";
        var amount = invoice.Field("amount_in_fiat").Dec() ?? (currency == "USD" ? invoice.Field("amount_usd").Dec() : null)
            ?? throw Missing("amount_in_fiat");

        return new JsonObject
        {
            ["payment_external_id"] = id,
            ["status"] = status,

            // Llega como número de punto flotante: se redondea a centavos.
            ["amount"] = Fmt.Money(decimal.Round(amount, 2, MidpointRounding.AwayFromZero), currency),

            // La API no tiene reembolsos.
            ["refunded"] = Fmt.Money(0m, currency),
            ["fee"] = Fee(invoice, currency),
            ["created_at"] = Fmt.Instant(invoice.Field("created")),
            ["approved_at"] = status == "approved" ? Fmt.Instant(invoice.Field("date_finished")) : null,
            ["external_reference"] = invoice.Field("order_id").Text(),
            ["gateway_status"] = gatewayStatus,
            ["gateway_status_detail"] = test ? "test_mode" : invoice.Field("invoice_status").Text(),
        };
    }

    /// <summary>
    /// Estados de CryptoCloud a los del núcleo (sección 8.1 del modelo de dominio). overpaid se cobró entero y el
    /// excedente queda en CryptoCloud. partial sigue esperando el resto. canceled es una factura vencida o anulada,
    /// aunque haya recibido algo. Un estado que no conocemos no se adivina: el núcleo se entera.
    /// </summary>
    public static string Status(string status) => status switch
    {
        "paid" or "overpaid" => "approved",
        "created" or "partial" => "pending",
        "canceled" => "rejected",
        _ => throw Fail.Unavailable($"CryptoCloud devolvió un estado de factura que el módulo no conoce: {status}."),
    };

    /// <summary>
    /// Lo que paga el comercio: la comisión de la red (fee_usd) y la del servicio (service_fee_usd), cada una salvo
    /// que su lado diga client. CryptoCloud las informa en USD, y el núcleo toma la comisión en la moneda del pago,
    /// así que en otra moneda no se informa.
    /// </summary>
    private static JsonObject? Fee(JsonNode? invoice, string currency)
    {
        if (currency != "USD")
        {
            return null;
        }

        var serviceSide = invoice.Field("side_commission_cc").Text() ?? invoice.Field("side_commission_service").Text();
        var network = invoice.Field("side_commission").Text() == "client" ? 0m : invoice.Field("fee_usd").Dec() ?? 0m;
        var service = serviceSide == "client" ? 0m : invoice.Field("service_fee_usd").Dec() ?? 0m;
        return Fmt.Money(decimal.Round(network + service, 2, MidpointRounding.AwayFromZero), currency);
    }

    private static ModuleException Missing(string field) =>
        Fail.Unavailable($"CryptoCloud devolvió una factura sin {field}.");
}
