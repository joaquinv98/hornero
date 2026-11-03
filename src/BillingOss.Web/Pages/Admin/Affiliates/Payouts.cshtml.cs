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

/// <summary>Cola de retiros pedidos: el staff transfiere y los marca pagados con la referencia, o los rechaza.</summary>
[Authorize(Policy = StaffPermissions.AffiliatesRead)]
public sealed class AffiliatePayoutsModel(BillingDbContext db, AffiliateService affiliates) : PageModel
{
    public sealed record Row(AffiliatePayout Payout, string Account, long AccountId);

    public IReadOnlyList<Row> Requested { get; private set; } = [];

    public IReadOnlyList<Row> Decided { get; private set; } = [];

    public string? Error { get; private set; }

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.AffiliatesManage);

    public async Task OnGet()
    {
        var rows = from p in db.Set<AffiliatePayout>().AsNoTracking()
                   join a in db.Set<Affiliate>() on p.AffiliateId equals a.Id
                   join c in db.Accounts on a.AccountId equals c.Id
                   select new { Payout = p, c.Name, a.AccountId };
        Requested = (await rows.Where(x => x.Payout.Status == PayoutStatus.Requested).OrderBy(x => x.Payout.Id).ToListAsync())
            .Select(x => new Row(x.Payout, x.Name, x.AccountId)).ToList();
        Decided = (await rows.Where(x => x.Payout.Status != PayoutStatus.Requested && x.Payout.Method == PayoutMethod.Withdrawal).OrderByDescending(x => x.Payout.Id).Take(50).ToListAsync())
            .Select(x => new Row(x.Payout, x.Name, x.AccountId)).ToList();
    }

    public Task<IActionResult> OnPostPaid(long id, string? reference) => Manage(() => affiliates.MarkPayoutPaid(id, reference ?? string.Empty));

    public Task<IActionResult> OnPostReject(long id, string? reason) => Manage(() => affiliates.RejectPayout(id, reason ?? string.Empty));

    private async Task<IActionResult> Manage(Func<Task> work)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await work();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await OnGet();
            return Page();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return Redirect("/admin/afiliados/retiros");
    }
}
