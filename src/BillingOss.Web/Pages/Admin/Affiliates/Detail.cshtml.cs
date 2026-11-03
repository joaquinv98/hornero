using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Affiliates;

/// <summary>Ficha de un afiliado: estado, reglas propias, referidos, comisiones, saldo y pagos, con sus acciones.</summary>
[Authorize(Policy = StaffPermissions.AffiliatesRead)]
public sealed class AffiliateDetailModel(BillingDbContext db, AffiliateService affiliates, IConfiguration config) : PageModel
{
    public sealed record ReferralRow(AffiliateReferral Referral, string Product, string Account);

    public sealed record CommissionRow(AffiliateCommission Commission, string Product, long? InvoiceNumber);

    public sealed record RuleRow(CommissionRule Rule, string? Product);

    public Affiliate? Affiliate { get; private set; }

    public string Account { get; private set; } = string.Empty;

    public string Link { get; private set; } = string.Empty;

    public int Clicks { get; private set; }

    public IReadOnlyList<AffiliateBalance> Balances { get; private set; } = [];

    public IReadOnlyList<RuleRow> Rules { get; private set; } = [];

    public IReadOnlyList<ReferralRow> Referrals { get; private set; } = [];

    public IReadOnlyList<CommissionRow> Commissions { get; private set; } = [];

    public IReadOnlyList<AffiliatePayout> Payouts { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Products { get; private set; } = [];

    [BindProperty]
    public RuleForm Rule { get; set; } = new();

    [BindProperty]
    public long? ServiceId { get; set; }

    /// <summary>El importe a pasar a saldo a favor, como lo escribió el staff.</summary>
    [BindProperty]
    public string? Amount { get; set; }

    [BindProperty]
    public string? Currency { get; set; }

    public string? Error { get; private set; }

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.AffiliatesManage);

    public async Task<IActionResult> OnGet(long id) => await Load(id) ? Page() : NotFound();

    public Task<IActionResult> OnPostStatus(long id, AffiliateStatus to) => Manage(id, () => affiliates.Decide(id, to));

    public Task<IActionResult> OnPostRule(long id) => Manage(id, async () =>
    {
        var (kind, value, currency) = Rule.Parse();
        await affiliates.SetRule(Rule.ProductId, id, kind, value, currency, Rule.Recurring);
    });

    public Task<IActionResult> OnPostDeleteRule(long id, long ruleId) => Manage(id, () => affiliates.DeleteRule(ruleId));

    public Task<IActionResult> OnPostAssign(long id) => Manage(id, () => ServiceId is { } service
        ? affiliates.AssignReferral(id, service)
        : throw new ValidationException("Falta el número de servicio."));

    /// <summary>El staff pasa saldo del afiliado al saldo a favor de su cuenta, como lo haría el afiliado desde el portal.</summary>
    public Task<IActionResult> OnPostCredit(long id) => Manage(id, () =>
        AffiliateWeb.ParseDecimal(Amount, 2) is { } amount && Currency is { Length: 3 } code && code.All(char.IsAsciiLetterUpper)
            ? affiliates.ConvertToCredit(id, new Currency(code), amount)
            : throw new ValidationException("Escribí el importe con coma o punto decimal y sin separador de miles, y elegí la moneda."));

    private async Task<IActionResult> Manage(long id, Func<Task> work)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        if (!await db.Set<Affiliate>().AnyAsync(a => a.Id == id))
        {
            return NotFound();
        }

        try
        {
            await work();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load(id);
            return Page();
        }

        return Redirect($"/admin/afiliados/{id}");
    }

    private async Task<bool> Load(long id)
    {
        Affiliate = await db.Set<Affiliate>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
        if (Affiliate is null)
        {
            return false;
        }

        Account = await db.Accounts.Where(a => a.Id == Affiliate.AccountId).Select(a => a.Name).SingleAsync();
        var publicUrl = config["Billing:PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
        Link = $"{publicUrl}/r/{Affiliate.Code}";
        Clicks = await db.Set<AffiliateClick>().CountAsync(c => c.AffiliateId == id);
        Balances = await affiliates.Balances(id);
        Rules = await (from r in db.Set<CommissionRule>().AsNoTracking()
                       join p in db.Products on r.ProductId equals (long?)p.Id into products
                       from p in products.DefaultIfEmpty()
                       where r.AffiliateId == id
                       orderby r.ProductId, r.Currency
                       select new RuleRow(r, p == null ? null : p.Name)).ToListAsync();
        Referrals = await (from r in db.Set<AffiliateReferral>().AsNoTracking()
                           join s in db.Services on r.ServiceId equals s.Id
                           join p in db.Products on s.ProductId equals p.Id
                           join a in db.Accounts on r.AccountId equals a.Id
                           where r.AffiliateId == id
                           orderby r.Id descending
                           select new ReferralRow(r, p.Name, a.Name)).Take(500).ToListAsync();
        Commissions = await (from c in db.Set<AffiliateCommission>().AsNoTracking()
                             join s in db.Services on c.ServiceId equals s.Id
                             join p in db.Products on s.ProductId equals p.Id
                             join d in db.Documents on c.DocumentId equals (long?)d.Id into docs
                             from d in docs.DefaultIfEmpty()
                             where c.AffiliateId == id
                             orderby c.Id descending
                             select new CommissionRow(c, p.Name, d == null ? null : d.Number)).Take(500).ToListAsync();
        Payouts = await db.Set<AffiliatePayout>().AsNoTracking().Where(p => p.AffiliateId == id).OrderByDescending(p => p.Id).Take(200).ToListAsync();
        Products = (await db.Products.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync()).Select(p => (p.Id, p.Name)).ToList();
        return true;
    }
}

/// <summary>Formulario de una regla de comisión, compartido por la ficha del afiliado y el programa.</summary>
public sealed class RuleForm
{
    public long? ProductId { get; set; }

    public CommissionKind Kind { get; set; } = CommissionKind.Percent;

    public string? Value { get; set; }

    public string? Currency { get; set; }

    public bool Recurring { get; set; } = true;

    public (CommissionKind Kind, decimal Value, Currency? Currency) Parse()
    {
        if (Kind == CommissionKind.None)
        {
            return (Kind, 0m, null);
        }

        var value = AffiliateWeb.ParseDecimal(Value, Kind == CommissionKind.Fixed ? 2 : 4)
            ?? throw new ValidationException("Escribí el valor con coma o punto decimal y sin separador de miles.");
        if (Kind == CommissionKind.Percent)
        {
            return (Kind, value, null);
        }

        return Currency is { Length: 3 } code && code.All(char.IsAsciiLetterUpper)
            ? (Kind, value, new Currency(code))
            : throw new ValidationException("El monto fijo necesita la moneda, por ejemplo ARS.");
    }
}
