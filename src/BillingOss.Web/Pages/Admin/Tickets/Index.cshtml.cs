using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using BillingOss.App.UseCases;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>
/// Soporte, Tickets (D-19, sección 10.2): filtros arriba como parámetros GET, orden por columna, paginado, acciones
/// masivas con confirmación y exportación CSV de lo filtrado. Primero los de movimiento más reciente, como en el
/// prototipo de la navegación clásica. Con la cuenta en la ruta es la solapa Tickets de la ficha del cliente.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportTickets)]
public sealed class QueueModel(BillingDbContext db, TicketService tickets, AuditLog audit, IOptions<BillingOptions> billing, IStringLocalizer<Texts> texts) : PageModel
{
    public const int Size = 50;

    /// <summary>Tope de la exportación: más que eso va por un reporte.</summary>
    public const int MaxExport = 10_000;

    public const string AssignToMe = "asignarme";

    public const string CloseThem = "cerrar";

    /// <summary>Bloquea al remitente con un filtro del control de spam y cierra. Solo tickets sin cuenta.</summary>
    public const string SpamThem = "spam";

    /// <summary>Une los elegidos al más antiguo.</summary>
    public const string MergeThem = "unir";

    public sealed record Row(long Id, string Number, string Subject, string RequesterName, string RequesterEmail, long? AccountId, string? Account, string Department,
        TicketStatus Status, TicketPriority Priority, string? Assigned, DateTimeOffset LastMessageAt, bool StaffReplied);

    /// <summary>
    /// El filtro de estado, como en el prototipo y el submenú de Soporte. Abiertos, el de entrada, es todo lo que no está
    /// cerrado. Nuevos y con respuesta del cliente son los que esperan respuesta, sin y con un mensaje del staff que el
    /// cliente vio: los separa la consulta, el estado es uno solo.
    /// </summary>
    public static IReadOnlyList<(string Value, string Label, TicketStatus[] Statuses, bool? StaffReplied)> States { get; } =
    [
        ("abiertos", "SupportAdmin.ViewOpen", [TicketStatus.Open, TicketStatus.InProgress, TicketStatus.AwaitingCustomer, TicketStatus.OnHold], null),
        ("sin-responder", "TicketStatus.Open", [TicketStatus.Open], null),
        ("nuevos", "SupportAdmin.ViewNew", [TicketStatus.Open], false),
        ("respuesta-del-cliente", "SupportAdmin.ViewCustomerReply", [TicketStatus.Open], true),
        ("en-curso", "TicketStatus.InProgress", [TicketStatus.InProgress], null),
        ("esperando-al-cliente", "TicketStatus.AwaitingCustomer", [TicketStatus.AwaitingCustomer], null),
        ("en-pausa", "TicketStatus.OnHold", [TicketStatus.OnHold], null),
        ("cerrados", "SupportAdmin.ViewClosed", [TicketStatus.Closed], null),
        ("todos", "SupportAdmin.ViewAll", Enum.GetValues<TicketStatus>(), null),
    ];

    [BindProperty(SupportsGet = true, Name = "estado")]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true, Name = "departamento")]
    public long? Department { get; set; }

    /// <summary>"yo", "nadie" o el ID de una persona del staff. Vacío, cualquiera.</summary>
    [BindProperty(SupportsGet = true, Name = "asignado")]
    public string? Assignee { get; set; }

    [BindProperty(SupportsGet = true, Name = "prioridad")]
    public TicketPriority? Priority { get; set; }

    /// <summary>La cuenta, de la ruta de la solapa de la ficha del cliente.</summary>
    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? Account { get; set; }

    [BindProperty(SupportsGet = true, Name = "orden")]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true, Name = "dir")]
    public string? Dir { get; set; }

    [BindProperty(SupportsGet = true, Name = "pagina")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "formato")]
    public string? Format { get; set; }

    /// <summary>Cuántos tickets cambió la última acción masiva, para el aviso de la vuelta.</summary>
    [BindProperty(SupportsGet = true, Name = "hechos")]
    public int? Done { get; set; }

    [BindProperty(Name = "ids")]
    public List<long> Selected { get; set; } = [];

    [BindProperty(Name = "accion")]
    public string? Action { get; set; }

    public IReadOnlyList<(long Id, string Name)> Departments { get; private set; } = [];

    /// <summary>El staff que atiende soporte, para el filtro de asignado.</summary>
    public IReadOnlyList<(long Id, string Name)> Staff { get; private set; } = [];

    public string? AccountName { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    /// <summary>Lo elegido a lo que de verdad le cambia algo la acción masiva, para confirmar.</summary>
    public IReadOnlyList<Row> Confirm { get; private set; } = [];

    /// <summary>En la unión, el ticket que recibe a los demás: el más antiguo de lo elegido.</summary>
    public Row? MergeTarget { get; private set; }

    public int Total { get; private set; }

    public int Pages => Math.Max(1, (Total + Size - 1) / Size);

    public int From => Total == 0 ? 0 : ((PageNumber - 1) * Size) + 1;

    public int To => Math.Min(Total, PageNumber * Size);

    public bool Descending => Dir == "desc";

    public BillingOptions Billing => billing.Value;

    public string StateValue => States.Any(s => s.Value == State) ? State! : States[0].Value;

    public static bool KnownAction(string? action) => action is AssignToMe or CloseThem or SpamThem or MergeThem;

    /// <summary>Bloquear remitentes toca el control de spam, que es de la configuración: pide también ese permiso.</summary>
    public bool CanBlock => User.HasClaim(Claims.Permission, StaffPermissions.SupportSettings);

    /// <summary>La misma lista con algunos parámetros cambiados. Cambiar un filtro o el orden vuelve a la primera página.</summary>
    public string Link(params (string Key, string? Value)[] changes) => SupportUi.Link(Request, changes);

    /// <summary>La primera vez ordena de forma ascendente y la segunda al revés. Sin orden, la última actividad va de la más reciente.</summary>
    public string SortLink(string key) => Link(("orden", key), ("dir", Sort == key && !Descending ? "desc" : null));

    public string? AriaSort(string key) => (Sort ?? "actividad") == key ? (Sort is null || Descending ? "descending" : "ascending") : null;

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        if (!await Load(ct))
        {
            return NotFound();
        }

        var query = Sorted(Filtered());
        if (Format == "csv")
        {
            return Csv(await Project(query.Take(MaxExport)).ToListAsync(ct));
        }

        await Show(query, ct);
        return Page();
    }

    /// <summary>Primer paso de la acción masiva: muestra a cuáles les cambia algo y pide confirmar. No cambia nada.</summary>
    public async Task<IActionResult> OnPostBulk(CancellationToken ct)
    {
        if (Action == SpamThem && !CanBlock)
        {
            return Forbid(Schemes.Staff);
        }

        if (!await Load(ct))
        {
            return NotFound();
        }

        Confirm = await Applicable(ct);
        await Show(Sorted(Filtered()), ct);
        return Page();
    }

    /// <summary>Segundo paso: un cambio por ticket, cada uno en su transacción y en la auditoría. Repetirlo no cambia nada más.</summary>
    public async Task<IActionResult> OnPostBulkConfirm(CancellationToken ct)
    {
        if (!KnownAction(Action))
        {
            return BadRequest();
        }

        if (Action == SpamThem && !CanBlock)
        {
            return Forbid(Schemes.Staff);
        }

        var done = 0;
        foreach (var row in await Applicable(ct))
        {
            try
            {
                await Apply(row, ct);
                done++;
            }
            catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
            {
                // Se unió a otro entre los dos pasos, o la unión no corresponde: queda afuera.
            }
        }

        return Redirect(Link(("hechos", done.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>Un ticket de la acción, en su transacción y en la auditoría. Repetirla no cambia nada más.</summary>
    private async Task Apply(Row row, CancellationToken ct)
    {
        switch (Action)
        {
            case AssignToMe:
                await tickets.Update(row.Id, new TicketChange(AssignedStaffId: User.UserId()), ct);
                return;
            case MergeThem:
                await tickets.Merge(row.Id, MergeTarget!.Id, ct);
                return;
            case SpamThem when row.RequesterEmail.Contains('@', StringComparison.Ordinal):
                await SpamFiltersModel.Add(db, audit, MailFilterKind.Sender, row.RequesterEmail.Trim().ToLowerInvariant());
                break;
        }

        if (row.Status != TicketStatus.Closed)
        {
            await tickets.Update(row.Id, new TicketChange(Status: TicketStatus.Closed), ct);
        }
    }

    /// <summary>
    /// De lo elegido, a lo que la acción le cambia algo: asignarme lo que no es mío, cerrar lo que no está cerrado,
    /// bloquear a quien escribió sin ser de una cuenta y, en la unión, todo menos el más antiguo, que los recibe.
    /// </summary>
    private async Task<IReadOnlyList<Row>> Applicable(CancellationToken ct)
    {
        var me = User.UserId();
        var ids = Selected.Distinct().Take(Size).ToList();
        var chosen = Query().Where(r => ids.Contains(r.Ticket.Id) && r.Ticket.MergedIntoId == null);
        chosen = Action switch
        {
            AssignToMe => chosen.Where(r => r.Ticket.AssignedStaffId != me),
            CloseThem => chosen.Where(r => r.Ticket.Status != TicketStatus.Closed),
            SpamThem => chosen.Where(r => r.Ticket.AccountId == null),
            MergeThem => chosen,
            _ => chosen.Where(_ => false),
        };
        var rows = await Project(chosen.OrderBy(r => r.Ticket.CreatedAt).ThenBy(r => r.Ticket.Id)).ToListAsync(ct);
        if (Action != MergeThem)
        {
            return rows;
        }

        MergeTarget = rows.Count > 1 ? rows[0] : null;
        return rows.Count > 1 ? rows[1..] : [];
    }

    private async Task<bool> Load(CancellationToken ct)
    {
        if (Account is { } account)
        {
            AccountName = await db.Accounts.Where(a => a.Id == account).Select(a => a.Name).SingleOrDefaultAsync(ct);
            if (AccountName is null)
            {
                return false;
            }
        }

        Departments = (await db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            .Select(d => (d.Id, d.Name)).ToList();
        Staff = (await db.Set<StaffUser>().AsNoTracking().Where(s => s.DisabledAt == null && s.Permissions.Contains(StaffPermissions.SupportTickets)).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Email }).ToListAsync(ct)).Select(s => (s.Id, s.Name.Length > 0 ? s.Name : s.Email)).ToList();
        return true;
    }

    private async Task Show(IQueryable<Entry> query, CancellationToken ct)
    {
        Total = await query.CountAsync(ct);
        PageNumber = Math.Clamp(PageNumber, 1, Pages);
        Rows = await Project(query.Skip((PageNumber - 1) * Size).Take(Size)).ToListAsync(ct);
    }

    /// <summary>
    /// Un ticket con lo que la lista muestra de su cuenta, departamento y asignado. Con propiedades y no con constructor:
    /// así EF sigue filtrando y ordenando sobre la proyección.
    /// </summary>
    private sealed class Entry
    {
        public required Ticket Ticket { get; init; }

        public string? Account { get; init; }

        public required string Department { get; init; }

        public string? Assigned { get; init; }

        /// <summary>Tiene un mensaje del staff que el cliente vio: separa los nuevos de los que tienen respuesta del cliente.</summary>
        public bool StaffReplied { get; init; }
    }

    private IQueryable<Entry> Query() =>
        from t in db.Set<Ticket>().AsNoTracking()
        join d in db.Set<Department>() on t.DepartmentId equals d.Id
        join a in db.Accounts on t.AccountId equals (long?)a.Id into accounts
        from a in accounts.DefaultIfEmpty()
        join s in db.Set<StaffUser>() on t.AssignedStaffId equals (long?)s.Id into staff
        from s in staff.DefaultIfEmpty()
        select new Entry
        {
            Ticket = t,
            Account = a == null ? null : a.Name,
            Department = d.Name,
            Assigned = s == null ? null : (s.Name == "" ? s.Email : s.Name),
            StaffReplied = db.Set<TicketMessage>().Any(m => m.TicketId == t.Id && m.Author == MessageAuthor.Staff && !m.Internal),
        };

    private IQueryable<Entry> Filtered()
    {
        var query = Query();
        var number = Q?.Trim().TrimStart('#') is { Length: > 0 } digits && digits.All(char.IsAsciiDigit) ? digits : null;
        if (Q?.Trim() is { Length: > 0 } q)
        {
            // Uno unido a otro aparece solo buscado por su número: su enlace lleva al que lo recibió.
            var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(r => r.Ticket.Number == number || (r.Ticket.MergedIntoId == null && (EF.Functions.ILike(r.Ticket.Subject, pattern)
                || EF.Functions.ILike(r.Ticket.RequesterEmail, pattern) || EF.Functions.ILike(r.Ticket.RequesterName, pattern) || (r.Account != null && EF.Functions.ILike(r.Account, pattern)))));
        }
        else
        {
            query = query.Where(r => r.Ticket.MergedIntoId == null);
        }

        // Un número busca en todos los estados, como en la API: el que lo escribe quiere ese ticket.
        var state = States.Single(s => s.Value == StateValue);
        var statuses = number is null ? state.Statuses : Enum.GetValues<TicketStatus>();
        query = query.Where(r => statuses.Contains(r.Ticket.Status));
        if (number is null && state.StaffReplied is { } replied)
        {
            query = query.Where(r => r.StaffReplied == replied);
        }

        if (Priority is { } priority)
        {
            query = query.Where(r => r.Ticket.Priority == priority);
        }
        if (Department is { } department)
        {
            query = query.Where(r => r.Ticket.DepartmentId == department);
        }

        if (Account is { } account)
        {
            query = query.Where(r => r.Ticket.AccountId == account);
        }

        var me = User.UserId();
        return Assignee switch
        {
            "yo" => query.Where(r => r.Ticket.AssignedStaffId == me),
            "nadie" => query.Where(r => r.Ticket.AssignedStaffId == null),
            _ when long.TryParse(Assignee, NumberStyles.None, CultureInfo.InvariantCulture, out var staffId) => query.Where(r => r.Ticket.AssignedStaffId == staffId),
            _ => query,
        };
    }

    private IOrderedQueryable<Entry> Sorted(IQueryable<Entry> query)
    {
        var sorted = Sort switch
        {
            "numero" => By(query, r => r.Ticket.Number),
            "asunto" => By(query, r => r.Ticket.Subject),
            "cliente" => By(query, r => r.Account ?? r.Ticket.RequesterName),
            "area" => By(query, r => r.Department),
            "prioridad" => By(query, r => r.Ticket.Priority),
            "estado" => By(query, r => r.Ticket.Status),
            "asignado" => By(query, r => r.Assigned),
            _ => Sort is null ? query.OrderByDescending(r => r.Ticket.LastMessageAt) : By(query, r => r.Ticket.LastMessageAt),
        };
        return sorted.ThenByDescending(r => r.Ticket.Id);
    }

    private IOrderedQueryable<Entry> By<TKey>(IQueryable<Entry> query, Expression<Func<Entry, TKey>> key) =>
        Descending ? query.OrderByDescending(key) : query.OrderBy(key);

    private static IQueryable<Row> Project(IQueryable<Entry> query) =>
        query.Select(r => new Row(r.Ticket.Id, r.Ticket.Number, r.Ticket.Subject, r.Ticket.RequesterName, r.Ticket.RequesterEmail, r.Ticket.AccountId, r.Account, r.Department,
            r.Ticket.Status, r.Ticket.Priority, r.Assigned, r.Ticket.LastMessageAt, r.StaffReplied));

    /// <summary>Lo filtrado entero, sin el paginado, en CSV (RFC 4180) con BOM para que la planilla lea los acentos.</summary>
    private FileContentResult Csv(IReadOnlyList<Row> rows)
    {
        var csv = new StringBuilder();
        CsvLine(csv, ["numero", "asunto", "cuenta_numero", "cuenta", "solicitante", "correo", "area", "prioridad", "estado", "asignado", "ultima_actividad"]);
        foreach (var r in rows)
        {
            CsvLine(csv, [r.Number, r.Subject, r.AccountId?.ToString(CultureInfo.InvariantCulture), r.Account, r.RequesterName, r.RequesterEmail, r.Department,
                texts["TicketPriority." + r.Priority].Value, texts["TicketStatus." + r.Status].Value, r.Assigned, SupportUi.Iso(r.LastMessageAt)]);
        }

        return File([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())], "text/csv; charset=utf-8",
            string.Create(CultureInfo.InvariantCulture, $"tickets-{DateTime.UtcNow:yyyyMMdd}.csv"));
    }

    private static void CsvLine(StringBuilder csv, IEnumerable<string?> cells)
    {
        csv.AppendJoin(',', cells.Select(Cell));
        csv.Append("\r\n");
    }

    /// <summary>Entre comillas si hace falta, y con un apóstrofo si la planilla la tomaría como fórmula.</summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var formula = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
        var text = formula ? "'" + value : value;
        return formula || text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
