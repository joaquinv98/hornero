using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.CPanel;

/// <summary>Módulo de hosting en cPanel, administrado por la API 1 de WHM: manifiesto y panel (sección 9.1 del protocolo).</summary>
internal static class CPanelModule
{
    public static ModuleDefinition Create(Func<string?, HttpClient> http, ILogger logger) => Create(http, logger, null);

    /// <param name="http">El cliente HTTP para una huella de certificado (o ninguna).</param>
    /// <param name="time">Reloj para el plazo de cada operación. Los tests lo adelantan a mano.</param>
    public static ModuleDefinition Create(Func<string?, HttpClient> http, ILogger logger, TimeProvider? time) =>
        HostingModule.Create(Manifest(), (call, budget, hidden) => WhmPanel.Open(call, http, budget, logger, hidden), time);

    public static JsonObject Manifest() => HostingModule.Manifest("cpanel", "cPanel y WHM", "0.1.0", JsonNode.Parse("""
        {
          "type": "object",
          "required": ["host", "api_token"],
          "properties": {
            "host": { "type": "string", "format": "hostname", "description": "Nombre del servidor WHM, sin esquema, puerto ni ruta. Es también el host del acceso directo" },
            "port": { "type": "integer", "minimum": 1, "maximum": 65535, "default": 2087, "description": "Puerto de WHM con TLS" },
            "tls_fingerprint": {
              "type": "string",
              "pattern": "^([0-9A-Fa-f]{2}:?){31}[0-9A-Fa-f]{2}$",
              "description": "SHA-256 del certificado de WHM si no lo firma una CA pública"
            },
            "api_user": { "type": "string", "pattern": "^[a-z][a-z0-9]{0,15}$", "default": "root", "description": "Usuario dueño del token de API: root o un revendedor" },
            "api_token": { "type": "string", "x-secret": true, "description": "Token de API de WHM con los privilegios del módulo" },
            "username_prefix": {
              "type": "string",
              "pattern": "^[a-z]{1,4}$",
              "default": "c",
              "description": "Prefijo del usuario de las cuentas nuevas, que sigue con letras y números que salen del uuid del servicio"
            }
          }
        }
        """)!.AsObject(), "Paquete de WHM");
}
