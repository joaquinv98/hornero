using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Updates;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Actualizaciones (D-20, sección 7.2): estado como el distintivo de GitLab más "sin datos", las versiones del canal con
/// su camino y el pedido, que pide volver a autenticarse con el segundo factor y queda en la auditoría. La web solo deja
/// el pedido: lo aplica el actualizador de cada nodo.
/// </summary>
[Authorize(Policy = StaffPermissions.SystemUpdates)]
public sealed class AdminUpdatesModel(UpdateService updates, UserManager<StaffUser> users, TimeProvider time, IOptions<BillingOptions> billing, SecuritySettingsStore security) : PageModel
{
    public UpdateOverview Overview { get; private set; } = null!;

    /// <summary>La versión que se está por pedir: muestra la confirmación.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Version { get; set; }

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    /// <summary>Clave de texto del error del código.</summary>
    public string? CodeError { get; private set; }

    /// <summary>El motivo del canal o del actualizador, tal cual.</summary>
    public string? RequestError { get; private set; }

    public OfferedRelease? Confirming => Overview.Offered.FirstOrDefault(o => o.Release.ReleaseVersion == Version && o.Path.Count > 0 && !o.Blocked);

    public async Task OnGet() => Overview = await updates.Overview(time.GetUtcNow());

    public async Task<IActionResult> OnPost()
    {
        var user = User.UserId() is { } id ? await users.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture)) : null;
        if (user is null)
        {
            return Forbid();
        }

        // Confirmación con un código de la app, no un ingreso: una falla cuenta para el bloqueo y no va al historial.
        if (!await TotpSetup.Confirm(users, user, Ui.StaffTotp, Code, await security.Get()))
        {
            CodeError = await users.IsLockedOutAsync(user) ? "Login.LockedOut" : "Updates.CodeFailed";
            await OnGet();
            return Page();
        }

        try
        {
            var request = await updates.Request(Version ?? string.Empty);
            return Redirect($"/admin/actualizaciones/{request.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (UpdateException ex)
        {
            RequestError = ex.Message;
            await OnGet();
            return Page();
        }
    }

    /// <summary>Tono y forma del estado: lleno es vigente, hueco pendiente y con anillo pide acción.</summary>
    public static (string Tone, string Shape) Look(UpdateState state) => state switch
    {
        UpdateState.UpToDate => ("ok", "filled"),
        UpdateState.Available => ("info", "hollow"),
        UpdateState.Urgent => ("danger", "ring"),
        _ => ("warn", "ring"),
    };

    public static (string Tone, string Shape) Look(string status) => status switch
    {
        "pending" => ("info", "hollow"),
        "running" => ("info", "hollow"),
        "done" => ("ok", "filled"),
        "failed" => ("danger", "ring"),
        _ => ("warn", "hollow"),
    };

    /// <summary>Fecha y hora en el huso de la instalación (Billing:TimeZone), como en las tablas: 28/10/2026 14:05.</summary>
    public string When(DateTimeOffset at) => Clock.When(at, billing.Value);
}

/// <summary>Horas del panel en el huso de la instalación.</summary>
public static class Clock
{
    public static string When(DateTimeOffset at, BillingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }
}
