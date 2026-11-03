# Módulo de Tactical RMM

Módulo de aprovisionamiento (`provisioning`) de Hornero para Tactical RMM: un servicio es un cliente MSP con sus agentes RMM. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md), sección 9.1. Llama a la API REST de Tactical RMM con `HttpClient` y `System.Text.Json`, autenticado con el encabezado `X-API-KEY`. No guarda configuración ni estado: el núcleo manda la de la instancia en cada llamada y todo lo demás sale de Tactical.

Capacidades: `provisioning.core`, `provisioning.status`, `provisioning.view`, `provisioning.change_plan` y `provisioning.actions`. No declara `provisioning.sso`: el panel de Tactical está cerrado a las IPs de administración y se entra por SSO.

Lo que hace en Tactical, y nada más:

- Crea un cliente con un sitio por servicio, y lo borra en la baja.
- Prende y apaga el modo mantenimiento de los agentes del cliente para suspender y reactivar.
- Lee clientes y agentes para el estado, la vista y los controles de baja y cambio de plan.
- Crea links de instalación del agente (deployments) con la acción `installer_link`.
- Desinstala los agentes del cliente solo en una baja forzada por el staff.

Nunca crea, cambia ni borra usuarios del panel ni roles (`accounts/users/` y `accounts/roles/`). Por eso `credentials` va vacío en el manifiesto: no hay contraseñas que generar. Si alguna vez se le da panel a un cliente, se sigue la receta manual de acceso acotado del despliegue de Tactical, fuera del módulo.

## Configuración de la instancia

| Campo | Secreto | Por defecto | Qué es |
|---|---|---|---|
| `api_url` | No | | Origen de la API de Tactical, por ejemplo `https://api.ejemplo.com`. Puede llevar una ruta base. Tiene que ser `https`, sin usuario, query ni fragmento |
| `api_key` | Sí | | API key de un usuario propio del módulo (ver abajo). Sin espacios ni caracteres de control |
| `client_name_prefix` | No | | Obligatorio. Prefijo del nombre de cada cliente en Tactical. De 1 a 16 letras, dígitos, `_` o `-`. Cada instalación de Hornero que use el mismo Tactical lleva el suyo (nunca el mismo en staging y en producción): comparten IDs de servicio, y con el mismo prefijo una adoptaría los clientes de la otra |
| `site_name` | No | `OficinaPrincipal` | Nombre del sitio que se crea con cada cliente. De 1 a 64 letras, dígitos, espacios, `_` o `-` |
| `deploy_expiry_days` | No | `7` | Días de vigencia de los links de instalación, de 1 a 30 |

Lo que no cumple da `invalid_config` con los campos en `details.fields`, nunca con los valores, y sin llamar a Tactical. Como la API key viaja a `api_url`, cambiar ese campo exige volver a cargar la key (sección 4 del protocolo). Las redirecciones no se siguen, para que la key no viaje a otro destino, y el certificado TLS siempre se valida.

**Hosts para el navegador.** La lista de hosts de la instancia lleva solo el host de `api_url`: es el de los links de instalación (`https://<host>/clients/<uid>/deploy/`), la única ruta de Tactical que tiene que ser pública.

### Usuario, rol y red en Tactical

El módulo usa la key de un usuario propio, sin contraseña, con el panel bloqueado y con un rol de mínimo privilegio, sin restricción de clientes ni sitios. El listado de agentes y el modo mantenimiento filtran por rol y el borrado del cliente no: con restricción, la suspensión y la baja forzada pueden no ver agentes que Tactical igual cuenta, y los clientes de los servicios migrados quedan fuera de vista.

| Permiso | Para qué |
|---|---|
| `can_list_clients`, `can_manage_clients` | Buscar, crear y borrar clientes. El sitio se crea con el cliente |
| `can_list_agents` | Contar agentes y ver su modo mantenimiento |
| `can_edit_agent` | `agents/maintenance/bulk/`, para suspender y reactivar |
| `can_list_deployments`, `can_manage_deployments` | Para la acción `installer_link` y para borrar los links al suspender. Sin ellos la vista sale sin link ni acción y la suspensión no los borra |
| `can_uninstall_agents` | Solo para la baja forzada. Sin él la baja forzada da `invalid_config` |

No lleva ninguno de los permisos peligrosos del endurecimiento (`can_manage_accounts`, `can_manage_roles`, `can_manage_scripts` y similares) ni `can_list_accounts`, que deja leer el secreto del SSO. La key se guarda en una bóveda y de ahí va a la instancia como campo secreto. Si el monitoreo avisa de altas de usuarios y cambios de rol en Tactical, conviene coordinarlo antes.

Si la API administrativa de Tactical solo responde a ciertas IPs, por ejemplo en el proxy, hay que sumar la del host donde corre el módulo. Sin eso, la prueba de la instancia da `invalid_config` con `details.cause` igual a `ip_no_permitida`.

## Parámetros del producto y del pedido

| Campo | Obligatorio | Qué es |
|---|---|---|
| `agent_limit` | Sí | Agentes contratados, entero de 1 en adelante. Tactical no tiene cupo por cliente: el módulo lo muestra en la vista, lo informa en `get_status` y lo controla al bajar de plan |

No hay `order_schema` ni `data_scopes`: el nombre del cliente en Tactical es el prefijo más el ID del servicio, sin datos personales.

## Cómo correrlo

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma que el núcleo tiene en la instalación de este módulo |
| `Module__PreviousSigningSecret` | Opcional. El secreto anterior, mientras dura una rotación |
| `Module__IdempotencyStorePath` | Archivo SQLite del registro de idempotencia de esta réplica. Por defecto `data/idempotency.db`. Tiene que sobrevivir a los reinicios |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo y el módulo no crea el directorio. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `ASPNETCORE_URLS` | Dónde escucha |

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:Module__IdempotencyStorePath = ".\datos\tacticalrmm\idempotency.db"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5193"
dotnet run --project src/modules/BillingOss.Module.TacticalRmm -c Release
```

El protocolo exige TLS entre el núcleo y el módulo, salvo en el mismo host. La salida a internet necesaria es solo el host de `api_url` por el puerto 443. Cada pedido a Tactical tiene 15 segundos de plazo y 5 para conectar.

## Operaciones

Todas son `POST /v0/provisioning/{operación}` con el sobre del protocolo. Las que cambian algo (`create`, `suspend`, `unsuspend`, `terminate`, `change_plan` y `run_action`) exigen `Idempotency-Key`.

Del contexto usa `service.id`, `service.status`, `service.external_ids` (`client_id`, `site_id` y `client_name`), `service.params.agent_limit` y `account.language` (`en` para inglés, el resto en castellano). Las operaciones sobre un servicio existente sin `external_ids.client_id` dan `invalid_input` con ese campo y no llaman a Tactical.

Antes de cambiar o mostrar algo, `suspend`, `unsuspend`, `terminate`, `change_plan`, `get_status` y `view` controlan que el cliente de `client_id` se llame como `external_ids.client_name` o, si no viene, como la marca del alta. Si no, dan `invalid_input` con `external_ids.client_id` y `details.reason` igual a `client_mismatch`, sin tocar nada: un ID viejo o mal cargado puede apuntar al cliente de otra empresa. Por eso el importador y la vinculación manual cargan `client_name` con el nombre que tiene el cliente en Tactical. `view` y `run_action` controlan además que `external_ids.site_id` sea un sitio de ese cliente. Si no, dan `invalid_input` con `external_ids.site_id` y `details.reason` igual a `site_mismatch`, sin mostrar ni crear links: un sitio mal importado daría el link de instalación de otra empresa.

### validate

Sin efectos. Controla `agent_limit` (de `input.params` si viene, si no de `service.params`) y, si el servicio ya tiene ID, que no haya en Tactical otro cliente con el nombre que usaría el alta. Si lo hay y no es el `client_id` del servicio, da `invalid_config` con `details.cause` igual a `name_taken` y su ID en `details.client_id`. Suele ser de un alta anterior de este servicio que terminó en error. Hay que revisar ese cliente y, si es del servicio, cargar su ID en `external_ids.client_id` o reintentar el alta, que lo reutiliza. Cambiar el prefijo lo dejaría huérfano. Respuesta: `{ "valid": true }`.

### create

1. Busca en `GET /clients/` un cliente llamado exactamente `<prefijo><ID del servicio>`, por ejemplo `BOSS-1042`. Ese nombre es la marca del alta (sección 6, punto 4 del protocolo): si existe, es de un intento anterior y se reutiliza.
2. Si no existe, `POST /clients/` con el cliente, el sitio de `site_name` y `custom_fields` vacío.
3. Vuelve a buscar el cliente. El éxito se confirma así y no por el texto de la respuesta.

```json
{ "observed_state": "active", "external_ids": { "client_id": "118", "site_id": "119", "client_name": "BOSS-1042" } }
```

- Los IDs van como texto. `client_name` es el nombre leído de Tactical, con el que las operaciones siguientes controlan el cliente.
- Si el alta se corta después de enviar, la respuesta es `outcome_unknown` y el reintento con la misma clave encuentra la marca.
- Tactical no repite nombres de cliente. Si dos réplicas dan el alta a la vez, la segunda recibe un 400 por nombre repetido, busca y reutiliza el cliente de la primera.
- Un cliente cargado a mano se reutiliza solo si se llama exactamente igual que la marca. Ese riesgo se acepta: la marca no tiene datos personales y el prefijo es de la instancia.
- Un 400 de Tactical por otra causa da `rejected` con los códigos en `details.upstream`.

### suspend y unsuspend

Leen el cliente (`GET /clients/{id}/`, para dar `not_found` si no existe, porque Tactical contesta bien aunque no exista, y para controlar que sea el del servicio) y llaman a `POST /agents/maintenance/bulk/` con `{ "type": "Client", "id": 118, "action": true }`, o `false` para reactivar. Al suspender borran además los links de instalación de los sitios del cliente (`DELETE /clients/deployments/{id}/`), tolerando el 404. Sin permiso de deployments no los borran y queda en el log. Repetirlas no cambia nada. Respuesta: `{ "observed_state": "suspended" }` o `active`.

Límites conocidos, para decidir qué significa suspender un servicio gestionado:

- El modo mantenimiento es por agente: uno que se instala durante la suspensión entra sin mantenimiento. Por eso la suspensión borra los links ya entregados y la acción `installer_link` no da links con el servicio suspendido.
- Solo apaga las alertas. No frena scripts, parches ni el acceso remoto del staff.
- Con 0 agentes la suspensión no se puede observar (ver `get_status`).

### get_status

Estado observado para la reconciliación: `not_found` si el cliente no existe, `suspended` si hay al menos un agente y todos están en mantenimiento, y `active` en cualquier otro caso.

```json
{
  "observed_state": "active",
  "details": { "agents": 3, "agents_in_maintenance": 0, "agent_limit": 5, "agent_limit_exceeded": false }
}
```

Con 0 agentes da `active` con `agents` en 0, aunque el servicio esté suspendido. El núcleo tiene que tomar eso como coherente con un estado deseado `suspended`.

Los agentes salen de `GET /agents/?client={id}` y se filtran además por `client_name`, por si el servidor no aplicara el filtro: así nunca se cuentan, ni se desinstalan en una baja forzada, agentes de otro cliente.

### change_plan

Tactical no tiene cupo de agentes por cliente, así que el cambio de plan no lo toca. Toma el `agent_limit` nuevo de `input.params` (o de `service.params` si el núcleo ya los actualizó), cuenta los agentes y:

- Si el límite nuevo queda por debajo de los agentes instalados, da `invalid_input` con `params.agent_limit` en `details.fields` y la cantidad en `details.agents`. Es una regla del módulo, no de Tactical, por eso no es `rejected`.
- Si no, `{ "agents": 7, "agent_limit": 10 }`.

### terminate

Borra el cliente con `DELETE /clients/{id}/`, que en Tactical se lleva sus sitios. No toca usuarios ni roles.

- Un cliente que ya no existe es una baja hecha: repetirla da `completed`.
- Con agentes instalados y sin `input.force`, da `invalid_input` con `force` en `details.fields` y la cantidad en `details.agents`, y no borra nada. Tactical tampoco borraría el cliente sin mover los agentes a otro sitio, y los equipos quedarían con el agente instalado y sin gestión.
- Con `{ "force": true }`, pedido por el staff, desinstala cada agente con `DELETE /agents/{agent_id}/` (Tactical lo saca de su base en el momento y le manda la orden de desinstalarse) y después borra el cliente.
- Una baja, y sobre todo una baja forzada, cambia la cantidad de agentes de Tactical que vigila Zabbix: antes se coordina con Monitoreo y después se ajusta `{$TRMM.AGENTES}` en Zabbix.

Respuesta: `{ "observed_state": "terminated" }`.

**Operación larga.** Un paso de la baja forzada desinstala agentes durante 30 segundos como máximo, y siempre al menos uno. Si quedan, responde `pending` con `operation_id` igual a `terminate-<client_id>-<inicio en segundos Unix>` y `retry_after` de 15 segundos. Cada consulta a `POST /v0/operations/{operation_id}` hace un paso más con el sobre completo que manda el núcleo, hasta `completed`. El avance queda en Tactical (los agentes que ya no están), no en el módulo, así que cualquier réplica atiende la consulta. Si se instaló un agente durante la baja y Tactical rechaza el borrado del cliente, la operación sigue en curso y el paso siguiente lo desinstala. Una consulta con un ID que no coincide con el `client_id` del servicio, o de hace más de una hora (el plazo por defecto de una operación larga, sección 7 del protocolo), da `not_found` sin desinstalar nada, y el núcleo repite la baja con la misma clave. Así una consulta tardía, por ejemplo antes de una reactivación, no retoma una baja vieja.

### view

Bloques de texto plano para el portal (sección 9.1 del protocolo), en el idioma de la cuenta:

| Bloque | Contenido |
|---|---|
| `status` | `ok` con el servicio activo dentro del límite. `warning` si está suspendido o pasado del límite |
| `fields` | Agentes instalados contra el límite (por ejemplo `3 de 5`) y el nombre del sitio |
| `notice` | Solo pasado del límite: cuántos hay, cuántos incluye el plan y qué hacer |
| `link` | El link de instalación vigente del sitio, con `expires_at`. Solo si el servicio no está suspendido |
| `actions` | `installer_link`, cuando no hay link vigente y el servicio no está suspendido |

```json
{
  "blocks": [
    { "type": "status", "level": "ok", "text": "Activo" },
    { "type": "fields", "items": [ { "label": "Agentes instalados", "value": "3 de 5" }, { "label": "Sitio", "value": "OficinaPrincipal" } ] },
    { "type": "link", "label": "Instalador del agente", "url": "https://api.ejemplo.com/clients/0b6c.../deploy/", "expires_at": "2026-11-03T10:00:00Z" }
  ]
}
```

Un link vigente es un deployment del sitio con al menos una hora de vigencia por delante. Los IDs de Tactical no se muestran. Si la key no tiene permiso de deployments, la vista sale sin link y sin la acción, que fallaría siempre, y la causa queda en el log. El núcleo descarta cualquier URL cuyo host no esté en la lista de la instancia.

### run_action

Una sola acción, `installer_link`, para el staff y el cliente, con confirmación:

```json
{ "action": "installer_link", "params": { "agent_type": "workstation" } }
```

`agent_type` es `workstation` (por defecto) o `server`. Reutiliza el link vigente del sitio de ese tipo y, si no hay, crea un deployment con `POST /clients/deployments/` (sitio del servicio, vencimiento de `deploy_expiry_days`, arquitectura `amd64` y sin opciones de instalación) y lo vuelve a leer para tomar su `uid`. Respuesta:

```json
{ "url": "https://api.ejemplo.com/clients/0b6c.../deploy/", "expires_at": "2026-11-03T10:00:00Z" }
```

- Repetirla después, con la misma clave o desde otra réplica, devuelve el mismo link mientras siga vigente. Si la respuesta del alta se pierde, el reintento encuentra el deployment creado.
- Dos pedidos simultáneos en réplicas distintas (un doble clic, por ejemplo) pueden crear dos deployments del sitio y devolver links distintos. Los dos son válidos y vencen solos.
- El `uid` tiene que ser un UUID: si no, el deployment se descarta y queda registrado.
- Con el servicio suspendido da `invalid_input` con `service.status`. Otra acción da `invalid_input` con `action`.

### instance/test

`GET /clients/` y `GET /agents/?client=0`, para probar la key, la red y los permisos de lectura sin listar los agentes de todos los clientes. Devuelve `{ "clients": 12 }`.

## Errores

| Tactical | Código del protocolo |
|---|---|
| 401 | `invalid_config`, con `details.cause` igual a `api_key` |
| 403 con JSON | `invalid_config`, con `details.cause` igual a `api_key_o_permisos`: key revocada o rol sin el permiso |
| 403 sin JSON | `invalid_config`, con `details.cause` igual a `ip_no_permitida`: lo contesta el proxy, no Tactical |
| 404 con JSON | `not_found` |
| 404 sin JSON | `invalid_config` sobre `api_url`: la ruta no es de Tactical |
| 400 y otros 4xx | `rejected`, con `details.http_status` y los mensajes de Tactical en `details.upstream` |
| 429 | `rate_limited`, con `retry_after` del encabezado `Retry-After` o 30 segundos |
| 5xx en una consulta | `upstream_unavailable` |
| 5xx en un pedido que cambia algo | `outcome_unknown` |
| Certificado que no valida | `invalid_config`, con `details.cause` igual a `tls` |
| Sin conexión antes de enviar | `upstream_unavailable` |
| Corte o plazo vencido después de enviar | `outcome_unknown` si el pedido cambia algo, y `upstream_unavailable` si es una consulta |
| Respuesta sin JSON o redirección | Lo mismo que el corte después de enviar |

- Los mensajes no pasan de 500 caracteres y nunca llevan cuerpos crudos ni la key: de los errores de Tactical se toman hasta 10 mensajes de hasta 200 caracteres, con la key tapada si Tactical la repitiera.
- Los logs del módulo llevan método, ruta y estado HTTP, nunca cuerpos ni encabezados.
- Los pedidos que cambian algo no se cortan cuando el núcleo se desconecta: si terminan, la SDK guarda el resultado por la clave de idempotencia y el reintento lo recupera.

## Varias réplicas

El módulo no guarda nada en memoria entre pedidos y corre en varias réplicas detrás de un balanceador sin duplicar operaciones:

- Las operaciones con clave pasan por el registro de idempotencia de la SDK. Con varias réplicas lo comparten en PostgreSQL (`Module__IdempotencyConnectionString`). Con SQLite, cada réplica tiene el suyo en un volumen propio.
- Un reintento que cae en otra réplica, sin el registro de la primera, tampoco duplica nada, porque cada operación es idempotente contra Tactical: el alta busca la marca y Tactical no repite nombres de cliente, el modo mantenimiento se fija y no se alterna, la baja tolera el 404 en cada paso, el link vigente se reutiliza y la baja forzada avanza sobre lo que queda en Tactical.
- Lo que se pierde con registros separados es detectar `idempotency_conflict` entre réplicas distintas.

## Pruebas

```
dotnet test tests/BillingOss.Module.TacticalRmm.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo con un secreto generado en cada corrida y reemplazan la API por un Tactical falso con estado: clientes con sitios, agentes y deployments, con las formas de la API pública y sus reglas (nombre de cliente único, borrado de un cliente con agentes rechazado, X-API-KEY obligatorio). Cubren los escenarios de conformidad 1 a 7 (el reinicio del registro lo prueba la SDK), las respuestas perdidas, dos réplicas con registros separados a la vez, el 400 por nombre repetido cuando otra réplica crea el cliente entre la búsqueda y el alta, un sitio de otro cliente, la baja forzada consultada desde otra réplica y un servidor que no filtra los agentes por cliente. Un test falla si el módulo llama a `accounts/`.

### Prueba contra un Tactical de prueba

Nunca contra el de producción. Hace falta la URL de la API y la key de un usuario con el rol del módulo. Sin las variables, la prueba queda inconclusa.

```
export TRMM_TEST_API_URL=https://api.tactical-prueba.example
export TRMM_TEST_API_KEY=la-key-del-usuario-de-prueba
dotnet test tests/BillingOss.Module.TacticalRmm.Tests --filter TestCategory=TacticalRmmReal
```

Prueba la instancia, crea un cliente con el prefijo `PRUEBA-`, repite el alta con la misma clave, suspende y reactiva, consulta estado y vista, pide un link de instalación si el rol tiene permiso, da de baja y comprueba que una key inventada da `invalid_config`. No instala agentes.

## Lo que falta en el núcleo

- `change_plan` y opciones configurables con precio por escalón: hoy el núcleo no los tiene. El módulo espera los parámetros nuevos en `input.params`.
- `run_action`: el núcleo tiene que mandar `input.action` y `input.params` validados contra el `input_schema` del manifiesto y controlar el `audience`.
- La baja forzada: el núcleo hoy manda `input` vacío. Hace falta que el staff pueda pedirla y que viaje como `input.force`.
- Un trabajo de reconciliación que llame a `get_status`. Hoy solo se concilian pagos.
- La baja trabada. La baja por mora llega sin `input.force`, y un cliente RMM casi siempre tiene agentes: `terminate` da `invalid_input` definitivo y el servicio queda suspendido. Hoy `BillingRunService` lo sigue renovando y `CancelUnpaid` anula cada factura con una nota de crédito, así que cada ciclo deja comprobantes fiscales de más. Un servicio con la baja trabada por `invalid_input` o `rejected` tiene que salir de la facturación y de `CancelUnpaid` y pasar a una cola del staff.
- El reintento del staff después de un error definitivo sube la generación de la clave (sección 6 del protocolo). Con la misma clave, la réplica que guardó el error lo devuelve otra vez y otra réplica en cambio da la baja.
- `PendingOperation`: el núcleo tiene que limpiarla cuando la consulta da un error definitivo, y consultar solo la operación que la generó, guardando su nombre junto al `operation_id`. Hoy consulta la pendiente antes de cualquier operación, así que un `unsuspend` posterior podría retomar una baja forzada. El módulo se resguarda con el inicio en el ID y el plazo de una hora, pero la regla es del núcleo.

## Pendiente de comprobar contra un Tactical de prueba

Salió de la documentación pública y de la API pública de Tactical RMM y no se probó contra una instalación:

- Que una key revocada dé 401 o 403 con JSON, y que el 403 del proxy por IP no traiga JSON.
- El filtro `agents/?client=` (el módulo igual filtra por `client_name`) y que `maintenance_mode` venga en cada agente.
- Que `POST /clients/deployments/` acepte el vencimiento con el formato `2026-11-03T10:00:00+0000` y que el link de `/clients/<uid>/deploy/` sirva desde una IP externa y deje de servir al vencer.
- Qué pasa en el equipo cuando se desinstala un agente que está apagado: Tactical lo borra de su base igual y la orden de desinstalarse puede no llegar nunca.
- Si un agente nuevo que se instala en un cliente en mantenimiento lo hereda.
- Que `DELETE /clients/deployments/{id}/` borre el link de instalación y conteste 404 con JSON cuando ya no existe.
