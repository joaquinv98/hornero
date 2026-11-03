using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Help;

/// <summary>
/// Un artículo con el voto de utilidad (secciones 3.3 y 3.4 de diseno/contenidos.md). El número de un artículo importado
/// redirige a su dirección corta. Lo que no existe o no corresponde a quien mira da 404.
/// </summary>
[EnableRateLimiting(Ui.FormLimiter)]
public sealed class ArticleModel(KnowledgeService kb, IOptions<ContentOptions> options) : ContentPage
{
    public ArticleView? Data { get; private set; }

    /// <summary>"si" o "no" después de votar, para agradecer.</summary>
    public string? Voted { get; private set; }

    public async Task<IActionResult> OnGet(string key, string? voto)
    {
        var ct = HttpContext.RequestAborted;
        if (await kb.Find(Viewer, key, ct) is not { } found)
        {
            return NotFound();
        }

        if (found.Slug != key)
        {
            return RedirectPermanent("/conocimiento/" + Uri.EscapeDataString(found.Slug));
        }

        Data = await kb.Read(Viewer, found.Slug, Language, ct);
        if (Data is null)
        {
            return NotFound();
        }

        Voted = voto is "si" or "no" ? voto : null;

        // Las imágenes del propio sitio y las de los hosts configurados: la política de la interfaz solo trae las del sitio.
        Response.Headers.ContentSecurityPolicy = ContentUi.ImagePolicy(options.Value.ImageHosts);
        return Page();
    }

    /// <summary>Un voto por votante: repetirlo no suma y cambiarlo mueve el conteo.</summary>
    public async Task<IActionResult> OnPostVote(string key, bool helpful)
    {
        var ct = HttpContext.RequestAborted;
        if (await kb.Find(Viewer, key, ct) is not { } found)
        {
            return NotFound();
        }

        var voter = ContentAccess.Voter(HttpContext, create: true)!;
        if (await kb.Vote(Viewer, found.Id, voter, helpful, ct) is null)
        {
            return NotFound();
        }

        return LocalRedirect($"/conocimiento/{Uri.EscapeDataString(found.Slug)}?voto={(helpful ? "si" : "no")}#voto");
    }
}
