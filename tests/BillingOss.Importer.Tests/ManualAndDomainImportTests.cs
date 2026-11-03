using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Importer.Import;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Importer.Tests;

/// <summary>Productos sin módulo, dominios y precios por TLD desde un origen sintético (sin datos de clientes reales).</summary>
[TestClass]
public sealed class ManualAndDomainImportTests
{
    private const string Dump = """
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `email` text, `country` text, `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` text, `language` text);
        CREATE TABLE `tblproductgroups` (`id` int(11), `name` text, `hidden` tinyint(1));
        CREATE TABLE `tblproducts` (`id` int(11), `gid` int(11), `name` text, `hidden` tinyint(1), `retired` tinyint(1), `paytype` text, `servertype` text, `tax` tinyint(1), `autosetup` text, `configoption1` text, `autoterminatedays` int(11));
        CREATE TABLE `tblpricing` (`id` int(11), `type` text, `currency` int(11), `relid` int(11), `msetupfee` decimal(16,2), `qsetupfee` decimal(16,2), `ssetupfee` decimal(16,2), `asetupfee` decimal(16,2), `bsetupfee` decimal(16,2), `tsetupfee` decimal(16,2), `monthly` decimal(16,2), `quarterly` decimal(16,2), `semiannually` decimal(16,2), `annually` decimal(16,2), `biennially` decimal(16,2), `triennially` decimal(16,2));
        CREATE TABLE `tblhosting` (`id` int(11), `userid` int(11), `packageid` int(11), `regdate` date, `domain` text, `qty` int(11), `amount` decimal(16,2), `billingcycle` text, `nextduedate` date, `termination_date` date, `domainstatus` text, `username` text, `paymentmethod` text);
        CREATE TABLE `tbldomains` (`id` int(10), `userid` int(10), `type` text, `registrationdate` date, `domain` text, `firstpaymentamount` decimal(16,2), `recurringamount` decimal(16,2), `registrar` text, `registrationperiod` int(1), `expirydate` date, `status` text, `nextduedate` date, `nextinvoicedate` date, `paymentmethod` text, `dnsmanagement` int(1), `emailforwarding` int(1), `idprotection` int(1), `donotrenew` int(1));
        CREATE TABLE `tbldomainpricing` (`id` int(10), `extension` text, `dnsmanagement` tinyint(1), `emailforwarding` tinyint(1), `idprotection` tinyint(1), `eppcode` tinyint(1), `autoreg` text);
        CREATE TABLE `tbldomainsadditionalfields` (`id` int(10), `domainid` int(10), `name` text, `value` text);
        CREATE TABLE `tblinvoices` (`id` int(10), `userid` int(11), `invoicenum` text, `date` date, `duedate` date, `subtotal` decimal(16,2), `credit` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` text);
        CREATE TABLE `tblinvoiceitems` (`id` int(11), `invoiceid` int(10), `type` varchar(30), `relid` int(11), `description` text, `amount` decimal(16,2), `taxed` int(11), `duedate` date);
        INSERT INTO `tblcurrencies` VALUES (1,'USD',1),(3,'ARS',0);
        INSERT INTO `tblclients` VALUES (501,'Ana','Prueba','ana@example.com','AR',3,0.00,'2025-03-31','Active','spanish');
        INSERT INTO `tblproductgroups` VALUES (11,'MSP modelo B',1),(20,'Software',0);
        INSERT INTO `tblproducts` VALUES (58,20,'Modulo de software',0,0,'onetime','',1,'payment','',0),(161,11,'MSP B1',0,0,'recurring','',1,'','',0),(190,20,'Enlace S2S A',0,0,'recurring','',1,'','',0),(29,20,'VPS retirado',0,1,'recurring','',1,'','',0),(2,20,'Hosting Web',0,0,'recurring','vesta',1,'on','gainsboro',0);
        INSERT INTO `tblhosting` VALUES (950,501,58,'2026-01-10','',1,0.00,'One Time','0000-00-00','0000-00-00','Active','u950','mercadopago_1'),(951,501,2,'2026-01-10','web.example.com',1,1000.00,'Monthly','2026-11-10','0000-00-00','Active','u951','mercadopago_1');
        INSERT INTO `tbldomains` VALUES
          (10,501,'Register','2025-01-10','cliente.proveedor.ar',0.00,0.00,'',1,'0000-00-00','Active','2027-01-10','2022-01-10','mercadopago_1',0,0,0,0),
          (11,501,'Register','2021-05-01','ejemplo.com.ar',0.00,0.00,'',1,'0000-00-00','Expired','2023-05-01','2023-05-01','mercadopago_1',0,0,0,0),
          (12,501,'Register','2024-12-01','ejemplo.com',18.99,18.99,'internetbs',1,'2026-12-01','Active','2026-12-01','2025-11-24','paypal_ppcpv',1,0,0,0),
          (13,501,'Register','2023-02-01','otro.net',15.00,15.00,'resellerclub',1,'2026-10-01','Grace','2026-10-01','2026-09-24','mercadopago_1',0,0,0,0),
          (14,501,'Register','2020-02-01','viejo.org',15.00,15.00,'',1,'0000-00-00','Cancelled','2021-02-01','2021-02-01','mercadopago_1',0,0,0,0),
          (15,501,'Register','2024-02-01','activo.proveedor.ar',0.00,0.00,'',1,'0000-00-00','Active','2027-02-01','2022-01-01','mercadopago_1',0,0,0,1),
          (16,501,'Register','2026-10-01','Ejemplo.com',18.99,18.99,'internetbs',1,'0000-00-00','Pending','2027-10-01','2027-10-01','paypal_ppcpv',0,0,0,0);
        INSERT INTO `tbldomainpricing` VALUES (1,'.com',1,1,0,1,'internetbs'),(2,'.cn',1,1,1,1,'');
        INSERT INTO `tblpricing` VALUES
          (1,'domainregister',1,1,17.99,34.99,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00),
          (2,'domainrenew',1,1,18.99,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00),
          (3,'domaintransfer',1,1,17.99,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00),
          (4,'domainregister',1,2,-1.00,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00),
          (5,'domainregister',1,999,9.99,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00),
          (6,'domainaddons',3,0,159.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00),
          (7,'domainaddons',1,0,0.10,0.00,2.50,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00,0.00);
        INSERT INTO `tbldomainsadditionalfields` VALUES (1,999,'Legal Type','Persona');
        INSERT INTO `tblinvoices` VALUES (7100,501,'7100','2026-11-24','2026-12-01',18.99,0.00,0.00,0.00,18.99,0.000,'Unpaid'),(7101,501,'7101','2026-09-24','2026-10-01',15.00,0.00,0.00,0.00,15.00,0.000,'Cancelled');
        INSERT INTO `tblinvoiceitems` VALUES (1,7100,'Domain',12,'Renovación de ejemplo.com',18.99,0,'2026-12-01'),(2,7100,'DomainAddonDNS',12,'DNS de ejemplo.com',0.00,0,'2026-12-01'),(3,7101,'Domain',13,'Renovación de otro.net',15.00,0,'2026-10-01');
        """;

    [TestMethod]
    public async Task ImportaManualesConSuInstancia_DominiosPorRegistrar_YPreciosPorTld()
    {
        var target = await TestDatabase.Create();
        try
        {
            await using var db = new BillingDbContext(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), target).Options);
            await db.Database.MigrateAsync();
            var import = new SourceImport(db, TextWriter.Null) { OwnZones = ["proveedor.ar", "prov.ar"] };
            await using (var dump = new DumpReader(new MemoryStream(Encoding.UTF8.GetBytes(Dump))))
            {
                await import.Load(dump, CancellationToken.None);
            }

            await import.Run(CancellationToken.None);

            var instances = await db.ModuleInstances.ToDictionaryAsync(i => i.Id, i => i.Name);
            var manual = instances.Single(i => i.Value == "servicios-manuales").Key;
            Assert.AreEqual(ManualModule.Address, (await db.ModuleInstallations.SingleAsync(i => i.Name == "manual")).BaseUrl);
            Assert.AreEqual(string.Empty, (await db.ModuleInstallations.SingleAsync(i => i.Name == "internetbs")).BaseUrl);
            Assert.AreEqual(string.Empty, (await db.ModuleInstallations.SingleAsync(i => i.Name == "resellerclub")).BaseUrl);

            // Productos sin módulo: manuales, con la instancia manual y la visibilidad del grupo.
            var products = await db.Products.ToDictionaryAsync(p => p.Id);
            Assert.AreEqual(ProductKind.Manual, products[58].Kind);
            Assert.AreEqual(manual, products[58].ModuleInstanceId);
            Assert.AreEqual(("on_payment", "state_only"), Manual(products[58]));
            Assert.AreEqual(("staff", "staff"), Manual(products[190]));
            Assert.AreEqual(("staff", "staff"), Manual(products[161]));
            Assert.IsTrue(products[58].Visible);
            Assert.IsTrue(products[190].Visible);
            Assert.IsFalse(products[161].Visible, "Visible dentro de un grupo oculto");
            Assert.IsFalse(products[29].Visible);
            Assert.AreEqual(ProductKind.Provisioned, products[2].Kind);
            Assert.IsNull(products[2].ModuleInstanceId);

            var manualService = await db.Services.SingleAsync(s => s.Id == 950);
            Assert.AreEqual(manual, manualService.ModuleInstanceId);
            Assert.AreEqual("{}", manualService.ExternalIds);
            StringAssert.Contains(manualService.ModuleParams, "on_payment");
            Assert.IsNull(manualService.NextDueDate);
            // Uno con módulo y sin mapa de módulos queda sin instancia, como cualquier otro (diseno/importacion.md).
            Assert.IsNull((await db.Services.SingleAsync(s => s.Id == 951)).ModuleInstanceId);

            // Dominios: ID, estado, instancia por registrar y fechas desde nextduedate.
            var domains = await db.Domains.ToDictionaryAsync(d => d.Id);
            Assert.HasCount(7, domains);
            Assert.AreEqual("subdominios-propios-manual", instances[domains[10].RegistrarInstanceId]);
            Assert.AreEqual("proveedor.ar", JsonNode.Parse(domains[10].ExternalIds)!["zone"]!.GetValue<string>());
            Assert.AreEqual(new DateOnly(2027, 1, 10), domains[10].NextDueDate);
            Assert.AreEqual("nic-ar-manual", instances[domains[11].RegistrarInstanceId]);
            Assert.AreEqual(DomainStatus.Expired, domains[11].Status);
            Assert.IsNull(domains[11].NextDueDate);
            Assert.AreEqual("internetbs", instances[domains[12].RegistrarInstanceId]);
            Assert.AreEqual("resellerclub", instances[domains[13].RegistrarInstanceId], "ResellerClub tiene módulo: va a su instalación, no al manual");
            Assert.AreEqual(DomainStatus.Grace, domains[13].Status);
            Assert.AreEqual((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1)), (domains[13].NextDueDate, domains[13].PaidThrough), "La renovación anulada en el origen vuelve a estar por facturar.");
            Assert.AreEqual("sin-registrar", instances[domains[14].RegistrarInstanceId]);
            Assert.AreEqual(DomainStatus.Cancelled, domains[14].Status);

            // El mismo nombre vivo dos veces no entra dos veces vivo: la copia repetida queda cancelada y avisada.
            Assert.AreEqual(DomainStatus.Cancelled, domains[16].Status);
            Assert.IsTrue(import.Incidents.Any(i => i.Contains("Dominio 16", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));

            // La renovación que vence en nextduedate ya está facturada e impaga: lo próximo a facturar es el período siguiente.
            Assert.AreEqual(new DateOnly(2027, 12, 1), domains[12].NextDueDate);
            Assert.AreEqual(new DateOnly(2026, 12, 1), domains[12].PaidThrough);
            Assert.AreEqual(18.99m, domains[12].RecurringAmount);
            Assert.AreEqual(Currency.Ars, domains[12].Currency);
            Assert.IsTrue(domains[12].DnsManagement);
            Assert.AreEqual(new DateOnly(2026, 12, 1), domains[12].ExpiresOn);
            Assert.IsNull(domains[10].ExpiresOn);
            Assert.IsFalse(domains[15].AutoRenew);
            Assert.AreEqual(new DateOnly(2027, 2, 1), domains[15].NextDueDate);

            var lines = await db.DocumentLines.Where(l => l.DocumentId == 7100).ToListAsync();
            Assert.IsTrue(lines.All(l => l.DomainId == 12));
            Assert.AreEqual(1, import.Figures[("domains_by_status", "Grace")].Count);
            Assert.AreEqual(2, import.Figures[("domains_by_instance", "subdominios-propios-manual")].Count);
            Assert.IsTrue(import.Incidents.Any(i => i.Contains("campos adicionales", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));

            // Precios por TLD: columnas de 1 a 10 años, -1 no se ofrece, las trienales en cero se ignoran y las huérfanas se descartan.
            var prices = await db.TldPrices.OrderBy(p => p.Years).ToListAsync();
            Assert.HasCount(2, prices);
            Assert.IsTrue(prices.All(p => p.Tld == ".com" && p.Currency == Currency.Usd && instances[p.RegistrarInstanceId] == "internetbs"));
            Assert.AreEqual((17.99m, 18.99m, 17.99m), (prices[0].Register!.Value, prices[0].Renew!.Value, prices[0].Transfer!.Value));
            Assert.AreEqual(34.99m, prices[1].Register);
            Assert.IsNull(prices[1].Renew);
            Assert.IsNull(prices[1].Transfer);
            Assert.IsTrue(import.Incidents.Any(i => i.Contains("sin extensión", StringComparison.Ordinal)));
            Assert.IsTrue(import.Incidents.Any(i => i.Contains("extensiones sin ningún precio", StringComparison.Ordinal)));

            // Las opciones del dominio: gestión de DNS, reenvío y protección en las tres primeras columnas. En cero son gratis y no hay fila.
            var options = (await db.DomainOptionPrices.ToListAsync()).Select(p => $"{p.Option} {p.Currency.Code} {p.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "dns ARS 159.00", "dns USD 0.10", "privacy USD 2.50" }, options);

            // Las marcas de cada extensión: .com no ofrece la protección y .cn ofrece todas, así que no tiene fila.
            var byTld = await db.TldOptions.ToListAsync();
            Assert.AreEqual((".com", true, true, false), byTld.Select(t => (t.Tld, t.Dns, t.EmailForwarding, t.Privacy)).Single());
        }
        finally
        {
            await TestDatabase.Drop(target);
        }
    }

    private static (string?, string?) Manual(Product product)
    {
        var parameters = JsonNode.Parse(product.ModuleParams)!;
        return (parameters["activation"]?.GetValue<string>(), parameters["lifecycle"]?.GetValue<string>());
    }
}
