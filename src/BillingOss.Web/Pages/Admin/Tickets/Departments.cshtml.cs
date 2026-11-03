using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>Los departamentos con el estado de su casilla: apagada, leyendo o con la última falla.</summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class DepartmentsModel(BillingDbContext db, IOptions<BillingOptions> billing) : PageModel
{
    public IReadOnlyList<Department> Departments { get; private set; } = [];

    public Dictionary<long, int> Waiting { get; private set; } = [];

    public BillingOptions Billing => billing.Value;

    public async Task OnGet()
    {
        Departments = await db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync();
        Waiting = await db.Set<Ticket>().Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress)
            .GroupBy(t => t.DepartmentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
    }
}
