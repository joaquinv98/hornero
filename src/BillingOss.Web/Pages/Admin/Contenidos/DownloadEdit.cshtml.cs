using System.Globalization;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Lo que se edita de una descarga.</summary>
public sealed class DownloadForm
{
    public long CategoryId { get; set; }

    public int SortOrder { get; set; }

    public ContentAudience Audience { get; set; } = ContentAudience.Customers;

    public long[] ProductIds { get; set; } = [];

    public long[] GroupIds { get; set; } = [];

    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public int? Revision { get; set; }
}

/// <summary>
/// El editor de una descarga con la subida del archivo (sección 9 de diseno/contenidos.md). La dirección termina en
/// <c>nueva</c> para crear o en el ID para editar. La página sube el tope del cuerpo hasta el máximo de las descargas.
/// </summary>
[Authorize(Policy = StaffPermissions.ContentWrite)]
[ContentUploadLimit]
public sealed class DownloadEditModel(DownloadService downloads, CategoryService categories, IOptions<ContentOptions> options) : AdminContentPage
{
    public const string NewKey = "nueva";

    [BindProperty]
    public DownloadForm Input { get; set; } = new();

    public string Key { get; private set; } = NewKey;

    public long? Id { get; private set; }

    public PublishState Status { get; private set; }

    public long Count { get; private set; }

    public string? FileName { get; private set; }

    public long Size { get; private set; }

    public string? Sha256 { get; private set; }

    public long MaxBytes => options.Value.MaxDownloadBytes;

    public IEnumerable<string> Extensions => options.Value.Extensions.Order();

    public IReadOnlyList<Choice> Categories { get; private set; } = [];

    public AudienceFields AudienceFields { get; private set; } = null!;

    public TextsFields TextFields => new("Input.Texts", Input.Texts, Summary: true, Body: false, SummaryKey: "Cms.Description");

    public async Task<IActionResult> OnGet(string key) => await Show(key, reload: true);

    public async Task<IActionResult> OnPostSave(string key)
    {
        if (!TryKey(key, out var id))
        {
            return NotFound();
        }

        var input = new DownloadInput(Input.CategoryId, Input.SortOrder, Input.Audience, Input.ProductIds, Input.GroupIds, TextForm.ToTexts(Input.Texts), Input.Revision);
        long? saved = id;
        var ok = await Try(async () =>
        {
            if (id is { } existing)
            {
                await downloads.Update(existing, input, Ct);
            }
            else
            {
                saved = await downloads.Create(input, Ct);
            }
        });
        return ok ? SavedRedirect(new { key = saved?.ToString(CultureInfo.InvariantCulture) }) : await Show(key, reload: false);
    }

    /// <summary>Sube el archivo. Pasar el tope da 413 y una extensión no permitida 415, como en la API.</summary>
    public async Task<IActionResult> OnPostUpload(string key, IFormFile? file)
    {
        if (!TryKey(key, out var id) || id is null)
        {
            return NotFound();
        }

        if (file is null || file.Length == 0)
        {
            Error = Labels["Cms.NoFileChosen"];
            return await Show(key, reload: true);
        }

        var ok = await Try(async () =>
        {
            await using var content = file.OpenReadStream();
            await downloads.Upload(id.Value, file.FileName, content, Ct);
        });
        return ok ? SavedRedirect() : await Show(key, reload: true);
    }

    /// <summary>El archivo para el staff, sin sumar al contador.</summary>
    public async Task<IActionResult> OnGetFile(string key)
    {
        if (!TryKey(key, out var id) || id is null || await downloads.OpenForStaff(id.Value, Ct) is not { } file)
        {
            return NotFound();
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }

    public Task<IActionResult> OnPostPublish(string key, int? revision) => Move(key, id => downloads.Publish(id, revision, Ct));

    public Task<IActionResult> OnPostUnpublish(string key, int? revision) => Move(key, id => downloads.Unpublish(id, revision, Ct));

    public Task<IActionResult> OnPostArchive(string key, int? revision) => Move(key, id => downloads.Archive(id, revision, Ct));

    public async Task<IActionResult> OnPostDelete(string key, bool confirm)
    {
        if (!TryKey(key, out var id) || id is null)
        {
            return NotFound();
        }

        if (!confirm)
        {
            Error = Labels["Cms.DeleteNeedsConfirm"];
            return await Show(key, reload: true);
        }

        return await Try(() => downloads.Delete(id.Value, Ct)) ? RedirectToPage("Downloads", new { guardado = 1 }) : await Show(key, reload: true);
    }

    private async Task<IActionResult> Move(string key, Func<long, Task> work)
    {
        if (!TryKey(key, out var id) || id is null)
        {
            return NotFound();
        }

        return await Try(() => work(id.Value)) ? SavedRedirect() : await Show(key, reload: true);
    }

    private static bool TryKey(string key, out long? id)
    {
        id = null;
        if (key == NewKey)
        {
            return true;
        }

        if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            id = parsed;
            return true;
        }

        return false;
    }

    private async Task<IActionResult> Show(string key, bool reload)
    {
        if (!TryKey(key, out var id))
        {
            return NotFound();
        }

        Key = key;
        Id = id;
        if (id is { } existing)
        {
            if (await downloads.Get(existing, Ct) is not { } row)
            {
                return NotFound();
            }

            Status = row.Download.Status;
            Count = row.Download.DownloadCount;
            FileName = row.Download.FileName;
            Size = row.Download.Size ?? 0;
            Sha256 = row.Download.Sha256;
            if (reload)
            {
                Input = new DownloadForm
                {
                    CategoryId = row.Download.CategoryId,
                    SortOrder = row.Download.SortOrder,
                    Audience = row.Download.Audience,
                    ProductIds = row.Download.ProductIds,
                    GroupIds = row.Download.GroupIds,
                    Texts = TextForm.From(row.Texts),
                    Revision = row.Download.Revision,
                };
            }
        }

        Categories = CategoryPaths(await categories.List(CategoryKind.Downloads, Ct));
        AudienceFields = Audience(Input.Audience, Input.ProductIds, Input.GroupIds, await ProductChoices(), await GroupChoices(), "Input");
        return Page();
    }
}
