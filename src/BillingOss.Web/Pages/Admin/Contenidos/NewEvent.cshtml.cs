using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>Lo que se carga para abrir un incidente: su primer estado, el título y el mensaje por idioma, y qué componentes toca.</summary>
public sealed class IncidentForm
{
    public string? State { get; set; }

    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public List<ComponentPick> Components { get; set; } = [];
}

/// <summary>Lo que se carga para programar un mantenimiento. El horario viaja como lo escribe el staff, en la zona del proveedor.</summary>
public sealed class MaintenanceForm
{
    public List<TextForm> Texts { get; set; } = TextForm.Blank();

    public string? StartsText { get; set; }

    public string? EndsText { get; set; }

    public List<ComponentPick> Components { get; set; } = [];
}

/// <summary>
/// La pantalla "Nueva incidencia" del menú: abrir un incidente que pasa ahora o programar un mantenimiento con fecha. Los dos
/// piden los servidores y servicios que tocan, y al guardar llevan a la página del evento (sección 9 de diseno/contenidos.md).
/// </summary>
[Authorize(Policy = StaffPermissions.StatusWrite)]
public sealed class NewEventModel(StatusService status) : AdminContentPage
{
    [BindProperty]
    public IncidentForm Incident { get; set; } = new();

    [BindProperty]
    public MaintenanceForm Maintenance { get; set; } = new();

    /// <summary>Servidores y servicios que se pueden elegir en un evento: los componentes que no son grupos.</summary>
    public IReadOnlyList<Choice> Choices { get; private set; } = [];

    public Task OnGet() => Load();

    public async Task<IActionResult> OnPostOpenIncident()
    {
        var state = Enum.TryParse<StatusEventState>(Incident.State, out var parsed) ? parsed : StatusEventState.Investigating;
        var input = new IncidentInput(
            TextForm.ToTitles(Incident.Texts),
            TextForm.ToMessages(Incident.Texts),
            [.. Incident.Components.Where(c => c.Selected).Select(c => new ComponentImpact(c.ComponentId, c.ImpactOr(ComponentStatus.Degraded)))],
            state);
        long id = 0;
        if (await Try(async () => id = await status.OpenIncident(input, Ct)))
        {
            return RedirectToPage("Event", new { id, guardado = 1 });
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostSchedule()
    {
        if (ContentUi.FromInput(Maintenance.StartsText, Zone) is not { } starts || ContentUi.FromInput(Maintenance.EndsText, Zone) is not { } ends)
        {
            Error = Labels["Cms.BadDate"];
            await Load();
            return Page();
        }

        var input = new MaintenanceInput(TextForm.ToTitles(Maintenance.Texts), [.. Maintenance.Components.Where(c => c.Selected).Select(c => c.ComponentId)], starts, ends);
        long id = 0;
        if (await Try(async () => id = await status.ScheduleMaintenance(input, Ct)))
        {
            return RedirectToPage("Event", new { id, guardado = 1 });
        }

        await Load();
        return Page();
    }

    private async Task Load() => Choices = ComponentTree.Of(await status.Components(Ct)).Choices;
}
