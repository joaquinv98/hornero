using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BillingOss.Web;

/// <summary>Lo que se espera de la base (sección Database). Ver docs/alta-disponibilidad/configuracion.md.</summary>
public sealed class ClusterOptions
{
    /// <summary>
    /// single: una base sin réplica. sync: PostgreSQL propio con réplica sincrónica (Patroni o CloudNativePG), que se
    /// comprueba en pg_stat_replication. managed: servicio administrado con HA sincrónica declarada por el proveedor.
    /// </summary>
    public string Durability { get; set; } = "single";

    /// <summary>Desvío máximo entre el reloj del nodo y el de la base.</summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Estado del clúster para el arranque y la alerta: con Durability=sync hay al menos una réplica sincrónica, y el reloj
/// del nodo no se aleja del de la base. No va en listo.
/// </summary>
internal sealed class ClusterCheck(BillingDbContext db, IOptions<ClusterOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        try
        {
            // Reloj real y no TimeProvider: lo que importa es la hora de la máquina contra la de la base.
            var before = DateTimeOffset.UtcNow;
            var dbNow = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
            var skew = (dbNow - (before + ((DateTimeOffset.UtcNow - before) / 2))).Duration();
            if (skew > o.MaxClockSkew)
            {
                return HealthCheckResult.Unhealthy($"El reloj del nodo está {skew.TotalSeconds:0.0} s corrido respecto de la base.");
            }

            if (o.Durability == "sync")
            {
                // Hace falta el rol pg_monitor para ver sync_state de las réplicas.
                var standbys = await db.Database
                    .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_replication WHERE sync_state IN ('sync', 'quorum')")
                    .SingleAsync(cancellationToken).ConfigureAwait(false);
                if (standbys == 0)
                {
                    return HealthCheckResult.Unhealthy("Durability=sync y no hay ninguna réplica sincrónica: un commit confirmado se puede perder en una conmutación.");
                }
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failover.Recover(ex);
            return HealthCheckResult.Unhealthy("No hay conexión con la base.", ex);
        }
    }
}

/// <summary>
/// Vigía del clúster, con Database:Durability=sync: cada pocos segundos mira si PostgreSQL está esperando a una réplica
/// sincrónica que no tiene (synchronous_standby_names con valor y ninguna réplica en sync ni quorum en
/// pg_stat_replication). Mientras sea así cierra el freno de las escrituras (<see cref="WriteGate"/>): el corredor no
/// toma trabajos y la API da 503 a las escrituras, porque cualquier COMMIT quedaría esperando hasta el Command Timeout
/// y después confirmado solo en la primaria. Lo abre apenas vuelve una réplica, o apenas el operador apaga el modo
/// estricto (synchronous_standby_names vacío: la base ya no espera a nadie). Necesita pg_read_all_stats en el rol de
/// la aplicación (deploy/sql/roles.sql).
/// </summary>
public sealed class ClusterWatch(IServiceScopeFactory scopes, WriteGate gate, ILogger<ClusterWatch> logger) : BackgroundService
{
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>True si la base está esperando a una réplica sincrónica que no tiene.</summary>
    public static Task<bool> WaitingForReplica(BillingDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.SqlQueryRaw<bool>("""
            SELECT current_setting('synchronous_standby_names') <> ''
               AND NOT EXISTS (SELECT 1 FROM pg_stat_replication WHERE sync_state IN ('sync', 'quorum')) AS "Value"
            """).SingleAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var waiting = await WaitingForReplica(scope.ServiceProvider.GetRequiredService<BillingDbContext>(), stoppingToken).ConfigureAwait(false);
                if (waiting && gate.Open)
                {
                    gate.Close("La base no tiene ninguna réplica sincrónica: no se aceptan escrituras hasta que vuelva una.");
                    logger.LogWarning("{Reason}", gate.ClosedReason);
                }
                else if (!waiting && !gate.Open)
                {
                    gate.Reopen();
                    logger.LogInformation("Volvió una réplica sincrónica: se vuelven a aceptar escrituras.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin respuesta de la base el freno queda como está: lo que escriba falla solo, como falla pasajera.
                Failover.Recover(ex);
                logger.LogDebug(ex, "El vigía del clúster no pudo consultar la base");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Una falla de conmutación en un pedido HTTP: 503 con Retry-After. El cliente repite con la misma clave de
/// idempotencia y para entonces la aplicación ya encontró la primaria nueva. Nunca se reintenta adentro del pedido.
/// </summary>
internal sealed class DatabaseFailoverHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!Failover.Recover(exception))
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "5";
        return await problems.TryWriteAsync(new()
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Title = "La base está conmutando. Repetí el pedido con la misma clave de idempotencia." },
        }).ConfigureAwait(false);
    }
}
