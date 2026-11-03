using System.Globalization;
using BillingOss.App;
using BillingOss.App.Content;
using BillingOss.Domain.Content;
using BillingOss.Web.Api;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Content;

/// <summary>
/// Lo que las páginas de contenidos sirven fuera de Razor: el canal RSS de los anuncios, el archivo de una descarga y las
/// direcciones del sistema anterior.
/// </summary>
public static class ContentEndpoints
{
    public static void AddContentUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ContentAccess>();
        services.AddScoped<KnownIssues>();
    }

    public static void MapContentUi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Anónimo: solo lleva anuncios públicos y vigentes. Los enlaces salen de Billing:PublicUrl.
        app.MapGet("/anuncios.rss", async (string? lang, HttpContext http, AnnouncementService news, IOptions<BillingOptions> billing, IConfiguration config, CancellationToken ct) =>
        {
            var baseUrl = Uri.TryCreate(billing.Value.PublicUrl, UriKind.Absolute, out var configured)
                ? configured
                : new Uri($"{http.Request.Scheme}://{http.Request.Host}");
            var xml = await news.Rss(lang, config["Ui:Brand"] ?? "Hornero", baseUrl, ct: ct);
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Text(xml, "application/rss+xml; charset=utf-8");
        });

        // El archivo de una descarga: solo a quien corresponde, siempre como adjunto. Lo que no corresponde da 404.
        // Cada pedido completo (GET) suma uno al contador. Un HEAD responde los datos sin contarlo.
        app.MapMethods("/descargas/{id:long}/archivo", [HttpMethods.Get, HttpMethods.Head], async (long id, HttpContext http, ContentAccess access, DownloadService downloads, CancellationToken ct) =>
        {
            var viewer = await access.Viewer(http, ct);
            if (HttpMethods.IsHead(http.Request.Method))
            {
                if (await downloads.Describe(viewer, id, ct) is not { } info)
                {
                    return Results.NotFound();
                }

                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{info.FileName}\"";
                http.Response.Headers.ETag = $"\"{info.Sha256}\"";
                http.Response.ContentType = info.ContentType;
                http.Response.ContentLength = info.Size;
                return Results.Empty;
            }

            http.Response.Headers.Vary = "Cookie";
            return await downloads.Open(viewer, id, ct) is { } file ? ContentFiles.Send(http, file) : Results.NotFound();
        });

        app.MapLegacyUrls();
    }
}
