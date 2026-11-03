using System.Data.Common;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>
/// Conmutaciones simuladas sobre la base de prueba. La primaria que cae corta todas las sesiones (57P01). La primaria
/// vieja que quedó en pie pasa a solo lectura (25006) hasta que la aplicación encuentra la nueva, que acá es la misma
/// base cuando vuelve a aceptar escrituras.
/// </summary>
internal static class Chaos
{
    /// <summary>
    /// Cae la primaria: se cortan todas las sesiones de la base. Espera a que los procesos terminen, porque la señal
    /// se procesa con demora y un comando que llega antes todavía se ejecuta.
    /// </summary>
    public static async Task CutConnections(string connectionString)
    {
        await Admin(connectionString, db => $"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()");
        for (var i = 0; i < 100; i++)
        {
            if (await Scalar(connectionString, db => $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()") == 0)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>La primaria vieja sigue en pie pero ya no acepta escrituras, y sus sesiones se cortan.</summary>
    public static async Task Demote(string connectionString)
    {
        await Admin(connectionString, db => $"ALTER DATABASE {db} SET default_transaction_read_only = on");
        await CutConnections(connectionString);
    }

    /// <summary>Hay primaria de nuevo.</summary>
    public static Task Promote(string connectionString) =>
        Admin(connectionString, db => $"ALTER DATABASE {db} RESET default_transaction_read_only");

    private static async Task Admin(string connectionString, Func<string, string> sql) => await Scalar(connectionString, sql);

    private static async Task<long> Scalar(string connectionString, Func<string, string> sql)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        await using var db = new NpgsqlConnection(TestDatabase.RequireAdmin());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql(database), db);
        return await cmd.ExecuteScalarAsync() is long n ? n : 0;
    }
}

[TestClass]
public sealed class FailoverTests
{
    [TestMethod]
    public void Clasifica_LasFallasDeUnaConmutacion()
    {
        foreach (var state in new[] { "57P01", "57P02", "57P03", "08006", "08003", "08007", "25006" })
        {
            Assert.IsTrue(Failover.IsTransient(Postgres(state)), state);
        }

        Assert.IsTrue(Failover.IsTransient(new DbUpdateException("guardar", Postgres("25006"))));
        Assert.IsTrue(Failover.IsTransient(new NpgsqlException("Exception while reading from stream", new IOException("corte"))));
        Assert.IsTrue(Failover.IsTransient(new NpgsqlException("No suitable host was found.")));
        Assert.IsTrue(Failover.IsTransient(new NpgsqlException("Unable to connect", new AggregateException(new IOException("a"), new IOException("b")))));

        Assert.IsFalse(Failover.IsTransient(new NpgsqlException("Exception while reading from stream", new TimeoutException())));
        Assert.IsFalse(Failover.IsTransient(Postgres("23505")));
        Assert.IsFalse(Failover.IsTransient(Postgres("40001")));
        Assert.IsFalse(Failover.IsTransient(new DbUpdateException("guardar", Postgres("23514"))));
        Assert.IsFalse(Failover.IsTransient(new InvalidOperationException("otra cosa")));
        Assert.IsFalse(Failover.IsTransient(null));
    }

    [TestMethod]
    public void CadenaPrincipal_ConVariosHosts_SoloVaALaPrimaria()
    {
        Assert.AreEqual("read-write", Target(Failover.MainConnection("Host=pg1,pg2,pg3;Database=billing")));
        Assert.AreEqual("primary", Target(Failover.MainConnection("Host=pg1,pg2;Database=billing;Target Session Attributes=primary")));
        Assert.IsNull(Target(Failover.MainConnection("Host=vip-billing;Database=billing")));

        foreach (var target in new[] { "any", "standby", "prefer-standby", "prefer-primary", "read-only" })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => Failover.MainConnection($"Host=pg1,pg2;Target Session Attributes={target}"), target);
        }
    }

    [TestMethod]
    public void CadenaDeReplicas_PrefiereStandbyYNoEscribe()
    {
        var replica = new NpgsqlConnectionStringBuilder(Failover.ReplicaConnection("Host=pg1,pg2,pg3;Database=billing"));
        Assert.AreEqual("prefer-standby", replica.TargetSessionAttributes);
        Assert.IsTrue(replica.LoadBalanceHosts);
        Assert.AreEqual("-c default_transaction_read_only=on", replica.Options);

        var standby = new NpgsqlConnectionStringBuilder(Failover.ReplicaConnection("Host=pg2,pg3;Target Session Attributes=standby"));
        Assert.AreEqual("standby", standby.TargetSessionAttributes);
    }

    [TestMethod]
    public async Task VariosHosts_SigueALaPrimariaYNuncaEscribeEnUnaDeSoloLectura()
    {
        // Sin pool: cada apertura tiene que ser una sesión nueva para ver la base en solo lectura y de vuelta.
        var (connectionString, drop) = await TestDatabase.Create(pooling: false);
        try
        {
            // El primer host no atiende: es la primaria caída. El segundo es la base de prueba.
            var real = new NpgsqlConnectionStringBuilder(connectionString);
            var multi = new NpgsqlConnectionStringBuilder(Failover.MainConnection(new NpgsqlConnectionStringBuilder(connectionString)
            {
                Host = $"127.0.0.1:1,{real.Host}",
                HostRecheckSeconds = 0,
            }.ConnectionString));

            Assert.AreEqual("off", await ReadOnlySetting(multi.ConnectionString));

            // La base pasa a solo lectura: con read-write la conexión no se abre, y es una falla de conmutación.
            await Chaos.Demote(connectionString);
            var refused = await Assert.ThrowsAsync<NpgsqlException>(() => ReadOnlySetting(multi.ConnectionString));
            Assert.IsTrue(Failover.IsTransient(refused), refused.Message);

            await Chaos.Promote(connectionString);
            Assert.AreEqual("off", await ReadOnlySetting(multi.ConnectionString));
        }
        finally
        {
            await Chaos.Promote(connectionString);
            await drop();
        }
    }

    [TestMethod]
    public async Task Emision_CaeLaPrimariaDespuesDeAutorizar_UnSoloComprobante()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        fisco.AfterAuthorize = () => Chaos.CutConnections(h.ConnectionString);

        var placed = await FiscalFlowTests.Order(h);
        await h.RunJobs();

        // El fisco dio el CAE y la base se cayó antes de guardarlo. Si la conexión que sigue era una de las cortadas, el
        // trabajo vuelve a la cola sin gastar intento y lo guarda en la vuelta siguiente. Si el pool le dio una nueva,
        // llegó a la base y lo guardó ahí mismo. En los dos casos hay un solo comprobante.
        Assert.IsNull(fisco.AfterAuthorize);
        var waiting = await FiscalJob(h);
        if (waiting.Status == JobStatus.Pending)
        {
            Assert.AreEqual(0, await h.Query(db => db.FiscalDocuments.CountAsync()));
            Assert.AreEqual(0, waiting.Attempts);
            StringAssert.Contains(waiting.LastError, "pasajera");
            h.Clock.Now += JobRunner.TransientRetry;
            await h.RunJobs();
        }

        await AssertOneVoucher(h, fisco, placed.InvoiceId!.Value);
    }

    [TestMethod]
    public async Task Emision_LaPrimariaPasaASoloLectura_SeReintentaYNoDuplica()
    {
        // Sin pool: la base en solo lectura vale para las sesiones nuevas, y una del pool abierta en ese rato seguiría así.
        string? connection = null;
        var close = new RefusedClose(() => Chaos.Promote(connection!));
        var (h, module, fisco) = await FiscalFlowTests.Start(pooling: false, services: s => s.ConfigureDbContext<BillingDbContext>(o => o.AddInterceptors(close)));
        await using var _ = h;
        await using var __ = module;
        connection = h.ConnectionString;
        fisco.AfterAuthorize = () => Chaos.Demote(h.ConnectionString);

        var placed = await FiscalFlowTests.Order(h);
        await h.RunJobs();

        // El fisco dio el CAE y el comprobante no se pudo guardar: el trabajo volvió a la cola sin gastar intento, y en
        // la vuelta consulta el comprobante en vez de pedir otro.
        Assert.IsTrue(close.Fired, "El corredor tendría que haber chocado con la base en solo lectura al cerrar el trabajo.");
        Assert.AreEqual(0, await h.Query(db => db.FiscalDocuments.CountAsync()));
        StringAssert.Contains((await FiscalJob(h)).LastError, "pasajera");
        h.Clock.Now += JobRunner.TransientRetry;
        await h.RunJobs();

        await AssertOneVoucher(h, fisco, placed.InvoiceId!.Value);
    }

    [TestMethod]
    public async Task Cobro_LaPrimariaPasaASoloLecturaAlRegistrar_UnSoloPago()
    {
        // Sin pool, por lo mismo que la emisión.
        string? connection = null;
        var close = new RefusedClose(() => Chaos.Promote(connection!));
        var (h, module, gateway, invoiceId) = await GatewayFlowTests.Start(pooling: false, services: s => s.ConfigureDbContext<BillingDbContext>(o => o.AddInterceptors(close)));
        await using var _ = h;
        await using var __ = module;
        connection = h.ConnectionString;
        var started = await GatewayFlowTests.Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        gateway.BeforePayment = () => Chaos.Demote(h.ConnectionString);

        await GatewayFlowTests.Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        Assert.IsNull(gateway.BeforePayment);
        Assert.IsTrue(close.Fired, "El corredor tendría que haber chocado con la base en solo lectura al cerrar el trabajo.");
        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()));
        h.Clock.Now += JobRunner.TransientRetry;
        await h.RunJobs();

        await AssertOnePayment(h, invoiceId);
    }

    [TestMethod]
    public async Task Cobro_CaeLaPrimariaJustoDespuesDelCommit_NoDuplicaNiPierde()
    {
        string? connection = null;
        var cut = new CutAfterCommit(() => Chaos.CutConnections(connection!));
        var (h, module, gateway, invoiceId) = await GatewayFlowTests.Start(services: s => s.ConfigureDbContext<BillingDbContext>(o => o.AddInterceptors(cut)));
        await using var _ = h;
        await using var __ = module;
        connection = h.ConnectionString;
        var started = await GatewayFlowTests.Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");

        // El pago queda confirmado en la base y la primaria cae antes de cerrar el intento.
        gateway.BeforePayment = () =>
        {
            cut.Armed = true;
            return Task.CompletedTask;
        };
        await GatewayFlowTests.Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        // El pago quedó. Lo que sigue depende de qué conexión toque: una conexión cortada falla y el trabajo vuelve a
        // la cola, y una nueva del pool llega a la base y termina. Las dos salidas son correctas: lo que no cambia es
        // que hay un solo pago y el intento termina pago.
        Assert.IsTrue(cut.Fired);
        Assert.AreEqual(1, await h.Query(db => db.Payments.CountAsync()));
        h.Clock.Now += JobRunner.TransientRetry;
        await h.RunJobs();

        await AssertOnePayment(h, invoiceId, transient: false);
    }

    [TestMethod]
    public async Task DosNodos_ConConmutacionEnElMedio_UnComprobantePorFacturaYSinHuecos()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        await using var other = h.Node();
        var invoices = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            invoices.Add((await FiscalFlowTests.Order(h)).InvoiceId!.Value);
        }

        // Los dos nodos compiten por la cola, y la primaria cae con la primera emisión autorizada por el fisco y sin guardar.
        fisco.AfterAuthorize = () => Chaos.CutConnections(h.ConnectionString);
        var runner = other.GetRequiredService<JobRunner>();
        await Drain(h, () => Task.WhenAll(Run(h.RunJobs), Run(() => runner.RunUntilIdle(CancellationToken.None))));

        var vouchers = await h.Query(db => db.FiscalDocuments.OrderBy(f => f.Number).ToListAsync());
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, vouchers.Select(v => v.Number).ToArray());
        CollectionAssert.AreEquivalent(invoices, vouchers.Select(v => v.DocumentId).ToList());
        Assert.AreEqual(3, fisco.Authorized);
        Assert.AreEqual(3, fisco.Authorizations.Count);
        Assert.IsNull(fisco.AfterAuthorize);
        Assert.IsFalse(await h.Query(db => db.Jobs.AnyAsync(j => j.Status == JobStatus.Dead)));
    }

    /// <summary>
    /// Corre una ronda de trabajos por vez hasta que no queda nada, adelantando el reloj entre rondas para lo que se
    /// reprogramó: el reintento después de una conmutación y la espera antes de consultar un comprobante incierto.
    /// </summary>
    private static async Task Drain(CoreHarness h, Func<Task> round)
    {
        for (var i = 0; i < 10; i++)
        {
            await round();
            if (!await h.Query(db => db.Jobs.AnyAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)))
            {
                return;
            }

            h.Clock.Now += FiscalEmissionHandler.UncertainWait + TimeSpan.FromSeconds(1);
        }

        // Lo que quedó, para saber si es un trabajo tomado que nadie cerró (el lease va con el reloj de la base) o una
        // emisión trabada que hace esperar al resto.
        var database = await h.Query(db => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"").SingleAsync());
        var jobs = await h.Query(db => db.Jobs.AsNoTracking().Where(j => j.Status != JobStatus.Done).OrderBy(j => j.Id).ToListAsync());
        var emissions = await h.Query(db => db.FiscalEmissions.AsNoTracking().OrderBy(e => e.Id).ToListAsync());
        Assert.Fail($"""
            La cola no se vació (reloj de la prueba {h.Clock.Now:HH:mm:ss}, de la base {database:HH:mm:ss}).
            Trabajos: {string.Join(" | ", jobs.Select(j => $"{j.Id} {j.Kind} {j.Status} intentos {j.Attempts} para {j.RunAt:HH:mm:ss} lease {j.LockedUntil:HH:mm:ss} error {j.LastError}"))}
            Emisiones: {string.Join(" | ", emissions.Select(e => $"documento {e.DocumentId} {e.Status} número {e.ProposedNumber} intentos {e.Attempts} error {e.LastError}"))}
            """);
    }

    /// <summary>Un nodo corre hasta quedarse sin trabajo. Si la base se cae justo al tomar uno, sigue en la ronda siguiente.</summary>
    private static async Task Run(Func<Task<int>> node)
    {
        try
        {
            await node();
        }
        catch (Exception ex) when (Failover.IsTransient(ex))
        {
        }
    }

    private static Task<Job> FiscalJob(CoreHarness h) =>
        h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == JobKinds.FiscalEmit));

    private static async Task AssertOneVoucher(CoreHarness h, FakeFisco fisco, long invoiceId)
    {
        var voucher = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(invoiceId, voucher.DocumentId);
        Assert.AreEqual(1L, voucher.Number);
        Assert.AreEqual("CAE1", voucher.AuthorizationCode);
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
        Assert.AreEqual(1, fisco.Authorized);
        Assert.AreEqual(1, fisco.Authorizations.Count);
        var job = await FiscalJob(h);
        Assert.AreEqual(JobStatus.Done, job.Status);
        Assert.AreEqual(1, job.Attempts);
    }

    private static async Task AssertOnePayment(CoreHarness h, long invoiceId, bool transient = true)
    {
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(PaymentStatus.Approved, payment.Status);
        Assert.AreEqual(12100m, await h.Query(db => db.Allocations.SumAsync(a => a.Amount)));
        Assert.AreEqual(1, await h.Query(db => db.Allocations.CountAsync()));
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(AttemptStatus.Paid, attempt.Status);
        Assert.AreEqual(payment.Id, attempt.PaymentId);
        var sync = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == JobKinds.PaymentSync));
        Assert.AreEqual(JobStatus.Done, sync.Status);
        if (transient)
        {
            StringAssert.Contains(sync.LastError, "pasajera");
        }

        Assert.IsFalse(await h.Query(db => db.Jobs.AnyAsync(j => j.Status == JobStatus.Dead)));
    }

    private static PostgresException Postgres(string sqlState) => new("falla", "ERROR", "ERROR", sqlState);

    private static string? Target(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).TargetSessionAttributes;

    private static async Task<string> ReadOnlySetting(string connectionString)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT current_setting('transaction_read_only')", db);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Corre la acción (la vuelta de la primaria) la primera vez que la base en solo lectura (25006) rechaza el cierre de
    /// un trabajo, antes de que el corredor reintente el cierre. El cierre viene después del manejador, así que para
    /// entonces todo lo que el manejador quiso escribir (el comprobante, el pago, la auditoría de la llamada al módulo)
    /// ya chocó con la base en solo lectura, y el reintento del cierre la encuentra de vuelta. Es un orden, no un plazo:
    /// con un plazo fijo la primaria volvía antes de que el núcleo llegara a escribir si el PostgreSQL de prueba estaba
    /// cargado, y la prueba no probaba la conmutación. El latido no cierra el trabajo y no cuenta.
    /// </summary>
    private sealed class RefusedClose(Func<Task> then) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Exception is PostgresException { SqlState: PostgresErrorCodes.ReadOnlySqlTransaction }
                && command.CommandText.StartsWith("UPDATE platform.jobs SET status", StringComparison.Ordinal))
            {
                Fired = true;
                await then();
            }
        }
    }

    /// <summary>Corta la base apenas se confirma una transacción, cuando se lo pide la prueba.</summary>
    private sealed class CutAfterCommit(Func<Task> cut) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                Fired = true;
                await cut();
            }
        }
    }
}
