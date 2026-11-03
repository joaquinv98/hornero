using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Web.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Configuración, API y webhooks (sección 3.6 de diseno/webhooks-y-extensiones.md): las llaves de la API propia y de la
/// compatible, y los destinos de webhooks con su última entrega. El secreto de una llave o de un webhook aparece una sola
/// vez, en la respuesta que lo crea, y esa respuesta no se guarda en ningún caché.
/// </summary>
[Authorize(Policy = StaffPermissions.Integrations)]
public sealed class ApiModel(BillingDbContext db, WebhookService webhooks, ApiKeyService keys, EndpointDataSource endpoints, IOptions<CompatOptions> compat, IOptions<BillingOptions> billing, TimeProvider time) : PageModel
{
    public sealed record Row(WebhookEndpoint Endpoint, WebhookDelivery? Last);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public IReadOnlyList<ApiKey> Keys { get; private set; } = [];

    /// <summary>Los permisos de la API propia, leídos de sus endpoints: se ofrecen todos los que existen.</summary>
    public IReadOnlyList<string> OwnScopes { get; private set; } = [];

    /// <summary>Los comandos de la API compatible. Cada uno es el permiso compat: y su nombre.</summary>
    public IReadOnlyList<string> CompatCommands { get; } = [.. CompatApi.CommandNames];

    public bool CompatEnabled => compat.Value.Enabled;

    public DateTimeOffset Now => time.GetUtcNow();

    [BindProperty]
    public string KeyName { get; set; } = string.Empty;

    [BindProperty]
    public List<string> Scopes { get; set; } = [];

    [BindProperty]
    public string? AllowedIps { get; set; }

    [BindProperty]
    public DateOnly? ExpiresOn { get; set; }

    [BindProperty]
    public int? RequestsPerMinute { get; set; }

    [BindProperty]
    public string Destination { get; set; } = string.Empty;

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public List<string> Events { get; set; } = [];

    public string? KeyError { get; private set; }

    public string? WebhookError { get; private set; }

    public IssuedApiKey? Issued { get; private set; }

    public (long Id, string Secret)? Created { get; private set; }

    public bool Revoked { get; private set; }

    public Task OnGet() => Load();

    public async Task<IActionResult> OnPostKey()
    {
        await Load();
        try
        {
            var known = OwnScopes.Concat(CompatCommands.Select(c => "compat:" + c)).ToHashSet(StringComparer.Ordinal);
            if (Scopes.FirstOrDefault(s => !known.Contains(s)) is { } unknown)
            {
                throw new ValidationException($"Permiso desconocido: {unknown}.");
            }

            if (RequestsPerMinute is < 1)
            {
                throw new ValidationException("El tope de pedidos por minuto va de 1 para arriba. Vacío usa el de la instalación.");
            }

            var ips = (AllowedIps ?? string.Empty).Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
            Issued = await keys.Issue(KeyName, Scopes, ips, ExpiresOn is { } day ? EndOf(day) : null, RequestsPerMinute, HttpContext.RequestAborted);
            Response.Headers.CacheControl = "no-store";
            (KeyName, Scopes, AllowedIps, ExpiresOn, RequestsPerMinute) = (string.Empty, [], null, null, null);
            await Load();
        }
        catch (ValidationException ex)
        {
            KeyError = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRevoke(long keyId)
    {
        try
        {
            await keys.Revoke(keyId, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        Revoked = true;
        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostWebhook()
    {
        try
        {
            Created = await webhooks.Create(new WebhookInput(Destination, Description, Events), HttpContext.RequestAborted);
            Response.Headers.CacheControl = "no-store";
            (Destination, Description, Events) = (string.Empty, null, []);
        }
        catch (ValidationException ex)
        {
            WebhookError = ex.Message;
        }

        await Load();
        return Page();
    }

    /// <summary>La llave vale hasta el final del día elegido, en la zona horaria del proveedor.</summary>
    private DateTimeOffset EndOf(DateOnly day)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(billing.Value.TimeZone);
        var next = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(next, zone.GetUtcOffset(next));
    }

    private async Task Load()
    {
        OwnScopes = ApiV1.Scopes(endpoints);
        Keys = await db.ApiKeys.AsNoTracking().OrderBy(k => k.RevokedAt != null).ThenByDescending(k => k.Id).ToListAsync();
        var list = await db.Set<WebhookEndpoint>().AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        var lastIds = await db.Set<WebhookDelivery>().GroupBy(d => d.EndpointId).Select(g => g.Max(d => d.Id)).ToListAsync();
        var last = await db.Set<WebhookDelivery>().AsNoTracking().Where(d => lastIds.Contains(d.Id)).ToDictionaryAsync(d => d.EndpointId);
        Rows = [.. list.Select(e => new Row(e, last.GetValueOrDefault(e.Id)))];
    }
}
