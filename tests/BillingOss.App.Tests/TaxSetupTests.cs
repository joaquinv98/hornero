using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Configuración impositiva (sección 3 del diseño fiscal): presets, simulador, avisos, frenos y lo que viaja al fisco.</summary>
[TestClass]
public sealed class TaxSetupTests
{
    private static readonly TaxPreset Argentina = new("AR", VatConditions.ResponsableInscripto, SellsAbroad: true, SellsToEuConsumers: false, SellsToUkConsumers: false, PerceivesRg2126: true, PaysGrossIncomeInCaba: false);

    private static Task Preset(CoreHarness h, TaxPreset preset) => h.Use<TaxSetupService, bool>(async s => { await s.ApplyPreset(preset); return true; });

    private static Task<Simulation> Simulate(CoreHarness h, SimulationInput input) => h.Use<TaxSimulator, Simulation>(s => s.Run(input));

    /// <summary>Una cuenta argentina con la condición dada, en pesos.</summary>
    private static Task<long> Account(CoreHarness h, int vatCondition, string email, string? province = null, bool largeCompany = false) =>
        h.Use<AccountService, long>(async s =>
        {
            var id = await s.Create(new AccountInput("Cliente", email, Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "30712345671", "Cliente SA", vatCondition, "AR", province)));
            if (largeCompany)
            {
                await s.SetLargeCompany(id, true);
            }

            return id;
        });

    [TestMethod]
    public async Task Preset_ResponsableInscripto_DejaElSimuladorComoLaTablaDelDiseño()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);

        var consumer = await Simulate(h, new SimulationInput("AR", "AR-C", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded));
        var business = await Simulate(h, new SimulationInput("AR", "AR-X", VatConditions.ResponsableInscripto, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded));
        var phone = await Simulate(h, new SimulationInput("AR", null, VatConditions.ResponsableInscripto, TaxCategories.Telecom, 12100m, Currency.Ars, PriceMode.TaxIncluded));
        var uncategorized = await Simulate(h, new SimulationInput("AR", null, VatConditions.NoCategorizado, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded));
        var foreign = await Simulate(h, new SimulationInput("US", null, VatConditions.ClienteDelExterior, TaxCategories.Digital, 100m, Currency.Usd, PriceMode.TaxExcluded));

        Assert.AreEqual(("B", 10000m, 2100m, 12100m, TaxReasons.Taxed), (consumer.Letter, consumer.Net, consumer.Tax, consumer.Total, consumer.Reason));
        Assert.AreEqual(("A", 10000m, 2100m, 12100m), (business.Letter, business.Net, business.Tax, business.Total));
        Assert.AreEqual(("A", 0.27m, 9527.56m, 2572.44m, 12100m), (phone.Letter, phone.Rate, phone.Net, phone.Tax, phone.Total));
        Assert.AreEqual(("B", 2100m, 13370.50m), (uncategorized.Letter, uncategorized.Tax, uncategorized.Total));
        Assert.AreEqual((Tax.VatPerceptionRg2126, 0.105m, 1270.50m, TaxReasons.Perception), (uncategorized.Taxes.Single().Code, uncategorized.Taxes.Single().Rate, uncategorized.Taxes.Single().Amount, uncategorized.Taxes.Single().Reason));
        Assert.AreEqual(("E", 100m, 0m, 100m, TaxReasons.Export), (foreign.Letter, foreign.Net, foreign.Tax, foreign.Total, foreign.Reason));
        Assert.IsEmpty(foreign.Taxes);
        Assert.IsTrue(await h.Query(db => db.TaxRules.AnyAsync(r => r.ValidTo != null)), "Las reglas de fábrica de la base de prueba se cierran: nada se edita.");
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "taxes.preset")));
    }

    [TestMethod]
    public async Task Preset_Monotributista_NoCobraIva_YElComprobanteEsClaseC_ConElNetoEnImpNeto()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;

        // El preset cambia la condición del emisor activo, que venía como responsable inscripto con mapa A y B: el
        // mapa de tipos se rehace con la clase C, si no la emisión muere buscando un tipo que el mapa no tiene.
        await Preset(h, Argentina with { IssuerVatCondition = VatConditions.Monotributo });
        var issuer = await h.Query(db => db.IssuerProfiles.SingleAsync());
        Assert.AreEqual(VatConditions.Monotributo, issuer.VatCondition);
        CollectionAssert.AreEquivalent(new[] { "C", "E" }, JsonNode.Parse(issuer.TypeMap)!.AsObject().Select(e => e.Key).ToList());

        var simulated = await Simulate(h, new SimulationInput("AR", null, VatConditions.ResponsableInscripto, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded));
        Assert.AreEqual(("C", TaxTreatment.NotTaxed, 0m, 12100m, TaxReasons.Monotax), (simulated.Letter, simulated.Treatment, simulated.Tax, simulated.Total, simulated.Reason));

        var placed = await FiscalFlowTests.Order(h);
        await h.RunJobs();

        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual((0m, 12100m, 12100m), (invoice.Tax, invoice.NotTaxed, invoice.Total));
        var sent = fisco.Authorizations.Single()["document"]!;
        Assert.AreEqual(11, sent["type_code"]!.GetValue<int>());
        Assert.AreEqual("12100.00", sent["amounts"]!["net_taxed"]!.GetValue<string>(), "En C el neto es el subtotal (validaciones 10043 a 10048).");
        Assert.AreEqual("0.00", sent["amounts"]!["net_untaxed"]!.GetValue<string>());
        Assert.IsEmpty(sent["amounts"]!["vat"]!.AsArray());
    }

    [TestMethod]
    public async Task PercepcionRg2126_ViajaAlFiscoComoTributo_YLaNotaLaRevierteConPeriodoAsociado()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var account = await Account(h, VatConditions.NoCategorizado, "nocat@example.com");

        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        await h.RunJobs();

        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual((1270.50m, 13370.50m, 13370.50m), (invoice.OtherTaxes, invoice.Total, invoice.Pending));
        var tax = invoice.IssuedTaxes().Single();
        Assert.AreEqual((Tax.VatPerceptionRg2126, 13, 12100m, 0.105m, 1270.50m), (tax.Code, tax.FiscalCode, tax.Base, tax.Rate, tax.Amount));
        var sent = fisco.Authorizations.Single()["document"]!;
        Assert.AreEqual("1270.50", sent["amounts"]!["other_taxes"]!.GetValue<string>());
        var tribute = sent["amounts"]!["taxes"]![0]!;
        Assert.AreEqual((13, "12100.00", "10.5", "1270.50"), (tribute["id"]!.GetValue<int>(), tribute["base"]!.GetValue<string>(), tribute["rate"]!.GetValue<string>(), tribute["amount"]!.GetValue<string>()));
        Assert.AreEqual("13370.50", sent["amounts"]!["total"]!.GetValue<string>());
        Assert.IsNull(sent["period_assoc"]);

        Assert.IsNotNull(await h.Use<CreditService, long?>(s => s.CancelIfUnpaid(placed.InvoiceId!.Value, "Anulación")));
        await h.RunJobs();

        var note = await h.Query(db => db.Documents.SingleAsync(d => d.Kind == DocumentKind.CreditNote));
        Assert.AreEqual((1270.50m, 13370.50m), (note.OtherTaxes, note.Total));
        var noteSent = fisco.Authorizations.Last()["document"]!;
        Assert.AreEqual("1270.50", noteSent["amounts"]!["taxes"]![0]!["amount"]!.GetValue<string>());
        Assert.AreEqual("2026-11-01", noteSent["period_assoc"]!["from"]!.GetValue<string>());
        Assert.AreEqual("2026-11-30", noteSent["period_assoc"]!["to"]!.GetValue<string>());
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Pending);
    }

    [TestMethod]
    public async Task NotaParcialSinCategoria_TomaLaDeLaFactura_YRevierteLaPercepcionDeTelecomunicaciones()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var account = await Account(h, VatConditions.NoCategorizado, "nocat@example.com");
        await h.Db(async db =>
        {
            var plan = await db.Plans.SingleAsync(p => p.Id == h.PlanId);
            (await db.Products.SingleAsync(p => p.Id == plan.ProductId)).TaxCategory = TaxCategories.Telecom;
            await db.SaveChangesAsync();
        });

        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        var perception = (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).IssuedTaxes().Single();
        Assert.AreEqual((0.135m, TaxCategories.Telecom, 1633.50m), (perception.Rate, perception.Categories, perception.Amount));

        // La línea llega sin categoría, como la puede mandar la API: toma la de la factura y revierte su parte.
        var noteId = await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Bonificación", 1, 6050m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));
        var reverted = (await h.Query(db => db.Documents.SingleAsync(d => d.Id == noteId))).IssuedTaxes().Single();
        Assert.AreEqual((6050m, 816.75m), (reverted.Base, reverted.Amount));
    }

    [TestMethod]
    public async Task NoCategorizado_SinPercepcionConfigurada_LaFichaNoLoAcepta_YUnaFacturaSeFrenaAntesDeLlamarAlFisco()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;

        // Sin la percepción de la RG 2126 la ficha no acepta la condición 7 (sección 3.8 del diseño fiscal).
        var rejected = await Assert.ThrowsExactlyAsync<ValidationException>(() => Account(h, VatConditions.NoCategorizado, "nocat@example.com"));
        StringAssert.Contains(rejected.Message, "RG 2126");

        // Con la percepción configurada entra. Si después la cierran, la factura se frena antes de ir a ARCA y el
        // motivo dice cómo se arregla: la factura emitida se anula con nota de crédito y se vuelve a emitir.
        await Preset(h, Argentina);
        var account = await Account(h, VatConditions.NoCategorizado, "nocat@example.com");
        var today = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime);
        foreach (var rule in await h.Query(db => db.TaxRules.Where(r => r.TaxCode == Tax.VatPerceptionRg2126 && r.ValidTo == null).ToListAsync()))
        {
            await h.Use<TaxSetupService, bool>(async s => { await s.CloseRule(rule.Id, today); return true; });
        }

        // Solo el pase a la 7 exige la percepción: la cuenta que ya la tenía se sigue editando, y otra no puede pasar a la 7.
        await h.Use<AccountService, bool>(async s => { await s.Update(account, new AccountInput("Cliente renombrado", "nocat@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "30712345671", "Cliente SA", VatConditions.NoCategorizado, "AR"))); return true; });
        Assert.AreEqual("Cliente renombrado", (await h.Query(db => db.Accounts.SingleAsync(a => a.Id == account))).Name);
        var other = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Otro", "otro@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "20123456786", "Otro", VatConditions.ConsumidorFinal, "AR"))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AccountService, bool>(async s => { await s.Update(other, new AccountInput("Otro", "otro@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "20123456786", "Otro", VatConditions.NoCategorizado, "AR"))); return true; }));

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Rejected, emission.Status);
        StringAssert.Contains(emission.LastError!, "RG 2126");
        StringAssert.Contains(emission.LastError!, "nota de crédito");
        Assert.AreEqual(0, fisco.Calls);
        var warnings = await h.Use<TaxSetupService, IReadOnlyList<TaxWarning>>(s => s.Warnings());
        Assert.AreEqual("1", warnings.Single(w => w.Code == "no_categorizado_sin_percepcion").Detail);
    }

    [TestMethod]
    public async Task Provincia_SeGuardaComoIso3166_2_YUnRegimenDeIngresosBrutosAlcanzaALaCuentaImportadaConElNombre()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var today = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime);

        var account = await Account(h, VatConditions.ConsumidorFinal, "pba@example.com", "buenos aires");
        Assert.AreEqual("AR-B", (await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == account && t.ValidTo == null))).Province);
        var unknown = await Assert.ThrowsExactlyAsync<ValidationException>(() => Account(h, VatConditions.ConsumidorFinal, "marte@example.com", "Marte"));
        StringAssert.Contains(unknown.Message, "ISO 3166-2");
        Assert.AreEqual("AR-C", ArgentinaProvinces.Code("Capital Federal"));
        Assert.AreEqual("AR-E", ArgentinaProvinces.Code("entre rios"));
        Assert.IsNull(ArgentinaProvinces.Code("Rosario"));

        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.03m, TaxBase.Net, today)); return true; });
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual((300m, 12400m), (invoice.OtherTaxes, invoice.Total), "La cuenta cargada con el nombre de la provincia entra en el régimen de AR-B.");
    }

    [TestMethod]
    public async Task EmpresaGrande_ConUnImporteDeFactoraDeCreditoElectronica_SeFrenaConElMotivo()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        var account = await Account(h, VatConditions.ResponsableInscripto, "grande@example.com", largeCompany: true);
        long plan = 0;
        await h.Db(async db =>
        {
            var product = new Product { Name = "Cluster", Kind = ProductKind.Manual };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            var p = new Plan(product.Id, BillingCycle.Monthly);
            db.Plans.Add(p);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new PlanPrice(p.Id, Currency.Ars, 6_000_000m, 0m, new DateOnly(2026, 1, 1)));
            await db.SaveChangesAsync();
            plan = p.Id;
        });

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(plan)]));
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Rejected, emission.Status);
        StringAssert.Contains(emission.LastError!, "FCE");
        Assert.AreEqual(0, fisco.Calls);

        // Por debajo del mínimo, la misma cuenta factura normal.
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);

        // En dólares el mínimo se compara en pesos con la cotización del fisco (1450,5): USD 6.050 (5.000 más IVA) son 8.775.525 y se frena; USD 121 no.
        var dollars = await h.Use<AccountService, long>(async s =>
        {
            var id = await s.Create(new AccountInput("Grande USD", "grande-usd@example.com", Currency.Usd, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "30712345671", "Grande SA", VatConditions.ResponsableInscripto, "AR")));
            await s.SetLargeCompany(id, true);
            return id;
        });
        long big = 0;
        long small = 0;
        await h.Db(async db =>
        {
            db.PlanPrices.Add(new PlanPrice(plan, Currency.Usd, 5_000m, 0m, new DateOnly(2026, 1, 1), PriceMode.TaxExcluded));
            db.PlanPrices.Add(new PlanPrice(h.PlanId, Currency.Usd, 100m, 0m, new DateOnly(2026, 1, 1), PriceMode.TaxExcluded));
            await db.SaveChangesAsync();
            big = plan;
            small = h.PlanId;
        });
        var bigOrder = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(dollars, [new OrderItem(big)]));
        await h.RunJobs();
        var stopped = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == bigOrder.InvoiceId));
        Assert.AreEqual(EmissionStatus.Rejected, stopped.Status);
        StringAssert.Contains(stopped.LastError!, "6050.00 USD, 8775525.00 pesos");
        Assert.AreEqual(1, fisco.Authorized, "No llegó al fisco.");
        var smallOrder = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(dollars, [new OrderItem(small)]));
        await h.RunJobs();
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == smallOrder.InvoiceId))).Status);
    }

    [TestMethod]
    public async Task NumeroDeIva_ValidadoEnVies_AplicaLaInversionDelSujetoPasivo_YSinValidarPagaElIvaDeSuPais()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina with { SellsToEuConsumers = true });
        await h.Db(async db =>
        {
            db.PlanPrices.Add(new PlanPrice(h.PlanId, new Currency("EUR"), 100m, 0m, new DateOnly(2026, 1, 1), PriceMode.TaxExcluded));
            await db.SaveChangesAsync();
        });
        var account = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Firma GmbH", "firma@example.de", new Currency("EUR"), Tax: new TaxIdentityInput(TaxDocumentType.Foreign, "HRB1", "Firma GmbH", VatConditions.ClienteDelExterior, "DE", VatNumber: "123456789"))));

        var beforeValidation = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        var consumer = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == beforeValidation.InvoiceId));
        Assert.AreEqual((0.19m, 119m), (consumer.Lines[0].Rate, consumer.Total));

        h.Vies.Valid["DE123456789"] = ("Firma GmbH", "Berlin");
        var check = await h.Use<AccountService, VatNumberCheck>(s => s.ValidateVatNumber(account));
        Assert.IsTrue(check.Valid);
        StringAssert.StartsWith(check.RequestId!, "WAPIAAAAZ");
        var identity = await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == account && t.ValidTo == null));
        Assert.AreEqual(check.RequestId, identity.VatValidationRef);
        Assert.IsTrue(identity.ValidatedBusiness);

        var afterValidation = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        var business = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == afterValidation.InvoiceId));
        Assert.AreEqual((TaxTreatment.Exempt, 100m, TaxReasons.ReverseCharge), (business.Lines[0].Treatment, business.Total, business.Lines[0].Reason));
        StringAssert.Contains(business.IssuedLegends().Single(), "Reverse charge");

        // Corregir otro dato de la ficha abre una versión nueva que hereda la constancia: el número y el país no cambiaron.
        // Escribir el mismo número en minúsculas no abre versión. Cambiarlo sí, y la constancia se pierde.
        var firm = await h.Query(db => db.Accounts.SingleAsync(a => a.Id == account));
        await h.Use<AccountService, bool>(async s => { await s.Update(account, new AccountInput(firm.Name, firm.Email, firm.Currency, Tax: new TaxIdentityInput(TaxDocumentType.Foreign, "HRB1", "Firma GmbH & Co", VatConditions.ClienteDelExterior, "de", VatNumber: "123456789"))); return true; });
        var renamed = await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == account && t.ValidTo == null));
        Assert.AreEqual(("Firma GmbH & Co", check.RequestId, true), (renamed.LegalName, renamed.VatValidationRef, renamed.ValidatedBusiness));
        var versions = await h.Query(db => db.TaxIdentities.CountAsync(t => t.AccountId == account));
        await h.Use<AccountService, bool>(async s => { await s.Update(account, new AccountInput(firm.Name, firm.Email, firm.Currency, Tax: new TaxIdentityInput(TaxDocumentType.Foreign, "HRB1", "Firma GmbH & Co", VatConditions.ClienteDelExterior, "DE", VatNumber: " 123456789 "))); return true; });
        Assert.AreEqual(versions, await h.Query(db => db.TaxIdentities.CountAsync(t => t.AccountId == account)), "Lo mismo en otra caja no abre una versión.");
        await h.Use<AccountService, bool>(async s => { await s.Update(account, new AccountInput(firm.Name, firm.Email, firm.Currency, Tax: new TaxIdentityInput(TaxDocumentType.Foreign, "HRB1", "Firma GmbH & Co", VatConditions.ClienteDelExterior, "DE", VatNumber: "987654321"))); return true; });
        var changed = await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == account && t.ValidTo == null));
        Assert.IsFalse(changed.ValidatedBusiness);
        var afterChange = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)]));
        Assert.AreEqual(119m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == afterChange.InvoiceId))).Total);

        // Un número que VIES rechaza borra la constancia, y el país sin registro no se valida.
        h.Vies.Valid.Clear();
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AccountService, VatNumberCheck>(s => s.ValidateVatNumber(account)));
        Assert.IsNull((await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == account && t.ValidTo == null))).VatValidationRef);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AccountService, VatNumberCheck>(s => s.ValidateVatNumber(h.AccountId)));
    }

    [TestMethod]
    public async Task FichaFiscal_CorregidaSinElDomicilio_LoConserva_YOtroTipoDeDocumentoAbreVersion()
    {
        await using var h = await CoreHarness.Start();
        var account = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Cliente", "domicilio@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "30712345671", "Cliente SA", VatConditions.ResponsableInscripto, "AR", "AR-B", "La Plata", "Calle 7 1234", "1900"))));

        // Las fichas del admin y del portal no mandan el domicilio: corregir la razón social no lo borra.
        await h.Use<AccountService, bool>(async s => { await s.Update(account, new AccountInput("Cliente", "domicilio@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "30712345671", "Cliente SRL", VatConditions.ResponsableInscripto, "AR", "AR-B"))); return true; });
        var renamed = await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == account && t.ValidTo == null));
        Assert.AreEqual(("Cliente SRL", "Calle 7 1234", "La Plata", "1900"), (renamed.LegalName, renamed.Street, renamed.City, renamed.PostalCode));

        await h.Use<AccountService, bool>(async s => { await s.Update(account, new AccountInput("Cliente", "domicilio@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuil, "30712345671", "Cliente SRL", VatConditions.ResponsableInscripto, "AR", "AR-B"))); return true; });
        Assert.AreEqual(TaxDocumentType.Cuil, (await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == account && t.ValidTo == null))).DocumentType);
    }

    [TestMethod]
    public async Task ModoDeCarga_PorPrecio_CongeladoEnElServicio_YUnPedidoQueMezclaModosSaleEnDosFacturas()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        long net = 0;
        await h.Db(async db =>
        {
            var product = new Product { Name = "Soporte", Kind = ProductKind.Manual };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            var plan = new Plan(product.Id, BillingCycle.Monthly);
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new PlanPrice(plan.Id, Currency.Ars, 10000m, 0m, new DateOnly(2026, 1, 1), PriceMode.TaxExcluded));
            await db.SaveChangesAsync();
            net = plan.Id;
        });

        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId), new OrderItem(net)]));

        Assert.HasCount(2, placed.InvoiceIds!);
        var invoices = await h.Query(db => db.Documents.Where(d => placed.InvoiceIds!.Contains(d.Id)).OrderBy(d => d.Id).ToListAsync());
        Assert.AreEqual((PriceMode.TaxIncluded, 12100m), (invoices[0].PriceMode, invoices[0].Total));
        Assert.AreEqual((PriceMode.TaxExcluded, 12100m), (invoices[1].PriceMode, invoices[1].Total));
        var services = await h.Query(db => db.Services.Where(s => placed.ServiceIds.Contains(s.Id)).OrderBy(s => s.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { PriceMode.TaxIncluded, PriceMode.TaxExcluded }, services.Select(s => s.PriceMode).ToList());

        // Cambiar el valor por defecto de la moneda no toca lo que paga un servicio que ya existe: pagadas las dos
        // facturas y activos los servicios, la renovación sale con el modo congelado en cada uno.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-modos", Currency.Ars, 24200m, BillingOss.Domain.Payments.PaymentStatus.Approved)));
        await h.RunJobs();
        await h.Db(async db =>
        {
            var settings = await db.TaxSettings.SingleAsync();
            settings.SetDefaultPriceMode(Currency.Ars, PriceMode.TaxExcluded);
            await db.SaveChangesAsync();
        });
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        var renewals = await h.Query(db => db.Documents.Where(d => d.Reason == "renewal").ToListAsync());
        Assert.HasCount(2, renewals);
        Assert.AreEqual(12100m, renewals.Single(r => r.PriceMode == PriceMode.TaxIncluded).Total);
    }

    [TestMethod]
    public async Task Avisos_DicenLoQuePuedeEstarMal()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina with { PerceivesRg2126 = false });
        await h.Db(async db =>
        {
            db.Products.Add(new Product { Name = "Telefonía", Kind = ProductKind.Manual, TaxCategory = TaxCategories.Telecom });
            foreach (var rule in await db.TaxRules.Where(r => r.Category == TaxCategories.Telecom && r.ValidTo == null).ToListAsync())
            {
                rule.ValidTo = new DateOnly(2026, 11, 1);
            }

            var account = new Account("Brasil", "br@example.com", Currency.Usd, h.Clock.Now);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            db.TaxIdentities.Add(new TaxIdentity(account.Id, TaxDocumentType.Foreign, "1", "Brasil", VatConditions.ClienteDelExterior, "BR", h.Clock.Now));
            db.TaxRules.Add(new TaxRule { AccountId = h.AccountId, Outcome = TaxTreatment.Exempt, Reason = TaxReasons.ExemptByLaw, CertificateRef = "Constancia 1", ValidFrom = new DateOnly(2026, 1, 1), ValidTo = new DateOnly(2026, 11, 20) });

            // Como las deja el importador: condición 7 sin percepción y la provincia con el nombre del origen.
            var imported = new Account("Importada", "nocat@example.com", Currency.Ars, h.Clock.Now);
            db.Accounts.Add(imported);
            await db.SaveChangesAsync();
            db.TaxIdentities.Add(new TaxIdentity(imported.Id, TaxDocumentType.Cuit, "30712345671", "Importada SA", VatConditions.NoCategorizado, "AR", h.Clock.Now) { Province = "Buenos Aires" });
            await db.SaveChangesAsync();
        });

        var warnings = (await h.Use<TaxSetupService, IReadOnlyList<TaxWarning>>(s => s.Warnings())).ToDictionary(w => w.Code, w => w.Detail);

        Assert.AreEqual("1", warnings["telecom_sin_27"]);
        Assert.AreEqual("BR", warnings["pais_sin_regla"]);
        Assert.AreEqual("1", warnings["no_categorizado_sin_percepcion"]);
        Assert.AreEqual("1", warnings["provincia_fuera_de_tabla"]);
        Assert.AreEqual("1", warnings["regla_de_cuenta_por_vencer"]);
        Assert.IsFalse(warnings.ContainsKey("monotributista_con_iva"));
    }

    [TestMethod]
    public async Task CondicionDelReceptor_SoloLosCodigosDeLaTablaDeArca()
    {
        await using var h = await CoreHarness.Start();

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Account(h, 2, "x@example.com"));
        await Account(h, VatConditions.LiberadoLey19640, "tdf@example.com", "AR-V");
    }

    [TestMethod]
    public async Task ReglaDeCuentaConConstancia_EximeLaProximaFactura_SinCambiarElPrecioFinal_YCerradaVuelveElIva()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var today = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, TaxRule>(s => s.AddAccountRule(new AccountRuleInput(h.AccountId, Tax.Vat, TaxTreatment.Exempt, 0m, " ", "Constancia 123", today))));
        var rule = await h.Use<TaxSetupService, TaxRule>(s => s.AddAccountRule(new AccountRuleInput(h.AccountId, Tax.Vat, TaxTreatment.Exempt, 0m, TaxReasons.ExemptByLaw, "Constancia de exención 2026-123", today, today.AddYears(1))));
        Assert.AreEqual(("Constancia de exención 2026-123", h.AccountId), (rule.CertificateRef, rule.AccountId));
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "taxes.account_rule.add" && a.Entity == $"account:{h.AccountId}")));

        var exempt = await FiscalFlowTests.Order(h);
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == exempt.InvoiceId));
        Assert.AreEqual((0m, 12100m, TaxTreatment.Exempt, TaxReasons.ExemptByLaw), (invoice.Tax, invoice.Total, invoice.Lines[0].Treatment, invoice.Lines[0].Reason), "Con precios con IVA incluido el cliente paga lo mismo y sube el neto.");

        await h.Use<TaxSetupService, bool>(async s => { await s.CloseRule(rule.Id, today); return true; });
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, bool>(async s => { await s.CloseRule(rule.Id, today.AddDays(-1)); return true; }));
        var taxed = await FiscalFlowTests.Order(h);
        Assert.AreEqual(2100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == taxed.InvoiceId))).Tax);
        Assert.AreEqual(today, (await h.Query(db => db.TaxRules.SingleAsync(r => r.Id == rule.Id))).ValidTo);
    }

    [TestMethod]
    public async Task IngresosBrutos_Configurable_PercibeEnSuJurisdiccion_ConElCodigo7_YNoEnOtra_NiAlExcluido()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var today = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("Buenos Aires", "ARBA", "1", 0.03m, TaxBase.Net, today)); return true; }));
        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.03m, TaxBase.Net, today, ExcludedConditions: [VatConditions.ResponsableInscripto])); return true; });

        var consumer = await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded));
        var perception = consumer.Taxes.Single();
        Assert.AreEqual(("perc_iibb_ar_b", 7, 10000m, 0.03m, 300m, TaxReasons.Perception), (perception.Code, perception.FiscalCode, perception.Base, perception.Rate, perception.Amount, perception.Reason));
        Assert.AreEqual(12400m, consumer.Total);
        Assert.IsEmpty((await Simulate(h, new SimulationInput("AR", "AR-C", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes, "Otra jurisdicción no percibe.");
        Assert.IsEmpty((await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ResponsableInscripto, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes, "La condición excluida no percibe.");
        Assert.AreEqual("AR-B", (await h.Query(db => db.TaxSettings.SingleAsync())).Regimes().Single().Jurisdiction);

        // Se vuelve a cargar con otra alícuota: la regla anterior se cierra y la nueva manda.
        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.05m, TaxBase.Net, today)); return true; });
        Assert.AreEqual(500m, (await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes.Single().Amount);

        await h.Use<TaxSetupService, bool>(async s => { await s.CloseGrossIncomeRegime("AR-B", today); return true; });
        Assert.IsEmpty((await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes);
        Assert.AreEqual(today, (await h.Query(db => db.TaxSettings.SingleAsync())).Regimes().Single().ValidTo);

        // Un régimen con fin y otro cargado encima antes de ese fin: el anterior se cierra al empezar el nuevo, sin empate.
        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.03m, TaxBase.Net, today, today.AddDays(30))); return true; });
        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.04m, TaxBase.Net, today)); return true; });
        Assert.AreEqual(400m, (await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes.Single().Amount);

        // Una alícuota nueva cargada hoy con fecha futura: el vigente sigue percibiendo hasta ese día y desde ahí manda la nueva.
        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.02m, TaxBase.Net, today.AddDays(10))); return true; });
        Assert.AreEqual(400m, (await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes.Single().Amount, "Hasta que empiece el nuevo percibe el vigente.");

        // La base es del impuesto: uno con fecha futura y otra base no entra mientras perciba el vigente, y el día que empieza sí.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.02m, TaxBase.NetPlusLineTaxes, today.AddDays(20))); return true; }));
        h.Clock.Now = h.Clock.Now.AddDays(10);
        Assert.AreEqual(200m, (await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes.Single().Amount);
        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.02m, TaxBase.NetPlusLineTaxes, today.AddDays(10))); return true; });
        Assert.AreEqual(242m, (await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Taxes.Single().Amount);
    }

    [TestMethod]
    public async Task Preset_AplicadoDeNuevo_CierraTambienLasReglasConFin_YDejaLosRegimenesDeIngresosBrutos()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var today = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime);
        await h.Use<TaxSetupService, bool>(async s => { await s.SetGrossIncomeRegime(new GrossIncomeRegime("AR-B", "ARBA régimen general", "30712345678", 0.03m, TaxBase.Net, today)); return true; });

        // La regla general del IVA queda con fin la semana que viene: el preset nuevo abre otra con los mismos filtros.
        var vat = await h.Query(db => db.TaxRules.SingleAsync(r => r.TaxCode == Tax.Vat && r.AccountId == null && r.Country == "AR" && r.Province == null && r.VatCondition == null && r.Category == null && r.ValidTo == null));
        await h.Use<TaxSetupService, bool>(async s => { await s.CloseRule(vat.Id, today.AddDays(7)); return true; });
        var replaces = await h.Use<TaxSetupService, int>(s => s.PresetReplaces());
        Assert.AreEqual(await h.Query(db => db.TaxRules.CountAsync(r => r.AccountId == null && !r.TaxCode.StartsWith("perc_iibb_") && (r.ValidTo == null || r.ValidTo > today))), replaces, "Empezar avisa las que va a cerrar, sin las del régimen.");
        await Preset(h, Argentina);

        var consumer = await Simulate(h, new SimulationInput("AR", "AR-B", VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded));
        Assert.AreEqual(2100m, consumer.Tax, "Una sola regla de IVA vigente: sin empate.");
        Assert.AreEqual(300m, consumer.Taxes.Single().Amount, "El régimen de ingresos brutos sigue percibiendo, como dice Percepciones.");
        Assert.AreEqual(today, (await h.Query(db => db.TaxRules.SingleAsync(r => r.Id == vat.Id))).ValidTo);
    }

    [TestMethod]
    public async Task ReglaConLosMismosFiltrosYVigenciaSolapada_NoEntra_HastaCerrarLaVigente()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var today = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime);
        var general = await h.Query(db => db.TaxRules.SingleAsync(r => r.TaxCode == Tax.Vat && r.Country == "AR" && r.Category == null && r.VatCondition == null && r.ValidTo == null));

        // Agregar el 10,5 % para AR sin cerrar el 21 % dejaría dos reglas igual de específicas, y el empate rompe el cálculo de todos los argentinos.
        var ex = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, TaxRule>(s => s.AddRule(new TaxRuleInput(Tax.Vat, "AR", null, null, null, null, TaxTreatment.Taxed, 0.105m, null, null, today))));
        StringAssert.Contains(ex.Message, "cerrala primero");
        Assert.AreEqual(2100m, (await Simulate(h, new SimulationInput("AR", null, VatConditions.ConsumidorFinal, TaxCategories.Digital, 12100m, Currency.Ars, PriceMode.TaxIncluded))).Tax, "Sigue calculando con la vigente.");

        // Con otra vigencia, o con otros filtros, sí entra. Cerrada la vigente, la nueva entra desde ese día.
        await h.Use<TaxSetupService, TaxRule>(s => s.AddRule(new TaxRuleInput(Tax.Vat, "AR", "AR-B", null, null, null, TaxTreatment.Taxed, 0.105m, null, null, today)));
        await h.Use<TaxSetupService, bool>(async s => { await s.CloseRule(general.Id, today.AddDays(10)); return true; });
        await h.Use<TaxSetupService, TaxRule>(s => s.AddRule(new TaxRuleInput(Tax.Vat, "AR", null, null, null, null, TaxTreatment.Taxed, 0.105m, null, null, today.AddDays(10))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, TaxRule>(s => s.AddRule(new TaxRuleInput(Tax.Vat, "AR", null, null, null, null, TaxTreatment.Taxed, 0.21m, null, null, today.AddDays(5)))));

        // Lo mismo con las reglas de cuenta.
        await h.Use<TaxSetupService, TaxRule>(s => s.AddAccountRule(new AccountRuleInput(h.AccountId, Tax.Vat, TaxTreatment.Exempt, 0m, TaxReasons.ExemptByLaw, "Constancia 1", today)));
        var twice = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, TaxRule>(s => s.AddAccountRule(new AccountRuleInput(h.AccountId, Tax.Vat, TaxTreatment.NotTaxed, 0m, TaxReasons.Untaxed, null, today.AddDays(1)))));
        StringAssert.Contains(twice.Message, "cerrala primero");
    }

    [TestMethod]
    public async Task ReglaGeneral_SeAgregaYSeCierra_LaCategoriaPropiaEntra_YLaMuestraDeArgentinaNoSeCambia()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await Preset(h, Argentina);
        var today = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime);

        var rule = await h.Use<TaxSetupService, TaxRule>(s => s.AddRule(new TaxRuleInput(Tax.Vat, "uy", null, null, null, null, TaxTreatment.Taxed, 0.22m, null, null, today)));
        Assert.AreEqual("UY", rule.Country);
        var uruguay = await Simulate(h, new SimulationInput("UY", null, VatConditions.ClienteDelExterior, TaxCategories.Digital, 100m, Currency.Usd, PriceMode.TaxExcluded));
        Assert.AreEqual((0.22m, 122m), (uruguay.Rate, uruguay.Total));
        await h.Use<TaxSetupService, bool>(async s => { await s.CloseRule(rule.Id, today); return true; });
        Assert.AreEqual(100m, (await Simulate(h, new SimulationInput("UY", null, VatConditions.ClienteDelExterior, TaxCategories.Digital, 100m, Currency.Usd, PriceMode.TaxExcluded))).Total);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, TaxRule>(s => s.AddRule(new TaxRuleInput("inexistente", null, null, null, null, null, TaxTreatment.Taxed, 0.1m, null, null, today))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, TaxRule>(s => s.AddRule(new TaxRuleInput(Tax.Vat, "AR", null, 2, null, null, TaxTreatment.Taxed, 0.1m, null, null, today))));

        var category = await h.Use<TaxSetupService, TaxCategory>(s => s.AddCategory("capacitacion", "Capacitación", "Cursos exentos por la ley.", TaxTreatment.Exempt));
        Assert.AreEqual(TaxReasons.ExemptByLaw, category.Reason);
        Assert.AreEqual(TaxTreatment.Exempt, (await Simulate(h, new SimulationInput("AR", null, VatConditions.ConsumidorFinal, "capacitacion", 1000m, Currency.Ars, PriceMode.TaxIncluded))).Treatment);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, TaxCategory>(s => s.AddCategory("Capacitación", "x", "x", TaxTreatment.Taxed)));

        var blocked = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TaxSetupService, bool>(async s => { await s.SetDisplay("final", true); return true; }));
        StringAssert.Contains(blocked.Message, "Res. 4/2025");
        await h.Use<TaxSetupService, bool>(async s => { await s.SetDisplay("final_with_net", false); return true; });
        await h.Use<TaxSetupService, bool>(async s => { await s.SetDefaultPriceMode(Currency.Usd, PriceMode.TaxIncluded); return true; });
        var settings = await h.Query(db => db.TaxSettings.SingleAsync());
        Assert.AreEqual((false, PriceMode.TaxIncluded), (settings.BusinessMayViewNet, settings.DefaultPriceMode(Currency.Usd)));
    }
}
