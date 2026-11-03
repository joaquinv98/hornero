using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace BillingOss.App.Platform;

/// <summary>
/// La base confirmó la transacción solo en la primaria y no se sabe si llegó a la réplica: el COMMIT se canceló
/// mientras esperaba a la réplica sincrónica, o la conexión se cortó en el medio. Lo escrito puede desaparecer en la
/// próxima conmutación. Nunca es un éxito: el trabajo se reintenta y la API responde 503 para que el cliente repita con
/// la misma clave de idempotencia.
/// </summary>
public sealed class CommitUncertainException(string message) : Exception(message);

/// <summary>El trabajo ya no es de este proceso: venció el lease o lo tomó otro nodo. Lo que iba a guardar no se guarda.</summary>
public sealed class LeaseLostException(string message) : Exception(message);

/// <summary>
/// Cerca de las escrituras de un trabajo (D-07). Mientras corre un manejador, cada transacción que confirma su contexto
/// comprueba en la misma transacción que el trabajo sigue siendo de esta toma. Si no lo es, el COMMIT no sale: un nodo
/// que perdió el lease (pausa larga, partición, reloj corrido) no pisa lo que ya hizo el nodo que lo retomó.
/// </summary>
/// <remarks>
/// Cubre SaveChanges y las transacciones explícitas, y como red de seguridad también el SQL suelto (ExecuteSql,
/// ExecuteUpdate, ExecuteDelete) que un manejador corre fuera de una transacción: antes de ejecutarlo hace la misma
/// comprobación en la misma conexión. ponytail: esa comprobación no es atómica con la escritura (hay microsegundos
/// entre las dos), así que lo que escribe un trabajo va en una transacción, y las escrituras compartidas (encolar,
/// auditar, guardar el estado de una instancia) abren una propia si no hay ninguna.
/// </remarks>
public sealed class JobFence : DbTransactionInterceptor, ISaveChangesInterceptor, IDbCommandInterceptor
{
    private static readonly AsyncLocal<ClaimedJob?> Current = new();

    public static JobFence Instance { get; } = new();

    /// <summary>Corre el manejador con la cerca puesta. Lo que se cambia en esta llamada no vuelve al que llama.</summary>
    public static async Task<T> Run<T>(ClaimedJob job, Func<Task<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Current.Value = job;
        return await handler().ConfigureAwait(false);
    }

    /// <summary>
    /// Hasta un guardado de una sola fila va en una transacción: con la cerca puesta, para poder cercarlo, y siempre,
    /// para que el aviso de un COMMIT confirmado sin réplica llegue a <see cref="CommitGuard"/> (un INSERT suelto se
    /// confirma solo y nadie lo escucha).
    /// </summary>
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
        {
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        }

        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SavingChanges(eventData, result));

    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        if (Current.Value is { } job)
        {
            using var cmd = Command(transaction.Connection!, transaction, job);
            Check(cmd.ExecuteNonQuery(), job);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Current.Value is { } job)
        {
            await using var cmd = Command(transaction.Connection!, transaction, job);
            Check(await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false), job);
        }

        return result;
    }

    /// <summary>SQL suelto de un manejador, sin transacción: se comprueba la toma justo antes, en la misma conexión.</summary>
    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Current.Value is { } job && command.Transaction is null)
        {
            using var check = Command(command.Connection!, null, job);
            Check(check.ExecuteNonQuery(), job);
        }

        return result;
    }

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Current.Value is { } job && command.Transaction is null)
        {
            await using var check = Command(command.Connection!, null, job);
            Check(await check.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false), job);
        }

        return result;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, ClaimedJob job)
    {
        // clock_timestamp y no now(): now() es la hora en que empezó la transacción.
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE platform.jobs SET updated_at = clock_timestamp()
            WHERE id = @id AND locked_by = @worker AND attempts = @attempts AND status = 'Running' AND locked_until > clock_timestamp()
            """;
        cmd.Parameters.Add(new NpgsqlParameter("id", job.Id));
        cmd.Parameters.Add(new NpgsqlParameter("worker", job.LockedBy));
        cmd.Parameters.Add(new NpgsqlParameter("attempts", job.Attempts));
        return cmd;
    }

    private static void Check(int rows, ClaimedJob job)
    {
        if (rows != 1)
        {
            throw new LeaseLostException($"El trabajo {job.Id} ({job.Kind}) ya no es de esta toma: no se guarda lo que hizo.");
        }
    }
}

/// <summary>
/// Un documento emitido no se confirma sin número comercial. El número lo asigna
/// <see cref="UseCases.BillingCore.Commit"/> justo antes del commit, y si una transacción con un documento emitido se
/// confirma por otro camino, no sale: mejor que una factura sin número.
/// </summary>
public sealed class NumberingGuard : DbTransactionInterceptor
{
    public static NumberingGuard Instance { get; } = new();

    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Check(eventData);
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Check(eventData);
        return ValueTask.FromResult(result);
    }

    private static void Check(TransactionEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context?.ChangeTracker.Entries<Domain.Billing.Document>().Any(e => e.State != EntityState.Detached && e.Entity.IsIssued && e.Entity.Number is null) == true)
        {
            throw new InvalidOperationException("Hay un documento emitido sin número comercial: la transacción que emite se confirma con BillingCore.Commit.");
        }
    }
}

/// <summary>
/// Guarda del COMMIT. Uno: ningún token de un pedido o de un trabajo lo cancela. Con replicación sincrónica, un COMMIT
/// cancelado mientras espera a la réplica (un cliente que corta, el latido que pierde el lease, el apagado) queda
/// confirmado solo en la primaria, así que el COMMIT corre siempre con CancellationToken.None, también el implícito de
/// SaveChanges, y solo lo acota el Command Timeout de la cadena (90 s o más, que supera la degradación del clúster).
/// Dos: detecta un COMMIT que la base dio por bueno sin garantía de réplica. PostgreSQL avisa con un WARNING en el mismo
/// COMMIT cuando se cancela la espera de la réplica sincrónica (por ese timeout, statement_timeout de un rol o un
/// operador), y la transacción queda visible solo en la primaria. Cualquier WARNING durante el COMMIT se toma como ese
/// caso: el texto viene traducido según el servidor y en un COMMIT no hay otros.
/// </summary>
public sealed class CommitGuard : DbTransactionInterceptor
{
    private static readonly ConditionalWeakTable<DbTransaction, Watch> Watches = [];

    public static CommitGuard Instance { get; } = new();

    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Start(transaction);
        return result;
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Start(transaction);

        // El COMMIT lo hace la guarda, sin el token del que llama: EF no lo repite (Suppress) y sigue con Committed.
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return InterceptionResult.Suppress();
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Stop(transaction, throwIfWarned: true);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Stop(transaction, throwIfWarned: true);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Stop(transaction, throwIfWarned: false);

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Stop(transaction, throwIfWarned: false);
        return Task.CompletedTask;
    }

    private static void Start(DbTransaction transaction)
    {
        if (transaction.Connection is NpgsqlConnection connection)
        {
            var watch = new Watch(connection);
            Watches.AddOrUpdate(transaction, watch);
            connection.Notice += watch.OnNotice;
        }
    }

    private static void Stop(DbTransaction transaction, bool throwIfWarned)
    {
        if (!Watches.TryGetValue(transaction, out var watch))
        {
            return;
        }

        Watches.Remove(transaction);
        watch.Connection.Notice -= watch.OnNotice;
        if (throwIfWarned && watch.Warning is { } warning)
        {
            throw new CommitUncertainException($"La base confirmó sin garantía de réplica: {warning}");
        }
    }

    private sealed class Watch(NpgsqlConnection connection)
    {
        public NpgsqlConnection Connection { get; } = connection;

        public string? Warning { get; private set; }

        public void OnNotice(object sender, NpgsqlNoticeEventArgs e)
        {
            if (e.Notice.InvariantSeverity == "WARNING")
            {
                Warning ??= e.Notice.MessageText;
            }
        }
    }
}
