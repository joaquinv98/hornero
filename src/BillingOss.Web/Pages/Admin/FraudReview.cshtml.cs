using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Cola de pedidos retenidos por la evaluación de fraude, con su puntaje y sus motivos, y la decisión del staff
/// (diseno/fraude-y-acceso.md, sección 3.2).
/// </summary>
[Authorize(Policy = StaffPermissions.OrdersReview)]
public sealed class FraudReviewModel(BillingDbContext db, FraudReviewService review) : PageModel
{
    public sealed record Row(FraudCheck Check, DateTimeOffset PlacedAt, long AccountId, string AccountName, string AccountEmail, Currency Currency, decimal? Total, decimal? Paid);

    [BindProperty]
    public string? Note { get; set; }

    public IReadOnlyList<Row> Holding { get; private set; } = [];

    public IReadOnlyList<Row> Decided { get; private set; } = [];

    public FraudDecision? Result { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGet()
    {
        var rows = from c in db.Set<FraudCheck>().AsNoTracking()
                   join o in db.Orders on c.OrderId equals o.Id
                   join a in db.Accounts on c.AccountId equals a.Id
                   select new { c, o.PlacedAt, a.Id, a.Name, a.Email, o.Currency };
        var holding = await rows.Where(r => r.c.Status == ScreeningStatus.Pending || r.c.Status == ScreeningStatus.Review).OrderBy(r => r.c.CreatedAt).Take(500).ToListAsync();
        var decided = await rows.Where(r => r.c.DecidedAt != null).OrderByDescending(r => r.c.DecidedAt).Take(20).ToListAsync();

        // Con todas las facturas de cada pedido: puede tener una por modo de carga.
        var amounts = await review.Amounts([.. holding.Concat(decided).Select(r => r.c.OrderId).Distinct()], HttpContext.RequestAborted);
        Row ToRow(long orderId, FraudCheck c, DateTimeOffset placedAt, long id, string name, string email, Currency currency) =>
            amounts.TryGetValue(orderId, out var a) ? new Row(c, placedAt, id, name, email, currency, a.Total, a.Paid) : new Row(c, placedAt, id, name, email, currency, null, null);
        Holding = [.. holding.Select(r => ToRow(r.c.OrderId, r.c, r.PlacedAt, r.Id, r.Name, r.Email, r.Currency))];
        Decided = [.. decided.Select(r => ToRow(r.c.OrderId, r.c, r.PlacedAt, r.Id, r.Name, r.Email, r.Currency))];
    }

    public Task<IActionResult> OnPostApprove(long id) => Decide(id, approve: true);

    public Task<IActionResult> OnPostReject(long id) => Decide(id, approve: false);

    private async Task<IActionResult> Decide(long orderId, bool approve)
    {
        try
        {
            Result = await review.Decide(orderId, approve, Note, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            Error = "Review.AlreadyDecided";
        }

        await OnGet();
        return Page();
    }
}
