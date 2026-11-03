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
/// Alta y edición de una regla de escalamiento. No se borra: se apaga, así su historial de escalaciones queda. Los
/// cambios quedan en la auditoría.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class EscalationModel(BillingDbContext db, AuditLog audit, IStringLocalizer<Texts> texts) : PageModel
{
    /// <summary>Una semana: más que eso es un recordatorio, no un escalamiento.</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    public bool IsNew { get; private set; }

    public IReadOnlyList<(long Id, string Name)> AllDepartments { get; private set; } = [];

    public IReadOnlyList<(long Id, string Name)> Staff { get; private set; } = [];

    public List<(string Field, string Message)> Problems { get; } = [];

    [BindProperty]
    public string? Name { get; set; }

    /// <summary>Sin valor por defecto: una casilla sin marcar no viaja, y el valor tiene que quedar en falso.</summary>
    [BindProperty]
    public bool Enabled { get; set; }

    [BindProperty]
    public List<long> Departments { get; set; } = [];

    [BindProperty]
    public List<TicketStatus> Statuses { get; set; } = [];

    [BindProperty]
    public List<TicketPriority> Priorities { get; set; } = [];

    [BindProperty]
    public int AfterMinutes { get; set; } = 60;

    [BindProperty]
    public long? NewDepartmentId { get; set; }

    [BindProperty]
    public TicketPriority? NewPriority { get; set; }

    [BindProperty]
    public TicketStatus? NewStatus { get; set; }

    [BindProperty]
    public long? AssignStaffId { get; set; }

    [BindProperty]
    public bool Notify { get; set; }

    /// <summary>Personas del staff que reciben el aviso, además del asignado o la casilla del equipo.</summary>
    [BindProperty]
    public List<long> NotifyStaff { get; set; } = [];

    [BindProperty]
    public string? Note { get; set; }

    /// <summary>Respuesta al cliente, que sale a nombre del departamento con su aviso.</summary>
    [BindProperty]
    public string? Reply { get; set; }

    public bool Invalid(string field) => Problems.Any(p => p.Field == field);

    public async Task<IActionResult> OnGet(long id)
    {
        IsNew = id == 0;
        await Load();
        if (IsNew)
        {
            (Enabled, Notify) = (true, true);
            return Page();
        }

        if (await db.Set<EscalationRule>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) is not { } rule)
        {
            return NotFound();
        }

        (Name, Enabled, Departments, Statuses, Priorities, AfterMinutes) = (rule.Name, rule.Enabled, [.. rule.DepartmentIds], [.. rule.Statuses], [.. rule.Priorities], rule.AfterMinutes);
        (NewDepartmentId, NewPriority, NewStatus, AssignStaffId, Notify, Note) = (rule.NewDepartmentId, rule.NewPriority, rule.NewStatus, rule.AssignStaffId, rule.Notify, rule.Note);
        (NotifyStaff, Reply) = ([.. rule.NotifyStaffIds], rule.Reply);
        return Page();
    }

    public async Task<IActionResult> OnPost(long id)
    {
        IsNew = id == 0;
        await Load();
        var rule = IsNew ? new EscalationRule() : await db.Set<EscalationRule>().SingleOrDefaultAsync(r => r.Id == id);
        if (rule is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 200)
        {
            Problems.Add((nameof(Name), texts["SupportAdmin.NameRequired"]));
        }

        if (AfterMinutes is < 1 or > MaxMinutes)
        {
            Problems.Add((nameof(AfterMinutes), texts["Escalation.MinutesRange", MaxMinutes]));
        }

        var departments = NewDepartmentId is { } moveTo ? Departments.Append(moveTo) : Departments;
        var staffChosen = AssignStaffId is { } staff ? NotifyStaff.Append(staff) : NotifyStaff;
        if (departments.Any(d => AllDepartments.All(x => x.Id != d)) || staffChosen.Any(s => Staff.All(x => x.Id != s)))
        {
            Problems.Add((string.Empty, texts["Escalation.UnknownChoice"]));
        }

        if (NewDepartmentId is null && NewPriority is null && NewStatus is null && AssignStaffId is null && !Notify && NotifyStaff.Count == 0
            && string.IsNullOrWhiteSpace(Note) && string.IsNullOrWhiteSpace(Reply))
        {
            Problems.Add((nameof(Notify), texts["Escalation.NoAction"]));
        }

        if (Note?.Trim().Length > TicketService.MaxBody)
        {
            Problems.Add((nameof(Note), texts["Support.MessageRequired"]));
        }

        if (Reply?.Trim().Length > TicketService.MaxBody)
        {
            Problems.Add((nameof(Reply), texts["Support.MessageRequired"]));
        }

        if (Problems.Count > 0)
        {
            return Page();
        }

        rule.Name = Name!.Trim();
        rule.Enabled = Enabled;
        rule.DepartmentIds = [.. Departments.Distinct()];
        rule.Statuses = [.. Statuses.Distinct()];
        rule.Priorities = [.. Priorities.Distinct()];
        rule.AfterMinutes = AfterMinutes;
        (rule.NewDepartmentId, rule.NewPriority, rule.NewStatus, rule.AssignStaffId, rule.Notify) = (NewDepartmentId, NewPriority, NewStatus, AssignStaffId, Notify);
        rule.Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        rule.Reply = string.IsNullOrWhiteSpace(Reply) ? null : Reply.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        rule.NotifyStaffIds = [.. NotifyStaff.Distinct()];
        if (IsNew)
        {
            db.Add(rule);
        }

        await db.SaveChangesAsync();
        await audit.Record("support.escalation_rule.save", string.Create(CultureInfo.InvariantCulture, $"escalation_rule:{rule.Id}"), new
        {
            name = rule.Name,
            enabled = rule.Enabled,
            minutes = rule.AfterMinutes,
            notify_staff = rule.NotifyStaffIds,
            reply = rule.Reply is not null,
        });
        return Redirect("/admin/soporte/escalamiento");
    }

    private async Task Load()
    {
        AllDepartments = (await db.Set<Department>().AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync())
            .Select(d => (d.Id, d.Name)).ToList();
        Staff = (await db.Set<StaffUser>().AsNoTracking().Where(s => s.DisabledAt == null && s.Permissions.Contains(StaffPermissions.SupportTickets)).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Email }).ToListAsync()).Select(s => (s.Id, s.Name.Length > 0 ? s.Name : s.Email)).ToList();
    }
}
