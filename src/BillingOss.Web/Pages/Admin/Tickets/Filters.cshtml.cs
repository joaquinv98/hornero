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

/// <summary>
/// Configuración, Control de spam (D-19, 10.2: `/admin/soporte/correo`): los filtros de remitente (casilla, dominio o comodín), asunto y frase del cuerpo. Un
/// correo que coincide queda rechazado en el registro del correo entrante, sin ticket ni respuesta. Los cambios quedan
/// en la auditoría.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class SpamFiltersModel(BillingDbContext db, AuditLog audit, IStringLocalizer<Texts> texts) : PageModel
{
    public IReadOnlyList<MailFilter> Filters { get; private set; } = [];

    public List<string> Problems { get; } = [];

    [BindProperty]
    public MailFilterKind Kind { get; set; }

    [BindProperty]
    public string? Value { get; set; }

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostAdd()
    {
        if (string.IsNullOrWhiteSpace(Value) || Value.Trim().Length > 500)
        {
            Problems.Add(texts["SupportAdmin.FilterRequired"]);
            await Load();
            return Page();
        }

        await Add(db, audit, Kind, Value);
        return Redirect("/admin/soporte/correo");
    }

    public async Task<IActionResult> OnPostDelete(long remove)
    {
        if (await db.Set<MailFilter>().Where(f => f.Id == remove).ExecuteDeleteAsync() == 0)
        {
            return NotFound();
        }

        await audit.Record("support.mail_filter.delete", string.Create(CultureInfo.InvariantCulture, $"mail_filter:{remove}"));
        return Redirect("/admin/soporte/correo");
    }

    /// <summary>Suma un filtro si no había uno igual, y lo deja en la auditoría. Lo usan esta pantalla, el registro y la cola.</summary>
    public static async Task<bool> Add(BillingDbContext db, AuditLog audit, MailFilterKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        var lower = text.ToLowerInvariant();
        if (await db.Set<MailFilter>().AnyAsync(f => f.Kind == kind && f.Value.ToLower() == lower))
        {
            return false;
        }

        var filter = new MailFilter { Kind = kind, Value = text };
        db.Add(filter);
        await db.SaveChangesAsync();
        // Sin el valor: puede ser la casilla de una persona, y la auditoría no se borra. El valor queda en el filtro.
        await audit.Record("support.mail_filter.add", string.Create(CultureInfo.InvariantCulture, $"mail_filter:{filter.Id}"), new { kind = kind.ToString() });
        return true;
    }

    private async Task Load() =>
        Filters = await db.Set<MailFilter>().AsNoTracking().OrderBy(f => f.Kind).ThenBy(f => f.Value).ToListAsync();
}
