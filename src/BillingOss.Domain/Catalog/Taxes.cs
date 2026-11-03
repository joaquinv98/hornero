using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Catalog;

/// <summary>Códigos de las categorías de fábrica. El proveedor puede crear otras.</summary>
public static class TaxCategories
{
    /// <summary>Hosting, VPS, game servers, bare metal y abonos: la alícuota general.</summary>
    public const string Digital = "digital";

    /// <summary>Telefonía y enlaces: 27 % a responsables inscriptos y monotributistas fuera de una vivienda.</summary>
    public const string Telecom = "telecom";

    public const string Exempt = "exempt";

    public const string Untaxed = "untaxed";
}

/// <summary>
/// Qué es lo que se vende, a efectos del impuesto. Va en el producto y en la regla. Una categoría exenta o no gravada
/// no pasa por las reglas: su resultado es fijo y lleva su motivo.
/// </summary>
public sealed class TaxCategory
{
    public long Id { get; private set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Explanation { get; set; } = string.Empty;

    public TaxTreatment Treatment { get; set; } = TaxTreatment.Taxed;

    /// <summary>Motivo fijo de las categorías exentas y no gravadas (<see cref="TaxReasons"/>).</summary>
    public string? Reason { get; set; }

    /// <summary>Códigos de un motor de cálculo externo, por ejemplo el de Stripe Tax. JSON.</summary>
    public string ExternalCodes { get; set; } = "{}";

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsValidOn(DateOnly on) => ValidFrom <= on && (ValidTo is null || on < ValidTo);
}

public enum TaxLevel
{
    /// <summary>Por renglón: el IVA.</summary>
    Line,

    /// <summary>Sobre el comprobante: percepciones e impuestos que solo se informan.</summary>
    Document,
}

/// <summary>Sobre qué se calcula un impuesto de comprobante.</summary>
public enum TaxBase
{
    Net,

    /// <summary>Neto más IVA: la base de la percepción de IVA de la RG 2126.</summary>
    NetPlusLineTaxes,

    Total,
}

/// <summary>Un renglón del desglose: el IVA, una percepción o una leyenda que solo se informa.</summary>
public sealed class Tax
{
    /// <summary>El impuesto principal de cada renglón. Sus reglas son las de IVA.</summary>
    public const string Vat = "iva";

    /// <summary>Percepción de IVA a no categorizados (RG 2126/2006), código 13 en la tabla de tributos de ARCA.</summary>
    public const string VatPerceptionRg2126 = "perc_iva_rg2126";

    public long Id { get; private set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public TaxLevel Level { get; set; } = TaxLevel.Document;

    public TaxBase Base { get; set; } = TaxBase.NetPlusLineTaxes;

    /// <summary>Código del tributo en la tabla del fisco (FEParamGetTiposTributos en ARCA). Nulo si no se informa.</summary>
    public int? FiscalCode { get; set; }

    /// <summary>Si suma al total. Apagado, es un impuesto que solo se informa con su leyenda.</summary>
    public bool AddsToTotal { get; set; } = true;

    /// <summary>Grupo para mostrar, por ejemplo "Percepciones".</summary>
    public string? Group { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsValidOn(DateOnly on) => ValidFrom <= on && (ValidTo is null || on < ValidTo);
}

/// <summary>Lo que del receptor decide el impuesto. La evidencia de empresa es el número fiscal validado (VIES o HMRC).</summary>
public sealed record TaxReceiver(string Country, string? Province, int VatCondition, long? AccountId = null, bool ValidatedBusiness = false);

/// <summary>
/// Qué trato fiscal corresponde según el receptor, la categoría y la cuenta, con vigencia. Los campos nulos valen para
/// cualquiera. Gana la regla más específica, y tiene que ser una sola: si no hay ninguna, el cálculo del IVA falla y
/// nunca se asume cero. Para los impuestos de comprobante, no tener regla quiere decir que no corresponden. Nada se
/// edita: se cierra la vigente y se abre otra, así un documento viejo siempre se explica con lo que usó.
/// </summary>
public sealed class TaxRule
{
    public long Id { get; private set; }

    /// <summary>De qué impuesto es la regla: el IVA o uno de comprobante (<see cref="Tax.Code"/>).</summary>
    public string TaxCode { get; set; } = Tax.Vat;

    public string? Country { get; set; }

    /// <summary>Subdivisión del receptor, como código ISO 3166-2 (AR-C, AR-B) donde el proveedor los usa.</summary>
    public string? Province { get; set; }

    public int? VatCondition { get; set; }

    /// <summary>Solo para empresas con número fiscal validado (true), solo para los demás (false) o cualquiera (null).</summary>
    public bool? BusinessWithValidatedNumber { get; set; }

    /// <summary>Categoría del renglón (<see cref="TaxCategory.Code"/>), o cualquiera.</summary>
    public string? Category { get; set; }

    /// <summary>
    /// Regla de cuenta: gana siempre dentro de su alcance. Cubre la exención con constancia, la exclusión de una
    /// percepción y el cliente del exterior que usa el servicio en el país.
    /// </summary>
    public long? AccountId { get; set; }

    public TaxTreatment Outcome { get; set; }

    public decimal Rate { get; set; }

    /// <summary>Palabra estable que explica el resultado (<see cref="TaxReasons"/>). Obligatoria en una regla de cuenta.</summary>
    public string? Reason { get; set; }

    /// <summary>Leyenda que lleva el comprobante cuando la regla aplica.</summary>
    public string? Legend { get; set; }

    /// <summary>Quién la cargó.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Número o referencia de la constancia adjunta (exención de ARCA, provincial o certificado de reventa).</summary>
    public string? CertificateRef { get; set; }

    public string? Note { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    /// <summary>Cuenta 16, categoría 8, condición o empresa 4, subdivisión 2 y país 1.</summary>
    public int Specificity =>
        (Country is null ? 0 : 1)
        + (Province is null ? 0 : 2)
        + (VatCondition is null && BusinessWithValidatedNumber is null ? 0 : 4)
        + (Category is null ? 0 : 8)
        + (AccountId is null ? 0 : 16);

    public bool IsAccountRule => AccountId is not null;

    public bool Matches(TaxReceiver receiver, string? category, DateOnly on, string taxCode = Tax.Vat)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        return TaxCode == taxCode
            && (Country is null || Country == receiver.Country)
            && (Province is null || Province == receiver.Province)
            && (VatCondition is null || VatCondition == receiver.VatCondition)
            && (BusinessWithValidatedNumber is null || BusinessWithValidatedNumber == receiver.ValidatedBusiness)
            && (Category is null || Category == category)
            && (AccountId is null || AccountId == receiver.AccountId)
            && ValidFrom <= on
            && (ValidTo is null || on < ValidTo);
    }

    /// <summary>La regla de IVA que corresponde. Sin ninguna o con empate, el cálculo falla.</summary>
    public static TaxRule Resolve(IEnumerable<TaxRule> rules, TaxReceiver receiver, string? category, DateOnly on, string taxCode = Tax.Vat)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        var best = Best(rules, receiver, category, on, taxCode);
        return best switch
        {
            null => throw new InvalidOperationException($"No hay regla de {taxCode} para {receiver.Country}/{receiver.Province}/{receiver.VatCondition}/{category}."),
            [var only] => only,
            _ => throw new InvalidOperationException($"Hay más de una regla de {taxCode} para {receiver.Country}/{receiver.Province}/{receiver.VatCondition}/{category}."),
        };
    }

    /// <summary>La regla más específica, o null si no hay ninguna. Con empate falla igual que <see cref="Resolve"/>.</summary>
    public static TaxRule? TryResolve(IEnumerable<TaxRule> rules, TaxReceiver receiver, string? category, DateOnly on, string taxCode)
    {
        var best = Best(rules, receiver, category, on, taxCode);
        return best switch
        {
            null => null,
            [var only] => only,
            _ => throw new InvalidOperationException($"Hay más de una regla de {taxCode} para {receiver.Country}/{receiver.Province}/{receiver.VatCondition}/{category}."),
        };
    }

    private static List<TaxRule>? Best(IEnumerable<TaxRule> rules, TaxReceiver receiver, string? category, DateOnly on, string taxCode)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.Where(r => r.Matches(receiver, category, on, taxCode))
            .GroupBy(r => r.Specificity)
            .OrderByDescending(g => g.Key)
            .FirstOrDefault()?
            .ToList();
    }
}

/// <summary>
/// Régimen de ingresos brutos donde el proveedor es agente de percepción (sección 3.8 del diseño fiscal): jurisdicción
/// ISO 3166-2, régimen, número de agente, alícuota fija, base, condiciones del receptor excluidas y vigencia. Sin un
/// régimen vigente ese impuesto no se cobra. El padrón mensual de alícuotas por CUIT queda para después.
/// </summary>
public sealed record GrossIncomeRegime(string Jurisdiction, string Regime, string AgentNumber, decimal Rate, TaxBase Base, DateOnly ValidFrom, DateOnly? ValidTo = null, int[]? ExcludedConditions = null)
{
    /// <summary>Percepción de ingresos brutos en la tabla de tributos de ARCA (FEParamGetTiposTributos).</summary>
    public const int FiscalCode = 7;

    public string TaxCode => "perc_iibb_" + Jurisdiction.ToLowerInvariant().Replace('-', '_');

    public bool IsValidOn(DateOnly on) => ValidFrom <= on && (ValidTo is null || on < ValidTo);
}

/// <summary>
/// Ajustes impositivos del proveedor, una sola fila. El modo de carga por defecto por moneda lo usan solo los precios
/// nuevos: cada precio y cada servicio llevan el suyo. Los umbrales legales tienen fecha porque se actualizan.
/// </summary>
public sealed class TaxSettings
{
    /// <summary>Lo que ve el visitante en catálogo y carrito. En Argentina la ley fija el primero (Res. 4/2025 SIC).</summary>
    public static IReadOnlyList<string> CatalogDisplays { get; } = ["final_with_net", "net_plus_tax", "final"];

    public long Id { get; set; } = 1;

    /// <summary>País del emisor, ISO 3166-1.</summary>
    public string IssuerCountry { get; set; } = "AR";

    /// <summary>Modo de carga por defecto por moneda. JSON, por ejemplo {"ARS":"TaxIncluded","USD":"TaxExcluded"}.</summary>
    public string PriceModeByCurrency { get; set; } = """{"ARS":"TaxIncluded","USD":"TaxExcluded"}""";

    /// <summary>
    /// Qué ve el visitante en catálogo y carrito: final_with_net (precio final y, en letra chica, el precio sin
    /// impuestos nacionales, lo que fija la Res. 4/2025 SIC en Argentina), net_plus_tax o final.
    /// </summary>
    public string CatalogDisplay { get; set; } = "final_with_net";

    /// <summary>El cliente identificado como responsable inscripto puede elegir ver neto más IVA.</summary>
    public bool BusinessMayViewNet { get; set; } = true;

    /// <summary>Desde qué importe un consumidor final se identifica con DNI, CUIL o CDI (RG 1415, anexo II, con la RG 5866/2026).</summary>
    public decimal ConsumerIdThreshold { get; set; } = 10_000_000m;

    public DateOnly ConsumerIdThresholdFrom { get; set; } = new(2026, 7, 1);

    /// <summary>Mínimo de la factura de crédito electrónica MiPyME (Ley 27.440 y RG 4367), según el micrositio de ARCA.</summary>
    public decimal FceMinimum { get; set; } = 5_549_862m;

    public DateOnly FceMinimumFrom { get; set; } = new(2026, 4, 14);

    /// <summary>Regímenes de ingresos brutos donde el proveedor es agente. JSON con la forma de GrossIncomeRegime.</summary>
    public string GrossIncomeRegimes { get; set; } = "[]";

    public PriceMode DefaultPriceMode(Currency currency)
    {
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PriceMode>>(PriceModeByCurrency, Json) ?? [];
        return map.TryGetValue(currency.Code, out var mode) ? mode : PriceMode.TaxIncluded;
    }

    public void SetDefaultPriceMode(Currency currency, PriceMode mode)
    {
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PriceMode>>(PriceModeByCurrency, Json) ?? [];
        map[currency.Code] = mode;
        PriceModeByCurrency = System.Text.Json.JsonSerializer.Serialize(map, Json);
    }

    public IReadOnlyDictionary<string, PriceMode> PriceModes() => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PriceMode>>(PriceModeByCurrency, Json) ?? [];

    public IReadOnlyList<GrossIncomeRegime> Regimes() => System.Text.Json.JsonSerializer.Deserialize<List<GrossIncomeRegime>>(GrossIncomeRegimes, Json) ?? [];

    public void SetRegimes(IEnumerable<GrossIncomeRegime> regimes) => GrossIncomeRegimes = System.Text.Json.JsonSerializer.Serialize(regimes, Json);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}
