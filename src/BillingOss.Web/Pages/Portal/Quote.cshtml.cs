using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Un presupuesto para el cliente: renglones, total y vigencia, con su equivalente en la moneda local si está en otra.
/// Mandado y vigente se acepta o se rechaza. Aceptado pasa a factura o a pedido y la factura queda lista para pagar.
/// </summary>
public sealed class QuoteModel(BillingDbContext db, PortalAccess access, QuoteService quotes, ExchangeRateService rates, IOptions<BillingOptions> options, TimeProvider time) : PageModel
{
    public Quote? Quote { get; private set; }

    /// <summary>El total en la moneda local con la cotización vigente, si el presupuesto está en otra.</summary>
    public Conversion? Local { get; private set; }

    public bool CanAnswer => Quote is { Status: QuoteStatus.Sent } q && !q.IsExpired(options.Value.Today(time));

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGet(long id) => await Load(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAccept(long id)
    {
        try
        {
            var accepted = await quotes.Accept(id, await Accounts(), HttpContext.RequestAborted);
            var pending = accepted.DocumentId is { } doc && await db.Documents.AnyAsync(d => d.Id == doc && d.Total - d.Credited - d.Allocated > 0);
            return Redirect(accepted.DocumentId is not { } invoice ? string.Create(CultureInfo.InvariantCulture, $"/presupuestos/{id}")
                : pending ? string.Create(CultureInfo.InvariantCulture, $"/pagar?factura={invoice}")
                : string.Create(CultureInfo.InvariantCulture, $"/facturas/{invoice}"));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            return await Load(id) ? Page() : NotFound();
        }
    }

    public async Task<IActionResult> OnPostDecline(long id)
    {
        try
        {
            await quotes.Decline(id, await Accounts(), HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/presupuestos/{id}"));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            return await Load(id) ? Page() : NotFound();
        }
    }

    private async Task<List<long>> Accounts() => await access.AccountIds(User, AccountCapabilities.Invoices).ToListAsync();

    private async Task<bool> Load(long id)
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Invoices);
        Quote = await db.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id && accounts.Contains(q.AccountId) && q.Status != QuoteStatus.Draft);
        if (Quote is not null && Quote.Currency != rates.Local)
        {
            Local = await rates.Current(Quote.Currency, rates.Local);
        }

        return Quote is not null;
    }
}
