using System.Text;
using BillingOss.App.Platform;
using BillingOss.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Retención de los avisos de pasarela y sus eventos (punto 14 de la revisión de alta disponibilidad).</summary>
[TestClass]
public sealed class RetentionTests
{
    [TestMethod]
    public async Task Retencion_VaciaLosCuerposViejosYaProcesados_YBorraLosEventosViejos()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        long old = 0, recent = 0, unprocessed = 0;
        await h.Db(async db =>
        {
            var installation = new ModuleInstallation { Name = "pasarela", BaseUrl = "http://127.0.0.1:1/", CreatedAt = now };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync();
            var instance = new ModuleInstance { InstallationId = installation.Id, Name = "instancia", CreatedAt = now };
            db.ModuleInstances.Add(instance);
            await db.SaveChangesAsync();
            WebhookReceipt Receipt(ReceiptStatus status, int daysAgo, string body) => new()
            {
                ModuleInstanceId = instance.Id,
                Status = status,
                ReceivedAt = now.AddDays(-daysAgo),
                Body = Encoding.UTF8.GetBytes(body),
            };
            var a = Receipt(ReceiptStatus.Verified, 100, "viejo");
            var b = Receipt(ReceiptStatus.Verified, 10, "nuevo");
            var c = Receipt(ReceiptStatus.Received, 100, "sin verificar");
            db.WebhookReceipts.AddRange(a, b, c);
            await db.SaveChangesAsync();
            db.GatewayEvents.Add(new GatewayEvent { ModuleInstanceId = instance.Id, EventId = "viejo", Type = "payment", ReceiptId = a.Id, CreatedAt = now.AddDays(-401) });
            db.GatewayEvents.Add(new GatewayEvent { ModuleInstanceId = instance.Id, EventId = "nuevo", Type = "payment", ReceiptId = b.Id, CreatedAt = now.AddDays(-10) });
            await db.SaveChangesAsync();
            (old, recent, unprocessed) = (a.Id, b.Id, c.Id);
        });
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(RetentionHandler.JobKind, new { });
            return true;
        });

        await h.RunJobs();

        var receipts = await h.Query(db => db.WebhookReceipts.AsNoTracking().ToDictionaryAsync(r => r.Id, r => Encoding.UTF8.GetString(r.Body)));
        Assert.AreEqual(string.Empty, receipts[old], "El cuerpo viejo ya procesado se vacía.");
        Assert.AreEqual("nuevo", receipts[recent]);
        Assert.AreEqual("sin verificar", receipts[unprocessed], "Un aviso sin procesar conserva el cuerpo.");
        Assert.AreEqual(3, receipts.Count, "Las filas quedan.");
        Assert.AreEqual("nuevo", (await h.Query(db => db.GatewayEvents.AsNoTracking().SingleAsync())).EventId);
    }
}
