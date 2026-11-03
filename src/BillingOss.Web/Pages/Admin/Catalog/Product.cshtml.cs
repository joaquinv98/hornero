using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Catalog;

/// <summary>Alta y edición de un producto con su tabla de precios y sus grupos de opciones, con el mismo caso de uso que la API.</summary>
[Authorize(Policy = StaffPermissions.CatalogRead)]
public sealed class ProductModel(BillingDbContext db, BillingCore core, CatalogService catalog, IStringLocalizer<Texts> l) : PageModel
{
    public sealed class Form
    {
        public string? Name { get; set; }

        public ProductKind Kind { get; set; } = ProductKind.Manual;

        public long? GroupId { get; set; }

        public string? Description { get; set; }

        public bool Visible { get; set; } = true;

        public int SortOrder { get; set; }

        public string TaxCategory { get; set; } = TaxCategories.Digital;

        public bool AllowQuantity { get; set; }

        public int? Stock { get; set; }

        public int? TrialDays { get; set; }

        public bool Addon { get; set; }

        public List<long> ParentProductIds { get; set; } = [];

        public List<long> UpgradeProductIds { get; set; } = [];

        public bool OptionChanges { get; set; }

        public long? ModuleInstanceId { get; set; }

        public string? ModuleParams { get; set; }

        public int? TerminationDays { get; set; }

        public ProductActivation Activation { get; set; }

        public List<PriceRow> Prices { get; set; } = [];

        public List<long> OptionGroupIds { get; set; } = [];
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public long? ProductId { get; private set; }

    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public bool CanWrite => this.HasCatalogWrite();

    /// <summary>El enlace directo para contratarlo en el portal, si se vende en la tienda.</summary>
    public string? OrderLink => ProductId is { } id && Input.Visible
        ? string.Create(CultureInfo.InvariantCulture, $"{core.Options.PublicUrl?.TrimEnd('/')}/contratar/{id}")
        : null;

    public IReadOnlyList<(long Id, string Name)> Groups { get; private set; } = [];

    public IReadOnlyList<ProductChoice> Products { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> OptionGroups { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Instances { get; private set; } = [];

    public IReadOnlyList<string> Currencies { get; private set; } = [];

    /// <summary>Las categorías de impuesto (sección Impuestos), con su código y su nombre.</summary>
    public IReadOnlyList<(string Code, string Name)> TaxCategoryOptions { get; private set; } = [];

    public async Task<IActionResult> OnGet(string id, bool guardado = false)
    {
        if (!Ids.TryParse(id, out var productId))
        {
            return NotFound();
        }

        if (productId is { } existing)
        {
            if ((await CatalogQueries.Products(db, core.Today(), [existing], HttpContext.RequestAborted)).SingleOrDefault() is not { } p)
            {
                return NotFound();
            }

            Input = new Form
            {
                Name = p.Name,
                Kind = p.Kind,
                GroupId = p.GroupId,
                Description = p.Description,
                Visible = p.Visible,
                SortOrder = p.SortOrder,
                TaxCategory = p.TaxCategory,
                AllowQuantity = p.AllowQuantity,
                Stock = p.Stock,
                TrialDays = p.TrialDays,
                Addon = p.Addon,
                ParentProductIds = [.. p.ParentProductIds],
                UpgradeProductIds = [.. p.UpgradeProductIds],
                OptionChanges = p.OptionChanges,
                ModuleInstanceId = p.ModuleInstanceId,
                ModuleParams = p.ModuleParams.Count == 0 ? null : p.ModuleParams.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                TerminationDays = p.TerminationDays,
                Activation = p.Activation,
                Prices = [.. p.Prices.Select(PriceRow.From)],
                OptionGroupIds = [.. p.OptionGroupIds],
            };
        }

        ProductId = productId;
        Saved = guardado;
        await Load();
        return Page();
    }

    /// <summary>Duplica lo que se está viendo y abre la copia para editarla.</summary>
    public async Task<IActionResult> OnPostCopy(string id)
    {
        if (!Ids.TryParse(id, out var existing) || existing is not { } source)
        {
            return NotFound();
        }

        if (!CanWrite)
        {
            return Forbid();
        }

        var original = await db.Products.Where(x => x.Id == source).Select(x => x.Name).SingleOrDefaultAsync(HttpContext.RequestAborted);
        if (original is null)
        {
            return NotFound();
        }

        try
        {
            // El nombre de la copia en el idioma del staff, dentro del largo del nombre.
            var copy = await catalog.CopyProduct(source, l["Catalog.CopyName", original.Length > 190 ? original[..190] : original], HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/catalogo/productos/{copy}?guardado=true"));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    public async Task<IActionResult> OnPost(string id)
    {
        if (!Ids.TryParse(id, out var productId))
        {
            return NotFound();
        }

        if (!CanWrite)
        {
            return Forbid();
        }

        ProductId = productId;
        if (ModelState.IsValid)
        {
            try
            {
                var f = Input;
                var saved = await catalog.SaveProduct(productId, new ProductInput(
                    f.Name ?? string.Empty,
                    f.Kind,
                    f.GroupId,
                    f.Description,
                    f.Visible,
                    f.SortOrder,
                    f.TaxCategory,
                    f.AllowQuantity,
                    f.Stock,
                    f.TrialDays,
                    f.Addon,
                    f.ParentProductIds,
                    f.UpgradeProductIds,
                    f.OptionChanges,
                    f.ModuleInstanceId,
                    f.ModuleParams,
                    f.TerminationDays,
                    CatalogForm.Prices(f.Prices, l["Catalog.Prices"]),
                    f.OptionGroupIds,
                    f.Activation), HttpContext.RequestAborted);
                return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/catalogo/productos/{saved}?guardado=true"));
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

    private async Task Load()
    {
        Input.Prices = CatalogForm.WithBlanks(Input.Prices);
        var groups = await db.ProductGroups.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Id).Select(g => new { g.Id, g.Name }).ToListAsync();
        Groups = [.. groups.Select(g => (g.Id, g.Name))];
        var names = groups.ToDictionary(g => g.Id, g => g.Name);
        Products = [.. (await db.Products.AsNoTracking().Where(p => p.Id != ProductId).OrderBy(p => p.GroupId).ThenBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.GroupId }).ToListAsync())
            .Select(p => new ProductChoice(p.Id, p.Name, p.GroupId is { } g ? names.GetValueOrDefault(g) : null))];
        OptionGroups = [.. (await db.OptionGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync()).Select(g => (g.Id, g.Name))];
        TaxCategoryOptions = [.. (await db.TaxCategories.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Code, c.Name }).ToListAsync()).Select(c => (c.Code, c.Name))];
        Instances = [.. (await db.ModuleInstances.AsNoTracking().OrderBy(i => i.Name).Select(i => new { i.Id, i.Name }).ToListAsync()).Select(i => (i.Id, i.Name))];
        var used = await db.Accounts.AsNoTracking().Select(a => a.Currency).Distinct().ToListAsync();
        used.AddRange(await db.PlanPrices.AsNoTracking().Select(p => p.Currency).Distinct().ToListAsync());
        Currencies = [.. used.Select(c => c.Code).Distinct().Order(StringComparer.Ordinal)];
    }
}
