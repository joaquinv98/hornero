using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>Las reglas de escalamiento, con cuántas veces se aplicó cada una.</summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class EscalationsModel(BillingDbContext db) : PageModel
{
    public IReadOnlyList<EscalationRule> Rules { get; private set; } = [];

    public Dictionary<long, int> Applied { get; private set; } = [];

    public Dictionary<long, string> Departments { get; private set; } = [];

    public async Task OnGet()
    {
        Rules = await db.Set<EscalationRule>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        Applied = await db.Set<TicketEscalation>().GroupBy(e => e.RuleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        Departments = await db.Set<Department>().AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name);
    }
}
