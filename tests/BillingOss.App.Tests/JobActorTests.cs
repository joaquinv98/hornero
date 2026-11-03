using System.Net;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>
/// El actor viaja con el trabajo (D-15): quien lo encoló desde la API o la interfaz, con su IP, corre el trabajo a su
/// nombre. Y una fila tomada que no se puede leer no queda en Running para siempre.
/// </summary>
[TestClass]
public sealed class JobActorTests
{
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("203.0.113.7")]
    [DataRow("2001:db8::1")]
    public async Task ElActorConIp_ViajaConElTrabajo_YElManejadorCorreASuNombre(string ip)
    {
        var handler = new Capturing();
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(_ => handler));
        var address = IPAddress.Parse(ip);
        Actor.Current = new Actor(ActorKind.Api, "apikey:1:bk_prueba", address);
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(handler.Kind, new { }); return true; });
        Actor.Current = Actor.Node;

        // La toma lee el actor tal como se guardó: una dirección, no "127.0.0.1/32".
        var claimed = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        Assert.IsNotNull(claimed);
        Assert.AreEqual(ActorKind.Api, claimed.Actor!.Kind);
        Assert.AreEqual("apikey:1:bk_prueba", claimed.Actor.Id);
        Assert.AreEqual(address, claimed.Actor.Ip);
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Release(claimed)));

        Assert.AreEqual(1, await h.RunJobs());
        Assert.AreEqual(address, handler.Seen!.Ip);
        Assert.AreEqual("apikey:1:bk_prueba", handler.Seen.Id);
        var job = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync());
        Assert.AreEqual(JobStatus.Done, job.Status);
        Assert.AreEqual(address, job.ActorIp);
    }

    [TestMethod]
    public async Task UnTrabajoQueNoSePuedeLeer_VuelveALaColaConSuError_YNoFrenaALosDemas()
    {
        var handler = new Capturing();
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(_ => handler));
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(handler.Kind, new { which = "roto" }, maxAttempts: 2);
            await q.Enqueue(handler.Kind, new { which = "sano" });
            return true;
        });
        var broken = await h.Query(db => db.Jobs.OrderBy(j => j.Id).Select(j => j.Id).FirstAsync());
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET actor_kind = 'Marciano', actor = 'x' WHERE id = {broken}"));

        // El roto se toma primero, no se puede leer y vuelve a la cola con su intento gastado: el sano corre igual.
        Assert.AreEqual(1, await h.RunJobs());
        Assert.AreEqual(1, handler.Runs);
        var job = await State(h, broken);
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(1, job.Attempts);
        Assert.IsNull(job.LockedUntil);
        Assert.IsGreaterThan(h.Clock.Now, job.RunAt, "Vuelve con espera, no enseguida.");
        StringAssert.Contains(job.LastError, "No se pudo leer el trabajo tomado");
        StringAssert.Contains(job.LastError, "Marciano");

        // Al agotar los intentos queda muerto y visible para el staff, nunca Running.
        h.Clock.Now = job.RunAt.AddSeconds(1);
        Assert.AreEqual(0, await h.RunJobs());
        job = await State(h, broken);
        Assert.AreEqual(JobStatus.Dead, job.Status);
        Assert.AreEqual(2, job.Attempts);
        StringAssert.Contains(job.LastError, "No se pudo leer el trabajo tomado");
        Assert.AreEqual(1, handler.Runs);
    }

    /// <summary>Lo que importa de la fila, sin el actor: la de prueba tiene un actor_kind que el modelo no puede leer.</summary>
    private static Task<(JobStatus Status, int Attempts, DateTimeOffset? LockedUntil, DateTimeOffset RunAt, string? LastError)> State(CoreHarness h, long id) =>
        h.Query(db => db.Jobs.AsNoTracking().Where(j => j.Id == id)
            .Select(j => new ValueTuple<JobStatus, int, DateTimeOffset?, DateTimeOffset, string?>(j.Status, j.Attempts, j.LockedUntil, j.RunAt, j.LastError))
            .SingleAsync());

    /// <summary>Guarda con qué actor corrió.</summary>
    private sealed class Capturing : IJobHandler
    {
        public string Kind => "test.actor";

        public Actor? Seen { get; private set; }

        public int Runs { get; private set; }

        public Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
        {
            Seen = Actor.Current;
            Runs++;
            return Task.FromResult(JobResult.Done);
        }
    }
}
