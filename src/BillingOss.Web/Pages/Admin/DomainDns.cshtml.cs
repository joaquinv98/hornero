using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>La zona DNS y los reenvíos de un dominio para el staff: la ve domains.read y la cambia domains.manage.</summary>
[Authorize(Policy = StaffPermissions.DomainsRead)]
public sealed class DomainDnsModel(BillingDbContext db, DomainService domains) : DomainDnsPage(db, domains)
{
    public override string DomainUrl => string.Create(CultureInfo.InvariantCulture, $"/admin/dominios/{Domain?.Id}");

    public override bool CanChange => User.HasClaim(Claims.Permission, StaffPermissions.DomainsManage);

    protected override Task<RegisteredDomain?> Find(long id) => Db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
}
