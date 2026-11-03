using BillingOss.Domain.Catalog;

namespace BillingOss.App;

/// <summary>
/// Ajustes de facturación del proveedor. El modo de carga de los precios no está acá: va en cada precio y en cada
/// servicio, con un valor por defecto por moneda en los ajustes impositivos de la base (<see cref="Domain.Catalog.TaxSettings"/>).
/// </summary>
public sealed class BillingOptions
{
    /// <summary>Zona horaria de las fechas de negocio (sección 13 del modelo de dominio).</summary>
    public string TimeZone { get; set; } = "America/Argentina/Buenos_Aires";

    /// <summary>Días para pagar una factura nueva.</summary>
    public int InvoiceDueDays { get; set; } = 7;

    /// <summary>Cuántos días antes del vencimiento se factura la renovación.</summary>
    public int RenewalLeadDays { get; set; } = 7;

    /// <summary>Anticipación propia por largo del ciclo en meses, por ejemplo {"12": 30} para los anuales.</summary>
    public Dictionary<int, int> RenewalLeadDaysByMonths { get; set; } = [];

    /// <summary>
    /// Si se factura el período siguiente aunque haya períodos facturados sin pagar. Apagado (por defecto), un
    /// servicio impago no acumula facturas: espera el pago o la baja.
    /// </summary>
    public bool ContinuousInvoicing { get; set; }

    /// <summary>Días de gracia después de lo pagado antes de suspender.</summary>
    public int GraceDays { get; set; } = 3;

    /// <summary>Días de mora hasta la baja. Vacío, nunca. Un producto puede tener su propio plazo.</summary>
    public int? TerminationDays { get; set; }

    /// <summary>Días después del vencimiento en que una factura impaga se anula con una nota de crédito. Vacío, nunca.</summary>
    public int? CancelUnpaidAfterDays { get; set; }

    /// <summary>Cuántos días antes del vencimiento se factura la renovación de un dominio. Es aparte de la de los servicios anuales.</summary>
    public int DomainRenewalLeadDays { get; set; } = 7;

    /// <summary>
    /// Días después del vencimiento en que un dominio sin renovación paga pasa de gracia a vencido. Pasado ese plazo
    /// tampoco se factura la renovación. NIC Argentina libera los .ar a los 45.
    /// </summary>
    public int DomainGraceDays { get; set; } = 45;

    /// <summary>Nameservers de los dominios nuevos cuando el pedido no trae otros.</summary>
    public List<string> DomainNameservers { get; set; } = [];

    /// <summary>
    /// Días antes del vencimiento en que se avisa de un dominio que no se va a renovar solo. Vacío, 30, 7 y 1. Va
    /// vacío por defecto porque la configuración suma a una lista, no la reemplaza.
    /// </summary>
    public List<int> DomainExpiryNoticeDays { get; set; } = [];

    /// <summary>Días después del vencimiento en que se avisa de un dominio en gracia. Vacío, 1 y 30.</summary>
    public List<int> DomainGraceNoticeDays { get; set; } = [];

    public IReadOnlyList<int> ExpiryNoticeDays => DomainExpiryNoticeDays.Count > 0 ? DomainExpiryNoticeDays : [30, 7, 1];

    public IReadOnlyList<int> GraceNoticeDays => DomainGraceNoticeDays.Count > 0 ? DomainGraceNoticeDays : [1, 30];

    /// <summary>Consulta de disponibilidad y de vencimientos por RDAP, con el DNS como alternativa.</summary>
    public DomainLookupOptions DomainLookup { get; set; } = new();

    /// <summary>Receptor por defecto si la cuenta no tiene identidad fiscal: consumidor final en Argentina.</summary>
    public int DefaultVatCondition { get; set; } = 5;

    public string DefaultCountry { get; set; } = "AR";

    /// <summary>URL pública del sistema, para las páginas de retorno y los avisos de las pasarelas.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Cotizaciones para cobrar y facturar en otra moneda (sección 6.4 del modelo de dominio).</summary>
    public ExchangeRateOptions ExchangeRates { get; set; } = new();

    /// <summary>
    /// Días después del vencimiento en que una factura impaga recibe el recargo por mora, en una nota de débito. Vacío,
    /// nunca (por defecto).
    /// </summary>
    public int? LateFeeAfterDays { get; set; }

    /// <summary>Porcentaje del saldo impago que se recarga, por ejemplo 5.</summary>
    public decimal LateFeePercent { get; set; }

    /// <summary>Monto fijo por moneda que se suma al porcentaje, por ejemplo {"ARS": 500}.</summary>
    public Dictionary<string, decimal> LateFeeFixed { get; set; } = [];

    /// <summary>Días que vale un presupuesto si no se dice otra cosa.</summary>
    public int QuoteValidDays { get; set; } = 30;

    /// <summary>
    /// Carga de saldo a favor desde el portal: mínimo y máximo de cada carga por moneda, por ejemplo
    /// {"ARS": {"Min": 1000, "Max": 1000000}}. Una moneda sin límites no se carga, y sin ninguna el portal no la ofrece.
    /// </summary>
    public Dictionary<string, TopUpLimits> TopUp { get; set; } = [];

    public int RenewalLeadDaysFor(BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return RenewalLeadDaysByMonths.TryGetValue(cycle.Months, out var days) ? days : RenewalLeadDays;
    }

    public DateOnly Today(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
    }
}

/// <summary>Límites de una carga de saldo en una moneda.</summary>
public sealed class TopUpLimits
{
    public decimal Min { get; set; }

    public decimal Max { get; set; }
}

/// <summary>Cotizaciones: de dónde salen y cuánto valen (sección 6.4 del modelo de dominio).</summary>
public sealed class ExchangeRateOptions
{
    /// <summary>Moneda local, en la que se expresan las cotizaciones.</summary>
    public string Local { get; set; } = "ARS";

    /// <summary>Trae las del Banco Nación una vez por hora. Apagado por defecto: cada proveedor elige su fuente.</summary>
    public bool Bna { get; set; }

    /// <summary>Tabla del Banco Nación: "divisas" (la de la RG 5616) o "billetes".</summary>
    public string BnaSeries { get; set; } = "divisas";

    /// <summary>Monedas que se traen.</summary>
    public string[] Currencies { get; set; } = ["USD"];

    /// <summary>Una cotización más vieja que esto no se usa y la conversión falla a la vista. Cubre los fines de semana largos.</summary>
    public int MaxAgeDays { get; set; } = 5;

    /// <summary>
    /// Precios en dólares para cuentas en pesos. Prendido, un plan sin precio en la moneda de la cuenta se vende con su
    /// precio en una de <see cref="Currencies"/>: el servicio queda pactado en esa moneda y cada factura, la del pedido y
    /// las de renovación, sale en la de la cuenta con la cotización de ese día. Apagado (por defecto), ese pedido falla.
    /// </summary>
    public bool IndexPrices { get; set; }
}

/// <summary>
/// RDAP y DNS para saber si un nombre está registrado y cuándo vence. El RDAP de NIC Argentina falla seguido (500 o
/// sin respuesta durante horas), así que cada consulta se reintenta y, si no contesta, se pregunta al DNS.
/// </summary>
public sealed class DomainLookupOptions
{
    /// <summary>Apagado, ningún nombre se consulta afuera: la disponibilidad queda en manos del registrar o del staff.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lista de servidores RDAP por TLD que publica IANA (RFC 9224).</summary>
    public string BootstrapUrl { get; set; } = "https://data.iana.org/rdap/dns.json";

    /// <summary>Servidor RDAP por sufijo, por encima de la lista de IANA. Por ejemplo {"ar": "https://rdap.nic.ar/"}.</summary>
    public Dictionary<string, string> Servers { get; set; } = [];

    /// <summary>Intentos por consulta. Un 500, un 429 o un corte se reintentan con espera creciente.</summary>
    public int Attempts { get; set; } = 3;

    /// <summary>Primera espera entre intentos. Se duplica en cada uno.</summary>
    public int RetryDelayMilliseconds { get; set; } = 400;

    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Presupuesto total, en segundos, de las consultas al registro de una búsqueda o un pedido: van en paralelo y lo que no
    /// llega queda sin confirmar. Los reintentos largos quedan para la sincronización diaria.
    /// </summary>
    public int SearchBudgetSeconds { get; set; } = 8;

    /// <summary>Resolvedores para la consulta de NS, como "1.1.1.1" o "127.0.0.1:5353". Vacío, los del sistema.</summary>
    public List<string> DnsResolvers { get; set; } = [];
}
