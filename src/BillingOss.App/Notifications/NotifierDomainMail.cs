using BillingOss.App.UseCases;

namespace BillingOss.App.Notifications;

/// <summary>
/// Los avisos de dominios (<see cref="IDomainMail"/>) por la cola de correo, con la plantilla de cada aviso
/// (<see cref="DomainNotices.Template"/>) en el idioma de la cuenta. Corre en la transacción del que avisa, y con la
/// misma clave no se arma dos veces. La fecha y el monto van ya formateados, como en el resto de los avisos.
/// </summary>
public sealed class NotifierDomainMail(Notifier notifier) : IDomainMail
{
    public async Task Send(DomainNotice notice, DomainNoticeData data, string dedupKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        var culture = TemplateRenderer.CultureFor(data.Language);
        var model = new Dictionary<string, object?>
        {
            ["account_name"] = data.AccountName,
            ["domain"] = data.Domain,
            ["expires_on"] = Notifier.Date(data.ExpiresOn, culture),
            ["days"] = data.Days,
            ["auto_renew"] = data.AutoRenew,
            ["amount"] = Notifier.Money(data.Amount, data.Currency, culture),
            ["invoice_id"] = data.InvoiceId,
            ["grace_until"] = Notifier.Date(data.GraceUntil, culture),
            ["reason"] = data.Reason,
        };
        await notifier.Queue(DomainNotices.Template(notice), data.Email, data.Language, model, dedupKey, data.AccountId, ct).ConfigureAwait(false);
    }
}
