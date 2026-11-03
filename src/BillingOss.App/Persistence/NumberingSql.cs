namespace BillingOss.App.Persistence;

/// <summary>
/// El número comercial se asigna justo antes de confirmar la transacción que emite (sección 7.1 del modelo de
/// dominio), en un UPDATE posterior al que congela el documento: el disparador lo deja pasar una sola vez, de vacío a
/// un valor. Lo demás sigue congelado.
/// </summary>
internal static class NumberingSql
{
    public const string Up = """
        CREATE OR REPLACE FUNCTION billing.documents_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_OP = 'DELETE' THEN
            IF OLD.issue_date IS NOT NULL THEN
              RAISE EXCEPTION 'El documento % está emitido y no se borra', OLD.id USING ERRCODE = 'check_violation';
            END IF;
            RETURN OLD;
          END IF;
          IF OLD.issue_date IS NOT NULL AND (
               (NEW.kind, NEW.account_id, NEW.currency, NEW.price_mode, NEW.issue_date, NEW.due_date,
                NEW.related_document_id, NEW.receiver, NEW.aliquots, NEW.net_taxed, NEW.exempt, NEW.not_taxed, NEW.tax,
                NEW.total, NEW.imported)
               IS DISTINCT FROM (
                OLD.kind, OLD.account_id, OLD.currency, OLD.price_mode, OLD.issue_date, OLD.due_date,
                OLD.related_document_id, OLD.receiver, OLD.aliquots, OLD.net_taxed, OLD.exempt, OLD.not_taxed, OLD.tax,
                OLD.total, OLD.imported)
               OR (OLD.number IS NOT NULL AND NEW.number IS DISTINCT FROM OLD.number)) THEN
            RAISE EXCEPTION 'El documento % está emitido y no se modifica', OLD.id USING ERRCODE = 'check_violation';
          END IF;
          RETURN NEW;
        END $$;
        """;

    public const string Down = """
        CREATE OR REPLACE FUNCTION billing.documents_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
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
        """;
}
