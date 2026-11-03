using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Pterodactyl;

/// <summary>Módulo de aprovisionamiento de game servers sobre Pterodactyl: manifiesto y operaciones (PTR-01).</summary>
internal static class PterodactylModule
{
    public static ModuleDefinition Create(HttpClient http, ILogger logger)
    {
        var operations = new PterodactylOperations(new PanelApi(http, logger));
        return new ModuleDefinition(Manifest())
            .Operation("provisioning", "validate", operations.Validate, mutating: false)
            .Operation("provisioning", "create", operations.Create)
            .Operation("provisioning", "suspend", operations.Suspend)
            .Operation("provisioning", "unsuspend", operations.Unsuspend)
            .Operation("provisioning", "terminate", operations.Terminate)
            .Operation("provisioning", "change_plan", operations.ChangePlan)
            .Operation("provisioning", "get_status", operations.GetStatus, mutating: false)
            .Operation("provisioning", "view", operations.View, mutating: false)
            .Operation("provisioning", "run_action", operations.RunAction)
            .InstanceTest(operations.Test);
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "pterodactyl",
        ["name"] = "Pterodactyl",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("provisioning"),
        ["capabilities"] = new JsonArray(
            "provisioning.core", "provisioning.change_plan", "provisioning.status", "provisioning.view", "provisioning.actions"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["panel_url", "application_key"],
              "properties": {
                "panel_url": { "type": "string", "format": "uri", "pattern": "^https://", "description": "URL del panel, por ejemplo https://panel.example.com" },
                "application_key": { "type": "string", "x-secret": true, "description": "Clave ptla_ de la Application API: servers, users y allocations en lectura y escritura, nodes, locations, nests y eggs en lectura" }
              }
            }
            """),
        ["product_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["nest_id", "egg_id", "location_id", "memory_mb", "disk_mb", "cpu_percent"],
              "properties": {
                "nest_id": { "type": "integer", "minimum": 1 },
                "egg_id": { "type": "integer", "minimum": 1 },
                "location_id": { "type": "integer", "minimum": 1 },
                "memory_mb": { "type": "integer", "minimum": 0, "description": "0 es sin límite" },
                "disk_mb": { "type": "integer", "minimum": 0, "description": "0 es sin límite" },
                "cpu_percent": { "type": "integer", "minimum": 0, "description": "100 es un núcleo, 0 es sin límite" },
                "swap_mb": { "type": "integer", "minimum": 0, "default": 0 },
                "io_weight": { "type": "integer", "minimum": 10, "maximum": 1000, "default": 500 },
                "oom_disabled": { "type": "boolean", "default": false },
                "databases": { "type": "integer", "minimum": 0, "default": 0 },
                "backups": { "type": "integer", "minimum": 0, "default": 0 },
                "allocation_limit": { "type": "integer", "minimum": 0, "default": 0 },
                "server_name": { "type": "string", "maxLength": 191, "description": "Nombre del server. Sin él va Servicio y el ID" },
                "startup": { "type": "string", "description": "Comando de inicio. Sin él va el del egg" },
                "variables": {
                  "type": "object",
                  "description": "Valores fijos de variables del egg, por env_variable o por nombre visible. Las opciones configurables suman las suyas acá",
                  "additionalProperties": { "type": ["string", "number", "boolean"] }
                },
                "extra_ports": {
                  "type": "object",
                  "description": "Puertos adicionales del alta en la misma IP. variables lleva el desplazamiento de cada variable desde el puerto principal",
                  "required": ["count"],
                  "properties": {
                    "count": { "type": "integer", "minimum": 1, "maximum": 20 },
                    "contiguous": { "type": "boolean", "default": false },
                    "variables": { "type": "object", "additionalProperties": { "type": "integer", "minimum": 1 } }
                  }
                },
                "order_fields": {
                  "type": "array",
                  "description": "Datos que completa el cliente al contratar. Van en context.service.order.variables y los secretos se guardan cifrados",
                  "items": {
                    "type": "object",
                    "required": ["key"],
                    "properties": {
                      "key": { "type": "string", "minLength": 1, "description": "env_variable o nombre visible de la variable del egg, o egg_id" },
                      "label": { "type": "object", "additionalProperties": { "type": "string" } },
                      "required": { "type": "boolean", "default": false },
                      "secret": { "type": "boolean", "default": false },
                      "options": { "type": "array", "items": { "type": "string" } }
                    }
                  }
                }
              }
            }
            """),
        ["order_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "variables": { "type": "object", "additionalProperties": { "type": "string", "maxLength": 1024 } }
              }
            }
            """),

        // El usuario del panel se crea sin contraseña: el panel le manda al cliente el enlace para definirla.
        ["credentials"] = new JsonArray(),
        ["actions"] = JsonNode.Parse($$"""
            [
              {
                "id": "{{PterodactylOperations.SetPanelPassword}}",
                "label": { "es": "Cambiar la contraseña del panel", "en": "Change panel password" },
                "audience": ["staff"],
                "confirm": true,
                "credentials": ["panel_password"]
              }
            ]
            """),

        // El usuario del panel se crea con el correo y el nombre del cliente.
        ["data_scopes"] = new JsonArray("account.contact"),
        ["timeouts"] = new JsonObject { ["create"] = 120, ["change_plan"] = 60, ["terminate"] = 60 },

        // El panel comparte host con otros servicios y ya dio 504 por timeout.
        ["max_in_flight"] = 2,
    };
}
