using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.NetStatus;

/// <summary>Un incidente o mantenimiento con todas sus novedades. Uno que solo toca componentes ocultos no se muestra: da 404.</summary>
public sealed class DetailModel(StatusService status) : ContentPage
{
    public EventView? Item { get; private set; }

    public async Task<IActionResult> OnGet(long id)
    {
        Item = await status.Event(id, Language, includeHidden: false, ct: HttpContext.RequestAborted);
        return Item is null ? NotFound() : Page();
    }
}
