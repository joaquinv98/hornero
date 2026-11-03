using System.Globalization;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Billing;

public enum BillableItemStatus
{
    Pending,
    Invoiced,
    Cancelled,
}

/// <summary>
/// Cargo suelto de una cuenta, por ejemplo horas de soporte o un disco de reemplazo. Se factura una sola vez: con la
/// corrida del día de <see cref="InvoiceOn"/> o en el momento, a pedido del staff. Su línea lleva la clave
/// <see cref="DedupKey"/>, así nunca entra en dos facturas.
/// </summary>
public sealed class BillableItem
{
    private BillableItem()
    {
    }

    public BillableItem(long accountId, string description, decimal quantity, decimal unitPrice, Currency currency, TaxTreatment treatment, DateOnly invoiceOn, DateTimeOffset now, long? serviceId = null, string? actor = null)
    {
        AccountId = accountId;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Currency = currency;
        Treatment = treatment;
        InvoiceOn = invoiceOn;
        ServiceId = serviceId;
        Actor = actor;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public long? ServiceId { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>Gravado sigue la regla del receptor al facturarse.</summary>
    public TaxTreatment Treatment { get; private set; }

    /// <summary>Desde qué día lo factura la corrida.</summary>
    public DateOnly InvoiceOn { get; private set; }

    public BillableItemStatus Status { get; private set; }

    /// <summary>La factura que lo cobró.</summary>
    public long? DocumentId { get; private set; }

    /// <summary>Quién lo cargó, como en la auditoría.</summary>
    public string? Actor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string DedupKey => string.Create(CultureInfo.InvariantCulture, $"item:{Id}");

    public void MarkInvoiced(long documentId, DateTimeOffset now)
    {
        if (Status != BillableItemStatus.Pending)
        {
            throw new InvalidOperationException($"El ítem {Id} ya no está pendiente.");
        }

        Status = BillableItemStatus.Invoiced;
        DocumentId = documentId;
        UpdatedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status != BillableItemStatus.Pending)
        {
            throw new InvalidOperationException($"El ítem {Id} ya no está pendiente: uno facturado se corrige con una nota de crédito.");
        }

        Status = BillableItemStatus.Cancelled;
        UpdatedAt = now;
    }
}
