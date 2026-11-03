using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace BillingOss.App.Notifications;

/// <summary>
/// Suscriptor de los eventos del núcleo que avisan al cliente (D-13). Cada aviso lleva una clave de deduplicación
/// atada al evento: correr el trabajo de nuevo no arma otro.
/// </summary>
public sealed class NotifyEventHandler(BillingDbContext db, Notifier notifier, BillingCore core, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.NotifyEvent;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var json = JsonDocument.Parse(job.Payload);
        var p = json.RootElement;
        long Id(string name) => p.GetProperty(name).GetInt64();
        int Generation() => p.GetProperty("generation").GetInt32();
        return p.GetProperty("event").GetString() switch
        {
            // La nota de débito de un recargo se avisa como una factura: es otro documento que hay que pagar.
            Events.InvoiceIssued or Events.DebitNoteIssued => await InvoiceIssued(Id("document_id"), ct).ConfigureAwait(false),
            Events.FiscalDocumentAuthorized => await FiscalAuthorized(Id("document_id"), ct).ConfigureAwait(false),
            Events.PaymentReceived => await PaymentReceived(Id("payment_id"), ct).ConfigureAwait(false),
            Events.ServiceActivated => await Service(TemplateKeys.ServiceActivated, Id("service_id"), Generation(), ct).ConfigureAwait(false),
            Events.ServiceReactivated => await Service(TemplateKeys.ServiceReactivated, Id("service_id"), Generation(), ct).ConfigureAwait(false),
            Events.ServiceSuspended => await Service(TemplateKeys.ServiceSuspended, Id("service_id"), Generation(), ct).ConfigureAwait(false),
            Events.ServiceTerminated => await Service(TemplateKeys.ServiceTerminated, Id("service_id"), Generation(), ct).ConfigureAwait(false),
            _ => JobResult.Done,
        };
    }

    /// <summary>
    /// Si la factura va a tener comprobante fiscal al emitirse, el aviso lo espera para salir con el CAE, hasta
    /// <see cref="NotificationOptions.FiscalWait"/>. Con el comprobante ya autorizado, sale desde su propio evento.
    /// Este trabajo y el del comprobante toman el lock de la factura: así nunca salen dos avisos de la misma factura.
    /// </summary>
    private async Task<JobResult> InvoiceIssued(long documentId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", [documentId], ct).ConfigureAwait(false);
        var doc = await db.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        if (doc.Total == 0m || await db.FiscalDocuments.AnyAsync(f => f.DocumentId == documentId && !f.Imported, ct).ConfigureAwait(false))
        {
            return JobResult.Done;
        }

        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == doc.AccountId, ct).ConfigureAwait(false);
        var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        var fiscalAtIssue = issuer is { ModuleInstanceId: not null, Moment: EmissionMoment.AtIssue } && !doc.Imported && !account.NoFiscalDocument;
        if (fiscalAtIssue && time.GetUtcNow() < doc.IssuedAt + notifier.Options.FiscalWait)
        {
            return JobResult.PollLater(TimeSpan.FromSeconds(30));
        }

        await notifier.QueueDocument(TemplateKeys.InvoiceIssued, doc, account, null, $"{TemplateKeys.InvoiceIssued}:{documentId}", ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    /// <summary>Si la factura todavía no se avisó, el aviso de factura sale ahora con el CAE. Si no, va el comprobante aparte.</summary>
    private async Task<JobResult> FiscalAuthorized(long documentId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", [documentId], ct).ConfigureAwait(false);
        var doc = await db.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == doc.AccountId, ct).ConfigureAwait(false);
        var fiscal = await db.FiscalDocuments.AsNoTracking().SingleAsync(f => f.DocumentId == documentId && !f.Imported, ct).ConfigureAwait(false);
        var invoiceKey = $"{TemplateKeys.InvoiceIssued}:{documentId}";
        var invoiceNotified = await db.Set<OutboundEmail>().AnyAsync(e => e.DedupKey == invoiceKey, ct).ConfigureAwait(false);
        var template = doc.Kind != DocumentKind.CreditNote && doc.Total != 0m && !invoiceNotified ? TemplateKeys.InvoiceIssued : TemplateKeys.FiscalDocument;
        await notifier.QueueDocument(template, doc, account, fiscal, $"{TemplateKeys.FiscalDocument}:{documentId}", ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    private async Task<JobResult> PaymentReceived(long paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId, ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == payment.AccountId, ct).ConfigureAwait(false);
        var culture = TemplateRenderer.CultureFor(account.Language);
        var applied = await db.Allocations.AsNoTracking()
            .Where(a => a.PaymentId == paymentId && a.ReversedAt == null)
            .Join(db.Documents, a => a.DocumentId, d => d.Id, (a, d) => new { d.Number, a.Amount })
            .OrderBy(x => x.Number)
            .ToListAsync(ct).ConfigureAwait(false);
        var credit = payment.Amount - applied.Sum(x => x.Amount);
        var model = new Dictionary<string, object?>
        {
            ["account_name"] = account.Name,
            ["amount"] = Notifier.Money(payment.Amount, payment.Currency, culture),
            ["date"] = Notifier.Date(notifier.LocalDate(payment.CreatedAt), culture),
            ["documents"] = applied.Select(x => new Dictionary<string, object?>
            {
                ["number"] = x.Number,
                ["amount"] = Notifier.Money(x.Amount, payment.Currency, culture),
            }).ToList(),
            ["credit"] = credit > 0m ? Notifier.Money(credit, payment.Currency, culture) : null,
        };
        await notifier.Queue(TemplateKeys.PaymentReceived, account.Email, account.Language, model, $"{TemplateKeys.PaymentReceived}:{paymentId}", account.Id, ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    /// <summary>La generación del servicio en la clave: cada cambio de estado tiene su aviso y uno solo.</summary>
    private async Task<JobResult> Service(string template, long serviceId, int generation, CancellationToken ct)
    {
        var service = await db.Services.AsNoTracking().SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
        var product = await db.Products.AsNoTracking().Where(p => p.Id == service.ProductId).Select(p => p.Name).SingleAsync(ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == service.AccountId, ct).ConfigureAwait(false);
        var culture = TemplateRenderer.CultureFor(account.Language);
        var model = new Dictionary<string, object?>
        {
            ["account_name"] = account.Name,
            ["product"] = product,
            ["label"] = service.Label,
            ["service"] = string.IsNullOrEmpty(service.Label) ? product : $"{product} ({service.Label})",
            ["due_date"] = service.NextDueDate is { } due ? Notifier.Date(due, culture) : null,
            ["unpaid"] = service.PaidThrough is { } paid && paid < core.Today(),
        };
        await notifier.Queue(template, account.Email, account.Language, model, $"{template}:{serviceId}:{generation}", account.Id, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>
/// Recordatorios antes del vencimiento y avisos de factura vencida, en los días de los ajustes (por ejemplo 7, 3 y 1
/// antes, y 3, 10 y 20 después). Corre una vez por día y cada aviso sale una sola vez por documento y por día de la
/// lista. Valen para las facturas y las notas de débito del recargo, que son plata que se cobra. Las cuentas exentas no
/// los reciben.
/// </summary>
public sealed class InvoiceRemindersHandler(BillingDbContext db, Notifier notifier, BillingCore core) : IJobHandler
{
    /// <summary>
    /// Un aviso de vencida que no salió por una caída sale igual dentro de esta ventana, y no más tarde: al estrenar
    /// el sistema no se avisan deudas viejas.
    /// </summary>
    public const int CatchUpDays = 7;

    public string Kind => JobKinds.InvoiceReminders;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        var today = core.Today();

        // Cada día de la lista cubre hasta el siguiente: lo que no salió por una caída sale después, pero nunca dos
        // avisos del mismo tipo el mismo día.
        var before = NotificationOptions.Days(notifier.Options.ReminderDaysBefore).Reverse().ToList();
        for (var i = 0; i < before.Count; i++)
        {
            var closer = i + 1 < before.Count ? before[i + 1] : -1;
            await Notify(TemplateKeys.InvoiceReminder, before[i], today.AddDays(closer + 1), today.AddDays(before[i]), ct).ConfigureAwait(false);
        }

        var after = NotificationOptions.Days(notifier.Options.OverdueDaysAfter);
        for (var i = 0; i < after.Count; i++)
        {
            var oldest = Math.Min(after[i] + CatchUpDays - 1, i + 1 < after.Count ? after[i + 1] - 1 : int.MaxValue);
            await Notify(TemplateKeys.InvoiceOverdue, after[i], today.AddDays(-oldest), today.AddDays(-after[i]), ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }

    private async Task Notify(string template, int days, DateOnly dueFrom, DateOnly dueTo, CancellationToken ct)
    {
        var ids = await db.Documents.AsNoTracking()
            .Where(d => d.Kind != DocumentKind.CreditNote && d.IssueDate != null && d.DueDate >= dueFrom && d.DueDate <= dueTo && d.Total - d.Credited - d.Allocated > 0)
            .Where(d => !db.Accounts.Any(a => a.Id == d.AccountId && a.NoReminders))
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var doc = await db.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == id, ct).ConfigureAwait(false);
            var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == doc.AccountId, ct).ConfigureAwait(false);
            await notifier.QueueDocument(template, doc, account, null, $"{template}:{id}:{days}", ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Manda un correo de la cola por SMTP. La cola da un solo nodo por trabajo, y el envío entero tiene un tope muy
/// por debajo del lease: otro nodo no lo toma mientras este manda. Si el proceso cae entre la entrega y la marca
/// de enviado, el reintento lo manda otra vez con el mismo Message-ID. SMTP no permite más que eso.
/// </summary>
public sealed class SendEmailHandler(BillingDbContext db, IOptions<NotificationOptions> options, TimeProvider time) : IJobHandler
{
    public static TimeSpan SendTimeout { get; } = TimeSpan.FromMinutes(2);

    public string Kind => JobKinds.SendEmail;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var json = JsonDocument.Parse(job.Payload);
        var id = json.RootElement.GetProperty("email_id").GetInt64();
        var email = await db.Set<OutboundEmail>().SingleAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (email.Status != EmailStatus.Pending)
        {
            return JobResult.Done;
        }

        // La traba se mira también al mandar: una base de producción restaurada en staging no le escribe a nadie.
        if (!options.Value.Delivers(email.Recipient))
        {
            await Fail(email, "No sale: envío apagado, traba de producción o casilla fuera de la lista permitida.", EmailStatus.Suppressed, ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        if (!MailboxAddress.TryParse(email.Recipient, out var to))
        {
            await Fail(email, "La casilla no es válida.", EmailStatus.Failed, ct).ConfigureAwait(false);
            throw new PermanentFailureException($"La casilla del correo {id} no es válida.");
        }

        try
        {
            await Send(options.Value, email, to, ct).ConfigureAwait(false);
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && (int)ex.StatusCode >= 500)
        {
            // La casilla no existe o no acepta correo: reintentar no cambia nada. Otros rechazos definitivos suelen
            // ser de configuración (remitente, credenciales del relay) y se reintentan hasta que el staff los corrige.
            await Fail(email, Describe(ex), EmailStatus.Failed, ct).ConfigureAwait(false);
            throw new PermanentFailureException(Describe(ex));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await Fail(email, Describe(ex), job.Attempts >= job.MaxAttempts ? EmailStatus.Failed : EmailStatus.Pending, ct).ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw new TimeoutException("El envío superó el tiempo máximo.", ex);
            }

            throw;
        }

        email.Status = EmailStatus.Sent;
        email.SentAt = time.GetUtcNow();
        email.LastError = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    private static async Task Send(NotificationOptions o, OutboundEmail email, MailboxAddress to, CancellationToken ct)
    {
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.From));
        message.To.Add(to);
        message.Subject = email.Subject;
        message.MessageId = email.MessageId;
        message.Date = email.CreatedAt;
        message.Body = new TextPart(TextFormat.Html) { Text = email.Html };
        foreach (var (name, value) in email.Headers is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(email.Headers)!)
        {
            switch (name)
            {
                case "Reply-To" when MailboxAddress.TryParse(value, out var replyTo):
                    message.ReplyTo.Add(replyTo);
                    break;
                case "In-Reply-To":
                    message.InReplyTo = value.Trim('<', '>');
                    break;
                case "References":
                    foreach (var id in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        message.References.Add(id.Trim('<', '>'));
                    }

                    break;
                case "Auto-Submitted":
                    message.Headers[name] = value;
                    break;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        using var smtp = new SmtpClient { Timeout = 60_000 };
        await smtp.ConnectAsync(o.Host ?? throw new InvalidOperationException("Falta Notifications:Host."), o.Port, o.Security, timeout.Token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(o.User))
        {
            await smtp.AuthenticateAsync(o.User, o.Password ?? string.Empty, timeout.Token).ConfigureAwait(false);
        }

        await smtp.SendAsync(message, timeout.Token).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>El código SMTP adelante, para que el staff vea enseguida si fue temporal (4xx) o definitivo (5xx).</summary>
    private static string Describe(Exception ex) => ex is SmtpCommandException c ? $"{(int)c.StatusCode} {c.Message}" : ex.Message;

    private async Task Fail(OutboundEmail email, string error, EmailStatus status, CancellationToken ct)
    {
        email.Status = status;
        email.LastError = error.Length > 2000 ? error[..2000] : error;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
