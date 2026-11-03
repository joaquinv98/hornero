using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.News;

/// <summary>Una novedad completa. Una que no existe, no está vigente o no corresponde a quien mira da 404.</summary>
public sealed class DetailModel(AnnouncementService news, IOptions<ContentOptions> options) : ContentPage
{
    public AnnouncementView? Item { get; private set; }

    public async Task<IActionResult> OnGet(long id)
    {
        Item = await news.Read(Viewer, id, Language, HttpContext.RequestAborted);
        if (Item is null)
        {
            return NotFound();
        }

        Response.Headers.ContentSecurityPolicy = ContentUi.ImagePolicy(options.Value.ImageHosts);
        return Page();
    }
}
