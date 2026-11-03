using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Hestia;

/// <summary>Módulo de hosting web en HestiaCP: manifiesto y operaciones (sección 9.1 del protocolo).</summary>
internal static class HestiaModule
{
    /// <summary>Espera del núcleo por operación, en segundos. Es la misma tabla que va en el manifiesto.</summary>
    public static readonly IReadOnlyDictionary<string, int> Timeouts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["validate"] = 30,
        ["create"] = 120,
        ["suspend"] = 90,
        ["unsuspend"] = 90,
        ["terminate"] = 120,
        ["change_plan"] = 90,
        ["get_status"] = 60,
        ["run_action"] = 60,
        ["view"] = 20,
    };

    /// <param name="http">El cliente HTTP para una huella de certificado (o ninguna).</param>
    /// <param name="time">Reloj para el plazo de cada operación. Los tests lo adelantan a mano.</param>
    public static ModuleDefinition Create(Func<string?, HttpClient> http, ILogger logger, TimeProvider? time = null)
    {
        var operations = new HestiaOperations(http, logger, time ?? TimeProvider.System);
        return new ModuleDefinition(Manifest())
            .Operation("provisioning", "validate", Guard(operations.Validate), mutating: false)
            .Operation("provisioning", "create", Guard(c => operations.Create(c)))
            .Operation("provisioning", "suspend", Guard(operations.Suspend))
            .Operation("provisioning", "unsuspend", Guard(operations.Unsuspend))
            .Operation("provisioning", "terminate", Guard(c => operations.Terminate(c)))
            .Operation("provisioning", "change_plan", Guard(c => operations.ChangePlan(c)))
            .Operation("provisioning", "get_status", Guard(operations.GetStatus), mutating: false)
            .Operation("provisioning", "run_action", Guard(operations.RunAction))
            .Operation("provisioning", "view", Guard(operations.View), mutating: false)
            .InstanceTest(Guard(operations.Test))
            .Poll((call, id) => Guard(c => operations.Poll(c, id))(call));
    }

    /// <summary>Lo que queda sin clasificar en una operación es una respuesta de Hestia que no se esperaba: se puede reintentar.</summary>
    private static OperationHandler Guard(OperationHandler handler) => async call =>
    {
        try
        {
            return await handler(call).ConfigureAwait(false);
        }
        catch (HestiaRefusal refusal)
        {
            throw Fail.Unavailable($"Hestia respondió un error inesperado: {refusal.Text}", "unexpected_refusal", refusal.Text);
        }
        catch (OutOfTime)
        {
            throw Fail.Unavailable("Hestia tardó demasiado en responder.", "out_of_time");
        }
    };

    public static JsonObject Manifest()
    {
        var manifest = JsonNode.Parse("""
            {
              "protocol": ["0"],
              "id": "hestia",
              "name": "HestiaCP",
              "version": "0.1.0",
              "vendor": "Hornero",
              "license": "Apache-2.0",
              "kinds": ["provisioning"],
              "capabilities": [
                "provisioning.core", "provisioning.change_plan", "provisioning.status",
                "provisioning.actions", "provisioning.view"
              ],
              "config_schema": {
                "type": "object",
                "required": ["host", "access_key_id", "secret_key"],
                "properties": {
                  "host": { "type": "string", "format": "hostname", "description": "Nombre del servidor Hestia, sin esquema, puerto ni ruta" },
                  "port": { "type": "integer", "minimum": 1, "maximum": 65535, "default": 8083, "description": "Puerto del panel y de la API" },
                  "tls_fingerprint": {
                    "type": "string",
                    "pattern": "^([0-9A-Fa-f]{2}:?){31}[0-9A-Fa-f]{2}$",
                    "description": "SHA-256 del certificado del puerto de la API si no lo firma una CA pública"
                  },
                  "access_key_id": { "type": "string", "pattern": "^[A-Za-z0-9_-]{1,64}$", "description": "Access key de Hestia con el perfil de comandos del módulo" },
                  "secret_key": { "type": "string", "x-secret": true, "description": "Secret key de esa access key" },
                  "default_language": { "type": "string", "pattern": "^[a-z]{2,3}$", "default": "es", "description": "Idioma del panel si Hestia no tiene el de la cuenta" },
                  "username_prefix": {
                    "type": "string",
                    "pattern": "^[a-z][a-z0-9]{0,7}$",
                    "default": "c",
                    "description": "Prefijo del usuario de las cuentas nuevas, que es el prefijo más el ID del servicio"
                  }
                }
              },
              "product_schema": {
                "type": "object",
                "required": ["package"],
                "properties": {
                  "package": { "type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", "description": "Paquete de Hestia" }
                }
              },
              "order_schema": {
                "type": "object",
                "required": ["domain"],
                "properties": {
                  "domain": { "type": "string", "format": "hostname", "description": "Dominio del hosting" }
                }
              },
              "credentials": ["password"],
              "actions": [
                {
                  "id": "change_password",
                  "label": { "es": "Cambiar la contraseña del panel", "en": "Change panel password" },
                  "audience": ["staff", "client"],
                  "confirm": true,
                  "credentials": ["password"]
                }
              ],
              "data_scopes": ["account.contact"],
              "max_in_flight": 2
            }
            """)!.AsObject();
        manifest["timeouts"] = new JsonObject(Timeouts.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value)));
        return manifest;
    }
}
