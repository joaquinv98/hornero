using BillingOss.App.Reports;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Los reportes de clientes: los que más facturan, por país, los pedidos por mes y la permanencia.</summary>
public sealed partial class ReportTests
{
    /// <summary>Factura importada de octubre con ese neto al 21 % y el receptor que se congeló, como la deja el importador.</summary>
    private static Document ImportedNet(long id, long account, decimal net, string receiver = "{}") =>
        Document.FromImport(id, DocumentKind.Invoice, account, Currency.Ars, id, new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 7), new TaxBreakdown([new AliquotTotal(0.21m, net, net * 0.21m)], [], 0m, 0m), 0m, 0m, receiver, DateTimeOffset.UtcNow, sourceStatus: "Unpaid");

    [TestMethod]
    public async Task ClientesQueMasFacturanYPorPais_DiezPorMonedaYElPaisDelReceptor()
    {
        await using var h = await CoreHarness.Start();
        var accounts = new List<long>();
        await h.Db(async db =>
        {
            var product = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            for (var i = 1; i <= 11; i++)
            {
                var account = new Account($"Cuenta {i}", $"cuenta{i}@example.com", Currency.Ars, h.Clock.Now);
                db.Accounts.Add(account);
                await db.SaveChangesAsync();
                accounts.Add(account.Id);
                if (i <= 2)
                {
                    db.TaxIdentities.Add(new TaxIdentity(account.Id, TaxDocumentType.Dni, $"3011130{i}", $"Cuenta {i}", 5, "UY", h.Clock.Now));
                }

                db.Documents.Add(ImportedNet(900000 + i, account.Id, 100m * i));
            }

            var live = new Service(accounts[0], product, new BillingCycle(CycleUnit.Month), Currency.Ars, 12100m, 1, October);
            live.ImportState(ServiceStatus.Active, 1, new DateOnly(2026, 11, 1), null, October, null);
            db.Services.Add(live);
            db.Documents.Add(ImportedNet(900100, h.AccountId, 500m, """{"Country":"ES"}"""));
            await db.SaveChangesAsync();
        });
        await Order(h);
        var usd = await UsdAccount(h);
        await Order(h, usd);

        var top = (await Report(h, r => r.TopAccounts(OctoberToNovember, default))).Table("TopAccounts");

        // Diez en pesos, de mayor a menor neto, con su parte de los 17.100 de la moneda. Las dos más chicas quedan afuera.
        Assert.HasCount(11, top.Rows);
        CollectionAssert.AreEqual(new object?[] { "ARS", 1, h.AccountId, "Cliente de prueba", 2, 10500m, 12705m, 61.40m }, top.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "ARS", 2, accounts[10], "Cuenta 11", 1, 1100m, 1331m, 6.43m }, top.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { "ARS", 10, accounts[2], "Cuenta 3", 1, 300m, 363m, 1.75m }, top.Rows[9]);
        CollectionAssert.AreEqual(new object?[] { "USD", 1, usd, "Cuenta en dólares", 1, 8.26m, 10m, 100m }, top.Rows[10]);

        var report = await Report(h, r => r.Countries(OctoberToNovember, default));

        // Nueve cuentas sin identidad fiscal no tienen país. La del pedido en dólares es del país.
        var byCountry = report.Table("AccountsByCountry");
        CollectionAssert.AreEqual(new object?[] { null, 9, 0, 9 }, byCountry.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "AR", 2, 0, 2 }, byCountry.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { "UY", 2, 1, 2 }, byCountry.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { null, 13L, 1L, 13L }, byCountry.Totals.Single());
        Assert.AreEqual("ReportCol.NoCountry", byCountry.Columns[0].Empty);

        // El país del documento es el del receptor congelado, y en un importado sin receptor, el de la cuenta.
        var revenue = report.Table("RevenueByCountry");
        Assert.HasCount(5, revenue.Rows);
        CollectionAssert.AreEqual(new object?[] { "AR", "ARS", 1, 10000m, 12100m }, revenue.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { null, "ARS", 9, 6300m, 7623m }, revenue.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { "ES", "ARS", 1, 500m, 605m }, revenue.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { "UY", "ARS", 2, 300m, 363m }, revenue.Rows[3]);
        CollectionAssert.AreEqual(new object?[] { "AR", "USD", 1, 8.26m, 10m }, revenue.Rows[4]);
    }

    [TestMethod]
    public async Task PedidosPorMesYPermanencia_EstadosDeHoyYTramosConLaMediana()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        h.Clock.Now = October;
        await Order(h);
        h.Clock.Now = now;
        await Order(h);
        await Order(h, await UsdAccount(h));
        await h.Db(async db =>
        {
            // A las 23:30 del 31/10 en Buenos Aires: es de octubre.
            db.Orders.AddRange(
                new Order(h.AccountId, Currency.Ars, new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)) { Status = OrderStatus.Cancelled },
                new Order(h.AccountId, Currency.Ars, new DateTimeOffset(2026, 11, 1, 2, 30, 0, TimeSpan.Zero)) { Status = OrderStatus.Accepted },
                new Order(h.AccountId, Currency.Ars, new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)) { Status = OrderStatus.Completed });

            var product = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            var count = 0;
            async Task Customer(params (ServiceStatus Status, DateTimeOffset? Activated, DateTimeOffset? Terminated)[] services)
            {
                var account = new Account("Permanencia", $"permanencia{++count}@example.com", Currency.Ars, October);
                db.Accounts.Add(account);
                await db.SaveChangesAsync();
                foreach (var (status, activated, terminated) in services)
                {
                    var s = new Service(account.Id, product, new BillingCycle(CycleUnit.Month), Currency.Ars, 12100m, 1, activated ?? October);
                    s.ImportState(status, 1, null, null, activated, terminated);
                    db.Services.Add(s);
                }

                await db.SaveChangesAsync();
            }

            static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);
            await Customer((ServiceStatus.Active, At(2025, 11, 2), null), (ServiceStatus.Terminated, At(2026, 1, 1), At(2026, 2, 1)));
            await Customer((ServiceStatus.Suspended, At(2026, 10, 1), null));
            await Customer((ServiceStatus.Active, At(2026, 5, 2), null));
            await Customer((ServiceStatus.Terminated, At(2026, 1, 15), At(2026, 5, 20)));
            await Customer((ServiceStatus.Terminated, At(2022, 1, 1), At(2025, 6, 1)));
            await Customer((ServiceStatus.Terminated, At(2025, 1, 1), At(2025, 3, 15)));
            await Customer((ServiceStatus.Terminated, At(2024, 1, 1), null));
            await Customer((ServiceStatus.Cancelled, null, null));
        });

        var orders = (await Report(h, r => r.Orders(OctoberToNovember, default))).Table("OrdersByMonth");

        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "ARS", 3, 1, 1, 0, 1, 12100m }, orders.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "ARS", 2, 1, 0, 1, 0, 12100m }, orders.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 1), "USD", 1, 1, 0, 0, 0, 10m }, orders.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { null, "ARS", 5L, 2L, 1L, 1L, 1L, 24200m }, orders.Totals[0]);

        var retention = await Report(h, r => r.Retention(default));

        // Al 2/11/2026 siguen la de un año (con una baja vieja que no corta), la de un mes y la de seis. Se fueron la de
        // cuatro meses, la de tres años y cinco meses y la de dos. La de la baja sin fecha no se mide y la cancelada nunca
        // contrató.
        var buckets = retention.Table("RetentionBuckets");
        CollectionAssert.AreEqual(new object?[] { "ReportCustomers.Current", 3, 1, 0, 1, 1, 0, 0, 6 }, buckets.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "ReportCustomers.Former", 3, 1, 1, 0, 0, 0, 1, 4 }, buckets.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { null, 6, 2, 1, 1, 1, 0, 1, 5 }, buckets.Totals.Single(), "la mediana de todas, no la suma");

        var cohorts = retention.Table("RetentionCohorts");
        Assert.HasCount(3, cohorts.Rows);
        CollectionAssert.AreEqual(new object?[] { "2022", 1, 0, 0m, 41 }, cohorts.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "2025", 2, 1, 50m, 7 }, cohorts.Rows[1]);
        CollectionAssert.AreEqual(new object?[] { "2026", 3, 2, 66.67m, 4 }, cohorts.Rows[2]);
        Assert.AreEqual("1", retention.Notes.Single(n => n.Key == "RetentionUndated").Args.Single());
    }
}
