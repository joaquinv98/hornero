using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>
/// Soporte, Resumen (D-19, 10.2): la entrada al soporte. Cuántos tickets hay en cada vista de la cola, lo que más espera,
/// la carga por departamento y por persona, la semana y las últimas encuestas. Cada número lleva a la cola filtrada.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportTickets)]
public sealed class OverviewModel(BillingDbContext db, IOptions<BillingOptions> billing, TimeProvider time) : PageModel
{
    public sealed record Card(string Label, int Count, string Href, string Tone);

    public sealed record Waiting(long Id, string Number, string Subject, string Department, TicketStatus Status, DateTimeOffset Since);

    public sealed record ByDepartment(long Id, string Name, int Open, int InProgress, int AwaitingCustomer, int OnHold);

    public sealed record ByStaff(long Id, string Name, int Waiting, int Active);

    public sealed record Survey(long TicketId, string Number, string Subject, int Rating, string? Comment, DateTimeOffset At);

    public IReadOnlyList<Card> Cards { get; private set; } = [];

    public IReadOnlyList<Waiting> Oldest { get; private set; } = [];

    public IReadOnlyList<ByDepartment> Departments { get; private set; } = [];

    public IReadOnlyList<ByStaff> Staff { get; private set; } = [];

    public int OpenedWeek { get; private set; }

    public int ClosedWeek { get; private set; }

    public TimeSpan? FirstReplyWeek { get; private set; }

    public IReadOnlyList<Survey> Surveys { get; private set; } = [];

    public BillingOptions Billing => billing.Value;

    public async Task OnGet(CancellationToken ct)
    {
        var active = db.Set<Ticket>().AsNoTracking().Where(t => t.MergedIntoId == null && t.Status != TicketStatus.Closed);
        var counts = await active.GroupBy(t => new { t.DepartmentId, t.Status }).Select(g => new { g.Key.DepartmentId, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var replied = await active.CountAsync(t => t.Status == TicketStatus.Open && db.Set<TicketMessage>().Any(m => m.TicketId == t.Id && m.Author == MessageAuthor.Staff && !m.Internal), ct);
        var me = User.UserId();
        int Of(TicketStatus status) => counts.Where(c => c.Status == status).Sum(c => c.Count);
        Cards =
        [
            new("SupportAdmin.ViewNew", Of(TicketStatus.Open) - replied, "/admin/soporte?estado=nuevos", "warn"),
            new("SupportAdmin.ViewCustomerReply", replied, "/admin/soporte?estado=respuesta-del-cliente", "warn"),
            new("TicketStatus.InProgress", Of(TicketStatus.InProgress), "/admin/soporte?estado=en-curso", "info"),
            new("TicketStatus.AwaitingCustomer", Of(TicketStatus.AwaitingCustomer), "/admin/soporte?estado=esperando-al-cliente", "neutral"),
            new("TicketStatus.OnHold", Of(TicketStatus.OnHold), "/admin/soporte?estado=en-pausa", "neutral"),
            new("Menu.MyTickets", await active.CountAsync(t => t.AssignedStaffId == me, ct), "/admin/soporte?asignado=yo", "info"),
            new("SupportOverview.UnassignedOpen", await active.CountAsync(t => t.Status == TicketStatus.Open && t.AssignedStaffId == null, ct), "/admin/soporte?asignado=nadie&estado=sin-responder", "warn"),
        ];

        Oldest = await (from t in active
                        join d in db.Set<Department>() on t.DepartmentId equals d.Id
                        where t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress
                        orderby t.LastMessageAt, t.Id
                        select new Waiting(t.Id, t.Number, t.Subject, d.Name, t.Status, t.LastMessageAt)).Take(5).ToListAsync(ct);

        var names = await db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        Departments = [.. names.Where(d => counts.Any(c => c.DepartmentId == d.Id)).Select(d =>
        {
            int In(TicketStatus status) => counts.Where(c => c.DepartmentId == d.Id && c.Status == status).Sum(c => c.Count);
            return new ByDepartment(d.Id, d.Name, In(TicketStatus.Open), In(TicketStatus.InProgress), In(TicketStatus.AwaitingCustomer), In(TicketStatus.OnHold));
        })];

        Staff = (await (from t in active
                        join s in db.Set<StaffUser>() on t.AssignedStaffId equals (long?)s.Id
                        group t by new { s.Id, s.Name, s.Email } into g
                        select new { g.Key.Id, g.Key.Name, g.Key.Email, Waiting = g.Count(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress), Active = g.Count() })
                .ToListAsync(ct))
            .Select(s => new ByStaff(s.Id, s.Name.Length > 0 ? s.Name : s.Email, s.Waiting, s.Active)).OrderByDescending(s => s.Waiting).ThenBy(s => s.Name, StringComparer.CurrentCulture).ToList();

        var week = time.GetUtcNow().AddDays(-7);
        var replies = await FirstReplies(week, ct);
        OpenedWeek = replies.Count;
        FirstReplyWeek = Median(replies.OfType<TimeSpan>());
        ClosedWeek = await db.Set<Ticket>().CountAsync(t => t.MergedIntoId == null && t.ClosedAt >= week, ct);

        Surveys = await (from f in db.Set<TicketFeedback>().AsNoTracking()
                         join t in db.Set<Ticket>() on f.TicketId equals t.Id
                         orderby f.CreatedAt descending, f.Id descending
                         select new Survey(t.Id, t.Number, t.Subject, f.Rating, f.Comment, f.CreatedAt)).Take(5).ToListAsync(ct);
    }

    /// <summary>
    /// La primera respuesta de cada ticket abierto desde <paramref name="since"/>, con la definición del reporte de soporte
    /// (docs/reportes): desde que se abrió hasta el primer mensaje del staff que ve el cliente, solo en los que abrió el
    /// cliente. Los unidos a otro no cuentan: sus mensajes están en el que los recibió.
    /// </summary>
    private async Task<List<TimeSpan?>> FirstReplies(DateTimeOffset since, CancellationToken ct)
    {
        // ponytail: trae una fila por ticket de la semana para la mediana. Con decenas de miles, percentile_cont en SQL.
        var opened = await (from t in db.Set<Ticket>().AsNoTracking()
                            where t.MergedIntoId == null && t.CreatedAt >= since
                            select new
                            {
                                t.CreatedAt,
                                ByCustomer = db.Set<TicketMessage>().Where(m => m.TicketId == t.Id).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Select(m => (MessageAuthor?)m.Author).FirstOrDefault() == MessageAuthor.Customer,
                                FirstReply = db.Set<TicketMessage>().Where(m => m.TicketId == t.Id && m.Author == MessageAuthor.Staff && !m.Internal).Min(m => (DateTimeOffset?)m.CreatedAt),
                            }).ToListAsync(ct);
        return [.. opened.Select(t => t.ByCustomer && t.FirstReply >= t.CreatedAt ? t.FirstReply.Value - t.CreatedAt : (TimeSpan?)null)];
    }

    public static TimeSpan? Median(IEnumerable<TimeSpan> spans)
    {
        var sorted = (spans ?? throw new ArgumentNullException(nameof(spans))).Order().ToList();
        return sorted.Count == 0 ? null : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>"45 min" o "3,5 h".</summary>
    public static string Duration(TimeSpan span) => span.TotalHours < 1
        ? string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min")
        : string.Create(CultureInfo.CurrentCulture, $"{span.TotalHours:0.#} h");
}
