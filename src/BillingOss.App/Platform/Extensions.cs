using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BillingOss.App.Platform;

/// <summary>
/// Une una instancia de un módulo del tipo extension con un evento público o con la validación de pedidos (sección 4
/// de diseno/webhooks-y-extensiones.md).
/// </summary>
public sealed class ExtensionHook
{
    public long Id { get; private set; }

    public long InstanceId { get; set; }

    /// <summary>Tipo público del catálogo o <see cref="PublicEvents.ValidateOrder"/>.</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>Solo en la validación de pedidos: si la extensión no responde, el pedido se rechaza.</summary>
    public bool Required { get; set; } = true;

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ExtensionHookConfiguration : IEntityTypeConfiguration<ExtensionHook>
{
    public void Configure(EntityTypeBuilder<ExtensionHook> b)
    {
        b.ToTable("extension_hooks", "platform");
        b.Property(x => x.Event).HasMaxLength(64);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.InstanceId);
        b.HasIndex(x => new { x.InstanceId, x.Event }).IsUnique();
    }
}

/// <summary>Las capacidades del tipo extension (sección 9.5 del protocolo).</summary>
public static class ExtensionCapabilities
{
    public const string Kind = "extension";
    public const string Events = "extension.events";
    public const string ValidateOrder = "extension.validate_order";

    public static bool IsExtension(JsonObject manifest) => Has(manifest, "kinds", Kind);

    public static bool Declares(JsonObject manifest, string capability) => Has(manifest, "capabilities", capability);

    private static bool Has(JsonObject manifest, string list, string value) =>
        manifest?[list] is JsonArray items && items.Any(i => i is JsonValue v && v.TryGetValue<string>(out var s) && s == value);
}

/// <summary>
/// Manda un evento a una extensión (sección 4.3 del diseño). La clave de idempotencia es la del gancho y el evento: un
/// reintento nunca repite el efecto. Un gancho que se apagó o se sacó después de publicar ya no recibe nada.
/// </summary>
public sealed class ExtensionEventHandler(BillingDbContext db, ModuleRegistry registry, ModuleClient client) : IJobHandler
{
    public string Kind => JobKinds.ExtensionEvent;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = JsonNode.Parse(job.Payload)!.AsObject();
        var hookId = payload["hook_id"]!.GetValue<long>();
        var hook = await db.Set<ExtensionHook>().AsNoTracking().SingleOrDefaultAsync(h => h.Id == hookId, ct).ConfigureAwait(false);
        if (hook is not { Active: true })
        {
            return JobResult.Done;
        }

        var module = await registry.Resolve(hook.InstanceId, ct).ConfigureAwait(false);
        var eventId = payload["event_id"]!.GetValue<string>();
        var input = new JsonObject
        {
            ["id"] = eventId,
            ["type"] = payload["type"]!.DeepClone(),
            ["occurred_at"] = payload["occurred_at"]!.DeepClone(),
            ["data"] = payload["data"]?.DeepClone() ?? new JsonObject(),
        };
        var key = string.Create(CultureInfo.InvariantCulture, $"hook-{hookId}-{eventId}");
        var result = await client.Call(module.Endpoint, "/v0/extension/on_event", new ModuleRequest(module.Data, [], input), key, module.Timeout("on_event"), ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw ErrorCodes.IsRetryable(error.Code)
                ? new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
                : new PermanentFailureException($"{error.Code}: {error.Message}");
        }

        if (result.Response is not { Status: ModuleResponse.CompletedStatus } response)
        {
            throw new PermanentFailureException("La extensión no respondió completed: un evento no admite operaciones largas.");
        }

        if (response.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }
}

/// <summary>
/// Validación de pedidos por las extensiones (sección 4.4 del diseño). La llama PlaceOrder antes de abrir su
/// transacción: ninguna llamada a un módulo ocurre con una transacción abierta. Sin ganchos cuesta una consulta.
/// </summary>
public sealed class OrderValidation(BillingDbContext db, IServiceProvider services, AuditLog audit, IOptions<BillingOptions> options, ILogger<OrderValidation> logger)
{
    /// <summary>Lo que ve el cliente si una extensión obligatoria no responde: no es culpa suya.</summary>
    public const string Unavailable = "No pudimos validar el pedido en este momento. Probá de nuevo en unos minutos.";

    public const string Rejected = "El pedido no se puede confirmar.";

    public async Task Check(long accountId, IReadOnlyList<OrderItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var hooks = await db.Set<ExtensionHook>().AsNoTracking()
            .Where(h => h.Active && h.Event == PublicEvents.ValidateOrder).OrderBy(h => h.Id).ToListAsync(ct).ConfigureAwait(false);
        if (hooks.Count == 0 || await Request(accountId, items, ct).ConfigureAwait(false) is not { } request)
        {
            return;
        }

        var (context, input) = request;
        foreach (var hook in hooks)
        {
            var (allowed, reason, failure) = await Ask(hook, context, input, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                logger.LogWarning("La extensión de la instancia {Instance} no validó el pedido: {Error}", hook.InstanceId, failure);
                await audit.TryRecord("extensions.validate_failed", Entity(accountId), new { hook_id = hook.Id, instance_id = hook.InstanceId, required = hook.Required, error = failure }).ConfigureAwait(false);
                if (hook.Required)
                {
                    throw new ValidationException(Unavailable);
                }

                continue;
            }

            if (!allowed)
            {
                await audit.TryRecord("extensions.order_rejected", Entity(accountId), new { hook_id = hook.Id, instance_id = hook.InstanceId }).ConfigureAwait(false);
                throw new ValidationException(reason is null ? Rejected : $"{Rejected[..^1]}: {reason}");
            }
        }
    }

    private async Task<(bool Allowed, string? Reason, string? Failure)> Ask(ExtensionHook hook, JsonObject context, JsonObject input, CancellationToken ct)
    {
        if (services.GetService<ModuleRegistry>() is not { } registry || services.GetService<ModuleClient>() is not { } client)
        {
            return (false, null, "Este nodo no tiene las llaves de los módulos (Secrets:KeyFile).");
        }

        ModuleCallResult result;
        ResolvedInstance module;
        try
        {
            module = await registry.Resolve(hook.InstanceId, ct).ConfigureAwait(false);
            var seconds = module.Manifest["timeouts"]?["validate_order"] is JsonValue t && t.TryGetValue<int>(out var s) ? s : 5;
            var request = new ModuleRequest(module.Data, (JsonObject)context.DeepClone(), (JsonObject)input.DeepClone());
            result = await client.Call(module.Endpoint, "/v0/extension/validate_order", request, null, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 10)), ct).ConfigureAwait(false);
        }
        catch (PermanentFailureException ex)
        {
            return (false, null, ex.Message);
        }

        if (result.Error is { } error)
        {
            return (false, null, $"{error.Code}: {error.Message}");
        }

        if (result.Response is not { Status: ModuleResponse.CompletedStatus } response
            || response.Result?["allowed"] is not JsonValue value || !value.TryGetValue<bool>(out var allowed))
        {
            return (false, null, "Respuesta sin allowed.");
        }

        if (response.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        return (allowed, Clean(response.Result?["reason"]), null);
    }

    /// <summary>Contexto y entrada de la sección 9.5 del protocolo: lo mínimo, sin contacto ni pagos.</summary>
    private async Task<(JsonObject Context, JsonObject Input)?> Request(long accountId, IReadOnlyList<OrderItem> items, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.Id, a.Language }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (account is null)
        {
            // PlaceOrder lo rechaza con su propio error.
            return null;
        }

        var country = await db.TaxIdentities.AsNoTracking().Where(t => t.AccountId == accountId && t.ValidTo == null).Select(t => t.Country).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var owned = await db.Services.AsNoTracking().Where(s => s.AccountId == accountId).OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.ProductId, s.Status }).ToListAsync(ct).ConfigureAwait(false);
        var planIds = items.Select(i => i.PlanId).Distinct().ToList();
        var plans = await db.Plans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);

        var context = new JsonObject
        {
            ["account"] = new JsonObject
            {
                ["id"] = account.Id,
                ["language"] = account.Language,
                ["country"] = country ?? options.Value.DefaultCountry,
                ["services"] = new JsonArray([.. owned.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id,
                    ["product_id"] = s.ProductId,
                    ["status"] = s.Status.ToString().ToLowerInvariant(),
                })]),
            },
        };
        var input = new JsonObject
        {
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject
            {
                ["product_id"] = plans.TryGetValue(i.PlanId, out var plan) ? plan.ProductId : null,
                ["plan_id"] = i.PlanId,
                ["cycle"] = plan is null ? null : new JsonObject { ["unit"] = plan.CycleUnit.ToString().ToLowerInvariant(), ["count"] = plan.CycleCount },
                ["quantity"] = i.Quantity,
                // Lo elegido de cada opción configurable, como lo recibe el pedido de la API propia.
                ["options"] = new JsonArray([.. (i.Options ?? []).Select(o => (JsonNode)new JsonObject { ["option_id"] = o.OptionId, ["value_id"] = o.ValueId, ["quantity"] = o.Quantity })]),
                ["order"] = Parse(i.OrderData),
            })]),
        };
        return (context, input);
    }

    private static JsonNode Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>El motivo para el cliente: texto plano de una línea, hasta 300 caracteres.</summary>
    private static string? Clean(JsonNode? reason)
    {
        if (reason is not JsonValue v || !v.TryGetValue<string>(out var text))
        {
            return null;
        }

        var plain = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return plain.Length == 0 ? null : plain.Length > 300 ? plain[..300] : plain;
    }

    private static string Entity(long accountId) => string.Create(CultureInfo.InvariantCulture, $"account:{accountId}");
}

public sealed record ExtensionInput(string Name, string Url, string SigningSecret, string Config = "{}");

/// <summary>
/// Alta, ganchos y baja de extensiones desde el panel (sección 4.5 del diseño). El manifiesto se lee firmado y tiene que
/// declarar el tipo extension. Los campos x-secret de la configuración se cifran (D-09) y nunca se vuelven a mostrar.
/// </summary>
public sealed class ExtensionService(BillingDbContext db, ModuleRegistry registry, ModuleClient client, AuditLog audit, TimeProvider time)
{
    public static TimeSpan ManifestTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Instala la extensión con una instancia y devuelve el ID de la instancia. Sin ganchos: se eligen después.</summary>
    public async Task<long> Install(ExtensionInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("El nombre va de 1 a 100 caracteres.");
        }

        var url = ModuleEndpoint.TryParseBaseUrl(input.Url?.Trim() ?? string.Empty)
            ?? throw new ValidationException(ModuleEndpoint.BaseUrlRule);
        if (string.IsNullOrWhiteSpace(input.SigningSecret) || input.SigningSecret.Length < 16)
        {
            throw new ValidationException("El secreto de firma tiene al menos 16 caracteres.");
        }

        JsonObject config;
        try
        {
            config = JsonNode.Parse(string.IsNullOrWhiteSpace(input.Config) ? "{}" : input.Config) as JsonObject
                ?? throw new ValidationException("La configuración es un objeto JSON.");
        }
        catch (JsonException)
        {
            throw new ValidationException("La configuración no es JSON válido.");
        }

        var manifest = await FetchManifest(url, input.SigningSecret, ct).ConfigureAwait(false);
        if (!ExtensionCapabilities.IsExtension(manifest))
        {
            throw new ValidationException("El módulo no es una extensión: su manifiesto no declara el tipo extension.");
        }

        var (visible, secrets) = Split(config, manifest);
        long instanceId;
        try
        {
            var installation = await registry.Install(name, url.AbsoluteUri, input.SigningSecret, manifest, ct: ct).ConfigureAwait(false);
            instanceId = (await registry.AddInstance(installation.Id, name, visible.ToJsonString(), secrets?.ToJsonString(), ct).ConfigureAwait(false)).Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException($"Ya hay un módulo instalado con el nombre {name}.");
        }

        // La prueba de configuración del protocolo (sección 9), ya sin transacción abierta: una extensión mal configurada
        // no queda instalada, porque con la validación obligatoria rechazaría todos los pedidos.
        if (await Test(instanceId, ct).ConfigureAwait(false) is { } problem)
        {
            await Discard(instanceId, ct).ConfigureAwait(false);
            throw new ValidationException($"Falló la prueba de configuración de la extensión: {problem}");
        }

        await audit.TryRecord("extensions.install", Entity(instanceId), new { name, url = url.AbsoluteUri, module = manifest["id"]?.ToString() }).ConfigureAwait(false);
        return instanceId;
    }

    /// <summary>/v0/instance/test de la instancia. Devuelve el problema, o null si la configuración sirve.</summary>
    private async Task<string?> Test(long instanceId, CancellationToken ct)
    {
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var result = await client.Call(module.Endpoint, "/v0/instance/test", new ModuleRequest(module.Data, [], []), null, ManifestTimeout, ct).ConfigureAwait(false);
        return result switch
        {
            { Error: { } error } => $"{error.Code}: {error.Message}",
            { Response.Status: ModuleResponse.CompletedStatus } => null,
            _ => "el módulo no respondió completed.",
        };
    }

    /// <summary>Saca una instalación que no llegó a quedar: sin ganchos ni auditoría, porque nunca se usó.</summary>
    private async Task Discard(long instanceId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var installationId = await db.ModuleInstances.Where(i => i.Id == instanceId).Select(i => i.InstallationId).SingleAsync(ct).ConfigureAwait(false);
        await db.ModuleInstances.Where(i => i.Id == instanceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ModuleInstallations.Where(i => i.Id == installationId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Reemplaza los ganchos de la instancia. Cada uno exige la capacidad que corresponde en el manifiesto: el núcleo no
    /// le pide a un módulo lo que no declara.
    /// </summary>
    public async Task SetHooks(long instanceId, IReadOnlyCollection<string> events, bool validateOrders, bool required, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var manifest = await Manifest(instanceId, ct).ConfigureAwait(false);
        var types = events.Select(e => e.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (types.FirstOrDefault(t => !PublicEvents.IsType(t)) is { } unknown)
        {
            throw new ValidationException($"Evento desconocido: {unknown}.");
        }

        if (types.Count > 0 && !ExtensionCapabilities.Declares(manifest, ExtensionCapabilities.Events))
        {
            throw new ValidationException($"La extensión no declara {ExtensionCapabilities.Events}: no recibe eventos.");
        }

        if (validateOrders && !ExtensionCapabilities.Declares(manifest, ExtensionCapabilities.ValidateOrder))
        {
            throw new ValidationException($"La extensión no declara {ExtensionCapabilities.ValidateOrder}: no valida pedidos.");
        }

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Set<ExtensionHook>().Where(h => h.InstanceId == instanceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.AddRange(types.Select(t => new ExtensionHook { InstanceId = instanceId, Event = t, Required = false, CreatedAt = now }));
        if (validateOrders)
        {
            db.Add(new ExtensionHook { InstanceId = instanceId, Event = PublicEvents.ValidateOrder, Required = required, CreatedAt = now });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("extensions.hooks", Entity(instanceId), new { events = types, validate_orders = validateOrders, required }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Saca la extensión: sus ganchos, su instancia y, si no le quedan otras, su instalación.</summary>
    public async Task Remove(long instanceId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var instance = await db.ModuleInstances.SingleOrDefaultAsync(i => i.Id == instanceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la extensión {instanceId}.");
        await Manifest(instanceId, ct).ConfigureAwait(false);
        await db.Set<ExtensionHook>().Where(h => h.InstanceId == instanceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.ModuleInstances.Remove(instance);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!await db.ModuleInstances.AnyAsync(i => i.InstallationId == instance.InstallationId, ct).ConfigureAwait(false))
        {
            await db.ModuleInstallations.Where(i => i.Id == instance.InstallationId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        await audit.Record("extensions.remove", Entity(instanceId), new { name = instance.Name }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>El manifiesto aceptado de la instancia, que tiene que ser de una extensión.</summary>
    public async Task<JsonObject> Manifest(long instanceId, CancellationToken ct = default)
    {
        var manifest = await (from i in db.ModuleInstances
                              join m in db.ModuleInstallations on i.InstallationId equals m.Id
                              where i.Id == instanceId
                              select m.Manifest).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return manifest is not null && JsonNode.Parse(manifest) is JsonObject parsed && ExtensionCapabilities.IsExtension(parsed)
            ? parsed
            : throw new KeyNotFoundException($"No existe la extensión {instanceId}.");
    }

    /// <summary>Separa los campos x-secret del config_schema, que van cifrados, del resto.</summary>
    internal static (JsonObject Visible, JsonObject? Secrets) Split(JsonObject config, JsonObject manifest)
    {
        var properties = manifest["config_schema"]?["properties"] as JsonObject;
        var visible = new JsonObject();
        var secrets = new JsonObject();
        foreach (var (name, value) in config)
        {
            var secret = properties?[name]?["x-secret"] is JsonValue flag && flag.TryGetValue<bool>(out var isSecret) && isSecret;
            (secret ? secrets : visible)[name] = value?.DeepClone();
        }

        return (visible, secrets.Count > 0 ? secrets : null);
    }

    /// <summary>GET /v0/manifest firmado (sección 4 del protocolo), sin seguir redirecciones.</summary>
    private static async Task<JsonObject> FetchManifest(Uri baseUrl, string signingSecret, CancellationToken ct)
    {
        const string Path = "/v0/manifest";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = ManifestTimeout, MaxResponseContentBufferSize = ModuleClient.MaxResponseBytes };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, Path.TrimStart('/')));
        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Encoding.UTF8.GetBytes(signingSecret), timestamp, "GET", Path, requestId, null, []));
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ValidationException("El módulo rechazó la firma: revisá el secreto.");
            }

            return response.IsSuccessStatusCode && JsonNode.Parse(text) is JsonObject manifest
                ? manifest
                : throw new ValidationException(string.Create(CultureInfo.InvariantCulture, $"El módulo no devolvió su manifiesto (HTTP {(int)response.StatusCode})."));
        }
        catch (HttpRequestException)
        {
            throw new ValidationException("No se pudo conectar con el módulo.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ValidationException("El módulo no respondió en 15 segundos.");
        }
        catch (JsonException)
        {
            throw new ValidationException("El manifiesto no es JSON válido.");
        }
    }

    private static string Entity(long instanceId) => string.Create(CultureInfo.InvariantCulture, $"extension:{instanceId}");
}
