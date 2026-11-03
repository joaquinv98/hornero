using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Lo que se edita de una categoría.</summary>
public sealed class CategoryForm
{
    public long? ParentId { get; set; }

    public string? Slug { get; set; }

    public int SortOrder { get; set; }

    public bool Hidden { get; set; }

    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public int? Revision { get; set; }
}

/// <summary>
/// El editor de una categoría de la base de conocimiento (<c>conocimiento</c>) o de las descargas (<c>descargas</c>), con las mismas
/// reglas (sección 9 de diseno/contenidos.md). Sin ID, crea.
/// </summary>
[Authorize(Policy = StaffPermissions.ContentWrite)]
public sealed class CategoryModel(CategoryService categories) : AdminContentPage
{
    [BindProperty]
    public CategoryForm Input { get; set; } = new();

    public string Tipo { get; private set; } = "conocimiento";

    public long? Id { get; private set; }

    public bool IsHelp => Kind == CategoryKind.KnowledgeBase;

    public IReadOnlyList<Choice> Parents { get; private set; } = [];

    public TextsFields TextFields => new("Input.Texts", Input.Texts, Summary: true, Body: false, SummaryKey: "Cms.Description");

    private CategoryKind Kind => Tipo == "descargas" ? CategoryKind.Downloads : CategoryKind.KnowledgeBase;

    public async Task<IActionResult> OnGet(string tipo, long? id) => await Show(tipo, id, reload: true);

    public async Task<IActionResult> OnPostSave(string tipo, long? id)
    {
        if (!Known(tipo))
        {
            return NotFound();
        }

        Tipo = tipo;
        var input = new CategoryInput(Input.ParentId, Input.Slug, Input.SortOrder, Input.Hidden, TextForm.ToTexts(Input.Texts), Input.Revision);
        long? saved = id;
        var ok = await Try(async () =>
        {
            if (id is { } existing)
            {
                await categories.Update(Kind, existing, input, Ct);
            }
            else
            {
                saved = await categories.Create(Kind, input, Ct);
            }
        });
        return ok ? SavedRedirect(new { tipo, id = saved }) : await Show(tipo, id, reload: false);
    }

    public async Task<IActionResult> OnPostDelete(string tipo, long id, bool confirm)
    {
        if (!Known(tipo))
        {
            return NotFound();
        }

        Tipo = tipo;
        if (!confirm)
        {
            Error = Labels["Cms.DeleteNeedsConfirm"];
            return await Show(tipo, id, reload: true);
        }

        return await Try(() => categories.Delete(Kind, id, Ct))
            ? RedirectToPage(Kind == CategoryKind.KnowledgeBase ? "Help" : "Downloads", new { guardado = 1 })
            : await Show(tipo, id, reload: true);
    }

    private static bool Known(string tipo) => tipo is "conocimiento" or "descargas";

    private async Task<IActionResult> Show(string tipo, long? id, bool reload)
    {
        if (!Known(tipo))
        {
            return NotFound();
        }

        Tipo = tipo;
        Id = id;
        var all = await categories.List(Kind, Ct);
        if (id is { } existing)
        {
            if (all.FirstOrDefault(c => c.Category.Id == existing) is not { } row)
            {
                return NotFound();
            }

            if (reload)
            {
                Input = new CategoryForm
                {
                    ParentId = row.Category.ParentId,
                    Slug = row.Category.Slug,
                    SortOrder = row.Category.SortOrder,
                    Hidden = row.Category.Hidden,
                    Texts = TextForm.From(row.Texts),
                    Revision = row.Category.Revision,
                };
            }
        }

        // Una categoría no cuelga de sí misma: se saca de la lista de madres. Las que cuelgan de ella las rechaza el servicio.
        Parents = CategoryPaths(all.Where(c => c.Category.Id != id));
        return Page();
    }
}
