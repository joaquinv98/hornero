using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Pagar una o varias facturas de una cuenta en la misma moneda (pasos de pago de D-19, sección 4.6): qué se paga, el
/// saldo a favor si hay, el medio (cada pasarela activa que cobra esa moneda, o en otra con la cotización vigente, y la
/// transferencia) y, si la pasarela lo ofrece, guardar el medio para el débito automático con la casilla de
/// consentimiento (sección 4.5 del diseño de cobros). Sin facturas elegidas, todas las que se deben en la moneda pedida.
/// </summary>
public sealed class PayModel(BillingDbContext db, PortalAccess access, ExchangeRateService rates, InvoiceService invoices, IStringLocalizer<Texts> texts, IConfiguration config) : PageModel
{
    /// <summary>El valor del medio "transferencia". Las pasarelas van como "g" y el ID de la instancia.</summary>
    public const string Transfer = "transferencia";

    /// <summary>
    /// Un medio: lo que cobra (<paramref name="Amount"/> en <paramref name="Currency"/>) y, si cobra en otra moneda, la
    /// cotización con su fuente y su fecha.
    /// </summary>
    public sealed record Choice(string Value, string Name, bool CanSave, Currency Currency, decimal Amount, Conversion? Conversion = null);

    /// <summary>Facturas elegidas: ?factura=12&amp;factura=13.</summary>
    [BindProperty(SupportsGet = true)]
    public long[] Factura { get; set; } = [];

    /// <summary>Sin facturas elegidas, todas las que se deben en esta moneda.</summary>
    [BindProperty(SupportsGet = true, Name = "moneda")]
    public string? CurrencyCode { get; set; }

    /// <summary>Sin facturas elegidas, las de esta cuenta: cada cuenta paga lo suyo.</summary>
    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    public IReadOnlyList<Document> Documents { get; private set; } = [];

    public IReadOnlyList<Choice> Choices { get; private set; } = [];

    /// <summary>Pasarelas que no pueden cobrar hoy porque falta la cotización a su moneda.</summary>
    public IReadOnlyList<string> WithoutRate { get; private set; } = [];

    /// <summary>Hay un débito automático en curso sobre alguna de las facturas: no se ofrece otro medio.</summary>
    public bool InFlight { get; private set; }

    /// <summary>Saldo a favor de la cuenta en la moneda de las facturas: se puede usar antes de pagar el resto.</summary>
    public decimal Credit { get; private set; }

    /// <summary>Clave del texto del error, si lo hubo.</summary>
    public string? Error { get; private set; }

    /// <summary>Se puede elegir medio: hay facturas de una sola cuenta y una sola moneda.</summary>
    public bool Ready => Documents.Count > 0 && Error is not ("Pay.MixedCurrencies" or "Pay.MixedAccounts" or "Pay.Nothing");

    public decimal Total => Documents.Sum(d => d.Pending);

    public Currency Currency => Documents[0].Currency;

    /// <summary>El texto exacto que acepta el cliente: es el que se guarda, con su huella, como constancia.</summary>
    public string ConsentText => Consent(texts, config);

    public static string Consent(IStringLocalizer<Texts> texts, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(config);
        return texts["Pay.Consent", config["Ui:Brand"] ?? "billing-oss"].Value;
    }

    /// <summary>Las facturas de la sesión con saldo, en el orden en que se imputan. Lo ajeno no aparece.</summary>
    public static async Task<List<Document>> Payable(BillingDbContext db, PortalAccess access, ClaimsPrincipal user, IEnumerable<long> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        var wanted = ids.Distinct().Take(50).ToList();
        var accounts = access.AccountIds(user, AccountCapabilities.Invoices);
        var docs = await db.Documents.AsNoTracking()
            .Where(d => wanted.Contains(d.Id) && d.IssueDate != null && d.Kind != DocumentKind.CreditNote && accounts.Contains(d.AccountId))
            .OrderBy(d => d.DueDate).ThenBy(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        return [.. docs.Where(d => d.Pending > 0m && d.Status != InvoiceStatus.Cancelled)];
    }

    public async Task<IActionResult> OnGet() => await Load() ? Page() : NotFound();

    public async Task<IActionResult> OnPost(string? method, bool save)
    {
        if (!await Load())
        {
            return NotFound();
        }

        if (!Ready || InFlight || Choices.Count == 0)
        {
            return Page();
        }

        var choice = Choices.FirstOrDefault(c => c.Value == method);
        if (choice is null)
        {
            Error = "Pay.ChooseMethod";
            return Page();
        }

        if (choice.Value == Transfer)
        {
            return Redirect("/pagar/transferencia?" + Query(Documents));
        }

        if (save && !choice.CanSave)
        {
            Error = "Pay.CannotSave";
            return Page();
        }

        try
        {
            var consent = save ? new ConsentInput(ConsentText, CultureInfo.CurrentUICulture.Name, Request.Headers.UserAgent.ToString()) : null;
            var instance = long.Parse(choice.Value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
            var started = await HttpContext.RequestServices.GetRequiredService<GatewayPayments>()
                .Checkout(Documents[0].AccountId, [.. Documents.Select(d => d.Id)], instance, choice.Currency.Code, consent, HttpContext.RequestAborted);
            return Redirect(started.RedirectUrl);
        }
        catch (ValidationException)
        {
            Error = "Invoice.NotPayable";
        }
        catch (Exception ex) when (ex is PermanentFailureException or RetryLaterException)
        {
            Error = "Invoice.GatewayError";
        }

        return Page();
    }

    /// <summary>Paga con el saldo a favor, de la que vence antes a la que vence después. Lo que falte se paga con un medio.</summary>
    public async Task<IActionResult> OnPostCredit()
    {
        if (!await Load() || !Ready || Credit <= 0m)
        {
            return NotFound();
        }

        await invoices.ApplyCredit(Documents[0].AccountId, [.. Documents.Select(d => d.Id)], HttpContext.RequestAborted);
        var left = Documents.Select(d => d.Id).ToList();
        var pending = await db.Documents.Where(d => left.Contains(d.Id) && d.Total - d.Credited - d.Allocated > 0).ToListAsync();
        return pending.Count > 0 ? Redirect("/pagar?" + Query(pending)) : Redirect("/facturas");
    }

    private static string Query(IEnumerable<Document> docs) =>
        string.Join('&', docs.Select(d => string.Create(CultureInfo.InvariantCulture, $"factura={d.Id}")));

    private async Task<bool> Load()
    {
        var ct = HttpContext.RequestAborted;
        if (Factura.Length > 0)
        {
            // Una ajena, una que no existe o una ya paga dan lo mismo: no está para pagar.
            Documents = await Payable(db, access, User, Factura, ct);
            if (Documents.Count == 0 || Documents.Count != Factura.Distinct().Count())
            {
                return false;
            }
        }
        else
        {
            var accounts = access.AccountIds(User, AccountCapabilities.Invoices);
            var all = (await db.Documents.AsNoTracking()
                    .Where(d => accounts.Contains(d.AccountId) && (AccountId == null || d.AccountId == AccountId) && d.IssueDate != null && d.Kind != DocumentKind.CreditNote)
                    .OrderBy(d => d.DueDate).ThenBy(d => d.Id).ToListAsync(ct))
                .Where(d => d.Pending > 0m && d.Status != InvoiceStatus.Cancelled).ToList();
            var currencies = all.Select(d => d.Currency.Code).Distinct().ToList();
            var code = CurrencyCode?.ToUpperInvariant() ?? (currencies.Count == 1 ? currencies[0] : null);
            Documents = [.. all.Where(d => d.Currency.Code == code)];
            if (code is null && all.Count > 0)
            {
                Error = "Pay.MixedCurrencies";
                return true;
            }

            if (Documents.Count == 0)
            {
                Error = "Pay.Nothing";
                return true;
            }
        }

        if (Documents.Select(d => d.Currency).Distinct().Count() > 1)
        {
            Error = "Pay.MixedCurrencies";
            return true;
        }

        if (Documents.Select(d => d.AccountId).Distinct().Count() > 1)
        {
            Error = "Pay.MixedAccounts";
            return true;
        }

        foreach (var doc in Documents)
        {
            InFlight |= await GatewayPayments.AutoChargeInFlight(db, doc.Id, ct);
        }

        var accountId = Documents[0].AccountId;
        var currency = Currency;
        Credit = await db.CreditBalances.Where(b => b.AccountId == accountId && b.Currency == currency).SumAsync(b => b.Balance, ct);
        Choices = await LoadChoices(ct);
        return true;
    }

    /// <summary>
    /// Las pasarelas que cobran la moneda de las facturas, o en otra con la cotización vigente, en el orden del asistente,
    /// y la transferencia si hay una cuenta en esa moneda. Una instancia que no pasó por el asistente se ofrece como
    /// antes, con su nombre. Guardar el medio para el débito solo se ofrece sin conversión: el débito cobra cada factura
    /// en su moneda.
    /// </summary>
    private async Task<IReadOnlyList<Choice>> LoadChoices(CancellationToken ct)
    {
        var choices = new List<(int Position, Choice Choice)>();
        var missing = new List<string>();
        var currency = Currency;
        if (HttpContext.RequestServices.GetService<GatewayPayments>() is not null)
        {
            // Con el débito por suscripción no se ofrece guardar el medio para el débito: es uno de los dos.
            var subscribed = await AutoChargeService.HasSubscription(db, Documents[0].AccountId, ct);
            var setups = await db.GatewaySetups.AsNoTracking().ToDictionaryAsync(s => s.ModuleInstanceId, ct);
            var instances = await db.ModuleInstances.AsNoTracking()
                .Join(db.ModuleInstallations, i => i.InstallationId, m => m.Id, (i, m) => new { i.Id, i.Name, m.Manifest })
                .ToListAsync(ct);
            foreach (var x in instances)
            {
                var manifest = JsonNode.Parse(x.Manifest)?.AsObject() ?? [];
                var capabilities = (manifest["capabilities"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
                if (!capabilities.Contains("payment.checkout"))
                {
                    continue;
                }

                Currency charge;
                try
                {
                    charge = GatewayPayments.ChargeCurrency(manifest, currency);
                }
                catch (ValidationException)
                {
                    continue;
                }

                setups.TryGetValue(x.Id, out var setup);
                if (setup is not null && !setup.Offers(charge))
                {
                    continue;
                }

                var name = setup?.DisplayName ?? x.Name;
                var value = string.Create(CultureInfo.InvariantCulture, $"g{x.Id}");
                if (charge == currency)
                {
                    var canSave = setup is { AutoCharge: true } && capabilities.Contains("payment.saved_methods") && !subscribed;
                    choices.Add((setup?.Position ?? int.MaxValue, new Choice(value, name, canSave, currency, Total)));
                }
                else if (await rates.Current(currency, charge, ct) is { } conversion)
                {
                    choices.Add((setup?.Position ?? int.MaxValue, new Choice(value, name, false, charge, conversion.Apply(Total), conversion)));
                }
                else
                {
                    missing.Add(name);
                }
            }
        }

        WithoutRate = missing;
        var list = choices.OrderBy(c => c.Position).ThenBy(c => c.Choice.Value, StringComparer.Ordinal).Select(c => c.Choice).ToList();
        if (await db.TransferAccounts.AnyAsync(a => a.Active && a.Currency == currency, ct))
        {
            list.Add(new Choice(Transfer, texts["Pay.Transfer"], false, currency, Total));
        }

        return list;
    }
}
