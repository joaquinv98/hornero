# Alta disponibilidad en VMs

**Parte de:** [despliegue](README.md) · **Recetas:** [`deploy/ha/`](../../deploy/ha) y [`deploy/compose/`](../../deploy/compose)

La receta de referencia es la **HA compacta**: 3 VMs en 3 hosts físicos distintos, y en cada una la base
(PostgreSQL 18 con Patroni y un miembro de etcd) y la aplicación (web, worker, módulos y Gotenberg con
`compose.yaml`). La **HA estándar** es lo mismo con las bases y la aplicación en VMs separadas: se suman nodos de
aplicación sin tocar la base.

| Pieza | En cada nodo | Para qué |
|---|---|---|
| PostgreSQL 18 | Una base: primaria o réplica | Los datos |
| Patroni 4.1 | Uno | Elige la primaria, promueve una réplica si cae y vuelve a sumar a la vieja |
| etcd | Un miembro, 3 en total | Dónde Patroni guarda quién es la primaria. Con 2 de 3 alcanza |
| pgBackRest | Uno, que solo archiva y respalda en la primaria | Archivo continuo de WAL y respaldos para PITR |
| Watchdog | `i6300esb` en Proxmox o `softdog` | Reinicia la VM si Patroni se cuelga siendo primaria |
| Compose de aplicación | web, worker, arca, mercadopago, stripe, paypal, cryptocloud y gotenberg | La aplicación, igual en los tres nodos |
| HAProxy y keepalived | En los nodos de entrada | Entrada HTTP con una IP virtual. Se reemplaza por el proxy que ya haya |

**Replicación sincrónica estricta por quórum:** cada commit espera a que una de las dos réplicas lo tenga.
Si cae la primaria, Patroni promueve una réplica que tiene todo lo confirmado: no se pierde nada. Si caen las dos
réplicas, las escrituras esperan hasta que vuelva una, en lugar de confirmarse en un solo lado. Por eso hacen falta
tres nodos con datos: con uno caído las escrituras siguen.

## Requisitos

- **3 hosts físicos distintos**, una VM de base por host. Nunca dos bases en el mismo host, ni la base en un
  almacenamiento compartido único.
- **Cada VM:** Debian 13, 3 vCPU, 6 GiB de RAM fija sin ballooning, 60 GiB en disco local con la reserva hecha
  (en ZFS, `refreservation`). En la HA estándar, las de base con 2 vCPU y 4 GiB y las de aplicación con 2 vCPU y
  3 GiB.
- **Watchdog:** en Proxmox, `qm set ID --watchdog model=i6300esb,action=reset`. Sin hipervisor que lo dé,
  `softdog`.
- **Red:** IP fijas en una misma VLAN, así la replicación y etcd no cruzan un firewall. chrony en todos.
- **Puertos entre los nodos:** 5432 (PostgreSQL), 8008 (API de Patroni), 2379 y 2380 (etcd) y 8080 (web, desde la
  entrada). Desde el monitoreo, los mismos más el del agente.
- **Fuera de los nodos:** un host para el repositorio 1 de pgBackRest por SFTP y, para producción, un
  almacenamiento S3 fuera del sitio para el repositorio 2.

En los ejemplos los nodos son `billnode01` a `billnode03`, con las IP 192.0.2.11 a 192.0.2.13, y la IP virtual de
la entrada es 192.0.2.10.

## 1. Paquetes

PostgreSQL, Patroni y pgBackRest salen del repositorio de PGDG. Antes de instalar, que el paquete no cree el
clúster por defecto:

```sh
mkdir -p /etc/postgresql-common
echo 'create_main_cluster = false' > /etc/postgresql-common/createcluster.conf
apt install postgresql-18 patroni pgbackrest etcd-server etcd-client curl
systemctl disable --now patroni etcd
pgbackrest version    # 2.60 o posterior
```

Más Docker Engine con el plugin de Compose para la aplicación, y `haproxy` y `keepalived` en los nodos de entrada.

## 2. Certificados

Una CA interna firma un certificado por nodo, que sirve a la vez para PostgreSQL, la API de Patroni y etcd. En una
máquina de administración:

```sh
deploy/certificados.sh ~/billing-ca billnode01 192.0.2.11
deploy/certificados.sh ~/billing-ca billnode02 192.0.2.12
deploy/certificados.sh ~/billing-ca billnode03 192.0.2.13
```

Si se va a usar una IP virtual para la base (vip-manager), sumarla al final de cada línea. En cada nodo:

```sh
groupadd --system billing-tls
install -d -m 0755 /etc/billing-ha/tls
install -m 0644 ca.crt /etc/billing-ha/tls/ca.crt
install -m 0644 billnode01.crt /etc/billing-ha/tls/nodo.crt
install -m 0640 -g billing-tls billnode01.key /etc/billing-ha/tls/nodo.key
usermod -aG billing-tls postgres && usermod -aG billing-tls etcd
```

PostgreSQL acepta la clave con dueño root y permisos 0640. `ca.key` va a la bóveda.

## 3. etcd

En cada nodo, [`etcd.env.example`](../../deploy/ha/etcd.env.example) va a `/etc/default/etcd`, con el nombre y la
IP del nodo. Arrancar los tres casi a la vez (el primero espera a los otros):

```sh
systemctl enable --now etcd
etcdctl --endpoints=https://192.0.2.11:2379 --cacert=/etc/billing-ha/tls/ca.crt \
  --cert=/etc/billing-ha/tls/nodo.crt --key=/etc/billing-ha/tls/nodo.key endpoint health --cluster
etcdctl ... check perf
```

`check perf` mide si el disco aguanta. El directorio de etcd va siempre en un disco virtual propio (un zvol aparte,
con `sync` estándar), nunca en el mismo disco que PostgreSQL: la primaria escribe en sincrónico y un fsync lento le
hace perder la llave de líder a etcd, con una conmutación falsa y su emisión incierta. `ETCD_HEARTBEAT_INTERVAL` y
`ETCD_ELECTION_TIMEOUT` ya vienen subidos en el ejemplo, y la métrica `etcd_disk_wal_fsync_duration_seconds` va al
monitoreo con alerta.

## 4. Watchdog

```sh
echo i6300esb > /etc/modules-load.d/watchdog.conf      # o softdog
modprobe i6300esb
echo 'KERNEL=="watchdog", OWNER="postgres", GROUP="postgres", MODE="0600"' > /etc/udev/rules.d/60-watchdog.rules
udevadm trigger --name-match=watchdog
```

Patroni está configurado con `watchdog mode: required`: sin watchdog, el nodo no puede ser primaria.

## 5. Patroni

En cada nodo:

1. [`patroni.yml.example`](../../deploy/ha/patroni.yml.example) a `/etc/patroni/billing.yml`, con lo marcado
   `CAMBIAR`. En el nodo con el disco más lento, `failover_priority` más bajo.
2. [`secretos.env.example`](../../deploy/ha/secretos.env.example) a `/etc/billing-ha/secretos.env`, dueño root y
   permisos 0600, con las contraseñas desde la bóveda. Iguales en los tres nodos.
3. [`systemd/patroni-billing.service`](../../deploy/ha/systemd/patroni-billing.service) a `/etc/systemd/system/`.

Arrancar primero en `billnode01`, que crea la base, y después en los otros dos, que se clonan:

```sh
systemctl daemon-reload && systemctl enable --now patroni-billing
patronictl -c /etc/patroni/billing.yml list
```

Tiene que mostrar un `Leader` y dos `Quorum Standby` en `streaming`, todos en la misma línea de tiempo. En la
primaria, `SHOW synchronous_standby_names` da `ANY 1 (...)` con las dos réplicas.

## 6. pgBackRest

En cada nodo, [`pgbackrest.conf.example`](../../deploy/ha/pgbackrest.conf.example) a
`/etc/pgbackrest/pgbackrest.conf`, y una llave SSH del usuario `postgres` autorizada en el host del repositorio 1.
En la primaria:

```sh
# Como postgres y con las claves de los repositorios, igual que lo corren Patroni y los timers.
pgb() { systemd-run --quiet --pipe --wait --uid=postgres -p EnvironmentFile=/etc/billing-ha/secretos.env pgbackrest "$@"; }
pgb --stanza=billing stanza-create
pgb --stanza=billing check
```

Los timers van en los tres nodos. Solo corren donde está la primaria:

```sh
cp deploy/ha/systemd/pgbackrest-* /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now pgbackrest-completo.timer pgbackrest-diferencial.timer
systemctl start pgbackrest-respaldo@full.service     # el primero, a mano
```

Detalle y prueba de restauración en [backups.md](backups.md).

## 7. Base y roles

Primero se preparan los secretos de la aplicación (paso 8.2), que generan las contraseñas de los roles. Con
ellas, en la primaria, como root y con el repo a mano:

```sh
sudo -u postgres psql -v ON_ERROR_STOP=1 \
  -v dueno_pw="$(cat deploy/compose/secrets/pg_dueno)" -v app_pw="$(cat deploy/compose/secrets/pg_app)" \
  < deploy/sql/roles.sql
```

Las contraseñas viajan solo hasta `psql`, y la base guarda el verificador SCRAM. El script deja a los dos roles con
`statement_timeout = 0` y `synchronous_commit = on`: un COMMIT cancelado mientras espera a la réplica queda
confirmado solo en la primaria y se pierde en la conmutación, así que nada lo cancela, ni el rol, ni PgBouncer
(`query_timeout = 0`), ni la cadena de la aplicación (`Command Timeout` de 90 s, que la aplicación sube sola si
viene menor). También le da `pg_read_all_stats` al rol de la aplicación: sin eso `pg_stat_replication` le muestra
`sync_state` en NULL y `/health/cluster` y el freno de las escrituras verían un clúster sin réplica.

## 8. Nodos de aplicación

1. **Imágenes.** Construir una vez (ver [receta-chica.md](receta-chica.md#1-construir-las-imágenes)) y llevarlas a
   los tres nodos con un registro privado o con `docker save` y `docker load`. Las tres con el mismo digest.
2. **Secretos, en un solo nodo.** Copiar `ca.crt` y `ca.key` de la CA interna a `deploy/compose/secrets/tls/` y:

   ```sh
   cd deploy/compose
   sudo DB_HOSTS=192.0.2.11,192.0.2.12,192.0.2.13 ./preparar.sh
   ```

   Después borrar `secrets/tls/ca.key` y copiar `secrets/` a los otros dos nodos conservando dueños y permisos
   (`tar` o `rsync -a` como root). La llave de D-09, los secretos de firma y los certificados de los módulos tienen
   que ser iguales en los tres.
3. **`.env`** en cada nodo, a partir de [`.env.example`](../../deploy/compose/.env.example), con
   `COMPOSE_FILE=compose.yaml`, `BILLING_HTTP` con la IP del nodo (`192.0.2.11:8080`) y `BILLING_PROXY` con la IP
   de la entrada (o su red CIDR): es a quien se le cree `X-Forwarded-For` y `X-Forwarded-Proto`.
4. **Migraciones, una sola vez,** desde un nodo: `docker compose run --rm migrate`.
5. **Arrancar** en cada nodo: `docker compose up -d`.
6. **Filtrar el 8080** para que solo entren los nodos de entrada:

   ```sh
   iptables -I DOCKER-USER -i eth0 -p tcp -m conntrack --ctorigdstport 8080 \
     -m iprange ! --src-range 192.0.2.11-192.0.2.13 -j DROP
   ```

   Con el rango de los nodos de entrada (o la IP del proxy), y persistido con el mecanismo del host.

`migrate` da de alta los módulos una sola vez en el núcleo, con las URL `https://arca:8443/`, `https://mercadopago:8443/`,
`https://stripe:8443/`, `https://paypal:8443/` y `https://cryptocloud:8443/`: en cada nodo resuelven al contenedor local.

## 9. Entrada

**Con HAProxy y keepalived**, en los tres nodos:
[`haproxy.cfg.example`](../../deploy/ha/haproxy.cfg.example) a `/etc/haproxy/haproxy.cfg` con el certificado
público, y [`keepalived.conf.example`](../../deploy/ha/keepalived.conf.example) con una prioridad distinta por nodo.
El DNS público apunta a la IP virtual.

**Con un proxy que ya existe** (NPM, nginx, un balanceador de la nube): un upstream con los tres nodos y estas
reglas:

- Chequeo a `/health/ready` y sacar el nodo en unos segundos, menos que `Shutdown:DrainDelay` (8 s en Compose).
- Sin afinidad de sesión: la aplicación no guarda estado.
- Timeouts de cliente y de servidor por encima de los 90 s del `Command Timeout` (100 s en `haproxy.cfg.example`):
  un COMMIT que espera a la réplica sincrónica dura hasta eso, y la respuesta reintentable tiene que llegar al
  cliente en vez de un corte del proxy.
- Reintentar en otro nodo solo si la conexión no se abrió. Nunca reintentar un POST que llegó al nodo.
- Mandar la IP real del cliente y el esquema original (`X-Forwarded-For` y `X-Forwarded-Proto: https`), y poner
  la IP del proxy en `BILLING_PROXY`. Prueba: con TLS terminado en el proxy, la interfaz tiene que responder 200 y
  no 500 (`AntiforgeryOptions.Cookie.SecurePolicy = Always` rechaza un pedido que la aplicación vea como HTTP), y
  una API key con lista de IP tiene que aceptar al cliente y no al proxy.

## 10. Verificación y aceptación

Antes de dar la instalación por buena:

1. `patronictl list`: un líder, dos réplicas sincrónicas, sin atraso.
2. `etcdctl endpoint health --cluster`: tres miembros sanos.
3. `/health/ready` en verde en los tres nodos, directo y por la entrada.
4. `pgbackrest --stanza=billing info`: un respaldo completo en cada repositorio, y el archivo de WAL al día.
5. Una conmutación planificada con carga corriendo (ver [conmutacion.md](conmutacion.md#conmutación-planificada)).
6. Un apagado forzado de la VM primaria desde el hipervisor, con carga corriendo. La base vuelve a aceptar
   escrituras en menos de 60 s y las consultas de [monitoreo.md](monitoreo.md#después-de-una-promoción) no
   muestran emisiones ni pagos perdidos o duplicados.
7. Una prueba de restauración completa (ver [backups.md](backups.md#prueba-de-restauración)).

## Variantes

- **HA estándar.** Las VMs de base hacen los pasos 1 a 7, y las de aplicación el 8. La entrada apunta a las de
  aplicación. Se suman nodos de aplicación copiando `secrets/` y con `docker compose up -d`.
- **Separar web y worker por nodo.** Con `docker compose up -d web` en unos y `worker` en otros. Los módulos van
  donde haya worker o web que los llame.
- **Endpoint único hacia la base.** Para herramientas que no aceptan varios hosts: el bloque `postgres_primaria`
  de HAProxy (puerto 5000), o vip-manager con una IP virtual que sigue a la llave de líder en etcd. La aplicación
  sigue con la cadena de varios hosts.
- **PgBouncer.** A partir de unos 6 procesos de aplicación, en modo transacción en cada nodo de aplicación,
  apuntando al endpoint único, con `query_timeout = 0` (un COMMIT cancelado se pierde en la conmutación). El código
  no depende del estado de sesión (una prueba de arquitectura lo exige), y las pruebas de integración se pueden
  correr a través de PgBouncer apuntando `BILLINGOSS_TEST_PG` a él. Las migraciones van directo a la primaria, nunca
  por PgBouncer.
- **Lo que no se soporta.** Dos nodos con datos más un árbitro con el modo estricto apagado: cuando cae el standby
  la primaria sigue confirmando con una sola copia y lo confirmado en esa ventana se pierde si pierde el disco, con
  ARCA y la pasarela ya adelante. Para facturar hacen falta tres nodos con datos y el modo estricto.
