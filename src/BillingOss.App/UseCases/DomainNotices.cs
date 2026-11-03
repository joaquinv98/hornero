using System.Globalization;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

public enum DomainNotice
{
    /// <summary>Vence pronto y no se va a renovar solo: no tiene renovación automática o la renovación está sin pagar.</summary>
    Expiring,

    /// <summary>Venció sin renovación paga y está en gracia: todavía se puede renovar.</summary>
    Expired,

    /// <summary>El registrar terminó el registro de un pedido.</summary>
    Registered,

    /// <summary>El registro de origen soltó el dominio y la transferencia terminó.</summary>
    Transferred,

    /// <summary>El registrar renovó el dominio: expires_on es el vencimiento nuevo.</summary>
    Renewed,

    /// <summary>
    /// La transferencia falló en el registrar. reason dice por qué: invalid_auth_code (el código EPP no sirve), taken o
    /// failed. El cliente puede reintentarla con otro código desde el dominio.
    /// </summary>
    TransferFailed,
}

/// <summary>
/// Lo que lleva un aviso de dominio, con los nombres que usan las plantillas (en snake_case): account_name, domain,
/// expires_on (en los de vencimiento, PaidThrough, el día que deja de estar pago, y en los de una operación terminada, el
/// vencimiento en el registro), days (los que faltan o los que pasaron), auto_renew, amount con la moneda, invoice_id si
/// la renovación está facturada sin pagar, grace_until, el último día para recuperarlo, y reason en una transferencia
/// fallida.
/// </summary>
public sealed record DomainNoticeData(
    long AccountId,
    string Email,
    string Language,
    string AccountName,
    long DomainId,
    string Domain,
    DateOnly ExpiresOn,
    int Days,
    bool AutoRenew,
    decimal Amount,
    Currency Currency,
    long? InvoiceId,
    DateOnly GraceUntil,
    string? Reason = null);

/// <summary>
/// Avisos de dominios (sección 11 del modelo de dominio): los de vencimiento (evento DomainExpiring) y los de una
/// operación terminada en el registrar. La interfaz la arma el núcleo y la implementa el módulo de notificaciones, que los
/// deja en la cola de correo con la plantilla de <see cref="DomainNotices.Template"/>. Con la misma clave no se manda dos
/// veces: la corrida diaria los pide todos los días que corresponde y la clave los deja en uno, y un trabajo que se repite
/// pide el mismo aviso con la misma clave. Se llama dentro de la transacción del que avisa, así el aviso queda con lo que
/// pasó o no queda.
/// </summary>
public interface IDomainMail
{
    Task Send(DomainNotice notice, DomainNoticeData data, string dedupKey, CancellationToken ct);
}

public static class DomainNotices
{
    /// <summary>La plantilla de cada aviso, en src/BillingOss.App/Templates con una por idioma.</summary>
    public static string Template(DomainNotice notice) => notice switch
    {
        DomainNotice.Expiring => "domain-expiring",
        DomainNotice.Expired => "domain-expired",
        DomainNotice.Registered => "domain-registered",
        DomainNotice.Transferred => "domain-transferred",
        DomainNotice.Renewed => "domain-renewed",
        DomainNotice.TransferFailed => "domain-transfer-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null),
    };

    /// <summary>
    /// Avisa al titular de la cuenta lo que terminó en el registrar: el registro, la transferencia o la renovación de
    /// <paramref name="domain"/>, o la transferencia que falló con su motivo. La clave sale de la operación: repetir el
    /// trabajo no manda otro.
    /// </summary>
    public static async Task OperationFinished(BillingDbContext db, IDomainMail mail, BillingOptions options, RegisteredDomain domain, DomainOperation op, DomainNotice notice, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(op);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == domain.AccountId, ct).ConfigureAwait(false);
        var expires = domain.ExpiresOn ?? domain.PaidThrough ?? DateOnly.FromDateTime(op.CreatedAt.UtcDateTime);
        var data = new DomainNoticeData(account.Id, account.Email, account.Language, account.Name, domain.Id, domain.Name, expires, 0, domain.AutoRenew,
            domain.RecurringAmount, domain.Currency, null, (domain.PaidThrough ?? expires).AddDays(options.DomainGraceDays), reason);
        var key = string.Create(CultureInfo.InvariantCulture, $"{Template(notice)}:{domain.Id}:{op.Id}");
        await mail.Send(notice, data, key, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// La corrida diaria de los avisos. Antes del vencimiento avisa a los dominios que no se van a renovar solos, en los
/// días de <see cref="BillingOptions.DomainExpiryNoticeDays"/>, y en la gracia, en los de
/// <see cref="BillingOptions.DomainGraceNoticeDays"/>. Cada umbral es un aviso por vencimiento: la clave lleva el
/// dominio, el día que deja de estar pago y el umbral, así que una corrida repetida o atrasada no duplica, y si un día
/// no corrió el aviso sale al siguiente. No guarda nada: el estado sale de lo pago.
/// </summary>
public sealed class DomainNoticesHandler(BillingDbContext db, BillingCore core, IDomainMail mail) : IJobHandler
{
    public string Kind => JobKinds.DomainNotices;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        var options = core.Options;
        var today = core.Today();
        var before = options.ExpiryNoticeDays;
        var after = options.GraceNoticeDays;
        var until = today.AddDays(before.Max());
        var rows = await (from d in db.Domains.AsNoTracking()
                          join a in db.Accounts.AsNoTracking() on d.AccountId equals a.Id
                          where d.PaidThrough != null
                                && ((d.Status == DomainStatus.Active && d.PaidThrough >= today && d.PaidThrough <= until)
                                    || (d.Status == DomainStatus.Grace && d.PaidThrough < today))
                          orderby d.Id
                          select new { Domain = d, a.Email, a.Language, a.Name }).ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var d = row.Domain;
            var paid = d.PaidThrough!.Value;
            var invoiced = d.NextDueDate is { } next && paid < next;
            DomainNotice notice;
            int days, threshold;
            if (d.Status == DomainStatus.Active)
            {
                // Uno con renovación automática y sin facturar se renueva solo: lo avisa la factura cuando sale.
                days = paid.DayNumber - today.DayNumber;
                if ((d.AutoRenew && !invoiced) || before.Where(t => t >= days).DefaultIfEmpty(-1).Min() is not (>= 0 and var t))
                {
                    continue;
                }

                (notice, threshold) = (DomainNotice.Expiring, t);
            }
            else
            {
                days = today.DayNumber - paid.DayNumber;
                if (after.Where(t => t <= days).DefaultIfEmpty(-1).Max() is not (>= 0 and var t))
                {
                    continue;
                }

                (notice, threshold) = (DomainNotice.Expired, t);
            }

            var invoice = invoiced ? await UnpaidInvoice(d.Id, ct).ConfigureAwait(false) : null;
            var data = new DomainNoticeData(d.AccountId, row.Email, row.Language, row.Name, d.Id, d.Name, paid, days, d.AutoRenew, d.RecurringAmount, d.Currency, invoice, paid.AddDays(options.DomainGraceDays));
            var key = string.Create(CultureInfo.InvariantCulture, $"{DomainNotices.Template(notice)}:{d.Id}:{paid:yyyy-MM-dd}:{threshold}");
            await mail.Send(notice, data, key, ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }

    /// <summary>
    /// La factura de la renovación emitida y sin pagar, para que el aviso lleve a pagarla. Solo la de la renovación: la de
    /// una opción contratada también es del dominio y pagarla no lo renueva.
    /// </summary>
    private Task<long?> UnpaidInvoice(long domainId, CancellationToken ct) =>
        db.Documents.AsNoTracking()
            .Where(x => x.Kind == DocumentKind.Invoice && x.IssueDate != null && x.Total - x.Credited - x.Allocated > 0 && x.Lines.Any(l => l.DomainId == domainId && l.Kind == LineKind.Renewal))
            .OrderByDescending(x => x.Id)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(ct);
}
