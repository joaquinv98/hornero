using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Cambios de plan y de opciones con prorrateo (diseno/catalogo.md). El VPS se contrata el 2/11 con un período de 30
/// días, se paga y queda activo. A mitad de período (17/11) quedan 15 días.
/// </summary>
[TestClass]
public sealed class ServiceChangeTests
{
    private static readonly OptionChoice Ram1 = new(CatalogSeed.Ram, CatalogSeed.Ram1);
    private static readonly OptionChoice Ram2 = new(CatalogSeed.Ram, CatalogSeed.Ram2);

    /// <summary>Contrata y paga el VPS con la RAM pedida, deja el servicio activo y lleva el reloj al 17/11.</summary>
    private static async Task<(long ServiceId, long InvoiceId)> ActiveVps(CoreHarness h, OptionChoice ram)
    {
        var productId = await h.Query(db => db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync());
        await h.Db(db => CatalogSeed.VpsOptions(db, productId));
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId, Options: [ram])]));
        await Pay(h, placed.InvoiceId!.Value, "T-alta");
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(15);
        return (placed.ServiceIds[0], placed.InvoiceId.Value);
    }

    private static async Task Pay(CoreHarness h, long invoiceId, string reference)
    {
        var pending = await h.Query(db => db.Documents.Where(d => d.Id == invoiceId).Select(d => d.Total - d.Credited - d.Allocated).SingleAsync());
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", reference, Currency.Ars, pending, PaymentStatus.Approved, [invoiceId])));
    }

    private static Task<ChangeResult> Change(CoreHarness h, long serviceId, ChangeRequest request, bool byCustomer = false) =>
        h.Use<ServiceChangeService, ChangeResult>(s => s.Request(serviceId, request, byCustomer));

    [TestMethod]
    public async Task Subir_CobraLaDiferenciaHastaLoFacturado_YSeAplicaRecienAlPagar()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);

        var quote = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(Options: [Ram2])));
        var result = await Change(h, serviceId, new ChangeRequest(Options: [Ram2]));

        // 5.000 por mes, 15 de 30 días. Los 2 GB tienen 1.000 de alta, que un cambio no cobra: solo la diferencia de precio.
        Assert.AreEqual(2500m, quote.Amount);
        Assert.AreEqual(17100m, quote.ToUnitPrice);
        Assert.AreEqual(ServiceChangeStatus.Pending, result.Status);
        var line = await h.Query(db => db.DocumentLines.SingleAsync(l => l.DocumentId == result.InvoiceId));
        Assert.AreEqual(LineKind.PlanChange, line.Kind);
        Assert.AreEqual(2500m, line.Amount);
        Assert.AreEqual($"change:{result.ChangeId}", line.DedupKey);
        StringAssert.Contains(line.Description, "17/11/2026 al 01/12/2026");
        Assert.AreEqual(12100m, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).UnitPrice);
        var twice = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Change(h, serviceId, new ChangeRequest(Options: [new(CatalogSeed.Backups, Quantity: 1)])));
        StringAssert.Contains(twice.Message, "ya tiene un cambio esperando el pago");

        await Pay(h, result.InvoiceId!.Value, "T-cambio");

        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(17100m, service.UnitPrice);
        Assert.AreEqual(2048, JsonNode.Parse(service.ModuleParams)!["memory_mb"]!.GetValue<long>());
        Assert.AreEqual(CatalogSeed.Ram2, await h.Query(db => db.ServiceOptions.Where(o => o.ServiceId == serviceId && o.OptionId == CatalogSeed.Ram).Select(o => o.ValueId).SingleAsync()));
        Assert.AreEqual(ServiceChangeStatus.Applied, (await h.Query(db => db.ServiceChanges.SingleAsync())).Status);
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ServiceOperation && j.DedupKey == $"service:{serviceId}:change_plan:{result.ChangeId}")));
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
    }

    [TestMethod]
    public async Task CambiarUnaOpcion_ConservaElPrecioViejoDelServicio_YSoloSumaLaDiferenciaDeLaOpcion()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);

        // Un precio de antes, o importado con su promoción adentro: menos que la lista de hoy.
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE services.services SET unit_price = 9000 WHERE id = {serviceId}"));

        var quote = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(Options: [new(CatalogSeed.Backups, Quantity: 1)])));

        // Los backups suman 1.500 por mes sobre los 9.000, no la lista entera: 15 de 30 días son 750.
        Assert.AreEqual((9000m, 10500m, 750m), (quote.FromUnitPrice, quote.ToUnitPrice, quote.Amount));

        // Otro producto sí toma el precio de lista.
        var (other, _) = await h.Query(db => CatalogSeed.Product(db, "VPS 4 GB", 20000m));
        var move = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(ProductId: other)));
        Assert.AreEqual(20000m, move.ToUnitPrice);
    }

    [TestMethod]
    public async Task Bajar_EnElUltimoPeriodoConDescuento_AcreditaLoCobradoConDescuento()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            db.Coupons.Add(new Coupon { Code = "MITAD", Kind = DiscountKind.Percent, Value = 50m });
            await db.SaveChangesAsync();
        });
        var productId = await h.Query(db => db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync());
        await h.Db(db => CatalogSeed.VpsOptions(db, productId));
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId, Options: [Ram2])], couponCode: "MITAD"));
        Assert.AreEqual(8550m + 1000m, await h.Query(db => db.Documents.Where(d => d.Id == placed.InvoiceId).Select(d => d.Total).SingleAsync()), "la mitad del período más el alta de los 2 GB");
        await Pay(h, placed.InvoiceId!.Value, "T-alta");
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(15);
        var serviceId = placed.ServiceIds[0];

        // La primera factura llevó el cupón y ya no le quedan ciclos, pero el período en curso se cobró a mitad de precio:
        // sacar los 2 GB devuelve la mitad de 5.000 por los 15 días que quedan, no la diferencia entera.
        var result = await Change(h, serviceId, new ChangeRequest(Options: [Ram1]));

        Assert.AreEqual(-1250m, result.Amount);
        Assert.AreEqual(1250m, await h.Query(db => db.Documents.Where(d => d.Id == result.CreditNoteIds[0]).Select(d => d.Total).SingleAsync()));
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual((12100m, 12100m), (service.UnitPrice, service.DiscountedUnitPrice));
    }

    [TestMethod]
    public async Task CambioDelCliente_NoEligeUnValorOculto_SalvoElQueYaTiene()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);
        long hidden = 0;
        await h.Db(async db =>
        {
            (await db.Products.SingleAsync(p => db.Plans.Any(x => x.ProductId == p.Id && x.Id == h.PlanId))).OptionChanges = true;
            var value = new OptionValue { OptionId = CatalogSeed.Ram, Name = "16 GB del staff", SortOrder = 9, Hidden = true };
            db.OptionValues.Add(value);
            await db.SaveChangesAsync();
            hidden = value.Id;
            db.OptionPrices.Add(new OptionPrice(hidden, Currency.Ars, BillingCycle.Monthly, 0m, 0m));
            await db.SaveChangesAsync();
        });
        var pick = new OptionChoice(CatalogSeed.Ram, hidden);

        var refused = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(Options: [pick]), byCustomer: true)));
        StringAssert.Contains(refused.Message, "no se ofrece");

        // El staff se lo pone, y el cliente lo conserva cuando cambia otra opción.
        await Change(h, serviceId, new ChangeRequest(Options: [pick]));
        var kept = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(Options: [pick, new(CatalogSeed.Backups, Quantity: 1)]), byCustomer: true));
        Assert.IsTrue(kept.Options.Contains(pick));
    }

    [TestMethod]
    public async Task Bajar_AcreditaConNotaDeCreditoContraLaFacturaDelPeriodo_YSeAplicaYa()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, invoiceId) = await ActiveVps(h, Ram2);

        var result = await Change(h, serviceId, new ChangeRequest(Options: [Ram1]));

        Assert.AreEqual(-2500m, result.Amount);
        Assert.AreEqual(ServiceChangeStatus.Applied, result.Status);
        var note = await h.Query(db => db.Documents.SingleAsync(d => d.Id == result.CreditNoteIds.Single()));
        Assert.AreEqual(DocumentKind.CreditNote, note.Kind);
        Assert.AreEqual(invoiceId, note.RelatedDocumentId);
        Assert.AreEqual(2500m, note.Total);
        Assert.AreEqual(ServiceChangeService.CreditReason, note.Reason);

        // La factura estaba paga: lo acreditado queda como saldo a favor, y el servicio sigue pago hasta el 2/12.
        Assert.AreEqual(2500m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual(12100m, service.UnitPrice);
        Assert.AreEqual(new DateOnly(2026, 12, 2), service.PaidThrough);
        Assert.AreEqual(1024, JsonNode.Parse(service.ModuleParams)!["memory_mb"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task CambioDeCiclo_SinProrrateo_ValeDesdeLaProximaRenovacion()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);
        var productId = await h.Query(db => db.Services.Where(s => s.Id == serviceId).Select(s => s.ProductId).SingleAsync());
        await h.Query(db => CatalogSeed.Plan(db, productId, BillingCycle.Annually, 121000m));

        var mixed = await Assert.ThrowsExactlyAsync<ValidationException>(() => Change(h, serviceId, new ChangeRequest(Cycle: "P1Y", Options: [Ram2])));
        StringAssert.Contains(mixed.Message, "El ciclo se cambia solo");
        var result = await Change(h, serviceId, new ChangeRequest(Cycle: "P1Y"));

        Assert.AreEqual(0m, result.Amount);
        Assert.AreEqual(ServiceChangeStatus.Applied, result.Status);
        h.Clock.Now = h.Clock.Now.AddDays(10);
        await h.Use<BillingRunService, int>(s => s.Run());
        var renewal = await h.Query(db => db.DocumentLines.SingleAsync(l => l.ServiceId == serviceId && l.Kind == LineKind.Renewal && l.Description.Contains("02/12/2026")));
        Assert.AreEqual(121000m, renewal.Amount);
        StringAssert.Contains(renewal.Description, "al 01/12/2027");
    }

    [TestMethod]
    public async Task ElCliente_SoloAProductosPermitidos_YOpcionesSiElProductoLoDeja()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);
        var (bigger, _) = await h.Query(db => CatalogSeed.Product(db, "VPS 8 GB", 30000m));

        var product = await Assert.ThrowsExactlyAsync<ValidationException>(() => Change(h, serviceId, new ChangeRequest(ProductId: bigger), byCustomer: true));
        StringAssert.Contains(product.Message, "no se puede pasar a VPS 8 GB");
        var options = await Assert.ThrowsExactlyAsync<ValidationException>(() => Change(h, serviceId, new ChangeRequest(Options: [Ram2]), byCustomer: true));
        StringAssert.Contains(options.Message, "las cambia el staff");

        await h.Db(async db =>
        {
            var vps = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            vps.UpgradeProductIds = [bigger];
            vps.OptionChanges = true;
            await db.SaveChangesAsync();
        });

        var quote = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(ProductId: bigger), byCustomer: true));
        Assert.AreEqual(Rounding.Commercial((30000m - 12100m) / 2), quote.Amount);
        Assert.AreEqual(0, quote.Options.Count);
        Assert.AreEqual(2500m, (await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(Options: [Ram2]), byCustomer: true))).Amount);
    }

    [TestMethod]
    public async Task CancelarUnCambioPendiente_AnulaSuFactura_YElServicioQuedaIgual()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);
        var result = await Change(h, serviceId, new ChangeRequest(Options: [Ram2]));

        await h.Use<ServiceChangeService, bool>(async s =>
        {
            await s.Cancel(result.ChangeId);
            return true;
        });

        Assert.AreEqual(ServiceChangeStatus.Cancelled, (await h.Query(db => db.ServiceChanges.SingleAsync())).Status);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == result.InvoiceId))).Status);
        Assert.AreEqual(12100m, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).UnitPrice);

        // Ya sin cambio pendiente se puede pedir otro.
        Assert.AreEqual(ServiceChangeStatus.Pending, (await Change(h, serviceId, new ChangeRequest(Options: [Ram2]))).Status);
    }

    [TestMethod]
    public async Task ConSaldoAFavor_LaFacturaDelCambioSePagaSola_YSeAplicaEnElMomento()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-saldo", Currency.Ars, 3000m, PaymentStatus.Approved)));

        var result = await Change(h, serviceId, new ChangeRequest(Options: [Ram2]));

        Assert.AreEqual(ServiceChangeStatus.Applied, (await h.Query(db => db.ServiceChanges.SingleAsync())).Status);
        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == result.InvoiceId))).Status);
        Assert.AreEqual(500m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(17100m, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).UnitPrice);
    }

    [TestMethod]
    public async Task PagadoDespuesDeOtraRenovacion_LaDiferenciaDeEsePeriodoSeFacturaAparte()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, _) = await ActiveVps(h, Ram1);
        var result = await Change(h, serviceId, new ChangeRequest(Options: [Ram2]));

        // Antes de pagar el cambio se factura el período siguiente con el precio viejo.
        h.Clock.Now = h.Clock.Now.AddDays(9);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(12100m, await h.Query(db => db.DocumentLines.Where(l => l.ServiceId == serviceId && l.Kind == LineKind.Renewal && l.Description.Contains("02/12/2026")).Select(l => l.Amount).SingleAsync()));

        await Pay(h, result.InvoiceId!.Value, "T-cambio");
        await h.RunJobs();
        await h.RunJobs();

        var gap = await h.Query(db => db.DocumentLines.SingleAsync(l => l.DedupKey == $"change:{result.ChangeId}:gap"));
        Assert.AreEqual(5000m, gap.Amount);
        Assert.AreEqual(LineKind.PlanChange, gap.Kind);
        StringAssert.Contains(gap.Description, "02/12/2026 al 01/01/2027");
    }

    /// <summary>Contrata el VPS con el cupón, lo paga, lo deja activo y devuelve el servicio y un VPS más grande (36.300).</summary>
    private static async Task<(long ServiceId, long Bigger)> VpsWithCoupon(CoreHarness h, CouponInput coupon, bool pay = true)
    {
        var (bigger, _) = await h.Query(db => CatalogSeed.Product(db, "VPS 8 GB", 36300m));
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, coupon));
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)], couponCode: coupon.Code));
        if (pay)
        {
            await Pay(h, placed.InvoiceId!.Value, "T-alta");
        }

        await h.RunJobs();
        return (placed.ServiceIds[0], bigger);
    }

    private static Task<long> Vps(CoreHarness h) => h.Query(db => db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync());

    private static Task<decimal> Renewal(CoreHarness h, long serviceId, string start) =>
        h.Query(db => db.DocumentLines.Where(l => l.ServiceId == serviceId && l.Kind == LineKind.Renewal && l.Description.Contains($"({start}")).Select(l => l.Amount).SingleAsync());

    [TestMethod]
    public async Task CuponQueNoValeParaElProductoNuevo_LaDiferenciaVaAPrecioLleno_YElDescuentoSeTermina()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, bigger) = await VpsWithCoupon(h, new CouponInput("MITAD", DiscountKind.Percent, 50m, Recurring: true, ProductIds: [await Vps(h)]));
        h.Clock.Now = h.Clock.Now.AddDays(15);

        var quote = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(ProductId: bigger)));

        // El cupón vale solo para el VPS chico: los 15 días del grande van a precio lleno (18.150) menos lo que se pagó con
        // descuento (3.025), y la cotización avisa que el descuento no sigue.
        Assert.AreEqual((15125m, true), (quote.Amount, quote.DiscountEnds));
        var result = await Change(h, serviceId, new ChangeRequest(ProductId: bigger));
        await Pay(h, result.InvoiceId!.Value, "T-cambio");
        await h.RunJobs();
        h.Clock.Now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(36300m, await Renewal(h, serviceId, "02/12/2026"), "las renovaciones del producto nuevo van sin el descuento");

        // Volver al producto del cupón no lo revive: el descuento ya se terminó y no hay nada que avisar.
        var back = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(ProductId: quote.FromProductId)));
        Assert.IsFalse(back.DiscountEnds);
        Assert.AreEqual(12100m, back.ToUnitPrice);
    }

    [TestMethod]
    public async Task CuponQueNoValeParaElCicloNuevo_ElCicloNuevoVaAPrecioLleno()
    {
        await using var h = await CoreHarness.Start();
        var vps = await Vps(h);
        await h.Query(db => CatalogSeed.Plan(db, vps, BillingCycle.Annually, 121000m));
        var (serviceId, _) = await VpsWithCoupon(h, new CouponInput("MENSUAL30", DiscountKind.Percent, 30m, Recurring: true, Cycles: ["P1M"]));
        h.Clock.Now = h.Clock.Now.AddDays(15);

        var result = await Change(h, serviceId, new ChangeRequest(Cycle: "P1Y"));

        Assert.AreEqual(ServiceChangeStatus.Applied, result.Status);
        h.Clock.Now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(121000m, await Renewal(h, serviceId, "02/12/2026"), "el cupón vale solo para el ciclo mensual");
    }

    [TestMethod]
    public async Task PrimerMesGratis_SubirAOtroProductoAlDiaSiguiente_CobraLaDiferenciaEntera()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, bigger) = await VpsWithCoupon(h, new CouponInput("PRIMERMES", DiscountKind.Percent, 100m, ProductIds: [await Vps(h)]), pay: false);
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
        h.Clock.Now = h.Clock.Now.AddDays(1);

        var quote = await h.Use<ServiceChangeService, ChangeQuote>(s => s.Preview(serviceId, new ChangeRequest(ProductId: bigger)));

        // El primer mes gratis es del VPS chico: los 29 días que quedan del grande se cobran enteros.
        Assert.AreEqual(Rounding.Commercial(36300m * 29 / 30), quote.Amount);
    }

    [TestMethod]
    public async Task CuponQueNoValeParaElProductoNuevo_ElPeriodoFacturadoMientrasEsperabaElPago_PagaLaDiferenciaConLoCobrado()
    {
        await using var h = await CoreHarness.Start();
        var (serviceId, bigger) = await VpsWithCoupon(h, new CouponInput("MITAD", DiscountKind.Percent, 50m, Recurring: true, ProductIds: [await Vps(h)]));
        h.Clock.Now = h.Clock.Now.AddDays(15);
        var result = await Change(h, serviceId, new ChangeRequest(ProductId: bigger));

        // Antes de pagar el cambio se factura el período siguiente con el precio viejo y su descuento: 6.050.
        h.Clock.Now = h.Clock.Now.AddDays(9);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(6050m, await Renewal(h, serviceId, "02/12/2026"));

        await Pay(h, result.InvoiceId!.Value, "T-cambio");
        await h.RunJobs();
        await h.RunJobs();

        // Ese período pasa a precio lleno del producto nuevo: 36.300 menos los 6.050 cobrados.
        Assert.AreEqual(30250m, await h.Query(db => db.DocumentLines.Where(l => l.DedupKey == $"change:{result.ChangeId}:gap").Select(l => l.Amount).SingleAsync()));
    }
}
