using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>
/// Presupuestos de todas las cuentas: los abiertos primero, con filtro por estado (abiertos, vigentes, vencidos sin
/// respuesta, aceptados, rechazados o todos) y por cuenta.
/// </summary>
[Authorize(Policy = StaffPermissions.BillingRead)]
public sealed class QuotesModel(BillingDbContext db, IOptions<BillingOptions> options, TimeProvider time) : PageModel
{
    public sealed record Row(Quote Quote, string Account);

    public static IReadOnlyList<string> States { get; } = ["abiertos", "vigentes", "vencidos", "aceptados", "rechazados", "todos"];

    [BindProperty(SupportsGet = true, Name = "estado")]
    public string State { get; set; } = "abiertos";

    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public DateOnly Today { get; private set; }

    public async Task OnGet()
    {
        var today = Today = options.Value.Today(time);
        var query = db.Quotes.AsNoTracking();
        query = State switch
        {
            "aceptados" => query.Where(q => q.Status == QuoteStatus.Invoiced || q.Status == QuoteStatus.Ordered),
            "rechazados" => query.Where(q => q.Status == QuoteStatus.Declined),
            "vigentes" => query.Where(q => (q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent) && q.ValidUntil >= today),
            "vencidos" => query.Where(q => (q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent) && q.ValidUntil < today),
            "todos" => query,
            _ => query.Where(q => q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent),
        };
        if (AccountId is { } account)
        {
            query = query.Where(q => q.AccountId == account);
        }

        Rows = [.. (await query
            .Join(db.Accounts, q => q.AccountId, a => a.Id, (q, a) => new { Quote = q, a.Name })
            .OrderByDescending(x => x.Quote.Id)
            .Take(200)
            .ToListAsync()).Select(x => new Row(x.Quote, x.Name))];
    }
}
