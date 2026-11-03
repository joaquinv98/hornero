using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Pedir un dominio en una página: buscar, elegir los años con el precio a la vista, revisar el titular y confirmar.
/// La transferencia es el mismo camino con el código EPP. El pedido emite la factura y lleva a pagarla: el registrar
/// recién actúa con la factura paga.
/// </summary>
public sealed class DomainNewModel(BillingDbContext db, PortalAccess access, DomainSearch search, TldPricing pricing, DomainOrderService orders) : PageModel
{
    public sealed record AccountOption(long Id, string Name, Currency Currency);

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>El nombre elegido: con él la página pasa a confirmar el pedido.</summary>
    [BindProperty(SupportsGet = true, Name = "nombre")]
    public string? Chosen { get; set; }

    [BindProperty(SupportsGet = true, Name = "transferir")]
    public bool Transfer { get; set; }

    /// <summary>Usar un titular propio en vez del que sale de la cuenta.</summary>
    [BindProperty(SupportsGet = true, Name = "titular")]
    public bool OwnRegistrant { get; set; }

    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    [BindProperty]
    public int Years { get; set; } = 1;

    [BindProperty]
    public string? AuthCode { get; set; }

    /// <summary>Registrar con la protección de identidad, si el registrar de la extensión la ofrece.</summary>
    [BindProperty]
    public bool Privacy { get; set; }

    public bool PrivacyOffered { get; private set; }

    /// <summary>Con la gestión de DNS, si tiene precio: sin precio está siempre y no se pide.</summary>
    [BindProperty]
    public bool Dns { get; set; }

    [BindProperty]
    public bool EmailForwarding { get; set; }

    /// <summary>Precio anual vigente de cada opción en la moneda de la cuenta. Cero es gratis.</summary>
    public IReadOnlyDictionary<string, decimal> OptionPrices { get; private set; } = new Dictionary<string, decimal>();

    /// <summary>Las opciones con precio que el registrar de la extensión ofrece y se eligen al pedir.</summary>
    public IReadOnlyList<string> PaidOptions { get; private set; } = [];

    [BindProperty]
    public Dictionary<string, string> Contact { get; set; } = [];

    public IReadOnlyList<AccountOption> Accounts { get; private set; } = [];

    public AccountOption? Account { get; private set; }

    public IReadOnlyList<DomainAvailability> Results { get; private set; } = [];

    /// <summary>Los precios del TLD elegido, uno por cantidad de años que se ofrece.</summary>
    public IReadOnlyList<TldOffer> Offers { get; private set; } = [];

    public string? Name { get; private set; }

    public JsonObject Registrant { get; private set; } = [];

    /// <summary>Un registrar con API exige el titular completo. El manual no: el trámite lo hace el staff con el cliente.</summary>
    public bool RegistrantRequired { get; private set; }

    public bool ShowRegistrantForm => OwnRegistrant || (RegistrantRequired && DomainContacts.Missing(Registrant).Count > 0);

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        if (!await LoadAccounts())
        {
            return NotFound();
        }

        if (Chosen is not null)
        {
            await Confirm();
        }
        else if (!string.IsNullOrWhiteSpace(Q))
        {
            try
            {
                Results = await search.Check(await search.Candidates(Q, Account!.Currency, HttpContext.RequestAborted), Account!.Currency, HttpContext.RequestAborted);
            }
            catch (ValidationException)
            {
                Error = "DomainNew.InvalidName";
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        if (!await LoadAccounts())
        {
            return NotFound();
        }

        if (!await Confirm())
        {
            return Page();
        }

        var registrant = ShowRegistrantForm ? Typed() : null;
        if (registrant is not null && RegistrantRequired && DomainContacts.Missing(DomainContacts.Clean(registrant)).Count > 0)
        {
            OwnRegistrant = true;
            Error = "DomainNew.RegistrantMissing";
            return Page();
        }

        if (Transfer && string.IsNullOrWhiteSpace(AuthCode))
        {
            Error = "DomainNew.AuthCodeMissing";
            return Page();
        }

        try
        {
            var order = new DomainOrder(Name!, Transfer ? 1 : Years, AuthCode: AuthCode, Registrant: registrant, Privacy: Privacy && PrivacyOffered,
                Dns: Dns && PaidOptions.Contains(DomainOptions.Dns), EmailForwarding: EmailForwarding && PaidOptions.Contains(DomainOptions.EmailForwarding));
            var placed = Transfer
                ? await orders.Transfer(Account!.Id, order, HttpContext.RequestAborted)
                : await orders.Register(Account!.Id, order, HttpContext.RequestAborted);
            var canPay = await access.AccountIds(User, AccountCapabilities.Invoices).ContainsAsync(Account!.Id);
            return Redirect(placed.InvoiceId is { } invoice && canPay
                ? string.Create(CultureInfo.InvariantCulture, $"/facturas/{invoice}")
                : string.Create(CultureInfo.InvariantCulture, $"/dominios/{placed.DomainId}"));
        }
        catch (ValidationException)
        {
            Error = await db.Domains.AnyAsync(d => d.Name == Name && (d.Status == DomainStatus.Pending || d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace))
                ? "DomainNew.InSystem"
                : "DomainNew.OrderFailed";
            return Page();
        }
    }

    /// <summary>El enlace para elegir un nombre, conservando la cuenta.</summary>
    public string ChooseUrl(string name, bool transfer) =>
        $"/dominios/nuevo?nombre={Uri.EscapeDataString(name)}{(transfer ? "&transferir=true" : string.Empty)}{(AccountId is { } a ? string.Create(CultureInfo.InvariantCulture, $"&cuenta={a}") : string.Empty)}";

    private JsonObject Typed()
    {
        var json = new JsonObject();
        foreach (var field in DomainContacts.Fields)
        {
            json[field] = Contact.GetValueOrDefault(field);
        }

        return json;
    }

    private async Task<bool> LoadAccounts()
    {
        var ids = access.AccountIds(User, AccountCapabilities.Domains);
        Accounts = (await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).OrderBy(a => a.Id).Select(a => new { a.Id, a.Name, a.Currency }).ToListAsync())
            .Select(a => new AccountOption(a.Id, a.Name, a.Currency)).ToList();
        Account = Accounts.FirstOrDefault(a => a.Id == AccountId) ?? Accounts.FirstOrDefault();
        return Account is not null;
    }

    /// <summary>Arma la confirmación del nombre elegido: precios del TLD y titular. Falso si no se puede pedir.</summary>
    private async Task<bool> Confirm()
    {
        if (!DomainNames.TryNormalize(Chosen, out var name))
        {
            Error = "DomainNew.InvalidName";
            return false;
        }

        Name = name;
        var tld = "." + DomainNames.Split(name).Extension;
        Offers = [.. (await pricing.Current(Account!.Currency, HttpContext.RequestAborted))
            .Where(o => o.Tld == tld && (Transfer ? o.Transfer is not null && o.Years == 1 : o.Register is not null))
            .GroupBy(o => o.Years).Select(g => g.OrderByDescending(o => o.ValidFrom).ThenBy(o => o.RegistrarInstanceId).First())
            .OrderBy(o => o.Years)];
        if (Offers.Count == 0)
        {
            Error = "DomainNew.NotOffered";
            return false;
        }

        Years = Offers.Any(o => o.Years == Years) ? Years : Offers[0].Years;
        var instance = Offers.Single(o => o.Years == Years).RegistrarInstanceId;
        var capabilities = await DomainJobs.Capabilities(db, instance, HttpContext.RequestAborted);
        if (Transfer && !capabilities.Contains("registrar.transfer"))
        {
            Error = "DomainNew.NotOffered";
            return false;
        }

        var offered = await TldPricing.OfferedOptions(db, tld, HttpContext.RequestAborted);
        PrivacyOffered = !Transfer && capabilities.Contains("registrar.privacy") && offered.Contains(DomainOptions.Privacy);
        OptionPrices = await pricing.OptionPrices(Account.Currency, HttpContext.RequestAborted);
        PaidOptions = [.. new[] { DomainOptions.Dns, DomainOptions.EmailForwarding }.Where(o => OptionPrices[o] > 0m && capabilities.Contains(DomainOptions.Capability(o)) && offered.Contains(o))];

        RegistrantRequired = await (from i in db.ModuleInstances
                                    join m in db.ModuleInstallations on i.InstallationId equals m.Id
                                    where i.Id == instance
                                    select m.BaseUrl).SingleAsync() != ManualModule.Address;
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == Account.Id);
        var tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == Account.Id && t.ValidTo == null);
        // Sin campos Contact[...] el binder llena el diccionario con todo el formulario: solo cuenta lo que vino con prefijo.
        var typed = Request.HasFormContentType && Request.Form.Keys.Any(k => k.StartsWith("Contact[", StringComparison.Ordinal));
        Registrant = typed ? Typed() : DomainContacts.Registrant(account, tax, null);
        return true;
    }
}
