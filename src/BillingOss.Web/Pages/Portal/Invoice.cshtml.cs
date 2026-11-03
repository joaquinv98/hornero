using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Detalle de un documento. Con saldo lleva a pagar (/pagar, que también paga varias facturas juntas), dice si la factura
/// se va a debitar sola y con qué medio, y mientras un débito automático está en curso no ofrece otro medio (sección 4.5
/// del diseño de cobros). Una nota muestra el documento del que sale.
/// </summary>
public sealed class InvoiceModel(BillingDbContext db, PortalAccess access, BillingCore core, IOptions<PaymentsOptions> payments) : PageModel
{
    public sealed record ScheduledDebit(DateOnly Date, string Method);

    public Document? Document { get; private set; }

    public IReadOnlyList<FiscalDocument> Fiscal { get; private set; } = [];

    /// <summary>La factura o nota de débito de la que sale este documento, si es una nota.</summary>
    public Document? Related { get; private set; }

    public bool InFlight { get; private set; }

    /// <summary>Cuándo y con qué medio se va a debitar, si la factura se debita sola.</summary>
    public ScheduledDebit? Debit { get; private set; }

    public bool CanPay => Document is { Kind: not DocumentKind.CreditNote } d && d.Pending > 0m && d.Status != InvoiceStatus.Cancelled;

    public async Task<IActionResult> OnGet(long id)
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Invoices);
        Document = await db.Documents.AsNoTracking().Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.Id == id && d.IssueDate != null && accounts.Contains(d.AccountId));
        if (Document is null)
        {
            return NotFound();
        }

        Fiscal = await db.FiscalDocuments.AsNoTracking().Where(f => f.DocumentId == id).ToListAsync();
        Related = Document.RelatedDocumentId is { } related ? await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == related) : null;
        if (CanPay)
        {
            InFlight = await GatewayPayments.AutoChargeInFlight(db, id, HttpContext.RequestAborted);
            Debit = await AutoChargeService.Preview(db, Document, payments.Value.AutoCharge, core.Today(), HttpContext.RequestAborted) is { } debit
                ? new ScheduledDebit(debit.Date, AutoChargeService.Describe(debit.Method))
                : null;
        }

        return Page();
    }
}
