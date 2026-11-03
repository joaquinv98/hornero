using System.Globalization;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Los listados para exportar: clientes, servicios, facturas y cobros.</summary>
public sealed partial class ReportTests
{
    [TestMethod]
    public async Task Listados_CuentasServiciosFacturasYCobros_ConElEstadoDeCadaUno()
    {
        await using var h = await CoreHarness.Start();
        var first = (await Order(h)).InvoiceId!.Value;
        var usd = await UsdAccount(h);
        var dollars = (await Order(h, usd)).InvoiceId!.Value;
        var note = await CreditNote(h, first);
        await h.Use<PaymentService, long>(async p =>
        {
            await p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-1", Currency.Ars, 5000m, PaymentStatus.Approved, [first]));
            await p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-2", Currency.Ars, 700m, PaymentStatus.Pending));
            return await p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "t-3", Currency.Ars, 300m, PaymentStatus.Rejected));
        });
        long active = 0;
        await h.Db(async db =>
        {
            db.Documents.Add(Imported(900001, h.AccountId, new DateOnly(2026, 10, 5), credited: 1210m, status: "Cancelled"));
            var installation = new ModuleInstallation { Name = "proxmox", BaseUrl = "http://127.0.0.1:9/", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync();
            var cluster = new ModuleInstance { InstallationId = installation.Id, Name = "Cluster Norte", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstances.Add(cluster);
            await db.SaveChangesAsync();
            var product = (await db.Plans.SingleAsync(p => p.Id == h.PlanId)).ProductId;
            var service = new Service(h.AccountId, product, new BillingCycle(CycleUnit.Month), Currency.Ars, 12100m, 1, October)
            {
                ModuleInstanceId = cluster.Id, DiscountPercent = 10m, Label = "vps1.example.com",
            };
            service.ImportState(ServiceStatus.Active, 1, new DateOnly(2026, 12, 1), null, October, null);
            db.Services.Add(service);
            await db.SaveChangesAsync();
            active = service.Id;
        });
        var numbers = await h.Query(db => db.Documents.Where(d => d.Id == first || d.Id == dollars || d.Id == note).ToDictionaryAsync(d => d.Id, d => d.Number!.Value.ToString(CultureInfo.InvariantCulture)));

        var accounts = (await Report(h, r => r.AccountList(default))).Table("AccountList");
        CollectionAssert.AreEqual(
            new object?[] { h.AccountId, "Cliente de prueba", null, "cliente@example.com", null, "AR", null, null, "ReportDocType.Dni", "30111222", 5, "AccountStatus.Active", new DateOnly(2026, 11, 2), "ARS", 1 },
            accounts.Rows[0]);
        CollectionAssert.AreEqual(new object?[] { usd, "Cuenta en dólares", null, "usd@example.com", null, "AR", null, null, "ReportDocType.Dni", "30111223", 5, "AccountStatus.Active", new DateOnly(2026, 11, 2), "USD", 0 }, accounts.Rows[1]);
        Assert.IsNull(accounts.Totals.Single()[10], "la condición frente al IVA no se suma");
        Assert.AreEqual(1L, accounts.Totals.Single()[14]);

        // Los dos pedidos esperan el pago y el activo lleva su descuento.
        var services = (await Report(h, r => r.ServiceList(default))).Table("ServiceList");
        Assert.HasCount(3, services.Rows);
        CollectionAssert.AreEqual(
            new object?[]
            {
                active.ToString(CultureInfo.InvariantCulture), h.AccountId, "Cliente de prueba", null, "VPS 2 GB", "vps1.example.com", "ServiceStatus.Active", 1, "ReportCycle.Month", "ARS",
                10890m, 10890m, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 1), "Cluster Norte", new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10),
            },
            services.Rows[2]);
        Assert.AreEqual("ServiceStatus.Pending", services.Rows[0][6]);

        var invoices = (await Report(h, r => r.InvoiceList(OctoberToNovember, default))).Table("InvoiceList");
        Assert.HasCount(4, invoices.Rows);
        CollectionAssert.AreEqual(
            new object?[] { new DateOnly(2026, 10, 5), "DocumentKind.Invoice", "900001", null, null, h.AccountId, "Cliente de prueba", "ARS", 1210m, 1210m, 0m, 0m, new DateOnly(2026, 10, 5), "InvoiceStatus.Cancelled" },
            invoices.Rows[0]);
        CollectionAssert.AreEqual(
            new object?[] { new DateOnly(2026, 11, 2), "DocumentKind.Invoice", numbers[first], null, null, h.AccountId, "Cliente de prueba", "ARS", 12100m, 1210m, 5000m, 5890m, new DateOnly(2026, 11, 9), "InvoiceStatus.PartiallyPaid" },
            invoices.Rows[1]);
        CollectionAssert.AreEqual(
            new object?[] { new DateOnly(2026, 11, 2), "DocumentKind.Invoice", numbers[dollars], null, null, usd, "Cuenta en dólares", "USD", 10m, 0m, 0m, 10m, new DateOnly(2026, 11, 9), "InvoiceStatus.Issued" },
            invoices.Rows[2]);
        Assert.AreEqual(numbers[note], invoices.Rows[3][2]);
        Assert.AreEqual(-1210m, invoices.Rows[3][8], "la nota de crédito resta");
        Assert.IsNull(invoices.Rows[3][13]);
        CollectionAssert.AreEqual(new object?[] { 12100m, 2420m, 5000m, 5890m }, invoices.Totals[0][8..12]);

        // Todos los estados en las filas, y en el pie solo lo que se cobró.
        var payments = (await Report(h, r => r.PaymentList(OctoberToNovember, default))).Table("PaymentList");
        Assert.HasCount(3, payments.Rows);
        CollectionAssert.AreEqual(
            new object?[] { new DateOnly(2026, 11, 2), h.AccountId, "Cliente de prueba", "transferencia (manual)", "t-1", "PaymentStatus.Approved", "ARS", 5000m, 0m, 0m, 5000m, numbers[first] },
            payments.Rows[0]);
        CollectionAssert.AreEqual(new[] { "PaymentStatus.Pending", "PaymentStatus.Rejected" }, payments.Rows.Skip(1).Select(r => (string)r[5]!).ToArray());
        CollectionAssert.AreEqual(new object?[] { "ARS", 5000m, 0m, 0m, 5000m }, payments.Totals.Single()[6..11]);
    }
}
