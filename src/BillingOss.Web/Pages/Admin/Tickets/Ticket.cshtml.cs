using System.Globalization;
using BillingOss.App;
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
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>
/// Un ticket para el staff: el hilo con las notas internas, responder o anotar con respuestas predefinidas y adjuntos,
/// y cambiar estado, prioridad, departamento y asignado. Los cambios quedan en la auditoría.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportTickets)]
[RequestSizeLimit(SupportUi.MaxRequestBytes)]
public sealed class StaffTicketModel(BillingDbContext db, TicketService tickets, SupportSettings settings, IOptions<BillingOptions> billing, IStringLocalizer<Texts> texts) : PageModel
{
    public Ticket? Ticket { get; private set; }

    public string? AccountName { get; private set; }

    public string? Service { get; private set; }

    public IReadOnlyList<SupportUi.ThreadEntry> Thread { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Departments { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Staff { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name, string? Category)> SavedReplies { get; private set; } = [];

    public List<string> Problems { get; } = [];

    /// <summary>Los ajustes de soporte vigentes, del panel o de la configuración: los usan el formulario y los adjuntos.</summary>
    public SupportOptions Options { get; private set; } = new();

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Options = await settings.Get(HttpContext.RequestAborted);
        await next();
    }

    public BillingOptions Billing => billing.Value;

    [BindProperty]
    public string? Message { get; set; }

    [BindProperty]
    public bool Internal { get; set; }

    /// <summary>En qué estado queda el ticket después de la respuesta. Por defecto, esperando al cliente.</summary>
    [BindProperty]
    public TicketStatus Next { get; set; } = TicketStatus.AwaitingCustomer;

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    [BindProperty]
    public TicketStatus Status { get; set; }

    [BindProperty]
    public TicketPriority Priority { get; set; }

    [BindProperty]
    public long DepartmentId { get; set; }

    /// <summary>Cero es sin asignar.</summary>
    [BindProperty]
    public long AssignedStaffId { get; set; }

    /// <summary>El número del ticket que recibe a este en una unión.</summary>
    [BindProperty]
    public string? MergeInto { get; set; }

    /// <summary>La respuesta del cliente a la encuesta, si la hay.</summary>
    public TicketFeedback? Feedback { get; private set; }

    public async Task<IActionResult> OnGet(long id, [FromQuery(Name = "respuesta")] long? savedReply)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        // Uno unido a otro no tiene mensajes: se ve el que lo recibió.
        if (Ticket!.MergedIntoId is { } target)
        {
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/{target}"));
        }

        Current();
        if (savedReply is { } reply)
        {
            Message = await db.Set<SavedReply>().Where(r => r.Id == reply).Select(r => r.Body).SingleOrDefaultAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostReply(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        Current();

        if (string.IsNullOrWhiteSpace(Message) || Message.Trim().Length > TicketService.MaxBody)
        {
            Problems.Add(texts["Support.MessageRequired"]);
        }

        var (files, problem, file) = await SupportUi.Read(Files, Options, HttpContext.RequestAborted);
        if (problem is not null)
        {
            Problems.Add(texts[problem, file!, Options.MaxAttachments, SupportUi.Megabytes(Options.MaxAttachmentBytes), SupportUi.Extensions(Options)]);
        }

        if (Problems.Count > 0)
        {
            return Page();
        }

        try
        {
            await tickets.Reply(id, new ReplyDraft(Message!, Internal, Internal ? null : Next, files), await Author(), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            Problems.Add(ex.Message);
            return Page();
        }

        return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/{id}#respuesta"));
    }

    public async Task<IActionResult> OnPostUpdate(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        try
        {
            await tickets.Update(id, new TicketChange(Status, Priority, DepartmentId, AssignedStaffId > 0 ? AssignedStaffId : null, Unassign: AssignedStaffId <= 0), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            Problems.Add(ex.Message);
            return Page();
        }

        return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/{id}"));
    }

    /// <summary>Une este ticket a otro, por su número. Los mensajes pasan a ese y este queda cerrado apuntándolo.</summary>
    public async Task<IActionResult> OnPostMerge(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        Current();
        var number = MergeInto?.Trim().TrimStart('#');
        var target = number is { Length: > 0 } ? await db.Set<Ticket>().Where(t => t.Number == number).Select(t => (long?)t.Id).SingleOrDefaultAsync() : null;
        if (target is null)
        {
            Problems.Add(texts["SupportAdmin.MergeUnknown", MergeInto ?? string.Empty]);
            return Page();
        }

        try
        {
            await tickets.Merge(id, target.Value, HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            Problems.Add(ex.Message);
            return Page();
        }

        return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/soporte/{target}"));
    }

    public async Task<IActionResult> OnGetFile(long id, long file) =>
        await Load(id) && await SupportUi.File(db, Thread, file, HttpContext.RequestAborted) is { } found
            ? File(found.Content, found.File.ContentType, found.File.FileName)
            : NotFound();

    /// <summary>El formulario de cambios arranca con lo que tiene el ticket.</summary>
    private void Current() => (Status, Priority, DepartmentId, AssignedStaffId) = (Ticket!.Status, Ticket.Priority, Ticket.DepartmentId, Ticket.AssignedStaffId ?? 0);

    private async Task<Author> Author()
    {
        var id = User.UserId()!.Value;
        var name = await db.Set<StaffUser>().Where(s => s.Id == id).Select(s => s.Name).SingleAsync();
        return new Author(MessageAuthor.Staff, string.IsNullOrWhiteSpace(name) ? User.Identity!.Name! : name, User.Identity!.Name, MessageSource.Panel, StaffId: id);
    }

    private async Task<bool> Load(long id)
    {
        Ticket = await db.Set<Ticket>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);
        if (Ticket is null)
        {
            return false;
        }

        AccountName = Ticket.AccountId is { } account ? await db.Accounts.Where(a => a.Id == account).Select(a => a.Name).SingleOrDefaultAsync() : null;
        Service = Ticket.ServiceId is { } serviceId
            ? await (from s in db.Services
                     join p in db.Products on s.ProductId equals p.Id
                     where s.Id == serviceId
                     select s.Label == null || s.Label == "" ? p.Name : p.Name + " · " + s.Label).SingleOrDefaultAsync()
            : null;
        Thread = await SupportUi.Thread(db, id, internals: true, HttpContext.RequestAborted);
        Feedback = await db.Set<TicketFeedback>().AsNoTracking().SingleOrDefaultAsync(f => f.TicketId == id);
        Departments = (await db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync())
            .Select(d => (d.Id, d.Name)).ToList();
        Staff = (await db.Set<StaffUser>().AsNoTracking().Where(s => s.DisabledAt == null && s.Permissions.Contains(StaffPermissions.SupportTickets)).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Email }).ToListAsync()).Select(s => (s.Id, s.Name.Length > 0 ? s.Name : s.Email)).ToList();
        SavedReplies = (await db.Set<SavedReply>().AsNoTracking().OrderBy(r => r.Category).ThenBy(r => r.Name).Select(r => new { r.Id, r.Name, r.Category }).ToListAsync())
            .Select(r => (r.Id, r.Name, r.Category)).ToList();
        return true;
    }
}
