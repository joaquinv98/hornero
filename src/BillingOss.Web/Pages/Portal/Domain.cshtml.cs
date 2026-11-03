using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Domains;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Un dominio del cliente: vencimiento, renovación con su precio, nameservers, bloqueo, código EPP y titular. Cada
/// cambio va al registrar por la cola, y la página muestra lo que está en curso. Lo que el registrar no declara no se
/// ofrece.
/// </summary>
public sealed class DomainModel(BillingDbContext db, PortalAccess access, DomainService domains, AuditLog audit) : PageModel
{
    public RegisteredDomain? Domain { get; private set; }

    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>();

    /// <summary>Las opciones que ofrece el registrar, con su precio y si están.</summary>
    public IReadOnlyList<DomainOptionState> Options { get; private set; } = [];

    /// <summary>Operaciones que todavía no terminaron, en orden.</summary>
    public IReadOnlyList<DomainOperation> InProgress { get; private set; } = [];

    /// <summary>El registro o la transferencia que falló y se puede reintentar.</summary>
    public DomainOperation? FailedOrder { get; private set; }

    public Document? UnpaidInvoice { get; private set; }

    public bool CanPay { get; private set; }

    public IReadOnlyList<string> CurrentNameservers { get; private set; } = [];

    public JsonObject Registrant { get; private set; } = [];

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    /// <summary>El código EPP recién mostrado. Solo vive en esta respuesta: nunca va a la URL ni a una cookie.</summary>
    public string? RevealedCode { get; private set; }

    [BindProperty]
    public List<string> Nameservers { get; set; } = [];

    [BindProperty]
    public Dictionary<string, string> Contact { get; set; } = [];

    /// <summary>Renovar ahora se ofrece si el dominio está vivo y su próxima renovación no tiene una factura abierta.</summary>
    public bool CanRenewNow { get; private set; }

    public bool Live => Domain is { Status: DomainStatus.Active or DomainStatus.Grace };

    public async Task<IActionResult> OnGet(long id) => await Load(id) ? Page() : NotFound();

    public Task<IActionResult> OnPostNameservers(long id) =>
        Act(id, () => domains.SetNameservers(id, Nameservers, HttpContext.RequestAborted), "Domain.NameserversRequested", "Domain.NameserversInvalid");

    public Task<IActionResult> OnPostLock(long id, bool locked) =>
        Act(id, () => domains.SetLock(id, locked, HttpContext.RequestAborted), locked ? "Domain.LockRequested" : "Domain.UnlockRequested", "Domain.ActionFailed");

    public Task<IActionResult> OnPostAuthCode(long id) =>
        Act(id, () => domains.RequestAuthCode(id, HttpContext.RequestAborted), "Domain.AuthCodeRequested", "Domain.ActionFailed");

    public Task<IActionResult> OnPostPrivacy(long id, bool enabled) => OnPostOption(id, DomainOptions.Privacy, enabled);

    /// <summary>
    /// Prende o da de baja una opción. Con precio, contratarla hace su factura y lleva a pagarla, si la sesión puede ver
    /// facturas: se activa con el pago.
    /// </summary>
    public async Task<IActionResult> OnPostOption(long id, string? option, bool enabled)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        try
        {
            var invoice = await domains.SetOption(id, option ?? string.Empty, enabled, HttpContext.RequestAborted);
            if (invoice is { } invoiceId && CanPay)
            {
                return Redirect(string.Create(CultureInfo.InvariantCulture, $"/facturas/{invoiceId}"));
            }

            Notice = invoice is not null ? "Domain.OptionInvoiced"
                : option == DomainOptions.Privacy ? (enabled ? "Domain.PrivacyRequested" : "Domain.PrivacyOffRequested")
                : enabled ? "Domain.OptionAdded" : "Domain.OptionRemoved";
        }
        catch (ValidationException)
        {
            Error = "Domain.ActionFailed";
        }

        await Load(id);
        return Page();
    }

    public Task<IActionResult> OnPostContacts(long id) =>
        Act(id, () => domains.SetContacts(id, ToJson(Contact), HttpContext.RequestAborted), "Domain.ContactsRequested", "Domain.ContactsInvalid");

    public Task<IActionResult> OnPostAutoRenew(long id, bool on) =>
        Act(id, () => domains.SetAutoRenew(id, on, HttpContext.RequestAborted), on ? "Domain.AutoRenewEnabled" : "Domain.AutoRenewDisabled", "Domain.ActionFailed");

    public Task<IActionResult> OnPostRetry(long id, string? authCode) =>
        Act(id, () => domains.Retry(id, authCode, HttpContext.RequestAborted), "Domain.RetryRequested", "Domain.ActionFailed");

    /// <summary>Renovar ahora lleva a pagar la factura nueva, si la sesión puede ver facturas.</summary>
    public async Task<IActionResult> OnPostRenew(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        try
        {
            var invoice = await domains.RenewNow(id, HttpContext.RequestAborted);
            if (invoice is { } invoiceId && CanPay)
            {
                return Redirect(string.Create(CultureInfo.InvariantCulture, $"/facturas/{invoiceId}"));
            }

            Notice = "Domain.Renewed";
        }
        catch (ValidationException)
        {
            Error = "Domain.RenewFailed";
        }

        await Load(id);
        return Page();
    }

    /// <summary>Mostrar el código EPP pide confirmación y queda en la auditoría (D-09).</summary>
    public async Task<IActionResult> OnPostReveal(long id, bool confirm)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        if (!confirm)
        {
            Error = "Domain.RevealConfirmMissing";
            return Page();
        }

        RevealedCode = domains.RevealAuthCode(Domain!);
        if (RevealedCode is null)
        {
            Error = "Domain.NoAuthCode";
            return Page();
        }

        // Quién lo vio lo pone el actor del pedido: el usuario, o el staff que entró con "ver como cliente".
        await audit.Record("domain.auth_code.reveal", string.Create(CultureInfo.InvariantCulture, $"domain:{id}"));
        return Page();
    }

    private static JsonObject ToJson(Dictionary<string, string> fields)
    {
        var json = new JsonObject();
        foreach (var field in DomainContacts.Fields)
        {
            json[field] = fields.GetValueOrDefault(field);
        }

        return json;
    }

    private async Task<IActionResult> Act(long id, Func<Task> action, string notice, string error)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        try
        {
            await action();
            Notice = notice;
        }
        catch (ValidationException)
        {
            Error = error;
        }

        await Load(id);
        return Page();
    }

    private async Task<bool> Load(long id)
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Domains);
        Domain = await db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && accounts.Contains(d.AccountId));
        if (Domain is null)
        {
            return false;
        }

        Capabilities = await DomainJobs.Capabilities(db, Domain.RegistrarInstanceId, HttpContext.RequestAborted);
        Options = await domains.Options(Domain, HttpContext.RequestAborted);
        CanRenewNow = await domains.CanRenewNow(Domain, HttpContext.RequestAborted);
        var operations = await db.DomainOperations.AsNoTracking().Where(o => o.DomainId == id).OrderBy(o => o.Id).ToListAsync();
        InProgress = [.. operations.Where(o => o.Status == DomainOperationStatus.Pending)];
        FailedOrder = operations.LastOrDefault(o => DomainJobs.OrderOperations.Contains(o.Operation)) is { Status: DomainOperationStatus.Failed } failed && Domain.Status == DomainStatus.Pending ? failed : null;
        CurrentNameservers = [.. (JsonNode.Parse(Domain.Nameservers) as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).Where(n => n.Length > 0)];

        var invoices = access.AccountIds(User, AccountCapabilities.Invoices);
        CanPay = await invoices.ContainsAsync(Domain.AccountId);
        var documents = db.DocumentLines.Where(l => l.DomainId == id).Select(l => l.DocumentId);
        UnpaidInvoice = (await db.Documents.AsNoTracking().Where(d => documents.Contains(d.Id) && d.IssueDate != null && d.Kind == DocumentKind.Invoice).OrderBy(d => d.Id).ToListAsync())
            .FirstOrDefault(d => d.Pending > 0m && d.Status != InvoiceStatus.Cancelled);

        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == Domain.AccountId);
        var tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == Domain.AccountId && t.ValidTo == null);
        Registrant = DomainContacts.Registrant(account, tax, Domain.Registrant);
        return true;
    }
}
