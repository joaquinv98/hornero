using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Modules.Sdk;

/// <summary>Lee el valor actual de las métricas pedidas. Una que no se pudo leer no va en el resultado.</summary>
public delegate Task<IReadOnlyDictionary<string, decimal>> UsageReader(ModuleCall call, IReadOnlyList<string> metrics);

public static class ModuleUsage
{
    /// <summary>
    /// Registra la operación usage (sección 9.1 del protocolo): una lectura sin efecto, que no pide Idempotency-Key.
    /// El autor devuelve un decimal por métrica y la SDK arma la respuesta con las pedidas, como texto. Los contadores
    /// van crudos: las diferencias las calcula el núcleo.
    /// </summary>
    public static ModuleDefinition Usage(this ModuleDefinition module, UsageReader reader)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(reader);
        if (!UsageProtocol.Declares(module.Manifest) || UsageProtocol.Declared(module.Manifest).Count == 0)
        {
            throw new InvalidOperationException($"El manifiesto tiene que declarar {UsageProtocol.Capability} y sus métricas en usage_metrics.");
        }

        return module.Operation("provisioning", UsageProtocol.Operation, async call =>
        {
            var wanted = Requested(call.Request.Input);
            var values = await reader(call, wanted).ConfigureAwait(false);
            var metrics = new JsonObject();
            foreach (var id in wanted.Where(values.ContainsKey))
            {
                var value = values[id];
                if (value < 0m)
                {
                    // Un error del módulo, no del sistema externo: que se vea, y no una lectura que el núcleo descartaría.
                    throw new ModuleException(ErrorCodes.Internal, $"La lectura de {id} es negativa.");
                }

                metrics[id] = UsageProtocol.Format(value);
            }

            return ModuleResponse.Completed(new JsonObject { ["metrics"] = metrics });
        }, mutating: false);
    }

    private static List<string> Requested(JsonObject input) =>
        input["metrics"] is JsonArray list && list.All(n => n is JsonValue v && v.TryGetValue<string>(out _))
            ? [.. list.Select(n => n!.GetValue<string>()).Distinct(StringComparer.Ordinal)]
            : throw new ModuleException(ErrorCodes.InvalidInput, "Falta la lista de métricas.", details: new JsonObject { ["fields"] = new JsonArray("metrics") });
}
