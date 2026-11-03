using System.Security.Cryptography;
using System.Text.Json;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Affiliates;

public enum AffiliateStatus
{
    Pending,
    Active,
    Rejected,
    Suspended,
}

public enum CommissionKind
{
    /// <summary>El producto no paga comisión.</summary>
    None,
    Percent,
    Fixed,
}

public enum CommissionStatus
{
    Held,
    Released,
    Reversed,
}

public enum PayoutMethod
{
    /// <summary>Al saldo a favor de la cuenta del afiliado, en el momento.</summary>
    Credit,

    /// <summary>Transferencia que hace el staff y marca pagada.</summary>
    Withdrawal,
}

public enum PayoutStatus
{
    Requested,
    Paid,
    Rejected,
}

public enum LedgerKind
{
    Release,
    Reversal,
    Payout,
    PayoutReturned,
    Import,

    /// <summary>El bono de alta, una sola vez, cuando el afiliado queda activo por primera vez.</summary>
    Bonus,
}

public enum ReferralSource
{
    Order,
    Staff,
    Import,
}

public enum AttributionSource
{
    Link,
    Api,
    Import,
}

/// <summary>Secciones 5 de diseno/afiliados.md.</summary>
public static class AffiliateStates
{
    public static StateMachine<AffiliateStatus> Machine { get; } = new(
        (AffiliateStatus.Pending, AffiliateStatus.Active),
        (AffiliateStatus.Pending, AffiliateStatus.Rejected),
        (AffiliateStatus.Rejected, AffiliateStatus.Active),
        (AffiliateStatus.Active, AffiliateStatus.Suspended),
        (AffiliateStatus.Suspended, AffiliateStatus.Active));

    public static StateMachine<PayoutStatus> Payouts { get; } = new(
        (PayoutStatus.Requested, PayoutStatus.Paid),
        (PayoutStatus.Requested, PayoutStatus.Rejected));
}

/// <summary>Ajustes del programa, en una sola fila. Viene apagado.</summary>
public sealed class AffiliateProgram
{
    public const int SingletonId = 1;

    public int Id { get; private set; } = SingletonId;

    public bool Enabled { get; set; }

    public bool RequiresApproval { get; set; } = true;

    /// <summary>Días que vale un clic para atribuir un pedido.</summary>
    public int CookieDays { get; private set; } = 60;

    /// <summary>Días que una comisión queda en espera antes de liberarse.</summary>
    public int HoldDays { get; private set; } = 30;

    /// <summary>Retiro mínimo por moneda, en JSON: {"ARS": 5000}. Sin la moneda, no hay mínimo.</summary>
    public string MinimumPayouts { get; private set; } = "{}";

    /// <summary>Bono de alta por moneda, en JSON como los mínimos. Va al saldo en la moneda de la cuenta del afiliado.</summary>
    public string SignupBonuses { get; private set; } = "{}";

    /// <summary>Casilla del equipo que recibe el aviso de cada retiro pedido, por ejemplo la de facturación.</summary>
    public string? PayoutNotifyEmail { get; private set; }

    /// <summary>Material para compartir que ve el afiliado: textos o código HTML con {enlace}, separados por una línea con ---.</summary>
    public string? Links { get; private set; }

    /// <summary>Si cada afiliado activo recibe el resumen del mes anterior.</summary>
    public bool MonthlyReport { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public const string LinkPlaceholder = "{enlace}";

    public void SetDays(int cookieDays, int holdDays)
    {
        if (cookieDays is < 1 or > 365 || holdDays is < 0 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(cookieDays), "La ventana va de 1 a 365 días y la espera de 0 a 365.");
        }

        CookieDays = cookieDays;
        HoldDays = holdDays;
    }

    public IReadOnlyDictionary<string, decimal> Minimums() => Amounts(MinimumPayouts);

    public decimal MinimumPayout(Currency currency) => Minimums().GetValueOrDefault(currency.Code);

    public void SetMinimumPayouts(IReadOnlyDictionary<string, decimal> minimums) => MinimumPayouts = Amounts(minimums, nameof(minimums));

    public IReadOnlyDictionary<string, decimal> Bonuses() => Amounts(SignupBonuses);

    public decimal SignupBonus(Currency currency) => Bonuses().GetValueOrDefault(currency.Code);

    public void SetSignupBonuses(IReadOnlyDictionary<string, decimal> bonuses) => SignupBonuses = Amounts(bonuses, nameof(bonuses));

    public void SetPayoutNotifyEmail(string? email)
    {
        var trimmed = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (trimmed is not null && (trimmed.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed))
        {
            throw new ArgumentException("La casilla para avisar los retiros no es un correo válido.", nameof(email));
        }

        PayoutNotifyEmail = trimmed;
    }

    public void SetLinks(string? links)
    {
        var trimmed = string.IsNullOrWhiteSpace(links) ? null : links.Trim();
        if (trimmed is { Length: > 4000 })
        {
            throw new ArgumentException("El material para compartir tiene hasta 4000 caracteres.", nameof(links));
        }

        Links = trimmed;
    }

    /// <summary>Cada pieza del material para compartir, con el enlace del afiliado en lugar de {enlace}.</summary>
    public IReadOnlyList<string> Snippets(string link) =>
        Links is null ? [] : [.. System.Text.RegularExpressions.Regex.Split(Links, @"^\s*---\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(s => s.Trim().Replace(LinkPlaceholder, link, StringComparison.Ordinal)).Where(s => s.Length > 0)];

    private static Dictionary<string, decimal> Amounts(string json) => JsonSerializer.Deserialize<Dictionary<string, decimal>>(json) ?? [];

    /// <summary>Importes por moneda en JSON: monedas válidas, centavos y nada negativo. Los ceros no se guardan.</summary>
    private static string Amounts(IReadOnlyDictionary<string, decimal> amounts, string name)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        foreach (var (code, amount) in amounts)
        {
            _ = new Currency(code);
            Rounding.EnsureCents(amount, name);
            if (amount < 0m)
            {
                throw new ArgumentOutOfRangeException(name, amount, "El importe no puede ser negativo.");
            }
        }

        return JsonSerializer.Serialize(amounts.Where(m => m.Value > 0m).OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary());
    }
}

/// <summary>
/// Porcentaje o monto fijo, una vez o recurrente. Sin producto ni afiliado es la del programa. El monto fijo lleva
/// moneda y el porcentaje no (sección 4 de diseno/afiliados.md).
/// </summary>
public sealed class CommissionRule
{
    private CommissionRule()
    {
    }

    public CommissionRule(long? productId, long? affiliateId, CommissionKind kind, decimal value, Currency? currency, bool recurring)
    {
        ProductId = productId;
        AffiliateId = affiliateId;
        Set(kind, value, currency, recurring);
    }

    public long Id { get; private set; }

    public long? ProductId { get; private set; }

    public long? AffiliateId { get; private set; }

    public CommissionKind Kind { get; private set; }

    /// <summary>El porcentaje (5 es 5 %) o el monto fijo.</summary>
    public decimal Value { get; private set; }

    public Currency? Currency { get; private set; }

    public bool Recurring { get; private set; }

    public void Set(CommissionKind kind, decimal value, Currency? currency, bool recurring)
    {
        switch (kind)
        {
            case CommissionKind.None:
                value = 0m;
                currency = null;
                recurring = false;
                break;
            case CommissionKind.Percent when value is <= 0m or > 100m || currency is not null:
                throw new ArgumentOutOfRangeException(nameof(value), value, "El porcentaje va de más de 0 a 100 y no lleva moneda.");
            case CommissionKind.Fixed when value <= 0m || currency is null || !Rounding.HasAtMostDecimals(value):
                throw new ArgumentOutOfRangeException(nameof(value), value, "El monto fijo es positivo, con dos decimales como máximo, y lleva moneda.");
        }

        Kind = kind;
        Value = value;
        Currency = currency;
        Recurring = recurring;
    }

    /// <summary>Comisión sobre el neto de una factura y un servicio, o nula si la regla no paga en esa moneda.</summary>
    public decimal? Amount(decimal net, Currency currency) => Kind switch
    {
        CommissionKind.Percent => Rounding.Commercial(net * Value / 100m),
        CommissionKind.Fixed when Currency == currency => Value,
        _ => null,
    };
}

public static class CommissionRules
{
    /// <summary>
    /// Neto sin impuestos de cada servicio en una factura, que es la base de su comisión. Cuentan todos sus renglones
    /// menos los recargos por mora y los ajustes de redondeo de los importados: un descuento del servicio, como la
    /// promoción importada, baja la base. Un servicio que no suma nada no tiene base.
    /// </summary>
    public static Dictionary<long, decimal> Bases(IEnumerable<(long ServiceId, LineKind Kind, decimal Net)> lines) =>
        lines.Where(l => l.Kind is not (LineKind.LateFee or LineKind.RoundingAdjustment))
            .GroupBy(l => l.ServiceId)
            .Select(g => (g.Key, Net: g.Sum(l => l.Net)))
            .Where(b => b.Net > 0m)
            .ToDictionary(b => b.Key, b => b.Net);

    /// <summary>
    /// La regla que se aplica, o nula si no hay ninguna. Gana el primer nivel con una regla usable en la moneda: la del
    /// afiliado para el producto, el producto sin comisión, la del afiliado, la del producto y la del programa. Dentro
    /// de un nivel, la de la moneda gana a la que no la tiene. Un monto fijo en otra moneda no es usable (D-04).
    /// </summary>
    public static CommissionRule? Resolve(IEnumerable<CommissionRule> rules, long affiliateId, long productId, Currency currency)
    {
        var usable = rules.Where(r => r.Currency is null || r.Currency == currency).ToList();
        CommissionRule? Pick(Func<CommissionRule, bool> level) => usable.Where(level).OrderByDescending(r => r.Currency is not null).FirstOrDefault();
        return Pick(r => r.AffiliateId == affiliateId && r.ProductId == productId)
            ?? Pick(r => r.AffiliateId is null && r.ProductId == productId && r.Kind == CommissionKind.None)
            ?? Pick(r => r.AffiliateId == affiliateId && r.ProductId is null)
            ?? Pick(r => r.AffiliateId is null && r.ProductId == productId)
            ?? Pick(r => r.AffiliateId is null && r.ProductId is null);
    }

    /// <summary>
    /// Las reglas del mismo nivel que una nueva deja sin efecto: "sin comisión" y porcentaje reemplazan a todas, y un
    /// monto fijo reemplaza al de su moneda y a las que no tienen moneda. Así un nivel tiene una regla sin moneda o
    /// montos fijos, uno por moneda.
    /// </summary>
    public static IEnumerable<CommissionRule> Replaced(IEnumerable<CommissionRule> rules, long? productId, long? affiliateId, Currency? currency) =>
        rules.Where(r => r.ProductId == productId && r.AffiliateId == affiliateId && (currency is null || r.Currency is null || r.Currency == currency));
}

/// <summary>Una cuenta que se sumó al programa, con su código público.</summary>
public sealed class Affiliate
{
    private const string CodeAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private Affiliate()
    {
    }

    public Affiliate(long accountId, string code, bool requiresApproval, DateTimeOffset now)
    {
        AccountId = accountId;
        Code = code;
        Status = requiresApproval ? AffiliateStatus.Pending : AffiliateStatus.Active;
        CreatedAt = now;
        DecidedAt = requiresApproval ? null : now;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    /// <summary>El código del enlace /r/{código}. Único sin distinguir mayúsculas.</summary>
    public string Code { get; private set; } = string.Empty;

    public AffiliateStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Última decisión del staff, o el alta si no pedía aprobación.</summary>
    public DateTimeOffset? DecidedAt { get; private set; }

    public bool IsActive => Status == AffiliateStatus.Active;

    public void MoveTo(AffiliateStatus status, DateTimeOffset now)
    {
        Status = AffiliateStates.Machine.Move(Status, status);
        DecidedAt = now;
    }

    /// <summary>Ocho caracteres sin los que se confunden al leerlos (0, o, 1, l, i).</summary>
    public static string NewCode() => RandomNumberGenerator.GetString(CodeAlphabet, 8);

    /// <summary>Afiliado del sistema anterior (D-17): activo, con su ID y su código.</summary>
    public static Affiliate FromImport(long id, long accountId, string code, DateTimeOffset createdAt) =>
        new(accountId, code, requiresApproval: false, createdAt) { Id = id };
}

/// <summary>Una visita por el enlace. Sin IP ni datos de la persona.</summary>
public sealed class AffiliateClick
{
    private AffiliateClick()
    {
    }

    public AffiliateClick(long affiliateId, DateTimeOffset at, string? referrerHost)
    {
        AffiliateId = affiliateId;
        At = at;
        ReferrerHost = referrerHost is { Length: > 255 } ? referrerHost[..255] : referrerHost;
    }

    public long Id { get; private set; }

    public long AffiliateId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public string? ReferrerHost { get; private set; }
}

/// <summary>Qué afiliado trajo a una cuenta y desde cuándo corre la ventana.</summary>
public sealed class AffiliateAttribution
{
    private AffiliateAttribution()
    {
    }

    public AffiliateAttribution(long accountId, long affiliateId, DateTimeOffset clickedAt, AttributionSource source, DateTimeOffset now)
    {
        AccountId = accountId;
        Set(affiliateId, clickedAt, source, now);
    }

    public long AccountId { get; private set; }

    public long AffiliateId { get; private set; }

    public DateTimeOffset ClickedAt { get; private set; }

    public AttributionSource Source { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>El pedido se hizo dentro de la ventana del clic.</summary>
    public bool Covers(DateTimeOffset at, int windowDays) => at >= ClickedAt && at <= ClickedAt.AddDays(windowDays);

    /// <summary>
    /// Un clic nuevo reemplaza la atribución si es del mismo afiliado (renueva la ventana) o si la vigente ya venció.
    /// Mientras corre la ventana de otro afiliado, gana el primer clic.
    /// </summary>
    public bool TakesClick(long affiliateId, DateTimeOffset clickedAt, int windowDays) =>
        affiliateId == AffiliateId ? clickedAt > ClickedAt : clickedAt > ClickedAt.AddDays(windowDays);

    public void Set(long affiliateId, DateTimeOffset clickedAt, AttributionSource source, DateTimeOffset now)
    {
        AffiliateId = affiliateId;
        ClickedAt = clickedAt;
        Source = source;
        UpdatedAt = now;
    }
}

/// <summary>Un servicio que llegó por un afiliado.</summary>
public sealed class AffiliateReferral
{
    private AffiliateReferral()
    {
    }

    public AffiliateReferral(long affiliateId, long accountId, long serviceId, long? orderId, ReferralSource source, DateTimeOffset now, DateOnly? paidBefore = null)
    {
        AffiliateId = affiliateId;
        AccountId = accountId;
        ServiceId = serviceId;
        OrderId = orderId;
        Source = source;
        CreatedAt = now;
        PaidBefore = paidBefore;
    }

    public long Id { get; private set; }

    public long AffiliateId { get; private set; }

    public long AccountId { get; private set; }

    public long ServiceId { get; private set; }

    public long? OrderId { get; private set; }

    public ReferralSource Source { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Última comisión que pagó el sistema anterior, en los importados. Una regla de una sola vez ya se cumplió.</summary>
    public DateOnly? PaidBefore { get; private set; }

    public void SetImportedId(long id) => Id = id;
}

/// <summary>Lo ganado por una factura y un servicio referido (sección 3 de diseno/afiliados.md).</summary>
public sealed class AffiliateCommission
{
    private AffiliateCommission()
    {
    }

    public AffiliateCommission(long affiliateId, long referralId, long serviceId, long? documentId, Currency currency, decimal netBase, CommissionRule rule, decimal amount, DateOnly releaseOn, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Rounding.EnsureCents(amount, nameof(amount));
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Una comisión es positiva.");
        }

        AffiliateId = affiliateId;
        ReferralId = referralId;
        ServiceId = serviceId;
        DocumentId = documentId;
        Currency = currency;
        Base = netBase;
        Kind = rule.Kind;
        Rate = rule.Value;
        Recurring = rule.Recurring;
        Amount = amount;
        ReleaseOn = releaseOn;
        CreatedAt = now;
        Status = CommissionStatus.Held;
    }

    public long Id { get; private set; }

    public long AffiliateId { get; private set; }

    public long ReferralId { get; private set; }

    public long ServiceId { get; private set; }

    /// <summary>La factura que la generó. Nula en algunas importadas.</summary>
    public long? DocumentId { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>Neto sin impuestos de los renglones del servicio en la factura.</summary>
    public decimal Base { get; private set; }

    public CommissionKind Kind { get; private set; }

    public decimal Rate { get; private set; }

    public bool Recurring { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>Lo revertido por notas, reembolsos o contracargos. Nunca vuelve.</summary>
    public decimal Reversed { get; private set; }

    public CommissionStatus Status { get; private set; }

    public DateOnly ReleaseOn { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public DateTimeOffset? ReversedAt { get; private set; }

    public bool Imported { get; private set; }

    public decimal Remaining => Amount - Reversed;

    /// <summary>
    /// Lo que se debe de una comisión según su factura: nada si no está paga por completo, y la parte no acreditada si
    /// una nota parcial la dejó paga.
    /// </summary>
    public static decimal Target(decimal amount, decimal total, decimal credited, decimal pending) =>
        pending > 0m || total <= 0m || total - credited <= 0m ? 0m : Rounding.Commercial(amount * (total - credited) / total);

    /// <summary>Baja lo vigente hasta <paramref name="target"/> y devuelve cuánto revirtió. Nunca sube.</summary>
    public decimal ReduceTo(decimal target, DateTimeOffset now)
    {
        if (Status == CommissionStatus.Reversed || target >= Remaining)
        {
            return 0m;
        }

        var delta = Remaining - Math.Max(target, 0m);
        Reversed += delta;
        if (Remaining == 0m)
        {
            Status = CommissionStatus.Reversed;
            ReversedAt = now;
        }

        return delta;
    }

    /// <summary>Libera lo que queda y devuelve el importe para el saldo. Sin nada que liberar, queda revertida.</summary>
    public decimal Release(DateTimeOffset now)
    {
        if (Status != CommissionStatus.Held)
        {
            throw new InvalidOperationException($"La comisión {Id} no está en espera.");
        }

        if (Remaining == 0m)
        {
            Status = CommissionStatus.Reversed;
            ReversedAt = now;
            return 0m;
        }

        Status = CommissionStatus.Released;
        ReleasedAt = now;
        return Remaining;
    }

    /// <summary>Comisión del sistema anterior: liberada, o en espera hasta su fecha si no se había liberado.</summary>
    public static AffiliateCommission FromImport(long affiliateId, long referralId, long serviceId, long? documentId, Currency currency, decimal amount, DateOnly date, bool held, DateTimeOffset at) =>
        new(affiliateId, referralId, serviceId, documentId, currency, 0m, new CommissionRule(null, null, CommissionKind.Fixed, amount, currency, recurring: true), amount, date, at)
        {
            Status = held ? CommissionStatus.Held : CommissionStatus.Released,
            ReleasedAt = held ? null : at,
            Imported = true,
        };
}

/// <summary>Libro del saldo del afiliado por moneda. Solo altas: se compensa, nunca se edita.</summary>
public sealed class AffiliateLedgerEntry
{
    private AffiliateLedgerEntry()
    {
    }

    public AffiliateLedgerEntry(long affiliateId, Currency currency, decimal amount, LedgerKind kind, DateTimeOffset now, long? commissionId = null, long? payoutId = null)
    {
        Rounding.EnsureCents(amount, nameof(amount));
        if (amount == 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Un movimiento no puede ser cero.");
        }

        AffiliateId = affiliateId;
        Currency = currency;
        Amount = amount;
        Kind = kind;
        CreatedAt = now;
        CommissionId = commissionId;
        PayoutId = payoutId;
    }

    public long Id { get; private set; }

    public long AffiliateId { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>Positivo suma al saldo, negativo resta.</summary>
    public decimal Amount { get; private set; }

    public LedgerKind Kind { get; private set; }

    public long? CommissionId { get; private set; }

    public long? PayoutId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Un cobro del afiliado: saldo a favor, que nace pagado, o retiro, que paga el staff.</summary>
public sealed class AffiliatePayout
{
    private AffiliatePayout()
    {
    }

    public AffiliatePayout(long affiliateId, PayoutMethod method, Currency currency, decimal amount, string? details, DateTimeOffset now)
    {
        Rounding.EnsureCents(amount, nameof(amount));
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Un pago es positivo.");
        }

        AffiliateId = affiliateId;
        Method = method;
        Currency = currency;
        Amount = amount;
        Details = details is { Length: > 500 } ? details[..500] : details;
        RequestedAt = now;
        Status = method == PayoutMethod.Credit ? PayoutStatus.Paid : PayoutStatus.Requested;
        DecidedAt = method == PayoutMethod.Credit ? now : null;
    }

    public long Id { get; private set; }

    public long AffiliateId { get; private set; }

    public PayoutMethod Method { get; private set; }

    public Currency Currency { get; private set; }

    public decimal Amount { get; private set; }

    public PayoutStatus Status { get; private set; }

    /// <summary>Dónde quiere cobrar el afiliado un retiro. Lo ve solo el staff.</summary>
    public string? Details { get; private set; }

    /// <summary>Referencia de la transferencia que cargó el staff.</summary>
    public string? Reference { get; private set; }

    /// <summary>Motivo de un rechazo.</summary>
    public string? Reason { get; private set; }

    /// <summary>Movimiento del libro de saldo a favor de la cuenta, en un pago a saldo a favor.</summary>
    public long? CreditEntryId { get; set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Marca pagado un retiro pedido. Devuelve false si ya estaba pagado: repetirlo no cambia nada.</summary>
    public bool MarkPaid(string reference, DateTimeOffset now)
    {
        if (Status == PayoutStatus.Paid)
        {
            return false;
        }

        Status = AffiliateStates.Payouts.Move(Status, PayoutStatus.Paid);
        Reference = reference is { Length: > 200 } ? reference[..200] : reference;
        DecidedAt = now;
        return true;
    }

    /// <summary>Rechaza un retiro pedido. Devuelve false si ya estaba rechazado: el importe vuelve una sola vez.</summary>
    public bool Reject(string reason, DateTimeOffset now)
    {
        if (Status == PayoutStatus.Rejected)
        {
            return false;
        }

        Status = AffiliateStates.Payouts.Move(Status, PayoutStatus.Rejected);
        Reason = reason is { Length: > 500 } ? reason[..500] : reason;
        DecidedAt = now;
        return true;
    }

    /// <summary>Retiro del sistema anterior: pagado en su fecha.</summary>
    public static AffiliatePayout FromImport(long affiliateId, Currency currency, decimal amount, DateTimeOffset at) =>
        new(affiliateId, PayoutMethod.Withdrawal, currency, amount, null, at) { Status = PayoutStatus.Paid, DecidedAt = at };
}
