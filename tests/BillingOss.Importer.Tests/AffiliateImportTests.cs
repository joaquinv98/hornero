using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Importer.Tests;

/// <summary>Programa de afiliados desde un origen sintético con la estructura de la copia (sin datos de clientes reales).</summary>
[TestClass]
public sealed class AffiliateImportTests
{
    private const string Dump = """
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `companyname` text, `email` text, `country` text, `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` text, `language` text);
        CREATE TABLE `tblproductgroups` (`id` int(11), `name` text);
        CREATE TABLE `tblproducts` (`id` int(11), `gid` int(11), `name` text, `hidden` tinyint(1), `retired` tinyint(1), `paytype` text, `servertype` text, `tax` tinyint(1), `affiliateonetime` tinyint(1), `affiliatepaytype` text, `affiliatepayamount` decimal(16,2));
        CREATE TABLE `tblpricing` (`id` int(11), `type` text, `currency` int(11), `relid` int(11), `msetupfee` decimal(16,2), `qsetupfee` decimal(16,2), `ssetupfee` decimal(16,2), `asetupfee` decimal(16,2), `bsetupfee` decimal(16,2), `tsetupfee` decimal(16,2), `monthly` decimal(16,2), `quarterly` decimal(16,2), `semiannually` decimal(16,2), `annually` decimal(16,2), `biennially` decimal(16,2), `triennially` decimal(16,2));
        CREATE TABLE `tblhosting` (`id` int(11), `userid` int(11), `packageid` int(11), `regdate` date, `domain` text, `qty` int(11), `amount` decimal(16,2), `billingcycle` text, `nextduedate` date, `termination_date` date, `domainstatus` text, `username` text, `paymentmethod` text);
        CREATE TABLE `tblinvoices` (`id` int(10), `userid` int(11), `invoicenum` text, `date` date, `duedate` date, `subtotal` decimal(16,2), `credit` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` text);
        CREATE TABLE `tblinvoiceitems` (`id` int(11), `invoiceid` int(10), `type` varchar(30), `relid` int(11), `description` text, `amount` decimal(16,2), `taxed` int(11), `duedate` date);
        CREATE TABLE `tblconfiguration` (`id` int(11), `setting` varchar(64), `value` mediumtext);
        CREATE TABLE `tblaffiliates` (`id` int(11), `date` date, `clientid` int(11), `visitors` int(11), `paytype` text, `payamount` decimal(16,2), `onetime` int(11), `balance` decimal(16,2), `withdrawn` decimal(16,2), `created_at` timestamp, `updated_at` timestamp);
        CREATE TABLE `tblaffiliates_hits` (`id` int(10), `affiliate_id` int(10), `referrer_id` int(10), `created_at` datetime);
        CREATE TABLE `tblaffiliates_referrers` (`id` int(10), `affiliate_id` int(10), `referrer` varchar(500), `created_at` datetime, `updated_at` datetime);
        CREATE TABLE `tblaffiliatesaccounts` (`id` int(11), `affiliateid` int(11), `relid` int(11), `lastpaid` date, `created_at` timestamp, `updated_at` timestamp);
        CREATE TABLE `tblaffiliateshistory` (`id` int(11), `affiliateid` int(11), `date` date, `affaccid` int(11), `invoice_id` int(10), `description` text, `amount` decimal(16,2), `created_at` timestamp, `updated_at` timestamp);
        CREATE TABLE `tblaffiliatespending` (`id` int(11), `affaccid` int(11), `invoice_id` int(10), `amount` decimal(16,2), `clearingdate` date, `created_at` timestamp, `updated_at` timestamp);
        CREATE TABLE `tblaffiliateswithdrawals` (`id` int(11), `affiliateid` int(11), `date` date, `amount` decimal(16,2), `created_at` timestamp, `updated_at` timestamp);
        INSERT INTO `tblcurrencies` VALUES (1,'USD',1),(3,'ARS',0);
        INSERT INTO `tblclients` VALUES (601,'Afi','Liada','','afiliada@example.com','AR',3,0.00,'2025-01-01','Active','spanish'),(602,'Cli','Ente','','cliente@example.com','AR',3,0.00,'2025-01-02','Active','spanish'),(603,'Otra','Afiliada','','otra@example.com','US',1,0.00,'2025-01-03','Active','english');
        INSERT INTO `tblproductgroups` VALUES (7,'Hosting');
        INSERT INTO `tblproducts` VALUES (40,7,'Hosting 1',0,0,'recurring','',1,0,'',0.00),(41,7,'Hosting interno',0,0,'recurring','',1,0,'none',0.00),(42,7,'Hosting raro',0,0,'recurring','',1,0,'otra',3.00);
        INSERT INTO `tblpricing` VALUES (1,'product',3,40,0.00,-1.00,-1.00,0.00,-1.00,-1.00,12100.00,-1.00,-1.00,-1.00,-1.00,-1.00);
        INSERT INTO `tblhosting` VALUES (900,602,40,'2025-03-31','a.example.com',1,12100.00,'Monthly','2026-11-30','0000-00-00','Active','u900',''),(901,602,41,'2025-04-01','b.example.com',1,12100.00,'Monthly','2026-12-01','0000-00-00','Active','u901',''),(902,601,40,'2025-04-02','c.example.com',1,12100.00,'Monthly','2026-12-02','0000-00-00','Active','u902','');
        INSERT INTO `tblinvoices` VALUES (7000,602,'7000','2025-03-31','2025-04-07',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Paid');
        INSERT INTO `tblinvoiceitems` VALUES (1,7000,'Hosting',900,'Hosting 1',12100.00,1,'2025-03-31');
        INSERT INTO `tblconfiguration` VALUES (1,'AffiliateEnabled','on'),(2,'AffiliateEarningPercent','5'),(3,'AffiliateBonusDeposit','250.00'),(4,'AffiliatePayout','5.00'),(5,'SendAffiliateReportMonthly','on'),(6,'AffiliatesDelayCommission','29'),(7,'AffiliateDepartment','1'),(8,'AffiliateLinks','');
        INSERT INTO `tblaffiliates` VALUES
        (10,'2025-01-01',601,3,'',0.00,0,120.00,10.00,'0000-00-00 00:00:00','0000-00-00 00:00:00'),
        (11,'2025-02-01',603,0,'percentage',10.00,0,7.00,0.00,'0000-00-00 00:00:00','0000-00-00 00:00:00'),
        (12,'2025-02-02',999,0,'',0.00,0,0.00,0.00,'0000-00-00 00:00:00','0000-00-00 00:00:00');
        INSERT INTO `tblaffiliates_referrers` VALUES (1,10,'https://www.example.org/blog?x=1','2025-03-01 10:00:00','2025-03-01 10:00:00');
        INSERT INTO `tblaffiliates_hits` VALUES (1,10,1,'2025-03-01 10:00:00'),(2,10,0,'2025-03-02 10:00:00'),(3,77,0,'2025-03-03 10:00:00');
        INSERT INTO `tblaffiliatesaccounts` VALUES (1,10,900,'2025-03-31','0000-00-00 00:00:00','0000-00-00 00:00:00'),(2,10,901,'0000-00-00','0000-00-00 00:00:00','0000-00-00 00:00:00'),(3,11,555,'0000-00-00','0000-00-00 00:00:00','0000-00-00 00:00:00'),(4,10,902,'0000-00-00','0000-00-00 00:00:00','0000-00-00 00:00:00');
        INSERT INTO `tblaffiliateshistory` VALUES (1,10,'2025-03-31',1,7000,'',100.00,'0000-00-00 00:00:00','0000-00-00 00:00:00'),(2,10,'2025-04-30',1,0,'',30.00,'0000-00-00 00:00:00','0000-00-00 00:00:00'),(3,10,'2025-05-30',1,0,'',0.00,'0000-00-00 00:00:00','0000-00-00 00:00:00');
        INSERT INTO `tblaffiliatespending` VALUES (1,2,0,12.50,'2026-12-01','0000-00-00 00:00:00','0000-00-00 00:00:00');
        INSERT INTO `tblaffiliateswithdrawals` VALUES (1,10,'2025-05-01',10.00,'0000-00-00 00:00:00','0000-00-00 00:00:00');
        """;

    [TestMethod]
    public Task ImportaProgramaAfiliadosReferidosComisionesRetirosYClics() => SourceImportTests.WithImport(Dump, async (db, import) =>
    {
        var program = await db.Set<AffiliateProgram>().SingleAsync();
        Assert.IsTrue(program.Enabled);
        Assert.AreEqual(5m, program.MinimumPayout(Currency.Usd), "El retiro mínimo va en la moneda por defecto del origen.");
        Assert.AreEqual(250m, program.SignupBonus(Currency.Usd), "El bono de alta también.");
        Assert.AreEqual((29, 60), (program.HoldDays, program.CookieDays), "La espera sale del origen y la ventana queda la de fábrica.");
        Assert.IsTrue(program.MonthlyReport);
        var rules = await db.Set<CommissionRule>().ToListAsync();
        var programRule = rules.Single(r => r.ProductId == null && r.AffiliateId == null);
        Assert.AreEqual((CommissionKind.Percent, 5m, true), (programRule.Kind, programRule.Value, programRule.Recurring));
        Assert.AreEqual(CommissionKind.None, rules.Single(r => r.ProductId == 41).Kind);
        Assert.IsFalse(rules.Any(r => r.ProductId is 40 or 42));
        var own = rules.Single(r => r.AffiliateId == 11);
        Assert.AreEqual((CommissionKind.Percent, 10m, true), (own.Kind, own.Value, own.Recurring));

        var affiliates = await db.Set<Affiliate>().OrderBy(a => a.Id).ToListAsync();
        CollectionAssert.AreEqual(new long[] { 10, 11 }, affiliates.Select(a => a.Id).ToList());
        Assert.IsTrue(affiliates.All(a => a.Status == AffiliateStatus.Active && a.Code == a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.AreEqual(601L, affiliates[0].AccountId);

        var referrals = await db.Set<AffiliateReferral>().OrderBy(r => r.Id).ToListAsync();
        CollectionAssert.AreEqual(new long[] { 1, 2 }, referrals.Select(r => r.Id).ToList());
        Assert.AreEqual((900L, 602L, new DateOnly(2025, 3, 31)), (referrals[0].ServiceId, referrals[0].AccountId, referrals[0].PaidBefore!.Value));
        Assert.IsNull(referrals[1].PaidBefore);

        var commissions = await db.Set<AffiliateCommission>().OrderBy(c => c.Id).ToListAsync();
        Assert.HasCount(3, commissions);
        Assert.AreEqual((100m, 7000L, CommissionStatus.Released, Currency.Ars), (commissions[0].Amount, commissions[0].DocumentId!.Value, commissions[0].Status, commissions[0].Currency));
        Assert.IsNull(commissions[1].DocumentId, "Sin factura en el origen.");
        Assert.AreEqual((12.5m, CommissionStatus.Held, new DateOnly(2026, 12, 1), 901L), (commissions[2].Amount, commissions[2].Status, commissions[2].ReleaseOn, commissions[2].ServiceId));
        Assert.IsTrue(commissions.All(c => c.Imported));

        var payout = await db.Set<AffiliatePayout>().SingleAsync();
        Assert.AreEqual((PayoutMethod.Withdrawal, PayoutStatus.Paid, 10m), (payout.Method, payout.Status, payout.Amount));

        // El libro da el saldo del origen: 100 + 30 - 10 para el 10, y un movimiento de importación cubre los 7 del 11.
        var ledger = await db.Set<AffiliateLedgerEntry>().GroupBy(e => e.AffiliateId).ToDictionaryAsync(g => g.Key, g => g.Sum(e => e.Amount));
        Assert.AreEqual(120m, ledger[10]);
        Assert.AreEqual(7m, ledger[11]);
        Assert.AreEqual(Currency.Usd, (await db.Set<AffiliateLedgerEntry>().FirstAsync(e => e.AffiliateId == 11)).Currency);

        var clicks = await db.Set<AffiliateClick>().OrderBy(c => c.At).ToListAsync();
        Assert.HasCount(2, clicks);
        Assert.AreEqual("www.example.org", clicks[0].ReferrerHost);
        Assert.IsNull(clicks[1].ReferrerHost);

        string[] expected =
        [
            "Afiliado 12: la cuenta 999 no existe, no se importa.",
            "Referido 3: el afiliado 11 o el servicio 555 no se importó, queda afuera.",
            "Referido 4: el servicio 902 es de la cuenta del propio afiliado 10, queda afuera.",
            "Afiliados: 1 clics de afiliados que ya no están en el origen quedan afuera.",
        ];
        foreach (var incident in expected)
        {
            Assert.IsTrue(import.Incidents.Contains(incident), incident + " | " + string.Join(" | ", import.Incidents));
        }

        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("Producto 42: comisión de tipo 'otra'", StringComparison.Ordinal)));
        Assert.IsTrue(import.Incidents.Any(i => i.StartsWith("Afiliado 11: el saldo del origen (7.00)", StringComparison.Ordinal)));
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("departamento 1", StringComparison.Ordinal)), "Los retiros iban a un departamento: hay que cargar la casilla.");
        Assert.IsFalse(import.Incidents.Any(i => i.Contains("bono", StringComparison.Ordinal) || i.Contains("material", StringComparison.Ordinal) || i.Contains("retiró", StringComparison.Ordinal)),
            "El bono se importa, el material vacío no es incidencia y lo retirado da con los retiros.");
        Assert.AreEqual(2, import.Figures[("affiliate_rows", "tblaffiliates")].Count);
        Assert.AreEqual(2, import.Figures[("affiliate_rows", "tblaffiliatesaccounts")].Count);
        Assert.AreEqual(3, import.Figures[("affiliate_rows", "tblaffiliateshistory")].Count, "La comisión en cero cuenta como vista, sin importarse.");
        Assert.IsTrue(import.Incidents.Contains("Comisión 3 de tblaffiliateshistory: importe cero, no se importa."));
        Assert.AreEqual(1, import.Figures[("affiliate_rows", "tblaffiliatespending")].Count);
        Assert.AreEqual(1, import.Figures[("affiliate_rows", "tblaffiliateswithdrawals")].Count);

        // Las secuencias arrancan arriba de los IDs conservados.
        Assert.IsGreaterThan(11L, await db.Database.SqlQueryRaw<long>("SELECT nextval(pg_get_serial_sequence('affiliates.affiliates', 'id')) AS \"Value\"").SingleAsync());
        Assert.IsGreaterThan(2L, await db.Database.SqlQueryRaw<long>("SELECT nextval(pg_get_serial_sequence('affiliates.referrals', 'id')) AS \"Value\"").SingleAsync());
    });
}
