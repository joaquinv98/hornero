using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingOss.App.Identity;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Support;

public static class SupportServices
{
    /// <summary>La lectura de casillas necesita las llaves para descifrar la contraseña de IMAP: sin ellas no se registra.</summary>
    public static IServiceCollection AddSupport(this IServiceCollection services, bool withSecrets)
    {
        services.AddOptions<SupportOptions>();
        services.AddScoped<SupportSettings>();
        services.AddScoped<TicketService>();
        services.AddScoped<MailIntake>();
        services.AddScoped<IJobHandler, SupportNotifyHandler>();
        services.AddScoped<IJobHandler, AutoCloseHandler>();
        services.AddScoped<IJobHandler, EscalateHandler>();
        if (withSecrets)
        {
            services.AddScoped<IJobHandler, MailPollHandler>();
        }

        return services;
    }

    /// <summary>
    /// Encola la lectura de cada casilla, cada dos minutos y de a una por casilla, las reglas de escalamiento cada cinco
    /// minutos y, cada hora, el cierre de los tickets que esperan al cliente. Lo llama el planificador dentro de su lock.
    /// </summary>
    public static async Task Schedule(BillingDbContext db, JobQueue jobs, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(jobs);
        var bucket = now.AddMinutes(-(now.Minute % 2)).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        foreach (var id in await db.Set<Department>().Where(d => d.MailEnabled).OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false))
        {
            await jobs.Enqueue(JobKinds.SupportMail, new { department_id = id }, MailPollHandler.Entity(id), $"{JobKinds.SupportMail}:{id}:{bucket}", ct: ct).ConfigureAwait(false);
        }

        if (await db.Set<EscalationRule>().AnyAsync(r => r.Enabled, ct).ConfigureAwait(false))
        {
            var five = now.AddMinutes(-(now.Minute % 5)).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
            await jobs.Enqueue(JobKinds.SupportEscalate, new { at = five }, dedupKey: $"{JobKinds.SupportEscalate}:{five}", ct: ct).ConfigureAwait(false);
        }

        // Cada hora y de a 500 (CloseInactive): una importación con miles que esperan al cliente se cierra en el día.
        var hour = now.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        await jobs.Enqueue(JobKinds.SupportAutoClose, new { hour }, dedupKey: $"{JobKinds.SupportAutoClose}:{hour}", ct: ct).ConfigureAwait(false);
    }
}

/// <summary>Las reglas de escalamiento, cada cinco minutos en cualquier nodo. La clave única de cada escalación evita repetir.</summary>
public sealed class EscalateHandler(TicketService tickets) : IJobHandler
{
    public string Kind => JobKinds.SupportEscalate;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        await tickets.Escalate(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>
/// Avisos de los tickets (sección 9.1 del modelo de dominio): la confirmación al abrir, cada respuesta del staff al
/// solicitante y a las copias, y el aviso al staff cuando escribe el cliente. Las notas internas nunca avisan. Todos
/// llevan el hilo del ticket en In-Reply-To y References, y la casilla del departamento en Reply-To: la respuesta del
/// cliente vuelve al ticket por IMAP.
/// </summary>
public sealed class SupportNotifyHandler(BillingDbContext db, Notifier notifier, IOptions<BillingOptions> billing) : IJobHandler
{
    public string Kind => JobKinds.SupportNotify;

    /// <summary>El Message-ID raíz del hilo de un ticket. La respuesta del cliente lo trae y así se encuentra el ticket.</summary>
    public static string ThreadId(string replyToken, string from) =>
        $"<ticket.{replyToken}@{(from.Split('@') is [_, var domain] && domain.Length > 0 ? domain : "localhost")}>";

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var json = JsonDocument.Parse(job.Payload);
        var p = json.RootElement;
        switch (p.GetProperty("event").GetString())
        {
            case Events.TicketClosed:
                await Survey(p.GetProperty("ticket_id").GetInt64(), ct).ConfigureAwait(false);
                return JobResult.Done;
            case Events.TicketEscalated:
                await Escalated(p.GetProperty("ticket_id").GetInt64(), p.GetProperty("escalation_id").GetInt64(), ct).ConfigureAwait(false);
                return JobResult.Done;
        }

        var messageId = p.GetProperty("message_id").GetInt64();
        var opened = p.GetProperty("event").GetString() == Events.TicketOpened;
        var message = await db.Set<TicketMessage>().AsNoTracking().SingleAsync(m => m.Id == messageId, ct).ConfigureAwait(false);

        // Del sistema solo avisa la respuesta al cliente de una regla de escalamiento: las marcas de cierre y unión no.
        var systemReply = p.TryGetProperty("system_reply", out var flag) && flag.GetBoolean();
        if (message.Internal || message.Source == MessageSource.Import || (message.Author == MessageAuthor.System && !systemReply))
        {
            return JobResult.Done;
        }

        // El ticket es el del mensaje: si se unió a otro antes del aviso, el aviso sale con el que lo recibió.
        var (ticket, department, language) = await Load(message.TicketId, ct).ConfigureAwait(false);
        var files = await db.Set<TicketAttachment>().AsNoTracking().Where(a => a.MessageId == messageId).OrderBy(a => a.Id).Select(a => a.FileName).ToListAsync(ct).ConfigureAwait(false);
        var model = Model(ticket, department);
        model["author"] = message.AuthorName;
        model["message"] = message.Body;
        model["files"] = files;

        // El staff abrió la consulta por el cliente: el aviso no dice "respondió".
        model["staff_opened"] = opened;
        var headers = Thread(ticket, department);

        if (message.Author == MessageAuthor.Customer)
        {
            if (opened && department.AutoReply)
            {
                await notifier.Queue(TemplateKeys.TicketOpened, ticket.RequesterEmail, language, model, $"{TemplateKeys.TicketOpened}:{ticket.Id}", ticket.AccountId, ct,
                    new Dictionary<string, string>(headers) { ["Auto-Submitted"] = "auto-replied" }).ConfigureAwait(false);
            }

            await AlertStaff(ticket, department, message, model, ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        // El staff abrió la consulta sin avisarle al cliente: queda en el ticket y sale recién con la próxima respuesta.
        if (opened && p.TryGetProperty("quiet", out var quiet) && quiet.GetBoolean())
        {
            return JobResult.Done;
        }

        foreach (var to in new[] { ticket.RequesterEmail }.Concat(ticket.Cc))
        {
            await notifier.Queue(TemplateKeys.TicketReply, to, language, model, $"{TemplateKeys.TicketReply}:{messageId}:{Short(to)}", ticket.AccountId, ct, headers).ConfigureAwait(false);
        }

        return JobResult.Done;
    }

    /// <summary>Al asignado, o a la casilla del equipo si el ticket no tiene a nadie.</summary>
    private async Task AlertStaff(Ticket ticket, Department department, TicketMessage message, Dictionary<string, object?> model, CancellationToken ct)
    {
        var staff = ticket.AssignedStaffId is { } staffId
            ? await db.Set<StaffUser>().AsNoTracking().Where(u => u.Id == staffId && u.DisabledAt == null).Select(u => new { u.Email, u.Language }).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var to = staff?.Email ?? department.StaffAlertEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        await notifier.Queue(TemplateKeys.TicketStaffAlert, to, staff?.Language, new Dictionary<string, object?>(model) { ["assigned"] = staff is not null },
            $"{TemplateKeys.TicketStaffAlert}:{message.Id}", ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// La encuesta de satisfacción, al solicitante, una sola vez por ticket. No sale si el departamento no la pide, si el
    /// ticket se reabrió o se unió a otro antes del aviso, si ya respondió, si no hay dirección pública para el enlace o si
    /// el solicitante está bloqueado en el control de spam: a quien se cierra por spam no se le escribe.
    /// </summary>
    private async Task Survey(long ticketId, CancellationToken ct)
    {
        var (ticket, department, language) = await Load(ticketId, ct).ConfigureAwait(false);
        var requester = ticket.RequesterEmail.ToLowerInvariant();
        if (!department.RequestFeedback || ticket.Status != TicketStatus.Closed || ticket.MergedIntoId is not null || billing.Value.PublicUrl is not { Length: > 0 } url
            || await db.Set<TicketFeedback>().AnyAsync(f => f.TicketId == ticketId, ct).ConfigureAwait(false)
            || await db.Set<MailFilter>().AnyAsync(f => f.Kind == MailFilterKind.Sender && f.Value.ToLower() == requester, ct).ConfigureAwait(false))
        {
            return;
        }

        // La ficha se crea una sola vez, también si dos nodos llegan a la vez.
        string token;
        await using (var cmd = await Sql.Command(db, "UPDATE support.tickets SET feedback_token = COALESCE(feedback_token, @token) WHERE id = @id RETURNING feedback_token", ct,
            ("token", RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz234567", 24)), ("id", ticketId)).ConfigureAwait(false))
        {
            token = (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var model = Model(ticket, department);
        model["survey_url"] = $"{url.TrimEnd('/')}/soporte/encuesta/{token}";
        await notifier.Queue(TemplateKeys.TicketFeedback, ticket.RequesterEmail, language, model, $"{TemplateKeys.TicketFeedback}:{ticket.Id}", ticket.AccountId, ct, Thread(ticket, department)).ConfigureAwait(false);
    }

    /// <summary>
    /// El aviso de una regla de escalamiento: al asignado o a la casilla del equipo del departamento, si la regla lo pide,
    /// y a cada persona del staff que la regla nombra. Uno por casilla.
    /// </summary>
    private async Task Escalated(long ticketId, long escalationId, CancellationToken ct)
    {
        var (ticket, department, _) = await Load(ticketId, ct).ConfigureAwait(false);
        var rule = await (from e in db.Set<TicketEscalation>()
                          join r in db.Set<EscalationRule>() on e.RuleId equals r.Id
                          where e.Id == escalationId
                          select r).AsNoTracking().SingleAsync(ct).ConfigureAwait(false);
        var recipients = new List<(string Email, string? Language, bool Assigned)>();
        if (rule.Notify)
        {
            var staff = ticket.AssignedStaffId is { } staffId
                ? await db.Set<StaffUser>().AsNoTracking().Where(u => u.Id == staffId && u.DisabledAt == null).Select(u => new { u.Email, u.Language }).SingleOrDefaultAsync(ct).ConfigureAwait(false)
                : null;
            if ((staff?.Email ?? department.StaffAlertEmail) is { Length: > 0 } to)
            {
                recipients.Add((to, staff?.Language, staff is not null));
            }
        }

        var named = rule.NotifyStaffIds;
        if (named.Length > 0)
        {
            recipients.AddRange((await db.Set<StaffUser>().AsNoTracking().Where(u => named.Contains(u.Id) && u.DisabledAt == null).OrderBy(u => u.Id)
                    .Select(u => new { u.Id, u.Email, u.Language }).ToListAsync(ct).ConfigureAwait(false))
                .Select(u => (u.Email, (string?)u.Language, u.Id == ticket.AssignedStaffId)));
        }

        foreach (var (to, language, assigned) in recipients.DistinctBy(r => r.Email.Trim().ToLowerInvariant()))
        {
            var model = Model(ticket, department);
            model["rule"] = rule.Name;
            model["minutes"] = rule.AfterMinutes;
            model["note"] = rule.Note;
            model["assigned"] = assigned;
            await notifier.Queue(TemplateKeys.TicketEscalated, to, language, model, $"{TemplateKeys.TicketEscalated}:{escalationId}:{Short(to)}", ct: ct).ConfigureAwait(false);
        }
    }

    private async Task<(Ticket Ticket, Department Department, string? Language)> Load(long ticketId, CancellationToken ct)
    {
        var ticket = await db.Set<Ticket>().AsNoTracking().SingleAsync(t => t.Id == ticketId, ct).ConfigureAwait(false);
        var department = await db.Set<Department>().AsNoTracking().SingleAsync(d => d.Id == ticket.DepartmentId, ct).ConfigureAwait(false);
        var language = ticket.AccountId is { } accountId
            ? await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Language).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        return (ticket, department, language);
    }

    private static Dictionary<string, object?> Model(Ticket ticket, Department department) => new()
    {
        ["ticket_id"] = ticket.Id,
        ["number"] = ticket.Number,
        ["subject"] = ticket.Subject,
        ["department"] = department.Name,
        ["requester"] = ticket.RequesterName,
        ["has_account"] = ticket.AccountId is not null,
        ["reply_marker"] = MailText.ReplyMarker,
    };

    /// <summary>El hilo del ticket y la casilla del departamento: lo que el cliente responda vuelve al ticket por IMAP.</summary>
    private Dictionary<string, string> Thread(Ticket ticket, Department department)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["In-Reply-To"] = ThreadId(ticket.ReplyToken, notifier.Options.From),
            ["References"] = ThreadId(ticket.ReplyToken, notifier.Options.From),
        };
        if (department.Email is { Length: > 0 } replyTo)
        {
            headers["Reply-To"] = replyTo;
        }

        return headers;
    }

    /// <summary>La casilla en la clave de deduplicación, acotada: la clave tiene 200 caracteres como mucho.</summary>
    private static string Short(string email) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..16];
}

/// <summary>Cierre, cada hora, de los tickets que esperan al cliente hace más de <see cref="SupportOptions.AutoCloseAfter"/>.</summary>
public sealed class AutoCloseHandler(TicketService tickets, SupportSettings settings, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.SupportAutoClose;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        if ((await settings.Get(ct).ConfigureAwait(false)).AutoCloseAfter is { } after)
        {
            await tickets.CloseInactive(time.GetUtcNow() - after, ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }
}
