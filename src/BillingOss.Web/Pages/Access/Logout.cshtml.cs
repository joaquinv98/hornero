using System.Globalization;
using BillingOss.App.Platform;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

/// <summary>Cierra la sesión del portal. Si era "ver como cliente", lo deja en la auditoría y vuelve al admin.</summary>
// Con la sesión puesta: el token antiforgery del formulario está atado a ese usuario.
[Authorize(Policy = Schemes.Portal)]
public sealed class LogoutModel(AuditLog audit) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPost()
    {
        var current = await HttpContext.AuthenticateAsync(Schemes.Portal);
        await HttpContext.SignOutAsync(Schemes.Portal);
        if (current.Principal?.ActingAccount() is { } account)
        {
            // El actor es quien del staff miraba: lo fija la interfaz a partir de la sesión del portal.
            await audit.Record("portal.impersonate_end", string.Create(CultureInfo.InvariantCulture, $"account:{account}"));
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/cuentas/{account}"));
        }

        return Redirect("/ingresar");
    }
}
