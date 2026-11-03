#!/bin/bash
# CA interna y certificados de billing-oss, con OpenSSL.
#
#   certificados.sh DIRECTORIO NOMBRE [IP...]
#
# Si DIRECTORIO no tiene ca.crt y ca.key, crea la CA (10 años). Después emite NOMBRE.crt y NOMBRE.key (825 días),
# con NOMBRE, 127.0.0.1 y las IP como nombres alternativos, para servidor y cliente a la vez: el mismo certificado
# sirve para PostgreSQL, la API de Patroni y etcd en un nodo, o para un módulo.
#
# La clave de la CA (ca.key) va a la bóveda y se borra del host apenas se terminan de emitir los certificados.
# Nunca sobreescribe un archivo que ya existe.
set -euo pipefail

if [ $# -lt 2 ]; then
  echo "Uso: $0 DIRECTORIO NOMBRE [IP...]" >&2
  exit 2
fi

dir=$1
nombre=$2
shift 2
mkdir -p "$dir"
umask 077

if [ ! -e "$dir/ca.crt" ]; then
  openssl req -x509 -new -nodes -newkey ec -pkeyopt ec_paramgen_curve:P-256 -sha256 -days 3650 \
    -subj "/CN=billing-oss CA interna" -keyout "$dir/ca.key" -out "$dir/ca.crt"
  chmod 644 "$dir/ca.crt"
fi

# Huella SHA-256 del certificado: es la que fija el núcleo para un módulo (TlsFingerprint de la instalación).
huella() { echo "$nombre: $(openssl x509 -in "$dir/$nombre.crt" -outform DER | sha256sum | cut -d' ' -f1)"; }

if [ -e "$dir/$nombre.crt" ]; then
  echo "$dir/$nombre.crt ya existe: no se toca." >&2
  huella
  exit 0
fi

if [ ! -e "$dir/ca.key" ]; then
  echo "Falta $dir/ca.key para firmar. Traerla de la bóveda." >&2
  exit 1
fi

san="DNS:$nombre,IP:127.0.0.1"
for ip in "$@"; do
  san="$san,IP:$ip"
done

printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth,clientAuth\nkeyUsage=digitalSignature\n' "$san" > "$dir/$nombre.ext"
openssl req -new -nodes -newkey ec -pkeyopt ec_paramgen_curve:P-256 -subj "/CN=$nombre" \
  -keyout "$dir/$nombre.key" -out "$dir/$nombre.csr"
openssl x509 -req -sha256 -days 825 -in "$dir/$nombre.csr" -CA "$dir/ca.crt" -CAkey "$dir/ca.key" \
  -set_serial "0x$(openssl rand -hex 16)" -extfile "$dir/$nombre.ext" -out "$dir/$nombre.crt"
rm -f "$dir/$nombre.csr" "$dir/$nombre.ext"
chmod 644 "$dir/$nombre.crt"
huella
