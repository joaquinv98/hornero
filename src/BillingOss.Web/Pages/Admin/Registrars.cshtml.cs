using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Los registradores de la instalación (Configuración): cada instancia con su módulo, si está desplegado, lo que sabe
/// hacer, cuántos dominios y extensiones atiende, y la prueba de su configuración contra el registrar.
/// </summary>
[Authorize(Policy = StaffPermissions.DomainsRead)]
public sealed class RegistrarsModel(BillingDbContext db) : PageModel
{
    public sealed record Row(long Id, string Name, string Module, string Where, IReadOnlyList<string> Capabilities, int Domains, int Tlds);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.DomainsManage);

    /// <summary>La instancia probada y lo que contestó: nulo es que anduvo.</summary>
    public (long Id, string? Error)? Tested { get; private set; }

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostTest(long instance)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        await Load();
        if (Rows.All(r => r.Id != instance))
        {
            return NotFound();
        }

        // Sin la llave de secretos los módulos no están habilitados: no hay con qué llamar.
        var queries = HttpContext.RequestServices.GetService<RegistrarQueries>();
        var result = queries is null ? null : await queries.Test(instance, HttpContext.RequestAborted);
        Tested = (instance, result is null ? "-" : result.Error is { } error ? $"{error.Code}: {error.Message}" : null);
        return Page();
    }

    private async Task Load()
    {
        var registrars = await DomainJobs.Registrars(db, HttpContext.RequestAborted);
        var ids = registrars.Select(r => r.Id).ToList();
        var installations = await (from i in db.ModuleInstances.AsNoTracking()
                                   join m in db.ModuleInstallations.AsNoTracking() on i.InstallationId equals m.Id
                                   where ids.Contains(i.Id)
                                   select new { i.Id, m.BaseUrl, m.Manifest, i.Config }).ToDictionaryAsync(x => x.Id);
        var domains = await db.Domains.AsNoTracking()
            .Where(d => ids.Contains(d.RegistrarInstanceId) && (d.Status == DomainStatus.Pending || d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace))
            .GroupBy(d => d.RegistrarInstanceId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var tlds = await db.TldPrices.AsNoTracking().Where(p => ids.Contains(p.RegistrarInstanceId))
            .GroupBy(p => p.RegistrarInstanceId).Select(g => new { g.Key, Count = g.Select(p => p.Tld).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        Rows = [.. registrars.Select(r =>
        {
            var m = installations[r.Id];
            var manifest = JsonNode.Parse(m.Manifest);
            var where = m.BaseUrl == ManualModule.Address ? $"manual: {JsonNode.Parse(m.Config)?["registry"]}" : m.BaseUrl.Length == 0 ? string.Empty : m.BaseUrl;
            return new Row(r.Id, r.Name, manifest?["name"]?.ToString() ?? manifest?["id"]?.ToString() ?? string.Empty, where,
                [.. r.Capabilities.Where(c => c != "registrar.core" && c.StartsWith("registrar.", StringComparison.Ordinal)).Select(c => c["registrar.".Length..]).Order(StringComparer.Ordinal)],
                domains.GetValueOrDefault(r.Id), tlds.GetValueOrDefault(r.Id));
        })];
    }
}
