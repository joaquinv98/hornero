using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Consulta de un dominio para el staff (Utilidades): si está libre según la cadena de la búsqueda del portal (sistema,
/// registrar, RDAP y DNS), lo que dice el registro por RDAP y en qué cuentas está en el sistema.
/// </summary>
[Authorize(Policy = StaffPermissions.DomainsRead)]
public sealed class DomainLookupModel(BillingDbContext db, DomainSearch search, DomainLookup lookup) : PageModel
{
    public sealed record Row(long Id, long AccountId, string Account, DomainStatus Status);

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public string? Name { get; private set; }

    public DomainAvailability? Availability { get; private set; }

    public RegistryAnswer? Registry { get; private set; }

    public IReadOnlyList<Row> InSystem { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task OnGet()
    {
        if (string.IsNullOrWhiteSpace(Q))
        {
            return;
        }

        if (!DomainNames.TryNormalize(Q, out var name))
        {
            Error = "DomainLookup.Invalid";
            return;
        }

        Name = name;
        var ct = HttpContext.RequestAborted;

        // La moneda de un precio de la extensión, para preguntarle al registrar que la vende.
        var tld = "." + DomainNames.Split(name).Extension;
        var currencies = await db.TldPrices.AsNoTracking().Where(p => p.Tld == tld).Select(p => p.Currency).Distinct().ToListAsync(ct);
        Availability = (await search.Check([name], currencies.Count > 0 ? currencies[0] : Currency.Ars, ct)).Single();
        Registry = await lookup.Lookup(name, ct);
        InSystem = await (from d in db.Domains.AsNoTracking()
                          join a in db.Accounts.AsNoTracking() on d.AccountId equals a.Id
                          where d.Name == name
                          orderby d.Id descending
                          select new Row(d.Id, d.AccountId, a.Name, d.Status)).ToListAsync(ct);
    }
}
