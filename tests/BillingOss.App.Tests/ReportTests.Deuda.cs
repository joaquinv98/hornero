using BillingOss.App.Reports;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Los reportes de deuda y saldo a favor: el estado de cuenta, los servicios suspendidos y los movimientos de saldo.</summary>
public sealed partial class ReportTests
{
    [TestMethod]
    public async Task EstadoDeCuenta_SaldoInicialMovimientosYCierreQueCoincideConLosDocumentos()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        h.Clock.Now = October;
        var first = (await Order(h)).InvoiceId!.Value;
        h.Clock.Now = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
        await h.Use<PaymentService, long>(p => p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-1", Currency.Ars, 5000m, PaymentStatus.Approved, [first])));
        h.Clock.Now = now;
        var second = (await Order(h)).InvoiceId!.Value;

        // Paga las dos y sobran 800, la nota sobre la segunda ya paga deja 1210 más a favor y el staff suma 500.
        await h.Use<PaymentService, long>(p => p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-2", Currency.Ars, 20000m, PaymentStatus.Approved)));
        var note = await CreditNote(h, second);
        await h.Db(async db =>
        {
            db.Documents.Add(Imported(900001, h.AccountId, new DateOnly(2026, 9, 5), credited: 1210m, status: "Cancelled"));
            db.CreditEntries.Add(new CreditEntry(h.AccountId, Currency.Ars, 500m, "Ajuste del staff", h.Clock.Now));
            await db.SaveChangesAsync();
        });
        var numbers = await h.Query(db => db.Documents.Where(d => d.Id == first || d.Id == second || d.Id == note).ToDictionaryAsync(d => d.Id, d => d.Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var report = await Report(h, r => r.Statement(h.AccountId, OctoberToNovember, default));

        // La importada de septiembre y su anulación sin nota se compensan antes del período.
        var movements = report.Table("StatementMovements").Rows;
        Assert.HasCount(7, movements);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 1), "ReportConcept.Opening", null, "ARS", null, null, 0m }, movements[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 10), "DocumentKind.Invoice", numbers[first], "ARS", 12100m, null, 12100m }, movements[1]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 20), "ReportConcept.Payment", "transferencia (manual) t-1", "ARS", null, 5000m, 7100m }, movements[2]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 2), "DocumentKind.Invoice", numbers[second], "ARS", 12100m, null, 19200m }, movements[3]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 2), "DocumentKind.CreditNote", numbers[note], "ARS", null, 1210m, 17990m }, movements[4]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 2), "ReportConcept.Payment", "transferencia (manual) t-2", "ARS", null, 20000m, -2010m }, movements[5]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 2), "ReportConcept.CreditAdjustment", "Ajuste del staff", "ARS", null, 500m, -2510m }, movements[6]);

        // El cierre es lo que dicen hoy las facturas y el saldo a favor: no debe nada y tiene 2510 a favor.
        CollectionAssert.AreEqual(new object?[] { h.AccountId, "Cliente de prueba", "ARS", 0m, 24200m, 26710m, -2510m, -2510m }, report.Table("StatementSummary").Rows.Single());
        Assert.IsEmpty(report.Notes);

        // Desde noviembre, octubre queda en el saldo inicial.
        var november = await Report(h, r => r.Statement(h.AccountId, new MonthRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1)), default));
        CollectionAssert.AreEqual(new object?[] { h.AccountId, "Cliente de prueba", "ARS", 7100m, 12100m, 21710m, -2510m, -2510m }, november.Table("StatementSummary").Rows.Single());

        var missing = await Report(h, r => r.Statement(999999, OctoberToNovember, default));
        Assert.AreEqual("StatementNoAccount", missing.Notes.Single().Key);
        Assert.IsEmpty(missing.Tables);
    }

    [TestMethod]
    public async Task SuspendidosYSaldoAFavor_BajaPrevistaPorMoraYMovimientosDelPeriodo()
    {
        await using var h = await CoreHarness.Start(configure: o => o.TerminationDays = 14);
        await Order(h);
        long mora = 0;
        long staff = 0;
        await h.Db(async db =>
        {
            var product = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            Service Suspended(int anchor, DateOnly next, DateOnly paid)
            {
                var s = new Service(h.AccountId, product, new BillingCycle(CycleUnit.Month), Currency.Ars, 12100m, 1, October);
                s.ImportState(ServiceStatus.Suspended, anchor, next, paid, October, null);
                return s;
            }

            var byLateness = Suspended(15, new DateOnly(2026, 11, 15), new DateOnly(2026, 10, 15));
            var byStaff = Suspended(10, new DateOnly(2026, 12, 10), new DateOnly(2026, 11, 10));
            byStaff.SuspendReason = "Abuso";
            byStaff.CancelAt = new DateOnly(2026, 12, 1);
            var active = new Service(h.AccountId, product, new BillingCycle(CycleUnit.Month), Currency.Ars, 12100m, 1, October);
            active.ImportState(ServiceStatus.Active, 1, new DateOnly(2026, 12, 1), null, October, null);
            db.Services.AddRange(byLateness, byStaff, active);
            db.CreditEntries.AddRange(
                new CreditEntry(h.AccountId, Currency.Ars, 1000m, "Saldo importado", new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero)),
                new CreditEntry(h.AccountId, Currency.Ars, 300m, "Saldo importado", new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)),
                new CreditEntry(h.AccountId, Currency.Ars, -100m, "Ajuste", new DateTimeOffset(2026, 11, 1, 2, 30, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
            (mora, staff) = (byLateness.Id, byStaff.Id);
        });
        var usd = await UsdAccount(h);
        var dollars = (await Order(h, usd)).InvoiceId!.Value;
        await h.Use<PaymentService, long>(p => p.Record(new PaymentInput(usd, "manual:transferencia", "t-3", Currency.Usd, 15m, PaymentStatus.Approved, [dollars])));

        var suspended = (await Report(h, r => r.Suspended(default))).Table("SuspendedServices");

        // Por falta de pago se da de baja a los 14 días de lo pagado. La del staff no, pero tiene la baja programada.
        Assert.HasCount(2, suspended.Rows);
        CollectionAssert.AreEqual(
            new object?[] { mora.ToString(System.Globalization.CultureInfo.InvariantCulture), h.AccountId, "Cliente de prueba", "VPS 2 GB", null, null, "ARS", 12100m, new DateOnly(2026, 10, 15), 18, 12100m, new DateOnly(2026, 10, 29) },
            suspended.Rows[0]);
        CollectionAssert.AreEqual(
            new object?[] { staff.ToString(System.Globalization.CultureInfo.InvariantCulture), h.AccountId, "Cliente de prueba", "VPS 2 GB", null, "Abuso", "ARS", 12100m, new DateOnly(2026, 11, 10), 0, 12100m, new DateOnly(2026, 12, 1) },
            suspended.Rows[1]);
        Assert.AreEqual(24200m, suspended.Totals.Single()[7]);
        Assert.IsNull(suspended.Totals.Single()[10], "la deuda es de la cuenta: sumarla la contaría dos veces");

        var credit = await Report(h, r => r.Credit(OctoberToNovember, default));

        CollectionAssert.AreEqual(new object?[] { "ARS", 1000m, 300m, -100m, 1200m }, credit.Table("CreditSummary").Rows[0]);
        CollectionAssert.AreEqual(new object?[] { "USD", 0m, 5m, 0m, 5m }, credit.Table("CreditSummary").Rows[1]);
        var entries = credit.Table("CreditEntries");
        Assert.HasCount(3, entries.Rows);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 5), h.AccountId, "Cliente de prueba", "ARS", 300m, "Saldo importado", null, null, null }, entries.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 10, 31), h.AccountId, "Cliente de prueba", "ARS", -100m, "Ajuste", null, null, null }, entries.Rows[1], "a las 2:30 del 1/11 en UTC todavía es octubre en Buenos Aires");
        CollectionAssert.AreEqual(new object?[] { new DateOnly(2026, 11, 2), usd, "Cuenta en dólares", "USD", 5m, "Pago sin imputar", null, null, "transferencia (manual) t-3" }, entries.Rows[2]);
        CollectionAssert.AreEqual(new object?[] { h.AccountId, "Cliente de prueba", "ARS", 1200m }, credit.Table("CreditBalances").Rows[0]);
        CollectionAssert.AreEqual(new object?[] { usd, "Cuenta en dólares", "USD", 5m }, credit.Table("CreditBalances").Rows[1]);
    }
}
