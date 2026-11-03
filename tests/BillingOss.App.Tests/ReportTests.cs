using System.Text.Json;
using BillingOss.App.Platform;
using BillingOss.App.Reports;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Reportes y salidas contables (docs/reportes) contra PostgreSQL: cada número sale de documentos, pagos, servicios y
/// comprobantes armados con los casos de uso, más importados como los deja el importador.
/// </summary>
[TestClass]
public sealed partial class ReportTests
{
    private static readonly DateTimeOffset October = new(2026, 10, 10, 15, 0, 0, TimeSpan.Zero);

    private static readonly MonthRange OctoberToNovember = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1));

    private static Task<T> Report<T>(CoreHarness h, Func<ReportService, Task<T>> work) => h.Use(work);

    private static Task<PlacedOrder> Order(CoreHarness h, long? account = null) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account ?? h.AccountId, [new OrderItem(h.PlanId)]));

    private static Task<long> CreditNote(CoreHarness h, long invoice) =>
        h.Use<CreditService, long>(c => c.IssueCreditNote(invoice, [new CreditLine("Ajuste", 1m, 1210m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));

    /// <summary>Cuenta en dólares de un consumidor final del país, con el plan también en dólares.</summary>
    private static async Task<long> UsdAccount(CoreHarness h)
    {
        long id = 0;
        await h.Db(async db =>
        {
            db.PlanPrices.Add(new PlanPrice(h.PlanId, Currency.Usd, 10m, 0m, new DateOnly(2026, 1, 1)));
            var account = new Account("Cuenta en dólares", "usd@example.com", Currency.Usd, h.Clock.Now);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            db.TaxIdentities.Add(new TaxIdentity(account.Id, TaxDocumentType.Dni, "30111223", "Cuenta en dólares", 5, "AR", h.Clock.Now));
            await db.SaveChangesAsync();
            id = account.Id;
        });
        return id;
    }

    /// <summary>Factura importada de 1210 con IVA del 21 %, como la deja el importador (D-17).</summary>
    private static Document Imported(long id, long account, DateOnly date, DateOnly? due = null, decimal credited = 0m, string status = "Unpaid") =>
        Document.FromImport(id, DocumentKind.Invoice, account, Currency.Ars, id, date, due ?? date, new TaxBreakdown([new AliquotTotal(0.21m, 1000m, 210m)], [], 0m, 0m), 0m, credited, "{}", DateTimeOffset.UtcNow, sourceStatus: status);

    [TestMethod]
    public async Task Ingresos_PorMesYMoneda_ConNotasDeCreditoYAnuladosSinNota()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        h.Clock.Now = October;
        await Order(h);
        await h.Db(async db =>
        {
            db.Documents.Add(Imported(900001, h.AccountId, new DateOnly(2026, 10, 5), credited: 1210m, status: "Cancelled"));
            await db.SaveChangesAsync();
        });
        h.Clock.Now = now;
        var november = (await Order(h)).InvoiceId!.Value;
        await CreditNote(h, november);
        await Order(h, await UsdAccount(h));

        var revenue = (await Report(h, r => r.Revenue(OctoberToNovember, default))).Table("Revenue");

        Assert.HasCount(3, revenue.Rows);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "ARS", 2, 13310m, 0m, 1210m, 10000m, 2100m, 12100m }, revenue.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ARS", 1, 12100m, 1210m, 0m, 9000m, 1890m, 10890m }, revenue.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "USD", 1, 10m, 0m, 0m, 8.26m, 1.74m, 10m }, revenue.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { null, "ARS", 3L, 25410m, 1210m, 1210m, 19000m, 3990m, 22990m }, revenue.Totals[0]);
        CollectionAssert.AreEqual(new object?[] { null, "USD", 1L, 10m, 0m, 0m, 8.26m, 1.74m, 10m }, revenue.Totals[1]);

        // Fuera del período no hay nada.
        Assert.IsEmpty((await Report(h, r => r.Revenue(new MonthRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 1)), default))).Table("Revenue").Rows);
    }

    [TestMethod]
    public async Task IngresosPorProducto_NetoPorLinea_LaNotaSinServicioVaSinProductoYElTotalEsElDeIngresos()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        h.Clock.Now = October;
        await Order(h);
        await h.Db(async db =>
        {
            var group = new ProductGroup { Name = "Hosting" };
            db.ProductGroups.Add(group);
            await db.SaveChangesAsync();
            var hosting = new Product { Name = "Hosting 1 GB", GroupId = group.Id, Kind = ProductKind.Manual };
            db.Products.Add(hosting);
            await db.SaveChangesAsync();
            var service = new Service(h.AccountId, hosting.Id, new BillingCycle(CycleUnit.Month), Currency.Ars, 1210m, 1, October);
            db.Services.Add(service);
            await db.SaveChangesAsync();

            // Importadas con su línea con IVA: una vigente y otra anulada en el sistema anterior sin nota.
            foreach (var (id, credited) in new[] { (900001L, 0m), (900002L, 1210m) })
            {
                var doc = Imported(id, h.AccountId, new DateOnly(2026, 10, 7), credited: credited, status: credited > 0m ? "Cancelled" : "Unpaid");
                doc.ImportLine(LineKind.Renewal, "Hosting 1 GB", 1210m, TaxTreatment.Taxed, 0.21m, service.Id);
                db.Documents.Add(doc);
            }

            await db.SaveChangesAsync();
        });
        h.Clock.Now = now;
        var november = (await Order(h)).InvoiceId!.Value;
        await CreditNote(h, november);
        await Order(h, await UsdAccount(h));

        var products = (await Report(h, r => r.RevenueByProduct(OctoberToNovember, default))).Table("RevenueByProduct");

        // De mayor a menor en cada moneda. La nota de crédito del ajuste no tiene servicio y resta en "Sin producto".
        Assert.HasCount(4, products.Rows);
        CollectionAssert.AreEqual(new object?[] { null, "VPS 2 GB", "ARS", 2, 20000m }, products.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "Hosting", "Hosting 1 GB", "ARS", 2, 1000m }, products.Rows[1], "la anulada sin nota no suma");
        CollectionAssert.AreEqual(new object?[] { null, null, "ARS", 0, -1000m }, products.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { null, "VPS 2 GB", "USD", 1, 8.26m }, products.Rows[3]);
        Assert.AreEqual("ReportCol.NoProduct", products.Columns[1].Empty);

        // Cada documento una vez en el pie, y el mismo neto que el reporte de ingresos.
        CollectionAssert.AreEqual(new object?[] { null, null, "ARS", 4, 20000m }, products.Totals[0]);
        CollectionAssert.AreEqual(new object?[] { null, null, "USD", 1, 8.26m }, products.Totals[1]);
        var revenue = (await Report(h, r => r.Revenue(OctoberToNovember, default))).Table("Revenue");
        CollectionAssert.AreEqual(new[] { 20000m, 8.26m }, revenue.Totals.Select(t => (decimal)t[6]!).ToArray());
    }

    [TestMethod]
    public async Task AltasDeClientes_PorMesDelProveedor_ContrataronYConServiciosHoy()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var product = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            var count = 0;
            async Task Signup(DateTimeOffset created, params ServiceStatus[] statuses)
            {
                var account = new Account("Alta", $"alta{++count}@example.com", Currency.Ars, created);
                db.Accounts.Add(account);
                await db.SaveChangesAsync();
                foreach (var status in statuses)
                {
                    var s = new Service(account.Id, product, new BillingCycle(CycleUnit.Month), Currency.Ars, 12100m, 1, created);
                    s.ImportState(status, 1, null, null, status is ServiceStatus.Cancelled or ServiceStatus.Pending ? null : created, status == ServiceStatus.Terminated ? h.Clock.Now : null);
                    db.Services.Add(s);
                }

                await db.SaveChangesAsync();
            }

            await Signup(new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), ServiceStatus.Active, ServiceStatus.Cancelled);

            // A las 23:30 del 31/10 en Buenos Aires: es de octubre aunque en UTC ya sea noviembre.
            await Signup(new(2026, 11, 1, 2, 30, 0, TimeSpan.Zero), ServiceStatus.Terminated);
            await Signup(new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero), ServiceStatus.Cancelled, ServiceStatus.Pending);
            await Signup(new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), ServiceStatus.Suspended);
            await Signup(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), ServiceStatus.Active);
        });

        var report = await Report(h, r => r.NewAccounts(OctoberToNovember, default));

        // Noviembre suma la cuenta que siembra el arnés, sin servicios. La de septiembre queda afuera.
        var months = report.Table("NewAccounts");
        Assert.HasCount(2, months.Rows);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), 2, 2, 1 }, months.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), 3, 1, 1 }, months.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { null, 5L, 3L, 2L }, months.Totals.Single());
        Assert.AreEqual("NewAccounts", report.Charts.Single().Table);
    }

    [TestMethod]
    public async Task Ventas_IvaPorAlicuotaYDetalle_LaNotaRestaYLoAnuladoNoAparece()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        h.Clock.Now = October;
        var october = (await Order(h)).InvoiceId!.Value;
        await h.Db(async db =>
        {
            db.Documents.Add(Imported(900001, h.AccountId, new DateOnly(2026, 10, 5), credited: 1210m, status: "Cancelled"));
            await db.SaveChangesAsync();
        });
        h.Clock.Now = now;
        var november = (await Order(h)).InvoiceId!.Value;
        var note = await CreditNote(h, november);
        await Order(h, await UsdAccount(h));

        var sales = await Report(h, r => r.Sales(OctoberToNovember, default));

        var byRate = sales.Table("SalesByAliquot");
        Assert.HasCount(3, byRate.Rows);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "ARS", "TaxTreatment.Taxed", 0.21m, 10000m, 2100m }, byRate.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ARS", "TaxTreatment.Taxed", 0.21m, 9000m, 1890m }, byRate.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "USD", "TaxTreatment.Taxed", 0.21m, 8.26m, 1.74m }, byRate.Rows[2]);

        var detail = sales.Table("SalesDocuments");
        CollectionAssert.AreEqual(
            new[] { "Date", "Document", "Number", "Voucher", "Cae", "Receiver", "TaxId", "VatCondition", "Currency", "NetAt", "VatAt", "Exempt", "NotTaxed", "Voided", "Total" },
            detail.Columns.Select(c => c.Id).ToArray());
        Assert.AreEqual(0.21m, detail.Columns[9].Rate);
        Assert.HasCount(4, detail.Rows, "la importada anulada entera no aparece");
        var numbers = await h.Query(db => db.Documents.Where(d => d.Id == october || d.Id == november || d.Id == note).ToDictionaryAsync(d => d.Id, d => d.Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        CollectionAssert.AreEqual(
            new object?[] { new DateOnly(2026, 11, 2), "DocumentKind.CreditNote", numbers[note], null, null, "Cliente de prueba", "30111222", 5, "ARS", -1000m, -210m, -0m, -0m, 0m, -1210m },
            detail.Rows[2]);
        Assert.AreEqual(numbers[october], detail.Rows[0][2]);
        Assert.AreEqual(12100m, detail.Rows[0][14]);
        Assert.AreEqual("USD", detail.Rows[3][8]);
    }

    [TestMethod]
    public async Task Deuda_PorAntiguedadDesdeElVencimiento_PorMonedaYPorCuenta()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        h.Clock.Now = October;
        var october = (await Order(h)).InvoiceId!.Value;
        h.Clock.Now = now;
        var november = (await Order(h)).InvoiceId!.Value;
        await CreditNote(h, november);
        await h.Use<PaymentService, long>(p => p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-1", Currency.Ars, 5000m, PaymentStatus.Approved, [october])));
        var usd = await UsdAccount(h);
        await Order(h, usd);
        await h.Db(async db =>
        {
            db.Documents.Add(Imported(900001, h.AccountId, new DateOnly(2026, 5, 20), due: new DateOnly(2026, 6, 1)));
            db.Documents.Add(Imported(900002, h.AccountId, new DateOnly(2026, 9, 1), credited: 1210m, status: "Cancelled"));
            await db.SaveChangesAsync();
        });

        var aging = await Report(h, r => r.Aging(default));

        // Al 2/11: la de octubre venció el 17/10 (16 días), la de noviembre vence el 9/11 y la importada el 1/6.
        CollectionAssert.AreEqual(new object?[] { "ARS", 1, 3, 10890m, 7100m, 0m, 0m, 1210m, 19200m }, aging.Table("AgingSummary").Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "USD", 1, 1, 10m, 0m, 0m, 0m, 0m, 10m }, aging.Table("AgingSummary").Rows[1]);
        var accounts = aging.Table("AgingAccounts").Rows;
        CollectionAssert.AreEqual(new object?[] { h.AccountId, "Cliente de prueba", "ARS", 3, 10890m, 7100m, 0m, 0m, 1210m, 19200m, new DateOnly(2026, 6, 1) }, accounts[0]);
        CollectionAssert.AreEqual(new object?[] { usd, "Cuenta en dólares", "USD", 1, 10m, 0m, 0m, 0m, 0m, 10m, new DateOnly(2026, 11, 9) }, accounts[1]);
        Assert.AreEqual(new DateOnly(2026, 11, 2), await Report(h, r => Task.FromResult(r.Today)));
    }

    [TestMethod]
    public async Task Recurrente_AltasBajasYTasaDeBajasPorMesYMoneda()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var product = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            Service Service(Currency currency, decimal price, ServiceStatus status, DateTimeOffset? activated, DateTimeOffset? terminated = null, CycleUnit unit = CycleUnit.Month)
            {
                var s = new Service(h.AccountId, product, new BillingCycle(unit), currency, price, 1, activated ?? October);
                s.ImportState(status, 1, null, null, activated, terminated);
                return s;
            }

            db.Services.AddRange(
                Service(Currency.Ars, 12100m, ServiceStatus.Active, new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)),
                Service(Currency.Ars, 12100m, ServiceStatus.Terminated, new(2026, 10, 10, 15, 0, 0, TimeSpan.Zero), new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero)),
                Service(Currency.Ars, 120000m, ServiceStatus.Active, new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), unit: CycleUnit.Year),
                Service(Currency.Ars, 12100m, ServiceStatus.Suspended, new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)),
                Service(Currency.Ars, 12100m, ServiceStatus.Terminated, new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)),
                Service(Currency.Ars, 12100m, ServiceStatus.Cancelled, null),
                Service(Currency.Ars, 5000m, ServiceStatus.Active, new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), unit: CycleUnit.OneTime),
                Service(Currency.Usd, 10m, ServiceStatus.Active, new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
        });

        var recurring = await Report(h, r => r.Recurring(new MonthRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 11, 1)), default));

        CollectionAssert.AreEqual(new object?[] { "ARS", 2, 22100m, 1, 12100m }, recurring.Table("RecurringNow").Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "USD", 1, 10m, 0, 0m }, recurring.Table("RecurringNow").Rows[1]);
        var months = recurring.Table("RecurringMonths").Rows;
        Assert.HasCount(6, months);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 9, 1), "ARS", 24200m, 1, 10000m, 0, 0m, 0m, 3, 34200m }, months[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 9, 1), "USD", 0m, 0, 0m, 0, 0m, null, 0, 0m }, months[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "ARS", 34200m, 1, 12100m, 0, 0m, 0m, 4, 46300m }, months[2]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "USD", 0m, 1, 10m, 0, 0m, null, 1, 10m }, months[3]);

        // La baja del 1/11 a la medianoche de Buenos Aires es de noviembre: 12.100 de 46.300.
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ARS", 46300m, 0, 0m, 1, 12100m, 26.13m, 3, 34200m }, months[4]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "USD", 10m, 0, 0m, 0, 0m, 0m, 1, 10m }, months[5]);
        var note = recurring.Notes.Single();
        Assert.AreEqual("UndatedTerminations", note.Key);
        Assert.AreEqual("1", note.Args.Single());
    }

    [TestMethod]
    public async Task Cobros_PorMesMonedaYMedio_ConComisionesYDevoluciones()
    {
        await using var h = await CoreHarness.Start();
        long instance = 0;
        await h.Db(async db =>
        {
            var installation = new ModuleInstallation { Name = "pasarela", BaseUrl = "http://127.0.0.1:9/", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync();
            var created = new ModuleInstance { InstallationId = installation.Id, Name = "Mercado Pago", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstances.Add(created);
            var imported = Payment.FromImport(h.AccountId, "import:mercadopago", "tx-1", Currency.Ars, 1000m, 50m, new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), PaymentStatus.PartiallyRefunded);
            imported.AddRefunded(200m);
            db.Payments.Add(imported);
            await db.SaveChangesAsync();
            instance = created.Id;
        });
        var usd = await UsdAccount(h);
        await h.Use<PaymentService, long>(async p =>
        {
            await p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-1", Currency.Ars, 5000m, PaymentStatus.Approved));
            await p.Record(new PaymentInput(h.AccountId, $"instance:{instance}", "mp-1", Currency.Ars, 2000m, PaymentStatus.Approved, Fee: 120m));
            await p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-2", Currency.Ars, 700m, PaymentStatus.Pending));
            await p.Record(new PaymentInput(h.AccountId, $"instance:{instance}", "mp-2", Currency.Ars, 900m, PaymentStatus.Rejected));
            return await p.Record(new PaymentInput(usd, "manual:transferencia", "t-3", Currency.Usd, 10m, PaymentStatus.Approved));
        });

        var collections = (await Report(h, r => r.Collections(OctoberToNovember, default))).Table("Collections");

        Assert.HasCount(4, collections.Rows, "lo pendiente y lo rechazado no se cobró");
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "ARS", "mercadopago (import)", 1, 1000m, 50m, 200m, 750m }, collections.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ARS", $"Mercado Pago (instance:{instance})", 1, 2000m, 120m, 0m, 1880m }, collections.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ARS", "transferencia (manual)", 1, 5000m, 0m, 0m, 5000m }, collections.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "USD", "transferencia (manual)", 1, 10m, 0m, 0m, 10m }, collections.Rows[3]);
        CollectionAssert.AreEqual(new object?[] { null, "ARS", null, 3L, 8000m, 170m, 200m, 7630m }, collections.Totals[0]);
    }

    [TestMethod]
    public async Task Impuestos_EnPesosPorComprobante_ConPercepcionesYLoQueFaltaRevisar()
    {
        await using var h = await CoreHarness.Start();
        var issuer = await Issuer(h);
        var ars = (await Order(h)).InvoiceId!.Value;
        var usd = await UsdAccount(h);
        var dollars = (await Order(h, usd)).InvoiceId!.Value;
        var unrated = (await Order(h, usd)).InvoiceId!.Value;
        await Order(h);
        await h.Db(async db =>
        {
            db.Documents.Add(Imported(900001, h.AccountId, new DateOnly(2026, 10, 5), credited: 1210m, status: "Cancelled"));
            await db.SaveChangesAsync();
            const string arsPayload = """
                {"document":{"receiver":{"doc_type":96,"doc_number":"30111222","vat_condition":5,"name":"Cliente de prueba"},"currency":"PES","exchange_rate":"1",
                 "amounts":{"other_taxes":"300.00","taxes":[{"id":7,"description":"Percepción IIBB","base":"10000.00","rate":"3","amount":"300.00"}]}}}
                """;
            const string imported = """{"imported":true,"doc_type":99,"doc_number":"0","currency":"PES","exchange_rate":"1.000000","total":"1210.00","net":"1000.00","vat":"210.00"}""";
            db.FiscalDocuments.AddRange(
                Voucher(issuer, ars, 6, 1, new DateOnly(2026, 11, 2), arsPayload),
                Voucher(issuer, dollars, 6, 2, new DateOnly(2026, 11, 2), """{"document":{"currency":"DOL","exchange_rate":"1450.5","receiver":{"doc_type":96,"doc_number":"30111223","name":"Cuenta en dólares"}}}"""),
                Voucher(issuer, unrated, 6, 3, new DateOnly(2026, 11, 2), """{"document":{"currency":"DOL","receiver":{"doc_type":96,"doc_number":"30111223","name":"Cuenta en dólares"}}}"""),
                Voucher(issuer, 900001, 6, 50, new DateOnly(2026, 10, 5), imported, true),
                Voucher(issuer, 900001, 8, 7, new DateOnly(2026, 11, 2), imported, true));
            await db.SaveChangesAsync();
        });

        var taxes = await Report(h, r => r.Taxes(OctoberToNovember, default));

        var table = taxes.Table("Taxes");
        Assert.HasCount(3, table.Rows);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "ReportTax.Vat", string.Empty, 0.21m, 1000m, 210m }, table.Rows[0]);

        // 10.000 en pesos, 8,26 dólares a 1450,5 y la nota que anuló la importada, colgada de la misma factura.
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ReportTax.Vat", string.Empty, 0.21m, 20981.13m, 4413.87m }, table.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ReportTax.Other", "Percepción IIBB", 0.03m, 10000m, 300m }, table.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { null, null, null, null, 31981.13m, 4923.87m }, table.Totals.Single());
        CollectionAssert.AreEqual(new[] { "NoExchangeRate", "WithoutVoucher" }, taxes.Notes.Select(n => n.Key).ToArray());
        Assert.AreEqual("006-00003-00000003", taxes.Notes[0].Args.Single());
        Assert.AreEqual("1", taxes.Notes[1].Args.Single(), "la cuarta factura todavía no tiene comprobante");

        // El libro de noviembre lleva los cuatro comprobantes, la nota importada como tipo 8.
        var libro = await Report(h, r => r.LibroIva(new DateOnly(2026, 11, 1), default));
        CollectionAssert.AreEqual(new[] { "006", "006", "006", "008" }, libro.Vouchers.Select(v => v[8..11]).ToArray());
        Assert.AreEqual("000000000121000", libro.Vouchers[3][108..123], "la nota con sus propios importes");
        Assert.AreEqual("0000000000", libro.Vouchers[2][231..241], "sin cotización va en cero, con su nota");
    }

    [TestMethod]
    public async Task LibroIva_ConElComprobanteQueAutorizoElFisco()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _h = h;
        await using var _m = module;
        await FiscalFlowTests.Order(h);
        await h.RunJobs();

        var libro = await Report(h, r => r.LibroIva(new DateOnly(2026, 11, 1), default));

        var expected = "20261102" + "006" + "00003" + "1".PadLeft(20, '0') + "1".PadLeft(20, '0') + "96" + "30111222".PadLeft(20, '0')
            + "Cliente de prueba".PadRight(30) + "000000001210000" + new string('0', 105) + "PES" + "0001000000" + "1" + " " + new string('0', 15) + "00000000";
        Assert.AreEqual(expected, libro.Vouchers.Single());
        Assert.AreEqual("006" + "00003" + "1".PadLeft(20, '0') + "000000001000000" + "0005" + "000000000210000", libro.Aliquots.Single());
        Assert.IsEmpty(libro.Notes);
        CollectionAssert.AreEqual(new object?[] { "006", "PES", 1, 10000m, 2100m, 0m, 12100m }, libro.Summary.Rows.Single());
    }

    [TestMethod]
    public async Task ClientesYServicios_PorProductoYServidor_CadaCuentaUnaVezEnLosTotales()
    {
        await using var h = await CoreHarness.Start();
        var usd = await UsdAccount(h);
        long other = 0;
        await h.Db(async db =>
        {
            var vps = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            var group = new ProductGroup { Name = "Hosting" };
            db.ProductGroups.Add(group);
            await db.SaveChangesAsync();
            var hosting = new Product { Name = "Hosting 1 GB", GroupId = group.Id, Kind = ProductKind.Provisioned };
            db.Products.Add(hosting);
            var proxmox = new ModuleInstallation { Name = "proxmox", BaseUrl = "http://127.0.0.1:9/", CreatedAt = DateTimeOffset.UtcNow };
            var hestia = new ModuleInstallation { Name = "hestia", BaseUrl = "http://127.0.0.1:9/", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstallations.AddRange(proxmox, hestia);
            var account = new Account("Otra cuenta", "otra@example.com", Currency.Ars, h.Clock.Now);
            db.Accounts.AddRange(account, new Account("Sin servicios", "inactiva@example.com", Currency.Ars, h.Clock.Now) { Status = AccountStatus.Inactive });
            await db.SaveChangesAsync();
            var cluster = new ModuleInstance { InstallationId = proxmox.Id, Name = "Cluster Norte", CreatedAt = DateTimeOffset.UtcNow };
            var panel = new ModuleInstance { InstallationId = hestia.Id, Name = "Panel Web", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstances.AddRange(cluster, panel);
            await db.SaveChangesAsync();
            other = account.Id;

            Service Service(long owner, long product, Currency currency, decimal price, ServiceStatus status, ModuleInstance? instance, CycleUnit unit = CycleUnit.Month, decimal? discount = null)
            {
                var s = new Service(owner, product, new BillingCycle(unit), currency, price, 1, October) { ModuleInstanceId = instance?.Id, DiscountPercent = discount };
                s.ImportState(status, 1, null, null, status == ServiceStatus.Pending ? null : October, status == ServiceStatus.Terminated ? h.Clock.Now : null);
                return s;
            }

            db.Services.AddRange(
                Service(h.AccountId, vps, Currency.Ars, 12100m, ServiceStatus.Active, cluster),
                Service(h.AccountId, vps, Currency.Ars, 12100m, ServiceStatus.Suspended, cluster),
                Service(other, vps, Currency.Ars, 120000m, ServiceStatus.Active, cluster, CycleUnit.Year),
                Service(other, vps, Currency.Ars, 12100m, ServiceStatus.Pending, null),
                Service(other, vps, Currency.Ars, 5000m, ServiceStatus.Active, null, CycleUnit.OneTime),
                Service(h.AccountId, hosting.Id, Currency.Ars, 5000m, ServiceStatus.Active, panel, discount: 10m),
                Service(usd, hosting.Id, Currency.Usd, 10m, ServiceStatus.Active, panel),
                Service(other, hosting.Id, Currency.Ars, 5000m, ServiceStatus.Terminated, panel),
                Service(other, hosting.Id, Currency.Ars, 5000m, ServiceStatus.Cancelled, panel));
            await db.SaveChangesAsync();
        });

        var report = await Report(h, r => r.Services(default));

        var accounts = report.Table("AccountsByStatus").Rows;
        Assert.HasCount(2, accounts, "sin cuentas cerradas no hay fila de cerradas");
        CollectionAssert.AreEqual(new object?[] { "AccountStatus.Active", 3, 3 }, accounts[0]);
        CollectionAssert.AreEqual(new object?[] { "AccountStatus.Inactive", 1, 0 }, accounts[1]);

        // El VPS anual cuenta la doceava parte, el pago único no es recurrente y lo dado de baja o cancelado no está.
        var products = report.Table("ServicesByProduct");
        Assert.HasCount(3, products.Rows);
        CollectionAssert.AreEqual(new object?[] { null, "VPS 2 GB", "ARS", 2, 3, 1, 1, 22100m }, products.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "Hosting", "Hosting 1 GB", "ARS", 1, 1, 0, 0, 4500m }, products.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { "Hosting", "Hosting 1 GB", "USD", 1, 1, 0, 0, 10m }, products.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { null, null, "ARS", 2, 4, 1, 1, 26600m }, products.Totals[0], "dos cuentas en pesos, no tres");
        CollectionAssert.AreEqual(new object?[] { null, null, "USD", 1, 1, 0, 0, 10m }, products.Totals[1]);

        var servers = report.Table("ServicesByServer");
        Assert.HasCount(4, servers.Rows);
        CollectionAssert.AreEqual(new object?[] { "Cluster Norte", "proxmox", "ARS", 2, 2, 1, 0, 22100m }, servers.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "Panel Web", "hestia", "ARS", 1, 1, 0, 0, 4500m }, servers.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { "Panel Web", "hestia", "USD", 1, 1, 0, 0, 10m }, servers.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { null, null, "ARS", 1, 1, 0, 1, 0m }, servers.Rows[3], "lo pendiente y lo manual, sin servidor");
        Assert.AreEqual("ReportCol.NoServer", servers.Columns[0].Empty);
        CollectionAssert.AreEqual(new[] { "ServicesByProduct", "ServicesByServer" }, report.Charts.Select(c => c.Id).ToArray());
    }

    [TestMethod]
    public async Task Soporte_SinElModulo_SaleVacioConSuNota()
    {
        await using var h = await CoreHarness.Start();
        // El soporte ya viene con el núcleo: una base sin sus tablas es la de una instalación a medias.
        await h.Db(db => db.Database.ExecuteSqlRawAsync("DROP SCHEMA support CASCADE"));

        var report = await Report(h, r => r.Support(OctoberToNovember, default));

        Assert.AreEqual("SupportNotInstalled", report.Notes.Single().Key);
        Assert.IsTrue(report.Tables.All(t => t.Rows.Count == 0 && t.Totals.Count == 0));
    }

    [TestMethod]
    public async Task Soporte_PorDepartamentoYMes_PrimeraRespuestaSinNotasInternasYResolucion()
    {
        await using var h = await CoreHarness.Start();
        await SupportTables(h);
        await h.Db(db => db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO support.departments (id, name, hidden, clients_only, auto_reply, request_feedback, sort_order, mail_enabled, imap_port, imap_security, imap_uid_validity, imap_last_uid, created_at)
            VALUES (1, 'Ventas', false, false, true, false, 2, false, 993, 'SslOnConnect', 0, 0, now()),
                   (2, 'Soporte técnico', false, false, true, false, 1, false, 993, 'SslOnConnect', 0, 0, now());
            INSERT INTO support.tickets (id, number, requester_name, requester_email, cc, department_id, subject, status, priority, reply_token, merged_into_id, imported, created_at, last_message_at, updated_at, closed_at)
            SELECT id, 'T-' || id, 'Cliente', 'cliente@example.com', ARRAY[]::text[], dep, 'Consulta', status, 'Normal', 'token-' || id, merged, false, created::timestamptz, created::timestamptz, created::timestamptz, closed::timestamptz
            FROM (VALUES
              (1, 2, 'Closed', '2026-10-05 12:00Z', '2026-10-06 12:00Z', NULL::bigint),
              (2, 2, 'AwaitingCustomer', '2026-10-20 10:00Z', NULL, NULL),
              (3, 2, 'Open', '2026-11-01 02:00Z', NULL, NULL),
              (7, 2, 'AwaitingCustomer', '2026-10-25 12:00Z', NULL, NULL),
              (8, 2, 'Closed', '2026-10-20 11:00Z', '2026-10-20 11:30Z', 2),
              (4, 1, 'Closed', '2026-11-03 15:00Z', '2026-11-04 15:00Z', NULL),
              (5, 1, 'Open', '2026-09-30 12:00Z', NULL, NULL),
              (6, 1, 'Open', '2026-11-10 12:00Z', NULL, NULL)) AS v(id, dep, status, created, closed, merged);
            INSERT INTO support.messages (ticket_id, author, author_name, body, internal, source, created_at)
            SELECT ticket, author, 'Alguien', 'Texto', internal, 'Portal', at::timestamptz
            FROM (VALUES
              (1, 'Customer', false, '2026-10-05 12:00Z'), (1, 'Staff', false, '2026-10-05 14:00Z'),
              (2, 'Customer', false, '2026-10-20 10:00Z'), (2, 'Staff', true, '2026-10-20 10:30Z'), (2, 'Staff', false, '2026-10-20 16:00Z'),
              (3, 'Customer', false, '2026-11-01 02:00Z'), (3, 'System', false, '2026-11-01 03:00Z'),
              (7, 'Customer', false, '2026-10-25 12:00Z'), (7, 'Staff', false, '2026-10-26 04:00Z'),
              (4, 'Staff', false, '2026-11-03 15:00Z'), (4, 'Customer', false, '2026-11-03 16:00Z'),
              (5, 'Customer', false, '2026-09-30 12:00Z'),
              (6, 'Customer', false, '2026-11-10 12:00Z'), (6, 'Staff', false, '2026-11-10 13:30Z'), (6, 'Customer', false, '2026-11-10 14:00Z')) AS v(ticket, author, internal, at);
            """));

        var report = await Report(h, r => r.Support(OctoberToNovember, default));

        // Soporte técnico: respuestas a las 2, 6 y 16 horas (la nota interna de la media hora no cuenta), una sin respuesta
        // y una cerrada al día. El que se unió a otro no cuenta. Ventas: el ticket que abrió el staff no espera respuesta.
        // El de septiembre queda afuera.
        Assert.IsEmpty(report.Notes);
        var departments = report.Table("TicketsByDepartment");
        CollectionAssert.AreEqual(new object?[] { "Soporte técnico", 4, 3, 1, 1, 6.0m, 8.0m, 24.0m }, departments.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "Ventas", 2, 1, 0, 1, 1.5m, 1.5m, 24.0m }, departments.Rows[1]);
        Assert.HasCount(2, departments.Rows);
        CollectionAssert.AreEqual(new object?[] { null, 6, 4, 1, 2, 4.0m, 6.4m, 24.0m }, departments.Totals.Single(), "la mediana de todos, no la suma");

        // El ticket de las 23 del 31/10 en Buenos Aires es de octubre aunque en UTC ya sea noviembre.
        var months = report.Table("TicketsByMonth");
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), 4, 3, 1, 1, 6.0m, 8.0m, 24.0m }, months.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), 2, 1, 0, 1, 1.5m, 1.5m, 24.0m }, months.Rows[1]);
        Assert.HasCount(2, months.Rows);
        Assert.AreSame(departments.Totals, months.Totals);
    }

    /// <summary>
    /// Las tablas de soporte con las columnas obligatorias de su migración y las que usa el reporte (20261029155857_Soporte), para probar el reporte antes de que el módulo llegue a esta rama. Con el módulo ya mezclado no
    /// hace nada y la prueba corre contra las tablas de verdad.
    /// </summary>
    private static Task SupportTables(CoreHarness h) => h.Db(db => db.Database.ExecuteSqlRawAsync(
        """
        CREATE SCHEMA IF NOT EXISTS support;
        CREATE TABLE IF NOT EXISTS support.departments (
          id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, name varchar(200) NOT NULL, hidden boolean NOT NULL, clients_only boolean NOT NULL,
          auto_reply boolean NOT NULL, request_feedback boolean NOT NULL, sort_order integer NOT NULL, mail_enabled boolean NOT NULL, imap_port integer NOT NULL,
          imap_security varchar(32) NOT NULL, imap_uid_validity bigint NOT NULL, imap_last_uid bigint NOT NULL, created_at timestamptz NOT NULL);
        CREATE TABLE IF NOT EXISTS support.tickets (
          id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, number varchar(32) NOT NULL UNIQUE, requester_name varchar(200) NOT NULL,
          requester_email varchar(320) NOT NULL, cc text[] NOT NULL, department_id bigint NOT NULL REFERENCES support.departments,
          subject varchar(300) NOT NULL, status varchar(32) NOT NULL, priority varchar(32) NOT NULL, reply_token varchar(32) NOT NULL UNIQUE,
          merged_into_id bigint REFERENCES support.tickets, imported boolean NOT NULL, created_at timestamptz NOT NULL, last_message_at timestamptz NOT NULL, updated_at timestamptz NOT NULL,
          closed_at timestamptz);
        CREATE TABLE IF NOT EXISTS support.messages (
          id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, ticket_id bigint NOT NULL REFERENCES support.tickets, author varchar(32) NOT NULL,
          author_name varchar(200) NOT NULL, body text NOT NULL, internal boolean NOT NULL, source varchar(32) NOT NULL, created_at timestamptz NOT NULL);
        """));

    private static async Task<long> Issuer(CoreHarness h)
    {
        long id = 0;
        await h.Db(async db =>
        {
            var issuer = new IssuerProfile
            {
                TaxId = "30712345678",
                LegalName = "Proveedor de prueba",
                PointOfSale = 3,
                TypeMap = """{"A":{"Invoice":1,"DebitNote":2,"CreditNote":3},"B":{"Invoice":6,"DebitNote":7,"CreditNote":8},"E":{"Invoice":19,"DebitNote":20,"CreditNote":21}}""",
            };
            db.IssuerProfiles.Add(issuer);
            await db.SaveChangesAsync();
            id = issuer.Id;
        });
        return id;
    }

    private static FiscalDocument Voucher(long issuer, long document, int type, long number, DateOnly date, string payload, bool imported = false) => new()
    {
        DocumentId = document,
        IssuerId = issuer,
        PointOfSale = 3,
        TypeCode = type,
        Number = number,
        Date = date,
        AuthorizationCode = $"CAE{number}",
        Payload = JsonDocument.Parse(payload).RootElement.GetRawText(),
        Imported = imported,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
