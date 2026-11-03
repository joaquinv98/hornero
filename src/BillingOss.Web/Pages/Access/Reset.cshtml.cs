using BillingOss.App.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

/// <summary>
/// Nueva contraseña con el enlace del correo. También reemplaza un hash importado y levanta el bloqueo. La contraseña
/// sigue la política de los ajustes (diseno/fraude-y-acceso.md, sección 7).
/// </summary>
public sealed class ResetModel(UserManager<PortalUser> users, SecuritySettingsStore settings) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string U { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string T { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string Confirm { get; set; } = string.Empty;

    public string? Error { get; private set; }

    /// <summary>Lo que la política rechazó, como códigos de Identity.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public int MinLength { get; private set; }

    public bool Done { get; private set; }

    public async Task OnGet() => MinLength = (await settings.Get()).MinLength;

    public async Task<IActionResult> OnPost()
    {
        await OnGet();
        if (Password != Confirm)
        {
            Error = "Reset.Mismatch";
            return Page();
        }

        var user = await users.FindByIdAsync(U);
        var result = user is null || user.DisabledAt is not null ? null : await users.ResetPasswordAsync(user, T, Password);
        if (result is not { Succeeded: true })
        {
            Problems = result?.Errors.Select(e => e.Code).Where(c => c.StartsWith("Password", StringComparison.Ordinal)).Distinct().ToList() ?? [];
            Error = Problems.Count > 0 ? "Pwd.Rejected" : "Reset.InvalidLink";
            return Page();
        }

        // El enlace llegó a su correo: queda confirmado.
        user!.EmailConfirmed = true;
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        Done = true;
        return Page();
    }
}
