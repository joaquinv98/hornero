#!/bin/bash
# Arma ./secrets para compose.yaml: contraseñas, cadenas de conexión, llave de secretos (D-09), certificados y
# secretos de firma de los módulos. Corre como root, porque cada archivo queda solo para el usuario que lo lee
# (1654 en el núcleo y los módulos, 999 en PostgreSQL). Nunca pisa un archivo que ya existe.
#
#   Receta chica:          ./preparar.sh
#   Alta disponibilidad:   DB_HOSTS=192.0.2.11,192.0.2.12,192.0.2.13 ./preparar.sh
#
# En alta disponibilidad, antes de correrlo, secrets/tls tiene que tener ca.crt y ca.key de la CA del clúster
# (deploy/certificados.sh). Se corre en un solo nodo y el directorio secrets se copia igual a los demás.
# Guías: docs/despliegue/receta-chica.md y docs/despliegue/ha-vms.md.
set -euo pipefail

cd "$(dirname "$0")"
if [ "$(id -u)" != 0 ]; then
  echo "Correr como root: los archivos quedan con dueño 1654 o 999." >&2
  exit 1
fi

DB_HOSTS=${DB_HOSTS:-postgres}
APP=1654
POSTGRES=999
umask 077
mkdir -p secrets/tls

# secreto ARCHIVO DUEÑO VALOR: lo escribe sin salto de línea final, solo si no existe.
secreto() {
  if [ ! -e "secrets/$1" ]; then
    printf '%s' "$3" > "secrets/$1"
  fi
  chown "$2:$2" "secrets/$1"
  chmod 400 "secrets/$1"
}

aleatorio() { openssl rand -hex 24; }

# Contraseñas de los roles. En la receta chica las lee el contenedor de PostgreSQL al crear la base.
dueno_pg=root
if [ "$DB_HOSTS" = postgres ]; then
  dueno_pg=$POSTGRES
  secreto pg_superusuario "$dueno_pg" "$(aleatorio)"
fi
secreto pg_dueno "$dueno_pg" "$(aleatorio)"
secreto pg_app "$dueno_pg" "$(aleatorio)"

# Cadenas de conexión. Con varios hosts, Npgsql busca la primaria (Target Session Attributes=read-write).
# Command Timeout de 90 s en HA: el COMMIT también corre bajo ese tiempo, y si se corta mientras espera a la réplica
# sincrónica queda confirmado solo en la primaria. Tiene que superar lo que tarda Patroni en degradar una primaria
# aislada (ttl 30 más loop_wait 10), con margen.
extra="SSL Mode=VerifyFull;Root Certificate=/run/billing/pg-ca.crt;Maximum Pool Size=20"
if [ "$DB_HOSTS" != postgres ]; then
  extra="$extra;Target Session Attributes=read-write;Host Recheck Seconds=5;Timeout=5;Keepalive=10;Command Timeout=90"
fi
secreto db_app $APP "Host=$DB_HOSTS;Database=billing_oss;Username=billing_app;Password=$(< secrets/pg_app);$extra"
secreto db_dueno $APP "Host=$DB_HOSTS;Database=billing_oss;Username=billing_owner;Password=$(< secrets/pg_dueno);$extra"

# La del actualizador (D-20), que corre en el host como root: el rol dueño, con la CA por su ruta en el host. En la
# receta chica llega a PostgreSQL por el puerto que compose.postgres.yaml publica en 127.0.0.1.
actualizador="SSL Mode=VerifyFull;Root Certificate=$PWD/secrets/tls/ca.crt;Maximum Pool Size=5"
if [ "$DB_HOSTS" = postgres ]; then
  actualizador="Host=127.0.0.1;Port=${BILLING_PG_PUERTO:-5432};$actualizador"
else
  actualizador="Host=$DB_HOSTS;$actualizador;Target Session Attributes=read-write;Host Recheck Seconds=5;Timeout=5;Keepalive=10;Command Timeout=90"
fi
secreto db_actualizador 0 "$actualizador;Database=billing_oss;Username=billing_owner;Password=$(< secrets/pg_dueno)"

# Llave de los secretos cifrados por campo (D-09): "versión:base64" de 32 bytes.
secreto llaves $APP "1:$(openssl rand -base64 32)"

# Secretos de firma de cada instalación de módulo. El núcleo guarda el mismo valor, cifrado.
secreto arca_firma $APP "$(openssl rand -hex 32)"
secreto mercadopago_firma $APP "$(openssl rand -hex 32)"
secreto stripe_firma $APP "$(openssl rand -hex 32)"
secreto paypal_firma $APP "$(openssl rand -hex 32)"
secreto cryptocloud_firma $APP "$(openssl rand -hex 32)"

# Certificados. En la receta chica también crea la CA y el certificado de PostgreSQL.
if [ "$DB_HOSTS" = postgres ]; then
  ../certificados.sh secrets/tls postgres
  chown $POSTGRES:$POSTGRES secrets/tls/postgres.key
  chmod 600 secrets/tls/postgres.key
elif [ ! -e secrets/tls/ca.crt ]; then
  echo "Falta secrets/tls/ca.crt: copiar la CA del clúster antes de seguir." >&2
  exit 1
fi
for modulo in arca mercadopago stripe paypal cryptocloud; do
  ../certificados.sh secrets/tls "$modulo"
  chown $APP:$APP "secrets/tls/$modulo.key"
  chmod 400 "secrets/tls/$modulo.key"
done

echo
echo "Listo. Pendiente:"
echo "  1. Guardar secrets/ en la bóveda (sobre todo llaves: sin ella no se leen los secretos de los módulos)."
if [ -e secrets/tls/ca.key ]; then
  echo "  2. Llevar secrets/tls/ca.key a la bóveda y borrarla de este host."
fi
echo "  Los módulos se dan de alta solos al correr migrate, con su secreto de firma y la huella de su certificado."
