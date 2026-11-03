# Módulo de Proxmox VE

Módulo de aprovisionamiento (`provisioning`) de Hornero para VPS en Proxmox VE. Sirve también para migrar desde un módulo propio de Proxmox del sistema anterior (sección 12 del [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md)). Es un servicio aparte que habla el protocolo v0 y llama a la API pública de Proxmox (`/api2/json`) con `HttpClient` y `System.Text.Json`, sin bibliotecas de terceros.

Capacidades: `provisioning.core` (validate, create, suspend, unsuspend y terminate), `provisioning.status`, `provisioning.actions`, `provisioning.view` y `provisioning.sso`. No declara `provisioning.change_plan`: el cambio de plan sigue por ticket (PVE-23).

## Varias réplicas sin duplicar operaciones

El módulo no guarda nada entre pedidos, ni en memoria ni en disco, salvo el registro de idempotencia de la SDK. Todo lo demás vive en Proxmox y cada llamada lo lee antes de actuar:

- **La marca externa.** La descripción de la VM es `billing-oss:<servicio>:<uuid>:<hex48>`. `<uuid>` es `context.service.uuid`, la identidad estable del servicio que no choca con la de otra instalación que comparte el cluster. Mientras el núcleo no lo mande, la marca queda `billing-oss:<servicio>:<hex48>`. El hex sale del SHA-256 del ID de la instancia y de la `Idempotency-Key`. La misma alta da siempre la misma marca, así que una repetición que cae en otra réplica, o que llega después de perder el registro de idempotencia (otra réplica sin el registro compartido, un volumen que no persiste o una restauración a un punto anterior), reconoce su VM. Otra generación da otra marca.
- **La consulta previa.** Antes de clonar, el alta lee la configuración de los dos VMID donde puede clonar, que es el dato real. El nombre del inventario (`/cluster/resources`) sale de lo que publica pvestatd, que llega hasta 10 segundos tarde o no llega si está trabado. Si uno de los dos VMID tiene la marca de esta alta, sigue desde ahí. Si tiene un clon en curso, contesta `conflict` y espera, porque Proxmox escribe primero una configuración temporal sin nombre ni marca. Una VM con el nombre del servicio y sin la marca frena todo con `manual_review`.
- **El VMID.** Es el ID del servicio si está libre y fuera de `reserved_vmids`. Si no, es 1.000.000 más el ID del servicio. Es fijo por servicio para que una repetición encuentre el clon que ya arrancó. El nombre siempre es `vm` más el ID del servicio.
- **La operación larga.** El `operation_id` lleva el VMID y la marca (`create-7001-<hex48>`, `reinstall-120-<hex48>`, `terminate-7001`). Cualquier réplica sigue la consulta desde lo que encuentra en Proxmox y nunca contesta que la perdió.
- **La MAC.** No hay lock compartido: el reclamo es la MAC escrita en `net0`, que todas las réplicas ven. Cada alta escribe primero y revisa el cluster después, y solo sigue si nadie más tiene esa MAC. Entre dos que la escribieron, al menos uno ve al otro. Cede el que ve a un rival prendido o con onboot, o con VMID menor, y toma la entrada siguiente. Si solo ve rivales con VMID mayor que todavía no ganaron, espera a que cedan. Nunca arrancan dos VMs con la misma MAC.

La idempotencia la da la SDK con la `Idempotency-Key` del núcleo. Con más de una réplica, todas comparten el registro en PostgreSQL (`Module__IdempotencyConnectionString`). Aun sin él, por ejemplo después de una restauración a un punto anterior, una repetición que cae en otra réplica no clona dos veces, porque encuentra la VM por su VMID y su marca.

**La reserva de la reinstalación.** Entre el borrado y el clon de una reinstalación la MAC del servicio no está en ninguna VM, y como el pool se asigna en orden, cualquier alta que llegara en esa ventana la tomaba. Por eso, antes de borrar, la reinstalación deja una reserva visible para todas las réplicas: una VM vacía `rsv<servicio>` en el VMID 2.000.000 más el servicio, sin discos, apagada, con `onboot=0`, la NIC sin enlace (`link_down=1`) y la MAC en `net0`, con la descripción `billing-oss-reserva:<servicio>:<uuid>`. El inventario de MACs la cuenta como una MAC ganada, así que ninguna alta la elige ni la disputa. Se borra apenas la VM nueva reclama la MAC, y la baja borra la que haya quedado de una reinstalación que no terminó. Pide `VM.Allocate` y `VM.Config.Network` en `/vms`, los mismos del clon. Si ese VMID está fuera de rango o en `reserved_vmids`, la reinstalación sigue sin reserva y lo deja en el log. Si lo ocupa otra VM, contesta `manual_review` con `reserve_vmid_taken` antes de borrar nada.

## Configuración de la instancia

| Campo | Secreto | Qué es |
|---|---|---|
| `api_url` | No | Origen https de la API, sin ruta. Por ejemplo `https://pve.example.com:8006` |
| `token_id` | No | ID del token de API, `usuario@realm!nombre` |
| `token_secret` | Sí | Secreto del token |
| `ca_pem` | No | CA propio de Proxmox en PEM. Si está, se confía solo en ese CA, como `PROXMOX_CA_FILE` en el módulo anterior. Sin él vale el almacén del sistema |
| `network_pool` | No | Lista ordenada de `{ mac, public_ip, bridge, mtu, private_ip }`. `bridge` es `vmbr1` si falta, `mtu` va de 576 a 9000 y `private_ip` es la reserva DHCP de esa MAC, que arma el ipset del anti-spoofing |
| `console_origin` | No | Origen https del gateway de consola. Su host tiene que estar en la lista de hosts del navegador de la instancia (marcado con `x-browser-host`) |
| `console_secret` | Sí | Secreto compartido con el gateway, de al menos 32 bytes. Se usa el texto literal |
| `console_server_key` | No | Clave del servidor en la configuración del gateway. Por defecto `"6"`, para no redesplegar el gateway |
| `legacy_acl_cleanup` | No | Quitar al suspender la ACL `PVEVMUser` de `client<cuenta>@pve`, que dejaron versiones viejas. En la baja no hace falta: el `DELETE` de Proxmox borra las ACL de la VM |
| `reserved_vmids` | No | Rangos `{ from, to }` que el alta no usa como VMID. Por defecto 9000 a 9019, las plantillas |

Todo se valida en cada llamada. Una línea del pool con MAC, IP, bridge, MTU o IP privada inválidos, o una MAC o IP repetida, invalida la configuración entera (`invalid_config` con `network_pool[i]` en `details.fields`) y no se llama a Proxmox. El esquema del manifiesto tiene los mismos patrones, para que el núcleo lo rechace al guardar.

## Parámetros del producto

| Clave | Opción del cliente | Qué es |
|---|---|---|
| `template_id` | Sí | VMID de la plantilla a clonar |
| `node` | No | Nodo donde está la plantilla y donde queda el clon |
| `cores` | Sí | Núcleos |
| `memory_mb` | Sí | RAM en MB |
| `disk_gb` | Sí | Disco scsi0 en GB |
| `rate_mb_s` | Sí | Límite de red en MB/s, como texto decimal, la unidad del `rate` de net0 |
| `console_enabled` | No | Si el producto tiene consola |
| `hostname_suffix` | No | Sufijo de la etiqueta `Host_<ip con guiones><sufijo>` |
| `isolation_group_prefix` | No | Prefijo del security group de cluster.fw, que se usa como prefijo más bridge. Vacío no aísla |
| `os_minimum_policy` | No | `{ "9002": { "cpu": 2, "ramMB": 3072, "diskGB": 40 } }`. Se acepta también como texto JSON, como venía del sistema anterior |
| `console_pool` | No | Pool de Proxmox del gateway de consola. Los clones entran ahí |

Las cinco claves marcadas con `x-client-option` son las únicas que pueden venir de opciones del cliente. El núcleo resuelve las opciones en estos parámetros y rechaza una opción que intente fijar las demás.

## Cómo correrlo

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma de la instalación en el núcleo |
| `Module__PreviousSigningSecret` | Opcional, durante una rotación |
| `Module__IdempotencyStorePath` | Registro de idempotencia de la SDK. Por defecto `data/idempotency.db` |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo y el módulo no crea el directorio. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `ASPNETCORE_URLS` | Dónde escucha |

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5193"
dotnet run --project src/modules/BillingOss.Module.Proxmox -c Release
```

Salida a la red: la API de Proxmox (por defecto el puerto 8006) y el gateway de consola por 443. El certificado de Proxmox se valida siempre, nombre incluido, y no hay opción para desactivarlo. No se siguen redirecciones. La conexión espera hasta 10 segundos y cada pedido hasta 30. Nunca se loguean cuerpos, el token ni trazas, y los mensajes que vienen de Proxmox se devuelven con el token y las IPs tapados.

### Permisos del token

`instance/test` exige `VM.Audit` heredado en `/vms`: sin eso Proxmox filtra en silencio las VMs que el token no ve, el inventario de MACs sale incompleto y una VM viva parece borrada. Por eso también se exige antes de asignar red, de dar una baja por hecha y de contestar `not_found`. La respuesta de la prueba lista en `missing_privileges` los de `/vms` que faltan para operar: `VM.Allocate`, `VM.Clone`, `VM.Config.CPU`, `VM.Config.Memory`, `VM.Config.Disk`, `VM.Config.Network`, `VM.Config.Cloudinit`, `VM.Config.Options` y `VM.PowerMgmt`. Además hacen falta `Datastore.AllocateSpace` en el storage, `Pool.Allocate` en el pool de consola, `Sys.Audit` en los nodos (lista de bridges) y en `/` para leer los grupos de cluster.fw. Con `legacy_acl_cleanup`, también `Permissions.Modify` en `/vms` para quitar la ACL y `Sys.Audit` o `User.Modify` en `/access/groups` para ver los usuarios `client`. `User.Modify` solo no alcanza: deja listar los usuarios pero no tocar ACL. `instance/test` los exige cuando la opción está prendida y contesta `invalid_config` con `reason: legacy_acl_permissions` si faltan.

Para el paralelo con el sistema anterior alcanza un token de solo lectura (`VM.Audit` y `Sys.Audit`), con el que andan `instance/test`, `validate`, `get_status` y `view`.

## Operaciones

Todas son `POST /v0/provisioning/{operación}` con el sobre del protocolo. Las que cambian algo (`create`, `suspend`, `unsuspend`, `terminate` y `run_action`) exigen `Idempotency-Key`.

### validate

Sin efectos y sin reservar red. Toma `input.params` o, si no viene, `context.service.params`. Valida los recursos, la política de mínimos de la plantilla elegida y, contra Proxmox, que la plantilla exista en el nodo y sea plantilla, que `disk_gb` no sea menor que su scsi0, que existan en el nodo los bridges de todas las entradas del pool, sus grupos de aislamiento y el pool de consola. Con `input.current_params` (cambio de plan) rechaza cualquier cambio de `template_id`, porque cambiar el sistema operativo es reinstalar.

Los errores de recursos son `invalid_input` con el motivo por campo, para que el núcleo arme su texto en el idioma del cliente:

```json
{ "error": { "code": "invalid_input", "details": { "fields": { "disk_gb": { "reason": "os_minimum", "min": 40 } } } } }
```

Motivos: `invalid`, `os_minimum`, `template_disk`, `not_found`, `not_template`, `reinstall_required` y `shrink_unsupported`. Lo que falta en Proxmox (bridge, grupo o pool de consola) es `invalid_config` con `details.reason`.

### create

Entrada: `input.credentials.guest_password`, que genera y guarda el núcleo. Pasos, cada uno mirando antes si ya está hecho:

1. Calcula la marca y hace la consulta previa (ver arriba). Si ya hay una VM `vm<servicio>` sin esta marca, contesta `manual_review` y no crea otra.
2. Si la VM no existe: verifica plantilla, disco, red libre, bridge, grupo y pool, y clona (`full=1`, `target`, `name`, `description` con la marca y `pool`). Que la plantilla sea plantilla y su disco salen de su configuración, no del inventario.
3. Espera el clon, con `retry_after` de 60 segundos mientras dure. Mientras corre, la VM tiene `lock: clone`: primero con la configuración temporal de Proxmox ("qmclone temporary file", sin nombre) y después de cada disco con el nombre y la marca. La marca se verifica cuando el lock se libera.
4. Verifica la marca y pone `onboot=0` enseguida, sin esperar la contraseña: el clon copia `onboot` de la plantilla, y sin configurar (sin firewall y con una MAC al azar) no tiene que arrancar sola con el nodo si el alta no sigue. Después apaga si hace falta, agranda scsi0 al tamaño exacto (nunca achica y lo verifica) y hace un solo `PUT /config` con `cores`, `memory`, `ciuser` (`client` más el ID de cuenta), `cipassword`, `agent=1`, `onboot=0` y `net0` (virtio, MAC, bridge, `firewall=1`, `mtu` y `rate`).
5. Reclama la MAC (ver arriba).
6. Arma y verifica el ipset `ipfilter-net0` con la IP privada, y recién ahí pone las opciones del firewall: `enable=1`, `policy_in` y `policy_out` en ACCEPT, `macfilter=1`, `ipfilter`, `ndp=1` y `radv=0`. Después la regla del grupo de aislamiento.
7. Arranca la VM y pone `onboot=1` como último paso.

Si el firewall, el ipset o el aislamiento fallan, la VM no arranca y queda con `onboot=0`. Ninguna falla borra la VM.

La contraseña solo viaja en `create`, y la consulta de una operación larga no la trae. Por eso, hasta cargarla (paso 4), una espera vencida contesta `conflict` con `reason: in_progress` y `retry_after`, para que el núcleo repita `create` con la misma clave y la contraseña. Desde ahí, una espera vencida contesta `pending` y el resto sigue por `POST /v0/operations/{id}`.

Resultado:

```json
{
  "observed_state": "active",
  "label": "Host_203-0-113-11.vps.example.com",
  "external_ids": {
    "vmid": "7001", "node": "pve1", "name": "vm7001", "marker": "billing-oss:7001:<hex48>",
    "mac": "bc:24:11:aa:00:01", "public_ip": "203.0.113.11", "private_ip": "10.20.0.11",
    "bridge": "vmbr2", "mtu": "1500", "username": "client42"
  }
}
```

En una plantilla Windows (`ostype` que empieza con `win` o `w2k`) `username` es `Administrador`, como dice el correo de bienvenida. `label` es un campo nuevo del protocolo: el núcleo lo guarda como etiqueta del servicio si el pedido no trajo una.

### suspend, unsuspend y terminate

- **suspend**: verifica que la VM sea del servicio, quita la ACL heredada si corresponde y revoca la consola antes de tocar la VM (así un error de permisos falla con la VM como estaba), apaga en seco (`status/stop`) si no está apagada y pone `onboot=0` como último paso. Si el gateway está configurado y no confirma la revocación, falla como `upstream_unavailable` antes de tocar la VM.
- **unsuspend**: prende la VM si no está prendida y pone `onboot=1`. Sin `external_ids.vmid` (alta incompleta) contesta `conflict`.
- **terminate**: revoca, apaga y borra la VM (`DELETE`, que borra también sus ACL). Si la VM ya no existe, con `VM.Audit` verificado, es `completed`. Si existe y no es del servicio, `manual_review` sin borrar nada. Sin `vmid` (un alta que quedó Fallida) mira los dos VMID del alta por su configuración y las VMs `vm<servicio>` del inventario, y borra la que tenga una marca del servicio. Si uno de esos VMID tiene un clon en curso, contesta `pending` y vuelve a mirar cuando termina. Un borrado largo contesta `pending` con `terminate-<vmid>`. Al terminar borra también la reserva de la MAC que haya dejado una reinstalación sin terminar.

onboot sigue al estado del servicio y no a si la VM está prendida: un VPS activo que su dueño apagó conserva `onboot=1`.

Una VM es del servicio si su descripción es `external_ids.marker`, o una marca posterior del mismo servicio con el nombre `vm<servicio>` en la configuración. Los importados sin marca se reconocen por el nombre `vm` más el ID del servicio en el VMID de `external_ids`.

**Tareas en curso.** Proxmox no pone lock en la configuración mientras apaga, prende, reinicia o borra los discos, y una segunda tarea sobre la VM espera el lock de su archivo y falla. Por eso, antes de lanzar `status/start`, `status/stop`, `status/reboot` o el `DELETE`, el módulo mira las tareas activas de la VM (`GET /nodes/{nodo}/tasks?vmid=...&source=active`). Si hay una igual, de un intento anterior que se cortó después del pedido, la sigue en vez de lanzar otra. Si hay otra distinta, contesta `conflict` con `reason: task_running`, y la baja contesta `pending`. Sin `Sys.Audit` Proxmox lista solo las tareas del token, que son las que importan: las de otros (un backup, una migración) dejan un lock en la configuración, que se mira aparte. Una tarea de borrado que falla con la VM ya borrada (la segunda de dos) cuenta como hecha. Un borrado con `WARNINGS` (por ejemplo un disco que no se pudo sacar del storage) completa la baja con `details.reason: destroy_warnings`, el `exitstatus` y los volúmenes que tenía la VM para que el staff los revise, y lo deja en el log.

### run_action

`input.action` es una de las acciones del manifiesto. Todas exigen el servicio activo (`rejected` con `service_not_active` si no) y la VM del servicio, y el nodo sale siempre de `/cluster/resources`, porque las VMs se migran a mano.

| Acción | Quién | Qué hace |
|---|---|---|
| `start` | staff y cliente | `status/start`. No hace nada si ya está prendida |
| `stop` | staff y cliente | `status/stop`. No hace nada si ya está apagada |
| `reboot` | staff y cliente | `status/reboot`. Con la VM apagada es `rejected` con `vm_stopped`: no cambia sola, y un reintento la reiniciaría si el cliente la prende mientras tanto. Si la tarea sigue al vencer la espera, contesta `pending` con `reboot-<vmid>-<UPID en hexadecimal>` y la consulta espera esa misma tarea: nada en Proxmox dice que un reinicio ya se hizo, así que una repetición no lanza otro. Una repetición que encuentra el reinicio todavía en curso lo sigue. El único caso que reinicia dos veces es una réplica que se cae justo después del pedido, sin guardar la respuesta, y una repetición que llega con la tarea ya terminada: Proxmox no deja atar la tarea a la clave |
| `reinstall` | staff y cliente | Ver abajo. Pide `input.credentials.guest_password` |
| `revoke_console` | staff | Revoca las consolas del servicio, por ejemplo al pasarlo a otra cuenta |

La respuesta trae `external_ids.node` para que el núcleo actualice el nodo.

**reinstall** borra la VM y la vuelve a clonar con el mismo VMID, la plantilla vigente de `params.template_id` y la misma red, que sale de `external_ids` (`mac`, `public_ip`, `bridge`, `mtu` y `private_ip`) y no del pool, porque hay VPS con IPs fuera del pool. Lo que falte de `bridge`, `mtu` o `private_ip` (un importado sin ipset, por ejemplo) se completa con la entrada del pool que tiene la misma MAC y la misma IP pública, así la VM nueva no queda sin el filtro de origen. Antes de borrar verifica todo: que la VM sea del servicio, la espera de 1800 segundos desde `external_ids.last_reinstall_at` (si no se cumplió, `conflict` con `reason: reinstall_wait` y `retry_after` con los segundos que faltan), que la MAC de la VM coincida con `external_ids` (si no, `manual_review`: hay que refrescarlos con `get_status`), plantilla, bridge, grupo y que ninguna otra VM tenga esa MAC. Después revoca la consola, apaga, deja la reserva de la MAC (ver arriba), borra y sigue como un alta. Si se corta, la repetición con la misma clave encuentra la marca nueva y sigue. El resultado trae la marca nueva y `last_reinstall_at`.

### get_status

Ubica la VM por `external_ids.vmid`, con `VM.Audit` verificado, y verifica que sea del servicio con su configuración: la marca de `external_ids`, o una marca posterior del mismo servicio con el nombre `vm<servicio>` (una reinstalación que falló después de clonar deja la marca nueva, que el núcleo nunca recibió), o el nombre en los importados sin marca. El inventario solo ubica el nodo y el VMID.

| onboot | Energía | `observed_state` |
|---|---|---|
| 1 | prendida o apagada | `active` |
| 0 | apagada | `suspended` |
| 0 | prendida | `unknown` |

Sin la VM es `not_found`, y si la VM no es del servicio, `unknown` con `details.reason: ownership`. `details` trae `power`, `onboot`, `node`, si hay marca y `drift`, la lista de desvíos que no se corrigen solos: `firewall_off`, `macfilter_off`, `ipfilter_mismatch`, `nic_firewall_off` (net0 sin `firewall=1`: Proxmox no aplica a la NIC ni macfilter ni ipfilter ni el grupo), `ipfilter_missing` (la IP privada de `external_ids` o del pool para esa MAC no está en el ipset, o ipfilter está apagado), `isolation_missing` y `onboot_mismatch` (onboot que no sigue al estado del servicio). `external_ids` vuelve refrescado con `vmid`, `node`, `name` de la configuración, `mac`, `bridge` y `mtu` de net0, `private_ip` del ipset y `marker` si la VM tiene una marca del servicio. Es solo lectura.

### view

Bloques de texto plano en el idioma de la cuenta (`es` o `en`). Con `input.audience: "staff"` suma VMID, nodo y si hay marca.

```json
{ "blocks": [
  { "type": "status", "level": "ok", "text": "Encendido" },
  { "type": "fields", "items": [ { "label": "IP pública", "value": "203.0.113.11" }, { "label": "RAM", "value": "512 / 1024 MB" } ] },
  { "type": "actions", "items": ["start", "stop", "reboot", "reinstall"] },
  { "type": "notice", "level": "info", "text": "Para cambiar el plan, abrí un ticket desde Soporte." }
] }
```

Los bloques tienen la misma forma que en los demás módulos y en el módulo manual del núcleo: el color de `status` y `notice` en `level` (`ok`, `info`, `warning` o `error`) y las listas de `fields` y `actions` en `items`. Los campos son IP pública, usuario, sistema operativo (el nombre de la plantilla), CPU y RAM. El bloque `actions` lleva solo acciones del manifiesto y solo con el servicio activo, como exigen `run_action` y la consola: un VPS suspendido no ofrece botones. La consola no es una acción sino la operación `sso`, que el núcleo ofrece por la capacidad `provisioning.sso`. Si el producto no tiene `console_enabled`, `sso` contesta `rejected` con `console_disabled`. Si Proxmox no responde, en vez de estado y campos va un aviso de error sin detalles y la página carga igual. Los gráficos de la última hora no están: el protocolo v0 no tiene un bloque de series (PVE-27).

### sso

Devuelve `{ "url", "expires_at" }`. La URL es `console_origin` más `/#` y el grant, que vale 60 segundos. Exige `console_enabled` (si no, `rejected`), el servicio activo y la VM prendida (si no, `conflict` con `reason: vm_stopped`).

El grant respeta el contrato del gateway desplegado: base64url del JSON `{ v: 1, aud: "proxmox-console", server, sub, node, vmid, iat, exp: iat + 60, jti }` (24 bytes al azar en hexadecimal), un punto y base64url del HMAC-SHA256 de ese segmento con `console_secret` como texto literal. La revocación manda a `POST <console_origin>/api/revoke` un grant con `aud` `proxmox-console-revoke`, sin `node` ni `vmid`, con 10 segundos de espera.

### instance/test

`GET /cluster/resources` con 20 segundos de espera y `GET /access/permissions` en `/vms`. Devuelve `{ "vms": 36, "missing_privileges": [] }`. Un token rechazado, sin `VM.Audit` en `/vms` o un certificado que no es de confianza dan `invalid_config` con `details.reason` (`token_rejected`, `vm_audit_missing` o `tls_untrusted`).

## Errores

| Caso | Código |
|---|---|
| Proxmox contesta 401 | `invalid_config`, `token_rejected` |
| Proxmox contesta 403 | `invalid_config`, `permission_denied` |
| Certificado no confiable o con otro nombre | `invalid_config`, `tls_untrusted` |
| Pool agotado, bridge, grupo o pool de consola inexistente | `invalid_config` con `details.reason` |
| Recursos, política de mínimos o plantilla | `invalid_input` con `details.fields` |
| VM con lock, otra tarea de la VM en curso, alta incompleta, clon en curso, VM apagada para la consola, espera de reinstalación | `conflict` con `retry_after` y `details.reason` |
| Proxmox rechaza los parámetros (400) o una tarea termina con error | `rejected` con `details.upstream` |
| Reiniciar una VM apagada | `rejected` con `reason: vm_stopped` |
| Una tarea que no consiguió el lock de la VM ("VM is locked" o "can't lock file") | `conflict` con `reason: vm_locked` y `retry_after` de 60 segundos |
| Sin conexión, también cuando vence el tiempo para conectar, o una consulta que falla | `upstream_unavailable` |
| Corte o plazo vencido después de enviar un cambio | `outcome_unknown`. Se repite con la misma clave y cada paso mira el estado antes de actuar |
| VMID ocupado por una VM del servicio sin la marca, marca distinta, VM que desapareció en medio de un alta, ipset con entradas ajenas, MAC del servicio en otra VM prendida | `manual_review` (409, no se reintenta) |

`manual_review` es un código nuevo, aditivo como permite v0. El núcleo no reintenta y pasa el caso al staff. Los mensajes son para el staff y nunca llevan el token, IPs de clientes ni trazas.

## Decisiones de diseño

Algunas decisiones a propósito:

- El VMID alternativo es 1.000.000 más el ID del servicio y no el siguiente libre, para que una repetición encuentre su clon sin estado guardado.
- La marca sale de la clave de idempotencia y no de un número al azar guardado en una tabla.
- La reserva de red es la MAC en net0 y no una fila con lock de base. La red de una reinstalación sale de `external_ids` y no del pool, así reinstalan los VPS con IP fuera del pool.
- `onboot=0` va en el `PUT /config` del alta. Las plantillas lo traen en 1 y una VM a medio armar no tiene que arrancar sola.
- `start` no hace nada con la VM prendida y `stop` exige el alta completa como las demás acciones.
- La baja de una VM que ya no existe es `completed` (con `VM.Audit` verificado).
- Reinstalar también lo puede pedir el staff.
- `validate` consulta Proxmox.
- `last_reinstall_at` es la hora en que terminó la reinstalación, no la de inicio.
- Una tarea que termina con `WARNINGS` cuenta como bien hecha.

## Lo que necesita del núcleo

- `run_action`, `get_status`, `view`, `sso` y `validate`. Hoy solo llama create, suspend, unsuspend y terminate.
- Mandar las credenciales también en `run_action` para las acciones que las declaran (`reinstall`).
- Guardar `result.label` como etiqueta del servicio y usarla en la línea de factura.
- Tratar `manual_review` como falla definitiva con incidencia para el staff, también fuera de create.
- Rechazar una `public_ip` repetida entre servicios vivos (PVE-08).
- Resolver las opciones del cliente en los parámetros del servicio (PVE-04).
- Pedir `revoke_console` al pasar un servicio a otra cuenta o instancia.
- El modo observación de la instancia durante el paralelo (PVE-20).
- No contar como intento fallido un `conflict` reintentable (`in_progress`, `vm_locked`, `task_running`, `mac_contended`): hoy `JobQueue.Fail` lo descuenta de los 10 intentos como cualquier error. Un clon de una plantilla grande con el storage cargado puede tardar más de 25 minutos, y el trabajo queda muerto con el servicio en aprovisionamiento. Respetar `retry_after` sin gastar intentos, o seguir el clon por consulta.
- Abrir una incidencia para el staff cuando `terminate` completa con `details.reason: destroy_warnings`, con los volúmenes de `details.volumes`: la VM ya no está y puede haber quedado un disco del cliente en el storage.
- Seguir con `POST /v0/operations/{id}` un `pending` de `run_action`, como el de un reinicio largo (`reboot-<vmid>-<UPID>`), igual que el de `create`.

## Pruebas

```
dotnet test tests/BillingOss.Module.Proxmox.Tests
```

Los tests levantan el módulo en un `TestServer` y del otro lado un Proxmox falso por HTTPS de verdad en loopback, con un certificado autofirmado que se genera en cada prueba. El Proxmox falso responde con las formas de la API pública (envoltorio `data`, tareas por UPID, motivo del error en la línea de estado), simula clones, tareas largas, migraciones, locks y fallas, y atiende la revocación del gateway. Como Proxmox, el clon escribe primero la configuración temporal y después la marca, el lock de un backup hace fallar la tarea por dentro y no el pedido, una segunda tarea sobre la VM falla esperando el lock, el borrado no pone lock hasta el final, y puede simular un pvestatd trabado (inventario sin nombres ni template), un nodo con más de 497 días encendido y un borrado con avisos. Cubren TLS (certificado no confiable, CA equivocado), rutas, el ciclo completo, la operación larga seguida por otra réplica, dos altas a la vez con una sola entrada libre, la reinstalación con IP fuera del pool, la reserva de la MAC mientras se reinstala (un alta en esa ventana no la toma y la baja borra la que quedó), los errores del protocolo y que el token, el secreto del gateway y la contraseña no aparezcan en respuestas ni logs.

### Contra un Proxmox de laboratorio

Sin las variables, la prueba queda inconclusa:

```
export PVE_TEST_API_URL=https://pve-lab.example.com:8006
export PVE_TEST_TOKEN_ID=billing@pve!lab
export PVE_TEST_TOKEN_SECRET=...
export PVE_TEST_NODE=pve1
export PVE_TEST_TEMPLATE=9001
dotnet test tests/BillingOss.Module.Proxmox.Tests --filter TestCategory=ProxmoxReal
```

Opcional: `PVE_TEST_CA_FILE` con el CA en PEM y `PVE_TEST_DISK_GB`. Por defecto solo lee (`instance/test` y `validate`). Con `PVE_TEST_MUTATE=1`, `PVE_TEST_POOL` (el pool en JSON) y `PVE_TEST_SERVICE_ID` (un VMID libre) hace alta, suspensión, reactivación, reinstalación y baja.

### Contra el gateway de consola

```
export CONSOLE_GATEWAY_DIR=<carpeta del gateway de consola>
dotnet test tests/BillingOss.Module.Proxmox.Tests --filter TestCategory=GatewayReal
```

Pide un grant al módulo y lo verifica con el `verifyGrant` de `server.mjs` en Node.

## Decisiones pendientes

Las de la especificación que cambian el módulo:

- Cambio de plan: implementar `change_plan` o seguir por ticket (hoy, ticket).
- Gráficos del portal: un bloque de series en el protocolo o sacarlos (hoy no están).
- Contraseña en la reinstalación: nueva o la misma. El módulo usa la que mande el núcleo.
- Prueba de dueño de los VPS importados sin marca: el nombre con el VMID (lo que hace hoy) o escribirles una marca en la adopción.
- Audiencias del contrato del gateway: `proxmox-console` y `proxmox-console-revoke`. Un gateway desplegado con otros nombres necesita el mismo cambio.
- Suspensión con apagado inmediato (hoy) o apagado ordenado con tiempo límite.
