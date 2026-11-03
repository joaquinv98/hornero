using System.Data.Common;
using BillingOss.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BillingOss.App.Persistence;

/// <summary>SQL directo, en la misma conexión y transacción que el contexto, para locks y contadores.</summary>
internal static class Sql
{
    public static async Task<DbCommand> Command(BillingDbContext db, string text, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }

        var cmd = connection.CreateCommand();
        cmd.CommandText = text;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    /// <summary>
    /// Una transacción propia si el que llama no abrió ninguna, o null si ya hay una: así un caso de uso se compone dentro
    /// de otro, como el alta de una cuenta con su grupo, sus campos y su usuario, que queda entera o no queda.
    /// </summary>
    public static async Task<IDbContextTransaction?> OwnTransaction(BillingDbContext db, CancellationToken ct) =>
        db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;

    /// <summary>Confirma la transacción propia de <see cref="OwnTransaction"/>. La del que llama la confirma él.</summary>
    public static async Task Commit(IDbContextTransaction? own, CancellationToken ct)
    {
        if (own is not null)
        {
            await own.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Próximo número comercial del tipo. El UPDATE deja la fila bloqueada hasta el commit: sin huecos.</summary>
    public static async Task<long> NextNumber(BillingDbContext db, DocumentKind kind, CancellationToken ct)
    {
        await using var cmd = await Command(db, "UPDATE billing.counters SET next = next + 1 WHERE kind = @kind RETURNING next - 1", ct, ("kind", kind.ToString())).ConfigureAwait(false);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Bloquea filas por ID en orden, para que dos operaciones no se crucen (sección 13 del modelo).</summary>
    public static async Task LockRows(BillingDbContext db, string table, IEnumerable<long> ids, CancellationToken ct, string strength = "UPDATE")
    {
        var sorted = ids.Distinct().Order().ToArray();
        if (sorted.Length == 0)
        {
            return;
        }

        await using var cmd = await Command(db, $"SELECT id FROM {table} WHERE id = ANY(@ids) ORDER BY id FOR {strength}", ct, ("ids", sorted)).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
        }
    }

    /// <summary>
    /// Lock de facturación de la cuenta: serializa la corrida, la emisión de sus borradores y la reemisión. Es NO KEY
    /// UPDATE para no frenar las altas que referencian la cuenta (las claves foráneas toman KEY SHARE), así no se cruza
    /// con pagos ni saldos.
    /// </summary>
    public static Task LockAccount(BillingDbContext db, long accountId, CancellationToken ct) =>
        LockRows(db, "accounts.accounts", [accountId], ct, "NO KEY UPDATE");

    /// <summary>Bloquea el saldo a favor de la cuenta en la moneda, creándolo en cero si no existe.</summary>
    public static async Task LockCreditBalance(BillingDbContext db, long accountId, string currency, CancellationToken ct)
    {
        await using var cmd = await Command(db, """
            INSERT INTO billing.credit_balances (account_id, currency, balance) VALUES (@a, @c, 0) ON CONFLICT DO NOTHING;
            SELECT balance FROM billing.credit_balances WHERE account_id = @a AND currency = @c FOR UPDATE;
            """, ct, ("a", accountId), ("c", currency)).ConfigureAwait(false);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
