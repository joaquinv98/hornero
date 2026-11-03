using System.Globalization;
using BillingOss.App;
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
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Portal.Tickets;

/// <summary>
/// Una consulta con su hilo, sin las notas internas del staff. Lo ajeno y lo de una cuenta sin la capacidad de soporte
/// da 404, igual que lo que no existe (sección 13 del modelo de dominio).
/// </summary>
[RequestSizeLimit(SupportUi.MaxRequestBytes)]
public sealed class TicketModel(BillingDbContext db, PortalAccess access, TicketService tickets, SupportSettings settings, IOptions<BillingOptions> billing, IStringLocalizer<Texts> texts) : PageModel
{
    public Ticket? Ticket { get; private set; }

    public string Department { get; private set; } = string.Empty;

    public string? Service { get; private set; }

    public IReadOnlyList<SupportUi.ThreadEntry> Thread { get; private set; } = [];

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

    /// <summary>Mirando como cliente se lee nada más: el staff responde desde el panel.</summary>
    public bool CanWrite => User.ActingAccount() is null;

    [BindProperty]
    public string? Message { get; set; }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    /// <summary>La ficha de la encuesta, cuando el ticket está cerrado, se la pidió y todavía no la respondió.</summary>
    public string? SurveyToken { get; private set; }

    public async Task<IActionResult> OnGet(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        // Uno unido a otro de la misma cuenta lleva al que lo recibió.
        return Ticket!.MergedIntoId is { } target ? Redirect(string.Create(CultureInfo.InvariantCulture, $"/soporte/{target}")) : Page();
    }

    public async Task<IActionResult> OnPostReply(long id)
    {
        if (!CanWrite || User.UserId() is not { } userId || !await Load(id))
        {
            return NotFound();
        }

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

        var name = await db.Accounts.Where(a => a.Id == Ticket!.AccountId).Select(a => a.Name).SingleAsync();
        try
        {
            await tickets.Reply(id, new ReplyDraft(Message!, Attachments: files), new Author(MessageAuthor.Customer, name, User.Identity!.Name, MessageSource.Portal, UserId: userId), HttpContext.RequestAborted);
        }
        catch (ValidationException)
        {
            Problems.Add(texts["Support.NotSent"]);
            return Page();
        }

        return Redirect(string.Create(CultureInfo.InvariantCulture, $"/soporte/{id}#respuesta"));
    }

    public async Task<IActionResult> OnPostClose(long id)
    {
        if (!CanWrite || !await Load(id))
        {
            return NotFound();
        }

        if (Ticket!.Status != TicketStatus.Closed)
        {
            await tickets.Close(id, HttpContext.RequestAborted);
        }

        return Redirect(string.Create(CultureInfo.InvariantCulture, $"/soporte/{id}"));
    }

    /// <summary>Un adjunto de un mensaje que el cliente ve. Se baja siempre como archivo, nunca se muestra en la página.</summary>
    public async Task<IActionResult> OnGetFile(long id, long file) =>
        await Load(id) && await SupportUi.File(db, Thread, file, HttpContext.RequestAborted) is { } found
            ? File(found.Content, found.File.ContentType, found.File.FileName)
            : NotFound();

    private async Task<bool> Load(long id)
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Support);
        Ticket = await db.Set<Ticket>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.AccountId != null && accounts.Contains(t.AccountId.Value));
        if (Ticket is null)
        {
            return false;
        }

        Department = await db.Set<Department>().Where(d => d.Id == Ticket.DepartmentId).Select(d => d.Name).SingleAsync();
        Service = Ticket.ServiceId is { } serviceId
            ? await (from s in db.Services
                     join p in db.Products on s.ProductId equals p.Id
                     where s.Id == serviceId
                     select s.Label == null || s.Label == "" ? p.Name : p.Name + " · " + s.Label).SingleOrDefaultAsync()
            : null;
        Thread = await SupportUi.Thread(db, id, internals: false, HttpContext.RequestAborted);
        SurveyToken = Ticket.Status == TicketStatus.Closed && Ticket.FeedbackToken is { } token && !await db.Set<TicketFeedback>().AnyAsync(f => f.TicketId == id) ? token : null;
        return true;
    }
}
