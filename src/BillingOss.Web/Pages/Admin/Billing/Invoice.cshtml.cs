using BillingOss.Web.Identity;
using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>
/// Una factura o nota con sus renglones e importes, y lo que el staff hace con ella: emitir o descartar un borrador
/// manual, pagarla con el saldo a favor de la cuenta o aplicarle el recargo por mora.
/// </summary>
[Authorize(Policy = StaffPermissions.BillingRead)]
public sealed class InvoiceModel(BillingDbContext db, InvoiceService invoices, DunningService dunning, IOptions<BillingOptions> options, TimeProvider time) : PageModel
{
    public Document? Document { get; private set; }

    public Account? Account { get; private set; }

    /// <summary>Notas que la corrigen o recargan, y el original si es una nota.</summary>
    public IReadOnlyList<Document> Related { get; private set; } = [];

    public decimal Credit { get; private set; }

    public string? Error { get; private set; }

    public bool CanWrite => this.Can(StaffPermissions.BillingWrite);

    /// <summary>El recargo se ofrece en una factura vencida que se debe, si hay porcentaje o fijo configurado.</summary>
    public bool CanChargeLateFee => Document is { Kind: DocumentKind.Invoice, IsIssued: true, Pending: > 0m } d && d.DueDate < options.Value.Today(time)
        && (options.Value.LateFeePercent > 0m || options.Value.LateFeeFixed.Count > 0);

    public async Task<IActionResult> OnGet(long id) => await Load(id) ? Page() : NotFound();

    public Task<IActionResult> OnPostIssue(long id) => Act(id, () => invoices.Issue(id, HttpContext.RequestAborted));

    public async Task<IActionResult> OnPostDiscard(long id)
    {
        if (!CanWrite)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await invoices.Discard(id, HttpContext.RequestAborted);
            return Redirect("/admin/facturas?estado=borradores");
        }
        catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
        {
            Error = ex.Message;
            return await Load(id) ? Page() : NotFound();
        }
    }

    public Task<IActionResult> OnPostCredit(long id) => Act(id, async () =>
    {
        var accountId = await db.Documents.Where(d => d.Id == id).Select(d => d.AccountId).SingleAsync();
        if ((await invoices.ApplyCredit(accountId, [id], HttpContext.RequestAborted)).Count == 0)
        {
            throw new ValidationException("No hay saldo a favor en la moneda del documento.");
        }
    });

    public Task<IActionResult> OnPostLateFee(long id) => Act(id, async () =>
    {
        if (await dunning.ChargeLateFee(id, HttpContext.RequestAborted) is null)
        {
            throw new ValidationException("No corresponde un recargo: la factura no venció, no se debe, ya tiene el suyo, la cuenta está exenta o el recargo da cero.");
        }
    });

    private async Task<IActionResult> Act(long id, Func<Task> work)
    {
        if (!CanWrite)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await work();
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/facturas/{id}"));
        }
        catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
        {
            Error = ex.Message;
            return await Load(id) ? Page() : NotFound();
        }
    }

    private async Task<bool> Load(long id)
    {
        Document = await db.Documents.AsNoTracking().Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id);
        if (Document is null)
        {
            return false;
        }

        Account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == Document.AccountId);
        Related = await db.Documents.AsNoTracking()
            .Where(d => d.IssueDate != null && (d.RelatedDocumentId == id || d.Id == Document.RelatedDocumentId))
            .OrderBy(d => d.Id).ToListAsync();
        var currency = Document.Currency;
        Credit = await db.CreditBalances.Where(b => b.AccountId == Document.AccountId && b.Currency == currency).SumAsync(b => b.Balance);
        return true;
    }
}
