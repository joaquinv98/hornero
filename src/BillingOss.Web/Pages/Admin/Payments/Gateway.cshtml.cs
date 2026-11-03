using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Payments;

/// <summary>
/// Asistente de una instancia de pasarela (sección 4.3 del diseño de cobros): ambiente, credenciales armadas con el
/// config_schema del módulo y su ayuda (x-help), URL y eventos de avisos, prueba de conexión, pago de prueba y
/// activación. Los secretos guardados nunca se vuelven a mostrar.
/// </summary>
[Authorize(Policy = StaffPermissions.PaymentsManage)]
public sealed class GatewayModel(BillingDbContext db) : PageModel
{
    public sealed record Field(string Name, string Type, bool Secret, bool Required, bool Saved, string? Value, string? Description, string? Help, string? HelpUrl, IReadOnlyList<string> Options);

    public ModuleInstance? Instance { get; private set; }

    public GatewaySetup? Setup { get; private set; }

    public string ModuleName { get; private set; } = string.Empty;

    public IReadOnlyList<Field> Fields { get; private set; } = [];

    public IReadOnlyList<string> Events { get; private set; } = [];

    /// <summary>Monedas que declara el manifiesto. "*" es cualquiera que acepte la pasarela.</summary>
    public IReadOnlyList<string> Currencies { get; private set; } = [];

    public bool AnyCurrency => Currencies.Contains("*");

    public bool CanCheckout { get; private set; }

    public bool CanAutoCharge { get; private set; }

    public string WebhookUrl { get; private set; } = string.Empty;

    public IReadOnlyList<PaymentAttempt> TestPayments { get; private set; } = [];

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Clave del texto de lo que se acaba de hacer.</summary>
    public string? Done { get; private set; }

    /// <summary>Mensaje del error, ya armado por el caso de uso.</summary>
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGet(long id, string? hecho)
    {
        Done = hecho switch
        {
            "credenciales" => "Gateway.SavedCredentials",
            "activada" => "Gateway.Saved",
            _ => null,
        };
        return await Load(id) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostCredentials(long id) =>
        await Run(id, async s =>
        {
            var schema = (await Schema(id))["properties"] as JsonObject ?? [];
            var values = schema.Select(p => p.Key).ToDictionary(k => k, k => (string?)Request.Form[k].ToString());
            await s.SaveCredentials(id, values, HttpContext.RequestAborted);
            return Redirect($"/admin/cobros/{id}?hecho=credenciales");
        });

    public async Task<IActionResult> OnPostTest(long id) =>
        await Run(id, async s =>
        {
            await s.TestConnection(id, HttpContext.RequestAborted);
            return Redirect($"/admin/cobros/{id}#probar");
        });

    public async Task<IActionResult> OnPostTestPayment(long id, string currency) =>
        await Run(id, async s =>
        {
            var started = await s.StartTestPayment(id, new Currency(currency.Trim().ToUpperInvariant()), HttpContext.RequestAborted);
            return Redirect(started.RedirectUrl);
        });

    public async Task<IActionResult> OnPostActivate(long id, bool enabled, string displayName, int position, string[] currency, string? otherCurrencies, bool sameCurrencyCancel, bool autoCharge) =>
        await Run(id, async s =>
        {
            var currencies = (currency ?? []).Concat((otherCurrencies ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)).ToList();
            await s.Activate(id, new GatewayActivation(enabled, displayName ?? string.Empty, position, currencies, sameCurrencyCancel, autoCharge), HttpContext.RequestAborted);
            return Redirect($"/admin/cobros/{id}?hecho=activada");
        });

    /// <summary>Corre un paso del asistente. Un error de validación o del módulo vuelve a la página con su mensaje.</summary>
    private async Task<IActionResult> Run(long id, Func<GatewaySetupService, Task<IActionResult>> step)
    {
        if (HttpContext.RequestServices.GetService<GatewaySetupService>() is not { } setup || !await db.GatewaySetups.AnyAsync(s => s.ModuleInstanceId == id))
        {
            return NotFound();
        }

        try
        {
            return await step(setup);
        }
        catch (Exception ex) when (ex is ValidationException or PermanentFailureException or RetryLaterException or ArgumentException)
        {
            Error = ex.Message;
        }

        await Load(id);
        return Page();
    }

    private async Task<JsonObject> Schema(long id)
    {
        var manifest = await db.ModuleInstances.Where(i => i.Id == id)
            .Join(db.ModuleInstallations, i => i.InstallationId, m => m.Id, (i, m) => m.Manifest).SingleAsync(HttpContext.RequestAborted);
        return JsonNode.Parse(manifest)?["config_schema"] as JsonObject ?? [];
    }

    private async Task<bool> Load(long id)
    {
        var ct = HttpContext.RequestAborted;
        Instance = await db.ModuleInstances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
        Setup = await db.GatewaySetups.AsNoTracking().SingleOrDefaultAsync(s => s.ModuleInstanceId == id, ct);
        if (Instance is null || Setup is null || HttpContext.RequestServices.GetService<GatewaySetupService>() is not { } service)
        {
            return false;
        }

        var installation = await db.ModuleInstallations.AsNoTracking().SingleAsync(i => i.Id == Instance.InstallationId, ct);
        var manifest = JsonNode.Parse(installation.Manifest) as JsonObject ?? [];
        ModuleName = manifest["name"]?.GetValue<string>() ?? installation.Name;
        var capabilities = Strings(manifest["capabilities"]);
        CanCheckout = capabilities.Contains("payment.checkout");
        CanAutoCharge = capabilities.Contains("payment.saved_methods") || capabilities.Contains("payment.subscriptions");
        Events = Strings(manifest["webhook_events"]);
        Currencies = GatewaySetupService.DeclaredCurrencies(manifest);
        WebhookUrl = service.WebhookUrl(id);

        var schema = manifest["config_schema"] as JsonObject ?? [];
        var required = Strings(schema["required"]);
        var config = JsonNode.Parse(Instance.Config) as JsonObject ?? [];
        var saved = await service.SavedSecrets(id, ct);
        var english = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";
        Fields = [.. (schema["properties"] as JsonObject ?? []).Select(p =>
        {
            var property = p.Value as JsonObject ?? [];
            var secret = property["x-secret"]?.GetValue<bool>() == true;
            var help = property["x-help"] as JsonObject;
            return new Field(
                p.Key,
                property["type"]?.GetValue<string>() ?? "string",
                secret,
                required.Contains(p.Key),
                saved.Contains(p.Key),
                secret ? null : config[p.Key]?.ToString(),
                property["description"]?.GetValue<string>(),
                help?[english ? "en" : "es"]?.GetValue<string>(),
                help?["url"]?.GetValue<string>() is { } url && url.StartsWith("https://", StringComparison.Ordinal) ? url : null,
                Strings(property["enum"]));
        })];

        TestPayments = await db.PaymentAttempts.AsNoTracking().Where(a => a.ModuleInstanceId == id && a.IsTest)
            .OrderByDescending(a => a.Id).Take(5).ToListAsync(ct);
        var warnings = new List<string>();
        if (Setup.Environment == GatewaySetupService.Live && Setup.LastTestOk == true && !await db.GatewayEvents.AnyAsync(e => e.ModuleInstanceId == id, ct))
        {
            warnings.Add("Gateways.NoWebhookYet");
        }

        Warnings = warnings;
        return true;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToJsonString()).OfType<string>()] : [];
}
