using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Help;

/// <summary>
/// Una categoría con sus subcategorías y artículos, y las migas de pan. Una sin nada visible para quien mira da 404. El número de
/// una categoría importada redirige a su dirección corta.
/// </summary>
public sealed class CategoryModel(KnowledgeService kb) : ContentPage
{
    public CategoryPage? Data { get; private set; }

    public async Task<IActionResult> OnGet(string slug)
    {
        Data = await kb.Category(Viewer, slug, Language, HttpContext.RequestAborted);
        if (Data is null)
        {
            return NotFound();
        }

        return Data.Category.Slug == slug ? Page() : RedirectPermanent("/conocimiento/c/" + Uri.EscapeDataString(Data.Category.Slug));
    }
}
