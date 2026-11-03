using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Taxes;

/// <summary>
/// "Así cobrás hoy" (sección 3.11 del diseño fiscal): el simulador con los clientes tipo, uno a medida y los avisos
/// de lo que puede estar mal. Todo es lectura: las acciones están en las otras páginas de la sección.
/// </summary>
[Authorize(Policy = StaffPermissions.TaxesEdit)]
public sealed class TaxesIndexModel(BillingDbContext db, TaxSimulator simulator, TaxSetupService setup) : PageModel
{
    /// <summary>Un cliente tipo con su resultado, o el motivo por el que el cálculo falla (sin regla, por ejemplo).</summary>
    public sealed record Sample(string Key, SimulationInput Input, Simulation? Result, string? Error);

    [BindProperty(SupportsGet = true, Name = "pais")]
    public string SimCountry { get; set; } = "AR";

    [BindProperty(SupportsGet = true, Name = "provincia")]
    public string? SimProvince { get; set; }

    [BindProperty(SupportsGet = true, Name = "condicion")]
    public int SimVatCondition { get; set; } = VatConditions.ConsumidorFinal;

    [BindProperty(SupportsGet = true, Name = "categoria")]
    public string SimCategory { get; set; } = TaxCategories.Digital;

    [BindProperty(SupportsGet = true, Name = "precio")]
    public string? SimPrice { get; set; }

    [BindProperty(SupportsGet = true, Name = "moneda")]
    public string SimCurrency { get; set; } = "ARS";

    [BindProperty(SupportsGet = true, Name = "modo")]
    public PriceMode SimMode { get; set; } = PriceMode.TaxIncluded;

    public IReadOnlyList<Sample> Samples { get; private set; } = [];

    public Sample? Custom { get; private set; }

    public IReadOnlyList<TaxWarning> Warnings { get; private set; } = [];

    public IReadOnlyList<TaxCategory> Categories { get; private set; } = [];

    public bool HasRules { get; private set; }

    public string? Done => Request.Query["listo"];

    public async Task OnGet()
    {
        Categories = await db.TaxCategories.AsNoTracking().Where(c => c.ValidTo == null).OrderBy(c => c.Id).ToListAsync();
        HasRules = await db.TaxRules.AnyAsync(r => r.AccountId == null && r.ValidTo == null);
        Warnings = await setup.Warnings();
        var samples = new List<Sample>();
        foreach (var (key, input) in SampleInputs())
        {
            samples.Add(await Run(key, input));
        }

        Samples = samples;
        if (Ui.ParseDecimal(SimPrice) is { } price and > 0m && SimCurrency is { Length: 3 } && SimCountry is { Length: 2 })
        {
            var province = string.IsNullOrWhiteSpace(SimProvince) ? null : SimProvince.Trim().ToUpperInvariant();
            Custom = await Run("custom", new SimulationInput(SimCountry.ToUpperInvariant(), province, SimVatCondition, SimCategory, price, new Currency(SimCurrency.ToUpperInvariant()), SimMode));
        }
    }

    private async Task<Sample> Run(string key, SimulationInput input)
    {
        try
        {
            return new Sample(key, input, await simulator.Run(input), null);
        }
        catch (InvalidOperationException ex)
        {
            return new Sample(key, input, null, ex.Message);
        }
    }

    /// <summary>Los clientes tipo de la tabla de la sección 3.11: un plan de $12.100 con IVA incluido y uno de USD 100 sin impuestos.</summary>
    private static IEnumerable<(string Key, SimulationInput Input)> SampleInputs() =>
    [
        ("consumer", new("AR", "AR-C", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded)),
        ("business", new("AR", "AR-X", VatConditions.ResponsableInscripto, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded)),
        ("telecom", new("AR", "AR-X", VatConditions.ResponsableInscripto, TaxCategories.Telecom, 12100m, Currency.Ars, PriceMode.TaxIncluded)),
        ("uncategorized", new("AR", null, VatConditions.NoCategorizado, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded)),
        ("foreign", new("US", null, VatConditions.ClienteDelExterior, TaxCategories.Digital, 100m, Currency.Usd, PriceMode.TaxExcluded)),
    ];
}
