using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

public sealed record ConnectionTest(bool Ok, string Message, bool? Livemode, IReadOnlyList<string> Warnings);

public sealed record GatewayActivation(bool Enabled, string DisplayName, int Position, IReadOnlyList<string> Currencies, bool SameCurrencyCancel, bool AutoCharge);

/// <summary>
/// Asistente de cobros del panel (sección 4.3 del diseño de cobros): una instancia por ambiente, credenciales desde el
/// config_schema del módulo, URL y eventos de avisos, prueba de conexión, pago de prueba y activación.
/// </summary>
public sealed class GatewaySetupService(BillingDbContext db, ModuleRegistry registry, GatewayPayments gateways, BillingCore core, AuditLog audit, IOptions<PaymentsOptions> options, TimeProvider time)
{
    public const string Test = "test";
    public const string Live = "live";

    /// <summary>
    /// Monedas que declara el manifiesto. El campo es opcional en el protocolo: sin él vale cualquiera, como con "*", y la
    /// pasarela rechaza al cobrar la que no acepta.
    /// </summary>
    public static IReadOnlyList<string> DeclaredCurrencies(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest["currencies"] is JsonArray declared ? [.. declared.Select(c => c?.GetValue<string>()).OfType<string>()] : ["*"];
    }

    /// <summary>La URL de avisos de la instancia, para copiarla en el panel de la pasarela.</summary>
    public string WebhookUrl(long instanceId) =>
        string.Create(CultureInfo.InvariantCulture, $"{core.Options.PublicUrl?.TrimEnd('/')}/webhooks/{instanceId}");

    /// <summary>
    /// Crea la instancia de un ambiente. Prueba y producción son instancias distintas, como en ARCA, y el ambiente no
    /// cambia después. Los hosts del navegador salen del manifiesto, así el staff no los escribe.
    /// </summary>
    public async Task<long> CreateInstance(long installationId, string environment, CancellationToken ct = default)
    {
        if (environment is not (Test or Live))
        {
            throw new ValidationException("El ambiente es test o live.");
        }

        var installation = await db.ModuleInstallations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == installationId, ct).ConfigureAwait(false);
        var manifest = installation is null ? null : JsonNode.Parse(installation.Manifest) as JsonObject;
        if (manifest?["kinds"] is not JsonArray kinds || !kinds.Any(k => k?.GetValue<string>() == "payment"))
        {
            throw new KeyNotFoundException($"No existe el módulo de cobros {installationId}.");
        }

        var name = manifest["name"]?.GetValue<string>() ?? installation!.Name;
        var instance = await registry.AddInstance(installationId, string.Create(CultureInfo.InvariantCulture, $"{name} ({environment})"), ct: ct).ConfigureAwait(false);
        var tracked = await db.ModuleInstances.SingleAsync(i => i.Id == instance.Id, ct).ConfigureAwait(false);
        tracked.BrowserHosts = (manifest["browser_hosts"]?[environment] as JsonArray ?? []).ToJsonString();
        db.GatewaySetups.Add(new GatewaySetup(instance.Id, name, time.GetUtcNow()) { Environment = environment });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.gateway_created", Entity(instance.Id), new { installation = installationId, environment }, ct).ConfigureAwait(false);
        return instance.Id;
    }

    /// <summary>
    /// Guarda las credenciales según el config_schema del módulo: los x-secret cifrados y nunca devueltos. Un secreto
    /// vacío conserva el guardado. Lo que el esquema no declara no se guarda.
    /// </summary>
    public async Task SaveCredentials(long instanceId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var schema = module.Manifest["config_schema"] as JsonObject ?? [];
        var properties = schema["properties"] as JsonObject ?? [];
        var required = (schema["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var config = new JsonObject();
        var secrets = new JsonObject();
        var errors = new List<string>();
        foreach (var (name, property) in properties)
        {
            var secret = property?["x-secret"]?.GetValue<bool>() == true;
            var text = values.TryGetValue(name, out var v) ? v?.Trim() : null;
            if (string.IsNullOrEmpty(text))
            {
                // Un secreto no se vuelve a mostrar: vacío es "dejar el que está".
                var kept = secret ? module.Data.Config[name]?.DeepClone() : null;
                if (kept is not null)
                {
                    secrets[name] = kept;
                }
                else if (required.Contains(name))
                {
                    errors.Add(name);
                }

                continue;
            }

            JsonNode? value = property?["type"]?.GetValue<string>() switch
            {
                "boolean" => text is "true" or "on" ? true : text is "false" or "off" ? false : null,
                "integer" => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null,
                _ => text,
            };
            if (value is null || (property?["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, value))))
            {
                errors.Add(name);
                continue;
            }

            (secret ? secrets : config)[name] = value;
        }

        if (errors.Count > 0)
        {
            throw new ValidationException($"Revisá estos campos: {string.Join(", ", errors)}.");
        }

        await registry.ReplaceConfig(instanceId, config.ToJsonString(), secrets.ToJsonString(), ct).ConfigureAwait(false);
        await audit.Record("payments.gateway_credentials", Entity(instanceId), new { fields = properties.Select(p => p.Key).ToArray() }, ct).ConfigureAwait(false);

        // Rotar las credenciales de una pasarela ofrecida no corta los cobros, pero se prueban en el momento: unas de otro
        // ambiente la apagan antes de que un cliente pague con ellas.
        if (await db.GatewaySetups.AsNoTracking().AnyAsync(s => s.ModuleInstanceId == instanceId && s.Enabled, ct).ConfigureAwait(false))
        {
            await TestConnection(instanceId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Los campos secretos que ya tienen valor, sin el valor: el formulario dice que están guardados y no los muestra.</summary>
    public async Task<IReadOnlyList<string>> SavedSecrets(long instanceId, CancellationToken ct = default)
    {
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var properties = module.Manifest["config_schema"]?["properties"] as JsonObject ?? [];
        return [.. properties.Where(p => p.Value?["x-secret"]?.GetValue<bool>() == true && module.Data.Config[p.Key] is not null).Select(p => p.Key)];
    }

    /// <summary>
    /// Prueba la conexión con instance/test y le pasa la URL de avisos, para que el módulo compruebe que el aviso apunta
    /// al núcleo. Guarda el resultado. Credenciales de otro ambiente que la instancia no pasan la prueba y, si la
    /// instancia se ofrecía, la apagan: con unas de prueba en producción un cliente pagaría con una tarjeta de prueba.
    /// </summary>
    public async Task<ConnectionTest> TestConnection(long instanceId, CancellationToken ct = default)
    {
        var setup = await Setup(instanceId, ct).ConfigureAwait(false);
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var result = await gateways.Send(module, "/v0/instance/test", "test", new JsonObject { ["webhook_url"] = WebhookUrl(instanceId) }, null, null, ct).ConfigureAwait(false);
        var warnings = new List<string>();
        bool? live = null;
        var otherEnvironment = false;
        string message;
        if (result.Error is { } error)
        {
            var fields = error.Details?["fields"] is JsonArray f ? $" ({string.Join(", ", f.Select(x => x?.ToString()))})" : string.Empty;
            message = $"{error.Code}: {error.Message}{fields}";
        }
        else
        {
            var r = result.Response?.Result as JsonObject ?? [];
            live = r["livemode"] is JsonValue lv && lv.TryGetValue<bool>(out var l) ? l : null;
            warnings.AddRange((r["warnings"] as JsonArray ?? []).Select(w => w?.ToString()).OfType<string>());
            if (live is { } isLive && isLive != (setup.Environment == Live))
            {
                otherEnvironment = true;
                warnings.Add(isLive ? "Las credenciales son de producción y la instancia es de prueba." : "Las credenciales son de prueba y la instancia es de producción.");
            }

            message = string.Join(" ", new[] { r["account_id"] is { } a ? $"Cuenta {a}." : null, live is null ? null : live.Value ? "Modo producción." : "Modo prueba." }.OfType<string>());
        }

        var ok = result.Error is null && !otherEnvironment;
        var disabled = otherEnvironment && setup.Enabled;
        setup.LastTestAt = time.GetUtcNow();
        setup.LastTestOk = ok;
        setup.Enabled &= !otherEnvironment;
        var text = string.Join(" ", new[] { message }.Concat(warnings)).Trim();
        setup.LastTestMessage = text.Length > 2000 ? text[..2000] : text;
        setup.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.gateway_tested", Entity(instanceId), new { ok, disabled }, ct).ConfigureAwait(false);
        return new ConnectionTest(ok, message, live, warnings);
    }

    /// <summary>
    /// Pago de prueba, solo en instancias de prueba: abre un checkout por el mínimo y espera el aviso. No se registra
    /// como pago, no toca facturas ni saldo y no emite comprobante.
    /// </summary>
    public async Task<CheckoutStarted> StartTestPayment(long instanceId, Currency currency, CancellationToken ct = default)
    {
        var setup = await Setup(instanceId, ct).ConfigureAwait(false);
        if (setup.Environment != Test)
        {
            throw new ValidationException("El pago de prueba es solo para instancias de prueba.");
        }

        if (!await GatewayPayments.Declares(db, instanceId, "payment.checkout", ct).ConfigureAwait(false))
        {
            throw new ValidationException("Esta pasarela no tiene página de pago.");
        }

        var publicUrl = core.Options.PublicUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Falta Billing:PublicUrl.");
        var amount = options.Value.TestPaymentAmount;
        var attempt = PaymentAttempt.Test(instanceId, currency, amount, GatewayPayments.NewReference(), time.GetUtcNow());
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var back = $"{publicUrl}/pay/return/{attempt.Reference}";
        var result = await gateways.Call(module, "create_checkout", new JsonObject
        {
            ["attempt_id"] = attempt.Reference,
            ["amount"] = GatewayPayments.Money(amount, currency),
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = "test",
                ["title"] = "Pago de prueba",
                ["quantity"] = 1,
                ["unit_price"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
            }),
            ["return_urls"] = new JsonObject { ["success"] = back, ["pending"] = back, ["failure"] = back },
            ["notification_url"] = WebhookUrl(instanceId),
        }, string.Create(CultureInfo.InvariantCulture, $"checkout-{attempt.Id}"), ct).ConfigureAwait(false);
        attempt.RedirectUrl = GatewayPayments.Redirect(module, result);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.gateway_test_payment", Entity(instanceId), new { attempt = attempt.Id }, ct).ConfigureAwait(false);
        return new CheckoutStarted(attempt.Reference, attempt.RedirectUrl);
    }

    /// <summary>
    /// Activa la pasarela en el portal: monedas (las del manifiesto, o cualquiera si declara "*"), nombre y orden, si
    /// cancela en la misma moneda extranjera (RG 5616) y, si el módulo lo declara, el débito automático. Para ofrecerla
    /// hace falta una prueba de conexión correcta.
    /// </summary>
    public async Task Activate(long instanceId, GatewayActivation input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var setup = await Setup(instanceId, ct).ConfigureAwait(false);
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var declared = DeclaredCurrencies(module.Manifest);
        var currencies = input.Currencies.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        var bad = currencies.Where(c => c.Length != 3 || !c.All(char.IsAsciiLetterUpper) || (!declared.Contains("*") && !declared.Contains(c))).ToList();
        if (bad.Count > 0)
        {
            throw new ValidationException($"La pasarela no cobra en {string.Join(", ", bad)}.");
        }

        if (input.Enabled && (setup.LastTestOk != true || currencies.Count == 0))
        {
            throw new ValidationException("Para ofrecerla en el portal hacen falta una prueba de conexión correcta y al menos una moneda.");
        }

        var capabilities = (module.Manifest["capabilities"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        if (input.AutoCharge && !capabilities.Contains("payment.saved_methods") && !capabilities.Contains("payment.subscriptions"))
        {
            throw new ValidationException("Esta pasarela no ofrece débito automático.");
        }

        if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 100)
        {
            throw new ValidationException("El nombre en el portal tiene entre 1 y 100 caracteres.");
        }

        setup.Enabled = input.Enabled;
        setup.DisplayName = input.DisplayName.Trim();
        setup.Position = input.Position;
        setup.Currencies = [.. currencies];
        setup.SameCurrencyCancel = input.SameCurrencyCancel;
        setup.AutoCharge = input.AutoCharge;
        setup.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.gateway_activated", Entity(instanceId), new { input.Enabled, currencies, input.AutoCharge }, ct).ConfigureAwait(false);
    }

    /// <summary>Acepta el manifiesto pendiente de una instalación (sección 5 del protocolo).</summary>
    public async Task ApproveManifest(long installationId, CancellationToken ct = default)
    {
        var installation = await db.ModuleInstallations.SingleOrDefaultAsync(i => i.Id == installationId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el módulo {installationId}.");
        if (installation.PendingManifest is not { } pending)
        {
            return;
        }

        installation.Manifest = pending;
        installation.PendingManifest = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("modules.manifest_approved", string.Create(CultureInfo.InvariantCulture, $"module_installation:{installationId}"), null, ct).ConfigureAwait(false);
    }

    private static string Entity(long instanceId) => string.Create(CultureInfo.InvariantCulture, $"module_instance:{instanceId}");

    private async Task<GatewaySetup> Setup(long instanceId, CancellationToken ct) =>
        await db.GatewaySetups.SingleOrDefaultAsync(s => s.ModuleInstanceId == instanceId, ct).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"La instancia {instanceId} no pasó por el asistente de cobros.");
}
