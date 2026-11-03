using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>Respuestas predefinidas: el staff las inserta al responder y las edita quien configura el soporte.</summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class SavedRepliesModel(BillingDbContext db, AuditLog audit, TimeProvider time, IStringLocalizer<Texts> texts) : PageModel
{
    public IReadOnlyList<SavedReply> Replies { get; private set; } = [];

    public List<string> Problems { get; } = [];

    [BindProperty(SupportsGet = true, Name = "id")]
    public long Id { get; set; }

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? Category { get; set; }

    [BindProperty]
    public string? Body { get; set; }

    public async Task<IActionResult> OnGet()
    {
        if (Id > 0)
        {
            if (await db.Set<SavedReply>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == Id) is not { } reply)
            {
                return NotFound();
            }

            (Name, Category, Body) = (reply.Name, reply.Category, reply.Body);
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 200 || (Category?.Trim().Length ?? 0) > 200)
        {
            Problems.Add(texts["SupportAdmin.NameRequired"]);
        }

        if (string.IsNullOrWhiteSpace(Body) || Body.Trim().Length > TicketService.MaxBody)
        {
            Problems.Add(texts["Support.MessageRequired"]);
        }

        var reply = Id > 0 ? await db.Set<SavedReply>().SingleOrDefaultAsync(r => r.Id == Id) : new SavedReply();
        if (reply is null)
        {
            return NotFound();
        }

        if (Problems.Count > 0)
        {
            await Load();
            return Page();
        }

        (reply.Name, reply.Category, reply.Body, reply.UpdatedAt) = (Name!.Trim(), string.IsNullOrWhiteSpace(Category) ? null : Category.Trim(), Body!.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), time.GetUtcNow());
        if (Id == 0)
        {
            db.Add(reply);
        }

        await db.SaveChangesAsync();
        await audit.Record("support.saved_reply.save", string.Create(CultureInfo.InvariantCulture, $"saved_reply:{reply.Id}"), new { name = reply.Name });
        return Redirect("/admin/soporte/respuestas");
    }

    public async Task<IActionResult> OnPostDelete(long remove)
    {
        if (await db.Set<SavedReply>().Where(r => r.Id == remove).ExecuteDeleteAsync() == 0)
        {
            return NotFound();
        }

        await audit.Record("support.saved_reply.delete", string.Create(CultureInfo.InvariantCulture, $"saved_reply:{remove}"));
        return Redirect("/admin/soporte/respuestas");
    }

    private async Task Load() =>
        Replies = await db.Set<SavedReply>().AsNoTracking().OrderBy(r => r.Category).ThenBy(r => r.Name).ToListAsync();
}
