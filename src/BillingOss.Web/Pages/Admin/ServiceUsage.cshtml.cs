using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using BillingOss.Domain.Usage;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin;

/// <summary>El uso de un servicio para el staff: la ventana abierta, lo facturado y las lecturas diarias.</summary>
[Authorize(Policy = StaffPermissions.AccountsRead)]
public sealed class ServiceUsageModel(UsageQueries usage) : PageModel
{
    public UsageView? View { get; private set; }

    public IReadOnlyList<UsageSample> Samples { get; private set; } = [];

    public IReadOnlyDictionary<string, string> Names { get; private set; } = new Dictionary<string, string>();

    public bool CanConfigure => User.HasClaim(Claims.Permission, StaffPermissions.CatalogWrite);

    public async Task<IActionResult> OnGet(long id)
    {
        View = await usage.ForService(id, HttpContext.RequestAborted);
        if (View is null)
        {
            return NotFound();
        }

        // Unos tres meses de lecturas de cada métrica.
        Samples = await usage.Samples(id, 93 * Math.Max(1, View.Metrics.Count), HttpContext.RequestAborted);
        Names = View.Metrics.ToDictionary(m => m.Code, m => m.Name);
        return Page();
    }
}
