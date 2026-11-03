using System.Globalization;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Lo que se edita de una novedad. El vencimiento viaja como lo escribe el staff, en la zona del proveedor.</summary>
public sealed class AnnouncementForm
{
    public bool Pinned { get; set; }

    public string? ExpiresText { get; set; }

    public ContentAudience Audience { get; set; }

    public long[] ProductIds { get; set; } = [];

    public long[] GroupIds { get; set; } = [];

    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public int? Revision { get; set; }
}

/// <summary>
/// El editor de una novedad con programación, fijado y vencimiento (sección 9 de diseno/contenidos.md). La dirección termina
/// en <c>nueva</c> para crear o en el ID para editar.
/// </summary>
[Authorize(Policy = StaffPermissions.ContentWrite)]
public sealed class NewsEditModel(AnnouncementService news) : AdminContentPage
{
    public const string NewKey = "nueva";

    [BindProperty]
    public AnnouncementForm Input { get; set; } = new();

    public string Key { get; private set; } = NewKey;

    public long? Id { get; private set; }

    public AnnouncementPhase Phase { get; private set; }

    public DateTimeOffset? PublishAt { get; private set; }

    /// <summary>La fecha de publicación para el campo de programar: la que tiene si está programada, vacía si no.</summary>
    public string PublishText => Phase == AnnouncementPhase.Scheduled ? ContentUi.ToInput(PublishAt, Zone) : string.Empty;

    public AudienceFields AudienceFields { get; private set; } = null!;

    public TextsFields TextFields => new("Input.Texts", Input.Texts, Summary: true, Body: true, Preview: true, PreviewUrl: "?handler=Preview");

    public async Task<IActionResult> OnGet(string key) => await Show(key, reload: true);

    public async Task<IActionResult> OnPostSave(string key)
    {
        if (!TryKey(key, out var id))
        {
            return NotFound();
        }

        if (!TryDate(Input.ExpiresText, out var expires))
        {
            Error = Labels["Cms.BadDate"];
            return await Show(key, reload: false);
        }

        var input = new AnnouncementInput(Input.Audience, Input.ProductIds, Input.GroupIds, Input.Pinned, expires, TextForm.ToTexts(Input.Texts), Input.Revision);
        long? saved = id;
        var ok = await Try(async () =>
        {
            if (id is { } existing)
            {
                await news.Update(existing, input, Ct);
            }
            else
            {
                saved = await news.Create(input, Ct);
            }
        });
        return ok ? SavedRedirect(new { key = saved?.ToString(CultureInfo.InvariantCulture) }) : await Show(key, reload: false);
    }

    public async Task<IActionResult> OnPostPublish(string key, string? publishAt, int? revision)
    {
        if (!TryKey(key, out var id) || id is null)
        {
            return NotFound();
        }

        if (!TryDate(publishAt, out var at))
        {
            Error = Labels["Cms.BadDate"];
            return await Show(key, reload: true);
        }

        return await Try(() => news.Publish(id.Value, at, revision, Ct)) ? SavedRedirect() : await Show(key, reload: true);
    }

    public Task<IActionResult> OnPostUnpublish(string key, int? revision) => Move(key, id => news.Unpublish(id, revision, Ct));

    public Task<IActionResult> OnPostArchive(string key, int? revision) => Move(key, id => news.Archive(id, revision, Ct));

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

        return await Try(() => news.Delete(id.Value, Ct)) ? RedirectToPage("News", new { guardado = 1 }) : await Show(key, reload: true);
    }

    /// <summary>La vista previa del cuerpo de uno de los idiomas, para htmx mientras se escribe.</summary>
    public IActionResult OnPostPreview(int which) =>
        Content(which >= 0 && which < Input.Texts.Count ? Render(Input.Texts[which].Body) : string.Empty, "text/html; charset=utf-8");

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
            if (await news.Get(existing, Ct) is not { } row)
            {
                return NotFound();
            }

            Phase = row.Phase;
            PublishAt = row.Announcement.PublishAt;
            if (reload)
            {
                Input = new AnnouncementForm
                {
                    Pinned = row.Announcement.Pinned,
                    ExpiresText = ContentUi.ToInput(row.Announcement.ExpiresAt, Zone),
                    Audience = row.Announcement.Audience,
                    ProductIds = row.Announcement.ProductIds,
                    GroupIds = row.Announcement.GroupIds,
                    Texts = TextForm.From(row.Texts),
                    Revision = row.Announcement.Revision,
                };
            }
        }

        AllowImages();
        AudienceFields = Audience(Input.Audience, Input.ProductIds, Input.GroupIds, await ProductChoices(), await GroupChoices(), "Input");
        return Page();
    }

    /// <summary>Una fecha vacía es válida y vale nada. Una que no se entiende no.</summary>
    private bool TryDate(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        value = ContentUi.FromInput(text, Zone);
        return value is not null;
    }
}
