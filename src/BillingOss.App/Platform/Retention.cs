using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Platform;

/// <summary>
/// Retención diaria, en lotes chicos y cada uno en su propia transacción, para no frenar a nadie: borra los trabajos
/// hechos de más de Workers:KeepDone (los muertos quedan hasta que los resuelva el staff) y los avisos de pasarela
/// inválidos del mismo plazo, que ya no tienen cuerpo ni eventos, vacía el cuerpo de los avisos válidos procesados de
/// más de Workers:KeepWebhookBodies conservando la fila, y borra los eventos de pasarela de más de
/// Workers:KeepGatewayEvents. Los avisos sin procesar quedan enteros: son los que muestra el monitoreo. Con los índices
/// parciales la toma no depende del historial, pero la tabla y los backups sí. También borra el historial de ingresos
/// más viejo que su ajuste, las entradas de las listas de bloqueo vencidas hace más de 30 días y los desafíos anti robots
/// ya vencidos.
/// </summary>
public sealed class RetentionHandler(BillingDbContext db, TimeProvider time, IOptions<WorkerOptions> options, Identity.SecuritySettingsStore security) : IJobHandler
{
    public const string JobKind = "platform.retention";

    private const int Batch = 5000;

    public string Kind => JobKind;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var o = options.Value;
        var doneBefore = now - o.KeepDone;
        var bodiesBefore = now - o.KeepWebhookBodies;
        var eventsBefore = now - o.KeepGatewayEvents;
        await InBatches(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM payments.webhook_receipts WHERE id IN (
              SELECT id FROM payments.webhook_receipts WHERE status = 'Invalid' AND received_at < {doneBefore} LIMIT {Batch})
            """, ct), ct).ConfigureAwait(false);
        await InBatches(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM platform.jobs WHERE id IN (
              SELECT id FROM platform.jobs WHERE status = 'Done' AND updated_at < {doneBefore} LIMIT {Batch})
            """, ct), ct).ConfigureAwait(false);
        await InBatches(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.webhook_receipts SET body = ''::bytea WHERE id IN (
              SELECT id FROM payments.webhook_receipts
              WHERE octet_length(body) > 0 AND status <> 'Received' AND received_at < {bodiesBefore} LIMIT {Batch})
            """, ct), ct).ConfigureAwait(false);
        await InBatches(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM payments.gateway_events WHERE id IN (
              SELECT id FROM payments.gateway_events WHERE created_at < {eventsBefore} LIMIT {Batch})
            """, ct), ct).ConfigureAwait(false);

        // El registro de entregas de webhooks, con Workers:KeepDone. Una pendiente todavía tiene su trabajo vivo y queda.
        await InBatches(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM platform.webhook_deliveries WHERE id IN (
              SELECT id FROM platform.webhook_deliveries WHERE status <> 'Pending' AND created_at < {doneBefore} LIMIT {Batch})
            """, ct), ct).ConfigureAwait(false);

        // Seguridad de acceso (diseno/fraude-y-acceso.md y diseno/ingreso-clientes.md).
        var logins = now.AddDays(-(await security.Get(ct).ConfigureAwait(false)).KeepLoginDays);
        await InBatches(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM identity.login_events WHERE id IN (
              SELECT id FROM identity.login_events WHERE created_at < {logins} LIMIT {Batch})
            """, ct), ct).ConfigureAwait(false);
        var expired = now.AddDays(-30);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.blocked_entries WHERE expires_at < {expired}", ct).ConfigureAwait(false);

        // Los desafíos anti robots ya usados solo importan mientras el desafío vale.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.challenge_uses WHERE expires_at < {now}", ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    private async Task InBatches(Func<Task<int>> step, CancellationToken ct)
    {
        int affected;
        do
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            affected = await step().ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        while (affected == Batch);
    }
}
