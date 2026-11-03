namespace BillingOss.App.Persistence;

/// <summary>
/// Lo que EF no expresa en la migración de endurecimiento: la función de métricas de negocio con las filas de la
/// conciliación y los pagos sin casar, y los conteos fiscales y de pagos acotados a lo que alerta, que es lo que
/// cubren los índices parciales ix_emissions_open e ix_payments_open. Reemplaza la de la migración ImpuestosArca
/// (<see cref="FiscalSql.MetricsUp"/>) y conserva sus días de certificado fiscal.
/// </summary>
internal static class HardeningSql
{
    public const string Up = """
        CREATE OR REPLACE FUNCTION platform.metrics(as_of timestamptz DEFAULT now())
        RETURNS TABLE (name text, label text, value double precision)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
          SELECT m.name, j.kind::text, m.value
          FROM (
            SELECT kind,
                   count(*) FILTER (WHERE status = 'Pending') AS pending,
                   count(*) FILTER (WHERE status = 'Pending' AND run_at <= as_of) AS due,
                   count(*) FILTER (WHERE status = 'Pending' AND attempts > 0) AS retrying,
                   count(*) FILTER (WHERE status = 'Running' AND locked_until >= as_of) AS running,
                   count(*) FILTER (WHERE status = 'Running' AND locked_until < as_of) AS stuck,
                   count(*) FILTER (WHERE status = 'Dead') AS dead,
                   coalesce(extract(epoch FROM as_of - min(run_at) FILTER (WHERE status = 'Pending' AND run_at <= as_of)), 0) AS oldest
            FROM platform.jobs
            WHERE status IN ('Pending', 'Running', 'Dead')
            GROUP BY kind) j
          CROSS JOIN LATERAL (VALUES
            ('jobs_pending', j.pending::float8),
            ('jobs_due', j.due::float8),
            ('jobs_retrying', j.retrying::float8),
            ('jobs_running', j.running::float8),
            ('jobs_stuck', j.stuck::float8),
            ('jobs_dead', j.dead::float8),
            ('jobs_oldest_due_seconds', j.oldest::float8)) AS m(name, value)
          UNION ALL
          -- Solo las que no terminaron bien: las autorizadas crecen para siempre y no alertan.
          SELECT 'fiscal_emissions', status::text, count(*)::float8 FROM fiscal.emissions WHERE status <> 'Authorized' GROUP BY status
          UNION ALL
          SELECT 'webhooks_unprocessed', '', count(*)::float8 FROM payments.webhook_receipts WHERE status = 'Received'
          UNION ALL
          SELECT 'webhooks_oldest_seconds', '', coalesce(extract(epoch FROM as_of - min(received_at)), 0)::float8
          FROM payments.webhook_receipts WHERE status = 'Received'
          UNION ALL
          SELECT 'payments_pending', status::text, count(*)::float8 FROM payments.payments
          WHERE status IN ('Pending', 'Disputed') GROUP BY status
          UNION ALL
          SELECT 'payments_unmatched', '', count(*)::float8 FROM payments.unmatched WHERE resolved_at IS NULL
          UNION ALL
          SELECT 'reconcile_findings', kind, count(*)::float8 FROM platform.reconcile_findings WHERE resolved_at IS NULL GROUP BY kind
          UNION ALL
          SELECT x.name, '', x.value
          FROM (SELECT (details ->> 'seconds')::float8 AS seconds, extract(epoch FROM as_of - created_at)::float8 AS age
                FROM platform.audit_events WHERE action = 'billing.run'
                ORDER BY created_at DESC LIMIT 1) r
          CROSS JOIN LATERAL (VALUES ('billing_run_seconds', r.seconds), ('billing_run_age_seconds', r.age)) AS x(name, value)
          WHERE x.value IS NOT NULL
          UNION ALL
          SELECT 'fiscal_certificate_days', alias || ':' || environment, floor(extract(epoch FROM not_after - as_of) / 86400)::float8
          FROM fiscal.credentials WHERE status = 'Active' AND not_after IS NOT NULL
        $$;
        """;

    /// <summary>Vuelve a la función de la migración ImpuestosArca, sin las tablas que esta migración borra.</summary>
    public const string Down = FiscalSql.MetricsUp;
}
