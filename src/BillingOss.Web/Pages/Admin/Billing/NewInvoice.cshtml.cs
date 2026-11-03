using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>Factura manual con renglones propios: queda en borrador o se emite enseguida, con el saldo a favor aplicado.</summary>
[Authorize(Policy = StaffPermissions.BillingWrite)]
public sealed class NewInvoiceModel(BillingDbContext db, InvoiceService invoices, IOptions<BillingOptions> options, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    [BindProperty]
    public string? Currency { get; set; }

    [BindProperty]
    public DateOnly? DueDate { get; set; }

    [BindProperty]
    public List<LineRow> Lines { get; set; } = [];

    public Account? Account { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGet()
    {
        Lines = BillingForm.WithBlanks([]);
        if (await Load() is { } account)
        {
            Currency = account.Currency.Code;
            DueDate = options.Value.Today(time).AddDays(options.Value.InvoiceDueDays);
        }
    }

    public Task<IActionResult> OnPostDraft() => Save(draft: true);

    public Task<IActionResult> OnPostIssue() => Save(draft: false);

    private async Task<IActionResult> Save(bool draft)
    {
        try
        {
            if (AccountId is not { } accountId)
            {
                throw new ValidationException("Elegí la cuenta.");
            }

            var lines = BillingForm.Lines(Lines).Select(l => new LineInput(l.Description, l.Quantity, l.UnitPrice, l.Treatment)).ToList();
            var id = await invoices.CreateManual(new ManualInvoiceInput(accountId, lines, Currency, DueDate, draft), HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/facturas/{id}"));
        }
        catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
        {
            Error = ex.Message;
            Lines = BillingForm.WithBlanks(Lines);
            await Load();
            return Page();
        }
    }

    private async Task<Account?> Load() =>
        Account = AccountId is { } id ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id) : null;
}
