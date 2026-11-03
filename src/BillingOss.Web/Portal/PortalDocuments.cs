using BillingOss.App.Documents;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Web.Api;
using BillingOss.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Portal;

/// <summary>
/// Descarga del PDF de facturas y notas desde el portal, con la sesión del portal (D-10). Las cuentas que ve la sesión
/// salen de <see cref="PortalAccess"/>, como en el resto del portal: un documento ajeno da 404, igual que uno que no
/// existe (sección 13 del modelo de dominio).
/// </summary>
internal static class PortalDocuments
{
    public static void MapPortalDocuments(this WebApplication app) =>
        app.MapGet("/facturas/{id:long}/pdf", async (long id, HttpContext http, BillingDbContext db, PortalAccess access, DocumentPdfService pdfs, ILogger<DocumentPdfService> logger, CancellationToken ct) =>
        {
            var accounts = access.AccountIds(http.User, AccountCapabilities.Invoices);
            if (!await db.Documents.AnyAsync(d => d.Id == id && accounts.Contains(d.AccountId), ct))
            {
                return Results.NotFound();
            }

            try
            {
                return await pdfs.Get(id, ct) is { } pdf ? File(http, pdf) : Results.NotFound();
            }
            catch (Exception ex) when (ex is RetryLaterException || (ex is InvalidOperationException && PublicErrors.IsOwn(ex)))
            {
                // Al cliente, la página de error de la interfaz. El detalle (conversor caído, datos del emisor) va al log.
                logger.LogWarning(ex, "No se pudo dar el PDF del documento {Document} en el portal", id);
                return Results.StatusCode(ex is RetryLaterException ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict);
            }
        }).RequireAuthorization(Schemes.Portal);

    /// <summary>El PDF para descargar. Lleva datos personales: no se guarda en cachés compartidas.</summary>
    public static IResult File(HttpContext http, RenderedPdf pdf)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(pdf);
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.File(pdf.Content, "application/pdf", pdf.FileName);
    }
}
