using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Taxes;

/// <summary>
/// "Empezar": tres preguntas (país y condición del emisor, dónde vende) que dejan un preset completo de categorías,
/// impuestos y reglas. Sin reglas el cálculo falla, y eso no puede ser lo primero que vea quien recién instala.
/// </summary>
[Authorize(Policy = StaffPermissions.TaxesEdit)]
public sealed class TaxesStartModel(BillingDbContext db, TaxSetupService setup) : PageModel
{
    [BindProperty]
    public string IssuerCountry { get; set; } = "AR";

    [BindProperty]
    public int IssuerVatCondition { get; set; } = VatConditions.ResponsableInscripto;

    [BindProperty]
    public bool SellsAbroad { get; set; } = true;

    [BindProperty]
    public bool SellsToEuConsumers { get; set; }

    [BindProperty]
    public bool SellsToUkConsumers { get; set; }

    [BindProperty]
    public bool PerceivesRg2126 { get; set; }

    [BindProperty]
    public bool PaysGrossIncomeInCaba { get; set; }

    public int CurrentRules { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGet()
    {
        var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active);
        IssuerVatCondition = issuer?.VatCondition ?? VatConditions.ResponsableInscripto;
        SellsAbroad = issuer?.Exports ?? true;
        var settings = await db.TaxSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1);
        IssuerCountry = settings?.IssuerCountry ?? "AR";
        await Load();
    }

    public async Task<IActionResult> OnPost()
    {
        try
        {
            await setup.ApplyPreset(new TaxPreset(IssuerCountry.Trim().ToUpperInvariant(), IssuerVatCondition, SellsAbroad, SellsToEuConsumers, SellsToUkConsumers, PerceivesRg2126, PaysGrossIncomeInCaba));
            return Redirect("/admin/impuestos?listo=preset");
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load();
            return Page();
        }
    }

    private async Task Load() => CurrentRules = await setup.PresetReplaces(HttpContext.RequestAborted);
}
