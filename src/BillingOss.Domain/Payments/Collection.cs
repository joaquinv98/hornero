using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Payments;

public enum SavedMethodStatus
{
    Active,
    Expired,
    Deleted,
}

/// <summary>
/// Medio de pago guardado en una pasarela (payment.saved_methods) para el débito automático. El núcleo guarda solo
/// los IDs de la pasarela y lo que se muestra (marca, últimos 4 y vencimiento), nunca el número ni el código de
/// seguridad. Único por instancia e ID externo del medio. La cuenta elige uno para el débito automático.
/// </summary>
public sealed class SavedPaymentMethod
{
    private SavedPaymentMethod()
    {
    }

    public SavedPaymentMethod(long accountId, long moduleInstanceId, string customerExternalId, string methodExternalId, string consent, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consent);
        AccountId = accountId;
        ModuleInstanceId = moduleInstanceId;
        CustomerExternalId = customerExternalId;
        MethodExternalId = methodExternalId;
        Consent = consent;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public long ModuleInstanceId { get; private set; }

    public string CustomerExternalId { get; private set; } = string.Empty;

    public string MethodExternalId { get; private set; } = string.Empty;

    /// <summary>card, por ejemplo.</summary>
    public string? Type { get; private set; }

    public string? Brand { get; private set; }

    public string? Last4 { get; private set; }

    public int? ExpMonth { get; private set; }

    public int? ExpYear { get; private set; }

    public SavedMethodStatus Status { get; private set; }

    /// <summary>Es el medio del débito automático de la cuenta. Una cuenta tiene a lo sumo uno activo así.</summary>
    public bool AutoCharge { get; private set; }

    /// <summary>
    /// Constancia del consentimiento: quién aceptó, cuándo, desde qué IP y navegador, y la huella del texto exacto. Se
    /// guarda mientras exista el medio. JSON.
    /// </summary>
    public string Consent { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Lo que se muestra del medio. Un medio borrado no vuelve.</summary>
    public void Describe(string? type, string? brand, string? last4, int? expMonth, int? expYear, DateTimeOffset now)
    {
        Type = Cut(type, 32);
        Brand = Cut(brand, 32);
        Last4 = last4 is { Length: 4 } && last4.All(char.IsAsciiDigit) ? last4 : null;
        ExpMonth = expMonth is >= 1 and <= 12 ? expMonth : null;
        ExpYear = expYear is >= 2000 and <= 2200 ? expYear : null;
        if (Status == SavedMethodStatus.Expired && !ExpiredOn(DateOnly.FromDateTime(now.UtcDateTime)))
        {
            Status = SavedMethodStatus.Active;
        }

        UpdatedAt = now;
    }

    public void UseForAutoCharge(bool on, DateTimeOffset now)
    {
        if (on && Status != SavedMethodStatus.Active)
        {
            throw new InvalidOperationException($"El medio {Id} no está activo: no puede ser el del débito automático.");
        }

        AutoCharge = on;
        UpdatedAt = now;
    }

    /// <summary>La tarjeta venció o la pasarela dijo que no sirve más: deja de usarse para el débito.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status == SavedMethodStatus.Active)
        {
            Status = SavedMethodStatus.Expired;
        }

        AutoCharge = false;
        UpdatedAt = now;
    }

    /// <summary>El cliente lo quitó o la pasarela lo borró. La revocación es inmediata (Ley 24.240, art. 10 ter).</summary>
    public void Delete(DateTimeOffset now)
    {
        Status = SavedMethodStatus.Deleted;
        AutoCharge = false;
        UpdatedAt = now;
    }

    /// <summary>Venció a fin del mes de su vencimiento.</summary>
    public bool ExpiredOn(DateOnly today) =>
        ExpYear is { } year && ExpMonth is { } month && (year < today.Year || (year == today.Year && month < today.Month));

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}

public enum SubscriptionStatus
{
    /// <summary>Creada en la pasarela, esperando que el cliente la apruebe.</summary>
    Pending,
    Active,
    Paused,
    Cancelled,
}

/// <summary>
/// Débito recurrente que administra la pasarela (sección 8 del modelo de dominio), por ejemplo una suscripción de
/// Mercado Pago. Cada cobro entra como pago con la suscripción y se imputa a la factura impaga más vieja de la cuenta
/// en su moneda. La referencia es propia y no se puede adivinar.
/// </summary>
public sealed class GatewaySubscription
{
    private GatewaySubscription()
    {
    }

    public GatewaySubscription(long accountId, long moduleInstanceId, Currency currency, decimal amount, string reference, string consent, DateTimeOffset now)
    {
        Rounding.EnsureCents(amount, nameof(amount));
        AccountId = accountId;
        ModuleInstanceId = moduleInstanceId;
        Currency = currency;
        Amount = amount;
        Reference = reference;
        Consent = consent;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public long ModuleInstanceId { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public string? ExternalId { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>Lo que cobra cada período, tal como se le informó a la pasarela.</summary>
    public decimal Amount { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public string? RedirectUrl { get; private set; }

    /// <summary>Constancia del consentimiento del débito. JSON.</summary>
    public string Consent { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Created(string externalId, string redirectUrl, DateTimeOffset now)
    {
        ExternalId = externalId;
        RedirectUrl = redirectUrl;
        UpdatedAt = now;
    }

    /// <summary>El estado que informa la pasarela. Una cancelada no vuelve.</summary>
    public bool Observe(SubscriptionStatus status, DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Cancelled || Status == status)
        {
            return false;
        }

        Status = status;
        UpdatedAt = now;
        return true;
    }

    public void ChangeAmount(decimal amount, DateTimeOffset now)
    {
        Rounding.EnsureCents(amount, nameof(amount));
        Amount = amount;
        UpdatedAt = now;
    }
}

/// <summary>
/// Cómo se ofrece una instancia de pasarela: lo que el staff activa en el asistente de cobros (sección 4.3 del diseño
/// de cobros). No viaja al módulo: es del núcleo.
/// </summary>
public sealed class GatewaySetup
{
    private GatewaySetup()
    {
    }

    public GatewaySetup(long moduleInstanceId, string displayName, DateTimeOffset now)
    {
        ModuleInstanceId = moduleInstanceId;
        DisplayName = displayName;
        UpdatedAt = now;
    }

    public long ModuleInstanceId { get; private set; }

    /// <summary>Se ofrece en el portal.</summary>
    public bool Enabled { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int Position { get; set; }

    /// <summary>Monedas que ofrece: las del manifiesto que el proveedor factura. Vacío, ninguna.</summary>
    public string[] Currencies { get; set; } = [];

    /// <summary>test o live. Prueba y producción son instancias distintas.</summary>
    public string? Environment { get; set; }

    /// <summary>Cobra en la misma moneda extranjera de la factura, para informarlo en el comprobante (RG 5616).</summary>
    public bool SameCurrencyCancel { get; set; }

    /// <summary>Ofrece guardar el medio y cobrar las renovaciones con él (payment.saved_methods).</summary>
    public bool AutoCharge { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    public bool? LastTestOk { get; set; }

    public string? LastTestMessage { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool Offers(Currency currency) => Enabled && Currencies.Contains(currency.Code, StringComparer.Ordinal);
}

/// <summary>Cuenta del proveedor para recibir transferencias: la que muestra el portal con el importe y el número de factura.</summary>
public sealed class TransferAccount
{
    public long Id { get; private set; }

    public string Bank { get; set; } = string.Empty;

    public string Holder { get; set; } = string.Empty;

    /// <summary>CUIT del titular, 11 dígitos.</summary>
    public string Cuit { get; set; } = string.Empty;

    /// <summary>CBU o CVU, 22 dígitos.</summary>
    public string Cbu { get; set; } = string.Empty;

    public string? Alias { get; set; }

    public Currency Currency { get; set; }

    public bool Active { get; set; } = true;

    /// <summary>Mapa de columnas del extracto de este banco, para importarlo. JSON.</summary>
    public string CsvMap { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Un CBU o CVU: 22 dígitos con sus dos dígitos verificadores (bloques de 8 y de 14).</summary>
    public static bool IsValidCbu(string? cbu)
    {
        if (cbu is not { Length: 22 } || !cbu.All(char.IsAsciiDigit))
        {
            return false;
        }

        return Check(cbu.AsSpan(0, 7), [7, 1, 3, 9, 7, 1, 3]) == cbu[7] - '0'
            && Check(cbu.AsSpan(8, 13), [3, 9, 7, 1, 3, 9, 7, 1, 3, 9, 7, 1, 3]) == cbu[21] - '0';

        static int Check(ReadOnlySpan<char> digits, int[] weights)
        {
            var sum = 0;
            for (var i = 0; i < digits.Length; i++)
            {
                sum += (digits[i] - '0') * weights[i];
            }

            return (10 - (sum % 10)) % 10;
        }
    }
}

public enum MovementStatus
{
    /// <summary>En la bandeja: sin cuenta identificada, o con sugerencia esperando al staff.</summary>
    Unmatched,

    /// <summary>Entró como pago, imputado a facturas o como saldo a favor.</summary>
    Applied,

    /// <summary>No es un cobro de un cliente, por ejemplo una transferencia entre cuentas propias.</summary>
    Ignored,
}

/// <summary>
/// Un crédito del extracto bancario. Único por cuenta de transferencias y referencia del banco, así reimportar el mismo
/// extracto no duplica. El CUIT del ordenante es la llave de la conciliación (BCRA, punto 3.2.1.2).
/// </summary>
public sealed class BankMovement
{
    public long Id { get; private set; }

    public long TransferAccountId { get; set; }

    /// <summary>Referencia unívoca del banco.</summary>
    public string Reference { get; set; } = string.Empty;

    public DateOnly Date { get; set; }

    public decimal Amount { get; set; }

    public Currency Currency { get; set; }

    public string? PayerCuit { get; set; }

    public string? PayerName { get; set; }

    public string? Concept { get; set; }

    public MovementStatus Status { get; set; }

    /// <summary>La cuenta que se identificó por el CUIT del ordenante, o la que eligió el staff.</summary>
    public long? AccountId { get; set; }

    /// <summary>Facturas cuyo saldo suma exactamente el importe: la imputación que se sugiere.</summary>
    public long[] SuggestedDocumentIds { get; set; } = [];

    public long? PaymentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum NoticeStatus
{
    Open,
    Resolved,
}

/// <summary>"Ya transferí": el aviso del cliente para el staff. No es un pago: el pago lo da el extracto.</summary>
public sealed class TransferNotice
{
    public long Id { get; private set; }

    public long AccountId { get; set; }

    public long[] DocumentIds { get; set; } = [];

    public decimal Amount { get; set; }

    public Currency Currency { get; set; }

    public DateOnly TransferredOn { get; set; }

    /// <summary>Lo que el cliente anotó: número de operación o banco de origen.</summary>
    public string? Reference { get; set; }

    /// <summary>Comprobante que adjuntó el cliente (PDF o imagen), opcional. En la base, así lo ven todos los nodos.</summary>
    public byte[]? Receipt { get; set; }

    public string? ReceiptName { get; set; }

    public string? ReceiptType { get; set; }

    public NoticeStatus Status { get; set; }

    public long? MovementId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
