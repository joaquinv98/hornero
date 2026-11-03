using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>Listas de bloqueo de IP, correos y dominios (diseno/fraude-y-acceso.md, sección 4), con las automáticas por intentos.</summary>
[Authorize(Policy = StaffPermissions.SecurityManage)]
public sealed class BlocklistModel(BillingDbContext db, AccessGuard guard, TimeProvider time) : PageModel
{
    [BindProperty]
    public BlockKind Kind { get; set; }

    [BindProperty]
    public string Value { get; set; } = string.Empty;

    [BindProperty]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Horas hasta que vence. Vacío es permanente.</summary>
    [BindProperty]
    public int? Hours { get; set; }

    public IReadOnlyList<BlockedEntry> Entries { get; private set; } = [];

    public DateTimeOffset Now { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGet()
    {
        Now = time.GetUtcNow();
        Entries = await db.Set<BlockedEntry>().AsNoTracking().OrderBy(e => e.Kind).ThenBy(e => e.Value).Take(1000).ToListAsync();
    }

    public async Task<IActionResult> OnPostAdd()
    {
        // Un campo vacío llega como null.
        var value = Value ?? string.Empty;
        if (Hours is < 1 or > 8760)
        {
            Error = "Block.BadHours";
        }
        else if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 300)
        {
            Error = "Block.NeedsReason";
        }
        else if (Kind == BlockKind.Ip && Networks.Parse(value) is { } network
                 && HttpContext.Connection.RemoteIpAddress is { } own && network.Contains(Networks.Normalize(own)))
        {
            // La lista también frena el ingreso al admin: quien la carga no se puede dejar afuera a sí mismo.
            Error = "Block.OwnIp";
        }
        else
        {
            try
            {
                await guard.Block(Kind, value, Reason, Hours is { } hours ? time.GetUtcNow().AddHours(hours) : null, BlockSource.Staff);
                return RedirectToPage();
            }
            catch (ValidationException)
            {
                Error = Kind switch { BlockKind.Ip => "Block.InvalidIp", BlockKind.Email => "Block.InvalidEmail", _ => "Block.InvalidDomain" };
            }
        }

        await OnGet();
        return Page();
    }

    public async Task<IActionResult> OnPostRemove(long id)
    {
        await guard.Unblock(id);
        return RedirectToPage();
    }
}
