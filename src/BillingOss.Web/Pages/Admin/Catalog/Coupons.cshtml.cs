using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Catalog;

/// <summary>Promociones y cupones: primero los que se pueden usar hoy, con el enlace a editar cada uno.</summary>
[Authorize(Policy = StaffPermissions.CatalogRead)]
public sealed class CouponsModel(BillingDbContext db, BillingCore core) : PageModel
{
    public IReadOnlyList<Coupon> Coupons { get; private set; } = [];

    public bool CanWrite => this.HasCatalogWrite();

    public DateOnly Today { get; private set; }

    public async Task OnGet()
    {
        var today = Today = core.Today();
        Coupons = await db.Coupons.AsNoTracking().OrderByDescending(c => c.Active && (c.ExpiresOn == null || c.ExpiresOn >= today)).ThenBy(c => c.Code).ToListAsync();
    }
}
