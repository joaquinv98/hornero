using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>La portada de contenidos: lo que hay para publicar, lo que vence y lo que falta (sección 9 de diseno/contenidos.md).</summary>
[Authorize(Policy = StaffPermissions.ContentRead)]
public sealed class IndexModel(ContentSummary summary) : AdminContentPage
{
    public ContentSummaryRow Data { get; private set; } = null!;

    public async Task OnGet() => Data = await summary.Get(Ct);
}
