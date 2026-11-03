# Módulo de cPanel y WHM

Módulo de aprovisionamiento (`provisioning`) de Hornero para cuentas de hosting en cPanel, administradas por la API 1 de WHM. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md). Las operaciones, la marca con el uuid del servicio, la consulta previa, las operaciones largas y los errores son los de la [biblioteca de paneles de hosting](../../BillingOss.Modules.Hosting/README.md). Este documento cubre lo propio de cPanel. Se escribió de cero desde la documentación pública de la API de cPanel.

Capacidades: `provisioning.core`, `provisioning.change_plan`, `provisioning.status`, `provisioning.actions`, `provisioning.view`, `provisioning.sso` y `provisioning.usage`.

## Configuración de la instancia

| Campo | Secreto | Para qué |
|---|---|---|
| `host` | No | Nombre del servidor WHM, sin esquema, puerto ni ruta. Es también el host del login del cliente y del acceso directo, así que va el nombre con que el servidor se publica |
| `port` | No | Puerto de WHM con TLS. Por defecto 2087 |
| `tls_fingerprint` | No | SHA-256 del certificado de WHM, en hexadecimal, con o sin dos puntos. Solo si no lo firma una CA pública |
| `api_user` | No | Usuario dueño del token: `root` (por defecto) o un revendedor |
| `api_token` | Sí | Token de API de WHM |
| `username_prefix` | No | De 1 a 4 minúsculas, por defecto `c`. No puede empezar con `test`, porque cPanel no acepta esos usuarios |

Se validan antes de salir a la red. Un campo que no sirve da `invalid_config` con el campo en `details.fields`.

**Autenticación.** Con un token de API de WHM, nunca con la contraseña de root. Cada pedido lleva `Authorization: whm USUARIO:TOKEN`.

**Privilegios.** El token tiene que poder correr estas funciones: `listpkgs`, `listaccts`, `getdomainowner`, `createacct`, `suspendacct`, `unsuspendacct`, `removeacct`, `changepackage`, `passwd`, `create_user_session` y `showbw`. Un revendedor ve y cambia solo sus cuentas: con su token, una cuenta de otro dueño se ve como ausente, así que todas las cuentas de la instancia tienen que ser suyas. Para el mes en paralelo de quien migra conviene un token sin los privilegios que cambian cuentas. Con ese, WHM rechaza cualquier cambio aunque el núcleo lo mande por error.

**Acceso por IP.** Si el servidor limita el acceso a WHM por IP, hay que sumar la IP de salida de cada réplica del módulo. Un rechazo del token, de un privilegio o de la IP da `invalid_config` con el texto de WHM en `details.upstream` y un mensaje que dice qué revisar.

`instance/test` corre `listpkgs` y devuelve los paquetes para cargar los productos: `{ "plans": ["basico", "pro"] }`.

## Funciones de WHM

Todas son `POST https://{host}:{port}/json-api/{función}?api.version=1` con los parámetros en el cuerpo, así que una contraseña nunca va en la URL. Cada respuesta trae `metadata.result` (1 es éxito) y `metadata.reason`. Un cambio con resultado 0 y un código menor a 500 es un rechazo, con el texto de `reason`. Un 5xx o un cuerpo que no es de WHM quedan como cambio sin respuesta, porque pueden venir de algo intermedio con la función todavía corriendo.

| Operación | Funciones |
|---|---|
| `instance/test` | `listpkgs` |
| `validate` | `listpkgs` y `getdomainowner` |
| `create` | `listaccts`, `getdomainowner` y `createacct` con `username`, `domain`, `plan`, `password` y `contactemail`. Si la cuenta ya existía con otro paquete, `changepackage` |
| `suspend`, `unsuspend` | `suspendacct` o `unsuspendacct` con `user` |
| `terminate` | `removeacct` con `username` (el nombre de la API 1. El viejo `user` está deprecado) |
| `change_plan` | `changepackage` con `user` y `pkg` |
| `run_action` | `passwd` con `user` y `password` |
| `get_status`, `view` | `listaccts` |
| `sso` | `create_user_session` con `user` y `service=cpaneld` |
| `usage` | `listaccts` y `showbw` |

Cada operación con efecto relee con `listaccts` antes de contestar.

## Lectura de estado

`listaccts` con `searchtype=user`, `search=USUARIO` y `searchmethod=exact`. El módulo además se queda solo con la fila de ese usuario exacto, por si WHM ignora el método. La cuenta está ausente solo si WHM contestó con resultado 1 una lista que no la tiene. De la fila salen el dominio principal (`domain`), que tiene que ser el del servicio, el paquete (`plan`) y la suspensión (`suspended` en 1 o 0). Las cuentas que crea el módulo tienen el dominio del servicio como principal.

`getdomainowner` dice el dueño de cualquier dominio del servidor, también adicionales y estacionados. Un `user` vacío es un dominio libre.

cPanel no tiene dónde guardar la marca del servicio: la marca es el usuario, que sale del uuid.

## Acceso directo

`create_user_session` crea una sesión de cPanel y devuelve su `url` y su vencimiento en `expires`, en segundos Unix. La sesión es de un solo uso: el navegador la activa con el primer pedido. La URL tiene que ser `https` y su host el de `host`. Si WHM la arma con otro nombre, el acceso da `invalid_config` y hay que poner en `host` el nombre del servidor. A una cuenta suspendida no se le ofrece: WHM igual crea la sesión, pero no deja entrar.

## Uso

El disco (`disk_gb`) sale de `diskused` en `listaccts`, en MiB con una `M` pegada. La transferencia del mes en curso (`transfer_gb`) sale de `totalbytes` en `showbw` con `searchtype=user`, en bytes. El módulo los pasa a GB de 1024 MiB. Si `showbw` no trae la fila del usuario o un valor que se pueda leer, la respuesta va sin `transfer_gb`.

## Migración

Para quien migra a Hornero desde un sistema de facturación con servidores cPanel. Lo que sigue es el procedimiento para otro proveedor, con el mapa de módulos del importador (sección 3 de `diseno/importacion.md`).

**Qué hay en la copia.** El tipo de servidor y el `servertype` de los productos salen de `BillingOss.Importer distinct <copia> tblservers type` y de `distinct <copia> tblproducts servertype`. Para saber en qué columna `configoptionN` de los productos está el nombre del paquete de WHM, se compara `distinct <copia> tblproducts servertype configoption1 configoption2` con los paquetes que devuelve `instance/test`. Son columnas de configuración, sin datos personales.

**Instancias.** Una por cada servidor WHM del origen (`tblservers`), con `host` igual a su `hostname` y `port` si el origen usaba otro puerto. Se cargan en la base destino antes de importar. El token no se importa: el origen guarda sus credenciales de servidor cifradas y no se descifran por fuera. El staff crea un token nuevo en WHM y lo carga desde la bóveda.

**Mapa.** Una entrada por `servertype`, o por `servertype:servidor` si hay más de un servidor:

```json
{
  "<servertype>:<servidor>": {
    "instance": "whm1",
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
| `ExternalIds.domain` | `tblhosting.domain`, en minúsculas. Es la prueba de que la cuenta es del servicio |
| Contraseña del servicio | No se importa. La tiene WHM, y si el cliente la necesita se usa `change_password` |

Las cuentas importadas conservan su usuario y no tienen marca: las reconoce el dominio. Solo las altas nuevas usan el usuario que sale del uuid. Los productos que crean revendedores de WHM se importan como manuales: este módulo no los crea.

**Conciliación antes del corte.** Con el token sin privilegios de cambio, `get_status` sobre cada servicio vivo importado, informando sin corregir nada: ausentes (`not_found`), dominio ajeno (`unknown` con `domain_mismatch`), paquete distinto del del producto (`details.plan`) y suspendidos que no coinciden con el origen. Aparte, `listaccts` sin búsqueda (solo lectura) lista las cuentas del servidor que no corresponden a ningún servicio vivo. Cada diferencia se resuelve o queda explicada antes del corte.

## Pruebas

```
dotnet test tests/BillingOss.Module.CPanel.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y lo conectan a un WHM falso que simula las funciones de la API 1 sobre cuentas en memoria, con la forma de sus respuestas. Cada test puede pisar la respuesta de una función o cortar la conexión antes o después de que WHM la ejecute. Cubren el manifiesto, la configuración que no sirve, el token y los privilegios rechazados, la forma de cada pedido (método, `api.version`, encabezado y parámetros en el cuerpo), el ciclo completo, la consulta previa con `getdomainowner`, los rechazos de WHM con secretos tapados, las respuestas que no son de WHM, los 5xx en un cambio, el acceso directo, el uso y los secretos testigo.

### Prueba contra un WHM de laboratorio

Nunca contra uno productivo. Sin las variables, la prueba queda inconclusa.

```
export CPANEL_TEST_HOST=whm-lab.example.com
export CPANEL_TEST_TOKEN=...
dotnet test tests/BillingOss.Module.CPanel.Tests --filter TestCategory=CPanelReal
```

Opcionales: `CPANEL_TEST_USER` (por defecto `root`), `CPANEL_TEST_PORT`, `CPANEL_TEST_FINGERPRINT`, `CPANEL_TEST_PLAN` (por defecto `default`) y `CPANEL_TEST_PLAN_2` para probar el cambio de plan. Crea una cuenta con prefijo `bo` y un dominio `.test` inventado, la suspende y reactiva dos veces, le cambia la contraseña, pide estado, vista, acceso directo y uso, y la borra al final, comprobando la ausencia.

## Pendiente de comprobar en el laboratorio

- Que la API 1 tome los parámetros del cuerpo del POST en todas estas funciones, con `api.version` en la URL.
- Que `listaccts` sin coincidencias conteste resultado 1 con `acct` como lista vacía. El módulo toma cualquier otra forma como una lectura que falló, nunca como ausencia: si WHM contesta distinto, ninguna alta pasa de la lectura previa.
- La respuesta de `getdomainowner` para un dominio que no existe. El módulo toma un `user` vacío o nulo como libre, y un resultado 0 como una lectura que falló.
- Los nombres y las unidades de `showbw` (`totalbytes` en bytes), que acepte `searchtype=user` y que la transferencia sea la del mes en curso.
- Los nombres de los privilegios de un token limitado y el texto con que WHM rechaza uno que falta. El módulo toma como configuración los textos con `access denied`, `permission`, `not allowed` o `invalid api token`.
- Que WHM acepte las contraseñas que genera el núcleo (20 caracteres con guion, guion bajo y punto) con su fuerza mínima.
- Que `create_user_session` arme la URL con el nombre del servidor.
- Si `createacct` contesta resultado 1 cuando la cuenta queda creada con avisos, por ejemplo del clúster de DNS. El módulo igual decide por la relectura.

## Fuera de esta versión

- Cuentas de revendedor, IP dedicada al crear, listas de funciones y opciones por cuenta que no vienen del paquete.
- Cambiar el dominio principal de una cuenta.
- Correo, bases de datos y DNS desde el portal: el cliente los maneja en cPanel con el acceso directo.
