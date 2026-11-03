# Módulo de Pterodactyl

Módulo de aprovisionamiento (`provisioning`) de Hornero para game servers sobre un panel Pterodactyl. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md), sección 9.1. Usa solo la Application API pública del panel (`/api/application`), con `HttpClient` y `System.Text.Json`. No guarda configuración ni estado: el núcleo manda la instancia y el contexto en cada llamada.

Los requisitos son los PTR-01 a PTR-17 de la especificación del módulo.

Capacidades: `provisioning.core`, `provisioning.change_plan`, `provisioning.status`, `provisioning.view` y `provisioning.actions`. No declara `provisioning.sso`: el panel no tiene acceso directo y el cliente entra con su usuario.

## Configuración de la instancia

| Campo | Secreto | Qué es |
|---|---|---|
| `panel_url` | No | URL del panel, solo `https`, sin usuario, query ni fragmento. Por ejemplo `https://panel.example.com` |
| `application_key` | Sí | Clave `ptla_` de la Application API |

Permisos de la clave: `servers`, `users` y `allocations` en lectura y escritura, y `nodes`, `locations`, `nests` y `eggs` en lectura. La lista de hosts de la instancia en el núcleo lleva solo el host del panel.

Si falta un campo, la URL no es `https` o la clave tiene espacios o caracteres de control, la respuesta es `invalid_config` con el campo en `details.fields`, sin llamar al panel.

**Clave propia y de solo lectura durante staging y el paralelo.** Mientras WHMCS siga aprovisionando, Hornero usa una clave propia, distinta de la de WHMCS, para atribuir y revocar. Esa clave tiene solo permisos de lectura: así `get_status` y `view` funcionan y cualquier escritura que se escape del modo sombra del núcleo la rechaza el panel con 403 (`invalid_config`). Al corte se cambia por una con escritura.

## Cómo correrlo

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma que el núcleo tiene en la instalación de este módulo |
| `Module__PreviousSigningSecret` | Opcional. El secreto anterior, mientras dura una rotación |
| `Module__IdempotencyStorePath` | Registro de idempotencia de la SDK. Por defecto `data/idempotency.db`, relativo al directorio del módulo. Tiene que sobrevivir a los reinicios |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo y el módulo no crea el directorio. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `ASPNETCORE_URLS` | Dónde escucha |

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:Module__IdempotencyStorePath = ".\datos\pterodactyl\idempotency.db"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5193"
dotnet run --project src/modules/BillingOss.Module.Pterodactyl -c Release
```

Llamadas al panel: TLS 1.2 o superior con el certificado validado, sin redirecciones (llevarían la clave a otro destino), 10 segundos para conectar y 20 por llamada. Con los 5 segundos del módulo de hoy el alta daba timeout y se perdía la confirmación. La salida a internet necesaria es solo el host del panel por el puerto 443. El protocolo exige TLS entre el núcleo y el módulo salvo en el mismo host: en producción va un proxy con TLS delante.

### Varias réplicas

El módulo puede correr en varias réplicas detrás de un balanceador sin duplicar un server, un usuario ni un puerto:

- No guarda nada en memoria entre pedidos. La marca externa de la sección 6 del protocolo es el `external_id` del server en el panel: el uuid del servicio (`context.service.uuid`), que no cambia nunca y a diferencia del ID no choca con el de otra instalación que comparte el panel. Los servers importados, y los creados mientras el núcleo no mande el uuid, tienen el ID numérico del servicio. Por eso el server se busca siempre por `servers/external/{uuid}` y, si el panel contesta 404, por `servers/external/{service.id}`.
- La SDK evita repetir el efecto con la misma `Idempotency-Key` dentro de un registro. Si dos réplicas no comparten registro, igual converge: la segunda encuentra el server por su `external_id` y no crea otro.
- Si dos réplicas crean el mismo server al mismo tiempo, el panel rechaza el segundo `POST servers` con un 422 sobre `external_id`, que es único, y el módulo devuelve el que ya existe. Lo mismo con el usuario del panel, por su correo y su `external_id`.
- Los puertos adicionales se eligen en bloques que no se pisan, y cada servicio toma otro bloque según su ID. Dentro de una réplica, las altas multipuerto de un mismo panel van de a una (PTR-07). Entre réplicas no hay lock: si dos altas eligen el mismo bloque, el panel rechaza la segunda con un 422 sobre `allocation.*` y el módulo vuelve a elegir, hasta tres veces, y después contesta `conflict` reintentable. Como el panel controla que las allocations estén libres antes de su transacción, el alta relee el server después del `POST` y reemplaza un puerto que otra alta se haya quedado.
- `max_in_flight` es 2 por instancia. El panel comparte host con otros servicios y ya dio 504 por timeout.

Con varias réplicas, todas comparten el registro de idempotencia en PostgreSQL (`Module__IdempotencyConnectionString`). El archivo SQLite es para una sola réplica, en un volumen persistente: compartirlo por la red no sirve.

## Parámetros del producto (`product_schema`)

Llegan en `context.service.params`, con las opciones configurables ya fusionadas por el núcleo.

| Parámetro | Tipo | Obligatorio | Qué es |
|---|---|---|---|
| `nest_id`, `egg_id` | entero de 1 en adelante | Sí | Nest y egg del server |
| `location_id` | entero de 1 en adelante | Sí | Location donde se despliega |
| `memory_mb`, `disk_mb` | entero de 0 en adelante | Sí | RAM y disco. 0 es sin límite |
| `cpu_percent` | entero de 0 en adelante | Sí | 100 es un núcleo |
| `swap_mb` | entero de 0 en adelante | No | Por defecto 0 |
| `io_weight` | entero de 10 a 1000 | No | Por defecto 500 |
| `oom_disabled` | booleano | No | Por defecto falso |
| `databases`, `backups`, `allocation_limit` | entero de 0 en adelante | No | Límites del cliente. Por defecto 0 |
| `server_name` | texto de hasta 191 | No | Nombre del server. Sin él va `Servicio {id}` |
| `startup` | texto | No | Comando de inicio fijo. Sin él va el del egg |
| `variables` | objeto | No | Valores fijos de variables del egg, por `env_variable` o por nombre visible. Texto, número o booleano |
| `extra_ports` | objeto | No | Puertos adicionales del alta (ver abajo) |
| `order_fields` | lista | No | Datos que completa el cliente al contratar (ver abajo) |

Un opcional vacío vale 0, como hoy, y el 0 se manda tal cual al panel. `databases` siempre va como entero: en el panel un `null` es sin límite, y como el alta de hoy mandaba `null` y el cambio de plan mandaba 0, la comparación del paquete daba distinta.

Una variable del egg se busca en `variables` primero por su `env_variable` y después por su nombre visible. Así el importador no necesita leer los eggs del panel.

### Puertos adicionales (`extra_ports`)

```json
{ "count": 3, "contiguous": true, "variables": { "QUERY_PORT": 1, "RCON_PORT": 3 } }
```

Un solo camino para DayZ y los multipuerto genéricos (PTR-07):

- `count` es cuántos puertos se suman al principal, de 1 a 20. Todos en la misma IP del mismo nodo.
- `contiguous`: con verdadero, un bloque de `count + 1` puertos seguidos. Si no, el principal y los siguientes libres.
- `variables` lleva el desplazamiento de cada variable del egg. Ordenados por puerto, el principal es el 0 y la variable con desplazamiento k toma el puerto k. En un bloque contiguo eso es el principal más k.
- Los puertos se eligen antes del alta, en el primer nodo de la location que tenga lugar según `nodes/deployable`. Sin nodo con lugar el panel contesta 400 y el alta es `rejected` con `no_deployable_node`. Sin puertos libres, `rejected` con `no_free_ports`. En los dos casos no se crea nada.
- Los bloques candidatos no se pisan: cada uno empieza después de donde termina el anterior. Con la misma lista de libres, dos altas eligen el mismo bloque o bloques sin puertos en común.
- El tope de allocations del server es al menos `count + 1`, como hoy en DayZ.
- Un alta repetida sobre un server que ya existe controla que tenga sus puertos y las variables puestas, y completa lo que falte con `PATCH build` y `PATCH startup`. Una variable que ya apunta a un puerto propio se respeta.
- El `PATCH build` del panel suma solo las allocations libres y saltea las demás sin error. Antes de escribir las variables, el módulo controla que el server tenga todas las que pidió. Si falta alguna es `conflict` con `ports_taken`, sin tocar las variables, y el reintento vuelve a elegir entre las libres.

| Producto | `extra_ports` |
|---|---|
| DayZ (pid 201) | `{ "count": 3, "contiguous": true, "variables": { "QUERY_PORT": 1, "RCON_PORT": 3 } }` |
| Rust (pid 12) | `{ "count": 3, "variables": { "QUERY_PORT": 1, "RCON_PORT": 2, "APP_PORT": 3 } }` |
| Project Zomboid (pid 37) | `{ "count": 1, "variables": { "STEAM_PORT": 1 } }` |
| ARK Survival Ascended (pid 54) | `{ "count": 2, "variables": { "QUERY_PORT": 1, "RCON_PORT": 2 } }` |
| Assetto Corsa (pid 140) | `{ "count": 1, "variables": { "HTTP_PORT": 1 } }` |
| Deceive Inc. (pid 199) | `{ "count": 1, "variables": { "QUERY_PORT": 1 } }` |

Los desplazamientos de los cinco genéricos siguen el orden de sus variables de puerto en la configuración de hoy. Hay que confirmarlos producto por producto al importar.

### Datos del pedido (`order_fields` y `order_schema`)

El protocolo tiene un solo `order_schema` por módulo, y acá los datos cambian por producto. Por eso el esquema es genérico y cada producto declara sus campos:

```json
"order_schema": { "type": "object", "properties": { "variables": { "type": "object", "additionalProperties": { "type": "string", "maxLength": 1024 } } } }

"order_fields": [
  { "key": "FIVEM_LICENSE", "label": { "es": "Licencia de FiveM", "en": "FiveM license" }, "required": true, "secret": true },
  { "key": "egg_id", "label": { "es": "Versión", "en": "Version" }, "options": ["128", "133"] }
]
```

- `key` es una `env_variable`, un nombre visible del egg, o `egg_id` para que el cliente elija el egg dentro del nest (Terraria).
- El núcleo arma el formulario de cada producto con `order_fields`, guarda cifrados los que tienen `secret` y los manda en `context.service.order.variables`.
- El módulo usa solo los campos que declara el producto. Al crear, un valor fuera de `options` o un obligatorio que falta es `invalid_input`: así un pedido armado a mano no elige un egg o un valor que el producto no ofrece.
- Los datos del pedido se aplican al crear. El cambio de plan no los vuelve a aplicar, para no pisar lo que el cliente cambió en el panel. `egg_id` es la excepción: vale siempre, porque el cliente no puede cambiar el egg desde el panel.
- Las opciones se controlan solo al crear el server. `change_plan`, `get_status` y un alta repetida sobre un server que ya existe aceptan lo guardado aunque el producto ya no lo ofrezca: se validó al contratar, y sacar una opción no puede trabar a los servicios que la eligieron.
- El módulo nunca registra los valores, ni los devuelve en errores.

## Contexto que usa

| Campo | Para qué |
|---|---|
| `context.service.uuid` | `external_id` de los servers nuevos. Un valor que no es un uuid da `invalid_input` sin llamar al panel |
| `context.service.id` | `external_id` de los servers importados o creados sin uuid, y el nombre por defecto |
| `context.service.params` | Parámetros del producto |
| `context.service.order.variables` | Datos del pedido |
| `context.service.external_ids` | `address` e `identifier` para la vista cuando el panel no responde |
| `context.account.id` | `external_id` del usuario del panel |
| `context.account.name` y `email` | Alta del usuario del panel. Por eso `data_scopes` es `account.contact` |
| `context.account.language` | Idioma de la vista: inglés si empieza con `en`, castellano en otro caso |

## Operaciones

Todas son `POST /v0/provisioning/{operación}` con el sobre del protocolo. Las que cambian algo exigen `Idempotency-Key`: `create`, `suspend`, `unsuspend`, `terminate`, `change_plan` y `run_action`. `validate`, `get_status` y `view` no la llevan.

Ninguna operación toma un error del panel como ausencia del server: solo el 404 del propio panel lo es, con `errors[].code` igual a `NotFoundHttpException`. Así quedó la corrección del 2026-09-09. Un 404 sin ese cuerpo lo da otro (el proxy, un vhost por defecto o una `panel_url` con otro prefijo) y es `upstream_unavailable`.

### validate

Sin efectos. Valida los parámetros y los datos del pedido (obligatorios y opciones), lee el egg y controla que cada campo de `order_fields` y cada variable de `extra_ports` exista en el egg. Responde `{}` o `invalid_input` con los campos.

### create

1. La consulta previa: busca el server por su marca, primero el uuid y después el ID (ver arriba). Si existe y su dueño es el usuario del panel de la cuenta, no crea nada y converge: completa los puertos adicionales y devuelve sus IDs y la dirección. El módulo de hoy devolvía éxito sin la dirección y quedaron 14 servicios vacíos. Si el dueño es otro, es `rejected` con `external_id_taken` y no se adopta: es un server ajeno con el mismo ID, por ejemplo uno que WHMCS creó durante el paralelo, y darlo por propio le mostraría al cliente el server de otro y una baja lo borraría.
2. Controla los datos del pedido (obligatorios y opciones). Solo acá: un server que ya existe converge aunque el producto ya no ofrezca lo que se eligió.
3. Busca el usuario del panel por `users/external/{account.id}` y después por correo exacto (el filtro del panel es parcial). Un usuario con ese correo y el `external_id` de otra cuenta no se adopta: es `rejected` con `email_of_other_account`, porque la cuenta vieja conserva el correo en el panel y el server nuevo le quedaría a otro cliente con las variables secretas del pedido a la vista. Si no hay usuario, lo crea con un `username` al azar de 8 caracteres, el correo, el nombre partido en nombre y apellido (con una sola palabra el apellido la repite) y `external_id` igual al ID de la cuenta, sin contraseña: el panel le manda al cliente el enlace para definirla.
4. Lee el egg con sus variables y arma el `environment`: primero los datos del pedido, después `variables` de los parámetros y por último el valor por defecto del egg.
5. Si hay `extra_ports`, elige los puertos.
6. `POST servers` con `external_id`, nombre, usuario, egg, la primera imagen de `docker_images` del egg, el startup, límites, `feature_limits`, `oom_disabled`, `start_on_completion` verdadero y `deploy` en la location (o la allocation explícita si hay puertos adicionales).
7. Con puertos adicionales, relee el server y completa los que otra alta se haya quedado.

El alta termina apenas existe el server, también mientras el panel instala, como pide PTR-06: `completed` con `observed_state` y los seis `external_ids`. La instalación se ve en `view` (Instalando o La instalación falló) y `get_status` da `active` mientras instala. No hay operación larga: antes el alta quedaba `pending` con `create-{server_id}`, la SDK guardaba ese `pending` como respuesta de la clave y, si el staff borraba el server, la consulta daba `not_found` y la repetición con la misma clave devolvía la misma operación muerta hasta agotar los intentos. Y si la instalación no terminaba nunca, el núcleo consultaba sin plazo y el resto de los trabajos del servicio esperaban detrás.

Un alta repetida que encuentra el server con la instalación fallida (`install_failed`) es `rejected` con `details.upstream.reason` igual a `install_failed`: lo ve el staff, que reinstala desde el admin del panel y reintenta con otra generación. Un alta sin la allocation principal no termina: si la clave no puede leer allocations el panel devuelve `null_resource` sin dar error, y eso es `invalid_config` con `details.missing` igual a `["allocations"]`.

| Caso | Respuesta |
|---|---|
| Panel sin nodo con lugar (400 en el `POST` o en `nodes/deployable`) | `rejected` con `no_deployable_node` |
| Sin puertos libres | `rejected` con `no_free_ports` |
| Puertos tomados por otras altas en tres intentos, o salteados por el `PATCH build` | `conflict` con `ports_taken`, reintentable |
| Server con la marca del servicio y otro dueño | `rejected` con `external_id_taken` |
| Correo de la cuenta en un usuario de otra cuenta | `rejected` con `email_of_other_account` |
| El panel rechaza los datos (422) | `invalid_input` con los campos del panel en `details.fields` |
| Corte después del `POST` | `outcome_unknown`. El reintento con la misma clave encuentra el server y converge |

### suspend y unsuspend

Leen el server. Si ya está en el estado pedido no escriben. Si no, `POST servers/{id}/suspend` o `unsuspend`. Responden `observed_state` (`suspended` o `active`) y los `external_ids`. Sin server, `not_found`.

### terminate

Sin server, ya convergió. Si existe, `DELETE servers/{id}`, y tanto 204 como 404 cuentan como borrado. Responde `observed_state` igual a `terminated`. No usa el borrado forzado: con el nodo caído el panel da 5xx, que sale como `upstream_unavailable`, y forzarlo lo decide el staff. El borrado es irreversible y lo usa el reaper del tier gratis, así que el tope de bajas por corrida lo pone el núcleo.

### change_plan

Arma el paquete deseado y lo compara con el server:

- Límites, `oom_disabled`, `databases`, `allocation_limit`, `backups`, nest y egg de los parámetros. El egg es el del pedido si el producto lo deja elegir, aunque ya no esté en sus opciones.
- Variables: `variables` de los parámetros, si no el valor actual del server, y si no el del egg. Los datos del pedido no se vuelven a aplicar.
- Con otro egg va su startup, y la imagen actual si el egg nuevo la ofrece o su imagen por defecto si no. Con el mismo egg se conservan el startup y la imagen. Un `startup` en los parámetros manda siempre.
- Startup, imagen y variables van recortados, y vacío y `null` son iguales, como los guarda el panel (su middleware recorta todo texto de entrada). Si no, un egg con un espacio al final daba `not_applied` en cada cambio y `package_matches` falso para siempre.

Si ya coincide no escribe. Si no, primero `PATCH startup` y después `PATCH build` (con la allocation principal actual). El startup va primero porque es lo que el panel valida contra las reglas del egg: si lo rechaza, el error es `invalid_input` y los límites quedan como estaban, en vez de un server a medias con el error guardado para la clave. Si el `PATCH startup` queda incierto, decide la relectura. Si después del cambio algo sigue distinto, es `rejected` con `not_applied` y los nombres en `details.differences`. No reinstala, no reinicia y no toca los puertos adicionales: el cliente reinstala desde el panel si cambió el tipo.

La location del server es la de su nodo (`GET nodes/{id}`). Si no es la del plan, por ejemplo con la opción Protección DDoS, lo demás se aplica igual y la respuesta es `rejected` con `requires_transfer` y `location_id` en `details.differences`: la Application API no traslada servers, lo hace el staff desde el panel y después repite el cambio con otra generación.

### get_status

```json
{
  "observed_state": "active",
  "external_ids": { "server_id": "312", "identifier": "a1b2c3d4", "user_id": "57", "node_id": "1", "allocation_id": "1044", "address": "mc.example.com:25570" },
  "package_matches": false,
  "differences": ["memory_mb", "variables.MC_VERSION"]
}
```

- `observed_state`: `suspended`, `active` (también mientras instala), `unknown` si la instalación falló, o `not_found` si el panel da su 404. Un 5xx, un corte, un 404 que no es del panel o una respuesta malformada son un error reintentable, nunca `not_found`.
- `differences` lista solo nombres, nunca valores, porque pueden ser secretos. Incluye `location_id` si el nodo del server está en otra location, y `egg_id` si el egg del pedido no es un ID o el panel no lo tiene.
- Sin parámetros completos, como un servicio recién importado, devuelve el estado y los IDs sin `package_matches`. Así sirve para adoptar los servicios importados.

### view

Bloques de texto plano, sin HTML:

```json
{
  "blocks": [
    { "type": "status", "level": "ok", "text": "Activo" },
    { "type": "fields", "items": [ { "label": "Dirección", "value": "mc.example.com:25570" } ] },
    { "type": "link", "label": "Ir al panel", "url": "https://panel.example.com/server/a1b2c3d4" }
  ]
}
```

- `status`: `ok` activo, `warning` suspendido o instalando, `error` si la instalación falló.
- La dirección sale de la lectura en vivo y, con el panel caído, de `external_ids.address`.
- `link` va a `/server/{identifier}` del panel, o a la raíz si no hay server.
- Con el panel caído no es un error: va un `notice` de nivel `warning` y la dirección guardada. Sin server, un `notice` de nivel `error`.

### run_action

Una sola acción, `set_panel_password`, para el staff y con confirmación (PTR-13):

```json
{ "action": "set_panel_password", "credentials": { "panel_password": "<la genera el núcleo>" } }
```

Lee el usuario dueño del server y hace `PATCH users/{id}` con sus datos y la contraseña nueva, porque el panel pide el usuario completo en cada cambio. El usuario es uno por cliente: la contraseña nueva vale para todos sus servers. Responde `{ "user_id": "57" }`. Otra acción es `not_supported`, y sin la credencial, de 8 a 191 caracteres, es `invalid_input`.

Si el dueño del server es un administrador del panel (`root_admin`, por ejemplo porque el staff se lo asignó para revisarlo) o tiene el `external_id` de otra cuenta, no se escribe nada y es `rejected` con `foreign_user`: la contraseña nueva la ve el cliente.

En el manifiesto la acción declara `"credentials": ["panel_password"]`, el campo por acción de la sección 5 del protocolo: le dice al núcleo qué credencial generar, guardar cifrada antes de llamar y mandar en `input.credentials`. Antes era `rotates_credentials`, que el protocolo no tiene, y un núcleo que lo siguiera llamaba la acción sin la contraseña.

### instance/test

Solo lecturas, nunca escrituras: `nodes`, las allocations del primer nodo, `users`, `locations`, `nests`, los eggs del primer nest y `servers`. Las allocations se prueban porque sin ese permiso el alta del 2026-06-30 devolvía `null_resource` sin dar error.

- Responde `{ "nodes": 2 }`.
- Un 403 es un permiso que falta: sigue con las demás y al final da `invalid_config` con `details.missing`, por ejemplo `["allocations"]`.
- Un 401 es la clave rechazada y corta ahí.
- Un panel sin nodos es `invalid_config`.

## IDs externos

| Clave | De dónde sale |
|---|---|
| `server_id` | `id` del server |
| `identifier` | `identifier` del server, el de la URL del panel |
| `user_id` | Usuario dueño del server |
| `node_id` | Nodo del server |
| `allocation_id` | Allocation principal |
| `address` | Dirección de la allocation principal: el alias y el puerto si la IP es `0.0.0.0`, y si no la IP y el puerto |

Todos son textos. Vuelven en `create` (al terminar), `suspend`, `unsuspend`, `change_plan` y `get_status`, y el núcleo los fusiona en el servicio. `terminate` no los devuelve: el núcleo conserva los anteriores como historial.

## Credenciales

`credentials` del manifiesto está vacío: el usuario del panel se crea sin contraseña y el panel le manda al cliente el correo para definirla, como hoy. La única credencial que genera el núcleo es `panel_password` de la acción `set_panel_password`. Ver las decisiones pendientes.

## Errores

| Panel | Código del protocolo |
|---|---|
| 401 y 403 | `invalid_config`, con `application_key` en `details.fields` |
| Allocations en `null_resource` (la clave no puede leerlas) | `invalid_config`, con `details.missing` igual a `["allocations"]` |
| 400 en el alta o en `nodes/deployable` (sin nodo) | `rejected` con `details.upstream.reason` igual a `no_deployable_node` |
| Sin puertos libres | `rejected` con `no_free_ports` |
| 404 con `NotFoundHttpException` | El recurso no existe: por ejemplo `not_found` en `get_status` |
| 404 sin el error del panel | `upstream_unavailable`: lo dio el proxy o un vhost, y el pedido no llegó al panel |
| 422 | `invalid_input` con `meta.source_field` de cada error en `details.fields` |
| 429 | `rate_limited`, con `retry_after` del encabezado `Retry-After` o 60 segundos |
| 409 | `conflict` reintentable, por ejemplo un server en transferencia |
| 500 y 503 | `upstream_unavailable` |
| 502 y 504 con `DaemonConnectionException` | `upstream_unavailable`: Wings no respondió y el panel no aplicó el cambio o lo deshizo |
| Otros 502 y 504 en una escritura | `outcome_unknown`: los da el proxy cuando el panel tarda, y el pedido pudo haberse ejecutado |
| 502 y 504 en una lectura | `upstream_unavailable` |
| Sin conexión antes de enviar | `upstream_unavailable` |
| Corte o plazo vencido después de enviar | `outcome_unknown` en escrituras y `upstream_unavailable` en lecturas |
| Respuesta que no es JSON o le falta un campo | `internal`, reintentable |
| Redirección | `invalid_config` con `panel_url` |
| Otros 4xx | `rejected` con el código HTTP y los códigos del panel en `details.upstream` |

Un 502 o 504 en una escritura sin `DaemonConnectionException` es la única diferencia con PTR-14, que los daba como `upstream_unavailable`: el código de `upstream_unavailable` dice que el pedido no llegó a ejecutarse, y con un proxy delante no se sabe. Las dos son reintentables y el reintento converge igual. Con `DaemonConnectionException` sí se sabe, porque lo arma el panel: el alta se borra, la suspensión se revierte y la baja no borra nada sin el forzado.

`message` es para el staff. Lleva el `detail` del panel salvo en los 422, y la clave se tapa si el panel la repite.

## Logs y secretos

El módulo registra solo método, ruta sin query, código y duración de cada llamada al panel. Nunca registra cuerpos: llevan variables secretas, y la query de la búsqueda de usuarios lleva el correo. El log del módulo de hoy capturó una contraseña de ARK en texto plano. Con esto la guarda de DayZ que exigía el debug apagado deja de hacer falta.

## Pruebas

```
dotnet test tests/BillingOss.Module.Pterodactyl.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y reemplazan el panel por uno falso con estado (usuarios, nodos, allocations, eggs y servers) que contesta con las formas de la Application API pública y se comporta como el panel: `external_id` único, allocations libres en el alta, `PATCH build` que saltea sin error las allocations ocupadas, 400 en `nodes/deployable` sin nodo con lugar, `null_resource` sin permiso de allocations, textos recortados y usuario completo en cada cambio. Cubren el manifiesto, la firma, los campos desconocidos, la idempotencia, el alta que termina mientras instala, cada fila de la tabla de errores, que la clave y los datos secretos del pedido no aparecen en respuestas, errores ni logs, la convergencia con otro registro de idempotencia, el server y el usuario de otro cliente, las opciones que ya no se ofrecen, la location distinta y las carreras entre réplicas por los puertos.

### Prueba contra un panel de laboratorio

Nunca contra el panel de producción. Sin las variables, la prueba queda inconclusa.

```
export PTERODACTYL_TEST_URL=https://panel-lab.example.com
export PTERODACTYL_TEST_KEY=la-clave-de-la-application-api-del-laboratorio
dotnet test tests/BillingOss.Module.Pterodactyl.Tests --filter TestCategory=PterodactylReal
```

Con esas dos corre solo `instance/test`, que lee. Con `PTERODACTYL_TEST_PLAN` (los parámetros del producto en JSON) y `PTERODACTYL_TEST_EMAIL` (un correo de prueba propio, al que el panel manda el enlace del usuario) además crea un server con un ID de servicio alto y al azar, espera la instalación, repite el alta, lo suspende, lo reactiva, consulta su estado y su vista, y lo borra.

## Pendiente de comprobar en el panel de laboratorio

Lo que el módulo de hoy no usa o no prueba, y la documentación pública no alcanza para confirmar:

- El campo `status` del server (`installing`, `install_failed`, `suspended`) y el `suspended` viejo, que el módulo usa como respaldo.
- Que suspender un server ya suspendido sea inocuo. El módulo igual no escribe si ya está en el estado pedido.
- Que `servers.external_id` sea único: es lo que resuelve dos altas simultáneas del mismo servicio. Si no lo es, dos réplicas sin registro compartido podrían crear dos servers en una carrera muy corta.
- Que el panel rechace con 422 sobre `allocation.*` una allocation ya asignada en el alta, y que `PATCH build` saltee sin error las ocupadas. El módulo cuenta con las dos cosas.
- El cuerpo de los 404 del panel (`NotFoundHttpException` en `errors[].code`), el 400 de `nodes/deployable` sin nodo con lugar, el `null_resource` de las allocations sin permiso y el 504 con `DaemonConnectionException` con Wings apagado. El módulo cuenta con esas formas.
- Qué significa `feature_limits.databases` en `null`, el formato de los 422 y el límite de pedidos por minuto.
- El `include=allocations` en `POST servers`, `PATCH build` y `PATCH startup`.
- Si el panel queda accesible desde la VM de Hornero por su nombre público con un certificado válido. Si hace falta una URL interna, hay que sumar a `config_schema` la huella del certificado.

## Lo que falta en el núcleo

El módulo cumple el contrato. Para usarlo entero, el núcleo tiene que sumar:

- `run_action` con control de `audience`, confirmación, clave por acción y generación, y las credenciales del `credentials` de la acción.
- `change_plan`, `get_status` (con la reconciliación y la fusión de `external_ids`) y `view` con su caché de un minuto. Hoy solo llama `create`, `suspend`, `unsuspend` y `terminate`.
- Las opciones configurables fusionadas en los parámetros del servicio (PTR-03) y los datos del pedido por producto con sus secretos cifrados (PTR-04).
- El modo sombra (PTR-16): mientras WHMCS aprovisione, ningún trabajo con efecto sobre servicios de esta instancia, y cada uno registrado como omitido.
- La tarea del staff ante los `rejected` del módulo que piden una persona: `external_id_taken`, `email_of_other_account`, `foreign_user`, `requires_transfer` e `install_failed`.
- Si las altas multipuerto simultáneas en varias réplicas se vuelven comunes, un lease por instancia para hacerlas de a una entre réplicas. Hoy van de a una solo dentro de cada réplica y el resto lo cubren los bloques que no se pisan, el 422 del panel y la relectura.
