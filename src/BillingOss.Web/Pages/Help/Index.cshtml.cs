using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Help;

/// <summary>Ayuda: buscador, categorías con la cantidad de artículos, destacados y últimos (sección 3.3 de diseno/contenidos.md).</summary>
public sealed class IndexModel(KnowledgeService kb) : ContentPage
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<ArticleCard> Results { get; private set; } = [];

    public IReadOnlyList<CategoryNode> Tree { get; private set; } = [];

    public IReadOnlyList<ArticleCard> Featured { get; private set; } = [];

    public IReadOnlyList<ArticleCard> Latest { get; private set; } = [];

    public bool Searching => !string.IsNullOrWhiteSpace(Q);

    public async Task OnGet()
    {
        var ct = HttpContext.RequestAborted;
        if (Searching)
        {
            Results = await kb.Search(Viewer, Q, Language, 30, ct);
            return;
        }

        Tree = await kb.Tree(Viewer, Language, ct);
        Featured = await kb.Featured(Viewer, Language, 6, ct);
        Latest = await kb.Latest(Viewer, Language, 6, ct);
    }
}
