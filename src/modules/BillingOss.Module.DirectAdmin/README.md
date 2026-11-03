# Módulo de DirectAdmin

Módulo de aprovisionamiento (`provisioning`) de Hornero para cuentas de hosting en DirectAdmin, administradas por su API de comandos. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md). Las operaciones, la marca con el uuid del servicio, la consulta previa, las operaciones largas y los errores son los de la [biblioteca de paneles de hosting](../../BillingOss.Modules.Hosting/README.md). Este documento cubre lo propio de DirectAdmin. Se escribió de cero desde la documentación pública de la API de DirectAdmin.

Capacidades: `provisioning.core`, `provisioning.change_plan`, `provisioning.status`, `provisioning.actions`, `provisioning.view`, `provisioning.sso` y `provisioning.usage`.

## Configuración de la instancia

| Campo | Secreto | Para qué |
|---|---|---|
| `host` | No | Nombre del servidor DirectAdmin, sin esquema, puerto ni ruta. Es también el host del login del cliente y del acceso directo, así que va el nombre con que el servidor se publica |
| `port` | No | Puerto de DirectAdmin con TLS, el mismo para la API y para el panel. Por defecto 2222 |
| `tls_fingerprint` | No | SHA-256 del certificado de DirectAdmin, en hexadecimal, con o sin dos puntos. Solo si no lo firma una CA pública |
| `api_user` | No | Usuario administrador dueño de la clave. Por defecto `admin` |
| `api_key` | Sí | Clave de acceso (login key) del administrador |
| `ip_address` | No | IPv4 compartida del servidor donde van las cuentas nuevas |
| `username_prefix` | No | De 1 a 4 minúsculas, por defecto `c` |

Se validan antes de salir a la red. Un campo que no sirve da `invalid_config` con el campo en `details.fields`. `api_user` no puede tener `:` ni `|`, que en DirectAdmin separan la clave y el usuario como el que se entra.

**Autenticación.** Con una clave de acceso del administrador, nunca con su contraseña. Cada pedido lleva autenticación básica con `api_user` y la clave. DirectAdmin deja limitar la clave a una lista de comandos y de IP: conviene darle solo los comandos de la tabla de abajo y la IP de salida de cada réplica del módulo.

**Privilegios.** Hace falta el administrador: la lectura de la cuenta usa la lista de todos los usuarios del servidor, y el acceso directo entra como el usuario. Con un revendedor, DirectAdmin rechaza esos comandos. Un rechazo de la clave, de un comando o de la IP da `invalid_config` con el texto de DirectAdmin en `details.upstream` y un mensaje que dice qué revisar. Para el mes en paralelo de quien migra conviene una clave sin los comandos que cambian cuentas. Con esa, DirectAdmin rechaza cualquier cambio aunque el núcleo lo mande por error.

`instance/test` lee los paquetes de usuario y los devuelve para cargar los productos: `{ "plans": ["basico", "pro"] }`.

## Comandos de DirectAdmin

Las lecturas son `GET https://{host}:{port}/CMD_API_...` con los parámetros en la URL, que nunca llevan secretos. Los cambios son `POST` con el formulario en el cuerpo, así que una contraseña nunca va en la URL. Las respuestas vienen codificadas como formulario. Un cambio trae `error` en 0, o en 1 con `text` y `details`. Un cambio con `error` en 1 y un código menor a 500 es un rechazo, con el texto de `text` y `details`. Un 5xx o un cuerpo que no es de DirectAdmin, como una página HTML, quedan como cambio sin respuesta, porque pueden venir de algo intermedio con el comando todavía corriendo.

| Operación | Comandos |
|---|---|
| `instance/test` | `CMD_API_PACKAGES_USER` |
| `validate` | `CMD_API_PACKAGES_USER` y `CMD_API_DOMAIN_OWNERS` |
| `create` | `CMD_API_SHOW_ALL_USERS`, `CMD_API_DOMAIN_OWNERS` y `CMD_API_ACCOUNT_USER` con `action=create`, `add=Submit`, `username`, `email`, `passwd`, `passwd2`, `domain`, `package`, `ip` y `notify=no`. Si la cuenta ya existía con otro paquete, `CMD_API_MODIFY_USER` |
| `suspend`, `unsuspend` | `CMD_API_SELECT_USERS` con `location=CMD_SELECT_USERS`, `suspend=Suspend` o `suspend=Unsuspend` y `select0` |
| `terminate` | `CMD_API_SELECT_USERS` con `confirmed=Confirm`, `delete=yes` y `select0` |
| `change_plan` | `CMD_API_MODIFY_USER` con `action=package`, `user` y `package` |
| `run_action` | `CMD_API_USER_PASSWD` con `username`, `passwd` y `passwd2` |
| `get_status`, `view` | `CMD_API_SHOW_ALL_USERS` y `CMD_API_SHOW_USER_CONFIG` |
| `sso` | `CMD_API_LOGIN_KEYS` con `action=create` y `type=one_time_url`, entrando como el usuario |
| `usage` | La lectura de la cuenta y `CMD_API_SHOW_USER_USAGE` |

Cada operación con efecto relee con `CMD_API_SHOW_ALL_USERS` y `CMD_API_SHOW_USER_CONFIG` antes de contestar.

## Lectura de estado

La cuenta está ausente solo si `CMD_API_SHOW_ALL_USERS` contestó una lista (`list[]`) que no la tiene. Una lista vacía es un cuerpo vacío. Un cuerpo con otras claves, con `error` o en HTML no prueba nada y da `upstream_unavailable`. Si el usuario está, `CMD_API_SHOW_USER_CONFIG` da el dominio principal (`domain`), que tiene que ser el del servicio, el paquete (`package`) y la suspensión (`suspended` en `yes` o `no`). Las cuentas que crea el módulo tienen el dominio del servicio como principal.

`CMD_API_DOMAIN_OWNERS` sin parámetros da todos los dominios del servidor, principales y adicionales, con su usuario. Un dominio que no está en la lista está libre. Si alguna clave no es un dominio o algún valor no es un usuario, la respuesta no se lee y el alta no sigue.

DirectAdmin no tiene dónde guardar la marca del servicio: la marca es el usuario, que sale del uuid. Con el prefijo por defecto tiene 8 caracteres, menos que los 10 que DirectAdmin acepta por defecto.

## Acceso directo

El administrador pide una URL de un solo uso entrando como el usuario: la autenticación básica lleva `api_user|usuario` y la clave. DirectAdmin devuelve la URL en `details` o en `url`. Tiene que ser `https` y su host el de `host`: si DirectAdmin la arma con otro nombre, el acceso da `invalid_config` y hay que poner en `host` el nombre del servidor. DirectAdmin no informa el vencimiento, así que la respuesta no trae `expires_at`. La URL lleva la sesión y nunca va a los logs.

## Uso

`CMD_API_SHOW_USER_USAGE` da el disco (`quota`, que va en `disk_gb`) y la transferencia del mes (`bandwidth`, que va en `transfer_gb`) en MB con decimales. El módulo los pasa a GB de 1024 MB. Un valor que falta o que no se puede leer no va en la respuesta.

## Migración

Para quien migra a Hornero desde un sistema de facturación con servidores DirectAdmin. Lo que sigue es el procedimiento para otro proveedor, con el mapa de módulos del importador (sección 3 de `diseno/importacion.md`).

**Qué hay en la copia.** El tipo de servidor y el `servertype` de los productos salen de `BillingOss.Importer distinct <copia> tblservers type` y de `distinct <copia> tblproducts servertype`. Para saber en qué columna `configoptionN` de los productos está el nombre del paquete de usuario, se compara `distinct <copia> tblproducts servertype configoption1 configoption2` con los paquetes que devuelve `instance/test`. Son columnas de configuración, sin datos personales.

**Instancias.** Una por cada servidor DirectAdmin del origen (`tblservers`), con `host` igual a su `hostname`, `port` si el origen usaba otro puerto e `ip_address` con la IP compartida del servidor. Se cargan en la base destino antes de importar. La clave no se importa: el origen guarda sus credenciales de servidor cifradas y no se descifran por fuera. El staff crea una clave de acceso nueva en DirectAdmin y la carga desde la bóveda.

**Mapa.** Una entrada por `servertype`, o por `servertype:servidor` si hay más de un servidor:

```json
{
  "<servertype>:<servidor>": {
    "instance": "da1",
    "params": { "plan": "configoptionN" },
    "external_ids": { "username": "username", "domain": "domain" }
  }
}
```

| Dato de Hornero | De dónde sale |
|---|---|
| `ModuleInstanceId` | La instancia del servidor del servicio (`tblhosting.server`) |
| `ModuleParams.plan` | La columna del paquete en el producto. Sin esto `create` y `change_plan` no reciben el paquete |
| `ExternalIds.username` | `tblhosting.username` tal cual. Si no cumple el formato de usuario va a la lista de incidencias |
| `ExternalIds.domain` | `tblhosting.domain`, en minúsculas. Tiene que ser el dominio principal del usuario: es la prueba de que la cuenta es del servicio |
| Contraseña del servicio | No se importa. La tiene DirectAdmin, y si el cliente la necesita se usa `change_password` |

Las cuentas importadas conservan su usuario y no tienen marca: las reconoce el dominio principal. Solo las altas nuevas usan el usuario que sale del uuid. Los productos de revendedor de DirectAdmin se importan como manuales: este módulo no los crea.

**Conciliación antes del corte.** Con la clave sin los comandos de cambio, `get_status` sobre cada servicio vivo importado, informando sin corregir nada: ausentes (`not_found`), dominio principal ajeno (`unknown` con `domain_mismatch`, por ejemplo un usuario que cambió su dominio principal), paquete distinto del del producto (`details.plan`) y suspendidos que no coinciden con el origen. Aparte, `CMD_API_SHOW_ALL_USERS` (solo lectura) lista los usuarios del servidor que no corresponden a ningún servicio vivo. Cada diferencia se resuelve o queda explicada antes del corte.

## Pruebas

```
dotnet test tests/BillingOss.Module.DirectAdmin.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y lo conectan a un DirectAdmin falso que simula los comandos sobre usuarios en memoria, con la forma de sus respuestas codificadas como formulario. Cada test puede pisar la respuesta de un comando o cortar la conexión antes o después de que DirectAdmin lo ejecute. Cubren el manifiesto, la configuración que no sirve, la clave y los comandos rechazados (también en un cambio), la forma de cada pedido (método, autenticación básica, parámetros en la URL o en el cuerpo), el ciclo completo, la consulta previa con dominios principales y adicionales, el usuario tomado, los rechazos de DirectAdmin con secretos tapados, las lecturas que no son de DirectAdmin, la lista de dominios que no se lee, los 5xx en un cambio, el acceso directo entrando como el usuario, el uso y los secretos testigo.

### Prueba contra un DirectAdmin de laboratorio

Nunca contra uno productivo. Sin las variables, la prueba queda inconclusa.

```
export DIRECTADMIN_TEST_HOST=da-lab.example.com
export DIRECTADMIN_TEST_KEY=...
export DIRECTADMIN_TEST_IP=192.0.2.20
export DIRECTADMIN_TEST_PLAN=basico
dotnet test tests/BillingOss.Module.DirectAdmin.Tests --filter TestCategory=DirectAdminReal
```

Opcionales: `DIRECTADMIN_TEST_USER` (por defecto `admin`), `DIRECTADMIN_TEST_PORT`, `DIRECTADMIN_TEST_FINGERPRINT` y `DIRECTADMIN_TEST_PLAN_2` para probar el cambio de plan. Crea un usuario con prefijo `bo` y un dominio `.test` inventado, lo suspende y reactiva dos veces, le cambia la contraseña, pide estado, vista, acceso directo y uso, y lo borra al final, comprobando la ausencia.

## Pendiente de comprobar en el laboratorio

- Los parámetros de suspensión de `CMD_API_SELECT_USERS` (`suspend=Suspend` y `suspend=Unsuspend` con `location`) en la versión instalada, que en versiones nuevas también acepta otros nombres.
- Que `CMD_API_SHOW_ALL_USERS` conteste un cuerpo vacío cuando no hay usuarios, y `CMD_API_DOMAIN_OWNERS` sin parámetros la lista completa de dominios y dueños.
- El nombre y la forma de la URL de un solo uso de `CMD_API_LOGIN_KEYS` con `type=one_time_url`, que la clave de acceso pueda entrar como el usuario, y que la URL use el nombre del servidor.
- Las unidades de `CMD_API_SHOW_USER_USAGE` (MB de 1024 KB, con decimales) y que `bandwidth` sea la transferencia del mes en curso.
- Los textos con que DirectAdmin rechaza una clave sin un comando o una IP bloqueada. El módulo toma como configuración los textos con `access denied`, `do not have access`, `not allowed`, `unauthorized`, `invalid login` o `blacklisted`, y los HTTP 401 y 403.
- Que DirectAdmin acepte las contraseñas que genera el núcleo con su política de contraseñas.
- Si `CMD_API_ACCOUNT_USER` contesta `error=0` cuando el usuario queda creado con avisos, por ejemplo del DNS. El módulo igual decide por la relectura.

## Fuera de esta versión

- Cuentas y paquetes de revendedor, IP dedicada al crear y límites por usuario que no vienen del paquete.
- Cambiar el dominio principal de una cuenta.
- La API nueva de DirectAdmin en JSON. El módulo usa la API de comandos, que está en todas las versiones.
- Correo, bases de datos y DNS desde el portal: el cliente los maneja en DirectAdmin con el acceso directo.
