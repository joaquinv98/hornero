using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Catalog;

/// <summary>Alta y edición de un paquete de productos: sus planes con sus unidades y el descuento de cada línea.</summary>
[Authorize(Policy = StaffPermissions.CatalogRead)]
public sealed class BundleModel(BillingDbContext db, CatalogService catalog, BillingCore core, IStringLocalizer<Texts> l) : PageModel
{
    public sealed class Form
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? DiscountPercent { get; set; }

        public int? DiscountCycles { get; set; }

        public bool Active { get; set; } = true;

        public List<ItemRow> Items { get; set; } = [];
    }

    /// <summary>Un plan del paquete. Una fila sin plan no cuenta, y sin unidades lleva una.</summary>
    public sealed class ItemRow
    {
        public long? PlanId { get; set; }

        public int? Quantity { get; set; }
    }

    /// <summary>Un plan para elegir: el producto, su grupo para ordenar y el código del ciclo.</summary>
    public sealed record PlanChoice(long Id, string Product, string? Group, string Cycle);

    [BindProperty]
    public Form Input { get; set; } = new();

    public long? BundleId { get; private set; }

    public IReadOnlyList<PlanChoice> Plans { get; private set; } = [];

    public IReadOnlyList<BundlePriceView> Prices { get; private set; } = [];

    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public bool CanWrite => this.HasCatalogWrite();

    public async Task<IActionResult> OnGet(string id, bool guardado = false)
    {
        if (!Ids.TryParse(id, out var bundleId))
        {
            return NotFound();
        }

        if (bundleId is { } existing)
        {
            if ((await CatalogQueries.Bundles(db, core.Today(), [existing], HttpContext.RequestAborted)).SingleOrDefault() is not { } b)
            {
                return NotFound();
            }

            Prices = b.Prices;
            Input = new Form
            {
                Name = b.Name,
                Description = b.Description,
                DiscountPercent = CatalogForm.Format(b.DiscountPercent),
                DiscountCycles = b.DiscountCycles,
                Active = b.Active,
                Items = [.. b.Items.Select(i => new ItemRow { PlanId = i.PlanId, Quantity = i.Quantity })],
            };
        }

        BundleId = bundleId;
        Saved = guardado;
        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPost(string id)
    {
        if (!Ids.TryParse(id, out var bundleId))
        {
            return NotFound();
        }

        if (!CanWrite)
        {
            return Forbid();
        }

        BundleId = bundleId;
        if (ModelState.IsValid)
        {
            try
            {
                var f = Input;
                var saved = await catalog.SaveBundle(bundleId, new BundleInput(
                    f.Name ?? string.Empty,
                    [.. f.Items.Where(i => i.PlanId is not null).Select(i => new BundleItemInput(i.PlanId!.Value, i.Quantity ?? 1))],
                    CatalogForm.Amount(f.DiscountPercent, l["Catalog.Discount"]),
                    f.DiscountCycles,
                    f.Description,
                    f.Active), HttpContext.RequestAborted);
                return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/catalogo/paquetes/{saved}?guardado=true"));
            }
            catch (ValidationException ex)
            {
                Error = ex.Message;
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }
        else
        {
            Error = l["Catalog.InvalidFields"];
        }

        await Load();
        return Page();
    }

    /// <summary>Los planes que se venden, por grupo y producto, y las filas en blanco para agregar.</summary>
    private async Task Load()
    {
        Input.Items = [.. Input.Items.Where(i => i.PlanId is not null), .. Enumerable.Range(0, CatalogForm.BlankRows).Select(_ => new ItemRow())];
        var groups = await db.ProductGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name);
        Plans = [.. (await (from plan in db.Plans.AsNoTracking()
                            join product in db.Products on plan.ProductId equals product.Id
                            where plan.Active
                            orderby product.GroupId, product.SortOrder, product.Id, plan.Id
                            select new { plan.Id, product.Name, product.GroupId, Plan = plan }).ToListAsync())
            .Select(p => new PlanChoice(p.Id, p.Name, p.GroupId is { } g ? groups.GetValueOrDefault(g) : null, p.Plan.Cycle.Code))];
    }
}
