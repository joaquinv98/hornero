using BillingOss.App.Persistence;
using BillingOss.App.Support;
using BillingOss.App.UseCases;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Support;

/// <summary>
/// La encuesta de satisfacción de un ticket, con la ficha del enlace del correo y sin iniciar sesión: la puede responder
/// también quien escribió por correo sin ser de una cuenta. Una sola respuesta por ticket.
/// </summary>
public sealed class SurveyModel(BillingDbContext db, TicketService tickets) : PageModel
{
    public string Number { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    /// <summary>Ya estaba respondida antes de este pedido.</summary>
    public bool Answered { get; private set; }

    /// <summary>Se acaba de guardar la respuesta.</summary>
    public bool Thanks { get; private set; }

    public string? Problem { get; private set; }

    [BindProperty]
    public int? Rating { get; set; }

    [BindProperty]
    public string? Comment { get; set; }

    public async Task<IActionResult> OnGet(string token) => await Load(token) ? Page() : NotFound();

    public async Task<IActionResult> OnPost(string token)
    {
        if (!await Load(token))
        {
            return NotFound();
        }

        if (Answered)
        {
            return Page();
        }

        if (Rating is not (>= 0 and <= 10))
        {
            Problem = "Survey.ChooseRating";
            return Page();
        }

        try
        {
            Thanks = await tickets.Rate(token, Rating.Value, Comment, HttpContext.RequestAborted);
            Answered = !Thanks;
        }
        catch (ValidationException)
        {
            Problem = "Survey.CommentTooLong";
        }

        return Page();
    }

    private async Task<bool> Load(string token)
    {
        var ticket = await db.Set<Ticket>().AsNoTracking().Where(t => t.FeedbackToken == token).Select(t => new { t.Id, t.Number, t.Subject }).SingleOrDefaultAsync(HttpContext.RequestAborted);
        if (ticket is null)
        {
            return false;
        }

        (Number, Subject) = (ticket.Number, ticket.Subject);
        Answered = await db.Set<TicketFeedback>().AnyAsync(f => f.TicketId == ticket.Id, HttpContext.RequestAborted);
        return true;
    }
}
