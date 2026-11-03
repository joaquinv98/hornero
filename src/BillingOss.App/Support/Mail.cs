using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace BillingOss.App.Support;

/// <summary>El texto de un correo: el cuerpo en texto, o el HTML pasado a texto, sin lo que cita de mensajes anteriores.</summary>
public static partial class MailText
{
    /// <summary>La línea que va arriba de cada aviso de un ticket: lo que el cliente escribe debajo se descarta.</summary>
    public const string ReplyMarker = "--- Escribí tu respuesta arriba de esta línea / Reply above this line ---";

    public static string Body(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = message.TextBody is { Length: > 0 } plain ? plain : HtmlToText(message.HtmlBody ?? string.Empty);
        return StripQuoted(text);
    }

    /// <summary>
    /// Corta en la línea de la marca, si está, y saca el bloque citado del final ("&gt;" y la línea de "escribió:"). Lo
    /// citado en el medio queda: puede ser la respuesta intercalada del cliente.
    /// </summary>
    public static string StripQuoted(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var marker = lines.FindIndex(l => l.Contains("Reply above this line", StringComparison.OrdinalIgnoreCase) || l.Contains("arriba de esta línea", StringComparison.OrdinalIgnoreCase));
        if (marker >= 0)
        {
            lines = lines[..marker];
        }

        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[^1]) || lines[^1].TrimStart().StartsWith('>')))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0 && Attribution().IsMatch(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines).Trim();
    }

    /// <summary>HTML a texto con el tokenizador de MimeKit: párrafos y saltos como líneas, sin scripts ni estilos.</summary>
    public static string HtmlToText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = new StringBuilder();
        var skip = 0;
        var tokenizer = new HtmlTokenizer(new StringReader(html));
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag when tag.Id is HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Head or HtmlTagId.Title:
                    skip += tag.IsEndTag ? -1 : tag.IsEmptyElement ? 0 : 1;
                    break;
                case HtmlTagToken tag when tag.Id is HtmlTagId.Br || (tag.IsEndTag && tag.Id is HtmlTagId.P or HtmlTagId.Div or HtmlTagId.LI or HtmlTagId.TR or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 or HtmlTagId.BlockQuote):
                    text.Append('\n');
                    break;
                case HtmlDataToken data when skip <= 0:
                    text.Append(Spaces().Replace(data.Data, " "));
                    break;
            }
        }

        return string.Join('\n', text.ToString().Split('\n').Select(l => l.Trim())).Trim();
    }

    [GeneratedRegex(@"(wrote|escribió|escribio|a écrit|schrieb)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Attribution();

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Spaces();
}

/// <summary>
/// El correo que entra a la casilla de un departamento (sección 9 del modelo de dominio). Una respuesta encuentra su
/// ticket por el hilo (la ficha secreta de In-Reply-To o References), por el Message-ID que cita o por el número del
/// asunto. Por la ficha alcanza; por lo demás, quien escribe tiene que ser parte del ticket. Lo que no es respuesta abre
/// un ticket. Las respuestas automáticas, los rebotes y los correos propios se descartan sin contestar, así no hay
/// bucles. Corre dentro de la transacción de quien lo llama.
/// </summary>
public sealed partial class MailIntake(BillingDbContext db, TicketService tickets, SupportSettings settings, IOptions<NotificationOptions> mail, TimeProvider time)
{
    /// <summary>Procesa un correo y deja su registro. Devuelve null si ese correo ya se había procesado.</summary>
    public async Task<InboundEmail?> Process(Department department, MimeMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(department);
        ArgumentNullException.ThrowIfNull(message);
        var record = await Record(department, message.Headers, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        (record.Outcome, record.Reason, record.TicketId) = await Route(department, message, record.Sender, ct).ConfigureAwait(false);
        db.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return record;
    }

    /// <summary>Un correo que no se procesa (demasiado grande, o que falló): queda en el registro para el staff.</summary>
    public async Task<InboundEmail?> Reject(Department department, HeaderList headers, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(department);
        ArgumentNullException.ThrowIfNull(headers);
        var record = await Record(department, headers, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        record.Outcome = InboundOutcome.Rejected;
        record.Reason = reason.Length > 500 ? reason[..500] : reason;
        db.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return record;
    }

    private async Task<InboundEmail?> Record(Department department, HeaderList headers, CancellationToken ct)
    {
        var key = headers[HeaderId.MessageId] is { Length: > 0 } id
            ? TicketService.MailKey(id)
            : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', headers.Select(h => h.ToString())))));
        if (await db.Set<InboundEmail>().AnyAsync(e => e.MessageKey == key, ct).ConfigureAwait(false))
        {
            return null;
        }

        var from = InternetAddressList.TryParse(headers[HeaderId.From] ?? string.Empty, out var list) ? list.Mailboxes.FirstOrDefault() : null;
        var subject = headers[HeaderId.Subject] ?? string.Empty;
        return new InboundEmail
        {
            DepartmentId = department.Id,
            MessageKey = key,
            Sender = Limit(from?.Address?.Trim().ToLowerInvariant() ?? string.Empty, 320),
            Subject = Limit(subject, 300),
            ReceivedAt = time.GetUtcNow(),
        };
    }

    private async Task<(InboundOutcome, string?, long?)> Route(Department department, MimeMessage message, string sender, CancellationToken ct)
    {
        var options = await settings.Get(ct).ConfigureAwait(false);
        if (sender.Length == 0)
        {
            return (InboundOutcome.Ignored, "Sin remitente.", null);
        }

        if (Automatic(message, sender) is { } automatic)
        {
            return (InboundOutcome.Ignored, automatic, null);
        }

        var own = await OwnAddresses(ct).ConfigureAwait(false);
        if (own.Contains(sender))
        {
            return (InboundOutcome.Ignored, "Correo de una casilla propia.", null);
        }

        var body = MailText.Body(message);
        if (await Filtered(sender, message.Subject ?? string.Empty, body, ct).ConfigureAwait(false) is { } filter)
        {
            return (InboundOutcome.Rejected, filter, null);
        }

        var hourAgo = time.GetUtcNow().AddHours(-1);
        if (await db.Set<InboundEmail>().CountAsync(e => e.Sender == sender && e.ReceivedAt > hourAgo, ct).ConfigureAwait(false) >= options.MaxEmailsPerSenderPerHour)
        {
            return (InboundOutcome.Rejected, "Pasó el tope de correos por hora de este remitente.", null);
        }

        var (files, skipped) = Attachments(message, options);
        if (skipped.Count > 0)
        {
            body = (body + "\n\n" + string.Join('\n', skipped)).Trim();
        }

        body = body.Length == 0 ? "(sin texto)" : body.Length > TicketService.MaxBody ? body[..(TicketService.MaxBody - 20)] + "\n[texto recortado]" : body;
        var from = message.From.Mailboxes.First();
        var user = await db.Set<PortalUser>().AsNoTracking().Where(u => u.NormalizedEmail == sender.ToUpperInvariant()).Select(u => (long?)u.Id).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var author = new Author(MessageAuthor.Customer, Limit(string.IsNullOrWhiteSpace(from.Name) ? sender : from.Name.Trim(), 200), sender, MessageSource.Email, user);

        // Las otras casillas del correo pasan a las copias, al abrir y al responder, salvo que el ajuste lo apague.
        var cc = options.EmailCc ? message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address.Trim().ToLowerInvariant()).Where(a => !own.Contains(a) && a != sender).ToList() : [];
        if (await FindTicket(message, sender, ct).ConfigureAwait(false) is { } found)
        {
            // Solo la respuesta con la ficha del hilo suma copias: demuestra que quien escribe recibió un correo del ticket.
            // Por el número del asunto alcanza con conocerlo, y un remitente falso podría sumar su casilla a las respuestas.
            await tickets.Reply(found.Id, new ReplyDraft(body, Attachments: files, EmailMessageId: message.MessageId, Cc: found.ByToken ? cc : null), author, ct).ConfigureAwait(false);
            return (InboundOutcome.Replied, null, found.Id);
        }

        var account = await AccountOf(sender, user, ct).ConfigureAwait(false);
        if (department.ClientsOnly && account is null)
        {
            return (InboundOutcome.Rejected, "El departamento atiende solo a clientes y el remitente no es de ninguna cuenta.", null);
        }

        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "(sin asunto)" : Limit(message.Subject.Trim(), TicketService.MaxSubject);
        var opened = await tickets.Open(new TicketDraft(department.Id, subject, body, account, RequesterName: author.Name, RequesterEmail: sender, Cc: cc, EmailMessageId: message.MessageId, Attachments: files), author, ct).ConfigureAwait(false);
        return (InboundOutcome.Opened, null, opened.Id);
    }

    /// <summary>RFC 3834 y las marcas de uso común de contestadores y rebotes.</summary>
    private static string? Automatic(MimeMessage message, string sender)
    {
        var headers = message.Headers;
        if (headers["Auto-Submitted"] is { } auto && !auto.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase))
        {
            return "Respuesta automática (Auto-Submitted).";
        }

        if (headers[HeaderId.Precedence]?.Trim().ToLowerInvariant() is "bulk" or "junk" or "list" or "auto_reply")
        {
            return "Correo masivo o automático (Precedence).";
        }

        if (headers.Contains("X-Autoreply") || headers.Contains("X-Autorespond"))
        {
            return "Respuesta automática.";
        }

        var local = sender.Split('@')[0];
        return headers[HeaderId.ReturnPath]?.Trim() == "<>" || local is "mailer-daemon" or "postmaster" ? "Rebote." : null;
    }

    private async Task<HashSet<string>> OwnAddresses(CancellationToken ct)
    {
        var own = await db.Set<Department>().AsNoTracking().Where(d => d.Email != null).Select(d => d.Email!.ToLower()).ToListAsync(ct).ConfigureAwait(false);
        own.AddRange(await db.Set<Department>().AsNoTracking().Where(d => d.ImapUser != null).Select(d => d.ImapUser!.ToLower()).ToListAsync(ct).ConfigureAwait(false));
        if (mail.Value.From is { Length: > 0 } from)
        {
            own.Add(from.ToLowerInvariant());
        }

        return [.. own.Where(o => o.Contains('@', StringComparison.Ordinal))];
    }

    private async Task<string?> Filtered(string sender, string subject, string body, CancellationToken ct)
    {
        var domain = sender.Split('@') is [_, var d] ? d : string.Empty;
        foreach (var filter in await db.Set<MailFilter>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
        {
            var value = filter.Value.Trim().ToLowerInvariant();
            var hit = value.Length > 0 && filter.Kind switch
            {
                MailFilterKind.Sender => sender == value || domain == value.TrimStart('@') || (value.StartsWith('*') && sender.EndsWith(value[1..], StringComparison.Ordinal)),
                MailFilterKind.Subject => subject.Contains(value, StringComparison.OrdinalIgnoreCase),
                _ => body.Contains(value, StringComparison.OrdinalIgnoreCase),
            };
            if (hit)
            {
                return $"Filtro de {filter.Kind switch { MailFilterKind.Sender => "remitente", MailFilterKind.Subject => "asunto", _ => "frase" }}: {filter.Value}";
            }
        }

        return null;
    }

    /// <summary>Los adjuntos que pasan el tope y los tipos permitidos. Los demás quedan nombrados en el mensaje.</summary>
    private static (List<NewAttachment> Files, List<string> Skipped) Attachments(MimeMessage message, SupportOptions options)
    {
        var files = new List<NewAttachment>();
        var skipped = new List<string>();
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            var name = TicketService.SafeName(part.FileName ?? "adjunto");
            using var content = new MemoryStream();
            part.Content?.DecodeTo(content);
            switch (TicketService.Check(options, name, content.Length, files.Count))
            {
                case AttachmentProblem.None:
                    files.Add(new NewAttachment(name, content.ToArray()));
                    break;
                case AttachmentProblem.Type:
                    skipped.Add($"[Adjunto omitido: {name}, tipo no permitido]");
                    break;
                case AttachmentProblem.Size:
                    skipped.Add($"[Adjunto omitido: {name}, supera el tope o está vacío]");
                    break;
                default:
                    skipped.Add($"[Adjunto omitido: {name}, demasiados adjuntos]");
                    break;
            }
        }

        return (files, skipped);
    }

    /// <summary>El ticket al que responde y si lo encontró por la ficha secreta del hilo, o null si es un correo nuevo.</summary>
    private async Task<(long Id, bool ByToken)?> FindTicket(MimeMessage message, string sender, CancellationToken ct)
    {
        var refs = message.References.Append(message.InReplyTo ?? string.Empty).Where(r => r.Length > 0).Select(TicketService.MailKey).Distinct().ToList();
        var tokens = refs.Select(r => ThreadToken().Match(r)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        var tickets = db.Set<Ticket>().AsNoTracking();

        // Un ticket unido a otro lleva al que lo recibió: los mensajes y quien consultaba ya están en ese.
        if (tokens.Count > 0 && await tickets.Where(t => tokens.Contains(t.ReplyToken)).Select(t => t.MergedIntoId ?? t.Id).Cast<long?>().FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } byToken)
        {
            return (byToken, true);
        }

        var candidate = refs.Count > 0
            ? await db.Set<TicketMessage>().AsNoTracking().Where(m => m.EmailMessageId != null && refs.Contains(m.EmailMessageId)).Select(m => (long?)m.TicketId).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        if (candidate is null && SubjectNumber().Match(message.Subject ?? string.Empty) is { Success: true } number)
        {
            candidate = await tickets.Where(t => t.Number == number.Groups[1].Value).Select(t => t.MergedIntoId ?? t.Id).Cast<long?>().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }

        return candidate is { } id && await Participates(id, sender, ct).ConfigureAwait(false) ? (id, false) : null;
    }

    /// <summary>Es el solicitante, una copia, el correo de la cuenta o un usuario del portal con acceso al soporte de la cuenta.</summary>
    private async Task<bool> Participates(long ticketId, string sender, CancellationToken ct)
    {
        var ticket = await db.Set<Ticket>().AsNoTracking().SingleAsync(t => t.Id == ticketId, ct).ConfigureAwait(false);
        if (ticket.RequesterEmail.Equals(sender, StringComparison.OrdinalIgnoreCase) || ticket.Cc.Contains(sender))
        {
            return true;
        }

        if (ticket.AccountId is not { } accountId)
        {
            return false;
        }

        var normalized = sender.ToUpperInvariant();
        return await db.Accounts.AnyAsync(a => a.Id == accountId && a.Email.ToLower() == sender, ct).ConfigureAwait(false)
            || await (from m in db.Set<AccountUser>()
                      join u in db.Set<PortalUser>() on m.UserId equals u.Id
                      where m.AccountId == accountId && u.NormalizedEmail == normalized && (m.IsOwner || m.Capabilities.Contains(AccountCapabilities.Support))
                      select m.Id).AnyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>La cuenta del remitente: la única con soporte de su usuario del portal, o la que tiene ese correo.</summary>
    private async Task<long?> AccountOf(string sender, long? user, CancellationToken ct)
    {
        if (user is { } userId)
        {
            var accounts = await db.Set<AccountUser>().AsNoTracking()
                .Where(m => m.UserId == userId && (m.IsOwner || m.Capabilities.Contains(AccountCapabilities.Support)))
                .Select(m => m.AccountId).Distinct().Take(2).ToListAsync(ct).ConfigureAwait(false);
            if (accounts.Count == 1)
            {
                return accounts[0];
            }
        }

        return await db.Accounts.AsNoTracking().Where(a => a.Email != string.Empty && a.Email.ToLower() == sender).Select(a => (long?)a.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];

    [GeneratedRegex(@"^ticket\.([a-z2-7]{20})@")]
    private static partial Regex ThreadToken();

    /// <summary>
    /// El número entre corchetes, con o sin rótulo delante: "[#123456]" de los avisos propios y también los asuntos del
    /// sistema anterior, que nombran el número con su propio rótulo. Igual hace falta que quien escribe participe.
    /// </summary>
    [GeneratedRegex(@"\[[^\[\]\d]{0,40}(\d{6,8})\]")]
    private static partial Regex SubjectNumber();
}

/// <summary>
/// Lee la casilla IMAP de un departamento (sección 9 del modelo de dominio). Corre de a un trabajo por casilla, por su
/// clave de entidad, y nunca borra ni marca nada: abre la casilla en solo lectura y avanza un cursor por UID. Cada correo
/// se procesa y se registra en la misma transacción que el cursor, así una caída no lo pierde ni lo duplica, y si el
/// servidor cambia el UIDVALIDITY la relectura no duplica por el Message-ID. Una falla de conexión queda en el
/// departamento para el staff y el próximo turno reintenta.
/// </summary>
public sealed class MailPollHandler(BillingDbContext db, MailIntake intake, SecretBox secrets, SupportSettings settings, TimeProvider time, ILogger<MailPollHandler> logger) : IJobHandler
{
    /// <summary>Correos por turno: lo que queda sigue en el próximo, y el trabajo nunca se acerca al lease.</summary>
    public const int MaxPerPoll = 50;

    public string Kind => JobKinds.SupportMail;

    public static string Entity(long departmentId) => string.Create(CultureInfo.InvariantCulture, $"support:mail:{departmentId}");

    /// <summary>La contraseña de IMAP va atada a la fila y al servidor: cambiar el servidor obliga a cargarla de nuevo.</summary>
    public static byte[] Protect(SecretBox secrets, Department department, string password)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(department);
        return secrets.Encrypt(password, "support.departments", "imap_password", department.Id, department.ImapHost);
    }

    public static SecureSocketOptions Socket(MailSecurity security) => security switch
    {
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        MailSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.SslOnConnect,
    };

    /// <summary>Conecta y abre la bandeja de entrada en solo lectura. Lo usa también el botón de probar del panel.</summary>
    public static async Task<ImapClient> Connect(Department department, SecretBox secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(department);
        ArgumentNullException.ThrowIfNull(secrets);
        if (department.ImapHost is null || department.ImapUser is null || department.ImapPassword is null)
        {
            throw new InvalidOperationException("Faltan el servidor, el usuario o la contraseña de IMAP.");
        }

        var password = secrets.Decrypt(department.ImapPassword, "support.departments", "imap_password", department.Id, department.ImapHost);
        return await Connect(department.ImapHost, department.ImapPort, department.ImapSecurity, department.ImapUser, password, ct).ConfigureAwait(false);
    }

    /// <summary>Con la contraseña en claro: la usa el botón de probar del panel antes de guardarla.</summary>
    public static async Task<ImapClient> Connect(string host, int port, MailSecurity security, string user, string password, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(host, port, Socket(security), ct).ConfigureAwait(false);
            await client.AuthenticateAsync(user, password, ct).ConfigureAwait(false);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var json = JsonDocument.Parse(job.Payload);
        var id = json.RootElement.GetProperty("department_id").GetInt64();
        var department = await db.Set<Department>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.MailEnabled, ct).ConfigureAwait(false);
        if (department is null)
        {
            return JobResult.Done;
        }

        try
        {
            using var client = await Connect(department, secrets, ct).ConfigureAwait(false);
            var inbox = client.Inbox;
            var validity = (long)inbox.UidValidity;
            var last = validity == department.ImapUidValidity ? department.ImapLastUid : 0;
            var pending = inbox.Count == 0 ? [] : (await inbox.FetchAsync(new UniqueIdRange(new UniqueId(inbox.UidValidity, (uint)last + 1), UniqueId.MaxValue), MessageSummaryItems.UniqueId | MessageSummaryItems.Size, ct).ConfigureAwait(false))
                .Where(s => s.UniqueId.Id > last).OrderBy(s => s.UniqueId.Id).Take(MaxPerPoll).ToList();
            foreach (var summary in pending)
            {
                await One(department, inbox, summary, validity, ct).ConfigureAwait(false);
            }

            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            await State(id, pending.Count == 0 ? validity : null, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Failover.IsTransient(ex))
        {
            logger.LogWarning("La casilla del departamento {Department} falló: {Error}", id, ex.Message);
            await State(id, null, ex.Message, ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }

    /// <summary>
    /// Un correo y el cursor en la misma transacción. Si procesarlo falla por algo del propio correo, queda registrado
    /// como rechazado con el motivo y el cursor avanza: un correo roto no traba la casilla.
    /// </summary>
    private async Task One(Department department, IMailFolder inbox, IMessageSummary summary, long validity, CancellationToken ct)
    {
        var uid = summary.UniqueId;
        var max = (await settings.Get(ct).ConfigureAwait(false)).MaxEmailBytes;
        if (summary.Size > max)
        {
            var headers = await inbox.GetHeadersAsync(uid, ct).ConfigureAwait(false);
            await Advance(department, validity, uid.Id, () => intake.Reject(department, headers, $"Supera el tope de {max / (1024 * 1024)} MB.", ct), ct).ConfigureAwait(false);
            return;
        }

        var message = await inbox.GetMessageAsync(uid, ct).ConfigureAwait(false);
        try
        {
            await Advance(department, validity, uid.Id, () => intake.Process(department, message, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Failover.IsTransient(ex))
        {
            db.ChangeTracker.Clear();
            logger.LogWarning("Correo {Uid} del departamento {Department} no se pudo procesar: {Error}", uid.Id, department.Id, ex.Message);
            await Advance(department, validity, uid.Id, () => intake.Reject(department, message.Headers, $"No se pudo procesar: {ex.Message}", ct), ct).ConfigureAwait(false);
        }
    }

    private async Task Advance(Department department, long validity, uint uid, Func<Task<InboundEmail?>> work, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await work().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE support.departments SET imap_uid_validity = {validity}, imap_last_uid = {(long)uid}
            WHERE id = {department.Id} AND (imap_uid_validity <> {validity} OR imap_last_uid < {(long)uid})
            """, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private Task State(long id, long? validity, string? error, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var message = error is { Length: > 1000 } ? error[..1000] : error;
        return validity is { } v
            ? db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE support.departments SET mail_checked_at = {now}, mail_error = NULL,
                  imap_last_uid = CASE WHEN imap_uid_validity = {v} THEN imap_last_uid ELSE 0 END, imap_uid_validity = {v}
                WHERE id = {id}
                """, ct)
            : db.Database.ExecuteSqlInterpolatedAsync($"UPDATE support.departments SET mail_checked_at = {now}, mail_error = {message} WHERE id = {id}", ct);
    }
}
