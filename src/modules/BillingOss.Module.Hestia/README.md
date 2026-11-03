# Módulo de HestiaCP

Módulo de aprovisionamiento (`provisioning`) de Hornero para hosting web en HestiaCP. Reemplaza al servertype `vesta` (sección 12 del [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md)). Es un servicio aparte que habla el protocolo v0 y llama a la API pública de Hestia con `HttpClient` y `System.Text.Json`. Se escribió de cero: del módulo anterior se tomaron solo las reglas de ciclo de vida verificado y los casos de su selftest.

Capacidades: `provisioning.core`, `provisioning.change_plan`, `provisioning.status`, `provisioning.actions` y `provisioning.view`. No declara `provisioning.sso`: el login de Hestia 1.10.5 exige un token CSRF que no se puede mandar desde afuera, así que el portal muestra un enlace simple al login.

El módulo no guarda nada entre pedidos. Cada operación lee el estado de la cuenta en Hestia, cambia solo lo que falta y vuelve a leer. Por eso puede correr en varias réplicas detrás de un balanceador (ver [Alta disponibilidad](#alta-disponibilidad)).

## Configuración de la instancia

| Campo | Secreto | Para qué |
|---|---|---|
| `host` | No | Nombre del servidor Hestia, sin esquema, puerto ni ruta |
| `port` | No | Puerto del panel y de la API. Por defecto 8083 |
| `tls_fingerprint` | No | SHA-256 del certificado del puerto de la API, en hexadecimal, con o sin dos puntos. Solo si el certificado no lo firma una CA pública |
| `access_key_id` | No | Access key de Hestia con el perfil de comandos del módulo |
| `secret_key` | Sí | Secret key de esa access key |
| `default_language` | No | Idioma del panel si Hestia no tiene el de la cuenta. Por defecto `es` |
| `username_prefix` | No | Prefijo del usuario de las cuentas nuevas, que es el prefijo más el ID del servicio. Por defecto `c` |

Se validan antes de salir a la red. Un campo que no sirve da `invalid_config` con el campo en `details.fields`.

**TLS.** Siempre validado: con la CA del sistema, o con la huella fijada en `tls_fingerprint`. No hay opción para desactivarlo. Las redirecciones no se siguen, porque llevarían la access key a otro destino.

**Autenticación.** Con access key y no con la contraseña de admin, que la documentación oficial de Hestia marca como obsoleta. El cuerpo lleva `hash` con la forma `ACCESS_KEY:SECRET_KEY`.

**Perfil de la access key.** Se crea un archivo de perfil en `/usr/local/hestia/data/api/` con `ROLE='admin'` y estos comandos, y la access key con ese perfil (`v-add-access-key 'admin' '<perfil>' billing-oss json`):

```
COMMANDS='v-list-users,v-list-user,v-list-user-packages,v-list-web-domains,v-list-dns-domains,v-list-mail-domains,v-list-databases,v-list-cron-jobs,v-search-domain-owner,v-add-user,v-add-domain,v-change-user-language,v-change-user-password,v-change-user-package,v-suspend-user,v-unsuspend-user,v-delete-user'
```

Durante el mes en paralelo con WHMCS se usa otra access key con un perfil de solo lectura, con los ocho `v-list-*` y `v-search-domain-owner`:

```
COMMANDS='v-list-users,v-list-user,v-list-user-packages,v-list-web-domains,v-list-dns-domains,v-list-mail-domains,v-list-databases,v-list-cron-jobs,v-search-domain-owner'
```

Con esa, Hestia rechaza cualquier mutador aunque el núcleo lo mande por error. Si falta uno de los ocho, `get_status` o `instance/test` dan `invalid_config`. Recién al pasar a producción se cambia por la de perfil completo.

**Lista de IPs.** La API de Hestia acepta solo las IPs de su lista (desde la versión 1.4.0). Hay que sumar la IP de salida de cada réplica del módulo. Un rechazo por IP, por autenticación o por un comando fuera del perfil da `invalid_config` con el texto de Hestia en `details.upstream` y un mensaje que dice qué revisar. Se reconoce por un 401 o un 403, y en las lecturas también por el texto de Hestia. En un mutador el texto no cuenta, porque puede ser la salida del comando (por ejemplo un "not allowed to connect" de la base): ahí lo decide la relectura.

`instance/test` corre `v-list-users` y `v-list-user-packages`. Prueba a la vez la access key, la IP permitida y el certificado, y devuelve los nombres de los paquetes para cargar los productos: `{ "packages": ["freetier", "gainsboro"] }`.

## Producto, pedido y datos personales

| Esquema | Campo | Qué es |
|---|---|---|
| `product_schema` | `package` | Paquete de Hestia. Obligatorio. Un paquete inválido o inexistente es un error del producto: `invalid_config` |
| `order_schema` | `domain` | Dominio del hosting. Obligatorio. Se normaliza a minúsculas, sin punto final y en punycode si tiene eñes o acentos |

`data_scopes` pide `account.contact` porque `v-add-user` exige un correo de contacto. El nombre de la cuenta va en el campo NAME de Hestia, limpio de símbolos y cortado a 50 caracteres. Si no queda nada, no se manda. No se piden domicilio ni otros datos.

Las opciones SSH Access e IP Address del módulo anterior no se portan: ningún producto las usa.

## Credenciales e IDs externos

El manifiesto declara `credentials: ["password"]`. El núcleo genera la contraseña, la guarda cifrada antes de llamar y la manda en `input.credentials.password` de `create`. El módulo nunca la devuelve, ni en `view`.

La acción `change_password` declara `credentials: ["password"]`, el campo por acción de la sección 5 del protocolo: el núcleo genera una contraseña nueva, la guarda y la manda en `input.credentials.password` de `run_action`.

Cada operación que termina devuelve `result.external_ids`:

| ID | Qué es |
|---|---|
| `username` | Usuario de Hestia. En las altas nuevas es `username_prefix` más el ID del servicio, por ejemplo `c1042`. Tiene que cumplir `^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$` |
| `domain` | Dominio del servicio, en minúsculas. Es la prueba de que la cuenta es del servicio |

## Lectura de estado

Todas las operaciones usan la misma lectura:

1. `v-list-user USER json`. Si Hestia contesta exactamente `Error: user USER doesn't exist`, o un objeto vacío, se pide `v-list-users json`. La cuenta está ausente solo si ese inventario no está vacío, todos sus registros tienen `SUSPENDED` en `yes` o `no` y el usuario no figura. Cualquier otra cosa no prueba nada.
2. Con el usuario presente, su `SUSPENDED` es el estado esperado.
3. Se leen `v-list-web-domains`, `v-list-mail-domains`, `v-list-dns-domains`, `v-list-databases` y `v-list-cron-jobs`. Si alguna fila tiene otro `SUSPENDED`, la cuenta está en estado parcial. Si alguna lista responde un error, el estado no se pudo leer.
4. El dominio de `external_ids` tiene que ser una clave exacta de la lista web o de la DNS, comparada en minúsculas, sin regex ni parecidos. Se acepta la de DNS porque los paquetes `dns-managment` y `DNSZONE` pueden no tener web.

El cuerpo de Hestia se lee aunque el HTTP no sea 2xx, porque ahí viene el texto del error. Un corte o una respuesta que no es JSON en una lectura da `upstream_unavailable`.

## Operaciones

Todas son `POST /v0/provisioning/{operación}` con el sobre del protocolo. Las que cambian algo exigen `Idempotency-Key`. Los mutadores de Hestia contestan `OK`, pero ni el OK ni el texto de error alcanzan: el resultado lo decide siempre la lectura posterior. No hay vuelta atrás en ninguna.

| Operación | Espera del núcleo |
|---|---|
| `validate` | 30 s |
| `create`, `terminate` | 120 s |
| `suspend`, `unsuspend`, `change_plan` | 90 s |
| `get_status`, `run_action` | 60 s |
| `view` | 20 s |

### validate

Recibe lo mismo que `create` y no cambia nada. Controla el formato del dominio y el correo de la cuenta, que el paquete exista (`v-list-user-packages`) y que el dominio no tenga dueño en Hestia (`v-search-domain-owner`). Un dominio ocupado da `invalid_input` con `details.fields: ["domain"]` y `details.reason: "domain_taken"`. Un paquete que no existe da `invalid_config`.

### create

1. El usuario es `external_ids.username` si viene (un reintento) o el de una cuenta nueva: el prefijo, el ID del servicio y, si el núcleo manda `context.service.uuid`, cuatro caracteres en base32 del SHA-256 del uuid, por ejemplo `c1042k7xq`. Un uuid inválido da `invalid_input` sin tocar Hestia.
2. Se lee el estado. Si la cuenta no existe, `v-add-user USER PASSWORD EMAIL PACKAGE [NAME]`.
3. Si ya existe, es una repetición cuando ya tiene el dominio, o cuando está vacía (sin dominios ni bases, como la deja `v-add-user`) y su correo de contacto (`CONTACT`) es el de la cuenta. Con otra cosa el usuario es de otro, o tiene sitios que un `terminate` de este servicio borraría: `rejected` con `details.reason: "user_taken"`, sin tocar nada.
4. Si la cuenta tiene otro paquete, por ejemplo porque el staff repitió con otro plan un alta que había fallado, `v-change-user-package USER PACKAGE`.
5. `v-change-user-language USER` con el idioma de la cuenta (`es-AR` se manda como `es`). Si Hestia lo rechaza, el de `default_language`. Si tampoco, el alta termina igual con un aviso `language_not_applied` en `result.warnings`.
6. Si la cuenta no tiene el dominio, `v-add-domain USER DOMAIN`, sin IP. Si Hestia dice que el dominio ya es de otro usuario, `rejected` con `details.reason: "domain_rejected"` y su texto en `details.upstream`.
7. Se vuelve a leer: cuenta presente, activa, entera, con el dominio y con el paquete pedido.

Resultado: `{ "observed_state": "active", "external_ids": { "username": "c1042", "domain": "ejemplo.com.ar" } }`. Un corte entre el usuario y el dominio deja el alta en curso (ver "Operaciones largas"): la consulta encuentra el usuario y sigue con el dominio. Un alta que falla de forma definitiva deja lo creado para que decida el staff.

### suspend y unsuspend

Se lee el estado. Si no se pudo leer, error sin mutar. Si la cuenta no existe, `not_found`. Si está parcial, `conflict`. Si ya está en el estado pedido, termina sin mutar. Si no, `v-suspend-user USER` o `v-unsuspend-user USER` y se exige que la relectura llegue al estado pedido en el usuario y en los cinco recursos. Resultado: `observed_state` `suspended` o `active`.

### terminate

Borra sitio, correo, bases y DNS, y el plan gratuito no tiene backups. Por eso sigue la ausencia probada: si la cuenta ya no está, termina sin mutar. Si está, aunque sea en estado parcial, `v-delete-user USER` y la relectura tiene que probar la ausencia con el inventario. Un estado que no se pudo leer o un dominio ajeno no borran nada. Resultado: `observed_state: "terminated"`.

### change_plan

Recibe el paquete nuevo en `context.service.params.package`. Si la cuenta ya lo tiene, termina sin mutar. Si no, `v-change-user-package USER PACKAGE` y la relectura tiene que mostrar ese `PACKAGE`. Un rechazo de Hestia, por ejemplo por topes, da `rejected` con su texto en `details.upstream`. Hestia reaplica del paquete el shell, los nameservers y los backups: si el shell cambió, el resultado lo avisa con `shell_changed`. Después la cuenta tiene que seguir entera y con el dominio, con el mismo criterio que `get_status`. Si no, `conflict` con la razón en `details.reason`. Sirve para subir y bajar de plan entre todos los paquetes, no solo desde el gratuito.

### run_action

Entrada: `{ "action": "change_password", "credentials": { "password": "..." } }`. Verifica que la cuenta sea del servicio y corre `v-change-user-password USER PASSWORD`. Termina solo con `OK`. Una contraseña no se puede leer para verificarla, así que un corte da `outcome_unknown` y el núcleo repite con la misma clave y la misma contraseña, que es seguro. Otra acción da `not_supported`.

### get_status

| Estado de Hestia | `observed_state` |
|---|---|
| Cuenta entera y activa, con el dominio | `active` |
| Cuenta entera y suspendida, con el dominio | `suspended` |
| Ausencia probada con el inventario | `not_found` |
| Estado parcial | `unknown` con `details.reason: "partial"` y los recursos en `details.mixed` |
| Dominio que no es de la cuenta | `unknown` con `details.reason: "domain_mismatch"` |
| Respuesta que no prueba nada (otro usuario, inventario vacío, una lista con error) | `unknown` con la razón, por ejemplo `absence_unproven` |

`details` trae además el paquete (`package`), el `SUSPENDED` del usuario, la cantidad de recursos y cuántos están suspendidos por tipo (`resources`), y el disco y la transferencia usados y sus topes tal cual los da Hestia (`disk.used_mb`, `disk.quota_mb`, `bandwidth.used_mb`, `bandwidth.limit_mb`, con `unlimited` si no hay tope). Un corte o un JSON roto no son un estado: dan `upstream_unavailable`.

### view

Bloques de texto plano, en castellano o en inglés según el idioma de la cuenta:

```json
{
  "blocks": [
    { "type": "status", "level": "ok", "text": "Activo" },
    { "type": "fields", "items": [
      { "label": "Usuario del panel", "value": "c1042" },
      { "label": "Dominio", "value": "ejemplo.com.ar" },
      { "label": "Paquete", "value": "gainsboro" },
      { "label": "Disco", "value": "120 MB de 1000 MB" },
      { "label": "Transferencia", "value": "35 MB de ilimitado" }
    ] },
    { "type": "link", "label": "Entrar al panel", "url": "https://panel.ejemplo.com.ar:8083/login/" },
    { "type": "actions", "items": ["change_password"] }
  ]
}
```

`level` es `ok`, `warning` o `error`. Los bloques tienen la misma forma que en los demás módulos y en el módulo manual del núcleo: el color en `level` y las listas de `fields` y `actions` en `items`. El enlace va al login de Hestia sin credenciales, y su host es el de la instancia, que tiene que estar en la lista de hosts de la instancia del núcleo. Con un estado parcial o que no se pudo verificar se agrega un bloque `notice`. Si la cuenta no tiene el dominio del servicio (`domain_mismatch`, por ejemplo un mapeo mal importado), salen solo el estado y el aviso: la cuenta puede ser de otro cliente, así que no se muestran su uso, su paquete ni el enlace. El botón de webmail del módulo anterior no se porta, porque apuntaba a una dirección que no existe. El uso no se guarda: la facturación por consumo está fuera de v0.

### Operaciones largas

Si a una operación no le queda tiempo para mandar el próximo cambio, responde `pending` con `operation_id` igual a la operación más el ID del servicio, por ejemplo `suspend-svc1042`. La consulta `POST /v0/operations/{id}` llega con el sobre completo y retoma la operación desde el estado de Hestia, así que la puede contestar cualquier réplica. Un ID que no es de ese servicio da `not_found`.

El alta pasa a `pending` solo después de crear el usuario, porque la consulta no trae la contraseña. Si se acaba el tiempo antes, responde `upstream_unavailable` y el núcleo la repite con la misma clave. `run_action` nunca queda en curso.

Si un cambio queda sin respuesta (corte, plazo vencido, un 5xx o un cuerpo que no es de Hestia) y la relectura no lo confirma, Hestia puede seguir ejecutándolo, por ejemplo un `v-delete-user` de una cuenta grande. Mandarlo de nuevo duplicaría el efecto. Por eso la operación responde `pending` con `operation_id` igual a la operación, el ID del servicio, `-sent-` y el momento en que salió el cambio en segundos Unix, por ejemplo `terminate-svc1042-sent-1761600000`. Durante 10 minutos desde ese momento la consulta solo relee: si el estado ya llegó termina, y si no, sigue en curso con el mismo ID. Pasada la ventana, la consulta corre la operación entera desde el estado de Hestia. El alta antes de crear el usuario y `run_action`, que no pueden quedar en curso, responden `outcome_unknown`. El mutador tiene todo el tiempo de la operación menos 10 segundos para la relectura, y la lectura 30 segundos.

## Errores

| Caso | Código | HTTP |
|---|---|---|
| Configuración inválida, autenticación rechazada, IP no permitida o comando fuera del perfil | `invalid_config` | 422 |
| Certificado que no es de una CA confiable ni coincide con `tls_fingerprint` | `invalid_config`, con `tls_fingerprint` en `details.fields` | 422 |
| Paquete del producto inválido o inexistente | `invalid_config` | 422 |
| Dominio del pedido inválido u ocupado, correo o contraseña que faltan | `invalid_input` con `details.fields` | 422 |
| El dominio de `external_ids` no es de la cuenta | `invalid_input` con `["external_ids.domain"]`, sin mutar | 422 |
| Ausencia probada en `suspend`, `unsuspend`, `change_plan` o `run_action` | `not_found` | 404 |
| Estado parcial | `conflict` con `retry_after` 60 | 409 |
| Usuario o dominio de otro en `create`, o Hestia rechaza el cambio con un texto de error y un código menor a 500 y el estado no cambió | `rejected` con `details.upstream` | 422 |
| Conexión rechazada, sin respuesta en una lectura, JSON roto o un estado que no se pudo leer, antes de mandar un cambio | `upstream_unavailable` | 503 |
| Corte, 5xx o respuesta que no es de Hestia después de mandar un cambio, sin que la relectura confirme el estado | `pending` con `-sent-` en el ID (ver "Operaciones largas"). En el alta antes de crear el usuario y en `run_action`, `outcome_unknown` | 200 o 504 |
| Una relectura que falla después de un cambio que Hestia aceptó con `OK` | `outcome_unknown` con `details.reason: "unreadable_after_change"` | 504 |
| Acción que no es `change_password` | `not_supported` | 501 |

`details.reason` dice el caso concreto (por ejemplo `no_reply`, `not_converged`, `partial` o `absence_unproven`). Los mensajes son para el staff y nunca llevan la secret key ni contraseñas: si Hestia las repite en un error, se tapan con `***`. Los logs registran el comando y el código HTTP, nunca los argumentos ni el cuerpo.

Un estado parcial que dura, como un cron que no se suspendió, vuelve `conflict` en cada reintento hasta que el trabajo del núcleo se agota y queda para el staff.

## Cómo correrlo

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma que el núcleo tiene en la instalación de este módulo |
| `Module__PreviousSigningSecret` | Opcional. El secreto anterior, mientras dura una rotación |
| `Module__IdempotencyStorePath` | Archivo SQLite del registro de idempotencia. Por defecto `data/idempotency.db`. Tiene que sobrevivir a los reinicios |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo y el módulo no crea el directorio. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `ASPNETCORE_URLS` | Dónde escucha |

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:Module__IdempotencyStorePath = ".\datos\hestia\idempotency.db"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5193"
dotnet run --project src/modules/BillingOss.Module.Hestia -c Release
```

El protocolo exige TLS entre el núcleo y el módulo, salvo en el mismo host. La salida a internet necesaria es solo el host de Hestia en su puerto de API. Se corre con un usuario sin privilegios y con el sistema de archivos de solo lectura salvo el directorio del registro, que con el registro en PostgreSQL no hace falta.

### Alta disponibilidad

- **Sin estado en memoria.** Lo único que vive en el proceso es el caché de conexiones HTTP por huella de certificado, que no guarda nada de los pedidos.
- **Idempotencia en dos capas.** El registro de la SDK evita repetir una llamada con la misma clave, en todas las réplicas si comparten el registro en PostgreSQL (`Module__IdempotencyConnectionString`). Sin un registro común, por ejemplo después de una restauración a un punto anterior, lo evita la lectura previa: una repetición encuentra la cuenta ya suspendida, ya creada o ya borrada y no vuelve a mutar.
- **Marca externa y consulta previa.** Hestia no tiene dónde guardar otra marca, así que la marca es el usuario de las altas (sección 6 del protocolo): sale siempre igual para el mismo servicio, y antes de `v-add-user` el alta lee esa cuenta. Una repetición que cae en otra réplica, o que llega después de perder el registro de idempotencia (un volumen que no persiste o una restauración a un punto anterior), la encuentra y sigue desde ahí. Con el uuid en el usuario, el mismo ID de otra instalación que comparte el panel, o un ID que el núcleo vuelve a usar después de una restauración, da otro usuario y no choca con `user_taken`. Las cuentas que ya existían con el usuario sin uuid (las de antes de que el núcleo lo mande) solo se encuentran por `external_ids.username`.
- **Operaciones largas sin dueño.** El `operation_id` alcanza para retomar desde el estado de Hestia, así que la consulta la puede contestar cualquier réplica.
- **Concurrencia.** `max_in_flight` es 2 por instancia para no saturar el panel. Lo tiene que aplicar el núcleo, que hoy no lo lee.

## Pruebas

```
dotnet test tests/BillingOss.Module.Hestia.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y lo conectan a una Hestia falsa que simula los comandos sobre cuentas en memoria, con los textos de error de Hestia. Cada test puede pisar la respuesta de un comando, cortar la conexión antes o después de que Hestia lo ejecute y adelantar un reloj para agotar el tiempo de una operación. Cubren:

- casos de prueba del módulo anterior, portados a C#,
- el alta repetida después de un corte, también desde otra réplica,
- las operaciones largas con consulta desde otra réplica, y la ventana en que la consulta solo relee después de un cambio sin respuesta,
- los escenarios 1, 2, 3, 4, 5, 6 y 7 de la conformidad.

Las pruebas de transporte usan el cliente HTTP de producción contra un Kestrel con HTTPS y un certificado autofirmado generado en el momento: certificado rechazado, huella fijada, conexión rechazada y redirección que no se sigue.

### Prueba contra un Hestia de laboratorio

Nunca contra uno productivo. Hace falta una access key con el perfil completo. Sin las variables, la prueba queda inconclusa.

```
export HESTIA_TEST_HOST=hestia-lab.example.com
export HESTIA_TEST_ACCESS_KEY_ID=...
export HESTIA_TEST_SECRET_KEY=...
dotnet test tests/BillingOss.Module.Hestia.Tests --filter TestCategory=HestiaReal
```

Opcionales: `HESTIA_TEST_PORT`, `HESTIA_TEST_FINGERPRINT`, `HESTIA_TEST_PACKAGE` (por defecto `default`), `HESTIA_TEST_PACKAGE_2` para probar el cambio de plan, y `HESTIA_TEST_READONLY_ACCESS_KEY_ID` con `HESTIA_TEST_READONLY_SECRET_KEY`, una access key con el perfil de solo lectura del paralelo. Crea una cuenta `prueba` más un número con un dominio `.test` inventado y una contraseña como las del núcleo (20 caracteres de su alfabeto, con un guion al principio, un guion bajo y un punto). La suspende y reactiva dos veces, le cambia la contraseña, pide la vista y, con la key de solo lectura, lee el estado y comprueba que Hestia le rechaza la suspensión con `invalid_config`. Al final la borra y comprueba la ausencia.

Quedan como prueba manual en el laboratorio, porque el módulo no crea bases ni crons: la suspensión y la reactivación de una cuenta con una base y un cron (los cinco recursos), y la verificación posterior que no da por suspendida una cuenta con un recurso activo. La Hestia falsa sí cubre las dos.

## Pendiente de comprobar en el laboratorio

- Que Hestia 1.10.5 acepte las contraseñas que genera el núcleo (20 caracteres con guion, guion bajo y punto).
- La forma exacta de las respuestas de error por HTTP de la API: qué código HTTP acompaña cada código de salida de Hestia y el texto de un comando fuera del perfil. El módulo trata 401 y 403 como configuración, y si una contraseña rechazada en `v-change-user-password` llegara como 401 se vería como `invalid_config` y no como `rejected`.
- El texto de `v-search-domain-owner` cuando el dominio no existe. El script real sale con `E_NOTEXIST` sin imprimir nada, que la API pasa a un 404 vacío. El módulo acepta eso, un error de inexistencia, `OK` o una salida vacía como dominio libre.
- La unidad de `U_DISK` y `U_BANDWIDTH` (el módulo la muestra en MB) y que el correo de contacto venga en `CONTACT` en `v-list-user json`.
- Que `v-search-domain-owner` y `v-list-user-packages` se puedan incluir en el perfil de una access key.
- Si los paquetes de solo DNS permiten web. El módulo acepta el dominio en la lista de DNS, así que funciona en los dos casos.
- Qué hace `v-change-user-package` cuando el uso supera los topes del paquete nuevo.

## Fuera de esta versión

- El registro SRV automático de un producto con la dirección y el puerto del game server. Necesita campos personalizados por producto en el núcleo.
- El acceso directo al panel (`provisioning.sso`).
- La facturación por consumo de disco y transferencia.

## Pendiente en el núcleo y en el protocolo

El módulo cumple su parte. Para que tenga efecto, el núcleo tiene que:

- Implementar `run_action` con el control de `audience`, la clave por acción y generación, y la generación de las credenciales que nombra el `credentials` de cada acción (sección 5 del protocolo).
- Llamar a `change_plan` al aceptar un cambio de plan, a `view` con el caché de un minuto, a `validate` antes de confirmar un pedido y a `instance/test` al guardar una instancia.
- Aplicar `max_in_flight` por instancia.
- Tener una instancia en solo lectura para el mes en paralelo, que no mande mutadores y registre lo que habría hecho.
- No dar por hecha una operación de un producto aprovisionado cuyo servicio no tiene instancia.
- Tener un plazo fijo, por ejemplo de 90 días para un hosting gratuito, con aviso 10 días antes, reintento al día siguiente si la baja falla y sin plazo si el servicio pasa a un plan pago.
- En el importador, lo de la sección de migración.
