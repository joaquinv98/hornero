using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Access;

// Con la sesión puesta: el token antiforgery del formulario está atado a ese usuario.
[Authorize(Policy = Schemes.Staff)]
public sealed class StaffLogoutModel : PageModel
{
    public IActionResult OnGet() => Redirect("/admin");

    public async Task<IActionResult> OnPost()
    {
        await HttpContext.SignOutAsync(Schemes.Staff);
        return Redirect("/admin/ingresar");
    }
}
