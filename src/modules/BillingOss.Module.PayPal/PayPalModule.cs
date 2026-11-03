using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.PayPal;

/// <summary>Módulo de cobros de PayPal: manifiesto y operaciones (sección 9.3 del protocolo).</summary>
internal static class PayPalModule
{
    public static ModuleDefinition Create(HttpClient http, ILogger logger, TimeProvider time)
    {
        var operations = new PayPalOperations(new PayPalApi(http, logger, time), logger);
        return new ModuleDefinition(Manifest())
            .Operation("payment", "create_checkout", operations.CreateCheckout)
            .Operation("payment", "get_payment", operations.GetPayment, mutating: false)
            .Operation("payment", "verify_webhook", operations.VerifyWebhook, mutating: false)
            .Operation("payment", "refund", operations.Refund)
            .Operation("payment", "list_payments", operations.ListPayments, mutating: false)
            .InstanceTest(operations.Test);
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "paypal",
        ["name"] = "PayPal",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("payment"),
        ["capabilities"] = new JsonArray("payment.core", "payment.checkout", "payment.webhooks", "payment.refund", "payment.reconcile"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["environment", "client_id", "client_secret", "webhook_id"],
              "properties": {
                "environment": { "type": "string", "enum": ["live", "sandbox"], "description": "live cobra de verdad y sandbox es el entorno de prueba de PayPal. Fija la URL de la API" },
                "client_id": { "type": "string", "description": "Client ID de la app REST de PayPal" },
                "client_secret": { "type": "string", "x-secret": true, "description": "Secret de la app REST de PayPal" },
                "webhook_id": { "type": "string", "pattern": "^[A-Za-z0-9]{1,50}$", "description": "ID del webhook de la app que apunta a /webhooks/{instancia} del núcleo" }
              }
            }
            """),
        ["data_scopes"] = new JsonArray(),

        // Para el asistente de cobros del núcleo: los hosts de la aprobación por ambiente y los eventos del webhook.
        ["browser_hosts"] = new JsonObject { ["test"] = new JsonArray("www.sandbox.paypal.com"), ["live"] = new JsonArray("www.paypal.com") },
        ["webhook_events"] = new JsonArray([.. PayPalOperations.RequiredEvents.Select(e => (JsonNode)e)]),

        // Cada operación puede pedir el token antes de su llamada. get_payment captura una orden aprobada, o lee la
        // captura, su orden y sus disputas: hasta cinco pedidos de hasta 25 segundos. list_payments consulta además cada
        // captura de la página.
        ["timeouts"] = new JsonObject
        {
            ["create_checkout"] = 60,
            ["get_payment"] = 120,
            ["verify_webhook"] = 60,
            ["refund"] = 60,
            ["list_payments"] = 120,
        },
        ["max_in_flight"] = 4,
    };
}
