# Protocolo de módulos v0

**Estado:** borrador v0 · **Parte de:** [diseño del núcleo](README.md) · **Decisión:** D-08

Un módulo es un servicio aparte, en su propio proceso o contenedor, que conecta el núcleo con un sistema
externo: un hipervisor, un panel, un registrar, una pasarela de pago o el fisco. El núcleo lo llama por HTTPS
con JSON. El módulo nunca toca la base del núcleo ni corre código dentro de él.

El protocolo es diseño propio. No imita la interfaz de módulos de WHMCS ni busca que sus módulos funcionen
acá: no hay un host de módulos PHP. Los módulos que vienen de otro sistema se reescriben contra este protocolo,
con su propia lógica de negocio.

v0 es inestable a propósito: puede cambiar sin aviso hasta v1, que se congela con el primer lanzamiento
público.

---

## 1. Principios

- **Fuera de proceso.** Un módulo que falla o se cuelga no tumba el núcleo, y se puede escribir en cualquier
  lenguaje.
- **Módulos de confianza.** En v0 solo corren módulos propios. Un módulo ve los secretos de sus instancias,
  así que se lo trata como parte del sistema. Los módulos de terceros llegan con el catálogo (sección 13).
- **Sin configuración propia.** El núcleo manda la configuración de la instancia en cada llamada, secretos
  incluidos, y es la única fuente de verdad. El núcleo nunca guarda el sobre de una llamada: los trabajos, la
  auditoría y los logs guardan referencias.
- **Solo llama el núcleo.** En v0 el módulo nunca llama al núcleo. Las operaciones largas se consultan
  (sección 7) y los webhooks de las pasarelas llegan al núcleo, que se los pasa al módulo para verificarlos.
- **Idempotencia.** Toda operación con efecto lleva una clave, y repetirla no repite el efecto.
- **Capacidades declaradas.** El módulo dice qué sabe hacer y el núcleo no le pide otra cosa.
- **Datos mínimos.** Cada llamada lleva solo lo que la operación necesita. Los datos personales que no son
  imprescindibles para el tipo de módulo se piden en el manifiesto y el staff los ve al instalarlo.
- **Desconfianza en la respuesta.** El núcleo valida todo lo que devuelve un módulo, contra el esquema y
  contra lo que pidió. Por ejemplo, el monto de un pago se compara con el intento de cobro.

---

## 2. Conceptos

| Concepto | Qué es |
|---|---|
| Módulo | Programa que implementa este protocolo para uno o más tipos. Tiene un `id` estable, por ejemplo `proxmox` |
| Tipo (`kind`) | `provisioning` (aprovisionamiento), `registrar`, `payment` (cobros), `fiscal`, `extension` (personalización, sección 9.5) o `fraud` (evaluación de fraude de pedidos, sección 9.6) |
| Instalación | Un módulo registrado en el núcleo: URL base, secreto de firma, certificado o huella si usa una CA propia, y manifiesto aceptado |
| Instancia | Una configuración concreta de una instalación, por ejemplo "cluster Proxmox principal" o "Mercado Pago de la empresa". Una instalación puede tener varias. Es la `ModuleInstance` del [modelo de dominio](dominio.md). Tiene además la lista de hosts que el navegador puede visitar (sección 9.1) |
| Parámetros del producto | Lo que el módulo necesita por producto, por ejemplo plantilla, RAM y disco. Se validan contra el esquema del módulo |
| Datos del pedido | Lo que completa el cliente al contratar, por ejemplo el hostname |
| Operación | Una llamada con efecto. Termina en el momento o queda en curso |

---

## 3. Rutas

| Método y ruta | Firmada | Para qué |
|---|---|---|
| `GET /v0/health` | No | Salud del módulo, sin datos |
| `GET /v0/manifest` | Sí | Manifiesto (sección 5) |
| `POST /v0/instance/test` | Sí | Probar la configuración de una instancia contra el sistema externo |
| `POST /v0/{kind}/{operation}` | Sí | Operaciones (sección 9) |
| `POST /v0/operations/{id}` | Sí | Estado de una operación larga, con el sobre completo (sección 7) |

Las rutas usan solo minúsculas, dígitos, `_`, `-` y `/`, y no llevan query: todo dato va en el cuerpo.

La versión del protocolo va en la ruta. Dentro de una versión mayor solo se agregan cosas: campos opcionales,
operaciones nuevas detrás de capacidades nuevas y códigos de error nuevos. Los dos lados ignoran los campos que
no conocen. El núcleo puede hablar dos versiones mayores a la vez durante una transición.

---

## 4. Transporte y seguridad

**Canal.** HTTPS con TLS 1.2 o superior y el certificado siempre validado. Si el módulo usa una CA propia, la
instalación guarda ese certificado o la huella del módulo. Nunca hay opción para no validar. Sin TLS solo se
permite en el mismo host, por loopback o por un socket Unix compartido. Como cada llamada lleva secretos, el
canal tiene que ser confidencial.

**Firma.** Cada pedido del núcleo lleva estos encabezados:

| Encabezado | Contenido |
|---|---|
| `X-Request-Id` | UUID único de cada intento. Un reintento lleva uno nuevo |
| `X-Timestamp` | Hora del pedido en segundos Unix |
| `Idempotency-Key` | Solo en operaciones con efecto (sección 6) |
| `X-Signature` | `v0=` más el HMAC-SHA256 en hexadecimal, con el secreto de la instalación, de estas líneas: timestamp, método, ruta desde `/v0`, `X-Request-Id`, `Idempotency-Key` (vacía si no hay) y SHA-256 del cuerpo en hexadecimal |
| `Accept-Language` | Idioma para los textos que el módulo devuelve |
| `traceparent` | Contexto de traza W3C, para correlacionar en OpenTelemetry |

El módulo responde `401 invalid_signature` si la firma no coincide, si el reloj difiere en más de 300
segundos o si `X-Request-Id`, `X-Timestamp`, `X-Signature` o `Idempotency-Key` vienen más de una vez: con una
segunda línea de la clave, la firma cubriría la primera y el registro leería otra, y un pedido capturado se
repetiría con efecto. No hace falta recordar pedidos ya vistos: la clave de idempotencia va firmada, así que
repetir un pedido capturado no repite el efecto. La respuesta no se firma: la protege TLS con el certificado
validado, o el canal local.

**Rotación.** El módulo acepta dos secretos a la vez mientras dura una rotación. Se despliega el módulo con el
secreto nuevo y el viejo, se cambia el núcleo al nuevo y después se retira el viejo.

**Destino de los secretos.** Cambiar la URL de una instalación, o cualquier campo no secreto de una instancia
que tiene secretos (por ejemplo la URL de la API del hipervisor), exige reautenticarse y volver a cargar esos
secretos. Así nadie puede mandar los secretos guardados a otro destino sin conocerlos.

**Límites.** Cuerpos JSON en UTF-8 de hasta 1 MiB en cada sentido. El núcleo espera 30 segundos por llamada,
salvo que el manifiesto declare otro valor para una operación, con un máximo de 120. Lo que tarda más pasa a
operación larga.

**Aislamiento.** Usuario sin privilegios, sistema de archivos de solo lectura salvo un directorio de trabajo,
salida a internet limitada a los destinos que el módulo necesita y ningún acceso a la base del núcleo. El
módulo fiscal va en su propia instalación, con una sola instancia por emisor y salida solo hacia el fisco,
porque recibe la clave privada del certificado.

---

## 5. Manifiesto

`GET /v0/manifest` describe el módulo. El núcleo lo guarda al instalarlo y lo vuelve a leer al actualizar. Si
una versión nueva cambia los esquemas o pide más datos personales, el staff lo ve y lo aprueba.

```json
{
  "protocol": ["0"],
  "id": "proxmox",
  "name": "Proxmox VE",
  "version": "0.3.0",
  "vendor": "Hornero",
  "license": "Apache-2.0",
  "kinds": ["provisioning"],
  "capabilities": [
    "provisioning.core", "provisioning.change_plan", "provisioning.status",
    "provisioning.actions", "provisioning.view"
  ],
  "config_schema": {
    "type": "object",
    "required": ["api_url", "token_id", "token_secret"],
    "properties": {
      "api_url": { "type": "string", "format": "uri" },
      "tls_fingerprint": { "type": "string", "description": "SHA-256 del certificado, si es autofirmado" },
      "token_id": { "type": "string" },
      "token_secret": { "type": "string", "x-secret": true }
    }
  },
  "product_schema": {
    "type": "object",
    "required": ["template", "cores", "memory_mb", "disk_gb"],
    "properties": {
      "template": { "type": "string" },
      "cores": { "type": "integer", "minimum": 1 },
      "memory_mb": { "type": "integer", "minimum": 512 },
      "disk_gb": { "type": "integer", "minimum": 10 }
    }
  },
  "order_schema": {
    "type": "object",
    "properties": { "hostname": { "type": "string", "pattern": "^[a-z0-9-]{1,63}$" } }
  },
  "credentials": ["root_password"],
  "actions": [
    { "id": "reboot", "label": { "es": "Reiniciar", "en": "Reboot" }, "audience": ["staff", "client"] },
    { "id": "reinstall", "label": { "es": "Reinstalar", "en": "Reinstall" }, "audience": ["staff", "client"],
      "confirm": true, "input_schema": { "type": "object", "properties": { "template": { "type": "string" } } } }
  ],
  "data_scopes": [],
  "timeouts": { "create": 60 },
  "max_in_flight": 4
}
```

| Campo | Para qué |
|---|---|
| `protocol` | Versiones mayores que el módulo habla |
| `capabilities` | Lo que sabe hacer (sección 9). El núcleo no ofrece en la interfaz lo que el módulo no declara |
| `config_schema`, `product_schema`, `order_schema` | JSON Schema 2020-12. El núcleo arma los formularios con estos esquemas. Los campos `x-secret` se guardan cifrados, se muestran como contraseña y nunca se vuelven a mostrar |
| `credentials` | Credenciales que el núcleo genera y manda en `create` y en las acciones que las cambian (sección 6) |
| `actions` | Acciones propias del módulo, con etiquetas por idioma, quién las ve y si piden confirmación. Una acción que usa credenciales las nombra en su propio `credentials`, y una que las cambia en `rotates_credentials`: el núcleo manda las primeras tal como las tiene y las segundas las genera de nuevo y las guarda cifradas antes de llamar. Las dos van en `input.credentials` de `run_action`, con `input.action` e `input.params` |
| `usage_metrics` | Métricas de uso que lee `usage`, con su tipo, unidad y etiqueta por idioma (sección 9.1) |
| `data_scopes` | Datos personales extra que el módulo pide (sección 8) |
| `timeouts`, `max_in_flight` | Espera por operación y llamadas simultáneas por instancia |
| `deadlines` | Plazo de una operación larga en segundos, por operación. Sin declarar es una hora. `null` es sin plazo, para las que esperan a una persona |
| `currencies`, `browser_hosts`, `webhook_events` | Solo cobros, opcionales. Monedas que cobra (`*` es cualquiera que acepte la pasarela), hosts del navegador por ambiente (`test` y `live`) y eventos que hay que suscribir en la pasarela. Los usa el asistente de cobros |
| `x-help` en cada campo de `config_schema` | Opcional. De dónde sale el dato, por idioma (`es`, `en`) y con el enlace al panel (`url`, solo https) |

---

## 6. Sobre de las llamadas

Todas las operaciones usan `POST /v0/{kind}/{operation}` con este cuerpo:

```json
{
  "instance": { "id": "inst-3", "config": { "...": "..." }, "state": null },
  "context": { "...": "datos según el tipo y los permisos (sección 8)" },
  "input": { "...": "parámetros de la operación" }
}
```

Respuesta cuando terminó:

```json
{ "status": "completed", "result": { "...": "..." }, "instance_state": null }
```

Respuesta cuando sigue en curso:

```json
{ "status": "pending", "operation_id": "op-77f2", "retry_after": 15 }
```

Respuesta con error, con el código HTTP de la sección 10:

```json
{
  "error": {
    "code": "upstream_unavailable",
    "message": "El hipervisor no responde",
    "retryable": true,
    "retry_after": 30,
    "details": {}
  },
  "instance_state": null
}
```

**Dinero.** Siempre como texto decimal con moneda, nunca como número de punto flotante:
`{ "value": "12100.00", "currency": "ARS" }`.

**Idempotencia.** Las operaciones con efecto llevan `Idempotency-Key`. El núcleo la arma con la entidad, la
operación y su generación, por ejemplo `svc-1042-create-1`. La generación sube cuando cambia la intención, por
ejemplo un reintento del staff después de un error definitivo o un plan distinto. El módulo garantiza:

1. La misma clave con la misma operación y la misma `input` produce el efecto una sola vez y devuelve el mismo
   resultado, o el estado actual de la operación. La configuración, el estado de la instancia y el contexto no
   cuentan para comparar.
2. La misma clave con otra `input` devuelve `409 idempotency_conflict`.
3. Recuerda cada clave al menos el plazo de la operación más 7 días, en un almacenamiento que sobrevive a los
   reinicios y que comparten todas sus réplicas.
4. En `create` y `register` deja además una marca en el sistema externo cuando este lo permite, por ejemplo
   una etiqueta con el ID del servicio en la VM. La marca sobrevive aunque se pierda el registro. Conviene que
   lleve el uuid del servicio (`context.service.uuid`), que no cambia nunca y, a diferencia del ID, no choca con
   el de otra instalación que comparte el sistema externo. Antes de crear, el módulo busca la marca o el nombre
   del recurso para no crear dos veces ni tomar uno ajeno (consulta previa).
5. En operaciones de dinero (`refund`) pasa la clave a la pasarela cuando esta acepta idempotencia. Si no la
   acepta y el módulo no tiene la clave registrada, consulta a la pasarela antes de repetir.

Pasado el plazo del punto 3, el núcleo no repite con la misma clave: consulta el estado (`get_status`,
`get_domain` o `get_payment`) y, si hace falta, decide el staff.

**IDs externos.** Cuando una operación crea algo, el módulo devuelve `result.external_ids`, un mapa de textos
(por ejemplo nodo y VMID). El núcleo lo guarda en el servicio y lo manda de vuelta en `context` en cada llamada
posterior. Así el módulo no necesita buscar a qué recurso corresponde cada servicio. `create` puede devolver además
`result.label`, la etiqueta del servicio (por ejemplo el hostname), que el núcleo guarda si el pedido no trajo una.

**Credenciales.** Las que declara el manifiesto las genera el núcleo, las guarda cifradas (D-09) antes de
llamar y las manda en `input.credentials`. Así una respuesta perdida no deja a nadie sin la contraseña. Si el
sistema externo es el que las genera, el módulo las devuelve en `result.secrets` y las guarda cifradas mientras
recuerda la clave, para poder devolverlas si se repite la llamada.

**Estado de la instancia.** Con la capacidad `instance_state`, el módulo puede devolver `instance_state`, un
JSON de hasta 64 KiB que el núcleo guarda cifrado y le vuelve a mandar en `instance.state`. Puede venir también
en una respuesta de error, al lado de `error`, y el núcleo lo guarda igual: un ticket obtenido en una llamada
que después falla no queda solo en la memoria de una réplica. Sirve para tickets
de acceso que no conviene pedir de nuevo, como el ticket de WSAA en ARCA, o para tokens OAuth que rotan. Para
que dos llamadas no se pisen el estado, el núcleo hace de a una las llamadas de una instancia que usa esta
capacidad. Una respuesta repetida por idempotencia no trae `instance_state`: el registro de idempotencia no lo
guarda, para no dejar tickets en otro lado y para que una repetición no pise un estado más nuevo. El núcleo guarda
el estado de cada respuesta que lo traiga y se lo manda a la llamada siguiente, aunque sea de la misma corrida.

---

## 7. Operaciones largas

Si una operación no termina dentro del tiempo de espera, el módulo responde `pending` con un `operation_id`.
El núcleo consulta `POST /v0/operations/{id}` con el sobre completo (configuración, estado y contexto, porque
el módulo no los guarda), respetando `retry_after` y con espera creciente, hasta recibir `completed` o un
error, o hasta el plazo de la operación (por defecto, una hora, o el de `deadlines` en el manifiesto). Las
consultas no gastan intentos del trabajo. El módulo manual declara todas sus operaciones sin plazo: una tarea
del staff puede durar días, y mientras tanto la cola del servicio o del dominio queda esperando, que es lo
correcto. Un módulo sin plazo recuerda sus claves mientras la operación siga abierta y 7 días más.

Si el módulo perdió la operación, por ejemplo porque se reinició, responde `404 not_found`. Entonces el núcleo
repite la llamada original con la misma `Idempotency-Key`, y el módulo resuelve con su registro o con la marca
externa (sección 6).

El núcleo guarda la operación en curso junto con el nombre de la operación que la abrió, y la consulta solo desde
esa operación. Si cambió la intención (por ejemplo quedó en curso una baja y el staff pidió reactivar), la
operación nueva no la retoma: la descarta y se pide entera. Un error definitivo en la consulta también la
descarta.

Si la llamada del núcleo al módulo se corta sin respuesta, el resultado es incierto. El núcleo reintenta con la
misma clave, lo que es seguro por la sección 6. En el tipo fiscal nunca repite `authorize` a ciegas: pasa
antes por `find` (sección 9.4).

---

## 8. Contexto y datos personales

El núcleo arma `context` según el tipo y la operación:

| Tipo | Contexto que siempre va | Con permiso |
|---|---|---|
| `provisioning` | Servicio: ID, uuid, estado, IDs externos, parámetros del producto y del pedido, plan. Cuenta: ID e idioma | `account.contact` (nombre y correo), `account.address` |
| `registrar` | Dominio e IDs externos. Los contactos del titular, porque el registro los exige | No aplica |
| `payment` | Intento de cobro: ID, monto, moneda, facturas y URLs de retorno. Cuenta: ID e idioma | `account.contact` |
| `fiscal` | El comprobante completo, con los datos del receptor que la ley exige | No aplica |
| `fraud` | Pedido: ID, fecha, monto, moneda y renglones. IP y navegador de quien compra. Cuenta: ID, país, idioma y alta | `account.contact` (nombre y correo), `account.address` (domicilio y teléfono) |

Los permisos quedan en la auditoría. Las SDK ocultan en los logs los campos `x-secret`, `input.credentials`,
`result.secrets` e `instance.state`.

---

## 9. Operaciones por tipo

Cada operación pertenece a una capacidad. Las marcadas como núcleo del tipo (`*.core`) son obligatorias para
ese tipo. La prueba de configuración (`/v0/instance/test`) es obligatoria para todos.

### 9.1 Aprovisionamiento (`provisioning`)

| Operación | Capacidad | Qué hace |
|---|---|---|
| `validate` | `provisioning.core` | Valida parámetros y datos del pedido sin efectos, por ejemplo que el hostname esté libre |
| `create` | `provisioning.core` | Crea el recurso con las credenciales que manda el núcleo. Devuelve IDs externos y estado observado |
| `suspend`, `unsuspend` | `provisioning.core` | Suspende y reactiva |
| `terminate` | `provisioning.core` | Da de baja y libera recursos |
| `change_plan` | `provisioning.change_plan` | Aplica parámetros nuevos de producto o de opciones |
| `get_status` | `provisioning.status` | Estado observado (`active`, `suspended`, `terminated`, `not_found` o `unknown`) para la reconciliación del [modelo de dominio](dominio.md) (sección 5.1) |
| `run_action` | `provisioning.actions` | Ejecuta una acción del manifiesto aceptado. El núcleo controla que la acción exista y que el usuario esté en su `audience` |
| `view` | `provisioning.view` | Bloques para mostrar el servicio en el portal (ver abajo) |
| `sso` | `provisioning.sso` | URL de acceso directo al panel del servicio, con vencimiento corto. El núcleo manda la IP del navegador en `input.client_ip`, porque algunos paneles atan la sesión a ella |
| `list` | `provisioning.list` | Lista los recursos del sistema externo que llevan la marca del núcleo (punto 4 de la idempotencia, sección 6), con `service_id`, `external_ids` y `label`, paginados con `offset` y `next_offset` como `list_payments`. Es para la conciliación de D-14 después de una restauración: lo que tiene marca y no tiene servicio queda como hallazgo. Un módulo que no la declara deja un hallazgo para revisar su panel a mano |
| `usage` | `provisioning.usage` | Lectura actual de las métricas de uso del servicio, para cobrar el excedente de lo que incluye el plan (ver abajo y [uso medido](uso-medido.md)) |

**Vista del servicio.** El módulo nunca manda HTML. `view` devuelve hasta 50 bloques de texto plano, que el
núcleo dibuja con sus propios componentes. El portal la carga aparte de la página y la guarda un minuto, para
no esperar ni multiplicar llamadas al sistema externo.

| Bloque | Contenido |
|---|---|
| `fields` | Pares de etiqueta y valor en `items`, cada uno con `label` y `value`, por ejemplo IP y sistema operativo |
| `status` | Estado en `text`, con su color en `level`: `ok`, `info`, `warning` o `error` |
| `actions` | Botones que remiten a acciones del manifiesto, con sus IDs en `items` |
| `link` | Enlace externo que se abre en otra pestaña, con `label` y `url` |
| `notice` | Aviso en `text`, con su color en `level`, con los mismos valores que `status` |

**Uso medido.** Con `provisioning.usage`, el manifiesto declara sus métricas en `usage_metrics`:

```json
"usage_metrics": [
  { "id": "transfer_gb", "kind": "counter", "unit": "GB", "label": { "es": "Transferencia", "en": "Transfer" } },
  { "id": "disk_gb", "kind": "gauge", "unit": "GB", "label": { "es": "Disco", "en": "Disk" } }
]
```

`kind` es `gauge` para un nivel (disco ocupado, casillas, dispositivos) y `counter` para un acumulado que solo sube
y puede volver a cero (bytes transferidos, segundos de CPU). `usage` es una lectura sin efecto, sin
`Idempotency-Key`. La entrada lleva los códigos que pide el núcleo, `{ "metrics": ["transfer_gb", "disk_gb"] }`, y
la respuesta trae la lectura actual de cada uno como texto decimal sin signo, con 6 decimales como máximo:
`{ "metrics": { "transfer_gb": "1834.25", "disk_gb": "12.5" } }`. El contador va crudo, como lo muestra el sistema
externo, y las diferencias las calcula el núcleo. Una métrica que no se pudo leer no va en la respuesta. Si el
recurso no existe, `not_found`. `usage` nunca responde `pending`.

**Uso medido.** Con `provisioning.usage`, el manifiesto declara sus métricas en `usage_metrics`:

```json
"usage_metrics": [
  { "id": "transfer_gb", "kind": "counter", "unit": "GB", "label": { "es": "Transferencia", "en": "Transfer" } },
  { "id": "disk_gb", "kind": "gauge", "unit": "GB", "label": { "es": "Disco", "en": "Disk" } }
]
```

`kind` es `gauge` para un nivel (disco ocupado, casillas, dispositivos) y `counter` para un acumulado que solo sube
y puede volver a cero (bytes transferidos, segundos de CPU). `usage` es una lectura sin efecto, sin
`Idempotency-Key`. La entrada lleva los códigos que pide el núcleo, `{ "metrics": ["transfer_gb", "disk_gb"] }`, y
la respuesta trae la lectura actual de cada uno como texto decimal sin signo, con 6 decimales como máximo:
`{ "metrics": { "transfer_gb": "1834.25", "disk_gb": "12.5" } }`. El contador va crudo, como lo muestra el sistema
externo, y las diferencias las calcula el núcleo. Una métrica que no se pudo leer no va en la respuesta. Si el
recurso no existe, `not_found`. `usage` nunca responde `pending`.

**Módulo manual.** Viene con el núcleo para los productos sin módulo, por ejemplo un enlace que se arma a mano
o una venta única de software. Corre dentro del núcleo, sin HTTP, con el mismo sobre, las mismas respuestas y
los mismos códigos de error. Su manifiesto declara `provisioning` y `registrar`, sin credenciales ni campos
`x-secret`, porque no llama a ningún sistema externo, y pide `account.contact` porque el staff contacta al
cliente. Por producto lleva:

| Parámetro | Valores |
|---|---|
| `activation` | `on_payment`: `create` termina enseguida con `observed_state` `active`. `staff`: abre una tarea |
| `lifecycle` | `state_only`: `suspend`, `unsuspend` y `terminate` terminan enseguida con el estado pedido. `staff`: abren una tarea |
| `instructions` | Texto para el staff, que va en la tarea con las opciones y los datos del pedido |
| `staff_fields` | Campos que carga el staff al cerrar: `id`, `label` por idioma, `client_visible` y `secret` |

Cada tarea es única por instancia y `Idempotency-Key`, y la operación responde `pending` con `operation_id`
`task-{id}` y `retry_after` de una hora. Al cerrarla, el núcleo reprograma el trabajo para que corra enseguida.
Cerrada, la operación devuelve `observed_state`, `external_ids` con `staff_ref` y los `fields`. Los campos
secretos se guardan cifrados como credenciales del servicio en el cierre y nunca vuelven en una respuesta.
`change_plan` siempre abre una tarea. `get_status` devuelve lo que confirmó el staff en la última tarea
cerrada, o `unknown`, y nunca `not_found`, para que la reconciliación no toque estos servicios. `view` devuelve
los campos visibles para el cliente, el estado y un aviso si hay una tarea abierta. `run_action` y `sso` dan
`not_supported`. Errores propios: `invalid_input` si faltan los parámetros del producto, `conflict` si el
servicio ya tiene otra tarea abierta, `idempotency_conflict` y `rejected` cuando el staff la rechaza, con el
motivo en `details.upstream.reason`.

**URLs para el navegador.** Toda URL que devuelve un módulo y que va a ver el navegador (`link`, `sso` y la
redirección de los cobros) tiene que ser `https` y su host tiene que estar en la lista de la instancia. `sso`
se pide solo cuando el usuario aprieta el botón, por `POST`, y el núcleo redirige enseguida, sin guardar la URL
en logs y con `Referrer-Policy: no-referrer`.

### 9.2 Registrar (`registrar`)

| Operación | Capacidad | Qué hace |
|---|---|---|
| `check` | `registrar.core` | Disponibilidad de uno o varios nombres, con precio si es premium |
| `register` | `registrar.core` | Registra por N años con contactos y nameservers |
| `renew` | `registrar.core` | Renueva N años |
| `get_domain` | `registrar.core` | Estado, vencimiento, nameservers y bloqueo, para la sincronización |
| `set_nameservers` | `registrar.core` | Cambia los nameservers |
| `transfer` | `registrar.transfer` | Transferencia entrante con el código EPP del cliente |
| `get_auth_code` | `registrar.auth_code` | Código EPP para llevarse el dominio (el núcleo lo guarda cifrado). Va aparte de `transfer` porque hay registrars que transfieren y no entregan el código por API |
| `set_lock` | `registrar.lock` | Bloqueo de transferencia |
| `set_contacts` | `registrar.contacts` | Cambia los contactos |
| `get_prices` | `registrar.pricing` | Costos por TLD, para sugerir precios de venta |
| `get_dns`, `set_dns` | `registrar.dns` | Registros de la zona, cuando el registrar la administra en sus servidores DNS |
| `get_email_forwards`, `set_email_forwards` | `registrar.email_forwarding` | Reenvíos de correo del dominio |
| `set_privacy` | `registrar.privacy` | Protección de identidad: los datos del titular no se publican en el WHOIS ni en el RDAP |

**Contexto.** `context.domain` lleva `id`, `name`, `status`, `expires_on`, `nameservers` y `external_ids`.
`context.account` lleva `id`, `language`, `name`, `company_name`, `email` y `phone`. `context.contacts.registrant` es el
titular: el propio del dominio o, si no tiene, el que sale de la cuenta y su identidad fiscal vigente, con
`first_name`, `last_name`, `company`, `email`, `phone` (código de país y número, `+54.1155551234`), `address1`,
`address2`, `city`, `state`, `postal_code` y `country` (ISO de dos letras). Los obligatorios son nombre, apellido,
correo, teléfono, domicilio, ciudad, código postal y país, y el núcleo los controla antes de facturar un pedido.

**Entradas y resultados** de los registrars con API:

| Operación | `input` | `result` |
|---|---|---|
| `check` | `names`, de 1 a 50 | `results`: por nombre `name`, `available` (nulo si el registrar no lo sabe), `premium` y, si lo es, `price` con `value` y `currency`. `reason` opcional |
| `register` | `name`, `years` (1 a 10), `nameservers` (2 a 13) y `privacy` opcional, que el núcleo manda solo si el registrar declara `registrar.privacy` | `status`, `expires_on`, `nameservers` y `external_ids`. Si el registro no es inmediato, queda en curso |
| `transfer` | `name`, `auth_code` (hasta 255), `years` y `nameservers` opcionales | Queda en curso hasta que el registro de origen la suelta. Termina como `register` |
| `renew` | `years` y `current_expiry`, el vencimiento que se renueva | `expires_on` |
| `get_domain` | Nada: el dominio va en el contexto | `status`, `expires_on`, `nameservers`, `locked`, `external_ids`, `source` `registrar` y `privacy` si se sabe |
| `set_nameservers` | `nameservers` | `nameservers` |
| `set_lock` | `locked` | `locked` |
| `get_auth_code` | Nada | `secrets.auth_code`. El núcleo lo guarda cifrado en el dominio y lo saca del resultado antes de guardarlo |
| `set_contacts` | Nada: el titular nuevo va en el contexto | Vacío |
| `get_prices` | `currency` opcional | `prices`: por TLD y años, `currency` y los costos `register`, `renew` y `transfer` como texto decimal, nulos si no se ofrecen |
| `get_dns` | Nada | `records`: por registro `name` (relativo a la zona, `@` es la zona), `type`, `value`, `ttl` y `priority` en los MX |
| `set_dns` | `records`, de 0 a 100: la zona entera | `records` como quedaron |
| `get_email_forwards` | Nada | `forwards`: por reenvío `mailbox` (la parte antes de la arroba) y `to` |
| `set_email_forwards` | `forwards`, de 0 a 50: todos los reenvíos | `forwards` como quedaron |
| `set_privacy` | `enabled` | `enabled` |

`status` es `active`, `pending`, `pending_transfer`, `expired`, `redemption`, `transferred_away`, `failed`,
`not_found` o `unknown`. Es el estado en el registro y va aparte del estado de facturación del dominio: solo
`transferred_away` cambia algo del lado del núcleo, porque el dominio deja de renovarse y de facturarse.

**Reglas de los registrars con API:**
- **Marca externa del registro** (punto 4 de la sección 6). Si el registrar dice que el nombre está tomado y el
  dominio ya figura activo o en curso en la cuenta, es la respuesta de un registro anterior que se perdió, y se da
  por hecho.
- **Marca externa de la renovación.** Antes de renovar se consulta el vencimiento. Si ya cubre `current_expiry` más
  los años pedidos, una llamada anterior renovó y se perdió su respuesta, así que devuelve ese vencimiento sin volver
  a renovar. Protege aunque falte el registro de la clave.
- **Operaciones largas sin estado.** El `operation_id` solo dice qué se espera (`register-...` o `transfer-...`). Cada
  consulta trae el dominio en el contexto y termina cuando aparece activo en la cuenta. `failed` o `transferred_away`
  dan `rejected`. El manifiesto declara plazos de 3 días para el registro y de 14 para la transferencia, que se
  consulta cada hora.
- **Errores.** `rejected` lleva en `details.upstream.reason` un motivo propio y estable: `taken`,
  `invalid_auth_code` o `failed`. Un corte antes de enviar es `upstream_unavailable` y uno después de enviar algo con
  efecto es `outcome_unknown`. Nunca siguen redirecciones, porque llevarían las credenciales a otro destino.
- **Estado de la instancia.** Lo usa OpenProvider para su token, que dura 48 horas: se renueva antes de vencer y,
  ante un 401, se vuelve a entrar una sola vez.
- **Zona y reenvíos completos.** `set_dns` y `set_email_forwards` reciben la lista entera y el módulo la compara con lo
  que hay: saca lo que sobra y agrega lo que falta. Así repetir el pedido, aun con otra clave o después de un corte,
  deja lo mismo y no cambia nada si ya estaba. Se administran los registros A, AAAA, CNAME, MX y TXT: los demás tipos
  de la zona (NS, SOA, SRV, CAA) no se leen ni se tocan. La regla de cada registro es la misma en el núcleo y en el
  módulo (`DnsRecords` en la biblioteca del protocolo): nombre relativo en minúsculas, A en IPv4, AAAA en IPv6, CNAME
  y MX con un nombre de host, TXT de hasta 2048 caracteres sin comillas, TTL de 60 a 86400 segundos, prioridad solo en
  MX, sin CNAME en `@` y sin otros registros donde hay un CNAME. Un reenvío lleva una casilla y un destino. Lo que no
  cumple da `invalid_input` sin llamar al registrar.
- **Protección de identidad.** `set_privacy` deja la protección como se pide, y si ya estaba así no falla. Namecheap la
  maneja como una suscripción del dominio: sin ella da `rejected` con `reason` `no_privacy_subscription`.

El núcleo pide `register` y `transfer` recién con la factura del pedido paga, `renew` con la renovación paga (o al
llegar su anticipación si el precio es cero) y `get_domain` una vez por día para cada dominio vivo, con la clave de
entidad del dominio. Las consultas sin efecto que alguien pide en el momento (`check` y `get_prices`) se llaman
directo, con espera corta.

El registrar manual es el mismo módulo `manual` con tipo `registrar`, y cumple el mismo contrato: cada operación
con efecto crea una tarea para el staff y queda en curso hasta que el staff la cierra. Sirve para los TLD que
no tienen un registrar con API en la instalación. La configuración de la instancia dice qué registro atiende
en `registry`:

| `registry` | Para qué | `check` |
|---|---|---|
| `nic.ar` | .ar de NIC Argentina, que no tiene API: los trámites se hacen en Trámites a Distancia con la clave fiscal de quien gestiona | `available` nulo con `reason` `requires_staff`, porque el RDAP de NIC falla demasiado para usarlo |
| `own_dns` | Subdominios de las zonas propias del proveedor | Libre si el nombre no está vivo en el núcleo |
| `manual_registrar` | Un registrar sin módulo, por ejemplo uno que dejó de tener API configurada | Como `nic.ar` |
| `none` | Dominios sin registrar, solo para conservar su historia | Como `nic.ar` |

Operaciones: `register` (`name`, `years`, `nameservers` y `contacts`) y `renew` (`years` y `current_expiry`)
devuelven al cerrarse `expires_on`, `nameservers` y `external_ids` vacío. `set_nameservers` pide al menos dos.
`get_domain` devuelve lo último que cargó el staff con `source` `staff`, y `status` `unknown` si no hay
vencimiento. `transfer`, `get_auth_code`, `set_lock`, `set_contacts`, `get_prices` y las de zona, reenvíos y
protección dan `not_supported`. No guarda credenciales del registro: el staff entra con las suyas, y por eso no hay
campos `x-secret`.

Reglas de .ar (`registry` `nic.ar`), según NIC Argentina: se registra y se renueva por un año, y `years`
distinto de 1 da `invalid_input`. La renovación se pide desde 30 días antes del vencimiento hasta los días de
gracia después (45, en `grace_days` de la instancia, porque hubo prórrogas). Antes de la ventana `renew` da
`conflict` con `retry_after` hasta que abra, y pasada la gracia da `rejected` con `reason` `released`, sin
abrir tarea.

Como el registrar manual no consulta ningún registro, para los .ar el núcleo pregunta directo al RDAP de NIC
Argentina, con reintentos y espera creciente porque falla seguido: en la búsqueda, cuando `check` devuelve
`available` nulo, y en la sincronización diaria, para traer el vencimiento. Solo un 404 dice que un nombre está
libre. Si el RDAP no contesta, la búsqueda prueba el DNS (un nombre delegado está registrado, y uno sin delegar queda
sin confirmar) y la sincronización lo vuelve a intentar al día siguiente, sin abrir tareas.

### 9.3 Cobros (`payment`)

| Operación | Capacidad | Qué hace |
|---|---|---|
| `create_checkout` | `payment.checkout` | Prepara el cobro de un intento. Devuelve la URL de la pasarela a la que se redirige al cliente |
| `get_payment` | `payment.core` | Estado de un pago por ID externo: monto, moneda, comisión, fecha y la referencia del intento |
| `verify_webhook` | `payment.webhooks` | Recibe el cuerpo crudo en `body_base64`, los encabezados y los parámetros que llegaron al núcleo, y `received_at`, la hora en que llegaron. Devuelve si la firma vale y los eventos normalizados: ID de evento, tipo e ID externo del pago. El ID de evento sale solo de lo que la pasarela firma, y la edad de lo firmado se mide contra `received_at`, no contra la verificación, que corre en un trabajo que puede demorar |
| `refund` | `payment.refund` | Reembolso total o parcial |
| `create_subscription`, `get_subscription`, `cancel_subscription` | `payment.subscriptions` | Débito recurrente que administra la pasarela. `create_subscription` devuelve la URL donde el cliente la aprueba y cada cobro llega como pago con `subscription_external_id` |
| `update_subscription` | `payment.subscription_update` | Cambia el monto de una suscripción existente |
| `list_payments` | `payment.reconcile` | Pagos de un rango de fechas, para conciliar. Pagina por posición (`next_offset`) o por cursor (`next_cursor`) |
| `charge_saved` | `payment.saved_methods` | Cobra un intento con un medio guardado, sin el cliente presente. Devuelve lo mismo que `get_payment`. Un rechazo trae `details.retry` (`later`, `never` o `customer_action`) y, si la pasarela da una página para resolverlo, `customer_action_url` |
| `get_method`, `delete_method` | `payment.saved_methods` | Estado de un medio guardado (marca, últimos 4, vencimiento) y su baja en la pasarela. Repetir la baja da éxito |

**Medios guardados** (`payment.saved_methods`). `create_checkout` acepta `save_method: true` y `customer_external_id`,
y la pasarela guarda el medio para cobros iniciados por el comercio. El consentimiento lo junta el núcleo antes de
redirigir. `get_payment` devuelve `saved_method` cuando el pago dejó un medio reutilizable, nunca el número completo
ni el código de seguridad. `charge_saved` mueve plata, así que vale el punto 5 de la sección 6: el módulo pasa la
clave `autocharge-{intento}` a la pasarela y, sin respuesta cierta, busca el cobro por la referencia del intento
antes de repetir, por lo menos tres días hacia atrás: el núcleo deja de repetir a los dos. Un módulo que no puede hacer
ninguna de las dos cosas no declara la capacidad. Un cobro rechazado se informa `rejected` también en `get_payment`. `verify_webhook`
puede devolver eventos de medio con `method_external_id` (la red actualizó la tarjeta o el cliente la borró).

**Campos opcionales de cobros.** `verify_webhook` recibe `received_at`, la hora en que el núcleo guardó el aviso,
para las pasarelas que piden una ventana sobre su marca de tiempo. `instance/test` recibe `webhook_url` para
comprobar que el aviso de la pasarela apunta al núcleo, y devuelve `livemode`: credenciales de otro ambiente que la
instancia no pasan la prueba. `get_payment` y `list_payments` pueden devolver `livemode` en cada pago, y en una
instancia de producción el núcleo no registra uno del ambiente de prueba.

Reglas del tipo:
- **Recepción de webhooks** en dos pasos. Primero el núcleo guarda lo que llega a `/webhooks/{instancia}` sin
  ninguna unicidad: hasta 64 KiB, con límite de pedidos por instancia y sin los encabezados `Cookie` ni
  `Authorization`. Después un trabajo llama a `verify_webhook`, y recién los eventos verificados se guardan
  con restricción única por instancia e ID de evento. Si la unicidad se aplicara al recibir, cualquiera podría
  ocupar de antemano el ID de un evento real con un pedido falso. Un aviso que no vale se queda sin cuerpo y
  se borra a los `Workers:KeepDone` días. Uno válido conserva la fila con su evento, y el cuerpo se vacía a los
  `Workers:KeepWebhookBodies` días.
- El webhook nunca alcanza para dar algo por pagado. El núcleo pide el pago con `get_payment` y valida monto,
  moneda y referencia contra el intento ([modelo de dominio](dominio.md), sección 8).
- `create_checkout` puede devolver además `checkout_external_id`, el ID del checkout en la pasarela. El núcleo lo
  guarda en el intento y, mientras el intento sigue abierto y el checkout todavía se puede cobrar (seis horas
  desde que se abrió), cada quince minutos lo pasa como `payment_external_id` a `get_payment`, por si el aviso de
  la pasarela no llegó. El módulo devuelve ahí un ID que su `get_payment` acepte (en PayPal, `order:` más la
  orden) y contesta `not_found` mientras no haya pago, que el núcleo no toma como falla.
- La página de retorno de la pasarela, `/pay/return/{intento}`, se atiende por `GET` y no cambia nada: muestra
  el estado del intento. El pago se confirma por webhook o por consulta.
- Los pagos manuales, como transferencias o efectivo, los carga el staff en el núcleo o una integración por
  la API, sin módulo. Las transferencias entran además por el extracto del banco, conciliadas por el CUIT del
  ordenante ([guía de cobros](../docs/cobros/README.md)).
- Los módulos de cobro son código de confianza: verificar y consultar al mismo módulo no frena a uno
  malicioso. La defensa final es la conciliación con los movimientos de la cuenta.

### 9.4 Fiscal (`fiscal`)

| Operación | Capacidad | Qué hace |
|---|---|---|
| `parameters` | `fiscal.core` | Tablas del fisco (tipos de comprobante, de documento, condiciones frente al IVA, alícuotas y monedas), para validar antes de emitir |
| `last_number` | `fiscal.core` | Último número autorizado para un punto de venta y tipo, con su fecha. En exportación suma `last_request_id`, el último ID de pedido que usó el emisor |
| `authorize` | `fiscal.core` | Pide autorización con el número que propone el núcleo. Devuelve código de autorización (CAE en ARCA), vencimiento y observaciones, o `rejected` con los códigos del fisco |
| `find` | `fiscal.core` | Busca un comprobante por punto de venta, tipo y número. Devuelve sus datos y su código, o `not_found` |
| `exchange_rate` | `fiscal.exchange_rate` | Cotización oficial de una moneda para una fecha. Con `type_code` usa la fuente que corresponde a ese tipo (en ARCA, la de WSFEX para exportación). Si el fisco no tiene cotización para esa fecha, prueba los días anteriores y devuelve la fecha de la que encontró |
| `taxpayer` | `fiscal.taxpayer` | Datos de un contribuyente por su número, para completar la ficha |

La configuración de una instancia fiscal lleva `environment`, con `production` para el fisco real y otro valor
para su ambiente de prueba. El núcleo trata como de producción solo a la instancia que lo dice, y mientras su
ajuste `Fiscal:ProductionEnabled` esté apagado no le manda ninguna operación que pueda autorizar un comprobante.
El módulo tiene su propia traba, en su configuración de proceso, y con ella apagada rechaza la instancia de
producción con `invalid_config`.

WSFEv1 no tiene idempotencia (WSFEX sí, con su ID de pedido según su manual, y el módulo lo usa) y el número
lo propone el núcleo. Por eso la emisión sigue estos pasos, que implementan los estados de la sección 7.1 del
[modelo de dominio](dominio.md), donde también están los manuales citados (sección 14):

1. El núcleo toma la serie (emisor, punto de venta y tipo) con un lease: una fila con dueño y vencimiento, no
   un lock de base abierto durante la llamada. Lo mantiene mientras el resultado sea incierto. El trabajo de
   emisión lleva la clave de entidad de la serie, así una serie emite de a uno y dos series no se esperan.
2. Pide `last_number`, que devuelve el número y la fecha del último autorizado. Si el fisco tiene un número
   mayor que el último que el núcleo registró para la serie, frena la serie y concilia con `find` antes de
   emitir. Eso pasa si se perdieron datos en un failover o si alguien usó el punto de venta desde otro
   sistema. Si la serie se comprobó contra el fisco hace menos de un día (al autorizar o en el control diario)
   el núcleo se saltea la consulta y propone el siguiente: un número ya usado vuelve como incierto en el paso
   6 y la consulta lo descubre. La exportación consulta siempre, porque el ID de pedido es por CUIT.
3. Propone el número siguiente, con la fecha del día, el vencimiento de pago y la cotización del intento
   (sección 7 del modelo de dominio). Guarda el intento antes de llamar.
4. Llama a `authorize` con la clave `{emisión}-{número}-{intento}`: cada intento es una intención distinta,
   con su fecha y su cotización, así que no choca con la huella que dejó uno anterior en el registro del
   módulo. Nunca repite `authorize` a ciegas: un intento nuevo sale solo después de un error definitivo o de que
   `find` confirme que el número no existe.
5. Si vuelve autorizado, guarda el código y libera la serie. Si vuelve `rejected` o cualquier otro error
   definitivo (`invalid_input`, `invalid_config`, `invalid_signature`, `not_supported` o `not_found`), guarda
   el mensaje y los detalles, marca la emisión como rechazada y libera la serie: esos errores dicen que no se
   autorizó nada, y un módulo fiscal nunca los devuelve si el comprobante pudo haberse autorizado. Si el error
   es del módulo y no del fisco, además deja el trabajo muerto y visible, porque hace falta que el staff corrija
   los datos o la configuración y reintente.
   `idempotency_conflict` no entra, porque el pedido anterior con esa clave pudo haber llegado: se trata como
   incierto. El módulo nunca devuelve como `rejected` un rechazo por número fuera de secuencia: lo devuelve como
   `outcome_unknown`, porque el comprobante pudo haberse autorizado en un intento anterior.
6. Si el resultado es incierto (corte, `outcome_unknown`, otro error reintentable o `idempotency_conflict`),
   espera un rato y llama a `find` con ese número:
   - Si lo encuentra y coinciden fecha y total, y también receptor, concepto, período de servicio, vencimiento
     de pago, moneda, desglose de IVA e ID de pedido de exportación cuando el fisco los informa, lo da por
     autorizado. Los números se comparan como números, porque el fisco devuelve el documento sin ceros
     adelante. Si otra emisión de la serie también propuso ese número, o ya hay un comprobante con él, nadie lo
     adopta: la emisión pasa a `Conflicto` y lo resuelve el staff.
   - Si no existe, vuelve al paso 2. Después de 5 intentos la emisión queda pendiente con el motivo y su
     trabajo, muerto y visible para el staff, así una emisión que nunca se aclara no frena la cola fiscal.
   - Si existe con otros datos, la emisión pasa a `Conflicto`, la serie queda frenada y se avisa al staff.
   Lo que queda en `Conflicto`, rechazado o pendiente con el trabajo muerto lo resuelve el staff por la API
   propia del núcleo: adopta el comprobante encontrado, reintenta o ajusta la serie (sección 7.1 del modelo de
   dominio).

Ejemplo de entrada de `authorize`. Es ilustrativo: los códigos salen de `parameters`, no se fijan en el
código.

```json
{
  "document": {
    "point_of_sale": 3,
    "type_code": 6,
    "number": 1488,
    "date": "2026-10-25",
    "concept": "services",
    "service_period": { "from": "2026-10-01", "to": "2026-10-31" },
    "payment_due": "2026-11-05",
    "receiver": { "doc_type": 96, "doc_number": "30111222", "vat_condition": 5, "name": "Cliente de ejemplo" },
    "currency": "PES",
    "exchange_rate": "1",
    "amounts": {
      "net_taxed": "10000.00",
      "net_untaxed": "0.00",
      "exempt": "0.00",
      "vat": [{ "rate_id": 5, "base": "10000.00", "amount": "2100.00" }],
      "other_taxes": "0.00",
      "total": "12100.00"
    },
    "associated": [],
    "optional": []
  }
}
```

Las fechas de `service_period` son inclusivas, como las pide el fisco: el núcleo convierte su rango
[desde, hasta). En moneda extranjera, el documento lleva además la cotización y si se cancela en esa misma
moneda (RG 5616). La exportación (WSFEX) suma `items` con su unidad, la forma de pago, el idioma y los datos
del cliente del exterior (domicilio, código de país y CUIT del país). Fuera de `document` va `wsfex_id`, el ID
de pedido: el núcleo lo calcula con `last_request_id` y el último propio, lo guarda antes de llamar y lo
repite en cada reintento de la misma emisión. Solo la factura de exportación informa la fecha de pago y la
cancelación en la misma moneda, y en ese caso va sin cotización. Las notas llevan siempre la cotización de su
comprobante asociado.

### 9.5 Extensiones (`extension`)

Una extensión personaliza el núcleo sin tocar su código: se entera de lo que pasa y puede frenar un pedido. El
núcleo la llama según los ganchos que el staff le asigna ([webhooks y extensiones](webhooks-y-extensiones.md),
sección 4).

| Operación | Capacidad | Qué hace |
|---|---|---|
| `on_event` | `extension.events` | Recibe un evento del catálogo público. Tiene efecto: lleva `Idempotency-Key` |
| `validate_order` | `extension.validate_order` | Dice si un pedido se puede confirmar. Sin efecto y sin clave |

**`on_event`.** El contexto va vacío. La entrada es el evento, con el mismo `id` para todos los ganchos y webhooks
de esa publicación:

```json
{ "id": "6f1c0a3e9b7d4c2a8e5f1b3d7c9a2e4f", "type": "invoice.paid", "occurred_at": "2026-11-02T15:00:00.000Z", "data": { "invoice_id": 4521 } }
```

Responde `completed`, y el núcleo ignora `result`. La clave es `hook-{gancho}-{evento}`: un reintento con la
misma clave no repite el efecto (sección 6). Un error reintentable vuelve a la cola y uno definitivo deja el
trabajo muerto y visible para el staff.

**`validate_order`.** El núcleo la llama antes de abrir la transacción del pedido, con el tiempo de espera del
manifiesto para esta operación, de 5 segundos por defecto y 10 como máximo. Contexto y entrada:

```json
{
  "context": { "account": { "id": 812, "language": "es", "country": "AR", "services": [ { "id": 3307, "product_id": 41, "status": "active" } ] } },
  "input": { "items": [ { "product_id": 41, "plan_id": 77, "cycle": { "unit": "free", "count": 1 }, "quantity": 1, "options": [ { "option_id": 5, "value_id": 16, "quantity": null } ], "order": {} } ] }
}
```

`options` es lo elegido de cada opción configurable, como lo recibe el pedido de la API propia: el valor de una lista
y la cantidad de una opción de cantidad o de sí o no.

Responde `completed` con `result.allowed`, y si rechaza, `result.reason`: un texto para el cliente en el idioma de
la cuenta, de hasta 300 caracteres y sin HTML. Si no responde, el pedido se rechaza o pasa según el gancho.

### 9.6 Fraude (`fraud`)

| Operación | Capacidad | Qué hace |
|---|---|---|
| `evaluate` | `fraud.core` | Evalúa un pedido y devuelve un puntaje de riesgo con sus motivos |

`evaluate` lleva `Idempotency-Key` (`order-{id}-evaluate`) aunque no cambie nada afuera: cada consulta a un servicio
de puntaje se paga, y un reintento devuelve el mismo resultado sin pagar dos veces. La entrada:

```json
{
  "order": {
    "id": "1042",
    "placed_at": "2026-10-29T15:00:00Z",
    "amount": { "value": "12100.00", "currency": "ARS" },
    "items": [{ "product_id": "7", "name": "VPS 2 GB", "quantity": 1 }]
  },
  "request": { "ip": "203.0.113.7", "user_agent": "Mozilla/5.0 ..." }
}
```

En `context` va la cuenta (`id`, `country`, `language` y `created_at`) y, si el manifiesto los pide, `contact`
(`name`, `email` y `phone`) y `address` (`street`, `city`, `province`, `postal_code` y `country`). El resultado:

```json
{
  "score": "37.50",
  "reasons": [{ "code": "ip.anonymous", "message": "La IP es de un servicio de anonimato" }],
  "external_id": "5f8e0c2a-0000-0000-0000-000000000000"
}
```

Reglas del tipo:
- `score` es un decimal de 0 a 100, en texto como el dinero. Más alto es más riesgo. El núcleo lo compara con su
  umbral: el módulo no decide.
- `reasons` tiene hasta 20 motivos. `code` usa minúsculas, dígitos, punto, guion y guion bajo, hasta 64 caracteres.
  `message` es texto plano para el staff, hasta 300. El cliente nunca los ve.
- Un servicio sin fondos, con la cuenta suspendida o con las credenciales rechazadas responde un error definitivo
  (`invalid_config` o `rejected`). El núcleo aplica su ajuste: el pedido va a revisión o pasa, según el proveedor.
- Los datos personales viajan solo con su permiso. Si el servicio acepta el correo como hash, el módulo manda el hash.

---

## 10. Errores

| Código | HTTP | Reintentable | Cuándo |
|---|---|---|---|
| `invalid_signature` | 401 | No | Firma inválida o reloj fuera de ventana |
| `invalid_config` | 422 | No | La configuración de la instancia no sirve, por ejemplo credenciales rechazadas |
| `invalid_input` | 422 | No | Parámetros inválidos. `details.fields` lista los campos |
| `not_supported` | 501 | No | Operación o capacidad que el módulo no implementa |
| `not_found` | 404 | No | El recurso o la operación no existe |
| `idempotency_conflict` | 409 | No | Misma clave con otra entrada |
| `conflict` | 409 | Sí | Estado transitorio incompatible, por ejemplo otra tarea en curso sobre el mismo recurso |
| `rejected` | 422 | No | El sistema externo rechazó por sus reglas (fisco, pasarela o registro). `details.upstream` trae sus códigos |
| `rate_limited` | 429 | Sí | Con `retry_after` |
| `upstream_unavailable` | 503 | Sí | El sistema externo no respondió y el módulo sabe que el pedido no llegó a ejecutarse |
| `outcome_unknown` | 504 | Con cuidado | El pedido pudo haberse ejecutado. El núcleo reintenta con la misma clave y, en el tipo fiscal, consulta antes |
| `internal` | 500 | Sí | Falla del módulo |
| `manual_review` | 409 | No | Inconsistencia que tiene que revisar el staff, por ejemplo una VM sin la marca del servicio. El módulo no tocó nada |

`message` es para el staff y nunca lleva secretos. Al cliente el núcleo le muestra un texto propio.

Un error reintentable puede traer `details.auto_retry` en `false`: el módulo pide que una persona lo mire antes
de repetirlo, y el núcleo lo toma como definitivo.

Un error que no se reintenta en una operación de aprovisionamiento, `manual_review` incluido, queda en el núcleo
como tarea de revisión del servicio para el staff, con el código, el mensaje y `details`, y el servicio tiene a
lo sumo una abierta. En `create` el servicio queda además fallido. Una baja que el módulo no pudo hacer deja el
servicio marcado para la baja: no renueva más y la mora la vuelve a pedir cada día. Si esa baja la había pedido
la mora y el cliente paga, se levanta: el servicio vuelve a renovar y la revisión se cierra. El staff revisa y
vuelve a pedir la operación, con el `input` que haga falta (por ejemplo `force` en una baja con agentes
instalados): ese pedido es una intención nueva, va con otra generación de la clave, y si termina bien cierra la
revisión. Una acción del módulo que sale bien cierra solo la revisión que dejó otra acción. El módulo manual queda
fuera de todo esto: lo que contesta es la tarea que el staff ya cerró, y una baja que el staff rechazó no marca el
servicio para la baja.

La reconciliación diaria con `get_status` (sección 5.1 del [modelo de dominio](dominio.md)) deja la misma tarea,
con el código propio del núcleo `state_mismatch` y el estado observado en `details`, cuando lo que informa el
módulo contradice al estado del núcleo. No manda operaciones sola, y cierra esa revisión cuando vuelven a
coincidir. Los módulos que no declaran `provisioning.status` y el módulo manual quedan fuera.

---

## 11. SDK y kit de conformidad

**SDK de C#** con licencia Apache-2.0. Resuelve la firma, la validación de esquemas, el sobre, el registro de
idempotencia, las operaciones largas y el ocultamiento de secretos en los logs. El autor de un módulo escribe
solo las funciones de negocio, por ejemplo `Create(context, input)`. Todos los módulos de referencia se
escriben con ella.

**Pruebas de conformidad.** En v0 son tests del repo que hacen de núcleo falso y corren estos escenarios
contra cada módulo de referencia:

1. Manifiesto y esquemas válidos.
2. Rechazo de firma inválida y de reloj corrido.
3. Idempotencia: misma clave dos veces, un solo efecto y la misma respuesta. Misma clave con otra entrada da
   `idempotency_conflict`. El registro sobrevive a un reinicio.
4. Operación larga completa, y pérdida de la operación con repetición por la misma clave.
5. Errores con el formato y los códigos de la sección 10.
6. Campos desconocidos ignorados.
7. Secretos que no se filtran: un valor testigo en los campos `x-secret` no aparece en respuestas, errores ni
   logs.
8. Escenarios por tipo: webhook duplicado, falso y fuera de orden, reembolso parcial, emisión fiscal con corte
   y recuperación por `find`, y evaluación de fraude repetida con la misma clave sin una segunda consulta.

---

## 12. Módulos de referencia

| Módulo | Tipo | Para qué |
|---|---|---|
| `proxmox` | `provisioning` | VPS en Proxmox VE |
| `pterodactyl` | `provisioning` | Game servers en un panel Pterodactyl |
| `hestia` | `provisioning` | Hosting web en HestiaCP |
| `cpanel` | `provisioning` | Hosting en cPanel y WHM |
| `plesk` | `provisioning` | Hosting en Plesk |
| `directadmin` | `provisioning` | Hosting en DirectAdmin |
| `tacticalrmm` | `provisioning` | Clientes MSP con sus agentes de Tactical RMM |
| `mercadopago` | `payment` | Cobros con Mercado Pago, con suscripciones y webhooks |
| `paypal` | `payment` | Cobros del exterior |
| `cryptocloud` | `payment` | Cobros en cripto con CryptoCloud (Trybit), sin reembolsos ni suscripciones |
| `arca` | `fiscal` | Factura electrónica de ARCA (WSAA, WSFEv1 y WSFEX) |
| `manual` | `provisioning`, `registrar` | Los productos sin módulo, que el staff hace a mano, y los dominios sin API, como .ar. Viene con el núcleo |
| `minfraud` | `fraud` | Ejemplo del tipo: evaluación de pedidos con MaxMind minFraud ([fraude y acceso](fraude-y-acceso.md)) |
| `onetrial` | `extension` | Ejemplo de extensión: una sola prueba gratis por cuenta ([integraciones](../docs/integraciones/README.md)) |

Quedan por escribir un registrar con API y un módulo de FastDL como servicio hijo de un game server.

Los tres módulos de paneles comparten la [biblioteca de paneles de hosting](../src/BillingOss.Modules.Hosting/README.md):
las operaciones, la marca con el uuid del servicio (`context.service.uuid`), la consulta previa del dominio antes de
crear, las operaciones largas sin dueño y los errores. Cada uno suma su panel y la sección de migración en su README.

---

## 13. Fuera de v0

- Módulos de terceros: catálogo, firma de manifiestos, SDK en Go, TypeScript y PHP, y el kit de conformidad
  como herramienta pública.
- Llamadas del módulo al núcleo, para avisos espontáneos como una VM borrada a mano. En v0 lo cubre la
  reconciliación con `get_status`.
- Pago embebido en la página (SDK de la pasarela en el navegador). El cobro es por redirección, y el débito
  automático va por suscripción de la pasarela o con un medio guardado en la página de la pasarela
  (`payment.saved_methods`, sección 9.3). Cambiar el medio sin pagar una factura (`payment.method_setup`) y crear el
  aviso desde el asistente (`payment.webhook_setup`) quedan para una segunda etapa.
- Firma del login de WSAA en el núcleo, para que la clave privada no salga nunca del núcleo.
- mTLS entre núcleo y módulos.
- Avisarle a un servicio de fraude un fraude confirmado o un contracargo (`fraud.report`).
- CAEA de contingencia en ARCA (`fiscal.caea`).
- Registros DNS de otros tipos (SRV, CAA) y reenvío de URL desde el registrar.
