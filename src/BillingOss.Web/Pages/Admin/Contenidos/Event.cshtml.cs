using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Una novedad sobre un evento: el estado en que lo deja, el mensaje por idioma y, en un incidente, los componentes con su impacto.</summary>
public sealed class UpdateForm
{
    public string? State { get; set; }

    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public List<ComponentPick> Components { get; set; } = [];

    public int? Revision { get; set; }
}

/// <summary>La corrección del título de un evento. No es una novedad y no avisa a nadie.</summary>
public sealed class RetitleForm
{
    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public int? Revision { get; set; }
}

/// <summary>El nuevo horario de un mantenimiento, como lo escribe el staff en la zona del proveedor.</summary>
public sealed class RescheduleForm
{
    public string? StartsText { get; set; }

    public string? EndsText { get; set; }

    public int? Revision { get; set; }
}

/// <summary>
/// Un incidente o un mantenimiento para el staff (sección 9 de diseno/contenidos.md): su historial, el formulario para publicar
/// una novedad, corregir el título y, en un mantenimiento, reprogramarlo, darlo por terminado o cancelarlo.
/// </summary>
[Authorize(Policy = StaffPermissions.StatusWrite)]
public sealed class EventModel(StatusService status) : AdminContentPage
{
    [BindProperty]
    public UpdateForm Update { get; set; } = new();

    [BindProperty]
    public RetitleForm Retitle { get; set; } = new();

    [BindProperty]
    public RescheduleForm Reschedule { get; set; } = new();

    public EventView? Item { get; private set; }

    public IReadOnlyList<Choice> Choices { get; private set; } = [];

    public async Task<IActionResult> OnGet(long id) => await Show(id, reload: true);

    public async Task<IActionResult> OnPostUpdate(long id)
    {
        var state = Enum.TryParse<StatusEventState>(Update.State, out var parsed) ? parsed : (StatusEventState?)null;
        var wants = Update.Components.Where(c => c.Selected).Select(c => new ComponentImpact(c.ComponentId, c.ImpactOr(ComponentStatus.Degraded))).ToList();

        // En un mantenimiento no hay estado ni impactos que cambiar: el formulario solo manda el mensaje.
        var isIncident = await status.Event(id, ContentTexts.DefaultLanguage, includeHidden: true, ct: Ct) is { Kind: StatusEventKind.Incident };
        var input = new StatusUpdateInput(TextForm.ToMessages(Update.Texts), isIncident ? state : null, isIncident && Update.Components.Count > 0 ? wants : null, Update.Revision);
        return await Try(() => status.PostUpdate(id, input, Ct)) ? SavedRedirect() : await Show(id, reload: false);
    }

    public async Task<IActionResult> OnPostRetitle(long id) =>
        await Try(() => status.Retitle(id, TextForm.ToTitles(Retitle.Texts), Retitle.Revision, Ct)) ? SavedRedirect() : await Show(id, reload: false);

    public async Task<IActionResult> OnPostReschedule(long id)
    {
        if (ContentUi.FromInput(Reschedule.StartsText, Zone) is not { } starts || ContentUi.FromInput(Reschedule.EndsText, Zone) is not { } ends)
        {
            Error = Labels["Cms.BadDate"];
            return await Show(id, reload: false);
        }

        return await Try(() => status.Reschedule(id, starts, ends, Reschedule.Revision, Ct)) ? SavedRedirect() : await Show(id, reload: false);
    }

    public async Task<IActionResult> OnPostCancel(long id, int? revision) =>
        await Try(() => status.Cancel(id, revision, Ct)) ? SavedRedirect() : await Show(id, reload: true);

    public async Task<IActionResult> OnPostComplete(long id, int? revision) =>
        await Try(() => status.Complete(id, revision, Ct)) ? SavedRedirect() : await Show(id, reload: true);

    /// <summary>La vista previa del mensaje de uno de los idiomas, para htmx mientras se escribe.</summary>
    public IActionResult OnPostPreview(int which) =>
        Content(which >= 0 && which < Update.Texts.Count ? Render(Update.Texts[which].Body) : string.Empty, "text/html; charset=utf-8");

    private async Task<IActionResult> Show(long id, bool reload)
    {
        AllowImages();
        Item = await status.Event(id, ContentTexts.DefaultLanguage, includeHidden: true, ct: Ct);
        if (Item is null)
        {
            return NotFound();
        }

        var tree = ComponentTree.Of(await status.Components(Ct));
        Choices = tree.Choices;
        if (reload)
        {
            var titles = await status.EventTitles(id, Ct) ?? new Dictionary<string, ContentText>();
            Update = new UpdateForm
            {
                State = Item.State.ToString(),
                Revision = Item.Revision,
                Components = [.. Item.Components.Select(c => new ComponentPick { ComponentId = c.ComponentId, Selected = true, Impact = c.Impact.ToString() })],
            };
            Retitle = new RetitleForm { Texts = TextForm.From(titles), Revision = Item.Revision };
            Reschedule = new RescheduleForm { StartsText = ContentUi.ToInput(Item.StartsAt, Zone), EndsText = ContentUi.ToInput(Item.EndsAt, Zone), Revision = Item.Revision };
        }

        return Page();
    }
}
