using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Arca;

// Entradas de las operaciones. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta,
// en vez de dejar que el deserializador falle sin decir qué campo era.

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class ArcaConfig
{
    public string? Cuit { get; init; }

    public string? Environment { get; init; }

    public string? CertificatePem { get; init; }

    public string? PrivateKeyPem { get; init; }
}

/// <summary>Entrada de authorize. WsfexId es el ID de pedido de WSFEX y solo se usa en los comprobantes de exportación (19, 20 y 21).</summary>
internal sealed class AuthorizeInput
{
    public AuthorizeDocument? Document { get; init; }

    public long? WsfexId { get; init; }
}

internal sealed class AuthorizeDocument
{
    public int PointOfSale { get; init; }

    public int TypeCode { get; init; }

    public long Number { get; init; }

    public DateOnly? Date { get; init; }

    public string? Concept { get; init; }

    public Period? ServicePeriod { get; init; }

    public DateOnly? PaymentDue { get; init; }

    public Receiver? Receiver { get; init; }

    public string? Currency { get; init; }

    public decimal? ExchangeRate { get; init; }

    public bool? SameCurrencyPayment { get; init; }

    public Amounts? Amounts { get; init; }

    public List<AssociatedDocument>? Associated { get; init; }

    public List<OptionalField>? Optional { get; init; }

    /// <summary>Período asociado (PeriodoAsoc): lo pide ARCA en una nota que revierte percepciones, del mismo mes.</summary>
    public Period? PeriodAssoc { get; init; }

    // Solo exportación (WSFEX).
    public string? Language { get; init; }

    public string? PaymentTerms { get; init; }

    public List<ExportItem>? Items { get; init; }
}

internal sealed class Period
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

/// <summary>Receptor. Para WSFEv1 se usan documento y condición de IVA. Para WSFEX, nombre, domicilio, país e identificación tributaria.</summary>
internal sealed class Receiver
{
    public int? DocType { get; init; }

    public string? DocNumber { get; init; }

    public int? VatCondition { get; init; }

    public string? Name { get; init; }

    public string? Address { get; init; }

    public int? CountryCode { get; init; }

    public long? CountryTaxId { get; init; }

    public string? TaxId { get; init; }
}

internal sealed class Amounts
{
    public decimal NetTaxed { get; init; }

    public decimal NetUntaxed { get; init; }

    public decimal Exempt { get; init; }

    public List<VatLine>? Vat { get; init; }

    /// <summary>Total de los otros tributos (ImpTrib). Tiene que ser la suma de <see cref="Taxes"/>.</summary>
    public decimal OtherTaxes { get; init; }

    /// <summary>Los tributos, uno por renglón del array de ARCA: percepciones y otros impuestos del comprobante.</summary>
    public List<TaxLine>? Taxes { get; init; }

    public decimal? Total { get; init; }
}

internal sealed class VatLine
{
    public int RateId { get; init; }

    public decimal Base { get; init; }

    public decimal Amount { get; init; }
}

/// <summary>Un tributo del comprobante: el código de la tabla FEParamGetTiposTributos, su descripción, base, alícuota en porcentaje e importe.</summary>
internal sealed class TaxLine
{
    public int Id { get; init; }

    public string? Description { get; init; }

    public decimal Base { get; init; }

    public decimal Rate { get; init; }

    public decimal Amount { get; init; }
}

internal sealed class AssociatedDocument
{
    public int TypeCode { get; init; }

    public int PointOfSale { get; init; }

    public long Number { get; init; }

    /// <summary>CUIT del emisor del comprobante asociado.</summary>
    public string? TaxId { get; init; }

    public DateOnly? Date { get; init; }
}

internal sealed class OptionalField
{
    public string? Id { get; init; }

    public string? Value { get; init; }
}

internal sealed class ExportItem
{
    public string? Code { get; init; }

    public string? Description { get; init; }

    public decimal? Quantity { get; init; }

    public int? UnitCode { get; init; }

    public decimal? UnitPrice { get; init; }

    public decimal? Discount { get; init; }

    public decimal? Total { get; init; }
}

/// <summary>Entrada de last_number (sin Number) y de find.</summary>
internal sealed record DocumentRef(int PointOfSale = 0, int TypeCode = 0, long Number = 0);

internal sealed class ExchangeRateInput
{
    public string? Currency { get; init; }

    public DateOnly? Date { get; init; }

    /// <summary>Tipo de comprobante para el que se cotiza. Con 19, 20 o 21 se pide a WSFEX. Sin tipo, a WSFEv1.</summary>
    public int? TypeCode { get; init; }
}

/// <summary>Entrada de instance/test: con fresh_login se pide un ticket nuevo sin usar los que hay.</summary>
internal sealed class TestInput
{
    public bool? FreshLogin { get; init; }
}

/// <summary>Entrada de parameters: con export se suman los puntos de venta de exportación, por WSFEX.</summary>
internal sealed class ParametersInput
{
    public bool? Export { get; init; }
}

/// <summary>Entrada de taxpayer: la CUIT a consultar en el padrón.</summary>
internal sealed class TaxpayerInput
{
    public string? TaxId { get; init; }
}

/// <summary>Código y mensaje de ARCA, tal cual vienen en Errors, Observaciones o FEXErr.</summary>
internal sealed record Upstream(int Code, string Message)
{
    public static Upstream From(XElement element) => new(
        int.TryParse(element.Text("Code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0,
        element.Text("Msg") ?? string.Empty);
}

/// <summary>Formatos de ARCA: fechas yyyymmdd, importes con punto decimal.</summary>
internal static class Fmt
{
    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Cotizaciones y cantidades: hasta 6 decimales, sin ceros de más.</summary>
    public static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Ymd(DateOnly? date) => date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static DateOnly? ParseYmd(string? text) =>
        text is { Length: 8 } && DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static decimal? ParseDecimal(string? text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Importe de documento: positivo o cero y con a lo sumo los decimales que acepta ARCA.</summary>
    public static bool IsAmount(decimal? value, int decimals = 2) =>
        value is >= 0 && decimal.Round(value.Value, decimals) == value;
}

/// <summary>Armado de los errores del protocolo con los detalles que usa este módulo.</summary>
internal static class Fail
{
    public static ModuleException InvalidInput(IReadOnlyCollection<string> fields) => new(
        ErrorCodes.InvalidInput,
        $"Faltan datos o son inválidos: {string.Join(", ", fields)}.",
        details: new JsonObject { ["fields"] = Strings(fields) });

    public static ModuleException InvalidConfig(IReadOnlyCollection<string> fields, string? message = null) => new(
        ErrorCodes.InvalidConfig,
        message ?? $"La configuración de la instancia no sirve: {string.Join(", ", fields)}.",
        details: new JsonObject { ["fields"] = Strings(fields) });

    /// <summary>Resultado incierto: el comprobante pudo haberse autorizado, así que el núcleo consulta antes de repetir.</summary>
    public static ModuleException Unknown(string message, string reason, IReadOnlyCollection<Upstream>? codes = null) => new(
        ErrorCodes.OutcomeUnknown,
        message,
        retryAfter: 30,
        details: Codes(codes, reason));

    public static ModuleException Rejected(string message, IReadOnlyCollection<Upstream> codes) => new(
        ErrorCodes.Rejected,
        message,
        details: Codes(codes, null));

    public static string Describe(IEnumerable<Upstream> codes)
    {
        var text = string.Join(" ", codes.Select(c => $"[{c.Code}] {c.Message}".Trim()));
        return text.Length <= 600 ? text : text[..600] + "...";
    }

    /// <summary>details.upstream trae los códigos de ARCA, como pide la sección 10 del protocolo.</summary>
    public static JsonObject Codes(IEnumerable<Upstream>? codes, string? reason)
    {
        var details = new JsonObject();
        if (reason is not null)
        {
            details["reason"] = reason;
        }

        details["upstream"] = new JsonArray(
            (codes ?? []).Select(c => (JsonNode?)new JsonObject { ["code"] = c.Code, ["message"] = c.Message }).ToArray());
        return details;
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}

internal static class Inputs
{
    /// <summary>Lee la entrada de la operación. Un tipo mal puesto sale como invalid_input con la ruta, nunca con el valor.</summary>
    public static T Read<T>(ModuleCall call)
    {
        try
        {
            return call.Input<T>();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidInput([ex.Path ?? "input"]);
        }
    }
}
