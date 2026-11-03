using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Access;

/// <summary>Segundo factor del staff, obligatorio: si todavía no tiene TOTP, lo enrola acá antes de dejarlo entrar.</summary>
public sealed class StaffCodeModel(UserManager<StaffUser> users, IUserStore<StaffUser> store, AccessGuard guard, AuditLog audit, IConfiguration config) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    public TotpSetup? Setup { get; private set; }

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Los minutos del bloqueo por intentos, para el mensaje.</summary>
    public int LockMinutes { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        var (user, _) = await Sessions.PendingUser(HttpContext, Schemes.StaffPending, users);
        if (user is null)
        {
            return Redirect("/admin/ingresar");
        }

        await Prepare(user);
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        var (user, _) = await Sessions.PendingUser(HttpContext, Schemes.StaffPending, users);
        if (user is null)
        {
            return Redirect("/admin/ingresar");
        }

        // Todavía no hay sesión del staff: el actor de la auditoría es quien está entrando.
        Actor.Current = new Actor(ActorKind.Staff, string.Create(CultureInfo.InvariantCulture, $"staff:{user.Id}"), HttpContext.Connection.RemoteIpAddress);
        var attempt = LoginAttempt.From(HttpContext, guard, LoginAudience.Staff);
        LoginOutcome outcome;
        if (!user.TwoFactorEnabled)
        {
            // El enrolado es un paso del ingreso: pasa por la lista, el bloqueo y el historial como un código.
            (outcome, RecoveryCodes) = await Sessions.EnrollSecondFactor(users, attempt, user, Ui.StaffTotp, Code);
            if (outcome == LoginOutcome.Success)
            {
                await audit.Record("staff.2fa_enabled");
                await audit.Record("staff.login");
                await Sessions.SignInStaff(HttpContext, user);
                return Page();
            }

            await Prepare(user);
        }
        else
        {
            outcome = await Sessions.CheckSecondFactor(users, attempt, user, Ui.StaffTotp, Code);
        }

        switch (outcome)
        {
            case LoginOutcome.Success:
                await audit.Record("staff.login");
                await Sessions.SignInStaff(HttpContext, user);
                return Redirect("/admin");
            case LoginOutcome.LockedOut:
                LockMinutes = (await guard.Settings()).StaffAccountMinutes;
                Error = "Access.LockedOut";
                return Page();
            case LoginOutcome.IpBlocked:
                Error = "Access.IpBlocked";
                return Page();
            default:
                Error = "Code.Failed";
                return Page();
        }
    }

    private async Task Prepare(StaffUser user)
    {
        if (!user.TwoFactorEnabled)
        {
            Setup = await TotpSetup.For(users, store, user, (config["Ui:Brand"] ?? "billing-oss") + " admin");
        }
    }
}
