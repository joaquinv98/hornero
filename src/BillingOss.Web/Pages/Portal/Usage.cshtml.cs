using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>El uso de un servicio: la ventana abierta de cada métrica y lo ya facturado. Lo ajeno da 404.</summary>
public sealed class UsageModel(BillingDbContext db, PortalAccess access, UsageQueries usage) : PageModel
{
    public UsageView? View { get; private set; }

    public async Task<IActionResult> OnGet(long id)
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Services);
        if (!await db.Services.AnyAsync(s => s.Id == id && accounts.Contains(s.AccountId)))
        {
            return NotFound();
        }

        View = await usage.ForService(id, HttpContext.RequestAborted);
        return View is null ? NotFound() : Page();
    }
}
