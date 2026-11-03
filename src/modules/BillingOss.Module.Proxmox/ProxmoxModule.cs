using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Proxmox;

/// <summary>Módulo de aprovisionamiento de VPS en Proxmox VE: manifiesto y operaciones (sección 9.1 del protocolo).</summary>
internal static class ProxmoxModule
{
    /// <param name="handlers">Arma el handler HTTP hacia Proxmox con el CA de la instancia, uno por llamada.</param>
    /// <param name="gateway">Cliente hacia el gateway de consola, con la validación TLS del sistema.</param>
    public static ModuleDefinition Create(
        Func<X509Certificate2Collection?, HttpMessageHandler> handlers,
        HttpClient gateway,
        TimeProvider time,
        ILogger logger,
        ProxmoxOptions? options = null)
    {
        var operations = new Operations(handlers, gateway, time, logger, options ?? ProxmoxOptions.Default);
        return new ModuleDefinition(Manifest())
            .Operation("provisioning", "validate", operations.Validate, mutating: false)
            .Operation("provisioning", "create", operations.Create)
            .Operation("provisioning", "suspend", operations.Suspend)
            .Operation("provisioning", "unsuspend", operations.Unsuspend)
            .Operation("provisioning", "terminate", operations.Terminate)
            .Operation("provisioning", "run_action", operations.RunAction)
            .Operation("provisioning", "get_status", operations.GetStatus, mutating: false)
            .Operation("provisioning", "view", operations.View, mutating: false)
            .Operation("provisioning", "sso", operations.Sso, mutating: false)
            .InstanceTest(operations.Test)
            .Poll(operations.Poll);
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "proxmox",
        ["name"] = "Proxmox VE",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("provisioning"),
        ["capabilities"] = new JsonArray("provisioning.core", "provisioning.status", "provisioning.actions", "provisioning.view", "provisioning.sso"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["api_url", "token_id", "token_secret"],
              "properties": {
                "api_url": { "type": "string", "format": "uri", "pattern": "^https://[^/?#\\s@]+/?$", "description": "Origen https de la API de Proxmox, sin ruta. Por ejemplo https://pve.example.com:8006" },
                "token_id": { "type": "string", "pattern": "^[^\\s=!@:]+@[A-Za-z][A-Za-z0-9._-]*![A-Za-z][A-Za-z0-9._-]*$", "description": "ID del token de API: usuario@realm!nombre" },
                "token_secret": { "type": "string", "x-secret": true, "description": "Secreto del token de API" },
                "ca_pem": { "type": "string", "description": "CA propio de Proxmox en PEM. Si está, se confía solo en ese CA. Reemplaza a PROXMOX_CA_FILE" },
                "network_pool": {
                  "type": "array",
                  "description": "Pool de red en orden: se toma la primera entrada cuya MAC no esté en ninguna VM ni contenedor del cluster",
                  "items": {
                    "type": "object",
                    "required": ["mac", "public_ip"],
                    "additionalProperties": false,
                    "properties": {
                      "mac": { "type": "string", "pattern": "^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$" },
                      "public_ip": { "type": "string", "anyOf": [{ "format": "ipv4" }, { "format": "ipv6" }] },
                      "bridge": { "type": "string", "default": "vmbr1", "pattern": "^[a-zA-Z][a-zA-Z0-9_.-]{0,14}$" },
                      "mtu": { "type": "integer", "minimum": 576, "maximum": 9000 },
                      "private_ip": { "type": "string", "anyOf": [{ "format": "ipv4" }, { "format": "ipv6" }], "description": "La IP que el DHCP reserva para esa MAC. Arma el ipset ipfilter-net0 del anti-spoofing" }
                    }
                  }
                },
                "console_origin": { "type": "string", "format": "uri", "pattern": "^https://[^/?#\\s@]+/?$", "x-browser-host": true, "description": "Origen https del gateway de consola noVNC. Su host va en la lista de hosts del navegador de la instancia" },
                "console_secret": { "type": "string", "x-secret": true, "minLength": 32, "description": "Secreto compartido con el gateway, de al menos 32 bytes. Se usa el texto literal" },
                "console_server_key": { "type": "string", "default": "6", "pattern": "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", "description": "Clave del servidor en la configuración del gateway desplegado" },
                "legacy_acl_cleanup": { "type": "boolean", "default": false, "description": "Quitar la ACL PVEVMUser de los usuarios client más el ID de cuenta que dejaron versiones viejas, al suspender. Pide Permissions.Modify en /vms y Sys.Audit o User.Modify en /access/groups" },
                "reserved_vmids": {
                  "type": "array",
                  "default": [{ "from": 9000, "to": 9019 }],
                  "description": "Rangos de VMID que el alta no usa aunque coincidan con el ID del servicio, por ejemplo las plantillas",
                  "items": {
                    "type": "object",
                    "required": ["from", "to"],
                    "properties": {
                      "from": { "type": "integer", "minimum": 100, "maximum": 999999999 },
                      "to": { "type": "integer", "minimum": 100, "maximum": 999999999 }
                    }
                  }
                }
              }
            }
            """),
        ["product_schema"] = JsonNode.Parse("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": ["template_id", "node", "cores", "memory_mb", "disk_gb", "rate_mb_s"],
              "properties": {
                "template_id": { "type": "integer", "minimum": 100, "x-client-option": true, "description": "VMID de la plantilla a clonar" },
                "node": { "type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$", "description": "Nodo donde está la plantilla y donde queda el clon" },
                "cores": { "type": "integer", "minimum": 1, "x-client-option": true },
                "memory_mb": { "type": "integer", "minimum": 1, "x-client-option": true },
                "disk_gb": { "type": "integer", "minimum": 1, "x-client-option": true },
                "rate_mb_s": { "type": "string", "pattern": "^[0-9]+(\\.[0-9]+)?$", "x-client-option": true, "description": "Límite de red en MB/s, la unidad del rate de net0 en Proxmox" },
                "console_enabled": { "type": "boolean", "default": false },
                "hostname_suffix": { "type": "string", "default": "", "description": "Sufijo de la etiqueta del servicio: Host_ más la IP con guiones más el sufijo" },
                "isolation_group_prefix": { "type": "string", "default": "", "pattern": "^([A-Za-z][A-Za-z0-9_-]{0,17})?$", "description": "Prefijo del security group de cluster.fw que aísla la VM, como prefijo más bridge. Vacío no aísla" },
                "os_minimum_policy": {
                  "type": "object",
                  "description": "Mínimos por plantilla, por ejemplo {\"9002\":{\"cpu\":2,\"ramMB\":3072,\"diskGB\":40}}",
                  "additionalProperties": false,
                  "patternProperties": {
                    "^[0-9]+$": {
                      "type": "object",
                      "additionalProperties": false,
                      "properties": {
                        "cpu": { "type": "integer", "minimum": 1 },
                        "ramMB": { "type": "integer", "minimum": 1 },
                        "diskGB": { "type": "integer", "minimum": 1 }
                      }
                    }
                  }
                },
                "console_pool": { "type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$", "description": "Pool de Proxmox del gateway de consola. Los clones entran a ese pool" }
              }
            }
            """),
        ["credentials"] = new JsonArray("guest_password"),
        ["actions"] = new JsonArray(
            Action("start", "Encender", "Start", ["staff", "client"], confirm: true),
            Action("stop", "Apagar", "Stop", ["staff", "client"], confirm: true),
            Action("reboot", "Reiniciar", "Reboot", ["staff", "client"], confirm: true),
            Action("reinstall", "Reinstalar", "Reinstall", ["staff", "client"], confirm: true, credentials: ["guest_password"]),
            Action("revoke_console", "Cerrar las consolas", "Close consoles", ["staff"], confirm: false)),
        ["data_scopes"] = new JsonArray(),
        ["timeouts"] = new JsonObject
        {
            ["validate"] = 20,
            ["create"] = 120,
            ["suspend"] = 60,
            ["unsuspend"] = 60,
            ["terminate"] = 60,
            ["run_action"] = 120,
            ["get_status"] = 20,
            ["view"] = 20,
            ["sso"] = 20,
        },
        ["max_in_flight"] = 4,
    };

    private static JsonObject Action(string id, string es, string en, string[] audience, bool confirm, string[]? credentials = null)
    {
        var action = new JsonObject
        {
            ["id"] = id,
            ["label"] = new JsonObject { ["es"] = es, ["en"] = en },
            ["audience"] = new JsonArray(audience.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["confirm"] = confirm,
        };
        if (credentials is not null)
        {
            action["credentials"] = new JsonArray(credentials.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        }

        return action;
    }
}
