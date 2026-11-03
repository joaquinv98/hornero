using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Operaciones de servicio que el estado no admite (sección 5.1 del modelo): mueren a la vista y no traban la cola del servicio.</summary>
[TestClass]
public sealed class ServiceOperationTests
{
    [TestMethod]
    public async Task OperacionQueElEstadoNoAdmite_MuereALaVista_YNoFrenaElAltaQueEspera()
    {
        await using var h = await CoreHarness.Start();
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        var serviceId = placed.ServiceIds.Single();

        // Una suspensión encolada antes de que corra el alta, con la misma clave de entidad que el alta usará después.
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(serviceId, "suspend", 1), $"service:{serviceId}", $"service:{serviceId}:suspend:1");
            return true;
        });
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();

        // El alta corrió enseguida en vez de esperar horas detrás de los reintentos de la suspensión imposible.
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId))).Status);
        var suspend = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == $"service:{serviceId}:suspend:1"));
        Assert.AreEqual(JobStatus.Dead, suspend.Status);
        Assert.AreEqual(1, suspend.Attempts);
        StringAssert.Contains(suspend.LastError, "no admite suspend");
    }
}
