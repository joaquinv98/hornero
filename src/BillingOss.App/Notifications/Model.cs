using BillingOss.Domain.Accounts;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Notifications;

/// <summary>Correo saliente y SMTP, pensado para un relay propio, con STARTTLS y una casilla del sistema.</summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// Si los avisos salen de verdad. Apagado (por defecto) cada aviso se arma y queda registrado, pero no se manda:
    /// así corren staging y el mes en paralelo.
    /// </summary>
    public bool Send { get; set; }

    /// <summary>Con <see cref="Send"/>, si tiene casillas, solo ellas reciben. El resto queda registrado sin salir.</summary>
    public IList<string> OnlyTo { get; set; } = [];

    /// <summary>
    /// Traba de producción del correo, apagada por defecto como la fiscal. Mientras lo esté, solo reciben las casillas
    /// de <see cref="OnlyTo"/>: ningún cliente recibe un aviso desde staging, el mes en paralelo o una base restaurada
    /// de producción. Se prende recién en el corte.
    /// </summary>
    public bool ProductionEnabled { get; set; }

    /// <summary>Si un aviso a esta casilla sale. Se mira al encolar y otra vez al mandar.</summary>
    public bool Delivers(string recipient) =>
        Send && (OnlyTo.Contains(recipient.Trim(), StringComparer.OrdinalIgnoreCase) || (ProductionEnabled && OnlyTo.Count == 0));

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>STARTTLS obligatorio por defecto: si el servidor no lo ofrece, no se manda.</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public string? User { get; set; }

    /// <summary>Se carga del vault en tiempo de ejecución, por variable de entorno. Nunca en un archivo.</summary>
    public string? Password { get; set; }

    /// <summary>Remitente. Con un relay autenticado tiene que ser la misma casilla que <see cref="User"/>.</summary>
    public string From { get; set; } = string.Empty;

    public string FromName { get; set; } = "Hornero";

    /// <summary>
    /// Días antes del vencimiento en que sale un recordatorio, separados por coma, por ejemplo "7,3,1". El 0 es el día
    /// del vencimiento. Vacío, no sale ninguno.
    /// </summary>
    public string? ReminderDaysBefore { get; set; } = "1";

    /// <summary>Días después del vencimiento en que sale un aviso de factura vencida, separados por coma, por ejemplo "3,10,20". Vacío, no sale ninguno.</summary>
    public string? OverdueDaysAfter { get; set; } = "3";

    /// <summary>Una lista de días de los ajustes: de 0 a 365, sin repetidos y en orden.</summary>
    public static IReadOnlyList<int> Days(string? list)
    {
        var days = new SortedSet<int>();
        foreach (var part in (list ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var day) || day > 365)
            {
                throw new InvalidOperationException($"'{list}' no es una lista de días separados por coma, de 0 a 365.");
            }

            days.Add(day);
        }

        return [.. days];
    }

    /// <summary>
    /// Cuánto espera el aviso de factura emitida al comprobante fiscal, para salir con el CAE. Pasado ese plazo sale
    /// sin él, y el comprobante llega después en un aviso aparte.
    /// </summary>
    public TimeSpan FiscalWait { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Plantilla editada por el proveedor. Sin fila, vale la que viene de fábrica en Templates/.</summary>
public sealed class Template
{
    public long Id { get; private set; }

    public string Key { get; set; } = string.Empty;

    public string Culture { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum EmailStatus
{
    /// <summary>En la cola, con su trabajo de envío.</summary>
    Pending,
    Sent,

    /// <summary>Registrado sin salir: envío apagado, traba de producción o casilla fuera de la lista permitida.</summary>
    Suppressed,

    /// <summary>El servidor lo rechazó para siempre, o se agotaron los reintentos.</summary>
    Failed,
}

/// <summary>
/// Correo saliente ya armado (sección 9 del modelo de dominio). La clave de deduplicación impide armar dos veces el
/// mismo aviso, y el Message-ID queda fijo para todos los reintentos.
/// </summary>
public sealed class OutboundEmail
{
    public long Id { get; private set; }

    public string Template { get; private set; } = string.Empty;

    public string Culture { get; private set; } = string.Empty;

    public long? AccountId { get; private set; }

    public string Recipient { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    public string Html { get; private set; } = string.Empty;

    public string MessageId { get; private set; } = string.Empty;

    public string DedupKey { get; private set; } = string.Empty;

    /// <summary>Encabezados de más, de la lista cerrada de <see cref="Notifier.ExtraHeaders"/>, como objeto JSON.</summary>
    public string? Headers { get; private set; }

    public EmailStatus Status { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; set; }
}

internal sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> b)
    {
        b.ToTable("templates", "notifications");
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Culture).HasMaxLength(10);
        b.Property(x => x.Subject).HasMaxLength(500);
        b.HasIndex(x => new { x.Key, x.Culture }).IsUnique();
    }
}

internal sealed class OutboundEmailConfiguration : IEntityTypeConfiguration<OutboundEmail>
{
    public void Configure(EntityTypeBuilder<OutboundEmail> b)
    {
        b.ToTable("outbound_emails", "notifications");
        b.Property(x => x.Template).HasMaxLength(64);
        b.Property(x => x.Culture).HasMaxLength(10);
        b.Property(x => x.Recipient).HasMaxLength(320);
        b.Property(x => x.Subject).HasMaxLength(500);
        b.Property(x => x.MessageId).HasMaxLength(200);
        b.Property(x => x.DedupKey).HasMaxLength(200);
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.Property(x => x.Headers).HasColumnType("jsonb");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasIndex(x => x.DedupKey).IsUnique();
    }
}
