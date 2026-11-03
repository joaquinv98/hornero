using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Una extensión: sus capacidades, los ganchos que el staff le asigna, los eventos que esperan o fallaron y el botón
/// para quitarla (sección 4.5 de diseno/webhooks-y-extensiones.md).
/// </summary>
[Authorize(Policy = StaffPermissions.Integrations)]
public sealed class ExtensionModel(BillingDbContext db, ExtensionService extensions) : PageModel
{
    public string Name { get; private set; } = string.Empty;

    public string BaseUrl { get; private set; } = string.Empty;

    public JsonObject Manifest { get; private set; } = [];

    public bool ReceivesEvents => ExtensionCapabilities.Declares(Manifest, ExtensionCapabilities.Events);

    public bool ValidatesOrders => ExtensionCapabilities.Declares(Manifest, ExtensionCapabilities.ValidateOrder);

    [BindProperty]
    public List<string> Events { get; set; } = [];

    [BindProperty]
    public bool ValidateOrders { get; set; }

    [BindProperty]
    public bool Required { get; set; }

    /// <summary>Eventos de sus ganchos que esperan en la cola y que quedaron muertos.</summary>
    public (int Pending, int Dead) Queue { get; private set; }

    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public async Task<IActionResult> OnGet(long id) => await Load(id, fill: true) ? Page() : NotFound();

    public async Task<IActionResult> OnPostSave(long id)
    {
        try
        {
            await extensions.SetHooks(id, Events, ValidateOrders, Required, HttpContext.RequestAborted);
            Saved = true;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        return await Load(id, fill: Saved) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostRemove(long id)
    {
        try
        {
            await extensions.Remove(id, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return Redirect("/admin/complementos/extensiones");
    }

    private async Task<bool> Load(long id, bool fill)
    {
        var row = await (from i in db.ModuleInstances.AsNoTracking()
                         join m in db.ModuleInstallations.AsNoTracking() on i.InstallationId equals m.Id
                         where i.Id == id
                         select new { i.Name, m.BaseUrl, m.Manifest }).SingleOrDefaultAsync();
        if (row is null || JsonNode.Parse(row.Manifest) is not JsonObject manifest || !ExtensionCapabilities.IsExtension(manifest))
        {
            return false;
        }

        (Name, BaseUrl, Manifest) = (row.Name, row.BaseUrl, manifest);
        if (fill)
        {
            var hooks = await db.Set<ExtensionHook>().AsNoTracking().Where(h => h.InstanceId == id).ToListAsync();
            Events = [.. hooks.Where(h => h.Event != PublicEvents.ValidateOrder).Select(h => h.Event)];
            var validate = hooks.SingleOrDefault(h => h.Event == PublicEvents.ValidateOrder);
            (ValidateOrders, Required) = (validate is not null, validate?.Required ?? true);
        }

        var prefix = string.Create(CultureInfo.InvariantCulture, $"extension:{id}:");
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.Kind == JobKinds.ExtensionEvent && j.EntityKey!.StartsWith(prefix))
            .GroupBy(j => j.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        Queue = (jobs.Where(j => j.Key is JobStatus.Pending or JobStatus.Running).Sum(j => j.Count), jobs.Where(j => j.Key == JobStatus.Dead).Sum(j => j.Count));
        return true;
    }
}
