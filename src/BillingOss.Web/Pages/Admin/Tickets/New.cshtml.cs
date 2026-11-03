using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using BillingOss.App.UseCases;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MimeKit;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>
/// El staff abre una consulta por un cliente (una llamada, un aviso de abuso) o por alguien sin cuenta. Queda esperando
/// al cliente, asignada a quien la abrió, y el aviso sale como cualquier respuesta.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportTickets)]
[RequestSizeLimit(SupportUi.MaxRequestBytes)]
public sealed class StaffTicketNewModel(BillingDbContext db, TicketService tickets, SupportSettings settings, IStringLocalizer<Texts> texts) : PageModel
{
    public IReadOnlyList<(long Id, string Name)> Departments { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Services { get; private set; } = [];

    public string? AccountName { get; private set; }

    public List<(string Field, string Message)> Problems { get; } = [];

    /// <summary>Los ajustes de soporte vigentes, del panel o de la configuración: los usan el formulario y los adjuntos.</summary>
    public SupportOptions Options { get; private set; } = new();

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Options = await settings.Get(HttpContext.RequestAborted);
        await next();
    }

    /// <summary>El número de la cuenta, o el correo de la cuenta o de un usuario del portal con soporte en una sola cuenta.</summary>
    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public string? AccountQuery { get; set; }

    public long? AccountId { get; private set; }

    /// <summary>Otras casillas que reciben las respuestas, separadas por comas o espacios.</summary>
    [BindProperty]
    public string? Cc { get; set; }

    /// <summary>Sin valor por defecto: una casilla sin marcar no viaja. Marcada, el cliente no recibe el aviso al abrirse.</summary>
    [BindProperty]
    public bool Quiet { get; set; }

    [BindProperty]
    public string? RequesterName { get; set; }

    [BindProperty]
    public string? RequesterEmail { get; set; }

    [BindProperty]
    public long DepartmentId { get; set; }

    [BindProperty]
    public long? ServiceId { get; set; }

    [BindProperty]
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    [BindProperty]
    public string? Subject { get; set; }

    [BindProperty]
    public string? Message { get; set; }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    public bool Invalid(string field) => Problems.Any(p => p.Field == field);

    public async Task<IActionResult> OnGet()
    {
        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        await Load();
        if (AccountQuery is { Length: > 0 } && AccountName is null)
        {
            Problems.Add((nameof(AccountId), texts["SupportAdmin.UnknownAccount"]));
        }

        var cc = (Cc ?? string.Empty).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (cc.Length > 10 || cc.Any(c => !MailboxAddress.TryParse(c, out _) || !c.Contains('@', StringComparison.Ordinal)))
        {
            Problems.Add((nameof(Cc), texts["SupportAdmin.CcInvalid"]));
        }

        if (AccountId is null && (RequesterEmail is null || !MailboxAddress.TryParse(RequesterEmail.Trim(), out _)))
        {
            Problems.Add((nameof(RequesterEmail), texts["SupportAdmin.EmailRequired"]));
        }

        if (Departments.All(d => d.Id != DepartmentId))
        {
            Problems.Add((nameof(DepartmentId), texts["Support.ChooseDepartment"]));
        }

        if (ServiceId is { } service && Services.All(s => s.Id != service))
        {
            Problems.Add((nameof(ServiceId), texts["Support.ServiceOfAccount"]));
        }

        if (string.IsNullOrWhiteSpace(Subject) || Subject.Trim().Length > TicketService.MaxSubject)
        {
            Problems.Add((nameof(Subject), texts["Support.SubjectRequired", TicketService.MaxSubject]));
        }

        if (string.IsNullOrWhiteSpace(Message) || Message.Trim().Length > TicketService.MaxBody)
        {
            Problems.Add((nameof(Message), texts["Support.MessageRequired"]));
        }

        var (files, problem, file) = await SupportUi.Read(Files, Options, HttpContext.RequestAborted);
        if (problem is not null)
        {
            Problems.Add((nameof(Files), texts[problem, file!, Options.MaxAttachments, SupportUi.Megabytes(Options.MaxAttachmentBytes), SupportUi.Extensions(Options)]));
        }

        if (Problems.Count > 0)
        {
            return Page();
        }

        var staffId = User.UserId()!.Value;
        var name = await db.Set<StaffUser>().Where(s => s.Id == staffId).Select(s => s.Name).SingleAsync();
        try
        {
            var opened = await tickets.Open(
                new TicketDraft(DepartmentId, Subject!, Message!, AccountId, Priority, ServiceId, files, Blank(RequesterName), Blank(RequesterEmail), cc, Notify: !Quiet),
                new Author(MessageAuthor.Staff, string.IsNullOrWhiteSpace(name) ? User.Identity!.Name! : name, User.Identity!.Name, MessageSource.Panel, StaffId: staffId),
                HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/{opened.Id}"));
        }
        catch (ValidationException ex)
        {
            Problems.Add((string.Empty, ex.Message));
            return Page();
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>La cuenta por su número, o la única que tiene ese correo: el de la cuenta o el de un usuario con soporte en ella.</summary>
    private async Task<long?> FindAccount(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        if (long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        if (!query.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        var lower = query.ToLowerInvariant();
        var accounts = await db.Accounts.Where(a => a.Email.ToLower() == lower).Select(a => a.Id).Take(2).ToListAsync();
        if (accounts.Count == 0)
        {
            var upper = query.ToUpperInvariant();
            accounts = await (from m in db.Set<AccountUser>()
                              join u in db.Set<PortalUser>() on m.UserId equals u.Id
                              where u.NormalizedEmail == upper && (m.IsOwner || m.Capabilities.Contains(AccountCapabilities.Support))
                              select m.AccountId).Distinct().Take(2).ToListAsync();
        }

        return accounts.Count == 1 ? accounts[0] : null;
    }

    private async Task Load()
    {
        Departments = (await db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync())
            .Select(d => (d.Id, d.Name)).ToList();
        AccountId = await FindAccount(AccountQuery?.Trim());
        if (AccountId is { } account)
        {
            AccountName = await db.Accounts.Where(a => a.Id == account).Select(a => a.Name).SingleOrDefaultAsync();
            Services = (await (from s in db.Services.AsNoTracking()
                               join p in db.Products on s.ProductId equals p.Id
                               where s.AccountId == account
                               orderby s.Id
                               select new { s.Id, Name = s.Label == null || s.Label == "" ? p.Name : p.Name + " · " + s.Label }).ToListAsync())
                .Select(s => (s.Id, s.Name)).ToList();
        }
    }
}
