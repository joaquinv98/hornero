using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Stripe;

/// <summary>Módulo de cobros de Stripe: manifiesto y operaciones (sección 9.3 del protocolo).</summary>
internal static class StripeModule
{
    public static ModuleDefinition Create(HttpClient http, ILogger logger, TimeProvider time)
    {
        var operations = new StripeOperations(new StripeApi(http, logger), time);
        return new ModuleDefinition(Manifest())
            .Operation("payment", "create_checkout", operations.CreateCheckout)
            .Operation("payment", "get_payment", operations.GetPayment, mutating: false)
            .Operation("payment", "verify_webhook", operations.VerifyWebhook, mutating: false)
            .Operation("payment", "refund", operations.Refund)
            .Operation("payment", "list_payments", operations.ListPayments, mutating: false)
            .Operation("payment", "charge_saved", operations.ChargeSaved)
            .Operation("payment", "get_method", operations.GetMethod, mutating: false)
            .Operation("payment", "delete_method", operations.DeleteMethod)
            .InstanceTest(operations.Test);
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "stripe",
        ["name"] = "Stripe",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("payment"),
        ["capabilities"] = new JsonArray(
            "payment.core", "payment.checkout", "payment.webhooks", "payment.refund", "payment.reconcile", "payment.saved_methods"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["api_key", "webhook_secret"],
              "properties": {
                "api_key": {
                  "type": "string",
                  "x-secret": true,
                  "description": "Clave restringida de Stripe (rk_test_ o rk_live_). El prefijo dice el ambiente",
                  "x-help": {
                    "es": "Panel de Stripe, Desarrolladores, Claves de API, Crear clave restringida. Escritura en Checkout Sessions, PaymentIntents, Customers, PaymentMethods y Refunds. Lectura en Charges, Disputes, Balance y Webhook endpoints",
                    "en": "Stripe Dashboard, Developers, API keys, Create restricted key. Write on Checkout Sessions, PaymentIntents, Customers, PaymentMethods and Refunds. Read on Charges, Disputes, Balance and Webhook endpoints",
                    "url": "https://dashboard.stripe.com/apikeys"
                  }
                },
                "webhook_secret": {
                  "type": "string",
                  "x-secret": true,
                  "description": "Secreto del destino de avisos (whsec_)",
                  "x-help": {
                    "es": "Panel de Stripe, Desarrolladores, Webhooks, Agregar destino: la URL de avisos de la instancia y los eventos que muestra el asistente. Después, Revelar el secreto de firma",
                    "en": "Stripe Dashboard, Developers, Webhooks, Add destination: the instance webhook URL and the events the wizard lists. Then reveal the signing secret",
                    "url": "https://dashboard.stripe.com/webhooks"
                  }
                }
              }
            }
            """),
        ["data_scopes"] = new JsonArray(),

        // Stripe cobra en más de 135 monedas y la tabla de decimales vive en el módulo: Stripe rechaza las que no admite.
        ["currencies"] = new JsonArray("*"),
        ["browser_hosts"] = new JsonObject { ["test"] = new JsonArray("checkout.stripe.com"), ["live"] = new JsonArray("checkout.stripe.com") },
        ["webhook_events"] = Fail.Strings(StripeOperations.WebhookEvents),

        // Checkout con medio guardado crea el cliente y la sesión. Un reembolso y un cobro con medio guardado consultan
        // antes de mover plata. Cada página de conciliación consulta los pagos de sus movimientos.
        ["timeouts"] = new JsonObject { ["create_checkout"] = 60, ["refund"] = 60, ["charge_saved"] = 60, ["list_payments"] = 120 },
        ["max_in_flight"] = 4,
    };
}
