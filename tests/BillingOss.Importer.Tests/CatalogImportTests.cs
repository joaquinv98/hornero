using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Importer.Import;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Importer.Tests;

/// <summary>El catálogo completo del origen sintético: opciones con precios, complementos, cupones y cambios pendientes.</summary>
[TestClass]
public sealed class CatalogImportTests
{
    private const string Dump = """
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `companyname` text, `email` text, `country` text, `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` text, `language` text);
        CREATE TABLE `tblproductgroups` (`id` int(11), `name` text, `headline` text, `hidden` tinyint(1), `order` int(11));
        CREATE TABLE `tblproducts` (`id` int(11), `gid` int(11), `name` text, `description` text, `hidden` tinyint(1), `retired` tinyint(1), `paytype` text, `servertype` text, `tax` tinyint(1), `order` int(11), `allowqty` int(11), `stockcontrol` tinyint(1), `qty` int(11), `configoptionsupgrade` tinyint(1), `autosetup` text);
        CREATE TABLE `tblproduct_upgrade_products` (`id` int(10), `product_id` int(11), `upgrade_product_id` int(11));
        CREATE TABLE `tblpricing` (`id` int(11), `type` text, `currency` int(11), `relid` int(11), `msetupfee` decimal(16,2), `qsetupfee` decimal(16,2), `ssetupfee` decimal(16,2), `asetupfee` decimal(16,2), `bsetupfee` decimal(16,2), `tsetupfee` decimal(16,2), `monthly` decimal(16,2), `quarterly` decimal(16,2), `semiannually` decimal(16,2), `annually` decimal(16,2), `biennially` decimal(16,2), `triennially` decimal(16,2));
        CREATE TABLE `tblproductconfiggroups` (`id` int(11), `name` text, `description` text);
        CREATE TABLE `tblproductconfiglinks` (`id` int(11), `gid` int(11), `pid` int(11));
        CREATE TABLE `tblproductconfigoptions` (`id` int(11), `gid` int(11), `optionname` text, `optiontype` text, `qtyminimum` int(11), `qtymaximum` int(11), `order` int(11), `hidden` int(11));
        CREATE TABLE `tblproductconfigoptionssub` (`id` int(11), `configid` int(11), `optionname` text, `sortorder` int(11), `hidden` int(11));
        CREATE TABLE `tblhosting` (`id` int(11), `userid` int(11), `packageid` int(11), `regdate` date, `domain` text, `qty` int(11), `amount` decimal(16,2), `billingcycle` text, `nextduedate` date, `nextinvoicedate` date, `termination_date` date, `domainstatus` text, `username` text, `paymentmethod` text, `promoid` int(11));
        CREATE TABLE `tblhostingconfigoptions` (`id` int(11), `relid` int(11), `configid` int(11), `optionid` int(11), `qty` int(11));
        CREATE TABLE `tbladdons` (`id` int(11), `packages` text, `name` text, `description` text, `billingcycle` text, `allowqty` tinyint(1), `tax` tinyint(1), `showorder` tinyint(1), `hidden` tinyint(1), `retired` tinyint(1), `module` varchar(32), `weight` int(11), `autoactivate` text);
        CREATE TABLE `tblhostingaddons` (`id` int(11), `orderid` int(11), `hostingid` int(11), `addonid` int(11), `userid` int(11), `qty` int(11), `setupfee` decimal(16,2), `recurring` decimal(16,2), `billingcycle` text, `status` text, `regdate` date, `nextduedate` date, `nextinvoicedate` date, `termination_date` date, `paymentmethod` text);
        CREATE TABLE `tblpromotions` (`id` int(11), `code` text, `type` text, `recurring` int(11), `value` decimal(16,2), `cycles` text, `appliesto` text, `requires` text, `requiresexisting` int(11), `startdate` date, `expirationdate` date, `maxuses` int(11), `uses` int(11), `lifetimepromo` int(11), `applyonce` int(11), `newsignups` int(11), `existingclient` int(11), `onceperclient` int(11), `recurfor` int(11), `upgrades` int(11), `notes` text);
        CREATE TABLE `tblorders` (`id` int(11), `userid` int(11), `date` datetime, `invoiceid` int(10), `status` text, `promocode` text);
        CREATE TABLE `tblinvoices` (`id` int(10), `userid` int(11), `invoicenum` text, `date` date, `duedate` date, `subtotal` decimal(16,2), `credit` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` text);
        CREATE TABLE `tblinvoiceitems` (`id` int(11), `invoiceid` int(10), `type` varchar(30), `relid` int(11), `description` text, `amount` decimal(16,2), `taxed` int(11), `duedate` date);
        CREATE TABLE `tblupgrades` (`id` int(11), `userid` int(11), `orderid` int(11), `type` text, `date` date, `relid` int(11), `originalvalue` text, `newvalue` text, `new_cycle` varchar(30), `amount` decimal(16,2), `credit_amount` decimal(16,2), `new_recurring_amount` decimal(16,2), `recurringchange` decimal(16,2), `status` text, `paid` text);
        INSERT INTO `tblcurrencies` VALUES (1,'USD',1),(3,'ARS',0);
        INSERT INTO `tblclients` VALUES (701,'Ana','Prueba','','ana@example.com','AR',3,0.00,'2025-01-01','Active','spanish');
        INSERT INTO `tblproductgroups` VALUES (8,'VPS','Servidores virtuales',0,2);
        INSERT INTO `tblproducts` VALUES (60,8,'VPS','<p>Un VPS con <b>SSD</b></p><p>L&iacute;nea dos</p>',0,0,'recurring','proxmox_custom',1,1,0,1,5,1,'payment'),(61,8,'VPS grande','',0,0,'recurring','proxmox_custom',1,2,0,0,0,0,'order'),(62,8,'Licencia','',0,0,'onetime','',1,3,0,0,0,0,'');
        INSERT INTO `tblproduct_upgrade_products` VALUES (1,60,61),(2,60,999);
        INSERT INTO `tblpricing` VALUES (1,'product',3,60,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00),(2,'product',1,60,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00),(3,'product',3,61,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,1000.00,-1.00,-1.00,-1.00,-1.00,-1.00),(4,'product',3,62,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,500.00,-1.00,-1.00,-1.00,-1.00,-1.00),
          (10,'configoptions',3,1000,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00),(11,'configoptions',1,1000,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00),(12,'configoptions',3,1001,1000.00,-1.00,-1.00,0.00,-1.00,-1.00,5000.00,-1.00,-1.00,50000.00,-1.00,-1.00),(13,'configoptions',1,1001,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,5.00,-1.00,-1.00,-1.00,-1.00,-1.00),(14,'configoptions',3,1002,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,9000.00,-1.00,-1.00,-1.00,-1.00,-1.00),(15,'configoptions',3,1010,500.00,-1.00,-1.00,-1.00,-1.00,-1.00,2500.00,-1.00,-1.00,-1.00,-1.00,-1.00),(16,'configoptions',3,1020,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,1500.00,-1.00,-1.00,-1.00,-1.00,-1.00),(17,'configoptions',3,1030,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,100.00,-1.00,-1.00,-1.00,-1.00,-1.00),
          (20,'addon',3,5,0.00,-1.00,-1.00,0.00,-1.00,-1.00,3000.00,-1.00,-1.00,30000.00,-1.00,-1.00),(21,'addon',3,6,0.00,0.00,0.00,0.00,0.00,0.00,1000.00,0.00,0.00,0.00,0.00,0.00);
        INSERT INTO `tblproductconfiggroups` VALUES (10,'VPS',''),(11,'Licencias',''),(12,'Sin productos','');
        INSERT INTO `tblproductconfiglinks` VALUES (1,10,60),(2,10,61),(3,11,62),(4,99,60);
        INSERT INTO `tblproductconfigoptions` VALUES (100,10,'memory|RAM','1',0,0,0,0),(101,10,'IP Addresses|IPs extra','4',0,4,1,0),(102,10,'backups|Backups','3',0,0,2,0),(103,11,'Usuarios','4',1,10,0,0),(104,99,'huerfana|Sin grupo','1',0,0,0,0);
        INSERT INTO `tblproductconfigoptionssub` VALUES (1000,100,'1024|1 GB',0,0),(1001,100,'2048|2 GB',1,0),(1002,100,'4096|4 GB',2,1),(1010,101,'IP',0,0),(1020,102,'Diario',0,0),(1030,103,'Usuario',0,0),(1040,104,'X',0,0);
        INSERT INTO `tblhosting` VALUES (950,701,60,'2026-08-10','vps.example.com',1,7500.00,'Monthly','2026-11-10','2026-11-10','0000-00-00','Active','','mercadopago_1',30),(951,701,60,'2025-01-10','',1,0.00,'Monthly','2025-03-10','2025-03-10','2025-03-15','Terminated','','mercadopago_1',0),(953,701,60,'2026-08-10','',1,0.00,'Monthly','2026-11-10','2026-11-10','0000-00-00','Active','','mercadopago_1',0);
        INSERT INTO `tblhostingconfigoptions` VALUES (1,950,100,1001,0),(2,950,101,1010,1),(3,950,102,1020,1),(4,951,100,1000,0),(5,953,100,1020,0),(6,953,101,1010,7);
        INSERT INTO `tbladdons` VALUES (5,',60,61,999,','FastDL','Descarga <b>r&aacute;pida</b>','recurring',0,1,0,0,0,'fastdl',0,'payment'),(6,',60,','Soporte','','Monthly',0,1,0,1,0,'',1,'on');
        INSERT INTO `tblhostingaddons` VALUES (8,400,950,5,701,1,0.00,3000.00,'Monthly','Active','2026-10-10','2026-11-10','2026-11-10','0000-00-00','mercadopago_1');
        INSERT INTO `tblpromotions` VALUES (30,'PROMO30','Percentage',1,30.00,'Monthly,Annually','60,A5,D.ar','',0,'2026-01-01','0000-00-00',100,3,0,1,0,1,1,6,1,'Lanzamiento'),(31,'promo30','Fixed Amount',0,20.00,'','','',0,'0000-00-00','0000-00-00',0,0,0,0,0,0,0,0,0,''),(32,'SOLODOMINIOS','Percentage',0,10.00,'','D.com.ar','',0,'0000-00-00','0000-00-00',0,0,0,0,0,0,0,0,0,''),(33,'REQUIERE','Percentage',0,10.00,'','60','61',0,'0000-00-00','0000-00-00',0,0,0,0,0,0,0,0,0,''),(34,'GRATIS','Free Setup',0,0.00,'','60','',0,'0000-00-00','0000-00-00',0,0,0,0,0,0,0,0,0,''),(35,'USADO','Percentage',0,10.00,'1Years','60','',0,'0000-00-00','2026-12-31',2,5,0,0,0,0,0,0,0,'');
        INSERT INTO `tblorders` VALUES (400,701,'2026-08-10 10:00:00',0,'Active','promo30'),(401,701,'2026-10-19 10:00:00',9002,'Pending',''),(402,701,'2026-10-20 10:00:00',9000,'Pending','');
        INSERT INTO `tblinvoices` VALUES (9000,701,'9000','2026-10-20','2026-10-27',1641.29,0.00,0.00,0.00,1641.29,0.000,'Unpaid'),(9001,701,'9001','2026-10-10','2026-10-17',3000.00,0.00,0.00,0.00,3000.00,0.000,'Paid'),(9002,701,'9002','2026-10-19','2026-10-26',100.00,0.00,0.00,0.00,100.00,0.000,'Unpaid');
        INSERT INTO `tblinvoiceitems` VALUES (1,9000,'Upgrade',7001,'RAM: 2 GB a 4 GB',1641.29,0,'2026-10-20'),(2,9001,'Addon',8,'FastDL',3000.00,0,'2026-10-10'),(3,9002,'Upgrade',7000,'RAM: 2 GB a 4 GB',100.00,0,'2026-10-19');
        INSERT INTO `tblupgrades` VALUES (7000,701,401,'configoptions','2026-10-19',950,'100=>1001','1002','',100.00,0.00,0.00,2000.00,'Pending','N'),(7001,701,402,'configoptions','2026-10-20',950,'100=>1001','1002','',1641.29,0.00,0.00,4000.00,'Pending','N'),(7002,701,402,'configoptions','2026-10-20',950,'101=>1','3','',0.00,0.00,0.00,5000.00,'Pending','N'),(7003,701,400,'configoptions','2025-02-01',951,'100=>1000','1001','',10.00,0.00,0.00,1.00,'Completed','Y');
        """;

    private const string Modules = """
        { "proxmox_custom": { "options": { "memory": "memory_mb", "IP Addresses": "network.extra_ips", "backups": "" } } }
        """;

    [TestMethod]
    public Task OpcionesConPreciosPorMonedaYCiclo_YElProductoSeVendeConEllas() => SourceImportTests.WithImport(Dump, async (db, import) =>
    {
        var all = string.Join(" | ", import.Incidents);
        CollectionAssert.AreEquivalent(new long[] { 10, 11, 12 }, await db.OptionGroups.Select(g => g.Id).ToListAsync());
        CollectionAssert.AreEquivalent(new[] { (60L, 10L), (61L, 10L), (62L, 11L) }, (await db.ProductOptionGroups.ToListAsync()).Select(l => (l.ProductId, l.OptionGroupId)).ToArray());

        // Lo que va antes de la barra es la clave de módulo y el valor de módulo. El mapa la traduce o la saca.
        var ram = await db.Options.SingleAsync(o => o.Id == 100);
        Assert.AreEqual(("RAM", OptionKind.Select, "memory_mb"), (ram.Name, ram.Kind, ram.ModuleKey));
        var ips = await db.Options.SingleAsync(o => o.Id == 101);
        Assert.AreEqual(("IPs extra", OptionKind.Quantity, "network.extra_ips", 0, 4), (ips.Name, ips.Kind, ips.ModuleKey, ips.MinQuantity, ips.MaxQuantity));
        Assert.IsNull((await db.Options.SingleAsync(o => o.Id == 102)).ModuleKey);
        Assert.IsNull((await db.Options.SingleAsync(o => o.Id == 103)).ModuleKey);
        Assert.IsFalse(await db.Options.AnyAsync(o => o.Id == 104));
        var big = await db.OptionValues.SingleAsync(v => v.Id == 1001);
        Assert.AreEqual(("2 GB", "2048", false), (big.Name, big.ModuleValue, big.Hidden));
        Assert.IsTrue((await db.OptionValues.SingleAsync(v => v.Id == 1002)).Hidden);

        // Un -1 es un ciclo que no se ofrece. La opción de un producto de pago único cobra también en ese ciclo.
        var bigPrices = (await db.OptionPrices.Where(p => p.ValueId == 1001).ToListAsync()).Select(p => (p.Currency.Code, p.Cycle.Code, p.Amount, p.SetupFee)).ToList();
        CollectionAssert.AreEquivalent(new[] { ("ARS", "P1M", 5000m, 1000m), ("ARS", "P1Y", 50000m, 0m), ("USD", "P1M", 5m, 0m) }, bigPrices);
        CollectionAssert.AreEquivalent(new[] { "P1M", "onetime" }, (await db.OptionPrices.Where(p => p.ValueId == 1030).ToListAsync()).Select(p => p.Cycle.Code).ToArray());

        // El producto de precio 0 se vende con sus opciones: 2 GB y dos IP cobran 5.000 + 2 × 2.500 por mes.
        var catalog = await CatalogQueries.Options(db, 60, CancellationToken.None);
        var chosen = catalog.Resolve([new OptionChoice(100, 1001), new OptionChoice(101, Quantity: 2)], Currency.Ars, BillingCycle.Monthly);
        Assert.AreEqual(10000m, chosen.Sum(c => c.Amount));
        Assert.AreEqual(1500m, chosen.Sum(c => c.SetupFee));

        var product = await db.Products.SingleAsync(p => p.Id == 60);
        Assert.AreEqual("Un VPS con SSD\nLínea dos", product.Description);
        CollectionAssert.AreEqual(new long[] { 61 }, product.UpgradeProductIds);
        Assert.IsTrue(product.OptionChanges);
        Assert.AreEqual(5, product.Stock);
        Assert.IsFalse((await db.Products.SingleAsync(p => p.Id == 61)).OptionChanges);

        Assert.IsTrue(import.Incidents.Any(i => i.Contains("2 opciones o valores de un grupo u opción que ya no existe", StringComparison.Ordinal)), all);
        Assert.AreEqual((3L, 0m), import.Figures[("catalog", "grupos de opciones")]);

        // Las secuencias siguen arriba de lo importado: lo que cargue el staff no choca con los IDs del origen.
        db.OptionValues.Add(new OptionValue { OptionId = 100, Name = "8 GB" });
        db.Coupons.Add(new Coupon { Code = "NUEVO", Kind = DiscountKind.Percent, Value = 5m });
        await db.SaveChangesAsync();
        Assert.IsTrue(await db.OptionValues.MaxAsync(v => v.Id) > 1030);
        Assert.IsTrue(await db.Coupons.MaxAsync(c => c.Id) > 35);
    }, modules: ModuleMapping.Parse(Modules));

    [TestMethod]
    public Task SeleccionDeCadaServicio_EnSusOpcionesYEnLosParametrosDelModulo() => SourceImportTests.WithImport(Dump, async (db, import) =>
    {
        var all = string.Join(" | ", import.Incidents);
        var chosen = (await db.ServiceOptions.Where(o => o.ServiceId == 950).ToListAsync()).Select(o => (o.OptionId, o.ValueId, o.Quantity)).ToList();
        CollectionAssert.AreEquivalent(new[] { (100L, 1001L, 1), (101L, 1010L, 1), (102L, 1020L, 1) }, chosen);
        var live = await db.Services.SingleAsync(s => s.Id == 950);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"memory_mb":2048,"network":{"extra_ips":1}}"""), JsonNode.Parse(live.ModuleParams)), live.ModuleParams);

        // El terminado guarda su selección como historia, sin parámetros de módulo.
        Assert.AreEqual(1000L, (await db.ServiceOptions.SingleAsync(o => o.ServiceId == 951)).ValueId);
        Assert.AreEqual("{}", (await db.Services.SingleAsync(s => s.Id == 951)).ModuleParams);

        // Un valor de otra opción no se importa. Una cantidad fuera de rango sí, y sale en el reporte.
        Assert.AreEqual(7, (await db.ServiceOptions.SingleAsync(o => o.ServiceId == 953)).Quantity);
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 opciones elegidas en servicios con un valor que ya no existe", StringComparison.Ordinal)), all);
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 cantidades elegidas en servicios vivos fuera del rango", StringComparison.Ordinal)), all);
    }, modules: ModuleMapping.Parse(Modules));

    [TestMethod]
    public Task Complementos_ComoProductosQueCuelgan_YSusServiciosHijos() => SourceImportTests.WithImport(Dump, async (db, import) =>
    {
        var fastdl = await db.Products.SingleAsync(p => p.Name == "FastDL");
        Assert.IsTrue(fastdl.Addon);
        Assert.IsTrue(fastdl.Id > 62, "IDs nuevos, arriba de los productos importados");
        CollectionAssert.AreEquivalent(new long[] { 60, 61 }, fastdl.ParentProductIds);
        Assert.AreEqual(("Descarga rápida", ProductKind.Provisioned, true, ProductActivation.OnPayment), (fastdl.Description, fastdl.Kind, fastdl.Visible, fastdl.Activation));
        var plans = await (from p in db.Plans where p.ProductId == fastdl.Id join price in db.PlanPrices on p.Id equals price.PlanId select new { p.CycleUnit, p.CycleCount, price.Amount }).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { "Month1:3000", "Year1:30000" }, plans.Select(p => $"{p.CycleUnit}{p.CycleCount}:{p.Amount:0}").ToArray());

        // Un complemento de ciclo fijo se vende solo en ese ciclo.
        var support = await db.Products.SingleAsync(p => p.Name == "Soporte");
        Assert.IsFalse(support.Visible);

        // Lo que en el origen se activa al aceptar el pedido, o nunca solo, lo acepta el staff. Un producto sin módulo va al
        // módulo manual, que ya abre la tarea para el staff: se crea al pagar.
        Assert.AreEqual(ProductActivation.Manual, support.Activation);
        CollectionAssert.AreEqual(
            new[] { ProductActivation.OnPayment, ProductActivation.OnOrder, ProductActivation.OnPayment },
            await db.Products.Where(p => p.Id >= 60 && p.Id <= 62).OrderBy(p => p.Id).Select(p => p.Activation).ToListAsync());
        Assert.AreEqual(1, await db.Plans.CountAsync(p => p.ProductId == support.Id));

        var child = await db.Services.SingleAsync(s => s.ProductId == fastdl.Id);
        Assert.AreEqual(((long?)950L, ServiceStatus.Active, 3000m, (DateOnly?)new DateOnly(2026, 11, 10)), (child.ParentServiceId, child.Status, child.UnitPrice, child.NextDueDate));
        Assert.IsTrue(child.Id > 953);

        // Los renglones de complemento y de cambio apuntan a su servicio, no al número que traen en el origen.
        Assert.AreEqual(child.Id, (await db.DocumentLines.SingleAsync(l => l.DocumentId == 9001)).ServiceId);
        Assert.AreEqual(950L, (await db.DocumentLines.SingleAsync(l => l.DocumentId == 9000)).ServiceId);
        Assert.AreEqual((2L, 0m), import.Figures[("catalog", "complementos")]);
    });

    [TestMethod]
    public Task Cupones_SinAmpliarNunca_YLosPedidosYServiciosQueLosUsaron() => SourceImportTests.WithImport(Dump, async (db, import) =>
    {
        var all = string.Join(" | ", import.Incidents);
        var fastdl = await db.Products.Where(p => p.Name == "FastDL").Select(p => p.Id).SingleAsync();
        var promo = await db.Coupons.SingleAsync(c => c.Id == 30);
        Assert.AreEqual(("PROMO30", DiscountKind.Percent, 30m, true, (int?)6), (promo.Code, promo.Kind, promo.Value, promo.Recurring, promo.RecurringCycles));
        CollectionAssert.AreEquivalent(new[] { 60L, fastdl }, promo.ProductIds);
        CollectionAssert.AreEquivalent(new[] { "P1M", "P1Y" }, promo.Cycles);
        Assert.AreEqual(((int?)100, 3, true, true, true, true, new DateOnly(2026, 1, 1), (DateOnly?)null), (promo.MaxUses, promo.Uses, promo.OncePerOrder, promo.ExistingAccountsOnly, promo.OncePerAccount, promo.Active, promo.StartsOn!.Value, promo.ExpiresOn));
        Assert.AreEqual("Lanzamiento", promo.Notes);

        // El mismo código sin distinguir mayúsculas queda con el número del origen al final. Su monto fijo está en la moneda
        // base del origen, que lo convertía al cobrar: en otra moneda no descuenta hasta que el staff lo revise.
        var twin = await db.Coupons.SingleAsync(c => c.Id == 31);
        Assert.AreEqual(("promo30-31", DiscountKind.Fixed, true, (Currency?)Currency.Usd), (twin.Code, twin.Kind, twin.Active, twin.Currency));
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 cupones de monto fijo entran en USD", StringComparison.Ordinal)), all);

        // Solo dominios, o exigía otro producto, o solo ciclos de dominio: inactivo, nunca más amplio que en el origen.
        Assert.IsFalse((await db.Coupons.SingleAsync(c => c.Id == 32)).Active);
        Assert.IsFalse((await db.Coupons.SingleAsync(c => c.Id == 33)).Active);
        var used = await db.Coupons.SingleAsync(c => c.Id == 35);
        Assert.AreEqual(((int?)5, 5, false), (used.MaxUses, used.Uses, used.Active));
        Assert.IsFalse(await db.Coupons.AnyAsync(c => c.Id == 34));
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("Cupón 34:", StringComparison.Ordinal)), all);
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("3 cupones entran inactivos", StringComparison.Ordinal)), all);
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 cupones también descontaban cambios de plan", StringComparison.Ordinal)), all);

        // El pedido y el servicio que lo usaron lo recuerdan: "una vez por cuenta" sigue valiendo después de migrar.
        Assert.AreEqual(30L, (await db.Orders.SingleAsync(o => o.Id == 400)).CouponId);
        var service = await db.Services.SingleAsync(s => s.Id == 950);
        Assert.AreEqual(((long?)30L, (decimal?)null, 7500m), (service.CouponId, service.DiscountPercent, service.UnitPrice));
    });

    [TestMethod]
    public Task PromocionRecurrenteParaSiempre_ElServicioGuardaElDescuentoSobreElPrecioLleno() => SourceImportTests.WithImport(Dump + """
        INSERT INTO `tblpromotions` VALUES (36,'VIDA40','Percentage',1,40.00,'','60','',0,'0000-00-00','0000-00-00',0,1,1,0,0,0,0,0,0,'');
        INSERT INTO `tblhosting` VALUES (954,701,60,'2026-08-10','',1,6000.00,'Monthly','2026-11-10','2026-11-10','0000-00-00','Active','','mercadopago_1',36);
        """, async (db, import) =>
    {
        // El origen trae el importe con la promoción adentro. El servicio guarda el precio lleno y el 40 % para siempre: cobra
        // lo mismo, y una opción que se agregue también lleva el descuento.
        var service = await db.Services.SingleAsync(s => s.Id == 954);
        Assert.AreEqual(((decimal?)40m, (int?)null, 10000m, 6000m), (service.DiscountPercent, service.DiscountCyclesLeft, service.UnitPrice, service.RecurringAmount));

        // La de PROMO30 descuenta seis períodos y no se sabe cuántos le quedan: queda con el importe neto, y se avisa.
        var limited = await db.Services.SingleAsync(s => s.Id == 950);
        Assert.AreEqual(((decimal?)null, 7500m), (limited.DiscountPercent, limited.UnitPrice));
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 servicios vivos con una promoción recurrente por períodos", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
    });

    [TestMethod]
    public Task PruebasGratisDelOrigen_UnaPorCuentaYProducto_EnElServicio() => SourceImportTests.WithImport(Dump + """
        CREATE TABLE `mod_pruebas_gratis` (`id` int(10), `clientid` int(11), `serviceid` int(11), `packageid` int(11), `otorgada_en` datetime, `vence_en` date);
        INSERT INTO `mod_pruebas_gratis` VALUES (1,701,951,60,'2025-01-10 10:00:00','2025-01-17'),(2,701,950,60,'2026-08-10 10:00:00','2026-08-17'),(3,701,999,61,'2026-09-01 10:00:00','2026-09-08');
        """, async (db, import) =>
    {
        // Las dos pruebas del VPS de la cuenta quedan en una, la última, que alcanza para que no pida otra. La del servicio
        // que no existe no entra, y el reporte dice de qué productos eran para que el staff cargue sus días de prueba.
        var all = string.Join(" | ", import.Incidents);
        var trial = await db.Services.Where(s => s.TrialEndsOn != null).Select(s => new { s.Id, s.TrialEndsOn }).SingleAsync();
        Assert.AreEqual((950L, new DateOnly(2026, 8, 17)), (trial.Id, trial.TrialEndsOn!.Value));
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("3 pruebas gratis del origen, de 1 cuentas y 2 productos (60, 61)", StringComparison.Ordinal)), all);
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 pruebas gratis son de servicios que no se importan", StringComparison.Ordinal)), all);
        Assert.AreEqual((1L, 0m), import.Figures[("catalog", "pruebas gratis")]);
    });

    [TestMethod]
    public Task CambioSinPagarDelOrigen_QuedaPendienteDeSuFactura() => SourceImportTests.WithImport(Dump, async (db, import) =>
    {
        var all = string.Join(" | ", import.Incidents);
        var change = await db.ServiceChanges.SingleAsync();
        Assert.AreEqual((950L, ServiceChangeStatus.Pending, 9000L, 1641.29m), (change.ServiceId, change.Status, change.DocumentId!.Value, change.Amount));
        Assert.AreEqual((60L, 60L, 7500m, 16500m, new DateOnly(2026, 11, 10)), (change.FromProductId, change.ToProductId, change.FromUnitPrice, change.ToUnitPrice, change.BilledUntil!.Value));
        var options = JsonSerializer.Deserialize<List<OptionChoice>>(change.Options)!.OrderBy(o => o.OptionId).ToList();
        CollectionAssert.AreEqual(new[] { new OptionChoice(100, 1002), new OptionChoice(101, 1010, 3), new OptionChoice(102, 1020, 1) }, options);
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 pedidos de cambio sin pagar de servicios que tienen uno más nuevo", StringComparison.Ordinal)), all);
    });
}
