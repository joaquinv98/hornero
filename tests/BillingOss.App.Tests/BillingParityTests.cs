using System.Text.Json.Nodes;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>Reglas de renovación y mora de un proveedor típico: anticipación por ciclo, sin acumular, anulación y baja.</summary>
[TestClass]
public sealed class BillingParityTests
{
    private static readonly Action<BillingOptions> Reglas = o =>
    {
        o.RenewalLeadDays = 7;
        o.RenewalLeadDaysByMonths = new Dictionary<int, int> { [3] = 14, [12] = 30 };
        o.GraceDays = 1;
        o.CancelUnpaidAfterDays = 7;
        o.TerminationDays = 14;
    };

    private static Task<PlacedOrder> Order(CoreHarness h, long planId, string? method = null) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(planId)], paymentMethod: method));

    private static Task<long> Record(CoreHarness h, decimal amount, string id) =>
        h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", id, Currency.Ars, amount, PaymentStatus.Approved)));

    private static async Task Pay(CoreHarness h, decimal amount, string id)
    {
        await Record(h, amount, id);
        await h.RunJobs();
    }

    private static Task<int> Renew(CoreHarness h) => h.Use<BillingRunService, int>(s => s.Run());

    private static async Task Dunning(CoreHarness h)
    {
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
    }

    /// <summary>Mediodía en Buenos Aires de ese día.</summary>
    private static void At(CoreHarness h, int year, int month, int day) => h.Clock.Now = new DateTimeOffset(year, month, day, 15, 0, 0, TimeSpan.Zero);

    private static Task<int> Renewals(CoreHarness h) => h.Query(db => db.Documents.CountAsync(d => d.Reason == "renewal" && d.IssueDate != null));

    /// <summary>Factura importada con el ID 1, impaga y vencida. Las secuencias siguen arriba, como las deja el importador.</summary>
    private static Task Imported(CoreHarness h, TaxBreakdown amounts, TaxTreatment lineTreatment, decimal credited = 0m) => h.Db(async db =>
    {
        var doc = Document.FromImport(1, DocumentKind.Invoice, h.AccountId, Currency.Ars, 1, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 25), amounts, 0m, credited, "{}", h.Clock.Now);
        doc.ImportLine(LineKind.Renewal, "Servicio", amounts.Total, lineTreatment, 0m, null);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT setval(pg_get_serial_sequence('billing.documents', 'id'), 1); UPDATE billing.counters SET next = 2 WHERE kind = 'Invoice'");
    });

    private static async Task<long> Plan(CoreHarness h, BillingCycle cycle, decimal price, decimal setupFee = 0m)
    {
        long id = 0;
        await h.Db(async db =>
        {
            var productId = await db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync();
            var plan = new Plan(productId, cycle);
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new PlanPrice(plan.Id, Currency.Ars, price, setupFee, new DateOnly(2026, 1, 1)));
            await db.SaveChangesAsync();
            id = plan.Id;
        });
        return id;
    }

    [TestMethod]
    public async Task Trimestral_SeFacturaConSuPropiaAnticipacion()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        var quarterly = await Plan(h, new BillingCycle(CycleUnit.Month, 3), 30000m);
        await Order(h, quarterly);
        await Pay(h, 30000m, "T-1");

        // El período vence el 2/2/2027: con 14 días de anticipación se factura el 19/1, no el 18.
        At(h, 2027, 1, 18);
        Assert.AreEqual(0, await Renew(h));
        At(h, 2027, 1, 19);
        Assert.AreEqual(1, await Renew(h));
    }

    [TestMethod]
    public async Task SinPagarLaRenovacion_NoSeFacturaElPeriodoSiguiente()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);

        At(h, 2026, 12, 27);
        await Renew(h);

        Assert.AreEqual(1, await Renewals(h));
    }

    [TestMethod]
    public async Task ConFacturacionContinua_SeFacturaAunqueHayaImpagas()
    {
        await using var h = await CoreHarness.Start(configure: o =>
        {
            Reglas(o);
            o.ContinuousInvoicing = true;
            o.CancelUnpaidAfterDays = null;
        });
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);

        At(h, 2026, 12, 27);
        await Renew(h);

        Assert.AreEqual(2, await Renewals(h));
    }

    [TestMethod]
    public async Task UnaFacturaPorVencimientoYPorMedioDePago()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId, "mercadopago_1");
        await Order(h, h.PlanId, "mercadopago_1");
        await Order(h, h.PlanId, "paypal_ppcpv");
        await Pay(h, 3 * 12100m, "T-1");
        At(h, 2026, 11, 5);
        await Order(h, h.PlanId, "mercadopago_1");
        await Pay(h, 12100m, "T-2");

        // Una sola corrida tardía: entran los cuatro, con dos vencimientos y dos medios de pago.
        At(h, 2026, 12, 1);
        Assert.AreEqual(3, await Renew(h));

        var invoices = await h.Query(db => db.Documents.Include(d => d.Lines).Where(d => d.Reason == "renewal").ToListAsync());
        Assert.AreEqual(1, invoices.Count(d => d.Lines.Count == 2));
        Assert.AreEqual(2, invoices.Count(d => d.DueDate == new DateOnly(2026, 12, 2)));
        Assert.AreEqual(1, invoices.Count(d => d.DueDate == new DateOnly(2026, 12, 5)));
    }

    [TestMethod]
    public async Task Mora_SuspendeAnulaYDaDeBajaEnSusPlazos()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        var renewal = await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal"));
        Assert.AreEqual(new DateOnly(2026, 12, 2), renewal.DueDate);

        At(h, 2026, 12, 2);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);

        At(h, 2026, 12, 3);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);

        At(h, 2026, 12, 8);
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Issued, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == renewal.Id))).Status);

        At(h, 2026, 12, 9);
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == renewal.Id))).Status);
        var note = await h.Query(db => db.Documents.SingleAsync(d => d.Kind == DocumentKind.CreditNote));
        Assert.AreEqual(renewal.Id, note.RelatedDocumentId);
        Assert.AreEqual(12100m, note.Total);

        // Anulada la factura, el servicio sigue sin pagar: no se le factura el período siguiente.
        At(h, 2026, 12, 27);
        await Renew(h);
        Assert.AreEqual(1, await Renewals(h));

        At(h, 2026, 12, 15);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);

        At(h, 2026, 12, 16);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task ProductoConPlazoPropio_SeDaDeBajaAntes()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await h.Db(async db =>
        {
            var productId = await db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync();
            (await db.Products.SingleAsync(p => p.Id == productId)).TerminationDays = 2;
            await db.SaveChangesAsync();
        });
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);

        At(h, 2026, 12, 4);
        await Dunning(h);

        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task ElPagoAvanzaLoPagadoYReactiva()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        Assert.AreEqual(new DateOnly(2026, 11, 2), (await h.Query(db => db.Services.SingleAsync())).PaidThrough);
        await Pay(h, 12100m, "T-1");
        Assert.AreEqual(new DateOnly(2026, 12, 2), (await h.Query(db => db.Services.SingleAsync())).PaidThrough);

        At(h, 2026, 11, 26);
        await Renew(h);
        At(h, 2026, 12, 3);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);

        await Pay(h, 12100m, "T-2");

        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual(new DateOnly(2027, 1, 2), service.PaidThrough);
    }

    [TestMethod]
    public async Task UnaFacturaConAlgoPagado_NoSeAnulaSola()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        await Pay(h, 100m, "T-2");

        At(h, 2026, 12, 10);
        await Dunning(h);

        Assert.AreEqual(InvoiceStatus.PartiallyPaid, (await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal"))).Status);
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote)));
    }

    [TestMethod]
    public async Task CorridaCortadaAntesDeEmitir_ElReintentoEmiteElBorrador()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);

        // La numeración falla después de renovar: el servicio ya avanzó y su borrador quedó confirmado sin emitir.
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE billing.counters SET kind = 'Rota' WHERE kind = 'Invoice'"));
        await Assert.ThrowsAsync<Exception>(() => Renew(h));
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE billing.counters SET kind = 'Invoice' WHERE kind = 'Rota'"));

        // El reintento llega más tarde: la corrida emite al empezar los borradores de más de una hora.
        h.Clock.Now = h.Clock.Now.AddHours(2);
        await Renew(h);

        Assert.AreEqual(1, await Renewals(h));
    }

    [TestMethod]
    public async Task UnBorradorQueNoSeEmite_NoFrenaALosDemas()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        await h.Db(async db =>
        {
            // Un borrador de renovación anterior que nunca se va a poder emitir: su total es cero.
            var stuck = new Document(DocumentKind.Invoice, h.AccountId, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 11, 20), h.Clock.Now, reason: "renewal");
            stuck.AddLine(LineKind.Manual, "Sin importe", 1, 0m, TaxTreatment.Taxed, 0.21m);
            db.Documents.Add(stuck);
            await db.SaveChangesAsync();
        });
        At(h, 2026, 11, 26);

        await Assert.ThrowsAsync<Exception>(() => Renew(h));

        Assert.AreEqual(1, await Renewals(h));
    }

    [TestMethod]
    public async Task AnulacionAutomatica_AcreditaLoQueFaltaDeCadaFactura()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);

        // Factura E importada: la cabecera es exenta y la línea vino gravada al 0 %.
        await Imported(h, new TaxBreakdown([], [], 3m, 0m), TaxTreatment.Taxed);

        // Factura propia con una nota parcial previa.
        var placed = await Order(h, h.PlanId);
        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Bonificación", 1, 5000m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));

        At(h, 2026, 11, 16);
        await Dunning(h);

        var notes = await h.Query(db => db.Documents.Where(d => d.Kind == DocumentKind.CreditNote && d.Reason != "Ajuste").ToListAsync());
        Assert.HasCount(2, notes);
        Assert.AreEqual(3m, notes.Single(n => n.RelatedDocumentId == 1).Exempt);
        Assert.AreEqual(7100m, notes.Single(n => n.RelatedDocumentId == placed.InvoiceId).Total);
        Assert.IsTrue(await h.Query(db => db.Documents.Where(d => d.Kind == DocumentKind.Invoice).AllAsync(d => d.Credited == d.Total)));
    }

    [TestMethod]
    public async Task AnulacionAutomatica_UnaFacturaQueFalla_NoFrenaALasDemas()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);

        // Importada con un crédito que no tiene nota: su anulación no cierra y falla.
        await Imported(h, new TaxBreakdown([], [], 3m, 0m), TaxTreatment.Exempt, credited: 1m);
        var placed = await Order(h, h.PlanId);
        At(h, 2026, 11, 16);

        await Assert.ThrowsAsync<Exception>(() => h.Use<DunningService, int>(s => s.Run()));

        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Status);
    }

    [TestMethod]
    public async Task AnulacionAutomatica_NoAnulaLaFacturaQueSePagoMientrasEsperaba()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        var id = (await Order(h, h.PlanId)).InvoiceId!.Value;
        At(h, 2026, 11, 16);

        // Otro proceso tiene la factura bloqueada: la mora la elige como impaga y espera el lock.
        await using var other = new NpgsqlConnection(await h.Query(db => Task.FromResult(db.Database.GetConnectionString()!)));
        await other.OpenAsync();
        await using var tx = await other.BeginTransactionAsync();
        await Exec(other, $"SELECT id FROM billing.documents WHERE id = {id} FOR UPDATE");
        var dunning = h.Use<DunningService, int>(s => s.Run());
        await WaitFor(h, "wait_event_type = 'Lock'");

        // Ese proceso era un pago por el total, y confirma antes de que la mora tome la factura.
        await Exec(other, $"UPDATE billing.documents SET allocated = total WHERE id = {id}");
        await tx.CommitAsync();
        await dunning;

        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote)));
        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == id))).Status);
    }

    [TestMethod]
    public async Task PagoAntesDeQueCorraLaBaja_LaDejaSinEfecto()
    {
        await using var h = await CoreHarness.Start(configure: o =>
        {
            Reglas(o);
            o.CancelUnpaidAfterDays = null;
        });
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        At(h, 2026, 12, 3);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);

        // La mora encola la baja y entra el pago antes de que corra. Con varios trabajadores la baja puede tomarse
        // antes que la activación de la factura pagada.
        At(h, 2026, 12, 16);
        await h.Use<DunningService, int>(s => s.Run());
        await Record(h, 12100m, "T-2");
        await h.RunJobs();

        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual(new DateOnly(2027, 1, 2), service.PaidThrough);
    }

    [TestMethod]
    public async Task PagoDespuesDeLaAnulacion_VuelveAFacturarElPeriodoYReactiva()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        At(h, 2026, 12, 3);
        await Dunning(h);
        At(h, 2026, 12, 9);
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal"))).Status);

        // El cliente paga la anulada: no hay factura abierta y el dinero entra como saldo.
        At(h, 2026, 12, 10);
        await Pay(h, 12100m, "T-2");

        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual(new DateOnly(2027, 1, 2), service.PaidThrough);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal" && d.Credited == 0))).Status);

        At(h, 2026, 12, 16);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task PedidoAnuladoPorLaMora_ElPagoPosteriorVuelveAFacturarloYAprovisiona()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        var withSetup = await Plan(h, BillingCycle.Monthly, 12100m, setupFee: 1000m);
        var placed = await Order(h, withSetup);
        Assert.AreEqual(13100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Total);

        // Nadie paga: a los 7 días del vencimiento la mora anula la factura del pedido y el servicio sigue pendiente.
        At(h, 2026, 11, 16);
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Status);
        Assert.AreEqual(ServiceStatus.Pending, (await h.Query(db => db.Services.SingleAsync())).Status);

        // El cliente paga al día siguiente: el saldo vuelve a facturar el período con su alta y el servicio se aprovisiona.
        At(h, 2026, 11, 17);
        await Pay(h, 13100m, "T-1");

        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual(new DateOnly(2026, 12, 2), service.PaidThrough);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var reissued = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Kind == DocumentKind.Invoice && d.Credited == 0));
        Assert.AreEqual(13100m, reissued.Total);
        Assert.AreEqual(InvoiceStatus.Paid, reissued.Status);
        Assert.AreEqual($"setup:{service.OrderLineId}:1", reissued.Lines.Single(l => l.Kind == LineKind.Setup).DedupKey);
    }

    [TestMethod]
    public async Task PedidoPorUnicaVezAnuladoPorLaMora_ElPagoPosteriorLoVuelveAFacturarYAprovisiona()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        var oneTime = await Plan(h, BillingCycle.OneTime, 5000m, setupFee: 1000m);
        var placed = await Order(h, oneTime);
        At(h, 2026, 11, 16);
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Status);
        Assert.AreEqual(ServiceStatus.Pending, (await h.Query(db => db.Services.SingleAsync())).Status);

        // Sin período que liberar, la línea del pedido vuelve igual con su alta, y el servicio se aprovisiona.
        At(h, 2026, 11, 17);
        await Pay(h, 6000m, "T-1");

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var reissued = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Kind == DocumentKind.Invoice && d.Credited == 0));
        Assert.AreEqual(6000m, reissued.Total);
        Assert.AreEqual(InvoiceStatus.Paid, reissued.Status);
        CollectionAssert.AreEquivalent(new[] { LineKind.OneTime, LineKind.Setup }, reissued.Lines.Select(l => l.Kind).ToList());

        // Ya pagada y aprovisionado: no vuelve a facturarse aunque lo pida el staff.
        Assert.AreEqual(0, await h.Use<BillingRunService, int>(s => s.ReissueReleased(h.AccountId, onlyIfCovered: false)));
    }

    [TestMethod]
    public async Task RenovacionDeDominioAnuladaPorLaMora_ElPagoPosteriorLaVuelveAFacturarYLaPaga()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox(), configure: Reglas);
        long domainId = 0;
        await h.Db(async db =>
        {
            var instance = await ManualModule.AddInstance(db, "nic.ar-manual", new JsonObject { ["queue"] = "dominios", ["registry"] = "nic.ar", ["grace_days"] = 45 }, h.Clock.Now);
            var domain = new RegisteredDomain(h.AccountId, "ejemplo.com.ar", instance, Currency.Ars, 5000m, h.Clock.Now);
            domain.Start(new DateOnly(2026, 12, 2));
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            domainId = domain.Id;
        });
        At(h, 2026, 11, 26);
        Assert.AreEqual(1, await Renew(h));
        At(h, 2026, 12, 9);
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal" && d.Kind == DocumentKind.Invoice))).Status);

        // El cliente paga la anulada: el saldo vuelve a facturar el período del dominio, lo paga y se pide al registrar.
        At(h, 2026, 12, 10);
        await Pay(h, 5000m, "T-1");

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual(DomainStatus.Active, domain.Status);
        Assert.AreEqual(new DateOnly(2027, 12, 2), domain.PaidThrough);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var reissued = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Reason == "renewal" && d.Kind == DocumentKind.Invoice && d.Credited == 0));
        Assert.AreEqual(InvoiceStatus.Paid, reissued.Status);
        Assert.AreEqual($"domain:{domainId}:2026-12-02:1", reissued.Lines.Single().DedupKey);
        Assert.AreEqual(1, await h.Query(db => db.DomainOperations.CountAsync(o => o.Operation == "renew")));
    }

    [TestMethod]
    public async Task BajaSinEfectoPorUnPagoEnElMedio_SeVuelveAPedirCuandoElSaldoSeConsume()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId), new OrderItem(h.PlanId)]));
        await Pay(h, 24200m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        At(h, 2026, 12, 3);
        await Dunning(h);
        At(h, 2026, 12, 9);
        await Dunning(h);

        // La mora encola las dos bajas y, antes de que corran, entra un pago que cubre un período: quedan sin efecto, y
        // la reemisión no factura porque el saldo no cubre la renovación entera.
        At(h, 2026, 12, 16);
        await h.Use<DunningService, int>(s => s.Run());
        await Pay(h, 12100m, "T-2");
        Assert.IsTrue((await h.Query(db => db.Services.Select(s => s.Status).ToListAsync())).All(s => s == ServiceStatus.Suspended));
        Assert.AreEqual(12100m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));

        // Un pedido nuevo consume el saldo. Al día siguiente la mora vuelve a pedir las bajas, y esta vez corren.
        At(h, 2026, 12, 20);
        await Order(h, h.PlanId);
        await h.RunJobs();
        At(h, 2026, 12, 21);
        await Dunning(h);

        var statuses = await h.Query(db => db.Services.OrderBy(s => s.Id).Take(2).Select(s => s.Status).ToListAsync());
        Assert.IsTrue(statuses.All(s => s == ServiceStatus.Terminated), string.Join(",", statuses));
    }

    [TestMethod]
    public async Task ConPreciosSinIva_LaGuardaDeLaBajaMideElPeriodoConImpuestos()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);

        // El precio del plan está cargado sin IVA: el servicio factura el neto más el impuesto.
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE catalog.plan_prices SET price_mode = 'TaxExcluded' WHERE plan_id = {h.PlanId}"));
        var placed = await Order(h, h.PlanId);
        Assert.AreEqual(14641m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Total);
        await Pay(h, 14641m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        At(h, 2026, 12, 3);
        await Dunning(h);
        At(h, 2026, 12, 9);
        await Dunning(h);

        // El cliente paga el neto: no alcanza para la factura con IVA, así que la reemisión no factura y la baja sigue.
        At(h, 2026, 12, 10);
        await Pay(h, 12100m, "T-2");
        Assert.AreEqual(1, await Renewals(h));
        At(h, 2026, 12, 16);
        await Dunning(h);

        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task ElPedidoPagadoQuedaAceptado_YCompletadoCuandoSeAprovisiona()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        var placed = await Order(h, h.PlanId);
        Assert.AreEqual(OrderStatus.Pending, (await h.Query(db => db.Orders.SingleAsync())).Status);

        await Pay(h, 12100m, "T-1");

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.AreEqual(OrderStatus.Completed, (await h.Query(db => db.Orders.SingleAsync(o => o.Id == placed.OrderId))).Status);
    }

    [TestMethod]
    public async Task ElPedidoDeUnServicioQueFallaAlAprovisionar_QuedaAceptadoYNoCompletado()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await h.Db(async db =>
        {
            // Un producto con módulo y sin instancia: la creación falla a la vista del staff.
            var productId = await db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync();
            (await db.Products.SingleAsync(p => p.Id == productId)).Kind = ProductKind.Provisioned;
            await db.SaveChangesAsync();
        });
        var placed = await Order(h, h.PlanId);

        await Pay(h, 12100m, "T-1");

        Assert.AreEqual(ServiceStatus.Failed, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.AreEqual(OrderStatus.Accepted, (await h.Query(db => db.Orders.SingleAsync(o => o.Id == placed.OrderId))).Status);
    }

    [TestMethod]
    public async Task SaldoQueNoCubreElPeriodo_NoVuelveAFacturar_SalvoQueLoPidaElStaff()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        At(h, 2026, 12, 9);
        await Dunning(h);

        At(h, 2026, 12, 10);
        await Pay(h, 100m, "T-2");
        Assert.AreEqual(1, await Renewals(h));

        Assert.AreEqual(1, await h.Use<BillingRunService, int>(s => s.ReissueReleased(h.AccountId, onlyIfCovered: false)));
        var reissued = await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal" && d.Credited == 0));
        Assert.AreEqual(12100m, reissued.Total);
        Assert.AreEqual(InvoiceStatus.PartiallyPaid, reissued.Status);
    }

    [TestMethod]
    public async Task ConSaldoQueCubreElPeriodo_NoSeDaDeBaja()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);

        // Un pago que no se imputa a nada queda como saldo, y la factura impaga se anula igual a los 7 días.
        At(h, 2026, 12, 5);
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 12100m, PaymentStatus.Approved, [])));
        await h.RunJobs();
        At(h, 2026, 12, 9);
        await Dunning(h);

        At(h, 2026, 12, 16);
        await Dunning(h);

        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task NotaParcialQueCompletaElPago_ReactivaYAvanzaLoPagado()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await Renew(h);
        var renewal = await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal"));
        await Pay(h, 4000m, "T-2");
        At(h, 2026, 12, 3);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);

        // El staff bonifica el resto: la factura queda paga.
        await h.Use<CreditService, long>(s => s.IssueCreditNote(renewal.Id, [new CreditLine("Bonificación", 1, 8100m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));
        await h.RunJobs();

        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == renewal.Id))).Status);
        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.AreEqual(new DateOnly(2027, 1, 2), service.PaidThrough);
        At(h, 2026, 12, 16);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task AnulacionAutomaticaRepetida_DevuelveLaMismaNota()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        var id = (await Order(h, h.PlanId)).InvoiceId!.Value;

        var first = await h.Use<CreditService, long?>(s => s.CancelIfUnpaid(id, DunningService.CancelReason));
        var again = await h.Use<CreditService, long?>(s => s.CancelIfUnpaid(id, DunningService.CancelReason));

        Assert.IsNotNull(first);
        Assert.AreEqual(first, again);
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote)));
    }

    [TestMethod]
    public async Task OtraCorridaRenovandoLaMismaCuenta_NoSeArmanDosBorradores()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);

        // Otra corrida tiene la cuenta tomada y acaba de armar el borrador del mismo vencimiento, sin confirmar todavía.
        var armed = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var other = h.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT id FROM accounts.accounts WHERE id = {0} FOR UPDATE", h.AccountId);
            var draft = new Document(DocumentKind.Invoice, h.AccountId, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 12, 2), h.Clock.Now, reason: "renewal");
            draft.AddLine(LineKind.Manual, "Otro servicio", 1, 100m, TaxTreatment.Taxed, 0.21m);
            db.Documents.Add(draft);
            await db.SaveChangesAsync();
            armed.SetResult();
            await release.Task;
            await tx.CommitAsync();
        });
        await armed.Task;
        var run = Renew(h);
        await WaitFor(h, "wait_event_type = 'Lock'");
        release.SetResult();
        await other;
        await run;

        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Reason == "renewal")));
    }

    [TestMethod]
    public async Task BorradorQueSeEmiteMientrasOtraCorridaLeAgregaUnaLinea_NoQuedaConLineasDeMas()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        At(h, 2026, 11, 26);
        await h.Db(async db =>
        {
            var draft = new Document(DocumentKind.Invoice, h.AccountId, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 12, 2), h.Clock.Now, reason: "renewal");
            draft.AddLine(LineKind.Manual, "Otro concepto", 1, 100m, TaxTreatment.Taxed, 0.21m);
            db.Documents.Add(draft);
            await db.SaveChangesAsync();

            // Solo en esta prueba: la emisión tarda, para que la otra corrida llegue mientras tanto.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION billing.slow_issue() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF OLD.issue_date IS NULL AND NEW.issue_date IS NOT NULL THEN PERFORM pg_sleep(3); END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER slow_issue BEFORE UPDATE ON billing.documents FOR EACH ROW EXECUTE FUNCTION billing.slow_issue();
                """);
        });

        // Una corrida emite el borrador viejo al empezar. La otra, para la que todavía es nuevo, renueva el servicio
        // de la misma cuenta y vencimiento mientras tanto.
        h.Clock.Now = h.Clock.Now.AddHours(2);
        var sweeping = Renew(h);
        await WaitFor(h, "wait_event = 'PgSleep'");
        h.Clock.Now = h.Clock.Now.AddHours(-2);
        var renewing = Renew(h);
        await Task.WhenAll(sweeping, renewing);

        var issued = await h.Query(db => db.Documents.Include(d => d.Lines).Where(d => d.Reason == "renewal").ToListAsync());
        Assert.IsTrue(issued.All(d => d.IsIssued && d.Total == d.Lines.Sum(l => l.Amount)), string.Join(" | ", issued.Select(d => $"{d.Total}/{d.Lines.Sum(l => l.Amount)}")));
        Assert.AreEqual(1, issued.SelectMany(d => d.Lines).Count(l => l.Kind == LineKind.Renewal));
    }

    [TestMethod]
    public async Task DescuentoTotal_ElPeriodoCorreSinFacturaYDespuesSeCobra()
    {
        await using var h = await CoreHarness.Start(configure: Reglas);
        await Order(h, h.PlanId);
        await Pay(h, 12100m, "T-1");
        await h.Db(async db =>
        {
            var s = await db.Services.SingleAsync();
            s.DiscountPercent = 100m;
            s.DiscountCyclesLeft = 1;
            await db.SaveChangesAsync();
        });

        At(h, 2026, 11, 26);
        await Renew(h);
        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(0, await Renewals(h));
        Assert.AreEqual(new DateOnly(2027, 1, 2), service.NextDueDate);
        Assert.AreEqual(new DateOnly(2027, 1, 2), service.PaidThrough);

        // El descuento se terminó para lo que se facture: el período que lo consumió lo guarda para el prorrateo.
        Assert.AreEqual((0, 12100m), (service.DiscountCyclesLeft, service.DiscountedUnitPrice));

        At(h, 2026, 12, 3);
        await Dunning(h);
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);

        At(h, 2026, 12, 26);
        await Renew(h);
        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal"))).Total);
    }

    /// <summary>Espera hasta que alguna conexión de la base de prueba cumpla la condición, o cinco segundos.</summary>
    private static async Task WaitFor(CoreHarness h, string condition)
    {
        var sql = $"SELECT count(*) AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND {condition}";
        for (var i = 0; i < 100 && await h.Query(db => db.Database.SqlQueryRaw<long>(sql).SingleAsync()) == 0; i++)
        {
            await Task.Delay(50);
        }
    }

    private static async Task<long> Exec(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }
}
