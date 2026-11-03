using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>
/// La cerca de las escrituras de un trabajo, el COMMIT confirmado sin réplica, la deduplicación que revive a los muertos,
/// la toma por tipo de trabajo y la retención (puntos 1, 2, 6 y 14 de la revisión de alta disponibilidad y 3 de dinero).
/// </summary>
[TestClass]
public sealed class JobFenceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task LeasePerdido_LaCercaNoDejaGuardarLoQueHizoElNodoViejo()
    {
        // El nodo A se quedó colgado con el trabajo tomado, venció el lease y el nodo B lo retomó. Cuando A despierta y
        // quiere guardar, el COMMIT no sale: la fila del trabajo ya no es de su toma.
        var gate = new Gate();
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(sp => new Writing(sp.GetRequiredService<BillingDbContext>(), gate)));
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(Writing.JobKind, new { }); return true; });

        var a = h.RunJobs();
        await gate.Started.Task.WaitAsync(Wait);
        var id = await h.Query(db => db.Jobs.Select(j => j.Id).SingleAsync());
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET locked_until = now() - interval '1 second' WHERE id = {id}"));
        var b = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-b"));
        gate.Proceed.SetResult();

        Assert.AreEqual(1, await a.WaitAsync(Wait));
        Assert.IsNotNull(gate.Error);
        Assert.IsNotNull(Failover.Find<LeaseLostException>(gate.Error));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == Writing.Marker)));
        var job = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));
        Assert.AreEqual(JobStatus.Running, job.Status);
        Assert.AreEqual(b!.LockedBy, job.LockedBy);
        Assert.AreEqual(2, job.Attempts);
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Complete(b)));
    }

    [TestMethod]
    public async Task LeasePerdido_LasEscriturasSueltasDelNodoViejoTampocoPasan()
    {
        // Encolar, auditar y el SQL suelto sin transacción de un manejador: con el lease ya de otro nodo, ninguno guarda.
        // Encolar y auditar abren su propia transacción y pasan por la cerca del COMMIT, y al SQL suelto lo frena el
        // interceptor de comandos antes de ejecutarlo.
        var gate = new Gate();
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(sp =>
            new Loose(sp.GetRequiredService<BillingDbContext>(), sp.GetRequiredService<JobQueue>(), sp.GetRequiredService<AuditLog>(), gate)));
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(Loose.JobKind, new { }); return true; });

        var a = h.RunJobs();
        await gate.Started.Task.WaitAsync(Wait);
        var id = await h.Query(db => db.Jobs.Where(j => j.Kind == Loose.JobKind).Select(j => j.Id).SingleAsync());
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET locked_until = now() - interval '1 second' WHERE id = {id}"));
        var b = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-b"));
        gate.Proceed.SetResult();

        Assert.AreEqual(1, await a.WaitAsync(Wait));
        Assert.HasCount(3, gate.Errors);
        foreach (var error in gate.Errors)
        {
            Assert.IsNotNull(Failover.Find<LeaseLostException>(error), error.ToString());
        }

        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == Loose.Marker)));
        Assert.AreEqual(0, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == Loose.Marker)));
        var job = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));
        Assert.IsNull(job.LastError);
        Assert.AreEqual(JobStatus.Running, job.Status);
        Assert.AreEqual(b!.LockedBy, job.LockedBy);
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Complete(b)));
    }

    [TestMethod]
    public async Task ElTokenDelQueLlamaNoCancelaElCommit()
    {
        // Un COMMIT cancelado mientras espera a la réplica sincrónica queda confirmado solo en la primaria (syncrep.c avisa
        // con WARNING y sigue). Acá la espera la simula un disparador diferido que duerme en el COMMIT y, si lo cancelan,
        // hace lo mismo: avisa y confirma. El token del pedido o del trabajo (un cliente que corta, el latido que pierde el
        // lease, el apagado) no tiene que llegar al COMMIT: cancelado a los 500 ms, el COMMIT espera igual los 2 s y
        // confirma sin aviso, tanto el explícito como el implícito de SaveChanges.
        await using var h = await CoreHarness.Start();
        await h.Db(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(SyncWaitFunction);
            await db.Database.ExecuteSqlRawAsync("CREATE CONSTRAINT TRIGGER prueba_espera_replica AFTER INSERT ON platform.jobs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.prueba_espera_replica()");
        });

        using var explicitToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = Stopwatch.GetTimestamp();
        await h.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(explicitToken.Token);
            db.Jobs.Add(new Job(Writing.Marker, "{}", h.Clock.Now, h.Clock.Now));
            await db.SaveChangesAsync(explicitToken.Token);
            await tx.CommitAsync(explicitToken.Token);
        });
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9), Stopwatch.GetElapsedTime(started), "El COMMIT tiene que esperar a la réplica aunque el token se cancele.");

        using var implicitToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        started = Stopwatch.GetTimestamp();
        await h.Db(async db =>
        {
            db.Jobs.Add(new Job(Writing.Marker, "{}", h.Clock.Now, h.Clock.Now));
            await db.SaveChangesAsync(implicitToken.Token);
        });
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9), Stopwatch.GetElapsedTime(started));

        Assert.AreEqual(2, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == Writing.Marker)));
    }

    [TestMethod]
    public async Task FrenoCerrado_ElCorredorNoTomaNada_YAlAbrirloSigue()
    {
        // Sin réplica sincrónica el vigía cierra el freno de las escrituras: el corredor no toma trabajos hasta que vuelva una.
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(sp => new Writing(sp.GetRequiredService<BillingDbContext>(), new Gate { Open = true })));
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(Writing.JobKind, new { }); return true; });
        var gate = await h.Use<WriteGate, WriteGate>(Task.FromResult);
        gate.Close("sin réplica");

        Assert.AreEqual(0, await h.RunJobs());
        var job = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == Writing.JobKind));
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(0, job.Attempts);

        gate.Reopen();
        Assert.AreEqual(1, await h.RunJobs());
        Assert.AreEqual(JobStatus.Done, (await Load(h, job.Id)).Status);
    }

    [TestMethod]
    public async Task CommitConfirmadoSinReplica_EsInciertoYElTrabajoVuelveSinGastarIntento()
    {
        // PostgreSQL avisa con un WARNING durante el COMMIT cuando cancela la espera de la réplica sincrónica y deja la
        // transacción visible solo en la primaria. Acá lo produce un disparador diferido, que corre en el COMMIT.
        await using var h = await CoreHarness.Start(services: s => s.AddScoped<IJobHandler>(sp => new Writing(sp.GetRequiredService<BillingDbContext>(), new Gate { Open = true })));

        // El trabajo se encola antes del disparador: encolar también confirma en una transacción y escucharía el aviso.
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(Writing.JobKind, new { }); return true; });
        await WarnOnCommit(h);

        var error = await Assert.ThrowsAsync<CommitUncertainException>(() => h.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Jobs.Add(new Job(Writing.Marker, "{}", h.Clock.Now, h.Clock.Now));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }));
        Assert.IsTrue(Failover.IsTransient(error));

        // Sin transacción explícita pasa lo mismo: todo SaveChanges va en una, para que el aviso llegue al interceptor.
        await Assert.ThrowsAsync<CommitUncertainException>(() => h.Db(async db =>
        {
            db.Jobs.Add(new Job(Writing.Marker, "{}", h.Clock.Now, h.Clock.Now));
            await db.SaveChangesAsync();
        }));

        await h.RunJobs();

        var job = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == Writing.JobKind));
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(0, job.Attempts);
        Assert.AreEqual(h.Clock.Now + JobRunner.TransientRetry, job.RunAt);
        StringAssert.Contains(job.LastError, "pasajera");
        StringAssert.Contains(job.LastError, "sin garantía de réplica");
    }

    [TestMethod]
    public async Task CommitConfirmadoSinReplica_LaApiContesta503ConRetryAfter()
    {
        await using var w = await WebHarness.Start();
        await w.Query(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(WarnFunction);
            await db.Database.ExecuteSqlRawAsync("CREATE CONSTRAINT TRIGGER prueba_aviso_commit AFTER INSERT ON accounts.accounts DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.prueba_aviso_commit()");
            return true;
        });
        var writer = await w.Key("accounts:write");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/accounts", UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { name = "Cuenta nueva", email = "nueva@example.com", currency = "ARS" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", writer);
        using var response = await w.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("5", response.Headers.RetryAfter?.ToString());
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "conmutando");
    }

    [TestMethod]
    public async Task TrabajoMuerto_LaMismaClaveDeDeduplicacionLoRevive()
    {
        // Una suspensión que murió (por ejemplo, not_found pasajero del módulo) se vuelve a encolar al día siguiente con la
        // misma clave. Un trabajo hecho o pendiente sigue deduplicando.
        await using var h = await CoreHarness.Start();
        const string key = "service:1:suspend:3";
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue("test.noop", new { }, dedupKey: key); return true; });
        var a = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Fail(a!, "not_found", permanent: true)));
        Assert.AreEqual(JobStatus.Dead, (await Load(h, a!.Id)).Status);

        await h.Use<JobQueue, bool>(async q => { await q.Enqueue("test.noop", new { otra = true }, dedupKey: key); return true; });

        var revived = await Load(h, a.Id);
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync()));
        Assert.AreEqual(JobStatus.Pending, revived.Status);
        Assert.AreEqual(0, revived.Attempts);
        Assert.IsNull(revived.LastError);
        Assert.IsNull(revived.LockedBy);
        StringAssert.Contains(revived.Payload, "otra");

        var again = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Complete(again!)));
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue("test.noop", new { }, dedupKey: key); return true; });
        Assert.AreEqual(JobStatus.Done, (await Load(h, a.Id)).Status);
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync()));
    }

    [TestMethod]
    public async Task UnNodoSoloTomaLosTiposDeTrabajoQueSabeCorrer()
    {
        // Un nodo viejo en un despliegue rodante, o uno sin llaves, deja los trabajos que no conoce para otro nodo.
        await using var h = await CoreHarness.Start();
        await h.Use<JobQueue, bool>(async q => { await q.Enqueue("test.de-otra-version", new { }); return true; });

        Assert.AreEqual(0, await h.RunJobs());

        var job = await h.Query(db => db.Jobs.SingleAsync());
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(0, job.Attempts);
        Assert.IsNull(job.LastError);
    }

    [TestMethod]
    public async Task Retencion_BorraLosHechosViejosYDejaLosMuertos()
    {
        await using var h = await CoreHarness.Start(services: s => s.Configure<WorkerOptions>(o => o.KeepDone = TimeSpan.FromDays(30)));
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue("test.noop", new { n = 1 });
            await q.Enqueue("test.noop", new { n = 2 });
            await q.Enqueue("test.noop", new { n = 3 });
            return true;
        });
        var old = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Complete(old!)));
        var recent = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Complete(recent!)));
        var dead = await h.Use<JobQueue, ClaimedJob?>(q => q.Claim("nodo-a"));
        Assert.IsTrue(await h.Use<JobQueue, bool>(q => q.Fail(dead!, "roto", permanent: true)));
        var before = h.Clock.Now - TimeSpan.FromDays(31);
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET updated_at = {before} WHERE id IN ({old!.Id}, {dead!.Id})"));

        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(RetentionHandler.JobKind, new { }); return true; });
        Assert.AreEqual(1, await h.RunJobs());

        var left = await h.Query(db => db.Jobs.AsNoTracking().OrderBy(j => j.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { recent!.Id, dead!.Id }, left.Where(j => j.Kind == "test.noop").Select(j => j.Id).ToArray());
        Assert.AreEqual(JobStatus.Done, left.Single(j => j.Kind == RetentionHandler.JobKind).Status);
    }

    [TestMethod]
    public void ElCodigoNoDependeDelEstadoDeSesionDePostgres()
    {
        // PgBouncer en modo transacción (D-14) rompe con locks de sesión, LISTEN, SET sin LOCAL, PREPARE, cursores WITH HOLD
        // y tablas temporales. Y statement_timeout o synchronous_commit cambiados desde el código cancelarían o
        // debilitarían el COMMIT que espera a la réplica (punto 2 de la revisión de alta disponibilidad).
        var forbidden = new Regex(
            @"pg_advisory_lock(_shared)?\(|\bLISTEN\s+\w|\bNOTIFY\s+\w|\bSET\s+(LOCAL\s+)?(statement_timeout|synchronous_commit)\b|\bSET\s+SESSION\b|\bPREPARE\s+\w+\s+AS\b|\bWITH\s+HOLD\b|CREATE\s+TEMP(ORARY)?\s+TABLE",
            RegexOptions.IgnoreCase);
        var root = Root();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains("Migrations", StringComparison.Ordinal))
            .Select(f => (File: Path.GetRelativePath(root, f), Hit: forbidden.Match(File.ReadAllText(f))))
            .Where(x => x.Hit.Success)
            .Select(x => $"{x.File}: {x.Hit.Value}")
            .ToList();

        Assert.IsEmpty(offenders, string.Join(", ", offenders));
    }

    private const string WarnFunction = """
        CREATE FUNCTION platform.prueba_aviso_commit() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          RAISE WARNING 'canceling wait for synchronous replication due to user request';
          RETURN NULL;
        END $$
        """;

    /// <summary>
    /// La espera a la réplica sincrónica: duerme en el COMMIT y, si lo cancelan, hace lo que hace syncrep.c: avisa con el
    /// WARNING y confirma igual.
    /// </summary>
    private const string SyncWaitFunction = """
        CREATE FUNCTION platform.prueba_espera_replica() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          BEGIN
            PERFORM pg_sleep(2);
          EXCEPTION WHEN query_canceled THEN
            RAISE WARNING 'canceling wait for synchronous replication due to user request';
          END;
          RETURN NULL;
        END $$
        """;

    private static Task WarnOnCommit(CoreHarness h) => h.Db(async db =>
    {
        await db.Database.ExecuteSqlRawAsync(WarnFunction);
        await db.Database.ExecuteSqlRawAsync("CREATE CONSTRAINT TRIGGER prueba_aviso_commit AFTER INSERT ON platform.jobs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.prueba_aviso_commit()");
    });

    private static Task<Job> Load(CoreHarness h, long id) => h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BillingOss.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró BillingOss.slnx.");
    }

    private sealed class Gate
    {
        public bool Open { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Error { get; set; }

        public List<Exception> Errors { get; } = [];
    }

    /// <summary>Escribe como los manejadores que encolan, auditan o corren SQL suelto fuera de una transacción.</summary>
    private sealed class Loose(BillingDbContext db, JobQueue jobs, AuditLog audit, Gate gate) : IJobHandler
    {
        public const string JobKind = "test.loose";

        public const string Marker = "test.marca-suelta";

        public string Kind => JobKind;

        public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
        {
            gate.Started.TrySetResult();
            await gate.Proceed.Task.WaitAsync(Wait);
            await Try(() => jobs.Enqueue(Marker, new { }));
            await Try(() => audit.Record(Marker, $"job:{job.Id}"));
            await Try(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET last_error = 'escrito por el nodo viejo' WHERE id = {job.Id}"));
            return JobResult.Done;
        }

        private async Task Try(Func<Task> write)
        {
            try
            {
                await write();
            }
            catch (Exception ex)
            {
                gate.Errors.Add(ex);
            }
        }
    }

    /// <summary>Espera a que lo dejen seguir y guarda una fila con su contexto, como cualquier manejador.</summary>
    private sealed class Writing(BillingDbContext db, Gate gate) : IJobHandler
    {
        public const string JobKind = "test.writing";

        public const string Marker = "test.marca";

        public string Kind => JobKind;

        public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
        {
            gate.Started.TrySetResult();
            if (!gate.Open)
            {
                await gate.Proceed.Task.WaitAsync(Wait);
            }

            var now = DateTimeOffset.UtcNow;
            db.Jobs.Add(new Job(Marker, "{}", now, now));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                gate.Error = ex;
                throw;
            }

            return JobResult.Done;
        }
    }
}
