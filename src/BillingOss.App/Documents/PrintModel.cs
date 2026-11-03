using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;

namespace BillingOss.App.Documents;

/// <summary>
/// Modelo plano para la plantilla del PDF (D-13): solo textos ya formateados, nunca objetos del dominio. Lo arma
/// <see cref="PrintModels.Build"/> con los datos que exige la RG 1415 (anexo II) y las leyendas que correspondan.
/// </summary>
public sealed record PrintModel(
    string Title,
    string? Letter,
    string? TypeCode,
    string Number,
    string Date,
    string? DueDate,
    string? PeriodFrom,
    string? PeriodTo,
    PrintParty Issuer,
    PrintParty Receiver,
    string? Associated,
    string Currency,
    string? ExchangeRate,
    bool SameCurrencyPayment,
    string? PaymentTerms,
    bool LineTax,
    IReadOnlyList<PrintLine> Lines,
    IReadOnlyList<PrintTotal> Totals,
    string Total,
    PrintTransparency? Transparency,
    PrintFiscal? Fiscal,
    bool NotValidAsInvoice,
    IReadOnlyList<string> Legends);

public sealed record PrintParty(string Name, string? TaxId, string? VatCondition, string? Address, string? GrossIncome = null, string? ActivityStart = null);

public sealed record PrintLine(string Description, string Quantity, string UnitPrice, string? Rate, string Amount);

public sealed record PrintTotal(string Label, string Amount);

/// <summary>Bloque del Régimen de Transparencia Fiscal al Consumidor (Ley 27.743, RG 5614).</summary>
public sealed record PrintTransparency(string ContainedVat, string OtherTaxes);

/// <summary>CAE, su vencimiento y el QR (RG 4892).</summary>
public sealed record PrintFiscal(string AuthorizationCode, string? AuthorizationDue, string QrUrl, string QrImage);

/// <summary>Todo lo que hace falta para imprimir un documento, ya leído de la base.</summary>
public sealed record PrintSource(
    Document Document,
    ReceiverSnapshot Receiver,
    IssuerProfile? Issuer,
    FiscalDocument? Fiscal,
    bool FiscalExpected,
    DateOnly? PeriodFrom = null,
    DateOnly? PeriodTo = null,
    long? RelatedNumber = null);

public static class PrintModels
{
    /// <summary>Ley 24.240, art. 10 ter: el texto tiene que ir en la factura o documento equivalente.</summary>
    public const string RescissionLegend =
        "Ley 24.240, art. 10 ter: cuando la contratación de un servicio, incluidos los servicios públicos domiciliarios, haya sido realizada en forma "
        + "telefónica, electrónica o similar, podrá ser rescindida a elección del consumidor o usuario mediante el mismo medio utilizado en la contratación. "
        + "La empresa receptora del pedido de rescisión del servicio deberá enviar sin cargo al domicilio del consumidor o usuario una constancia fehaciente "
        + "dentro de las setenta y dos (72) horas posteriores a la recepción del pedido de rescisión.";

    /// <summary>RG 1415, art. 15 inc. a: factura A a un monotributista.</summary>
    public const string MonotributoLegend =
        "El crédito fiscal discriminado en el presente comprobante, sólo podrá ser computado a efectos del Régimen de Sostenimiento e Inclusión Fiscal "
        + "para Pequeños Contribuyentes de la Ley Nº 27.618";

    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };

    private static readonly int[] Monotributo = [6, 13, 16];

    public static PrintModel Build(PrintSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (doc, receiver, issuer, fiscal) = (source.Document, source.Receiver, source.Issuer, source.Fiscal);
        if (!doc.IsIssued)
        {
            throw new InvalidOperationException($"El documento {doc.Id} es un borrador: no tiene PDF.");
        }

        var sent = fiscal is null ? null : Voucher.Parse(fiscal.Payload);
        var letter = fiscal is null ? null : LetterOf(fiscal.TypeCode);
        var lineTax = letter == "A" || (letter is null && doc.PriceMode == PriceMode.TaxExcluded);
        var perLine = lineTax && !doc.Imported;
        var code = doc.Currency.Code;
        string Money(decimal v) => (code == "ARS" ? "$ " : code + " ") + v.ToString("#,##0.00", Numbers);

        var lines = doc.Lines.Select(l =>
        {
            // Los importados guardan el renglón como vino. El resto muestra neto en la A y precio final en las demás.
            var amount = doc.Imported ? l.Amount : perLine ? l.Net : l.Net + l.LineTax;
            var sameBasis = doc.Imported || (perLine ? doc.PriceMode == PriceMode.TaxExcluded : doc.PriceMode == PriceMode.TaxIncluded);
            var unit = l.Quantity == 1m ? amount : sameBasis ? l.UnitPrice : Math.Round(amount / l.Quantity, 2, MidpointRounding.AwayFromZero);
            return new PrintLine(
                l.Description,
                l.Quantity.ToString("#,##0.####", Numbers),
                unit.ToString("#,##0.00####", Numbers),
                perLine ? Rate(l.Treatment == TaxTreatment.Taxed ? l.Rate : null, l.Treatment) : null,
                amount.ToString("#,##0.00", Numbers));
        }).ToList();

        var totals = new List<PrintTotal>();
        if (lineTax)
        {
            totals.Add(new("Importe neto gravado", Money(doc.NetTaxed)));
            foreach (var a in JsonSerializer.Deserialize<List<AliquotTotal>>(doc.Aliquots ?? "[]") ?? [])
            {
                totals.Add(new($"IVA {Percent(a.Rate)}", Money(a.Tax)));
            }
        }

        // La C no discrimina: su subtotal es el neto que va a ARCA, no un importe no gravado (validaciones 10043 a 10048).
        var taxes = doc.IssuedTaxes();
        if (letter == "C")
        {
            totals.Add(new("Subtotal", Money(doc.Total - doc.OtherTaxes)));
        }
        else
        {
            if (doc.Exempt != 0m)
            {
                totals.Add(new("Importe exento", Money(doc.Exempt)));
            }

            if (doc.NotTaxed != 0m)
            {
                totals.Add(new("Importe no gravado", Money(doc.NotTaxed)));
            }
        }

        // Los tributos del comprobante, como la percepción de la RG 2126, suman al total y van discriminados.
        foreach (var tax in taxes.Where(x => x.AddsToTotal))
        {
            totals.Add(new($"{tax.Name} {Percent(tax.Rate)}", Money(tax.Amount)));
        }

        var legends = new List<string>();
        var vatCondition = sent?.VatCondition ?? receiver.VatCondition;
        if (letter == "A" && Monotributo.Contains(vatCondition))
        {
            legends.Add(MonotributoLegend);
        }

        // Las leyendas de las reglas, como la inversión del sujeto pasivo. Un tributo que solo se informa, como la
        // alícuota de ISIB CABA, va con su alícuota.
        var informational = taxes.Where(x => !x.AddsToTotal && x.Legend is not null).ToList();
        legends.AddRange(doc.IssuedLegends().Except(informational.Select(x => x.Legend!), StringComparer.Ordinal));
        legends.AddRange(informational.Select(x => $"{x.Legend} {Percent(x.Rate)}").Distinct(StringComparer.Ordinal));
        legends.Add(RescissionLegend);

        var date = fiscal?.Date ?? doc.IssueDate!.Value;
        var periodFrom = sent?.PeriodFrom ?? source.PeriodFrom;
        var periodTo = sent?.PeriodTo ?? source.PeriodTo;
        return new PrintModel(
            Title: doc.Kind switch { DocumentKind.CreditNote => "NOTA DE CRÉDITO", DocumentKind.DebitNote => "NOTA DE DÉBITO", _ => "FACTURA" },
            Letter: letter,
            TypeCode: fiscal?.TypeCode.ToString("00", CultureInfo.InvariantCulture),
            Number: fiscal is null
                ? (doc.Number ?? doc.Id).ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{fiscal.PointOfSale:00000}-{fiscal.Number:00000000}"),
            Date: Day(date),
            DueDate: doc.Kind == DocumentKind.CreditNote ? null : Day(sent?.PaymentDue ?? doc.DueDate),
            PeriodFrom: periodFrom is { } f ? Day(f) : null,
            PeriodTo: periodTo is { } t ? Day(t) : null,
            Issuer: IssuerParty(issuer),
            Receiver: ReceiverParty(receiver, sent, letter, vatCondition),
            Associated: sent?.Associated ?? (source.RelatedNumber is { } related ? string.Create(CultureInfo.InvariantCulture, $"Factura {related}") : null),
            Currency: code,
            ExchangeRate: code == "ARS" ? null : sent?.ExchangeRate?.ToString("#,##0.00####", Numbers),
            SameCurrencyPayment: code != "ARS" && sent?.SameCurrency == true,
            PaymentTerms: sent?.PaymentTerms,
            LineTax: perLine,
            Lines: lines,
            Totals: totals,
            Total: Money(doc.Total),
            Transparency: letter == "B" ? new PrintTransparency(Money(doc.Tax), Money(0m)) : null,
            Fiscal: fiscal is null ? null : Qr(fiscal, issuer, sent!, doc),
            NotValidAsInvoice: fiscal is null && source.FiscalExpected,
            Legends: legends);
    }

    /// <summary>Letra del comprobante según su código en la tabla de ARCA. A con leyenda (51 a 54) es clase A.</summary>
    public static string? LetterOf(int typeCode) => typeCode switch
    {
        1 or 2 or 3 or 4 or 5 or 51 or 52 or 53 or 54 => "A",
        6 or 7 or 8 or 9 or 10 => "B",
        11 or 12 or 13 or 15 => "C",
        19 or 20 or 21 => "E",
        _ => null,
    };

    /// <summary>Leyenda de la condición frente al IVA (RG 1415, anexo II), por código de la tabla de ARCA.</summary>
    public static string? VatConditionLabel(int code, bool receiver) => code switch
    {
        1 => "IVA RESPONSABLE INSCRIPTO",
        4 => "IVA EXENTO",
        5 => receiver ? "A CONSUMIDOR FINAL" : "CONSUMIDOR FINAL",
        6 => "RESPONSABLE MONOTRIBUTO",
        7 => "SUJETO NO CATEGORIZADO",
        8 => "PROVEEDOR DEL EXTERIOR",
        9 => "CLIENTE DEL EXTERIOR",
        10 => "IVA LIBERADO LEY 19.640",
        13 => "MONOTRIBUTISTA SOCIAL",
        15 => "NO RESPONSABLE IVA",
        16 => "MONOTRIBUTO TRABAJADOR INDEPENDIENTE PROMOVIDO",
        _ => null,
    };

    /// <summary>
    /// Lo que le falta al emisor para imprimir un comprobante con CAE (RG 1415, anexo II): razón social real, CUIT,
    /// domicilio comercial, ingresos brutos e inicio de actividades. Vacío si está completo.
    /// </summary>
    public static IReadOnlyList<string> MissingIssuerData(IssuerProfile? issuer)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(issuer?.LegalName) || issuer.LegalName == IssuerProfile.ImportedName)
        {
            missing.Add("razón social");
        }

        if ((issuer?.TaxId ?? string.Empty).Count(char.IsAsciiDigit) != 11)
        {
            missing.Add("CUIT");
        }

        if (string.IsNullOrWhiteSpace(issuer?.Address))
        {
            missing.Add("domicilio comercial");
        }

        if (string.IsNullOrWhiteSpace(issuer?.GrossIncomeNumber))
        {
            missing.Add("ingresos brutos");
        }

        if (issuer?.ActivityStart is null)
        {
            missing.Add("inicio de actividades");
        }

        return missing;
    }

    private static PrintParty IssuerParty(IssuerProfile? issuer) => issuer is null
        ? new PrintParty(string.Empty, null, null, null)
        : new PrintParty(
            issuer.LegalName,
            Cuit(issuer.TaxId),
            VatConditionLabel(issuer.VatCondition, receiver: false),
            issuer.Address,
            issuer.GrossIncomeNumber,
            issuer.ActivityStart is { } start ? Day(start) : null);

    private static PrintParty ReceiverParty(ReceiverSnapshot r, Voucher? sent, string? letter, int vatCondition)
    {
        if (letter == "E")
        {
            var ids = new[] { sent?.ForeignTaxId is { } id ? $"Id. fiscal {id}" : null, sent?.CountryTaxId is { } c ? $"CUIT del país {c}" : null };
            return new PrintParty(r.LegalName, string.Join(" · ", ids.Where(x => x is not null)) is { Length: > 0 } text ? text : null,
                "IVA EXENTO OPERACIÓN DE EXPORTACIÓN", sent?.Address ?? r.Address);
        }

        var docType = sent?.DocType ?? DocTypeCode(r.DocumentType);
        var number = sent?.DocNumber ?? r.DocumentNumber;
        string? taxId = docType switch
        {
            80 => $"CUIT {Cuit(number)}",
            86 => $"CUIL {Cuit(number)}",
            96 => $"DNI {number}",
            94 => $"Pasaporte {number}",
            _ => null,
        };
        return new PrintParty(r.LegalName, number is { Length: > 0 } and not "0" ? taxId : null, VatConditionLabel(vatCondition, receiver: true), r.Address);
    }

    private static PrintFiscal Qr(FiscalDocument fiscal, IssuerProfile? issuer, Voucher sent, Document doc)
    {
        var where = string.Create(CultureInfo.InvariantCulture, $"{fiscal.TypeCode}-{fiscal.PointOfSale}-{fiscal.Number}");
        if (!long.TryParse(fiscal.AuthorizationCode, NumberStyles.None, CultureInfo.InvariantCulture, out var cae))
        {
            throw new InvalidOperationException($"El comprobante {where} no tiene un CAE numérico: no se puede armar el QR.");
        }

        var cuit = new string((issuer?.TaxId ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (cuit.Length != 11)
        {
            throw new InvalidOperationException($"El emisor del comprobante {where} no tiene una CUIT válida: no se puede armar el QR.");
        }

        var currency = sent.Currency ?? doc.Currency.Code switch { "ARS" => "PES", "USD" => "DOL", var c => c };
        var rate = currency == "PES" ? 1m : sent.ExchangeRate
            ?? throw new InvalidOperationException($"Falta la cotización que registró el fisco para el comprobante {where}: el QR la exige (RG 4892).");
        var receiverNumber = sent.DocType is { } t and not 99 && long.TryParse(sent.DocNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : (long?)null;
        var data = new FiscalQrData(
            fiscal.Date,
            long.Parse(cuit, CultureInfo.InvariantCulture),
            fiscal.PointOfSale,
            fiscal.TypeCode,
            fiscal.Number,
            sent.Total ?? doc.Total,
            currency,
            rate,
            receiverNumber is null ? null : sent.DocType,
            receiverNumber,
            cae);
        var url = FiscalQr.Url(data);
        return new PrintFiscal(fiscal.AuthorizationCode, fiscal.AuthorizationDue is { } due ? Day(due) : null, url, FiscalQr.ImageDataUri(url));
    }

    private static int DocTypeCode(TaxDocumentType type) => type switch
    {
        TaxDocumentType.Cuit => 80,
        TaxDocumentType.Cuil => 86,
        TaxDocumentType.Dni => 96,
        TaxDocumentType.Passport => 94,
        _ => 99,
    };

    private static string Cuit(string value) =>
        value is { Length: 11 } && value.All(char.IsAsciiDigit) ? $"{value[..2]}-{value[2..10]}-{value[10]}" : value;

    private static string Rate(decimal? rate, TaxTreatment treatment) => treatment switch
    {
        TaxTreatment.Exempt => "Exento",
        TaxTreatment.NotTaxed => "No gravado",
        _ => Percent(rate ?? 0m),
    };

    private static string Percent(decimal rate) => (rate * 100m).ToString("0.##", Numbers) + " %";

    private static string Day(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// Lo que se mandó al fisco, en las dos formas que hay: la del núcleo, con el nivel "document", y la plana de los
    /// comprobantes importados (D-17).
    /// </summary>
    private sealed record Voucher(
        string? Currency,
        decimal? ExchangeRate,
        bool? SameCurrency,
        decimal? Total,
        int? DocType,
        string? DocNumber,
        int? VatCondition,
        DateOnly? PeriodFrom,
        DateOnly? PeriodTo,
        DateOnly? PaymentDue,
        string? PaymentTerms,
        string? Associated,
        string? ForeignTaxId,
        string? CountryTaxId,
        string? Address)
    {
        public static Voucher Parse(string payload)
        {
            var root = JsonNode.Parse(payload);
            var d = root?["document"] ?? root;
            var r = d?["receiver"] ?? d;
            var a = d?["associated"]?.AsArray().FirstOrDefault();
            return new Voucher(
                Text(d?["currency"]),
                Dec(d?["exchange_rate"]),
                d?["same_currency_payment"] is JsonValue s && s.TryGetValue<bool>(out var same) ? same : null,
                Dec(d?["amounts"]?["total"] ?? d?["total"]),
                (int?)Dec(r?["doc_type"]),
                Text(r?["doc_number"]),
                (int?)Dec(r?["vat_condition"]),
                Date(d?["service_period"]?["from"]),
                Date(d?["service_period"]?["to"]),
                Date(d?["payment_due"]),
                Text(d?["payment_terms"]),
                a is null ? null : AssociatedText(a),
                Text(r?["tax_id"]),
                Text(r?["country_tax_id"]),
                Text(r?["address"]));
        }

        private static string AssociatedText(JsonNode a)
        {
            var type = (int)(Dec(a["type_code"]) ?? 0);
            var name = type switch { 1 or 6 or 11 or 19 or 51 => "Factura", 2 or 7 or 12 or 20 or 52 => "Nota de débito", 3 or 8 or 13 or 21 or 53 => "Nota de crédito", _ => "Comprobante" };
            var text = string.Create(CultureInfo.InvariantCulture, $"{name} {LetterOf(type)} {Dec(a["point_of_sale"]):00000}-{Dec(a["number"]):00000000}");
            return Date(a["date"]) is { } date ? $"{text} del {Day(date)}" : text;
        }

        private static string? Text(JsonNode? n) => n is JsonValue v ? v.ToString() : null;

        private static decimal? Dec(JsonNode? n) => n switch
        {
            JsonValue v when v.TryGetValue<decimal>(out var d) => d,
            JsonValue v when v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };

        private static DateOnly? Date(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<string>(out var s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
