using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Avisos de vencimiento de dominios: a quién, cuándo y una sola vez por umbral, con la corrida diaria.</summary>
[TestClass]
public sealed class DomainNoticeTests
{
    /// <summary>Un dominio activo pago hasta <paramref name="paidThrough"/>, en el registrar manual.</summary>
    private static async Task<long> Domain(CoreHarness h, string name, DateOnly paidThrough, bool autoRenew = true)
    {
        long id = 0;
        await h.Db(async db =>
        {
            var instance = await db.ModuleInstances.Select(i => (long?)i.Id).FirstOrDefaultAsync()
                ?? await ManualModule.AddInstance(db, "nic-ar-manual", new JsonObject { ["queue"] = "dominios", ["registry"] = "nic.ar" }, h.Clock.Now);
            var domain = new RegisteredDomain(h.AccountId, name, instance, Currency.Ars, 9000m, h.Clock.Now) { AutoRenew = autoRenew };
            domain.Start(paidThrough);
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            id = domain.Id;
        });
        return id;
    }

    /// <summary>
    /// Un día: la facturación de todas las cuentas y después el planificador con los avisos. El planificador reparte la
    /// facturación en un trabajo por cuenta que puede correr después de los avisos, y entonces el dominio que se factura
    /// ese día recibe el aviso al siguiente (la factura le llega ese mismo día). Acá la facturación va primero, como en
    /// un día en que las cuentas ya se facturaron.
    /// </summary>
    private static async Task Day(CoreHarness h, int year, int month, int day)
    {
        h.Clock.Now = new DateTimeOffset(year, month, day, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(r => r.Run());
        await h.Tick();
        await h.RunJobs();
    }

    private static string[] Keys(CoreHarness h) => [.. h.DomainMail.Sent.Keys.Order(StringComparer.Ordinal)];

    [TestMethod]
    public async Task Avisos_PorLaColaDeCorreo_ConSuPlantillaEnElIdiomaDeLaCuenta_YUnaVezPorClave()
    {
        await using var h = await CoreHarness.Start();
        var data = new DomainNoticeData(h.AccountId, "cliente@example.com", "es", "Ana & Cía", 7, "aviso.com.ar", new DateOnly(2026, 11, 6), 4, false, 5000m, Currency.Ars, null, new DateOnly(2026, 12, 21));

        // Fuera de las pruebas, que capturan los avisos, el núcleo los manda por el módulo de notificaciones.
        await using (var plain = new ServiceCollection().AddLogging().AddBillingCore(h.ConnectionString).BuildServiceProvider())
        await using (var scope = plain.CreateAsyncScope())
        {
            Assert.IsInstanceOfType<NotifierDomainMail>(scope.ServiceProvider.GetRequiredService<IDomainMail>());
        }

        await h.Scope(async sp =>
        {
            var mail = new NotifierDomainMail(sp.GetRequiredService<Notifier>());
            await mail.Send(DomainNotice.Expiring, data, "domain-expiring:7:2026-11-06:7", CancellationToken.None);
            await mail.Send(DomainNotice.Expiring, data, "domain-expiring:7:2026-11-06:7", CancellationToken.None);
            await mail.Send(DomainNotice.Renewed, data with { Language = "en" }, "domain-renewed:7:1", CancellationToken.None);
            return true;
        });

        var emails = await h.Query(db => db.Set<OutboundEmail>().OrderBy(e => e.Id).ToListAsync());
        Assert.HasCount(2, emails, "Con la misma clave no se arma dos veces.");
        Assert.AreEqual(("domain-expiring", "es-AR", "cliente@example.com", h.AccountId), (emails[0].Template, emails[0].Culture, emails[0].Recipient, emails[0].AccountId!.Value));
        Assert.AreEqual("Tu dominio aviso.com.ar vence el 06/11/2026", emails[0].Subject);
        StringAssert.Contains(emails[0].Html, "ARS 5.000,00");
        StringAssert.Contains(emails[0].Html, "Ana &amp; C");
        Assert.AreEqual(("domain-renewed", "en", "Your domain aviso.com.ar has been renewed"), (emails[1].Template, emails[1].Culture, emails[1].Subject));
    }

    [TestMethod]
    public async Task Avisos_SoloALosQueNoSeRenuevanSolos_UnoPorUmbral_YEnLaGracia()
    {
        await using var h = await CoreHarness.Start();
        var off = await Domain(h, "sin-renovacion.com.ar", new DateOnly(2026, 11, 20), autoRenew: false);
        await Domain(h, "se-renueva.com.ar", new DateOnly(2026, 11, 25));
        var unpaid = await Domain(h, "sin-pagar.com.ar", new DateOnly(2026, 11, 6));
        var grace = await Domain(h, "vencido.com.ar", new DateOnly(2026, 10, 30), autoRenew: false);

        await Day(h, 2026, 11, 2);
        await h.Tick();
        await h.RunJobs();

        CollectionAssert.AreEquivalent(
            new[]
            {
                $"domain-expired:{grace}:2026-10-30:1",
                $"domain-expiring:{off}:2026-11-20:30",
                $"domain-expiring:{unpaid}:2026-11-06:7",
            },
            Keys(h));
        var (notice, data) = h.DomainMail.Sent[$"domain-expiring:{unpaid}:2026-11-06:7"];
        Assert.AreEqual((DomainNotice.Expiring, 4, true), (notice, data.Days, data.AutoRenew));
        Assert.IsNotNull(data.InvoiceId, "La renovación facturada va con su factura para pagarla.");
        Assert.AreEqual(("cliente@example.com", "Cliente de prueba", "sin-pagar.com.ar", 9000m), (data.Email, data.AccountName, data.Domain, data.Amount));
        var expired = h.DomainMail.Sent[$"domain-expired:{grace}:2026-10-30:1"];
        Assert.AreEqual((DomainNotice.Expired, 3, new DateOnly(2026, 12, 14)), (expired.Notice, expired.Data.Days, expired.Data.GraceUntil));
        Assert.IsNull(h.DomainMail.Sent[$"domain-expiring:{off}:2026-11-20:30"].Data.InvoiceId);
    }

    [TestMethod]
    public async Task Avisos_CadaUmbralSaleUnaVez_YSiUnDiaNoCorrioSaleAlSiguiente()
    {
        await using var h = await CoreHarness.Start();
        var id = await Domain(h, "sin-renovacion.com.ar", new DateOnly(2026, 11, 20), autoRenew: false);

        await Day(h, 2026, 11, 2);
        await Day(h, 2026, 11, 3);
        await Day(h, 2026, 11, 15); // No corrió el 13 ni el 14: el umbral de 7 días sale igual, una vez.
        await Day(h, 2026, 11, 16);
        await Day(h, 2026, 11, 20);
        await Day(h, 2026, 11, 21);
        await Day(h, 2026, 12, 20);

        CollectionAssert.AreEqual(
            new[]
            {
                $"domain-expired:{id}:2026-11-20:1",
                $"domain-expired:{id}:2026-11-20:30",
                $"domain-expiring:{id}:2026-11-20:1",
                $"domain-expiring:{id}:2026-11-20:30",
                $"domain-expiring:{id}:2026-11-20:7",
            },
            Keys(h));
        Assert.AreEqual(7, h.DomainMail.Requests, "La corrida pide el aviso cada día de la ventana: la clave lo deja en uno.");
        Assert.AreEqual(5, h.DomainMail.Sent[$"domain-expiring:{id}:2026-11-20:7"].Data.Days);
        Assert.AreEqual(0, h.DomainMail.Sent[$"domain-expiring:{id}:2026-11-20:1"].Data.Days, "El día que vence también avisa.");
    }

    [TestMethod]
    public async Task Avisos_LlevanLaFacturaDeLaRenovacion_NoLaDeUnaOpcionContratadaDespues()
    {
        await using var h = await CoreHarness.Start();
        var id = await Domain(h, "sin-pagar.com.ar", new DateOnly(2026, 11, 6));

        await Day(h, 2026, 11, 2);
        var renewal = h.DomainMail.Sent[$"domain-expiring:{id}:2026-11-06:7"].Data.InvoiceId;
        var option = await h.DomainInvoice(id, LineKind.OneTime, 1590m, "Gestión de DNS del dominio sin-pagar.com.ar");
        await Day(h, 2026, 11, 5);

        Assert.IsNotNull(renewal);
        Assert.AreNotEqual(renewal, option);
        Assert.AreEqual(renewal, h.DomainMail.Sent[$"domain-expiring:{id}:2026-11-06:1"].Data.InvoiceId, "Pagar la opción no renueva: el aviso lleva a la renovación.");
    }

    [TestMethod]
    public async Task Avisos_AlPagarseLaRenovacionNoSiguen_YLosUmbralesSeConfiguran()
    {
        await using var h = await CoreHarness.Start(configure: o =>
        {
            o.DomainExpiryNoticeDays = [10];
            o.DomainGraceNoticeDays = [5];
        });
        var id = await Domain(h, "paga.com.ar", new DateOnly(2026, 11, 6));

        await Day(h, 2026, 11, 2);
        Assert.AreEqual($"domain-expiring:{id}:2026-11-06:10", Keys(h).Single());
        var invoice = h.DomainMail.Sent.Values.Single().Data.InvoiceId!.Value;
        await h.Use<PaymentService, long>(p => p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 9000m, PaymentStatus.Approved, [invoice])));
        await h.RunJobs();
        await Day(h, 2026, 11, 8);
        await Day(h, 2026, 11, 12);

        Assert.AreEqual(1, h.DomainMail.Sent.Count, "Paga, se renueva sola: no hay más avisos de ese vencimiento.");
    }
}
