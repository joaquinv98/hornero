using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// "Ingreso con proveedores externos" de Todos los ajustes (diseno/ingreso-clientes.md, sección 3): Google primero y otros
/// proveedores OpenID Connect, con el cliente, el secreto cifrado por campo, la dirección de vuelta para registrar en el
/// proveedor y una prueba de la conexión. El secreto nunca vuelve a la pantalla.
/// </summary>
[Authorize(Policy = StaffPermissions.SecurityManage)]
public sealed class ExternalProvidersModel(ExternalLoginStore store, ExternalSignIn signIn, BillingDbContext db) : PageModel
{
    /// <summary>Un proveedor, cargado o no. Google aparece siempre, aunque todavía no tenga fila.</summary>
    public sealed record Row(string Key, ExternalProviderKind Kind, ExternalProvider? Provider, string? RedirectUri, int Links);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    /// <summary>El resultado de la prueba y de qué proveedor.</summary>
    public (string Key, string Result)? Tested { get; private set; }

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostSave(string key, ExternalProviderKind kind, string? name, string? authority, string? clientId, string? clientSecret, bool enabled, int version)
    {
        try
        {
            await store.Save(key ?? string.Empty, kind, new ExternalProviderInput(name ?? string.Empty, authority ?? string.Empty, clientId ?? string.Empty, clientSecret, enabled), version, HttpContext.RequestAborted);
            Notice = "Providers.Saved";
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message == "External.Conflict" ? "External.Conflict" : "Providers.NoKeys";
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostTest(string key)
    {
        var provider = await store.Find(key ?? string.Empty, HttpContext.RequestAborted);
        if (provider is null)
        {
            return NotFound();
        }

        try
        {
            Tested = (provider.Key, await signIn.Test(provider, HttpContext.RequestAborted));
        }
        catch (InvalidOperationException)
        {
            Error = "Providers.NoKeys";
        }

        await Load();
        return Page();
    }

    private async Task Load()
    {
        var providers = await store.Providers(HttpContext.RequestAborted);
        var links = await db.Set<ExternalLogin>().AsNoTracking().GroupBy(l => l.ProviderId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, HttpContext.RequestAborted);
        var rows = providers.Select(p => new Row(p.Key, p.Kind, p, signIn.RedirectUri(p.Key), links.GetValueOrDefault(p.Id))).ToList();
        if (!rows.Any(r => r.Kind == ExternalProviderKind.Google))
        {
            rows.Insert(0, new Row(ExternalProvider.GoogleKey, ExternalProviderKind.Google, null, signIn.RedirectUri(ExternalProvider.GoogleKey), 0));
        }

        Rows = rows;
    }
}
