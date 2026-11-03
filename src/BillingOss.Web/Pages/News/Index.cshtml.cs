using BillingOss.App.Content;
using BillingOss.Web.Content;

namespace BillingOss.Web.Pages.News;

/// <summary>Las novedades vigentes para quien mira: las fijadas arriba y después las más nuevas (sección 4.3 de diseno/contenidos.md).</summary>
public sealed class IndexModel(AnnouncementService news) : ContentPage
{
    public IReadOnlyList<AnnouncementView> Items { get; private set; } = [];

    public async Task OnGet() => Items = await news.Visible(Viewer, Language, 50, HttpContext.RequestAborted);
}
