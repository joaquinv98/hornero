using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Pagar por transferencia (sección 4.6 del diseño de cobros): las cuentas del proveedor en la moneda, el importe
/// exacto, el número de factura y desde qué CUIT transferir, que es lo que identifica el pago en el extracto. "Ya
/// transferí" deja un aviso para el staff, con el comprobante si el cliente lo adjunta. El aviso no es un pago.
/// </summary>
public sealed class PayTransferModel(BillingDbContext db, PortalAccess access, TransferService transfers, BillingCore core) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long[] Factura { get; set; } = [];

    public IReadOnlyList<Document> Documents { get; private set; } = [];

    public IReadOnlyList<TransferAccount> Accounts { get; private set; } = [];

    /// <summary>El CUIT o CUIL de la ficha, con guiones, o null si la cuenta no lo tiene.</summary>
    public string? PayerCuit { get; private set; }

    public DateOnly Today { get; private set; }

    public bool Sent { get; private set; }

    /// <summary>Clave del texto del error, si lo hubo.</summary>
    public string? Error { get; private set; }

    public decimal Total => Documents.Sum(d => d.Pending);

    /// <summary>"30-71234567-8" para un CUIT de 11 números. Otro texto queda igual.</summary>
    public static string Cuit(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 11 && value.All(char.IsAsciiDigit) ? $"{value[..2]}-{value[2..10]}-{value[10..]}" : value;
    }

    public async Task<IActionResult> OnGet() => await Load() ? Page() : NotFound();

    public async Task<IActionResult> OnPost(DateOnly? transferredOn, string? reference, IFormFile? receipt)
    {
        if (!await Load())
        {
            return NotFound();
        }

        if (transferredOn is not { } on || on > Today || on < Today.AddDays(-90))
        {
            Error = "PayTransfer.BadDate";
            return Page();
        }

        NoticeFile? file = null;
        if (receipt is { Length: > 0 })
        {
            if (receipt.Length > NoticeFile.MaxBytes)
            {
                Error = "PayTransfer.BadReceipt";
                return Page();
            }

            using var buffer = new MemoryStream();
            await receipt.CopyToAsync(buffer, HttpContext.RequestAborted);
            file = new NoticeFile(receipt.FileName, buffer.ToArray());
        }

        try
        {
            await transfers.Notify(Documents[0].AccountId, [.. Documents.Select(d => d.Id)], on, reference, file, HttpContext.RequestAborted);
            Sent = true;
        }
        catch (ValidationException)
        {
            Error = await transfers.NoticesFull(Documents[0].AccountId, HttpContext.RequestAborted) ? "PayTransfer.TooMany"
                : file is null ? "Invoice.NotPayable" : "PayTransfer.BadReceipt";
        }

        return Page();
    }

    private async Task<bool> Load()
    {
        var ct = HttpContext.RequestAborted;
        Documents = await PayModel.Payable(db, access, User, Factura, ct);
        if (Documents.Count == 0 || Documents.Select(d => (d.AccountId, d.Currency)).Distinct().Count() > 1)
        {
            return false;
        }

        var currency = Documents[0].Currency;
        var accountId = Documents[0].AccountId;
        Accounts = await db.TransferAccounts.AsNoTracking().Where(a => a.Active && a.Currency == currency).OrderBy(a => a.Id).ToListAsync(ct);
        if (Accounts.Count == 0)
        {
            return false;
        }

        var tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == accountId && t.ValidTo == null, ct);
        PayerCuit = tax is { DocumentType: TaxDocumentType.Cuit or TaxDocumentType.Cuil } ? tax.DocumentNumber : null;
        Today = core.Today();
        return true;
    }
}
