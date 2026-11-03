namespace BillingOss.Domain.Fiscal;

public enum CredentialStatus
{
    /// <summary>Espera el certificado que da el fisco para el pedido.</summary>
    Pending,

    /// <summary>Certificado cargado y validado, todavía sin usar.</summary>
    Ready,

    /// <summary>Es la que usa la instancia del módulo. Hay una sola por emisor y ambiente.</summary>
    Active,

    Retired,
}

/// <summary>Ambientes del fisco. El de una instancia fiscal no cambia: para el otro se crea otra instancia.</summary>
public static class FiscalEnvironments
{
    public const string Homologation = "homologation";
    public const string Production = "production";

    public static bool IsValid(string? value) => value is Homologation or Production;
}

/// <summary>
/// Credencial fiscal (sección 2.4 del diseño fiscal): un par de claves generado en el servidor con su pedido de
/// certificado, el certificado que dio el fisco y sus metadatos. La clave privada se guarda cifrada por campo (D-09),
/// nunca vuelve al navegador y se borra de la fila al activar la credencial, porque pasa a los secretos de la instancia.
/// </summary>
public sealed class FiscalCredential
{
    private FiscalCredential()
    {
    }

    public FiscalCredential(long issuerId, string environment, string alias, string subjectTaxId, string organization, string csr, DateTimeOffset now)
    {
        IssuerId = issuerId;
        Environment = environment;
        Alias = alias;
        SubjectTaxId = subjectTaxId;
        Organization = organization;
        Csr = csr;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    public long IssuerId { get; private set; }

    public string Environment { get; private set; } = FiscalEnvironments.Homologation;

    /// <summary>Nombre simbólico del DN en ARCA. Solo minúsculas y números.</summary>
    public string Alias { get; private set; } = string.Empty;

    /// <summary>CUIT del sujeto del pedido: la de quien pide el certificado en ARCA.</summary>
    public string SubjectTaxId { get; private set; } = string.Empty;

    public string Organization { get; private set; } = string.Empty;

    public CredentialStatus Status { get; private set; }

    /// <summary>Clave privada en PKCS#8 PEM, cifrada. Nula desde que se activa o se retira.</summary>
    public byte[]? PrivateKey { get; set; }

    /// <summary>Pedido de certificado en PEM. No es secreto.</summary>
    public string Csr { get; private set; } = string.Empty;

    /// <summary>Certificado en PEM, cuando el fisco lo dio. No es secreto.</summary>
    public string? Certificate { get; private set; }

    public string? CertificateTaxId { get; private set; }

    public string? CertificateAlias { get; private set; }

    public string? CertificateIssuer { get; private set; }

    public string? CertificateSerial { get; private set; }

    /// <summary>Huella SHA-256 del certificado, en hexadecimal.</summary>
    public string? Thumbprint { get; private set; }

    public DateTimeOffset? NotBefore { get; private set; }

    public DateTimeOffset? NotAfter { get; private set; }

    /// <summary>Umbrales de días (30, 15, 7 y 1) ya avisados, para no repetir el aviso. JSON.</summary>
    public string NoticesSent { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>Guarda el certificado validado con sus metadatos. Queda lista para activar.</summary>
    public void LoadCertificate(string pem, string? taxId, string? alias, string issuer, string serial, string thumbprint, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        if (Status is not (CredentialStatus.Pending or CredentialStatus.Ready))
        {
            throw new InvalidOperationException($"La credencial {Id} está {Status}: no acepta otro certificado.");
        }

        Certificate = pem;
        CertificateTaxId = taxId;
        CertificateAlias = alias;
        CertificateIssuer = issuer;
        CertificateSerial = serial;
        Thumbprint = thumbprint;
        NotBefore = notBefore;
        NotAfter = notAfter;
        Status = CredentialStatus.Ready;
    }

    /// <summary>Pasa a activa: la clave deja esta fila porque queda en los secretos de la instancia.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status != CredentialStatus.Ready || Certificate is null)
        {
            throw new InvalidOperationException($"La credencial {Id} no tiene certificado validado: no se puede activar.");
        }

        Status = CredentialStatus.Active;
        ActivatedAt = now;
        PrivateKey = null;
    }

    public void Retire(DateTimeOffset now)
    {
        Status = CredentialStatus.Retired;
        RetiredAt = now;
        PrivateKey = null;
    }

    public int? DaysLeft(DateOnly today) => NotAfter is { } until ? DateOnly.FromDateTime(until.UtcDateTime).DayNumber - today.DayNumber : null;
}
