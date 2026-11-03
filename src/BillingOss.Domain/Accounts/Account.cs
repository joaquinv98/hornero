using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Accounts;

public enum AccountStatus
{
    Active,
    Inactive,
    Closed,
}

public enum TaxDocumentType
{
    None,
    Cuit,
    Cuil,
    Dni,
    Passport,
    Foreign,
}

/// <summary>El cliente: persona o empresa (sección 2 del modelo de dominio).</summary>
public sealed class Account
{
    private Account()
    {
    }

    public Account(string name, string email, Currency currency, DateTimeOffset now, string language = "es")
    {
        Name = name;
        Email = email;
        Currency = currency;
        Language = language;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    public string? CompanyName { get; set; }

    public string Email { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public string Language { get; set; } = "es";

    public Currency Currency { get; set; }

    public AccountStatus Status { get; set; }

    /// <summary>Cortesías o cuentas internas: se factura, pero no se emite comprobante fiscal.</summary>
    public bool NoFiscalDocument { get; set; }

    /// <summary>Exenta del recargo por mora.</summary>
    public bool NoLateFees { get; set; }

    /// <summary>Grupo de clientes, si está en uno (diseno/cuentas-avanzadas.md, sección 2).</summary>
    public long? GroupId { get; set; }

    /// <summary>Exenta de los recordatorios de vencimiento y de los avisos de factura vencida.</summary>
    public bool NoReminders { get; set; }

    /// <summary>
    /// Figura en el listado de empresas grandes de ARCA: una factura por el mínimo de la factura de crédito
    /// electrónica MiPyME o más tiene que ser FCE, y hasta que exista se frena antes de llamar al fisco.
    /// </summary>
    public bool LargeCompany { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Para conservar el ID de origen en una importación (D-17).</summary>
    public void SetImportedId(long id) => Id = id;
}

/// <summary>Identidad fiscal con vigencia: un cambio crea una versión nueva y los comprobantes guardan la usada.</summary>
public sealed class TaxIdentity
{
    private TaxIdentity()
    {
    }

    public TaxIdentity(long accountId, TaxDocumentType documentType, string documentNumber, string legalName, int vatCondition, string country, DateTimeOffset validFrom)
    {
        AccountId = accountId;
        DocumentType = documentType;
        DocumentNumber = documentNumber;
        LegalName = legalName;
        VatCondition = vatCondition;
        Country = country;
        ValidFrom = validFrom;
    }

    public long Id { get; private set; }

    public long AccountId { get; private set; }

    public TaxDocumentType DocumentType { get; private set; }

    public string DocumentNumber { get; private set; } = string.Empty;

    public string LegalName { get; private set; } = string.Empty;

    /// <summary>Condición frente al IVA con el código del fisco (en ARCA, CondicionIVAReceptorId).</summary>
    public int VatCondition { get; private set; }

    public string? Street { get; set; }

    public string? City { get; set; }

    public string? Province { get; set; }

    public string? PostalCode { get; set; }

    /// <summary>País ISO 3166-1 alfa-2.</summary>
    public string Country { get; private set; } = "AR";

    /// <summary>Número de IVA de una empresa de la Unión Europea o del Reino Unido, sin el prefijo del país.</summary>
    public string? VatNumber { get; set; }

    /// <summary>Cuándo se validó el número (VIES o HMRC). Nulo si no se validó.</summary>
    public DateTimeOffset? VatNumberValidatedAt { get; set; }

    /// <summary>Número de consulta que devolvió la validación: es la constancia ante una inspección.</summary>
    public string? VatValidationRef { get; set; }

    /// <summary>De dónde salió la ficha: manual, arca (padrón) o import.</summary>
    public string? Source { get; set; }

    /// <summary>Cuándo se verificó la ficha contra el padrón.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset? ValidTo { get; private set; }

    /// <summary>Empresa con número fiscal validado: la evidencia que pide la inversión del sujeto pasivo.</summary>
    public bool ValidatedBusiness => VatNumber is { Length: > 0 } && VatNumberValidatedAt is not null;

    public void Close(DateTimeOffset at) => ValidTo = at;

    /// <summary>CUIT y CUIL: 11 dígitos con dígito verificador módulo 11.</summary>
    public static bool IsValidCuit(string value)
    {
        if (value is not { Length: 11 } || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = weights.Select((w, i) => w * (value[i] - '0')).Sum();
        var check = 11 - (sum % 11);
        check = check switch { 11 => 0, 10 => 9, _ => check };
        return check == value[10] - '0';
    }
}
