using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>Falla definitiva: el trabajo queda muerto y visible para el staff. Code es el código del protocolo si la informó un módulo.</summary>
public sealed class PermanentFailureException(string message, string? code = null, bool reviewed = false) : Exception(message)
{
    public string? Code { get; } = code;

    /// <summary>Una persona ya decidió: el módulo manual contesta lo que el staff cerró, y no hace falta otra revisión ni marcar nada.</summary>
    public bool Reviewed { get; } = reviewed;
}

/// <summary>Falla transitoria: el trabajo vuelve a la cola, respetando la espera que pidió el módulo.</summary>
public sealed class RetryLaterException(string message, TimeSpan? after = null) : Exception(message)
{
    public TimeSpan? After { get; } = after;
}

/// <summary>
/// Operaciones de servicios por el protocolo v0 (D-08): arma el sobre con la configuración descifrada, el
/// contexto mínimo y las credenciales que genera el núcleo, y clasifica la respuesta.
/// </summary>
public sealed class ProtocolServiceModules(BillingDbContext db, ModuleClient client, ModuleRegistry registry, SecretBox box, ManualModule manual, StaffTaskService tasks) : IServiceModules
{
    public async Task<ModuleOutcome> Execute(Service service, ServiceOperation op, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(op);
        var module = await registry.Resolve(service.ModuleInstanceId!.Value, ct).ConfigureAwait(false);
        var (endpoint, manifest, instanceData) = (module.Endpoint, module.Manifest, module.Data);
        var context = await Context(service, manifest, ct).ConfigureAwait(false);
        var operation = op.Operation;
        var timeout = module.Timeout(operation);
        var input = JsonNode.Parse(op.Input ?? "{}") as JsonObject ?? [];
        var action = operation == "run_action" ? input["action"]?.GetValue<string>() : null;
        var key = string.Create(CultureInfo.InvariantCulture, $"svc-{service.Id}-{operation}{(action is null ? string.Empty : "-" + action)}-{service.Generation}");

        ModuleCallResult result;
        if (PendingOf(service, operation) is { } operationId)
        {
            var path = $"/v0/operations/{operationId}";
            var request = new ModuleRequest(instanceData, context, []);
            result = module.InCore
                ? await manual.Call(module, path, request, null, ct).ConfigureAwait(false)
                : await client.Call(endpoint, path, request, null, timeout, ct).ConfigureAwait(false);
            if (result.Error?.Code == ErrorCodes.NotFound)
            {
                // El módulo perdió la operación: se repite la original con la misma clave (sección 7 del protocolo).
                service.PendingOperation = null;
                throw new RetryLaterException("El módulo perdió la operación en curso.", TimeSpan.FromSeconds(5));
            }
        }
        else
        {
            var names = CredentialNames(manifest, operation, action);
            if (names.Count > 0)
            {
                input["credentials"] = await Credentials(service, names, ct).ConfigureAwait(false);
            }

            var path = $"/v0/provisioning/{operation}";
            var request = new ModuleRequest(instanceData, context, input);
            result = module.InCore
                ? await manual.Call(module, path, request, key, ct).ConfigureAwait(false)
                : await client.Call(endpoint, path, request, key, timeout, ct).ConfigureAwait(false);
        }

        return await Interpret(service, module, operation, key, input, op.Dunning, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Consulta sin efecto, como get_status: va sin clave de idempotencia ni credenciales y devuelve el result del
    /// módulo. Un error falla como en una operación, reintentable o definitivo.
    /// </summary>
    public async Task<JsonObject> Query(Service service, string operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        var module = await registry.Resolve(service.ModuleInstanceId!.Value, ct).ConfigureAwait(false);
        var context = await Context(service, module.Manifest, ct).ConfigureAwait(false);
        var path = $"/v0/provisioning/{operation}";
        var request = new ModuleRequest(module.Data, context, []);
        var result = module.InCore
            ? await manual.Call(module, path, request, null, ct).ConfigureAwait(false)
            : await client.Call(module.Endpoint, path, request, null, module.Timeout(operation), ct).ConfigureAwait(false);

        // Como en una operación, el estado se guarda también cuando la consulta falló.
        if (result.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        if (result.Error is { } error)
        {
            throw Retryable(error)
                ? new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
                : new PermanentFailureException($"{error.Code}: {error.Message}", error.Code);
        }

        return result.Response!.Result as JsonObject ?? [];
    }

    /// <summary>
    /// Reintentable por su código (sección 10 del protocolo), salvo que el módulo mande details.auto_retry en false:
    /// pide que una persona lo mire antes de repetirlo, y el núcleo lo toma como definitivo.
    /// </summary>
    internal static bool Retryable(ModuleError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ErrorCodes.IsRetryable(error.Code) && !(error.Details?["auto_retry"] is JsonValue flag && flag.TryGetValue<bool>(out var auto) && !auto);
    }

    /// <summary>
    /// Las credenciales que lleva la operación (sección 5 del protocolo): las del manifiesto en create, y en run_action
    /// las que la acción declara en credentials (las usa) o en rotates_credentials (las cambia, y el núcleo ya las generó
    /// de nuevo al pedir la acción).
    /// </summary>
    internal static List<string> CredentialNames(JsonObject manifest, string operation, string? action)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        IEnumerable<JsonNode?> names = operation switch
        {
            "create" => manifest["credentials"] as JsonArray ?? [],
            "run_action" when Action(manifest, action) is { } declared => (declared["credentials"] as JsonArray ?? []).Concat(declared["rotates_credentials"] as JsonArray ?? []),
            _ => [],
        };
        return names.Select(n => n?.GetValue<string>()).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>La acción del manifiesto aceptado, o null si no existe.</summary>
    internal static JsonObject? Action(JsonObject manifest, string? action)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return action is null ? null : (manifest["actions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => a["id"]?.GetValue<string>() == action);
    }

    /// <summary>
    /// La operación larga en curso, solo si la abrió esta misma operación. Una de otra intención (por ejemplo una baja
    /// que quedó en curso cuando el staff pidió reactivar) no se retoma: se descarta y la operación nueva se pide entera.
    /// </summary>
    private static string? PendingOf(Service service, string operation)
    {
        if (service.PendingOperation is not { } pending)
        {
            return null;
        }

        var split = pending.IndexOf(':', StringComparison.Ordinal);
        if (split < 0 || pending[..split] == operation)
        {
            return split < 0 ? pending : pending[(split + 1)..];
        }

        service.PendingOperation = null;
        return null;
    }

    private async Task<ModuleOutcome> Interpret(Service service, ResolvedInstance module, string operation, string key, JsonObject input, bool dunning, ModuleCallResult result, CancellationToken ct)
    {
        // El estado se guarda también cuando la llamada falló: un ticket nuevo no se pierde con el error.
        if (result.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        if (result.Error is { } error)
        {
            if (Retryable(error))
            {
                throw new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null);
            }

            // Error definitivo, manual_review incluido (sección 10 del protocolo): no se reintenta, lo que estaba en curso
            // ya no se consulta y el staff lo ve como tarea de revisión del servicio. Del módulo manual no: lo que contesta
            // es lo que el staff ya cerró, y otra tarea abierta sobre el mismo servicio le trabaría la próxima.
            service.PendingOperation = null;
            if (!module.InCore)
            {
                await tasks.OpenReview(module.Instance.Id, operation, key, service, input, error, dunning, ct).ConfigureAwait(false);
            }

            throw new PermanentFailureException($"{error.Code}: {error.Message}", error.Code, reviewed: module.InCore);
        }

        var response = result.Response!;
        if (response.Status == ModuleResponse.PendingStatus)
        {
            service.PendingOperation = $"{operation}:{response.OperationId}";
            return new ModuleOutcome(false, PollAfter: TimeSpan.FromSeconds(response.RetryAfter ?? 15));
        }

        service.PendingOperation = null;
        var external = response.Result?["external_ids"] is JsonObject ids ? Merge(service.ExternalIds, ids) : null;
        if (response.Result?["secrets"] is JsonObject secrets)
        {
            foreach (var (name, value) in secrets)
            {
                await StoreSecret(db, box, service.Id, name, value?.ToString() ?? string.Empty, ct).ConfigureAwait(false);
            }
        }

        // La etiqueta que informa el módulo (por ejemplo el hostname) vale si el pedido no trajo una (sección 6 del protocolo).
        if (string.IsNullOrEmpty(service.Label) && response.Result?["label"] is JsonValue labelValue && labelValue.TryGetValue<string>(out var label) && label.Length > 0)
        {
            service.Label = label.Length > 255 ? label[..255] : label;
        }

        return new ModuleOutcome(true, response.Result?["observed_state"]?.GetValue<string>(), external);
    }

    private Task<JsonObject> Context(Service service, JsonObject manifest, CancellationToken ct) => ServiceContext(db, service, manifest, ct);

    /// <summary>
    /// Contexto mínimo (sección 8 del protocolo). Los datos de contacto van solo si el manifiesto los pidió. Lo usan las
    /// operaciones y la lectura del uso.
    /// </summary>
    internal static async Task<JsonObject> ServiceContext(BillingDbContext db, Service service, JsonObject manifest, CancellationToken ct)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == service.AccountId, ct).ConfigureAwait(false);
        var line = service.OrderLineId is { } lineId
            ? await db.Set<OrderLine>().Where(l => l.Id == lineId).Select(l => new { l.OrderData, l.Options }).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var accountNode = new JsonObject { ["id"] = account.Id, ["language"] = account.Language };
        if (manifest["data_scopes"] is JsonArray scopes && scopes.Any(s => s?.GetValue<string>() == "account.contact"))
        {
            accountNode["name"] = account.Name;
            accountNode["email"] = account.Email;
        }

        return new JsonObject
        {
            ["service"] = new JsonObject
            {
                ["id"] = service.Id,
                ["uuid"] = service.Uuid.ToString(),
                ["status"] = service.Status.ToString().ToLowerInvariant(),
                ["label"] = service.Label,
                ["external_ids"] = JsonNode.Parse(service.ExternalIds),
                ["params"] = JsonNode.Parse(service.ModuleParams),
                ["order"] = JsonNode.Parse(line?.OrderData ?? "{}"),
                ["options"] = JsonNode.Parse(line?.Options ?? "{}"),
                ["plan"] = new JsonObject { ["unit"] = service.CycleUnit.ToString().ToLowerInvariant(), ["count"] = service.CycleCount },
            },
            ["account"] = accountNode,
        };
    }

    /// <summary>Las genera una sola vez y las guarda antes de llamar: un reintento manda las mismas.</summary>
    private async Task<JsonObject> Credentials(Service service, IEnumerable<string> names, CancellationToken ct)
    {
        var result = new JsonObject();
        foreach (var name in names)
        {
            var stored = await db.ServiceSecrets.SingleOrDefaultAsync(s => s.ServiceId == service.Id && s.Name == name, ct).ConfigureAwait(false);
            string value;
            if (stored is null)
            {
                value = Passwords.New();
                await StoreSecret(db, box, service.Id, name, value, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            else
            {
                value = box.Decrypt(stored.Value, "services.service_secrets", name, service.Id);
            }

            result[name] = value;
        }

        return result;
    }

    /// <summary>Credencial del servicio cifrada por campo (D-09). Queda en el contexto: la guarda quien llama.</summary>
    internal static async Task StoreSecret(BillingDbContext db, SecretBox box, long serviceId, string name, string value, CancellationToken ct)
    {
        var secret = await db.ServiceSecrets.SingleOrDefaultAsync(s => s.ServiceId == serviceId && s.Name == name, ct).ConfigureAwait(false);
        if (secret is null)
        {
            secret = new ServiceSecret { ServiceId = serviceId, Name = name, CreatedAt = DateTimeOffset.UtcNow };
            db.ServiceSecrets.Add(secret);
        }

        secret.Value = box.Encrypt(value, "services.service_secrets", name, serviceId);
    }

    /// <summary>Los IDs externos que informa el módulo se suman a los que ya tiene el servicio, clave por clave.</summary>
    internal static string Merge(string current, JsonObject incoming)
    {
        var merged = JsonNode.Parse(current)?.AsObject() ?? [];
        foreach (var (k, v) in incoming)
        {
            merged[k] = v?.DeepClone();
        }

        return merged.ToJsonString();
    }
}
