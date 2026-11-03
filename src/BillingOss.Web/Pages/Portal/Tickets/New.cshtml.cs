using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using BillingOss.App.UseCases;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Portal.Tickets;

/// <summary>Nueva consulta desde el portal: departamento, servicio opcional, prioridad, texto y adjuntos.</summary>
[RequestSizeLimit(SupportUi.MaxRequestBytes)]
public sealed class TicketNewModel(BillingDbContext db, PortalAccess access, TicketService tickets, SupportSettings settings, IStringLocalizer<Texts> texts) : PageModel
{
    public sealed record ServiceOption(long Id, long AccountId, string Name);

    public IReadOnlyList<(long Id, string Name)> Accounts { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name, string? Description)> Departments { get; private set; } = [];

    public IReadOnlyList<ServiceOption> Services { get; private set; } = [];

    /// <summary>Campo y mensaje de cada problema, para el resumen de arriba y para marcar el campo.</summary>
    public List<(string Field, string Message)> Problems { get; } = [];

    /// <summary>Los ajustes de soporte vigentes, del panel o de la configuración: los usan el formulario y los adjuntos.</summary>
    public SupportOptions Options { get; private set; } = new();

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Options = await settings.Get(HttpContext.RequestAborted);
        await next();
    }

    [BindProperty]
    public long AccountId { get; set; }

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

    public async Task<IActionResult> OnGet([FromQuery(Name = "servicio")] long? service)
    {
        if (User.ActingAccount() is not null)
        {
            return NotFound();
        }

        await Load();
        if (Services.FirstOrDefault(s => s.Id == service) is { } chosen)
        {
            (ServiceId, AccountId) = (chosen.Id, chosen.AccountId);
        }

        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        if (User.ActingAccount() is not null || User.UserId() is not { } userId)
        {
            return NotFound();
        }

        await Load();
        if (Accounts.Count == 1)
        {
            AccountId = Accounts[0].Id;
        }

        if (Accounts.All(a => a.Id != AccountId))
        {
            Problems.Add((nameof(AccountId), texts["Support.ChooseAccount"]));
        }

        if (Departments.All(d => d.Id != DepartmentId))
        {
            Problems.Add((nameof(DepartmentId), texts["Support.ChooseDepartment"]));
        }

        if (ServiceId is { } service && Services.All(s => s.Id != service || s.AccountId != AccountId))
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

        var account = Accounts.Single(a => a.Id == AccountId);
        try
        {
            var opened = await tickets.Open(
                new TicketDraft(DepartmentId, Subject!, Message!, AccountId, Priority, ServiceId, files),
                new Author(MessageAuthor.Customer, account.Name, User.Identity!.Name, MessageSource.Portal, UserId: userId),
                HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/soporte/{opened.Id}?creada=1"));
        }
        catch (ValidationException)
        {
            Problems.Add((string.Empty, texts["Support.NotSent"]));
            return Page();
        }
    }

    private async Task Load()
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Support);
        Accounts = (await db.Accounts.AsNoTracking().Where(a => accounts.Contains(a.Id)).OrderBy(a => a.Id).Select(a => new { a.Id, a.Name }).ToListAsync())
            .Select(a => (a.Id, a.Name)).ToList();
        Departments = (await db.Set<Department>().AsNoTracking().Where(d => !d.Hidden).OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.Description }).ToListAsync()).Select(d => (d.Id, d.Name, d.Description)).ToList();

        // Los servicios se ofrecen a quien además puede verlos.
        var visible = access.AccountIds(User, AccountCapabilities.Services);
        Services = await (from s in db.Services.AsNoTracking()
                          join p in db.Products on s.ProductId equals p.Id
                          where accounts.Contains(s.AccountId) && visible.Contains(s.AccountId)
                          orderby s.AccountId, s.Id
                          select new ServiceOption(s.Id, s.AccountId, s.Label == null || s.Label == "" ? p.Name : p.Name + " · " + s.Label)).ToListAsync();
    }
}
