using System.Globalization;
using BillingOss.App;
using BillingOss.Domain.Content;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Content;

/// <summary>
/// Base de las páginas públicas de contenidos: resuelve quién mira antes de cada manejador y fija las cabeceras. Lo que se ve
/// depende de la cookie de sesión, así que nada se guarda en cachés compartidas.
/// </summary>
public abstract class ContentPage : PageModel
{
    /// <summary>Quien mira: una visita sin sesión o un cliente con sus productos y grupos.</summary>
    public ContentViewer Viewer { get; private set; } = ContentViewer.Anonymous;

    /// <summary>El idioma de quien lee: "es-AR" o "en".</summary>
    public string Language => ContentTexts.Reader(CultureInfo.CurrentUICulture.Name);

    /// <summary>La zona horaria del proveedor, para mostrar las horas.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(HttpContext.RequestServices.GetRequiredService<IOptions<BillingOptions>>().Value.TimeZone);

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.Vary = "Cookie, Accept-Language";
        Viewer = await HttpContext.RequestServices.GetRequiredService<ContentAccess>().Viewer(HttpContext, HttpContext.RequestAborted);
        await next();
    }
}
