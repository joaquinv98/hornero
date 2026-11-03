using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Un dominio para el staff: estado de facturación y del registro, historial de operaciones con sus errores, la tarea
/// abierta del registrar manual y las acciones, incluidos reintentar y cancelar un pedido que no se completó.
/// </summary>
[Authorize(Policy = StaffPermissions.DomainsRead)]
public sealed class DomainModel(BillingDbContext db, DomainService domains, AuditLog audit) : PageModel
{
    public RegisteredDomain? Domain { get; private set; }

    public string Account { get; private set; } = string.Empty;

    public string Registrar { get; private set; } = string.Empty;

    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>();

    /// <summary>Las opciones que ofrece el registrar, con su precio y si están.</summary>
    public IReadOnlyList<DomainOptionState> Options { get; private set; } = [];

    public IReadOnlyList<DomainOperation> Operations { get; private set; } = [];

    public StaffTask? OpenTask { get; private set; }

    public IReadOnlyList<string> CurrentNameservers { get; private set; } = [];

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    public string? RevealedCode { get; private set; }

    [BindProperty]
    public List<string> Nameservers { get; set; } = [];

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.DomainsManage);

    public bool Live => Domain is { Status: DomainStatus.Active or DomainStatus.Grace };

    /// <summary>Renovar ahora se ofrece si el dominio está vivo y su próxima renovación no tiene una factura abierta.</summary>
    public bool CanRenewNow { get; private set; }

    public DomainOperation? LastOrder => Operations.LastOrDefault(o => DomainJobs.OrderOperations.Contains(o.Operation));

    /// <summary>La operación fallida que se puede reintentar, con la misma regla que el caso de uso.</summary>
    public DomainOperation? FailedOperation => Domain is { Status: not DomainStatus.Cancelled } ? DomainJobs.LastFailed(Operations) : null;

    public async Task<IActionResult> OnGet(long id) => await Load(id) ? Page() : NotFound();

    public Task<IActionResult> OnPostSync(long id) => Act(id, () => domains.SyncNow(id, HttpContext.RequestAborted), "AdminDomain.SyncRequested");

    public Task<IActionResult> OnPostNameservers(long id) => Act(id, () => domains.SetNameservers(id, Nameservers, HttpContext.RequestAborted), "Domain.NameserversRequested");

    public Task<IActionResult> OnPostLock(long id, bool locked) => Act(id, () => domains.SetLock(id, locked, HttpContext.RequestAborted), locked ? "Domain.LockRequested" : "Domain.UnlockRequested");

    public Task<IActionResult> OnPostAuthCode(long id) => Act(id, () => domains.RequestAuthCode(id, HttpContext.RequestAborted), "Domain.AuthCodeRequested");

    public Task<IActionResult> OnPostPrivacy(long id, bool enabled) => OnPostOption(id, DomainOptions.Privacy, enabled);

    /// <summary>Prende o da de baja una opción. Con precio, contratarla deja la factura en la cuenta: se activa con el pago.</summary>
    public Task<IActionResult> OnPostOption(long id, string? option, bool enabled)
    {
        var notice = option == DomainOptions.Privacy ? (enabled ? "Domain.PrivacyRequested" : "Domain.PrivacyOffRequested") : enabled ? "Domain.OptionAdded" : "Domain.OptionRemoved";
        return Act(id, async () =>
        {
            if (await domains.SetOption(id, option ?? string.Empty, enabled, HttpContext.RequestAborted) is not null)
            {
                notice = "Domain.OptionInvoiced";
            }
        }, () => notice);
    }

    public Task<IActionResult> OnPostAutoRenew(long id, bool on) => Act(id, () => domains.SetAutoRenew(id, on, HttpContext.RequestAborted), on ? "Domain.AutoRenewEnabled" : "Domain.AutoRenewDisabled");

    public Task<IActionResult> OnPostRenew(long id) => Act(id, () => domains.RenewNow(id, HttpContext.RequestAborted), "AdminDomain.Renewed");

    public Task<IActionResult> OnPostRetry(long id, string? authCode) => Act(id, () => domains.Retry(id, authCode, HttpContext.RequestAborted), "Domain.RetryRequested");

    public Task<IActionResult> OnPostCancel(long id, string? reason) => Act(id, () => domains.CancelOrder(id, reason ?? string.Empty, HttpContext.RequestAborted), "AdminDomain.Cancelled");

    /// <summary>Mostrar el código EPP queda en la auditoría con quién del staff lo vio.</summary>
    public async Task<IActionResult> OnPostReveal(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        RevealedCode = domains.RevealAuthCode(Domain!);
        if (RevealedCode is null)
        {
            Error = "Domain.NoAuthCode";
            return Page();
        }

        // Quién lo vio y desde dónde lo pone el actor del pedido.
        await audit.Record("domain.auth_code.reveal", string.Create(CultureInfo.InvariantCulture, $"domain:{id}"));
        return Page();
    }

    private Task<IActionResult> Act(long id, Func<Task> action, string notice) => Act(id, action, () => notice);

    /// <summary>Las acciones piden el permiso de operar. Los errores del núcleo se muestran tal cual: el staff los necesita.</summary>
    private async Task<IActionResult> Act(long id, Func<Task> action, Func<string> notice)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await action();
            Notice = notice();
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        await Load(id);
        return Page();
    }

    private async Task<bool> Load(long id)
    {
        Domain = await db.Domains.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
        if (Domain is null)
        {
            return false;
        }

        Account = await db.Accounts.Where(a => a.Id == Domain.AccountId).Select(a => a.Name).SingleAsync();
        Registrar = await db.ModuleInstances.Where(i => i.Id == Domain.RegistrarInstanceId).Select(i => i.Name).SingleAsync();
        Capabilities = await DomainJobs.Capabilities(db, Domain.RegistrarInstanceId, HttpContext.RequestAborted);
        Options = await domains.Options(Domain, HttpContext.RequestAborted);
        CanRenewNow = await domains.CanRenewNow(Domain, HttpContext.RequestAborted);
        Operations = await db.DomainOperations.AsNoTracking().Where(o => o.DomainId == id).OrderBy(o => o.Id).ToListAsync();
        var subject = string.Create(CultureInfo.InvariantCulture, $"domain:{id}");
        OpenTask = await db.StaffTasks.AsNoTracking().Where(t => t.Status == StaffTaskStatus.Open && t.Subject == subject).OrderBy(t => t.Id).FirstOrDefaultAsync();
        CurrentNameservers = [.. (JsonNode.Parse(Domain.Nameservers) as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).Where(n => n.Length > 0)];
        return true;
    }
}
