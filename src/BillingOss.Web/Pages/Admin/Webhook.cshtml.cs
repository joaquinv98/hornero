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
/// Un destino de webhooks: cambiarlo, rotar el secreto, mandar una prueba, borrarlo y sus últimas 50 entregas con el
/// botón de reenvío (sección 3.6 de diseno/webhooks-y-extensiones.md).
/// </summary>
[Authorize(Policy = StaffPermissions.Integrations)]
public sealed class WebhookModel(BillingDbContext db, WebhookService webhooks) : PageModel
{
    public const int Shown = 50;

    public WebhookEndpoint? Endpoint { get; private set; }

    public IReadOnlyList<WebhookDelivery> Deliveries { get; private set; } = [];

    [BindProperty]
    public string Destination { get; set; } = string.Empty;

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public List<string> Events { get; set; } = [];

    [BindProperty]
    public bool Active { get; set; }

    public string? Error { get; private set; }

    /// <summary>Clave del texto de confirmación de la última acción.</summary>
    public string? Done { get; private set; }

    public string? Secret { get; private set; }

    public async Task<IActionResult> OnGet(long id) => await Load(id, fill: true) ? Page() : NotFound();

    public Task<IActionResult> OnPostSave(long id) =>
        Act(id, () => webhooks.Update(id, new WebhookInput(Destination, Description, Events, Active), HttpContext.RequestAborted), "Webhooks.Saved", fill: false);

    public Task<IActionResult> OnPostTest(long id) =>
        Act(id, () => webhooks.SendTest(id, HttpContext.RequestAborted), "Webhooks.TestSent");

    public Task<IActionResult> OnPostResend(long id, long deliveryId) =>
        Act(id, () => webhooks.Resend(deliveryId, HttpContext.RequestAborted), "Webhooks.Resent");

    public async Task<IActionResult> OnPostRotate(long id)
    {
        if (!await db.Set<WebhookEndpoint>().AnyAsync(e => e.Id == id))
        {
            return NotFound();
        }

        Secret = await webhooks.RotateSecret(id, HttpContext.RequestAborted);
        Response.Headers.CacheControl = "no-store";
        await Load(id, fill: true);
        return Page();
    }

    public async Task<IActionResult> OnPostDelete(long id)
    {
        if (!await db.Set<WebhookEndpoint>().AnyAsync(e => e.Id == id))
        {
            return NotFound();
        }

        await webhooks.Delete(id, HttpContext.RequestAborted);
        return Redirect("/admin/configuracion/webhooks");
    }

    /// <summary>Corre la acción y vuelve a mostrar la ficha con su confirmación o con el error, que dice qué hacer.</summary>
    private async Task<IActionResult> Act(long id, Func<Task> work, string done, bool fill = true)
    {
        if (!await db.Set<WebhookEndpoint>().AnyAsync(e => e.Id == id))
        {
            return NotFound();
        }

        try
        {
            await work();
            Done = done;
            fill = true;
        }
        catch (Exception ex) when (Api.PublicErrors.IsOwn(ex) && ex is ValidationException or InvalidOperationException or KeyNotFoundException)
        {
            Error = ex.Message;
        }

        await Load(id, fill);
        return Page();
    }

    private async Task<bool> Load(long id, bool fill)
    {
        Endpoint = await db.Set<WebhookEndpoint>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
        if (Endpoint is null)
        {
            return false;
        }

        if (fill)
        {
            (Destination, Description, Events, Active) = (Endpoint.Url, Endpoint.Description, [.. Endpoint.Events], Endpoint.Active);
        }

        Deliveries = await db.Set<WebhookDelivery>().AsNoTracking().Where(d => d.EndpointId == id).OrderByDescending(d => d.Id).Take(Shown).ToListAsync();
        return true;
    }
}
