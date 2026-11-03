using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Lo que se edita de un artículo.</summary>
public sealed class ArticleForm
{
    public long CategoryId { get; set; }

    public string? Slug { get; set; }

    public bool Featured { get; set; }

    public int SortOrder { get; set; }

    public ContentAudience Audience { get; set; }

    public long[] ProductIds { get; set; } = [];

    public long[] GroupIds { get; set; } = [];

    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    /// <summary>La revisión con que se abrió el formulario: si otra persona lo cambió mientras tanto, guardar falla.</summary>
    public int? Revision { get; set; }
}

/// <summary>El editor de un artículo con sus dos idiomas y la vista previa del Markdown (sección 9 de diseno/contenidos.md). Sin ID, crea.</summary>
[Authorize(Policy = StaffPermissions.ContentWrite)]
public sealed class ArticleModel(KnowledgeService kb, CategoryService categories) : AdminContentPage
{
    [BindProperty]
    public ArticleForm Input { get; set; } = new();

    public long? Id { get; private set; }

    public PublishState Status { get; private set; }

    public string? Slug { get; private set; }

    public long Views { get; private set; }

    public long Helpful { get; private set; }

    public long Unhelpful { get; private set; }

    public IReadOnlyList<Choice> Categories { get; private set; } = [];

    public AudienceFields AudienceFields { get; private set; } = null!;

    public TextsFields TextFields => new("Input.Texts", Input.Texts, Summary: true, Body: true, Tags: true, Preview: true, PreviewUrl: "?handler=Preview");

    public async Task<IActionResult> OnGet(long? id) => await Show(id, reload: true);

    public async Task<IActionResult> OnPostSave(long? id)
    {
        var input = new ArticleInput(Input.CategoryId, Input.Slug, Input.Featured, Input.SortOrder, Input.Audience, Input.ProductIds, Input.GroupIds, TextForm.ToArticleTexts(Input.Texts), Input.Revision);
        long? saved = id;
        var ok = await Try(async () =>
        {
            if (id is { } existing)
            {
                await kb.Update(existing, input, Ct);
            }
            else
            {
                saved = await kb.Create(input, Ct);
            }
        });
        return ok ? SavedRedirect(new { id = saved }) : await Show(id, reload: false);
    }

    public Task<IActionResult> OnPostPublish(long id, int? revision) => Move(id, () => kb.Publish(id, revision, Ct));

    public Task<IActionResult> OnPostUnpublish(long id, int? revision) => Move(id, () => kb.Unpublish(id, revision, Ct));

    public Task<IActionResult> OnPostArchive(long id, int? revision) => Move(id, () => kb.Archive(id, revision, Ct));

    public async Task<IActionResult> OnPostDelete(long id, bool confirm)
    {
        if (!confirm)
        {
            Error = Labels["Cms.DeleteNeedsConfirm"];
            return await Show(id, reload: true);
        }

        return await Try(() => kb.Delete(id, Ct)) ? RedirectToPage("Help", new { guardado = 1 }) : await Show(id, reload: true);
    }

    /// <summary>La vista previa del cuerpo de uno de los idiomas, para htmx mientras se escribe.</summary>
    public IActionResult OnPostPreview(int which) =>
        Content(which >= 0 && which < Input.Texts.Count ? Render(Input.Texts[which].Body) : string.Empty, "text/html; charset=utf-8");

    private async Task<IActionResult> Move(long id, Func<Task> work) => await Try(work) ? SavedRedirect(new { id }) : await Show(id, reload: true);

    /// <summary>
    /// Muestra el formulario. Con <paramref name="reload"/> toma lo guardado para llenarlo. Sin él (un guardado que falló)
    /// conserva lo que escribió el staff y solo trae el estado del artículo.
    /// </summary>
    private async Task<IActionResult> Show(long? id, bool reload)
    {
        AllowImages();
        Id = id;
        if (id is { } existing)
        {
            if (await kb.Get(existing, Ct) is not { } detail)
            {
                return NotFound();
            }

            Status = detail.Article.Status;
            Slug = detail.Article.Slug;
            Views = detail.Article.Views;
            Helpful = detail.Article.Helpful;
            Unhelpful = detail.Article.Unhelpful;
            if (reload)
            {
                Input = new ArticleForm
                {
                    CategoryId = detail.Article.CategoryId,
                    Slug = detail.Article.Slug,
                    Featured = detail.Article.Featured,
                    SortOrder = detail.Article.SortOrder,
                    Audience = detail.Article.Audience,
                    ProductIds = detail.Article.ProductIds,
                    GroupIds = detail.Article.GroupIds,
                    Texts = TextForm.From(detail.Texts),
                    Revision = detail.Article.Revision,
                };
            }
        }

        Categories = CategoryPaths(await categories.List(CategoryKind.KnowledgeBase, Ct));
        AudienceFields = Audience(Input.Audience, Input.ProductIds, Input.GroupIds, await ProductChoices(), await GroupChoices(), "Input");
        return Page();
    }
}
