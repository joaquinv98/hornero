using System.Text.Json.Nodes;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Arca.Tests;

/// <summary>Armado de pedidos y lectura de respuestas de WSFEv1, con respuestas sintéticas escritas según el manual 4.7.</summary>
[TestClass]
public sealed class WsfeTests
{
    private static readonly Ticket Ticket = new("TOKEN", "SIGN", new DateTimeOffset(2026, 10, 27, 3, 0, 0, TimeSpan.Zero), "20000000001", "homologation");

    private static XElement Result(string xml, string name) => Xml.Parse(xml).Descendants().First(e => e.Name.LocalName == name);

    private static string Value(XElement parent, string name) => parent.Elements().Single(e => e.Name.LocalName == name).Value;

    private static XElement Build(JsonObject input) => Wsfe.BuildAuthorize(Ticket, "20000000001", Docs.Parse(input).Document!);

    [TestMethod]
    public void Authorize_ArmaElPedidoDelEjemploDelProtocolo()
    {
        var request = Build(Docs.Invoice());

        Assert.AreEqual(Wsfe.Ns + "FECAESolicitar", request.Name);
        var auth = request.Element(Wsfe.Ns + "Auth")!;
        Assert.AreEqual("TOKEN", Value(auth, "Token"));
        Assert.AreEqual("SIGN", Value(auth, "Sign"));
        Assert.AreEqual("20000000001", Value(auth, "Cuit"));

        var header = request.Descendants(Wsfe.Ns + "FeCabReq").Single();
        Assert.AreEqual("1", Value(header, "CantReg"));
        Assert.AreEqual("3", Value(header, "PtoVta"));
        Assert.AreEqual("6", Value(header, "CbteTipo"));

        // El orden de los campos es el del manual.
        var detail = request.Descendants(Wsfe.Ns + "FECAEDetRequest").Single();
        string[] fields =
        [
            "Concepto", "DocTipo", "DocNro", "CbteDesde", "CbteHasta", "CbteFch", "ImpTotal", "ImpTotConc", "ImpNeto", "ImpOpEx",
            "ImpTrib", "ImpIVA", "FchServDesde", "FchServHasta", "FchVtoPago", "MonId", "MonCotiz", "CondicionIVAReceptorId", "Iva",
        ];
        CollectionAssert.AreEqual(fields, detail.Elements().Select(e => e.Name.LocalName).ToArray());

        Assert.AreEqual("2", Value(detail, "Concepto"));
        Assert.AreEqual("96", Value(detail, "DocTipo"));
        Assert.AreEqual("30111222", Value(detail, "DocNro"));
        Assert.AreEqual("1488", Value(detail, "CbteDesde"));
        Assert.AreEqual("1488", Value(detail, "CbteHasta"));
        Assert.AreEqual("20261025", Value(detail, "CbteFch"));
        Assert.AreEqual("12100.00", Value(detail, "ImpTotal"));
        Assert.AreEqual("0.00", Value(detail, "ImpTotConc"));
        Assert.AreEqual("10000.00", Value(detail, "ImpNeto"));
        Assert.AreEqual("0.00", Value(detail, "ImpOpEx"));
        Assert.AreEqual("0.00", Value(detail, "ImpTrib"));
        Assert.AreEqual("2100.00", Value(detail, "ImpIVA"));

        // El período de servicio es inclusivo y va tal cual: desde el 1 hasta el 31.
        Assert.AreEqual("20261001", Value(detail, "FchServDesde"));
        Assert.AreEqual("20261031", Value(detail, "FchServHasta"));
        Assert.AreEqual("20261105", Value(detail, "FchVtoPago"));
        Assert.AreEqual("PES", Value(detail, "MonId"));
        Assert.AreEqual("1", Value(detail, "MonCotiz"));
        Assert.AreEqual("5", Value(detail, "CondicionIVAReceptorId"));
        var vat = detail.Descendants(Wsfe.Ns + "AlicIva").Single();
        Assert.AreEqual("5", Value(vat, "Id"));
        Assert.AreEqual("10000.00", Value(vat, "BaseImp"));
        Assert.AreEqual("2100.00", Value(vat, "Importe"));

        // Todo el pedido está en el espacio de nombres de WSFEv1: ningún elemento vuelve a declararlo vacío.
        Assert.DoesNotContain("xmlns=\"\"", request.ToString(SaveOptions.DisableFormatting));
    }

    [TestMethod]
    public void Authorize_ProductosNoLlevaFechasDeServicio()
    {
        var input = Docs.Invoice();
        input["document"]!["concept"] = "products";

        var detail = Build(input).Descendants(Wsfe.Ns + "FECAEDetRequest").Single();

        Assert.AreEqual("1", Value(detail, "Concepto"));
        Assert.IsEmpty(detail.Elements().Where(e => e.Name.LocalName.StartsWith("Fch", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Authorize_ConMonedaExtranjeraInformaSiSeCancelaEnLaMismaMoneda()
    {
        var input = Docs.Invoice();
        input["document"]!["currency"] = "DOL";
        input["document"]!["exchange_rate"] = "1450.25";
        input["document"]!["same_currency_payment"] = true;

        var detail = Build(input).Descendants(Wsfe.Ns + "FECAEDetRequest").Single();

        Assert.AreEqual("1450.25", Value(detail, "MonCotiz"));
        Assert.AreEqual("S", Value(detail, "CanMisMonExt"));

        // Con pesos el campo no se informa (validación 10241 del manual).
        var pesos = Docs.Invoice();
        pesos["document"]!["same_currency_payment"] = true;
        Assert.IsEmpty(Build(pesos).Descendants(Wsfe.Ns + "CanMisMonExt"));
    }

    [TestMethod]
    public void Authorize_ConCancelacionEnLaMismaMoneda_OmiteLaCotizacion()
    {
        // Con CanMisMonExt = S la cotización se omite y ARCA registra la del día hábil anterior (validación 10038).
        var input = Docs.Invoice();
        input["document"]!["currency"] = "DOL";
        input["document"]!["same_currency_payment"] = true;
        ((JsonObject)input["document"]!).Remove("exchange_rate");

        var detail = Build(input).Descendants(Wsfe.Ns + "FECAEDetRequest").Single();

        Assert.IsEmpty(detail.Elements(Wsfe.Ns + "MonCotiz"));
        Assert.AreEqual("S", Value(detail, "CanMisMonExt"));
        Assert.IsLessThan(detail.Elements().Select(e => e.Name.LocalName).ToList().IndexOf("CanMisMonExt"), detail.Elements().Select(e => e.Name.LocalName).ToList().IndexOf("MonId"));
    }

    [TestMethod]
    public void Validate_SinCotizacion_SoloPasaConCancelacionEnLaMismaMonedaExtranjera()
    {
        var same = Docs.Invoice();
        same["document"]!["currency"] = "DOL";
        same["document"]!["same_currency_payment"] = true;
        ((JsonObject)same["document"]!).Remove("exchange_rate");
        var other = Docs.Invoice();
        other["document"]!["currency"] = "DOL";
        other["document"]!["same_currency_payment"] = false;
        ((JsonObject)other["document"]!).Remove("exchange_rate");
        var pesos = Docs.Invoice();
        pesos["document"]!["same_currency_payment"] = true;
        ((JsonObject)pesos["document"]!).Remove("exchange_rate");

        Assert.IsEmpty(Wsfe.Validate(Docs.Parse(same).Document!));
        Assert.Contains("document.exchange_rate", Wsfe.Validate(Docs.Parse(other).Document!));
        Assert.Contains("document.exchange_rate", Wsfe.Validate(Docs.Parse(pesos).Document!));
    }

    [TestMethod]
    public void Validate_ElCuitDelAsociadoVaConOnceDigitos()
    {
        var input = Docs.Invoice();
        input["document"]!["type_code"] = 8;
        input["document"]!["associated"] = new JsonArray(new JsonObject { ["type_code"] = 6, ["point_of_sale"] = 3, ["number"] = 1400, ["tax_id"] = "20-00000000-1" });

        Assert.Contains("document.associated[0].tax_id", Wsfe.Validate(Docs.Parse(input).Document!));

        input["document"]!["associated"]![0]!["tax_id"] = "20000000001";
        Assert.IsEmpty(Wsfe.Validate(Docs.Parse(input).Document!));
    }

    [TestMethod]
    public void Authorize_NotaDeCredito_LlevaElComprobanteAsociadoYLosOpcionales()
    {
        var input = Docs.Invoice();
        input["document"]!["type_code"] = 8;
        input["document"]!["associated"] = new JsonArray(new JsonObject { ["type_code"] = 6, ["point_of_sale"] = 3, ["number"] = 1400, ["tax_id"] = "20000000001", ["date"] = "2026-10-01" });
        input["document"]!["optional"] = new JsonArray(new JsonObject { ["id"] = "17", ["value"] = "2" });

        var detail = Build(input).Descendants(Wsfe.Ns + "FECAEDetRequest").Single();

        var associated = detail.Descendants(Wsfe.Ns + "CbteAsoc").Single();
        Assert.AreEqual("6", Value(associated, "Tipo"));
        Assert.AreEqual("3", Value(associated, "PtoVta"));
        Assert.AreEqual("1400", Value(associated, "Nro"));
        Assert.AreEqual("20000000001", Value(associated, "Cuit"));
        Assert.AreEqual("20261001", Value(associated, "CbteFch"));
        var optional = detail.Descendants(Wsfe.Ns + "Opcional").Single();
        Assert.AreEqual("17", Value(optional, "Id"));
        Assert.AreEqual("2", Value(optional, "Valor"));

        // Los asociados van antes que el IVA y los opcionales después, como en el manual.
        var order = detail.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.IsLessThan(order.IndexOf("Iva"), order.IndexOf("CbtesAsoc"));
        Assert.IsGreaterThan(order.IndexOf("Iva"), order.IndexOf("Opcionales"));
    }

    [TestMethod]
    public void Validate_ConUnDocumentoCompleto_NoObjeta()
    {
        Assert.IsEmpty(Wsfe.Validate(Docs.Parse(Docs.Invoice()).Document!));
    }

    [TestMethod]
    public void Validate_ListaLosCamposQueFaltanOEstanMal()
    {
        var input = Docs.Invoice();
        var document = input["document"]!;
        document["concept"] = "otro";
        document["receiver"] = new JsonObject { ["doc_type"] = 96, ["doc_number"] = "12AB" };
        document["exchange_rate"] = "0";
        document["amounts"]!["total"] = "12100.005";
        document["amounts"]!["other_taxes"] = "5.00";
        document["amounts"]!["vat"] = new JsonArray(new JsonObject { ["rate_id"] = 0, ["base"] = "1.00", ["amount"] = "0.21" });

        var bad = Wsfe.Validate(Docs.Parse(input).Document!);

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "document.concept",
                "document.receiver.doc_number",
                "document.receiver.vat_condition",
                "document.exchange_rate",
                "document.amounts.total",
                "document.amounts.other_taxes",
                "document.amounts.vat[0].rate_id",
            },
            bad);
    }

    [TestMethod]
    public void Validate_ParaServiciosExigePeriodoYVencimientoDePago()
    {
        var input = Docs.Invoice();
        ((JsonObject)input["document"]!).Remove("service_period");
        input["document"]!["payment_due"] = "2026-10-01";

        var bad = Wsfe.Validate(Docs.Parse(input).Document!);

        CollectionAssert.IsSubsetOf(new[] { "document.service_period", "document.payment_due" }, bad);
    }

    [TestMethod]
    public void ReadAuthorize_Autorizado_DevuelveCodigoVencimientoYObservaciones()
    {
        var observation = "<Observaciones><Obs><Code>10245</Code><Msg>La condicion frente al IVA del receptor es obligatoria</Msg></Obs></Observaciones>";
        var result = Result(Samples.Authorized(observations: observation), "FECAESolicitarResult");

        var json = Wsfe.ReadAuthorize(result, Docs.Parse(Docs.Invoice()).Document!);

        Assert.AreEqual("71234567890123", json["authorization_code"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05", json["authorization_due"]!.GetValue<string>());
        Assert.AreEqual(1488, json["number"]!.GetValue<long>());
        Assert.AreEqual(10245, json["observations"]![0]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public void ReadAuthorize_LasObservacionesVanEnCualquieraDeLosDosAnidados()
    {
        // El esquema del manual las anida como Obs/Observaciones y los ejemplos como Observaciones/Obs.
        var schema = "<Obs><Observaciones><Code>10063</Code><Msg>Observación</Msg></Observaciones></Obs>";
        var result = Result(Samples.Authorized(observations: schema), "FECAESolicitarResult");

        var json = Wsfe.ReadAuthorize(result, Docs.Parse(Docs.Invoice()).Document!);

        Assert.AreEqual(10063, json["observations"]![0]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public void ReadFind_LeeElComprobanteYSuCodigo()
    {
        var json = Wsfe.ReadFind(Result(Samples.Found(), "FECompConsultarResult"), new DocumentRef(3, 6, 1488));

        var document = json["document"]!;
        Assert.AreEqual("2026-10-25", document["date"]!.GetValue<string>());
        Assert.AreEqual("services", document["concept"]!.GetValue<string>());
        Assert.AreEqual("2026-10-01", document["service_period"]!["from"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05", document["payment_due"]!.GetValue<string>());
        Assert.AreEqual(96, document["receiver"]!["doc_type"]!.GetValue<int>());
        Assert.AreEqual("30111222", document["receiver"]!["doc_number"]!.GetValue<string>());

        // El WSDL escribe la condición de IVA de la respuesta como CondicionIvaReceptorId, distinto al pedido.
        Assert.AreEqual(5, document["receiver"]!["vat_condition"]!.GetValue<int>());
        Assert.AreEqual("PES", document["currency"]!.GetValue<string>());

        // Los importes salen siempre como texto con dos decimales.
        Assert.AreEqual("12100.00", document["amounts"]!["total"]!.GetValue<string>());
        Assert.AreEqual("10000.00", document["amounts"]!["net_taxed"]!.GetValue<string>());
        Assert.AreEqual(5, document["amounts"]!["vat"]![0]!["rate_id"]!.GetValue<int>());
        Assert.AreEqual("2100.00", document["amounts"]!["vat"]![0]!["amount"]!.GetValue<string>());
        Assert.AreEqual("71234567890123", json["authorization_code"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05", json["authorization_due"]!.GetValue<string>());
        Assert.AreEqual("CAE", json["emission_type"]!.GetValue<string>());
    }

    [TestMethod]
    public void ReadFind_SiARCANoLoTiene_EsNotFound()
    {
        var key = new DocumentRef(3, 6, 99);
        var withError = Result(Samples.ErrorsOnly("FECompConsultar", (602, "No existen datos en nuestros registros")), "FECompConsultarResult");
        var empty = Result($"""<FECompConsultarResponse xmlns="{Samples.FeNs}"><FECompConsultarResult><ResultGet/></FECompConsultarResult></FECompConsultarResponse>""", "FECompConsultarResult");

        Assert.AreEqual(ErrorCodes.NotFound, Assert.ThrowsExactly<ModuleException>(() => Wsfe.ReadFind(withError, key)).Code);
        Assert.AreEqual(ErrorCodes.NotFound, Assert.ThrowsExactly<ModuleException>(() => Wsfe.ReadFind(empty, key)).Code);
    }

    [TestMethod]
    public void ReadTable_LeeFilasYTomaNULLComoSinVencimiento()
    {
        var xml = Samples.Table(
            "FEParamGetTiposCbte",
            "CbteTipo",
            ("1", "Factura A", "20100917", "NULL"),
            ("4", "Recibos A", "20100917", "20261231"));

        var rows = Wsfe.ReadTable(Result(xml, "FEParamGetTiposCbteResult"), "CbteTipo", numericId: true);

        Assert.HasCount(2, rows);
        Assert.AreEqual(1, rows[0]!["id"]!.GetValue<int>());
        Assert.AreEqual("Factura A", rows[0]!["description"]!.GetValue<string>());
        Assert.AreEqual("2010-09-17", rows[0]!["valid_from"]!.GetValue<string>());
        Assert.IsNull(rows[0]!["valid_to"]);
        Assert.AreEqual("2026-12-31", rows[1]!["valid_to"]!.GetValue<string>());
    }

    [TestMethod]
    public void ReadTable_LasMonedasTienenIdDeTexto()
    {
        var xml = Samples.Table("FEParamGetTiposMonedas", "Moneda", ("PES", "Pesos Argentinos", "20090403", "NULL"), ("060", "Euro", "20090403", "NULL"));

        var rows = Wsfe.ReadTable(Result(xml, "FEParamGetTiposMonedasResult"), "Moneda", numericId: false);

        Assert.AreEqual("PES", rows[0]!["id"]!.GetValue<string>());
        Assert.AreEqual("060", rows[1]!["id"]!.GetValue<string>());
    }

    [TestMethod]
    public void ReadVatConditions_UnaFilaPorCondicionYClase()
    {
        var rows = Wsfe.ReadVatConditions(Result(Samples.VatConditions, "FEParamGetCondicionIvaReceptorResult"));

        Assert.HasCount(2, rows);
        Assert.AreEqual(5, rows[1]!["id"]!.GetValue<int>());
        Assert.AreEqual("Consumidor Final", rows[1]!["description"]!.GetValue<string>());
        Assert.AreEqual("B", rows[1]!["voucher_class"]!.GetValue<string>());
    }

    [TestMethod]
    public void ReadExchangeRate_DevuelveLaCotizacionComoTexto()
    {
        var json = Wsfe.ReadExchangeRate(Result(Samples.Exchange("DOL", "1450.250000", "20261023"), "FEParamGetCotizacionResult"), "DOL");

        Assert.AreEqual("DOL", json["currency"]!.GetValue<string>());
        Assert.AreEqual("1450.25", json["rate"]!.GetValue<string>());
        Assert.AreEqual("2026-10-23", json["date"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("")]
    public void ReadExchangeRate_ConUnaCotizacionEnCeroOVacia_EsNotFound(string rate)
    {
        // Es lo que ARCA puede devolver para un día sin cotización: no es una cotización que se pueda usar.
        var empty = Result(Samples.Exchange("DOL", rate, string.Empty), "FEParamGetCotizacionResult");

        Assert.AreEqual(ErrorCodes.NotFound, Assert.ThrowsExactly<ModuleException>(() => Wsfe.ReadExchangeRate(empty, "DOL")).Code);
    }

    [TestMethod]
    public void ReadLastNumber_Lee0CuandoNoHayComprobantes()
    {
        Assert.AreEqual(0, Wsfe.ReadLastNumber(Result(Samples.LastAuthorized(0), "FECompUltimoAutorizadoResult")));
        Assert.AreEqual(1487, Wsfe.ReadLastNumber(Result(Samples.LastAuthorized(1487), "FECompUltimoAutorizadoResult")));
    }

    [TestMethod]
    public void ReadDummy_SiUnServidorNoDiceOK_NoEstaDisponible()
    {
        var down = Samples.Dummy.Replace("<DbServer>OK</DbServer>", "<DbServer>ERROR</DbServer>", StringComparison.Ordinal);

        Assert.AreEqual("OK", Wsfe.ReadDummy(Result(Samples.Dummy, "FEDummyResult"))["db_server"]!.GetValue<string>());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, Assert.ThrowsExactly<ModuleException>(() => Wsfe.ReadDummy(Result(down, "FEDummyResult"))).Code);
    }
}
