using System.Text.Json.Nodes;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using BillingOss.Web.Pages.Admin.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Cargar saldo a favor con cualquier pasarela que cobre en la moneda elegida, dentro de los límites del proveedor
/// (Billing:TopUp). Sin límites configurados la página no existe.
/// </summary>
public sealed class TopUpModel(BillingDbContext db, PortalAccess access, IOptions<BillingOptions> options, IStringLocalizer<Texts> texts) : PageModel
{
    public sealed record Method(long Id, string Name);

    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    [BindProperty(SupportsGet = true, Name = "moneda")]
    public string? CurrencyCode { get; set; }

    [BindProperty]
    public string? Amount { get; set; }

    [BindProperty]
    public long? Instance { get; set; }

    public IReadOnlyList<Account> Accounts { get; private set; } = [];

    public IReadOnlyList<Currency> Currencies { get; private set; } = [];

    public IReadOnlyList<Method> Methods { get; private set; } = [];

    public IReadOnlyList<CreditBalance> Balances { get; private set; } = [];

    public TopUpLimits? Limits { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGet() => await Load() ? Page() : NotFound();

    public async Task<IActionResult> OnPost()
    {
        var gateways = HttpContext.RequestServices.GetService<GatewayPayments>();
        if (!await Load() || gateways is null)
        {
            return NotFound();
        }

        if (Methods.FirstOrDefault(m => m.Id == Instance) is not { } method)
        {
            Error = texts["Pay.ChooseMethod"];
            return Page();
        }

        try
        {
            var started = await gateways.TopUp(AccountId!.Value, CurrencyCode!, BillingForm.Number(Amount, "Importe"), method.Id, HttpContext.RequestAborted);
            return Redirect(started.RedirectUrl);
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is PermanentFailureException or RetryLaterException)
        {
            Error = texts["Invoice.GatewayError"];
        }

        return Page();
    }

    /// <summary>La cuenta y la moneda elegidas, de las de la sesión y de las que admiten carga, y los medios que cobran en ella.</summary>
    private async Task<bool> Load()
    {
        Currencies = [.. options.Value.TopUp.Keys.Select(k => new Currency(k.ToUpperInvariant())).Distinct().OrderBy(c => c.Code)];
        if (Currencies.Count == 0)
        {
            return false;
        }

        var accounts = access.AccountIds(User, AccountCapabilities.Invoices);
        Accounts = await db.Accounts.AsNoTracking().Where(a => accounts.Contains(a.Id)).OrderBy(a => a.Id).ToListAsync();
        if (Accounts.Count == 0 || (AccountId is { } chosen && Accounts.All(a => a.Id != chosen)))
        {
            return false;
        }

        var account = Accounts.FirstOrDefault(a => a.Id == AccountId) ?? Accounts[0];
        AccountId = account.Id;
        var currency = Currencies.FirstOrDefault(c => c.Code == CurrencyCode?.ToUpperInvariant());
        if (currency == default)
        {
            currency = Currencies.Contains(account.Currency) ? account.Currency : Currencies[0];
        }

        CurrencyCode = currency.Code;
        Limits = GatewayPayments.TopUpLimits(options.Value, currency);
        Balances = await db.CreditBalances.AsNoTracking().Where(b => b.AccountId == account.Id && b.Balance > 0).OrderBy(b => b.Currency).ToListAsync();
        if (HttpContext.RequestServices.GetService<GatewayPayments>() is null)
        {
            return true;
        }

        var instances = await db.ModuleInstances.AsNoTracking()
            .Join(db.ModuleInstallations, i => i.InstallationId, m => m.Id, (i, m) => new { i.Id, i.Name, m.Manifest })
            .OrderBy(x => x.Id).ToListAsync();
        var setups = await db.GatewaySetups.AsNoTracking().ToDictionaryAsync(s => s.ModuleInstanceId);
        var methods = new List<Method>();
        foreach (var instance in instances)
        {
            var manifest = JsonNode.Parse(instance.Manifest)?.AsObject() ?? [];
            if (manifest["capabilities"] is not JsonArray caps || !caps.Any(c => c?.GetValue<string>() == "payment.checkout"))
            {
                continue;
            }

            // Una instancia que pasó por el asistente cobra solo si está activa y en las monedas que se le activaron.
            setups.TryGetValue(instance.Id, out var setup);
            if (setup is not null && !setup.Offers(currency))
            {
                continue;
            }

            try
            {
                GatewayPayments.ChargeCurrency(manifest, currency, currency);
                methods.Add(new Method(instance.Id, setup?.DisplayName ?? instance.Name));
            }
            catch (ValidationException)
            {
                // No cobra en esta moneda: la carga no se convierte.
            }
        }

        Methods = methods;
        return true;
    }
}
