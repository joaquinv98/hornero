using BillingOss.App.Platform;
using BillingOss.App.Reports;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Los reportes de ingresos que miran años, días y lo que viene: ingresos por año, actividad diaria y proyección.</summary>
public sealed partial class ReportTests
{
    [TestMethod]
    public async Task IngresosPorAno_NetoDeCadaMesConUnAnoPorFila_YLosMesesFueraDelPeriodoVacios()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        h.Clock.Now = October;
        await Order(h);
        h.Clock.Now = now;
        await Order(h);
        await h.Db(async db =>
        {
            db.Documents.Add(Imported(900001, h.AccountId, new DateOnly(2025, 3, 10)));
            db.Documents.Add(Imported(900002, h.AccountId, new DateOnly(2026, 3, 5)));
            db.Documents.Add(Imported(900003, h.AccountId, new DateOnly(2024, 12, 20)));
            await db.SaveChangesAsync();
        });

        var report = await Report(h, r => r.RevenueByYear(new MonthRange(new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 1)), default));

        var years = report.Table("RevenueByYear");
        Assert.HasCount(2, years.Rows, "la de 2024 queda afuera");
        CollectionAssert.AreEqual(new object?[] { "ARS", "2025", 0m, 0m, 1000m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 1000m }, years.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "ARS", "2026", 0m, 0m, 1000m, 0m, 0m, 0m, 0m, 0m, 0m, 10000m, 10000m, null, 21000m }, years.Rows[1], "diciembre de 2026 está fuera del período");
        Assert.AreEqual(22000m, years.Totals.Single()[14]);
        Assert.AreEqual("Year", report.Charts.Single().Category);
    }

    [TestMethod]
    public async Task ActividadDiaria_CadaDiaDelPeriodo_ConCuentasPedidosFacturasYCobros()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        h.Clock.Now = new DateTimeOffset(2026, 11, 5, 13, 0, 0, TimeSpan.Zero);
        var invoice = (await Order(h)).InvoiceId!.Value;
        await CreditNote(h, invoice);
        await h.Use<PaymentService, long>(async p =>
        {
            await p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-2", Currency.Ars, 700m, PaymentStatus.Pending));
            return await p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-1", Currency.Ars, 5000m, PaymentStatus.Approved, [invoice]));
        });

        var report = await Report(h, r => r.DailyActivity(new MonthRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1)), default));

        // Todos los días del mes, con cero en los que no pasó nada, también en los tickets del soporte.
        var days = report.Table("DailyActivity");
        Assert.HasCount(30, days.Rows);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), 0, 0, 0, 0, 0, 0, 0, 0 }, days.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 2), 1, 1, 1, 0, 0, 0, 0, 0 }, days.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 5), 0, 1, 1, 1, 1, 0, 0, 0 }, days.Rows[4], "el cobro pendiente no cuenta");
        CollectionAssert.AreEqual(new object?[] { null, 1L, 2L, 2L, 1L, 1L, 0L, 0L, 0L }, days.Totals.Single());

        var amounts = report.Table("DailyAmounts");
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 2), "ARS", 12100m, 0m, 0m }, amounts.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 5), "ARS", 12100m, 1210m, 5000m }, amounts.Rows[1]);
        Assert.HasCount(2, amounts.Rows);
        CollectionAssert.AreEqual(new[] { "DailyCollected", "DailyOrders" }, report.Charts.Select(c => c.Id).ToArray());
    }

    [TestMethod]
    public async Task Proyeccion_RenovacionesPorMesYServidor_ConDescuentoPorCiclosYBajaProgramada()
    {
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            var product = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            var proxmox = new ModuleInstallation { Name = "proxmox", BaseUrl = "http://127.0.0.1:9/", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstallations.Add(proxmox);
            await db.SaveChangesAsync();
            var cluster = new ModuleInstance { InstallationId = proxmox.Id, Name = "Cluster Norte", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstances.Add(cluster);
            await db.SaveChangesAsync();

            Service Service(Currency currency, decimal price, DateOnly next, CycleUnit unit = CycleUnit.Month, ServiceStatus status = ServiceStatus.Active, long? instance = null)
            {
                var s = new Service(h.AccountId, product, new BillingCycle(unit), currency, price, 1, October) { ModuleInstanceId = instance };
                s.ImportState(status, next.Day, next, next, October, null);
                return s;
            }

            var discounted = Service(Currency.Ars, 5000m, new DateOnly(2026, 11, 20));
            discounted.DiscountPercent = 10m;
            discounted.DiscountCyclesLeft = 2;
            var leaving = Service(Currency.Usd, 10m, new DateOnly(2026, 12, 5));
            leaving.CancelAt = new DateOnly(2027, 2, 5);
            db.Services.AddRange(
                Service(Currency.Ars, 12100m, new DateOnly(2026, 11, 15), instance: cluster.Id),
                Service(Currency.Ars, 1000m, new DateOnly(2026, 10, 1), instance: cluster.Id),
                Service(Currency.Ars, 120000m, new DateOnly(2027, 3, 1), CycleUnit.Year),
                discounted,
                leaving,
                Service(Currency.Ars, 12100m, new DateOnly(2026, 11, 10), status: ServiceStatus.Suspended),
                Service(Currency.Ars, 5000m, new DateOnly(2026, 11, 10), CycleUnit.OneTime));
            await db.SaveChangesAsync();
        });

        var report = await Report(h, r => r.Forecast(new MonthRange(new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 1)), default));

        // El atrasado de octubre renueva desde noviembre, el descuento dura dos ciclos, el anual cae en marzo y la baja del
        // 5/2 deja dos renovaciones en dólares. El suspendido y el pago único no renuevan.
        var months = report.Table("ForecastMonths");
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ARS", 3, 17600m }, months.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 12, 1), "ARS", 3, 17600m }, months.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 12, 1), "USD", 1, 10m }, months.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2027, 1, 1), "ARS", 3, 18100m }, months.Rows[3]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2027, 3, 1), "ARS", 4, 138100m }, months.Rows[6]);
        Assert.HasCount(14, months.Rows);
        CollectionAssert.AreEqual(new object?[] { null, "ARS", 37L, 336200m }, months.Totals[0]);
        CollectionAssert.AreEqual(new object?[] { null, "USD", 2L, 20m }, months.Totals[1]);

        var servers = report.Table("ForecastServers");
        CollectionAssert.AreEqual(new object?[] { null, null, "ARS", 2, 13, 179000m }, servers.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "Cluster Norte", "proxmox", "ARS", 2, 24, 157200m }, servers.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { null, null, "USD", 1, 2, 20m }, servers.Rows[2]);
        Assert.AreEqual("ForecastSuspended", report.Notes.Single().Key);
        Assert.AreEqual("1", report.Notes.Single().Args.Single());
    }
}
