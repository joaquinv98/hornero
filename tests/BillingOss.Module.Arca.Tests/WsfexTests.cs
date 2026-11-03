using System.Text.Json.Nodes;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Arca.Tests;

/// <summary>Armado de pedidos y lectura de respuestas de WSFEX, con respuestas sintéticas escritas según el manual 3.1.1.</summary>
[TestClass]
public sealed class WsfexTests
{
    private static readonly Ticket Ticket = new("TOKEN", "SIGN", new DateTimeOffset(2026, 10, 27, 3, 0, 0, TimeSpan.Zero), "20000000001", "homologation");

    private static XElement Result(string xml, string name) => Xml.Parse(xml).Descendants().First(e => e.Name.LocalName == name);

    private static string Value(XElement parent, string name) => parent.Elements().Single(e => e.Name.LocalName == name).Value;

    [TestMethod]
    public void Authorize_ArmaElPedidoDeUnaFacturaDeServicios()
    {
        var request = Wsfex.BuildAuthorize(Ticket, "20000000001", Docs.Parse(Docs.Export()));

        Assert.AreEqual(Wsfex.Ns + "FEXAuthorize", request.Name);
        Assert.AreEqual("TOKEN", Value(request.Element(Wsfex.Ns + "Auth")!, "Token"));
        var cmp = request.Element(Wsfex.Ns + "Cmp")!;

        // El orden de los campos es el del manual.
        string[] fields =
        [
            "Id", "Fecha_cbte", "Cbte_Tipo", "Punto_vta", "Cbte_nro", "Tipo_expo", "Permiso_existente", "Dst_cmp", "Cliente",
            "Cuit_pais_cliente", "Domicilio_cliente", "Id_impositivo", "Moneda_Id", "Moneda_ctz", "CanMisMonExt", "Imp_total",
            "Forma_pago", "Idioma_cbte", "Items", "Fecha_pago",
        ];
        CollectionAssert.AreEqual(fields, cmp.Elements().Select(e => e.Name.LocalName).ToArray());

        // El ID de pedido es el que manda el núcleo.
        Assert.AreEqual("4501", Value(cmp, "Id"));
        Assert.AreEqual("20261025", Value(cmp, "Fecha_cbte"));
        Assert.AreEqual("19", Value(cmp, "Cbte_Tipo"));
        Assert.AreEqual("4", Value(cmp, "Punto_vta"));
        Assert.AreEqual("12", Value(cmp, "Cbte_nro"));

        // Servicios: Tipo_expo 2 y sin permiso de embarque.
        Assert.AreEqual("2", Value(cmp, "Tipo_expo"));
        Assert.AreEqual(string.Empty, Value(cmp, "Permiso_existente"));
        Assert.AreEqual("203", Value(cmp, "Dst_cmp"));
        Assert.AreEqual("Joao Da Silva", Value(cmp, "Cliente"));
        Assert.AreEqual("50000000016", Value(cmp, "Cuit_pais_cliente"));
        Assert.AreEqual("PJ54482221-l", Value(cmp, "Id_impositivo"));
        Assert.AreEqual("DOL", Value(cmp, "Moneda_Id"));
        Assert.AreEqual("1450.25", Value(cmp, "Moneda_ctz"));
        Assert.AreEqual("N", Value(cmp, "CanMisMonExt"));
        Assert.AreEqual("120.00", Value(cmp, "Imp_total"));
        Assert.AreEqual("Transferencia", Value(cmp, "Forma_pago"));
        Assert.AreEqual("2", Value(cmp, "Idioma_cbte"));
        Assert.AreEqual("20261105", Value(cmp, "Fecha_pago"));

        var items = cmp.Element(Wsfex.Ns + "Items")!.Elements().ToList();
        Assert.HasCount(2, items);
        string[] itemFields = ["Pro_codigo", "Pro_ds", "Pro_qty", "Pro_umed", "Pro_precio_uni", "Pro_total_item"];
        CollectionAssert.AreEqual(itemFields, items[0].Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("Hosting mensual", Value(items[0], "Pro_ds"));
        Assert.AreEqual("7", Value(items[0], "Pro_umed"));
        Assert.AreEqual("100.00", Value(items[0], "Pro_total_item"));
        Assert.DoesNotContain("xmlns=\"\"", request.ToString(SaveOptions.DisableFormatting));
    }

    [TestMethod]
    public void Authorize_ConPesosNoInformaCanMisMonExt_YSinCotizacionSiSeCancelaEnLaMismaMoneda()
    {
        var pesos = Docs.Export();
        pesos["document"]!["currency"] = "PES";
        pesos["document"]!["exchange_rate"] = "1";
        pesos["document"]!["same_currency_payment"] = true;
        Assert.IsEmpty(Wsfex.BuildAuthorize(Ticket, "20000000001", Docs.Parse(pesos)).Descendants(Wsfex.Ns + "CanMisMonExt"));

        // En dólares, "se cancela en la misma moneda" permite omitir la cotización: ARCA usa la suya.
        var dolares = Docs.Export();
        ((JsonObject)dolares["document"]!).Remove("exchange_rate");
        dolares["document"]!["same_currency_payment"] = true;
        var input = Docs.Parse(dolares);
        Assert.IsEmpty(Wsfex.Validate(input));
        var cmp = Wsfex.BuildAuthorize(Ticket, "20000000001", input).Element(Wsfex.Ns + "Cmp")!;
        Assert.AreEqual("S", Value(cmp, "CanMisMonExt"));
        Assert.IsEmpty(cmp.Elements(Wsfex.Ns + "Moneda_ctz"));
    }

    [TestMethod]
    [DataRow(20)]
    [DataRow(21)]
    public void Authorize_UnaNota_PoneCmpsAsocEntreImpTotalYFormaPago_YNoMandaFechaPagoNiCanMisMonExt(int type)
    {
        // El núcleo manda payment_due y same_currency_payment también en las notas, y la instancia tiene otro CUIT que el del asociado.
        var request = Wsfex.BuildAuthorize(Ticket, "27000000002", Docs.Parse(Docs.ExportNote(type)));

        var cmp = request.Element(Wsfex.Ns + "Cmp")!;
        string[] fields =
        [
            "Id", "Fecha_cbte", "Cbte_Tipo", "Punto_vta", "Cbte_nro", "Tipo_expo", "Permiso_existente", "Dst_cmp", "Cliente",
            "Cuit_pais_cliente", "Domicilio_cliente", "Id_impositivo", "Moneda_Id", "Moneda_ctz", "Imp_total", "Cmps_asoc",
            "Forma_pago", "Idioma_cbte", "Items",
        ];
        CollectionAssert.AreEqual(fields, cmp.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual(type.ToString(System.Globalization.CultureInfo.InvariantCulture), Value(cmp, "Cbte_Tipo"));
        Assert.AreEqual("4502", Value(cmp, "Id"));
        Assert.AreEqual("1450.25", Value(cmp, "Moneda_ctz"));
        Assert.AreEqual("20.00", Value(cmp, "Imp_total"));

        // Los cuatro campos del asociado, en el orden del manual, con las minúsculas del WSDL en Cbte_tipo.
        var associated = cmp.Element(Wsfex.Ns + "Cmps_asoc")!.Elements().ToList();
        Assert.HasCount(1, associated);
        Assert.AreEqual(Wsfex.Ns + "Cmp_asoc", associated[0].Name);
        CollectionAssert.AreEqual(new[] { "Cbte_tipo", "Cbte_punto_vta", "Cbte_nro", "Cbte_cuit" }, associated[0].Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("19", Value(associated[0], "Cbte_tipo"));
        Assert.AreEqual("4", Value(associated[0], "Cbte_punto_vta"));
        Assert.AreEqual("12", Value(associated[0], "Cbte_nro"));
        Assert.AreEqual("20000000001", Value(associated[0], "Cbte_cuit"));
        Assert.AreEqual("27000000002", Value(request.Element(Wsfex.Ns + "Auth")!, "Cuit"));
        Assert.DoesNotContain("xmlns=\"\"", request.ToString(SaveOptions.DisableFormatting));
    }

    [TestMethod]
    public void Authorize_UnAsociadoSinTaxId_UsaElCuitDeLaInstancia()
    {
        var input = Docs.ExportNote();
        ((JsonObject)input["document"]!["associated"]![0]!).Remove("tax_id");

        var cmp = Wsfex.BuildAuthorize(Ticket, "27000000002", Docs.Parse(input)).Element(Wsfex.Ns + "Cmp")!;

        Assert.AreEqual("27000000002", Value(cmp.Element(Wsfex.Ns + "Cmps_asoc")!.Elements().Single(), "Cbte_cuit"));
    }

    [TestMethod]
    public void Authorize_UnaFactura_NoLlevaCmpsAsocYSigueConFechaDePagoYCanMisMonExt()
    {
        var cmp = Wsfex.BuildAuthorize(Ticket, "20000000001", Docs.Parse(Docs.Export())).Element(Wsfex.Ns + "Cmp")!;

        Assert.IsEmpty(cmp.Elements(Wsfex.Ns + "Cmps_asoc"));
        Assert.AreEqual("20261105", Value(cmp, "Fecha_pago"));
        Assert.AreEqual("N", Value(cmp, "CanMisMonExt"));
    }

    [TestMethod]
    public void Authorize_LimpiaLosCaracteresQueXmlNoAdmite()
    {
        var input = Docs.Export();
        input["document"]!["receiver"]!["name"] = "Joao\u0001 Da \u0008Silva";

        var cmp = Wsfex.BuildAuthorize(Ticket, "20000000001", Docs.Parse(input)).Element(Wsfex.Ns + "Cmp")!;

        Assert.AreEqual("Joao Da Silva", Value(cmp, "Cliente"));
    }

    [TestMethod]
    public void Authorize_UnRenglonSinUnidadUsaLaUnidad7()
    {
        var input = Docs.Export();
        ((JsonObject)input["document"]!["items"]![0]!).Remove("unit_code");

        var parsed = Docs.Parse(input);
        var item = Wsfex.BuildAuthorize(Ticket, "20000000001", parsed).Descendants(Wsfex.Ns + "Item").First();

        Assert.IsEmpty(Wsfex.Validate(parsed));
        Assert.AreEqual("7", Value(item, "Pro_umed"));
    }

    [TestMethod]
    public void Validate_ConUnaFacturaCompleta_NoObjeta()
    {
        Assert.IsEmpty(Wsfex.Validate(Docs.Parse(Docs.Export())));
    }

    [TestMethod]
    public void Validate_ListaLoQueFaltaParaExportar()
    {
        var input = Docs.Export();
        input.Remove("wsfex_id");
        var document = (JsonObject)input["document"]!;
        document["receiver"] = new JsonObject { ["name"] = "Joao" };
        document["payment_terms"] = "";
        document["language"] = "fr";
        document["payment_due"] = "2026-10-01";
        document["items"] = new JsonArray();
        document["associated"] = new JsonArray(new JsonObject { ["type_code"] = 19, ["point_of_sale"] = 4, ["number"] = 1 });

        var bad = Wsfex.Validate(Docs.Parse(input));

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "wsfex_id",
                "document.receiver.address",
                "document.receiver.country_code",
                "document.receiver.tax_id",
                "document.payment_terms",
                "document.language",
                "document.payment_due",
                "document.items",
                "document.associated",
            },
            bad);
    }

    [TestMethod]
    public void Validate_UnRenglonDeDescuentoPuedeSerNegativo()
    {
        var input = Docs.Export();
        ((JsonArray)input["document"]!["items"]!).Add(new JsonObject { ["description"] = "Descuento", ["unit_code"] = 99, ["total"] = "-5.00" });

        Assert.IsEmpty(Wsfex.Validate(Docs.Parse(input)));
    }

    [TestMethod]
    [DataRow(20)]
    [DataRow(21)]
    public void Validate_UnaNotaCompleta_NoObjeta(int type)
    {
        Assert.IsEmpty(Wsfex.Validate(Docs.Parse(Docs.ExportNote(type))));
    }

    [TestMethod]
    public void Validate_UnaNotaNoPideFechaDePagoNiFormaDePago()
    {
        // Fecha_pago y Forma_pago solo son obligatorias en la factura (errores 1672 y 1620 del manual).
        var input = Docs.ExportNote();
        var document = (JsonObject)input["document"]!;
        document.Remove("payment_terms");
        document["payment_due"] = "2026-10-01";

        Assert.IsEmpty(Wsfex.Validate(Docs.Parse(input)));
    }

    [TestMethod]
    public void Validate_UnaNotaSiemprePideCotizacion_AunConSameCurrencyPayment()
    {
        // En la factura same_currency_payment permite omitirla porque ARCA usa la suya. La nota no informa CanMisMonExt (1602).
        var note = Docs.ExportNote();
        ((JsonObject)note["document"]!).Remove("exchange_rate");
        var invoice = Docs.Export();
        ((JsonObject)invoice["document"]!).Remove("exchange_rate");
        invoice["document"]!["same_currency_payment"] = true;

        Assert.Contains("document.exchange_rate", Wsfex.Validate(Docs.Parse(note)));
        Assert.IsEmpty(Wsfex.Validate(Docs.Parse(invoice)));
    }

    [TestMethod]
    public void Validate_UnaNotaExigeUnoYSoloUnAsociado()
    {
        var none = Docs.ExportNote();
        ((JsonObject)none["document"]!).Remove("associated");
        var two = Docs.ExportNote();
        ((JsonArray)two["document"]!["associated"]!).Add(new JsonObject { ["type_code"] = 19, ["point_of_sale"] = 4, ["number"] = 11 });

        Assert.Contains("document.associated", Wsfex.Validate(Docs.Parse(none)));
        Assert.Contains("document.associated", Wsfex.Validate(Docs.Parse(two)));
    }

    [TestMethod]
    [DataRow(20, 19, true)]
    [DataRow(20, 20, false)]
    [DataRow(20, 21, false)]
    [DataRow(21, 19, true)]
    [DataRow(21, 20, true)]
    [DataRow(21, 21, false)]
    [DataRow(21, 6, false)]
    public void Validate_ElTipoDelAsociado_DependeDeLaNota(int note, int associated, bool valid)
    {
        // Una nota de débito asocia una factura. Una de crédito, una factura o una nota de débito (errores 2040 a 2046).
        var input = Docs.ExportNote(note);
        input["document"]!["associated"]![0]!["type_code"] = associated;

        var bad = Wsfex.Validate(Docs.Parse(input));

        Assert.AreEqual(valid, bad.Count == 0, string.Join(", ", bad));
        Assert.AreEqual(valid, !bad.Contains("document.associated[0].type_code"));
    }

    [TestMethod]
    public void Validate_ListaLosCamposMalDelAsociado()
    {
        var input = Docs.ExportNote();
        var associated = (JsonObject)input["document"]!["associated"]![0]!;
        associated["point_of_sale"] = 0;
        associated["number"] = 0;
        associated["tax_id"] = "2000-0000001";

        CollectionAssert.AreEquivalent(
            new[] { "document.associated[0].point_of_sale", "document.associated[0].number", "document.associated[0].tax_id" },
            Wsfex.Validate(Docs.Parse(input)));
    }

    [TestMethod]
    public void Last_PideElNumeroConPuntoDeVentaYTipoDentroDeAuth()
    {
        var request = Wsfex.BuildLast(Ticket, "20000000001", new DocumentRef(4, 19));

        var auth = request.Element(Wsfex.Ns + "Auth")!;
        CollectionAssert.AreEqual(new[] { "Token", "Sign", "Cuit", "Pto_venta", "Cbte_Tipo" }, auth.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("4", Value(auth, "Pto_venta"));
        Assert.AreEqual("19", Value(auth, "Cbte_Tipo"));
    }

    [TestMethod]
    public void ReadLast_TraElNumeroYLaFecha()
    {
        var (number, date) = Wsfex.ReadLast(Result(Samples.ExportLast(11, "20261020"), "FEXGetLast_CMPResult"));

        Assert.AreEqual(11, number);
        Assert.AreEqual(new DateOnly(2026, 10, 20), date);
    }

    [TestMethod]
    public void LastId_SoloLlevaAuth()
    {
        var request = Wsfex.BuildLastId(Ticket, "20000000001");

        Assert.AreEqual(Wsfex.Ns + "FEXGetLast_ID", request.Name);
        CollectionAssert.AreEqual(new[] { "Auth" }, request.Elements().Select(e => e.Name.LocalName).ToArray());
        CollectionAssert.AreEqual(new[] { "Token", "Sign", "Cuit" }, request.Element(Wsfex.Ns + "Auth")!.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [TestMethod]
    [DataRow(4501L)]
    [DataRow(0L)]
    public void ReadLastId_TraElUltimoIdDePedido(long id)
    {
        Assert.AreEqual(id, Wsfex.ReadLastId(Result(Samples.ExportLastId(id), "FEXGetLast_IDResult")));
    }

    [TestMethod]
    public void ReadLastId_SinIdEnLaRespuesta_NoEstaDisponible()
    {
        var empty = Samples.ExportLastId(1).Replace("<Id>1</Id>", string.Empty, StringComparison.Ordinal);

        var error = Assert.ThrowsExactly<ModuleException>(() => Wsfex.ReadLastId(Result(empty, "FEXGetLast_IDResult")));

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, error.Code);
    }

    [TestMethod]
    public void ExchangeRate_PideMonIdYFchCotizAlLadoDeAuth_ConLaFechaConGuiones()
    {
        var request = Wsfex.BuildExchangeRate(Ticket, "20000000001", "DOL", new DateOnly(2026, 10, 23));

        Assert.AreEqual(Wsfex.Ns + "FEXGetPARAM_Ctz", request.Name);
        CollectionAssert.AreEqual(new[] { "Auth", "Mon_id", "FchCotiz" }, request.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("DOL", Value(request, "Mon_id"));

        // El manual (error 1003) pide yyyy-MM-dd en este método, a diferencia del yyyymmdd de WSFEv1.
        Assert.AreEqual("2026-10-23", Value(request, "FchCotiz"));
        Assert.IsEmpty(Wsfex.BuildExchangeRate(Ticket, "20000000001", "DOL", null).Elements(Wsfex.Ns + "FchCotiz"));
    }

    [TestMethod]
    public void ReadExchangeRate_LeeMonCtzYMonFecha_ConLaMismaFormaQueWsfev1()
    {
        var json = Wsfex.ReadExchangeRate(Result(Samples.ExportExchange("1450.250000", "20261023"), "FEXGetPARAM_CtzResult"), "DOL");

        Assert.AreEqual("DOL", json["currency"]!.GetValue<string>());
        Assert.AreEqual("1450.25", json["rate"]!.GetValue<string>());
        Assert.AreEqual("2026-10-23", json["date"]!.GetValue<string>());
    }

    [TestMethod]
    public void ReadExchangeRate_ConUnaCotizacionEnCero_EsNotFound()
    {
        var error = Assert.ThrowsExactly<ModuleException>(() => Wsfex.ReadExchangeRate(Result(Samples.ExportExchange("0", "20261023"), "FEXGetPARAM_CtzResult"), "DOL"));

        Assert.AreEqual(ErrorCodes.NotFound, error.Code);
    }

    [TestMethod]
    public void ReadFind_LeeElComprobanteYSuCae()
    {
        var json = Wsfex.ReadFind(Result(Samples.ExportFound(), "FEXGetCMPResult"), new DocumentRef(4, 19, 12));

        var document = json["document"]!;
        Assert.AreEqual("2026-10-25", document["date"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05", document["payment_due"]!.GetValue<string>());
        Assert.AreEqual("Joao Da Silva", document["receiver"]!["name"]!.GetValue<string>());
        Assert.AreEqual(203, document["receiver"]!["country_code"]!.GetValue<int>());
        Assert.AreEqual("DOL", document["currency"]!.GetValue<string>());
        Assert.AreEqual("1450.25", document["exchange_rate"]!.GetValue<string>());
        Assert.AreEqual("120.00", document["amounts"]!["total"]!.GetValue<string>());
        Assert.AreEqual("71999999999999", json["authorization_code"]!.GetValue<string>());
        Assert.AreEqual("4501", json["wsfex_id"]!.GetValue<string>());
    }

    [TestMethod]
    public void ReadFind_ComprobanteInexistente_EsNotFound()
    {
        var result = Result(Samples.ExportError("FEXGetCMP", 1020, "Comprobante inexistente"), "FEXGetCMPResult");

        Assert.AreEqual(ErrorCodes.NotFound, Assert.ThrowsExactly<ModuleException>(() => Wsfex.ReadFind(result, new DocumentRef(4, 19, 99))).Code);
    }

    [TestMethod]
    public void ReadAuthorize_LeeLosNombresSinImportarLasMayusculas()
    {
        // La respuesta del WSDL escribe Cbte_tipo y el manual Cbte_Tipo: en los dos casos tiene que detectar el comprobante equivocado.
        var other = Samples.ExportAuthorized().Replace("<Cbte_Tipo>19</Cbte_Tipo>", "<Cbte_tipo>20</Cbte_tipo>", StringComparison.Ordinal);

        var error = Assert.ThrowsExactly<ModuleException>(() => Wsfex.ReadAuthorize(Result(other, "FEXAuthorizeResult"), Docs.Parse(Docs.Export())));

        Assert.AreEqual(ErrorCodes.Rejected, error.Code);
    }

    [TestMethod]
    public void ReadAuthorize_ElErrCodeEnCeroEsQueTodoSalioBien()
    {
        var json = Wsfex.ReadAuthorize(Result(Samples.ExportAuthorized(), "FEXAuthorizeResult"), Docs.Parse(Docs.Export()));

        Assert.AreEqual("71999999999999", json["authorization_code"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05", json["authorization_due"]!.GetValue<string>());
        Assert.IsFalse(json["reprocessed"]!.GetValue<bool>());
    }
}
