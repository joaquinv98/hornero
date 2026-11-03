using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Importer.Import;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.Importer.Tests;

/// <summary>Importación de un origen sintético a una base de prueba propia (sin datos de clientes reales).</summary>
[TestClass]
public sealed class SourceImportTests
{
    private const string Dump = """
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `companyname` text, `email` text, `address1` text, `address2` text, `city` text, `state` text, `postcode` text, `country` text, `phonenumber` text, `tax_id` varchar(128), `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` enum('Active','Inactive','Closed'), `language` text);
        CREATE TABLE `tblcustomfields` (`id` int(11), `type` text, `relid` int(11), `fieldname` text, `fieldtype` text);
        CREATE TABLE `tblcustomfieldsvalues` (`id` int(10), `fieldid` int(11), `relid` int(11), `value` text);
        CREATE TABLE `tblproductgroups` (`id` int(11), `name` text);
        CREATE TABLE `tblproducts` (`id` int(11), `gid` int(11), `name` text, `hidden` tinyint(1), `retired` tinyint(1), `paytype` text, `servertype` text, `tax` tinyint(1), `configoption1` text, `autoterminatedays` int(11));
        CREATE TABLE `tblpricing` (`id` int(11), `type` text, `currency` int(11), `relid` int(11), `msetupfee` decimal(16,2), `qsetupfee` decimal(16,2), `ssetupfee` decimal(16,2), `asetupfee` decimal(16,2), `bsetupfee` decimal(16,2), `tsetupfee` decimal(16,2), `monthly` decimal(16,2), `quarterly` decimal(16,2), `semiannually` decimal(16,2), `annually` decimal(16,2), `biennially` decimal(16,2), `triennially` decimal(16,2));
        CREATE TABLE `tblhosting` (`id` int(11), `userid` int(11), `packageid` int(11), `regdate` date, `domain` text, `qty` int(11), `amount` decimal(16,2), `billingcycle` text, `nextduedate` date, `termination_date` date, `domainstatus` text, `username` text, `paymentmethod` text);
        CREATE TABLE `tblorders` (`id` int(11), `userid` int(11), `date` datetime, `invoiceid` int(10), `status` text);
        CREATE TABLE `tblinvoices` (`id` int(10), `userid` int(11), `invoicenum` text, `date` date, `duedate` date, `subtotal` decimal(16,2), `credit` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` text);
        CREATE TABLE `tblinvoiceitems` (`id` int(11), `invoiceid` int(10), `type` varchar(30), `relid` int(11), `description` text, `amount` decimal(16,2), `taxed` int(11), `duedate` date);
        CREATE TABLE `tblaccounts` (`id` int(11), `userid` int(11), `currency` int(11), `gateway` text, `date` datetime, `amountin` decimal(16,2), `fees` decimal(16,2), `amountout` decimal(16,2), `transid` text, `invoiceid` int(10));
        CREATE TABLE `mod_afip_invoices` (`id` int(10), `invoice_id` int(11), `client_id` int(11), `voucher_type` int(11), `punto_venta` int(11), `voucher_number` int(11), `cae` varchar(14), `cae_vto` date, `fecha_emision` date, `monto_total` decimal(14,2), `monto_neto` decimal(14,2), `monto_iva` decimal(14,2), `doc_tipo` int(11), `doc_nro` varchar(20), `condicion_iva_receptor_id` int(11), `cuit_emisor` varchar(11), `ws_service` varchar(10), `moneda` varchar(3), `cotizacion` decimal(12,6));
        CREATE TABLE `tbladdonmodules` (`id` int(10), `module` text, `setting` text, `value` text);
        CREATE TABLE `tblconfiguration` (`id` int(11), `setting` varchar(64), `value` mediumtext);
        CREATE TABLE `tblcancelrequests` (`id` int(11), `date` datetime, `relid` int(11), `reason` text, `type` text);
        INSERT INTO `tblcancelrequests` VALUES (1,'2026-11-20 10:00:00',900,'','End of Billing Period'),(2,'2025-06-01 10:00:00',901,'','Immediate');
        INSERT INTO `tblconfiguration` VALUES (1,'CreateInvoiceDaysBefore','7'),(2,'CreateInvoiceDaysBeforeMonthly','7'),(3,'CreateInvoiceDaysBeforeQuarterly','14'),(4,'CreateInvoiceDaysBeforeAnnually','30'),(5,'AutoSuspension','on'),(6,'AutoSuspensionDays','1'),(7,'AutoTermination','on'),(8,'AutoTerminationDays','14'),(9,'ContinuousInvoiceGeneration',''),(10,'OrderDaysGrace','0');
        INSERT INTO `tblcurrencies` VALUES (1,'USD',1),(3,'ARS',0);
        INSERT INTO `tbladdonmodules` VALUES (1,'otro','punto_venta','99'),(2,'afip','cuit','30-71111111-0'),(3,'afip','punto_venta','11'),(4,'afip','punto_venta_nc','11'),(5,'afip','punto_venta_export','12'),(6,'afip','punto_venta_export_nc','12'),(7,'afip','export_country_map','US=212|55000002126\nUY=225|55000000018\n'),(8,'afip','export_payment_currency_mode','paypal_ppcpv=S\nmercadopago=N'),(9,'afip','export_default_payment_mode','N'),(10,'afip','export_forma_pago','Pago Online'),(11,'afip','export_idioma_cbte','1'),(12,'afip','export_unidad_medida','7'),(13,'afip','fiscalization_trigger','on_created'),(14,'afip','export_currency_map','USD=DOL|auto'),(15,'afip','local_foreign_currency_mode','wsfe_foreign_currency'),(16,'afip','iva_condicion','Responsable Inscripto');
        INSERT INTO `tblclients` VALUES (501,'Ana','Prueba','','ana@example.com','Calle 1','Piso 2','Ciudad','Buenos Aires','1000','AR','111','',3,150.00,'2025-03-31','Active','spanish'),(502,'Bob','Exterior','Ejemplo SA','bob@example.com','','','','','','US','','',1,0.00,'2025-04-01','Inactive','english'),(503,'Ana','Repetida','','ANA@example.com','','','','','','AR','','',3,0.00,'2025-05-01','Inactive','spanish');
        INSERT INTO `tblcustomfields` VALUES (81,'client',0,'Condicion ante el IVA','dropdown'),(82,'client',0,'CUIT/CUIL/DNI','text');
        INSERT INTO `tblcustomfieldsvalues` VALUES (1,81,501,'Responsable Inscripto'),(2,82,501,'20-12345678-6'),(3,82,502,'12-3456789');
        INSERT INTO `tblproductgroups` VALUES (7,'Game servers');
        INSERT INTO `tblproducts` VALUES (40,7,'Minecraft 4 GB',0,0,'recurring','pterodactyl',1,'4096',2);
        INSERT INTO `tblpricing` VALUES (1,'product',3,40,0.00,-1.00,-1.00,0.00,-1.00,-1.00,12100.00,-1.00,-1.00,120000.00,-1.00,-1.00);
        INSERT INTO `tblhosting` VALUES (900,501,40,'2025-03-31','mc.example.com',1,12100.00,'Monthly','2026-11-30','0000-00-00','Active','u900','paypal_ppcpv'),(901,502,40,'2025-04-01','',1,12100.00,'Monthly','2025-06-01','2025-06-05','Terminated','','mercadopago_1');
        INSERT INTO `tblorders` VALUES (300,501,'2025-03-31 10:00:00',7000,'Active');
        INSERT INTO `tblinvoices` VALUES (7000,501,'7000','2025-03-31','2025-04-07',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Paid'),(7001,501,'7001','2026-10-31','2026-11-07',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(7002,502,'7002','2025-05-01','2025-05-08',10.00,0.00,0.00,0.00,10.00,0.000,'Cancelled');
        INSERT INTO `tblinvoiceitems` VALUES (1,7000,'Hosting',900,'Minecraft 4 GB',12100.00,1,'2025-03-31'),(2,7001,'Hosting',900,'Minecraft 4 GB',12100.00,1,'2026-11-30'),(3,7002,'Hosting',901,'Minecraft 4 GB',10.00,0,'2025-05-01');
        INSERT INTO `tblaccounts` VALUES (1,501,0,'mercadopago_1','2025-03-31 11:00:00',12100.00,500.00,0.00,'MP-1',7000),(2,501,0,'','2025-05-01 11:00:00',0.00,0.00,50.00,'',0);
        INSERT INTO `mod_afip_invoices` VALUES (1,7000,501,1,11,41,'71234567890123','2025-04-10','2025-03-31',12100.00,10000.00,2100.00,80,'20123456786',1,'30711111110','wsfe','PES',1.000000),(2,6999,501,8,11,9,'71234567890124','2025-04-11','2025-04-01',500.00,413.22,86.78,80,'20123456786',1,'30711111110','wsfe','PES',1.000000);
        """;

    [TestMethod]
    public Task ListasDeBloqueo_DominiosEIpsVigentes() => WithImport(Dump + "\n" + """
        CREATE TABLE `tblbannedemails` (`id` int(11), `domain` text, `count` int(11));
        CREATE TABLE `tblbannedips` (`id` int(11), `ip` text, `reason` text, `expires` datetime);
        INSERT INTO `tblbannedemails` VALUES (1,'Descartable.Example',3),(2,'no es un dominio',0),(3,'descartable.example',1);
        INSERT INTO `tblbannedips` VALUES (1,'203.0.113.9','Escaneo','2099-01-01 00:00:00'),(2,'198.51.100.7','Vieja','2020-01-01 00:00:00'),(3,'192.0.2.0/24','','0000-00-00 00:00:00');
        """, async (db, _) =>
    {
        var entries = await db.Set<BlockedEntry>().OrderBy(e => e.Value).ToListAsync();
        CollectionAssert.AreEqual(new[] { "192.0.2.0/24", "203.0.113.9/32", "descartable.example" }, entries.Select(e => e.Value).ToArray());
        Assert.AreEqual(BlockKind.Domain, entries[2].Kind);
        Assert.IsNull(entries[0].ExpiresAt, "Sin fecha queda permanente.");
        Assert.AreEqual("Escaneo", entries[1].Reason);
        Assert.AreEqual(new DateTimeOffset(2099, 1, 1, 3, 0, 0, TimeSpan.Zero), entries[1].ExpiresAt);
        Assert.IsTrue(entries.All(e => e.CreatedBy == "import" && e.Source == BlockSource.Staff));
    });

    [TestMethod]
    public Task PedidosMarcadosComoFraude_QuedanRechazados_YLosAjustesDeSeguridadSeInforman() => WithImport(Dump + "\n" + """
        INSERT INTO `tblorders` VALUES (301,501,'2025-04-01 10:00:00',0,'Fraud');
        INSERT INTO `tblconfiguration` VALUES (90,'InvalidLoginBanLength','20'),(91,'SkipFraudForExisting','on');
        """, async (db, import) =>
    {
        var check = await db.Set<FraudCheck>().SingleAsync();
        Assert.AreEqual(301, check.OrderId);
        Assert.AreEqual(ScreeningStatus.Rejected, check.Status);
        Assert.AreEqual("import", check.DecidedBy);
        Assert.AreEqual("import.fraud", check.ReasonList.Single().Code);
        Assert.AreEqual(OrderStatus.Cancelled, await db.Orders.Where(o => o.Id == 301).Select(o => o.Status).SingleAsync());
        Assert.AreEqual(
            """{"AccountMinutes":20,"StaffAccountMinutes":20,"IpMinutes":20,"FraudSkipExisting":true}""",
            import.SecuritySettingsReport().ToJsonString());
    });

    [TestMethod]
    public Task VinculosDeGoogle_ConSuUsuario_YElProveedorApagadoSinSecreto() => WithImport(Dump + "\n" + """
        CREATE TABLE `tblauthn_account_links` (`id` int(10), `provider` char(32), `remote_user_id` char(255), `user_id` int(11), `client_id` int(11), `contact_id` int(11), `metadata` text, `created_at` timestamp, `updated_at` timestamp);
        CREATE TABLE `tblauthn_config` (`id` int(10), `provider` char(64), `setting` char(128), `value` text, `created_at` timestamp, `updated_at` timestamp);
        INSERT INTO `tblauthn_account_links` VALUES (1,'google_signin','111111111111111111111',77,NULL,NULL,'{\"email\":\"ana@gmail.example\",\"name\":\"Ana\"}','2024-05-01 10:00:00',NULL),(2,'google_signin','222222222222222222222',78,NULL,NULL,'{}','2024-05-01 10:00:00',NULL),(3,'google_signin','333333333333333333333',77,NULL,NULL,'no es json','2024-06-01 10:00:00',NULL),(4,'otro','444',77,NULL,NULL,NULL,NULL,NULL);
        INSERT INTO `tblauthn_config` VALUES (1,'google_signin','ClientId','12345-abcdef.apps.googleusercontent.com',NULL,NULL),(2,'google_signin','ClientSecret','',NULL,NULL),(3,'google_signin','Enabled','on',NULL,NULL);
        """, async (db, import) =>
    {
        var provider = await db.Set<ExternalProvider>().SingleAsync();
        Assert.AreEqual(("google", "12345-abcdef.apps.googleusercontent.com", false), (provider.Key, provider.ClientId, provider.Enabled));
        Assert.IsNull(provider.ClientSecret, "El secreto no se importa: se carga en el panel.");
        var links = await db.Set<ExternalLogin>().OrderBy(l => l.Subject).ToListAsync();
        CollectionAssert.AreEqual(new[] { "111111111111111111111", "333333333333333333333" }, links.Select(l => l.Subject).ToArray());
        Assert.IsTrue(links.All(l => l.UserId == 77 && l.ProviderId == provider.Id));
        Assert.AreEqual("ana@gmail.example", links[0].Email);
        Assert.IsNull(links[1].Email, "Una metadata que no se entiende deja el vínculo sin correo.");
        Assert.AreEqual(new DateTimeOffset(2024, 5, 1, 13, 0, 0, TimeSpan.Zero), links[0].CreatedAt);
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 vínculos de Google sin su usuario", StringComparison.Ordinal)));
    }, prepare: async db =>
    {
        // Los usuarios se importan antes que los vínculos.
        var user = new PortalUser { Email = "ana@example.com", NormalizedEmail = "ANA@EXAMPLE.COM", SecurityStamp = "sello", CreatedAt = DateTimeOffset.UtcNow };
        user.SetImportedId(77);
        db.Add(user);
        await db.SaveChangesAsync();
    });

    [TestMethod]
    public Task ImportaConIdsMontosEstadosYComprobantes() => WithImport(Dump, async (db, import) =>
    {
        var ana = await db.Accounts.SingleAsync(a => a.Id == 501);
        Assert.AreEqual("Ana Prueba", ana.Name);
        Assert.AreEqual("ARS", ana.Currency.Code);
        // El correo repetido en el origen queda en la cuenta activa más vieja y la otra queda sin correo, con incidencia.
        Assert.AreEqual("ana@example.com", ana.Email);
        Assert.AreEqual(string.Empty, (await db.Accounts.SingleAsync(a => a.Id == 503)).Email);
        Assert.IsTrue(import.Incidents.Contains("Cuenta 503: tenía el mismo correo que la cuenta 501, queda sin correo hasta que el staff lo resuelva."), string.Join(" | ", import.Incidents));
        var identity = await db.TaxIdentities.SingleAsync(t => t.AccountId == 501);
        Assert.AreEqual(TaxDocumentType.Cuit, identity.DocumentType);
        Assert.AreEqual(1, identity.VatCondition);
        Assert.AreEqual("Calle 1, Piso 2", identity.Street);
        Assert.AreEqual("AR-B", identity.Province, "La provincia del origen se normaliza a ISO 3166-2.");
        var foreign = await db.TaxIdentities.SingleAsync(t => t.AccountId == 502);
        Assert.AreEqual(9, foreign.VatCondition);
        Assert.AreEqual(TaxDocumentType.Foreign, foreign.DocumentType);
        Assert.AreEqual("123456789", foreign.DocumentNumber);

        var active = await db.Services.SingleAsync(s => s.Id == 900);
        Assert.AreEqual(ServiceStatus.Active, active.Status);
        // La factura 7001 impaga ya cubre el período que empieza en nextduedate: el próximo a facturar es el siguiente.
        Assert.AreEqual(new DateOnly(2026, 12, 31), active.NextDueDate);
        Assert.AreEqual(31, active.AnchorDay);
        Assert.AreEqual(new DateOnly(2026, 11, 30), active.PaidThrough);
        Assert.AreEqual(2, (await db.Products.SingleAsync(p => p.Id == 40)).TerminationDays);
        Assert.AreEqual(
            """{"RenewalLeadDays":7,"RenewalLeadDaysByMonths":{"3":14,"12":30},"ContinuousInvoicing":false,"InvoiceDueDays":0,"DomainRenewalLeadDays":7,"GraceDays":1,"TerminationDays":14}""",
            import.BillingSettings().ToJsonString());
        Assert.AreEqual("paypal_ppcpv", active.PaymentMethod);
        Assert.IsNull((await db.Services.SingleAsync(s => s.Id == 901)).NextDueDate);

        var paid = await db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == 7000);
        Assert.AreEqual(InvoiceStatus.Paid, paid.Status);
        Assert.AreEqual(12100m, paid.Total);
        Assert.AreEqual(2100m, paid.Tax);
        Assert.AreEqual(900L, paid.Lines.Single().ServiceId);
        Assert.AreEqual(InvoiceStatus.Issued, (await db.Documents.SingleAsync(d => d.Id == 7001)).Status);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await db.Documents.SingleAsync(d => d.Id == 7002)).Status);

        Assert.AreEqual(1, await db.Payments.CountAsync());
        Assert.AreEqual(12100m, await db.Allocations.Where(a => a.DocumentId == 7000).SumAsync(a => a.Amount));
        Assert.AreEqual(150m, (await db.CreditBalances.SingleAsync(b => b.AccountId == 501)).Balance);

        Assert.AreEqual(2, await db.FiscalDocuments.CountAsync());
        Assert.IsTrue(await db.Documents.AnyAsync(d => d.Id == 6999 && d.Imported));
        Assert.AreEqual(41L, (await db.FiscalSeries.SingleAsync(s => s.PointOfSale == 11 && s.TypeCode == 1)).LastNumber);
        var issuer = await db.IssuerProfiles.SingleAsync();
        Assert.AreEqual("30711111110", issuer.TaxId);
        Assert.AreEqual(11, issuer.PointOfSale);
        Assert.AreEqual(12, issuer.ExportPointOfSale);
        Assert.AreEqual(EmissionMoment.AtIssue, issuer.Moment);
        Assert.IsFalse(issuer.Active);
        var settings = IssuerSettings.Parse(issuer.Settings);
        Assert.AreEqual(new ExportCountry(212, 55000002126), settings.ExportCountries!["US"]);
        Assert.AreEqual(2, settings.ExportCountries.Count);
        Assert.IsTrue(settings.SameCurrency("paypal_ppcpv"));
        Assert.IsFalse(settings.SameCurrency("mercadopago_1"));
        Assert.AreEqual("Pago Online", settings.ExportPaymentTerms);
        Assert.AreEqual("es", settings.ExportLanguage);
        Assert.AreEqual(7, settings.ExportUnitCode);
        Assert.IsFalse(import.Incidents.Any(i => i.StartsWith("Addon fiscal", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
        Assert.AreEqual(300L, (await db.Orders.SingleAsync()).Id);

        await using var cmd = new NpgsqlCommand("SELECT next FROM billing.counters WHERE kind = 'Invoice'", (NpgsqlConnection)db.Database.GetDbConnection());
        await db.Database.OpenConnectionAsync();
        Assert.AreEqual(7003L, (long)(await cmd.ExecuteScalarAsync())!);
    });

    /// <summary>Casos que la revisión encontró mal importados en la copia, con datos sintéticos.</summary>
    private const string Cases = """
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `companyname` text, `email` text, `address1` text, `address2` text, `city` text, `state` text, `postcode` text, `country` text, `phonenumber` text, `tax_id` varchar(128), `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` enum('Active','Inactive','Closed'), `language` text);
        CREATE TABLE `tblcustomfields` (`id` int(11), `type` text, `relid` int(11), `fieldname` text, `fieldtype` text);
        CREATE TABLE `tblcustomfieldsvalues` (`id` int(10), `fieldid` int(11), `relid` int(11), `value` text);
        CREATE TABLE `tblproductgroups` (`id` int(11), `name` text);
        CREATE TABLE `tblproducts` (`id` int(11), `gid` int(11), `name` text, `hidden` tinyint(1), `retired` tinyint(1), `paytype` text, `servertype` text, `tax` tinyint(1), `configoption1` text, `autoterminatedays` int(11));
        CREATE TABLE `tblpricing` (`id` int(11), `type` text, `currency` int(11), `relid` int(11), `msetupfee` decimal(16,2), `qsetupfee` decimal(16,2), `ssetupfee` decimal(16,2), `asetupfee` decimal(16,2), `bsetupfee` decimal(16,2), `tsetupfee` decimal(16,2), `monthly` decimal(16,2), `quarterly` decimal(16,2), `semiannually` decimal(16,2), `annually` decimal(16,2), `biennially` decimal(16,2), `triennially` decimal(16,2));
        CREATE TABLE `tblhosting` (`id` int(11), `userid` int(11), `packageid` int(11), `regdate` date, `domain` text, `qty` int(11), `amount` decimal(16,2), `billingcycle` text, `nextduedate` date, `nextinvoicedate` date, `termination_date` date, `domainstatus` text, `username` text, `paymentmethod` text, `server` int(11), `dedicatedip` text, `suspendreason` text, `overideautosuspend` tinyint(1), `overidesuspenduntil` date);
        CREATE TABLE `tblhostingconfigoptions` (`id` int(11), `relid` int(11), `configid` int(11), `optionid` int(11), `qty` int(11));
        CREATE TABLE `tblproductconfigoptions` (`id` int(11), `gid` int(11), `optionname` text, `optiontype` text);
        CREATE TABLE `tblproductconfiggroups` (`id` int(11), `name` text, `description` text);
        CREATE TABLE `tblproductconfiglinks` (`id` int(11), `gid` int(11), `pid` int(11));
        CREATE TABLE `tblproductconfigoptionssub` (`id` int(11), `configid` int(11), `optionname` text);
        CREATE TABLE `tblcancelrequests` (`id` int(11), `date` datetime, `relid` int(11), `reason` text, `type` text);
        CREATE TABLE `tblinvoices` (`id` int(10), `userid` int(11), `invoicenum` text, `date` date, `duedate` date, `subtotal` decimal(16,2), `credit` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` text);
        CREATE TABLE `tblinvoiceitems` (`id` int(11), `invoiceid` int(10), `type` varchar(30), `relid` int(11), `description` text, `amount` decimal(16,2), `taxed` int(11), `duedate` date);
        CREATE TABLE `tblaccounts` (`id` int(11), `userid` int(11), `currency` int(11), `gateway` text, `date` datetime, `amountin` decimal(16,2), `fees` decimal(16,2), `amountout` decimal(16,2), `transid` text, `invoiceid` int(10), `refundid` int(11), `type` varchar(64), `billingnoteid` int(10));
        CREATE TABLE `tblbillingnotes` (`id` int(10), `note_type` enum('credit','debit'), `custom_number` varchar(255), `client_id` int(10), `date_issued` timestamp, `subtotal` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` enum('draft','issued','closed'));
        CREATE TABLE `tblbillingnoteitems` (`id` int(10), `billingnote_id` int(10), `type` varchar(32), `relid` int(11), `description` text, `amount` decimal(16,2), `taxed` tinyint(4));
        CREATE TABLE `mod_afip_invoices` (`id` int(10), `invoice_id` int(11), `client_id` int(11), `voucher_type` int(11), `punto_venta` int(11), `voucher_number` int(11), `cae` varchar(14), `cae_vto` date, `fecha_emision` date, `monto_total` decimal(14,2), `monto_neto` decimal(14,2), `monto_iva` decimal(14,2), `doc_tipo` int(11), `doc_nro` varchar(20), `condicion_iva_receptor_id` int(11), `cuit_emisor` varchar(11), `ws_service` varchar(10), `moneda` varchar(3), `cotizacion` decimal(12,6), `native_note_id` int(10));
        CREATE TABLE `tbldomains` (`id` int(10), `userid` int(10), `status` text, `recurringamount` decimal(16,2), `nextduedate` date);
        INSERT INTO `tblcurrencies` VALUES (1,'USD',1),(3,'ARS',0);
        INSERT INTO `tblclients` VALUES (601,'Dan','Dolar','','dan@example.com','','','','','','US','','',1,0.00,'2025-01-01','Active','english'),(602,'Eva','Pesos','','eva@example.com','','','','','','AR','','',3,0.00,'2025-01-01','Active','spanish');
        INSERT INTO `tblcustomfields` VALUES (149,'client',0,'Sin comprobante fiscal|Cortesia','tickbox'),(81,'client',0,'Condicion ante el IVA','dropdown'),(70,'product',50,'server_id|ID del server','text'),(71,'product',50,'PASSWORD|Clave','password');
        INSERT INTO `tblcustomfieldsvalues` VALUES (1,149,602,'on'),(2,70,910,'77'),(3,71,910,'no-va'),(4,81,601,'Consumidor Final');
        INSERT INTO `tblproductgroups` VALUES (7,'Game servers');
        INSERT INTO `tblproducts` VALUES (50,7,'Minecraft',0,0,'recurring','pterodactyl',1,'4096',14),(51,7,'Abono',0,0,'recurring','modulo_propio',1,'',0),(52,7,'Hosting',0,0,'recurring','vesta',1,'gainsboro',0);
        INSERT INTO `tblpricing` VALUES (1,'product',3,50,0.00,-1.00,-1.00,-1.00,-1.00,-1.00,0.00,-1.00,-1.00,0.00,-1.00,-1.00);
        INSERT INTO `tblhosting` VALUES (910,602,50,'2026-09-14','',1,100.00,'Monthly','2026-11-16','2026-11-16','0000-00-00','Active','','mercadopago_1',1,'10.20.30.5:25565','',0,'0000-00-00'),(911,602,50,'2025-01-10','',1,100.00,'Monthly','2025-03-10','2025-03-10','2025-03-15','Terminated','u911','mercadopago_1',1,'','',0,'0000-00-00'),(912,602,50,'2026-03-29','',1,100.00,'Monthly','2026-11-01','2026-11-01','0000-00-00','Active','','mercadopago_1',1,'10.20.30.6:25565','',0,'0000-00-00'),(913,602,51,'2026-01-05','',1,175.00,'Monthly','2026-11-05','2026-11-05','0000-00-00','Active','','mercadopago_1',0,'','',1,'0000-00-00'),(914,602,52,'2026-01-05','web.example.com',1,50.00,'Monthly','2026-11-05','2026-11-05','0000-00-00','Active','web1','mercadopago_1',2,'','',0,'0000-00-00'),(915,602,50,'2026-01-20','',1,100.00,'Monthly','2026-11-20','2026-11-20','0000-00-00','Active','','mercadopago_1',1,'','',0,'0000-00-00'),(916,602,50,'2026-03-01','',1,100.00,'Monthly','2026-08-01','2026-08-01','0000-00-00','Suspended','','mercadopago_1',1,'','Pedido interno',0,'0000-00-00');
        INSERT INTO `tblhostingconfigoptions` VALUES (1,910,5,51,0),(2,911,5,51,0);
        INSERT INTO `tblproductconfigoptions` VALUES (5,3,'Slots|Jugadores','1');
        INSERT INTO `tblproductconfiggroups` VALUES (3,'Minecraft','');
        INSERT INTO `tblproductconfiglinks` VALUES (1,3,50);
        INSERT INTO `tblproductconfigoptionssub` VALUES (51,5,'20 slots');
        INSERT INTO `tblcancelrequests` VALUES (1,'2026-10-20 10:00:00',910,'','End of Billing Period'),(2,'2026-10-21 10:00:00',915,'','Immediate'),(3,'2025-03-01 10:00:00',911,'','End of Billing Period');
        INSERT INTO `tblinvoices` VALUES (8000,601,'8000','2026-09-01','2026-09-08',10.55,0.00,0.00,0.00,10.55,0.000,'Paid'),(8001,601,'8001','2026-10-18','2026-10-25',3.00,0.00,0.00,0.00,3.00,0.000,'Unpaid'),(8002,602,'8002','2026-09-01','2026-09-08',1000.00,0.00,210.00,0.00,1210.00,21.000,'Paid'),(8003,602,'8003','2026-09-02','2026-09-02',0.00,0.00,0.00,0.00,0.00,0.000,'Paid'),(8004,602,'8004','2026-09-03','2026-09-03',0.00,0.00,0.00,0.00,0.00,0.000,'Cancelled'),(8005,602,'8005','2026-09-04','2026-09-04',500.00,0.00,0.00,0.00,500.00,0.000,'Refunded'),(8006,602,'8006','2026-10-25','2026-11-01',100.00,0.00,0.00,0.00,100.00,0.000,'Unpaid'),(8007,602,'8007','2026-10-26','2026-10-26',37.00,0.00,0.00,0.00,37.00,0.000,'Draft'),(8008,601,'8008','2025-02-28','2025-03-07',28.00,0.00,0.00,0.00,28.00,0.000,'Collections'),(8009,602,'8009','2026-10-10','2026-10-17',100.00,0.00,0.00,0.00,100.00,0.000,'Unpaid'),(8011,602,'8011','2026-10-01','2026-10-01',300.00,300.00,0.00,0.00,0.00,0.000,'Paid'),(8012,602,'8012','2026-10-29','2026-11-05',175.00,0.00,0.00,0.00,175.00,0.000,'Paid'),(8013,602,'8013','2026-10-11','2026-10-18',1000.00,0.00,0.00,0.00,1000.00,0.000,'Unpaid');
        INSERT INTO `tblinvoiceitems` VALUES (1,8000,'Hosting',0,'Servicio',10.55,0,'2026-09-01'),(2,8001,'Hosting',0,'Hosting',3.00,1,'2026-10-25'),(3,8002,'Hosting',910,'Minecraft',1000.00,0,'2026-09-16'),(4,8005,'Hosting',0,'Servicio',500.00,0,'2026-09-04'),(5,8006,'Hosting',912,'Minecraft',100.00,0,'2026-11-01'),(6,8007,'Hosting',0,'Borrador',37.00,0,'2026-10-26'),(7,8009,'Hosting',0,'Servicio',100.00,0,'2026-10-17'),(8,8011,'Hosting',0,'Servicio',300.00,0,'2026-10-01'),(9,8012,'Hosting',913,'Abono',175.00,0,'2026-11-05'),(10,8013,'Hosting',0,'Servicio',1000.00,0,'2026-10-18');
        INSERT INTO `tblaccounts` VALUES (1,601,0,'mercadopago_1','2026-09-01 10:00:00',12340.00,600.00,0.00,'MP-9',8000,0,'',0),(2,602,0,'mercadopago_1','2026-09-01 10:00:00',1210.00,0.00,0.00,'MP-10',8002,0,'',0),(3,602,0,'mercadopago_1','2026-09-04 10:00:00',500.00,0.00,0.00,'MP-11',8005,0,'',0),(4,601,0,'paypal_ppcpv','2025-02-28 10:00:00',28.00,1.00,0.00,'PP-1',8008,0,'',0),(5,601,0,'paypal_ppcpv','2025-03-28 10:00:00',0.00,0.00,28.00,'PP-1R',8008,4,'',0),(6,602,0,'','2026-09-05 10:00:00',500.00,0.00,0.00,'',8005,0,'invoice_billing_adjustment_credit',1),(7,602,0,'','2026-10-10 10:00:00',40.00,0.00,0.00,'',8009,0,'invoice_credit_applied',4),(8,602,0,'mercadopago_1','2026-09-06 10:00:00',0.00,0.00,500.00,'MP-11R',8005,3,'',0),(9,602,0,'mercadopago_1','2026-09-02 10:00:00',100.00,0.00,0.00,'MP-12',8002,0,'',0),(10,602,0,'','2026-10-12 10:00:00',500.00,0.00,0.00,'',8013,0,'invoice_credit_applied',5),(11,602,0,'','2026-10-13 10:00:00',0.00,0.00,500.00,'',8013,0,'invoice_credit_removed',6),(12,602,0,'mercadopago_1','2026-10-14 10:00:00',500.00,0.00,0.00,'MP-13',8013,0,'',0);
        INSERT INTO `tblbillingnotes` VALUES (1,'credit','',602,'2026-09-05 10:00:00',500.00,0.00,0.00,500.00,0.000,'closed'),(2,'credit','',602,'2026-09-20 10:00:00',200.00,42.00,0.00,242.00,21.000,'closed'),(3,'debit','',602,'2026-10-02 10:00:00',50.00,0.00,0.00,50.00,0.000,'closed'),(4,'credit','',602,'2026-10-10 10:00:00',40.00,0.00,0.00,40.00,0.000,'closed'),(5,'credit','',602,'2026-10-12 10:00:00',500.00,0.00,0.00,500.00,0.000,'closed'),(6,'debit','',602,'2026-10-13 10:00:00',500.00,0.00,0.00,500.00,0.000,'closed');
        INSERT INTO `tblbillingnoteitems` VALUES (1,1,'invoice_item',4,'Servicio',500.00,0),(2,2,'invoice_item',3,'Minecraft',242.00,1),(3,3,'other',0,'Cargo',50.00,0),(4,4,'other',0,'Saldo aplicado',40.00,0),(5,5,'other',0,'Saldo aplicado',500.00,0),(6,6,'other',0,'Saldo quitado',500.00,0);
        INSERT INTO `mod_afip_invoices` VALUES (1,8001,601,19,12,5,'71234567890125','2026-10-28','2026-10-18',3.00,3.00,0.00,80,'55000002126',9,'30711111110','wsfex','DOL',1.000000,0),(2,8002,602,6,11,50,'71234567890126','2026-09-11','2026-09-01',1210.00,1000.00,210.00,99,'0',5,'30711111110','wsfe','PES',1.000000,0),(3,8002,602,8,11,7,'71234567890127','2026-09-30','2026-09-20',242.00,200.00,42.00,99,'0',5,'30711111110','wsfe','PES',1.000000,2);
        INSERT INTO `tbldomains` VALUES (1,602,'Active',18000.00,'2027-03-01'),(2,602,'Cancelled',0.00,'2024-01-01'),(3,602,'Active',1210.00,'2026-08-01'),(4,602,'Grace',1210.00,'2026-10-20');
        """;

    private const string Modules = """
        {
          "pterodactyl": {
            "instance": "juegos",
            "params": { "memory_mb": "entero:configoption1", "oom_disabled": "booleano:configoption1", "egg": "configoption1" },
            "options": { "Slots": "variables.SERVER_SLOTS" },
            "external_ids": { "address": "dedicatedip", "server_id": "campo:70", "clave": "campo:PASSWORD" }
          },
          "modulo_propio": { "manual": true }
        }
        """;

    [TestMethod]
    public Task AnclaDesdeElVencimientoVigente() => WithImport(Cases, async (db, import) =>
    {
        // El origen factura el día de nextduedate aunque el alta haya sido otro día.
        var service = await db.Services.SingleAsync(s => s.Id == 910);
        Assert.AreEqual(16, service.AnchorDay);
        Assert.AreEqual(new Period(new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 16)), service.TakeNextPeriod());

        // Alta un 29 y vencimiento el 1 con el período del 1 ya facturado: el próximo a facturar es el 1 siguiente.
        var shifted = await db.Services.SingleAsync(s => s.Id == 912);
        Assert.AreEqual(1, shifted.AnchorDay);
        Assert.AreEqual(new DateOnly(2026, 12, 1), shifted.NextDueDate);
        Assert.AreEqual(new DateOnly(2026, 11, 1), shifted.PaidThrough);

        // Agregados para la conciliación: 2 vivos con alta en otro día que el vencimiento, ninguno anclado fuera de él.
        Assert.AreEqual(2L, import.Figures[("services_anchor", "alta_en_otro_dia")].Count);
        Assert.AreEqual(0L, import.Figures[("services_anchor", "fuera_del_vencimiento")].Count);
    });

    [TestMethod]
    public async Task ConciliacionMarcaElAnclajeFueraDelVencimiento()
    {
        var import = new SourceImport(null!, TextWriter.Null);
        import.Figures[("services_anchor", "alta_en_otro_dia")] = (65, 0m);
        import.Figures[("services_anchor", "fuera_del_vencimiento")] = (3, 0m);
        var baseline = Path.Combine(Path.GetTempPath(), $"linea-base-{Guid.NewGuid():N}.tsv");
        await File.WriteAllLinesAsync(baseline, ["# section\tkeys...\tvalues..."]);
        try
        {
            using var output = new StringWriter();
            Assert.AreEqual(1, await Reconciliation.Compare(import, baseline, output), output.ToString());
            Assert.Contains("65 con alta en otro día", output.ToString());
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [TestMethod]
    public Task CuentaSinComprobanteFiscal() => WithImport(Cases, async (db, import) =>
    {
        Assert.IsTrue((await db.Accounts.SingleAsync(a => a.Id == 602)).NoFiscalDocument);
        Assert.IsFalse((await db.Accounts.SingleAsync(a => a.Id == 601)).NoFiscalDocument);
        Assert.AreEqual(1L, import.Figures[("customfield_values", "149\ton")].Count);
    });

    [TestMethod]
    public Task PagoEnPesosDeFacturaEnDolares() => WithImport(Cases, async (db, import) =>
    {
        var payment = await db.Payments.SingleAsync(p => p.ExternalId == "MP-9");
        Assert.AreEqual(Currency.Ars, payment.Currency);
        Assert.AreEqual(12340m, payment.Amount);
        Assert.AreEqual(600m, payment.Fee);
        var allocation = await db.Allocations.SingleAsync(a => a.DocumentId == 8000);
        Assert.AreEqual(10.55m, allocation.Amount);
        Assert.AreEqual(Math.Round(10.55m / 12340m, 8), allocation.Rate);

        // La línea base agrupa por la moneda del origen (la de la cuenta, con currency en 0): esa cifra no cambia.
        Assert.AreEqual((3L, 12340m + 28m), import.Figures[("payments_by_currency", "USD")]);
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("en pesos", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
    });

    [TestMethod]
    public Task LineasConElTratamientoDeLaCabecera() => WithImport(Cases, async (db, import) =>
    {
        var e = await db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == 8001);
        Assert.IsTrue(e.Lines.All(l => l.Treatment == TaxTreatment.Exempt), "La factura E es exenta y sus líneas también.");
        var b = await db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == 8002);
        Assert.IsTrue(b.Lines.All(l => l.Treatment == TaxTreatment.Taxed && l.Rate == 0.21m), "El comprobante tiene IVA y las líneas también.");

        // Una nota armada con las líneas de cualquier importada cierra contra la cabecera.
        foreach (var doc in await db.Documents.Include(d => d.Lines).Where(d => d.Imported && d.Total > 0).ToListAsync())
        {
            var computed = doc.Compute();
            var issued = doc.IssuedBreakdown();
            Assert.AreEqual(issued.Exempt, computed.Exempt, $"exento de {doc.Id}");
            Assert.AreEqual(issued.NotTaxed, computed.NotTaxed, $"no gravado de {doc.Id}");
            CollectionAssert.AreEqual(issued.Aliquots.ToList(), computed.Aliquots.ToList(), $"alícuotas de {doc.Id}");
            CreditNoteRules.Apply(issued, [], computed);
        }

        Assert.IsFalse(import.Incidents.Any(i => i.Contains("no dan los importes", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
    });

    [TestMethod]
    public Task EstadosQueLosImportesNoDicen() => WithImport(Cases, async (db, import) =>
    {
        Assert.AreEqual(InvoiceStatus.Paid, (await db.Documents.SingleAsync(d => d.Id == 8003)).Status);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await db.Documents.SingleAsync(d => d.Id == 8004)).Status);
        var refunded = await db.Documents.SingleAsync(d => d.Id == 8005);
        Assert.AreEqual(InvoiceStatus.Cancelled, refunded.Status);
        Assert.AreEqual("Refunded", refunded.SourceStatus);

        // Cobrada y después revertida: en el origen sigue debiéndose.
        Assert.AreEqual(InvoiceStatus.Issued, (await db.Documents.SingleAsync(d => d.Id == 8008)).Status);
        Assert.IsFalse(import.Incidents.Any(i => i.Contains("estado derivado", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("borrador", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
        Assert.AreEqual(4, await db.Domains.CountAsync());

        // Dominios que el origen tiene vivos con el vencimiento pasado: la primera corrida da por vencido el 3, que pasó
        // la gracia, y el 4 sigue en gracia. El staff lo ve antes.
        var domains = import.Incidents.Single(i => i.Contains("dominios vivos con el vencimiento pasado", StringComparison.Ordinal));
        Assert.Contains("2 dominios", domains);
        Assert.Contains("1 pasaron la gracia", domains);
        Assert.Contains("(dominios 3, 4)", domains);
    });

    [TestMethod]
    public Task MovimientosQueNoSonCobros() => WithImport(Cases, async (db, import) =>
    {
        // La contrapartida de una nota y el saldo aplicado no son plata que entró.
        Assert.IsFalse(await db.Payments.AnyAsync(p => p.ExternalId == "tx-6" || p.ExternalId == "tx-7"));
        var applied = await db.Allocations.SingleAsync(a => a.DocumentId == 8009);
        Assert.IsNotNull(applied.CreditEntryId);
        Assert.AreEqual(40m, applied.Amount);
        Assert.AreEqual(40m, (await db.Documents.SingleAsync(d => d.Id == 8009)).Allocated);

        // El saldo aplicado que la factura trae en su cabecera también es una imputación de saldo.
        var fromHeader = await db.Allocations.SingleAsync(a => a.DocumentId == 8011);
        Assert.IsNotNull(fromHeader.CreditEntryId);
        Assert.AreEqual(300m, fromHeader.Amount);

        // El libro de saldo suma lo mismo que el saldo de la cuenta, que en el origen ya está neto de lo aplicado.
        Assert.AreEqual(0m, await db.CreditEntries.Where(e => e.AccountId == 602).SumAsync(e => e.Amount));
        Assert.AreEqual(1L, import.Figures[("payments_by_type", "invoice_billing_adjustment_credit")].Count);
        Assert.AreEqual((2L, 540m), import.Figures[("payments_by_type", "invoice_credit_applied")]);
    });

    [TestMethod]
    public Task ImputacionesQueCierranConLaCabecera() => WithImport(Cases, async (db, import) =>
    {
        // Pago con salida por el mismo importe: reembolsado, sin imputación.
        var refunded = await db.Payments.SingleAsync(p => p.ExternalId == "MP-11");
        Assert.AreEqual(PaymentStatus.Refunded, refunded.Status);
        Assert.AreEqual(0m, refunded.Applied);
        Assert.IsFalse(await db.Allocations.AnyAsync(a => a.DocumentId == 8005 || a.DocumentId == 8008));

        // Lo que excede lo cobrado de la factura queda sin aplicar.
        Assert.AreEqual(0m, (await db.Payments.SingleAsync(p => p.ExternalId == "MP-12")).Applied);
        Assert.AreEqual(968m, await db.Allocations.Where(a => a.DocumentId == 8002).SumAsync(a => a.Amount));

        var rows = await db.Allocations.GroupBy(a => a.DocumentId).Select(g => new { Id = g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Id, x => x.Sum);
        foreach (var doc in await db.Documents.Where(d => d.Imported).ToListAsync())
        {
            Assert.IsLessThanOrEqualTo(doc.Allocated, rows.GetValueOrDefault(doc.Id), $"imputaciones de {doc.Id}");
        }

        Assert.AreEqual(0L, import.Figures[("documents_control", "imputaciones_sobre_cabecera")].Count);
    });

    [TestMethod]
    public Task NotasNativasComoDocumentos() => WithImport(Cases, async (db, import) =>
    {
        var notes = await db.Documents.Where(d => d.Kind == DocumentKind.CreditNote).OrderBy(d => d.Number).ToListAsync();
        Assert.HasCount(2, notes);
        Assert.AreEqual(8005L, notes[0].RelatedDocumentId);
        Assert.AreEqual(500m, notes[0].Total);
        Assert.AreEqual(8002L, notes[1].RelatedDocumentId);
        Assert.AreEqual(42m, notes[1].Tax);
        var debit = await db.Documents.SingleAsync(d => d.Kind == DocumentKind.DebitNote);
        Assert.AreEqual(50m, debit.Total);
        Assert.IsNull(debit.RelatedDocumentId);

        // La nota parcial acredita la factura pagada, que sigue pagada.
        var invoice = await db.Documents.SingleAsync(d => d.Id == 8002);
        Assert.AreEqual(242m, invoice.Credited);
        Assert.AreEqual(InvoiceStatus.Paid, invoice.Status);

        // El comprobante de la nota cuelga de la nota, no de la factura.
        Assert.AreEqual(notes[1].Id, (await db.FiscalDocuments.SingleAsync(f => f.TypeCode == 8)).DocumentId);
        Assert.AreEqual((2L, 742m), import.Figures[("billing_notes_by_type_currency", "credit\tARS")]);

        await db.Database.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT kind || ':' || next FROM billing.counters WHERE kind <> 'Invoice' ORDER BY kind", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await cmd.ExecuteReaderAsync();
        var counters = new List<string>();
        while (await reader.ReadAsync())
        {
            counters.Add(reader.GetString(0));
        }

        // Las notas de saldo no son documentos, pero sus números quedaron usados en el origen: la numeración sigue arriba.
        CollectionAssert.AreEqual(new[] { "CreditNote:6", "DebitNote:7" }, counters);
    });

    [TestMethod]
    public Task NotasDeMovimientosDeSaldo_NoSonDocumentos() => WithImport(Cases, async (db, import) =>
    {
        // El origen abre una nota de crédito al aplicar saldo a una factura y una de débito al quitarlo. No son
        // documentos: el saldo entra como imputación, y esa nota de débito sería una deuda que el cliente no tiene.
        Assert.AreEqual(2, await db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote));
        Assert.AreEqual(50m, (await db.Documents.SingleAsync(d => d.Kind == DocumentKind.DebitNote)).Total);
        Assert.AreEqual((2L, 540m), import.Figures[("billing_notes_by_type_currency", "credit:invoice_credit_applied\tARS")]);
        Assert.AreEqual((1L, 500m), import.Figures[("billing_notes_by_type_currency", "debit:invoice_credit_removed\tARS")]);

        // El saldo que se aplicó y después se quitó de 8013 no es imputación ni plata que salió: el pago posterior queda
        // imputado y la factura pagada en parte, como en el origen.
        var payment = await db.Payments.SingleAsync(p => p.ExternalId == "MP-13");
        Assert.AreEqual(PaymentStatus.Approved, payment.Status);
        Assert.AreEqual(500m, payment.Applied);
        var invoice = await db.Documents.SingleAsync(d => d.Id == 8013);
        Assert.AreEqual(500m, invoice.Allocated);
        Assert.AreEqual(InvoiceStatus.PartiallyPaid, invoice.Status);
        Assert.IsFalse(await db.Allocations.AnyAsync(a => a.DocumentId == 8013 && a.CreditEntryId != null));
    });

    [TestMethod]
    public Task BajasPedidasSuspensionesYExterior() => WithImport(Cases, async (db, import) =>
    {
        var all = string.Join(" | ", import.Incidents);

        // Baja pedida a fin de período: sigue activo hasta su vencimiento y ese día se da de baja.
        var scheduled = await db.Services.SingleAsync(s => s.Id == 910);
        Assert.AreEqual(ServiceStatus.Active, scheduled.Status);
        Assert.AreEqual(new DateOnly(2026, 11, 16), scheduled.CancelAt);
        Assert.AreEqual(1L, import.Figures[("cancel_requests_pending_by_type", "End of Billing Period")].Count);
        Assert.AreEqual(1L, import.Figures[("cancel_requests_pending_by_type", "Immediate")].Count);

        // Baja inmediata que el origen no ejecutó: el servidor sigue andando, así que queda activo, sin renovarse desde
        // el pedido y con una tarea de revisión del staff, nunca dado de baja en silencio.
        var immediate = await db.Services.SingleAsync(s => s.Id == 915);
        Assert.AreEqual(ServiceStatus.Active, immediate.Status);
        Assert.AreEqual(new DateOnly(2026, 10, 21), immediate.CancelAt);

        // Suspensión con motivo propio y servicio exento de la suspensión automática.
        Assert.AreEqual("Pedido interno", (await db.Services.SingleAsync(s => s.Id == 916)).SuspendReason);
        Assert.IsTrue((await db.Services.SingleAsync(s => s.Id == 913)).NoAutoSuspend);

        // Las dos bajas que vencían en la copia y el origen no hizo quedan retenidas, cada una en su revisión.
        var reviews = await db.StaffTasks.Where(t => t.Kind == StaffTask.Review).OrderBy(t => t.Subject).ToListAsync();
        CollectionAssert.AreEqual(new[] { "review:service:915", "review:service:916" }, reviews.Select(t => t.Subject).ToList());
        Assert.IsTrue(reviews.All(t => t.Status == StaffTaskStatus.Open && t.Operation == "terminate"));
        Assert.Contains("Baja inmediata pedida el 2026-10-21", reviews[0].Details);
        Assert.Contains("Baja por mora que vencía el 2026-08-15", reviews[1].Details);
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("retenidas", StringComparison.Ordinal) && i.Contains("915, 916", StringComparison.Ordinal)), all);

        // Un renglón pagado en el vencimiento no cuenta como período facturado sin cobrar.
        Assert.AreEqual(new DateOnly(2026, 11, 5), (await db.Services.SingleAsync(s => s.Id == 913)).NextDueDate);
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("renglón pagado", StringComparison.Ordinal)), all);

        // Del exterior la condición es siempre 9, aunque el campo diga otra cosa.
        Assert.AreEqual(9, (await db.TaxIdentities.SingleAsync(t => t.AccountId == 601)).VatCondition);
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("del exterior", StringComparison.Ordinal)), all);
    });

    [TestMethod]
    public Task OpcionesElegidasEnLosParametros() => WithImport(Cases, async (db, import) =>
    {
        // Lo elegido queda en el servicio, y en los vivos también en los parámetros del módulo, en la clave de la opción.
        var live = await db.Services.SingleAsync(s => s.Id == 910);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"Slots":"20 slots"}"""), JsonNode.Parse(live.ModuleParams)), live.ModuleParams);
        Assert.AreEqual("{}", (await db.Services.SingleAsync(s => s.Id == 911)).ModuleParams);
        var chosen = await db.ServiceOptions.Where(o => o.ServiceId == 910 || o.ServiceId == 911).OrderBy(o => o.ServiceId).ToListAsync();
        CollectionAssert.AreEqual(new[] { (910L, 5L, 51L, 1), (911L, 5L, 51L, 1) }, chosen.Select(o => (o.ServiceId, o.OptionId, o.ValueId, o.Quantity)).ToArray());

        // El producto vale 0 en todos sus ciclos, pero su precio sale de las opciones: no es incidencia.
        Assert.IsFalse(import.Incidents.Any(i => i.Contains("precio 0", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
    });

    [TestMethod]
    public async Task ConciliacionConCifrasPropias()
    {
        var import = new SourceImport(null!, TextWriter.Null);
        import.Figures[("payments_by_type", "manual")] = (1, 10m);
        import.Figures[("cancel_requests_pending_by_type", "Immediate")] = (2, 0m);
        import.Figures[("documents_control", "imputaciones_sobre_cabecera")] = (1, 0m);
        var baseline = Path.Combine(Path.GetTempPath(), $"linea-base-{Guid.NewGuid():N}.tsv");
        await File.WriteAllLinesAsync(baseline, ["payments_by_type(count,amountin)\tmanual\t1\t10.00"]);
        try
        {
            using var output = new StringWriter();
            var diffs = await Reconciliation.Compare(import, baseline, output);
            var text = output.ToString();

            // La cifra con línea base se compara, la que no tiene se muestra, y el control por documento cuenta.
            Assert.AreEqual(1, diffs, text);
            Assert.Contains("OK   pagos por tipo 'manual'", text);
            Assert.Contains("INFO cancel_requests_pending_by_type Immediate: 2", text);
            Assert.Contains("DIF  documentos con imputaciones por encima de su cabecera", text);
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [TestMethod]
    public Task ServiciosAtadosASuInstanciaDeModulo()
    {
        long instanceId = 0;
        return WithImport(
            Cases,
            async (db, import) =>
            {
                var live = await db.Services.SingleAsync(s => s.Id == 910);
                Assert.AreEqual(instanceId, live.ModuleInstanceId);
                Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"address":"10.20.30.5:25565","server_id":"77"}"""), JsonNode.Parse(live.ExternalIds)), live.ExternalIds);
                Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"memory_mb":4096,"oom_disabled":false,"egg":"4096","variables":{"SERVER_SLOTS":"20 slots"}}"""), JsonNode.Parse(live.ModuleParams)), live.ModuleParams);
                Assert.AreEqual("variables.SERVER_SLOTS", (await db.Options.SingleAsync(o => o.Id == 5)).ModuleKey);
                var product = await db.Products.SingleAsync(p => p.Id == 50);
                Assert.AreEqual(instanceId, product.ModuleInstanceId);
                Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"memory_mb":4096,"oom_disabled":false,"egg":"4096"}"""), JsonNode.Parse(product.ModuleParams)), product.ModuleParams);

                var terminated = await db.Services.SingleAsync(s => s.Id == 911);
                Assert.IsNull(terminated.ModuleInstanceId);
                Assert.AreEqual("{}", terminated.ExternalIds);

                Assert.AreEqual(ProductKind.Manual, (await db.Products.SingleAsync(p => p.Id == 51)).Kind);
                var manual = await db.ModuleInstances.SingleAsync(i => i.Name == "servicios-manuales");
                Assert.AreEqual(manual.Id, (await db.Services.SingleAsync(s => s.Id == 913)).ModuleInstanceId);
                Assert.IsNull((await db.Services.SingleAsync(s => s.Id == 914)).ModuleInstanceId);

                var all = string.Join(" | ", import.Incidents);
                Assert.IsTrue(import.Incidents.Any(i => i.Contains("vesta", StringComparison.Ordinal) && i.Contains("sin instancia", StringComparison.Ordinal)), all);
                Assert.IsTrue(import.Incidents.Any(i => i.Contains("PASSWORD", StringComparison.Ordinal)), all);
                Assert.IsFalse(import.Incidents.Any(i => i.Contains("modulo_propio", StringComparison.Ordinal)), all);
            },
            prepare: async db =>
            {
                var installation = new ModuleInstallation { Name = "pterodactyl", BaseUrl = "https://modulos.example.net/pterodactyl/", CreatedAt = DateTimeOffset.UtcNow };
                db.ModuleInstallations.Add(installation);
                await db.SaveChangesAsync();
                var instance = new ModuleInstance { InstallationId = installation.Id, Name = "juegos", CreatedAt = DateTimeOffset.UtcNow };
                db.ModuleInstances.Add(instance);
                await db.SaveChangesAsync();
                instanceId = instance.Id;
            },
            modules: ModuleMapping.Parse(Modules));
    }

    [TestMethod]
    public async Task ConciliacionNoDejaSeccionesSinMirar()
    {
        var import = new SourceImport(null!, TextWriter.Null);
        import.Figures[("accounts_by_status", "Active")] = (1, 0m);
        import.Figures[("domains_by_status", "Active")] = (2, 0m);
        var baseline = Path.Combine(Path.GetTempPath(), $"linea-base-{Guid.NewGuid():N}.tsv");
        await File.WriteAllLinesAsync(baseline,
        [
            "# section\tkeys...\tvalues...",
            "clients_total\t1",
            "clients_by_status\tActive\t1",
            "domains_by_status\tActive\t2",
            "table_rows\ttblclients\t1",
            "seccion_nueva\tx\t1",
        ]);
        try
        {
            using var output = new StringWriter();
            var diffs = await Reconciliation.Compare(import, baseline, output);
            var text = output.ToString();
            Assert.AreEqual(1, diffs, text);
            Assert.Contains("seccion_nueva", text);
            Assert.Contains("OK   dominios Active: 2 / 2", text);
            Assert.Contains("table_rows", text);
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [TestMethod]
    public Task ModoSombra_AtaLosVivosSinNingunModuloReal() => WithImport(
        Cases,
        async (db, import) =>
        {
            // La instancia que nombra el mapa se crea sin URL: el servicio queda atado y su operación nunca sale.
            var juegos = await db.ModuleInstances.SingleAsync(i => i.Name == "juegos");
            Assert.AreEqual(string.Empty, (await db.ModuleInstallations.SingleAsync(m => m.Id == juegos.InstallationId)).BaseUrl);
            Assert.AreEqual(juegos.Id, (await db.Services.SingleAsync(s => s.Id == 910)).ModuleInstanceId);

            // Con params vacío no se copia ningún configoptionN del producto, tampoco al servicio.
            Assert.AreEqual("{}", (await db.Products.SingleAsync(p => p.Id == 50)).ModuleParams);
            Assert.IsNull(JsonNode.Parse((await db.Services.SingleAsync(s => s.Id == 910)).ModuleParams)!["configoption1"]);

            // Los servicios de un servidor que el mapa marca como manual van al módulo manual, con sus parámetros.
            var hosting = await db.Services.SingleAsync(s => s.Id == 914);
            Assert.AreEqual((await db.ModuleInstances.SingleAsync(i => i.Name == "servicios-manuales")).Id, hosting.ModuleInstanceId);
            Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"activation":"staff","lifecycle":"staff"}"""), JsonNode.Parse(hosting.ModuleParams)), hosting.ModuleParams);
            Assert.IsFalse(import.Incidents.Any(i => i.Contains("sin instancia", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
            Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("Modo sombra", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
        },
        modules: ModuleMapping.Parse("""{ "pterodactyl": { "instance": "juegos", "params": {} }, "modulo_propio": { "manual": true }, "vesta:2": { "manual": true } }"""),
        shadow: true);

    [TestMethod]
    public async Task ModoSombra_NoSeAtaAUnaInstanciaConURL()
    {
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WithImport(
            Cases,
            (_, _) => Task.CompletedTask,
            prepare: async db =>
            {
                var installation = new ModuleInstallation { Name = "pterodactyl", BaseUrl = "https://modulos.example.net/pterodactyl/", CreatedAt = DateTimeOffset.UtcNow };
                db.ModuleInstallations.Add(installation);
                await db.SaveChangesAsync();
                db.ModuleInstances.Add(new ModuleInstance { InstallationId = installation.Id, Name = "juegos", CreatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            },
            modules: ModuleMapping.Parse(Modules),
            shadow: true));
        Assert.Contains("juegos", error.Message);
    }

    [TestMethod]
    public async Task LineaBaseDelVolcado_ConciliaSinDiferencias()
    {
        var baseline = Path.Combine(Path.GetTempPath(), $"linea-base-{Guid.NewGuid():N}.tsv");
        try
        {
            await using (var dump = new DumpReader(new MemoryStream(Encoding.UTF8.GetBytes(Cases))))
            await using (var file = new StreamWriter(baseline))
            {
                await Baseline.Write(dump, file, CancellationToken.None);
            }

            var lines = await File.ReadAllLinesAsync(baseline);
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    "services_alive_by_due_month(count,amount)\t2026-08\t1\t100.00",
                    "services_alive_by_due_month(count,amount)\t2026-11\t5\t525.00",
                    "services_billed_unpaid(count)\tActive\t1",
                    "cancel_requests_pending_by_type(count)\tEnd of Billing Period\t1",
                    "cancel_requests_pending_by_type(count)\tImmediate\t1",
                    "payments_by_type(count,amountin)\t(sin tipo)\t6\t14678.00",
                    "payments_by_type(count,amountin)\tinvoice_billing_adjustment_credit\t1\t500.00",
                    "billing_notes_by_type_currency(count,total)\tcredit\tARS\t2\t742.00",
                    "billing_notes_by_type_currency(count,total)\tcredit:invoice_credit_applied\tARS\t2\t540.00",
                    "billing_notes_by_type_currency(count,total)\tdebit:invoice_credit_removed\tARS\t1\t500.00",
                    "invoices_by_status_currency(count,subtotal,credit,tax,tax2,total)\tCollections\tUSD\t1\t28.00\t0.00\t0.00\t0.00\t28.00",
                },
                lines,
                string.Join(Environment.NewLine, lines));

            // Lo importado concilia con lo que se contó directo del volcado, también en las cifras que la línea base de
            // la copia no tenía: si el importador perdiera o contara distinto una fila, saldría acá.
            await WithImport(Cases, async (_, import) =>
            {
                using var output = new StringWriter();
                Assert.AreEqual(0, await Reconciliation.Compare(import, baseline, output), output.ToString());
                Assert.Contains("OK   servicios Active con el período del vencimiento ya facturado: 1 / 1", output.ToString());

                // Las únicas cifras sin línea base son lo que entró de contenidos, que se informa siempre, y las del catálogo,
                // que el comando baseline no cuenta: ninguna otra cifra sale como INFO.
                Assert.IsFalse(output.ToString().Split(Environment.NewLine).Any(l => l.StartsWith("INFO", StringComparison.Ordinal)
                    && !l.StartsWith("INFO catalog ", StringComparison.Ordinal) && !l.StartsWith($"INFO {SourceImport.ContentImported} ", StringComparison.Ordinal)), output.ToString());
            });
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [TestMethod]
    public async Task ConciliacionMarcaLoQueSoloTieneLoImportado()
    {
        var import = new SourceImport(null!, TextWriter.Null);
        import.Figures[("services_alive_by_due_month", "2026-11")] = (2, 20m);
        import.Figures[("services_alive_by_due_month", "2026-12")] = (1, 10m);
        var baseline = Path.Combine(Path.GetTempPath(), $"linea-base-{Guid.NewGuid():N}.tsv");
        await File.WriteAllLinesAsync(baseline, ["services_alive_by_due_month(count,amount)\t2026-11\t2\t20.00"]);
        try
        {
            using var output = new StringWriter();
            Assert.AreEqual(1, await Reconciliation.Compare(import, baseline, output), output.ToString());
            Assert.Contains("DIF  services_alive_by_due_month 2026-12: 1 importados y la línea base no lo tiene", output.ToString());
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [TestMethod]
    public Task ProvinciaQueNoSeReconoce_SeCuentaSinImprimirElTextoDelOrigen()
    {
        // Texto que escribió el cliente en el campo, hasta un JSON pegado: la conciliación va al log y no lo repite.
        const string free = "Prov. inventada {\"calle\":\"Falsa 123\"}";
        return WithImport(Dump.Replace("'Buenos Aires'", $"'{free}'", StringComparison.Ordinal), async (db, import) =>
        {
            Assert.AreEqual(free, (await db.TaxIdentities.SingleAsync(t => t.AccountId == 501)).Province, "Queda tal cual para corregirlo desde la ficha.");
            Assert.AreEqual(1L, import.Figures[("provinces_unmapped", "cuentas")].Count);
            Assert.IsFalse(import.Figures.Keys.Any(k => k.Key.Contains("inventada", StringComparison.Ordinal)), "La cifra no lleva el texto del origen.");
            Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("1 cuentas de Argentina con una provincia", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));

            var baseline = Path.Combine(Path.GetTempPath(), $"linea-base-{Guid.NewGuid():N}.tsv");
            await File.WriteAllLinesAsync(baseline, ["# sin cifras"]);
            try
            {
                using var output = new StringWriter();
                await Reconciliation.Compare(import, baseline, output);
                var text = output.ToString();
                Assert.Contains("INFO provinces_unmapped cuentas: 1", text);
                Assert.DoesNotContain("inventada", text);
                Assert.DoesNotContain("Falsa 123", text);
            }
            finally
            {
                File.Delete(baseline);
            }
        });
    }

    internal static async Task WithImport(string source, Func<BillingDbContext, SourceImport, Task> check, Func<BillingDbContext, Task>? prepare = null, IReadOnlyDictionary<string, ModuleMapping>? modules = null, bool shadow = false, string? supportFiles = null)
    {
        var target = await TestDatabase.Create();
        try
        {
            await using var db = new BillingDbContext(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), target).Options);
            await db.Database.MigrateAsync();
            if (prepare is not null)
            {
                await prepare(db);
                db.ChangeTracker.Clear();
            }

            // El día de la copia es fijo: lo que vencía hasta ese día no depende de cuándo corre la prueba.
            var import = new SourceImport(db, TextWriter.Null, modules) { AsOf = new DateOnly(2026, 10, 31), ShadowInstances = shadow, SupportFiles = supportFiles };
            await using (var dump = new DumpReader(new MemoryStream(Encoding.UTF8.GetBytes(source))))
            {
                await import.Load(dump, CancellationToken.None);
            }

            await import.Run(CancellationToken.None);
            await check(db, import);
        }
        finally
        {
            await TestDatabase.Drop(target);
        }
    }

    [TestMethod]
    public async Task FacturaReembolsadaEnElOrigen_QuedaAnuladaYDevuelta()
    {
        const string dump = """
            CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
            CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `companyname` text, `email` text, `country` text, `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` text, `language` text);
            CREATE TABLE `tblinvoices` (`id` int(10), `userid` int(11), `invoicenum` text, `date` date, `duedate` date, `subtotal` decimal(16,2), `credit` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` text);
            INSERT INTO `tblcurrencies` VALUES (3,'ARS',1);
            INSERT INTO `tblclients` VALUES (501,'Ana','Prueba','','ana@example.com','AR',3,0.00,'2025-03-31','Active','spanish');
            INSERT INTO `tblinvoices` VALUES (7100,501,'7100','2025-03-31','2025-04-07',100.00,0.00,0.00,0.00,100.00,0.000,'Refunded'),(7101,501,'7101','2025-03-31','2025-04-07',100.00,0.00,0.00,0.00,100.00,0.000,'Cancelled');
            """;
        var target = await TestDatabase.Create();
        try
        {
            await using var db = new BillingDbContext(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), target).Options);
            await db.Database.MigrateAsync();
            var import = new SourceImport(db, TextWriter.Null);
            await using (var reader = new DumpReader(new MemoryStream(Encoding.UTF8.GetBytes(dump))))
            {
                await import.Load(reader, CancellationToken.None);
            }

            await import.Run(CancellationToken.None);

            // Las dos quedan anuladas, pero solo la reembolsada tiene lo pagado devuelto: así la API compatible las distingue.
            var refunded = await db.Documents.SingleAsync(d => d.Id == 7100);
            Assert.AreEqual(InvoiceStatus.Cancelled, refunded.Status);
            Assert.AreEqual(100m, refunded.Refunded);
            Assert.AreEqual(0m, (await db.Documents.SingleAsync(d => d.Id == 7101)).Refunded);
        }
        finally
        {
            await TestDatabase.Drop(target);
        }
    }
}
