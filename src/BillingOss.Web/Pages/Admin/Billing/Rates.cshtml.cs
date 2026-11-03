using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>
/// Monedas y cotizaciones: la que vale hoy para cada moneda, las últimas cargadas, la carga manual y el pedido al
/// Banco Nación, que lo hace un trabajo.
/// </summary>
[Authorize(Policy = StaffPermissions.BillingRead)]
public sealed class RatesModel(ExchangeRateService rates, JobQueue jobs, IOptions<BillingOptions> options, TimeProvider time) : PageModel
{
    public sealed record CurrentRate(Currency Currency, Conversion? Conversion);

    [BindProperty]
    public string? Currency { get; set; }

    [BindProperty]
    public string? Rate { get; set; }

    [BindProperty]
    public DateOnly? Date { get; set; }

    public IReadOnlyList<CurrentRate> Current { get; private set; } = [];

    public IReadOnlyList<ExchangeRate> Recent { get; private set; } = [];

    public bool Bna => options.Value.ExchangeRates.Bna;

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    public bool CanWrite => this.Can(StaffPermissions.BillingWrite);

    public async Task OnGet(bool pedido = false)
    {
        Notice = pedido ? "Rates.Requested" : null;
        await Load();
    }

    public async Task<IActionResult> OnPostManual()
    {
        if (!CanWrite)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await rates.AddManual(Currency ?? string.Empty, BillingForm.Number(Rate, "Cotización"), Date, HttpContext.RequestAborted);
            return Redirect("/admin/monedas");
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRefresh()
    {
        if (!CanWrite)
        {
            return Forbid(Schemes.Staff);
        }

        // La misma clave que la API: dos pedidos en el mismo minuto son un solo trabajo.
        await jobs.Enqueue(JobKinds.ExchangeRates, new { manual = true }, ExchangeRateService.Entity, $"{JobKinds.ExchangeRates}:{time.GetUtcNow():yyyy-MM-dd'T'HH:mm}", maxAttempts: 3, ct: HttpContext.RequestAborted);
        return Redirect("/admin/monedas?pedido=true");
    }

    private async Task Load()
    {
        var local = rates.Local;
        var current = new List<CurrentRate>();
        foreach (var code in options.Value.ExchangeRates.Currencies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var currency = InvoiceService.ParseCurrency(code);
            if (currency != local)
            {
                current.Add(new CurrentRate(currency, await rates.Current(currency, local, HttpContext.RequestAborted)));
            }
        }

        Current = current;
        Recent = await rates.Recent(30, HttpContext.RequestAborted);
    }
}
