# Despliegue de Hornero

**Estado:** v0, 2026-10-27 · **Decisión:** [D-14](../../diseno/decisiones.md#d-14-despliegue-y-alta-disponibilidad)

Recetas oficiales para correr Hornero, desde un solo host hasta alta disponibilidad (HA) en varias VMs, con
la base integrada en la plataforma o en un servicio externo. Las recetas están en [`deploy/`](../../deploy) y
estas guías dicen cómo usarlas.

| Guía | Qué tiene |
|---|---|
| [receta-chica.md](receta-chica.md) | Un host con Docker Compose: núcleo, PostgreSQL 18 con TLS, módulos y Gotenberg |
| [ha-vms.md](ha-vms.md) | HA en VMs: Patroni con replicación sincrónica, etcd, pgBackRest, HAProxy y varios nodos de aplicación |
| [conmutacion.md](conmutacion.md) | Qué hacer en cada falla: switchover, failover, réplicas caídas, etcd sin quórum, partición |
| [backups.md](backups.md) | pgBackRest con PITR, prueba periódica de restauración, restauración a un punto y pérdida del sitio |
| [actualizador.md](actualizador.md) | El actualizador de cada nodo: instalarlo, el canal firmado, actualizar con un clic o por consola y volver atrás (D-20) |
| [actualizaciones.md](actualizaciones.md) | Actualizar sin corte, migraciones en dos pasos, PostgreSQL, Patroni, etcd y rotación de llaves |
| [base-externa.md](base-externa.md) | Cómo apuntar a una base administrada o a un clúster propio del cliente |
| [monitoreo.md](monitoreo.md) | Qué mirar, consultas de negocio y alertas |

## Topologías soportadas

Todas usan la misma imagen y el mismo `deploy/compose/compose.yaml` para la aplicación. Cambia dónde está la base.

| Topología | Para quién | Piezas | Si cae un nodo | Pérdida y corte |
|---|---|---|---|---|
| Receta chica | Proveedores que aceptan un corte, laboratorios, demos y el staging chico | 1 host con Compose y PostgreSQL | Todo cae junto. Docker reinicia los procesos | Hasta un día de datos con la copia diaria. Horas para restaurar |
| HA compacta | Referencia. Proveedores que quieren HA en 3 VMs | 3 VMs, cada una con base (Patroni y etcd) y aplicación | Siguen los otros dos nodos | Nada perdido. Escrituras de vuelta en menos de 60 s |
| HA estándar | Proveedores medianos y hosting gestionado | 3 VMs de base y 2 o más de aplicación | Igual que la compacta, y base y aplicación caen por separado | Igual que la compacta |
| Base externa | Quien ya tiene un servicio administrado o un clúster propio | Nodos de aplicación con Compose y la base del proveedor | La conmutación de la base la decide el proveedor | Nada perdido si su HA es sincrónica. El corte es el del proveedor |

La HA cubre la caída de un host, una VM, un disco, un proceso o un enlace dentro del sitio. No cubre el sitio
entero (para eso está la [recuperación del sitio](backups.md#pérdida-del-sitio)), un switch o un firewall únicos,
ni el proveedor de identidad.

No hay receta propia para Kubernetes todavía. Quien lo use entra por la [base externa](base-externa.md):
CloudNativePG con replicación sincrónica cumple el contrato. El multiprimario no está soportado: la numeración de
los documentos, la exclusión de períodos facturados y la cola de trabajos dependen de un solo escritor.

## Cómo se evita perder o duplicar un comprobante o un pago

- **La base no pierde lo confirmado.** En HA, la replicación es sincrónica y estricta (quórum de Patroni o la HA
  sincrónica del proveedor): un commit confirmado ya está en otra base, y sin réplica las escrituras esperan en
  lugar de confirmarse en un solo lado. Una primaria aislada no puede confirmar nada.
- **Un trabajo no corre dos veces a la vez.** La cola toma cada trabajo con un lease y los de una misma entidad
  van de a uno (D-07). El planificador no tiene líder: dos nodos encolando lo mismo no duplican nada, por las
  claves únicas.
- **Un efecto afuera no se repite.** Las llamadas a los módulos llevan clave de idempotencia y el módulo guarda
  lo que ya hizo (D-08). Una emisión fiscal que quedó sin respuesta se resuelve consultando al fisco antes de
  volver a pedir.
- **Después de una promoción o una restauración se concilia** antes de volver a emitir y cobrar (D-14).

## La aplicación frente a la plataforma

### Imágenes

[`deploy/Dockerfile`](../../deploy/Dockerfile) arma las seis imágenes de la receta para Linux x64 desde la misma
solución, más la del importador para el paralelo ([paralelo](../paralelo.md)):

```sh
docker build -f deploy/Dockerfile --target nucleo      -t billing-oss:0.1.0 .
docker build -f deploy/Dockerfile --target arca        -t billing-oss-arca:0.1.0 .
docker build -f deploy/Dockerfile --target mercadopago -t billing-oss-mercadopago:0.1.0 .
docker build -f deploy/Dockerfile --target stripe      -t billing-oss-stripe:0.1.0 .
docker build -f deploy/Dockerfile --target paypal      -t billing-oss-paypal:0.1.0 .
docker build -f deploy/Dockerfile --target cryptocloud -t billing-oss-cryptocloud:0.1.0 .
```

- Base `aspnet:10.0.N-noble-chiseled-extra`, con la versión exacta del runtime: sin shell ni gestor de paquetes,
  con ICU y tzdata, que hacen falta para `es-AR` y `Billing:TimeZone`. El vigía compara esa versión con la metadata
  oficial de .NET ([docs/versiones](../versiones/README.md)).
- Corren como el usuario `app` (UID 1654), sin privilegios, y aguantan el sistema de archivos de solo lectura.
  Escriben solo en `/tmp` (tmpfs) y, los módulos, en `/data`.
- La compilación restaura con los archivos de bloqueo sin tocarlos (`RestoreLockedMode`).
- Cada despliegue anota el SHA del commit y el digest de cada imagen (`docker image inspect --format '{{.Id}}'`),
  así se sabe qué versión facturó cada día.

### Roles y comandos del núcleo

| Cómo se corre | Qué hace |
|---|---|
| `Role=web` | Atiende HTTP: API propia, API compatible, avisos de pasarelas y página de retorno |
| `Role=worker` | Corre la cola de trabajos y el planificador. No publica puertos, pero responde la salud en el 8080 |
| `Role=all` | Las dos cosas en un proceso. Sirve para desarrollo |
| comando `migrate` | Aplica las migraciones y termina. Corre aparte, con el rol dueño del esquema, antes de arrancar o actualizar |
| comando `probe URL` | Chequeo de salud del contenedor: sale con 0 si la URL responde 2xx |

Se pueden correr tantos web y worker como hagan falta, en uno o varios nodos.

### Salud

- `/health/live`: el proceso responde. Es el chequeo de salud de la imagen. Que la base no responda no reinicia el
  contenedor.
- `/health/startup`: el esquema está al día con el binario. Un nodo nuevo no recibe tráfico hasta que corrieron las
  migraciones.
- `/health/ready`: además la base acepta escrituras (es la primaria) y el proceso no se está apagando. Es el que
  mira el balanceador para sacar un nodo.
- `/health/cluster`: con `Database:Durability=sync`, que haya una réplica sincrónica, y que el reloj del nodo no se
  aleje del de la base. Para el arranque y la alerta, nunca para el balanceador.

Detalle en [docs/alta-disponibilidad/configuracion.md](../alta-disponibilidad/configuracion.md).

### Secretos

Nunca van en el repo: solo hay archivos `.example`. Cada instalación los genera ([`preparar.sh`](../../deploy/compose/preparar.sh))
y los guarda en su bóveda.

- **Como archivos.** El núcleo y los módulos leen `/run/secrets` como configuración: cada archivo es una clave y
  `__` separa los niveles. Así un Docker secret con destino `ConnectionStrings__Main` es la cadena de conexión.
  Los archivos van sin salto de línea al final.
- **Archivos que no son valores**, como la llave de D-09 o los certificados, se montan en otra ruta
  (`/run/billing`, `/run/tls`) para que no entren a la configuración.
- **Por variables de entorno** también funciona (`ConnectionStrings__Main=...`), pero quedan a la vista en
  `docker inspect`. Las recetas usan archivos.

### Configuración que toca el despliegue

| Clave | Valor en las recetas |
|---|---|
| `ConnectionStrings:Main` | Secreto. Un host o varios (ver abajo) |
| `Secrets:KeyFile` | `/run/billing/llaves`, el archivo de llaves de D-09 |
| `Role` | `web` o `worker` |
| `Billing:PublicUrl` | La URL pública, de `.env` |
| `ASPNETCORE_HTTP_PORTS` | 8080, el de la imagen |
| `Module:SigningSecret` | Secreto de firma de cada módulo, como archivo |
| `Modules:Included:{módulo}:Url`, `SigningSecret` y `Certificate` | Solo en `migrate`: la URL, el secreto de firma (archivo) y la ruta del certificado de cada módulo incluido, para darlo de alta solo |
| `Module:IdempotencyStorePath` | `/data/idempotency.db` en las imágenes de los módulos |
| `Kestrel:Certificates:Default:Path` y `KeyPath` | Certificado TLS de cada módulo |
| `Fiscal:ProductionEnabled` y `Arca:AllowProduction` | Apagadas en las recetas. Ninguna instancia fiscal de producción recibe pedidos hasta que el operador las prende |
| `Notifications:ProductionEnabled` | Apagada en las recetas. Aun con `Notifications:Send` prendido, solo reciben las casillas de `Notifications:OnlyTo`: ningún cliente recibe correo hasta que se prende |
| `ForwardedHeaders:KnownProxies:{n}` y `ForwardedHeaders:KnownNetworks:{n}` | `BILLING_PROXY` de `.env` (IP o red CIDR) y, si hace falta, más proxies o redes: el proxy que termina TLS, único al que se le cree `X-Forwarded-For` y `X-Forwarded-Proto`. Sin ninguno, detrás del proxy la interfaz responde 400 con el aviso de que falta declararlo (la API y la salud siguen andando) |
| `Pdf:GotenbergUrl` | `http://gotenberg:3000` en el rol web. El PDF con CAE no se genera hasta que el emisor tiene sus datos ([docs/pdf](../pdf/README.md)) |
| `Shutdown:Timeout`, `Shutdown:DrainDelay`, `Workers:ShutdownGrace` | 90 s, 8 s en web y 20 s: por debajo del `stop_grace_period` de cada servicio. El drenaje supera lo que HAProxy tarda en dar por caído al nodo (`inter 2s fall 3` más el timeout del chequeo) |
| `DOTNET_shutdownTimeoutSeconds` | 200 en los módulos: por encima del doble del plazo más largo de una operación con efecto de su manifiesto (el manejador tiene ese doble) y del plazo más largo de una de lectura, ver [modulos.md](../alta-disponibilidad/modulos.md) |
| `Database:Durability` | `sync` en HA, `single` en la receta chica, `managed` con base externa |
| `Module:IdempotencyConnectionString` | Solo con varias réplicas de un módulo, ver [modulos.md](../alta-disponibilidad/modulos.md) |

### Conexión a la base: una o varias

Una sola clave, `ConnectionStrings:Main`, para todas las topologías:

- **Un host:** `Host=postgres;...` en la receta chica, o el endpoint de escritura de un servicio administrado.
- **Varios hosts:** `Host=192.0.2.11,192.0.2.12,192.0.2.13;Target Session Attributes=read-write;...`. Npgsql
  prueba cada base y se queda con la que acepta escrituras, así la aplicación sigue a la primaria sin IP
  virtual ni balanceador. Es la forma oficial en la HA en VMs.
- **Un endpoint único** que siempre apunta a la primaria (IP virtual de vip-manager, HAProxy con el chequeo de
  Patroni o el servicio `-rw` de CloudNativePG) también sirve, con un solo host.

| Parámetro | Valor | Por qué |
|---|---|---|
| `SSL Mode` | `VerifyFull` | TLS siempre, validando el certificado y el nombre |
| `Root Certificate` | `/run/billing/pg-ca.crt` | La CA interna que firmó los certificados de PostgreSQL |
| `Target Session Attributes` | `read-write` | Con varios hosts, solo la primaria |
| `Host Recheck Seconds` | 5 | Cada cuánto vuelve a mirar quién es la primaria |
| `Timeout` | 5 | Una base caída no frena la conexión a la siguiente |
| `Keepalive` | 10 | Detecta conexiones muertas después de una conmutación |
| `Command Timeout` | 90 en HA | El COMMIT también corre bajo este tiempo. Si se corta mientras espera a la réplica sincrónica, queda confirmado solo en la primaria. Tiene que superar lo que tarda Patroni en degradar una primaria aislada (ttl 30 más loop_wait 10). La aplicación lo sube sola a 90 si viene menor, y además escucha el aviso con que PostgreSQL informa un COMMIT confirmado sin réplica y lo trata como falla pasajera |
| `Maximum Pool Size` | 20 | Por proceso. Con 3 nodos y web y worker en cada uno son 120, debajo de los 200 de la base |

Ante una conmutación, cada conexión del pool que apuntaba a la primaria vieja falla una vez y la siguiente ya va a
la nueva.

### Módulos

Cada nodo de aplicación corre todos los módulos, cada uno en su contenedor y en su propia red con el núcleo. El
núcleo los llama por el nombre del Compose (`https://arca:8443/`), que en cada nodo es el contenedor local, sin
balanceador en el medio. Por eso:

- `migrate` da de alta la instalación de cada módulo con su URL (`https://arca:8443/`, `https://mercadopago:8443/`,
  `https://stripe:8443/`, `https://paypal:8443/` o `https://cryptocloud:8443/`), su secreto de firma y la huella de su certificado, sin pasos a mano.
- Todos los nodos llevan el mismo certificado y el mismo secreto de firma de cada módulo: se generan una vez y
  se copian.
- El fiscal nunca comparte red, volumen ni secreto con otro módulo (D-08).

## Versiones y licencias

| Pieza | Versión | Licencia |
|---|---|---|
| PostgreSQL | 18 en las recetas, 17 soportada | PostgreSQL License |
| Patroni | 4.1 | MIT |
| etcd | 3.5 o 3.6 | Apache-2.0 |
| pgBackRest | 2.60 o posterior. Nunca una anterior a 2.59.3, por las subclaves de cifrado débiles | MIT |
| HAProxy y keepalived | Las de Debian 13 | GPL-2.0, como procesos aparte |
| vip-manager (opcional) | La vigente | BSD-2-Clause |
| Gotenberg | 8 | MIT. La imagen trae Chromium y LibreOffice con sus licencias y se baja del proyecto, no se redistribuye |
| Docker Engine y Compose | Las de Debian 13 o las del proyecto | Apache-2.0 |
| .NET | 10 | MIT |

Descartados: Consul y Vault (BSL 1.1), Redis (RSALv2, SSPL o AGPL según la versión) y MinIO (repositorio archivado).

## Lo que ya trae el código y lo que falta

Entró a la integración: el lease cercado con latido y reloj de la base, la cerca de todas las escrituras de un
trabajo dentro de su transacción, el apagado ordenado, el listo que distingue primaria de réplica y migraciones
pendientes, el registro de idempotencia de los módulos en PostgreSQL, el aviso del COMMIT confirmado sin réplica
tratado como falla pasajera, el anillo de Data Protection en la base, los encabezados reenviados, el estado de
instancia también en los errores, la conciliación (`reconcile`, sola ante una línea de tiempo nueva) y la
retención de los avisos de pasarela. Está contado
en [docs/alta-disponibilidad](../alta-disponibilidad/README.md). Lo que todavía falta:

| Falta | Qué pasa hoy | Mientras tanto |
|---|---|---|
| Marca externa de aprovisionamiento con consulta previa en `create` | Un reintento después de un PITR puede crear una segunda VM. La conciliación lista lo que tiene marca y no tiene servicio solo en los módulos que declaran `provisioning.list`, y por los demás deja un hallazgo para revisar el panel a mano | Los módulos de aprovisionamiento no corren con más de una réplica ni detrás de un volumen no persistente hasta que entre |
| `Secrets:ActiveVersion` | La versión vigente de la llave de D-09 es la mayor del archivo | Rotar como dice [actualizaciones.md](actualizaciones.md#llave-de-los-secretos-d-09) |
| Alta de instalaciones de módulos | No hay API ni admin para darlas de alta | Pendiente |
