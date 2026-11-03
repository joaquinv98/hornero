using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>Buscar cuentas por número, nombre, empresa o correo. Con htmx los resultados se actualizan al escribir.</summary>
[Authorize(Policy = StaffPermissions.AccountsRead)]
public sealed class AdminIndexModel(BillingDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<Account> Results { get; private set; } = [];

    public async Task OnGet()
    {
        var q = Q?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return;
        }

        var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var id = long.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
        Results = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == id || EF.Functions.ILike(a.Name, pattern) || EF.Functions.ILike(a.Email, pattern) || (a.CompanyName != null && EF.Functions.ILike(a.CompanyName, pattern)))
            .OrderBy(a => a.Id)
            .Take(50)
            .ToListAsync();
    }
}
