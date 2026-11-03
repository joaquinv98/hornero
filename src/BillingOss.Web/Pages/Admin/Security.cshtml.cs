using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// La seguridad de quien está en el admin: su contraseña, sus códigos de recuperación, sus ingresos y el estado del ingreso
/// por el proveedor de identidad (OIDC), de solo lectura. El segundo factor del staff es obligatorio: acá no se desactiva.
/// </summary>
public sealed class AdminSecurityModel(UserManager<StaffUser> users, AccessGuard guard, AuditLog audit, IAuthenticationSchemeProvider schemes) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public int CodesLeft { get; private set; }

    public int MinLength { get; private set; }

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public IReadOnlyList<LoginEvent> Logins { get; private set; } = [];

    /// <summary>El ingreso por OIDC está configurado en este nodo (Staff:Oidc).</summary>
    public bool OidcConfigured { get; private set; }

    /// <summary>Quien mira tiene su usuario vinculado al proveedor (comando staff oidc).</summary>
    public bool OidcLinked { get; private set; }

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.SecurityManage);

    public bool CanRead => User.HasClaim(Claims.Permission, StaffPermissions.SecurityRead);

    public async Task<IActionResult> OnGet() => await users.GetUserAsync(User) is { } user ? await Show(user) : NotFound();

    public async Task<IActionResult> OnPostCodes()
    {
        if (await users.GetUserAsync(User) is not { } user)
        {
            return NotFound();
        }

        RecoveryCodes = await TotpSetup.NewRecoveryCodes(users, user, Ui.StaffTotp, Code, await guard.Settings());
        if (RecoveryCodes is null)
        {
            Error = "Code.Failed";
        }
        else
        {
            await audit.Record("staff.recovery_codes");
        }

        return await Show(user);
    }

    /// <summary>Cambio de contraseña con la actual. Renueva el sello, así corta las otras sesiones, y renueva esta.</summary>
    public async Task<IActionResult> OnPostPassword()
    {
        if (await users.GetUserAsync(User) is not { } user)
        {
            return NotFound();
        }

        if (NewPassword != ConfirmPassword)
        {
            Error = "Reset.Mismatch";
            return await Show(user);
        }

        var result = await users.ChangePasswordAsync(user, CurrentPassword, NewPassword);
        if (!result.Succeeded)
        {
            Problems = [.. result.Errors.Select(e => e.Code).Where(c => c != "PasswordMismatch" && c.StartsWith("Password", StringComparison.Ordinal)).Distinct()];
            Error = result.Errors.Any(e => e.Code == "PasswordMismatch") ? "Pwd.WrongCurrent" : "Pwd.Rejected";
            return await Show(user);
        }

        await audit.Record("staff.password_changed");
        await Sessions.SignInStaff(HttpContext, user);
        Notice = "Pwd.Changed";
        return await Show(user);
    }

    private async Task<IActionResult> Show(StaffUser user)
    {
        CodesLeft = await users.CountRecoveryCodesAsync(user);
        MinLength = (await guard.Settings()).StaffMinLength;
        Logins = await guard.History(LoginAudience.Staff, user.Id, 20);
        OidcConfigured = await schemes.GetSchemeAsync(Schemes.StaffOidc) is not null;
        OidcLinked = user.OidcSubject is not null;
        return Page();
    }
}
