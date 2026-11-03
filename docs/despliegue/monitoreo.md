# Monitoreo y alertas

**Parte de:** [despliegue](README.md)

Facturación y aprovisionamiento fallan en silencio si nadie mira la cola (D-15). `/health/ready` en verde no
alcanza: dice que hay base, no que los trabajos avanzan.

## Fuentes

| Fuente | Qué da |
|---|---|
| `/health/live` y `/health/ready` de cada nodo, directo y por la entrada | Proceso vivo, y con base |
| API de Patroni (`https://NODO:8008/cluster`, `/health` y `/metrics`) | Líder, réplicas, línea de tiempo y atraso |
| etcd (`/health` y `/metrics` en el 2379) | Miembros y latencia del disco |
| `pg_stat_replication` y `pg_stat_archiver` | Réplicas sincrónicas y archivo de WAL |
| `pgbackrest --stanza=billing info --output=json` | Edad del último respaldo de cada repositorio |
| `/var/lib/billing-restore/ultimo-ok` en la VM de prueba | Fecha de la última restauración verificada |
| Las consultas de negocio de abajo | Si la facturación avanza |

## Rol de monitoreo

Solo lectura de las tablas que hacen falta, sin datos de clientes. Después de la primera migración, como
superusuario:

```sql
CREATE ROLE billing_monitor LOGIN PASSWORD :'monitor_pw';
GRANT pg_monitor TO billing_monitor;
GRANT CONNECT ON DATABASE billing_oss TO billing_monitor;
\connect billing_oss
GRANT USAGE ON SCHEMA platform, fiscal, payments TO billing_monitor;
-- Solo las columnas de las consultas: el contenido de los trabajos y de los avisos queda afuera.
GRANT SELECT (status, dedup_key, run_at, locked_until, updated_at) ON platform.jobs TO billing_monitor;
GRANT SELECT (status, updated_at) ON fiscal.emissions TO billing_monitor;
GRANT SELECT (status, received_at) ON payments.webhook_receipts TO billing_monitor;
```

## Consultas de negocio

```sql
-- 1. ¿Corrió la facturación de hoy? Esperado 'Done' pasada la 01:00 de Argentina. Done quiere decir que repartió
--    las cuentas en trabajos billing.account: una cuenta que falló aparece en la consulta 2 como trabajo muerto.
SELECT status FROM platform.jobs
WHERE dedup_key = 'billing.run:' || to_char((now() AT TIME ZONE 'America/Argentina/Buenos_Aires')::date, 'YYYY-MM-DD');

-- 2. Trabajos muertos en el último día. Esperado 0.
SELECT count(*) FROM platform.jobs WHERE status = 'Dead' AND updated_at > now() - interval '1 day';

-- 3. Trabajos atrasados: listos para correr hace más de 15 minutos. Esperado 0.
SELECT count(*) FROM platform.jobs WHERE status = 'Pending' AND run_at < now() - interval '15 minutes';

-- 4. Trabajos tomados con el lease vencido: un worker que se cayó. Esperado 0 la mayor parte del tiempo.
SELECT count(*) FROM platform.jobs WHERE status = 'Running' AND locked_until < now();

-- 5. Emisiones fiscales trabadas: inciertas o enviándose hace más de 10 minutos, o en conflicto. Esperado 0.
SELECT status, count(*) FROM fiscal.emissions
WHERE status = 'Conflict' OR (status IN ('Unknown', 'Sending') AND updated_at < now() - interval '10 minutes')
GROUP BY status;

-- 6. Avisos de pasarelas sin procesar hace más de 15 minutos. Esperado 0.
SELECT count(*) FROM payments.webhook_receipts WHERE status = 'Received' AND received_at < now() - interval '15 minutes';
```

## Después de una promoción

Ante una línea de tiempo nueva en Patroni, mirar enseguida las consultas 2 a 6. Lo normal es que una emisión que
estaba en vuelo quede incierta unos minutos y se resuelva sola consultando al fisco. Una en `Conflict`, un trabajo
muerto nuevo o un aviso trabado se revisan antes de seguir. El planificador ve la línea de tiempo nueva y encola
la conciliación solo (`platform.reconcile`): su resultado queda en `platform.audit_events` con la acción
`reconcile`, y lo que no cierra, en `platform.reconcile_findings` y `payments.unmatched`, con alerta
([backups.md](backups.md#después-de-restaurar)).

## Alertas

**Altas** (despiertan a alguien):

- Sin líder en Patroni, o sin réplica sincrónica (`synchronous_standby_names` vacío o `pg_stat_replication` sin
  ninguna en `quorum` o `sync`).
- Línea de tiempo nueva: hubo una conmutación.
- Emisión fiscal en `Conflict` (consulta 5).
- Avisos de pasarelas sin procesar (consulta 6), pagos sin casar (`payments.unmatched`) o hallazgos de la
  conciliación sin resolver (`platform.reconcile_findings`). Los dos últimos salen de `platform.metrics()`, con
  la plantilla de [docs/observabilidad](../observabilidad/README.md).
- Facturación del día sin terminar (consulta 1).
- Respaldo completo de más de 8 días o diferencial de más de 26 horas, o archivo de WAL con errores
  (`pg_stat_archiver.last_failed_time` reciente).
- Prueba de restauración vencida: `ultimo-ok` con más de 8 días.

**De aviso:**

- Atraso de una réplica de más de 16 MB o 30 segundos, o menos de 3 miembros en Patroni o en etcd.
- Conexiones por encima del 80 % de `max_connections`.
- Trabajos muertos nuevos o atrasados (consultas 2 a 4).
- Un nodo de aplicación con `/health/ready` en rojo.
- Desfase de reloj en algún nodo.
- Dos bases en el mismo host físico.

## Zabbix

- Agente 2 en cada nodo, con PSK, y la plantilla de PostgreSQL de agente 2 conectada como `billing_monitor`.
- Ítems HTTP para `/health/ready` de cada nodo, la API de Patroni y etcd.
- Las consultas de negocio como ítems de base de datos.
- `vfs.file.time[/var/lib/billing-restore/ultimo-ok]` en la VM de prueba de restauración.
- Un `UserParameter` con `pgbackrest info --output=json` para la edad de los respaldos.
- Un chequeo externo de la entrada pública (por ejemplo Gatus), cuando sea pública.
