#!/bin/bash
# Copia lógica de la receta chica: pg_dump en formato custom, verificada con pg_restore --list antes de darla por
# buena. Se corre una vez por día desde un timer o cron del host, y el directorio de destino se copia fuera del
# host con el backup cifrado de la casa. Sin PITR: se puede perder hasta un día (docs/despliegue/receta-chica.md).
#
#   respaldo.sh [DESTINO]      DIAS=14 por defecto
set -euo pipefail

cd "$(dirname "$0")"
destino=${1:-/var/backups/billing-oss}
dias=${DIAS:-14}
umask 077
mkdir -p "$destino"

archivo="$destino/billing_oss-$(date -u +%Y%m%dT%H%M%SZ).dump"
docker compose exec -T -u postgres postgres pg_dump -Fc -d billing_oss > "$archivo.parcial"
docker compose exec -T -u postgres postgres pg_restore --list < "$archivo.parcial" > /dev/null
mv "$archivo.parcial" "$archivo"

find "$destino" -name 'billing_oss-*.dump' -mtime +"$dias" -delete
echo "$archivo"
