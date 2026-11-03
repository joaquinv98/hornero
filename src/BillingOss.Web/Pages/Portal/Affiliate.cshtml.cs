using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Panel del afiliado (sección 10 de diseno/afiliados.md): sumarse, el enlace, clics, referidos, comisiones, saldo y
/// cobros. Nunca muestra datos de los referidos: solo el producto, la fecha y el estado.
/// </summary>
public sealed class AffiliateModel(BillingDbContext db, PortalAccess access, AffiliateService affiliates, IConfiguration config, TimeProvider time) : PageModel
{
    public sealed record ReferralRow(DateTimeOffset CreatedAt, string Product, BillingOss.Domain.Services.ServiceStatus Status);

    public sealed record CommissionRow(AffiliateCommission Commission, string Product);

    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountParam { get; set; }

    /// <summary>El importe como lo escribió la persona: con coma o punto decimal y sin separador de miles.</summary>
    [BindProperty]
    public string? Amount { get; set; }

    [BindProperty]
    public string Currency { get; set; } = string.Empty;

    [BindProperty]
    public string? Details { get; set; }

    public IReadOnlyList<(long Id, string Name)> Accounts { get; private set; } = [];

    public long AccountId { get; private set; }

    public AffiliateProgram Program { get; private set; } = new();

    public CommissionRule? DefaultRule { get; private set; }

    /// <summary>El bono de alta en la moneda de la cuenta, si el programa tiene uno.</summary>
    public (decimal Amount, Currency Currency)? Bonus { get; private set; }

    public Affiliate? Affiliate { get; private set; }

    public string Link { get; private set; } = string.Empty;

    /// <summary>El material para compartir del programa, con el enlace del afiliado.</summary>
    public IReadOnlyList<string> Snippets { get; private set; } = [];

    public int RecentClicks { get; private set; }

    public int TotalClicks { get; private set; }

    public IReadOnlyList<ReferralRow> Referrals { get; private set; } = [];

    public IReadOnlyList<CommissionRow> Commissions { get; private set; } = [];

    public IReadOnlyList<AffiliateBalance> Balances { get; private set; } = [];

    public IReadOnlyList<AffiliatePayout> Payouts { get; private set; } = [];

    /// <summary>Clave del texto del error, y el dato que completa el texto.</summary>
    public string? Error { get; private set; }

    public string? ErrorDetail { get; private set; }

    [TempData]
    public string? Notice { get; set; }

    public bool Acting => User.ActingAccount() is not null;

    public async Task<IActionResult> OnGet() => await Load() ? Page() : NotFound();

    public async Task<IActionResult> OnPostJoin() => await Act(async () =>
    {
        var affiliate = await affiliates.Join(AccountId);
        Notice = affiliate.Status == AffiliateStatus.Pending ? "Affiliates.JoinedPending" : "Affiliates.Joined";
    });

    public async Task<IActionResult> OnPostCredit() => await Act(async () =>
    {
        if (Check(withdrawal: false) is { } currency)
        {
            await affiliates.ConvertToCredit(Mine(), currency, Parsed);
            Notice = "Affiliates.CreditDone";
        }
    });

    public async Task<IActionResult> OnPostWithdraw() => await Act(async () =>
    {
        if (Check(withdrawal: true) is { } currency)
        {
            await affiliates.RequestWithdrawal(Mine(), currency, Parsed, Details ?? string.Empty);
            Notice = "Affiliates.WithdrawalDone";
        }
    });

    /// <summary>Las acciones con plata no se hacen mirando como cliente: las hace el titular o el staff desde el admin.</summary>
    private async Task<IActionResult> Act(Func<Task> work)
    {
        if (!await Load())
        {
            return NotFound();
        }

        if (Acting)
        {
            return Forbid(Schemes.Portal);
        }

        try
        {
            await work();
        }
        catch (ValidationException ex)
        {
            // Lo que el chequeo de la página no vio, por ejemplo un saldo que cambió en el medio.
            Error = "Affiliates.ErrorOther";
            ErrorDetail = ex.Message;
        }

        if (Error is not null)
        {
            await Load();
            return Page();
        }

        return Redirect(AccountParam is { } account ? string.Create(CultureInfo.InvariantCulture, $"/afiliados?cuenta={account}") : "/afiliados");
    }

    private decimal Parsed { get; set; }

    private long Mine() => Affiliate?.Id ?? throw new ValidationException("Todavía no sos afiliado.");

    /// <summary>Los errores de siempre, con su texto en cada idioma. Devuelve la moneda si el pedido está bien.</summary>
    private Currency? Check(bool withdrawal)
    {
        var balance = Balances.FirstOrDefault(b => b.Currency.Code == Currency);
        var minimum = balance is null ? 0m : Program.MinimumPayout(balance.Currency);
        Parsed = AffiliateWeb.ParseDecimal(Amount, 2) ?? 0m;
        (Error, ErrorDetail) = (balance, Parsed) switch
        {
            (null, _) => ("Affiliates.ErrorCurrency", null),
            (_, <= 0m) => ("Affiliates.ErrorAmount", null),
            _ when Parsed > balance!.Available => ("Affiliates.ErrorBalance", Ui.Money(balance.Available, balance.Currency)),
            _ when withdrawal && Parsed < minimum => ("Affiliates.ErrorMinimum", Ui.Money(minimum, balance!.Currency)),
            _ when withdrawal && string.IsNullOrWhiteSpace(Details) => ("Affiliates.ErrorDetails", null),
            _ => (null, null),
        };
        return Error is null ? balance!.Currency : null;
    }

    private async Task<bool> Load()
    {
        var allowed = access.AccountIds(User, AccountCapabilities.Affiliate);
        Accounts = (await db.Accounts.AsNoTracking().Where(a => allowed.Contains(a.Id)).OrderBy(a => a.Id).Select(a => new { a.Id, a.Name }).ToListAsync())
            .Select(a => (a.Id, a.Name)).ToList();
        if (Accounts.Count == 0 || (AccountParam is { } wanted && Accounts.All(a => a.Id != wanted)))
        {
            return false;
        }

        AccountId = AccountParam ?? Accounts[0].Id;
        Program = await affiliates.Program();
        DefaultRule = await db.Set<CommissionRule>().AsNoTracking().SingleOrDefaultAsync(r => r.ProductId == null && r.AffiliateId == null && r.Currency == null);
        Affiliate = await db.Set<Affiliate>().AsNoTracking().SingleOrDefaultAsync(a => a.AccountId == AccountId);
        if (Affiliate is not { } affiliate)
        {
            var currency = await db.Accounts.Where(a => a.Id == AccountId).Select(a => a.Currency).SingleAsync();
            Bonus = Program.SignupBonus(currency) is > 0m and var bonus ? (bonus, currency) : null;
            return true;
        }

        var publicUrl = config["Billing:PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
        Link = $"{publicUrl}/r/{affiliate.Code}";
        Snippets = Program.Snippets(Link);
        var since = time.GetUtcNow().AddDays(-30);
        TotalClicks = await db.Set<AffiliateClick>().CountAsync(c => c.AffiliateId == affiliate.Id);
        RecentClicks = await db.Set<AffiliateClick>().CountAsync(c => c.AffiliateId == affiliate.Id && c.At >= since);
        Referrals = await (from r in db.Set<AffiliateReferral>().AsNoTracking()
                           join s in db.Services on r.ServiceId equals s.Id
                           join p in db.Products on s.ProductId equals p.Id
                           where r.AffiliateId == affiliate.Id
                           orderby r.CreatedAt descending, r.Id descending
                           select new ReferralRow(r.CreatedAt, p.Name, s.Status)).Take(200).ToListAsync();
        Commissions = await (from c in db.Set<AffiliateCommission>().AsNoTracking()
                             join s in db.Services on c.ServiceId equals s.Id
                             join p in db.Products on s.ProductId equals p.Id
                             where c.AffiliateId == affiliate.Id
                             orderby c.CreatedAt descending, c.Id descending
                             select new CommissionRow(c, p.Name)).Take(200).ToListAsync();
        Balances = await affiliates.Balances(affiliate.Id);
        Payouts = await db.Set<AffiliatePayout>().AsNoTracking().Where(p => p.AffiliateId == affiliate.Id).OrderByDescending(p => p.Id).Take(50).ToListAsync();
        return true;
    }
}
