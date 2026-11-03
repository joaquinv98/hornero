# Observabilidad

**Decisión:** [D-15](../../diseno/decisiones.md)

Facturación y aprovisionamiento fallan en silencio si nadie mira la cola. Esta guía dice qué expone Hornero
para verlo, cómo se prende en cada destino y qué hacer con cada alerta. Vale igual para una instalación de un
nodo y para varios nodos web y trabajadores contra la misma base.

| Pieza | Dónde | Para qué |
|---|---|---|
| Logs estructurados | Consola en JSON, y OTLP si está prendido | Diagnóstico, sin secretos |
| OpenTelemetry | OTLP hacia el destino que elija el proveedor | Logs, métricas y trazas de cada nodo |
| Métricas de negocio | Función `platform.metrics()` en la base | Cola, fiscal, avisos, pagos y facturación, iguales desde cualquier nodo |
| Auditoría | Tabla `platform.audit_events` | Quién hizo qué, sobre qué y cuándo |
| Salud | `/health/live` y `/health/ready` | Si el proceso vive y si llega a la base |
| Paneles y alertas | [`deploy/observabilidad/`](../../deploy/observabilidad/) | Zabbix, Gatus y Prometheus |

Nada de esto manda datos al proyecto. Sin destino configurado, OpenTelemetry queda apagado.

---

## Logs

La consola escribe una línea JSON por evento (`Logging:Console:FormatterName = json` en `appsettings.json`), con
fecha en UTC y los scopes: `TraceId` y `SpanId` del pedido o trabajo, y `JobId` y `JobKind` dentro de un
trabajo. Así se puede seguir un trabajo entre nodos con el `journalctl` o el colector de cada uno. Para leerlos
a mano en desarrollo: `Logging__Console__FormatterName=simple`.

Los niveles por defecto son `Information`, con ASP.NET Core y EF Core en `Warning`, así el SQL de cada consulta
no llena los logs.

**Lo que nunca va a un log:** el secreto de una API key, los encabezados `Authorization` y `Cookie`, el cuerpo de
un pedido o de un aviso de pasarela, la cadena de conexión, los valores de los parámetros SQL y los secretos de
los módulos. Lo controla la prueba `LosLogsNoLlevanCredencialesNiLaCadenaDeConexion`, que hace pedidos con
credenciales válidas e inválidas con todos los logs en `Trace` y busca el secreto y la contraseña de la base en
cada línea.

Para el código nuevo:
- Mensajes con plantilla y propiedades (`"Trabajo {Id} falló"`), nunca texto armado a mano.
- Nunca el cuerpo ni los encabezados de un pedido, ni un objeto del dominio entero.
- Nunca `EnableSensitiveDataLogging` fuera de una prueba local.

---

## OpenTelemetry

Se prende con una variable: `OTEL_EXPORTER_OTLP_ENDPOINT`. Sin ella no se registra nada de OpenTelemetry y no
hay costo. El resto se configura con las variables estándar, como variables de entorno o claves de
configuración:

| Variable | Para qué | Por defecto |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Destino OTLP, por ejemplo `http://otel-collector:4317` | Apagado |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` o `http/protobuf` | `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Credencial del destino si la pide. Es un secreto: con systemd, `EnvironmentFile=/etc/billing-oss/otel.env` con dueño root y modo 0600 y el valor sacado de la bóveda. Con Compose, `env_file` a un archivo fuera del repo con el mismo modo. Nunca en `appsettings.json` | Vacío |
| `OTEL_SERVICE_NAME` | Nombre del servicio | `billing-oss` |
| `OTEL_RESOURCE_ATTRIBUTES` | Atributos extra, por ejemplo `deployment.environment.name=staging` | Vacío |
| `OTEL_METRIC_EXPORT_INTERVAL` | Cada cuánto se exportan las métricas, en milisegundos | `60000` |

Cada nodo se identifica con `service.instance.id` (host y proceso, el mismo ID que usa en los leases de la cola)
y con `billing.role` (`web`, `worker` o `all`).

**Trazas:**
- Cada pedido HTTP, menos `/health`.
- Cada llamada a un módulo, que lleva `traceparent` y el módulo puede continuar.
- Un span por trabajo de la cola (`job {tipo}`, con `job.id`, `job.kind` y `job.attempt`), hijo de la traza que
  lo encoló. El vínculo viaja en `platform.jobs.trace_parent`, así un aviso de pasarela, su verificación, el
  pago y la emisión fiscal quedan en la misma traza aunque los corran nodos distintos.
- Sin spans de SQL: el sondeo de la cola cada 2 segundos haría una traza suelta por nodo. Se pueden sumar con un
  filtro de comandos de Npgsql si hace falta ver el SQL dentro de un pedido.
- Siempre se muestrea (`AlwaysOnSampler`), sin mirar la marca del `traceparent` entrante. Una pasarela que
  propaga su contexto sin muestrear, o alguien que manda `traceparent` con la marca en cero para esconderse, no
  deja sin span al pedido ni a los trabajos que cuelgan de él por `platform.jobs.trace_parent`. Lo que se
  descarta se decide en el colector, no en el cliente.

**Métricas propias** (medidor `BillingOss`):

| Instrumento | Tipo | Atributo | Qué mide |
|---|---|---|---|
| `billing.jobs.pending` | gauge | `job.kind` | Trabajos pendientes, incluidos los programados para más adelante |
| `billing.jobs.due` | gauge | `job.kind` | Pendientes que ya tendrían que estar corriendo |
| `billing.jobs.retrying` | gauge | `job.kind` | Pendientes que ya fallaron al menos una vez |
| `billing.jobs.running` | gauge | `job.kind` | En curso con el lease vigente |
| `billing.jobs.stuck` | gauge | `job.kind` | En curso con el lease vencido: el nodo que los tenía se cayó |
| `billing.jobs.dead` | gauge | `job.kind` | Agotaron los intentos y esperan al staff |
| `billing.jobs.oldest_due` | gauge (s) | `job.kind` | Espera del trabajo vencido más viejo |
| `billing.fiscal.emissions` | gauge | `emission.status` | Emisiones fiscales por estado |
| `billing.webhooks.unprocessed` | gauge | | Avisos de pasarela sin verificar |
| `billing.webhooks.oldest` | gauge (s) | | Espera del aviso sin verificar más viejo |
| `billing.payments.pending` | gauge | `payment.status` | Pagos pendientes o en disputa |
| `billing.run.duration` | gauge (s) | | Duración de la última corrida de facturación |
| `billing.run.age` | gauge (s) | | Tiempo desde que terminó la última corrida |
| `billing.job.duration` | histograma (s) | `job.kind`, `outcome` | Duración de cada trabajo: `done`, `later`, `retry` o `dead` |

Además se exportan las de la plataforma: pedidos HTTP y Kestrel, rechazos del límite de avisos
(`Microsoft.AspNetCore.RateLimiting`), llamadas salientes, el runtime de .NET y el pool de Npgsql.

**Con varios nodos:**
- Las gauges de negocio salen de la base, no de la memoria de un proceso. Las publican solo los roles `worker` y
  `all`, cada 30 segundos, y cada trabajador publica el mismo valor. En el destino se agregan con `max`, nunca
  con `sum`.
- El histograma de trabajos lo mide el nodo que corrió cada uno: ese sí se suma entre nodos.
- Si un trabajador no llega a la base, deja de publicar las gauges en lugar de repetir un valor viejo.

---

## Métricas de negocio: `platform.metrics()`

Es la fuente única de las métricas de negocio. La leen la aplicación, para OpenTelemetry, y Zabbix, por SQL.
Devuelve filas `(name, label, value)`:

| `name` | `label` | `value` |
|---|---|---|
| `jobs_pending`, `jobs_due`, `jobs_retrying`, `jobs_running`, `jobs_stuck`, `jobs_dead` | Tipo de trabajo | Cantidad |
| `jobs_oldest_due_seconds` | Tipo de trabajo | Segundos |
| `fiscal_emissions` | Estado (`Pending`, `Sending`, `Rejected`, `Unknown`, `Conflict`): las autorizadas no se cuentan, crecen para siempre y no alertan | Cantidad |
| `webhooks_unprocessed` | Vacío | Cantidad |
| `webhooks_oldest_seconds` | Vacío | Segundos |
| `payments_pending` | `Pending` o `Disputed` | Cantidad |
| `payments_unmatched` | Vacío | Pagos que una pasarela informó sin intento propio y siguen sin resolver (`payments.unmatched`) |
| `reconcile_findings` | Tipo de hallazgo (`fiscal.orphan`, `fiscal.ambiguous`, `counter.gap`, `provisioning.orphan`, `provisioning.unlisted`) | Hallazgos abiertos de la conciliación (`platform.reconcile_findings`) |
| `billing_run_seconds`, `billing_run_age_seconds` | Vacío | Segundos. Solo si ya hubo una corrida: en Zabbix el ítem queda sin datos y un disparador `nodata` avisa, y en Prometheus lo cubre `absent()` |

Los tipos de trabajo sin pendientes, en curso ni muertos no aparecen. La duración de la corrida sale de la
auditoría, donde cada corrida deja su registro.

Detalles que importan:
- Recibe la hora como parámetro (`platform.metrics(now())` por defecto). La aplicación pasa su reloj, el mismo
  con el que la cola decide qué está vencido.
- Corre con los permisos del dueño (`SECURITY DEFINER`). Un rol de monitoreo necesita solo `USAGE` sobre el
  esquema `platform`: ve conteos y nunca una fila con datos de clientes. El rol está en
  [`deploy/observabilidad/postgres/rol-metricas.sql`](../../deploy/observabilidad/postgres/rol-metricas.sql).
- Es solo lectura, así que con alta disponibilidad puede leerse de una réplica.
- Los avisos sin verificar, las emisiones que no terminaron bien y los pagos pendientes o en disputa tienen
  índices parciales, así la consulta no recorre lo ya procesado, lo autorizado ni lo aprobado, que crecen para
  siempre.

**Correos pendientes.** El núcleo todavía no manda correo. Cuando llegue el envío, si va por la cola de
trabajos sus pendientes, muertos y esperas aparecen solos con su tipo como etiqueta. Si usa una tabla propia, se
suma su fila a `platform.metrics()` con una migración (`CREATE OR REPLACE FUNCTION`) y una línea en
`BusinessMetrics`, y la plantilla de Zabbix gana un ítem dependiente.

Consultas útiles para diagnosticar, con un rol que pueda leer las tablas:

```sql
-- Todo lo que mira el monitoreo, como lo ve Zabbix.
SELECT * FROM platform.metrics() ORDER BY name, label;

-- Trabajos muertos con su último error.
SELECT id, kind, attempts, entity_key, updated_at, last_error
FROM platform.jobs WHERE status = 'Dead' ORDER BY updated_at DESC;

-- Lo vencido que nadie toma, del más viejo al más nuevo.
SELECT id, kind, run_at, attempts, last_error
FROM platform.jobs WHERE status = 'Pending' AND run_at <= now() ORDER BY run_at LIMIT 50;

-- La corrida de facturación de hoy.
SELECT status, attempts, updated_at, last_error
FROM platform.jobs WHERE dedup_key = 'billing.run:' || to_char(now() AT TIME ZONE 'America/Argentina/Buenos_Aires', 'YYYY-MM-DD');

-- Las cuentas que esa corrida repartió (un trabajo billing.account por cuenta) y cómo van.
SELECT status, count(*), max(last_error)
FROM platform.jobs WHERE kind = 'billing.account'
  AND dedup_key LIKE 'billing.account:' || to_char(now() AT TIME ZONE 'America/Argentina/Buenos_Aires', 'YYYY-MM-DD') || ':%'
GROUP BY status;
```

---

## Auditoría: `platform.audit_events`

La auditoría es una tabla del núcleo, no un log (D-15). Cada fila dice quién hizo qué, sobre qué y cuándo:

| Columna | Qué tiene |
|---|---|
| `created_at` | Cuándo |
| `actor_kind` | `System`, `Staff`, `Api`, `Module` o `Customer` |
| `actor` | `apikey:12:bk_7f3a9c21`, `staff:5` o `system:host:pid` |
| `action` | `POST /api/v1/orders`, `compat AddOrder`, `module /v0/provisioning/suspend` o `billing.run` |
| `entity` | Sobre qué: la ruta pedida, la URL del módulo o `job:42` |
| `ip` | IP del actor, si vino por HTTP. Detrás de un proxy es la del cliente solo con `ForwardedHeaders:KnownProxies` o `KnownNetworks` configurados ([configuracion.md](../alta-disponibilidad/configuracion.md)): sin eso es la del proxy, y el origen real sale del log del proxy cruzando por fecha y `trace_id` |
| `details` | JSON chico: estado HTTP, IDs de la respuesta, clave de idempotencia, resultado |
| `trace_id` | Traza en la que pasó, para cruzar con logs y trazas |

**Qué se registra hoy:**
- **API propia:** todo pedido que no es una lectura, de una credencial válida, con el estado HTTP y los campos de
  la respuesta que son identificadores. Nunca el cuerpo: puede traer datos personales o una credencial nueva.
- **API compatible:** los comandos que cambian algo (`AddClient`, `UpdateClient`, `AddOrder`,
  `AddInvoicePayment`, `AcceptOrder`).
- **Módulos:** cada operación con efecto, que son las que llevan clave de idempotencia según el protocolo, con
  quien la pidió, la clave y el resultado (`ok`, `pending` o el código de error). El actor viaja con el trabajo
  (`platform.jobs.actor_kind`, `actor` y `actor_ip`, como la traza): así queda quién suspendió un servicio aunque la
  suspensión la haya corrido un trabajador.
- **API compatible:** además del estado HTTP, el campo `result` de la respuesta (`success` o `error`) y los
  identificadores del formulario que nombran el objetivo (`clientid`, `invoiceid`, `orderid`, `transid`, `gateway`):
  un pago rechazado no queda igual que uno registrado.
- **Facturación:** cada corrida, con su duración y las facturas que emitió.

**Desde el código.** Quien autentica fija el actor y todo lo que sigue en ese flujo lo hereda, como la traza:

```csharp
Actor.Current = new Actor(ActorKind.Staff, $"staff:{userId}", http.Connection.RemoteIpAddress);
await audit.Record("documents.credit-note", $"document:{invoiceId}", new { reason_code });
```

`Record` escribe en la conexión y la transacción del contexto: dentro de un caso de uso queda o se va junto con
el cambio. `TryRecord` es para después de algo que ya pasó afuera: si la base falla, deja un error en el log y
no rompe lo hecho. Lo que no fija actor corre como `System`. El staff y el portal se suman cuando llegue la
interfaz: su autenticación fija `Actor.Current` y los casos de uso llaman a `AuditLog`.

En `details` nunca van secretos ni datos personales: IDs, estados y códigos alcanzan.

**Solo se agrega.** Dos triggers rechazan `UPDATE`, `DELETE` y `TRUNCATE`. El dueño de la base igual puede
desactivarlos: lo que lo cierra de verdad es que la aplicación corra con un rol distinto del dueño, sin `UPDATE`,
`DELETE` ni `TRUNCATE` sobre `platform.audit_events` (`billing_app` en las recetas de despliegue, que solo tiene lo
que las migraciones le dan). En v0 no hay purga. Si algún día hace falta, la hace el dueño de la base en una ventana,
con un `pg_dump` de la tabla antes y los triggers desactivados solo durante el borrado, y queda anotada en el registro
de cambios.

```sql
-- Quién tocó el servicio 42.
SELECT created_at, actor, action, details FROM platform.audit_events
WHERE entity LIKE '/api/v1/services/42/%' OR details ->> 'idempotency_key' LIKE 'svc-42-%' ORDER BY created_at;

-- Lo que hizo una API key en el último día.
SELECT created_at, action, entity, details ->> 'status' AS status FROM platform.audit_events
WHERE actor LIKE 'apikey:12:%' AND created_at > now() - interval '1 day' ORDER BY created_at;
```

---

## Salud

- `/health/live`: el proceso responde. Si falla, se reinicia el nodo.
- `/health/ready`: el nodo llega a la base. Si falla, el balanceador lo saca.

Ninguno de los dos ve un trabajador trabado o una cola que no avanza: eso lo dicen las métricas de negocio. Por
eso las alertas de la cola son las que importan.

---

## Zabbix

Para Zabbix (agent 2 con PSK), la plantilla está en
[`deploy/observabilidad/zabbix/billing-oss-templates.yaml`](../../deploy/observabilidad/zabbix/billing-oss-templates.yaml)
(formato 7.0, se importa también en 8.0) y trae dos plantillas:

- **Hornero negocio:** un ítem maestro `billing.metrics` con la salida de `platform.metrics()` en JSON,
  ítems dependientes por JSONPath, disparadores y gráficos. Se asigna a **un solo host**: el que corre la
  consulta. Asignarla a cada nodo repetiría cada alerta.
- **Hornero salud:** `/health/live` y `/health/ready` por HTTP desde el servidor de Zabbix. Se asigna a cada
  nodo web y trabajador, con `{$BILLING.URL}` apuntando a ese nodo.

**Puesta en marcha:**
1. Antes de tocar nada, avisar a quien opera el monitoreo. Mientras se despliega, el host va en un mantenimiento con
   recolección.
2. Con el dueño de la base, correr [`rol-metricas.sql`](../../deploy/observabilidad/postgres/rol-metricas.sql) y
   ponerle contraseña con `\password billing_metrics`. La contraseña se guarda en la bóveda. Habilitar al rol en
   `pg_hba.conf` desde el host de Zabbix: `hostssl billing_oss billing_metrics <IP del host de Zabbix>/32 scram-sha-256`
   y recargar. Con Patroni, en `postgresql.pg_hba` de `patroni.yml` y `patronictl reload`.
3. En el host elegido, que necesita `psql` y llegar a la base (el de la base, o un trabajador):
   - `/etc/zabbix/billing-oss/pg_service.conf` a partir de
     [`pg_service.conf.example`](../../deploy/observabilidad/zabbix/pg_service.conf.example).
   - `/etc/zabbix/billing-oss/pgpass` con la línea `<host igual que en pg_service.conf>:5432:billing_oss:billing_metrics:<contraseña de la bóveda>`.
   - La CA del certificado de PostgreSQL en `/etc/zabbix/billing-oss/root.crt`, con dueño `zabbix`: `verify-full` la
     exige, y el `host` tiene que ser el nombre del certificado.
   - Los dos archivos con dueño `zabbix` y modo `0600`.
   - [`billing-oss.conf`](../../deploy/observabilidad/zabbix/billing-oss.conf) en `/etc/zabbix/zabbix_agent2.d/`
     y reinicio del agente.
   - Prueba: `sudo -u zabbix zabbix_agent2 -t billing.metrics` tiene que devolver un arreglo JSON.
4. Importar la plantilla, asignar **Hornero negocio** al host del paso 3 y **Hornero salud** a cada nodo.
5. Sacar el mantenimiento cuando los ítems tengan datos.

**Disparadores:**

| Disparador | Severidad | Cuándo |
|---|---|---|
| La cola está trabada | High | El trabajo vencido más viejo espera más de `{$BILLING.QUEUE.MAX_AGE}` (900 s) |
| Hay trabajos muertos | High | Algún trabajo agotó los intentos |
| Trabajos con lease vencido | Warning | Hay leases vencidos durante 10 minutos |
| Emisión fiscal incierta | High | Una emisión sigue `Unknown` durante 15 minutos |
| Emisión fiscal en conflicto | High | Una emisión quedó en `Conflict` |
| Avisos de pasarela sin procesar | High | El aviso más viejo espera más de `{$BILLING.WEBHOOK.MAX_AGE}` (600 s) |
| Hay pagos en disputa | Warning | Algún pago en `Disputed` |
| La facturación no corre | High | Pasaron más de `{$BILLING.RUN.MAX_AGE}` (26 h) desde la última corrida |
| No se pueden leer las métricas | High | El ítem maestro no tiene datos hace 10 minutos |
| El nodo no responde | High | `/health/live` sin respuesta 200 hace 5 minutos |
| El nodo no está listo | High | Tres lecturas seguidas de `/health/ready` sin `Healthy` |

Los umbrales son macros de la plantilla: se cambian por host sin editarla.

---

## Gatus

Gatus mira desde afuera lo que ve un cliente o una pasarela, con proxy y certificado incluidos. Tiene sentido
recién cuando hay nombre público. Los chequeos están en
[`deploy/observabilidad/gatus/billing-oss.yaml`](../../deploy/observabilidad/gatus/billing-oss.yaml): listo, con
el certificado a más de 14 días, y la ruta de avisos, que con una instancia que no existe responde 404 sin
guardar nada. Se cambia `billing.example.com` por el nombre real y el tipo de alerta por el que use la
instalación de Gatus.

---

## Prometheus y otros destinos OTLP

Cualquier destino que reciba OTLP sirve: un OpenTelemetry Collector, Prometheus con su receptor OTLP, o un
servicio administrado. Las alertas equivalentes a las de Zabbix están en
[`deploy/observabilidad/prometheus/billing-oss.rules.yml`](../../deploy/observabilidad/prometheus/billing-oss.rules.yml).

En Prometheus los nombres cambian puntos por guiones bajos y suman la unidad: `billing.jobs.oldest_due` queda
`billing_jobs_oldest_due_seconds` y el atributo `job.kind` queda `job_kind`.

---

## Qué hacer con cada alerta

**La cola está trabada.** Ver si hay trabajadores vivos (`/health/live` de cada uno) y qué tipo de trabajo es el
que espera, con la consulta de vencidos de arriba. Si hay trabajadores y la cola no baja, buscar en sus logs el
`JobKind` que falla. Mientras la cola esté frenada no se factura, no se emite y no se acreditan pagos.

**Hay trabajos muertos.** Leer `last_error` con la consulta de arriba y arreglar la causa: credenciales de un
módulo, un módulo caído, un dato del cliente. Recién después se vuelve a encolar con
`UPDATE platform.jobs SET status = 'Pending', attempts = 0, run_at = now() WHERE id = ...`. Los trabajos son
idempotentes por diseño, pero un `fiscal.emit` se mira antes en `fiscal.emissions`: si la emisión ya está
`Authorized`, no hay nada que reintentar.

**Trabajos con lease vencido.** Un nodo se cayó con trabajos tomados. Cualquier trabajador vivo los retoma solo
cuando vence el lease. Si la alerta sigue, no hay trabajadores vivos.

**Emisión fiscal incierta.** El fisco no contestó a tiempo. El trabajo consulta solo qué pasó con ese número.
No reemitir a mano: podría salir un segundo comprobante para el mismo documento.

**Emisión fiscal en conflicto.** El fisco tiene un número que el núcleo no reconoce y la serie quedó frenada. Se
concilia la serie como dice D-14, con el contador si el comprobante del fisco no corresponde a ningún documento.

**Avisos de pasarela sin procesar.** Mirar los trabajos `payments.webhook` (pendientes o muertos) y la salud del
módulo de la pasarela. Los avisos quedan guardados: cuando el módulo vuelve, se verifican solos. La
conciliación horaria de cada pasarela trae además los pagos cuyo aviso nunca llegó.

**Hay pagos en disputa.** El cliente reclamó en la pasarela. Se responde ahí, antes del plazo que fija la
pasarela.

**Hay pagos sin casar.** La pasarela informó un pago que no corresponde a ningún intento propio: otro sistema
que usa la misma cuenta, o un cobro posterior al punto al que se restauró la base. Están en
`payments.unmatched`. Si el pago es de otro sistema, se marca resuelto. Si es de un cliente, se registra a mano
contra su factura y se marca resuelto. El procedimiento está en
[backups.md](../despliegue/backups.md#después-de-restaurar).

**Hay hallazgos de conciliación sin resolver.** Hubo una promoción o una restauración y la conciliación
encontró algo que no cierra: un comprobante del fisco sin documento, un número con dos candidatos, un salto de
numeración o un recurso con marca sin servicio. Están en `platform.reconcile_findings`, y qué hacer con cada
uno está en [backups.md](../despliegue/backups.md#después-de-restaurar).

**La facturación no corre.** Ver el trabajo `billing.run` del día con la consulta de arriba. Si no existe, el
planificador no corre: no hay trabajadores. Si está muerto, su `last_error` dice por qué. `billing.run` solo
reparte una cuenta por trabajo `billing.account`: si la corrida está hecha pero falta una factura, la cuenta está
en uno de esos trabajos, pendiente, reintentando o muerto con su `last_error`.

**No se pueden leer las métricas.** La base no responde, el rol `billing_metrics` no entra o el UserParameter
falta. Probar con `zabbix_agent2 -t billing.metrics` en el host.
