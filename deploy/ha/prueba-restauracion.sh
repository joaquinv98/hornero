#!/bin/bash
# Prueba de restauración de billing-oss (docs/despliegue/backups.md). Corre como postgres en una VM descartable,
# sin salida a la red salvo hacia el repositorio, con PostgreSQL 18 y pgBackRest instalados y el mismo
# pgbackrest.conf de los nodos. Nunca en un nodo del clúster.
#
# Restaura el último respaldo más el WAL hasta 10 minutos antes, arranca PostgreSQL solo por socket, verifica
# las páginas y los índices, cuenta filas y, si todo da bien, escribe la fecha en $RESULTADO. El monitoreo alerta
# si ese archivo tiene más de 8 días.
set -euo pipefail

REPO=${REPO:-2}
TRABAJO=${TRABAJO:-/var/lib/billing-restore}
RESULTADO=${RESULTADO:-$TRABAJO/ultimo-ok}
BIN=${BIN:-/usr/lib/postgresql/18/bin}
datos=$TRABAJO/datos
socket=$TRABAJO/socket
puerto=55432

detener() { "$BIN/pg_ctl" -D "$datos" -m fast stop > /dev/null 2>&1 || true; }
trap detener EXIT

rm -rf "$datos" "$socket"
mkdir -p "$datos" "$socket"
chmod 700 "$datos"

objetivo=$(date -u -d '-10 min' '+%Y-%m-%d %H:%M:%S+00')
pgbackrest --stanza=billing --repo="$REPO" --pg1-path="$datos" \
  --type=time --target="$objetivo" --target-action=promote restore

# Lo que dejó Patroni en la configuración apunta al clúster: acá no hay TLS, ni archivo, ni réplicas, y solo
# entra el usuario postgres por el socket.
echo 'local all postgres peer' > "$TRABAJO/pg_hba.conf"
"$BIN/pg_ctl" -D "$datos" -w -t 3600 -o "-p $puerto -c listen_addresses='' -c unix_socket_directories=$socket \
  -c ssl=off -c archive_mode=off -c synchronous_standby_names='' -c shared_buffers=256MB \
  -c hba_file=$TRABAJO/pg_hba.conf" start

# Páginas, índices y tablas. --install-missing instala amcheck en esta copia, que se descarta.
"$BIN/pg_amcheck" -h "$socket" -p "$puerto" -U postgres --install-missing --heapallindexed billing_oss

# Conteos para comparar con los que publica el monitoreo del clúster.
"$BIN/psql" -h "$socket" -p "$puerto" -U postgres -d billing_oss -At -F ' ' -c "
  SELECT 'documentos', count(*) FROM billing.documents
  UNION ALL SELECT 'comprobantes', count(*) FROM fiscal.documents
  UNION ALL SELECT 'pagos', count(*) FROM payments.payments
  UNION ALL SELECT 'trabajos', count(*) FROM platform.jobs"

date -u '+%Y-%m-%dT%H:%M:%SZ' > "$RESULTADO"
echo "Restauración verificada al $objetivo."
