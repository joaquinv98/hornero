using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.CryptoCloud;

/// <summary>
/// Módulo de cobros de CryptoCloud, que desde junio de 2026 se llama Trybit: manifiesto y operaciones (sección 9.3
/// del protocolo). La API no tiene reembolsos ni suscripciones, así que refund, create_subscription y
/// cancel_subscription no se publican y la SDK contesta not_supported.
/// </summary>
internal static class CryptoCloudModule
{
    public static ModuleDefinition Create(HttpClient http, ILogger logger)
    {
        var operations = new CryptoCloudOperations(new CryptoCloudApi(http, logger), logger);
        return new ModuleDefinition(Manifest())
            .Operation("payment", "create_checkout", operations.CreateCheckout)
            .Operation("payment", "get_payment", operations.GetPayment, mutating: false)
            .Operation("payment", "verify_webhook", operations.VerifyWebhook, mutating: false)
            .Operation("payment", "list_payments", operations.ListPayments, mutating: false)
            .InstanceTest(operations.Test);
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "cryptocloud",
        ["name"] = "CryptoCloud (Trybit)",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("payment"),
        ["capabilities"] = new JsonArray("payment.core", "payment.checkout", "payment.webhooks", "payment.reconcile"),
        ["config_schema"] = JsonNode.Parse($$"""
            {
              "type": "object",
              "required": ["shop_id", "api_key", "secret_key"],
              "properties": {
                "shop_id": { "type": "string", "description": "Identificador del proyecto (shop_id) en el panel de CryptoCloud" },
                "api_key": { "type": "string", "x-secret": true, "description": "API key del proyecto, para llamar a la API" },
                "secret_key": { "type": "string", "x-secret": true, "description": "Secret key del proyecto, para verificar la firma de los avisos de pago" },
                "invoice_hours": { "type": "integer", "minimum": 1, "maximum": {{CryptoCloudOperations.MaxInvoiceHours}}, "description": "Horas que vive cada factura de cobro. Sin valor, 24 horas" },
                "allow_test_invoices": { "type": "boolean", "default": false, "description": "Tomar como reales las facturas en modo prueba, que se confirman sin pago. Solo para probar la instancia" }
              }
            }
            """),
        ["data_scopes"] = new JsonArray(),

        // Para el asistente de cobros del núcleo: CryptoCloud factura solo en dólares y el link de pago va a Trybit.
        ["currencies"] = new JsonArray("USD"),
        ["browser_hosts"] = new JsonObject { ["test"] = new JsonArray("pay.trybit.com"), ["live"] = new JsonArray("pay.trybit.com") },
        ["max_in_flight"] = 4,
    };
}
