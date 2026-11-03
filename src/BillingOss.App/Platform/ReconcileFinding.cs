namespace BillingOss.App.Platform;

/// <summary>
/// Hallazgo de la conciliación después de una promoción o una restauración (D-14, punto 7 de la revisión de alta
/// disponibilidad): un comprobante que el fisco tiene y la base no, un número con dos candidatos, un salto de la
/// numeración comercial, un recurso con marca del núcleo sin servicio. Queda abierto hasta que el staff lo resuelve.
/// </summary>
public sealed class ReconcileFinding
{
    public const string FiscalOrphan = "fiscal.orphan";
    public const string FiscalAmbiguous = "fiscal.ambiguous";
    public const string CounterGap = "counter.gap";
    public const string ProvisioningOrphan = "provisioning.orphan";
    public const string ProvisioningUnlisted = "provisioning.unlisted";

    public long Id { get; private set; }

    public string Kind { get; set; } = string.Empty;

    /// <summary>Sobre qué: "3-6-1200" (punto de venta, tipo y número), "Invoice" o "instance:7".</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Lo que hace falta para resolverlo, sin datos personales. JSON.</summary>
    public string Details { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// La línea de tiempo de PostgreSQL que vio por última vez el planificador, con la hora. Cambia con cada promoción y
/// con cada restauración: cuando el planificador ve otra, encola la conciliación desde la hora guardada, que en una
/// base restaurada es anterior al punto de restauración.
/// </summary>
public sealed class TimelineMark
{
    public int Id { get; set; } = 1;

    public long TimelineId { get; set; }

    public DateTimeOffset SeenAt { get; set; }
}
