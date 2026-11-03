using System.Text;
using BillingOss.App.Reports;
using BillingOss.Domain.Billing;

namespace BillingOss.App.Tests;

/// <summary>
/// Libro IVA Digital de ventas (RG 4597): cada campo en la posición del diseño de registro oficial de ARCA, con las
/// reglas de su especificación. Sin base de datos: corre siempre.
/// </summary>
[TestClass]
public sealed class LibroIvaDigitalTests
{
    private static readonly DateOnly Date = new(2026, 10, 15);

    /// <summary>Un campo por sus posiciones del diseño de registro, de 1 en adelante y las dos incluidas.</summary>
    private static string Field(string line, int from, int to) => line[(from - 1)..to];

    private static FiscalVoucher Voucher(
        int type = 6, long number = 123, DocumentKind kind = DocumentKind.Invoice, string currency = "PES", decimal? rate = 1m,
        AliquotTotal[]? aliquots = null, decimal exempt = 0m, decimal notTaxed = 0m, decimal? total = null, OtherTax[]? taxes = null,
        int docType = 99, string docNumber = "0", string name = "Cliente") =>
        new(Date, type, 3, number, kind, currency, rate, aliquots ?? [new(0.21m, 10000m, 2100m)], exempt, notTaxed,
            total ?? (aliquots ?? [new(0.21m, 10000m, 2100m)]).Sum(a => a.Net + a.Tax) + exempt + notTaxed + (taxes ?? []).Sum(t => t.Amount),
            taxes ?? [], docType, docNumber, name);

    [TestMethod]
    public void FacturaB_AConsumidorFinal_CadaCampoEnSuPosicion()
    {
        var libro = LibroIvaDigital.Build([Voucher()]);

        var line = libro.Vouchers.Single();
        Assert.HasCount(266, line);
        Assert.AreEqual("20261015", Field(line, 1, 8));
        Assert.AreEqual("006", Field(line, 9, 11));
        Assert.AreEqual("00003", Field(line, 12, 16));
        Assert.AreEqual("00000000000000000123", Field(line, 17, 36));
        Assert.AreEqual("00000000000000000123", Field(line, 37, 56), "número hasta: el mismo");
        Assert.AreEqual("99", Field(line, 57, 58));
        Assert.AreEqual(new string('0', 20), Field(line, 59, 78));
        Assert.AreEqual("CONSUMIDOR FINAL".PadRight(30), Field(line, 79, 108));
        Assert.AreEqual("000000001210000", Field(line, 109, 123), "total");
        for (var start = 124; start <= 214; start += 15)
        {
            Assert.AreEqual(new string('0', 15), Field(line, start, start + 14), $"campo en {start}");
        }

        Assert.AreEqual("PES", Field(line, 229, 231));
        Assert.AreEqual("0001000000", Field(line, 232, 241), "4 enteros y 6 decimales, también en pesos");
        Assert.AreEqual("1", Field(line, 242, 242));
        Assert.AreEqual(" ", Field(line, 243, 243), "con IVA el código de operación va en blanco");
        Assert.AreEqual(new string('0', 15), Field(line, 244, 258));
        Assert.AreEqual("00000000", Field(line, 259, 266));

        var aliquot = libro.Aliquots.Single();
        Assert.HasCount(62, aliquot);
        Assert.AreEqual("006", Field(aliquot, 1, 3));
        Assert.AreEqual("00003", Field(aliquot, 4, 8));
        Assert.AreEqual("00000000000000000123", Field(aliquot, 9, 28));
        Assert.AreEqual("000000001000000", Field(aliquot, 29, 43), "neto gravado");
        Assert.AreEqual("0005", Field(aliquot, 44, 47), "21 %");
        Assert.AreEqual("000000000210000", Field(aliquot, 48, 62), "IVA, también al consumidor final");
    }

    [TestMethod]
    public void FacturaA_ConDosAlicuotasYExento_UnRegistroPorAlicuotaEnElMismoOrden()
    {
        var first = Voucher(type: 1, number: 7, docType: 80, docNumber: "30-71234567-1", name: "Empresa S.A.",
            aliquots: [new(0.21m, 1000m, 210m), new(0.105m, 200m, 21m)], exempt: 50m);
        var second = Voucher(type: 1, number: 8, docType: 80, docNumber: "30712345671", name: "Otra");

        var libro = LibroIvaDigital.Build([first, second]);

        var line = libro.Vouchers[0];
        Assert.AreEqual("80", Field(line, 57, 58));
        Assert.AreEqual("00000000030712345671", Field(line, 59, 78), "sin guiones y con ceros adelante");
        Assert.AreEqual("Empresa S.A.".PadRight(30), Field(line, 79, 108));
        Assert.AreEqual("000000000148100", Field(line, 109, 123));
        Assert.AreEqual("000000000005000", Field(line, 154, 168), "exento");
        Assert.AreEqual("2", Field(line, 242, 242));
        Assert.AreEqual(" ", Field(line, 243, 243), "con una parte gravada, el exento no cambia el código");
        CollectionAssert.AreEqual(
            new[] { "00100003" + "7".PadLeft(20, '0'), "00100003" + "7".PadLeft(20, '0'), "00100003" + "8".PadLeft(20, '0') },
            libro.Aliquots.Select(a => a[..28]).ToArray());
        Assert.AreEqual("0005", Field(libro.Aliquots[0], 44, 47));
        Assert.AreEqual("0004", Field(libro.Aliquots[1], 44, 47), "10,5 %");
        Assert.AreEqual("000000000002100", Field(libro.Aliquots[1], 48, 62));
    }

    [TestMethod]
    public void FacturaE_EnDolares_ExportacionConCuitDelPaisYCotizacion()
    {
        var voucher = FiscalVoucher.From(
            Date, 19, 12, 41, imported: false,
            """{"document":{"receiver":{"doc_type":80,"doc_number":"123456789","name":"Foreign Co","country_code":212,"country_tax_id":55000002126},"currency":"DOL","exchange_rate":"1450.5","amounts":{"other_taxes":"0.00"}}}""",
            DocumentKind.Invoice, "USD", [], 10m, 0m, 10m, "{}", "Cuenta");

        var libro = LibroIvaDigital.Build([voucher]);

        var line = libro.Vouchers.Single();
        Assert.AreEqual("019", Field(line, 9, 11));
        Assert.AreEqual("80", Field(line, 57, 58));
        Assert.AreEqual("00000000055000002126", Field(line, 59, 78), "CUIT genérica del país");
        Assert.AreEqual("Foreign Co".PadRight(30), Field(line, 79, 108));
        Assert.AreEqual("000000000001000", Field(line, 109, 123));
        Assert.AreEqual("000000000001000", Field(line, 154, 168), "exportación exenta");
        Assert.AreEqual("DOL", Field(line, 229, 231));
        Assert.AreEqual("1450500000", Field(line, 232, 241));
        Assert.AreEqual("X", Field(line, 243, 243));
        var aliquot = libro.Aliquots.Single();
        Assert.AreEqual("0003", Field(aliquot, 44, 47), "alícuota cero");
        Assert.AreEqual(new string('0', 15), Field(aliquot, 29, 43));
        Assert.IsEmpty(libro.Notes);
    }

    [TestMethod]
    public void SinIva_ElCodigoDeOperacionDiceExentoONoGravado()
    {
        var exempt = Voucher(number: 1, aliquots: [], exempt: 500m);
        var notTaxed = Voucher(number: 2, aliquots: [], notTaxed: 500m);

        var libro = LibroIvaDigital.Build([exempt, notTaxed]);

        Assert.AreEqual("E", Field(libro.Vouchers[0], 243, 243));
        Assert.AreEqual("N", Field(libro.Vouchers[1], 243, 243));
        Assert.AreEqual("000000000050000", Field(libro.Vouchers[1], 124, 138), "no gravado");
        Assert.HasCount(2, libro.Aliquots);
    }

    [TestMethod]
    public void NotaDeCredito_VaConImportesPositivosYSuTipo()
    {
        var libro = LibroIvaDigital.Build([Voucher(type: 8, kind: DocumentKind.CreditNote, aliquots: [new(0.21m, 1000m, 210m)])]);

        Assert.AreEqual("008", Field(libro.Vouchers.Single(), 9, 11));
        Assert.AreEqual("000000000121000", Field(libro.Vouchers.Single(), 109, 123));
    }

    [TestMethod]
    public void Percepciones_CadaCodigoDeArcaEnSuCampo()
    {
        var voucher = FiscalVoucher.From(
            Date, 1, 3, 9, imported: false,
            """
            {"document":{"receiver":{"doc_type":80,"doc_number":"30712345671","name":"Empresa"},"currency":"PES","exchange_rate":"1",
             "amounts":{"other_taxes":"420.00","taxes":[
               {"id":13,"description":"RG 2126","base":"1000.00","rate":"10","amount":"100.00"},
               {"id":6,"description":"Percepción de IVA","base":"1000.00","rate":"3","amount":"30.00"},
               {"id":7,"description":"Percepción IIBB CABA","base":"1000.00","rate":"3","amount":"30.00"},
               {"id":8,"description":"Municipal","base":"1000.00","rate":"1","amount":"10.00"},
               {"id":4,"description":"Internos","base":"1000.00","rate":"20","amount":"200.00"},
               {"id":99,"description":"Otro","base":"1000.00","rate":"5","amount":"50.00"}]}}}
            """,
            DocumentKind.Invoice, "ARS", [new(0.21m, 1000m, 210m)], 0m, 0m, 1630m, "{}", "Cuenta");

        Assert.AreEqual(0.03m, voucher.OtherTaxes[1].Rate, "la alícuota del fisco viene en porcentaje");
        var line = LibroIvaDigital.Build([voucher]).Vouchers.Single();
        Assert.AreEqual("000000000163000", Field(line, 109, 123));
        Assert.AreEqual("000000000010000", Field(line, 139, 153), "percepción a no categorizados");
        Assert.AreEqual("000000000003000", Field(line, 169, 183), "nacionales");
        Assert.AreEqual("000000000003000", Field(line, 184, 198), "ingresos brutos");
        Assert.AreEqual("000000000001000", Field(line, 199, 213), "municipales");
        Assert.AreEqual("000000000020000", Field(line, 214, 228), "internos");
        Assert.AreEqual("000000000005000", Field(line, 244, 258), "otros tributos");
    }

    [TestMethod]
    public void Nombre_SinCaracteresFueraDeLatin1NiControles_YRecortadoATreintaPosiciones()
    {
        var libro = LibroIvaDigital.Build([Voucher(docType: 96, docNumber: "30.111.222", name: "Peña 🚀 Ñandú\tde la Ribera y Asociados Grandes")]);
        var line = libro.Vouchers.Single();

        Assert.AreEqual("96", Field(line, 57, 58));
        Assert.AreEqual("00000000000030111222", Field(line, 59, 78));
        Assert.AreEqual("Peña ? Ñandúde la Ribera y Aso", Field(line, 79, 108));
        var bytes = LibroIvaDigital.File(libro.Vouchers);
        Assert.HasCount(268, bytes, "266 bytes por registro más CRLF: una letra, un byte");
        Assert.AreEqual(0xF1, bytes[80], "la ñ en ISO 8859-1, en la posición 81");
        Assert.AreEqual("\r\n", Encoding.Latin1.GetString(bytes[^2..]));
    }

    [TestMethod]
    public void SinCotizacion_VaEnCeroYQuedaParaRevisar()
    {
        var libro = LibroIvaDigital.Build([Voucher(currency: "DOL", rate: null, aliquots: [new(0.21m, 8.26m, 1.74m)])]);

        Assert.AreEqual(new string('0', 10), Field(libro.Vouchers.Single(), 232, 241));
        Assert.AreEqual("NoExchangeRate", libro.Notes.Single().Key);
        Assert.AreEqual("006-00003-00000123", libro.Notes.Single().Args.Single());
    }

    [TestMethod]
    public void AlicuotaFueraDeLaTabla_QuedaAfueraConSuNota()
    {
        var libro = LibroIvaDigital.Build([Voucher(number: 1, aliquots: [new(0.19m, 100m, 19m)]), Voucher(number: 2)]);

        Assert.HasCount(1, libro.Vouchers);
        Assert.AreEqual("UnknownRate", libro.Notes.Single().Key);
        Assert.AreEqual("006-00003-00000001", libro.Notes.Single().Args.Single());
    }

    [TestMethod]
    public void Resumen_PorTipoYMoneda()
    {
        var libro = LibroIvaDigital.Build([Voucher(number: 1), Voucher(number: 2), Voucher(type: 8, number: 3, kind: DocumentKind.CreditNote, aliquots: [new(0.21m, 100m, 21m)])]);

        var rows = libro.Summary.Rows;
        Assert.HasCount(2, rows);
        CollectionAssert.AreEqual(new object?[] { "006", "PES", 2, 20000m, 4200m, 0m, 24200m }, rows[0]);
        CollectionAssert.AreEqual(new object?[] { "008", "PES", 1, 100m, 21m, 0m, 121m }, rows[1]);
    }

    [TestMethod]
    public void Importado_TomaLosImportesDeSuComprobanteYNoLosDelDocumento()
    {
        // El documento importado tiene la factura y, colgada de él, la nota que la anuló en el sistema anterior.
        var voucher = FiscalVoucher.From(
            Date, 8, 3, 55, imported: true,
            """{"imported":true,"doc_type":99,"doc_number":"0","currency":"PES","exchange_rate":"1.000000","total":"1210.00","net":"1000.00","vat":"210.00"}""",
            DocumentKind.CreditNote, "ARS", [new(0.21m, 5000m, 1050m)], 0m, 0m, 6050m, "{}", "Cuenta importada");

        Assert.AreEqual(1210m, voucher.Total);
        Assert.AreEqual(new AliquotTotal(0.21m, 1000m, 210m), voucher.Aliquots.Single());
        Assert.AreEqual(-1, voucher.Sign);
        Assert.AreEqual(1m, voucher.ExchangeRate);
    }

    [TestMethod]
    [DataRow(1000.00, 210.00, 0.21)]
    [DataRow(0.83, 0.17, 0.21)]
    [DataRow(0.11, 0.02, 0.21)]
    [DataRow(1000.00, 105.00, 0.105)]
    [DataRow(1000.00, 270.00, 0.27)]
    public void Importado_LaAlicuotaEsLaDeLaTablaMasCercanaAlCentavo(double net, double vat, double expected)
    {
        var (aliquots, exempt, notTaxed) = FiscalVoucher.ImportedAmounts(6, (decimal)net + (decimal)vat, (decimal)net, (decimal)vat);

        Assert.AreEqual((decimal)expected, aliquots.Single().Rate);
        Assert.AreEqual(0m, exempt);
        Assert.AreEqual(0m, notTaxed);
    }

    [TestMethod]
    public void Importado_SinIva_ExportacionExentaYElRestoNoGravado()
    {
        Assert.AreEqual(25m, FiscalVoucher.ImportedAmounts(21, 25m, 25m, 0m).Exempt);
        Assert.AreEqual(25m, FiscalVoucher.ImportedAmounts(6, 25m, 25m, 0m).NotTaxed);
        Assert.AreEqual(0.1234m, FiscalVoucher.ImportedAmounts(6, 1123.40m, 1000m, 123.40m).Aliquots.Single().Rate, "fuera de la tabla: el libro lo deja afuera");
    }

    [TestMethod]
    public void ImporteNegativo_NoEntraEnElLibro()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LibroIvaDigital.Build([Voucher(aliquots: [new(0.21m, -10m, -2.1m)])]));
    }
}
