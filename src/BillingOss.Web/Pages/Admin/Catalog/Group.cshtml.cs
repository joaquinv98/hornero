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

/// <summary>Alta y edición de un grupo de productos: ordena la tienda y el catálogo.</summary>
[Authorize(Policy = StaffPermissions.CatalogRead)]
public sealed class GroupModel(BillingDbContext db, CatalogService catalog, IStringLocalizer<Texts> l) : PageModel
{
    public sealed class Form
    {
        public string? Name { get; set; }

        public string? Headline { get; set; }

        public bool Visible { get; set; } = true;

        public int SortOrder { get; set; }
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public long? GroupId { get; private set; }

    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public bool CanWrite => this.HasCatalogWrite();

    public async Task<IActionResult> OnGet(string id, bool guardado = false)
    {
        if (!Ids.TryParse(id, out var groupId))
        {
            return NotFound();
        }

        if (groupId is { } existing)
        {
            if (await db.ProductGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == existing) is not { } g)
            {
                return NotFound();
            }

            Input = new Form { Name = g.Name, Headline = g.Headline, Visible = g.Visible, SortOrder = g.SortOrder };
        }

        GroupId = groupId;
        Saved = guardado;
        return Page();
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
        if (!ModelState.IsValid)
        {
            Error = l["Catalog.InvalidFields"];
            return Page();
        }

        try
        {
            var saved = await catalog.SaveGroup(groupId, new ProductGroupInput(Input.Name ?? string.Empty, Input.Headline, Input.Visible, Input.SortOrder), HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/catalogo/grupos/{saved}?guardado=true"));
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            return Page();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
