namespace BillingOss.Domain.Fiscal;

public enum EmissionMoment
{
    /// <summary>Por defecto: nunca llega tarde (sección 7.2 del modelo de dominio).</summary>
    AtIssue,

    /// <summary>Al primer cobro, aunque sea parcial, con el límite de fin de mes como red.</summary>
    AtFirstPayment,
}

/// <summary>Perfil fiscal del emisor (sección 7 del modelo de dominio).</summary>
public sealed class IssuerProfile
{
    /// <summary>Razón social que deja el importador hasta que el staff carga los datos reales del emisor.</summary>
    public const string ImportedName = "Emisor importado";

    public long Id { get; private set; }

    public string TaxId { get; set; } = string.Empty;

    public string LegalName { get; set; } = string.Empty;

    /// <summary>Condición del emisor frente al IVA (códigos de ARCA): decide la clase de comprobante y el preset de impuestos.</summary>
    public int VatCondition { get; set; } = Accounts.VatConditions.ResponsableInscripto;

    /// <summary>ARCA lo habilitó solo para la A con leyenda (tipos 51 a 54): el mapa de tipos usa esos códigos.</summary>
    public bool ClassAWithLegend { get; set; }

    /// <summary>Domicilio comercial para el impreso y el PDF (RG 1415, anexo II).</summary>
    public string? Address { get; set; }

    /// <summary>Número de inscripción en ingresos brutos, o el texto que corresponda (por ejemplo "Exento" o "Convenio multilateral 901-123456-7").</summary>
    public string? GrossIncomeNumber { get; set; }

    /// <summary>Fecha de inicio de actividades.</summary>
    public DateOnly? ActivityStart { get; set; }

    /// <summary>Vende al exterior: hace falta wsfex y su punto de venta.</summary>
    public bool Exports { get; set; }

    /// <summary>Completa fichas con el padrón de ARCA: hace falta autorizar ws_sr_constancia_inscripcion.</summary>
    public bool UsesRegistry { get; set; }

    /// <summary>Hasta qué paso llegó el asistente de ARCA (1 a 7). 0 si no empezó.</summary>
    public int SetupStep { get; set; }

    /// <summary>Resultado de la última prueba sin emitir. JSON con la forma de FiscalTestReport.</summary>
    public string? LastTest { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    /// <summary>Puntos de venta que informó el fisco en la última prueba, para elegirlos en vez de escribirlos. JSON.</summary>
    public string KnownPointsOfSale { get; set; } = "[]";

    /// <summary>Instancia del módulo fiscal (en v0, ARCA). Vacía mientras el staff no lo configura: no se emite.</summary>
    public long? ModuleInstanceId { get; set; }

    /// <summary>Punto de venta de los comprobantes del mercado interno.</summary>
    public int PointOfSale { get; set; }

    /// <summary>Punto de venta de exportación, si se usa.</summary>
    public int? ExportPointOfSale { get; set; }

    /// <summary>
    /// Tipo de comprobante por clase y tipo de documento, con los códigos del fisco. JSON, por ejemplo
    /// {"A":{"Invoice":1,"DebitNote":2,"CreditNote":3},"B":{...},"E":{...}}.
    /// </summary>
    public string TypeMap { get; set; } = "{}";

    /// <summary>Condiciones frente al IVA del receptor que llevan clase A. JSON con una lista de códigos.</summary>
    public string ClassAConditions { get; set; } = "[1,6,13,16]";

    public EmissionMoment Moment { get; set; } = EmissionMoment.AtIssue;

    /// <summary>
    /// Datos del emisor para la exportación y la moneda extranjera: país del receptor, forma de pago, idioma, unidad
    /// y qué medios de pago cancelan en la misma moneda. JSON con la forma de IssuerSettings.
    /// </summary>
    public string Settings { get; set; } = "{}";

    public bool Active { get; set; } = true;

    /// <summary>
    /// Desde cuándo emite, si reemplazó a otro emisor activo (el pase de homologación a producción, sección 2.10 del
    /// diseño fiscal): lo emitido antes es del anterior y no sale con este. Vacío en el primero, que emite todo.
    /// </summary>
    public DateTimeOffset? ActiveSince { get; set; }
}

/// <summary>Serie de numeración del fisco: emisor, punto de venta y tipo.</summary>
public sealed class FiscalSeries
{
    private FiscalSeries()
    {
    }

    public FiscalSeries(long issuerId, int pointOfSale, int typeCode)
    {
        IssuerId = issuerId;
        PointOfSale = pointOfSale;
        TypeCode = typeCode;
    }

    public long Id { get; private set; }

    public long IssuerId { get; private set; }

    public int PointOfSale { get; private set; }

    public int TypeCode { get; private set; }

    /// <summary>Último número que el núcleo registró como autorizado.</summary>
    public long LastNumber { get; set; }

    public DateOnly? LastDate { get; set; }

    /// <summary>
    /// Última vez que se comprobó que el fisco tiene el mismo último número: al autorizar y en el control diario. Vacío
    /// después de un conflicto o de un ajuste a mano. Mientras valga (<see cref="EmissionRules.IsSynced"/>) la emisión
    /// propone el siguiente sin consultar el último número.
    /// </summary>
    public DateTimeOffset? SyncedAt { get; set; }
}

/// <summary>Diario de emisión de un documento (secciones 7 y 7.1 del modelo de dominio).</summary>
public sealed class FiscalEmission
{
    private FiscalEmission()
    {
    }

    public FiscalEmission(long documentId, long issuerId, int pointOfSale, int typeCode, DateTimeOffset now)
    {
        DocumentId = documentId;
        IssuerId = issuerId;
        PointOfSale = pointOfSale;
        TypeCode = typeCode;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public long DocumentId { get; private set; }

    public long IssuerId { get; private set; }

    public int PointOfSale { get; private set; }

    public int TypeCode { get; private set; }

    public EmissionStatus Status { get; private set; }

    /// <summary>Número propuesto en el intento en curso. Se guarda antes de llamar al fisco.</summary>
    public long? ProposedNumber { get; private set; }

    public DateOnly? Date { get; private set; }

    /// <summary>
    /// ID de pedido que exige el fisco en exportación (wsfex_id en ARCA). Se fija una sola vez, se guarda antes de
    /// llamar y se repite en cada reintento: con el mismo ID el fisco devuelve lo que ya autorizó.
    /// </summary>
    public long? RequestId { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Códigos y observaciones del fisco del último intento. JSON.</summary>
    public string? Codes { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Propose(long number, DateOnly date, DateTimeOffset now)
    {
        Status = EmissionRules.Machine.Move(Status, EmissionStatus.Sending);
        ProposedNumber = number;
        Date = date;
        Attempts++;
        UpdatedAt = now;
    }

    public void MoveTo(EmissionStatus status, DateTimeOffset now)
    {
        Status = EmissionRules.Machine.Move(Status, status);
        UpdatedAt = now;
    }

    /// <summary>Fija el ID de pedido si todavía no tiene. Devuelve el vigente.</summary>
    public long ReserveRequestId(long candidate) => RequestId ??= candidate;

    /// <summary>Suelta el ID de pedido cuando el fisco dice que ya lo usó otro comprobante: el reintento reserva otro.</summary>
    public void ReleaseRequestId() => RequestId = null;
}

/// <summary>Comprobante fiscal autorizado. Uno por documento comercial, salvo los importados (D-17).</summary>
public sealed class FiscalDocument
{
    public long Id { get; private set; }

    public long DocumentId { get; set; }

    public long? EmissionId { get; set; }

    public long IssuerId { get; set; }

    public int PointOfSale { get; set; }

    public int TypeCode { get; set; }

    public long Number { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Código de autorización: CAE en ARCA.</summary>
    public string AuthorizationCode { get; set; } = string.Empty;

    public DateOnly? AuthorizationDue { get; set; }

    /// <summary>Lo que se mandó al fisco, para el PDF y el QR. JSON.</summary>
    public string Payload { get; set; } = "{}";

    public bool Imported { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
