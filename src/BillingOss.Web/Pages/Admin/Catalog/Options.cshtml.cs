using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Catalog;

/// <summary>
/// Un grupo de opciones configurables con sus opciones, valores y precios por moneda y ciclo, y los productos que lo
/// ofrecen. Lo que se saca de la pantalla no se borra: un valor que ya no se vende se oculta, porque hay servicios que
/// lo tienen.
/// </summary>
[Authorize(Policy = StaffPermissions.CatalogRead)]
public sealed class OptionsModel(BillingDbContext db, CatalogService catalog, IStringLocalizer<Texts> l) : PageModel
{
    public sealed class Form
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<long> ProductIds { get; set; } = [];

        public List<OptionRow> Options { get; set; } = [];
    }

    public sealed class OptionRow
    {
        public long? Id { get; set; }

        public string? Name { get; set; }

        public OptionKind Kind { get; set; }

        public string? ModuleKey { get; set; }

        public int MinQuantity { get; set; }

        public int MaxQuantity { get; set; }

        public int SortOrder { get; set; }

        public bool Hidden { get; set; }

        public List<ValueRow> Values { get; set; } = [];

        public bool IsBlank => Id is null && string.IsNullOrWhiteSpace(Name) && Values.All(v => v.IsBlank);
    }

    public sealed class ValueRow
    {
        public long? Id { get; set; }

        public string? Name { get; set; }

        public string? ModuleValue { get; set; }

        public int SortOrder { get; set; }

        public bool Hidden { get; set; }

        public List<PriceRow> Prices { get; set; } = [];

        public bool IsBlank => Id is null && string.IsNullOrWhiteSpace(Name) && Prices.All(p => p.IsBlank);
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public long? GroupId { get; private set; }

    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public bool CanWrite => this.HasCatalogWrite();

    public IReadOnlyList<ProductChoice> Products { get; private set; } = [];

    public IReadOnlyList<string> Currencies { get; private set; } = [];

    public async Task<IActionResult> OnGet(string id, bool guardado = false)
    {
        if (!Ids.TryParse(id, out var groupId))
        {
            return NotFound();
        }

        if (groupId is { } existing)
        {
            if ((await CatalogQueries.OptionGroups(db, [existing], HttpContext.RequestAborted)).SingleOrDefault() is not { } g)
            {
                return NotFound();
            }

            Input = new Form
            {
                Name = g.Name,
                Description = g.Description,
                ProductIds = [.. g.ProductIds],
                Options = [.. g.Options.Select(o => new OptionRow
                {
                    Id = o.Id,
                    Name = o.Name,
                    Kind = o.Kind,
                    ModuleKey = o.ModuleKey,
                    MinQuantity = o.MinQuantity,
                    MaxQuantity = o.MaxQuantity,
                    SortOrder = o.SortOrder,
                    Hidden = o.Hidden,
                    Values = [.. o.Values.Select(v => new ValueRow
                    {
                        Id = v.Id,
                        Name = v.Name,
                        ModuleValue = v.ModuleValue,
                        SortOrder = v.SortOrder,
                        Hidden = v.Hidden,
                        Prices = [.. v.Prices.Select(PriceRow.From)],
                    })],
                })],
            };
        }

        GroupId = groupId;
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

        var original = await db.OptionGroups.Where(x => x.Id == source).Select(x => x.Name).SingleOrDefaultAsync(HttpContext.RequestAborted);
        if (original is null)
        {
            return NotFound();
        }

        try
        {
            // El nombre de la copia en el idioma del staff, dentro del largo del nombre.
            var copy = await catalog.CopyOptionGroup(source, l["Catalog.CopyName", original.Length > 190 ? original[..190] : original], HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/catalogo/opciones/{copy}?guardado=true"));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    public async Task<IActionResult> OnPost(string id)
    {
        if (!Ids.TryParse(id, out var groupId))
        {
            return NotFound();
        }

        if (!CanWrite)
        {
            return Forbid();
        }

        GroupId = groupId;
        if (ModelState.IsValid)
        {
            try
            {
                var options = Input.Options.Where(o => !o.IsBlank).Select(o => new OptionInput(
                    o.Id,
                    o.Name ?? string.Empty,
                    o.Kind,
                    o.ModuleKey,
                    o.MinQuantity,
                    o.MaxQuantity,
                    o.SortOrder,
                    o.Hidden,
                    [.. o.Values.Where(v => !v.IsBlank).Select(v => new OptionValueInput(v.Id, v.Name ?? string.Empty, v.ModuleValue, v.SortOrder, v.Hidden, CatalogForm.Prices(v.Prices, $"{o.Name}: {v.Name}")))])).ToList();
                var saved = await catalog.SaveOptionGroup(groupId, new OptionGroupInput(Input.Name ?? string.Empty, Input.Description, options, Input.ProductIds), HttpContext.RequestAborted);
                return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/catalogo/opciones/{saved}?guardado=true"));
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

    /// <summary>
    /// Lo que la pantalla necesita además del formulario, y las filas en blanco para cargar sin JavaScript: un valor más
    /// por opción, un precio más por valor y una opción nueva con dos valores.
    /// </summary>
    private async Task Load()
    {
        Input.Options = [.. Input.Options.Where(o => !o.IsBlank), new OptionRow { Values = [new ValueRow(), new ValueRow()] }];
        foreach (var option in Input.Options)
        {
            option.Values = [.. option.Values.Where(v => !v.IsBlank), .. option.Id is null && option.Values.All(v => v.IsBlank) ? option.Values : [new ValueRow()]];
            foreach (var value in option.Values)
            {
                value.Prices = CatalogForm.WithBlanks(value.Prices, 1);
            }
        }

        var groups = await db.ProductGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name);
        Products = [.. (await db.Products.AsNoTracking().OrderBy(p => p.GroupId).ThenBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.GroupId }).ToListAsync())
            .Select(p => new ProductChoice(p.Id, p.Name, p.GroupId is { } g ? groups.GetValueOrDefault(g) : null))];
        var used = await db.Accounts.AsNoTracking().Select(a => a.Currency).Distinct().ToListAsync();
        used.AddRange(await db.OptionPrices.AsNoTracking().Select(p => p.Currency).Distinct().ToListAsync());
        Currencies = [.. used.Select(c => c.Code).Distinct().Order(StringComparer.Ordinal)];
    }
}
