using System.Globalization;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Services;
using BillingOss.Web.Content;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

public sealed class IndexModel(BillingDbContext db, PortalAccess access, KnownIssues knownIssues, BillingCore core) : PageModel
{
    public IReadOnlyList<(long Id, string Name)> Accounts { get; private set; } = [];

    public IReadOnlyList<Document> Unpaid { get; private set; } = [];

    public int ActiveServices { get; private set; }

    public IReadOnlyList<CreditBalance> Credit { get; private set; } = [];

    /// <summary>Si puede usar el soporte de alguna cuenta, y cuántas consultas tienen una respuesta para leer.</summary>
    public bool HasSupport { get; private set; }

    public int Answered { get; private set; }

    /// <summary>Los incidentes y mantenimientos de la red que tocan los productos de sus servicios.</summary>
    public IReadOnlyList<EventView> Issues { get; private set; } = [];

    /// <summary>Dominios en gracia o vencidos, y los que vencen en los próximos 30 días (D-19, el inicio por lo que pide acción).</summary>
    public IReadOnlyList<RegisteredDomain> DomainsNeedingAction { get; private set; } = [];

    public async Task OnGet()
    {
        var invoices = access.AccountIds(User, AccountCapabilities.Invoices);
        var services = access.AccountIds(User, AccountCapabilities.Services);
        var domains = access.AccountIds(User, AccountCapabilities.Domains);
        Accounts = (await db.Accounts.AsNoTracking().Where(a => invoices.Contains(a.Id) || services.Contains(a.Id) || domains.Contains(a.Id)).OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Name }).ToListAsync()).Select(a => (a.Id, a.Name)).ToList();
        Unpaid = (await db.Documents.AsNoTracking()
                .Where(d => invoices.Contains(d.AccountId) && d.IssueDate != null && d.Kind != DocumentKind.CreditNote)
                .OrderBy(d => d.DueDate).ToListAsync())
            .Where(d => d.Pending > 0m && d.Status != InvoiceStatus.Cancelled).ToList();
        ActiveServices = await db.Services.CountAsync(s => services.Contains(s.AccountId) && s.Status == ServiceStatus.Active);
        Credit = await db.CreditBalances.AsNoTracking().Where(c => invoices.Contains(c.AccountId) && c.Balance > 0m).ToListAsync();
        var support = access.AccountIds(User, AccountCapabilities.Support);
        HasSupport = await support.AnyAsync();
        Answered = await db.Set<BillingOss.App.Support.Ticket>()
            .CountAsync(t => t.AccountId != null && support.Contains(t.AccountId.Value) && t.Status == BillingOss.App.Support.TicketStatus.AwaitingCustomer);
        Issues = await knownIssues.For(User, CultureInfo.CurrentUICulture.Name, HttpContext.RequestAborted);
        var soon = core.Today().AddDays(30);
        DomainsNeedingAction = await db.Domains.AsNoTracking()
            .Where(d => domains.Contains(d.AccountId) && (d.Status == DomainStatus.Grace || d.Status == DomainStatus.Expired || (d.Status == DomainStatus.Active && (d.ExpiresOn ?? d.PaidThrough) <= soon)))
            .OrderBy(d => d.ExpiresOn ?? d.PaidThrough).Take(10).ToListAsync();
    }
}
