using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Access;

/// <summary>
/// Vuelta del proveedor OIDC. El usuario se busca por su "sub", que vincula un administrador con el comando
/// "staff oidc". Nunca se vincula solo por correo. Después sigue el TOTP, como con contraseña.
/// </summary>
public sealed class StaffExternalModel(BillingDbContext db, IAuthenticationSchemeProvider schemes) : PageModel
{
    public bool Linked { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        if (await schemes.GetSchemeAsync(Schemes.StaffExternal) is null)
        {
            return NotFound();
        }

        var external = await HttpContext.AuthenticateAsync(Schemes.StaffExternal);
        await HttpContext.SignOutAsync(Schemes.StaffExternal);
        var subject = external.Principal?.FindFirst("sub")?.Value;
        var user = subject is null ? null : await db.Set<StaffUser>().SingleOrDefaultAsync(u => u.OidcSubject == subject && u.DisabledAt == null);
        if (user is not null)
        {
            await Sessions.BeginSecondFactor(HttpContext, Schemes.StaffPending, user, persistent: false);
            Linked = true;

            // La vuelta del proveedor llega desde otro sitio y la cookie Strict del paso siguiente no viajaría en esa
            // misma cadena de redirecciones. El Refresh hace la navegación desde el propio sitio.
            Response.Headers["Refresh"] = "0;url=/admin/ingresar/codigo";
        }

        return Page();
    }
}
