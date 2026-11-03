using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.DirectAdmin;

/// <summary>Módulo de hosting en DirectAdmin, administrado por su API de comandos: manifiesto y panel (sección 9.1 del protocolo).</summary>
internal static class DirectAdminModule
{
    public static ModuleDefinition Create(Func<string?, HttpClient> http, ILogger logger) => Create(http, logger, null);

    /// <param name="http">El cliente HTTP para una huella de certificado (o ninguna).</param>
    /// <param name="time">Reloj para el plazo de cada operación. Los tests lo adelantan a mano.</param>
    public static ModuleDefinition Create(Func<string?, HttpClient> http, ILogger logger, TimeProvider? time) =>
        HostingModule.Create(Manifest(), (call, budget, hidden) => DirectAdminPanel.Open(call, http, budget, logger, hidden), time);

    public static JsonObject Manifest() => HostingModule.Manifest("directadmin", "DirectAdmin", "0.1.0", JsonNode.Parse("""
        {
          "type": "object",
          "required": ["host", "api_key", "ip_address"],
          "properties": {
            "host": { "type": "string", "format": "hostname", "description": "Nombre del servidor DirectAdmin, sin esquema, puerto ni ruta. Es también el host del acceso directo" },
            "port": { "type": "integer", "minimum": 1, "maximum": 65535, "default": 2222, "description": "Puerto de DirectAdmin con TLS, el de la API y el del panel" },
            "tls_fingerprint": {
              "type": "string",
              "pattern": "^([0-9A-Fa-f]{2}:?){31}[0-9A-Fa-f]{2}$",
              "description": "SHA-256 del certificado de DirectAdmin si no lo firma una CA pública"
            },
            "api_user": { "type": "string", "pattern": "^[a-z][a-z0-9]{0,15}$", "default": "admin", "description": "Usuario administrador dueño de la clave de acceso" },
            "api_key": { "type": "string", "x-secret": true, "description": "Clave de acceso (login key) del administrador con los comandos del módulo" },
            "ip_address": { "type": "string", "format": "ipv4", "description": "IPv4 compartida del servidor para las cuentas nuevas" },
            "username_prefix": {
              "type": "string",
              "pattern": "^[a-z]{1,4}$",
              "default": "c",
              "description": "Prefijo del usuario de las cuentas nuevas, que sigue con letras y números que salen del uuid del servicio"
            }
          }
        }
        """)!.AsObject(), "Paquete de usuario de DirectAdmin");
}
