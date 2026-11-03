namespace BillingOss.App.Persistence;

/// <summary>
/// Lo que EF no expresa en la migración de observabilidad (D-15): auditoría que solo se agrega, la función de
/// métricas de negocio y el índice de avisos sin procesar.
/// </summary>
internal static class ObservabilitySql
{
    public const string Up = """
        -- La auditoría solo se agrega: nada la cambia ni la borra desde la aplicación.
        CREATE FUNCTION platform.audit_events_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          RAISE EXCEPTION 'La auditoría no se modifica ni se borra' USING ERRCODE = 'check_violation';
        END $$;
        CREATE TRIGGER audit_events_append_only BEFORE UPDATE OR DELETE ON platform.audit_events
          FOR EACH ROW EXECUTE FUNCTION platform.audit_events_append_only();
        CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON platform.audit_events
          FOR EACH STATEMENT EXECUTE FUNCTION platform.audit_events_append_only();

        -- Avisos sin verificar: la métrica los cuenta a cada rato sin recorrer los ya procesados.
        CREATE INDEX ix_webhook_receipts_unprocessed ON payments.webhook_receipts (received_at) WHERE status = 'Received';

        -- Métricas de negocio para cualquier cantidad de nodos: salen de la base, no de la memoria de un proceso.
        -- La leen la aplicación (OpenTelemetry) y Zabbix. Corre con los permisos del dueño, así un rol de monitoreo
        -- solo necesita USAGE sobre el esquema platform y ve conteos, nunca filas.
        CREATE FUNCTION platform.metrics(as_of timestamptz DEFAULT now())
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
          SELECT 'fiscal_emissions', status::text, count(*)::float8 FROM fiscal.emissions GROUP BY status
          UNION ALL
          SELECT 'webhooks_unprocessed', '', count(*)::float8 FROM payments.webhook_receipts WHERE status = 'Received'
          UNION ALL
          SELECT 'webhooks_oldest_seconds', '', coalesce(extract(epoch FROM as_of - min(received_at)), 0)::float8
          FROM payments.webhook_receipts WHERE status = 'Received'
          UNION ALL
          SELECT 'payments_pending', status::text, count(*)::float8 FROM payments.payments
          WHERE status IN ('Pending', 'Disputed') GROUP BY status
          UNION ALL
          SELECT x.name, '', x.value
          FROM (SELECT (details ->> 'seconds')::float8 AS seconds, extract(epoch FROM as_of - created_at)::float8 AS age
                FROM platform.audit_events WHERE action = 'billing.run'
                ORDER BY created_at DESC LIMIT 1) r
          CROSS JOIN LATERAL (VALUES ('billing_run_seconds', r.seconds), ('billing_run_age_seconds', r.age)) AS x(name, value)
          WHERE x.value IS NOT NULL
        $$;
        -- SECURITY DEFINER: solo la ejecuta quien recibió el permiso (rol-metricas.sql), no cualquier rol con USAGE.
        REVOKE ALL ON FUNCTION platform.metrics(timestamptz) FROM PUBLIC;
        """;

    public const string Down = """
        DROP FUNCTION IF EXISTS platform.metrics(timestamptz);
        DROP INDEX IF EXISTS payments.ix_webhook_receipts_unprocessed;
        DROP TRIGGER IF EXISTS audit_events_no_truncate ON platform.audit_events;
        DROP TRIGGER IF EXISTS audit_events_append_only ON platform.audit_events;
        DROP FUNCTION IF EXISTS platform.audit_events_append_only();
        """;
}
