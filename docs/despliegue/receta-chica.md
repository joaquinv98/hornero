# Receta chica con Docker Compose

**Parte de:** [despliegue](README.md)

Todo en un host: el núcleo (web y worker), PostgreSQL 18 con TLS, los módulos de ARCA, Mercado Pago, Stripe, PayPal y CryptoCloud
y Gotenberg.
Sirve para proveedores que aceptan un corte mientras se reinicia o se restaura, para laboratorios y para el
staging chico. Si se cae el host, se cae todo: para eso está la [HA en VMs](ha-vms.md).

| Servicio | Imagen | Qué hace |
|---|---|---|
| `web` | `billing-oss` | API, avisos de pasarelas y página de retorno. Publica el 8080 |
| `worker` | `billing-oss` | Cola de trabajos y planificador |
| `migrate` | `billing-oss` | Aplica las migraciones y termina. Solo corre cuando se lo llama |
| `arca` | `billing-oss-arca` | Módulo fiscal, con su red y su salida a internet aparte |
| `mercadopago` | `billing-oss-mercadopago` | Módulo de cobro, con su red y su salida aparte |
| `stripe` | `billing-oss-stripe` | Módulo de cobro, con su red y su salida aparte |
| `paypal` | `billing-oss-paypal` | Módulo de cobro, con su red y su salida aparte |
| `cryptocloud` | `billing-oss-cryptocloud` | Módulo de cobro, con su red y su salida aparte |
| `gotenberg` | `gotenberg/gotenberg:8` | HTML a PDF, en una red interna sin salida |
| `postgres` | `postgres:18` | La base, solo por TLS |

## Requisitos

- Debian 13 o similar, con 2 vCPU, 4 GiB de RAM y 20 GiB de disco.
- Docker Engine con el plugin de Compose v2.
- La hora sincronizada (chrony): ARCA rechaza pedidos con la hora corrida.
- Salida por 443 hacia ARCA y la pasarela, y un proxy con TLS delante del 8080.

## 1. Construir las imágenes

Desde una etiqueta del repo, en el mismo host o en otro con acceso a un registro:

```sh
git clone https://git.example.com/billing-oss.git && cd billing-oss
git checkout v0.1.0
docker build -f deploy/Dockerfile --target nucleo      -t billing-oss:0.1.0 .
docker build -f deploy/Dockerfile --target arca        -t billing-oss-arca:0.1.0 .
docker build -f deploy/Dockerfile --target mercadopago -t billing-oss-mercadopago:0.1.0 .
docker build -f deploy/Dockerfile --target stripe      -t billing-oss-stripe:0.1.0 .
docker build -f deploy/Dockerfile --target paypal      -t billing-oss-paypal:0.1.0 .
docker build -f deploy/Dockerfile --target cryptocloud -t billing-oss-cryptocloud:0.1.0 .
```

Anotar el SHA del commit y el digest de cada imagen.

## 2. Preparar los secretos

```sh
cd deploy/compose
cp .env.example .env        # completar BILLING_VERSION y BILLING_PUBLIC_URL
sudo ./preparar.sh
```

`preparar.sh` crea `secrets/` con permisos solo para quien lee cada archivo, y nunca pisa uno que ya existe:

| Archivo | Qué es | Lo lee |
|---|---|---|
| `pg_superusuario`, `pg_dueno`, `pg_app` | Contraseñas del superusuario y de los roles `billing_owner` y `billing_app` | PostgreSQL (999) |
| `db_app`, `db_dueno` | Cadenas de conexión de la aplicación y de `migrate` | Núcleo (1654) |
| `llaves` | Llave de los secretos cifrados por campo (D-09) | Núcleo (1654) |
| `arca_firma`, `mercadopago_firma`, `stripe_firma`, `paypal_firma`, `cryptocloud_firma` | Secreto de firma de cada instalación de módulo | Cada módulo y `migrate` (1654) |
| `tls/ca.crt`, `tls/ca.key` | CA interna | Todos el certificado. La clave, nadie |
| `tls/postgres.*` | Certificado de PostgreSQL, para el nombre `postgres` | PostgreSQL (999) |
| `tls/arca.*`, `tls/mercadopago.*`, `tls/stripe.*`, `tls/paypal.*`, `tls/cryptocloud.*` | Certificado de cada módulo | Cada módulo (1654). El núcleo fija su huella al correr `migrate` |

Al terminar imprime la huella SHA-256 de cada módulo, que `migrate` fija sola en el núcleo. Después:

1. Guardar todo `secrets/` en la bóveda. **`llaves` es lo más importante:** sin ella no se pueden leer las
   credenciales de los módulos ni el certificado de ARCA (D-09). Conviene además una copia impresa.
2. Llevar `tls/ca.key` a la bóveda y borrarla del host. Solo hace falta para emitir certificados nuevos.

## 3. Base y migraciones

```sh
docker compose up -d postgres
docker compose run --rm migrate
```

La primera vez que arranca el volumen, PostgreSQL crea la base `billing_oss` y los dos roles con
[`deploy/sql/roles.sql`](../../deploy/sql/roles.sql). `billing_owner` es dueño del esquema y lo usa solo `migrate`.
`billing_app` es el de la aplicación y no puede crear ni borrar objetos, así que tampoco puede sacar los triggers
que congelan los documentos emitidos. `migrate` levanta además los módulos y los da de alta en el núcleo (sección 6).

## 4. Arrancar

```sh
docker compose up -d
docker compose ps
curl -fsS http://127.0.0.1:8080/health/ready
```

Todos los contenedores del núcleo y de los módulos corren sin privilegios, sin capacidades y con el sistema de
archivos de solo lectura.

## 5. El proxy delante

El rol web escucha HTTP en `BILLING_HTTP` (por defecto `127.0.0.1:8080`). El TLS público lo pone un proxy:

- Si el proxy está en el mismo host, dejar `127.0.0.1:8080`.
- Si está en otro, poner la IP del host y filtrar en la cadena `DOCKER-USER`, porque Docker publica los puertos
  por delante del firewall del host:

  ```sh
  iptables -I DOCKER-USER -i eth0 -p tcp -m conntrack --ctorigdstport 8080 ! -s IP_DEL_PROXY -j DROP
  ```

El proxy chequea `/health/ready`, no reintenta un POST que ya llegó y manda `X-Forwarded-For` y
`X-Forwarded-Proto`. La aplicación les cree solo al proxy de `BILLING_PROXY` en `.env` (IP o red CIDR). Sin eso ve
cada pedido como HTTP: la interfaz da 500 por las cookies seguras y los topes por IP cuentan todo como la IP del
proxy. Prueba: con TLS terminado en el proxy, la interfaz responde 200 y una API key con lista de IP acepta al
cliente. Hacia afuera tienen que quedar `/webhooks/{instancia}` (avisos de las pasarelas) y
`/pay/return/{referencia}` (vuelta del cliente desde la pasarela), además de la API y el portal.

## 6. Módulos

`docker compose run --rm migrate` levanta antes los módulos y da de alta cada uno en el núcleo, con su URL, su
secreto de firma y la huella de su certificado, que lee de los mismos archivos de `secrets/` (sección 4.2 del
[diseño de cobros](../cobros/README.md)). Nadie copia huellas ni secretos de firma:

| Módulo | URL | Secreto de firma | Certificado |
|---|---|---|---|
| `arca` | `https://arca:8443/` | `secrets/arca_firma` | `secrets/tls/arca.crt` |
| `mercadopago` | `https://mercadopago:8443/` | `secrets/mercadopago_firma` | `secrets/tls/mercadopago.crt` |
| `stripe` | `https://stripe:8443/` | `secrets/stripe_firma` | `secrets/tls/stripe.crt` |
| `paypal` | `https://paypal:8443/` | `secrets/paypal_firma` | `secrets/tls/paypal.crt` |
| `cryptocloud` | `https://cryptocloud:8443/` | `secrets/cryptocloud_firma` | `secrets/tls/cryptocloud.crt` |

- Si un módulo no responde, `migrate` lo intenta tres veces, lo deja en el log y sigue. Se da de alta en la próxima
  corrida.
- Cada corrida vuelve a cargar el secreto de firma y la huella, así rotarlos es cambiar el archivo y correr
  `migrate`. Una versión nueva del módulo que cambia sus esquemas o pide más datos personales queda pendiente de
  aprobación en Cobros del admin, y mientras tanto vale la anterior.
- Las instancias (las credenciales de cada cuenta de pasarela) se cargan en Cobros, con el asistente. La guía de
  cobros está en [docs/cobros](../cobros/README.md).
- Todos los módulos corren aunque no tengan instancias: sin credenciales no hacen nada.
- ARCA necesita salida a sus hosts por 443 (lista en el [README del módulo](../../src/modules/BillingOss.Module.Arca/README.md))
  y la hora bien sincronizada. Conviene un certificado de ARCA propio de esta instalación.
- Las pasarelas salen solo por 443: `api.mercadopago.com`, `api.stripe.com`, `api-m.paypal.com` (o
  `api-m.sandbox.paypal.com` en las instancias de prueba) y `api.trybit.com`. Compose separa redes pero no filtra
  destinos: el límite por destino va en el firewall del host o en un proxy de salida.
- El registro de idempotencia de cada módulo vive en su volumen (`arca-datos`, `mercadopago-datos`, `stripe-datos`,
  `paypal-datos` y `cryptocloud-datos`).
  No se borra: es lo que evita repetir un efecto si el núcleo reintenta.

## 7. Copias y restauración

[`respaldo.sh`](../../deploy/compose/respaldo.sh) hace un `pg_dump` en formato custom, lo verifica con
`pg_restore --list` y borra los de más de 14 días. Una vez por día, por ejemplo con un timer:

```ini
# /etc/systemd/system/billing-respaldo.service
[Service]
Type=oneshot
ExecStart=/opt/billing-oss/deploy/compose/respaldo.sh /var/backups/billing-oss

# /etc/systemd/system/billing-respaldo.timer
[Timer]
OnCalendar=*-*-* 03:30:00 America/Argentina/Buenos_Aires
Persistent=true
[Install]
WantedBy=timers.target
```

El directorio de destino se copia fuera del host con un backup cifrado: tiene datos personales. La llave de
D-09 nunca viaja en el mismo backup que la base.

**Lo que se puede perder:** hasta un día. Los comprobantes que el fisco autorizó en ese día se recuperan
consultándolo, y los pagos, desde la pasarela. Si eso no alcanza, hace falta archivo continuo de WAL con
pgBackRest, que es lo que trae la [HA en VMs](ha-vms.md).

**Prueba de restauración, una vez por mes,** en un contenedor sin red:

```sh
docker run -d --rm --name prueba-restauracion --network none \
  -e POSTGRES_HOST_AUTH_METHOD=trust postgres:18
# Por TCP, así no responde el servidor temporal que la imagen levanta mientras inicializa.
until docker exec prueba-restauracion pg_isready -h 127.0.0.1 -U postgres; do sleep 1; done
docker exec prueba-restauracion createdb -U postgres billing_oss
docker exec -i prueba-restauracion pg_restore -U postgres -d billing_oss --no-owner --no-privileges \
  < /var/backups/billing-oss/billing_oss-ULTIMO.dump
docker exec prueba-restauracion psql -U postgres -d billing_oss -c "SELECT count(*) FROM billing.documents"
docker rm -f prueba-restauracion
```

**Restaurar de verdad:**

```sh
docker compose stop web worker
docker compose exec -T -u postgres postgres dropdb billing_oss
# --create vuelve a crear la base con su dueño y sus permisos, tal como estaban.
docker compose exec -T -u postgres postgres pg_restore -d postgres --create \
  < /var/backups/billing-oss/billing_oss-ELEGIDO.dump
docker compose up -d web worker
```

Después de restaurar con pérdida, antes de emitir y cobrar, hay que conciliar: ver
[después de restaurar](backups.md#después-de-restaurar).

## 8. Actualizar

```sh
# imágenes nuevas, como en el paso 1, y BILLING_VERSION nueva en .env
docker compose run --rm migrate
docker compose up -d
```

En un solo host hay un corte de segundos mientras se recrean los contenedores. Las migraciones se escriben en dos
pasos (ver [actualizaciones.md](actualizaciones.md#migraciones-en-dos-pasos)), así que la versión anterior funciona
con el esquema nuevo: para volver atrás alcanza con la etiqueta anterior en `BILLING_VERSION` y otro
`docker compose up -d`.

## 9. Pasar a alta disponibilidad

Con el clúster de la [HA en VMs](ha-vms.md) armado y la base y los roles creados en él:

1. `docker compose stop web worker` en el host viejo.
2. `respaldo.sh` y copiar el archivo a la primaria del clúster.
3. En la primaria: `pg_restore -d billing_oss --role=billing_owner ARCHIVO`.
4. Copiar `secrets/` a los nodos nuevos y rehacer `db_app` y `db_dueno` con los tres hosts (borrarlos y correr
   `DB_HOSTS=... ./preparar.sh`). `llaves`, las firmas y los certificados de los módulos se conservan.
5. Arrancar los nodos nuevos con `COMPOSE_FILE=compose.yaml`.
