using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// "Datos fiscales" del lado del cliente (sección 3.11 del diseño fiscal): la condición frente al IVA con las
/// descripciones de ARCA, la CUIT con "Completar con ARCA", el país, la provincia y el número de IVA del exterior con
/// su validación. Edita el titular o quien tiene la capacidad de facturación, y cada cambio queda en la auditoría.
/// </summary>
public sealed class TaxDataModel(BillingDbContext db, PortalAccess access, AccountService accounts, TaxSetupService taxes, FiscalSetupService fiscal, AuditLog audit, IStringLocalizer<Texts> texts) : PageModel
{
    public sealed record Row(Account Account, TaxIdentity? Tax);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public bool Rg2126Active { get; private set; }

    /// <summary>Lo que informó el padrón para una cuenta, a confirmar antes de guardarlo.</summary>
    public TaxpayerInfo? Registry { get; private set; }

    public long? RegistryAccount { get; private set; }

    public string? Error { get; private set; }

    public string? Done => Request.Query["listo"];

    /// <summary>
    /// Adónde sigue después del alta (diseno/ingreso-clientes.md, sección 2.1): el pedido que trajo a la persona. Guardar
    /// los datos lleva ahí, y también se puede seguir sin cargarlos, como consumidor final.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "volver")]
    public string? Continue { get; set; }

    /// <summary>Recién se registró: el formulario viene abierto.</summary>
    public bool New => Request.Query.ContainsKey("nuevo");

    /// <summary>Las condiciones que se ofrecen: la tabla de ARCA, con la 7 solo si hay percepción de la RG 2126 o la cuenta ya la tiene.</summary>
    public IEnumerable<VatConditions.Entry> Conditions(TaxIdentity? tax) =>
        VatConditions.Table.Where(c => c.Code != VatConditions.NoCategorizado || Rg2126Active || tax?.VatCondition == c.Code);

    public static bool CanValidateVat(TaxIdentity? tax) => tax is { VatNumber.Length: > 0 } t && AccountService.VatNumberCountries.Contains(t.Country);

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostTax(long accountId, TaxDocumentType documentType, string documentNumber, string legalName, int vatCondition, string country, string? province, string? vatNumber)
    {
        if (!await Allowed(accountId))
        {
            return NotFound();
        }

        return await Try(async () =>
        {
            var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
            var tax = new TaxIdentityInput(documentType, documentNumber.Trim().Replace("-", string.Empty, StringComparison.Ordinal), legalName.Trim(), vatCondition, country.Trim(), Blank(province), VatNumber: Blank(vatNumber));
            await accounts.Update(accountId, new AccountInput(account.Name, account.Email, account.Currency, account.Language, account.Phone, account.CompanyName, tax));
            await audit.Record("portal.tax_identity", $"account:{accountId}", new { vatCondition, country });
        }, "ficha");
    }

    public async Task<IActionResult> OnPostLookup(long accountId, string taxId)
    {
        if (!await Allowed(accountId))
        {
            return NotFound();
        }

        await Load();
        try
        {
            var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active) ?? throw new ValidationException(texts["Customer.NoIssuer"]);
            Registry = await fiscal.LookupTaxpayer(issuer.Id, taxId.Trim().Replace("-", string.Empty, StringComparison.Ordinal));
            RegistryAccount = accountId;
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostFill(long accountId, string taxId, int? confirmedCondition)
    {
        if (!await Allowed(accountId))
        {
            return NotFound();
        }

        return await Try(async () =>
        {
            var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active) ?? throw new ValidationException(texts["Customer.NoIssuer"]);
            var info = await fiscal.LookupTaxpayer(issuer.Id, taxId);
            await accounts.FillFromRegistry(accountId, info, confirmedCondition is > 0 ? confirmedCondition : null);
            await audit.Record("portal.tax_identity.registry", $"account:{accountId}", new { info.TaxId, confirmedCondition });
        }, "padron");
    }

    public async Task<IActionResult> OnPostValidateVat(long accountId)
    {
        if (!await Allowed(accountId))
        {
            return NotFound();
        }

        return await Try(async () =>
        {
            var check = await accounts.ValidateVatNumber(accountId);
            await audit.Record("portal.vat_number.validated", $"account:{accountId}", new { check.RequestId, check.Name });
        }, "iva");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Solo las cuentas que la sesión puede facturar: lo ajeno da 404, igual que lo que no existe.</summary>
    private Task<bool> Allowed(long accountId) => access.AccountIds(User, AccountCapabilities.Invoices).AnyAsync(a => a == accountId);

    private async Task<IActionResult> Try(Func<Task> work, string done)
    {
        try
        {
            await work();
            return Continue is { Length: > 0 } ? LocalRedirect(Ui.SafeReturn(Continue)) : Redirect($"/cuenta/datos-fiscales?listo={done}");
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load();
            return Page();
        }
    }

    private async Task Load()
    {
        var allowed = access.AccountIds(User, AccountCapabilities.Invoices);
        var list = await db.Accounts.AsNoTracking().Where(a => allowed.Contains(a.Id)).OrderBy(a => a.Id).ToListAsync();
        var ids = list.Select(a => a.Id).ToList();
        var identities = await db.TaxIdentities.AsNoTracking().Where(t => ids.Contains(t.AccountId) && t.ValidTo == null).ToListAsync();
        Rows = [.. list.Select(a => new Row(a, identities.FirstOrDefault(t => t.AccountId == a.Id)))];
        Rg2126Active = await taxes.Rg2126Active();
    }
}
