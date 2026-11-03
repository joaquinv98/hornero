#!/bin/bash
# Primer arranque del volumen: crea la base y los roles con las contraseñas de los secretos.
# La imagen de PostgreSQL lo corre como el usuario postgres, por el socket local.
psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname postgres \
  -v dueno_pw="$(< /run/secrets/pg_dueno)" \
  -v app_pw="$(< /run/secrets/pg_app)" \
  -f /etc/billing/roles.sql
