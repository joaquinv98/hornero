using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Affiliates;

/// <summary>Afiliados, con los pendientes de aprobación primero, y los contadores de lo que espera al staff.</summary>
[Authorize(Policy = StaffPermissions.AffiliatesRead)]
public sealed class AffiliatesIndexModel(BillingDbContext db, AffiliateService affiliates) : PageModel
{
    public sealed record Row(Affiliate Affiliate, string Account, int Clicks, int Referrals, IReadOnlyList<AffiliateBalance> Balances);

    [BindProperty(SupportsGet = true, Name = "estado")]
    public AffiliateStatus? Status { get; set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public int Pending { get; private set; }

    public int RequestedPayouts { get; private set; }

    public string? Error { get; private set; }

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.AffiliatesManage);

    public async Task OnGet()
    {
        Pending = await db.Set<Affiliate>().CountAsync(a => a.Status == AffiliateStatus.Pending);
        RequestedPayouts = await db.Set<AffiliatePayout>().CountAsync(p => p.Status == PayoutStatus.Requested);
        var query = db.Set<Affiliate>().AsNoTracking();
        if (Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        var list = await (from a in query
                          join c in db.Accounts on a.AccountId equals c.Id
                          orderby a.Status == AffiliateStatus.Pending descending, a.Id descending
                          select new { Affiliate = a, c.Name }).Take(500).ToListAsync();
        var ids = list.Select(x => x.Affiliate.Id).ToList();
        var clicks = await db.Set<AffiliateClick>().Where(c => ids.Contains(c.AffiliateId)).GroupBy(c => c.AffiliateId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var referrals = await db.Set<AffiliateReferral>().Where(r => ids.Contains(r.AffiliateId)).GroupBy(r => r.AffiliateId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var balances = await AffiliateData.Balances(db, ids, HttpContext.RequestAborted);
        Rows = [.. list.Select(x => new Row(x.Affiliate, x.Name, clicks.GetValueOrDefault(x.Affiliate.Id), referrals.GetValueOrDefault(x.Affiliate.Id), balances.GetValueOrDefault(x.Affiliate.Id) ?? []))];
    }

    public Task<IActionResult> OnPostApprove(long id) => Decide(id, AffiliateStatus.Active);

    public Task<IActionResult> OnPostReject(long id) => Decide(id, AffiliateStatus.Rejected);

    private async Task<IActionResult> Decide(long id, AffiliateStatus status)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await affiliates.Decide(id, status);
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await OnGet();
            return Page();
        }

        return Redirect("/admin/afiliados");
    }
}
