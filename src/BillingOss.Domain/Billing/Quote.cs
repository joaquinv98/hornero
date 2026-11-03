using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Billing;

public enum QuoteStatus
{
    /// <summary>Lo arma el staff. El cliente no lo ve.</summary>
    Draft,

    /// <summary>El cliente lo ve en el portal y lo puede aceptar o rechazar mientras esté vigente.</summary>
    Sent,

    Declined,

    /// <summary>Aceptado y convertido en una factura con sus renglones.</summary>
    Invoiced,

    /// <summary>Aceptado y convertido en un pedido: los renglones con plan crean sus servicios con el precio pactado.</summary>
    Ordered,
}

/// <summary>
/// Renglón de un presupuesto. Con <see cref="PlanId"/> cotiza un plan del catálogo: al pasar a pedido crea el servicio
/// con <see cref="UnitPrice"/> como precio pactado, que también vale para sus renovaciones. Sin plan es un renglón suelto.
/// </summary>
public sealed record QuoteLine(string Description, decimal Quantity, decimal UnitPrice, TaxTreatment Treatment = TaxTreatment.Taxed, long? PlanId = null);

/// <summary>
/// Presupuesto (sección 6.5 del modelo de dominio). No es un documento fiscal ni consume numeración: se edita mientras no
/// se acepta, y aceptado se convierte una sola vez en una factura o en un pedido. Su número es el ID.
/// </summary>
public sealed class Quote
{
    private Quote()
    {
    }

    public Quote(long accountId, DateTimeOffset now, string? actor)
    {
        AccountId = accountId;
        CreatedAt = now;
        UpdatedAt = now;
        Actor = actor;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public Currency Currency { get; private set; }

    /// <summary>Último día en que el cliente lo puede aceptar.</summary>
    public DateOnly ValidUntil { get; private set; }

    public QuoteStatus Status { get; private set; }

    public IReadOnlyList<QuoteLine> Lines { get; private set; } = [];

    /// <summary>Texto para el cliente: condiciones, alcance o forma de pago.</summary>
    public string? Notes { get; private set; }

    /// <summary>Total con impuestos al guardarlo, para las listas. La factura vuelve a calcular con la ficha de ese día.</summary>
    public decimal Total { get; private set; }

    /// <summary>Quién lo armó, como en la auditoría.</summary>
    public string? Actor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Cuándo se aceptó y convirtió, o cuándo se rechazó.</summary>
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>La factura que lo cobra: la de la conversión, o la del pedido.</summary>
    public long? DocumentId { get; private set; }

    public long? OrderId { get; private set; }

    public bool IsOpen => Status is QuoteStatus.Draft or QuoteStatus.Sent;

    /// <summary>Vencido sin respuesta: el cliente ya no lo puede aceptar, el staff sí lo puede convertir o extender.</summary>
    public bool IsExpired(DateOnly today) => IsOpen && ValidUntil < today;

    /// <summary>Si cotiza algún plan: aceptado pasa a pedido, si está en la moneda de la cuenta, y si no a factura.</summary>
    public bool HasPlans => Lines.Any(l => l.PlanId is not null);

    public void Set(string subject, Currency currency, DateOnly validUntil, IReadOnlyList<QuoteLine> lines, string? notes, decimal total, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        EnsureOpen();
        Subject = subject;
        Currency = currency;
        ValidUntil = validUntil;
        Lines = [.. lines];
        Notes = notes;
        Total = total;
        UpdatedAt = now;
    }

    public void Send(DateTimeOffset now)
    {
        EnsureOpen();
        Status = QuoteStatus.Sent;
        SentAt = now;
        UpdatedAt = now;
    }

    public void Decline(DateTimeOffset now)
    {
        EnsureOpen();
        Status = QuoteStatus.Declined;
        ClosedAt = now;
        UpdatedAt = now;
    }

    public void Invoiced(long documentId, DateTimeOffset now)
    {
        EnsureOpen();
        Status = QuoteStatus.Invoiced;
        DocumentId = documentId;
        ClosedAt = now;
        UpdatedAt = now;
    }

    public void Ordered(long orderId, long? invoiceId, DateTimeOffset now)
    {
        EnsureOpen();
        Status = QuoteStatus.Ordered;
        OrderId = orderId;
        DocumentId = invoiceId;
        ClosedAt = now;
        UpdatedAt = now;
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException($"El presupuesto {Id} ya está cerrado.");
        }
    }
}
