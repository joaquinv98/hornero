using BillingOss.Web.Identity;
using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>
/// Un presupuesto: se arma y se edita mientras está abierto, se manda al cliente, se convierte en factura o en pedido y
/// se rechaza. Cerrado se muestra con la factura o el pedido que generó.
/// </summary>
[Authorize(Policy = StaffPermissions.BillingRead)]
public sealed class QuoteModel(BillingDbContext db, QuoteService quotes, IStringLocalizer<Texts> texts) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    [BindProperty]
    public string? Subject { get; set; }

    [BindProperty]
    public string? Currency { get; set; }

    [BindProperty]
    public DateOnly? ValidUntil { get; set; }

    [BindProperty]
    public string? Notes { get; set; }

    [BindProperty]
    public List<LineRow> Lines { get; set; } = [];

    public Quote? Quote { get; private set; }

    public Account? Account { get; private set; }

    public IReadOnlyList<PlanChoice> Plans { get; private set; } = [];

    public string? Error { get; private set; }

    public bool CanWrite => this.Can(StaffPermissions.BillingWrite);

    public async Task<IActionResult> OnGet(string id)
    {
        if (!TryId(id, out var quoteId))
        {
            return NotFound();
        }

        if (quoteId is null)
        {
            Lines = BillingForm.WithBlanks([]);
            await Load(null);
            Currency = Account?.Currency.Code;
            return CanWrite ? Page() : Forbid(Schemes.Staff);
        }

        if (!await Load(quoteId))
        {
            return NotFound();
        }

        var q = Quote!;
        (Subject, Currency, ValidUntil, Notes) = (q.Subject, q.Currency.Code, q.ValidUntil, q.Notes);
        Lines = BillingForm.WithBlanks(q.Lines.Select(LineRow.From));
        return Page();
    }

    public async Task<IActionResult> OnPostSave(string id)
    {
        if (!TryId(id, out var quoteId))
        {
            return NotFound();
        }

        return await Act(quoteId, async () =>
        {
            if (AccountId is not { } accountId)
            {
                throw new ValidationException("Elegí la cuenta.");
            }

            var input = new QuoteInput(accountId, Subject ?? string.Empty, BillingForm.Lines(Lines), ValidUntil, Currency, Notes);
            if (quoteId is { } existing)
            {
                await quotes.Update(existing, input, HttpContext.RequestAborted);
                return existing;
            }

            return await quotes.Create(input, HttpContext.RequestAborted);
        });
    }

    public Task<IActionResult> OnPostSend(long id) => Act(id, async () =>
    {
        await quotes.Send(id, HttpContext.RequestAborted);
        return id;
    });

    public Task<IActionResult> OnPostInvoice(long id) => Act(id, async () => (await quotes.Convert(id, QuoteTarget.Invoice, HttpContext.RequestAborted)).QuoteId);

    public Task<IActionResult> OnPostOrder(long id) => Act(id, async () => (await quotes.Convert(id, QuoteTarget.Order, HttpContext.RequestAborted)).QuoteId);

    public Task<IActionResult> OnPostDuplicate(long id) => Act(id, () => quotes.Duplicate(id, HttpContext.RequestAborted));

    public Task<IActionResult> OnPostDecline(long id) => Act(id, async () =>
    {
        await quotes.Decline(id, ct: HttpContext.RequestAborted);
        return id;
    });

    public async Task<IActionResult> OnPostDelete(long id)
    {
        if (!CanWrite)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await quotes.Delete(id, HttpContext.RequestAborted);
            return Redirect("/admin/presupuestos");
        }
        catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
        {
            return await Failed(id, ex);
        }
    }

    /// <summary>"nuevo" es un alta y un número, un presupuesto que ya existe.</summary>
    private static bool TryId(string id, out long? value)
    {
        value = null;
        if (id == "nuevo")
        {
            return true;
        }

        if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            value = n;
            return true;
        }

        return false;
    }

    private async Task<IActionResult> Act(long? quoteId, Func<Task<long>> work)
    {
        if (!CanWrite)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            var id = await work();
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/presupuestos/{id}"));
        }
        catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
        {
            return await Failed(quoteId, ex);
        }
    }

    /// <summary>Vuelve a mostrar el formulario con lo que se cargó y el error, sin perder lo escrito.</summary>
    private async Task<IActionResult> Failed(long? quoteId, Exception ex)
    {
        Error = ex.Message;
        if (!await Load(quoteId))
        {
            return NotFound();
        }

        if (Lines.Count == 0 && Quote is { } q)
        {
            (Subject, Currency, ValidUntil, Notes) = (q.Subject, q.Currency.Code, q.ValidUntil, q.Notes);
            Lines = [.. q.Lines.Select(LineRow.From)];
        }

        Lines = BillingForm.WithBlanks(Lines);
        return Page();
    }

    private async Task<bool> Load(long? quoteId)
    {
        if (quoteId is { } id)
        {
            Quote = await db.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id);
            if (Quote is null)
            {
                return false;
            }

            AccountId = Quote.AccountId;
        }

        Account = AccountId is { } accountId ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId) : null;
        Plans = [.. (await db.Plans.AsNoTracking().Where(p => p.Active)
                .Join(db.Products, p => p.ProductId, p => p.Id, (plan, product) => new { plan.Id, plan.CycleUnit, plan.CycleCount, product.Name })
                .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync())
            .Select(x => new PlanChoice(x.Id, x.CycleUnit is CycleUnit.Month or CycleUnit.Year
                ? string.Create(CultureInfo.CurrentCulture, $"{x.Name} ({x.CycleCount} {texts["CycleUnit." + x.CycleUnit]})")
                : $"{x.Name} ({texts["CycleUnit." + x.CycleUnit]})"))];
        return true;
    }
}
