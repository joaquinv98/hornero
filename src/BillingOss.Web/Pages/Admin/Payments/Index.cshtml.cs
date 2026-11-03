using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Payments;

/// <summary>Estado de una instancia de pasarela en el asistente de cobros.</summary>
public enum GatewayState
{
    /// <summary>Creada fuera del asistente: se ofrece como antes, en todas las monedas.</summary>
    Legacy,

    /// <summary>Sin credenciales probadas.</summary>
    New,

    Error,

    /// <summary>Instancia de prueba que pasó la prueba de conexión.</summary>
    Test,

    /// <summary>De producción, probada y todavía sin ofrecer en el portal.</summary>
    Ready,

    Live,
}

/// <summary>
/// Sección Cobros del admin (sección 4.3 del diseño de cobros): una tarjeta por módulo de cobros con sus instancias y su
/// estado, la aprobación de un manifiesto nuevo, avisos de lo que falta y el acceso a transferencias.
/// </summary>
[Authorize(Policy = StaffPermissions.PaymentsManage)]
public sealed class PaymentsIndexModel(BillingDbContext db) : PageModel
{
    public sealed record InstanceRow(long Id, string Name, string? Environment, GatewayState State, string? Message, bool NoVerifiedWebhook);

    public sealed record ModuleRow(long Id, string Name, string? Version, bool PendingManifest, IReadOnlyList<string> Capabilities, IReadOnlyList<InstanceRow> Instances);

    public IReadOnlyList<ModuleRow> Modules { get; private set; } = [];

    /// <summary>Monedas de las cuentas que ningún medio activo cobra.</summary>
    public IReadOnlyList<string> Uncovered { get; private set; } = [];

    public int Unmatched { get; private set; }

    public int OpenNotices { get; private set; }

    /// <summary>El proceso tiene llave de secretos: puede crear instancias y hablar con los módulos.</summary>
    public bool Ready { get; private set; }

    public string? Error { get; private set; }

    public static GatewayState StateOf(GatewaySetup? setup) => setup switch
    {
        null => GatewayState.Legacy,
        { LastTestAt: null } => GatewayState.New,
        { LastTestOk: not true } => GatewayState.Error,
        { Environment: GatewaySetupService.Test } => GatewayState.Test,
        { Enabled: true } => GatewayState.Live,
        _ => GatewayState.Ready,
    };

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostCreate(long installation, string environment)
    {
        if (HttpContext.RequestServices.GetService<GatewaySetupService>() is not { } setup)
        {
            return NotFound();
        }

        try
        {
            var id = await setup.CreateInstance(installation, environment, HttpContext.RequestAborted);
            return Redirect($"/admin/cobros/{id}");
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostApprove(long installation)
    {
        if (HttpContext.RequestServices.GetService<GatewaySetupService>() is not { } setup)
        {
            return NotFound();
        }

        try
        {
            await setup.ApproveManifest(installation, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return RedirectToPage();
    }

    private async Task Load()
    {
        var ct = HttpContext.RequestAborted;
        Ready = HttpContext.RequestServices.GetService<GatewaySetupService>() is not null;
        var installations = await db.ModuleInstallations.AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct);
        var instances = await db.ModuleInstances.AsNoTracking().OrderBy(i => i.Id).ToListAsync(ct);
        var setups = await db.GatewaySetups.AsNoTracking().ToDictionaryAsync(s => s.ModuleInstanceId, ct);
        var withEvents = (await db.GatewayEvents.AsNoTracking().Select(e => e.ModuleInstanceId).Distinct().ToListAsync(ct)).ToHashSet();
        var modules = new List<ModuleRow>();
        foreach (var installation in installations)
        {
            var manifest = JsonNode.Parse(installation.Manifest) as JsonObject ?? [];
            if (manifest["kinds"] is not JsonArray kinds || !kinds.Any(k => k?.GetValue<string>() == "payment"))
            {
                continue;
            }

            var rows = instances.Where(i => i.InstallationId == installation.Id).Select(i =>
            {
                setups.TryGetValue(i.Id, out var setup);
                var state = StateOf(setup);
                return new InstanceRow(i.Id, setup?.DisplayName ?? i.Name, setup?.Environment, state, setup?.LastTestMessage, state is GatewayState.Live or GatewayState.Ready && !withEvents.Contains(i.Id));
            }).ToList();
            var capabilities = (manifest["capabilities"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).OfType<string>().ToList();
            modules.Add(new ModuleRow(installation.Id, manifest["name"]?.GetValue<string>() ?? installation.Name, manifest["version"]?.GetValue<string>(), installation.PendingManifest is not null, capabilities, rows));
        }

        Modules = modules;
        var transfers = await db.TransferAccounts.AsNoTracking().Where(a => a.Active).Select(a => a.Currency).ToListAsync(ct);
        var covered = setups.Values.Where(s => s.Enabled).SelectMany(s => s.Currencies).Concat(transfers.Select(c => c.Code)).ToHashSet(StringComparer.Ordinal);
        var legacy = Modules.SelectMany(m => m.Instances).Any(i => i.State == GatewayState.Legacy);
        var billed = await db.Accounts.AsNoTracking().Select(a => a.Currency).Distinct().ToListAsync(ct);
        Uncovered = legacy ? [] : [.. billed.Select(c => c.Code).Where(c => !covered.Contains(c)).Order(StringComparer.Ordinal)];
        Unmatched = await db.BankMovements.CountAsync(m => m.Status == MovementStatus.Unmatched, ct);
        OpenNotices = await db.TransferNotices.CountAsync(n => n.Status == NoticeStatus.Open, ct);
    }
}
