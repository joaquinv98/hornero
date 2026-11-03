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

namespace BillingOss.Web.Pages.Admin.Taxes;

/// <summary>
/// Categorías, impuestos, reglas y percepciones (secciones 3.3, 3.8 y 3.11 del diseño fiscal). Nada se edita: una
/// regla vigente se cierra y se abre otra, así un documento viejo siempre se explica con lo que usó.
/// </summary>
[Authorize(Policy = StaffPermissions.TaxesEdit)]
public sealed class TaxesRulesModel(BillingDbContext db, TaxSetupService setup, BillingCore core, Microsoft.Extensions.Localization.IStringLocalizer<Texts> texts) : PageModel
{
    public sealed record CategoryRow(TaxCategory Category, int Products);

    [BindProperty(SupportsGet = true, Name = "tax")]
    public string? FilterTax { get; set; }

    [BindProperty(SupportsGet = true, Name = "country")]
    public string? FilterCountry { get; set; }

    [BindProperty(SupportsGet = true, Name = "category")]
    public string? FilterCategory { get; set; }

    [BindProperty(SupportsGet = true, Name = "condition")]
    public int? FilterCondition { get; set; }

    [BindProperty(SupportsGet = true, Name = "closed")]
    public bool ShowClosed { get; set; }

    public IReadOnlyList<CategoryRow> Categories { get; private set; } = [];

    public IReadOnlyList<Tax> Taxes { get; private set; } = [];

    public IReadOnlyList<TaxRule> Rules { get; private set; } = [];

    public IReadOnlyList<GrossIncomeRegime> Regimes { get; private set; } = [];

    public bool Rg2126Active { get; private set; }

    public DateOnly Today => core.Today();

    public string? Error { get; private set; }

    public string? Done => Request.Query["listo"];

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostAddCategory(string code, string name, string explanation, TaxTreatment treatment) =>
        await Try(async () => await setup.AddCategory(code?.Trim() ?? string.Empty, name, explanation, treatment), "categoria");

    public async Task<IActionResult> OnPostAddRule(string taxCode, string? country, string? province, int? vatCondition, string? business, string? category, TaxTreatment outcome, string? ratePercent, string? reason, string? legend, DateOnly validFrom, DateOnly? validTo, string? note)
    {
        var rate = outcome == TaxTreatment.Taxed ? Ui.ParseDecimal(ratePercent) : 0m;
        if (rate is null)
        {
            Error = texts["Taxes.RateInvalid"];
            await Load();
            return Page();
        }

        bool? validated = business switch { "yes" => true, "no" => false, _ => null };
        var input = new TaxRuleInput(taxCode, Blank(country), Blank(province), vatCondition is > 0 ? vatCondition : null, validated, Blank(category), outcome, rate.Value / 100m, Blank(reason), Blank(legend), validFrom, validTo, Blank(note));
        return await Try(async () => await setup.AddRule(input), "regla");
    }

    public async Task<IActionResult> OnPostClose(long id) =>
        await Try(async () => await setup.CloseRule(id, Today), "cierre");

    public async Task<IActionResult> OnPostRegime(string jurisdiction, string regime, string agentNumber, string? ratePercent, TaxBase taxBase, DateOnly validFrom, DateOnly? validTo, int[]? excluded)
    {
        var rate = Ui.ParseDecimal(ratePercent);
        if (rate is null)
        {
            Error = texts["Taxes.RateInvalid"];
            await Load();
            return Page();
        }

        var input = new GrossIncomeRegime(jurisdiction?.Trim().ToUpperInvariant() ?? string.Empty, regime, agentNumber, rate.Value / 100m, taxBase, validFrom, validTo, excluded is { Length: > 0 } ? excluded : null);
        return await Try(async () => await setup.SetGrossIncomeRegime(input), "iibb");
    }

    public async Task<IActionResult> OnPostCloseRegime(string jurisdiction) =>
        await Try(async () => await setup.CloseGrossIncomeRegime(jurisdiction, Today), "iibb_cierre");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<IActionResult> Try(Func<Task> work, string done)
    {
        try
        {
            await work();
            return Redirect("/admin/impuestos/reglas?listo=" + done);
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
        var counts = await db.Products.GroupBy(p => p.TaxCategory).Select(g => new { Code = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Code, g => g.Count);
        Categories = [.. (await db.TaxCategories.AsNoTracking().OrderBy(c => c.Id).ToListAsync()).Select(c => new CategoryRow(c, counts.GetValueOrDefault(c.Code)))];
        Taxes = await db.Taxes.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        var query = db.TaxRules.AsNoTracking().Where(r => r.AccountId == null);
        var today = Today;
        if (!ShowClosed)
        {
            query = query.Where(r => r.ValidTo == null || r.ValidTo > today);
        }

        if (!string.IsNullOrEmpty(FilterTax))
        {
            query = query.Where(r => r.TaxCode == FilterTax);
        }

        if (!string.IsNullOrEmpty(FilterCountry))
        {
            var country = FilterCountry.ToUpperInvariant();
            query = query.Where(r => r.Country == country);
        }

        if (!string.IsNullOrEmpty(FilterCategory))
        {
            query = query.Where(r => r.Category == FilterCategory);
        }

        if (FilterCondition is > 0)
        {
            query = query.Where(r => r.VatCondition == FilterCondition);
        }

        Rules = await query.OrderByDescending(r => r.ValidFrom).ThenByDescending(r => r.Id).Take(500).ToListAsync();
        var settings = await core.Settings(HttpContext.RequestAborted);
        Regimes = settings.Regimes();
        Rg2126Active = await setup.Rg2126Active(HttpContext.RequestAborted);
    }
}
