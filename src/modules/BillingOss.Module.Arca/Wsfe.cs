using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Arca;

/// <summary>WSFEv1 (manual del desarrollador RG 4291, versión 4.7): armado de pedidos y lectura de respuestas.</summary>
internal static class Wsfe
{
    public static readonly XNamespace Ns = "http://ar.gov.afip.dif.FEV1/";

    /// <summary>Nombre del servicio que se pide a WSAA.</summary>
    public const string Service = "wsfe";

    /// <summary>Número de comprobante fuera de secuencia (10016): el comprobante pudo haberse autorizado antes.</summary>
    private const int OutOfSequence = 10016;

    public static XElement E(string name, object? value) => new(Ns + name, value);

    /// <summary>Pedido de una operación: el Auth con el ticket más lo propio de cada método.</summary>
    public static XElement Request(string operation, Ticket ticket, string cuit, params object?[] content) =>
        new(Ns + operation, new XElement(Ns + "Auth", E("Token", ticket.Token), E("Sign", ticket.Sign), E("Cuit", cuit)), content);

    public static int ConceptCode(string? concept) => concept switch
    {
        "products" => 1,
        "services" => 2,
        "products_and_services" => 3,
        _ => 0,
    };

    private static string? ConceptName(string? code) => code switch
    {
        "1" => "products",
        "2" => "services",
        "3" => "products_and_services",
        _ => null,
    };

    // ---- Errores ----

    public static List<Upstream> Errors(XElement result) =>
        result.Child("Errors")?.Children("Err").Select(Upstream.From).ToList() ?? [];

    /// <summary>
    /// Observaciones de un comprobante. Los ejemplos del manual las traen como Observaciones/Obs y el esquema como
    /// Obs/Observaciones: vale el elemento que lleva el Code, sea cual sea el anidado.
    /// </summary>
    private static List<Upstream> Observations(XElement? parent) =>
        parent?.Descendants().Where(e => e.Name.LocalName is "Obs" or "Observaciones" && e.Child("Code") is not null).Select(Upstream.From).ToList() ?? [];

    /// <summary>Para las operaciones de consulta: cualquier error de ARCA corta la operación.</summary>
    public static void ThrowIfErrors(XElement result)
    {
        var errors = Errors(result);
        if (errors.Count > 0)
        {
            throw Map(errors);
        }
    }

    /// <summary>
    /// Errores de ARCA en operaciones que no emiten. 600 y 601 son de ticket o CUIT, 500 a 502 de infraestructura
    /// y 602 es "no hay datos". El resto son reglas de ARCA.
    /// </summary>
    public static ModuleException Map(IReadOnlyCollection<Upstream> errors)
    {
        var text = Fail.Describe(errors);
        if (errors.Any(e => e.Code is 600 or 601))
        {
            return new ModuleException(ErrorCodes.InvalidConfig, $"ARCA no aceptó el ticket o la CUIT: {text}");
        }

        if (errors.Any(e => e.Code == 602))
        {
            return new ModuleException(ErrorCodes.NotFound, text, details: Fail.Codes(errors, null));
        }

        if (errors.Any(e => e.Code is 500 or 501 or 502))
        {
            return new ModuleException(ErrorCodes.UpstreamUnavailable, $"ARCA tuvo un error interno: {text}", 30, Fail.Codes(errors, null));
        }

        return Fail.Rejected($"ARCA rechazó la consulta: {text}", errors);
    }

    // ---- Validación de la entrada ----

    /// <summary>Campos que faltan o están mal en un documento de WSFEv1. Vacía si está todo.</summary>
    public static List<string> Validate(AuthorizeDocument d)
    {
        var bad = new List<string>();
        void Check(bool ok, string field)
        {
            if (!ok)
            {
                bad.Add(field);
            }
        }

        Check(d.PointOfSale is >= 1 and <= 99998, "document.point_of_sale");
        Check(d.TypeCode > 0, "document.type_code");
        Check(d.Number is >= 1 and <= 99_999_999, "document.number");
        Check(d.Date is not null, "document.date");
        var concept = ConceptCode(d.Concept);
        Check(concept != 0, "document.concept");
        if (concept is 2 or 3)
        {
            // Para servicios ARCA exige período y vencimiento de pago, y el vencimiento no puede ser anterior al comprobante.
            Check(d.ServicePeriod is { From: not null, To: not null } p && p.From <= p.To, "document.service_period");
            Check(d.PaymentDue is not null && d.PaymentDue >= d.Date, "document.payment_due");
        }

        // El período asociado de una nota que revierte percepciones: las dos fechas, en orden.
        Check(d.PeriodAssoc is null || (d.PeriodAssoc is { From: not null, To: not null } pa && pa.From <= pa.To), "document.period_assoc");

        var r = d.Receiver;
        Check(r?.DocType is >= 0, "document.receiver.doc_type");
        Check(r?.DocNumber is { Length: >= 1 and <= 11 } n && n.All(char.IsAsciiDigit), "document.receiver.doc_number");
        Check(r?.VatCondition is > 0, "document.receiver.vat_condition");
        Check(d.Currency is { Length: 3 }, "document.currency");

        // MonCotiz es obligatorio salvo con CanMisMonExt = S (validación 10038): ahí se omite y ARCA registra la del día hábil anterior.
        Check(
            (d.ExchangeRate is > 0 and < 10000m && decimal.Round(d.ExchangeRate.Value, 6) == d.ExchangeRate)
            || (d.ExchangeRate is null && d.SameCurrencyPayment == true && d.Currency != "PES"),
            "document.exchange_rate");

        var a = d.Amounts;
        Check(a is not null, "document.amounts");
        if (a is not null)
        {
            Check(Fmt.IsAmount(a.Total), "document.amounts.total");
            Check(Fmt.IsAmount(a.NetTaxed), "document.amounts.net_taxed");
            Check(Fmt.IsAmount(a.NetUntaxed), "document.amounts.net_untaxed");
            Check(Fmt.IsAmount(a.Exempt), "document.amounts.exempt");

            // Otros tributos: el total (ImpTrib) tiene que ser la suma del detalle, que va al array de tributos.
            var taxes = a.Taxes ?? [];
            Check(Fmt.IsAmount(a.OtherTaxes) && a.OtherTaxes == taxes.Sum(t => t.Amount), "document.amounts.other_taxes");
            for (var i = 0; i < taxes.Count; i++)
            {
                var t = taxes[i];
                Check(t.Id > 0, $"document.amounts.taxes[{i}].id");
                Check(Xml.Clean(t.Description) is { Length: >= 1 and <= 80 }, $"document.amounts.taxes[{i}].description");
                Check(Fmt.IsAmount(t.Base), $"document.amounts.taxes[{i}].base");
                Check(t.Rate >= 0m && decimal.Round(t.Rate, 2) == t.Rate, $"document.amounts.taxes[{i}].rate");
                Check(Fmt.IsAmount(t.Amount), $"document.amounts.taxes[{i}].amount");
            }

            for (var i = 0; i < (a.Vat?.Count ?? 0); i++)
            {
                var v = a.Vat![i];
                Check(v.RateId > 0, $"document.amounts.vat[{i}].rate_id");
                Check(Fmt.IsAmount(v.Base), $"document.amounts.vat[{i}].base");
                Check(Fmt.IsAmount(v.Amount), $"document.amounts.vat[{i}].amount");
            }
        }

        for (var i = 0; i < (d.Associated?.Count ?? 0); i++)
        {
            var x = d.Associated![i];
            Check(x.TypeCode > 0 && x.PointOfSale is >= 1 and <= 99998 && x.Number > 0, $"document.associated[{i}]");

            // El CUIT del asociado es numérico de 11 dígitos, como en WSFEX.
            Check(string.IsNullOrWhiteSpace(x.TaxId) || (x.TaxId.Trim() is { Length: 11 } taxId && taxId.All(char.IsAsciiDigit)), $"document.associated[{i}].tax_id");
        }

        for (var i = 0; i < (d.Optional?.Count ?? 0); i++)
        {
            Check(!string.IsNullOrEmpty(d.Optional![i].Id) && !string.IsNullOrEmpty(d.Optional[i].Value), $"document.optional[{i}]");
        }

        return bad;
    }

    // ---- FECAESolicitar ----

    /// <summary>FECAESolicitar con un solo comprobante: CbteDesde y CbteHasta son el número que propone el núcleo.</summary>
    public static XElement BuildAuthorize(Ticket ticket, string cuit, AuthorizeDocument d)
    {
        var concept = ConceptCode(d.Concept);
        var a = d.Amounts!;
        var r = d.Receiver!;
        var vat = a.Vat ?? [];
        var taxes = a.Taxes ?? [];
        var associated = d.Associated ?? [];
        var optional = d.Optional ?? [];

        // El orden de los campos es el del manual.
        var detail = new XElement(
            Ns + "FECAEDetRequest",
            E("Concepto", concept),
            E("DocTipo", r.DocType),
            E("DocNro", long.Parse(r.DocNumber!, CultureInfo.InvariantCulture)),
            E("CbteDesde", d.Number),
            E("CbteHasta", d.Number),
            E("CbteFch", Fmt.Ymd(d.Date)),
            E("ImpTotal", Fmt.Amount(a.Total!.Value)),
            E("ImpTotConc", Fmt.Amount(a.NetUntaxed)),
            E("ImpNeto", Fmt.Amount(a.NetTaxed)),
            E("ImpOpEx", Fmt.Amount(a.Exempt)),
            E("ImpTrib", Fmt.Amount(a.OtherTaxes)),
            E("ImpIVA", Fmt.Amount(vat.Sum(v => v.Amount))),
            concept is 2 or 3 ? E("FchServDesde", Fmt.Ymd(d.ServicePeriod!.From)) : null,
            concept is 2 or 3 ? E("FchServHasta", Fmt.Ymd(d.ServicePeriod!.To)) : null,
            concept is 2 or 3 ? E("FchVtoPago", Fmt.Ymd(d.PaymentDue)) : null,
            E("MonId", d.Currency),

            // Sin cotización (cancelación en la misma moneda) ARCA registra la oficial del día hábil anterior.
            d.ExchangeRate is { } rate ? E("MonCotiz", Fmt.Number(rate)) : null,

            // Con moneda extranjera se informa si se cancela en la misma moneda (RG 5616). Con pesos no se informa.
            d.Currency != "PES" && d.SameCurrencyPayment is { } same ? E("CanMisMonExt", same ? "S" : "N") : null,
            E("CondicionIVAReceptorId", r.VatCondition),
            associated.Count == 0
                ? null
                : new XElement(
                    Ns + "CbtesAsoc",
                    associated.Select(x => new XElement(
                        Ns + "CbteAsoc",
                        E("Tipo", x.TypeCode),
                        E("PtoVta", x.PointOfSale),
                        E("Nro", x.Number),
                        x.TaxId is { Length: > 0 } ? E("Cuit", x.TaxId.Trim()) : null,
                        x.Date is not null ? E("CbteFch", Fmt.Ymd(x.Date)) : null))),
            taxes.Count == 0
                ? null
                : new XElement(
                    Ns + "Tributos",
                    taxes.Select(t => new XElement(Ns + "Tributo", E("Id", t.Id), E("Desc", Xml.Clean(t.Description)), E("BaseImp", Fmt.Amount(t.Base)), E("Alic", Fmt.Amount(t.Rate)), E("Importe", Fmt.Amount(t.Amount))))),
            vat.Count == 0
                ? null
                : new XElement(
                    Ns + "Iva",
                    vat.Select(v => new XElement(Ns + "AlicIva", E("Id", v.RateId), E("BaseImp", Fmt.Amount(v.Base)), E("Importe", Fmt.Amount(v.Amount))))),
            optional.Count == 0
                ? null
                : new XElement(
                    Ns + "Opcionales",
                    optional.Select(o => new XElement(Ns + "Opcional", E("Id", o.Id), E("Valor", o.Value)))),
            d.PeriodAssoc is { From: not null, To: not null } period
                ? new XElement(Ns + "PeriodoAsoc", E("FchDesde", Fmt.Ymd(period.From)), E("FchHasta", Fmt.Ymd(period.To)))
                : null);

        return Request(
            "FECAESolicitar",
            ticket,
            cuit,
            new XElement(
                Ns + "FeCAEReq",
                new XElement(Ns + "FeCabReq", E("CantReg", 1), E("PtoVta", d.PointOfSale), E("CbteTipo", d.TypeCode)),
                new XElement(Ns + "FeDetReq", detail)));
    }

    /// <summary>
    /// Decide qué pasó con el pedido. Autorizado devuelve el resultado. Rechazado, incierto o de configuración sale como
    /// error. El 10016 nunca es un rechazo: el comprobante pudo haberse autorizado en un intento anterior.
    /// </summary>
    public static JsonObject ReadAuthorize(XElement result, AuthorizeDocument doc)
    {
        var errors = Errors(result);
        var detail = result.Child("FeDetResp")?.Elements().FirstOrDefault();
        var observations = Observations(detail);
        var codes = errors.Concat(observations).ToList();
        var outcome = detail?.Text("Resultado") ?? result.Child("FeCabResp")?.Text("Resultado");
        var cae = detail?.Text("CAE");

        if (outcome == "A" && cae is not null)
        {
            return new JsonObject
            {
                ["authorization_code"] = cae,
                ["authorization_due"] = Fmt.Iso(Fmt.ParseYmd(detail!.Text("CAEFchVto"))),
                ["point_of_sale"] = doc.PointOfSale,
                ["type_code"] = doc.TypeCode,
                ["number"] = doc.Number,
                ["observations"] = Observed(observations),
            };
        }

        if (codes.Any(c => c.Code == OutOfSequence))
        {
            throw Fail.Unknown(
                $"ARCA informa que el número {doc.Number} no es el que sigue en la serie: {Fail.Describe(codes)} El comprobante pudo haberse autorizado en un intento anterior, hay que consultarlo antes de repetir.",
                "number_out_of_sequence",
                codes);
        }

        if (errors.Any(e => e.Code is 500 or 501 or 502))
        {
            throw Fail.Unknown(
                $"ARCA tuvo un error interno y no se sabe si autorizó el comprobante: {Fail.Describe(errors)}",
                "upstream_internal_error",
                errors);
        }

        if (errors.Any(e => e.Code is 600 or 601))
        {
            throw new ModuleException(ErrorCodes.InvalidConfig, $"ARCA no aceptó el ticket o la CUIT: {Fail.Describe(errors)}");
        }

        // Con errores generales y sin resultado de detalle ARCA no procesó el comprobante: nada quedó autorizado.
        if (outcome == "R" || (outcome is null && errors.Count > 0))
        {
            throw Fail.Rejected(
                codes.Count == 0 ? "ARCA rechazó el comprobante sin informar el motivo." : $"ARCA rechazó el comprobante: {Fail.Describe(codes)}",
                codes);
        }

        throw Fail.Unknown(
            "ARCA devolvió una respuesta que no permite saber si autorizó el comprobante.",
            "unexpected_response",
            codes);
    }

    // ---- Consultas ----

    /// <summary>FECompUltimoAutorizado: el número del último comprobante autorizado. 0 si todavía no hay.</summary>
    public static long ReadLastNumber(XElement result)
    {
        ThrowIfErrors(result);
        return long.TryParse(result.Text("CbteNro"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ModuleException(ErrorCodes.UpstreamUnavailable, "ARCA no informó el último número autorizado.", 30);
    }

    /// <summary>Lee FECompConsultar. Si ARCA no tiene el comprobante, not_found.</summary>
    public static JsonObject ReadFind(XElement result, DocumentRef key)
    {
        var errors = Errors(result);
        var found = result.Child("ResultGet");
        if (errors.Any(e => e.Code == 602) || (errors.Count == 0 && found?.Text("CbteDesde") is null))
        {
            throw new ModuleException(ErrorCodes.NotFound, $"ARCA no tiene el comprobante {key.PointOfSale}-{key.Number} del tipo {key.TypeCode}.");
        }

        ThrowIfErrors(result);
        var g = found!;
        var vat = g.Child("Iva")?.Children("AlicIva").Select(v => new JsonObject
        {
            ["rate_id"] = int.TryParse(v.Text("Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0,
            ["base"] = Money(v.Text("BaseImp")),
            ["amount"] = Money(v.Text("Importe")),
        }).ToArray<JsonNode?>() ?? [];
        var from = Fmt.ParseYmd(g.Text("FchServDesde"));
        var to = Fmt.ParseYmd(g.Text("FchServHasta"));
        var vatCondition = g.Text("CondicionIVAReceptorId");

        return new JsonObject
        {
            ["document"] = new JsonObject
            {
                ["point_of_sale"] = key.PointOfSale,
                ["type_code"] = key.TypeCode,
                ["number"] = key.Number,
                ["date"] = Fmt.Iso(Fmt.ParseYmd(g.Text("CbteFch"))),
                ["concept"] = ConceptName(g.Text("Concepto")),
                ["service_period"] = from is null || to is null ? null : new JsonObject { ["from"] = Fmt.Iso(from), ["to"] = Fmt.Iso(to) },
                ["payment_due"] = Fmt.Iso(Fmt.ParseYmd(g.Text("FchVtoPago"))),
                ["receiver"] = new JsonObject
                {
                    ["doc_type"] = int.TryParse(g.Text("DocTipo"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var docType) ? docType : null,
                    ["doc_number"] = g.Text("DocNro"),
                    ["vat_condition"] = int.TryParse(vatCondition, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vc) ? vc : null,
                },
                ["currency"] = g.Text("MonId"),
                ["exchange_rate"] = Fmt.ParseDecimal(g.Text("MonCotiz")) is { } rate ? Fmt.Number(rate) : null,
                ["amounts"] = new JsonObject
                {
                    ["net_taxed"] = Money(g.Text("ImpNeto")),
                    ["net_untaxed"] = Money(g.Text("ImpTotConc")),
                    ["exempt"] = Money(g.Text("ImpOpEx")),
                    ["vat"] = new JsonArray(vat),
                    ["other_taxes"] = Money(g.Text("ImpTrib")),
                    ["taxes"] = new JsonArray(g.Child("Tributos")?.Children("Tributo").Select(t => (JsonNode?)new JsonObject
                    {
                        ["id"] = int.TryParse(t.Text("Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var taxId) ? taxId : 0,
                        ["description"] = t.Text("Desc"),
                        ["base"] = Money(t.Text("BaseImp")),
                        ["rate"] = Fmt.ParseDecimal(t.Text("Alic")) is { } alic ? Fmt.Number(alic) : null,
                        ["amount"] = Money(t.Text("Importe")),
                    }).ToArray() ?? []),
                    ["total"] = Money(g.Text("ImpTotal")),
                },
            },
            ["authorization_code"] = g.Text("CodAutorizacion"),
            ["authorization_due"] = Fmt.Iso(Fmt.ParseYmd(g.Text("FchVto"))),
            ["result"] = g.Text("Resultado"),
            ["emission_type"] = g.Text("EmisionTipo"),
            ["observations"] = Observed(Observations(g)),
        };
    }

    // ---- Tablas de parámetros ----

    /// <summary>Filas de una tabla FEParamGet*: Id, Desc y vigencia. FchHasta viene como "NULL" cuando no vence.</summary>
    public static JsonArray ReadTable(XElement result, string item, bool numericId)
    {
        ThrowIfErrors(result);
        var rows = result.Child("ResultGet")?.Children(item).Select(row => (JsonNode?)new JsonObject
        {
            ["id"] = numericId && int.TryParse(row.Text("Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : row.Text("Id"),
            ["description"] = row.Text("Desc"),
            ["valid_from"] = Fmt.Iso(Fmt.ParseYmd(row.Text("FchDesde"))),
            ["valid_to"] = Fmt.Iso(Fmt.ParseYmd(row.Text("FchHasta"))),
        }).ToArray();
        return new JsonArray(rows ?? []);
    }

    /// <summary>Condiciones frente al IVA del receptor: una fila por condición y clase de comprobante.</summary>
    public static JsonArray ReadVatConditions(XElement result)
    {
        ThrowIfErrors(result);
        var rows = result.Child("ResultGet")?.Children("CondicionIvaReceptor").Select(row => (JsonNode?)new JsonObject
        {
            ["id"] = int.TryParse(row.Text("Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null,
            ["description"] = row.Text("Desc"),
            ["voucher_class"] = row.Text("Cmp_Clase"),
        }).ToArray();
        return new JsonArray(rows ?? []);
    }

    /// <summary>
    /// FEParamGetPtosVenta: número, tipo de emisión (CAE o CAEA), si está bloqueado y la fecha de baja. Sin resultado
    /// (ARCA responde 602 cuando no hay puntos) es una lista vacía.
    /// </summary>
    public static JsonArray ReadPointsOfSale(XElement? result)
    {
        if (result is null)
        {
            return [];
        }

        ThrowIfErrors(result);
        var rows = result.Child("ResultGet")?.Children("PtoVenta").Select(row => (JsonNode?)new JsonObject
        {
            ["id"] = int.TryParse(row.Text("Nro"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null,
            ["emission_type"] = row.Text("EmisionTipo"),
            ["blocked"] = row.Text("Bloqueado") == "S",
            ["retired_on"] = Fmt.Iso(Fmt.ParseYmd(row.Text("FchBaja"))),
        }).ToArray();
        return new JsonArray(rows ?? []);
    }

    /// <summary>Lee FEParamGetCotizacion. Una cotización ausente o en cero es "sin cotización": not_found.</summary>
    public static JsonObject ReadExchangeRate(XElement result, string currency)
    {
        ThrowIfErrors(result);
        return Fmt.ParseDecimal(result.Child("ResultGet")?.Text("MonCotiz")) is { } rate and > 0
            ? new JsonObject
            {
                ["currency"] = currency,
                ["rate"] = Fmt.Number(rate),
                ["date"] = Fmt.Iso(Fmt.ParseYmd(result.Child("ResultGet")?.Text("FchCotiz"))),
            }
            : throw new ModuleException(ErrorCodes.NotFound, $"ARCA no tiene cotización para {currency}.");
    }

    /// <summary>FEDummy: AppServer, DbServer y AuthServer tienen que decir OK.</summary>
    public static JsonObject ReadDummy(XElement result)
    {
        var servers = new JsonObject
        {
            ["app_server"] = result.Text("AppServer"),
            ["db_server"] = result.Text("DbServer"),
            ["auth_server"] = result.Text("AuthServer"),
        };
        return servers.All(s => s.Value?.GetValue<string>() == "OK")
            ? servers
            : throw new ModuleException(ErrorCodes.UpstreamUnavailable, $"ARCA informa servidores caídos: {servers.ToJsonString()}", 60);
    }

    // ---- Utilidades ----

    private static JsonArray Observed(IEnumerable<Upstream> observations) =>
        new(observations.Select(o => (JsonNode?)new JsonObject { ["code"] = o.Code, ["message"] = o.Message }).ToArray());

    /// <summary>Importe de ARCA a texto con dos decimales.</summary>
    private static string? Money(string? text) => Fmt.ParseDecimal(text) is { } value ? Fmt.Amount(value) : null;
}
