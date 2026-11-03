-- Base y roles de billing-oss (D-02). Corre una vez, como superusuario, con psql:
--   psql -v ON_ERROR_STOP=1 -v dueno_pw="..." -v app_pw="..." -f roles.sql
--
-- billing_owner es dueño de la base y del esquema: lo usa solo "migrate". billing_app es el de la aplicación,
-- sin permiso para crear ni borrar objetos, así no puede tocar los triggers que congelan los documentos emitidos.

CREATE ROLE billing_owner LOGIN PASSWORD :'dueno_pw';
CREATE ROLE billing_app LOGIN PASSWORD :'app_pw';
CREATE DATABASE billing_oss OWNER billing_owner;

\connect billing_oss
REVOKE ALL ON DATABASE billing_oss FROM PUBLIC;
GRANT CONNECT ON DATABASE billing_oss TO billing_app;

-- Ningún timeout del rol cancela un COMMIT que espera a la réplica sincrónica: cancelado, queda confirmado solo en
-- la primaria y se pierde en la conmutación. Lo mismo vale para PgBouncer (query_timeout = 0) y para la cadena de la
-- aplicación (Command Timeout de 90 s o más, nunca el de 30 por defecto).
ALTER ROLE billing_app SET statement_timeout = 0;
ALTER ROLE billing_app SET synchronous_commit = on;
ALTER ROLE billing_owner SET statement_timeout = 0;
ALTER ROLE billing_owner SET synchronous_commit = on;

-- La aplicación mira en pg_stat_replication si hay réplica sincrónica (/health/cluster y el freno de las escrituras
-- con Database:Durability=sync). Sin pg_read_all_stats esa vista le muestra sync_state en NULL y vería "sin réplica".
GRANT pg_read_all_stats TO billing_app;

-- El actualizador (D-20) usa el rol dueño: la marca de recuperación con PITR, que por defecto solo hace un
-- superusuario, y la réplica sincrónica en los chequeos previos, que se ve con pg_monitor.
GRANT EXECUTE ON FUNCTION pg_create_restore_point(text) TO billing_owner;
GRANT pg_monitor TO billing_owner;

-- Lo que creen las migraciones (esquemas, tablas y secuencias) queda disponible para la aplicación.
ALTER DEFAULT PRIVILEGES FOR ROLE billing_owner GRANT USAGE ON SCHEMAS TO billing_app;
ALTER DEFAULT PRIVILEGES FOR ROLE billing_owner GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO billing_app;
ALTER DEFAULT PRIVILEGES FOR ROLE billing_owner GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO billing_app;
