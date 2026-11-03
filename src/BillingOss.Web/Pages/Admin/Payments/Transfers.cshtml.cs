using System.Text;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Payments;

/// <summary>
/// Facturación, Pagos sin imputar (sección 10.2 del diseño de interfaz y 4.6 del de cobros): importación del extracto,
/// la bandeja de movimientos sin identificar con la imputación sugerida por el CUIT del ordenante y los avisos de "Ya
/// transferí" con su comprobante. Las cuentas para transferir se configuran en TransferAccountsModel.
/// </summary>
[Authorize(Policy = StaffPermissions.PaymentsManage)]
public sealed class TransfersModel(BillingDbContext db, TransferService transfers) : PageModel
{
    public sealed record MovementRow(BankMovement Movement, string Bank, string? AccountName, IReadOnlyList<(long Id, long? Number, decimal Pending)> Suggested);

    public sealed record NoticeRow(long Id, long AccountId, string AccountName, decimal Amount, Currency Currency, DateOnly TransferredOn, string? Reference, string? ReceiptName, IReadOnlyList<long> DocumentIds);

    public IReadOnlyList<TransferAccount> BankAccounts { get; private set; } = [];

    public IReadOnlyList<MovementRow> Tray { get; private set; } = [];

    public IReadOnlyList<NoticeRow> Notices { get; private set; } = [];

    public StatementImport? Imported { get; private set; }

    /// <summary>Clave del texto de lo que se acaba de hacer.</summary>
    public string? Done { get; private set; }

    /// <summary>Mensaje del error, ya armado por el caso de uso.</summary>
    public string? Error { get; private set; }

    public async Task OnGet(string? hecho)
    {
        Done = hecho switch
        {
            "imputado" => "Transfers.Applied",
            "ignorado" => "Transfers.Ignored",
            "cerrado" => "Transfers.NoticeClosed",
            _ => null,
        };
        await Load();
    }

    public async Task<IActionResult> OnPostImport(long account, IFormFile? statement)
    {
        if (statement is not { Length: > 0 })
        {
            Error = "Transfers.NoFile";
        }
        else
        {
            try
            {
                using var reader = new StreamReader(statement.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                Imported = await transfers.Import(account, await reader.ReadToEndAsync(HttpContext.RequestAborted), HttpContext.RequestAborted);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ValidationException ex)
            {
                Error = ex.Message;
            }
        }

        await Load();
        return Page();
    }

    /// <summary>Imputa un movimiento: a lo sugerido, a las facturas impagas más viejas o todo a saldo a favor.</summary>
    public async Task<IActionResult> OnPostApply(long movement, long? account, string? mode)
    {
        var current = await db.BankMovements.AsNoTracking().SingleOrDefaultAsync(m => m.Id == movement, HttpContext.RequestAborted);
        if (current is null)
        {
            return NotFound();
        }

        var target = account ?? current.AccountId;
        if (target is not { } accountId)
        {
            Error = "Transfers.AccountRequired";
            await Load();
            return Page();
        }

        IReadOnlyList<long>? documents = mode switch
        {
            "sugerido" when target == current.AccountId && current.SuggestedDocumentIds.Length > 0 => current.SuggestedDocumentIds,
            "saldo" => [],
            _ => null,
        };
        try
        {
            await transfers.Apply(movement, accountId, documents, HttpContext.RequestAborted);
            return RedirectToPage(new { hecho = "imputado" });
        }
        catch (KeyNotFoundException)
        {
            Error = "Transfers.AccountMissing";
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostIgnore(long movement)
    {
        try
        {
            await transfers.Ignore(movement, HttpContext.RequestAborted);
            return RedirectToPage(new { hecho = "ignorado" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostResolve(long notice)
    {
        try
        {
            await transfers.ResolveNotice(notice, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return RedirectToPage(new { hecho = "cerrado" });
    }

    /// <summary>El comprobante que adjuntó el cliente, como descarga.</summary>
    public async Task<IActionResult> OnGetReceipt(long notice)
    {
        var file = await db.TransferNotices.AsNoTracking().Where(n => n.Id == notice && n.Receipt != null)
            .Select(n => new { n.Receipt, n.ReceiptType, n.ReceiptName }).SingleOrDefaultAsync(HttpContext.RequestAborted);
        return file is null ? NotFound() : File(file.Receipt!, file.ReceiptType ?? "application/octet-stream", file.ReceiptName ?? "comprobante");
    }

    private async Task Load()
    {
        var ct = HttpContext.RequestAborted;
        BankAccounts = await db.TransferAccounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);

        var movements = await db.BankMovements.AsNoTracking().Where(m => m.Status == MovementStatus.Unmatched)
            .OrderBy(m => m.Date).ThenBy(m => m.Id).Take(200).ToListAsync(ct);
        var accountIds = movements.Select(m => m.AccountId).OfType<long>().ToList();
        var documentIds = movements.SelectMany(m => m.SuggestedDocumentIds).Distinct().ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var documents = await db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        Tray = [.. movements.Select(m => new MovementRow(
            m,
            BankAccounts.FirstOrDefault(a => a.Id == m.TransferAccountId)?.Bank ?? string.Empty,
            m.AccountId is { } a && names.TryGetValue(a, out var name) ? name : null,
            [.. m.SuggestedDocumentIds.Where(documents.ContainsKey).Select(d => (d, documents[d].Number, documents[d].Pending))]))];

        var notices = await db.TransferNotices.AsNoTracking().Where(n => n.Status == NoticeStatus.Open).OrderBy(n => n.Id).Take(200)
            .Select(n => new { n.Id, n.AccountId, n.Amount, n.Currency, n.TransferredOn, n.Reference, n.ReceiptName, n.DocumentIds }).ToListAsync(ct);
        var noticeAccounts = notices.Select(n => n.AccountId).Distinct().ToList();
        var noticeNames = await db.Accounts.AsNoTracking().Where(a => noticeAccounts.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        Notices = [.. notices.Select(n => new NoticeRow(n.Id, n.AccountId, noticeNames.GetValueOrDefault(n.AccountId, string.Empty), n.Amount, n.Currency, n.TransferredOn, n.Reference, n.ReceiptName, n.DocumentIds))];
    }
}
