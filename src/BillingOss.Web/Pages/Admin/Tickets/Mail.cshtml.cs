using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>
/// Registros del sistema, Correo entrante (D-19, 10.2): el registro de cada correo leído (qué abrió, qué se sumó, qué se descartó y por qué),
/// con búsqueda, filtros y páginas. Un correo rechazado queda acá para que el staff lo vea: nunca se contesta. Desde una
/// fila se bloquea al remitente, que suma un filtro del control de spam.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class InboundMailModel(BillingDbContext db, AuditLog audit, IOptions<BillingOptions> billing) : PageModel
{
    public const int Size = 50;

    public sealed record Entry(InboundEmail Email, string Department);

    public IReadOnlyList<Entry> Log { get; private set; } = [];

    /// <summary>Los remitentes que ya tienen un filtro, para no ofrecer bloquearlos de nuevo.</summary>
    public IReadOnlySet<string> Blocked { get; private set; } = new HashSet<string>();

    public IReadOnlyList<(long Id, string Name)> Departments { get; private set; } = [];

    public BillingOptions Billing => billing.Value;

    /// <summary>Busca en el remitente y el asunto.</summary>
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true, Name = "resultado")]
    public InboundOutcome? Outcome { get; set; }

    [BindProperty(SupportsGet = true, Name = "departamento")]
    public long? Department { get; set; }

    [BindProperty(SupportsGet = true, Name = "pagina")]
    public int PageNumber { get; set; } = 1;

    /// <summary>Cuántos remitentes bloqueó la última acción, para el aviso de la vuelta.</summary>
    [BindProperty(SupportsGet = true, Name = "hechos")]
    public int? Done { get; set; }

    public int Total { get; private set; }

    public int Pages => Math.Max(1, (Total + Size - 1) / Size);

    public string Link(params (string Key, string? Value)[] changes) => SupportUi.Link(Request, changes);

    public async Task OnGet(CancellationToken ct)
    {
        Departments = (await db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            .Select(d => (d.Id, d.Name)).ToList();
        var query = from e in db.Set<InboundEmail>().AsNoTracking()
                    join d in db.Set<Department>() on e.DepartmentId equals d.Id
                    select new { Email = e, Department = d.Name };
        if (Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(r => EF.Functions.ILike(r.Email.Sender, pattern) || EF.Functions.ILike(r.Email.Subject, pattern));
        }

        if (Outcome is { } outcome)
        {
            query = query.Where(r => r.Email.Outcome == outcome);
        }

        if (Department is { } department)
        {
            query = query.Where(r => r.Email.DepartmentId == department);
        }

        Total = await query.CountAsync(ct);
        PageNumber = Math.Clamp(PageNumber, 1, Pages);
        Log = (await query.OrderByDescending(r => r.Email.Id).Skip((PageNumber - 1) * Size).Take(Size).ToListAsync(ct)).Select(r => new Entry(r.Email, r.Department)).ToList();
        var senders = Log.Select(e => e.Email.Sender).Distinct().ToList();
        Blocked = (await db.Set<MailFilter>().AsNoTracking().Where(f => f.Kind == MailFilterKind.Sender && senders.Contains(f.Value.ToLower())).Select(f => f.Value.ToLower()).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Bloquea al remitente: suma un filtro de remitente con su casilla y vuelve a la misma página del registro.</summary>
    public async Task<IActionResult> OnPostBlock(string sender)
    {
        if (string.IsNullOrWhiteSpace(sender) || sender.Trim().Length > 320 || !sender.Contains('@', StringComparison.Ordinal))
        {
            return BadRequest();
        }

        var added = await SpamFiltersModel.Add(db, audit, MailFilterKind.Sender, sender.Trim().ToLowerInvariant());
        return Redirect(Link(("hechos", added ? "1" : "0")));
    }
}
