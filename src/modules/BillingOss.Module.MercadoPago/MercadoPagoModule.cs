using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.MercadoPago;

/// <summary>Módulo de cobros de Mercado Pago: manifiesto y operaciones (sección 9.3 del protocolo).</summary>
internal static class MercadoPagoModule
{
    public static ModuleDefinition Create(HttpClient http, ILogger logger)
    {
        var operations = new MercadoPagoOperations(new MercadoPagoApi(http, logger));
        return new ModuleDefinition(Manifest())
            .Operation("payment", "create_checkout", operations.CreateCheckout)
            .Operation("payment", "get_payment", operations.GetPayment, mutating: false)
            .Operation("payment", "verify_webhook", operations.VerifyWebhook, mutating: false)
            .Operation("payment", "refund", operations.Refund)
            .Operation("payment", "list_payments", operations.ListPayments, mutating: false)
            .Operation("payment", "create_subscription", operations.CreateSubscription)
            .Operation("payment", "get_subscription", operations.GetSubscription, mutating: false)
            .Operation("payment", "cancel_subscription", operations.CancelSubscription)
            .Operation("payment", "update_subscription", operations.UpdateSubscription)
            .InstanceTest(operations.Test);
    }

    /// <summary>Hosts de Checkout Pro y de la aprobación de suscripciones, por país de la cuenta.</summary>
    private static JsonArray Hosts() => new(
        "www.mercadopago.com.ar", "sandbox.mercadopago.com.ar", "www.mercadopago.com.br", "www.mercadopago.com.mx",
        "www.mercadopago.cl", "www.mercadopago.com.co", "www.mercadopago.com.pe", "www.mercadopago.com.uy");

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "mercadopago",
        ["name"] = "Mercado Pago",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("payment"),
        ["capabilities"] = new JsonArray(
            "payment.core", "payment.checkout", "payment.webhooks", "payment.refund", "payment.reconcile",
            "payment.subscriptions", "payment.subscription_update"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["access_token", "webhook_secret"],
              "properties": {
                "access_token": {
                  "type": "string",
                  "x-secret": true,
                  "description": "Access token de la aplicación de Mercado Pago. Para probar, el de prueba",
                  "x-help": {
                    "es": "Tus integraciones de Mercado Pago, la aplicación, Credenciales de producción o de prueba: Access Token",
                    "en": "Mercado Pago Your integrations, the application, production or test credentials: Access Token",
                    "url": "https://www.mercadopago.com.ar/developers/panel/app"
                  }
                },
                "webhook_secret": {
                  "type": "string",
                  "x-secret": true,
                  "description": "Clave secreta de los webhooks de la aplicación, para verificar la firma x-signature",
                  "x-help": {
                    "es": "Tus integraciones, la aplicación, Webhooks, Configurar notificaciones: la URL de avisos de la instancia, los eventos Pagos y Planes y suscripciones, y la clave secreta que muestra",
                    "en": "Your integrations, the application, Webhooks, Configure notifications: the instance webhook URL, the Payments and Plans and subscriptions events, and the secret key it shows",
                    "url": "https://www.mercadopago.com.ar/developers/panel/app"
                  }
                }
              }
            }
            """),

        // El correo del pagador, solo para crear la suscripción: los ejemplos oficiales lo llevan.
        ["data_scopes"] = new JsonArray("account.contact"),
        ["currencies"] = new JsonArray("ARS", "BRL", "CLP", "COP", "MXN", "PEN", "UYU"),
        ["browser_hosts"] = new JsonObject { ["test"] = Hosts(), ["live"] = Hosts() },
        ["webhook_events"] = new JsonArray("payment", "subscription_preapproval"),

        // Un reembolso consulta el pago y después lo reembolsa: dos llamadas de hasta 25 segundos cada una. La baja
        // de una suscripción también consulta antes.
        ["timeouts"] = new JsonObject { ["refund"] = 60, ["cancel_subscription"] = 60 },
        ["max_in_flight"] = 4,
    };
}
