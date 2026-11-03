using System.Globalization;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.App.Platform;

/// <summary>
/// Estado del actualizador (D-20), en su esquema: lo escribe solo el actualizador con el rol dueño. La aplicación lo lee
/// y no puede cambiarlo, así una web comprometida no borra el aviso de un parche, el bloqueo de una versión ni el último
/// serial visto. Una sola fila.
/// </summary>
public sealed class UpdaterState
{
    public short Id { get; private set; }

    /// <summary>Identificador al azar de la instalación, para el despliegue escalonado. Nunca sale de la base.</summary>
    public Guid InstallationId { get; private set; }

    /// <summary>El último canal que verificó el actualizador, tal cual.</summary>
    public string? Channel { get; private set; }

    public long ChannelSerial { get; private set; }

    public DateTimeOffset? ChannelIssued { get; private set; }

    /// <summary>Huellas de las llaves que el actualizador dejó de aceptar al rotar.</summary>
    public string[] RevokedKeys { get; private set; } = [];

    /// <summary>Último chequeo que verificó.</summary>
    public DateTimeOffset? CheckedAt { get; private set; }

    public DateTimeOffset? CheckAttemptAt { get; private set; }

    public string? CheckError { get; private set; }

    /// <summary>El último intento bajó un canal y lo rechazó (firma, serial, vencimiento o reloj), no fue una falla de red.</summary>
    public bool CheckRejected { get; private set; }

    /// <summary>Versión entera de la instalación: la última que terminó en todos los nodos.</summary>
    public string? Version { get; private set; }

    /// <summary>Imágenes de esa versión por servicio, en JSON: un nodo atrasado se pone al día con ellas.</summary>
    public string? Images { get; private set; }

    /// <summary>La versión más vieja del núcleo que soporta el esquema. La anota la migración que quita algo.</summary>
    public string? MinCoreVersion { get; private set; }

    public string[] BlockedVersions { get; private set; } = [];

    public string? LeaseHolder { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    /// <summary>Mientras dura una actualización, el planificador no encola. Vence solo si el actualizador se cae.</summary>
    public DateTimeOffset? SchedulerPausedUntil { get; private set; }
}

/// <summary>Un pedido de actualización. La aplicación solo puede crearlo con la versión y quién lo pidió.</summary>
public sealed class UpdateRequest
{
    public long Id { get; private set; }

    public string Version { get; private set; } = string.Empty;

    /// <summary>web o console. Desde la web nunca se baja del último parche de seguridad de la línea.</summary>
    public string Origin { get; private set; } = "web";

    public string RequestedBy { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>pending, running, done, failed o rejected.</summary>
    public string Status { get; private set; } = "pending";

    public string? Phase { get; private set; }

    /// <summary>Paso del camino que se está aplicando.</summary>
    public int Step { get; private set; }

    /// <summary>Turno de nodo dentro del paso.</summary>
    public int NodeIndex { get; private set; }

    /// <summary>Camino y nodos, en JSON.</summary>
    public string? Plan { get; private set; }

    /// <summary>Lo que fue pasando, en un arreglo JSON: lo muestra la página de avance.</summary>
    public string Progress { get; private set; } = "[]";

    public string? Error { get; private set; }

    public string? FromVersion { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Hasta cuándo se esperan los chequeos previos o un nodo.</summary>
    public DateTimeOffset? Deadline { get; private set; }

    public bool Active => Status is "pending" or "running";
}

/// <summary>Un nodo con su actualizador: qué versión e imágenes corre y cuándo dio señales.</summary>
public sealed class UpdateNode
{
    public string Name { get; private set; } = string.Empty;

    public string? Version { get; private set; }

    public string? Images { get; private set; }

    public string? PreviousVersion { get; private set; }

    public string? PreviousImages { get; private set; }

    public string? UpdaterVersion { get; private set; }

    public DateTimeOffset SeenAt { get; private set; }

    /// <summary>"pedido:paso" para el que ya bajó las imágenes.</summary>
    public string? Pulled { get; private set; }

    /// <summary>"pedido:paso" que ya aplicó.</summary>
    public string? Applied { get; private set; }

    public string? Status { get; private set; }

    public string? Detail { get; private set; }
}

internal sealed class UpdaterStateConfiguration : IEntityTypeConfiguration<UpdaterState>
{
    public void Configure(EntityTypeBuilder<UpdaterState> b)
    {
        b.ToTable("state", "updates", t => t.HasCheckConstraint("ck_state_single", "id = 1"));
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Channel).HasColumnType("jsonb");
        b.Property(x => x.Images).HasColumnType("jsonb");
        b.Property(x => x.CheckError).HasMaxLength(2000);
        b.Property(x => x.Version).HasMaxLength(20);
        b.Property(x => x.MinCoreVersion).HasMaxLength(20);
        b.Property(x => x.LeaseHolder).HasMaxLength(200);
    }
}

internal sealed class UpdateRequestConfiguration : IEntityTypeConfiguration<UpdateRequest>
{
    public void Configure(EntityTypeBuilder<UpdateRequest> b)
    {
        b.ToTable("requests", "updates", t =>
        {
            t.HasCheckConstraint("ck_requests_version", UpdatesSql.VersionCheck("version"));
            t.HasCheckConstraint("ck_requests_origin", "origin IN ('web', 'console')");
            t.HasCheckConstraint("ck_requests_status", "status IN ('pending', 'running', 'done', 'failed', 'rejected')");
        });
        b.Property(x => x.Version).HasMaxLength(20);
        b.Property(x => x.Origin).HasMaxLength(10).HasDefaultValue("web");
        b.Property(x => x.RequestedBy).HasMaxLength(200);
        b.Property(x => x.RequestedAt).HasDefaultValueSql("now()");
        b.Property(x => x.Status).HasMaxLength(10).HasDefaultValue("pending");
        b.Property(x => x.Phase).HasMaxLength(20);

        // La aplicación inserta solo versión y quién pide: lo demás lo completa la base.
        b.Property(x => x.Step).HasDefaultValue(0);
        b.Property(x => x.NodeIndex).HasDefaultValue(0);
        b.Property(x => x.Plan).HasColumnType("jsonb");
        b.Property(x => x.Progress).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        b.Property(x => x.Error).HasMaxLength(2000);
        b.Property(x => x.FromVersion).HasMaxLength(20);
        b.Ignore(x => x.Active);
    }
}

internal sealed class UpdateNodeConfiguration : IEntityTypeConfiguration<UpdateNode>
{
    public void Configure(EntityTypeBuilder<UpdateNode> b)
    {
        b.ToTable("nodes", "updates");
        b.HasKey(x => x.Name);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Version).HasMaxLength(20);
        b.Property(x => x.PreviousVersion).HasMaxLength(20);
        b.Property(x => x.UpdaterVersion).HasMaxLength(20);
        b.Property(x => x.Images).HasColumnType("jsonb");
        b.Property(x => x.PreviousImages).HasColumnType("jsonb");
        b.Property(x => x.Pulled).HasMaxLength(40);
        b.Property(x => x.Applied).HasMaxLength(40);
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.Detail).HasMaxLength(2000);
    }
}

/// <summary>SQL del esquema del actualizador que EF no expresa, para las migraciones.</summary>
public static class UpdatesSql
{
    /// <summary>Un solo pedido activo por instalación: la base es la última palabra.</summary>
    public const string OneActiveRequest = "CREATE UNIQUE INDEX ux_requests_active ON updates.requests ((status IN ('pending', 'running'))) WHERE status IN ('pending', 'running')";

    /// <summary>La única fila de estado, con el identificador al azar de la instalación.</summary>
    public const string Seed = "INSERT INTO updates.state (id, installation_id, channel_serial, revoked_keys, check_rejected, blocked_versions) VALUES (1, gen_random_uuid(), 0, '{}', false, '{}') ON CONFLICT DO NOTHING";

    /// <summary>
    /// Deja a todo rol que no sea el dueño (billing_app en las recetas, que recibe permisos por defecto sobre lo que crean
    /// las migraciones) con lectura y con alta de pedidos, solo con la versión y quién lo pide. Se corre de nuevo en cada
    /// migración que sume una tabla a este esquema.
    /// </summary>
    public const string RestrictOtherRoles = """
        DO $$
        DECLARE r record;
        BEGIN
          FOR r IN SELECT DISTINCT grantee FROM information_schema.role_table_grants
                   WHERE table_schema = 'updates' AND grantee NOT IN (current_user, 'PUBLIC')
          LOOP
            EXECUTE format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA updates FROM %I', r.grantee);
            EXECUTE format('REVOKE UPDATE ON ALL SEQUENCES IN SCHEMA updates FROM %I', r.grantee);
            EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA updates TO %I', r.grantee);
            EXECUTE format('GRANT INSERT (version, requested_by) ON updates.requests TO %I', r.grantee);
          END LOOP;
          REVOKE ALL ON ALL TABLES IN SCHEMA updates FROM PUBLIC;
        END $$
        """;

    public static string VersionCheck(string column) => $"{column} ~ '^(0|[1-9][0-9]{{0,4}})\\.(0|[1-9][0-9]{{0,4}})\\.(0|[1-9][0-9]{{0,5}})$'";

    /// <summary>
    /// Para la migración que quita lo que la minor anterior dejó de usar (sección 7.4): anota que el esquema ya no soporta
    /// núcleos anteriores a <paramref name="version"/>, y un nodo con una versión más vieja no arranca.
    /// </summary>
    public static void RequireCoreVersion(this MigrationBuilder migrationBuilder, string version)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var v = Versions.Parse(version).Text();
        migrationBuilder.Sql($"""
            UPDATE updates.state SET min_core_version = '{v}'
            WHERE min_core_version IS NULL OR string_to_array(min_core_version, '.')::int[] < string_to_array('{v}', '.')::int[]
            """);
    }

    /// <summary>
    /// Un índice que no bloquea escrituras y que se puede repetir: va solo en su migración y fuera de transacción. Si un
    /// intento anterior falló, dejó un índice inválido que un IF NOT EXISTS daría por bueno, por eso primero se borra.
    /// </summary>
    public static void CreateIndexConcurrently(this MigrationBuilder migrationBuilder, string name, string table, string definition)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var schema = table.Split('.')[0];
        migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS {schema}.{name}", suppressTransaction: true);
        migrationBuilder.Sql($"CREATE INDEX CONCURRENTLY {name} ON {table} {definition}", suppressTransaction: true);
    }
}

/// <summary>Lo que la aplicación lee del actualizador y los pedidos que deja (sección 7.2 y 7.3).</summary>
public sealed class UpdateService(BillingDbContext db, AuditLog audit)
{
    public const string Audited = "updates.requested";

    /// <summary>La versión de este binario.</summary>
    public static Version Installed { get; } = Versions.Of(typeof(UpdateService).Assembly);

    public async Task<UpdateOverview> Overview(DateTimeOffset now, CancellationToken ct = default)
    {
        var state = await db.Set<UpdaterState>().AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var nodes = await db.Set<UpdateNode>().AsNoTracking().OrderBy(n => n.Name).ToListAsync(ct).ConfigureAwait(false);
        var requests = await db.Set<UpdateRequest>().AsNoTracking().OrderByDescending(r => r.Id).Take(10).ToListAsync(ct).ConfigureAwait(false);
        var serverVersion = await db.Database.SqlQueryRaw<int>("SELECT current_setting('server_version_num')::int AS \"Value\"").SingleAsync(ct).ConfigureAwait(false);
        var channel = Channel(state);
        var snapshot = new ChannelSnapshot(channel, state?.CheckedAt, state?.CheckAttemptAt, state?.CheckError, state?.CheckRejected ?? false, nodes.Count == 0 ? null : nodes.Max(n => n.SeenAt));
        var summary = UpdateStatus.Summarize(snapshot, Installed, now, serverVersion);
        var blocked = state?.BlockedVersions ?? [];
        var offered = summary.Newer.Select(r => new OfferedRelease(r, Path(channel!, r, blocked), Blocked: blocked.Contains(r.ReleaseVersion))).ToList();
        return new UpdateOverview(summary, state?.CheckedAt, state?.CheckError, offered, nodes, requests);
    }

    public async Task<UpdateRequest?> Find(long id, CancellationToken ct = default) =>
        await db.Set<UpdateRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);

    /// <summary>
    /// Deja el pedido para el actualizador. Solo versiones del último canal verificado con camino desde la instalada, nunca
    /// por debajo del último parche de seguridad de su línea. El actualizador vuelve a validar todo antes de aplicar.
    /// </summary>
    public async Task<long> Request(string version, CancellationToken ct = default)
    {
        if (!Versions.TryParse(version, out var target))
        {
            throw new UpdateException("Pedí una versión con la forma X.Y.Z.");
        }

        var state = await db.Set<UpdaterState>().AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var channel = Channel(state) ?? throw new UpdateException("Todavía no hay un canal verificado: revisá que el actualizador esté corriendo.");
        _ = UpdatePlanner.Plan(channel, Installed, target, state!.BlockedVersions);
        if (UpdatePlanner.SecurityFloor(channel, target) is { } floor && target < floor)
        {
            throw new UpdateException($"Desde el panel no se puede pedir {target.Text()}: su línea tiene el parche de seguridad {floor.Text()}.");
        }

        var actor = Actor.Current.Id;
        long id;
        try
        {
            id = await db.Database.SqlQuery<long>($"INSERT INTO updates.requests (version, requested_by) VALUES ({target.Text()}, {actor}) RETURNING id AS \"Value\"")
                .AsAsyncEnumerable().SingleAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UpdateException("Ya hay una actualización pedida o en curso.");
        }

        await audit.Record(Audited, $"updates/requests/{id.ToString(CultureInfo.InvariantCulture)}", new { version = target.Text(), from = Installed.Text() }, ct).ConfigureAwait(false);
        return id;
    }

    public static ReleaseChannel? Channel(UpdaterState? state) => state?.Channel is { } json ? ReleaseChannel.Parse(json) : null;

    /// <summary>El avance que deja el actualizador en el pedido, para la página de avance y la API.</summary>
    public static IReadOnlyList<UpdateEvent> Events(UpdateRequest request) =>
        JsonSerializer.Deserialize<List<UpdateEvent>>(request?.Progress ?? "[]", JsonSerializerOptions.Web) ?? [];

    private static IReadOnlyList<string> Path(ReleaseChannel channel, Release release, string[] blocked)
    {
        try
        {
            return [.. UpdatePlanner.Plan(channel, Installed, release.Version, blocked).Select(r => r.ReleaseVersion)];
        }
        catch (UpdateException)
        {
            return [];
        }
    }
}

public sealed record OfferedRelease(Release Release, IReadOnlyList<string> Path, bool Blocked);

/// <param name="Event">Started, Rejected, Pulled, Mark, Migrated, NodeApplied, NodeFailed, StepDone, RollingBack, RolledBack, Done o Failed.</param>
public sealed record UpdateEvent(DateTimeOffset At, string Event, string? Node = null, string? Version = null, string? Detail = null);

public sealed record UpdateOverview(UpdateSummary Summary, DateTimeOffset? CheckedAt, string? CheckError, IReadOnlyList<OfferedRelease> Offered, IReadOnlyList<UpdateNode> Nodes, IReadOnlyList<UpdateRequest> Requests);

/// <summary>
/// Al arrancar, el núcleo mira que el esquema esté al día con este binario y que todavía lo soporte (sección 7.4). Si no,
/// no arranca ni como web ni como worker: un nodo que quedó apagado mientras pasaban dos minors no vuelve a atender ni a
/// tomar trabajos, y un binario nuevo no corre sobre una base restaurada a la marca.
/// </summary>
public sealed class SchemaGate(IServiceScopeFactory scopes, ILogger<SchemaGate> logger) : IHostedService
{
    /// <summary>Lo que le pasa al esquema con esta versión: migraciones que faltan, o un esquema que ya no la soporta.</summary>
    public static async Task<string?> Problem(BillingDbContext db, Version version, CancellationToken ct = default) =>
        await Pending(db, version, ct).ConfigureAwait(false) ?? await Unsupported(db, version, ct).ConfigureAwait(false);

    /// <summary>Migraciones que faltan: el nodo arranca, pero no queda listo hasta que corran (D-14, /health/startup).</summary>
    public static async Task<string?> Pending(BillingDbContext db, Version version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(version);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        return pending.Count > 0 ? $"Faltan migraciones ({string.Join(", ", pending)}): corré migrate antes de arrancar la versión {version.Text()}." : null;
    }

    /// <summary>El esquema ya no soporta esta versión (lo anotó una migración posterior): el nodo no arranca.</summary>
    public static async Task<string?> Unsupported(BillingDbContext db, Version version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(version);
        var minimum = await db.Database.SqlQueryRaw<string?>("SELECT min_core_version AS \"Value\" FROM updates.state WHERE id = 1").SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return Versions.TryParse(minimum, out var min) && version < min
            ? $"El esquema ya no soporta la versión {version.Text()}: la más vieja que soporta es {min.Text()}. El actualizador de este nodo lo pone al día."
            : null;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Sin base no se sabe: se reintenta hasta que conteste o hasta que apaguen el proceso.
        while (true)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
                if (await Pending(db, UpdateService.Installed, cancellationToken).ConfigureAwait(false) is { } pending)
                {
                    // Con el esquema atrasado no se mira nada más: el nodo queda sin tráfico hasta que corra migrate.
                    logger.LogWarning("Este nodo no queda listo: {Pending}", pending);
                    return;
                }

                if (await Unsupported(db, UpdateService.Installed, cancellationToken).ConfigureAwait(false) is { } unsupported)
                {
                    logger.LogCritical("No arranca: {Problem}", unsupported);
                    throw new InvalidOperationException(unsupported);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && Failover.IsTransient(ex))
            {
                logger.LogWarning("Esperando a la base para ver si el esquema soporta esta versión: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>El actualizador pausó el planificador y la pausa no venció.</summary>
    public static async Task<bool> SchedulerPaused(BillingDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Database.SqlQueryRaw<bool>("SELECT coalesce(scheduler_paused_until > now(), false) AS \"Value\" FROM updates.state WHERE id = 1")
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
