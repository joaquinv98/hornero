using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Orders;

public enum OrderStatus
{
    Pending,
    Accepted,
    Completed,
    Cancelled,
}

/// <summary>Lo confirmado, con precios congelados (sección 4 del modelo de dominio).</summary>
public sealed class Order
{
    private readonly List<OrderLine> lines = [];

    private Order()
    {
    }

    public Order(long accountId, Currency currency, DateTimeOffset placedAt)
    {
        AccountId = accountId;
        Currency = currency;
        PlacedAt = placedAt;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public Currency Currency { get; private set; }

    public OrderStatus Status { get; set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public long? InvoiceId { get; set; }

    /// <summary>Cupón usado en el pedido.</summary>
    public long? CouponId { get; set; }

    /// <summary>Paquete de productos del pedido, si lo pidió.</summary>
    public long? BundleId { get; set; }

    /// <summary>Clave de idempotencia del cliente: el mismo pedido mandado dos veces se confirma una sola.</summary>
    public string? ClientReference { get; set; }

    /// <summary>API key que mandó la clave de idempotencia. La clave vale por llave: otra llave con la misma clave es otro pedido.</summary>
    public long? ApiKeyId { get; set; }

    /// <summary>Huella SHA-256 del pedido que llegó con la clave: la misma clave con otro pedido se rechaza.</summary>
    public string? RequestFingerprint { get; set; }

    public IReadOnlyList<OrderLine> Lines => lines;

    public OrderLine AddLine(long productId, long planId, int quantity, decimal unitPrice, decimal setupFee, string options = "[]", string orderData = "{}")
    {
        var line = new OrderLine(productId, planId, quantity, unitPrice, setupFee, options, orderData);
        lines.Add(line);
        return line;
    }

    public void SetImportedId(long id) => Id = id;
}

public sealed class OrderLine
{
    private OrderLine()
    {
    }

    internal OrderLine(long productId, long planId, int quantity, decimal unitPrice, decimal setupFee, string options, string orderData)
    {
        if (quantity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "La cantidad tiene que ser 1 o más.");
        }

        ProductId = productId;
        PlanId = planId;
        Quantity = quantity;
        UnitPrice = unitPrice;
        SetupFee = setupFee;
        Options = options;
        OrderData = orderData;
    }

    public long Id { get; private set; }

    public long OrderId { get; private set; }

    public long ProductId { get; private set; }

    public long PlanId { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal SetupFee { get; private set; }

    /// <summary>Opciones configurables elegidas, como lista de opción, valor y cantidad. JSON.</summary>
    public string Options { get; private set; } = "[]";

    /// <summary>Datos que completó el cliente, como el hostname. JSON.</summary>
    public string OrderData { get; private set; } = "{}";

    public long? ServiceId { get; set; }
}
