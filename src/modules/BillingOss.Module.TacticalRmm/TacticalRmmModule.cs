using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.TacticalRmm;

/// <summary>Módulo de aprovisionamiento de Tactical RMM: manifiesto y operaciones (sección 9.1 del protocolo).</summary>
internal static class TacticalRmmModule
{
    public const string InstallerLink = "installer_link";

    /// <summary>stepBudget es para las pruebas: con cero, cada paso de la baja forzada desinstala un solo agente.</summary>
    public static ModuleDefinition Create(HttpClient http, ILogger logger, TimeSpan? stepBudget = null)
    {
        var operations = new TacticalOperations(new TacticalApi(http, logger), logger, stepBudget ?? TacticalOperations.DefaultStepBudget);
        return new ModuleDefinition(Manifest())
            .Operation("provisioning", "validate", operations.Validate, mutating: false)
            .Operation("provisioning", "create", operations.Create)
            .Operation("provisioning", "suspend", call => operations.Maintenance(call, on: true))
            .Operation("provisioning", "unsuspend", call => operations.Maintenance(call, on: false))
            .Operation("provisioning", "terminate", operations.Terminate)
            .Operation("provisioning", "change_plan", operations.ChangePlan)
            .Operation("provisioning", "get_status", operations.GetStatus, mutating: false)
            .Operation("provisioning", "view", operations.View, mutating: false)
            .Operation("provisioning", "run_action", operations.RunAction)
            .InstanceTest(operations.Test)
            .Poll(operations.Poll);
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "tacticalrmm",
        ["name"] = "Tactical RMM",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("provisioning"),
        ["capabilities"] = new JsonArray(
            "provisioning.core", "provisioning.status", "provisioning.view", "provisioning.change_plan", "provisioning.actions"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["api_url", "api_key", "client_name_prefix"],
              "properties": {
                "api_url": { "type": "string", "format": "uri", "pattern": "^https://", "description": "Origen de la API de Tactical RMM, por ejemplo https://api.ejemplo.com" },
                "api_key": { "type": "string", "x-secret": true, "description": "API key de un usuario propio del módulo, con un rol de mínimo privilegio" },
                "client_name_prefix": { "type": "string", "pattern": "^[A-Za-z0-9_-]{1,16}$", "description": "Prefijo del nombre de cada cliente en Tactical. Prefijo más ID del servicio es la marca del alta. Distinto en cada instalación de billing-oss que use el mismo Tactical" },
                "site_name": { "type": "string", "pattern": "^[A-Za-z0-9 _-]{1,64}$", "default": "OficinaPrincipal", "description": "Nombre del sitio que se crea con cada cliente" },
                "deploy_expiry_days": { "type": "integer", "minimum": 1, "maximum": 30, "default": 7, "description": "Días de vigencia de los links de instalación" }
              }
            }
            """),
        ["product_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["agent_limit"],
              "properties": {
                "agent_limit": { "type": "integer", "minimum": 1, "description": "Agentes contratados. Tactical no tiene cupo: el módulo lo informa y lo controla al bajar de plan" }
              }
            }
            """),
        ["credentials"] = new JsonArray(),
        ["actions"] = new JsonArray(JsonNode.Parse($$"""
            {
              "id": "{{InstallerLink}}",
              "label": { "es": "Link de instalación del agente", "en": "Agent installer link" },
              "audience": ["staff", "client"],
              "confirm": true,
              "input_schema": {
                "type": "object",
                "properties": { "agent_type": { "type": "string", "enum": ["workstation", "server"], "default": "workstation" } }
              }
            }
            """)),
        ["data_scopes"] = new JsonArray(),

        // Hasta tres pedidos a Tactical de 15 segundos cada uno. Un paso de la baja forzada desinstala agentes
        // durante 30 segundos y después borra el cliente (TacticalOperations.DefaultStepBudget).
        ["timeouts"] = new JsonObject
        {
            ["create"] = 60,
            ["suspend"] = 45,
            ["unsuspend"] = 45,
            ["terminate"] = 90,
            ["change_plan"] = 45,
            ["get_status"] = 45,
            ["view"] = 60,
            ["run_action"] = 60,
        },
        ["max_in_flight"] = 4,
    };
}
