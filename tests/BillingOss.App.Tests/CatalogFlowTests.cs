using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Opciones del VPS del catálogo mínimo, con los IDs fijos que usa la API compatible: RAM en lista (1 GB sin cargo,
/// 2 GB a 5.000 con 1.000 de alta), IP extra por cantidad (0 a 4, 2.500 cada una y 500 de alta) y backups de sí o no
/// (1.500). Todo en pesos y mensual, y la RAM también anual.
/// </summary>
internal static class CatalogSeed
{
    public const long Ram = 5;
    public const long Ram1 = 15;
    public const long Ram2 = 16;
    public const long Ips = 6;
    public const long Ip = 21;
    public const long Backups = 7;
    public const long Daily = 31;

    public static async Task VpsOptions(BillingDbContext db, long productId)
    {
        var group = new OptionGroup { Name = "VPS" };
        group.SetImportedId(1);
        db.OptionGroups.Add(group);
        db.Options.AddRange(Option(Ram, "RAM", OptionKind.Select, "memory_mb", 0), Option(Ips, "IP extra", OptionKind.Quantity, "network.extra_ips", 1, max: 4), Option(Backups, "Backups", OptionKind.YesNo, "backups", 2));
        db.OptionValues.AddRange(Value(Ram1, Ram, "1 GB", "1024", 0), Value(Ram2, Ram, "2 GB", "2048", 1), Value(Ip, Ips, "IP"), Value(Daily, Backups, "Diario"));
        await db.SaveChangesAsync();
        db.ProductOptionGroups.Add(new ProductOptionGroup(productId, group.Id));
        db.OptionPrices.AddRange(
            new OptionPrice(Ram1, Currency.Ars, BillingCycle.Monthly, 0m, 0m),
            new OptionPrice(Ram2, Currency.Ars, BillingCycle.Monthly, 5000m, 1000m),
            new OptionPrice(Ram1, Currency.Ars, BillingCycle.Annually, 0m, 0m),
            new OptionPrice(Ram2, Currency.Ars, BillingCycle.Annually, 50000m, 0m),
            new OptionPrice(Ip, Currency.Ars, BillingCycle.Monthly, 2500m, 500m),
            new OptionPrice(Daily, Currency.Ars, BillingCycle.Monthly, 1500m, 0m));
        await db.SaveChangesAsync();
    }

    /// <summary>Un plan más para el producto, con su precio en pesos.</summary>
    public static async Task<long> Plan(BillingDbContext db, long productId, BillingCycle cycle, decimal amount, decimal setupFee = 0m)
    {
        var plan = new Plan(productId, cycle);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        db.PlanPrices.Add(new PlanPrice(plan.Id, Currency.Ars, amount, setupFee, new DateOnly(2026, 1, 1)));
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>Otro producto con un plan mensual en pesos.</summary>
    public static async Task<(long ProductId, long PlanId)> Product(BillingDbContext db, string name, decimal monthly, Action<Product>? configure = null)
    {
        var product = new Product { Name = name, Kind = ProductKind.Manual };
        configure?.Invoke(product);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return (product.Id, await Plan(db, product.Id, BillingCycle.Monthly, monthly));
    }

    private static Option Option(long id, string name, OptionKind kind, string key, int order, int max = 0)
    {
        var option = new Option { GroupId = 1, Name = name, Kind = kind, ModuleKey = key, SortOrder = order, MaxQuantity = max };
        option.SetImportedId(id);
        return option;
    }

    private static OptionValue Value(long id, long optionId, string name, string? moduleValue = null, int order = 0)
    {
        var value = new OptionValue { OptionId = optionId, Name = name, ModuleValue = moduleValue, SortOrder = order };
        value.SetImportedId(id);
        return value;
    }
}

[TestClass]
public sealed class CatalogFlowTests
{
    private static async Task<long> ProductOf(CoreHarness h) => await h.Query(db => db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync());

    private static Task<PlacedOrder> Order(CoreHarness h, IReadOnlyList<OptionChoice>? options = null, string? coupon = null, long? account = null, long? planId = null) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account ?? h.AccountId, [new OrderItem(planId ?? h.PlanId, Options: options)], couponCode: coupon));

    [TestMethod]
    public async Task Pedido_ConOpciones_CobraPlanMasOpciones_YLasPasaAlServicioYAlModulo()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db => await CatalogSeed.VpsOptions(db, await ProductOf(h)));

        var placed = await Order(h, [new(CatalogSeed.Ram, CatalogSeed.Ram2), new(CatalogSeed.Ips, Quantity: 2), new(CatalogSeed.Backups, Quantity: 1)]);

        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == placed.InvoiceId));
        var renewal = invoice.Lines.Single(l => l.Kind == LineKind.Renewal);
        Assert.AreEqual(12100m + 5000m + 5000m + 1500m, renewal.Amount);
        StringAssert.Contains(renewal.Description, "VPS 2 GB · RAM: 2 GB, IP extra: 2, Backups");
        Assert.AreEqual(1000m + 500m, invoice.Lines.Single(l => l.Kind == LineKind.Setup).Amount);
        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(23600m, service.UnitPrice);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"memory_mb":2048,"network":{"extra_ips":2},"backups":true}"""), JsonNode.Parse(service.ModuleParams)), service.ModuleParams);
        var chosen = await h.Query(db => db.ServiceOptions.Where(o => o.ServiceId == service.Id).OrderBy(o => o.OptionId).Select(o => new { o.OptionId, o.ValueId, o.Quantity }).ToListAsync());
        Assert.AreEqual(3, chosen.Count);
        Assert.AreEqual(CatalogSeed.Ram2, chosen[0].ValueId);
        Assert.AreEqual(2, chosen[1].Quantity);
        Assert.AreEqual(1, chosen[2].Quantity);
        var line = await h.Query(db => db.Orders.Include(o => o.Lines).SelectMany(o => o.Lines).SingleAsync());
        Assert.AreEqual(line.Id, service.OrderLineId);

        // La renovación repite las opciones en la línea.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, invoice.Total, PaymentStatus.Approved)));
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        var next = await h.Query(db => db.DocumentLines.SingleAsync(l => l.Kind == LineKind.Renewal && l.DocumentId != invoice.Id));
        Assert.AreEqual(23600m, next.Amount);
        StringAssert.Contains(next.Description, "RAM: 2 GB");
    }

    [TestMethod]
    public async Task Cotizacion_DaLaMismaFacturaQueElPedido_SinTocarStockNiCuponNiServicios()
    {
        await using var h = await CoreHarness.Start();
        var productId = await ProductOf(h);
        await h.Db(async db =>
        {
            await CatalogSeed.VpsOptions(db, productId);
            var product = await db.Products.SingleAsync(p => p.Id == productId);
            product.Stock = 2;
            await db.SaveChangesAsync();
        });
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("UNO", DiscountKind.Percent, 10m, MaxUses: 1)));
        var (addonProduct, addon) = await h.Query(db => CatalogSeed.Product(db, "Backup", 2000m, p => { p.Addon = true; p.ParentProductIds = [productId]; }));
        IReadOnlyList<OrderItem> items = [new OrderItem(h.PlanId, Options: [new(CatalogSeed.Ram, CatalogSeed.Ram2), new(CatalogSeed.Ips, Quantity: 2)]), new OrderItem(addon, ParentItem: 0)];

        var quote = await h.Use<OrderService, OrderQuote>(s => s.Quote(h.AccountId, items, "uno"));

        // VPS con 2 GB y dos IP (22.100) y el backup (2.000), con el 10 % del cupón en las dos, y las altas de las opciones:
        // 1.000 de la RAM y 500 de las IP, que se cobra una vez por línea aunque sean dos.
        Assert.AreEqual(2, quote.Lines.Count);
        Assert.AreEqual(("ARS", 22100m, 19890m, 1500m), (quote.Currency, quote.Lines[0].UnitPrice, quote.Lines[0].DiscountedUnitPrice, quote.Lines[0].SetupFee));
        StringAssert.Contains(quote.Lines[0].Description, "RAM: 2 GB, IP extra: 2");
        Assert.AreEqual((addonProduct, 1800m, 0), (quote.Lines[1].ProductId, quote.Lines[1].DiscountedUnitPrice, quote.Lines[1].ParentItem!.Value));
        Assert.AreEqual(19890m + 1800m + 1500m, quote.Total);
        Assert.AreEqual(Rounding.Commercial(quote.Total - (quote.Total / 1.21m)), quote.Tax);
        Assert.AreEqual(quote.Total, quote.Net + quote.Tax);

        // Nada cambió: ni pedidos, ni servicios, ni documentos, ni stock, ni usos.
        Assert.AreEqual(0, await h.Query(db => db.Orders.CountAsync()) + await h.Query(db => db.Services.CountAsync()) + await h.Query(db => db.Documents.CountAsync()));
        Assert.AreEqual((2, 0), (await h.Query(db => db.Products.Where(p => p.Id == productId).Select(p => p.Stock!.Value).SingleAsync()), await h.Query(db => db.Coupons.Select(c => c.Uses).SingleAsync())));

        // El pedido sale igual que la cotización.
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, items, couponCode: "UNO"));
        Assert.AreEqual(quote.Total, await h.Query(db => db.Documents.Where(d => d.Id == placed.InvoiceId).Select(d => d.Total).SingleAsync()));

        // Rechaza lo mismo que el pedido: el stock que no alcanza para todo el pedido y el cupón agotado.
        var stock = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, OrderQuote>(s => s.Quote(h.AccountId, [new OrderItem(h.PlanId), new OrderItem(h.PlanId)])));
        StringAssert.Contains(stock.Message, "No queda stock");
        var used = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, OrderQuote>(s => s.Quote(h.AccountId, [new OrderItem(h.PlanId)], "UNO")));
        StringAssert.Contains(used.Message, "todas las veces");
    }

    [TestMethod]
    public async Task Cotizacion_ConPruebaGratis_NoCobraHoy_YDiceCuandoTermina()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            product.TrialDays = 7;
            await db.SaveChangesAsync();
        });

        var quote = await h.Use<OrderService, OrderQuote>(s => s.Quote(h.AccountId, [new OrderItem(h.PlanId)]));

        Assert.AreEqual((0m, 0m), (quote.Total, quote.Tax));
        Assert.AreEqual((new DateOnly(2026, 11, 9), 12100m), (quote.Lines[0].TrialEndsOn!.Value, quote.Lines[0].UnitPrice));
    }

    [TestMethod]
    public async Task Pedido_SinElegir_TomaLosDefectos_YLoAjenoOFueraDeRangoSeRechaza()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db => await CatalogSeed.VpsOptions(db, await ProductOf(h)));

        var placed = await Order(h);
        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Total);
        Assert.AreEqual(3, await h.Query(db => db.ServiceOptions.CountAsync()));

        var foreign = await Assert.ThrowsExactlyAsync<ValidationException>(() => Order(h, [new(99, 1)]));
        StringAssert.Contains(foreign.Message, "no es de este producto");
        var tooMany = await Assert.ThrowsExactlyAsync<ValidationException>(() => Order(h, [new(CatalogSeed.Ips, Quantity: 5)]));
        StringAssert.Contains(tooMany.Message, "va de 0 a 4");
        Assert.AreEqual(1, await h.Query(db => db.Orders.CountAsync()));
    }

    [TestMethod]
    public async Task Alta_AlPedirNoEsperaElPago_YLaQueAceptaElStaffNoLaActivaNiElPago()
    {
        await using var h = await CoreHarness.Start();
        var (_, onOrder) = await h.Query(db => CatalogSeed.Product(db, "Licencia", 1000m, p => p.Activation = ProductActivation.OnOrder));
        var (_, manual) = await h.Query(db => CatalogSeed.Product(db, "Bare metal", 5000m, p => p.Activation = ProductActivation.Manual));
        Task<ServiceStatus> Status(long id) => h.Query(db => db.Services.Where(s => s.Id == id).Select(s => s.Status).SingleAsync());

        // Al pedir: se aprovisiona enseguida y la factura queda igual para cobrar.
        var first = await Order(h, planId: onOrder);
        Assert.IsNotNull(first.InvoiceId);
        Assert.AreEqual(ServiceStatus.Provisioning, await Status(first.ServiceIds[0]));

        // La que acepta el staff: pagar la factura no la aprovisiona y el pedido sigue pendiente.
        var second = await Order(h, planId: manual);
        var total = await h.Query(db => db.Documents.Where(d => d.AccountId == h.AccountId).SumAsync(d => d.Total));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-9", Currency.Ars, total, PaymentStatus.Approved)));
        await h.RunJobs();
        Assert.IsTrue(await h.Query(db => db.Documents.Where(d => d.AccountId == h.AccountId).AllAsync(d => d.Allocated == d.Total)), "las dos facturas quedan pagas");
        Assert.AreEqual(ServiceStatus.Pending, await Status(second.ServiceIds[0]));
        Assert.AreEqual(OrderStatus.Pending, await h.Query(db => db.Orders.Where(o => o.Id == second.OrderId).Select(o => o.Status).SingleAsync()));

        // Cuando el staff acepta el pedido se aprovisiona.
        await h.Use<OrderService, bool>(async s =>
        {
            await s.Accept(second.OrderId, provision: true);
            return true;
        });
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, await Status(second.ServiceIds[0]));
        Assert.AreEqual(ServiceStatus.Active, await Status(first.ServiceIds[0]));
    }

    [TestMethod]
    public async Task Alta_QueAceptaElStaff_SinNadaParaPagar_DejaElPedidoPendiente_YElRestoSeActiva()
    {
        await using var h = await CoreHarness.Start();
        var (_, manual) = await h.Query(db => CatalogSeed.Product(db, "Hosting gratis", 0m, p => p.Activation = ProductActivation.Manual));

        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(manual), new OrderItem(h.FreePlanId)]));
        await h.RunJobs();

        Assert.IsNull(placed.InvoiceId);
        Assert.AreEqual(OrderStatus.Pending, await h.Query(db => db.Orders.Where(o => o.Id == placed.OrderId).Select(o => o.Status).SingleAsync()));
        var statuses = await h.Query(db => db.Services.Where(s => placed.ServiceIds.Contains(s.Id)).OrderBy(s => s.Id).Select(s => s.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { ServiceStatus.Pending, ServiceStatus.Active }, statuses);
    }

    [TestMethod]
    public async Task Pedido_DelCliente_NoEligeLoOculto_YElStaffSi()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            await CatalogSeed.VpsOptions(db, await ProductOf(h));
            db.OptionValues.Add(new OptionValue { OptionId = CatalogSeed.Ram, Name = "16 GB del staff", SortOrder = 9, Hidden = true });
            await db.SaveChangesAsync();
        });
        var hidden = await h.Query(db => db.OptionValues.Where(v => v.Hidden).Select(v => v.Id).SingleAsync());
        IReadOnlyList<OrderItem> items = [new OrderItem(h.PlanId, Options: [new(CatalogSeed.Ram, hidden)])];

        // El portal no muestra el valor: pedirlo a mano no pasa, ni al calcular ni al confirmar.
        var quoted = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, OrderQuote>(s => s.Quote(h.AccountId, items, byCustomer: true)));
        StringAssert.Contains(quoted.Message, "16 GB del staff no se ofrece");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, items, byCustomer: true)));
        Assert.AreEqual(0, await h.Query(db => db.Orders.CountAsync()));

        // Una opción oculta tampoco, aunque tenga un solo valor.
        await h.Db(async db =>
        {
            (await db.Options.SingleAsync(o => o.Id == CatalogSeed.Backups)).Hidden = true;
            await db.SaveChangesAsync();
        });
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, OrderQuote>(s => s.Quote(h.AccountId, [new OrderItem(h.PlanId, Options: [new(CatalogSeed.Backups, Quantity: 1)])], byCustomer: true)));

        // El staff y la API sí lo eligen.
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, items));
        Assert.AreEqual(hidden, await h.Query(db => db.ServiceOptions.Where(o => o.ServiceId == placed.ServiceIds[0] && o.OptionId == CatalogSeed.Ram).Select(o => o.ValueId).SingleAsync()));
    }

    [TestMethod]
    public async Task Cupon_NoBajaElTopeDeUsosDebajoDeLoUsado_NiGuardaUnTipoQueNoExiste()
    {
        await using var h = await CoreHarness.Start();
        var id = await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("TOPE", DiscountKind.Percent, 10m, MaxUses: 5)));
        await h.Db(async db =>
        {
            (await db.Coupons.SingleAsync(c => c.Id == id)).Uses = 3;
            await db.SaveChangesAsync();
        });

        var lower = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<CatalogService, long>(s => s.SaveCoupon(id, new CouponInput("TOPE", DiscountKind.Percent, 10m, MaxUses: 2))));
        StringAssert.Contains(lower.Message, "ya se usó 3 veces");
        Assert.AreEqual(id, await h.Use<CatalogService, long>(s => s.SaveCoupon(id, new CouponInput("TOPE", DiscountKind.Percent, 10m, MaxUses: 3))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("RARO", (DiscountKind)7, 10m))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<CatalogService, long>(s => s.SaveProduct(null, new ProductInput("Raro", Kind: (ProductKind)9))));
    }

    [TestMethod]
    public async Task Duplicar_ProductoOcultoConSusPreciosYOpciones_YGrupoSinProductos()
    {
        await using var h = await CoreHarness.Start();
        var productId = await ProductOf(h);
        await h.Db(async db =>
        {
            await CatalogSeed.VpsOptions(db, productId);
            (await db.Products.SingleAsync(p => p.Id == productId)).Activation = ProductActivation.OnOrder;
            await db.SaveChangesAsync();

            // Las opciones del ejemplo van con los IDs del origen, como las importadas: la importación sube las secuencias.
            await db.Database.ExecuteSqlRawAsync("""
                SELECT setval(pg_get_serial_sequence('catalog.option_groups', 'id'), (SELECT max(id) FROM catalog.option_groups));
                SELECT setval(pg_get_serial_sequence('catalog.options', 'id'), (SELECT max(id) FROM catalog.options));
                SELECT setval(pg_get_serial_sequence('catalog.option_values', 'id'), (SELECT max(id) FROM catalog.option_values));
                """);
        });

        var copy = await h.Use<CatalogService, long>(s => s.CopyProduct(productId));
        var products = await h.Query(db => CatalogQueries.Products(db, new DateOnly(2026, 11, 2), [productId, copy], CancellationToken.None));
        var (original, copied) = (products.Single(p => p.Id == productId), products.Single(p => p.Id == copy));
        Assert.AreEqual(("VPS 2 GB (copia)", false, ProductActivation.OnOrder), (copied.Name, copied.Visible, copied.Activation));
        CollectionAssert.AreEqual(original.OptionGroupIds.ToList(), copied.OptionGroupIds.ToList());
        CollectionAssert.AreEqual(original.Prices.Select(p => (p.Cycle, p.Currency, p.Amount, p.SetupFee)).ToList(), copied.Prices.Select(p => (p.Cycle, p.Currency, p.Amount, p.SetupFee)).ToList());
        Assert.AreNotEqual(original.Prices[0].PlanId, copied.Prices[0].PlanId, "planes propios");

        var group = await h.Use<CatalogService, long>(s => s.CopyOptionGroup(original.OptionGroupIds[0], "VPS nuevo"));
        var groups = await h.Query(db => CatalogQueries.OptionGroups(db, [original.OptionGroupIds[0], group], CancellationToken.None));
        var (source, twin) = (groups.Single(g => g.Id != group), groups.Single(g => g.Id == group));
        Assert.AreEqual("VPS nuevo", twin.Name);
        Assert.AreEqual(0, twin.ProductIds.Count);
        CollectionAssert.AreEqual(
            source.Options.SelectMany(o => o.Values.SelectMany(v => v.Prices.Select(p => $"{o.Name}/{v.Name}/{p.Cycle}/{p.Amount}"))).ToList(),
            twin.Options.SelectMany(o => o.Values.SelectMany(v => v.Prices.Select(p => $"{o.Name}/{v.Name}/{p.Cycle}/{p.Amount}"))).ToList());
        Assert.IsTrue(twin.Options.All(o => source.Options.All(x => x.Id != o.Id)), "opciones propias");

        // Un nombre de más de 200 caracteres no llega a la base: lo dice.
        var tooLong = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<CatalogService, long>(s => s.CopyProduct(productId, new string('x', 201))));
        StringAssert.Contains(tooLong.Message, "hasta 200 caracteres");
    }

    [TestMethod]
    public async Task PrecioNuevo_TomaElModoDeSuMoneda_LaVigenciaNuevaLoConserva_YElPedidoFacturaPorModo()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var settings = await db.TaxSettings.SingleOrDefaultAsync(s => s.Id == 1) ?? db.TaxSettings.Add(new TaxSettings()).Entity;
            settings.SetDefaultPriceMode(Currency.Ars, PriceMode.TaxExcluded);
            await db.SaveChangesAsync();
        });

        var productId = await h.Use<CatalogService, long>(s => s.SaveProduct(null, new ProductInput("Soporte", Prices: [new PriceInput("P1M", "ARS", 1000m)])));
        var planId = await h.Query(db => db.Plans.Where(p => p.ProductId == productId).Select(p => p.Id).SingleAsync());
        Assert.AreEqual(PriceMode.TaxExcluded, await h.Query(db => db.PlanPrices.Where(p => p.PlanId == planId).Select(p => p.PriceMode).SingleAsync()));

        // Un pedido con un precio sin impuesto y otro con impuesto incluido sale en dos facturas, y la cotización las suma.
        IReadOnlyList<OrderItem> items = [new OrderItem(planId), new OrderItem(h.PlanId)];
        var quote = await h.Use<OrderService, OrderQuote>(s => s.Quote(h.AccountId, items));
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, items));
        var invoices = await h.Query(db => db.Documents.Where(d => placed.InvoiceIds!.Contains(d.Id)).Select(d => new { d.PriceMode, d.Total }).ToListAsync());
        Assert.AreEqual(2, invoices.Count);
        Assert.AreEqual(1210m, invoices.Single(i => i.PriceMode == PriceMode.TaxExcluded).Total);
        Assert.AreEqual(12100m, invoices.Single(i => i.PriceMode == PriceMode.TaxIncluded).Total);
        Assert.AreEqual(1210m + 12100m, quote.Total);
        Assert.AreEqual(PriceMode.TaxExcluded, await h.Query(db => db.Services.Where(s => s.ProductId == productId).Select(s => s.PriceMode).SingleAsync()));

        // Otro importe abre una vigencia nueva con el mismo modo, aunque la moneda pase a otro.
        await h.Db(async db =>
        {
            (await db.TaxSettings.SingleAsync(s => s.Id == 1)).SetDefaultPriceMode(Currency.Ars, PriceMode.TaxIncluded);
            await db.SaveChangesAsync();
        });
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Use<CatalogService, long>(s => s.SaveProduct(productId, new ProductInput("Soporte", Prices: [new PriceInput("P1M", "ARS", 1500m)])));
        CollectionAssert.AreEqual(new[] { PriceMode.TaxExcluded, PriceMode.TaxExcluded }, await h.Query(db => db.PlanPrices.Where(p => p.PlanId == planId).OrderBy(p => p.ValidFrom).Select(p => p.PriceMode).ToListAsync()));
    }

    [TestMethod]
    public async Task CancelarUnPedido_AnulaLaFacturaDeCadaModo_YDevuelveElStockYElUsoDelCupon()
    {
        await using var h = await CoreHarness.Start();
        var productId = await ProductOf(h);
        await h.Db(async db =>
        {
            var settings = await db.TaxSettings.SingleOrDefaultAsync(s => s.Id == 1) ?? db.TaxSettings.Add(new TaxSettings()).Entity;
            settings.SetDefaultPriceMode(Currency.Ars, PriceMode.TaxExcluded);
            (await db.Products.SingleAsync(p => p.Id == productId)).Stock = 1;
            await db.SaveChangesAsync();
        });
        var support = await h.Use<CatalogService, long>(s => s.SaveProduct(null, new ProductInput("Soporte", Prices: [new PriceInput("P1M", "ARS", 1000m)])));
        var supportPlan = await h.Query(db => db.Plans.Where(p => p.ProductId == support).Select(p => p.Id).SingleAsync());
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("UNO", DiscountKind.Percent, 10m, MaxUses: 1)));
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId), new OrderItem(supportPlan)], couponCode: "UNO"));
        Assert.AreEqual(2, placed.InvoiceIds!.Count, "una factura por modo de carga");

        await h.Use<OrderService, bool>(async s =>
        {
            await s.Cancel(placed.OrderId);
            return true;
        });

        // Las dos facturas quedan anuladas, y el stock y el uso del cupón vuelven: otro pedido los puede tomar.
        Assert.IsTrue(await h.Query(db => db.Documents.Where(d => placed.InvoiceIds.Contains(d.Id)).AllAsync(d => d.Credited == d.Total)));
        Assert.AreEqual((1, 0), (await h.Query(db => db.Products.Where(p => p.Id == productId).Select(p => p.Stock!.Value).SingleAsync()), await h.Query(db => db.Coupons.Select(c => c.Uses).SingleAsync())));
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)], couponCode: "UNO"));
        Assert.AreEqual((0, 1), (await h.Query(db => db.Products.Where(p => p.Id == productId).Select(p => p.Stock!.Value).SingleAsync()), await h.Query(db => db.Coupons.Select(c => c.Uses).SingleAsync())));
    }

    [TestMethod]
    public async Task TablaDePreciosSinUnaMoneda_ElCicloDejaDeVenderseEnEsaMoneda_YVuelveSiSeCarga()
    {
        await using var h = await CoreHarness.Start();
        var productId = await h.Use<CatalogService, long>(s => s.SaveProduct(null, new ProductInput("Soporte", Prices: [new PriceInput("P1M", "ARS", 1000m), new PriceInput("P1M", "USD", 10m)])));
        var planId = await h.Query(db => db.Plans.Where(p => p.ProductId == productId).Select(p => p.Id).SingleAsync());
        h.Clock.Now = h.Clock.Now.AddDays(1);
        var today = await h.Use<BillingCore, DateOnly>(c => Task.FromResult(c.Today()));
        Task<decimal?> Price(Currency currency) => h.Query(async db => (await CatalogQueries.Price(db, planId, currency, today, default))?.Amount);

        // El staff saca el precio en dólares del ciclo mensual: el ciclo sigue en pesos y en dólares ya no se vende.
        await h.Use<CatalogService, long>(s => s.SaveProduct(productId, new ProductInput("Soporte", Prices: [new PriceInput("P1M", "ARS", 1000m)])));

        Assert.AreEqual(((decimal?)1000m, (decimal?)null), (await Price(Currency.Ars), await Price(Currency.Usd)));
        var view = await h.Query(db => CatalogQueries.Products(db, today, [productId], default));
        CollectionAssert.AreEqual(new[] { "ARS" }, view.Single().Prices.Select(p => p.Currency).ToArray());

        // Cargarlo otra vez el mismo día lo vuelve a vender, con el importe nuevo.
        await h.Use<CatalogService, long>(s => s.SaveProduct(productId, new ProductInput("Soporte", Prices: [new PriceInput("P1M", "ARS", 1000m), new PriceInput("P1M", "USD", 12m)])));
        Assert.AreEqual((decimal?)12m, await Price(Currency.Usd));
    }

    [TestMethod]
    public async Task Stock_SeDescuenta_YDosPedidosALaVezNoPasanElUltimo()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Id == db.Plans.Where(x => x.Id == h.PlanId).Select(x => x.ProductId).Single());
            product.Stock = 1;
            await db.SaveChangesAsync();
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await Order(h);
                return true;
            }
            catch (ValidationException ex)
            {
                StringAssert.Contains(ex.Message, "No queda stock");
                return false;
            }
        }));

        Assert.AreEqual(1, results.Count(r => r));
        Assert.AreEqual(0, await h.Query(db => db.Products.Where(p => p.Stock != null).Select(p => p.Stock).SingleAsync()));
        Assert.AreEqual(1, await h.Query(db => db.Services.CountAsync()));
    }

    [TestMethod]
    public async Task Pedido_LaMismaReferenciaDosVecesALaVez_EsUnSoloPedido()
    {
        await using var h = await CoreHarness.Start();

        var placed = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)], clientReference: "portal:doble"))));

        Assert.AreEqual((placed[0].OrderId, placed[0].InvoiceId), (placed[1].OrderId, placed[1].InvoiceId));
        Assert.AreEqual(1, await h.Query(db => db.Orders.CountAsync()));
        Assert.AreEqual(1, await h.Query(db => db.Services.CountAsync()));
    }

    [TestMethod]
    public async Task Linea_CantidadSoloSiElProductoSePidePorCantidad_YElNombreConSuLargo()
    {
        await using var h = await CoreHarness.Start();
        var many = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId, 3)])));
        StringAssert.Contains(many.Message, "de a una unidad por línea");
        var named = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId, Label: new string('a', 256))])));
        StringAssert.Contains(named.Message, "hasta 255 caracteres");
        var (_, licenses) = await h.Query(db => CatalogSeed.Product(db, "Licencia", 1000m, p => p.AllowQuantity = true));

        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(licenses, 3)]));

        Assert.AreEqual(3, (await h.Query(db => db.Services.SingleAsync())).Quantity);
        Assert.AreEqual(3000m, await h.Query(db => db.Documents.Where(d => d.Id == placed.InvoiceId).Select(d => d.Total).SingleAsync()));
    }

    [TestMethod]
    public async Task PruebaGratis_NoFactura_AprovisionaYa_YFacturaAlTerminar_UnaSolaVezPorCuenta()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            product.TrialDays = 14;
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new PlanPrice(h.PlanId, Currency.Ars, 12100m, 3000m, new DateOnly(2026, 6, 1)));
            await db.SaveChangesAsync();
        });

        var trial = await Order(h);

        Assert.IsNull(trial.InvoiceId);
        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Provisioning, service.Status);
        Assert.AreEqual(new DateOnly(2026, 11, 16), service.TrialEndsOn);
        Assert.AreEqual(new DateOnly(2026, 11, 16), service.NextDueDate);
        Assert.AreEqual(OrderStatus.Accepted, (await h.Query(db => db.Orders.SingleAsync())).Status);

        // La segunda vez no hay prueba: se factura desde el día, con el cargo de alta.
        var paid = await Order(h);
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == paid.InvoiceId));
        Assert.AreEqual(12100m + 3000m, invoice.Total);

        // Al terminar la prueba la renovación factura el primer período, sin cargo de alta.
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(8);
        await h.Use<BillingRunService, int>(s => s.Run());
        var renewal = await h.Query(db => db.DocumentLines.SingleAsync(l => l.ServiceId == service.Id));
        Assert.AreEqual(12100m, renewal.Amount);
        StringAssert.Contains(renewal.Description, "16/11/2026");
    }

    [TestMethod]
    public async Task Cupon_PorcentajeRecurrente_DescuentaSusCiclos_YUnaVezPorCuenta()
    {
        await using var h = await CoreHarness.Start();
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("Verano25", DiscountKind.Percent, 25m, Recurring: true, RecurringCycles: 2, OncePerAccount: true)));

        var placed = await Order(h, coupon: "VERANO25");

        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(9075m, invoice.Total);
        Assert.AreEqual(1, await h.Query(db => db.Coupons.Select(c => c.Uses).SingleAsync()));
        Assert.IsNotNull((await h.Query(db => db.Orders.SingleAsync())).CouponId);
        var again = await Assert.ThrowsExactlyAsync<ValidationException>(() => Order(h, coupon: "verano25"));
        StringAssert.Contains(again.Message, "ya se usó en esta cuenta");

        // Dos ciclos: el del pedido y la primera renovación. La segunda va completa.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 9075m, PaymentStatus.Approved)));
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 9075m, PaymentStatus.Approved)));
        h.Clock.Now = h.Clock.Now.AddDays(30);
        await h.Use<BillingRunService, int>(s => s.Run());
        var totals = await h.Query(db => db.Documents.Where(d => d.Kind == DocumentKind.Invoice).OrderBy(d => d.Id).Select(d => d.Total).ToListAsync());
        CollectionAssert.AreEqual(new[] { 9075m, 9075m, 12100m }, totals);
    }

    [TestMethod]
    public async Task Cupon_FijoConMoneda_SoloSusProductos_YElTopeDeUsosAunqueLleguenDosALaVez()
    {
        await using var h = await CoreHarness.Start();
        var productId = await ProductOf(h);
        var other = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Otra cuenta", "otra@example.com", Currency.Ars)));
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("MIL", DiscountKind.Fixed, 1000m, "ARS", ProductIds: [productId], MaxUses: 1)));
        var (_, otherPlan) = await h.Query(db => CatalogSeed.Product(db, "Hosting", 5000m));

        var wrong = await Assert.ThrowsExactlyAsync<ValidationException>(() => Order(h, coupon: "MIL", planId: otherPlan));
        StringAssert.Contains(wrong.Message, "no vale para nada de este pedido");

        var results = await Task.WhenAll(new[] { h.AccountId, other }.Select(async account =>
        {
            try
            {
                return (await Order(h, coupon: "MIL", account: account)).InvoiceId;
            }
            catch (ValidationException)
            {
                return null;
            }
        }));

        Assert.AreEqual(1, results.Count(r => r is not null));
        Assert.AreEqual(11100m, await h.Query(db => db.Documents.Where(d => d.Id == results.Single(r => r != null)).Select(d => d.Total).SingleAsync()));
        Assert.AreEqual(1, await h.Query(db => db.Coupons.Select(c => c.Uses).SingleAsync()));
    }

    [TestMethod]
    public async Task Paquete_DescuentaSusLineas_NoTomanCupon_YElPedidoTraeExactamenteSusPlanes()
    {
        await using var h = await CoreHarness.Start();
        var vps = await ProductOf(h);
        var (_, backup) = await h.Query(db => CatalogSeed.Product(db, "Backup", 2000m, p => { p.Addon = true; p.ParentProductIds = [vps]; }));
        var (_, hosting) = await h.Query(db => CatalogSeed.Product(db, "Hosting", 5000m));
        var bundle = await h.Use<CatalogService, long>(s => s.SaveBundle(null, new BundleInput("VPS con backup", [new(h.PlanId), new(backup)], 20m, DiscountCycles: 1)));
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("DIEZ", DiscountKind.Percent, 10m)));
        Task<PlacedOrder> Place(IReadOnlyList<OrderItem> items, long? bundleId) =>
            h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, items, couponCode: "DIEZ", bundleId: bundleId));

        var missing = await Assert.ThrowsExactlyAsync<ValidationException>(() => Place([new OrderItem(h.PlanId, Bundled: true)], bundle));
        StringAssert.Contains(missing.Message, "VPS 2 GB (P1M): 1, Backup (P1M): 1");
        var twice = await Assert.ThrowsExactlyAsync<ValidationException>(() => Place([new OrderItem(h.PlanId, 2, Bundled: true), new OrderItem(backup, ParentItem: 0, Bundled: true)], bundle));
        StringAssert.Contains(twice.Message, "con sus unidades");
        var unmarked = await Assert.ThrowsExactlyAsync<ValidationException>(() => Place([new OrderItem(h.PlanId, Bundled: true)], null));
        StringAssert.Contains(unmarked.Message, "necesita el paquete");

        // El VPS y su backup con el 20 % por un período, y el hosting aparte con el cupón.
        var placed = await Place([new OrderItem(h.PlanId, Bundled: true), new OrderItem(backup, ParentItem: 0, Bundled: true), new OrderItem(hosting)], bundle);

        var amounts = await h.Query(db => db.DocumentLines.Where(l => l.DocumentId == placed.InvoiceId).OrderBy(l => l.Id).Select(l => l.Amount).ToListAsync());
        CollectionAssert.AreEqual(new[] { 9680m, 1600m, 4500m }, amounts);
        Assert.AreEqual(bundle, await h.Query(db => db.Orders.Select(o => o.BundleId).SingleAsync()));
        var services = await h.Query(db => db.Services.OrderBy(s => s.Id).ToListAsync());
        Assert.IsNull(services[0].CouponId);
        Assert.AreEqual((0, services[0].UnitPrice), (services[0].DiscountCyclesLeft, services[0].DiscountedUnitPrice), "un período de descuento: la primera factura lo consumió");
        Assert.AreEqual(placed.ServiceIds[0], services[1].ParentServiceId);
        Assert.IsNotNull(services[2].CouponId);

        // Apagado no se puede pedir, y lo que se lee del paquete dice cuánto sale.
        var view = (await h.Query(db => CatalogQueries.Bundles(db, new DateOnly(2026, 10, 29), [bundle], default))).Single();
        Assert.AreEqual(("ARS", 11280m, 14100m), (view.Prices[0].Currency, view.Prices[0].Amount, view.Prices[0].FullAmount));
        await h.Use<CatalogService, long>(s => s.SaveBundle(bundle, new BundleInput("VPS con backup", [new(h.PlanId), new(backup)], 20m, 1, Active: false)));
        var off = await Assert.ThrowsExactlyAsync<ValidationException>(() => Place([new OrderItem(h.PlanId, Bundled: true), new OrderItem(backup, ParentItem: 0, Bundled: true)], bundle));
        StringAssert.Contains(off.Message, "no está disponible");
        var empty = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<CatalogService, long>(s => s.SaveBundle(null, new BundleInput("Vacío", []))));
        StringAssert.Contains(empty.Message, "al menos un plan");
    }

    [TestMethod]
    public async Task Complemento_CuelgaDeUnServicioQueLoAdmite_YSeDaDeBajaConElPadre()
    {
        await using var h = await CoreHarness.Start();
        var vps = await ProductOf(h);
        var (_, fastdl) = await h.Query(db => CatalogSeed.Product(db, "FastDL 80 GB", 3164.10m, p => { p.Addon = true; p.ParentProductIds = [vps]; }));
        var (_, hosting) = await h.Query(db => CatalogSeed.Product(db, "Hosting", 5000m));

        var parent = await Order(h);
        var alone = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(fastdl)])));
        StringAssert.Contains(alone.Message, "va colgado de un servicio");
        var hostingOrder = await Order(h, planId: hosting);
        var wrongParent = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(fastdl, ParentServiceId: hostingOrder.ServiceIds[0])])));
        StringAssert.Contains(wrongParent.Message, "no se puede agregar a ese servicio");

        // En el mismo pedido, colgado de otra línea, y en otro pedido, del servicio ya contratado.
        var both = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId), new OrderItem(fastdl, ParentItem: 0)]));
        var addon = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(fastdl, ParentServiceId: parent.ServiceIds[0])]));
        Assert.AreEqual(both.ServiceIds[0], (await h.Query(db => db.Services.SingleAsync(s => s.Id == both.ServiceIds[1]))).ParentServiceId);
        Assert.AreEqual(parent.ServiceIds[0], (await h.Query(db => db.Services.SingleAsync(s => s.Id == addon.ServiceIds[0]))).ParentServiceId);

        foreach (var invoice in new[] { parent.InvoiceId, addon.InvoiceId })
        {
            var total = await h.Query(db => db.Documents.Where(d => d.Id == invoice).Select(d => d.Total).SingleAsync());
            await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", $"T-{invoice}", Currency.Ars, total, PaymentStatus.Approved, [invoice!.Value])));
        }

        await h.RunJobs();
        var parentService = await h.Query(db => db.Services.SingleAsync(s => s.Id == parent.ServiceIds[0]));
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(parentService.Id, "terminate", parentService.Generation), $"service:{parentService.Id}", "terminate-parent");
            return true;
        });
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync(s => s.Id == addon.ServiceIds[0]))).Status);
    }

    [TestMethod]
    [DataRow(DiscountKind.Percent, 100)]
    [DataRow(DiscountKind.Fixed, 20000)]
    public async Task DescuentoTotalEnElPedido_ElPeriodoGratisQuedaPago_NoEntraEnMora_YElSiguienteSeFactura(DiscountKind kind, int value)
    {
        await using var h = await CoreHarness.Start();
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("GRATIS", kind, value, kind == DiscountKind.Fixed ? "ARS" : null)));

        var placed = await Order(h, coupon: "GRATIS");

        // Sin nada que pagar no hay factura: el primer período corre sin facturarse y queda pago, como en la renovación.
        Assert.IsNull(placed.InvoiceId);
        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual((new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 2)), (service.NextDueDate, service.PaidThrough));
        Assert.AreEqual(0, await h.Query(db => db.BilledPeriods.CountAsync()));
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(10);
        Assert.AreEqual(0, await h.Use<DunningService, int>(s => s.Run()), "la mora no suspende un período que no se cobra");
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);

        // Al terminar el período gratis se factura el segundo, a precio lleno porque el cupón valía para la primera factura.
        h.Clock.Now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        Assert.AreEqual(12100m, await h.Query(db => db.DocumentLines.Where(l => l.Kind == LineKind.Renewal && l.ServiceId == service.Id).Select(l => l.Amount).SingleAsync()));
    }

    [TestMethod]
    public async Task PedidoQueMezclaModos_ConLaFacturaDeUnModoEnCero_SoloGuardaLosPeriodosDeLaEmitida()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var settings = await db.TaxSettings.SingleOrDefaultAsync(s => s.Id == 1) ?? db.TaxSettings.Add(new TaxSettings()).Entity;
            settings.SetDefaultPriceMode(Currency.Ars, PriceMode.TaxExcluded);
            await db.SaveChangesAsync();
        });
        var support = await h.Use<CatalogService, long>(s => s.SaveProduct(null, new ProductInput("Soporte", Prices: [new PriceInput("P1M", "ARS", 1000m)])));
        var supportPlan = await h.Query(db => db.Plans.Where(p => p.ProductId == support).Select(p => p.Id).SingleAsync());
        await h.Use<CatalogService, long>(s => s.SaveCoupon(null, new CouponInput("SOPORTE", DiscountKind.Percent, 100m, ProductIds: [support])));

        // El soporte, sin impuesto y gratis, va en una factura que queda en cero y no se emite. El VPS sale en la suya.
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(supportPlan), new OrderItem(h.PlanId)], couponCode: "SOPORTE"));

        Assert.AreEqual(1, placed.InvoiceIds!.Count);
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync()));
        var period = await h.Query(db => db.BilledPeriods.Include(p => p.Line).SingleAsync());
        Assert.AreEqual((placed.ServiceIds[1], placed.InvoiceId), (period.ServiceId, (long?)period.Line!.DocumentId));
        var free = await h.Query(db => db.Services.SingleAsync(s => s.Id == placed.ServiceIds[0]));
        Assert.AreEqual(free.NextDueDate, free.PaidThrough, "el período gratis queda pago");
        Assert.AreEqual(ServiceStatus.Provisioning, free.Status, "no tiene nada que pagar: no espera la factura del VPS");
    }
}
