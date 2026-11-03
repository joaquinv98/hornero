using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Payments;

/// <summary>
/// Lo efectivamente cobrado (sección 8 del modelo de dominio). <see cref="Channel"/> dice por dónde entró: una
/// instancia de pasarela ("instance:7") o un nombre de carga manual o por API ("manual:transferencia"). El par
/// canal e ID externo es único: el mismo pago nunca entra dos veces.
/// </summary>
public sealed class Payment
{
    private Payment()
    {
    }

    public Payment(long accountId, string channel, string? externalId, Currency currency, decimal amount, PaymentStatus status, DateTimeOffset now, decimal fee = 0m)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Un pago tiene que ser positivo.");
        }

        Rounding.EnsureCents(amount, nameof(amount));
        Rounding.EnsureCents(fee, nameof(fee));
        AccountId = accountId;
        Channel = channel;
        ExternalId = externalId;
        Currency = currency;
        Amount = amount;
        Status = status;
        Fee = fee;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public string Channel { get; private set; } = string.Empty;

    public string? ExternalId { get; private set; }

    public long? AttemptId { get; set; }

    public Currency Currency { get; private set; }

    public decimal Amount { get; private set; }

    public decimal Fee { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>Suma imputada a documentos o pasada a saldo. Nunca supera el pago.</summary>
    public decimal Applied { get; private set; }

    /// <summary>Lo que ya volvió al pagador, por reembolsos o por contracargo, y está reflejado en las cuentas.</summary>
    public decimal Refunded { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public decimal Unapplied => Amount - Applied;

    /// <summary>Lo cobrado que todavía no volvió al pagador.</summary>
    public decimal Kept => Amount - Refunded;

    /// <summary>Se cobró alguna vez: todo estado que no es pendiente ni rechazado pasó por aprobado.</summary>
    public bool Collected => Status is not (PaymentStatus.Pending or PaymentStatus.Rejected);

    /// <summary>Aplica una transición del diagrama. Devuelve false si el aviso no encaja y se ignora.</summary>
    public bool TryMoveTo(PaymentStatus status)
    {
        if (!PaymentStates.Machine.CanMove(Status, status))
        {
            return false;
        }

        Status = status;
        return true;
    }

    public void Apply(decimal amount)
    {
        if (Status != PaymentStatus.Approved || amount <= 0m || amount > Unapplied)
        {
            throw new InvalidOperationException($"No se pueden aplicar {amount} del pago {Id}.");
        }

        Applied += amount;
    }

    /// <summary>
    /// Pago traído del sistema anterior (D-17), con su estado y lo que se le imputó. Sin estado, aprobado y aplicado
    /// por completo.
    /// </summary>
    public static Payment FromImport(long accountId, string channel, string externalId, Currency currency, decimal amount, decimal fee, DateTimeOffset paidAt, PaymentStatus status = PaymentStatus.Approved, decimal? applied = null) =>
        new(accountId, channel, externalId, currency, amount, status, paidAt, fee) { Applied = applied ?? amount };

    public void Unapply(decimal amount)
    {
        if (amount <= 0m || amount > Applied)
        {
            throw new InvalidOperationException($"No se pueden desaplicar {amount} del pago {Id}.");
        }

        Applied -= amount;
    }

    public void AddRefunded(decimal amount)
    {
        if (amount <= 0m || amount > Kept)
        {
            throw new InvalidOperationException($"No se pueden devolver {amount} del pago {Id}: quedan {Kept}.");
        }

        Refunded += amount;
    }
}

/// <summary>Cuánto de un pago o del saldo a favor se aplica a un documento, en la moneda del documento.</summary>
public sealed class Allocation
{
    private Allocation()
    {
    }

    public Allocation(long documentId, decimal amount, decimal rate, DateTimeOffset now, long? paymentId = null, long? creditEntryId = null, decimal? paymentAmount = null)
    {
        if ((paymentId is null) == (creditEntryId is null))
        {
            throw new ArgumentException("Una imputación sale de un pago o del saldo, de uno solo.");
        }

        Rounding.EnsureCents(amount, nameof(amount));
        if (paymentAmount is { } paid)
        {
            Rounding.EnsureCents(paid, nameof(paymentAmount));
        }

        DocumentId = documentId;
        Amount = amount;
        Rate = rate;
        CreatedAt = now;
        PaymentId = paymentId;
        CreditEntryId = creditEntryId;
        PaymentAmount = paymentAmount;
    }

    public long Id { get; private set; }

    public long DocumentId { get; private set; }

    public long? PaymentId { get; private set; }

    public long? CreditEntryId { get; private set; }

    /// <summary>Importe en la moneda del documento.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Cotización de la moneda del pago a la del documento: unidades del documento por una del pago. 1 si son iguales.</summary>
    public decimal Rate { get; private set; }

    /// <summary>Lo que consumió del pago, en la moneda del pago, cuando es otra que la del documento.</summary>
    public decimal? PaymentAmount { get; private set; }

    /// <summary>Lo que consumió del pago en su moneda. Las importadas en otra moneda no lo guardan: sale de la cotización.</summary>
    public decimal PaymentShare => PaymentAmount ?? (Rate == 1m ? Amount : Rounding.Commercial(Amount / Rate));

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReversedAt { get; private set; }

    public void Reverse(DateTimeOffset now) => ReversedAt = now;

    /// <summary>
    /// Revierte la imputación. Si se revierte solo una parte, devuelve otra por el resto, con el mismo origen. En otra
    /// moneda, <paramref name="paymentShare"/> es lo que vuelve del pago, y si falta sale en proporción.
    /// </summary>
    public Allocation? ReverseUpTo(decimal amount, DateTimeOffset now, decimal? paymentShare = null)
    {
        if (amount <= 0m || amount > Amount)
        {
            throw new InvalidOperationException($"Reversa de {amount} inválida en la imputación {Id}.");
        }

        Reverse(now);
        var restPaid = PaymentAmount is { } paid ? paid - (paymentShare ?? Rounding.Commercial(paid * amount / Amount)) : (decimal?)null;
        return amount < Amount ? new Allocation(DocumentId, Amount - amount, Rate, now, PaymentId, CreditEntryId, restPaid) : null;
    }
}

public enum RefundOrigin
{
    /// <summary>Lo pidió el staff: primero la nota de crédito y después la plata por la pasarela.</summary>
    Staff,

    /// <summary>La pasarela informó un reembolso que el núcleo no pidió, por ejemplo hecho desde su panel.</summary>
    Gateway,

    Chargeback,
}

public enum RefundStatus
{
    Requested,
    Completed,
    Failed,
}

/// <summary>
/// Plata que vuelve al pagador (sección 8 del modelo de dominio). <see cref="Uncovered"/> es lo que no se pudo sacar
/// de las imputaciones ni del saldo a favor porque ya se había consumido: eso lo resuelve el staff.
/// </summary>
public sealed class Refund
{
    private Refund()
    {
    }

    public Refund(long paymentId, RefundOrigin origin, decimal amount, DateTimeOffset now, long? creditNoteId = null)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Un reembolso tiene que ser positivo.");
        }

        Rounding.EnsureCents(amount, nameof(amount));
        PaymentId = paymentId;
        Origin = origin;
        Amount = amount;
        CreditNoteId = creditNoteId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public long PaymentId { get; private set; }

    public RefundOrigin Origin { get; private set; }

    public decimal Amount { get; private set; }

    public RefundStatus Status { get; private set; }

    /// <summary>Nota de crédito que se emitió antes de mover la plata, en los reembolsos del staff.</summary>
    public long? CreditNoteId { get; private set; }

    /// <summary>ID del reembolso en la pasarela, si lo informó.</summary>
    public string? ExternalId { get; private set; }

    public decimal Uncovered { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>La plata salió. Vale también para uno que había fallado y que después la pasarela mostró devuelto.</summary>
    public void Complete(decimal uncovered, DateTimeOffset now, string? externalId = null)
    {
        if (Status == RefundStatus.Completed || uncovered < 0m || uncovered > Amount)
        {
            throw new InvalidOperationException($"El reembolso {Id} no se puede completar.");
        }

        Status = RefundStatus.Completed;
        Uncovered = uncovered;
        ExternalId = externalId ?? ExternalId;
        UpdatedAt = now;
    }

    /// <summary>La pasarela no devolvió la plata: el importe sigue como saldo a favor.</summary>
    public void Fail(string error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Status != RefundStatus.Requested)
        {
            throw new InvalidOperationException($"El reembolso {Id} ya no está pedido.");
        }

        Status = RefundStatus.Failed;
        Error = error.Length > 2000 ? error[..2000] : error;
        UpdatedAt = now;
    }
}
