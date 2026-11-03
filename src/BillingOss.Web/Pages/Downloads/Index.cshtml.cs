using BillingOss.App.Content;
using BillingOss.Web.Content;

namespace BillingOss.Web.Pages.Downloads;

/// <summary>Las descargas que quien mira puede bajar, por categoría (sección 5.4 de diseno/contenidos.md).</summary>
public sealed class IndexModel(DownloadService downloads) : ContentPage
{
    public IReadOnlyList<DownloadGroup> Groups { get; private set; } = [];

    public async Task OnGet() => Groups = await downloads.Visible(Viewer, Language, HttpContext.RequestAborted);
}
