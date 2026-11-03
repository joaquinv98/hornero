using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Catalog;

/// <summary>Productos y servicios de un vistazo: productos por grupo, complementos, grupos de opciones y paquetes, con el enlace a editar cada uno.</summary>
[Authorize(Policy = StaffPermissions.CatalogRead)]
public sealed class CatalogIndexModel(BillingDbContext db, BillingCore core) : PageModel
{
    public IReadOnlyList<ProductGroup> Groups { get; private set; } = [];

    public IReadOnlyList<ProductView> Products { get; private set; } = [];

    public IReadOnlyList<OptionGroupView> OptionGroups { get; private set; } = [];

    public IReadOnlyList<BundleView> Bundles { get; private set; } = [];

    public bool CanWrite => this.HasCatalogWrite();

    public async Task OnGet()
    {
        var today = core.Today();
        Groups = await db.ProductGroups.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToListAsync();
        Products = await CatalogQueries.Products(db, today, null, HttpContext.RequestAborted);
        OptionGroups = await CatalogQueries.OptionGroups(db, null, HttpContext.RequestAborted);
        Bundles = await CatalogQueries.Bundles(db, today, null, HttpContext.RequestAborted);
    }
}
