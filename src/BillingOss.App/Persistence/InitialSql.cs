namespace BillingOss.App.Persistence;

/// <summary>Lo que EF no expresa en la migración inicial: exclusión de períodos, congelamiento y contadores.</summary>
internal static class InitialSql
{
    public const string Up = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;

        -- Dos períodos vigentes del mismo servicio nunca se superponen (sección 6 del modelo de dominio).
        ALTER TABLE billing.billed_periods
          ADD CONSTRAINT ex_billed_periods_overlap
          EXCLUDE USING gist (service_id WITH =, daterange(start, "end", '[)') WITH &&) WHERE (NOT released);

        -- Numeración comercial por tipo de documento: se asigna con UPDATE ... RETURNING, bajo lock de fila.
        CREATE TABLE billing.counters (
          kind varchar(32) PRIMARY KEY,
          next bigint NOT NULL CHECK (next > 0));
        INSERT INTO billing.counters (kind, next) VALUES ('Invoice', 1), ('CreditNote', 1), ('DebitNote', 1);

        -- Un documento emitido queda congelado: solo cambian sus importes imputados y acreditados (D-05).
        CREATE FUNCTION billing.documents_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_OP = 'DELETE' THEN
            IF OLD.issue_date IS NOT NULL THEN
              RAISE EXCEPTION 'El documento % está emitido y no se borra', OLD.id USING ERRCODE = 'check_violation';
            END IF;
            RETURN OLD;
          END IF;
          IF OLD.issue_date IS NOT NULL AND (
               NEW.kind, NEW.number, NEW.account_id, NEW.currency, NEW.price_mode, NEW.issue_date, NEW.due_date,
               NEW.related_document_id, NEW.receiver, NEW.aliquots, NEW.net_taxed, NEW.exempt, NEW.not_taxed, NEW.tax,
               NEW.total, NEW.imported)
             IS DISTINCT FROM (
               OLD.kind, OLD.number, OLD.account_id, OLD.currency, OLD.price_mode, OLD.issue_date, OLD.due_date,
               OLD.related_document_id, OLD.receiver, OLD.aliquots, OLD.net_taxed, OLD.exempt, OLD.not_taxed, OLD.tax,
               OLD.total, OLD.imported) THEN
            RAISE EXCEPTION 'El documento % está emitido y no se modifica', OLD.id USING ERRCODE = 'check_violation';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER documents_frozen BEFORE UPDATE OR DELETE ON billing.documents
          FOR EACH ROW EXECUTE FUNCTION billing.documents_frozen();

        CREATE FUNCTION billing.document_lines_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
          doc bigint := CASE WHEN TG_OP = 'DELETE' THEN OLD.document_id ELSE NEW.document_id END;
        BEGIN
          IF EXISTS (SELECT 1 FROM billing.documents d WHERE d.id = doc AND d.issue_date IS NOT NULL AND NOT d.imported) THEN
            RAISE EXCEPTION 'Las líneas del documento % están congeladas', doc USING ERRCODE = 'check_violation';
          END IF;
          RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
        END $$;
        CREATE TRIGGER document_lines_frozen BEFORE INSERT OR UPDATE OR DELETE ON billing.document_lines
          FOR EACH ROW EXECUTE FUNCTION billing.document_lines_frozen();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS document_lines_frozen ON billing.document_lines;
        DROP FUNCTION IF EXISTS billing.document_lines_frozen();
        DROP TRIGGER IF EXISTS documents_frozen ON billing.documents;
        DROP FUNCTION IF EXISTS billing.documents_frozen();
        DROP TABLE IF EXISTS billing.counters;
        """;
}
