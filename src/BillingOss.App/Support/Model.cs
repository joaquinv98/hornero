using BillingOss.App.Identity;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Support;

/// <summary>Ajustes de soporte (sección Support). Ver docs/soporte.md.</summary>
public sealed class SupportOptions
{
    /// <summary>Tope de cada adjunto, del portal, del panel o de un correo.</summary>
    public long MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Adjuntos por mensaje. Los que sobran de un correo se omiten y el mensaje lo dice.</summary>
    public int MaxAttachments { get; set; } = 5;

    /// <summary>
    /// Extensiones permitidas, separadas por comas. Lo demás se rechaza o, si viene por correo, se omite. Es un texto y
    /// no una lista para que la configuración la reemplace en vez de sumarle elementos.
    /// </summary>
    public string AllowedExtensions { get; set; } = ".pdf,.png,.jpg,.jpeg,.gif,.webp,.txt,.log,.csv";

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Extensions =>
        [.. AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant())];

    /// <summary>Un ticket que espera al cliente se cierra solo pasado este tiempo sin respuesta. Vacío, nunca.</summary>
    public TimeSpan? AutoCloseAfter { get; set; }

    /// <summary>Correos más grandes no se descargan: quedan en el registro de la casilla como rechazados.</summary>
    public long MaxEmailBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Correos por remitente y por hora que se aceptan. Corta los bucles con respuestas automáticas.</summary>
    public int MaxEmailsPerSenderPerHour { get; set; } = 15;

    /// <summary>Las otras casillas de un correo (Para y CC) pasan a las copias del ticket, al abrirlo y al responder.</summary>
    public bool EmailCc { get; set; } = true;
}

/// <summary>Quién tiene la pelota. Abierto y en curso esperan al staff, esperando al cliente espera al cliente.</summary>
public enum TicketStatus
{
    /// <summary>Nuevo o con respuesta del cliente: espera al staff.</summary>
    Open,

    /// <summary>El staff lo está trabajando.</summary>
    InProgress,

    /// <summary>El staff respondió y espera al cliente. Es el único que se cierra solo.</summary>
    AwaitingCustomer,

    /// <summary>Depende de un tercero: no cuenta como pendiente del staff.</summary>
    OnHold,
    Closed,
}

public enum TicketPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum MessageAuthor
{
    Customer,
    Staff,

    /// <summary>Marcas del propio sistema: cierre automático, unión de tickets importados.</summary>
    System,
}

public enum MessageSource
{
    Portal,
    Email,
    Panel,
    Api,
    Import,
}

/// <summary>Cifrado de la conexión IMAP. Sin cifrar solo sirve para pruebas en la misma máquina.</summary>
public enum MailSecurity
{
    SslOnConnect,
    StartTls,
    None,
}

public enum InboundOutcome
{
    /// <summary>Abrió un ticket.</summary>
    Opened,

    /// <summary>Se sumó como respuesta del cliente a un ticket.</summary>
    Replied,

    /// <summary>Respuesta automática, rebote o correo propio: se descarta sin avisar a nadie.</summary>
    Ignored,

    /// <summary>Filtro, departamento solo para clientes, tope por remitente o tamaño: queda para el staff.</summary>
    Rejected,
}

public enum MailFilterKind
{
    Sender,
    Subject,
    Phrase,
}

/// <summary>
/// Departamento de soporte (sección 9 del modelo de dominio), con su casilla de correo. La contraseña de IMAP va
/// cifrada por campo y atada al servidor (D-09): cambiar el servidor obliga a cargarla de nuevo.
/// </summary>
public sealed class Department
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>La casilla del departamento: va en Reply-To de los avisos, así las respuestas vuelven por IMAP.</summary>
    public string? Email { get; set; }

    /// <summary>No se ofrece en el portal. El staff y el correo igual lo usan.</summary>
    public bool Hidden { get; set; }

    /// <summary>Por correo solo abren tickets los remitentes que son de una cuenta.</summary>
    public bool ClientsOnly { get; set; }

    /// <summary>Confirmación automática al cliente cuando abre un ticket.</summary>
    public bool AutoReply { get; set; } = true;

    /// <summary>Casilla del equipo que recibe el aviso de un mensaje del cliente en un ticket sin asignar.</summary>
    public string? StaffAlertEmail { get; set; }

    /// <summary>Al cerrarse un ticket se le pide al cliente que califique la atención, una sola vez por ticket.</summary>
    public bool RequestFeedback { get; set; }

    public int SortOrder { get; set; }

    public bool MailEnabled { get; set; }

    public string? ImapHost { get; set; }

    public int ImapPort { get; set; } = 993;

    public MailSecurity ImapSecurity { get; set; }

    public string? ImapUser { get; set; }

    /// <summary>Cifrada con <see cref="Platform.SecretBox"/>, atada a la fila y a <see cref="ImapHost"/>.</summary>
    public byte[]? ImapPassword { get; set; }

    /// <summary>Hasta dónde se leyó la casilla. Si el servidor cambia el UIDVALIDITY se relee todo sin duplicar.</summary>
    public long ImapUidValidity { get; set; }

    public long ImapLastUid { get; set; }

    public DateTimeOffset? MailCheckedAt { get; set; }

    /// <summary>La última falla de la casilla, para el staff. Sin secretos.</summary>
    public string? MailError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Para conservar el ID de origen en una importación (D-17).</summary>
    public void SetImportedId(long id) => Id = id;
}

/// <summary>Ticket (sección 9 del modelo de dominio). El número público es distinto del ID interno.</summary>
public sealed class Ticket
{
    public long Id { get; private set; }

    public string Number { get; set; } = string.Empty;

    /// <summary>Nula para quien escribió por correo sin ser de una cuenta.</summary>
    public long? AccountId { get; set; }

    /// <summary>El usuario del portal que lo abrió, si lo abrió desde el portal.</summary>
    public long? RequesterUserId { get; set; }

    public string RequesterName { get; set; } = string.Empty;

    /// <summary>A quién se le contesta. Para un ticket del portal, el correo del usuario.</summary>
    public string RequesterEmail { get; set; } = string.Empty;

    /// <summary>Otras casillas que reciben las respuestas del staff.</summary>
    public string[] Cc { get; set; } = [];

    public long DepartmentId { get; set; }

    /// <summary>El servicio por el que se consulta, si el cliente lo eligió.</summary>
    public long? ServiceId { get; set; }

    public string Subject { get; set; } = string.Empty;

    public TicketStatus Status { get; set; }

    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    public long? AssignedStaffId { get; set; }

    /// <summary>
    /// Ficha secreta del hilo de correo: va en In-Reply-To y References de cada aviso. Una respuesta que la trae
    /// demuestra que quien escribe recibió un correo del ticket.
    /// </summary>
    public string ReplyToken { get; set; } = string.Empty;

    /// <summary>Idempotency-Key de la API: abrir dos veces con la misma no duplica.</summary>
    public string? ClientReference { get; set; }

    /// <summary>
    /// El ticket al que se unió. Sus mensajes pasaron a ese, queda cerrado, y el correo o el enlace que lo nombre lleva
    /// al que lo recibió.
    /// </summary>
    public long? MergedIntoId { get; set; }

    /// <summary>Ficha de la encuesta de satisfacción: va en el enlace del correo y sirve sin iniciar sesión.</summary>
    public string? FeedbackToken { get; set; }

    public bool Imported { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>El último mensaje que ve el cliente. Las notas internas no lo mueven.</summary>
    public DateTimeOffset LastMessageAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Lo que el staff tiene que mirar: abierto o en curso.</summary>
    public bool WaitsForStaff => Status is TicketStatus.Open or TicketStatus.InProgress;

    /// <summary>Escribió el cliente: el ticket vuelve a esperar al staff, aunque estuviera cerrado.</summary>
    public void CustomerWrote(DateTimeOffset now)
    {
        Move(TicketStatus.Open, now);
        LastMessageAt = now;
    }

    /// <summary>Respondió el staff: queda en el estado que eligió, por defecto esperando al cliente.</summary>
    public void StaffWrote(TicketStatus next, DateTimeOffset now)
    {
        Move(next, now);
        LastMessageAt = now;
    }

    public void Move(TicketStatus next, DateTimeOffset now)
    {
        if (next == TicketStatus.Closed && Status != TicketStatus.Closed)
        {
            ClosedAt = now;
        }
        else if (next != TicketStatus.Closed)
        {
            ClosedAt = null;
        }

        Status = next;
        UpdatedAt = now;
    }

    /// <summary>Para conservar el ID de origen en una importación (D-17).</summary>
    public void SetImportedId(long id) => Id = id;
}

/// <summary>Mensaje de un ticket. El cuerpo es texto: el HTML de un correo se pasa a texto al entrar.</summary>
public sealed class TicketMessage
{
    public long Id { get; private set; }

    public long TicketId { get; set; }

    public MessageAuthor Author { get; set; }

    public long? UserId { get; set; }

    public long? StaffId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string? AuthorEmail { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>Nota del staff: el cliente nunca la ve ni recibe aviso.</summary>
    public bool Internal { get; set; }

    public MessageSource Source { get; set; }

    /// <summary>Message-ID del correo que lo trajo, para enlazar las respuestas que lo citan.</summary>
    public string? EmailMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Adjunto de un mensaje. El contenido va en una tabla aparte y nunca se carga con la lista.</summary>
public sealed class TicketAttachment
{
    public long Id { get; private set; }

    public long MessageId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long Size { get; set; }

    /// <summary>Importado sin el archivo: se lista con su nombre y no se puede bajar.</summary>
    public bool Missing { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>El contenido de un adjunto, en la base: así lo ven todos los nodos sin un volumen compartido.</summary>
public sealed class AttachmentContent
{
    public long AttachmentId { get; set; }

    public byte[] Content { get; set; } = [];
}

/// <summary>Respuesta predefinida del staff.</summary>
public sealed class SavedReply
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>La respuesta del cliente a la encuesta de satisfacción de un ticket: una por ticket, del 0 al 10.</summary>
public sealed class TicketFeedback
{
    public const int MaxComment = 2000;

    public long Id { get; private set; }

    public long TicketId { get; set; }

    public int Rating { get; set; }

    public string? Comment { get; set; }

    /// <summary>Quién atendía el ticket al cerrarse, para el reporte por persona.</summary>
    public long? StaffId { get; set; }

    public bool Imported { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Regla de escalamiento: un ticket de esos departamentos, estados y prioridades que pasa tantos minutos sin un mensaje
/// nuevo cambia de departamento, prioridad, estado o asignado, avisa al staff, deja una nota interna y le responde al
/// cliente. Se aplica una vez por cada espera: un mensaje nuevo la habilita otra vez.
/// </summary>
public sealed class EscalationRule
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Vacío, todos los departamentos.</summary>
    public long[] DepartmentIds { get; set; } = [];

    /// <summary>Vacío, los que esperan al staff (abierto y en curso).</summary>
    public TicketStatus[] Statuses { get; set; } = [];

    /// <summary>Vacío, todas las prioridades.</summary>
    public TicketPriority[] Priorities { get; set; } = [];

    /// <summary>Minutos desde el último mensaje que ve el cliente.</summary>
    public int AfterMinutes { get; set; }

    public long? NewDepartmentId { get; set; }

    public TicketPriority? NewPriority { get; set; }

    public TicketStatus? NewStatus { get; set; }

    public long? AssignStaffId { get; set; }

    /// <summary>Avisa al asignado o, sin asignado, a la casilla del equipo del departamento.</summary>
    public bool Notify { get; set; }

    /// <summary>Personas del staff que reciben el aviso, además del asignado o la casilla del equipo.</summary>
    public long[] NotifyStaffIds { get; set; } = [];

    /// <summary>Nota interna que se suma al ticket.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Respuesta al cliente: un mensaje del departamento que el cliente ve y recibe por correo, como una respuesta. No
    /// cuenta como mensaje nuevo, así la espera sigue y la regla no se vuelve a aplicar por su propia respuesta.
    /// </summary>
    public string? Reply { get; set; }

    /// <summary>Los estados que mira: los elegidos o, sin elegir, los que esperan al staff.</summary>
    public TicketStatus[] EffectiveStatuses => Statuses.Length > 0 ? Statuses : [TicketStatus.Open, TicketStatus.InProgress];
}

/// <summary>Cada vez que una regla escaló un ticket. La clave única impide aplicarla dos veces a la misma espera, también entre nodos.</summary>
public sealed class TicketEscalation
{
    public long Id { get; private set; }

    public long RuleId { get; set; }

    public long TicketId { get; set; }

    /// <summary>El último mensaje del ticket cuando se aplicó.</summary>
    public DateTimeOffset Since { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>Filtro del correo entrante: remitente (casilla o dominio), asunto o frase del cuerpo.</summary>
public sealed class MailFilter
{
    public long Id { get; private set; }

    public MailFilterKind Kind { get; set; }

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Cada correo leído de una casilla, con lo que se hizo. La clave única es el Message-ID: un correo nunca se procesa
/// dos veces, ni entre nodos, ni si el servidor renumera la casilla, ni si llegó a dos departamentos.
/// </summary>
public sealed class InboundEmail
{
    public long Id { get; private set; }

    public long DepartmentId { get; set; }

    public string MessageKey { get; set; } = string.Empty;

    public string Sender { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public InboundOutcome Outcome { get; set; }

    public string? Reason { get; set; }

    public long? TicketId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("departments", "support");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.StaffAlertEmail).HasMaxLength(320);
        b.Property(x => x.ImapHost).HasMaxLength(253);
        b.Property(x => x.ImapUser).HasMaxLength(320);
        b.Property(x => x.MailError).HasMaxLength(1000);
    }
}

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.ToTable("tickets", "support");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Number).HasMaxLength(32);
        b.Property(x => x.RequesterName).HasMaxLength(200);
        b.Property(x => x.RequesterEmail).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(300);
        b.Property(x => x.ReplyToken).HasMaxLength(32);
        b.Property(x => x.ClientReference).HasMaxLength(200);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<PortalUser>().WithMany().HasForeignKey(x => x.RequesterUserId);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne<StaffUser>().WithMany().HasForeignKey(x => x.AssignedStaffId);
        b.HasOne<Ticket>().WithMany().HasForeignKey(x => x.MergedIntoId);
        b.Property(x => x.FeedbackToken).HasMaxLength(32);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.ReplyToken).IsUnique();
        b.HasIndex(x => x.ClientReference).IsUnique().HasFilter("client_reference IS NOT NULL");
        b.HasIndex(x => x.FeedbackToken).IsUnique().HasFilter("feedback_token IS NOT NULL");
        b.HasIndex(x => x.MergedIntoId).HasFilter("merged_into_id IS NOT NULL");
        b.HasIndex(x => new { x.Status, x.LastMessageAt });
        b.HasIndex(x => x.AccountId);
        b.HasIndex(x => x.AssignedStaffId);
        b.Ignore(x => x.WaitsForStaff);
    }
}

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> b)
    {
        b.ToTable("messages", "support");
        b.Property(x => x.AuthorName).HasMaxLength(200);
        b.Property(x => x.AuthorEmail).HasMaxLength(320);
        b.Property(x => x.EmailMessageId).HasMaxLength(998);
        b.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId);
        b.HasOne<PortalUser>().WithMany().HasForeignKey(x => x.UserId);
        b.HasOne<StaffUser>().WithMany().HasForeignKey(x => x.StaffId);
        b.HasIndex(x => new { x.TicketId, x.Id });
        b.HasIndex(x => x.EmailMessageId).HasFilter("email_message_id IS NOT NULL");
    }
}

internal sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> b)
    {
        b.ToTable("attachments", "support");
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.HasOne<TicketMessage>().WithMany().HasForeignKey(x => x.MessageId);
        b.HasIndex(x => x.MessageId);
    }
}

internal sealed class AttachmentContentConfiguration : IEntityTypeConfiguration<AttachmentContent>
{
    public void Configure(EntityTypeBuilder<AttachmentContent> b)
    {
        b.ToTable("attachment_contents", "support");
        b.HasKey(x => x.AttachmentId);
        b.HasOne<TicketAttachment>().WithOne().HasForeignKey<AttachmentContent>(x => x.AttachmentId);
    }
}

internal sealed class SavedReplyConfiguration : IEntityTypeConfiguration<SavedReply>
{
    public void Configure(EntityTypeBuilder<SavedReply> b)
    {
        b.ToTable("saved_replies", "support");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Category).HasMaxLength(200);
    }
}

internal sealed class TicketFeedbackConfiguration : IEntityTypeConfiguration<TicketFeedback>
{
    public void Configure(EntityTypeBuilder<TicketFeedback> b)
    {
        b.ToTable("feedback", "support", t => t.HasCheckConstraint("ck_feedback_rating", "rating BETWEEN 0 AND 10"));
        b.Property(x => x.Comment).HasMaxLength(TicketFeedback.MaxComment);
        b.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId);
        b.HasOne<StaffUser>().WithMany().HasForeignKey(x => x.StaffId);
        b.HasIndex(x => x.TicketId).IsUnique();
        b.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class EscalationRuleConfiguration : IEntityTypeConfiguration<EscalationRule>
{
    public void Configure(EntityTypeBuilder<EscalationRule> b)
    {
        b.ToTable("escalation_rules", "support", t => t.HasCheckConstraint("ck_escalation_rules_after_minutes", "after_minutes > 0"));
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Note).HasMaxLength(TicketService.MaxBody);
        b.Property(x => x.Reply).HasMaxLength(TicketService.MaxBody);
        b.PrimitiveCollection(x => x.Statuses).ElementType(e => e.HasConversion<string>());
        b.PrimitiveCollection(x => x.Priorities).ElementType(e => e.HasConversion<string>());
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.NewDepartmentId);
        b.HasOne<StaffUser>().WithMany().HasForeignKey(x => x.AssignStaffId);
        b.Ignore(x => x.EffectiveStatuses);
    }
}

internal sealed class TicketEscalationConfiguration : IEntityTypeConfiguration<TicketEscalation>
{
    public void Configure(EntityTypeBuilder<TicketEscalation> b)
    {
        b.ToTable("escalations", "support");
        b.HasOne<EscalationRule>().WithMany().HasForeignKey(x => x.RuleId);
        b.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId);
        b.HasIndex(x => new { x.RuleId, x.TicketId, x.Since }).IsUnique();
        b.HasIndex(x => x.TicketId);
    }
}

internal sealed class MailFilterConfiguration : IEntityTypeConfiguration<MailFilter>
{
    public void Configure(EntityTypeBuilder<MailFilter> b)
    {
        b.ToTable("mail_filters", "support");
        b.Property(x => x.Value).HasMaxLength(500);
    }
}

internal sealed class InboundEmailConfiguration : IEntityTypeConfiguration<InboundEmail>
{
    public void Configure(EntityTypeBuilder<InboundEmail> b)
    {
        b.ToTable("inbound_emails", "support");
        b.Property(x => x.MessageKey).HasMaxLength(998);
        b.Property(x => x.Sender).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(300);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
        b.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId);
        b.HasIndex(x => x.MessageKey).IsUnique();
        b.HasIndex(x => new { x.Sender, x.ReceivedAt });
    }
}
