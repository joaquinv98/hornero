-- Rol de monitoreo de billing-oss: ve conteos por platform.metrics() y nunca una fila de la base.
-- Lo corre el dueño de la base después de las migraciones. La contraseña se carga aparte, con \password
-- billing_metrics desde psql, y se guarda en la bóveda. Guía: docs/observabilidad/README.md.
-- Se puede volver a correr: la segunda vez el rol ya existe. Si la base no se llama billing_oss, cambiar el GRANT.
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'billing_metrics') THEN
    CREATE ROLE billing_metrics LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT CONNECTION LIMIT 5;
  END IF;
END $$;
GRANT CONNECT ON DATABASE billing_oss TO billing_metrics;
GRANT USAGE ON SCHEMA platform TO billing_metrics;
GRANT EXECUTE ON FUNCTION platform.metrics(timestamptz) TO billing_metrics;
ALTER ROLE billing_metrics SET statement_timeout = '5s';
