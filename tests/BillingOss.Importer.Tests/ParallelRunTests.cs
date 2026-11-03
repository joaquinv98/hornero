using System.Text;
using BillingOss.App.Persistence;
using BillingOss.Domain.Billing;
using BillingOss.Importer.Import;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.Importer.Tests;

/// <summary>
/// Un día del mes en paralelo sobre un origen sintético: el volcado previo se importa, el posterior trae lo que el
/// origen hizo en su ciclo del 24/11 y los cobros del día. Sin datos de clientes reales.
/// </summary>
[TestClass]
public sealed class ParallelRunTests
{
    private const string Schema = """
        CREATE TABLE `tblconfiguration` (`id` int(11), `setting` varchar(64), `value` mediumtext);
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblclients` (`id` int(11), `firstname` text, `lastname` text, `companyname` text, `email` text, `country` text, `currency` int(11), `credit` decimal(16,2), `datecreated` date, `status` text, `language` text);
        CREATE TABLE `tblproducts` (`id` int(11), `gid` int(11), `name` text, `hidden` tinyint(1), `retired` tinyint(1), `paytype` text, `servertype` text, `tax` tinyint(1), `autoterminatedays` int(11));
        CREATE TABLE `tblhosting` (`id` int(11), `userid` int(11), `packageid` int(11), `regdate` date, `qty` int(11), `amount` decimal(16,2), `billingcycle` text, `nextduedate` date, `nextinvoicedate` date, `termination_date` date, `domainstatus` text, `paymentmethod` text);
        CREATE TABLE `tblorders` (`id` int(11), `userid` int(11), `date` datetime, `invoiceid` int(10), `status` text);
        CREATE TABLE `tblinvoices` (`id` int(10), `userid` int(11), `invoicenum` text, `date` date, `duedate` date, `subtotal` decimal(16,2), `credit` decimal(16,2), `tax` decimal(16,2), `tax2` decimal(16,2), `total` decimal(16,2), `taxrate` decimal(10,3), `status` text);
        CREATE TABLE `tblinvoiceitems` (`id` int(11), `invoiceid` int(10), `type` varchar(30), `relid` int(11), `description` text, `amount` decimal(16,2), `taxed` int(11), `duedate` date);
        CREATE TABLE `tblaccounts` (`id` int(11), `userid` int(11), `currency` int(11), `gateway` text, `date` datetime, `amountin` decimal(16,2), `fees` decimal(16,2), `amountout` decimal(16,2), `transid` text, `invoiceid` int(10), `type` text);
        CREATE TABLE `tblcancelrequests` (`id` int(11), `date` datetime, `relid` int(11), `reason` text, `type` text);
        CREATE TABLE `tbldomains` (`id` int(10), `userid` int(10), `domain` text, `registrar` text, `registrationperiod` int(1), `status` text, `nextduedate` date, `expirydate` date, `recurringamount` decimal(16,2), `paymentmethod` text);
        INSERT INTO `tbldomains` VALUES (50,501,'ejemplo.com.ar','',1,'Active','2026-11-28','2026-11-28',1210.00,'mercadopago_1');
        INSERT INTO `tblconfiguration` VALUES (1,'CreateInvoiceDaysBefore','7'),(2,'AutoSuspension','on'),(3,'AutoSuspensionDays','1'),(4,'AutoTermination','on'),(5,'AutoTerminationDays','14'),(6,'ContinuousInvoiceGeneration',''),(7,'OrderDaysGrace','0');
        INSERT INTO `tblcurrencies` VALUES (1,'USD',1),(3,'ARS',0);
        INSERT INTO `tblclients` VALUES (501,'Ana','Prueba','','ana@example.com','AR',3,0.00,'2025-01-01','Active','spanish');
        INSERT INTO `tblproducts` VALUES (40,7,'Minecraft 4 GB',0,0,'recurring','pterodactyl',1,0);
        INSERT INTO `tblcancelrequests` VALUES (1,'2026-11-20 10:00:00',901,'','End of Billing Period');

        """;

    // 900 y 904 renuevan, 901 tiene baja pedida, 902 se suspende, 903 está en mora hace mucho (su baja vencía el 15/10),
    // 905 tiene una impaga que se anula, 906 paga en el día, 908 está en mora desde el 15/11 y el origen no lo suspendió
    // ni le anuló la factura, y 909 tiene una promoción recurrente. De los dominios, 51 venció en septiembre y el origen
    // lo sigue teniendo activo, 52 vence el 23/11 con la renovación impaga, 53 está al día y 54 tiene la renovación
    // facturada y la paga en el día.
    private const string Before = Schema + """
        INSERT INTO `tblhosting` VALUES (900,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','mercadopago_1'),(901,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','mercadopago_1'),(902,501,40,'2025-12-23',1,12100.00,'Monthly','2026-11-23','2026-11-23','0000-00-00','Active','mercadopago_1'),(903,501,40,'2025-10-01',1,12100.00,'Monthly','2026-10-01','2026-10-01','0000-00-00','Suspended','mercadopago_1'),(904,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','paypal_ppcpv'),(905,501,40,'2025-11-17',1,12100.00,'Monthly','2026-11-17','2026-11-17','0000-00-00','Suspended','mercadopago_1'),(906,501,40,'2025-11-20',1,12100.00,'Monthly','2026-11-20','2026-11-20','0000-00-00','Suspended','mercadopago_1'),(908,501,40,'2025-11-15',1,12100.00,'Monthly','2026-11-15','2026-11-15','0000-00-00','Active','mercadopago_1'),(909,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','transferencia');
        INSERT INTO `tblinvoices` VALUES (7000,501,'7000','2026-10-25','2026-11-01',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Paid'),(7002,501,'7002','2026-11-16','2026-11-23',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(7003,501,'7003','2026-11-10','2026-11-17',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(7004,501,'7004','2026-11-13','2026-11-20',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(7005,501,'7005','2026-09-24','2026-10-01',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Cancelled'),(7006,501,'7006','2026-11-08','2026-11-15',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(7007,501,'7007','2026-11-16','2026-11-23',1000.00,0.00,210.00,0.00,1210.00,21.000,'Unpaid'),(7008,501,'7008','2026-11-19','2026-11-26',1000.00,0.00,210.00,0.00,1210.00,21.000,'Unpaid');
        INSERT INTO `tblinvoiceitems` VALUES (1,7000,'Hosting',900,'Minecraft (01/11/2026 - 30/11/2026)',12100.00,1,'2026-11-01'),(2,7002,'Hosting',902,'Minecraft (23/11/2026 - 22/12/2026)',12100.00,1,'2026-11-23'),(3,7003,'Hosting',905,'Minecraft (17/11/2026 - 16/12/2026)',12100.00,1,'2026-11-17'),(4,7004,'Hosting',906,'Minecraft (20/11/2026 - 19/12/2026)',12100.00,1,'2026-11-20'),(5,7005,'Hosting',903,'Minecraft (01/10/2026 - 31/10/2026)',12100.00,1,'2026-10-01'),(6,7006,'Hosting',908,'Minecraft (15/11/2026 - 14/12/2026)',12100.00,1,'2026-11-15'),(7,7007,'Domain',52,'Renovación (23/11/2026 - 22/11/2027)',1210.00,1,'2026-11-23'),(8,7008,'Domain',54,'Renovación (26/11/2026 - 25/11/2027)',1210.00,1,'2026-11-26');
        INSERT INTO `tblaccounts` VALUES (1,501,0,'mercadopago_1','2026-10-26 10:00:00',12100.00,0.00,0.00,'MP-1',7000,'');
        INSERT INTO `tbldomains` VALUES (51,501,'viejo.example','',1,'Active','2026-09-01','2027-09-01',1210.00,'mercadopago_1'),(52,501,'impago.example','',1,'Active','2026-11-23','2026-11-23',1210.00,'mercadopago_1'),(53,501,'sincronizado.example','',1,'Active','2027-01-10','2027-01-10',1210.00,'mercadopago_1'),(54,501,'pagado.example','',1,'Active','2026-11-26','2026-11-26',1210.00,'mercadopago_1');
        """;

    // El ciclo del origen renovó 900, 904 (con otro importe), 909 (con la promoción en un renglón aparte) y el dominio
    // 50, suspendió 902 y anuló 7003. En el día entró un pedido nuevo, 906 pagó y el staff dio de baja a mano el 903. El
    // origen dejó activos los dominios 51 y 52, pasó 53 a vencido por su cuenta y cobró la renovación de 54.
    private const string After = Schema + """
        INSERT INTO `tblhosting` VALUES (900,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','mercadopago_1'),(901,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','mercadopago_1'),(902,501,40,'2025-12-23',1,12100.00,'Monthly','2026-11-23','2026-11-23','0000-00-00','Suspended','mercadopago_1'),(903,501,40,'2025-10-01',1,12100.00,'Monthly','2026-10-01','2026-10-01','2026-11-24','Terminated','mercadopago_1'),(904,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','paypal_ppcpv'),(905,501,40,'2025-11-17',1,12100.00,'Monthly','2026-11-17','2026-11-17','0000-00-00','Suspended','mercadopago_1'),(906,501,40,'2025-11-20',1,12100.00,'Monthly','2026-12-20','2026-12-20','0000-00-00','Active','mercadopago_1'),(907,501,40,'2026-11-24',1,12100.00,'Monthly','2026-11-24','2026-11-24','0000-00-00','Pending','mercadopago_1'),(908,501,40,'2025-11-15',1,12100.00,'Monthly','2026-11-15','2026-11-15','0000-00-00','Active','mercadopago_1'),(909,501,40,'2025-12-01',1,12100.00,'Monthly','2026-12-01','2026-12-01','0000-00-00','Active','transferencia');
        INSERT INTO `tblorders` VALUES (301,501,'2026-11-24 10:00:00',8003,'Pending');
        INSERT INTO `tblinvoices` VALUES (7000,501,'7000','2026-10-25','2026-11-01',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Paid'),(7002,501,'7002','2026-11-16','2026-11-23',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(7003,501,'7003','2026-11-10','2026-11-17',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Cancelled'),(7004,501,'7004','2026-11-13','2026-11-20',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Paid'),(7005,501,'7005','2026-09-24','2026-10-01',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Cancelled'),(7006,501,'7006','2026-11-08','2026-11-15',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(7007,501,'7007','2026-11-16','2026-11-23',1000.00,0.00,210.00,0.00,1210.00,21.000,'Unpaid'),(7008,501,'7008','2026-11-19','2026-11-26',1000.00,0.00,210.00,0.00,1210.00,21.000,'Paid'),(8001,501,'8001','2026-11-24','2026-12-01',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(8002,501,'8002','2026-11-24','2026-12-01',10743.80,0.00,2256.20,0.00,13000.00,21.000,'Unpaid'),(8003,501,'8003','2026-11-24','2026-11-24',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid'),(8004,501,'8004','2026-11-24','2026-11-28',1000.00,0.00,210.00,0.00,1210.00,21.000,'Unpaid'),(8005,501,'8005','2026-11-24','2026-12-01',10000.00,0.00,2100.00,0.00,12100.00,21.000,'Unpaid');
        INSERT INTO `tblinvoiceitems` VALUES (1,7000,'Hosting',900,'Minecraft (01/11/2026 - 30/11/2026)',12100.00,1,'2026-11-01'),(2,7002,'Hosting',902,'Minecraft (23/11/2026 - 22/12/2026)',12100.00,1,'2026-11-23'),(3,7003,'Hosting',905,'Minecraft (17/11/2026 - 16/12/2026)',12100.00,1,'2026-11-17'),(4,7004,'Hosting',906,'Minecraft (20/11/2026 - 19/12/2026)',12100.00,1,'2026-11-20'),(5,7005,'Hosting',903,'Minecraft (01/10/2026 - 31/10/2026)',12100.00,1,'2026-10-01'),(6,7006,'Hosting',908,'Minecraft (15/11/2026 - 14/12/2026)',12100.00,1,'2026-11-15'),(7,7007,'Domain',52,'Renovación (23/11/2026 - 22/11/2027)',1210.00,1,'2026-11-23'),(8,7008,'Domain',54,'Renovación (26/11/2026 - 25/11/2027)',1210.00,1,'2026-11-26'),(10,8001,'Hosting',900,'Minecraft (01/12/2026 - 31/12/2026)',12100.00,1,'2026-12-01'),(11,8002,'Hosting',904,'Minecraft (01/12/2026 - 31/12/2026)',13000.00,1,'2026-12-01'),(12,8003,'Hosting',907,'Minecraft (24/11/2026 - 23/12/2026)',12100.00,1,'2026-11-24'),(13,8004,'Domain',50,'Renovación de ejemplo.com.ar (28/11/2026 - 27/11/2027)',1210.00,1,'2026-11-28'),(14,8005,'Hosting',909,'Minecraft (01/12/2026 - 31/12/2026)',15000.00,1,'2026-12-01'),(15,8005,'PromoHosting',909,'Promoción del 19,33 %',-2900.00,1,'2026-12-01');
        INSERT INTO `tblaccounts` VALUES (1,501,0,'mercadopago_1','2026-10-26 10:00:00',12100.00,0.00,0.00,'MP-1',7000,''),(2,501,0,'mercadopago_1','2026-11-24 11:00:00',12100.00,500.00,0.00,'MP-9',7004,''),(3,501,0,'mercadopago_1','2026-11-24 12:00:00',1210.00,0.00,0.00,'MP-11',7008,'');
        INSERT INTO `tbldomains` VALUES (51,501,'viejo.example','',1,'Active','2026-09-01','2027-09-01',1210.00,'mercadopago_1'),(52,501,'impago.example','',1,'Active','2026-11-23','2026-11-23',1210.00,'mercadopago_1'),(53,501,'sincronizado.example','',1,'Expired','2027-01-10','2027-01-10',1210.00,'mercadopago_1'),(54,501,'pagado.example','',1,'Active','2027-11-26','2027-11-26',1210.00,'mercadopago_1');
        """;

    // Una cuenta en dólares que paga por Mercado Pago en pesos, como los cobros anteriores a junio de 2026: el de 7020
    // llega sin moneda y por diez veces el bruto, y el de 7021 con la moneda en pesos.
    private const string UsdBefore = Schema + """
        INSERT INTO `tblclients` VALUES (502,'Dan','Dolar','','dan@example.com','US',1,0.00,'2025-01-01','Active','english');
        INSERT INTO `tblinvoices` VALUES (7020,502,'7020','2026-11-17','2026-11-24',6.00,0.00,0.00,0.00,6.00,0.000,'Unpaid'),(7021,502,'7021','2026-11-17','2026-11-24',69.30,0.00,0.00,0.00,69.30,0.000,'Unpaid');
        INSERT INTO `tblinvoiceitems` VALUES (1,7020,'Item',0,'Servicio',6.00,0,'2026-11-24'),(2,7021,'Item',0,'Servicio',69.30,0,'2026-11-24');
        """;

    private const string UsdAfter = Schema + """
        INSERT INTO `tblclients` VALUES (502,'Dan','Dolar','','dan@example.com','US',1,0.00,'2025-01-01','Active','english');
        INSERT INTO `tblinvoices` VALUES (7020,502,'7020','2026-11-17','2026-11-24',6.00,0.00,0.00,0.00,6.00,0.000,'Paid'),(7021,502,'7021','2026-11-17','2026-11-24',69.30,0.00,0.00,0.00,69.30,0.000,'Paid');
        INSERT INTO `tblinvoiceitems` VALUES (1,7020,'Item',0,'Servicio',6.00,0,'2026-11-24'),(2,7021,'Item',0,'Servicio',69.30,0,'2026-11-24');
        INSERT INTO `tblaccounts` VALUES (1,502,0,'mercadopago_1','2026-11-24 10:00:00',8643.00,0.00,0.00,'MP-20',7020,''),(2,502,3,'mercadopago_1','2026-11-24 11:00:00',99549.45,0.00,0.00,'MP-21',7021,'');
        """;

    private static readonly DateOnly Day = new(2026, 11, 24);

    [TestMethod]
    public Task ElDiaCoincideSalvoLoQueDifiereDeVerdad() => WithDay(Before, After, (diffs, _) =>
    {
        var all = string.Join(" ; ", diffs);

        // El renglón de 909 con su promoción aparte se compara neto: no es diferencia. El dominio 53 lo venció el origen
        // por su cuenta y billing-oss no: es defecto.
        CollectionAssert.AreEqual(new[] { "dominio|53|estado", "factura de renovación|904|IVA", "factura de renovación|904|total", "renovación|904|importe" }, Of(diffs, DifferenceClass.Defect, d => d.Field), all);

        // La suspensión y la anulación de 908 vencían antes del día y el origen no las hizo: billing-oss las hace, y
        // alguien las tiene que mirar antes del corte. Lo mismo el dominio 51, que billing-oss da por vencido.
        CollectionAssert.AreEqual(new[] { "anulación|908|7006", "dominio|51|", "suspendido|908|" }, Of(diffs, DifferenceClass.Review, d => d.SourceInvoiceId?.ToString()), all);

        // La baja de 903 ya vencía en la copia: billing-oss la tiene retenida en una revisión del staff y no la hace
        // sola, y el origen la hizo a mano en el día. El dominio 52 entra en gracia el día después de su vencimiento
        // impago, por la regla propia de billing-oss.
        CollectionAssert.AreEqual(new[] { "baja|903|", "dominio|52|", "factura de pedido||8003" }, Of(diffs, DifferenceClass.Expected, d => d.SourceInvoiceId?.ToString()), all);
        Assert.AreEqual("en gracia", diffs.Single(d => d.Kind == "dominio" && d.ServiceId == 52).Own);
        Assert.Contains("retenida", diffs.Single(d => d.ServiceId == 903).Reason);
        Assert.AreEqual(new DateOnly(2026, 10, 30), ParallelRun.PeriodEnd("Minecraft (01/10/2026 - 30/10/2026) RAM: 4GB"));
        Assert.AreEqual(new DateOnly(2026, 12, 2), ParallelRun.DomainStart("domain:7:2026-12-02"));
        Assert.AreEqual(new DateOnly(2026, 12, 2), ParallelRun.DomainStart("domain:7:2026-12-02:1"));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task CobroEnPesosDeFacturaEnDolares_SeRepiteComoLoImputaElImportador() => WithDay(UsdBefore, UsdAfter, async (diffs, db) =>
    {
        // Las dos facturas quedan pagas, como en el origen, sin un saldo a favor que el cliente no tiene en ninguna
        // moneda: el cobro se repite en la moneda de la factura por lo que debía, como lo imputa el importador.
        Assert.IsFalse(diffs.Any(d => d.Error && d.Kind == "cobro"), string.Join(" ; ", diffs));
        CollectionAssert.AreEqual(new[] { InvoiceStatus.Paid, InvoiceStatus.Paid }, await db.Documents.Where(d => d.Id == 7020 || d.Id == 7021).OrderBy(d => d.Id).Select(d => d.Status).ToListAsync());
        Assert.AreEqual(0m, await db.CreditBalances.Where(b => b.AccountId == 502).SumAsync(b => b.Balance));
        CollectionAssert.AreEqual(new[] { "MP-20 USD 6.00", "MP-21 USD 69.30" },
            (await db.Payments.Where(p => p.AccountId == 502).OrderBy(p => p.ExternalId).ToListAsync()).Select(p => FormattableString.Invariant($"{p.ExternalId} {p.Currency.Code} {p.Amount:0.00}")).ToList());
    });

    [TestMethod]
    public async Task DiaCompleto_DejaElInformeClasificadoYElHistorial()
    {
        var name = new NpgsqlConnectionStringBuilder(await TestDatabase.Create()).Database!;
        var before = Path.GetTempFileName();
        var after = Path.GetTempFileName();
        var history = Path.Combine(Path.GetTempPath(), $"paralelo-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(before, Before);
            await File.WriteAllTextAsync(after, After);
            using var output = new StringWriter();

            // Con el mapa en modo sombra: la instancia del panel se crea sin URL y ningún módulo se llama.
            var code = await DailyRun.Run(TestDatabase.Admin, new DailyOptions(before, after, Day, history)
            {
                Modules = ModuleMapping.Parse("""{"pterodactyl": {"instance": "juegos"}}"""),
                BaseDb = name,
            }, output, CancellationToken.None);

            Assert.AreEqual(3, code, output.ToString());
            var folder = Path.Combine(history, "2026-11-24");
            Assert.Contains("Conciliación: sin diferencias.", await File.ReadAllTextAsync(Path.Combine(folder, "importacion.txt")));
            Assert.Contains("Modo sombra", await File.ReadAllTextAsync(Path.Combine(folder, "importacion.txt")));
            var lines = await File.ReadAllLinesAsync(Path.Combine(folder, "diferencias.tsv"));
            Assert.AreEqual("clase\ttipo\tservicio\tfactura_origen\tdocumento_billing_oss\tcampo\torigen\tbilling_oss\tmotivo", lines[0]);
            CollectionAssert.AreEqual(new[] { "defecto", "defecto", "defecto", "defecto", "a revisar", "a revisar", "a revisar", "esperada", "esperada", "esperada" }, lines.Skip(1).Select(l => l.Split('\t')[0]).ToList());
            Assert.Contains("defecto    renovación: 1", await File.ReadAllTextAsync(Path.Combine(folder, "resumen.txt")));

            // El dominio 51 venció hace más que la gracia y el origen lo tiene activo: la importación lo avisa.
            Assert.Contains("dominios vivos con el vencimiento pasado", await File.ReadAllTextAsync(Path.Combine(folder, "importacion.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(folder, "linea-base.tsv")));
            Assert.IsTrue(File.Exists(Path.Combine(folder, "dia.txt")));

            var log = await File.ReadAllLinesAsync(Path.Combine(history, DailyRun.HistoryFile));
            Assert.HasCount(2, log);
            var fields = log[1].Split('\t');
            Assert.AreEqual("2026-11-24", fields[0]);
            CollectionAssert.AreEqual(new[] { "ok", "3", "3", "4", "3" }, fields[3..].ToList(), log[1]);
        }
        finally
        {
            File.Delete(before);
            File.Delete(after);
            if (Directory.Exists(history))
            {
                Directory.Delete(history, recursive: true);
            }

            await TestDatabase.Drop(TestDatabase.Other($"{name}_dia"));
            await TestDatabase.Drop(TestDatabase.Other(name));
        }
    }

    [TestMethod]
    public async Task DiaQueNoPudoCorrer_TambienQuedaEnElHistorial()
    {
        var history = Path.Combine(Path.GetTempPath(), $"paralelo-{Guid.NewGuid():N}");
        try
        {
            // Sin el volcado previo el día no corre: la falla sale igual, y el historial no queda con el día salteado.
            var missing = Path.Combine(history, "no-existe.sql.gz");
            await Assert.ThrowsAsync<IOException>(() => DailyRun.Run("Host=127.0.0.1", new DailyOptions(missing, missing, Day, history), TextWriter.Null, CancellationToken.None));

            var log = await File.ReadAllLinesAsync(Path.Combine(history, DailyRun.HistoryFile));
            Assert.HasCount(2, log);
            var fields = log[1].Split('\t');
            CollectionAssert.AreEqual(new[] { "2026-11-24", "falló", string.Empty, string.Empty, string.Empty, "1" }, new[] { fields[0], fields[3], fields[4], fields[5], fields[6], fields[7] }, log[1]);
        }
        finally
        {
            if (Directory.Exists(history))
            {
                Directory.Delete(history, recursive: true);
            }
        }
    }

    /// <summary>
    /// Importa el volcado previo como el día anterior, corre el día contra el posterior y deja revisar la base de trabajo
    /// antes de borrar las dos. Sin instancias de módulo en la base de prueba: el producto pasa a manual, como haría el
    /// mapa de módulos.
    /// </summary>
    private static async Task WithDay(string before, string after, Func<IReadOnlyList<Difference>, BillingDbContext, Task> check)
    {
        var target = await TestDatabase.Create();
        var name = new NpgsqlConnectionStringBuilder(target).Database!;
        var work = $"{name}_w";
        var post = Path.GetTempFileName();
        try
        {
            await using (var db = Context(target))
            {
                await db.Database.MigrateAsync();
                var import = new SourceImport(db, TextWriter.Null, ModuleMapping.Parse("""{"pterodactyl": {"manual": true}}""")) { AsOf = Day.AddDays(-1) };
                await using (var dump = new DumpReader(new MemoryStream(Encoding.UTF8.GetBytes(before))))
                {
                    await import.Load(dump, CancellationToken.None);
                }

                await import.Run(CancellationToken.None);
            }

            // La base importada es la plantilla de la de trabajo: sin sesiones abiertas, tampoco las del pool de la prueba.
            await TestDatabase.ClosePool(target);
            await File.WriteAllTextAsync(post, after);
            var diffs = await new ParallelRun(TestDatabase.Admin, TextWriter.Null).Run(post, name, work, Day, 7, CancellationToken.None);
            await using var result = Context(TestDatabase.Other(work));
            await check(diffs, result);
        }
        finally
        {
            File.Delete(post);
            await TestDatabase.Drop(TestDatabase.Other(work));
            await TestDatabase.Drop(target);
        }
    }

    private static BillingDbContext Context(string connection) => new(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), connection).Options);

    private static List<string> Of(IEnumerable<Difference> diffs, DifferenceClass kind, Func<Difference, string?> detail) =>
        [.. diffs.Where(d => d.Class == kind).Select(d => $"{d.Kind}|{d.ServiceId}|{detail(d)}").Order(StringComparer.Ordinal)];
}
