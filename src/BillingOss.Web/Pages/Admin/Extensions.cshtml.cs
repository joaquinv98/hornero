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
/// Complementos, Extensiones (sección 4.5 de diseno/webhooks-y-extensiones.md): las extensiones instaladas con sus
/// ganchos y el alta. El secreto de firma y los campos secretos de la configuración no vuelven a la página.
/// </summary>
[Authorize(Policy = StaffPermissions.Integrations)]
public sealed class ExtensionsModel(BillingDbContext db, ExtensionService extensions) : PageModel
{
    public sealed record Row(long InstanceId, string Name, string Url, JsonObject Manifest, IReadOnlyList<ExtensionHook> Hooks);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string ModuleUrl { get; set; } = string.Empty;

    [BindProperty]
    public string SigningSecret { get; set; } = string.Empty;

    [BindProperty]
    public string Config { get; set; } = "{}";

    public string? Error { get; private set; }

    public Task OnGet() => Load();

    public async Task<IActionResult> OnPost()
    {
        try
        {
            var id = await extensions.Install(new ExtensionInput(Name, ModuleUrl, SigningSecret, Config), HttpContext.RequestAborted);
            return Redirect($"/admin/complementos/extensiones/{id}");
        }
        catch (ValidationException ex)
        {
            // La configuración puede traer campos secretos: ni ella ni el secreto vuelven al navegador (D-09).
            Error = ex.Message;
            (SigningSecret, Config) = (string.Empty, "{}");
        }

        await Load();
        return Page();
    }

    private async Task Load()
    {
        var installed = await (from i in db.ModuleInstances.AsNoTracking()
                               join m in db.ModuleInstallations.AsNoTracking() on i.InstallationId equals m.Id
                               orderby i.Id
                               select new { i.Id, i.Name, m.BaseUrl, m.Manifest }).ToListAsync();
        var hooks = await db.Set<ExtensionHook>().AsNoTracking().ToListAsync();
        Rows = [.. installed
            .Select(x => (x, Manifest: JsonNode.Parse(x.Manifest) as JsonObject ?? []))
            .Where(x => ExtensionCapabilities.IsExtension(x.Manifest))
            .Select(x => new Row(x.x.Id, x.x.Name, x.x.BaseUrl, x.Manifest, [.. hooks.Where(h => h.InstanceId == x.x.Id)]))];
    }
}
