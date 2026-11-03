using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Payments;

/// <summary>
/// Configuración, Pasarelas de pago, Cuentas para transferir (sección 4.6 del diseño de cobros): banco, titular, CUIT,
/// CBU o CVU, alias, moneda y el mapa de columnas del extracto de cada cuenta.
/// </summary>
[Authorize(Policy = StaffPermissions.PaymentsManage)]
public sealed class TransferAccountsModel(BillingDbContext db, TransferService transfers) : PageModel
{
    public IReadOnlyList<TransferAccount> BankAccounts { get; private set; } = [];

    /// <summary>La cuenta que se edita, o null para una nueva.</summary>
    public TransferAccount? Editing { get; private set; }

    public bool Saved { get; private set; }

    /// <summary>Mensaje del error, ya armado por el caso de uso.</summary>
    public string? Error { get; private set; }

    /// <summary>El mapa de columnas por defecto, como ejemplo para el formulario.</summary>
    public static string DefaultMap { get; } = new StatementMap().ToJson();

    public async Task OnGet(long? cuenta, string? hecho)
    {
        Saved = hecho == "cuenta";
        await Load(cuenta);
    }

    public async Task<IActionResult> OnPost(long? id, string? bank, string? holder, string? cuit, string? cbu, string? alias, string? currency, bool active, string? csvMap)
    {
        try
        {
            await transfers.SaveAccount(id, new TransferAccountInput(bank ?? string.Empty, holder ?? string.Empty, cuit ?? string.Empty, cbu ?? string.Empty, alias, (currency ?? string.Empty).Trim().ToUpperInvariant(), active, csvMap), HttpContext.RequestAborted);
            return RedirectToPage(new { hecho = "cuenta" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        await Load(id);
        return Page();
    }

    private async Task Load(long? editing)
    {
        BankAccounts = await db.TransferAccounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(HttpContext.RequestAborted);
        Editing = editing is { } id ? BankAccounts.FirstOrDefault(a => a.Id == id) : null;
    }
}
