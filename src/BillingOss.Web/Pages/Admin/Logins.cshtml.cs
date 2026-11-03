using System.Net;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>Historial de ingresos de clientes y staff, con filtros (diseno/fraude-y-acceso.md, sección 6.1).</summary>
[Authorize(Policy = StaffPermissions.SecurityRead)]
public sealed class LoginsModel(BillingDbContext db) : PageModel
{
    public const int Limit = 200;

    public sealed record Row(LoginEvent Event, string? Email);

    /// <summary>"portal", "staff" o vacío para los dos.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Side { get; set; }

    /// <summary>"failed", "success", "new" (lugar nuevo) o vacío.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Show { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Ip { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Email { get; set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGet()
    {
        var events = db.Set<LoginEvent>().AsNoTracking();
        if (Side is "portal" or "staff")
        {
            var audience = Side == "portal" ? LoginAudience.Portal : LoginAudience.Staff;
            events = events.Where(e => e.Audience == audience);
        }

        events = Show switch
        {
            "failed" => events.Where(e => e.Result != LoginResult.Success),
            "success" => events.Where(e => e.Result == LoginResult.Success),
            "new" => events.Where(e => e.NewLocation),
            _ => events,
        };
        if (Ip is { Length: > 0 } && Networks.Parse(Ip) is { } network)
        {
            var address = network.BaseAddress;
            events = network.PrefixLength is 32 or 128 ? events.Where(e => e.Ip == address) : events.Where(e => e.Network == network);
        }

        if (Email?.Trim().ToLowerInvariant() is { Length: > 0 } email)
        {
            var portal = db.Set<PortalUser>().Where(u => u.NormalizedEmail == email.ToUpperInvariant()).Select(u => u.Id);
            var staff = db.Set<StaffUser>().Where(u => u.NormalizedEmail == email.ToUpperInvariant()).Select(u => u.Id);
            events = events.Where(e => (e.Audience == LoginAudience.Portal && portal.Contains(e.UserId!.Value)) || (e.Audience == LoginAudience.Staff && staff.Contains(e.UserId!.Value)));
        }

        var list = await events.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(Limit).ToListAsync();
        var portalIds = list.Where(e => e.Audience == LoginAudience.Portal && e.UserId != null).Select(e => e.UserId!.Value).Distinct().ToList();
        var staffIds = list.Where(e => e.Audience == LoginAudience.Staff && e.UserId != null).Select(e => e.UserId!.Value).Distinct().ToList();
        var portalEmails = await db.Set<PortalUser>().Where(u => portalIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email);
        var staffEmails = await db.Set<StaffUser>().Where(u => staffIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email);
        Rows = [.. list.Select(e => new Row(e, e.UserId is not { } id ? null : e.Audience == LoginAudience.Portal ? portalEmails.GetValueOrDefault(id) : staffEmails.GetValueOrDefault(id)))];
    }
}
