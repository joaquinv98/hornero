using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

/// <summary>
/// Verificación del correo con el enlace que manda la página de seguridad (diseno/fraude-y-acceso.md, sección 5). No
/// pide sesión. El GET solo muestra el botón: así un lector de correo que abre los enlaces no confirma nada.
/// </summary>
public sealed class VerifyModel(UserManager<PortalUser> users, AuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string U { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string T { get; set; } = string.Empty;

    public bool Done { get; private set; }

    public bool Failed { get; private set; }

    public void OnGet() => Failed = U.Length == 0 || T.Length == 0;

    public async Task<IActionResult> OnPost()
    {
        var user = await users.FindByIdAsync(U);
        if (user is null || user.DisabledAt is not null || !(await users.ConfirmEmailAsync(user, T)).Succeeded)
        {
            Failed = true;
            return Page();
        }

        await audit.Record("portal.email_verified", string.Create(CultureInfo.InvariantCulture, $"user:{user.Id}"));
        Done = true;
        return Page();
    }
}
