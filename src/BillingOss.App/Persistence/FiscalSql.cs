namespace BillingOss.App.Persistence;

/// <summary>
/// Lo que EF no expresa en la migración de impuestos y ARCA: las categorías, los impuestos y los ajustes de fábrica, el
/// paso de los productos al modelo de categorías y la métrica de días que le quedan a cada certificado fiscal.
/// </summary>
internal static class FiscalSql
{
    /// <summary>Después de agregar tax_category y antes de borrar tax_treatment: cada producto conserva su trato.</summary>
    public const string ProductsToCategories = """
        UPDATE catalog.products SET tax_category = CASE tax_treatment WHEN 'Exempt' THEN 'exempt' WHEN 'NotTaxed' THEN 'untaxed' ELSE 'digital' END;
        """;

    /// <summary>Para volver atrás: cada categoría vuelve a su trato.</summary>
    public const string CategoriesToProducts = """
        UPDATE catalog.products SET tax_treatment = CASE tax_category WHEN 'exempt' THEN 'Exempt' WHEN 'untaxed' THEN 'NotTaxed' ELSE 'Taxed' END;
        """;

    public const string Seed = """
        INSERT INTO catalog.tax_categories (code, name, explanation, treatment, reason, external_codes, valid_from) VALUES
          ('digital', 'Servicios digitales y hosting', 'Hosting, VPS, game servers, bare metal y abonos. La alícuota general.', 'Taxed', NULL, '{}', '2000-01-01'),
          ('telecom', 'Telecomunicaciones', 'Telefonía y enlaces, si el contador los ubica acá: 27 % a responsables inscriptos y monotributistas fuera de una vivienda.', 'Taxed', NULL, '{}', '2000-01-01'),
          ('exempt', 'Exento por ley', 'Lo que la Ley de IVA exime. Va como exento ante el fisco.', 'Exempt', 'exento_por_ley', '{}', '2000-01-01'),
          ('untaxed', 'No gravado', 'Lo que no es una operación gravada. Va como no gravado ante el fisco.', 'NotTaxed', 'no_gravado', '{}', '2000-01-01')
        ON CONFLICT (code) DO NOTHING;
        INSERT INTO catalog.taxes (code, name, level, base, fiscal_code, adds_to_total, "group", valid_from) VALUES
          ('iva', 'IVA', 'Line', 'Net', NULL, true, NULL, '2000-01-01'),
          ('perc_iva_rg2126', 'Percepción de IVA RG 2126', 'Document', 'NetPlusLineTaxes', 13, true, 'Percepciones', '2000-01-01'),
          ('isib_caba', 'Alícuota ISIB CABA', 'Document', 'Net', NULL, false, 'Leyendas', '2000-01-01')
        ON CONFLICT (code) DO NOTHING;
        INSERT INTO catalog.tax_settings (id, issuer_country, price_mode_by_currency, catalog_display, business_may_view_net, consumer_id_threshold, consumer_id_threshold_from, fce_minimum, fce_minimum_from, gross_income_regimes)
        VALUES (1, 'AR', '{"ARS":"TaxIncluded","USD":"TaxExcluded"}', 'final_with_net', true, 10000000, '2026-07-01', 5549862, '2026-04-14', '[]')
        ON CONFLICT (id) DO NOTHING;
        """;

    /// <summary>
    /// Las filas que ya existían cuando se agregaron las columnas: el modo de carga de los servicios, de los precios y
    /// de los dominios era el global con IVA incluido. Sin esto quedan con un valor que no es ningún miembro del enum.
    /// La condición del emisor ya llega con 1 (responsable inscripto) desde la migración Integracion.
    /// </summary>
    public const string Backfill = """
        UPDATE services.services SET price_mode = 'TaxIncluded' WHERE price_mode = '';
        UPDATE catalog.plan_prices SET price_mode = 'TaxIncluded' WHERE price_mode = '';
        UPDATE domains.domains SET price_mode = 'TaxIncluded' WHERE price_mode = '';
        """;

    /// <summary>La función de métricas de la migración Integracion, más los días que le quedan a cada certificado activo.</summary>
    public const string MetricsUp = """
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
          UNION ALL
          SELECT 'fiscal_certificate_days', alias || ':' || environment, floor(extract(epoch FROM not_after - as_of) / 86400)::float8
          FROM fiscal.credentials WHERE status = 'Active' AND not_after IS NOT NULL
        $$;
        """;

    /// <summary>La función de métricas de la migración Integracion, para volver atrás.</summary>
    public const string MetricsDown = """
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
        """;
}
