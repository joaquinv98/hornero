using System.Net;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>Lo del núcleo para las actualizaciones (D-20): el esquema del actualizador, la pausa, la versión y los pedidos.</summary>
[TestClass]
public sealed class UpdatesTests
{
    private static readonly DateTimeOffset Issued = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task LaMigracionDejaUnaFilaDeEstado_ConIdentificadorAlAzar_YUnSoloPedidoActivo()
    {
        await using var core = await CoreHarness.Start();
        await Owner(core, "INSERT INTO updates.requests (version, requested_by) VALUES ('0.1.1', 'staff:1')");

        var state = await core.Query(db => db.Set<UpdaterState>().AsNoTracking().SingleAsync());
        Assert.AreEqual(1, state.Id);
        Assert.AreNotEqual(Guid.Empty, state.InstallationId);
        var ex = await Assert.ThrowsExactlyAsync<PostgresException>(() => Owner(core, "INSERT INTO updates.requests (version, requested_by) VALUES ('0.1.2', 'staff:1')"));
        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        await Assert.ThrowsExactlyAsync<PostgresException>(() => Owner(core, "INSERT INTO updates.requests (version, requested_by) VALUES ('0.1.2\n', 'staff:1')"));
    }

    [TestMethod]
    public async Task LaAplicacionSoloLeeYDejaPedidos_NoTocaElEstadoDelActualizador()
    {
        // Como en las recetas: el rol de la aplicación recibe permisos por defecto sobre lo que crean las migraciones.
        var (connectionString, drop) = await TestDatabase.Create();
        var role = $"billing_oss_t_app_{Guid.NewGuid():N}"[..40];
        try
        {
            await Exec(connectionString, $"CREATE ROLE {role}; ALTER DEFAULT PRIVILEGES GRANT USAGE ON SCHEMAS TO {role}; ALTER DEFAULT PRIVILEGES GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {role}; ALTER DEFAULT PRIVILEGES GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {role};");
            await using (var db = new BillingDbContext(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), connectionString).Options))
            {
                await db.Database.MigrateAsync();
            }

            await Exec(connectionString, $"SET ROLE {role}; INSERT INTO updates.requests (version, requested_by) VALUES ('0.1.1', 'staff:1'); SELECT * FROM updates.state; SELECT * FROM updates.nodes;");
            foreach (var forbidden in (string[])[
                "UPDATE updates.state SET channel_serial = 0",
                "UPDATE updates.state SET blocked_versions = '{}'",
                "DELETE FROM updates.requests",
                "UPDATE updates.requests SET status = 'done'",
                "INSERT INTO updates.requests (version, requested_by, status) VALUES ('0.1.2', 'staff:1', 'done')",
                "INSERT INTO updates.requests (version, requested_by, origin) VALUES ('0.1.2', 'staff:1', 'console')",
                "INSERT INTO updates.nodes (name, seen_at) VALUES ('falso', now())",
                "TRUNCATE updates.requests",
            ])
            {
                var ex = await Assert.ThrowsExactlyAsync<PostgresException>(() => Exec(connectionString, $"SET ROLE {role}; {forbidden}"), forbidden);
                Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState, forbidden);
            }
        }
        finally
        {
            await drop();
            await Exec(TestDatabase.RequireAdmin(), $"DROP ROLE IF EXISTS {role}");
        }
    }

    [TestMethod]
    public async Task ConElPlanificadorPausado_NoSeEncolaNada_YAlVencerVuelve()
    {
        await using var core = await CoreHarness.Start();
        await Owner(core, "UPDATE updates.state SET scheduler_paused_until = now() + interval '10 minutes'");

        await core.Tick();
        Assert.AreEqual(0, await core.Query(db => db.Jobs.CountAsync()));

        await Owner(core, "UPDATE updates.state SET scheduler_paused_until = now() - interval '1 second'");
        await core.Tick();
        Assert.IsTrue(await core.Query(db => db.Jobs.AnyAsync(j => j.Kind == JobKinds.BillingRun)));
    }

    [TestMethod]
    public async Task ElNucleoNoArranca_SiLeFaltanMigraciones_OSiElEsquemaYaNoLoSoporta()
    {
        await using var core = await CoreHarness.Start();

        Assert.IsNull(await core.Query(db => SchemaGate.Problem(db, UpdateService.Installed)));

        // Una minor posterior quitó lo que esta versión usa y lo anotó en su migración.
        await Owner(core, "UPDATE updates.state SET min_core_version = '0.3.0'");
        var problem = await core.Query(db => SchemaGate.Problem(db, new Version(0, 2, 9)));
        StringAssert.Contains(problem, "ya no soporta la versión 0.2.9");
        Assert.IsNull(await core.Query(db => SchemaGate.Problem(db, new Version(0, 3, 0))));

        // Un binario nuevo sobre una base restaurada antes de sus migraciones.
        var (empty, drop) = await TestDatabase.Create();
        try
        {
            await using var db = new BillingDbContext(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), empty).Options);
            StringAssert.Contains(await SchemaGate.Problem(db, new Version(0, 3, 0)), "Faltan migraciones");
        }
        finally
        {
            await drop();
        }
    }

    [TestMethod]
    public async Task LaMarcaDeVersionMinimaSoloSube()
    {
        await using var core = await CoreHarness.Start();
        var builder = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.RequireCoreVersion("0.3.0");
        builder.RequireCoreVersion("0.2.0");
        builder.RequireCoreVersion("0.10.0");

        foreach (var sql in builder.Operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
        {
            await Owner(core, sql.Sql);
        }

        Assert.AreEqual("0.10.0", await core.Query(db => db.Set<UpdaterState>().Select(s => s.MinCoreVersion).SingleAsync()));
    }

    [TestMethod]
    public async Task UnPedidoDelPanel_ValidaContraElCanal_YQuedaAuditado()
    {
        await using var core = await CoreHarness.Start();
        Actor.Current = new Actor(ActorKind.Staff, "staff:7", IPAddress.Loopback);

        var sinCanal = await Assert.ThrowsExactlyAsync<UpdateException>(() => core.Use<UpdateService, long>(s => s.Request("0.1.1")));
        StringAssert.Contains(sinCanal.Message, "canal verificado");

        await SaveChannel(core, Channel(Release("0.1.1"), Release("0.1.2", security: true), Release("0.2.0", from: "0.1.0")));
        await Assert.ThrowsExactlyAsync<UpdateException>(() => core.Use<UpdateService, long>(s => s.Request("0.9.9")));
        await Assert.ThrowsExactlyAsync<UpdateException>(() => core.Use<UpdateService, long>(s => s.Request("0.1.1\n")));
        var piso = await Assert.ThrowsExactlyAsync<UpdateException>(() => core.Use<UpdateService, long>(s => s.Request("0.1.1")));
        StringAssert.Contains(piso.Message, "0.1.2");

        var id = await core.Use<UpdateService, long>(s => s.Request("0.1.2"));
        var request = await core.Use<UpdateService, UpdateRequest?>(s => s.Find(id));
        Assert.AreEqual("0.1.2", request!.Version);
        Assert.AreEqual("web", request.Origin);
        Assert.AreEqual("pending", request.Status);
        Assert.AreEqual("staff:7", request.RequestedBy);
        var otro = await Assert.ThrowsExactlyAsync<UpdateException>(() => core.Use<UpdateService, long>(s => s.Request("0.2.0")));
        StringAssert.Contains(otro.Message, "Ya hay");
        var audited = await core.Query(db => db.Database.SqlQueryRaw<string>("SELECT actor AS \"Value\" FROM platform.audit_events WHERE action = 'updates.requested'").ToListAsync());
        CollectionAssert.AreEqual(new[] { "staff:7" }, audited);
    }

    [TestMethod]
    public async Task ElPanelDiceSinDatosSinActualizador_YActualizaYaConUnParcheDeSeguridad()
    {
        await using var core = await CoreHarness.Start();
        var now = DateTimeOffset.UtcNow;

        var vacio = await core.Use<UpdateService, UpdateOverview>(s => s.Overview(now));
        Assert.AreEqual(UpdateState.NoData, vacio.Summary.State);

        await SaveChannel(core, Channel(Release("0.1.1"), Release("0.1.2", security: true)), issued: now.AddHours(-1));
        await Owner(core, "INSERT INTO updates.nodes (name, version, seen_at) VALUES ('nodo1', '0.1.0', now())");
        var overview = await core.Use<UpdateService, UpdateOverview>(s => s.Overview(now));

        Assert.AreEqual(UpdateState.Urgent, overview.Summary.State);
        Assert.AreEqual("Security", overview.Summary.Reason);
        CollectionAssert.AreEqual(new[] { "0.1.2" }, overview.Offered[0].Path.ToArray());
        Assert.AreEqual("nodo1", overview.Nodes.Single().Name);
    }

    [TestMethod]
    public async Task UnTrabajoEncoladoPorUnaVersionNueva_ConCamposDeMas_LoCorreLaAnterior()
    {
        // Regla de la sección 7.4: durante el cambio un worker viejo puede tomar un trabajo encolado por un nodo nuevo.
        await using var core = await CoreHarness.Start();
        await core.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.Dunning, new { date = new DateOnly(2026, 11, 2), campo_de_una_version_nueva = "x" });
            return true;
        });

        Assert.AreEqual(1, await core.RunJobs());
        Assert.AreEqual("Done", await core.Query(db => db.Jobs.Select(j => j.Status.ToString()).SingleAsync()));
    }

    internal static Release Release(string version, string? from = null, bool security = false) => new()
    {
        ReleaseVersion = version,
        From = from,
        Security = security,
        Severity = security ? "high" : null,
        CveList = security ? [new Cve("CVE-2026-0001", "dotnet")] : [],
        Images = new ReleaseImages { Nucleo = "registro.example/hornero/billing-oss@sha256:" + new string('a', 64) },
    };

    internal static ReleaseChannel Channel(params Release[] releases) => new()
    {
        Format = 1,
        Name = "estable",
        Serial = 1,
        Issued = Issued,
        Expires = Issued.AddDays(7),
        Releases = releases,
    };

    /// <summary>Lo que deja el actualizador después de un chequeo que verificó.</summary>
    internal static Task SaveChannel(CoreHarness core, ReleaseChannel channel, DateTimeOffset? issued = null)
    {
        var json = (issued is { } at ? channel with { Issued = at, Expires = at.AddDays(7) } : channel).ToJson().Replace("'", "''", StringComparison.Ordinal);
        return Owner(core, $"UPDATE updates.state SET channel = '{json}'::jsonb, checked_at = now(), check_attempt_at = now()");
    }

    /// <summary>Lo que hace el actualizador, con el rol dueño.</summary>
    internal static Task Owner(CoreHarness core, string sql) => Exec(core.ConnectionString, sql);

    private static async Task Exec(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
