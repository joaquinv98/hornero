using System.Text.Json;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Domains;

public enum DomainStatus
{
    Pending,
    Active,

    /// <summary>Venció sin renovación paga: todavía se puede renovar.</summary>
    Grace,
    Expired,
    Cancelled,
}

/// <summary>
/// Dominio de un cliente (Domain en la sección 9 del modelo de dominio). Se factura por su cuenta, sin producto:
/// guarda el precio congelado de la renovación y hasta dónde está pago, como un servicio anual.
/// </summary>
public sealed class RegisteredDomain
{
    private RegisteredDomain()
    {
    }

    public RegisteredDomain(long accountId, string name, long registrarInstanceId, Currency currency, decimal recurringAmount, DateTimeOffset now, int years = 1)
    {
        AccountId = accountId;
        Name = name.Trim().ToLowerInvariant();
        RegistrarInstanceId = registrarInstanceId;
        Currency = currency;
        RecurringAmount = recurringAmount;
        CreatedAt = now;
        Years = Math.Clamp(years, 1, 10);
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Instancia del registrar que lo atiende. Los TLD sin API van a una instancia del registrar manual.</summary>
    public long RegistrarInstanceId { get; set; }

    public DomainStatus Status { get; set; }

    public DateOnly? RegisteredOn { get; set; }

    /// <summary>Vencimiento en el registro, si se conoce. Lo actualiza el registrar o el staff.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Años de cada renovación.</summary>
    public int Years { get; private set; } = 1;

    public Currency Currency { get; private set; }

    /// <summary>Precio congelado de la renovación, en la moneda de la cuenta. Cero renueva sin factura.</summary>
    public decimal RecurringAmount { get; set; }

    /// <summary>Si ese precio es con impuesto incluido o sin impuesto, congelado con él como en un servicio (sección 3.4 del diseño fiscal).</summary>
    public PriceMode PriceMode { get; set; } = PriceMode.TaxIncluded;

    public bool AutoRenew { get; set; } = true;

    /// <summary>
    /// Gestión de DNS contratada (<see cref="DomainOptions.Dns"/>). Con cargo, su precio va dentro de la renovación. En
    /// los importados es la marca del sistema anterior, con el precio ya incluido en el importado.
    /// </summary>
    public bool DnsManagement { get; set; }

    /// <summary>Reenvío de correo contratado (<see cref="DomainOptions.EmailForwarding"/>), como la gestión de DNS.</summary>
    public bool EmailForwarding { get; set; }

    /// <summary>Protección de identidad prendida en el registro, según la última respuesta del registrar.</summary>
    public bool IdProtection { get; set; }

    /// <summary>
    /// Precio anual congelado de cada opción contratada con cargo, por su nombre en <see cref="DomainOptions"/>. Ya está
    /// sumado en <see cref="RecurringAmount"/>: se guarda aparte para restarlo si se da de baja. JSON.
    /// </summary>
    public string OptionPrices { get; private set; } = "{}";

    /// <summary>Nameservers conocidos. JSON.</summary>
    public string Nameservers { get; set; } = "[]";

    /// <summary>IDs que devolvió el registrar. JSON.</summary>
    public string ExternalIds { get; set; } = "{}";

    public string? PaymentMethod { get; set; }

    /// <summary>Bloqueo de transferencia según la última respuesta del registrar. Nulo si no se sabe.</summary>
    public bool? Locked { get; set; }

    /// <summary>
    /// Estado en el registro según la última sincronización (sección 9.2 del protocolo): active, pending,
    /// pending_transfer, expired, redemption, transferred_away, not_found o unknown. Es aparte del estado de facturación.
    /// </summary>
    public string? RegistryStatus { get; set; }

    /// <summary>Última sincronización con el registro.</summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>Código EPP cifrado (D-09): el que trae el cliente para transferir o el que dio el registrar.</summary>
    public byte[]? AuthCode { get; set; }

    /// <summary>Titular propio del dominio, si no es el que sale de la cuenta. JSON con los campos de contacts.registrant.</summary>
    public string? Registrant { get; set; }

    /// <summary>Inicio del próximo período a facturar. Nulo si no renueva.</summary>
    public DateOnly? NextDueDate { get; private set; }

    /// <summary>Hasta dónde está pago: el vencimiento según la facturación. Pasado este día entra en gracia.</summary>
    public DateOnly? PaidThrough { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsLive => Status is DomainStatus.Pending or DomainStatus.Active or DomainStatus.Grace;

    /// <summary>Período que sigue en facturarse y avance de la fecha.</summary>
    public Period TakeNextPeriod()
    {
        var start = NextDueDate ?? throw new InvalidOperationException($"El dominio {Id} no renueva.");
        var period = new Period(start, start.AddYears(Years));
        NextDueDate = period.End;
        return period;
    }

    /// <summary>
    /// Se pagó una renovación: lo pago avanza un período, sin pasar lo facturado, y un dominio en gracia vuelve a
    /// activo. Devuelve el vencimiento anterior, que es el que hay que renovar en el registrar, o nulo si no avanzó.
    /// </summary>
    public DateOnly? RenewalPaid(DateOnly today)
    {
        if (PaidThrough is not { } paid || NextDueDate is not { } next || paid >= next)
        {
            return null;
        }

        PaidThrough = paid.AddYears(Years);
        if (Status == DomainStatus.Grace && PaidThrough >= today)
        {
            Status = DomainStatus.Active;
        }

        return paid;
    }

    /// <summary>
    /// El registrar renovó el dominio. Uno vencido en el núcleo que pagó tarde recién vuelve a activo acá, porque hasta
    /// que el registro acepta la renovación no se sabe si todavía se podía: lo pago ya avanzó con el pago.
    /// </summary>
    public void Renewed(DateOnly today)
    {
        if (Status is DomainStatus.Grace or DomainStatus.Expired && PaidThrough >= today)
        {
            Status = DomainStatus.Active;
        }
    }

    /// <summary>
    /// Se anuló con una nota de crédito la factura del período que empieza en <paramref name="start"/>: vuelve a estar
    /// por facturar. Si es el último facturado, el próximo a facturar retrocede a su inicio, y si además estaba pago (la
    /// nota devuelve la plata) lo pago también; lo que el registrar ya hizo queda como está. Un período anterior a otro
    /// ya facturado no se toca: lo decide el staff. Devuelve si cambió algo.
    /// </summary>
    public bool RenewalCancelled(DateOnly start)
    {
        var end = start.AddYears(Years);
        if (NextDueDate != end)
        {
            return false;
        }

        NextDueDate = start;
        if (PaidThrough == end)
        {
            PaidThrough = start;
        }

        return true;
    }

    public void SetImportedId(long id) => Id = id;

    /// <summary>Estado traído del sistema anterior (D-17). Solo los vivos renuevan.</summary>
    public void ImportState(DomainStatus status, int years, DateOnly? nextDueDate, DateOnly? paidThrough)
    {
        Status = status;
        Years = Math.Max(1, years);
        NextDueDate = status is DomainStatus.Active or DomainStatus.Grace ? nextDueDate : null;
        PaidThrough = NextDueDate is null ? null : paidThrough ?? NextDueDate;
    }

    /// <summary>Alta en el núcleo de un dominio ya registrado, con su vencimiento como primer período a facturar.</summary>
    public void Start(DateOnly expiresOn)
    {
        Status = DomainStatus.Active;
        ExpiresOn = expiresOn;
        NextDueDate = expiresOn;
        PaidThrough = expiresOn;
    }

    /// <summary>
    /// Deja de renovarse y de facturarse: se fue a otro registrar o se canceló su pedido. Lo pago queda como estaba.
    /// </summary>
    public void Close()
    {
        Status = DomainStatus.Cancelled;
        AutoRenew = false;
        NextDueDate = null;
    }

    /// <summary>
    /// Si tiene la opción: la gestión de DNS y el reenvío por su marca, y la protección si está contratada con cargo o
    /// prendida en el registro.
    /// </summary>
    public bool HasOption(string option) => option switch
    {
        DomainOptions.Dns => DnsManagement,
        DomainOptions.EmailForwarding => EmailForwarding,
        DomainOptions.Privacy => IdProtection || PaidOptions().ContainsKey(option),
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "No es una opción de dominio."),
    };

    /// <summary>Si la opción está contratada con cargo, con su precio anual congelado.</summary>
    public bool HasPaidOption(string option) => PaidOptions().ContainsKey(option);

    /// <summary>
    /// Contrata la opción con su precio anual: queda prendida y su precio por los años de cada renovación entra en la
    /// renovación. Contratarla de nuevo no cambia nada. La protección la prende el registrar: acá solo queda contratada.
    /// </summary>
    public void ContractOption(string option, decimal yearlyPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(yearlyPrice);
        _ = HasOption(option);
        var prices = PaidOptions();
        if (prices.ContainsKey(option))
        {
            return;
        }

        if (yearlyPrice > 0m)
        {
            prices[option] = yearlyPrice;
            OptionPrices = JsonSerializer.Serialize(prices);
            RecurringAmount += yearlyPrice * Years;
        }

        DnsManagement |= option == DomainOptions.Dns;
        EmailForwarding |= option == DomainOptions.EmailForwarding;
    }

    /// <summary>
    /// Da de baja la opción: se apaga y su precio sale de la renovación. Sin precio guardado, como en un importado que lo
    /// trae dentro del suyo, la renovación queda como estaba. La protección la apaga el registrar.
    /// </summary>
    public void DropOption(string option)
    {
        _ = HasOption(option);
        var prices = PaidOptions();
        if (prices.Remove(option, out var price))
        {
            OptionPrices = JsonSerializer.Serialize(prices);
            RecurringAmount = Math.Max(0m, RecurringAmount - (price * Years));
        }

        DnsManagement &= option != DomainOptions.Dns;
        EmailForwarding &= option != DomainOptions.EmailForwarding;
    }

    private Dictionary<string, decimal> PaidOptions() =>
        JsonSerializer.Deserialize<Dictionary<string, decimal>>(OptionPrices) ?? [];
}

/// <summary>
/// Opciones del dominio que pueden tener precio (sección 9 del modelo de dominio): la gestión de DNS, el reenvío de
/// correo y la protección de identidad. Cada una se ofrece si el registrar declara su capacidad. Sin precio vigente es
/// gratis y está siempre; con precio se contrata, y su precio anual queda congelado en el dominio.
/// </summary>
public static class DomainOptions
{
    public const string Dns = "dns";

    public const string EmailForwarding = "email_forwarding";

    public const string Privacy = "privacy";

    public static IReadOnlyList<string> All { get; } = [Dns, EmailForwarding, Privacy];

    /// <summary>La capacidad del registrar que la ofrece (sección 9.2 del protocolo).</summary>
    public static string Capability(string option) => option switch
    {
        Dns => "registrar.dns",
        EmailForwarding => "registrar.email_forwarding",
        Privacy => "registrar.privacy",
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "No es una opción de dominio."),
    };
}

/// <summary>
/// Precio anual de una opción del dominio en una moneda, con vigencia como los precios por TLD: un cambio es una fila
/// nueva y los dominios que ya la tienen conservan el suyo. Sin fila vigente la opción es gratis.
/// </summary>
public sealed class DomainOptionPrice
{
    private DomainOptionPrice()
    {
    }

    public DomainOptionPrice(string option, Currency currency, DateOnly validFrom)
    {
        _ = DomainOptions.Capability(option);
        Option = option;
        Currency = currency;
        ValidFrom = validFrom;
    }

    public long Id { get; private set; }

    /// <summary>Nombre de la opción en <see cref="DomainOptions"/>.</summary>
    public string Option { get; private set; } = string.Empty;

    public Currency Currency { get; private set; }

    /// <summary>Por año. Cero es gratis.</summary>
    public decimal Price { get; set; }

    public DateOnly ValidFrom { get; private set; }
}

/// <summary>
/// Las opciones del dominio que se ofrecen en una extensión, por ejemplo sin la protección de identidad donde el
/// registro no la permite. Sin fila se ofrecen todas, y cada una sale además solo si su registrar declara la capacidad.
/// </summary>
public sealed class TldOptions
{
    private TldOptions()
    {
    }

    public TldOptions(string tld) => Tld = tld;

    /// <summary>Con el punto inicial, como en <see cref="TldPrice"/>.</summary>
    public string Tld { get; private set; } = string.Empty;

    public bool Dns { get; set; } = true;

    public bool EmailForwarding { get; set; } = true;

    public bool Privacy { get; set; } = true;

    public bool Offers(string option) => option switch
    {
        DomainOptions.Dns => Dns,
        DomainOptions.EmailForwarding => EmailForwarding,
        DomainOptions.Privacy => Privacy,
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "No es una opción de dominio."),
    };
}

public enum DomainOperationStatus
{
    Pending,
    Completed,
    Failed,
}

/// <summary>
/// Registro, renovación, transferencia o cambio de nameservers, como trabajo idempotente (sección 9 del modelo de
/// dominio). Cada fila es una intención: su ID va en la clave de idempotencia, y la clave de deduplicación evita
/// pedir dos veces la misma renovación.
/// </summary>
public sealed class DomainOperation
{
    private DomainOperation()
    {
    }

    public DomainOperation(long domainId, string operation, string input, string dedupKey, DateTimeOffset now)
    {
        DomainId = domainId;
        Operation = operation;
        Input = input;
        DedupKey = dedupKey;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    public long DomainId { get; private set; }

    /// <summary>Operación del registrar: register, renew, transfer o set_nameservers.</summary>
    public string Operation { get; private set; } = string.Empty;

    /// <summary>input del sobre. JSON.</summary>
    public string Input { get; private set; } = "{}";

    public string DedupKey { get; private set; } = string.Empty;

    public DomainOperationStatus Status { get; private set; }

    /// <summary>Operación larga en curso en el módulo.</summary>
    public string? PendingOperation { get; set; }

    /// <summary>result de la respuesta. JSON.</summary>
    public string? Result { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public void Complete(string result, DateTimeOffset now)
    {
        Status = DomainOperationStatus.Completed;
        Result = result;
        PendingOperation = null;
        FinishedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = DomainOperationStatus.Failed;
        LastError = error.Length > 2000 ? error[..2000] : error;
        PendingOperation = null;
        FinishedAt = now;
    }
}

/// <summary>
/// Precio de un TLD por instancia de registrar, moneda y años, con vigencia (sección 9 del modelo de dominio). Nulo
/// es que esa operación no se ofrece a esos años.
/// </summary>
public sealed class TldPrice
{
    private TldPrice()
    {
    }

    public TldPrice(string tld, long registrarInstanceId, Currency currency, int years, DateOnly validFrom)
    {
        Tld = tld;
        RegistrarInstanceId = registrarInstanceId;
        Currency = currency;
        Years = years;
        ValidFrom = validFrom;
    }

    public long Id { get; private set; }

    /// <summary>Con el punto inicial, por ejemplo ".com.ar".</summary>
    public string Tld { get; private set; } = string.Empty;

    public long RegistrarInstanceId { get; private set; }

    public Currency Currency { get; private set; }

    public int Years { get; private set; }

    public decimal? Register { get; set; }

    public decimal? Renew { get; set; }

    public decimal? Transfer { get; set; }

    public DateOnly ValidFrom { get; private set; }
}
