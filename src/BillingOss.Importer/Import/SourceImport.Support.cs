using System.Globalization;
using System.Security.Cryptography;
using BillingOss.App.Identity;
using BillingOss.App.Support;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace BillingOss.Importer.Import;

/// <summary>
/// El soporte de la copia (D-17): departamentos y tickets con sus IDs, mensajes, notas internas, respuestas
/// predefinidas, filtros del correo, reglas de escalamiento y encuestas. El mapeo sale de la estructura de la copia.
/// Los adjuntos entran con su nombre, y con su archivo si está en la carpeta de adjuntos del origen (SupportFiles). Las
/// contraseñas de las casillas no se importan (regla 4 de la sección 3.6): cada casilla queda apagada hasta que el
/// staff la cargue.
/// </summary>
public sealed partial class SourceImport
{
    /// <summary>Tablas del soporte cuyas filas se concilian contra la línea base, con la cifra que les corresponde.</summary>
    public static IReadOnlyList<string> SupportTables { get; } =
        ["tblticketdepartments", "tbltickets", "tblticketreplies", "tblticketnotes", "tblticketpredefinedreplies", "tblticketspamfilters", "tblticketescalations", "tblticketfeedback"];

    private readonly HashSet<long> departments = [];
    private readonly Dictionary<long, long> replacements = [];

    /// <summary>A qué ticket van los mensajes de cada ticket del origen: el mismo, o el último de su cadena de uniones.</summary>
    private readonly Dictionary<long, long> ticketTarget = [];

    /// <summary>El usuario del staff de cada persona del origen, por su ID y por su nombre completo.</summary>
    private readonly Dictionary<long, long> staffById = [];
    private readonly Dictionary<string, long> staffByName = new(StringComparer.OrdinalIgnoreCase);

    private async Task Support(CancellationToken ct)
    {
        await StaffMap(ct).ConfigureAwait(false);
        await Departments(ct).ConfigureAwait(false);
        await Tickets(ct).ConfigureAwait(false);
        await SupportMessages(ct).ConfigureAwait(false);
        await SupportSettings(ct).ConfigureAwait(false);
        await Feedback(ct).ConfigureAwait(false);
        if (ticketTarget.Count > 0)
        {
            await log.WriteLineAsync($"Tickets: {ticketTarget.Count}, mensajes: {Figures.GetValueOrDefault(("support_rows", "tblticketreplies")).Count}, notas: {Figures.GetValueOrDefault(("support_rows", "tblticketnotes")).Count}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// El staff no se importa: sus contraseñas no se descifran y cada persona
    /// se carga antes en el sistema nuevo. La que ya tiene usuario con el mismo correo conserva sus tickets asignados, la
    /// firma de sus mensajes, sus encuestas y las reglas que la nombran. Las demás se cuentan.
    /// </summary>
    private async Task StaffMap(CancellationToken ct)
    {
        var admins = Rows("tbladmins").Select(r => (Id: Long(r, "id"), Email: (Str(r, "email") ?? string.Empty).Trim().ToUpperInvariant(), Name: $"{Str(r, "firstname")} {Str(r, "lastname")}".Trim())).ToList();
        var emails = admins.Select(a => a.Email).Where(e => e.Length > 0).ToList();

        // La firma de un mensaje es el nombre: solo vale el que no se repite entre las personas del origen.
        var unique = admins.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var users = emails.Count == 0 ? [] : await db.Set<StaffUser>().AsNoTracking().Where(u => emails.Contains(u.NormalizedEmail!)).ToDictionaryAsync(u => u.NormalizedEmail!, u => u.Id, ct).ConfigureAwait(false);
        foreach (var admin in admins)
        {
            if (!users.TryGetValue(admin.Email, out var user))
            {
                Count("support_control", "staff_sin_usuario");
                continue;
            }

            staffById[admin.Id] = user;
            if (admin.Name.Length > 0 && unique.Contains(admin.Name))
            {
                staffByName[admin.Name] = user;
            }

            Count("support_control", "staff_con_usuario");
        }
    }

    private long? StaffOf(long sourceId) => staffById.TryGetValue(sourceId, out var user) ? user : null;

    private async Task Departments(CancellationToken ct)
    {
        foreach (var r in Rows("tblticketdepartments"))
        {
            var id = Long(r, "id");
            var host = Blank(Str(r, "host"));
            var department = new Department
            {
                Name = Limit(Blank(Str(r, "name")) ?? $"Departamento {id}", 200),
                Description = Blank(Str(r, "description")) is { } description ? Limit(description, 1000) : null,
                Email = Blank(Str(r, "email"))?.ToLowerInvariant() is { } email && MailboxAddress.TryParse(email, out _) ? Limit(email, 320) : null,
                Hidden = Str(r, "hidden") == "on",
                ClientsOnly = Str(r, "clientsonly") == "on",
                AutoReply = Str(r, "noautoresponder") != "on",
                RequestFeedback = Long(r, "feedback_request") == 1,
                SortOrder = (int)Long(r, "order"),
                ImapHost = host is null ? null : Limit(host, 253),
                ImapPort = int.TryParse(Str(r, "port"), CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : 993,
                ImapUser = Blank(Str(r, "login")) is { } user ? Limit(user, 320) : null,
                CreatedAt = now,
            };
            department.SetImportedId(id);
            db.Add(department);
            departments.Add(id);
            Count("support_rows", "tblticketdepartments");
            if (host is not null)
            {
                Incidents.Add($"Departamento {id}: leía una casilla de correo. Queda apagada hasta cargar la contraseña, que no se importa.");
            }

            if (Str(r, "piperepliesonly") == "on")
            {
                Incidents.Add($"Departamento {id}: por correo solo aceptaba respuestas. El núcleo también abre tickets por correo: revisar si conviene marcarlo solo para clientes.");
            }
        }

        await Flush(ct).ConfigureAwait(false);
    }

    private async Task Tickets(CancellationToken ct)
    {
        var people = Rows("tblclients").ToDictionary(r => Long(r, "id"), r => (Name: $"{Str(r, "firstname")} {Str(r, "lastname")}".Trim(), Email: Str(r, "email")?.Trim() ?? string.Empty));
        var closedStatuses = Rows("tblticketstatuses").Where(r => Long(r, "showactive") == 0).Select(r => Str(r, "title") ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var awaiting = Rows("tblticketstatuses").Where(r => Long(r, "showawaiting") == 1).Select(r => Str(r, "title") ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var tickets = Rows("tbltickets").ToList();
        var pending = 0;
        foreach (var r in tickets)
        {
            var id = Long(r, "id");
            var userId = Long(r, "userid");
            long? account = accountCurrency.ContainsKey(userId) ? userId : null;
            if (userId > 0 && account is null)
            {
                Count("support_control", "tickets_cuenta_inexistente");
            }

            var person = account is { } a ? people.GetValueOrDefault(a) : default;
            var email = Blank(Str(r, "email")) ?? Blank(person.Email) ?? string.Empty;
            var name = Blank(Str(r, "name")) ?? Blank(person.Name) ?? Blank(email) ?? $"Ticket {id}";
            var number = Blank(Str(r, "tid")) is { Length: <= 32 } tid && numbers.Add(tid) ? tid : $"I{id}";
            if (number == $"I{id}")
            {
                numbers.Add(number);
                Incidents.Add($"Ticket {id}: sin número público o repetido, queda como {number}.");
            }

            var source = Str(r, "status") ?? string.Empty;
            var status = source switch
            {
                "Open" or "Customer-Reply" => TicketStatus.Open,
                "Answered" => TicketStatus.AwaitingCustomer,
                "In Progress" => TicketStatus.InProgress,
                "On Hold" => TicketStatus.OnHold,
                "Closed" => TicketStatus.Closed,
                _ when closedStatuses.Contains(source) => TicketStatus.Closed,
                _ when awaiting.Contains(source) => TicketStatus.Open,
                _ => TicketStatus.InProgress,
            };
            var created = Instant(DateTimeValue(r, "date")) ?? now;
            var last = Instant(DateTimeValue(r, "lastreply")) is { } reply && reply >= created ? reply : created;
            var ticket = new Ticket
            {
                Number = number,
                AccountId = account,
                RequesterName = Limit(name, 200),
                RequesterEmail = Limit(email, 320),
                Cc = [.. (Str(r, "cc") ?? string.Empty).Split(',', ';').Select(c => c.Trim().ToLowerInvariant())
                    .Where(c => c.Length is > 0 and <= 320 && MailboxAddress.TryParse(c, out _) && c != email.ToLowerInvariant()).Distinct().Take(10)],
                DepartmentId = await DepartmentFor(Long(r, "did"), ct).ConfigureAwait(false),
                ServiceId = Str(r, "service") is ['S', .. var digits] && long.TryParse(digits, CultureInfo.InvariantCulture, out var service) && services.Contains(service) ? service : null,
                Subject = Limit(Blank(Str(r, "title")) ?? "(sin asunto)", TicketService.MaxSubject),
                Status = status,
                Priority = Str(r, "urgency") switch { "Low" => TicketPriority.Low, "High" => TicketPriority.High, _ => TicketPriority.Normal },
                AssignedStaffId = StaffOf(Long(r, "flag")),
                ReplyToken = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz234567", 20),
                Imported = true,
                CreatedAt = created,
                LastMessageAt = last,
                UpdatedAt = last,
                ClosedAt = status == TicketStatus.Closed ? last : null,
            };
            ticket.SetImportedId(id);
            db.Add(ticket);
            ticketTarget[id] = id;
            Count("support_rows", "tbltickets");
            Count("tickets_by_status", source);
            if (Str(r, "service") is ['D', ..])
            {
                Count("support_control", "tickets_de_dominio");
            }

            if (Long(r, "flag") > 0 && ticket.AssignedStaffId is null)
            {
                Count("support_control", "asignados_sin_usuario");
            }

            if (++pending >= 1000)
            {
                await Flush(ct).ConfigureAwait(false);
                pending = 0;
            }
        }

        await Flush(ct).ConfigureAwait(false);

        // Los unidos apuntan al último de la cadena: así nunca hay que seguir más de un paso, como en una unión nueva.
        var merged = tickets.Where(r => Long(r, "merged_ticket_id") is > 0 and var target && target != Long(r, "id")).ToDictionary(r => Long(r, "id"), r => Long(r, "merged_ticket_id"));
        foreach (var (id, first) in merged)
        {
            var target = first;
            for (var step = 0; step < 20 && merged.TryGetValue(target, out var next); step++)
            {
                target = next;
            }

            if (!ticketTarget.ContainsKey(target) || target == id)
            {
                Count("support_control", "unidos_sin_destino");
                continue;
            }

            ticketTarget[id] = target;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE support.tickets SET merged_into_id = {target}, status = 'Closed', closed_at = COALESCE(closed_at, last_message_at) WHERE id = {id}", ct).ConfigureAwait(false);
            Count("support_control", "tickets_unidos");
        }
    }

    /// <summary>
    /// El departamento del ticket, o uno oculto de reemplazo si el origen ya no lo tiene, con el mismo ID o, sin ID, con
    /// el siguiente al mayor: la secuencia todavía no se movió y un ID generado chocaría con los importados.
    /// </summary>
    private async Task<long> DepartmentFor(long id, CancellationToken ct)
    {
        if (departments.Contains(id))
        {
            return id;
        }

        if (replacements.TryGetValue(id, out var known))
        {
            return known;
        }

        var replacement = new Department { Name = $"Departamento {id} (importado)", Hidden = true, CreatedAt = now };
        replacement.SetImportedId(id > 0 ? id : departments.Concat(replacements.Values).DefaultIfEmpty(0).Max() + 1);
        db.Add(replacement);
        await Flush(ct).ConfigureAwait(false);
        replacements[id] = replacement.Id;
        Incidents.Add($"Departamento {id}: lo usan tickets pero el origen ya no lo tiene. Queda uno oculto de reemplazo.");
        return replacement.Id;
    }

    /// <summary>
    /// El primer mensaje de cada ticket (está en el ticket del origen), las respuestas y las notas internas. Los de un
    /// ticket unido van al que lo recibió, con la marca de la unión, igual que en una unión nueva.
    /// </summary>
    private async Task SupportMessages(CancellationToken ct)
    {
        var withFiles = new List<(TicketMessage Message, (string Stored, string Name)[] Files)>();
        var subjects = new Dictionary<long, (string Number, string Subject, DateTimeOffset Last)>();
        var pending = 0;

        async Task Add(long sourceTicket, MessageAuthor author, string? name, string? email, string? body, DateTimeOffset? at, string? files, bool isInternal)
        {
            var message = new TicketMessage
            {
                TicketId = ticketTarget[sourceTicket],
                Author = author,
                StaffId = author == MessageAuthor.Staff && Blank(name) is { } signed && staffByName.TryGetValue(signed, out var staffId) ? staffId : null,
                AuthorName = Limit(Blank(name) ?? (author == MessageAuthor.Staff ? "Staff" : "Cliente"), 200),
                AuthorEmail = Blank(email) is { } address ? Limit(address, 320) : null,
                Body = Blank(body) is { } text ? (text.Length > TicketService.MaxBody ? text[..TicketService.MaxBody] : text) : "(sin texto)",
                Internal = isInternal,
                Source = MessageSource.Import,
                CreatedAt = at ?? now,
            };
            db.Add(message);
            if (AttachmentNames(files) is { Length: > 0 } names)
            {
                withFiles.Add((message, names));
            }

            if (++pending >= 1000)
            {
                await SaveMessages(withFiles, ct).ConfigureAwait(false);
                pending = 0;
            }
        }

        foreach (var r in Rows("tbltickets"))
        {
            var id = Long(r, "id");
            var staff = Blank(Str(r, "admin"));
            await Add(id, staff is null ? MessageAuthor.Customer : MessageAuthor.Staff, staff ?? Str(r, "name"), staff is null ? Str(r, "email") : null, Str(r, "message"),
                Instant(DateTimeValue(r, "date")), Str(r, "attachment"), false).ConfigureAwait(false);
            if (ticketTarget[id] != id)
            {
                subjects[id] = (Blank(Str(r, "tid")) ?? id.ToString(CultureInfo.InvariantCulture), Blank(Str(r, "title")) ?? "(sin asunto)", Instant(DateTimeValue(r, "lastreply")) ?? now);
            }
        }

        foreach (var r in Rows("tblticketreplies"))
        {
            var ticket = Long(r, "tid");
            Count("support_rows", "tblticketreplies");
            if (!ticketTarget.ContainsKey(ticket))
            {
                Count("support_control", "respuestas_sin_ticket");
                continue;
            }

            var staff = Blank(Str(r, "admin"));
            await Add(ticket, staff is null ? MessageAuthor.Customer : MessageAuthor.Staff, staff ?? Str(r, "name"), staff is null ? Str(r, "email") : null, Str(r, "message"),
                Instant(DateTimeValue(r, "date")), Str(r, "attachment"), false).ConfigureAwait(false);
            if (Long(r, "rating") > 0)
            {
                Count("support_control", "respuestas_calificadas");
            }
        }

        foreach (var r in Rows("tblticketnotes"))
        {
            var ticket = Long(r, "ticketid");
            Count("support_rows", "tblticketnotes");
            if (!ticketTarget.ContainsKey(ticket))
            {
                Count("support_control", "notas_sin_ticket");
                continue;
            }

            await Add(ticket, MessageAuthor.Staff, Str(r, "admin"), null, Str(r, "message"), Instant(DateTimeValue(r, "date")), Str(r, "attachments"), true).ConfigureAwait(false);
        }

        foreach (var (id, (number, subject, last)) in subjects)
        {
            db.Add(new TicketMessage
            {
                TicketId = ticketTarget[id],
                Author = MessageAuthor.System,
                AuthorName = "Sistema",
                Body = TicketService.MergedText(number, Limit(subject, TicketService.MaxSubject)),
                Source = MessageSource.Import,
                CreatedAt = last,
            });
        }

        await SaveMessages(withFiles, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Guarda los mensajes y después sus adjuntos, que necesitan el ID del mensaje. Con la carpeta de adjuntos del origen
    /// cada archivo que está entra con su contenido, de a uno para no juntar todo en memoria. Los demás quedan con su
    /// nombre y sin el archivo.
    /// </summary>
    private async Task SaveMessages(List<(TicketMessage Message, (string Stored, string Name)[] Files)> withFiles, CancellationToken ct)
    {
        await Flush(ct).ConfigureAwait(false);
        foreach (var (message, files) in withFiles)
        {
            foreach (var (stored, name) in files)
            {
                var content = await SupportFile(stored, ct).ConfigureAwait(false);
                var attachment = new TicketAttachment
                {
                    MessageId = message.Id,
                    FileName = TicketService.SafeName(name),
                    ContentType = MimeTypes.GetMimeType(name),
                    Size = content?.LongLength ?? 0,
                    Missing = content is null,
                    CreatedAt = message.CreatedAt,
                };
                db.Add(attachment);
                Count("support_control", content is null ? "adjuntos_sin_archivo" : "adjuntos_con_archivo");
                if (content is not null)
                {
                    await Flush(ct).ConfigureAwait(false);
                    db.Add(new AttachmentContent { AttachmentId = attachment.Id, Content = content });
                    await Flush(ct).ConfigureAwait(false);
                }
            }
        }

        withFiles.Clear();
        await Flush(ct).ConfigureAwait(false);
    }

    /// <summary>Tope de cada archivo que se trae de la carpeta de adjuntos: lo que pasa queda sin el archivo.</summary>
    public const long MaxSupportFile = 50L * 1024 * 1024;

    /// <summary>
    /// El contenido de un adjunto en la carpeta del origen, o null. El nombre guardado es solo un nombre: si trae carpetas
    /// o algo que no es de un archivo, no se busca, así un nombre armado no lee fuera de la carpeta.
    /// </summary>
    private async Task<byte[]?> SupportFile(string stored, CancellationToken ct)
    {
        if (SupportFiles is not { Length: > 0 } folder || stored != Path.GetFileName(stored) || stored is "." or ".." || stored.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var file = new FileInfo(Path.Combine(folder, stored));
        if (!file.Exists || file.Length == 0)
        {
            return null;
        }

        if (file.Length > MaxSupportFile)
        {
            Count("support_control", "adjuntos_demasiado_grandes");
            return null;
        }

        return await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Los adjuntos del origen, separados por barras: el nombre con que se guardó en su carpeta y el que se muestra, sin el
    /// prefijo numérico propio del origen.
    /// </summary>
    private static (string Stored, string Name)[] AttachmentNames(string? raw) =>
        [.. (raw ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => (Stored: f, Name: f.IndexOf('_', StringComparison.Ordinal) is > 0 and var cut && f[..cut].All(char.IsAsciiDigit) ? f[(cut + 1)..] : f))
            .Where(f => f.Name.Length > 0)];

    /// <summary>Respuestas predefinidas con su categoría, filtros del correo y reglas de escalamiento.</summary>
    private async Task SupportSettings(CancellationToken ct)
    {
        var categories = Rows("tblticketpredefinedcats").ToDictionary(r => Long(r, "id"), r => Blank(Str(r, "name")));
        foreach (var r in Rows("tblticketpredefinedreplies"))
        {
            db.Add(new SavedReply
            {
                Name = Limit(Blank(Str(r, "name")) ?? "Respuesta", 200),
                Category = categories.GetValueOrDefault(Long(r, "catid")) is { } category ? Limit(category, 200) : null,
                Body = Blank(Str(r, "reply")) ?? "(sin texto)",
                UpdatedAt = now,
            });
            Count("support_rows", "tblticketpredefinedreplies");
        }

        foreach (var r in Rows("tblticketspamfilters"))
        {
            var kind = Str(r, "type") switch { "subject" => MailFilterKind.Subject, "phrase" => MailFilterKind.Phrase, _ => MailFilterKind.Sender };
            if (Blank(Str(r, "content")) is { } value)
            {
                db.Add(new MailFilter { Kind = kind, Value = Limit(value, 500) });
            }

            Count("support_rows", "tblticketspamfilters");
        }

        foreach (var r in Rows("tblticketescalations"))
        {
            var rule = new EscalationRule
            {
                Name = Limit(Blank(Str(r, "name")) ?? "Regla importada", 200),
                DepartmentIds = [.. List(Str(r, "departments")).Select(d => long.TryParse(d, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(departments.Contains).Distinct()],
                Statuses = [.. List(Str(r, "statuses")).Select(TicketStatusOf).OfType<TicketStatus>().Distinct()],
                Priorities = [.. List(Str(r, "priorities")).Select(PriorityOf).OfType<TicketPriority>().Distinct()],
                AfterMinutes = (int)Math.Clamp(Long(r, "timeelapsed"), 1, 7 * 24 * 60),
                NewDepartmentId = Long(r, "newdepartment") is > 0 and var to && departments.Contains(to) ? to : null,
                NewPriority = PriorityOf(Str(r, "newpriority") ?? string.Empty),
                NewStatus = TicketStatusOf(Str(r, "newstatus") ?? string.Empty),
                AssignStaffId = StaffOf(Long(r, "flagto")),
                Notify = Blank(Str(r, "notify")) is not null,
                Reply = Blank(Str(r, "addreply")),
            };
            db.Add(rule);
            Count("support_rows", "tblticketescalations");
            if (Long(r, "flagto") > 0 && rule.AssignStaffId is null)
            {
                Incidents.Add($"Regla de escalamiento {Long(r, "id")}: asignaba a una persona del staff que no tiene usuario con su correo. Elegirla de nuevo en la regla.");
            }

            if (rule.Reply is not null)
            {
                Incidents.Add($"Regla de escalamiento {Long(r, "id")}: le responde al cliente con su texto. Revisarlo antes del corte.");
            }
        }

        await Flush(ct).ConfigureAwait(false);
    }

    private static TicketStatus? TicketStatusOf(string name) => name switch
    {
        "Open" or "Customer-Reply" => TicketStatus.Open,
        "Answered" => TicketStatus.AwaitingCustomer,
        "In Progress" => TicketStatus.InProgress,
        "On Hold" => TicketStatus.OnHold,
        "Closed" => TicketStatus.Closed,
        _ => null,
    };

    private static TicketPriority? PriorityOf(string name) => name switch
    {
        "Low" => TicketPriority.Low,
        "Medium" => TicketPriority.Normal,
        "High" => TicketPriority.High,
        _ => null,
    };

    /// <summary>Las encuestas respondidas. Una por ticket: si el origen tiene más, queda la última.</summary>
    private async Task Feedback(CancellationToken ct)
    {
        foreach (var g in Rows("tblticketfeedback").GroupBy(r => Long(r, "ticketid")))
        {
            foreach (var _ in g)
            {
                Count("support_rows", "tblticketfeedback");
            }

            if (!ticketTarget.ContainsKey(g.Key))
            {
                Count("support_control", "encuestas_sin_ticket");
                continue;
            }

            if (g.Count() > 1)
            {
                Count("support_control", "encuestas_repetidas");
            }

            var r = g.OrderBy(x => DateTimeValue(x, "datetime")).ThenBy(x => Long(x, "id")).Last();
            db.Add(new TicketFeedback
            {
                TicketId = g.Key,
                Rating = (int)Math.Clamp(Long(r, "rating"), 0, 10),
                Comment = Blank(Str(r, "comments")) is { } comment ? Limit(comment, TicketFeedback.MaxComment) : null,
                StaffId = StaffOf(Long(r, "adminid")),
                Imported = true,
                CreatedAt = Instant(DateTimeValue(r, "datetime")) ?? now,
            });
        }

        await Flush(ct).ConfigureAwait(false);
    }

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];
}
