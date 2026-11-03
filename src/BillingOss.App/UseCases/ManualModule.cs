using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Domains;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BillingOss.App.UseCases;

public enum StaffTaskStatus
{
    Open,
    Completed,
    Rejected,
}

/// <summary>
/// Tarea para el staff que abre el módulo manual por cada operación con efecto. Es única por instancia y clave de
/// idempotencia, y un servicio o dominio tiene a lo sumo una abierta: lo garantiza la base.
/// </summary>
public sealed class StaffTask
{
    /// <summary>
    /// Tarea de revisión. La abre el núcleo por un error definitivo de un módulo, con el sujeto service:{id} de sus
    /// operaciones (<see cref="StaffTaskService.OpenReview"/>), o el importador por una baja que el sistema anterior dejó
    /// sin ejecutar, con el sujeto review:service:{id}. Mientras la de una baja retenida está abierta, la baja automática
    /// de ese servicio espera (<see cref="DunningService"/>).
    /// </summary>
    public const string Review = "review";

    private const string ReviewPrefix = "review:service:";

    public long Id { get; private set; }

    public long ModuleInstanceId { get; set; }

    /// <summary>provisioning, registrar o review.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>SHA-256 del input: la misma clave con otro input es un conflicto.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>service:{id} o domain:{id}. Es también la clave de entidad de sus trabajos. En una revisión, review:service:{id}.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Lo que el staff necesita para hacerla: instrucciones del producto, opciones y datos del pedido o del dominio. JSON.</summary>
    public string Details { get; set; } = "{}";

    /// <summary>input de la operación. JSON.</summary>
    public string Input { get; set; } = "{}";

    public StaffTaskStatus Status { get; set; }

    /// <summary>Lo que devuelve la operación al cerrarse. Nunca tiene secretos. JSON.</summary>
    public string? Result { get; set; }

    /// <summary>Motivo del rechazo.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Sujeto de la revisión de baja de un servicio. Va aparte del service:{id} de sus operaciones: así no choca con la
    /// tarea abierta del módulo manual (una por sujeto), no se ve como configuración en curso en el portal y su cierre
    /// no pasa por lo que confirmó el staff.
    /// </summary>
    public static string ReviewSubject(long serviceId) => string.Create(CultureInfo.InvariantCulture, $"{ReviewPrefix}{serviceId}");

    /// <summary>El servicio de una revisión de baja, o nulo si el sujeto es de otra tarea.</summary>
    public static long? ReviewedService(string subject) =>
        subject is not null && subject.StartsWith(ReviewPrefix, StringComparison.Ordinal)
        && long.TryParse(subject.AsSpan(ReviewPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

/// <summary>
/// Módulo "manual" que viene con el núcleo (sección 9 del protocolo): aprovisionamiento de productos sin módulo y
/// registrar de los TLD sin API, como .ar de NIC Argentina. Corre dentro del núcleo, sin HTTP, con el mismo sobre,
/// respuestas y códigos de error de v0. Cada operación con efecto abre una tarea del staff y queda en curso hasta
/// que el staff la cierra. Nunca llama a la red.
/// </summary>
public sealed class ManualModule(BillingDbContext db, IOptions<BillingOptions> options, TimeProvider time)
{
    /// <summary>URL base de la instalación del módulo manual: el núcleo lo resuelve sin salir del proceso.</summary>
    public const string Address = "core:manual";

    /// <summary>Una tarea del staff dura días: se consulta cada hora, y al cerrarla el trabajo se reprograma para ya.</summary>
    public const int PollSeconds = 3600;

    private const string ManifestJson = """
        {
          "protocol": ["0"], "id": "manual", "name": "Manual (tareas del staff)", "version": "0.1.0", "vendor": "Hornero",
          "kinds": ["provisioning", "registrar"],
          "capabilities": ["provisioning.core", "provisioning.change_plan", "provisioning.status", "provisioning.view", "registrar.core"],
          "config_schema": {
            "type": "object", "required": ["queue"],
            "properties": {
              "queue": { "type": "string" },
              "registry": { "type": "string", "enum": ["none", "nic.ar", "own_dns", "manual_registrar"] },
              "grace_days": { "type": "integer", "minimum": 0 },
              "task_sla_hours": { "type": "integer", "minimum": 1 }
            }
          },
          "product_schema": {
            "type": "object", "required": ["activation", "lifecycle"],
            "properties": {
              "activation": { "enum": ["on_payment", "staff"] },
              "lifecycle": { "enum": ["state_only", "staff"] },
              "instructions": { "type": "string", "maxLength": 4000 },
              "staff_fields": {
                "type": "array", "maxItems": 20,
                "items": {
                  "type": "object", "required": ["id", "label"],
                  "properties": { "id": { "type": "string" }, "label": { "type": "object" }, "client_visible": { "type": "boolean" }, "secret": { "type": "boolean" } }
                }
              }
            }
          },
          "order_schema": { "type": "object" },
          "credentials": [], "actions": [], "data_scopes": ["account.contact"], "timeouts": {},
          "deadlines": { "create": null, "suspend": null, "unsuspend": null, "terminate": null, "change_plan": null, "register": null, "renew": null, "set_nameservers": null },
          "max_in_flight": 50
        }
        """;

    private static readonly string[] Provisioning = ["validate", "create", "suspend", "unsuspend", "terminate", "change_plan", "get_status", "view"];

    public static JsonObject Manifest() => JsonNode.Parse(ManifestJson)!.AsObject();

    /// <summary>Registra la instalación del módulo manual si falta y le agrega una instancia. Devuelve su ID.</summary>
    public static async Task<long> AddInstance(BillingDbContext db, string name, JsonObject config, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        var installation = await db.ModuleInstallations.SingleOrDefaultAsync(i => i.BaseUrl == Address, ct).ConfigureAwait(false);
        if (installation is null)
        {
            installation = new ModuleInstallation { Name = "manual", BaseUrl = Address, Manifest = ManifestJson, CreatedAt = now };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var instance = new ModuleInstance { InstallationId = installation.Id, Name = name, Config = config.ToJsonString(), CreatedAt = now };
        db.ModuleInstances.Add(instance);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return instance.Id;
    }

    /// <summary>Atiende una llamada del protocolo como lo haría un módulo por HTTP, con la misma forma de respuesta y de error.</summary>
    public async Task<ModuleCallResult> Call(ResolvedInstance module, string path, ModuleRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var instanceId = module.Instance.Id;
            var response = path.Split('/', StringSplitOptions.RemoveEmptyEntries) switch
            {
                ["v0", "operations", var id] => await Poll(instanceId, id, ct).ConfigureAwait(false),
                ["v0", "provisioning", var op] when Provisioning.Contains(op) => await Provision(instanceId, op, request, idempotencyKey, ct).ConfigureAwait(false),
                ["v0", "registrar", var op] => await Registrar(instanceId, op, request, idempotencyKey, ct).ConfigureAwait(false),
                _ => throw Fail(ErrorCodes.NotSupported, $"El módulo manual no hace {path}."),
            };
            return new ModuleCallResult(response, null);
        }
        catch (ManualError e)
        {
            return new ModuleCallResult(null, e.Error);
        }
    }

    private async Task<ModuleResponse> Provision(long instanceId, string op, ModuleRequest request, string? key, CancellationToken ct)
    {
        var service = request.Context["service"]?.AsObject() ?? throw Fail(ErrorCodes.InvalidInput, "Falta context.service.");
        var subject = string.Create(CultureInfo.InvariantCulture, $"service:{service["id"]}");
        if (op == "get_status")
        {
            // Lo que confirmó el staff en la última tarea cerrada. Nunca not_found: la reconciliación no toca estos servicios.
            var last = await LastClosed(subject, null, false, ct).ConfigureAwait(false);
            return ModuleResponse.Completed(new JsonObject { ["observed_state"] = last?["observed_state"]?.DeepClone() ?? "unknown" });
        }

        if (op == "view")
        {
            return await View(service, request.Context["account"]?["language"]?.GetValue<string>() ?? "es", subject, ct).ConfigureAwait(false);
        }

        var parameters = service["params"] as JsonObject ?? [];
        var activation = parameters["activation"]?.GetValue<string>();
        var lifecycle = parameters["lifecycle"]?.GetValue<string>();
        var missing = new JsonArray();
        if (activation is not ("on_payment" or "staff"))
        {
            missing.Add("params.activation");
        }

        if (lifecycle is not ("state_only" or "staff"))
        {
            missing.Add("params.lifecycle");
        }

        if (missing.Count > 0)
        {
            throw Fail(ErrorCodes.InvalidInput, "El producto no tiene activation y lifecycle válidos.", new JsonObject { ["fields"] = missing });
        }

        var target = op switch { "suspend" => "suspended", "terminate" => "terminated", _ => "active" };
        return op switch
        {
            "validate" => ModuleResponse.Completed(new JsonObject { ["valid"] = true }),
            "create" when activation == "on_payment" => ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active", ["external_ids"] = new JsonObject() }),
            "suspend" or "unsuspend" or "terminate" when lifecycle == "state_only" => ModuleResponse.Completed(new JsonObject { ["observed_state"] = target }),
            _ => await OpenTask(instanceId, "provisioning", op, request, key, subject, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Bloques para el portal: los campos que cargó el staff y el cliente puede ver, el estado y si hay algo en curso.</summary>
    private async Task<ModuleResponse> View(JsonObject service, string language, string subject, CancellationToken ct)
    {
        var declared = (service["params"]?["staff_fields"] as JsonArray ?? []).OfType<JsonObject>();
        var loaded = (await LastClosed(subject, "create", false, ct).ConfigureAwait(false))?["fields"] as JsonObject ?? [];
        var items = new JsonArray();
        foreach (var field in declared.Where(f => f["client_visible"]?.GetValue<bool>() == true && f["secret"]?.GetValue<bool>() != true))
        {
            var id = field["id"]!.GetValue<string>();
            if (loaded[id] is { } value)
            {
                var label = field["label"]?[language] ?? field["label"]?["es"] ?? id;
                items.Add(new JsonObject { ["label"] = label.DeepClone(), ["value"] = value.DeepClone() });
            }
        }

        var status = service["status"]?.GetValue<string>();
        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "fields", ["items"] = items },
            new JsonObject { ["type"] = "status", ["level"] = status == "active" ? "ok" : "warning", ["text"] = status },
        };
        if (await db.Set<StaffTask>().AnyAsync(t => t.Subject == subject && t.Status == StaffTaskStatus.Open, ct).ConfigureAwait(false))
        {
            blocks.Add(new JsonObject { ["type"] = "notice", ["text"] = language == "en" ? "Setup in progress." : "Configuración en curso." });
        }

        return ModuleResponse.Completed(new JsonObject { ["blocks"] = blocks });
    }

    private async Task<ModuleResponse> Registrar(long instanceId, string op, ModuleRequest request, string? key, CancellationToken ct)
    {
        var registry = request.Instance.Config["registry"]?.GetValue<string>() ?? "manual_registrar";
        var input = request.Input;
        var domain = request.Context["domain"]?.AsObject();
        var subject = string.Create(CultureInfo.InvariantCulture, $"domain:{domain?["id"]}");
        var nicAr = registry == "nic.ar";
        switch (op)
        {
            case "check":
                return ModuleResponse.Completed(new JsonObject { ["results"] = await Check(registry, input["names"] as JsonArray ?? [], ct).ConfigureAwait(false) });

            case "get_domain":
                // Lo último que cargó el staff. Sin vencimiento el estado es desconocido.
                var known = await LastClosed(subject, null, true, ct).ConfigureAwait(false);
                return ModuleResponse.Completed(new JsonObject
                {
                    ["source"] = "staff",
                    ["status"] = known is null ? "unknown" : "active",
                    ["expires_on"] = known?["expires_on"]?.DeepClone(),
                    ["nameservers"] = known?["nameservers"]?.DeepClone() ?? new JsonArray(),
                });

            case "register" or "renew":
                var years = input["years"]?.GetValue<int>() ?? 1;
                if (nicAr && years != 1)
                {
                    throw Fail(ErrorCodes.InvalidInput, "NIC Argentina registra y renueva por un año.", new JsonObject { ["fields"] = new JsonArray("years") });
                }

                if (op == "renew" && nicAr)
                {
                    RenewalWindow(input, request.Instance.Config);
                }

                break;

            case "set_nameservers":
                if ((input["nameservers"] as JsonArray ?? []).Count < 2)
                {
                    throw Fail(ErrorCodes.InvalidInput, "La delegación pide al menos dos nameservers.", new JsonObject { ["fields"] = new JsonArray("nameservers") });
                }

                break;

            default:
                // transfer, get_auth_code, set_lock, set_contacts y get_prices piden una API que el registro no tiene.
                throw Fail(ErrorCodes.NotSupported, $"El registrar manual no hace {op}.");
        }

        if (domain is null)
        {
            throw Fail(ErrorCodes.InvalidInput, "Falta context.domain.");
        }

        return await OpenTask(instanceId, "registrar", op, request, key, subject, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// NIC Argentina renueva desde 30 días antes del vencimiento hasta los días de gracia después (45 por defecto,
    /// configurable porque hubo prórrogas). Antes de la ventana la tarea espera, y pasada la gracia el dominio se liberó.
    /// </summary>
    private void RenewalWindow(JsonObject input, JsonObject config)
    {
        if (input["current_expiry"]?.GetValue<string>() is not { } text
            || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
        {
            throw Fail(ErrorCodes.InvalidInput, "Falta current_expiry.", new JsonObject { ["fields"] = new JsonArray("current_expiry") });
        }

        var today = options.Value.Today(time);
        var opens = expiry.AddDays(-30);
        if (today < opens)
        {
            var wait = (opens.DayNumber - today.DayNumber) * 86400;
            throw Fail(ErrorCodes.Conflict, $"La renovación se puede pedir desde el {opens:yyyy-MM-dd}.", retryAfter: wait);
        }

        var grace = config["grace_days"]?.GetValue<int>() ?? 45;
        if (today > expiry.AddDays(grace))
        {
            throw Fail(ErrorCodes.Rejected, "Pasó la gracia de la renovación: el dominio se liberó.", new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "released" } });
        }
    }

    /// <summary>Sin API no se sabe si un nombre está libre, salvo en las zonas propias, donde manda lo que hay en el núcleo.</summary>
    private async Task<JsonArray> Check(string registry, JsonArray names, CancellationToken ct)
    {
        var results = new JsonArray();
        foreach (var name in names.Select(n => n!.GetValue<string>().Trim().ToLowerInvariant()))
        {
            if (registry == "own_dns")
            {
                var taken = await db.Set<RegisteredDomain>().AnyAsync(
                    d => d.Name == name && (d.Status == DomainStatus.Pending || d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace), ct).ConfigureAwait(false);
                results.Add(new JsonObject { ["name"] = name, ["available"] = !taken });
            }
            else
            {
                results.Add(new JsonObject { ["name"] = name, ["available"] = null, ["reason"] = "requires_staff" });
            }
        }

        return results;
    }

    /// <summary>Abre la tarea o devuelve la que ya existe con esa clave (sección 6 del protocolo).</summary>
    private async Task<ModuleResponse> OpenTask(long instanceId, string kind, string op, ModuleRequest request, string? key, string subject, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw Fail(ErrorCodes.InvalidInput, "Falta Idempotency-Key.");
        }

        var input = request.Input.ToJsonString();
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        var details = request.Context.DeepClone().AsObject();
        if (details["service"] is JsonObject s)
        {
            s.Remove("external_ids");
        }

        var now = time.GetUtcNow();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO platform.staff_tasks (module_instance_id, kind, operation, idempotency_key, fingerprint, subject, details, input, status, created_at)
                VALUES ({instanceId}, {kind}, {op}, {key}, {fingerprint}, {subject}, {details.ToJsonString()}::jsonb, {input}::jsonb, 'Open', {now})
                ON CONFLICT (module_instance_id, idempotency_key) DO NOTHING
                """, ct).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw Fail(ErrorCodes.Conflict, $"Ya hay una tarea abierta para {subject}.", retryAfter: 60);
        }

        var task = await db.Set<StaffTask>().AsNoTracking()
            .SingleAsync(t => t.ModuleInstanceId == instanceId && t.IdempotencyKey == key, ct).ConfigureAwait(false);
        if (task.Kind != kind || task.Operation != op || task.Fingerprint != fingerprint)
        {
            throw Fail(ErrorCodes.IdempotencyConflict, "La clave ya se usó con otra entrada.");
        }

        return Answer(task);
    }

    private async Task<ModuleResponse> Poll(long instanceId, string operationId, CancellationToken ct)
    {
        var task = operationId.StartsWith("task-", StringComparison.Ordinal) && long.TryParse(operationId[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? await db.Set<StaffTask>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.ModuleInstanceId == instanceId, ct).ConfigureAwait(false)
            : null;
        return task is null ? throw Fail(ErrorCodes.NotFound, $"No existe la operación {operationId}.") : Answer(task);
    }

    private static ModuleResponse Answer(StaffTask task) => task.Status switch
    {
        StaffTaskStatus.Open => ModuleResponse.Pending(string.Create(CultureInfo.InvariantCulture, $"task-{task.Id}"), PollSeconds),
        StaffTaskStatus.Completed => ModuleResponse.Completed(JsonNode.Parse(task.Result ?? "{}")),
        _ => throw Fail(ErrorCodes.Rejected, $"El staff rechazó la tarea {task.Id}.", new JsonObject { ["upstream"] = new JsonObject { ["reason"] = task.Reason } }),
    };

    private async Task<JsonObject?> LastClosed(string subject, string? operation, bool withExpiry, CancellationToken ct)
    {
        var results = await db.Set<StaffTask>().AsNoTracking()
            .Where(t => t.Subject == subject && t.Status == StaffTaskStatus.Completed && (operation == null || t.Operation == operation))
            .OrderByDescending(t => t.ClosedAt).ThenByDescending(t => t.Id)
            .Select(t => t.Result)
            .ToListAsync(ct).ConfigureAwait(false);
        return results.Select(r => JsonNode.Parse(r ?? "{}") as JsonObject).FirstOrDefault(r => !withExpiry || r?["expires_on"] is not null);
    }

    private static ManualError Fail(string code, string message, JsonObject? details = null, int? retryAfter = null) =>
        new(new ModuleError(code, message, ErrorCodes.IsRetryable(code), retryAfter, details));

    private sealed class ManualError(ModuleError error) : Exception(error.Message)
    {
        public ModuleError Error { get; } = error;
    }
}

/// <summary>Lo que carga el staff al cerrar una tarea. Los secretos se guardan cifrados como credenciales del servicio.</summary>
public sealed record StaffTaskClose(
    string? StaffRef = null,
    IReadOnlyDictionary<string, string>? Fields = null,
    IReadOnlyDictionary<string, string>? Secrets = null,
    DateOnly? ExpiresOn = null,
    IReadOnlyList<string>? Nameservers = null);

/// <summary>
/// Cierre de tareas del staff desde el admin. Al cerrarla, el trabajo que espera se reprograma para ya. Cerrar una
/// revisión de baja la libera: completarla deja que la mora siga con sus reglas en el acto, y rechazarla deja sin
/// efecto la baja pedida.
/// </summary>
public sealed class StaffTaskService(BillingDbContext db, TimeProvider time, JobQueue jobs, SecretBox? box = null)
{
    /// <summary>
    /// Error definitivo de un módulo (sección 10 del protocolo): queda como tarea de revisión del servicio, con el
    /// código, el mensaje y los detalles para el staff, nunca las credenciales. Un servicio tiene a lo sumo una
    /// abierta: una falla nueva la actualiza. Se cierra sola cuando una operación del servicio termina bien, o la
    /// cierra el staff. Se guarda en el acto, fuera de la transacción del trabajo que va a morir. <paramref name="dunning"/>
    /// dice si la operación la pidió la mora, y queda aunque la fecha de baja la vuelva a pedir después: una baja por
    /// mora trabada se levanta con el pago (<see cref="LiftBlockedTermination"/>).
    /// </summary>
    public async Task OpenReview(long instanceId, string operation, string key, Domain.Services.Service service, JsonObject input, ModuleError error, bool dunning, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(error);
        var shown = input.DeepClone().AsObject();
        shown.Remove("credentials");
        var text = shown.ToJsonString();
        var details = new JsonObject
        {
            ["code"] = error.Code,
            ["message"] = error.Message,
            ["details"] = error.Details?.DeepClone(),
            ["idempotency_key"] = key,
            ["dunning"] = dunning,
            ["service"] = new JsonObject
            {
                ["id"] = service.Id,
                ["label"] = service.Label,
                ["status"] = service.Status.ToString().ToLowerInvariant(),
                ["external_ids"] = JsonNode.Parse(service.ExternalIds),
            },
        };
        var subject = string.Create(CultureInfo.InvariantCulture, $"service:{service.Id}");
        // La clave de la tarea no es la del módulo: una revisión cerrada y otra falla con la misma clave son dos tareas.
        var taskKey = string.Create(CultureInfo.InvariantCulture, $"{key}/{Guid.NewGuid():N}");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.staff_tasks (module_instance_id, kind, operation, idempotency_key, fingerprint, subject, details, input, status, created_at)
            VALUES ({instanceId}, {StaffTask.Review}, {operation}, {taskKey}, {fingerprint}, {subject}, {details.ToJsonString()}::jsonb, {text}::jsonb, 'Open', {now})
            ON CONFLICT (subject) WHERE status = 'Open' DO UPDATE SET module_instance_id = EXCLUDED.module_instance_id,
              operation = EXCLUDED.operation, idempotency_key = EXCLUDED.idempotency_key, fingerprint = EXCLUDED.fingerprint,
              details = EXCLUDED.details || jsonb_build_object('dunning', (platform.staff_tasks.details ->> 'dunning')::boolean OR (EXCLUDED.details ->> 'dunning')::boolean),
              input = EXCLUDED.input, created_at = EXCLUDED.created_at
              WHERE platform.staff_tasks.kind = {StaffTask.Review}
            """, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cierra como resueltas las revisiones abiertas de un servicio o dominio: una operación suya terminó bien. Una
    /// acción del módulo cierra solo las que dejó otra acción, y una consulta de estado que vuelve a coincidir solo la
    /// que dejó la reconciliación: ninguna de las dos dice nada de una baja o una suspensión trabada.
    /// </summary>
    public async Task ResolveReviews(string subject, string operation, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var onlySame = operation is "run_action" or "get_status";
        var result = new JsonObject { ["resolved"] = "operation_completed" }.ToJsonString();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.staff_tasks SET status = 'Completed', closed_at = {now}, result = {result}::jsonb
            WHERE subject = {subject} AND kind = {StaffTask.Review} AND status = 'Open' AND (NOT {onlySame} OR operation = {operation})
            """, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// La baja por mora que el módulo no pudo hacer se levanta si el cliente paga: cierra esa revisión y dice si había
    /// una. Una baja pedida por el staff o a fin de período no se levanta con un pago.
    /// </summary>
    public async Task<bool> LiftBlockedTermination(string subject, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var result = new JsonObject { ["resolved"] = "paid" }.ToJsonString();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.staff_tasks SET status = 'Completed', closed_at = {now}, result = {result}::jsonb
            WHERE subject = {subject} AND kind = {StaffTask.Review} AND operation = 'terminate' AND status = 'Open' AND (details ->> 'dunning') = 'true'
            """, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task Complete(long taskId, StaffTaskClose close, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(close);
        await Close(taskId, async task =>
        {
            var result = new JsonObject();
            if (task.Kind == StaffTask.Review)
            {
                // La revisión de un error del módulo no tiene nada que informar: el staff la cierra cuando la resolvió. La
                // de una baja retenida la libera: la mora corre ahora, sin esperar al día siguiente, y si el cliente
                // regularizó la deuda no hace nada.
                if (StaffTask.ReviewedService(task.Subject) is not null)
                {
                    var at = time.GetUtcNow();
                    await jobs.Enqueue(JobKinds.Dunning, new { date = at }, Scheduler.BillingEntity, string.Create(CultureInfo.InvariantCulture, $"{JobKinds.Dunning}:task:{task.Id}"), ct: ct).ConfigureAwait(false);
                }
            }
            else if (task.Kind == "provisioning")
            {
                result["observed_state"] = task.Operation switch { "suspend" => "suspended", "terminate" => "terminated", _ => "active" };
                result["external_ids"] = close.StaffRef is null ? new JsonObject() : new JsonObject { ["staff_ref"] = close.StaffRef };
                result["fields"] = await Fields(task, close, ct).ConfigureAwait(false);
            }
            else
            {
                if (task.Operation is "register" or "renew" && close.ExpiresOn is null)
                {
                    throw new ValidationException("Falta el vencimiento que informó el registro.");
                }

                if (task.Operation == "set_nameservers" && close.Nameservers is not { Count: >= 2 })
                {
                    throw new ValidationException("Faltan los nameservers que quedaron delegados.");
                }

                result["expires_on"] = close.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                result["nameservers"] = new JsonArray([.. (close.Nameservers ?? []).Select(n => (JsonNode)n)]);
                result["external_ids"] = new JsonObject();
            }

            task.Status = StaffTaskStatus.Completed;
            task.Result = result.ToJsonString();
        }, ct).ConfigureAwait(false);
    }

    public Task Reject(long taskId, string reason, CancellationToken ct = default) =>
        Close(taskId, async task =>
        {
            task.Status = StaffTaskStatus.Rejected;
            task.Reason = string.IsNullOrWhiteSpace(reason) ? throw new ValidationException("Falta el motivo del rechazo.") : reason.Trim();
            if (task.Kind == StaffTask.Review && StaffTask.ReviewedService(task.Subject) is { } serviceId)
            {
                // Rechazar la revisión deja sin efecto la baja pedida. Una baja por mora no se rechaza: se regulariza la
                // deuda y se completa la tarea, así la mora ya no la hace.
                var service = await db.Services.SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
                service.CancelAt = service.CancelAt is null
                    ? throw new ValidationException("La baja de este servicio es por mora: para conservarlo, registrar el pago y completar la tarea.")
                    : null;
            }
        }, ct);

    private async Task Close(long taskId, Func<StaffTask, Task> apply, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "platform.staff_tasks", [taskId], ct).ConfigureAwait(false);
        var task = await db.Set<StaffTask>().SingleOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la tarea {taskId}.");
        if (task.Status != StaffTaskStatus.Open)
        {
            throw new InvalidOperationException($"La tarea {taskId} ya está cerrada.");
        }

        await apply(task).ConfigureAwait(false);
        var now = time.GetUtcNow();
        task.ClosedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE platform.jobs SET run_at = {now} WHERE entity_key = {task.Subject} AND status = 'Pending' AND run_at > {now}", ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Solo se aceptan los campos que declara el producto, cada uno en su lugar: los secretos nunca quedan en la tarea.</summary>
    private async Task<JsonObject> Fields(StaffTask task, StaffTaskClose close, CancellationToken ct)
    {
        var declared = (JsonNode.Parse(task.Details)?["service"]?["params"]?["staff_fields"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(f => f["id"]!.GetValue<string>(), f => f["secret"]?.GetValue<bool>() == true);
        var fields = new JsonObject();
        foreach (var (name, value) in close.Fields ?? new Dictionary<string, string>())
        {
            fields[name] = declared.TryGetValue(name, out var secret) && !secret ? value : throw new ValidationException($"El producto no declara el campo {name}.");
        }

        var serviceId = long.Parse(task.Subject["service:".Length..], CultureInfo.InvariantCulture);
        foreach (var (name, value) in close.Secrets ?? new Dictionary<string, string>())
        {
            if (!declared.GetValueOrDefault(name))
            {
                throw new ValidationException($"El producto no declara el secreto {name}.");
            }

            await ProtocolServiceModules.StoreSecret(db, box ?? throw new ValidationException("No hay llave para guardar secretos."), serviceId, name, value, ct).ConfigureAwait(false);
        }

        return fields;
    }
}
