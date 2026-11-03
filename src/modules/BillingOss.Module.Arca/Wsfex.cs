using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Arca;

/// <summary>
/// WSFEX v1 (manual 3.1.1): factura de exportación de servicios (tipo 19) y sus notas de débito (20) y de crédito (21).
/// A diferencia de WSFEv1 tiene idempotencia: el mismo Id de pedido devuelve el mismo CAE con Reproceso = S.
/// </summary>
internal static class Wsfex
{
    public static readonly XNamespace Ns = "http://ar.gov.afip.dif.fexv1/";

    /// <summary>Nombre del servicio que se pide a WSAA.</summary>
    public const string Service = "wsfex";

    /// <summary>Factura de exportación E. Es la única con fecha de pago y CanMisMonExt, y no lleva comprobantes asociados.</summary>
    public const int Invoice = 19;

    /// <summary>Nota de débito por operaciones con el exterior. Asocia una factura.</summary>
    private const int DebitNote = 20;

    /// <summary>Nota de crédito por operaciones con el exterior. Asocia una factura o una nota de débito.</summary>
    private const int CreditNote = 21;

    /// <summary>1535: el comprobante no corresponde en secuencia al próximo a autorizar.</summary>
    private const int OutOfSequence = 1535;

    /// <summary>Unidad de medida de los renglones que no traen una: 7, "unidades" en la tabla de ARCA.</summary>
    private const int DefaultUnit = 7;

    private static readonly Regex Digits = new(@"\d+", RegexOptions.CultureInvariant);

    public static bool IsExportType(int typeCode) => typeCode is 19 or 20 or 21;

    public static XElement E(string name, object? value) => new(Ns + name, value);

    private static XElement Auth(Ticket ticket, string cuit, params object?[] extra) =>
        new(Ns + "Auth", E("Token", ticket.Token), E("Sign", ticket.Sign), E("Cuit", cuit), extra);

    public static XElement Request(string operation, Ticket ticket, string cuit, params object?[] content) =>
        new(Ns + operation, Auth(ticket, cuit), content);

    // ---- Validación de la entrada ----

    /// <summary>Campos que faltan o están mal en un comprobante de exportación (19, 20 o 21). Vacía si está todo.</summary>
    public static List<string> Validate(AuthorizeInput input)
    {
        var d = input.Document!;
        var invoice = d.TypeCode == Invoice;
        var bad = new List<string>();
        void Check(bool ok, string field)
        {
            if (!ok)
            {
                bad.Add(field);
            }
        }

        Check(d.PointOfSale is >= 1 and <= 99998, "document.point_of_sale");
        Check(d.Number is >= 1 and <= 99_999_999, "document.number");
        Check(d.Date is not null, "document.date");

        // El ID de pedido es de 15 dígitos y lo manda el núcleo, que lo guarda antes de llamar.
        Check(input.WsfexId is > 0 and < 1_000_000_000_000_000, "wsfex_id");

        // Fecha_pago es solo de la factura: para servicios es obligatoria y no puede ser anterior al comprobante (errores
        // 1672 y 1674). Una nota no la informa (1673), así que su payment_due no se mira ni se envía.
        Check(!invoice || (d.PaymentDue is not null && d.PaymentDue >= d.Date), "document.payment_due");
        var r = d.Receiver;
        Check(Length(r?.Name, 200), "document.receiver.name");
        Check(Length(r?.Address, 300), "document.receiver.address");
        Check(r?.CountryCode is > 0 and <= 999, "document.receiver.country_code");

        // Alcanza con el CUIT del país o con la identificación tributaria del comprador (error 1580).
        Check(r?.CountryTaxId is > 0 || (!string.IsNullOrWhiteSpace(r?.TaxId) && r.TaxId.Length <= 50), "document.receiver.tax_id");
        Check(d.Currency is { Length: 3 }, "document.currency");

        // Moneda_ctz es obligatoria salvo que se informe CanMisMonExt = S (error 1602). Como solo la factura lo informa,
        // solo ella puede omitirla (en moneda extranjera, con same_currency_payment) y ARCA usa su cotización. Las notas
        // siempre la llevan, y ARCA exige que sea la del comprobante asociado (2053).
        Check(
            (d.ExchangeRate is > 0 and < 10000m && decimal.Round(d.ExchangeRate.Value, 6) == d.ExchangeRate)
            || (invoice && d.ExchangeRate is null && d.SameCurrencyPayment == true && d.Currency != "PES"),
            "document.exchange_rate");

        // Forma_pago es obligatoria solo en la factura.
        Check(Length(d.PaymentTerms, 50) || (!invoice && string.IsNullOrWhiteSpace(d.PaymentTerms)), "document.payment_terms");
        Check(LanguageCode(d.Language) != 0, "document.language");
        Check(Fmt.IsAmount(d.Amounts?.Total), "document.amounts.total");

        // Comprobantes asociados (manual 3.1.1, sección 2.1.6, validaciones de Cmps_asoc): la factura no informa ninguno
        // (error 1755) y la nota de débito o de crédito de servicios informa uno y solo uno (1754 y 2047). Una nota de débito
        // asocia una factura. Una de crédito, una factura o una nota de débito, nunca una de crédito (2040 a 2046).
        var associated = d.Associated ?? [];
        Check(associated.Count == (invoice ? 0 : 1), "document.associated");
        for (var i = 0; !invoice && i < associated.Count; i++)
        {
            var x = associated[i];
            Check(x.TypeCode == Invoice || (d.TypeCode == CreditNote && x.TypeCode == DebitNote), $"document.associated[{i}].type_code");
            Check(x.PointOfSale is >= 1 and <= 99998, $"document.associated[{i}].point_of_sale");
            Check(x.Number is >= 1 and <= 999_999_999, $"document.associated[{i}].number");

            // Cbte_cuit es numérico de 11 dígitos. Si no viene se usa el de la instancia.
            Check(string.IsNullOrWhiteSpace(x.TaxId) || (x.TaxId.Trim() is { Length: 11 } taxId && taxId.All(char.IsAsciiDigit)), $"document.associated[{i}].tax_id");
        }

        var items = d.Items ?? [];
        Check(items.Count is >= 1 and <= 9999, "document.items");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            Check(Length(item.Description, 4000), $"document.items[{i}].description");
            Check(item.UnitCode is null or (>= 0 and <= 99), $"document.items[{i}].unit_code");

            // El total del renglón puede ser negativo en renglones de descuento, por eso no usa IsAmount.
            Check(item.Total is { } t && decimal.Round(t, 2) == t, $"document.items[{i}].total");
            Check(item.Code is null || item.Code.Length <= 50, $"document.items[{i}].code");
            Check(Fmt.IsAmount(item.Quantity, 6) || item.Quantity is null, $"document.items[{i}].quantity");
            Check(Fmt.IsAmount(item.UnitPrice, 6) || item.UnitPrice is null, $"document.items[{i}].unit_price");
            Check(Fmt.IsAmount(item.Discount, 6) || item.Discount is null, $"document.items[{i}].discount");
        }

        return bad;

        static bool Length(string? text, int max) => Xml.Clean(text) is { Length: > 0 } clean && clean.Length <= max;
    }

    /// <summary>Idioma del comprobante: 1 español, 2 inglés y 3 portugués. 0 si no es ninguno.</summary>
    private static int LanguageCode(string? language) => language switch
    {
        "es" => 1,
        "en" => 2,
        "pt" => 3,
        _ => 0,
    };

    // ---- FEXAuthorize ----

    public static XElement BuildAuthorize(Ticket ticket, string cuit, AuthorizeInput input)
    {
        var d = input.Document!;
        var r = d.Receiver!;
        var invoice = d.TypeCode == Invoice;
        var associated = d.Associated ?? [];

        // El orden de los campos es el del manual (sección 2.1.3) y el de ClsFEXRequest en el WSDL, ambos leídos el
        // 2026-10-26. Para servicios (Tipo_expo 2) no se informan permisos de embarque. Cmps_asoc va entre Obs y Forma_pago
        // y solo lo llevan las notas, que lo exigen (error 2047). Fecha_pago y CanMisMonExt son solo de la factura: con
        // una nota ARCA rechaza que se informen (errores 1673 y 1605).
        return Request(
            "FEXAuthorize",
            ticket,
            cuit,
            new XElement(
                Ns + "Cmp",
                E("Id", input.WsfexId),
                E("Fecha_cbte", Fmt.Ymd(d.Date)),
                E("Cbte_Tipo", d.TypeCode),
                E("Punto_vta", d.PointOfSale),
                E("Cbte_nro", d.Number),
                E("Tipo_expo", 2),
                E("Permiso_existente", string.Empty),
                E("Dst_cmp", r.CountryCode),
                E("Cliente", Xml.Clean(r.Name)),
                r.CountryTaxId is > 0 ? E("Cuit_pais_cliente", r.CountryTaxId) : null,
                E("Domicilio_cliente", Xml.Clean(r.Address)),
                !string.IsNullOrWhiteSpace(r.TaxId) ? E("Id_impositivo", Xml.Clean(r.TaxId)) : null,
                E("Moneda_Id", d.Currency),
                d.ExchangeRate is { } rate ? E("Moneda_ctz", Fmt.Number(rate)) : null,

                // Con pesos tampoco se informa CanMisMonExt (1605).
                invoice && d.Currency != "PES" && d.SameCurrencyPayment is { } same ? E("CanMisMonExt", same ? "S" : "N") : null,
                E("Imp_total", Fmt.Amount(d.Amounts!.Total!.Value)),

                // Cbte_cuit: el CUIT del emisor del asociado. ARCA exige que sea el del emisor del comprobante (2031 y 2032).
                associated.Count == 0
                    ? null
                    : new XElement(
                        Ns + "Cmps_asoc",
                        associated.Select(x => new XElement(
                            Ns + "Cmp_asoc",
                            E("Cbte_tipo", x.TypeCode),
                            E("Cbte_punto_vta", x.PointOfSale),
                            E("Cbte_nro", x.Number),
                            E("Cbte_cuit", string.IsNullOrWhiteSpace(x.TaxId) ? cuit : x.TaxId.Trim())))),
                Xml.Clean(d.PaymentTerms) is { Length: > 0 } terms ? E("Forma_pago", terms) : null,
                E("Idioma_cbte", LanguageCode(d.Language)),
                new XElement(
                    Ns + "Items",
                    d.Items!.Select(i => new XElement(
                        Ns + "Item",
                        string.IsNullOrWhiteSpace(i.Code) ? null : E("Pro_codigo", Xml.Clean(i.Code)),
                        E("Pro_ds", Xml.Clean(i.Description)),
                        i.Quantity is { } quantity ? E("Pro_qty", Fmt.Number(quantity)) : null,
                        E("Pro_umed", i.UnitCode ?? DefaultUnit),
                        i.UnitPrice is { } price ? E("Pro_precio_uni", Fmt.Number(price)) : null,
                        i.Discount is { } discount ? E("Pro_bonificacion", Fmt.Number(discount)) : null,
                        E("Pro_total_item", Fmt.Amount(i.Total!.Value))))),
                invoice ? E("Fecha_pago", Fmt.Ymd(d.PaymentDue)) : null));
    }

    /// <summary>
    /// Decide qué pasó con el pedido. Con el mismo Id ARCA devuelve lo que ya autorizó (Reproceso = S), así que acá
    /// un corte se repite sin riesgo. El 1535 (fuera de secuencia) sigue siendo incierto, igual que el 10016 de WSFEv1.
    /// </summary>
    public static JsonObject ReadAuthorize(XElement result, AuthorizeInput input)
    {
        var d = input.Document!;
        var error = ReadError(result);
        var auth = result.Child("FEXResultAuth");
        var outcome = auth?.Text("Resultado");
        var cae = auth?.Text("Cae");
        var observed = Reasons(auth?.Text("Motivos_Obs"));

        if (error is not null)
        {
            Upstream[] errors = [error];
            if (error.Code is OutOfSequence or 500 or 501 or 502 or 505)
            {
                throw Fail.Unknown(
                    $"ARCA no confirmó el comprobante: {Fail.Describe(errors)} Puede haberse autorizado en un intento anterior, hay que consultarlo antes de repetir.",
                    error.Code == OutOfSequence ? "number_out_of_sequence" : "upstream_internal_error",
                    errors);
            }

            if (error.Code is 1000 or 1001)
            {
                throw new ModuleException(ErrorCodes.InvalidConfig, $"ARCA no aceptó el ticket o la CUIT: {Fail.Describe(errors)}");
            }

            throw Fail.Rejected($"ARCA rechazó el comprobante: {Fail.Describe(errors)}", errors);
        }

        if (outcome is "A" or "O" && cae is not null && auth is not null)
        {
            // Si el Id ya estaba usado por otro comprobante, ARCA devuelve aquel y no este.
            if (Differs(auth.Text("Cbte_nro"), d.Number) || Differs(auth.Text("Cbte_Tipo"), d.TypeCode) || Differs(auth.Text("Punto_vta"), d.PointOfSale))
            {
                throw Fail.Rejected(
                    $"ARCA devolvió otro comprobante para el ID de pedido {input.WsfexId}: ese ID ya se usó. Hay que mandar un ID nuevo.",
                    [new Upstream(0, "wsfex_id_in_use")]);
            }

            return new JsonObject
            {
                ["authorization_code"] = cae,
                ["authorization_due"] = Fmt.Iso(Fmt.ParseYmd(auth.Text("Fch_venc_Cae"))),
                ["point_of_sale"] = d.PointOfSale,
                ["type_code"] = d.TypeCode,
                ["number"] = d.Number,
                ["observations"] = Observed(observed),
                ["reprocessed"] = auth.Text("Reproceso") == "S",
            };
        }

        if (outcome == "R")
        {
            throw Fail.Rejected(
                observed.Count == 0 ? "ARCA rechazó el comprobante sin informar el motivo." : $"ARCA rechazó el comprobante: {Fail.Describe(observed)}",
                observed);
        }

        throw Fail.Unknown("ARCA devolvió una respuesta que no permite saber si autorizó el comprobante.", "unexpected_response", observed);

        static bool Differs(string? text, long expected) =>
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value != expected;
    }

    // ---- Consultas ----

    /// <summary>FEXGetLast_CMP trae el número y la fecha del último comprobante en la misma respuesta.</summary>
    public static XElement BuildLast(Ticket ticket, string cuit, DocumentRef key) =>
        new(
            Ns + "FEXGetLast_CMP",
            Auth(ticket, cuit, E("Pto_venta", key.PointOfSale), E("Cbte_Tipo", key.TypeCode)));

    public static (long Number, DateOnly? Date) ReadLast(XElement result)
    {
        ThrowIfError(result);
        var last = result.Child("FEXResult_LastCMP");
        return long.TryParse(last?.Text("Cbte_nro"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? (number, Fmt.ParseYmd(last?.Text("Cbte_fecha")))
            : throw new ModuleException(ErrorCodes.UpstreamUnavailable, "ARCA no informó el último número autorizado.", 30);
    }

    /// <summary>
    /// FEXGetLast_ID: el último ID de pedido que recibió ARCA para el CUIT, sea del punto de venta y el tipo que sea.
    /// Solo lleva Auth. El núcleo propone el ID siguiente sumándole uno.
    /// </summary>
    public static XElement BuildLastId(Ticket ticket, string cuit) => Request("FEXGetLast_ID", ticket, cuit);

    public static long ReadLastId(XElement result)
    {
        ThrowIfError(result);
        return long.TryParse(result.Child("FEXResultGet")?.Text("Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id >= 0
            ? id
            : throw new ModuleException(ErrorCodes.UpstreamUnavailable, "ARCA no informó el último ID de pedido.", 30);
    }

    /// <summary>
    /// FEXGetPARAM_Ctz (manual 3.1.1, sección 2.13): Mon_id y FchCotiz van al lado de Auth. A diferencia de WSFEv1, la
    /// fecha es yyyy-MM-dd (error 1003). Sin fecha ARCA devuelve la última cotización de su base aduanera.
    /// </summary>
    public static XElement BuildExchangeRate(Ticket ticket, string cuit, string currency, DateOnly? date) =>
        Request("FEXGetPARAM_Ctz", ticket, cuit, E("Mon_id", currency), date is null ? null : E("FchCotiz", Fmt.Iso(date)));

    /// <summary>Lee FEXGetPARAM_Ctz con la misma forma que la cotización de WSFEv1. La fecha de Mon_fecha es yyyymmdd.</summary>
    public static JsonObject ReadExchangeRate(XElement result, string currency)
    {
        ThrowIfError(result);
        var found = result.Child("FEXResultGet");
        return Fmt.ParseDecimal(found?.Text("Mon_ctz")) is { } rate and > 0
            ? new JsonObject
            {
                ["currency"] = currency,
                ["rate"] = Fmt.Number(rate),
                ["date"] = Fmt.Iso(Fmt.ParseYmd(found?.Text("Mon_fecha"))),
            }
            : throw new ModuleException(ErrorCodes.NotFound, $"ARCA no tiene cotización para {currency}.");
    }

    /// <summary>FEXGetCMP: el WSDL pide Cbte_tipo con la t en minúscula, a diferencia del manual y del resto de los pedidos.</summary>
    public static XElement BuildFind(Ticket ticket, string cuit, DocumentRef key) =>
        Request(
            "FEXGetCMP",
            ticket,
            cuit,
            new XElement(Ns + "Cmp", E("Cbte_tipo", key.TypeCode), E("Punto_vta", key.PointOfSale), E("Cbte_nro", key.Number)));

    /// <summary>Lee FEXGetCMP. El error 1020 es "comprobante inexistente".</summary>
    public static JsonObject ReadFind(XElement result, DocumentRef key)
    {
        ThrowIfError(result);
        var g = result.Child("FEXResultGet")
            ?? throw new ModuleException(ErrorCodes.NotFound, $"ARCA no tiene el comprobante {key.PointOfSale}-{key.Number} del tipo {key.TypeCode}.");
        return new JsonObject
        {
            ["document"] = new JsonObject
            {
                ["point_of_sale"] = key.PointOfSale,
                ["type_code"] = key.TypeCode,
                ["number"] = key.Number,
                ["date"] = Fmt.Iso(Fmt.ParseYmd(g.Text("Fecha_cbte"))),
                ["payment_due"] = Fmt.Iso(Fmt.ParseYmd(g.Text("Fecha_pago"))),
                ["receiver"] = new JsonObject
                {
                    ["name"] = g.Text("Cliente"),
                    ["address"] = g.Text("Domicilio_cliente"),
                    ["country_code"] = int.TryParse(g.Text("Dst_cmp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var country) ? country : null,
                    ["country_tax_id"] = g.Text("Cuit_pais_cliente"),
                    ["tax_id"] = g.Text("Id_impositivo"),
                },
                ["currency"] = g.Text("Moneda_Id"),
                ["exchange_rate"] = Fmt.ParseDecimal(g.Text("Moneda_ctz")) is { } rate ? Fmt.Number(rate) : null,
                ["amounts"] = new JsonObject
                {
                    ["total"] = Fmt.ParseDecimal(g.Text("Imp_total")) is { } total ? Fmt.Amount(total) : null,
                },
            },
            ["wsfex_id"] = g.Text("Id"),
            ["authorization_code"] = g.Text("Cae"),
            ["authorization_due"] = Fmt.Iso(Fmt.ParseYmd(g.Text("Fch_venc_Cae"))),
            ["result"] = g.Text("Resultado"),
            ["observations"] = Observed(Reasons(g.Text("Motivos_Obs"))),
        };
    }

    /// <summary>FEXGetPARAM_PtoVenta: los puntos de venta de exportación, con la misma forma que los de WSFEv1. Sin resultado es una lista vacía.</summary>
    public static JsonArray ReadPointsOfSale(XElement? result)
    {
        if (result is null)
        {
            return [];
        }

        ThrowIfError(result);
        var rows = result.Child("FEXResultGet")?.Children("ClsFEXResponse_PtoVenta").Select(row => (JsonNode?)new JsonObject
        {
            ["id"] = int.TryParse(row.Text("Pve_Nro"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null,
            ["blocked"] = row.Text("Pve_Bloqueado") == "S",
            ["retired_on"] = Fmt.Iso(Fmt.ParseYmd(row.Text("Pve_FchBaja"))),
        }).ToArray();
        return new JsonArray(rows ?? []);
    }

    // ---- Errores ----

    /// <summary>FEXErr con código distinto de 0. ARCA manda el elemento aun cuando todo salió bien (0 es OK).</summary>
    private static Upstream? ReadError(XElement result)
    {
        var error = result.Child("FEXErr");
        return int.TryParse(error?.Text("ErrCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code != 0
            ? new Upstream(code, error!.Text("ErrMsg") ?? string.Empty)
            : null;
    }

    /// <summary>Errores de ARCA en las consultas: 1000 y 1001 son de ticket o CUIT, 1020 es "inexistente" y 500 a 505 de infraestructura.</summary>
    private static void ThrowIfError(XElement result)
    {
        if (ReadError(result) is not { } error)
        {
            return;
        }

        Upstream[] errors = [error];
        var text = Fail.Describe(errors);
        throw error.Code switch
        {
            1000 or 1001 => new ModuleException(ErrorCodes.InvalidConfig, $"ARCA no aceptó el ticket o la CUIT: {text}"),
            1020 => new ModuleException(ErrorCodes.NotFound, text, details: Fail.Codes(errors, null)),
            500 or 501 or 502 or 505 => new ModuleException(ErrorCodes.UpstreamUnavailable, $"ARCA tuvo un error interno: {text}", 30, Fail.Codes(errors, null)),
            _ => Fail.Rejected($"ARCA rechazó la consulta: {text}", errors),
        };
    }

    /// <summary>Motivos_Obs es un texto con los códigos de observación o de rechazo, por ejemplo "14" o "21".</summary>
    private static List<Upstream> Reasons(string? motivos) =>
        motivos is null
            ? []
            : Digits.Matches(motivos).Select(m => new Upstream(int.Parse(m.Value, CultureInfo.InvariantCulture), motivos)).ToList();

    private static JsonArray Observed(IEnumerable<Upstream> observations) =>
        new(observations.Select(o => (JsonNode?)new JsonObject { ["code"] = o.Code, ["message"] = o.Message }).ToArray());
}
