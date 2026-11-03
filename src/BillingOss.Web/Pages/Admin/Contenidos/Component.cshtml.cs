using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Lo que se edita de un componente de la red. El tipo viaja con su nombre y no se cambia después de crearlo.</summary>
public sealed class ComponentForm
{
    public string Kind { get; set; } = nameof(ComponentKind.Server);

    public long? ParentId { get; set; }

    public string? Slug { get; set; }

    public int SortOrder { get; set; }

    public bool Hidden { get; set; }

    public long[] ProductIds { get; set; } = [];

    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public int? Revision { get; set; }
}

/// <summary>El editor de un servidor, un servicio o un grupo de la red (sección 9 de diseno/contenidos.md). Sin ID, crea.</summary>
[Authorize(Policy = StaffPermissions.StatusWrite)]
public sealed class ComponentModel(StatusService status) : AdminContentPage
{
    [BindProperty]
    public ComponentForm Input { get; set; } = new();

    public long? Id { get; private set; }

    /// <summary>Los grupos a los que puede colgar. Un grupo no cuelga de otro, pero el servicio lo valida.</summary>
    public IReadOnlyList<Choice> Groups { get; private set; } = [];

    public IReadOnlyList<Choice> Products { get; private set; } = [];

    public TextsFields TextFields => new("Input.Texts", Input.Texts, Summary: true, Body: false, SummaryKey: "Cms.Description");

    public async Task<IActionResult> OnGet(long? id) => await Show(id, reload: true);

    public async Task<IActionResult> OnPostSave(long? id)
    {
        var kind = Enum.TryParse<ComponentKind>(Input.Kind, out var parsed) && Enum.IsDefined(parsed) ? parsed : ComponentKind.Server;
        var input = new ComponentInput(Input.ParentId, kind, Input.Slug, Input.SortOrder, Input.Hidden, Input.ProductIds, TextForm.ToTexts(Input.Texts), Input.Revision);
        long? saved = id;
        var ok = await Try(async () =>
        {
            if (id is { } existing)
            {
                await status.UpdateComponent(existing, input, Ct);
            }
            else
            {
                saved = await status.CreateComponent(input, Ct);
            }
        });
        return ok ? SavedRedirect(new { id = saved }) : await Show(id, reload: false);
    }

    public async Task<IActionResult> OnPostDelete(long id, bool confirm)
    {
        if (!confirm)
        {
            Error = Labels["Cms.DeleteNeedsConfirm"];
            return await Show(id, reload: true);
        }

        return await Try(() => status.DeleteComponent(id, Ct)) ? RedirectToPage("Network", new { guardado = 1 }) : await Show(id, reload: true);
    }

    private async Task<IActionResult> Show(long? id, bool reload)
    {
        Id = id;
        var rows = await status.Components(Ct);
        if (id is { } existing)
        {
            if (rows.FirstOrDefault(r => r.Component.Id == existing) is not { } row)
            {
                return NotFound();
            }

            if (reload)
            {
                Input = new ComponentForm
                {
                    Kind = row.Component.Kind.ToString(),
                    ParentId = row.Component.ParentId,
                    Slug = row.Component.Slug,
                    SortOrder = row.Component.SortOrder,
                    Hidden = row.Component.Hidden,
                    ProductIds = row.Component.ProductIds,
                    Texts = TextForm.From(row.Texts),
                    Revision = row.Component.Revision,
                };
            }
        }

        Groups = [.. rows.Where(r => r.Component.Kind == ComponentKind.Group && r.Component.Id != id).Select(r => new Choice(r.Component.Id, ComponentTree.NameOf(r)))];
        Products = await ProductChoices();
        return Page();
    }
}
