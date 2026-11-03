using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Las descargas con su archivo y su contador, las que todavía no tienen archivo, y las categorías.</summary>
[Authorize(Policy = StaffPermissions.ContentRead)]
public sealed class DownloadsModel(DownloadService downloads, CategoryService categories) : AdminContentPage
{
    private Dictionary<long, string> names = [];

    [BindProperty(SupportsGet = true)]
    public long? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    public IReadOnlyList<DownloadRow> Rows { get; private set; } = [];

    public IReadOnlyList<CategoryRow> Categories { get; private set; } = [];

    public IReadOnlyList<Choice> CategoryChoices { get; private set; } = [];

    public async Task OnGet()
    {
        Categories = await categories.List(CategoryKind.Downloads, Ct);
        CategoryChoices = CategoryPaths(Categories);
        names = CategoryChoices.ToDictionary(c => c.Id, c => c.Name);
        PublishState? state = Enum.TryParse<PublishState>(State, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        Rows = await downloads.List(Category, state, 500, Ct);
    }

    public string CategoryName(long id) => names.GetValueOrDefault(id, "#" + id);

    public string TitleOf(DownloadRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ContentTexts.Pick(row.Texts, ContentTexts.DefaultLanguage)?.Title ?? "#" + row.Download.Id;
    }
}
