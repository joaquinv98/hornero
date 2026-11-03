using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>La zona DNS y los reenvíos de un dominio del cliente. Lo ajeno da 404, como el resto del portal.</summary>
public sealed class DomainDnsModel(BillingDbContext db, PortalAccess access, DomainService domains) : DomainDnsPage(db, domains)
{
    public override string DomainUrl => string.Create(CultureInfo.InvariantCulture, $"/dominios/{Domain?.Id}");

    public override bool CanChange => true;

    protected override Task<RegisteredDomain?> Find(long id)
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Domains);
        return Db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && accounts.Contains(d.AccountId));
    }
}
