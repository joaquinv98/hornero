using System.Text.Json;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Facturación avanzada: facturas manuales, saldo a favor a pedido y ajustes de saldo con auditoría.</summary>
[TestClass]
public sealed class AdvancedBillingTests
{
    private static readonly LineInput Hours = new("Soporte fuera de abono, 2 horas", 2m, 1000m);

    private static readonly LineInput Hardware = new("Disco de reemplazo", 1m, 500m, TaxTreatment.Exempt);

    internal static Task<long> Manual(CoreHarness h, IReadOnlyList<LineInput> lines, bool draft = false, DateOnly? due = null, string? currency = null) =>
        h.Use<InvoiceService, long>(s => s.CreateManual(new ManualInvoiceInput(h.AccountId, lines, currency, due, draft)));

    internal static Task<long> Adjust(CoreHarness h, decimal amount, string currency = "ARS") =>
        h.Use<InvoiceService, long>(s => s.AdjustCredit(h.AccountId, currency, amount, "Bonificación por la caída del 20/10"));

    internal static Task<List<string>> Payloads(CoreHarness h, string kind) =>
        h.Query(db => db.Jobs.Where(j => j.Kind == kind).OrderBy(j => j.Id).Select(j => j.Payload).ToListAsync());

    [TestMethod]
    public async Task FacturaManual_ConRenglonesPropios_SeEmiteConElImpuestoDelReceptor()
    {
        await using var h = await CoreHarness.Start();

        var id = await Manual(h, [Hours, Hardware]);

        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == id));
        Assert.AreEqual(1L, invoice.Number);
        Assert.AreEqual(DocumentReasons.Manual, invoice.Reason);
        Assert.AreEqual(2500m, invoice.Total);
        Assert.AreEqual(1652.89m, invoice.NetTaxed);
        Assert.AreEqual(347.11m, invoice.Tax);
        Assert.AreEqual(500m, invoice.Exempt);
        Assert.AreEqual(new DateOnly(2026, 11, 9), invoice.DueDate);
        CollectionAssert.AreEqual(new[] { LineKind.Manual, LineKind.Manual }, invoice.Lines.Select(l => l.Kind).ToArray());
        Assert.AreEqual(0.21m, invoice.Lines[0].Rate);
        Assert.AreEqual(TaxTreatment.Exempt, invoice.Lines[1].Treatment);
        Assert.IsTrue((await Payloads(h, JobKinds.NotifyEvent)).Any(p => p.Contains(Events.InvoiceIssued, StringComparison.Ordinal)));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "invoice.manual")));
    }

    [TestMethod]
    public async Task FacturaManual_EnBorrador_NoTieneNumero_YSeEmiteOSeDescarta()
    {
        await using var h = await CoreHarness.Start();

        var draft = await Manual(h, [Hours], draft: true);
        Assert.IsNull(await h.Query(db => db.Documents.Where(d => d.Id == draft).Select(d => d.Number).SingleAsync()));

        await h.Use<InvoiceService, bool>(async s =>
        {
            await s.Issue(draft);
            await s.Issue(draft);
            return true;
        });
        Assert.AreEqual(1L, await h.Query(db => db.Documents.Where(d => d.Id == draft).Select(d => d.Number).SingleAsync()));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<InvoiceService, bool>(async s =>
        {
            await s.Discard(draft);
            return true;
        }));

        var other = await Manual(h, [Hardware], draft: true);
        await h.Use<InvoiceService, bool>(async s =>
        {
            await s.Discard(other);
            return true;
        });
        Assert.IsFalse(await h.Query(db => db.Documents.AnyAsync(d => d.Id == other)));
        Assert.IsFalse(await h.Query(db => db.DocumentLines.AnyAsync(l => l.DocumentId == other)));
    }

    [TestMethod]
    public async Task FacturaManual_Invalida_NoSeCrea()
    {
        await using var h = await CoreHarness.Start();
        var foreign = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Otra", "otra@example.com", Currency.Ars)));
        var foreignService = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(foreign, [new OrderItem(h.PlanId)]));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Manual(h, []));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Manual(h, [new LineInput("Descuento", 1m, -100m)]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Manual(h, [new LineInput("Algo", 0m, 100m)]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Manual(h, [new LineInput(" ", 1m, 100m)]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Manual(h, [Hours with { ServiceId = foreignService.ServiceIds[0] }]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Manual(h, [Hours], due: new DateOnly(2026, 11, 1)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Manual(h, [Hours], currency: "pesos"));

        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.AccountId == h.AccountId)));
    }

    [TestMethod]
    public async Task FacturaManual_ConDescuento_YEnOtraMoneda()
    {
        await using var h = await CoreHarness.Start();

        var id = await Manual(h, [new LineInput("Migración de sitio", 1m, 100m), new LineInput("Bonificación", 1m, -20m)], currency: "usd");

        // Los renglones se cargan en el modo de carga por defecto de la moneda: en dólares, sin IVA.
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == id));
        Assert.AreEqual(Currency.Usd, invoice.Currency);
        Assert.AreEqual(PriceMode.TaxExcluded, invoice.PriceMode);
        Assert.AreEqual(80m, invoice.NetTaxed);
        Assert.AreEqual(96.80m, invoice.Total);
    }

    [TestMethod]
    public async Task FacturaManual_ConsumeElSaldoAFavor()
    {
        await using var h = await CoreHarness.Start();
        await Adjust(h, 1000m);

        var id = await Manual(h, [Hours, Hardware]);

        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == id));
        Assert.AreEqual(1000m, invoice.Allocated);
        Assert.AreEqual(1500m, invoice.Pending);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
    }

    [TestMethod]
    public async Task AjusteDeSaldo_QuedaEnElLibroYEnLaAuditoria_YNoBajaDeCero()
    {
        await using var h = await CoreHarness.Start();
        Actor.Current = new Actor(ActorKind.Staff, "staff:7");

        await Adjust(h, 500m);
        await Adjust(h, -200m);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Adjust(h, -400m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Adjust(h, 0m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Adjust(h, 1.005m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<InvoiceService, long>(s => s.AdjustCredit(h.AccountId, "ARS", 10m, "  ")));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => h.Use<InvoiceService, long>(s => s.AdjustCredit(999999, "ARS", 10m, "Prueba")));

        Assert.AreEqual(300m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var entries = await h.Query(db => db.CreditEntries.OrderBy(e => e.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { 500m, -200m }, entries.Select(e => e.Amount).ToArray());
        Assert.IsTrue(entries.All(e => e.Actor == "staff:7" && e.Reason == "Bonificación por la caída del 20/10"));
        var audit = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action == "credit.adjust").OrderBy(a => a.Id).ToListAsync());
        Assert.HasCount(2, audit);
        Assert.IsTrue(audit.All(a => a.Actor == "staff:7" && a.Entity == $"account:{h.AccountId}"));
        Assert.IsFalse(audit.Any(a => a.Details!.Contains("caída", StringComparison.Ordinal)), "El motivo puede tener datos personales: no va a la auditoría.");
    }

    internal static Task<long> Item(CoreHarness h, decimal price, DateOnly? on = null, string? currency = null, string description = "Hora de soporte") =>
        h.Use<BillableItemService, long>(s => s.Add(h.AccountId, new BillableItemInput(description, 1m, price, Currency: currency, InvoiceOn: on)));

    [TestMethod]
    public async Task ItemFacturable_LaCorridaDeSuDiaLoFactura_YSeSumaALaRenovacion()
    {
        await using var h = await CoreHarness.Start();
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, Domain.Payments.PaymentStatus.Approved)));
        await h.RunJobs();
        var today = await Item(h, 1500m);
        var later = await Item(h, 800m, new DateOnly(2026, 11, 26), description: "Instalación de certificado");

        Assert.AreEqual(1, await h.Use<BillingRunService, int>(s => s.Run()));
        Assert.AreEqual(0, await h.Use<BillingRunService, int>(s => s.Run()));

        var first = await h.Query(db => db.BillableItems.SingleAsync(i => i.Id == today));
        Assert.AreEqual(BillableItemStatus.Invoiced, first.Status);
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == first.DocumentId));
        Assert.AreEqual(1500m, invoice.Total);
        Assert.AreEqual(LineKind.Item, invoice.Lines.Single().Kind);
        Assert.AreEqual($"item:{today}", invoice.Lines.Single().DedupKey);
        Assert.AreEqual(BillableItemStatus.Pending, (await h.Query(db => db.BillableItems.SingleAsync(i => i.Id == later))).Status);

        h.Clock.Now = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(1, await h.Use<BillingRunService, int>(s => s.Run()));

        var second = await h.Query(db => db.BillableItems.SingleAsync(i => i.Id == later));
        var renewal = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == second.DocumentId));
        CollectionAssert.AreEquivalent(new[] { LineKind.Renewal, LineKind.Item }, renewal.Lines.Select(l => l.Kind).ToArray());
        Assert.AreEqual(12900m, renewal.Total);
        Assert.AreEqual(new DateOnly(2026, 12, 2), renewal.DueDate);
        Assert.AreNotEqual(placed.InvoiceId, renewal.Id);
    }

    [TestMethod]
    public async Task ItemFacturable_FacturarAhora_UnaFacturaPorMoneda_YCancelarSoloPendientes()
    {
        await using var h = await CoreHarness.Start();
        var pesos = await Item(h, 1000m, new DateOnly(2026, 12, 1));
        var dollars = await Item(h, 15m, currency: "USD");
        var dropped = await Item(h, 300m);

        await h.Use<BillableItemService, bool>(async s =>
        {
            await s.Cancel(dropped);
            return true;
        });
        var created = await h.Use<BillableItemService, IReadOnlyList<long>>(s => s.InvoiceNow(h.AccountId));

        Assert.HasCount(2, created);
        var invoices = await h.Query(db => db.Documents.Where(d => created.Contains(d.Id)).OrderBy(d => d.Currency).ToListAsync());
        CollectionAssert.AreEqual(new[] { Currency.Ars, Currency.Usd }, invoices.Select(d => d.Currency).ToArray());
        Assert.IsTrue(invoices.All(d => d.IsIssued && d.Reason == DocumentReasons.Manual));
        Assert.AreEqual(1000m, invoices[0].Total);
        Assert.AreEqual(BillableItemStatus.Cancelled, (await h.Query(db => db.BillableItems.SingleAsync(i => i.Id == dropped))).Status);
        Assert.AreEqual(2, await h.Query(db => db.BillableItems.CountAsync(i => (i.Id == pesos || i.Id == dollars) && i.Status == BillableItemStatus.Invoiced)));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<BillableItemService, bool>(async s =>
        {
            await s.Cancel(pesos);
            return true;
        }));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<BillableItemService, IReadOnlyList<long>>(s => s.InvoiceNow(h.AccountId, [pesos])));
        Assert.IsEmpty(await h.Use<BillableItemService, IReadOnlyList<long>>(s => s.InvoiceNow(h.AccountId)));
        Assert.AreEqual(0, await h.Use<BillingRunService, int>(s => s.Run()));
    }

    [TestMethod]
    public async Task ItemFacturable_Invalido_NoSeCarga()
    {
        await using var h = await CoreHarness.Start();

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Item(h, -100m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Item(h, 100m, new DateOnly(2026, 11, 1)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<BillableItemService, long>(s => s.Add(h.AccountId, new BillableItemInput("Algo", 1m, 10m, ServiceId: 999999))));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => h.Use<BillableItemService, long>(s => s.Add(999999, new BillableItemInput("Algo", 1m, 10m))));

        Assert.AreEqual(0, await h.Query(db => db.BillableItems.CountAsync()));
    }

    private static void LateFees(BillingOptions o, int days = 5, int? cancel = null)
    {
        o.LateFeeAfterDays = days;
        o.LateFeePercent = 10m;
        o.LateFeeFixed["ARS"] = 100m;
        o.CancelUnpaidAfterDays = cancel;
    }

    private static Task<int> Dunning(CoreHarness h) => h.Use<DunningService, int>(s => s.Run());

    private static Task<List<Document>> Notes(CoreHarness h, DocumentKind kind) =>
        h.Query(db => db.Documents.Include(d => d.Lines).Where(d => d.Kind == kind).OrderBy(d => d.Id).ToListAsync());

    [TestMethod]
    public async Task RecargoPorMora_UnaNotaDeDebitoPorFactura_ConElImpuestoDeLoQueRecarga()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o));
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)])); // 12.100, vence el 09/11.

        h.Clock.Now = new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);
        await Dunning(h);
        Assert.IsEmpty(await Notes(h, DocumentKind.DebitNote));

        h.Clock.Now = h.Clock.Now.AddDays(1);
        await Dunning(h);
        await Dunning(h);

        var note = (await Notes(h, DocumentKind.DebitNote)).Single();
        Assert.AreEqual(placed.InvoiceId, note.RelatedDocumentId);
        Assert.AreEqual(DunningService.LateFeeReason, note.Reason);
        Assert.AreEqual(1L, note.Number);
        Assert.AreEqual(1310m, note.Total, "El 10 % de lo que se debe más el fijo de la moneda.");
        Assert.AreEqual(1082.64m, note.NetTaxed);
        Assert.AreEqual(227.36m, note.Tax);
        Assert.AreEqual(new DateOnly(2026, 11, 21), note.DueDate);
        Assert.AreEqual(LineKind.LateFee, note.Lines.Single().Kind);
        Assert.AreEqual($"late-fee:{placed.InvoiceId}:0", note.Lines.Single().DedupKey);
        var events = await Payloads(h, JobKinds.FiscalEmit);
        Assert.AreEqual(1, events.Count(p => p.Contains(Events.DebitNoteIssued, StringComparison.Ordinal)));
        Assert.IsTrue((await Payloads(h, JobKinds.NotifyEvent)).Any(p => p.Contains(Events.DebitNoteIssued, StringComparison.Ordinal)));
    }

    private static Task<long?> Charge(CoreHarness h, long invoiceId) => h.Use<DunningService, long?>(s => s.ChargeLateFee(invoiceId));

    /// <summary>Una factura impaga de 12.100 traída del sistema anterior, con el receptor vacío que deja el importador.</summary>
    internal static Task ImportedInvoice(CoreHarness h, long id, DateOnly due) => h.Db(async db =>
    {
        var amounts = TaxCalculator.Compute(PriceMode.TaxIncluded, [new TaxableLine("0", 12100m, TaxTreatment.Taxed, 0.21m)]);
        var doc = Document.FromImport(id, DocumentKind.Invoice, h.AccountId, Currency.Ars, id, due.AddDays(-19), due, amounts, 0m, 0m, "{}", h.Clock.Now, sourceStatus: "Unpaid");
        doc.ImportLine(LineKind.Renewal, "Hosting de octubre", 12100m, TaxTreatment.Taxed, 0.21m, null);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
    });

    [TestMethod]
    public async Task RecargoPorMora_SobreUnaFacturaImportada_LlevaElReceptorDeLaCuenta()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o));
        await ImportedInvoice(h, 900001, new DateOnly(2026, 10, 20));

        var noteId = await Charge(h, 900001);

        var note = (await Notes(h, DocumentKind.DebitNote)).Single();
        Assert.AreEqual(noteId, note.Id);
        Assert.AreEqual(1310m, note.Total);
        var receiver = JsonSerializer.Deserialize<ReceiverSnapshot>(note.Receiver!)!;
        Assert.AreEqual(("30111222", "Cliente de prueba", 5, "AR"), (receiver.DocumentNumber, receiver.LegalName, receiver.VatCondition, receiver.Country));

        // La nota de crédito que anula una importada también va con el receptor de la cuenta.
        var creditId = await h.Use<CreditService, long?>(s => s.CancelIfUnpaid(900001, "Anulada a pedido del cliente"));
        StringAssert.Contains((await h.Query(db => db.Documents.SingleAsync(d => d.Id == creditId))).Receiver, "30111222");
    }

    [TestMethod]
    public async Task FacturaAnuladaPorElStaff_SuRecargoImpagoSeAnulaConElla_YUnoCobradoQueda()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o));
        var first = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)])); // Vence el 09/11.
        var second = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        h.Clock.Now = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);
        var fee = (await Charge(h, first.InvoiceId!.Value))!.Value;
        var paidFee = (await Charge(h, second.InvoiceId!.Value))!.Value;
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 1310m, Domain.Payments.PaymentStatus.Approved, [paidFee])));

        await h.Use<CreditService, long?>(s => s.CancelIfUnpaid(first.InvoiceId!.Value, "Anulada a pedido del cliente"));
        await h.Use<CreditService, long?>(s => s.CancelIfUnpaid(second.InvoiceId!.Value, "Anulada a pedido del cliente"));

        var cancelled = await h.Query(db => db.Documents.SingleAsync(d => d.Id == fee));
        Assert.AreEqual((InvoiceStatus.Cancelled, 0m), (cancelled.Status, cancelled.Pending), "La deuda que recargaba ya no existe.");
        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == paidFee))).Status, "Un recargo cobrado lo resuelve el staff.");
        var credits = await Notes(h, DocumentKind.CreditNote);
        CollectionAssert.AreEquivalent(new long?[] { first.InvoiceId, fee, second.InvoiceId }, credits.Select(c => c.RelatedDocumentId).ToArray());
        var feeNote = credits.Single(c => c.RelatedDocumentId == fee);
        Assert.AreEqual("Anulada a pedido del cliente", feeNote.Reason);
        StringAssert.Contains(feeNote.Lines.Single().Description, "nota de débito");
        Assert.AreEqual(0m, await h.Query(db => db.Documents.Where(d => d.AccountId == h.AccountId && d.Kind != DocumentKind.CreditNote).SumAsync(d => d.Total - d.Credited - d.Allocated)));
    }

    [TestMethod]
    public async Task RecargoPorMora_ConPreciosSinIva_LaNotaCierraEnElPorcentaje()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o));
        var usd = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Cliente en dólares", "usd@example.com", Currency.Usd)));
        var invoice = await h.Use<InvoiceService, long>(s => s.CreateManual(new ManualInvoiceInput(usd, [new LineInput("Soporte", 1m, 45.91m)]))); // 45,91 más IVA: 55,55.
        h.Clock.Now = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);

        var noteId = (await Charge(h, invoice))!.Value;

        var note = await h.Query(db => db.Documents.SingleAsync(d => d.Id == noteId));
        // El 10 % de 55,55 son 5,56. Con el neto redondeado y el IVA calculado otra vez sobre él daba 5,57: el IVA es la
        // diferencia, como en un precio con IVA, dentro de la tolerancia del fisco.
        Assert.AreEqual(PriceMode.TaxExcluded, note.PriceMode);
        Assert.AreEqual((5.56m, 4.60m, 0.96m), (note.Total, note.NetTaxed, note.Tax));
    }

    [TestMethod]
    public async Task RecargoPorMora_ElFijoDeLaMonedaNoDistingueMayusculas()
    {
        // Billing__LateFeeFixed__ars=500 en una variable de entorno es el mismo fijo que "ARS": sin porcentaje, es todo el recargo.
        await using var h = await CoreHarness.Start(configure: o =>
        {
            o.LateFeeAfterDays = 5;
            o.LateFeeFixed["ars"] = 500m;
        });
        var invoice = await Manual(h, [Hours]);
        h.Clock.Now = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);

        var noteId = await Charge(h, invoice);

        Assert.AreEqual(500m, await h.Query(db => db.Documents.Where(d => d.Id == noteId).Select(d => d.Total).SingleAsync()));
    }

    [TestMethod]
    public async Task RecargoPorMora_ConPercepcion_LaNotaEsElPorcentajeDeTodoLoQueSeDebe()
    {
        await using var h = await CoreHarness.Start(configure: o =>
        {
            o.LateFeeAfterDays = 5;
            o.LateFeePercent = 10m;
        });
        await h.Use<TaxSetupService, bool>(async s =>
        {
            await s.ApplyPreset(new TaxPreset("AR", VatConditions.ResponsableInscripto, SellsAbroad: false, SellsToEuConsumers: false, SellsToUkConsumers: false, PerceivesRg2126: true, PaysGrossIncomeInCaba: false));
            return true;
        });
        var uncategorized = await h.Use<AccountService, long>(s => s.Create(new AccountInput("No categorizado", "nocat@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "30712345671", "Cliente SA", VatConditions.NoCategorizado, "AR", null))));
        var invoice = await h.Use<InvoiceService, long>(s => s.CreateManual(new ManualInvoiceInput(uncategorized, [new LineInput("Soporte", 1m, 12100m)])));
        Assert.AreEqual(13370.50m, await h.Query(db => db.Documents.Where(d => d.Id == invoice).Select(d => d.Total).SingleAsync()), "12.100 más la percepción de la RG 2126.");
        h.Clock.Now = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);

        var noteId = (await Charge(h, invoice))!.Value;

        // El porcentaje va sobre lo que se debe sin la percepción de la factura, y la nota suma la suya: así es el 10 % de
        // todo lo que se debe, y no un recargo sobre la percepción con otra percepción encima.
        var note = await h.Query(db => db.Documents.SingleAsync(d => d.Id == noteId));
        Assert.AreEqual((1337.05m, 127.05m), (note.Total, note.OtherTaxes));
    }

    [TestMethod]
    public async Task RecargoPorMora_NiCuentaExenta_NiFacturaPaga_NiDeudaVieja()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o));
        var exempt = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Exenta", "exenta@example.com", Currency.Ars)));
        await h.Db(async db =>
        {
            (await db.Accounts.SingleAsync(a => a.Id == exempt)).NoLateFees = true;
            await db.SaveChangesAsync();
        });
        var exemptOrder = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(exempt, [new OrderItem(h.PlanId)]));
        var paid = await Manual(h, [Hours]);
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-9", Currency.Ars, 2000m, Domain.Payments.PaymentStatus.Approved, [paid])));
        var partial = await Manual(h, [Hours]);
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-10", Currency.Ars, 500m, Domain.Payments.PaymentStatus.Approved, [partial])));

        h.Clock.Now = new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);
        await Dunning(h);

        var note = (await Notes(h, DocumentKind.DebitNote)).Single();
        Assert.AreEqual(partial, note.RelatedDocumentId, "Solo la que se debe en parte, por lo que falta.");
        Assert.AreEqual(250m, note.Total);
        Assert.IsFalse(await h.Query(db => db.Documents.AnyAsync(d => d.RelatedDocumentId == exemptOrder.InvoiceId)));

        // Prender el recargo no castiga deudas viejas: pasada la ventana de recuperación, no sale.
        await using var late = await CoreHarness.Start(configure: o => LateFees(o));
        await late.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(late.AccountId, [new OrderItem(late.PlanId)]));
        late.Clock.Now = new DateTimeOffset(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
        await Dunning(late);
        Assert.IsEmpty(await Notes(late, DocumentKind.DebitNote));
    }

    [TestMethod]
    public async Task RecargoAPedido_SoloConLaFacturaVencida_YUnoQueFallaNoFrenaLaAnulacion()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o, days: 2, cancel: 7));
        var first = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)])); // Vence el 09/11.
        Task<long?> Charge(long id) => h.Use<DunningService, long?>(s => s.ChargeLateFee(id));

        Assert.IsNull(await Charge(first.InvoiceId!.Value), "Antes del vencimiento no hay mora, aunque el staff lo pida.");
        h.Clock.Now = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)])); // Vence el 12/11.
        h.Clock.Now = new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);
        Assert.IsNull(await Charge(first.InvoiceId!.Value), "El día del vencimiento todavía se puede pagar.");

        // El 16/11 la primera se anula por impaga y la segunda recibe el recargo. Si el recargo falla (acá, sin numeración
        // de notas de débito), la corrida termina con error para reintentar, pero la anulación sale igual.
        await h.Db(db => db.Database.ExecuteSqlRawAsync("DELETE FROM billing.counters WHERE kind = 'DebitNote'"));
        h.Clock.Now = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => Dunning(h));

        Assert.AreEqual(first.InvoiceId, (await Notes(h, DocumentKind.CreditNote)).Single().RelatedDocumentId);
        Assert.IsEmpty(await Notes(h, DocumentKind.DebitNote));
    }

    [TestMethod]
    public async Task Mora_ConUnRecargoYUnaAnulacionQueFallan_LaCorridaInformaLasDos()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o, days: 2, cancel: 7));
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)])); // Vence el 09/11.
        h.Clock.Now = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)])); // Vence el 12/11.

        // El 16/11 la primera se anula y la segunda se recarga. Sin numeración de notas fallan las dos, y el error del
        // trabajo tiene que decir las dos: antes la falla de la anulación tapaba la del recargo.
        await h.Db(db => db.Database.ExecuteSqlRawAsync("DELETE FROM billing.counters WHERE kind IN ('DebitNote', 'CreditNote')"));
        h.Clock.Now = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

        var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => Dunning(h));

        Assert.HasCount(2, error.InnerExceptions);
    }

    [TestMethod]
    public async Task RecargoPorMora_SeAnulaConLaFacturaImpaga_YNoSaleSiLaAnulacionLlegaAntes()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o, days: 2, cancel: 7));
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        h.Clock.Now = new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
        await Dunning(h);
        var fee = (await Notes(h, DocumentKind.DebitNote)).Single();

        h.Clock.Now = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
        await Dunning(h);

        var credits = await Notes(h, DocumentKind.CreditNote);
        CollectionAssert.AreEquivalent(new long?[] { placed.InvoiceId, fee.Id }, credits.Select(c => c.RelatedDocumentId).ToArray());
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == fee.Id))).Status);

        await using var same = await CoreHarness.Start(configure: o => LateFees(o, days: 7, cancel: 7));
        await same.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(same.AccountId, [new OrderItem(same.PlanId)]));
        same.Clock.Now = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
        await Dunning(same);
        Assert.IsEmpty(await Notes(same, DocumentKind.DebitNote));
        Assert.HasCount(1, await Notes(same, DocumentKind.CreditNote));
    }

    private static Task Reminders(CoreHarness h) => h.Use<IEnumerable<IJobHandler>, JobResult>(handlers => handlers.Single(x => x.Kind == JobKinds.InvoiceReminders)
        .Handle(new ClaimedJob(0, JobKinds.InvoiceReminders, "{}", 1, 1), CancellationToken.None));

    private static Task<List<string>> Sent(CoreHarness h) =>
        h.Query(db => db.Set<Notifications.OutboundEmail>().Where(e => e.Template != Notifications.TemplateKeys.InvoiceIssued).OrderBy(e => e.Id).Select(e => e.DedupKey).ToListAsync());

    [TestMethod]
    public async Task Recordatorios_EnLosDiasDeLosAjustes_UnoPorDia_YNoALaCuentaExenta()
    {
        await using var h = await CoreHarness.Start(notifications: o =>
        {
            o.ReminderDaysBefore = "5, 1";
            o.OverdueDaysAfter = "2,6";
        });
        var invoice = await Manual(h, [Hours]); // Vence el 09/11.
        var exempt = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Exenta", "exenta@example.com", Currency.Ars)));
        await h.Use<InvoiceService, bool>(async s =>
        {
            await s.SetExemptions(exempt, noLateFees: false, noReminders: true);
            return true;
        });
        await h.Use<InvoiceService, long>(s => s.CreateManual(new ManualInvoiceInput(exempt, [Hours])));

        async Task On(int day)
        {
            h.Clock.Now = new DateTimeOffset(2026, 11, day, 15, 0, 0, TimeSpan.Zero);
            await Reminders(h);
            await Reminders(h);
        }

        // El primero no salió el 04/11 por una caída: sale el 05, y el de 1 día antes sale igual el 08.
        await On(5);
        await On(6);
        await On(8);
        await On(11);
        await On(16);

        var r = Notifications.TemplateKeys.InvoiceReminder;
        var o = Notifications.TemplateKeys.InvoiceOverdue;
        CollectionAssert.AreEqual(new[] { $"{r}:{invoice}:5", $"{r}:{invoice}:1", $"{o}:{invoice}:2", $"{o}:{invoice}:6" }, (await Sent(h)).ToArray());
        Assert.IsTrue((await h.Query(db => db.Accounts.SingleAsync(a => a.Id == exempt))).NoReminders);
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "account.billing-exemptions")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Notifications.NotificationOptions.Days("1, dos"));
        CollectionAssert.AreEqual(new[] { 0, 3, 7 }, Notifications.NotificationOptions.Days("7,3,0,3").ToArray());
        Assert.IsEmpty(Notifications.NotificationOptions.Days(" "));
    }

    [TestMethod]
    public async Task Recordatorios_TambienDeLaNotaDeDebitoDelRecargo_QueSeNombraComoTal()
    {
        await using var h = await CoreHarness.Start(configure: o => LateFees(o), notifications: o =>
        {
            o.ReminderDaysBefore = "1";
            o.OverdueDaysAfter = "2";
        });
        var invoice = await Manual(h, [Hours]); // Vence el 09/11.
        h.Clock.Now = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);
        var fee = (await Charge(h, invoice))!.Value; // Vence el 22/11.

        foreach (var day in new[] { 21, 24 })
        {
            h.Clock.Now = new DateTimeOffset(2026, 11, day, 15, 0, 0, TimeSpan.Zero);
            await Reminders(h);
        }

        var r = Notifications.TemplateKeys.InvoiceReminder;
        var o = Notifications.TemplateKeys.InvoiceOverdue;
        var emails = await h.Query(db => db.Set<Notifications.OutboundEmail>().Where(e => e.DedupKey == $"{r}:{fee}:1" || e.DedupKey == $"{o}:{fee}:2").OrderBy(e => e.Id).ToListAsync());
        Assert.HasCount(2, emails, "El recargo es plata que se cobra: se recuerda antes y se avisa vencido, como una factura.");
        Assert.IsTrue(emails.All(e => e.Subject.Contains("nota de débito", StringComparison.Ordinal) && !e.Html.Contains("servicios sigan activos", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AplicarSaldo_AVariasFacturas_DeLaQueVenceAntes()
    {
        await using var h = await CoreHarness.Start();
        var later = await Manual(h, [Hours], due: new DateOnly(2026, 11, 20));
        var sooner = await Manual(h, [Hardware], due: new DateOnly(2026, 11, 10));
        await Adjust(h, 1200m);

        var applied = await h.Use<InvoiceService, IReadOnlyList<AppliedCredit>>(s => s.ApplyCredit(h.AccountId, [later, sooner]));

        CollectionAssert.AreEqual(new[] { sooner, later }, applied.Select(a => a.DocumentId).ToArray());
        CollectionAssert.AreEqual(new[] { 500m, 700m }, applied.Select(a => a.Amount).ToArray());
        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == sooner))).Status);
        Assert.AreEqual(1300m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == later))).Pending);
        Assert.IsTrue(await h.Query(db => db.Jobs.AnyAsync(j => j.Kind == JobKinds.ActivateServicesOfInvoice)));
        Assert.AreEqual(2, await h.Query(db => db.Allocations.CountAsync(a => a.CreditEntryId != null)));

        var nothing = await h.Use<InvoiceService, IReadOnlyList<AppliedCredit>>(s => s.ApplyCredit(h.AccountId, [later]));
        Assert.IsEmpty(nothing);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<InvoiceService, IReadOnlyList<AppliedCredit>>(s => s.ApplyCredit(h.AccountId + 1, [later])));
    }
}
