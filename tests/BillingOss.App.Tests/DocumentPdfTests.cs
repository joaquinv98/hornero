using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Documents;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Gotenberg de mentira: cuenta las conversiones y guarda el último HTML.</summary>
internal sealed class FakeGotenberg : HttpMessageHandler
{
    public static readonly byte[] Pdf = "%PDF-1.7 prueba"u8.ToArray();

    private int calls;

    public int Calls => calls;

    public string? LastHtml { get; private set; }

    public GotenbergClient Client() => new(new HttpClient(this) { BaseAddress = new Uri("http://gotenberg.test/") });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        Assert.AreEqual("/forms/chromium/convert/html", request.RequestUri!.AbsolutePath);
        var form = (MultipartFormDataContent)request.Content!;
        var file = form.Single(c => c.Headers.ContentDisposition?.FileName?.Trim('"') == "index.html");
        LastHtml = await file.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pdf) };
    }
}

[TestClass]
public sealed class DocumentPdfTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private static readonly ReceiverSnapshot FinalConsumer = new(TaxDocumentType.Dni, "30111222", "Ana Pérez", 5, "AR", "Chaco", "Av. Siempre Viva 742, Resistencia");

    [TestMethod]
    public void Qr_ArmaElEjemploDeLaEspecificacion()
    {
        var data = new FiscalQrData(new DateOnly(2020, 10, 13), 30000000007, 10, 1, 94, 12100.00m, "DOL", 65.000000m, 80, 20000000001, 70417054367476);

        Assert.AreEqual(
            """{"ver":1,"fecha":"2020-10-13","cuit":30000000007,"ptoVta":10,"tipoCmp":1,"nroCmp":94,"importe":12100,"moneda":"DOL","ctz":65,"tipoDocRec":80,"nroDocRec":20000000001,"tipoCodAut":"E","codAut":70417054367476}""",
            FiscalQr.Json(data));
        Assert.AreEqual(
            "https://www.arca.gob.ar/fe/qr/?p=eyJ2ZXIiOjEsImZlY2hhIjoiMjAyMC0xMC0xMyIsImN1aXQiOjMwMDAwMDAwMDA3LCJwdG9WdGEiOjEwLCJ0aXBvQ21wIjoxLCJucm9DbXAiOjk0LCJpbXBvcnRlIjoxMjEwMCwibW9uZWRhIjoiRE9MIiwiY3R6Ijo2NSwidGlwb0RvY1JlYyI6ODAsIm5yb0RvY1JlYyI6MjAwMDAwMDAwMDEsInRpcG9Db2RBdXQiOiJFIiwiY29kQXV0Ijo3MDQxNzA1NDM2NzQ3Nn0=",
            FiscalQr.Url(data));
        StringAssert.StartsWith(FiscalQr.ImageDataUri(FiscalQr.Url(data)), "data:image/svg+xml;base64,");
    }

    [TestMethod]
    public async Task FacturaB_ConsumidorFinal_LlevaQrTransparenciaYRescision()
    {
        var doc = Issued(DocumentKind.Invoice, Currency.Ars, PriceMode.TaxIncluded, FinalConsumer, ("VPS 2 GB", 1m, 12100m, TaxTreatment.Taxed, 0.21m));
        var fiscal = Fiscal(6, 12, Payload("PES", "1", 96, "30111222", 5, "12100.00"));

        var model = PrintModels.Build(new PrintSource(doc, FinalConsumer, Issuer(), fiscal, FiscalExpected: true));

        Assert.AreEqual("B", model.Letter);
        Assert.AreEqual("06", model.TypeCode);
        Assert.AreEqual("00003-00000012", model.Number);
        Assert.AreEqual("A CONSUMIDOR FINAL", model.Receiver.VatCondition);
        Assert.AreEqual("DNI 30111222", model.Receiver.TaxId);
        Assert.AreEqual("12.100,00", model.Lines[0].Amount);
        Assert.AreEqual("$ 12.100,00", model.Total);
        Assert.AreEqual("$ 2.100,00", model.Transparency!.ContainedVat);
        Assert.AreEqual("$ 0,00", model.Transparency.OtherTaxes);
        Assert.IsFalse(model.NotValidAsInvoice);
        CollectionAssert.Contains(model.Legends.ToList(), PrintModels.RescissionLegend);
        CollectionAssert.DoesNotContain(model.Legends.ToList(), PrintModels.MonotributoLegend);

        var qr = QrJson(model);
        Assert.AreEqual(
            """{"ver":1,"fecha":"2026-11-02","cuit":30712345678,"ptoVta":3,"tipoCmp":6,"nroCmp":12,"importe":12100,"moneda":"PES","ctz":1,"tipoDocRec":96,"nroDocRec":30111222,"tipoCodAut":"E","codAut":76451234567890}""",
            qr);

        var html = await DocumentHtml.Render(model);
        StringAssert.Contains(html, "COD. 06");
        StringAssert.Contains(html, "CAE N°: <b>76451234567890</b>");
        StringAssert.Contains(html, "Fecha de Vto. de CAE: <b>12/11/2026</b>");
        StringAssert.Contains(html, "Régimen de Transparencia Fiscal al Consumidor (Ley 27.743)");
        StringAssert.Contains(html, "IVA Contenido: <b>$ 2.100,00</b>");
        StringAssert.Contains(html, "Otros Impuestos Nacionales Indirectos: <b>$ 0,00</b>");
        StringAssert.Contains(WebUtility.HtmlDecode(html), "<img src=\"data:image/svg+xml;base64,");
        StringAssert.Contains(html, "ORIGINAL");
        StringAssert.Contains(html, "Inicio de actividades: 01/03/2015");
        StringAssert.Contains(html, "Ingresos brutos: 30-71234567-8");
        StringAssert.Contains(html, "IVA RESPONSABLE INSCRIPTO");
        StringAssert.Contains(html, "Calle Falsa 123, Resistencia, Chaco");
        StringAssert.Contains(html, PrintModels.RescissionLegend);
        Assert.DoesNotContain("NO VÁLIDO", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("src=\"https", html);
    }

    [TestMethod]
    public async Task FacturaA_Monotributista_DiscriminaIvaYLlevaLeyendaDeLaLey27618()
    {
        var receiver = new ReceiverSnapshot(TaxDocumentType.Cuit, "20111111112", "Juan Gómez", 6, "AR", null);
        var doc = Issued(DocumentKind.Invoice, Currency.Ars, PriceMode.TaxExcluded, receiver, ("Hosting", 2m, 5000m, TaxTreatment.Taxed, 0.21m));
        var fiscal = Fiscal(1, 40, Payload("PES", "1", 80, "20111111112", 6, "12100.00"));

        var model = PrintModels.Build(new PrintSource(doc, receiver, Issuer(), fiscal, FiscalExpected: true));

        Assert.AreEqual("A", model.Letter);
        Assert.IsTrue(model.LineTax);
        Assert.AreEqual("21 %", model.Lines[0].Rate);
        Assert.AreEqual("5.000,00", model.Lines[0].UnitPrice);
        Assert.AreEqual("10.000,00", model.Lines[0].Amount);
        CollectionAssert.Contains(model.Totals.ToList(), new PrintTotal("Importe neto gravado", "$ 10.000,00"));
        CollectionAssert.Contains(model.Totals.ToList(), new PrintTotal("IVA 21 %", "$ 2.100,00"));
        Assert.IsNull(model.Transparency);
        Assert.AreEqual("CUIT 20-11111111-2", model.Receiver.TaxId);
        Assert.AreEqual("RESPONSABLE MONOTRIBUTO", model.Receiver.VatCondition);
        CollectionAssert.Contains(model.Legends.ToList(), PrintModels.MonotributoLegend);
        StringAssert.Contains(QrJson(model), "\"tipoCmp\":1,\"nroCmp\":40,\"importe\":12100,\"moneda\":\"PES\",\"ctz\":1,\"tipoDocRec\":80,\"nroDocRec\":20111111112");

        var html = await DocumentHtml.Render(model);
        StringAssert.Contains(html, "Alícuota IVA");
        StringAssert.Contains(html, PrintModels.MonotributoLegend);
        Assert.DoesNotContain("Transparencia Fiscal", html);
    }

    [TestMethod]
    public async Task FacturaB_DiscriminaLaPercepcionYLaAlicuotaQueSoloSeInforma_YLaC_MuestraSuSubtotal()
    {
        var receiver = new ReceiverSnapshot(TaxDocumentType.Cuit, "20111111112", "Juan Gómez", 7, "AR", "AR-C");
        var perception = new DocumentTaxAmount("perc_iva_rg2126", "Percepción de IVA RG 2126", 13, 12100m, 0.105m, 1270.50m, true, TaxReasons.Perception);
        var isib = new DocumentTaxAmount("isib_caba", "Alícuota ISIB CABA", null, 10000m, 0.03m, 0m, false, TaxReasons.Informational, "ALÍCUOTA ISIB CABA");
        var doc = new Document(DocumentKind.Invoice, 1, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 11, 9), Now);
        doc.AddLine(LineKind.Renewal, "VPS 2 GB", 1m, 12100m, TaxTreatment.Taxed, 0.21m);
        doc.Issue(new DateOnly(2026, 11, 2), JsonSerializer.Serialize(receiver), Now, doc.PrepareIssue(), [perception, isib]);
        doc.AssignNumber(12);

        var model = PrintModels.Build(new PrintSource(doc, receiver, Issuer(), Fiscal(6, 12, Payload("PES", "1", 80, "20111111112", 7, "13370.50")), FiscalExpected: true));

        CollectionAssert.Contains(model.Totals.ToList(), new PrintTotal("Percepción de IVA RG 2126 10,5 %", "$ 1.270,50"), "La percepción suma al total y va discriminada.");
        Assert.AreEqual("$ 13.370,50", model.Total);
        CollectionAssert.Contains(model.Legends.ToList(), "ALÍCUOTA ISIB CABA 3 %");
        CollectionAssert.DoesNotContain(model.Legends.ToList(), "ALÍCUOTA ISIB CABA");
        StringAssert.Contains(await DocumentHtml.Render(model), "Percepción de IVA RG 2126 10,5 %");

        // La C de un monotributista no discrimina: su subtotal, no un importe no gravado.
        var c = new Document(DocumentKind.Invoice, 1, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 11, 9), Now);
        c.AddLine(LineKind.Renewal, "Hosting", 1m, 10000m, TaxTreatment.NotTaxed, 0m);
        c.Issue(new DateOnly(2026, 11, 2), JsonSerializer.Serialize(FinalConsumer), Now, c.PrepareIssue());
        c.AssignNumber(13);
        var classC = PrintModels.Build(new PrintSource(c, FinalConsumer, Issuer(), Fiscal(11, 5, Payload("PES", "1", 96, "30111222", 5, "10000.00")), FiscalExpected: true));
        Assert.AreEqual("C", classC.Letter);
        CollectionAssert.AreEqual(new[] { new PrintTotal("Subtotal", "$ 10.000,00") }, classC.Totals.ToList());
    }

    [TestMethod]
    public async Task FacturaE_EnDolaresCanceladaEnLaMismaMoneda_MuestraCotizacionYExportacion()
    {
        var receiver = new ReceiverSnapshot(TaxDocumentType.None, string.Empty, "Acme LLC", 9, "US", null, "1 Main St, Austin");
        var doc = Issued(DocumentKind.Invoice, Currency.Usd, PriceMode.TaxIncluded, receiver, ("Servidor dedicado", 1m, 100m, TaxTreatment.Exempt, 0m));
        var fiscal = Fiscal(19, 5, Payload("DOL", "1449.0", 99, "0", 9, "100.00", same: true, export: true));

        var model = PrintModels.Build(new PrintSource(doc, receiver, Issuer(), fiscal, FiscalExpected: true));

        Assert.AreEqual("E", model.Letter);
        Assert.AreEqual("IVA EXENTO OPERACIÓN DE EXPORTACIÓN", model.Receiver.VatCondition);
        Assert.AreEqual("CUIT del país 55000002206", model.Receiver.TaxId);
        Assert.AreEqual("1.449,00", model.ExchangeRate);
        Assert.IsTrue(model.SameCurrencyPayment);
        Assert.AreEqual("Transferencia bancaria", model.PaymentTerms);
        Assert.AreEqual("USD 100,00", model.Total);
        Assert.IsNull(model.Transparency);
        Assert.AreEqual(
            """{"ver":1,"fecha":"2026-11-02","cuit":30712345678,"ptoVta":3,"tipoCmp":19,"nroCmp":5,"importe":100,"moneda":"DOL","ctz":1449,"tipoCodAut":"E","codAut":76451234567890}""",
            QrJson(model));

        var html = await DocumentHtml.Render(model);
        StringAssert.Contains(html, "Moneda: <b>USD</b> · Tipo de cambio: <b>1.449,00</b> · Se cancela en la misma moneda");
        StringAssert.Contains(html, "Forma de pago: Transferencia bancaria");
    }

    [TestMethod]
    public void MonedaExtranjeraSinCotizacion_NoArmaUnQrInvalido()
    {
        var receiver = new ReceiverSnapshot(TaxDocumentType.None, string.Empty, "Acme LLC", 9, "US", null);
        var doc = Issued(DocumentKind.Invoice, Currency.Usd, PriceMode.TaxIncluded, receiver, ("Servidor dedicado", 1m, 100m, TaxTreatment.Exempt, 0m));
        var fiscal = Fiscal(19, 5, Payload("DOL", null, 99, "0", 9, "100.00", same: true, export: true));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => PrintModels.Build(new PrintSource(doc, receiver, Issuer(), fiscal, FiscalExpected: true)));
        StringAssert.Contains(error.Message, "cotización");
    }

    [TestMethod]
    public async Task NotaDeCredito_NombraElComprobanteAsociado()
    {
        var doc = Issued(DocumentKind.CreditNote, Currency.Ars, PriceMode.TaxIncluded, FinalConsumer, ("Anulación de la factura 12", 1m, 12100m, TaxTreatment.Taxed, 0.21m));
        var payload = JsonNode.Parse(Payload("PES", "1", 96, "30111222", 5, "12100.00"))!;
        payload["document"]!["associated"] = new JsonArray(new JsonObject { ["type_code"] = 6, ["point_of_sale"] = 3, ["number"] = 12, ["date"] = "2026-11-02" });

        var model = PrintModels.Build(new PrintSource(doc, FinalConsumer, Issuer(), Fiscal(8, 4, payload.ToJsonString()), FiscalExpected: true));

        Assert.AreEqual("NOTA DE CRÉDITO", model.Title);
        Assert.AreEqual("Factura B 00003-00000012 del 02/11/2026", model.Associated);
        Assert.IsNull(model.DueDate);
        StringAssert.Contains(await DocumentHtml.Render(model), "Comprobante asociado: Factura B 00003-00000012 del 02/11/2026");
    }

    [TestMethod]
    public async Task SinComprobanteFiscal_NoEsValidoComoFacturaSiSeEspera()
    {
        var doc = Issued(DocumentKind.Invoice, Currency.Ars, PriceMode.TaxIncluded, FinalConsumer, ("VPS 2 GB", 1m, 12100m, TaxTreatment.Taxed, 0.21m));

        var pending = PrintModels.Build(new PrintSource(doc, FinalConsumer, Issuer(), null, FiscalExpected: true));
        var noFiscal = PrintModels.Build(new PrintSource(doc, FinalConsumer, Issuer(), null, FiscalExpected: false));

        Assert.IsTrue(pending.NotValidAsInvoice);
        Assert.IsNull(pending.Fiscal);
        Assert.IsNull(pending.Letter);
        Assert.AreEqual("12", pending.Number);
        var html = await DocumentHtml.Render(pending);
        StringAssert.Contains(html, "DOCUMENTO NO VÁLIDO COMO FACTURA");
        Assert.DoesNotContain("data:image/svg", html);
        Assert.DoesNotContain("NO VÁLIDO", await DocumentHtml.Render(noFiscal));
    }

    [TestMethod]
    public async Task Html_CodificaLosTextosDelDocumento()
    {
        var doc = Issued(DocumentKind.Invoice, Currency.Ars, PriceMode.TaxIncluded, FinalConsumer, ("<script>alert(1)</script>", 1m, 100m, TaxTreatment.Taxed, 0.21m));

        var html = await DocumentHtml.Render(PrintModels.Build(new PrintSource(doc, FinalConsumer, Issuer(), null, FiscalExpected: false)));

        Assert.DoesNotContain("<script>", html);
        StringAssert.Contains(html, "&lt;script&gt;");
    }

    [TestMethod]
    public void Importado_UsaLosDatosPlanosDelComprobante()
    {
        var receiver = new ReceiverSnapshot(TaxDocumentType.Cuit, "30700000001", "Empresa SA", 1, "AR", null);
        var doc = Document.FromImport(900, DocumentKind.Invoice, 1, Currency.Usd, 77, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 10),
            new TaxBreakdown([new AliquotTotal(0.21m, 100m, 21m)], [], 0m, 0m), 0m, 0m, "{}", Now);
        doc.ImportLine(LineKind.Renewal, "VPS", 121m, TaxTreatment.Taxed, 0.21m, null);
        var fiscal = Fiscal(1, 501, """{"imported":true,"doc_type":80,"doc_number":"30700000001","vat_condition":1,"currency":"DOL","exchange_rate":"1000.5","same_currency_payment":false,"total":"121.00","net":"100.00","vat":"21.00"}""");

        var model = PrintModels.Build(new PrintSource(doc, receiver, Issuer(), fiscal, FiscalExpected: false));

        Assert.AreEqual("121,00", model.Lines[0].Amount);
        Assert.AreEqual("1.000,50", model.ExchangeRate);
        Assert.AreEqual("IVA RESPONSABLE INSCRIPTO", model.Receiver.VatCondition);
        StringAssert.Contains(QrJson(model), "\"importe\":121,\"moneda\":\"DOL\",\"ctz\":1000.5,\"tipoDocRec\":80,\"nroDocRec\":30700000001");
    }

    /// <summary>Contra un Gotenberg de verdad. Se saltea sin BILLINGOSS_TEST_GOTENBERG (por ejemplo http://localhost:3000).</summary>
    [TestMethod]
    public async Task GotenbergReal_ConvierteElComprobante()
    {
        var url = Environment.GetEnvironmentVariable("BILLINGOSS_TEST_GOTENBERG")
            ?? throw new AssertInconclusiveException("Falta BILLINGOSS_TEST_GOTENBERG: se saltea la prueba contra Gotenberg.");
        var doc = Issued(DocumentKind.Invoice, Currency.Ars, PriceMode.TaxIncluded, FinalConsumer, ("VPS 2 GB", 1m, 12100m, TaxTreatment.Taxed, 0.21m));
        var model = PrintModels.Build(new PrintSource(doc, FinalConsumer, Issuer(), Fiscal(6, 12, Payload("PES", "1", 96, "30111222", 5, "12100.00")), FiscalExpected: true));
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };

        var pdf = await new GotenbergClient(http).Convert(await DocumentHtml.Render(model), CancellationToken.None);

        Assert.IsTrue(pdf.AsSpan().StartsWith("%PDF"u8));
    }

    [TestMethod]
    public async Task Api_DescargaConPermisoYGuardaElPdfFiscalUnaVez()
    {
        var gotenberg = new FakeGotenberg();
        await using var w = await WebHarness.Start(configure: b => b.Services.AddSingleton(gotenberg.Client()));
        var fiscalId = await Invoice(w, withFiscal: true);
        var plainId = await Invoice(w, withFiscal: false);
        var reader = await w.Key("documents:read");
        var other = await w.Key("accounts:read");

        using (var anonymous = await Get(w, $"/api/v1/documents/{fiscalId}/pdf", null))
        using (var forbidden = await Get(w, $"/api/v1/documents/{fiscalId}/pdf", other))
        using (var missing = await Get(w, "/api/v1/documents/999999/pdf", reader))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        for (var i = 0; i < 2; i++)
        {
            using var ok = await Get(w, $"/api/v1/documents/{fiscalId}/pdf", reader);
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, await ok.Content.ReadAsStringAsync());
            Assert.AreEqual("application/pdf", ok.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("factura-B-00003-00000001.pdf", ok.Content.Headers.ContentDisposition!.FileName);
            CollectionAssert.AreEqual(FakeGotenberg.Pdf, await ok.Content.ReadAsByteArrayAsync());
        }

        Assert.AreEqual(1, gotenberg.Calls, "El PDF con CAE se genera una vez y después sale guardado.");
        StringAssert.Contains(gotenberg.LastHtml!, "76451234567890");

        using (var plain1 = await Get(w, $"/api/v1/documents/{plainId}/pdf", reader))
        using (var plain2 = await Get(w, $"/api/v1/documents/{plainId}/pdf", reader))
        {
            Assert.AreEqual(HttpStatusCode.OK, plain2.StatusCode);
        }

        Assert.AreEqual(3, gotenberg.Calls, "Sin comprobante fiscal no se guarda: puede llegar el CAE después.");
        Assert.AreEqual(1, await w.Query(db => db.Set<DocumentPdf>().CountAsync()));
    }

    [TestMethod]
    public async Task EmisorIncompleto_NoGuardaElPdfConCae_HastaQueSeCarganSusDatos()
    {
        var gotenberg = new FakeGotenberg();
        await using var w = await WebHarness.Start(configure: b => b.Services.AddSingleton(gotenberg.Client()));
        var id = await Invoice(w, withFiscal: true, issuer: new IssuerProfile { TaxId = "30712345678", LegalName = IssuerProfile.ImportedName, PointOfSale = 3 });
        var reader = await w.Key("documents:read");

        using (var incomplete = await Get(w, $"/api/v1/documents/{id}/pdf", reader))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, incomplete.StatusCode);
            StringAssert.Contains(await incomplete.Content.ReadAsStringAsync(), "Faltan datos del emisor para el PDF: razón social, domicilio comercial, ingresos brutos, inicio de actividades.");
        }

        Assert.AreEqual(0, gotenberg.Calls);
        Assert.AreEqual(0, await w.Query(db => db.Set<DocumentPdf>().CountAsync()));

        await w.Query(db => db.IssuerProfiles.ExecuteUpdateAsync(u => u
            .SetProperty(i => i.LegalName, "Proveedor de prueba SA")
            .SetProperty(i => i.Address, "Calle Falsa 123, Resistencia, Chaco")
            .SetProperty(i => i.GrossIncomeNumber, "30-71234567-8")
            .SetProperty(i => i.ActivityStart, new DateOnly(2015, 3, 1))));

        using (var ok = await Get(w, $"/api/v1/documents/{id}/pdf", reader))
        {
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        }

        Assert.AreEqual(1, await w.Query(db => db.Set<DocumentPdf>().CountAsync()));
        StringAssert.Contains(gotenberg.LastHtml!, "Calle Falsa 123, Resistencia, Chaco");
    }

    private static async Task<long> Invoice(WebHarness w, bool withFiscal, IssuerProfile? issuer = null)
    {
        var token = await w.Key("orders:write");
        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync());
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/orders", UriKind.Relative))
        {
            Content = JsonContent.Create(new { account_id = w.AccountId, items = new[] { new { plan_id = planId, quantity = 1 } } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await w.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var invoiceId = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["invoice_id"]!.GetValue<long>();
        if (withFiscal)
        {
            await w.Query(async db =>
            {
                issuer ??= Issuer();
                db.IssuerProfiles.Add(issuer);
                await db.SaveChangesAsync();
                var fiscal = Fiscal(6, 1, Payload("PES", "1", 96, "30111222", 5, "12100.00"));
                fiscal.DocumentId = invoiceId;
                fiscal.IssuerId = issuer.Id;
                fiscal.CreatedAt = Now;
                db.FiscalDocuments.Add(fiscal);
                return await db.SaveChangesAsync();
            });
        }

        return invoiceId;
    }

    private static Task<HttpResponseMessage> Get(WebHarness w, string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return w.Client.SendAsync(request);
    }

    private static IssuerProfile Issuer() => new()
    {
        TaxId = "30712345678",
        LegalName = "Proveedor de prueba SA",
        Address = "Calle Falsa 123, Resistencia, Chaco",
        GrossIncomeNumber = "30-71234567-8",
        ActivityStart = new DateOnly(2015, 3, 1),
        PointOfSale = 3,
    };

    private static Document Issued(DocumentKind kind, Currency currency, PriceMode mode, ReceiverSnapshot receiver, params (string Description, decimal Quantity, decimal Price, TaxTreatment Treatment, decimal Rate)[] lines)
    {
        var doc = new Document(kind, 1, currency, mode, new DateOnly(2026, 11, 9), Now);
        foreach (var l in lines)
        {
            doc.AddLine(LineKind.Renewal, l.Description, l.Quantity, l.Price, l.Treatment, l.Rate);
        }

        doc.Issue(new DateOnly(2026, 11, 2), JsonSerializer.Serialize(receiver), Now, doc.PrepareIssue());
        doc.AssignNumber(12);
        return doc;
    }

    private static FiscalDocument Fiscal(int typeCode, long number, string payload) => new()
    {
        PointOfSale = 3,
        TypeCode = typeCode,
        Number = number,
        Date = new DateOnly(2026, 11, 2),
        AuthorizationCode = "76451234567890",
        AuthorizationDue = new DateOnly(2026, 11, 12),
        Payload = payload,
    };

    /// <summary>Lo que el núcleo guarda como enviado al fisco, con la forma de la sección 9.4 del protocolo.</summary>
    private static string Payload(string currency, string? rate, int docType, string docNumber, int vatCondition, string total, bool same = false, bool export = false)
    {
        var receiver = new JsonObject { ["doc_type"] = docType, ["doc_number"] = docNumber, ["vat_condition"] = vatCondition };
        var document = new JsonObject
        {
            ["currency"] = currency,
            ["exchange_rate"] = rate,
            ["same_currency_payment"] = same,
            ["service_period"] = new JsonObject { ["from"] = "2026-11-02", ["to"] = "2026-12-01" },
            ["payment_due"] = "2026-11-09",
            ["receiver"] = receiver,
            ["amounts"] = new JsonObject { ["total"] = total },
        };
        if (export)
        {
            receiver["country_code"] = 212;
            receiver["country_tax_id"] = 55000002206;
            receiver["address"] = "1 Main St, Austin";
            document["payment_terms"] = "Transferencia bancaria";
        }

        return new JsonObject { ["document"] = document }.ToJsonString();
    }

    private static string QrJson(PrintModel model)
    {
        var url = model.Fiscal!.QrUrl;
        StringAssert.StartsWith(url, "https://www.arca.gob.ar/fe/qr/?p=");
        return Encoding.UTF8.GetString(Convert.FromBase64String(url["https://www.arca.gob.ar/fe/qr/?p=".Length..]));
    }
}
