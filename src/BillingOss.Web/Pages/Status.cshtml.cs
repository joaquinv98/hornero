using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages;

/// <summary>
/// Página para los errores de la interfaz: conserva el código de estado original. Abierta directo (por ejemplo desde
/// el acceso denegado) responde con el código de la ruta, y uno que no sea de error vale como 404.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class StatusModel : PageModel
{
    public int Code { get; private set; }

    public bool Admin { get; private set; }

    public void OnGet(int code) => Show(code);

    public void OnPost(int code) => Show(code);

    private void Show(int code)
    {
        var original = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        Code = original?.OriginalStatusCode ?? (code is >= 400 and <= 599 ? code : StatusCodes.Status404NotFound);
        Admin = (original?.OriginalPath ?? string.Empty).StartsWith("/admin", StringComparison.OrdinalIgnoreCase);
        Response.StatusCode = Code;
    }
}
