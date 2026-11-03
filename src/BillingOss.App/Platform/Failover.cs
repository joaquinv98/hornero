using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BillingOss.App.Platform;

/// <summary>
/// La aplicación frente a una base con alta disponibilidad (docs/alta-disponibilidad): varios hosts con conmutación del
/// lado del cliente, una segunda cadena opcional para leer de réplicas y las fallas de una conmutación reconocidas como
/// pasajeras.
/// </summary>
public static class Failover
{
    /// <summary>Clave del contexto que lee de réplicas: <c>[FromKeyedServices(Failover.Replica)] BillingDbContext</c>.</summary>
    public const string Replica = "replica";

    /// <summary>
    /// Command Timeout mínimo. También acota el COMMIT: si vence mientras el COMMIT espera a la réplica sincrónica, la
    /// base lo da por bueno solo en la primaria. Tiene que pasar el tiempo en que el clúster degrada a una primaria
    /// aislada (en Patroni, ttl más loop_wait, 40 s con los valores de la guía), con margen.
    /// </summary>
    public const int MinCommandTimeout = 90;

    /// <summary>
    /// Cadena principal. Con varios hosts y sin Target Session Attributes se conecta solo al que acepta escrituras: una
    /// conmutación se sigue sola y nunca se escribe en una réplica. Cualquier otro destino que no sea la primaria es un
    /// error de configuración, porque la principal escribe. El Command Timeout sube a <see cref="MinCommandTimeout"/>
    /// si es menor (0, sin límite, queda).
    /// </summary>
    public static string MainConnection(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (b.TargetSessionAttributes is { Length: > 0 } target
            && !target.Equals("read-write", StringComparison.OrdinalIgnoreCase)
            && !target.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ConnectionStrings:Main tiene que ir a la primaria (Target Session Attributes=read-write), no a {target}. Para leer de réplicas está ConnectionStrings:Replica.");
        }

        if (string.IsNullOrEmpty(b.TargetSessionAttributes) && b.Host?.Contains(',', StringComparison.Ordinal) == true)
        {
            b.TargetSessionAttributes = "read-write";
        }

        if (b.CommandTimeout is > 0 and < MinCommandTimeout)
        {
            b.CommandTimeout = MinCommandTimeout;
        }

        return b.ConnectionString;
    }

    /// <summary>
    /// Cadena de réplicas. Si no dice otra cosa prefiere un standby y reparte las conexiones entre los hosts. Las
    /// sesiones son de solo lectura aunque caigan en la primaria, así que nada puede escribir por acá.
    /// </summary>
    public static string ReplicaConnection(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(b.TargetSessionAttributes) && b.Host?.Contains(',', StringComparison.Ordinal) == true)
        {
            b.TargetSessionAttributes = "prefer-standby";
            b.LoadBalanceHosts = true;
        }

        b.Options = $"{b.Options} -c default_transaction_read_only=on".Trim();
        return b.ConnectionString;
    }

    /// <summary>
    /// Registra el contexto de lecturas en réplicas, sin seguimiento de cambios. Es solo para lo que tolera leer algo
    /// apenas viejo (listados del staff, reportes, exportaciones): nunca para casos de uso, trabajos, cobros, lo fiscal,
    /// la autenticación ni una lectura que sigue a una escritura. Sin cadena de réplicas lee de la principal.
    /// </summary>
    public static IServiceCollection AddBillingReplica(this IServiceCollection services, string? connectionString)
    {
        var options = connectionString is null
            ? null
            : DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), ReplicaConnection(connectionString))
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
        return services.AddKeyedScoped(Replica, (sp, _) => new BillingDbContext(options
            ?? new DbContextOptionsBuilder<BillingDbContext>(sp.GetRequiredService<DbContextOptions<BillingDbContext>>())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options));
    }

    /// <summary>
    /// Falla de una conmutación: el servidor se apagó o no acepta conexiones (57P01, 57P02, 57P03), la conexión se
    /// rompió o el resultado del commit quedó incierto (clase 08), la base pasó a solo lectura (25006), un corte de red,
    /// o ningún host aceptó escrituras. No es culpa del trabajo y se resuelve sola. Los timeouts no entran: pueden ser
    /// una consulta pesada que repetida sin tope castigaría a la base, y van por los reintentos comunes.
    /// </summary>
    public static bool IsTransient(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case CommitUncertainException:
                    return true;
                case PostgresException p:
                    return p.SqlState is PostgresErrorCodes.ReadOnlySqlTransaction or PostgresErrorCodes.AdminShutdown
                            or PostgresErrorCodes.CrashShutdown or PostgresErrorCodes.CannotConnectNow
                        || p.SqlState.StartsWith("08", StringComparison.Ordinal);
                case NpgsqlException n:
                    // Del lado del cliente: conexión rota o sin host adecuado. Solo el timeout queda afuera.
                    return n.InnerException is not TimeoutException;
            }
        }

        return false;
    }

    /// <summary>La excepción del tipo pedido en la cadena, o null.</summary>
    public static T? Find<T>(Exception? ex)
        where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Si la falla es de una conmutación, descarta las conexiones del pool: las que quedaron apuntan a un servidor caído
    /// o a la primaria vieja, y las nuevas buscan la primaria actual. Devuelve si la falla era de una conmutación.
    /// </summary>
    public static bool Recover(Exception? ex)
    {
        if (!IsTransient(ex))
        {
            return false;
        }

        NpgsqlConnection.ClearAllPools();
        return true;
    }
}
