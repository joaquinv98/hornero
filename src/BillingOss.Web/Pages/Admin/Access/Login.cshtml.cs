using BillingOss.App.Identity;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Access;

/// <summary>Ingreso del staff: contraseña u OIDC, y siempre después el TOTP (D-10). Cada intento queda en el historial.</summary>
public sealed class StaffLoginModel(UserManager<StaffUser> users, AccessGuard guard, IAuthenticationSchemeProvider schemes) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? Error { get; private set; }

    /// <summary>Los minutos del bloqueo por intentos, para el mensaje.</summary>
    public int LockMinutes { get; private set; }

    public bool OidcEnabled { get; private set; }

    public async Task OnGet() => OidcEnabled = await schemes.GetSchemeAsync(Schemes.StaffOidc) is not null;

    public async Task<IActionResult> OnPost()
    {
        var (outcome, user) = await Sessions.CheckPassword(users, LoginAttempt.From(HttpContext, guard, LoginAudience.Staff), Email, Password);
        if (user is null)
        {
            await OnGet();
            LockMinutes = (await guard.Settings()).StaffAccountMinutes;
            Error = outcome switch
            {
                LoginOutcome.LockedOut => "Access.LockedOut",
                LoginOutcome.IpBlocked => "Access.IpBlocked",
                _ => "Login.Failed",
            };
            return Page();
        }

        await Sessions.BeginSecondFactor(HttpContext, Schemes.StaffPending, user, persistent: false);
        return Redirect("/admin/ingresar/codigo");
    }

    public async Task<IActionResult> OnPostOidc() =>
        await schemes.GetSchemeAsync(Schemes.StaffOidc) is null
            ? NotFound()
            : Challenge(new AuthenticationProperties { RedirectUri = "/admin/ingresar/externo" }, Schemes.StaffOidc);
}
