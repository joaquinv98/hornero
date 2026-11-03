using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>Varios nodos sobre la misma cola: cerca por toma, latido, fallas pasajeras y apagado ordenado (D-07 y D-14).</summary>
[TestClass]
public sealed class JobLeaseTests
{
    [TestMethod]
    public async Task LeaseVencido_ElDuenoViejoNoPuedeCerrarlo()
    {
        await using var h = await CoreHarness.Start();
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue("test.noop", new { }); return true; });

        var a = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        await Expire(h, a!.Id);
        var b = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-b"));

        Assert.AreEqual(a.Id, b!.Id);
        Assert.AreNotEqual(a.LockedBy, b.LockedBy);
        Assert.IsFalse(await h.Use<JobQueue, bool>(q => q.Complete(a)));
        Assert.IsFalse(await h.Use<JobQueue, bool>(q => q.Extend(a)));
        Assert.IsFalse(await h.Use<JobQueue, bool>(q => q.Fail(a, "tarde")));
        Assert.IsFalse(await h.Use<JobQueue, bool>(q => q.Release(a)));
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Complete(b)));
        var job = await Load(h, b.Id);
        Assert.AreEqual(JobStatus.Done, job.Status);
        Assert.AreEqual(2, job.Attempts);
    }

    [TestMethod]
    public async Task Latido_CorreElLeaseConElRelojDeLaBase()
    {
        await using var h = await CoreHarness.Start();
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue("test.noop", new { }); return true; });

        var a = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        var before = (await Load(h, a!.Id)).LockedUntil;
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Extend(a)));

        Assert.IsGreaterThan(before!.Value, (await Load(h, a.Id)).LockedUntil!.Value);
        Assert.IsNull(await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-b")));
    }

    [TestMethod]
    public async Task DosNodos_TrabajoMasLargoQueElLease_CorreUnaSolaVez()
    {
        // Con pool, como en producción: sin él cada latido abre una conexión nueva, y con el PostgreSQL de prueba cargado
        // abrirla puede tardar más que el tercio de lease que el latido se da.
        var handler = new Counting(TimeSpan.FromSeconds(6));
        await using var h = await CoreHarness.Start(services: s => s
            .Configure<WorkerOptions>(o => o.Lease = TimeSpan.FromSeconds(3))
            .AddScoped<IJobHandler>(_ => handler));
        await using var other = h.Node();
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(handler.Kind, new { }); return true; });

        var first = h.RunJobs();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        while (!first.IsCompleted)
        {
            // El otro nodo intenta tomarlo todo el tiempo: el latido lo mantiene tomado.
            await other.GetRequiredService<JobRunner>().RunUntilIdle(CancellationToken.None);
            await Task.Delay(200);
        }

        Assert.AreEqual(1, await first);
        Assert.AreEqual(1, handler.Runs);
        Assert.AreEqual(JobStatus.Done, (await h.Query(db => db.Jobs.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task LeasePerdido_ElLatidoCortaAlManejador()
    {
        var handler = new Blocking();
        await using var h = await CoreHarness.Start(services: s => s
            .Configure<WorkerOptions>(o => o.Lease = TimeSpan.FromSeconds(1.5))
            .AddScoped<IJobHandler>(_ => handler));
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(handler.Kind, new { }); return true; });

        var a = h.RunJobs();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var id = await h.Query(db => db.Jobs.Select(j => j.Id).SingleAsync());
        await Expire(h, id);
        var b = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-b"));
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET locked_until = now() + interval '1 minute' WHERE id = {id}"));

        // El nodo viejo se entera en el latido siguiente, corta el manejador y no toca lo que ya es del otro.
        await a.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(handler.Canceled);
        var job = await Load(h, id);
        Assert.AreEqual(JobStatus.Running, job.Status);
        Assert.AreEqual(b!.LockedBy, job.LockedBy);
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Complete(b)));
    }

    [TestMethod]
    public async Task FallaPasajeraDeLaBase_VuelveALaColaSinGastarIntento()
    {
        var handler = new Throwing(new PostgresException("cannot execute UPDATE in a read-only transaction", "ERROR", "ERROR", PostgresErrorCodes.ReadOnlySqlTransaction));
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(_ => handler));
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(handler.Kind, new { }); return true; });

        await h.RunJobs();

        var job = await h.Query(db => db.Jobs.SingleAsync());
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(0, job.Attempts);
        Assert.AreEqual(h.Clock.Now + JobRunner.TransientRetry, job.RunAt);
        StringAssert.Contains(job.LastError, "pasajera");
    }

    [TestMethod]
    public async Task OtraFalla_GastaUnIntento()
    {
        var handler = new Throwing(new InvalidOperationException("dato roto"));
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(_ => handler));
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(handler.Kind, new { }); return true; });

        await h.RunJobs();

        var job = await h.Query(db => db.Jobs.SingleAsync());
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(1, job.Attempts);
        Assert.AreEqual("dato roto", job.LastError);
    }

    [TestMethod]
    public async Task Apagar_DevuelveElLeaseDeLoQueSeCorto()
    {
        var handler = new Blocking();
        await using var h = await CoreHarness.Start(services: s => s
            .Configure<WorkerOptions>(o => o.ShutdownGrace = TimeSpan.Zero)
            .AddScoped<IJobHandler>(_ => handler));
        await using var node = h.Node();
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(handler.Kind, new { }); return true; });
        var runner = node.GetRequiredService<JobRunner>();

        await runner.StartAsync(CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await runner.StopAsync(CancellationToken.None);

        Assert.IsTrue(handler.Canceled);
        var job = await h.Query(db => db.Jobs.SingleAsync());
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.IsNull(job.LockedUntil);
        Assert.AreEqual(0, job.Attempts);
        Assert.AreEqual(h.Clock.Now, job.RunAt);
    }

    [TestMethod]
    public async Task Apagar_LoQueTerminaDentroDeLaGraciaQuedaHecho()
    {
        var handler = new Counting(TimeSpan.FromMilliseconds(500));
        await using var h = await CoreHarness.Start(services: s => s
            .Configure<WorkerOptions>(o => o.ShutdownGrace = TimeSpan.FromSeconds(10))
            .AddScoped<IJobHandler>(_ => handler));
        await using var node = h.Node();
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(handler.Kind, new { }); return true; });
        var runner = node.GetRequiredService<JobRunner>();

        await runner.StartAsync(CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await runner.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, handler.Runs);
        Assert.AreEqual(JobStatus.Done, (await h.Query(db => db.Jobs.SingleAsync())).Status);
    }

    /// <summary>El nodo que lo tenía se colgó o perdió la base: el lease vence en el reloj de la base.</summary>
    private static Task Expire(CoreHarness h, long id) =>
        h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET locked_until = now() - interval '1 second' WHERE id = {id}"));

    private static Task<Job> Load(CoreHarness h, long id) => h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    /// <summary>Tarda lo pedido sin mirar el token y cuenta cuántas veces corrió.</summary>
    private sealed class Counting(TimeSpan duration) : IJobHandler
    {
        private int runs;

        public string Kind => "test.counting";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs => runs;

        public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            Started.TrySetResult();
            await Task.Delay(duration, CancellationToken.None);
            return JobResult.Done;
        }
    }

    /// <summary>Espera hasta que lo corten.</summary>
    private sealed class Blocking : IJobHandler
    {
        public string Kind => "test.blocking";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Canceled { get; private set; }

        public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }

            return JobResult.Done;
        }
    }

    private sealed class Throwing(Exception error) : IJobHandler
    {
        public string Kind => "test.throwing";

        public Task<JobResult> Handle(ClaimedJob job, CancellationToken ct) => throw error;
    }
}
