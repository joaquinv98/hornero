using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Payments;

public enum AttemptStatus
{
    Open,
    Paid,

    /// <summary>La pasarela rechazó el cobro: Outcome dice por qué y Retry si se puede volver a intentar.</summary>
    Failed,
}

/// <summary>Quién inicia el cobro: el cliente desde el portal o el comercio con un medio guardado (débito automático).</summary>
public enum AttemptInitiator
{
    Customer,
    Merchant,
}

/// <summary>
/// Intento de cobro por una pasarela (sección 8 del modelo de dominio). La referencia es pública y no se puede
/// adivinar: viaja a la pasarela, vuelve con el pago y es la de la página de retorno.
/// </summary>
public sealed class PaymentAttempt
{
    private PaymentAttempt()
    {
    }

    public PaymentAttempt(long? accountId, long moduleInstanceId, Currency currency, decimal amount, IReadOnlyList<long> documentIds, string reference, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Un intento de cobro tiene que ser positivo.");
        }

        Rounding.EnsureCents(amount, nameof(amount));
        AccountId = accountId;
        ModuleInstanceId = moduleInstanceId;
        Currency = currency;
        Amount = amount;
        DocumentIds = [.. documentIds];
        Reference = reference;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    /// <summary>La cuenta que paga. Solo un pago de prueba del asistente va sin cuenta.</summary>
    public long? AccountId { get; private set; }

    public long ModuleInstanceId { get; private set; }

    public Currency Currency { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>Facturas que cubre el intento.</summary>
    public IReadOnlyList<long> DocumentIds { get; private set; } = [];

    public AttemptStatus Status { get; private set; }

    public string? RedirectUrl { get; set; }

    /// <summary>ID del checkout en la pasarela, si lo informó: con él el núcleo pregunta por el pago mientras el intento sigue abierto.</summary>
    public string? CheckoutExternalId { get; set; }

    public long? PaymentId { get; private set; }

    /// <summary>
    /// Si la pasarela cobra en otra moneda que la de las facturas: unidades de la moneda del intento por una de la de
    /// las facturas, congelada al abrirlo. El pago se imputa con ella.
    /// </summary>
    public decimal? Rate { get; private set; }

    /// <summary>La cotización de la que sale <see cref="Rate"/>, con su fuente y su fecha.</summary>
    public long? ExchangeRateId { get; private set; }

    public AttemptInitiator Initiator { get; private set; }

    /// <summary>El cliente pidió guardar el medio para el débito automático. Consent es la constancia de esa aceptación.</summary>
    public bool SaveMethod { get; private set; }

    /// <summary>Constancia del consentimiento: quién, cuándo, IP, navegador y huella del texto aceptado. JSON.</summary>
    public string? Consent { get; private set; }

    /// <summary>El medio guardado con que cobra el comercio, en los cobros automáticos.</summary>
    public long? SavedMethodId { get; private set; }

    /// <summary>Pago de prueba del asistente: no se registra como pago ni toca facturas, saldo ni comprobantes.</summary>
    public bool IsTest { get; private set; }

    /// <summary>
    /// Lo último que dijo la pasarela: el estado del pago o el código del rechazo. En un cobro del comercio,
    /// <see cref="SendingOutcome"/> mientras se pide sin respuesta todavía.
    /// </summary>
    public string? Outcome { get; private set; }

    /// <summary>Outcome de un cobro del comercio pedido sin respuesta todavía: el pedido pudo llegar a la pasarela.</summary>
    public const string SendingOutcome = "sending";

    /// <summary>En un rechazo, si se puede reintentar: later, never o customer_action.</summary>
    public string? Retry { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void ConvertedWith(long exchangeRateId, decimal rate)
    {
        ExchangeRateId = exchangeRateId;
        Rate = rate > 0m ? rate : throw new ArgumentOutOfRangeException(nameof(rate), rate, "La cotización tiene que ser positiva.");
    }

    /// <summary>Intento que inicia el cliente y que además guarda el medio, con la constancia de su consentimiento.</summary>
    public void SaveMethodWith(string consent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consent);
        SaveMethod = true;
        Consent = consent;
    }

    /// <summary>Cobro que inicia el comercio con un medio guardado: el débito automático de una renovación.</summary>
    public static PaymentAttempt Automatic(long accountId, long moduleInstanceId, Currency currency, decimal amount, long documentId, long savedMethodId, string reference, DateTimeOffset now) =>
        new(accountId, moduleInstanceId, currency, amount, [documentId], reference, now) { Initiator = AttemptInitiator.Merchant, SavedMethodId = savedMethodId };

    /// <summary>Pago de prueba del asistente, por un monto chico, sin cuenta y sin facturas.</summary>
    public static PaymentAttempt Test(long moduleInstanceId, Currency currency, decimal amount, string reference, DateTimeOffset now) =>
        new(null, moduleInstanceId, currency, amount, [], reference, now) { IsTest = true };

    public void MarkPaid(long paymentId, DateTimeOffset now)
    {
        Status = AttemptStatus.Paid;
        PaymentId = paymentId;
        UpdatedAt = now;
    }

    /// <summary>La pasarela no cobró. Un intento ya pagado no vuelve atrás.</summary>
    public void Fail(string outcome, string? retry, DateTimeOffset now)
    {
        if (Status == AttemptStatus.Paid)
        {
            return;
        }

        Status = AttemptStatus.Failed;
        Outcome = outcome.Length > 200 ? outcome[..200] : outcome;
        Retry = retry;
        UpdatedAt = now;
    }

    /// <summary>
    /// El comercio va a pedir el cobro: desde acá el pedido pudo llegar a la pasarela aunque la respuesta nunca vuelva
    /// (un corte, o un nodo que se apaga en el medio). Solo marca un intento que todavía no tenía nada.
    /// </summary>
    public void Sending(DateTimeOffset now)
    {
        if (Outcome is null)
        {
            Outcome = SendingOutcome;
            UpdatedAt = now;
        }
    }

    /// <summary>El pedido marcado con <see cref="Sending"/> no salió: el intento vuelve a no tener nada.</summary>
    public void NotSent(DateTimeOffset now)
    {
        if (Outcome == SendingOutcome)
        {
            Outcome = null;
            UpdatedAt = now;
        }
    }

    /// <summary>Lo que dijo la pasarela de un intento que sigue abierto, o de un pago de prueba.</summary>
    public void Observe(string outcome, DateTimeOffset now)
    {
        Outcome = outcome.Length > 200 ? outcome[..200] : outcome;
        if (IsTest && outcome is "approved" or "partially_refunded" or "refunded" or "disputed" or "charged_back")
        {
            Status = AttemptStatus.Paid;
        }

        UpdatedAt = now;
    }
}

public enum ReceiptStatus
{
    Received,
    Verified,
    Invalid,
}

/// <summary>Lo que llegó al webhook de una instancia, crudo y sin unicidad (sección 9.3 del protocolo).</summary>
public sealed class WebhookReceipt
{
    public long Id { get; private set; }

    public long ModuleInstanceId { get; set; }

    /// <summary>Encabezados sin Cookie ni Authorization. JSON con un texto o una lista de textos por nombre.</summary>
    public string Headers { get; set; } = "{}";

    /// <summary>Parámetros de la URL, con la misma forma que los encabezados. JSON.</summary>
    public string Query { get; set; } = "{}";

    public byte[] Body { get; set; } = [];

    public ReceiptStatus Status { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// Pago que la pasarela informó y no corresponde a ningún intento propio: de otro sistema que usa la misma cuenta,
/// o cobrado después del punto al que se restauró la base. No se registra como pago: queda a la vista del staff.
/// </summary>
public sealed class UnmatchedPayment
{
    public long Id { get; private set; }

    public long ModuleInstanceId { get; set; }

    public string PaymentExternalId { get; set; } = string.Empty;

    public string? ExternalReference { get; set; }

    public string Status { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateTimeOffset SeenAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>Evento que el módulo verificó. Único por instancia e ID de evento: así una repetición no se procesa dos veces.</summary>
public sealed class GatewayEvent
{
    public long Id { get; private set; }

    public long ModuleInstanceId { get; set; }

    public string EventId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string? PaymentExternalId { get; set; }

    public long ReceiptId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
