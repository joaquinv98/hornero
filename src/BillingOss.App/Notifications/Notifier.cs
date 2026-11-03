using System.Globalization;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Shared;
using BillingOss.App.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MimeKit.Utils;

namespace BillingOss.App.Notifications;

public static class NotificationServices
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddOptions<NotificationOptions>();
        services.AddScoped<TemplateRenderer>();
        services.AddScoped<Notifier>();
        services.AddScoped<IJobHandler, NotifyEventHandler>();
        services.AddScoped<IJobHandler, InvoiceRemindersHandler>();
        services.AddScoped<IJobHandler, SendEmailHandler>();
        services.TryAddScoped<IAccountMail, NotifierAccountMail>();
        services.TryAddScoped<IDomainMail, NotifierDomainMail>();
        return services;
    }
}

/// <summary>
/// Arma los avisos y los deja en la cola de correo saliente (sección 9 del modelo de dominio). El envío es otro
/// trabajo, fuera de cualquier transacción. Los flujos que no salen de un evento del núcleo (recuperar la
/// contraseña, constancias de baja y de arrepentimiento) llaman a <see cref="Queue"/> en su propia transacción.
/// </summary>
public sealed class Notifier(BillingDbContext db, JobQueue jobs, TemplateRenderer renderer, BillingCore core, IOptions<NotificationOptions> options, IOptions<PaymentsOptions> payments, TimeProvider time)
{
    /// <summary>Intentos de envío. Con la espera creciente de la cola son casi un día de servidor de correo caído.</summary>
    public const int SendAttempts = 30;

    /// <summary>
    /// Los únicos encabezados de más que acepta un aviso: el hilo y la casilla de respuesta de un ticket, y la marca de
    /// respuesta automática (RFC 3834) para que el contestador del cliente no conteste.
    /// </summary>
    public static IReadOnlySet<string> ExtraHeaders { get; } = new HashSet<string>(StringComparer.Ordinal) { "Reply-To", "In-Reply-To", "References", "Auto-Submitted" };

    public NotificationOptions Options { get; } = options.Value;

    /// <summary>
    /// Arma el aviso y lo deja en la cola, en la transacción del que llama o en una propia. Con la misma clave de
    /// deduplicación no se arma dos veces. Devuelve false si ya estaba o si no hay a quién mandarlo.
    /// </summary>
    public async Task<bool> Queue(string template, string recipient, string? language, IReadOnlyDictionary<string, object?> model, string dedupKey, long? accountId = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return false;
        }

        if (headers?.FirstOrDefault(h => !ExtraHeaders.Contains(h.Key) || h.Value.AsSpan().IndexOfAny('\r', '\n') >= 0) is { Key: not null } bad)
        {
            throw new ArgumentException($"Encabezado no permitido en un aviso: {bad.Key}.", nameof(headers));
        }

        var culture = TemplateRenderer.CultureFor(language);
        var full = new Dictionary<string, object?>(model) { ["company"] = Options.FromName, ["portal_url"] = core.Options.PublicUrl };
        var email = await renderer.Render(template, culture, full, ct).ConfigureAwait(false);
        var status = Options.Delivers(recipient) ? EmailStatus.Pending : EmailStatus.Suppressed;
        var messageId = MimeUtils.GenerateMessageId(Options.From.Split('@') is [_, var domain] ? domain : "localhost");

        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await using (own)
        {
            await using var cmd = await Sql.Command(db, """
                INSERT INTO notifications.outbound_emails (template, culture, account_id, recipient, subject, html, message_id, dedup_key, headers, status, created_at)
                VALUES (@template, @culture, @account, @recipient, @subject, @html, @message, @dedup, @headers::jsonb, @status, @now)
                ON CONFLICT (dedup_key) DO NOTHING
                RETURNING id
                """, ct, ("template", template), ("culture", culture), ("account", accountId), ("recipient", recipient.Trim()), ("subject", email.Subject),
                ("html", email.Html), ("message", messageId), ("dedup", dedupKey), ("headers", headers is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(headers) : null),
                ("status", status.ToString()), ("now", time.GetUtcNow())).ConfigureAwait(false);
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long id)
            {
                return false;
            }

            if (status == EmailStatus.Pending)
            {
                await jobs.Enqueue(JobKinds.SendEmail, new { email_id = id }, dedupKey: $"{JobKinds.SendEmail}:{id}", maxAttempts: SendAttempts, ct: ct).ConfigureAwait(false);
            }

            if (own is not null)
            {
                await own.CommitAsync(ct).ConfigureAwait(false);
            }

            return true;
        }
    }

    /// <summary>Aviso sobre un documento: factura emitida, recordatorio, vencida o comprobante fiscal.</summary>
    public async Task<bool> QueueDocument(string template, Document doc, Account account, FiscalDocument? fiscal, string dedupKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(account);
        var culture = TemplateRenderer.CultureFor(account.Language);
        var model = new Dictionary<string, object?>
        {
            ["account_name"] = account.Name,
            ["number"] = doc.Number?.ToString(CultureInfo.InvariantCulture),
            ["credit_note"] = doc.Kind == DocumentKind.CreditNote,
            ["debit_note"] = doc.Kind == DocumentKind.DebitNote,
            ["issue_date"] = doc.IssueDate is { } issued ? Date(issued, culture) : null,
            ["due_date"] = Date(doc.DueDate, culture),
            ["days_overdue"] = Math.Max(0, core.Today().DayNumber - doc.DueDate.DayNumber),
            ["total"] = Money(doc.Total, doc.Currency, culture),
            ["pending"] = Money(doc.Pending, doc.Currency, culture),
            ["paid"] = doc.Pending <= 0m,
            ["lines"] = doc.Lines.Select(l => new Dictionary<string, object?>
            {
                ["description"] = l.Description,
                ["amount"] = Money(l.Amount, doc.Currency, culture),
            }).ToList(),
            ["fiscal"] = fiscal is not null,
            ["fiscal_number"] = fiscal is null ? null : string.Create(CultureInfo.InvariantCulture, $"{fiscal.PointOfSale:D5}-{fiscal.Number:D8}"),
            ["fiscal_type"] = fiscal?.TypeCode,
            ["cae"] = fiscal?.AuthorizationCode,
            ["cae_due"] = fiscal?.AuthorizationDue is { } due ? Date(due, culture) : null,
        };

        // Aviso previo de cada débito automático, dentro del correo de la factura (sección 4.5 del diseño de cobros).
        if (template == TemplateKeys.InvoiceIssued && await AutoChargeService.Preview(db, doc, payments.Value.AutoCharge, core.Today(), ct).ConfigureAwait(false) is { } debit)
        {
            model["autocharge_date"] = Date(debit.Date, culture);
            model["autocharge_method"] = AutoChargeService.Describe(debit.Method);
        }

        return await Queue(template, account.Email, account.Language, model, dedupKey, account.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Fecha de negocio de un instante, en la zona del proveedor.</summary>
    public DateOnly LocalDate(DateTimeOffset at) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(core.Options.TimeZone)).DateTime);

    /// <summary>Fecha y hora de un instante en la zona del proveedor, para los avisos de acceso.</summary>
    public string LocalTime(DateTimeOffset at, string culture)
    {
        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(core.Options.TimeZone));
        return Date(DateOnly.FromDateTime(local.DateTime), culture) + local.ToString(" HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Money(decimal amount, Currency currency, string culture) =>
        $"{currency.Code} {amount.ToString("N2", CultureInfo.GetCultureInfo(culture))}";

    public static string Date(DateOnly date, string culture) =>
        culture == "en" ? date.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en")) : date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}

/// <summary>
/// Los correos de acceso y de los trámites del consumidor (D-10) salen por la misma cola que el resto de los avisos,
/// con reintentos y la misma traba. El enlace y el código quedan solo en el correo armado, nunca en el log.
/// </summary>
public sealed class NotifierAccountMail(Notifier notifier) : IAccountMail
{
    public Task SendPasswordReset(string email, string language, Uri link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        return notifier.Queue(TemplateKeys.PasswordReset, email, language, new Dictionary<string, object?> { ["link"] = link.ToString(), ["minutes"] = 60 },
            $"{TemplateKeys.PasswordReset}:{Guid.NewGuid():N}", ct: ct);
    }

    public Task SendConsumerCode(string email, string language, ConsumerRequestKind kind, string code, CancellationToken ct) =>
        notifier.Queue(TemplateKeys.ConsumerCode, email, language,
            new Dictionary<string, object?> { ["code"] = code, ["withdrawal"] = kind == ConsumerRequestKind.Withdrawal, ["minutes"] = (int)ConsumerRequestService.CodeLifetime.TotalMinutes },
            $"{TemplateKeys.ConsumerCode}:{Guid.NewGuid():N}", ct: ct);

    public Task SendEmailVerification(string email, string language, Uri link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        return notifier.Queue(TemplateKeys.EmailVerification, email, language, new Dictionary<string, object?> { ["link"] = link.ToString(), ["hours"] = 24 },
            $"{TemplateKeys.EmailVerification}:{Guid.NewGuid():N}", ct: ct);
    }

    /// <summary>Un aviso por ingreso: la fecha y la hora van en la zona del proveedor.</summary>
    public Task SendNewLogin(string email, string language, DateTimeOffset at, string ip, string device, Uri? link, CancellationToken ct)
    {
        var culture = TemplateRenderer.CultureFor(language);
        var model = new Dictionary<string, object?>
        {
            ["when"] = notifier.LocalTime(at, culture),
            ["ip"] = ip,
            ["device"] = device,
            ["link"] = link?.ToString(),
        };
        return notifier.Queue(TemplateKeys.NewLogin, email, language, model, $"{TemplateKeys.NewLogin}:{Guid.NewGuid():N}", ct: ct);
    }

    public Task SendExternalLink(string email, string language, string provider, Uri link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        return notifier.Queue(TemplateKeys.ExternalLink, email, language, new Dictionary<string, object?> { ["link"] = link.ToString(), ["provider"] = provider, ["minutes"] = 60 },
            $"{TemplateKeys.ExternalLink}:{Guid.NewGuid():N}", ct: ct);
    }

    /// <summary>Una constancia por código: repetir el trámite no manda otra.</summary>
    public Task SendConsumerReceipt(string email, string language, ConsumerRequestKind kind, string receipt, DateTimeOffset at, CancellationToken ct)
    {
        var template = kind == ConsumerRequestKind.Cancellation ? TemplateKeys.CancellationReceipt : TemplateKeys.WithdrawalReceipt;
        var model = new Dictionary<string, object?> { ["code"] = receipt, ["requested"] = Notifier.Date(notifier.LocalDate(at), TemplateRenderer.CultureFor(language)) };
        return notifier.Queue(template, email, language, model, $"{template}:{receipt}", ct: ct);
    }
}
