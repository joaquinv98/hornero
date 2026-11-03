namespace BillingOss.App;

/// <summary>Ajustes de los cobros incluidos (sección Payments). Ver docs/cobros/README.md.</summary>
public sealed class PaymentsOptions
{
    /// <summary>
    /// Débito automático por suscripción de la pasarela (Mercado Pago). Apagado (por defecto) no se ofrece en el portal
    /// y los cobros de suscripciones no se registran: así un staging con credenciales reales no registra cobros de
    /// verdad. Se prende en el corte.
    /// </summary>
    public bool SubscriptionsEnabled { get; set; }

    public AutoChargeOptions AutoCharge { get; set; } = new();

    /// <summary>Una transferencia con una sola coincidencia exacta de CUIT e importe se imputa sola.</summary>
    public bool TransfersAutoApply { get; set; }

    /// <summary>Monto del pago de prueba del asistente de cobros, en la moneda que se elija.</summary>
    public decimal TestPaymentAmount { get; set; } = 1m;
}

/// <summary>Política del débito automático con medio guardado (sección 4.5 del diseño de cobros).</summary>
public sealed class AutoChargeOptions
{
    private static readonly int[] DefaultDays = [0, 1, 3, 5];

    /// <summary>
    /// Días de cada intento respecto del vencimiento de la factura: negativos antes, 0 el mismo día. Vacío, el día del
    /// vencimiento y a los 1, 3 y 5 días. Con gracia corta conviene antes del vencimiento, por ejemplo -3, -2, -1 y 0.
    /// </summary>
    public int[]? Days { get; set; }

    /// <summary>Hora del día, en la zona del proveedor, en que corre el débito.</summary>
    public int Hour { get; set; } = 10;

    /// <summary>Tope por moneda: una factura con más saldo no se debita sola. Sin la moneda, sin tope.</summary>
    public Dictionary<string, decimal> MaxAmount { get; set; } = [];

    public IReadOnlyList<int> EffectiveDays => Days is { Length: > 0 } days ? [.. days.Distinct().Order()] : DefaultDays;
}
