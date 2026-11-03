using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Domains;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>Los dominios de las cuentas que la sesión puede ver, con su vencimiento y lo que cuesta renovarlos.</summary>
public sealed class DomainsModel(BillingDbContext db, PortalAccess access) : PageModel
{
    public sealed record Row(RegisteredDomain Domain, string Account);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public bool ManyAccounts { get; private set; }

    public async Task OnGet()
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Domains);
        Rows = await (from d in db.Domains.AsNoTracking()
                      join a in db.Accounts on d.AccountId equals a.Id
                      where accounts.Contains(d.AccountId)
                      orderby d.Status == DomainStatus.Cancelled, d.ExpiresOn, d.Name
                      select new Row(d, a.Name)).ToListAsync();
        ManyAccounts = Rows.Select(r => r.Domain.AccountId).Distinct().Count() > 1;
    }
}
