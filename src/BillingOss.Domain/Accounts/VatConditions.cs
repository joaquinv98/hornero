namespace BillingOss.Domain.Accounts;

/// <summary>
/// Condiciones frente al IVA del receptor, con los códigos y descripciones de la tabla de ARCA
/// (FEParamGetCondicionIvaReceptor). La ficha del cliente acepta solo estos códigos: la RG 5616/2024 exige la
/// condición en todo comprobante electrónico. La clase dice qué comprobante le corresponde a un emisor responsable
/// inscripto; un emisor monotributista o exento emite C a cualquiera.
/// </summary>
public static class VatConditions
{
    public const int ResponsableInscripto = 1;
    public const int Exento = 4;
    public const int ConsumidorFinal = 5;
    public const int Monotributo = 6;
    public const int NoCategorizado = 7;
    public const int ProveedorDelExterior = 8;
    public const int ClienteDelExterior = 9;
    public const int LiberadoLey19640 = 10;
    public const int MonotributistaSocial = 13;
    public const int NoAlcanzado = 15;
    public const int MonotributoPromovido = 16;

    public sealed record Entry(int Code, string Description, string Class);

    public static IReadOnlyList<Entry> Table { get; } =
    [
        new(ResponsableInscripto, "IVA Responsable Inscripto", "A"),
        new(Exento, "IVA Sujeto Exento", "B"),
        new(ConsumidorFinal, "Consumidor Final", "B"),
        new(Monotributo, "Responsable Monotributo", "A"),
        new(NoCategorizado, "Sujeto No Categorizado", "B"),
        new(ProveedorDelExterior, "Proveedor del Exterior", "B"),
        new(ClienteDelExterior, "Cliente del Exterior", "B"),
        new(LiberadoLey19640, "IVA Liberado – Ley N° 19.640", "B"),
        new(MonotributistaSocial, "Monotributista Social", "A"),
        new(NoAlcanzado, "IVA No Alcanzado", "B"),
        new(MonotributoPromovido, "Monotributo Trabajador Independiente Promovido", "A"),
    ];

    /// <summary>Condiciones que un emisor puede tener: responsable inscripto, monotributista (en sus variantes) o exento.</summary>
    public static IReadOnlyList<int> IssuerConditions { get; } = [ResponsableInscripto, Monotributo, MonotributistaSocial, MonotributoPromovido, Exento];

    public static bool IsKnown(int code) => Table.Any(e => e.Code == code);

    public static string Describe(int code) => Table.FirstOrDefault(e => e.Code == code)?.Description ?? $"Condición {code}";

    /// <summary>Un emisor monotributista o exento emite comprobantes de clase C, sin IVA (RG 1415, art. 16).</summary>
    public static bool IssuesClassC(int issuerCondition) => issuerCondition is Exento or Monotributo or MonotributistaSocial or MonotributoPromovido;

    /// <summary>Clase del comprobante que le corresponde al receptor con un emisor responsable inscripto.</summary>
    public static string ClassFor(int code) => Table.FirstOrDefault(e => e.Code == code)?.Class ?? "B";
}
