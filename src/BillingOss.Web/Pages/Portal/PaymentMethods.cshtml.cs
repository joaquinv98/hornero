using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Medios de pago (sección 4.5 del diseño de cobros): los medios guardados de cada cuenta, cuál cobra el débito
/// automático, quitarlos y apagar el débito en el momento (Ley 24.240, art. 10 ter), y el débito por suscripción de la
/// pasarela cuando Payments:SubscriptionsEnabled está prendido. Una suscripción vigente se ve y se desactiva aunque esté
/// apagado. Prender un débito pide la casilla de consentimiento.
/// </summary>
public sealed class PaymentMethodsModel(BillingDbContext db, PortalAccess access, IStringLocalizer<Texts> texts, IConfiguration config) : PageModel
{
    public sealed record AccountView(
        long Id,
        string Name,
        Currency Currency,
        IReadOnlyList<SavedPaymentMethod> Methods,
        GatewaySubscription? Subscription,
        IReadOnlyList<(long Id, string Name)> Offered,
        decimal MonthlyTotal);

    public IReadOnlyList<AccountView> Accounts { get; private set; } = [];

    /// <summary>Nombre de cada pasarela en el portal, por instancia.</summary>
    public IReadOnlyDictionary<long, string> Gateways { get; private set; } = new Dictionary<long, string>();

    /// <summary>Clave del texto de lo que se acaba de hacer.</summary>
    public string? Done { get; private set; }

    /// <summary>Clave del texto del error, si lo hubo.</summary>
    public string? Error { get; private set; }

    public bool SubscriptionsEnabled { get; private set; }

    public string ConsentText => PayModel.Consent(texts, config);

    public async Task OnGet(string? hecho)
    {
        Done = hecho switch
        {
            "quitado" => "PaymentMethods.Removed",
            "debito" => "PaymentMethods.AutoChargeOn",
            "sin-debito" => "PaymentMethods.AutoChargeOff",
            "suscripcion" => "PaymentMethods.SubscriptionOff",
            _ => null,
        };
        await Load();
    }

    /// <summary>"Quitar": el débito se apaga en el momento y un trabajo lo borra en la pasarela.</summary>
    public async Task<IActionResult> OnPostRemove(long id)
    {
        if (HttpContext.RequestServices.GetService<SavedMethodService>() is not { } methods)
        {
            return NotFound();
        }

        try
        {
            await methods.Remove(id, await AccountIds(), HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return RedirectToPage(new { hecho = "quitado" });
    }

    /// <summary>Prende el débito con este medio (con la casilla tildada) o lo apaga.</summary>
    public async Task<IActionResult> OnPostAutoCharge(long id, bool on, bool consent)
    {
        if (HttpContext.RequestServices.GetService<SavedMethodService>() is not { } methods)
        {
            return NotFound();
        }

        if (on && !consent)
        {
            Error = "PaymentMethods.ConsentRequired";
            await Load();
            return Page();
        }

        try
        {
            await methods.SetAutoCharge(id, await AccountIds(), on ? Consent() : null, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ValidationException)
        {
            Error = "PaymentMethods.CannotUse";
            await Load();
            return Page();
        }

        return RedirectToPage(new { hecho = on ? "debito" : "sin-debito" });
    }

    /// <summary>Débito por suscripción: con la casilla tildada, crea la suscripción y lleva a la pasarela a aprobarla.</summary>
    public async Task<IActionResult> OnPostSubscribe(long account, long instance, bool consent)
    {
        if (HttpContext.RequestServices.GetService<SubscriptionService>() is not { Enabled: true } subscriptions || !(await AccountIds()).Contains(account))
        {
            return NotFound();
        }

        if (!consent)
        {
            Error = "PaymentMethods.ConsentRequired";
            await Load();
            return Page();
        }

        try
        {
            var started = await subscriptions.Start(account, instance, Consent(), HttpContext.RequestAborted);
            return Redirect(started.RedirectUrl);
        }
        catch (ValidationException)
        {
            Error = "PaymentMethods.CannotSubscribe";
        }
        catch (Exception ex) when (ex is PermanentFailureException or RetryLaterException)
        {
            Error = "Invoice.GatewayError";
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostUnsubscribe(long id)
    {
        if (HttpContext.RequestServices.GetService<SubscriptionService>() is not { } subscriptions)
        {
            return NotFound();
        }

        try
        {
            await subscriptions.Cancel(id, await AccountIds(), HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return RedirectToPage(new { hecho = "suscripcion" });
    }

    private ConsentInput Consent() => new(ConsentText, CultureInfo.CurrentUICulture.Name, Request.Headers.UserAgent.ToString());

    private async Task<List<long>> AccountIds() =>
        await access.AccountIds(User, AccountCapabilities.Invoices).ToListAsync(HttpContext.RequestAborted);

    private async Task Load()
    {
        var ct = HttpContext.RequestAborted;
        var ids = await AccountIds();
        var accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).OrderBy(a => a.Id).ToListAsync(ct);
        var methods = await db.SavedPaymentMethods.AsNoTracking()
            .Where(m => ids.Contains(m.AccountId) && m.Status != SavedMethodStatus.Deleted)
            .OrderByDescending(m => m.AutoCharge).ThenByDescending(m => m.Id).ToListAsync(ct);
        var subscriptionsLive = await db.GatewaySubscriptions.AsNoTracking()
            .Where(s => ids.Contains(s.AccountId) && s.Status != SubscriptionStatus.Cancelled).ToListAsync(ct);
        Gateways = await db.ModuleInstances.AsNoTracking()
            .GroupJoin(db.GatewaySetups, i => i.Id, s => s.ModuleInstanceId, (i, s) => new { i.Id, i.Name, Setups = s })
            .SelectMany(x => x.Setups.DefaultIfEmpty(), (x, s) => new { x.Id, Name = s != null ? s.DisplayName : x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var subscriptions = HttpContext.RequestServices.GetService<SubscriptionService>();
        SubscriptionsEnabled = subscriptions is { Enabled: true };
        var views = new List<AccountView>();
        foreach (var a in accounts)
        {
            var offered = subscriptions is { Enabled: true } ? await subscriptions.Offered(a.Currency, ct) : [];
            var total = offered.Count > 0 ? await subscriptions!.MonthlyTotal(a.Id, a.Currency, ct) : 0m;
            views.Add(new AccountView(a.Id, a.Name, a.Currency, [.. methods.Where(m => m.AccountId == a.Id)], subscriptionsLive.FirstOrDefault(s => s.AccountId == a.Id), offered, total));
        }

        Accounts = views;
    }
}
