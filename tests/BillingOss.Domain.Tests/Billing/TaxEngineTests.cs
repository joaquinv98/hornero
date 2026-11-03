using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Billing;

[TestClass]
public sealed class TaxEngineTests
{
    private static readonly DateOnly On = new(2026, 11, 2);

    private static readonly TaxCategory Digital = new() { Code = TaxCategories.Digital, Name = "Servicios digitales y hosting", ValidFrom = new(2026, 1, 1) };

    private static readonly TaxCategory Telecom = new() { Code = TaxCategories.Telecom, Name = "Telecomunicaciones", ValidFrom = new(2026, 1, 1) };

    private static readonly TaxCategory Exempt = new() { Code = TaxCategories.Exempt, Name = "Exento por ley", Treatment = TaxTreatment.Exempt, Reason = TaxReasons.ExemptByLaw, ValidFrom = new(2026, 1, 1) };

    /// <summary>El preset argentino de un responsable inscripto: 21 % general, 27 % de telecomunicaciones a inscriptos y el resto del mundo exento.</summary>
    private static List<TaxRule> Argentina() =>
    [
        new() { Country = "AR", Outcome = TaxTreatment.Taxed, Rate = 0.21m, ValidFrom = new(2026, 1, 1) },
        new() { Country = "AR", Category = TaxCategories.Telecom, VatCondition = VatConditions.ResponsableInscripto, Outcome = TaxTreatment.Taxed, Rate = 0.27m, ValidFrom = new(2026, 1, 1) },
        new() { Outcome = TaxTreatment.Exempt, ValidFrom = new(2026, 1, 1) },
    ];

    private static List<TaxRule> Rg2126() =>
    [
        new() { TaxCode = Tax.VatPerceptionRg2126, Country = "AR", VatCondition = VatConditions.NoCategorizado, Outcome = TaxTreatment.Taxed, Rate = 0.105m, ValidFrom = new(2026, 1, 1) },
        new() { TaxCode = Tax.VatPerceptionRg2126, Country = "AR", VatCondition = VatConditions.NoCategorizado, Category = TaxCategories.Telecom, Outcome = TaxTreatment.Taxed, Rate = 0.135m, ValidFrom = new(2026, 1, 1) },
    ];

    private static readonly Tax Perception = new() { Code = Tax.VatPerceptionRg2126, Name = "Percepción de IVA RG 2126", Level = TaxLevel.Document, Base = TaxBase.NetPlusLineTaxes, FiscalCode = 13, ValidFrom = new(2026, 1, 1) };

    [TestMethod]
    public void ConsumidorFinalEnArgentina_Paga21_Gravado()
    {
        var r = TaxEngine.ResolveLine(Digital, new TaxReceiver("AR", "AR-C", VatConditions.ConsumidorFinal), Argentina(), On);

        Assert.AreEqual(new LineTax(TaxTreatment.Taxed, 0.21m, TaxReasons.Taxed), r);
    }

    [TestMethod]
    public void Telecomunicacion_AResponsableInscripto_Paga27_YAlConsumidor21()
    {
        var inscripto = TaxEngine.ResolveLine(Telecom, new TaxReceiver("AR", null, VatConditions.ResponsableInscripto), Argentina(), On);
        var consumidor = TaxEngine.ResolveLine(Telecom, new TaxReceiver("AR", null, VatConditions.ConsumidorFinal), Argentina(), On);

        Assert.AreEqual(0.27m, inscripto.Rate);
        Assert.AreEqual(0.21m, consumidor.Rate);
    }

    [TestMethod]
    public void ClienteDelExterior_SinReglaPropia_EsExportacion()
    {
        var r = TaxEngine.ResolveLine(Digital, new TaxReceiver("BR", null, VatConditions.ClienteDelExterior), Argentina(), On);

        Assert.AreEqual(TaxTreatment.Exempt, r.Treatment);
        Assert.AreEqual(TaxReasons.Export, r.Reason);
    }

    [TestMethod]
    public void ReglaDeCuenta_GanaSiempreDentroDeSuAlcance_YLlevaSuConstancia()
    {
        // El cliente del exterior que usa el servicio en el país paga IVA: regla de cuenta, que gana a la del resto del mundo.
        var rules = Argentina();
        rules.Add(new TaxRule { AccountId = 7, Outcome = TaxTreatment.Taxed, Rate = 0.21m, Reason = TaxReasons.AccountRule, CertificateRef = "Consulta al contador 3", ValidFrom = new(2026, 1, 1) });

        var withRule = TaxEngine.ResolveLine(Digital, new TaxReceiver("BR", null, VatConditions.ClienteDelExterior, AccountId: 7), rules, On);
        var other = TaxEngine.ResolveLine(Digital, new TaxReceiver("BR", null, VatConditions.ClienteDelExterior, AccountId: 8), rules, On);

        Assert.AreEqual(new LineTax(TaxTreatment.Taxed, 0.21m, TaxReasons.AccountRule), withRule);
        Assert.AreEqual(TaxTreatment.Exempt, other.Treatment);
    }

    [TestMethod]
    public void ExencionConConstancia_VenceSola()
    {
        var rules = Argentina();
        rules.Add(new TaxRule { AccountId = 7, Country = "AR", Outcome = TaxTreatment.Exempt, Reason = TaxReasons.ExemptByLaw, CertificateRef = "Constancia 123", ValidFrom = new(2026, 1, 1), ValidTo = new(2026, 11, 1) });
        var receiver = new TaxReceiver("AR", null, VatConditions.Exento, AccountId: 7);

        Assert.AreEqual(TaxTreatment.Exempt, TaxEngine.ResolveLine(Digital, receiver, rules, new DateOnly(2026, 10, 31)).Treatment);
        Assert.AreEqual(TaxTreatment.Taxed, TaxEngine.ResolveLine(Digital, receiver, rules, On).Treatment);
    }

    [TestMethod]
    public void CategoriaExenta_NoPasaPorLasReglas()
    {
        var r = TaxEngine.ResolveLine(Exempt, new TaxReceiver("AR", null, VatConditions.ConsumidorFinal), [], On);

        Assert.AreEqual(new LineTax(TaxTreatment.Exempt, 0m, TaxReasons.ExemptByLaw), r);
    }

    [TestMethod]
    public void EmisorMonotributista_NoCobraIva_ConElMotivoMonotributo()
    {
        var r = TaxEngine.ResolveLine(Digital, new TaxReceiver("AR", null, VatConditions.ResponsableInscripto), Argentina(), On, VatConditions.Monotributo);

        Assert.AreEqual(new LineTax(TaxTreatment.NotTaxed, 0m, TaxReasons.Monotax), r);
    }

    [TestMethod]
    public void SinRegla_ElCalculoFalla_YNuncaAsumeCero() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            TaxEngine.ResolveLine(Digital, new TaxReceiver("AR", null, VatConditions.ConsumidorFinal), [], On));

    [TestMethod]
    public void EmpresaEuropeaConNumeroValidado_InversionDelSujetoPasivo_YElConsumidorPagaElIvaDeSuPais()
    {
        var rules = Argentina();
        rules.Add(new TaxRule { Country = "DE", Outcome = TaxTreatment.Taxed, Rate = 0.19m, ValidFrom = new(2026, 1, 1) });
        rules.Add(new TaxRule { Country = "DE", BusinessWithValidatedNumber = true, Outcome = TaxTreatment.Exempt, Reason = TaxReasons.ReverseCharge, Legend = "Reverse charge", ValidFrom = new(2026, 1, 1) });

        var business = TaxEngine.ResolveLine(Digital, new TaxReceiver("DE", null, VatConditions.ClienteDelExterior, ValidatedBusiness: true), rules, On);
        var consumer = TaxEngine.ResolveLine(Digital, new TaxReceiver("DE", null, VatConditions.ClienteDelExterior), rules, On);

        Assert.AreEqual(new LineTax(TaxTreatment.Exempt, 0m, TaxReasons.ReverseCharge, "Reverse charge"), business);
        Assert.AreEqual(0.19m, consumer.Rate);
    }

    [TestMethod]
    public void PercepcionRg2126_SaleUnRenglonPorAlicuota_SobreNetoMasIva()
    {
        // Un no categorizado con hosting ($12.100 con IVA incluido) y telefonía ($12.100): 10,5 % y 13,5 % sobre neto más IVA.
        var breakdown = TaxCalculator.Compute(PriceMode.TaxIncluded,
        [
            new TaxableLine("0", 12100m, TaxTreatment.Taxed, 0.21m),
            new TaxableLine("1", 12100m, TaxTreatment.Taxed, 0.21m),
        ]);
        var lines = new List<TaxedLine> { new(TaxCategories.Digital, breakdown.Lines[0]), new(TaxCategories.Telecom, breakdown.Lines[1]) };

        var taxes = TaxEngine.ComputeDocumentTaxes([Perception], Rg2126(), new TaxReceiver("AR", null, VatConditions.NoCategorizado), lines, On);

        Assert.HasCount(2, taxes);
        Assert.AreEqual(new DocumentTaxAmount(Tax.VatPerceptionRg2126, "Percepción de IVA RG 2126", 13, 12100m, 0.105m, 1270.50m, true, TaxReasons.Perception, null, TaxCategories.Digital), taxes[0]);
        Assert.AreEqual(new DocumentTaxAmount(Tax.VatPerceptionRg2126, "Percepción de IVA RG 2126", 13, 12100m, 0.135m, 1633.50m, true, TaxReasons.Perception, null, TaxCategories.Telecom), taxes[1]);
    }

    [TestMethod]
    public void PercepcionRg2126_NoAlcanzaLosRenglonesExentosNiNoGravados()
    {
        // La RG 2126 (arts. 1 y 5) alcanza solo operaciones gravadas, y la validación 10067 de WSFEv1 exige el tributo 13
        // solo cuando neto más IVA es mayor a cero: un producto no gravado o exento a $1.000 no lleva percepción.
        var breakdown = TaxCalculator.Compute(PriceMode.TaxIncluded,
        [
            new TaxableLine("0", 1000m, TaxTreatment.NotTaxed),
            new TaxableLine("1", 1000m, TaxTreatment.Exempt),
            new TaxableLine("2", 12100m, TaxTreatment.Taxed, 0.21m),
        ]);
        var receiver = new TaxReceiver("AR", null, VatConditions.NoCategorizado);

        var untaxedOnly = TaxEngine.ComputeDocumentTaxes([Perception], Rg2126(), receiver, [new(TaxCategories.Untaxed, breakdown.Lines[0], TaxTreatment.NotTaxed), new(TaxCategories.Exempt, breakdown.Lines[1], TaxTreatment.Exempt)], On);
        var mixed = TaxEngine.ComputeDocumentTaxes([Perception], Rg2126(), receiver, [new(TaxCategories.Untaxed, breakdown.Lines[0], TaxTreatment.NotTaxed), new(TaxCategories.Digital, breakdown.Lines[2])], On);

        Assert.IsEmpty(untaxedOnly);
        Assert.AreEqual((12100m, 1270.50m), (mixed.Single().Base, mixed.Single().Amount), "Solo el renglón gravado entra en la base.");
    }

    [TestMethod]
    public void PercepcionRg2126_NoCorrespondeAlConsumidorFinal_NiAlExcluidoPorReglaDeCuenta()
    {
        var breakdown = TaxCalculator.Compute(PriceMode.TaxIncluded, [new TaxableLine("0", 12100m, TaxTreatment.Taxed, 0.21m)]);
        var lines = new List<TaxedLine> { new(TaxCategories.Digital, breakdown.Lines[0]) };
        var rules = Rg2126();
        rules.Add(new TaxRule { TaxCode = Tax.VatPerceptionRg2126, AccountId = 9, Outcome = TaxTreatment.NotTaxed, Reason = TaxReasons.AccountRule, CertificateRef = "Constancia de inscripción", ValidFrom = new(2026, 1, 1) });

        Assert.IsEmpty(TaxEngine.ComputeDocumentTaxes([Perception], rules, new TaxReceiver("AR", null, VatConditions.ConsumidorFinal), lines, On));
        Assert.IsEmpty(TaxEngine.ComputeDocumentTaxes([Perception], rules, new TaxReceiver("AR", null, VatConditions.NoCategorizado, AccountId: 9), lines, On));
        Assert.HasCount(1, TaxEngine.ComputeDocumentTaxes([Perception], rules, new TaxReceiver("AR", null, VatConditions.NoCategorizado, AccountId: 10), lines, On));
    }

    [TestMethod]
    public void ImpuestoQueSoloSeInforma_LlevaLeyendaYNoSumaAlTotal()
    {
        var isib = new Tax { Code = "isib_caba", Name = "ISIB CABA", Level = TaxLevel.Document, Base = TaxBase.Net, AddsToTotal = false, ValidFrom = new(2026, 1, 1) };
        var rule = new TaxRule { TaxCode = "isib_caba", Country = "AR", Province = "AR-C", VatCondition = VatConditions.ConsumidorFinal, Outcome = TaxTreatment.Taxed, Rate = 0.03m, Legend = "ALÍCUOTA ISIB CABA 3 %", ValidFrom = new(2027, 1, 1) };
        var breakdown = TaxCalculator.Compute(PriceMode.TaxIncluded, [new TaxableLine("0", 12100m, TaxTreatment.Taxed, 0.21m)]);
        var lines = new List<TaxedLine> { new(TaxCategories.Digital, breakdown.Lines[0]) };

        var taxes = TaxEngine.ComputeDocumentTaxes([isib], [rule], new TaxReceiver("AR", "AR-C", VatConditions.ConsumidorFinal), lines, new DateOnly(2027, 1, 2));
        var before = TaxEngine.ComputeDocumentTaxes([isib], [rule], new TaxReceiver("AR", "AR-C", VatConditions.ConsumidorFinal), lines, On);

        Assert.HasCount(1, taxes);
        Assert.AreEqual(0m, taxes[0].Amount);
        Assert.IsFalse(taxes[0].AddsToTotal);
        Assert.AreEqual("ALÍCUOTA ISIB CABA 3 %", taxes[0].Legend);
        Assert.IsEmpty(before, "La leyenda rige desde 2027.");
    }

    [TestMethod]
    public void ElDocumento_SumaLosTributosAlTotal_YCongelaLasLeyendas()
    {
        var doc = new Document(DocumentKind.Invoice, 1, Currency.Ars, PriceMode.TaxIncluded, On, DateTimeOffset.UnixEpoch);
        doc.AddLine(LineKind.Renewal, "Hosting", 1, 12100m, TaxTreatment.Taxed, 0.21m, category: TaxCategories.Digital, reason: TaxReasons.Taxed, legend: "Leyenda del renglón");
        var breakdown = doc.PrepareIssue();
        var taxes = TaxEngine.ComputeDocumentTaxes([Perception], Rg2126(), new TaxReceiver("AR", null, VatConditions.NoCategorizado), doc.TaxedLines(), On);

        doc.Issue(On, "{}", DateTimeOffset.UnixEpoch, breakdown, taxes, ["Leyenda del comprobante"]);
        doc.AssignNumber(1);

        Assert.AreEqual(1270.50m, doc.OtherTaxes);
        Assert.AreEqual(13370.50m, doc.Total);
        Assert.AreEqual(13370.50m, doc.Pending);
        Assert.AreEqual(taxes[0], doc.IssuedTaxes().Single());
        CollectionAssert.AreEqual(new[] { "Leyenda del comprobante", "Leyenda del renglón" }, doc.IssuedLegends().ToList());
        Assert.AreEqual(TaxCategories.Digital, doc.Lines[0].Category);
        Assert.AreEqual(TaxReasons.Taxed, doc.Lines[0].Reason);
    }

    [TestMethod]
    public void NotaQueCompletaElOriginal_TomaExactoLoQueFaltaDelTributo()
    {
        var original = new List<DocumentTaxAmount> { new(Tax.VatPerceptionRg2126, "Percepción", 13, 12100m, 0.105m, 1270.50m, true, TaxReasons.Perception) };
        var partial = new List<DocumentTaxAmount> { new(Tax.VatPerceptionRg2126, "Percepción", 13, 6050m, 0.105m, 635.25m, true, TaxReasons.Perception) };
        var rest = Lines(PriceMode.TaxIncluded, (TaxCategories.Digital, 6050m, 0.21m));

        var note = DocumentTaxRules.ForNote(original, [partial], rest, completesOriginal: true);

        Assert.AreEqual(635.25m, note.Single().Amount);
        Assert.AreEqual(6050m, note.Single().Base);
    }

    [TestMethod]
    public void NotaParcial_RecalculaElTributoSobreSuBase_ConTopeEnLoQueFalta()
    {
        var original = new List<DocumentTaxAmount> { new(Tax.VatPerceptionRg2126, "Percepción", 13, 12100m, 0.105m, 1270.50m, true, TaxReasons.Perception) };
        var half = Lines(PriceMode.TaxIncluded, (TaxCategories.Digital, 6050m, 0.21m));
        var almostAll = Lines(PriceMode.TaxIncluded, (TaxCategories.Digital, 12100m, 0.21m));
        var credited = new List<DocumentTaxAmount> { new(Tax.VatPerceptionRg2126, "Percepción", 13, 1000m, 0.105m, 1200m, true, TaxReasons.Perception) };

        Assert.AreEqual(635.25m, DocumentTaxRules.ForNote(original, [], half, completesOriginal: false).Single().Amount);
        Assert.AreEqual(70.50m, DocumentTaxRules.ForNote(original, [credited], almostAll, completesOriginal: false).Single().Amount, "Topeado en lo que falta revertir.");
    }

    [TestMethod]
    public void NotaParcial_UsaLaBaseDelTributo_YRepartePorCategoria()
    {
        // Ingresos brutos al 3 % sobre el neto: una nota por $6.050 brutos (neto 5.000) revierte 150, no 181,50.
        var grossIncome = new List<DocumentTaxAmount> { new("perc_iibb_ar_b", "Percepción IIBB AR-B", 7, 10000m, 0.03m, 300m, true, TaxReasons.Perception, null, TaxCategories.Digital, TaxBase.Net) };
        var half = Lines(PriceMode.TaxIncluded, (TaxCategories.Digital, 6050m, 0.21m));
        var partial = DocumentTaxRules.ForNote(grossIncome, [], half, false).Single();
        Assert.AreEqual((5000m, 150m), (partial.Base, partial.Amount));

        // Hosting y telefonía con su percepción cada uno: la nota de solo la telefonía revierte solo la del 13,5 %.
        var twoRates = new List<DocumentTaxAmount>
        {
            new(Tax.VatPerceptionRg2126, "Percepción", 13, 12100m, 0.105m, 1270.50m, true, TaxReasons.Perception, null, TaxCategories.Digital),
            new(Tax.VatPerceptionRg2126, "Percepción", 13, 12100m, 0.135m, 1633.50m, true, TaxReasons.Perception, null, TaxCategories.Telecom),
        };
        var phoneOnly = Lines(PriceMode.TaxIncluded, (TaxCategories.Telecom, 12100m, 0.21m));

        var note = DocumentTaxRules.ForNote(twoRates, [], phoneOnly, false);

        Assert.AreEqual((0.135m, 12100m, 1633.50m), (note.Single().Rate, note.Single().Base, note.Single().Amount));
    }

    private static List<TaxedLine> Lines(PriceMode mode, params (string Category, decimal Amount, decimal Rate)[] lines)
    {
        var breakdown = TaxCalculator.Compute(mode, [.. lines.Select((l, i) => new TaxableLine(i.ToString(System.Globalization.CultureInfo.InvariantCulture), l.Amount, TaxTreatment.Taxed, l.Rate))]);
        return [.. lines.Select((l, i) => new TaxedLine(l.Category, breakdown.Lines[i]))];
    }

    [TestMethod]
    public void LaReglaMasEspecificaGana_YElEmpateFalla()
    {
        var rules = Argentina();
        rules.Add(new TaxRule { Country = "AR", VatCondition = VatConditions.ConsumidorFinal, Outcome = TaxTreatment.Taxed, Rate = 0.105m, ValidFrom = new(2026, 1, 1) });

        Assert.AreEqual(0.105m, TaxEngine.ResolveLine(Digital, new TaxReceiver("AR", null, VatConditions.ConsumidorFinal), rules, On).Rate);

        rules.Add(new TaxRule { Country = "AR", VatCondition = VatConditions.ConsumidorFinal, Outcome = TaxTreatment.Taxed, Rate = 0.21m, ValidFrom = new(2026, 1, 1) });
        Assert.ThrowsExactly<InvalidOperationException>(() => TaxEngine.ResolveLine(Digital, new TaxReceiver("AR", null, VatConditions.ConsumidorFinal), rules, On));
    }

    [TestMethod]
    public void ModoDeCarga_CongeladoEnElServicio_YPorDefectoPorMoneda()
    {
        var settings = new TaxSettings();
        settings.SetDefaultPriceMode(Currency.Usd, PriceMode.TaxExcluded);
        var service = new Service(1, 1, BillingCycle.Monthly, Currency.Ars, 12100m, 1, DateTimeOffset.UnixEpoch, PriceMode.TaxIncluded);

        settings.SetDefaultPriceMode(Currency.Ars, PriceMode.TaxExcluded);

        Assert.AreEqual(PriceMode.TaxIncluded, service.PriceMode, "Cambiar el ajuste no cambia lo que paga un servicio que ya existe.");
        Assert.AreEqual(PriceMode.TaxExcluded, settings.DefaultPriceMode(Currency.Ars));
        Assert.AreEqual(PriceMode.TaxExcluded, settings.DefaultPriceMode(Currency.Usd));
        Assert.AreEqual(PriceMode.TaxIncluded, settings.DefaultPriceMode(new Currency("EUR")));
    }

    [TestMethod]
    public void CondicionesDelReceptor_SonLasDeLaTablaDeArca()
    {
        Assert.IsTrue(VatConditions.IsKnown(VatConditions.ConsumidorFinal));
        Assert.IsFalse(VatConditions.IsKnown(2));
        Assert.AreEqual("A", VatConditions.ClassFor(VatConditions.Monotributo));
        Assert.AreEqual("B", VatConditions.ClassFor(VatConditions.NoCategorizado));
        Assert.IsTrue(VatConditions.IssuesClassC(VatConditions.Monotributo));
        Assert.IsFalse(VatConditions.IssuesClassC(VatConditions.ResponsableInscripto));
    }

    [TestMethod]
    public void Credencial_SoloSeActivaConCertificadoValidado_YLaClaveDejaLaFila()
    {
        var credential = new FiscalCredential(1, FiscalEnvironments.Homologation, "billingoss", "20000000001", "Prueba SA", "-----BEGIN CERTIFICATE REQUEST-----", DateTimeOffset.UnixEpoch) { PrivateKey = [1, 2, 3] };

        Assert.ThrowsExactly<InvalidOperationException>(() => credential.Activate(DateTimeOffset.UnixEpoch));
        credential.LoadCertificate("-----BEGIN CERTIFICATE-----", "20000000001", "billingoss", "CN=AC", "01", "AB", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(2));
        Assert.AreEqual(CredentialStatus.Ready, credential.Status);
        credential.Activate(DateTimeOffset.UnixEpoch);

        Assert.AreEqual(CredentialStatus.Active, credential.Status);
        Assert.IsNull(credential.PrivateKey);
        Assert.AreEqual(730, credential.DaysLeft(new DateOnly(1970, 1, 1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => credential.LoadCertificate("x", null, null, "y", "z", "w", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }
}
