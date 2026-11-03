using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BillingOss.App.Support;

/// <summary>Quién escribe un mensaje y por dónde llegó.</summary>
public sealed record Author(MessageAuthor Kind, string Name, string? Email, MessageSource Source, long? UserId = null, long? StaffId = null);

/// <summary>Un archivo subido o recibido. El tope de <see cref="SupportOptions.MaxAttachmentBytes"/> acota la memoria.</summary>
public sealed record NewAttachment(string FileName, byte[] Content);

/// <summary>
/// Un ticket nuevo. Para el portal, la cuenta ya viene filtrada por <c>PortalAccess</c>. El solicitante sale del
/// autor si es un cliente, o de la cuenta si lo abre el staff, salvo que se indique otro.
/// </summary>
public sealed record TicketDraft(
    long DepartmentId,
    string Subject,
    string Body,
    long? AccountId = null,
    TicketPriority Priority = TicketPriority.Normal,
    long? ServiceId = null,
    IReadOnlyList<NewAttachment>? Attachments = null,
    string? RequesterName = null,
    string? RequesterEmail = null,
    IReadOnlyList<string>? Cc = null,
    string? ClientReference = null,
    string? EmailMessageId = null,
    bool Notify = true);

/// <summary>
/// Una respuesta. El estado lo elige el staff; un mensaje del cliente siempre deja el ticket abierto. <see cref="Cc"/> son
/// las otras casillas de una respuesta por correo, que se suman a las copias del ticket.
/// </summary>
public sealed record ReplyDraft(string Body, bool Internal = false, TicketStatus? Status = null, IReadOnlyList<NewAttachment>? Attachments = null, string? EmailMessageId = null, IReadOnlyList<string>? Cc = null);

/// <summary>Cambios del staff sobre un ticket. Lo que viene nulo no cambia.</summary>
public sealed record TicketChange(TicketStatus? Status = null, TicketPriority? Priority = null, long? DepartmentId = null, long? AssignedStaffId = null, bool Unassign = false);

public sealed record OpenedTicket(long Id, string Number, long MessageId);

public enum AttachmentProblem
{
    None,
    Type,
    Size,
    Count,
}

/// <summary>
/// Casos de uso del soporte (sección 9 del modelo de dominio): abrir, responder, notas internas, estados, prioridad,
/// departamento, asignación y cierre. Los mismos para el portal, el panel, la API y el correo entrante. Cada método
/// corre en la transacción de quien llama o en una propia, y publica TicketOpened o TicketReplied en el outbox.
/// </summary>
public sealed class TicketService(BillingDbContext db, JobQueue jobs, AuditLog audit, SupportSettings settings, TimeProvider time)
{
    /// <summary>Largo máximo del cuerpo de un mensaje, en caracteres.</summary>
    public const int MaxBody = 65_536;

    public const int MaxSubject = 300;

    /// <summary>Lock de PostgreSQL que serializa la elección del número público entre nodos.</summary>
    private const long NumberLock = 4242101;

    /// <summary>El primer problema de un adjunto, o <see cref="AttachmentProblem.None"/>. El portal lo traduce a su texto.</summary>
    public static AttachmentProblem Check(SupportOptions options, string fileName, long size, int index = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (index >= options.MaxAttachments)
        {
            return AttachmentProblem.Count;
        }

        var extension = Path.GetExtension(SafeName(fileName)).ToLowerInvariant();
        if (extension.Length == 0 || !options.Extensions.Contains(extension))
        {
            return AttachmentProblem.Type;
        }

        return size <= 0 || size > options.MaxAttachmentBytes ? AttachmentProblem.Size : AttachmentProblem.None;
    }

    /// <summary>Nombre sin carpetas ni caracteres de control, de 120 caracteres como mucho y con su extensión.</summary>
    public static string SafeName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/')[^1]);
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim().Trim('.');
        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            name = name[..(120 - Math.Min(extension.Length, 20))] + extension[..Math.Min(extension.Length, 20)];
        }

        return name.Length == 0 ? "adjunto" : name;
    }

    /// <summary>
    /// Abre un ticket. Con la misma <see cref="TicketDraft.ClientReference"/> devuelve el que ya existe, también si dos
    /// pedidos llegan a la vez (la API reintenta, el flujo de n8n repite).
    /// </summary>
    public async Task<OpenedTicket> Open(TicketDraft draft, Author author, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(author);
        var subject = Clean(draft.Subject, MaxSubject, "El asunto");
        var body = Clean(draft.Body, MaxBody, "El mensaje");
        ValidateAttachments(await settings.Get(ct).ConfigureAwait(false), draft.Attachments);
        if (draft.ClientReference is { } reference && await Existing(reference, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var ownsTransaction = db.Database.CurrentTransaction is null;
        try
        {
            return await OpenNew(draft, author, subject, body, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ownsTransaction && draft.ClientReference is not null
            && ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_tickets_client_reference" })
        {
            db.ChangeTracker.Clear();
            return await Existing(draft.ClientReference, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("El ticket de la misma referencia no aparece.", ex);
        }
    }

    private async Task<OpenedTicket?> Existing(string reference, CancellationToken ct)
    {
        var existing = await db.Set<Ticket>().AsNoTracking().SingleOrDefaultAsync(t => t.ClientReference == reference, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var first = await db.Set<TicketMessage>().Where(m => m.TicketId == existing.Id).MinAsync(m => m.Id, ct).ConfigureAwait(false);
        return new OpenedTicket(existing.Id, existing.Number, first);
    }

    private async Task<OpenedTicket> OpenNew(TicketDraft draft, Author author, string subject, string body, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await using (own)
        {
            var department = await db.Set<Department>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == draft.DepartmentId, ct).ConfigureAwait(false)
                ?? throw new ValidationException($"No existe el departamento {draft.DepartmentId}.");
            if (department.Hidden && author.Source == MessageSource.Portal)
            {
                throw new ValidationException($"No existe el departamento {draft.DepartmentId}.");
            }

            var account = draft.AccountId is { } accountId
                ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.")
                : null;
            if (draft.ServiceId is { } serviceId && !await db.Services.AnyAsync(s => s.Id == serviceId && s.AccountId == draft.AccountId, ct).ConfigureAwait(false))
            {
                throw new ValidationException($"El servicio {serviceId} no es de la cuenta.");
            }

            var customer = author.Kind == MessageAuthor.Customer;
            var given = (draft.RequesterEmail ?? (customer ? author.Email : account?.Email))?.Trim();
            if (string.IsNullOrEmpty(given))
            {
                throw new ValidationException("Falta el correo de quien consulta.");
            }

            // A esta casilla van los avisos y con ella se reconocen sus respuestas: sin nombre ni ángulos.
            var email = MailAddress.TryCreate(given, out var address) ? address.Address : throw new ValidationException($"El correo de quien consulta no es válido: {given}.");

            var ticket = new Ticket
            {
                Number = await NewNumber(ct).ConfigureAwait(false),
                AccountId = account?.Id,
                RequesterUserId = customer ? author.UserId : null,
                RequesterName = Limit(draft.RequesterName ?? (customer ? author.Name : account?.Name) ?? email, 200),
                RequesterEmail = Limit(email, 320),
                Cc = Recipients(draft.Cc, email),
                DepartmentId = department.Id,
                ServiceId = draft.ServiceId,
                Subject = subject,
                Status = customer ? TicketStatus.Open : TicketStatus.AwaitingCustomer,
                Priority = draft.Priority,
                AssignedStaffId = author.StaffId,
                ReplyToken = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz234567", 20),
                ClientReference = draft.ClientReference,
                CreatedAt = now,
                LastMessageAt = now,
                UpdatedAt = now,
            };
            db.Add(ticket);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var message = await AddMessage(ticket.Id, author, body, false, draft.EmailMessageId, draft.Attachments, now, ct).ConfigureAwait(false);
            await jobs.Publish(Events.TicketOpened, new { ticket_id = ticket.Id, message_id = message, quiet = !draft.Notify }, Entity(ticket.Id), ct).ConfigureAwait(false);
            if (own is not null)
            {
                await own.CommitAsync(ct).ConfigureAwait(false);
            }

            return new OpenedTicket(ticket.Id, ticket.Number, message);
        }
    }

    /// <summary>
    /// Respuesta del cliente o del staff, o nota interna. Escribir en un ticket cerrado lo reabre si es el cliente. La
    /// primera respuesta del staff a un ticket sin asignar se lo asigna.
    /// </summary>
    public async Task<long> Reply(long ticketId, ReplyDraft draft, Author author, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(author);
        var body = Clean(draft.Body, MaxBody, "El mensaje");
        ValidateAttachments(await settings.Get(ct).ConfigureAwait(false), draft.Attachments);
        var isInternal = draft.Internal && author.Kind == MessageAuthor.Staff;
        var now = time.GetUtcNow();
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await using (own)
        {
            await Sql.LockRows(db, "support.tickets", [ticketId], ct).ConfigureAwait(false);
            var ticket = await db.Set<Ticket>().SingleOrDefaultAsync(t => t.Id == ticketId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"No existe el ticket {ticketId}.");
            if (ticket.MergedIntoId is not null)
            {
                throw new ValidationException("El ticket se unió a otro: la respuesta va en ese.");
            }

            var wasClosed = ticket.Status == TicketStatus.Closed;
            if (author.Kind == MessageAuthor.Customer)
            {
                ticket.CustomerWrote(now);
                if (draft.Cc is { Count: > 0 } cc)
                {
                    ticket.Cc = Recipients([.. ticket.Cc, .. cc], ticket.RequesterEmail);
                }
            }
            else if (isInternal)
            {
                if (draft.Status is { } status)
                {
                    ticket.Move(status, now);
                }

                ticket.UpdatedAt = now;
            }
            else
            {
                ticket.StaffWrote(draft.Status ?? TicketStatus.AwaitingCustomer, now);
                ticket.AssignedStaffId ??= author.StaffId;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var closes = !wasClosed && ticket.Status == TicketStatus.Closed;
            var message = await AddMessage(ticketId, author, body, isInternal, draft.EmailMessageId, draft.Attachments, now, ct).ConfigureAwait(false);
            if (!isInternal)
            {
                await jobs.Publish(Events.TicketReplied, new { ticket_id = ticketId, message_id = message }, Entity(ticketId), ct).ConfigureAwait(false);
            }

            if (closes)
            {
                await Closed(ticketId, ct).ConfigureAwait(false);
            }

            if (own is not null)
            {
                await own.CommitAsync(ct).ConfigureAwait(false);
            }

            return message;
        }
    }

    /// <summary>Cambios del staff. Quedan en la auditoría con lo que cambió.</summary>
    public async Task Update(long ticketId, TicketChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "support.tickets", [ticketId], ct).ConfigureAwait(false);
        var ticket = await db.Set<Ticket>().SingleOrDefaultAsync(t => t.Id == ticketId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el ticket {ticketId}.");
        if (ticket.MergedIntoId is not null)
        {
            throw new ValidationException("El ticket se unió a otro: los cambios van en ese.");
        }

        if (change.DepartmentId is { } department && !await db.Set<Department>().AnyAsync(d => d.Id == department, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe el departamento {department}.");
        }

        if (change.AssignedStaffId is { } staff && !await db.Set<Identity.StaffUser>().AnyAsync(u => u.Id == staff && u.DisabledAt == null, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe el usuario del staff {staff}.");
        }

        var before = Snapshot(ticket);
        var wasClosed = ticket.Status == TicketStatus.Closed;
        if (change.Status is { } status)
        {
            ticket.Move(status, now);
        }

        ticket.Priority = change.Priority ?? ticket.Priority;
        ticket.DepartmentId = change.DepartmentId ?? ticket.DepartmentId;
        ticket.AssignedStaffId = change.Unassign ? null : change.AssignedStaffId ?? ticket.AssignedStaffId;
        ticket.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("support.ticket.update", $"ticket:{ticketId}", new
        {
            before,
            after = Snapshot(ticket),
        }, ct).ConfigureAwait(false);
        if (!wasClosed && ticket.Status == TicketStatus.Closed)
        {
            await Closed(ticketId, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Se cerró: la encuesta de satisfacción sale, si el departamento la pide, por el aviso del ticket.</summary>
    private Task Closed(long ticketId, CancellationToken ct) =>
        jobs.Publish(Events.TicketClosed, new { ticket_id = ticketId }, Entity(ticketId), ct);

    /// <summary>Lo que cambia el staff, con los estados en texto para que la auditoría se lea sola.</summary>
    private static object Snapshot(Ticket t) => new { status = t.Status.ToString(), priority = t.Priority.ToString(), department = t.DepartmentId, assigned = t.AssignedStaffId };

    /// <summary>El cliente da la consulta por resuelta. Si después escribe, se reabre.</summary>
    public async Task Close(long ticketId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "support.tickets", [ticketId], ct).ConfigureAwait(false);
        var ticket = await db.Set<Ticket>().SingleOrDefaultAsync(t => t.Id == ticketId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el ticket {ticketId}.");
        if (ticket.Status == TicketStatus.Closed)
        {
            return;
        }

        ticket.Move(TicketStatus.Closed, time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("support.ticket.close", $"ticket:{ticketId}", null, ct).ConfigureAwait(false);
        await Closed(ticketId, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cierra los tickets que esperan al cliente desde antes de <paramref name="before"/>, con una marca del sistema
    /// que el cliente ve. Devuelve cuántos cerró.
    /// </summary>
    public async Task<int> CloseInactive(DateTimeOffset before, CancellationToken ct = default)
    {
        var ids = await db.Set<Ticket>().Where(t => t.Status == TicketStatus.AwaitingCustomer && t.LastMessageAt < before)
            .OrderBy(t => t.Id).Select(t => t.Id).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var system = new Author(MessageAuthor.System, "Sistema", null, MessageSource.Panel);
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await Sql.LockRows(db, "support.tickets", [id], ct).ConfigureAwait(false);
            var ticket = await db.Set<Ticket>().SingleAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (ticket.Status != TicketStatus.AwaitingCustomer || ticket.LastMessageAt >= before)
            {
                continue;
            }

            ticket.Move(TicketStatus.Closed, time.GetUtcNow());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AddMessage(id, system, AutoClosedText, false, null, null, time.GetUtcNow(), ct).ConfigureAwait(false);
            await Closed(id, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return ids.Count;
    }

    /// <summary>La marca del cierre automático. Es texto del sistema, en los dos idiomas.</summary>
    public const string AutoClosedText = "Cerramos la consulta porque pasó un tiempo sin respuesta. Si hace falta, respondé y se reabre.\nWe closed this request after a while without a reply. Reply to reopen it.";

    /// <summary>
    /// Une <paramref name="sourceId"/> a <paramref name="targetId"/>: los mensajes, con sus adjuntos, pasan al segundo, el
    /// primero queda cerrado y apuntando al segundo, y quien consultaba en el primero pasa a las copias. Solo se une a un
    /// ticket de la misma cuenta, o uno sin cuenta (un correo de alguien que el staff reconoce) a cualquiera. Los que ya
    /// se habían unido al primero pasan a apuntar al segundo: nunca hay que seguir más de un paso.
    /// </summary>
    public async Task Merge(long sourceId, long targetId, CancellationToken ct = default)
    {
        if (sourceId == targetId)
        {
            throw new ValidationException("Un ticket no se puede unir consigo mismo.");
        }

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "support.tickets", [sourceId, targetId], ct).ConfigureAwait(false);
        var both = await db.Set<Ticket>().Where(t => t.Id == sourceId || t.Id == targetId).ToListAsync(ct).ConfigureAwait(false);
        var source = both.SingleOrDefault(t => t.Id == sourceId) ?? throw new KeyNotFoundException($"No existe el ticket {sourceId}.");
        var target = both.SingleOrDefault(t => t.Id == targetId) ?? throw new ValidationException($"No existe el ticket {targetId}.");
        if (source.MergedIntoId is not null || target.MergedIntoId is not null)
        {
            throw new ValidationException("Uno de los dos tickets ya se unió a otro.");
        }

        if (source.AccountId is not null && source.AccountId != target.AccountId)
        {
            throw new ValidationException("Solo se unen tickets de la misma cuenta, o uno sin cuenta a cualquiera.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE support.messages SET ticket_id = {targetId} WHERE ticket_id = {sourceId}", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE support.tickets SET merged_into_id = {targetId} WHERE merged_into_id = {sourceId}", ct).ConfigureAwait(false);
        target.Cc = Recipients([.. target.Cc, .. source.Cc, source.RequesterEmail], target.RequesterEmail);
        if (source.WaitsForStaff && !target.WaitsForStaff)
        {
            target.Move(TicketStatus.Open, now);
        }

        target.LastMessageAt = source.LastMessageAt > target.LastMessageAt ? source.LastMessageAt : target.LastMessageAt;
        target.UpdatedAt = now;
        source.MergedIntoId = targetId;
        source.Move(TicketStatus.Closed, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var system = new Author(MessageAuthor.System, "Sistema", null, MessageSource.Panel);
        await AddMessage(targetId, system, MergedText(source.Number, source.Subject), false, null, null, now, ct).ConfigureAwait(false);
        await audit.Record("support.ticket.merge", Entity(targetId), new { source = sourceId, source_number = source.Number }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>La marca de la unión, que ven el cliente y el staff, en los dos idiomas.</summary>
    public static string MergedText(string number, string subject) =>
        $"Unimos a esta la consulta N.º {number} ({subject}): sus mensajes están arriba.\nRequest #{number} ({subject}) was merged into this one: its messages are above.";

    /// <summary>
    /// La respuesta a la encuesta de satisfacción, con la ficha del enlace. Una por ticket: da false si ya se había
    /// respondido. Guarda quién atendía, para el reporte por persona.
    /// </summary>
    public async Task<bool> Rate(string token, int rating, string? comment, CancellationToken ct = default)
    {
        if (rating is < 0 or > 10)
        {
            throw new ValidationException("La calificación va del 0 al 10.");
        }

        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text?.Length > TicketFeedback.MaxComment)
        {
            throw new ValidationException($"El comentario supera los {TicketFeedback.MaxComment} caracteres.");
        }

        var ticket = await db.Set<Ticket>().AsNoTracking().Where(t => t.FeedbackToken == token).Select(t => new { t.Id, t.AssignedStaffId }).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("No existe la encuesta.");
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO support.feedback (ticket_id, rating, comment, staff_id, imported, created_at)
            VALUES ({ticket.Id}, {rating}, {text}, {ticket.AssignedStaffId}, false, {time.GetUtcNow()})
            ON CONFLICT (ticket_id) DO NOTHING
            """, ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Aplica las reglas de escalamiento prendidas a los tickets que cumplen sus condiciones. Cada ticket va en su
    /// transacción con su lock, y el registro de la escalación, con clave única por regla, ticket y espera, hace que dos
    /// nodos o dos corridas no la apliquen dos veces. Devuelve cuántas aplicó.
    /// </summary>
    public async Task<int> Escalate(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var rules = await db.Set<EscalationRule>().AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
        var applied = 0;
        foreach (var rule in rules)
        {
            var before = now.AddMinutes(-rule.AfterMinutes);
            var statuses = rule.EffectiveStatuses;
            var candidates = db.Set<Ticket>().AsNoTracking().Where(t => t.MergedIntoId == null && statuses.Contains(t.Status) && t.LastMessageAt <= before
                && !db.Set<TicketEscalation>().Any(e => e.RuleId == rule.Id && e.TicketId == t.Id && e.Since == t.LastMessageAt));
            if (rule.DepartmentIds.Length > 0)
            {
                candidates = candidates.Where(t => rule.DepartmentIds.Contains(t.DepartmentId));
            }

            if (rule.Priorities.Length > 0)
            {
                candidates = candidates.Where(t => rule.Priorities.Contains(t.Priority));
            }

            // ponytail: 200 por regla y por corrida, la próxima corrida sigue con el resto.
            foreach (var id in await candidates.OrderBy(t => t.LastMessageAt).Select(t => t.Id).Take(200).ToListAsync(ct).ConfigureAwait(false))
            {
                applied += await EscalateOne(rule, id, before, now, ct).ConfigureAwait(false) ? 1 : 0;
            }
        }

        return applied;
    }

    private async Task<bool> EscalateOne(EscalationRule rule, long ticketId, DateTimeOffset before, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "support.tickets", [ticketId], ct).ConfigureAwait(false);
        var ticket = await db.Set<Ticket>().SingleAsync(t => t.Id == ticketId, ct).ConfigureAwait(false);
        if (ticket.MergedIntoId is not null || !rule.EffectiveStatuses.Contains(ticket.Status) || ticket.LastMessageAt > before
            || (rule.DepartmentIds.Length > 0 && !rule.DepartmentIds.Contains(ticket.DepartmentId)) || (rule.Priorities.Length > 0 && !rule.Priorities.Contains(ticket.Priority)))
        {
            return false;
        }

        object? inserted;
        await using (var insert = await Sql.Command(db, """
            INSERT INTO support.escalations (rule_id, ticket_id, since, applied_at) VALUES (@rule, @ticket, @since, @now)
            ON CONFLICT (rule_id, ticket_id, since) DO NOTHING RETURNING id
            """, ct, ("rule", rule.Id), ("ticket", ticketId), ("since", ticket.LastMessageAt), ("now", now)).ConfigureAwait(false))
        {
            inserted = await insert.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        if (inserted is not long escalation)
        {
            return false;
        }

        var previous = Snapshot(ticket);
        var wasClosed = ticket.Status == TicketStatus.Closed;
        if (rule.NewDepartmentId is { } department && await db.Set<Department>().AnyAsync(d => d.Id == department, ct).ConfigureAwait(false))
        {
            ticket.DepartmentId = department;
        }

        if (rule.AssignStaffId is { } staff && await db.Set<Identity.StaffUser>().AnyAsync(u => u.Id == staff && u.DisabledAt == null, ct).ConfigureAwait(false))
        {
            ticket.AssignedStaffId = staff;
        }

        if (rule.NewStatus is { } status)
        {
            ticket.Move(status, now);
        }

        ticket.Priority = rule.NewPriority ?? ticket.Priority;
        ticket.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (rule.Note is { Length: > 0 } note)
        {
            await AddMessage(ticketId, new Author(MessageAuthor.System, "Sistema", null, MessageSource.Panel), note, true, null, null, now, ct).ConfigureAwait(false);
        }

        if (rule.Reply is { Length: > 0 } reply)
        {
            // Sale a nombre del departamento y con el aviso de una respuesta, pero como mensaje del sistema: no mueve la
            // espera, así la regla no se vuelve a aplicar por su propia respuesta, ni cuenta como primera respuesta.
            var from = await db.Set<Department>().Where(d => d.Id == ticket.DepartmentId).Select(d => d.Name).SingleAsync(ct).ConfigureAwait(false);
            var replyId = await AddMessage(ticketId, new Author(MessageAuthor.System, from, null, MessageSource.Panel), reply, false, null, null, now, ct).ConfigureAwait(false);
            await jobs.Publish(Events.TicketReplied, new { ticket_id = ticketId, message_id = replyId, system_reply = true }, Entity(ticketId), ct).ConfigureAwait(false);
        }

        await audit.Record("support.ticket.escalate", Entity(ticketId), new { rule = rule.Id, rule.Name, before = previous, after = Snapshot(ticket) }, ct).ConfigureAwait(false);
        if (rule.Notify || rule.NotifyStaffIds.Length > 0)
        {
            await jobs.Publish(Events.TicketEscalated, new { ticket_id = ticketId, escalation_id = escalation }, Entity(ticketId), ct).ConfigureAwait(false);
        }

        if (!wasClosed && ticket.Status == TicketStatus.Closed)
        {
            await Closed(ticketId, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public static string Entity(long ticketId) => string.Create(CultureInfo.InvariantCulture, $"ticket:{ticketId}");

    private async Task<long> AddMessage(long ticketId, Author author, string body, bool isInternal, string? emailMessageId, IReadOnlyList<NewAttachment>? attachments, DateTimeOffset now, CancellationToken ct)
    {
        var message = new TicketMessage
        {
            TicketId = ticketId,
            Author = author.Kind,
            UserId = author.UserId,
            StaffId = author.StaffId,
            AuthorName = Limit(author.Name, 200),
            AuthorEmail = author.Email is null ? null : Limit(author.Email, 320),
            Body = body,
            Internal = isInternal,
            Source = author.Source,
            EmailMessageId = emailMessageId is null ? null : MailKey(emailMessageId),
            CreatedAt = now,
        };
        db.Add(message);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var file in attachments ?? [])
        {
            var name = SafeName(file.FileName);
            var attachment = new TicketAttachment { MessageId = message.Id, FileName = name, ContentType = MimeTypes.GetMimeType(name), Size = file.Content.Length, CreatedAt = now };
            db.Add(attachment);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.Add(new AttachmentContent { AttachmentId = attachment.Id, Content = file.Content });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.Entry(attachment).State = EntityState.Detached;
        }

        db.ChangeTracker.Clear();
        return message.Id;
    }

    /// <summary>
    /// Seis dígitos al azar, como los del sistema anterior, distintos de todos los existentes. El lock de la
    /// transacción hace que dos nodos no elijan el mismo a la vez. Si los de seis se llenan, pasa a ocho.
    /// </summary>
    private async Task<string> NewNumber(CancellationToken ct)
    {
        await using (var cmd = await Sql.Command(db, $"SELECT pg_advisory_xact_lock({NumberLock})", ct).ConfigureAwait(false))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var attempt = 0; ; attempt++)
        {
            var number = (attempt < 20 ? RandomNumberGenerator.GetInt32(100_000, 1_000_000) : RandomNumberGenerator.GetInt32(10_000_000, 100_000_000)).ToString(CultureInfo.InvariantCulture);
            if (!await db.Set<Ticket>().AnyAsync(t => t.Number == number, ct).ConfigureAwait(false))
            {
                return number;
            }
        }
    }

    private static void ValidateAttachments(SupportOptions options, IReadOnlyList<NewAttachment>? attachments)
    {
        for (var i = 0; i < (attachments?.Count ?? 0); i++)
        {
            var file = attachments![i];
            switch (Check(options, file.FileName, file.Content.LongLength, i))
            {
                case AttachmentProblem.Count:
                    throw new ValidationException($"Se pueden adjuntar hasta {options.MaxAttachments} archivos por mensaje.");
                case AttachmentProblem.Type:
                    throw new ValidationException($"El tipo de archivo de {SafeName(file.FileName)} no está permitido. Se aceptan {string.Join(", ", options.Extensions)}.");
                case AttachmentProblem.Size:
                    throw new ValidationException($"El archivo {SafeName(file.FileName)} está vacío o supera los {options.MaxAttachmentBytes / (1024 * 1024)} MB.");
            }
        }
    }

    /// <summary>Clave de un Message-ID para buscar y deduplicar: sin ángulos, en minúsculas y acotada.</summary>
    public static string MailKey(string messageId)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        var key = messageId.Trim().Trim('<', '>').ToLowerInvariant();
        return key.Length <= 300 ? key : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>Las copias como casillas sueltas, en minúsculas, sin repetir ni al solicitante, hasta diez.</summary>
    private static string[] Recipients(IReadOnlyList<string>? cc, string requester) =>
        [.. (cc ?? []).Select(c => MailAddress.TryCreate(c.Trim(), out var address) ? address.Address.ToLowerInvariant() : null).OfType<string>()
            .Where(c => c.Length <= 320 && !c.Equals(requester, StringComparison.OrdinalIgnoreCase))
            .Distinct().Take(10)];

    private static string Clean(string? text, int max, string what)
    {
        var value = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (value.Length == 0)
        {
            throw new ValidationException($"{what} no puede estar vacío.");
        }

        return value.Length > max ? throw new ValidationException($"{what} supera los {max} caracteres.") : value;
    }

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];
}
