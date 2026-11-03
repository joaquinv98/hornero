using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Las novedades con su fase (borrador, programada, publicada, vencida o archivada) y el filtro por estado.</summary>
[Authorize(Policy = StaffPermissions.ContentRead)]
public sealed class NewsModel(AnnouncementService news) : AdminContentPage
{
    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    public IReadOnlyList<AnnouncementRow> Rows { get; private set; } = [];

    public async Task OnGet()
    {
        PublishState? state = Enum.TryParse<PublishState>(State, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        Rows = await news.List(state, 200, Ct);
    }

    public string TitleOf(AnnouncementRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ContentTexts.Pick(row.Texts, ContentTexts.DefaultLanguage)?.Title ?? "#" + row.Announcement.Id;
    }
}
