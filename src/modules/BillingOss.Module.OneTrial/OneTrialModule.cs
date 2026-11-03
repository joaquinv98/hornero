using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.OneTrial;

/// <summary>
/// Extensión de ejemplo (sección 9.5 del protocolo y docs/integraciones): una sola prueba gratis por cuenta. Valida
/// cada pedido antes de que se confirme y deja en su log los eventos que recibe. Es el ejemplo completo de la guía:
/// todo lo que hace una extensión real está acá, sin tocar el núcleo.
/// </summary>
public static class OneTrialModule
{
    public static ModuleDefinition Create(ILogger logger) =>
        new ModuleDefinition(Manifest())
            .Operation("extension", "validate_order", ValidateOrder, mutating: false)
            .Operation("extension", "on_event", call => OnEvent(call, logger))
            .InstanceTest(Test);

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "one-trial",
        ["name"] = "Una prueba gratis por cuenta",
        ["version"] = "1.0.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("extension"),
        ["capabilities"] = new JsonArray("extension.validate_order", "extension.events"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["trial_product_ids"],
              "properties": {
                "trial_product_ids": { "type": "array", "items": { "type": "integer" }, "description": "Productos que son prueba gratis" }
              }
            }
            """),
        ["data_scopes"] = new JsonArray(),

        // El cliente espera mientras se valida: el núcleo corta a los 3 segundos.
        ["timeouts"] = new JsonObject { ["validate_order"] = 3 },
    };

    /// <summary>
    /// Rechaza el pedido si pide más de una prueba o si la cuenta ya tuvo una que no se canceló antes de empezar. El
    /// motivo va en el idioma de la cuenta, porque lo ve el cliente.
    /// </summary>
    private static Task<ModuleResponse> ValidateOrder(ModuleCall call)
    {
        var trials = Trials(call);
        var account = call.Request.Context["account"];
        var english = account?["language"]?.GetValue<string>()?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
        var asked = (call.Request.Input["items"] as JsonArray ?? [])
            .Where(i => i?["product_id"] is JsonValue p && trials.Contains(p.GetValue<long>()))
            .Sum(i => i!["quantity"]?.GetValue<int>() ?? 1);
        var used = (account?["services"] as JsonArray ?? [])
            .Any(s => s?["product_id"] is JsonValue p && trials.Contains(p.GetValue<long>()) && s["status"]?.GetValue<string>() != "cancelled");

        string? reason = asked switch
        {
            0 => null,
            > 1 => english ? "The free trial is only one." : "La prueba gratis es una sola.",
            _ when used => english ? "You already used your free trial. Choose a paid plan." : "Ya usaste la prueba gratis. Elegí un plan pago.",
            _ => null,
        };
        return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["allowed"] = reason is null, ["reason"] = reason }));
    }

    /// <summary>
    /// Un evento del catálogo. Acá una extensión real avisa a su CRM o a su chat. El SDK ya garantiza que un reintento
    /// con la misma clave no repite este efecto.
    /// </summary>
    private static Task<ModuleResponse> OnEvent(ModuleCall call, ILogger logger)
    {
        var input = call.Request.Input;
        logger.LogInformation("Evento {Type} {Id} con {Data}", input["type"]?.GetValue<string>(), input["id"]?.GetValue<string>(), input["data"]?.ToJsonString());
        return Task.FromResult(ModuleResponse.Completed());
    }

    private static Task<ModuleResponse> Test(ModuleCall call)
    {
        Trials(call);
        return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["ok"] = true }));
    }

    private static HashSet<long> Trials(ModuleCall call) =>
        call.Request.Instance.Config["trial_product_ids"] is JsonArray ids && ids.All(i => i is JsonValue)
            ? [.. ids.Select(i => i!.GetValue<long>())]
            : throw new ModuleException(ErrorCodes.InvalidConfig, "Falta trial_product_ids: la lista de productos que son prueba gratis.");
}
