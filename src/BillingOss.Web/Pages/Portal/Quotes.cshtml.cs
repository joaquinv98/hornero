using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Billing;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>Los presupuestos que el proveedor le mandó a la sesión. Los borradores no se ven.</summary>
public sealed class QuotesModel(BillingDbContext db, PortalAccess access) : PageModel
{
    public IReadOnlyList<Quote> Quotes { get; private set; } = [];

    public async Task OnGet()
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Invoices);
        Quotes = await db.Quotes.AsNoTracking()
            .Where(q => accounts.Contains(q.AccountId) && q.Status != QuoteStatus.Draft)
            .OrderByDescending(q => q.Id)
            .Take(200)
            .ToListAsync();
    }
}
