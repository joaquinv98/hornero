using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;

namespace BillingOss.App.Reports;

/// <summary>
/// Un tributo del comprobante que no es IVA, con el código de ARCA (FEParamGetTiposTributos). La alícuota va como
/// fracción, igual que la del IVA: 0,03 es 3 %.
/// </summary>
public sealed record OtherTax(int Code, string Description, decimal Base, decimal? Rate, decimal Amount);

/// <summary>
/// Un comprobante autorizado con lo que necesitan el libro y el reporte de impuestos, con sus importes en su moneda.
/// <see cref="ExchangeRate"/> es nulo si el comprobante está en moneda extranjera y no se sabe su cotización.
/// </summary>
public sealed record FiscalVoucher(
    DateOnly Date,
    int TypeCode,
    int PointOfSale,
    long Number,
    DocumentKind Kind,
    string FiscalCurrency,
    decimal? ExchangeRate,
    IReadOnlyList<AliquotTotal> Aliquots,
    decimal Exempt,
    decimal NotTaxed,
    decimal Total,
    IReadOnlyList<OtherTax> OtherTaxes,
    int DocType,
    string DocNumber,
    string Name)
{
    public int Sign => Kind == DocumentKind.CreditNote ? -1 : 1;

    public string Label => ReportService.Voucher(TypeCode, PointOfSale, Number);

    /// <summary>
    /// Arma el comprobante con lo que se mandó al fisco. Lo emitido por el núcleo tiene el nivel "document" de la
    /// sección 9.4 del protocolo, con el receptor, la moneda, la cotización y, cuando los haya, los otros tributos en
    /// amounts.taxes, con su alícuota en porcentaje como la pide el fisco. Sus importes son los del documento comercial,
    /// que es lo que se autorizó. Lo importado (D-17) guarda sin ese nivel doc_type, doc_number, currency,
    /// exchange_rate y los importes del comprobante (total, net y vat), que valen aunque el documento tenga otros: el
    /// mismo documento puede tener la factura y la nota que la anuló.
    /// </summary>
    public static FiscalVoucher From(
        DateOnly date, int typeCode, int pointOfSale, long number, bool imported, string payload, DocumentKind kind, string currency,
        IReadOnlyList<AliquotTotal> aliquots, decimal exempt, decimal notTaxed, decimal total, string receiver, string accountName)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var sent = JsonNode.Parse(payload);
        var doc = sent?["document"] ?? sent;
        var to = doc?["receiver"];
        var snapshot = receiver?.Length > 2 ? JsonSerializer.Deserialize<ReceiverSnapshot>(receiver) : null;

        // Exportación: el receptor va con la CUIT genérica de su país y el código de CUIT (diseño del Libro IVA Digital).
        var (docType, docNumber) = to?["country_tax_id"] is { } country
            ? (80, country.ToString())
            : ((int)(Decimal(to?["doc_type"] ?? doc?["doc_type"]) ?? 99), (to?["doc_number"] ?? doc?["doc_number"])?.ToString() ?? "0");
        var name = to?["name"]?.ToString() is { Length: > 0 } sentName ? sentName : snapshot?.LegalName is { Length: > 0 } legal ? legal : accountName;

        if (imported && Decimal(doc?["total"]) is { } importedTotal)
        {
            (aliquots, exempt, notTaxed) = ImportedAmounts(typeCode, importedTotal, Decimal(doc?["net"]) ?? 0m, Decimal(doc?["vat"]) ?? 0m);
            total = importedTotal;
        }

        var amounts = doc?["amounts"];
        var taxes = (amounts?["taxes"] as JsonArray ?? []).OfType<JsonNode>()
            .Select(t => new OtherTax((int)(Decimal(t["id"]) ?? 99), t["description"]?.ToString() ?? string.Empty, Decimal(t["base"]) ?? 0m, Decimal(t["rate"]) / 100m, Decimal(t["amount"]) ?? 0m))
            .ToList();
        var unlisted = (Decimal(amounts?["other_taxes"]) ?? 0m) - taxes.Sum(t => t.Amount);
        if (unlisted > 0m)
        {
            taxes.Add(new OtherTax(99, string.Empty, 0m, null, unlisted));
        }

        return new FiscalVoucher(
            date,
            typeCode,
            pointOfSale,
            number,
            kind,
            doc?["currency"]?.ToString() ?? currency switch { "ARS" => "PES", "USD" => "DOL", _ => currency },
            currency == "ARS" ? 1m : Decimal(doc?["exchange_rate"]),
            aliquots,
            exempt,
            notTaxed,
            total,
            taxes,
            docType,
            docNumber,
            name);
    }

    /// <summary>
    /// Los importes de un comprobante importado, que el sistema anterior guardó como total, neto e IVA. La alícuota es la
    /// de la tabla cuyo IVA queda más cerca del informado, dentro del centavo de tolerancia del fisco: con importes
    /// chicos el cociente entre IVA y neto no da justo la alícuota. Si ninguna queda a un centavo, va el cociente, que
    /// no está en la tabla y el libro lo deja afuera con su nota. Sin IVA, el comprobante E es exportación exenta y el
    /// resto, no gravado, igual que al importar el documento.
    /// </summary>
    public static (IReadOnlyList<AliquotTotal> Aliquots, decimal Exempt, decimal NotTaxed) ImportedAmounts(int typeCode, decimal total, decimal net, decimal vat)
    {
        if (vat == 0m)
        {
            return LibroIvaDigital.IsExport(typeCode) ? ([], total, 0m) : ([], 0m, total);
        }

        var rate = LibroIvaDigital.RateCodes.Keys.Where(r => r > 0m).MinBy(r => Math.Abs((net * r) - vat));
        if (Math.Abs(Rounding.Commercial(net * rate) - vat) > 0.01m)
        {
            rate = net == 0m ? 1m : Math.Round(vat / net, 4);
        }

        return ([new AliquotTotal(rate, net, vat)], 0m, total - net - vat);
    }

    /// <summary>Un número que puede venir como número o como texto en el JSON. Nulo si no está o no es un número.</summary>
    private static decimal? Decimal(JsonNode? node) =>
        decimal.TryParse(node?.ToString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// Los dos archivos del Libro IVA Digital de ventas de un mes, lo que el staff tiene que revisar antes de importarlos y
/// un resumen por tipo de comprobante y moneda.
/// </summary>
public sealed record LibroIva(IReadOnlyList<string> Vouchers, IReadOnlyList<string> Aliquots, IReadOnlyList<ReportNote> Notes, ReportTable Summary)
{
    public byte[] VouchersFile => LibroIvaDigital.File(Vouchers);

    public byte[] AliquotsFile => LibroIvaDigital.File(Aliquots);
}

/// <summary>
/// Exportación del Libro IVA Digital de ventas (RG 4597 de AFIP, hoy ARCA), con los diseños de registro oficiales:
/// LIBRO_IVA_DIGITAL_VENTAS_CBTE de 266 posiciones y LIBRO_IVA_DIGITAL_VENTAS_ALICUOTAS de 62. Los importes van en la
/// moneda del comprobante con su cotización: al importar se elige "Los importes están expresados en la Moneda Original
/// del Comprobante". Los archivos van en ISO 8859-1, un registro por línea, y los dos en el mismo orden. Las reglas de
/// cada campo salen de las especificaciones de ARCA (revisión del 30/07/2025), citadas en docs/reportes.
/// </summary>
public static class LibroIvaDigital
{
    public const int VoucherLength = 266;

    public const int AliquotLength = 62;

    /// <summary>Códigos de alícuota de ARCA, los mismos de WSFEv1.</summary>
    internal static readonly Dictionary<decimal, int> RateCodes = new() { [0m] = 3, [0.025m] = 9, [0.05m] = 8, [0.105m] = 4, [0.21m] = 5, [0.27m] = 6 };

    /// <summary>
    /// Campo del registro de cada código de tributo de ARCA: 13 es la percepción a no categorizados, 1 y 6 los
    /// nacionales, 2, 5 y 7 los de ingresos brutos, 3 y 8 los municipales y 4 los internos. El resto va a otros tributos.
    /// </summary>
    private static readonly Dictionary<int, string> TaxFields = new()
    {
        [13] = "non_categorized", [1] = "national", [6] = "national", [2] = "gross_income", [5] = "gross_income", [7] = "gross_income",
        [3] = "municipal", [8] = "municipal", [4] = "internal",
    };

    /// <summary>Factura, nota de débito y nota de crédito E: exportación, sin IVA.</summary>
    public static bool IsExport(int typeCode) => typeCode is 19 or 20 or 21;

    public static LibroIva Build(IEnumerable<FiscalVoucher> vouchers)
    {
        ArgumentNullException.ThrowIfNull(vouchers);
        var lines = new List<string>();
        var aliquotLines = new List<string>();
        var noRate = new List<string>();
        var unknownRate = new List<string>();
        var summary = new SortedDictionary<(int Type, string Currency), (int Count, decimal Net, decimal Vat, decimal Exempt, decimal Total)>();
        foreach (var v in vouchers)
        {
            var taxed = v.Aliquots.Where(a => a.Net != 0m || a.Tax != 0m).ToList();
            if (taxed.Any(a => !RateCodes.ContainsKey(a.Rate)))
            {
                unknownRate.Add(v.Label);
                continue;
            }

            if (v.ExchangeRate is null)
            {
                noRate.Add(v.Label);
            }

            // Sin IVA va un registro de alícuota cero, con el código de operación que dice por qué. Con IVA el código va en
            // blanco, aunque el comprobante tenga además una parte exenta (campo 20 de la especificación).
            var records = taxed.Count > 0 ? taxed.Select(a => (Code: RateCodes[a.Rate], a.Net, a.Tax)).ToList() : [(Code: 3, Net: 0m, Tax: 0m)];
            var operation = taxed.Any(a => a.Rate > 0m) ? ' '
                : IsExport(v.TypeCode) ? 'X'
                : v.NotTaxed > 0m && v.Exempt == 0m && taxed.Count == 0 ? 'N'
                : 'E';
            decimal Field(string field) => v.OtherTaxes.Where(t => TaxFields.GetValueOrDefault(t.Code, "other") == field).Sum(t => t.Amount);

            var line = new StringBuilder(VoucherLength)
                .Append(v.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
                .Append(Number(v.TypeCode, 3))
                .Append(Number(v.PointOfSale, 5))
                .Append(Number(v.Number, 20))
                .Append(Number(v.Number, 20))
                .Append(Number(v.DocType, 2))
                .Append(Identifier(v.DocType == 99 ? "0" : v.DocNumber, 20))
                .Append(Text(v.DocType == 99 ? "CONSUMIDOR FINAL" : v.Name, 30))
                .Append(Amount(v.Total))
                .Append(Amount(v.NotTaxed))
                .Append(Amount(Field("non_categorized")))
                .Append(Amount(v.Exempt))
                .Append(Amount(Field("national")))
                .Append(Amount(Field("gross_income")))
                .Append(Amount(Field("municipal")))
                .Append(Amount(Field("internal")))
                .Append(Text(v.FiscalCurrency, 3))
                .Append(v.ExchangeRate is { } rate ? Number((long)Math.Round(rate * 1_000_000m, MidpointRounding.AwayFromZero), 10) : new string('0', 10))
                .Append(Number(records.Count, 1))
                .Append(operation)
                .Append(Amount(Field("other")))

                // Fecha de vencimiento o pago: solo la exigen las liquidaciones de servicios públicos (017 y 018).
                .Append("00000000")
                .ToString();
            lines.Add(Checked(line, VoucherLength));
            foreach (var (code, net, tax) in records)
            {
                aliquotLines.Add(Checked(Number(v.TypeCode, 3) + Number(v.PointOfSale, 5) + Number(v.Number, 20) + Amount(net) + Number(code, 4) + Amount(tax), AliquotLength));
            }

            var key = (v.TypeCode, v.FiscalCurrency);
            var s = summary.GetValueOrDefault(key);
            summary[key] = (s.Count + 1, s.Net + taxed.Sum(a => a.Net), s.Vat + taxed.Sum(a => a.Tax), s.Exempt + v.Exempt + v.NotTaxed, s.Total + v.Total);
        }

        var notes = new List<ReportNote>();
        if (noRate.Count > 0)
        {
            notes.Add(new("NoExchangeRate", [string.Join(", ", noRate)]));
        }

        if (unknownRate.Count > 0)
        {
            notes.Add(new("UnknownRate", [string.Join(", ", unknownRate)]));
        }

        var table = new ReportTable(
            "LibroIvaSummary",
            [new("VoucherType", CellKind.Text), new("Currency", CellKind.Text), new("Vouchers", CellKind.Count), new("NetTaxed", CellKind.Money), new("Vat", CellKind.Money), new("ExemptAndNotTaxed", CellKind.Money), new("Total", CellKind.Money)],
            [.. summary.Select(s => new object?[] { Number(s.Key.Type, 3), s.Key.Currency, s.Value.Count, s.Value.Net, s.Value.Vat, s.Value.Exempt, s.Value.Total })]);
        return new LibroIva(lines, aliquotLines, notes, table);
    }

    /// <summary>El archivo: ISO 8859-1, que es lo que acepta la importación, con CRLF al final de cada registro.</summary>
    public static byte[] File(IEnumerable<string> lines) => Encoding.Latin1.GetBytes(string.Concat(lines.Select(l => l + "\r\n")));

    /// <summary>13 enteros y 2 decimales, sin punto. Los importes de un comprobante nunca son negativos.</summary>
    private static string Amount(decimal value) =>
        value >= 0m && Rounding.HasAtMostDecimals(value)
            ? Number((long)(value * 100m), 15)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Un importe del libro tiene que ser positivo y con dos decimales como máximo.");

    private static string Number(long value, int length) => value.ToString(new string('0', length), CultureInfo.InvariantCulture);

    /// <summary>El documento del comprador es alfanumérico (un pasaporte lleva letras): sin guiones ni puntos y con ceros adelante.</summary>
    private static string Identifier(string value, int length) =>
        new string([.. value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]).PadLeft(length, '0');

    /// <summary>
    /// Texto alineado a la izquierda y completado con blancos. Lo que no entra en ISO 8859-1 sale como "?", de a un
    /// carácter por letra aunque ocupe dos en UTF-16, así cada registro sigue midiendo lo mismo en bytes.
    /// </summary>
    private static string Text(string value, int length)
    {
        var latin = new StringBuilder();
        foreach (var rune in value.EnumerateRunes().Where(r => !Rune.IsControl(r)))
        {
            latin.Append(rune.Value <= 0xFF ? (char)rune.Value : '?');
        }

        var clean = latin.ToString().Trim();
        return (clean.Length > length ? clean[..length] : clean).PadRight(length);
    }

    private static string Checked(string line, int length) =>
        line.Length == length ? line : throw new InvalidOperationException($"Un registro del libro quedó de {line.Length} posiciones y no de {length}.");
}
