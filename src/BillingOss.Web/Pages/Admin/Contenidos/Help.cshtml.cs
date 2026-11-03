using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Los artículos de la base de conocimiento con sus filtros, y las categorías (sección 9 de diseno/contenidos.md).</summary>
[Authorize(Policy = StaffPermissions.ContentRead)]
public sealed class HelpModel(KnowledgeService kb, CategoryService categories) : AdminContentPage
{
    private Dictionary<long, string> names = [];

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    public IReadOnlyList<ArticleRow> Rows { get; private set; } = [];

    public IReadOnlyList<CategoryRow> Categories { get; private set; } = [];

    public IReadOnlyList<Choice> CategoryChoices { get; private set; } = [];

    public async Task OnGet()
    {
        Categories = await categories.List(CategoryKind.KnowledgeBase, Ct);
        CategoryChoices = CategoryPaths(Categories);
        names = CategoryChoices.ToDictionary(c => c.Id, c => c.Name);
        PublishState? state = Enum.TryParse<PublishState>(State, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        // ponytail: los 200 artículos más recientes del filtro, sin paginar. Paginar si la base de conocimiento pasa de unos cientos.
        Rows = await kb.List(new ArticleFilter(Category, state, Q), Ct);
    }

    public string CategoryName(long id) => names.GetValueOrDefault(id, "#" + id);
}
