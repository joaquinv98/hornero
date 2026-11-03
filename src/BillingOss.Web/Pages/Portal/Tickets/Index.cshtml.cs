using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Portal.Tickets;

/// <summary>Las consultas de las cuentas que la sesión puede ver con la capacidad de soporte.</summary>
public sealed class TicketListModel(BillingDbContext db, PortalAccess access, IOptions<BillingOptions> billing) : PageModel
{
    public sealed record Row(long Id, string Number, string Subject, string Department, TicketStatus Status, DateTimeOffset LastMessageAt);

    public static IReadOnlyList<(string Value, string Label)> Views { get; } = [("abiertas", "Support.ViewOpen"), ("cerradas", "Support.ViewClosed"), ("todas", "Support.ViewAll")];

    [BindProperty(SupportsGet = true, Name = "ver")]
    public string View { get; set; } = "abiertas";

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public Dictionary<string, int> Counts { get; } = [];

    /// <summary>Mirando como cliente se lee nada más: el staff responde desde el panel.</summary>
    public bool CanWrite => User.ActingAccount() is null;

    public BillingOptions Billing => billing.Value;

    public async Task OnGet()
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Support);
        var mine = db.Set<Ticket>().AsNoTracking().Where(t => t.AccountId != null && accounts.Contains(t.AccountId.Value) && t.MergedIntoId == null);
        var all = await mine.CountAsync();
        var open = await mine.CountAsync(t => t.Status != TicketStatus.Closed);
        Counts["abiertas"] = open;
        Counts["cerradas"] = all - open;
        Counts["todas"] = all;
        var shown = View switch
        {
            "cerradas" => mine.Where(t => t.Status == TicketStatus.Closed),
            "todas" => mine,
            _ => mine.Where(t => t.Status != TicketStatus.Closed),
        };
        View = Views.Any(v => v.Value == View) ? View : "abiertas";
        Rows = await (from t in shown
                      join d in db.Set<Department>() on t.DepartmentId equals d.Id
                      orderby t.LastMessageAt descending, t.Id descending
                      select new Row(t.Id, t.Number, t.Subject, d.Name, t.Status, t.LastMessageAt)).Take(200).ToListAsync();
    }
}
