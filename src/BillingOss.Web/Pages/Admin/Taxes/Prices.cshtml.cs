using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Taxes;

/// <summary>
/// "Precios y muestra" (secciones 3.4, 3.5 y 3.7 del diseño fiscal): el modo de carga por defecto de cada moneda, lo
/// que ve el visitante en cada canal, con lo que fija la ley bloqueado y explicado, el redondeo y los umbrales legales.
/// </summary>
[Authorize(Policy = StaffPermissions.TaxesEdit)]
public sealed class TaxesPricesModel(BillingDbContext db, TaxSetupService setup, BillingCore core) : PageModel
{
    public TaxSettings Settings { get; private set; } = new();

    /// <summary>Monedas con precio cargado o con modo por defecto, y el modo de cada una.</summary>
    public IReadOnlyList<(string Currency, PriceMode Mode)> Currencies { get; private set; } = [];

    public bool DisplayFixedByLaw => Settings.IssuerCountry == "AR";

    public string? Error { get; private set; }

    public string? Done => Request.Query["listo"];

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostPriceMode(string currency, PriceMode mode)
    {
        if (currency is not { Length: 3 })
        {
            Error = "ISO 4217";
            await Load();
            return Page();
        }

        await setup.SetDefaultPriceMode(new Currency(currency.ToUpperInvariant()), mode);
        return Redirect("/admin/impuestos/precios?listo=modo");
    }

    public async Task<IActionResult> OnPostDisplay(string catalogDisplay, bool businessMayViewNet)
    {
        try
        {
            await setup.SetDisplay(catalogDisplay, businessMayViewNet);
            return Redirect("/admin/impuestos/precios?listo=muestra");
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
        Settings = await core.Settings(HttpContext.RequestAborted);
        var modes = Settings.PriceModes();
        var priced = (await db.PlanPrices.AsNoTracking().Select(p => p.Currency).ToListAsync()).Select(c => c.Code);
        Currencies = [.. modes.Keys.Concat(priced).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(c => (c, modes.TryGetValue(c, out var mode) ? mode : PriceMode.TaxIncluded))];
    }
}
