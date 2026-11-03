using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Affiliates;

/// <summary>Ajustes del programa, la regla por defecto y las reglas por producto (sección 4 de diseno/afiliados.md).</summary>
[Authorize(Policy = StaffPermissions.AffiliatesRead)]
public sealed class AffiliateProgramModel(BillingDbContext db, AffiliateService affiliates) : PageModel
{
    public sealed record RuleRow(CommissionRule Rule, string? Product);

    public AffiliateProgram Program { get; private set; } = new();

    public IReadOnlyList<RuleRow> Rules { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Products { get; private set; } = [];

    [BindProperty]
    public bool Enabled { get; set; }

    [BindProperty]
    public bool RequiresApproval { get; set; }

    [BindProperty]
    public int CookieDays { get; set; }

    [BindProperty]
    public int HoldDays { get; set; }

    /// <summary>Mínimos de retiro como "ARS 5000, USD 5".</summary>
    [BindProperty]
    public string? Minimums { get; set; }

    /// <summary>Bono de alta por moneda, con la misma forma que los mínimos.</summary>
    [BindProperty]
    public string? Bonuses { get; set; }

    [BindProperty]
    public string? PayoutNotifyEmail { get; set; }

    [BindProperty]
    public string? Links { get; set; }

    [BindProperty]
    public bool MonthlyReport { get; set; }

    [BindProperty]
    public RuleForm Rule { get; set; } = new();

    public string? Error { get; private set; }

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.AffiliatesManage);

    public async Task OnGet()
    {
        await Load();
        Enabled = Program.Enabled;
        RequiresApproval = Program.RequiresApproval;
        CookieDays = Program.CookieDays;
        HoldDays = Program.HoldDays;
        Minimums = Format(Program.Minimums());
        Bonuses = Format(Program.Bonuses());
        PayoutNotifyEmail = Program.PayoutNotifyEmail;
        Links = Program.Links;
        MonthlyReport = Program.MonthlyReport;
    }

    public Task<IActionResult> OnPostProgram() => Manage(() =>
        affiliates.UpdateProgram(Enabled, RequiresApproval, CookieDays, HoldDays, ParseAmounts(Minimums), ParseAmounts(Bonuses), PayoutNotifyEmail, Links, MonthlyReport));

    public Task<IActionResult> OnPostRule() => Manage(async () =>
    {
        var (kind, value, currency) = Rule.Parse();
        await affiliates.SetRule(Rule.ProductId, null, kind, value, currency, Rule.Recurring);
    });

    public Task<IActionResult> OnPostDeleteRule(long ruleId) => Manage(() => affiliates.DeleteRule(ruleId));

    private async Task<IActionResult> Manage(Func<Task> work)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await work();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load();
            return Page();
        }

        return Redirect("/admin/afiliados/programa");
    }

    private static string Format(IReadOnlyDictionary<string, decimal> amounts) =>
        string.Join(", ", amounts.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Key} {m.Value:0.##}")));

    /// <summary>
    /// Importes por moneda escritos como "ARS 5000, USD 5", para los mínimos y el bono. Una coma seguida de letras separa
    /// monedas y una seguida de dígitos es decimal: "ARS 1000,50, USD 5" vale.
    /// </summary>
    private static Dictionary<string, decimal> ParseAmounts(string? text)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var item in System.Text.RegularExpressions.Regex.Split(text ?? string.Empty, @",\s*(?=\p{L})").Select(i => i.Trim().TrimEnd(',').TrimEnd()).Where(i => i.Length > 0))
        {
            var parts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is not [var code, var value] || code.Length != 3 || !code.All(char.IsAsciiLetterUpper) || AffiliateWeb.ParseDecimal(value, 2) is not { } amount)
            {
                throw new ValidationException($"Importe inválido: {item}. Va la moneda y el importe, por ejemplo ARS 5000.");
            }

            result[code] = amount;
        }

        return result;
    }

    private async Task Load()
    {
        Program = await affiliates.Program();
        Rules = await (from r in db.Set<CommissionRule>().AsNoTracking()
                       join p in db.Products on r.ProductId equals (long?)p.Id into products
                       from p in products.DefaultIfEmpty()
                       where r.AffiliateId == null
                       orderby r.ProductId, r.Currency
                       select new RuleRow(r, p == null ? null : p.Name)).ToListAsync();
        Products = (await db.Products.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync()).Select(p => (p.Id, p.Name)).ToList();
    }
}
