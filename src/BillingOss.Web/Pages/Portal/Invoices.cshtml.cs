using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>Las facturas y notas de la sesión, con lo que se debe por moneda y "Pagar todo" (D-19, 4.6).</summary>
public sealed class InvoicesModel(BillingDbContext db, PortalAccess access) : PageModel
{
    public sealed record Owed(long AccountId, Currency Currency, decimal Amount, int Count);

    public IReadOnlyList<Document> Documents { get; private set; } = [];

    public IReadOnlyList<Owed> Due { get; private set; } = [];

    public async Task OnGet()
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Invoices);
        Documents = await db.Documents.AsNoTracking()
            .Where(d => accounts.Contains(d.AccountId) && d.IssueDate != null)
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Id)
            .Take(500)
            .ToListAsync();
        Due = (await db.Documents.AsNoTracking()
                .Where(d => accounts.Contains(d.AccountId) && d.IssueDate != null && d.Kind != DocumentKind.CreditNote && d.Total - d.Credited - d.Allocated > 0)
                .Select(d => new { d.AccountId, d.Currency, Pending = d.Total - d.Credited - d.Allocated })
                .ToListAsync())
            .GroupBy(d => (d.AccountId, d.Currency)).OrderBy(g => g.Key.AccountId).ThenBy(g => g.Key.Currency.Code)
            .Select(g => new Owed(g.Key.AccountId, g.Key.Currency, g.Sum(d => d.Pending), g.Count())).ToList();
    }
}
