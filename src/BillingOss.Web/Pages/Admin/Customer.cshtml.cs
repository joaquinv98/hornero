using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Usage;
using BillingOss.Web.Identity;
using BillingOss.Web.Pages.Admin.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Una cuenta con sus usuarios, servicios, documentos, pagos y trámites, el botón de "ver como cliente" y los datos
/// fiscales (sección 3.11 del diseño fiscal): condición frente al IVA, "Completar con ARCA", número de IVA validado,
/// marca de empresa grande y reglas de cuenta con su constancia. Lo fiscal lo edita quien tiene taxes.edit. Con
/// billing.read muestra también el saldo a favor con su libro, los presupuestos abiertos y los ítems pendientes, y con
/// billing.write las exenciones de recargo y de recordatorios. El ajuste de saldo pide credit.adjust.
/// </summary>
[Authorize(Policy = StaffPermissions.AccountsRead)]
public sealed class CustomerModel(BillingDbContext db, UserManager<StaffUser> staff, AuditLog audit, InvoiceService invoices, AccountService accounts, TaxSetupService taxes, FiscalSetupService fiscal, BillingCore core, IStringLocalizer<Texts> texts) : PageModel
{
    public sealed record ServiceRow(Service Service, string Product);

    public sealed record UserRow(string Email, bool IsOwner, string[] Capabilities);

    public Account? Account { get; private set; }

    public TaxIdentity? Tax { get; private set; }

    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    public IReadOnlyList<ServiceRow> Services { get; private set; } = [];

    public IReadOnlyList<Document> Documents { get; private set; } = [];

    public IReadOnlyList<Payment> Payments { get; private set; } = [];

    public IReadOnlyList<CancellationRequest> Requests { get; private set; } = [];

    /// <summary>Productos que miden uso: sus servicios llevan el enlace al uso.</summary>
    public IReadOnlySet<long> Metered { get; private set; } = new HashSet<long>();

    public IReadOnlyList<RegisteredDomain> Domains { get; private set; } = [];

    public bool CanSeeDomains => User.HasClaim(Claims.Permission, StaffPermissions.DomainsRead);

    public IReadOnlyList<TaxRule> AccountRules { get; private set; } = [];

    public IReadOnlyList<Domain.Catalog.Tax> Taxes { get; private set; } = [];

    /// <summary>Lo que informó el padrón, a confirmar antes de guardarlo en la ficha.</summary>
    public TaxpayerInfo? Registry { get; private set; }

    public bool CanImpersonate => User.HasClaim(Claims.Permission, StaffPermissions.Impersonate);

    /// <summary>Las últimas consultas de la cuenta, para quien atiende el soporte. Todas están en la solapa Tickets.</summary>
    public IReadOnlyList<BillingOss.App.Support.Ticket> Tickets { get; private set; } = [];

    public bool CanSupport => User.HasClaim(Claims.Permission, StaffPermissions.SupportTickets);

    public bool CanSeeBilling => this.Can(StaffPermissions.BillingRead);

    public bool CanWriteBilling => this.Can(StaffPermissions.BillingWrite);

    public bool CanAdjustCredit => this.Can(StaffPermissions.CreditAdjust);

    /// <summary>Saldo a favor por moneda y los últimos movimientos del libro.</summary>
    public IReadOnlyList<CreditBalance> Balances { get; private set; } = [];

    public IReadOnlyList<CreditEntry> Entries { get; private set; } = [];

    public int OpenQuotes { get; private set; }

    public int PendingItems { get; private set; }

    [BindProperty]
    public string? AdjustCurrency { get; set; }

    [BindProperty]
    public string? AdjustAmount { get; set; }

    [BindProperty]
    public string? AdjustReason { get; set; }

    [BindProperty]
    public bool NoLateFees { get; set; }

    [BindProperty]
    public bool NoReminders { get; set; }

    public bool CanChangeServices => User.HasClaim(Claims.Permission, StaffPermissions.ServicesChange);

    public bool CanPlaceOrders => User.HasClaim(Claims.Permission, StaffPermissions.OrdersWrite);

    public bool CanEditTaxes => User.HasClaim(Claims.Permission, StaffPermissions.TaxesEdit);

    public bool CanValidateVat => Tax is { VatNumber.Length: > 0 } t && AccountService.VatNumberCountries.Contains(t.Country);

    /// <summary>Hoy rige la percepción de la RG 2126: sin ella la condición 7 no se ofrece (sección 3.8 del diseño fiscal).</summary>
    public bool Rg2126Active { get; private set; }

    /// <summary>Las condiciones que la ficha ofrece: la tabla de ARCA, con la 7 solo si hay percepción o la cuenta ya la tiene.</summary>
    public IEnumerable<VatConditions.Entry> Conditions => VatConditions.Table.Where(c => c.Code != VatConditions.NoCategorizado || Rg2126Active || Tax?.VatCondition == c.Code);

    public DateOnly Today => core.Today();

    public string? Error { get; private set; }

    public string? Done => Request.Query["listo"];

    public async Task<IActionResult> OnGet(long id) => await Load(id) ? Page() : NotFound();

    /// <summary>Ajuste del saldo a favor: suma o resta, con motivo, en el libro y en la auditoría.</summary>
    public async Task<IActionResult> OnPostAdjust(long id)
    {
        if (!CanAdjustCredit)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await invoices.AdjustCredit(id, AdjustCurrency ?? string.Empty, BillingForm.Number(AdjustAmount, "Importe"), AdjustReason ?? string.Empty, HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/cuentas/{id}#saldo"));
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            return await Load(id) ? Page() : NotFound();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    public async Task<IActionResult> OnPostExemptions(long id)
    {
        if (!CanWriteBilling)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await invoices.SetExemptions(id, NoLateFees, NoReminders, HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/cuentas/{id}#facturacion"));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>"Ver como cliente": queda en la auditoría y el portal lo muestra con un aviso en todas las páginas.</summary>
    public async Task<IActionResult> OnPostImpersonate(long id)
    {
        if (!CanImpersonate)
        {
            // Con el esquema del staff: sin esquema por defecto, Forbid() a secas da un 500.
            return Forbid(Schemes.Staff);
        }

        var user = await staff.GetUserAsync(User);
        if (user is null || !await db.Accounts.AnyAsync(a => a.Id == id))
        {
            return NotFound();
        }

        await audit.Record("portal.impersonate", string.Create(CultureInfo.InvariantCulture, $"account:{id}"));
        await Sessions.SignInAsAccount(HttpContext, id, user);
        return Redirect("/");
    }

    /// <summary>Cambia la ficha fiscal: una versión nueva de la identidad, con la condición solo de la tabla de ARCA.</summary>
    public async Task<IActionResult> OnPostTax(long id, TaxDocumentType documentType, string documentNumber, string legalName, int vatCondition, string country, string? province, string? vatNumber)
    {
        if (!CanEditTaxes)
        {
            return Forbid();
        }

        return await Try(id, async () =>
        {
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id) ?? throw new ValidationException("No existe la cuenta.");
            var tax = new TaxIdentityInput(documentType, documentNumber.Trim().Replace("-", string.Empty, StringComparison.Ordinal), legalName.Trim(), vatCondition, country.Trim().ToUpperInvariant(), Blank(province)?.ToUpperInvariant(), VatNumber: Blank(vatNumber));
            await accounts.Update(id, new AccountInput(account.Name, account.Email, account.Currency, account.Language, account.Phone, account.CompanyName, tax));
            await audit.Record("account.tax_identity", $"account:{id}", new { vatCondition, country });
        }, "ficha");
    }

    /// <summary>Consulta el padrón y muestra lo que informó, para confirmar la condición antes de guardar.</summary>
    public async Task<IActionResult> OnPostLookup(long id, string taxId)
    {
        if (!CanEditTaxes)
        {
            return Forbid();
        }

        if (!await Load(id))
        {
            return NotFound();
        }

        try
        {
            var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active) ?? throw new ValidationException(texts["Customer.NoIssuer"]);
            Registry = await fiscal.LookupTaxpayer(issuer.Id, taxId.Trim().Replace("-", string.Empty, StringComparison.Ordinal));
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    /// <summary>Guarda lo del padrón con la condición confirmada por el staff: fuente arca y fecha.</summary>
    public async Task<IActionResult> OnPostFill(long id, string taxId, int? confirmedCondition)
    {
        if (!CanEditTaxes)
        {
            return Forbid();
        }

        return await Try(id, async () =>
        {
            var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active) ?? throw new ValidationException(texts["Customer.NoIssuer"]);
            var info = await fiscal.LookupTaxpayer(issuer.Id, taxId);
            await accounts.FillFromRegistry(id, info, confirmedCondition is > 0 ? confirmedCondition : null);
            await audit.Record("account.tax_identity.registry", $"account:{id}", new { info.TaxId, confirmedCondition });
        }, "padron");
    }

    public async Task<IActionResult> OnPostValidateVat(long id)
    {
        if (!CanEditTaxes)
        {
            return Forbid();
        }

        return await Try(id, async () =>
        {
            var check = await accounts.ValidateVatNumber(id);
            await audit.Record("account.vat_number.validated", $"account:{id}", new { check.RequestId, check.Name });
        }, "iva");
    }

    public async Task<IActionResult> OnPostLargeCompany(long id, bool largeCompany)
    {
        if (!CanEditTaxes)
        {
            return Forbid();
        }

        return await Try(id, async () =>
        {
            await accounts.SetLargeCompany(id, largeCompany);
            await audit.Record("account.large_company", $"account:{id}", new { largeCompany });
        }, "grande");
    }

    public async Task<IActionResult> OnPostRule(long id, string taxCode, TaxTreatment outcome, string? ratePercent, string reason, string? certificateRef, DateOnly validFrom, DateOnly? validTo, string? note)
    {
        if (!CanEditTaxes)
        {
            return Forbid();
        }

        var rate = outcome == TaxTreatment.Taxed ? Ui.ParseDecimal(ratePercent) : 0m;
        if (rate is null)
        {
            Error = texts["Taxes.RateInvalid"];
            return await Load(id) ? Page() : NotFound();
        }

        return await Try(id, () => taxes.AddAccountRule(new AccountRuleInput(id, taxCode, outcome, rate.Value / 100m, reason, certificateRef, validFrom, validTo, null, Blank(note))), "regla");
    }

    public async Task<IActionResult> OnPostCloseRule(long id, long ruleId)
    {
        if (!CanEditTaxes)
        {
            return Forbid();
        }

        return await Try(id, async () =>
        {
            if (!await db.TaxRules.AnyAsync(r => r.Id == ruleId && r.AccountId == id))
            {
                throw new ValidationException("La regla no es de esta cuenta.");
            }

            await taxes.CloseRule(ruleId, Today);
        }, "cierre");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<IActionResult> Try(long id, Func<Task> work, string done)
    {
        try
        {
            await work();
            return Redirect($"/admin/cuentas/{id}?listo={done}#fiscal");
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            return await Load(id) ? Page() : NotFound();
        }
    }

    private async Task<bool> Load(long id)
    {
        Account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
        if (Account is null)
        {
            return false;
        }

        (NoLateFees, NoReminders) = (Account.NoLateFees, Account.NoReminders);
        if (CanSeeBilling)
        {
            Balances = await db.CreditBalances.AsNoTracking().Where(b => b.AccountId == id).OrderBy(b => b.Currency).ToListAsync();
            Entries = await db.CreditEntries.AsNoTracking().Where(e => e.AccountId == id).OrderByDescending(e => e.Id).Take(20).ToListAsync();
            OpenQuotes = await db.Quotes.CountAsync(q => q.AccountId == id && (q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent));
            PendingItems = await db.BillableItems.CountAsync(i => i.AccountId == id && i.Status == BillableItemStatus.Pending);
        }

        Tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == id && t.ValidTo == null);
        Users = await (from m in db.Set<AccountUser>().AsNoTracking()
                       join u in db.Set<PortalUser>() on m.UserId equals u.Id
                       where m.AccountId == id
                       orderby m.IsOwner descending, u.Email
                       select new UserRow(u.Email, m.IsOwner, m.Capabilities)).ToListAsync();
        Services = await (from s in db.Services.AsNoTracking()
                          join p in db.Products on s.ProductId equals p.Id
                          where s.AccountId == id
                          orderby s.Id
                          select new ServiceRow(s, p.Name)).ToListAsync();
        Metered = (await db.Set<UsageMetric>().Where(m => m.Active).Select(m => m.ProductId).Distinct().ToListAsync()).ToHashSet();
        Documents = await db.Documents.AsNoTracking().Where(d => d.AccountId == id && d.IssueDate != null)
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Id).Take(200).ToListAsync();
        Payments = await db.Payments.AsNoTracking().Where(p => p.AccountId == id).OrderByDescending(p => p.Id).Take(200).ToListAsync();
        Domains = await db.Domains.AsNoTracking().Where(d => d.AccountId == id).OrderBy(d => d.Name).ToListAsync();
        Requests = await db.Set<CancellationRequest>().AsNoTracking().Where(r => r.Receipt != null && r.AccountIds.Contains(id))
            .OrderByDescending(r => r.Id).ToListAsync();
        AccountRules = await db.TaxRules.AsNoTracking().Where(r => r.AccountId == id).OrderByDescending(r => r.ValidFrom).ThenByDescending(r => r.Id).ToListAsync();
        Taxes = await db.Taxes.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        Rg2126Active = await taxes.Rg2126Active();
        if (CanSupport)
        {
            Tickets = await db.Set<BillingOss.App.Support.Ticket>().AsNoTracking().Where(t => t.AccountId == id && t.MergedIntoId == null)
                .OrderByDescending(t => t.LastMessageAt).Take(10).ToListAsync();
        }

        return true;
    }
}
