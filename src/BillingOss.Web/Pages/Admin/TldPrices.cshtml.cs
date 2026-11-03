using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Precios por TLD: los vigentes por moneda, la carga a mano y la importación desde los costos del registrar, que
/// primero muestra la propuesta y recién después la guarda. Un precio nuevo rige desde su fecha y no toca los dominios
/// ya contratados.
/// </summary>
[Authorize(Policy = StaffPermissions.DomainsRead)]
public sealed class TldPricesModel(BillingDbContext db, TldPricing pricing, BillingCore core) : PageModel
{
    public sealed record Registrar(long Id, string Name, bool Pricing);

    public sealed class PriceForm
    {
        public string Tld { get; set; } = string.Empty;

        public long InstanceId { get; set; }

        public int Years { get; set; } = 1;

        public decimal? Register { get; set; }

        public decimal? Renew { get; set; }

        public decimal? Transfer { get; set; }

        public DateOnly? ValidFrom { get; set; }
    }

    public sealed class ImportForm
    {
        public long InstanceId { get; set; }

        public decimal ExchangeRate { get; set; } = 1m;

        public decimal MarginPercent { get; set; }

        public decimal TaxPercent { get; set; }

        public decimal RoundTo { get; set; } = 1m;

        public DateOnly? ValidFrom { get; set; }
    }

    [BindProperty(SupportsGet = true, Name = "moneda")]
    public string CurrencyCode { get; set; } = "ARS";

    [BindProperty]
    public PriceForm Price { get; set; } = new();

    [BindProperty]
    public ImportForm Import { get; set; } = new();

    public IReadOnlyList<TldOffer> Offers { get; private set; } = [];

    /// <summary>Precio anual vigente de cada opción del dominio en la moneda. Cero es gratis.</summary>
    public IReadOnlyDictionary<string, decimal> OptionPrices { get; private set; } = new Dictionary<string, decimal>();

    /// <summary>Las extensiones que se venden en la moneda y las que tienen opciones cargadas, con las que ofrecen.</summary>
    public IReadOnlyList<(string Tld, IReadOnlySet<string> Offered)> TldOptions { get; private set; } = [];

    public IReadOnlyList<Registrar> Registrars { get; private set; } = [];

    public IReadOnlyList<ImportedTldPrice>? Preview { get; private set; }

    /// <summary>De dónde salió la propuesta: registrar o csv. Cada formulario ofrece guardar solo la suya.</summary>
    public string? PreviewSource { get; private set; }

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.DomainsManage);

    public string RegistrarName(long id) => Registrars.FirstOrDefault(r => r.Id == id)?.Name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<IActionResult> OnGet()
    {
        await Load();
        return Page();
    }

    public Task<IActionResult> OnPostSet() => Act(async currency =>
    {
        await pricing.Set(Price.Tld, Price.InstanceId, currency, Price.Years, Price.Register, Price.Renew, Price.Transfer, Price.ValidFrom ?? core.Today(), HttpContext.RequestAborted);
        Notice = "AdminPrices.Saved";
    });

    /// <summary>El precio anual de una opción del dominio desde una fecha. Cero la deja gratis.</summary>
    public Task<IActionResult> OnPostOption(string? option, decimal optionPrice, DateOnly? optionFrom) => Act(async currency =>
    {
        await pricing.SetOptionPrice(option ?? string.Empty, currency, optionPrice, optionFrom ?? core.Today(), HttpContext.RequestAborted);
        Notice = "AdminPrices.Saved";
    });

    /// <summary>Qué opciones del dominio se ofrecen en una extensión.</summary>
    public Task<IActionResult> OnPostTldOptions(string? optionsTld, List<string> offered) => Act(async _ =>
    {
        await pricing.SetTldOptions(optionsTld ?? string.Empty, offered, HttpContext.RequestAborted);
        Notice = "AdminPrices.Saved";
    });

    public Task<IActionResult> OnPostPreview() => Act(async currency =>
    {
        Preview = await pricing.ImportFromRegistrar(Import.InstanceId, Rule(currency), Import.ValidFrom ?? core.Today(), apply: false, HttpContext.RequestAborted);
        PreviewSource = "registrar";
    });

    public Task<IActionResult> OnPostApply() => Act(async currency =>
    {
        Preview = await pricing.ImportFromRegistrar(Import.InstanceId, Rule(currency), Import.ValidFrom ?? core.Today(), apply: true, HttpContext.RequestAborted);
        Notice = "AdminPrices.Imported";
    });

    public Task<IActionResult> OnPostCsvPreview(long csvInstance, string? csv, DateOnly? csvFrom) => Act(async currency =>
    {
        Preview = await pricing.ImportCsv(csvInstance, currency, csv ?? string.Empty, csvFrom ?? core.Today(), apply: false, HttpContext.RequestAborted);
        PreviewSource = "csv";
        (CsvInstance, Csv, CsvFrom) = (csvInstance, csv, csvFrom);
    });

    public Task<IActionResult> OnPostCsvApply(long csvInstance, string? csv, DateOnly? csvFrom) => Act(async currency =>
    {
        Preview = await pricing.ImportCsv(csvInstance, currency, csv ?? string.Empty, csvFrom ?? core.Today(), apply: true, HttpContext.RequestAborted);
        Notice = "AdminPrices.Imported";
    });

    /// <summary>Lo que se mandó en el CSV, para volver a mostrarlo con la propuesta.</summary>
    public long? CsvInstance { get; private set; }

    public string? Csv { get; private set; }

    public DateOnly? CsvFrom { get; private set; }

    private PriceRule Rule(Currency currency) => new(currency, Import.ExchangeRate, Import.MarginPercent, Import.TaxPercent, Import.RoundTo);

    /// <summary>Los cambios piden el permiso de operar. Los errores del núcleo se muestran tal cual, para el staff.</summary>
    private async Task<IActionResult> Act(Func<Currency, Task> action)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await action(new Currency(CurrencyCode));
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }
        catch (ArgumentException)
        {
            Error = "AdminPrices.InvalidCurrency";
        }

        await Load();
        return Page();
    }

    private async Task Load()
    {
        Registrars = [.. (await DomainJobs.Registrars(db, HttpContext.RequestAborted)).Select(r => new Registrar(r.Id, r.Name, r.Capabilities.Contains("registrar.pricing")))];
        try
        {
            Offers = await pricing.Current(new Currency(CurrencyCode), HttpContext.RequestAborted);
        }
        catch (ArgumentException)
        {
            Error ??= "AdminPrices.InvalidCurrency";
            CurrencyCode = "ARS";
            Offers = await pricing.Current(Currency.Ars, HttpContext.RequestAborted);
        }

        OptionPrices = await pricing.OptionPrices(new Currency(CurrencyCode), HttpContext.RequestAborted);
        var rows = (await pricing.OptionsByTld(HttpContext.RequestAborted)).ToDictionary(t => t.Tld, StringComparer.Ordinal);
        TldOptions = [.. Offers.Select(o => o.Tld).Concat(rows.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(tld => (tld, (IReadOnlySet<string>)DomainOptions.All.Where(o => rows.GetValueOrDefault(tld)?.Offers(o) != false).ToHashSet(StringComparer.Ordinal)))];
    }
}
