# Módulo de Plesk

Módulo de aprovisionamiento (`provisioning`) de Hornero para cuentas de hosting en Plesk, administradas por su API XML. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md). Las operaciones, la marca con el uuid del servicio, la consulta previa, las operaciones largas y los errores son los de la [biblioteca de paneles de hosting](../../BillingOss.Modules.Hosting/README.md). Este documento cubre lo propio de Plesk. Se escribió de cero desde la documentación pública de la API XML de Plesk.

Capacidades: `provisioning.core`, `provisioning.change_plan`, `provisioning.status`, `provisioning.actions`, `provisioning.view`, `provisioning.sso` y `provisioning.usage`.

## La cuenta en Plesk

La cuenta de un servicio son dos objetos de Plesk:

- Un **cliente** (customer) con el usuario del servicio como login, la contraseña que genera el núcleo, el nombre y el correo de la cuenta, y la marca: el uuid del servicio en su `external-id`.
- Una **suscripción** (webspace) de ese cliente con el dominio del servicio, el plan de servicio del producto, la IP de la instancia y un usuario de FTP con el mismo nombre y la misma contraseña.

Las altas nuevas crean un cliente por servicio. Un cliente importado puede tener varias suscripciones de servicios distintos: el módulo nunca toca las que no son del dominio del servicio.

## Configuración de la instancia

| Campo | Secreto | Para qué |
|---|---|---|
| `host` | No | Nombre del servidor Plesk, sin esquema, puerto ni ruta. Es también el host del login del cliente y del acceso directo, así que va el nombre con que el servidor se publica |
| `port` | No | Puerto de Plesk con TLS, el mismo para la API y para el panel. Por defecto 8443 |
| `tls_fingerprint` | No | SHA-256 del certificado de Plesk, en hexadecimal, con o sin dos puntos. Solo si no lo firma una CA pública |
| `api_key` | Sí | Clave secreta de la API de Plesk |
| `ip_address` | No | IPv4 compartida de Plesk donde van las suscripciones nuevas |
| `username_prefix` | No | De 1 a 4 minúsculas, por defecto `c` |

Se validan antes de salir a la red. Un campo que no sirve da `invalid_config` con el campo en `details.fields`.

**Autenticación.** Con una clave secreta de la API, nunca con la contraseña del administrador. Cada pedido la lleva en el encabezado `KEY`. Plesk ata la clave a una IP: se crea en el servidor con la utilidad `secret_key` de Plesk para la IP de salida del módulo, y hace falta una por cada IP si hay varias réplicas con salidas distintas. Una clave rechazada, de otra IP o sin permiso da `invalid_config` con el texto de Plesk en `details.upstream` y un mensaje que dice qué revisar.

**Privilegios.** La clave es del administrador: ve todos los clientes y suscripciones del servidor y puede cambiarlos. Plesk no tiene claves de solo lectura. Para el mes en paralelo de quien migra, los servicios se importan sin atarlos a la instancia (sección 3 de `diseno/importacion.md`), así ningún cambio llega a Plesk aunque el núcleo lo mande por error.

`instance/test` lee los planes de servicio y los devuelve para cargar los productos: `{ "plans": ["Default Domain", "Unlimited"] }`.

## Paquetes de Plesk

Todos son `POST https://{host}:{port}/enterprise/control/agent.php` con `Content-Type: text/xml` y un paquete (`<packet>`) que lleva una o más operaciones, así que ninguna contraseña va en la URL. Cada operación vuelve con sus resultados: `status` en `ok`, o `error` con `errcode` y `errtext`. El código 1013 (el objeto no existe) es la ausencia probada. Los códigos 1001 (autenticación) y 1006 (permiso) son configuración. Un error de todo el paquete viene en `<system>`.

Un cambio es éxito solo si todas sus operaciones vuelven en `ok`, y aun así se decide por la relectura. Un error con un código menor a 500 es un rechazo, con el texto de `errtext` y su código. Un 5xx, un cuerpo que no es un paquete de Plesk o un paquete sin los resultados quedan como cambio sin respuesta, porque pueden venir de algo intermedio con el pedido todavía corriendo.

| Operación | Paquete |
|---|---|
| `instance/test` | `service-plan/get` |
| `validate` | `service-plan/get`, y `webspace/get` con `site/get` por el dominio |
| `create` | La lectura de la cuenta, la consulta previa y `customer/add` con `webspace/add` en un mismo paquete. Si el cliente quedó de un alta anterior de este servicio, solo `webspace/add` |
| `suspend`, `unsuspend` | `webspace/set` con `status` en 16 o en 0 |
| `terminate` | `customer/del` si el cliente no tiene otra suscripción. Si tiene otras, `webspace/del` del dominio |
| `change_plan` | `service-plan/get` y `webspace/switch-subscription` con el GUID del plan |
| `run_action` | `customer/set` con `passwd` y `webspace/set` con `ftp_password`, en un mismo paquete |
| `get_status`, `view` | La lectura de la cuenta |
| `sso` | `server/create_session` |
| `usage` | `webspace/get` con `gen_info` y `stat` |

La lectura de la cuenta es un solo paquete: `customer/get` por login, `webspace/get` por `owner-login` con los conjuntos `gen_info` y `subscriptions`, y `service-plan/get`. Cada operación con efecto relee así antes de contestar.

## Lectura de estado

| En Plesk | La cuenta |
|---|---|
| No hay cliente con ese login | Ausente |
| El cliente tiene la suscripción del dominio | La del servicio, con su plan y su estado |
| El cliente no tiene ninguna suscripción | Un alta a medias, o un cliente vacío. La marca dice de qué servicio es |
| El cliente tiene otras suscripciones y no la del dominio | Ausente para este servicio: las otras son de otros servicios |

El plan es el plan de servicio cuya GUID figura en las suscripciones de la suscripción. Una suscripción sin plan (personalizada) no tiene plan, y `create` o `change_plan` la pasan al del producto. Cualquier `status` distinto de 0 cuenta como suspendida: por el administrador (16), por un revendedor, por el cliente o por vencimiento.

El dominio se compara con el `ascii-name` de la suscripción, en punycode, o con su `name` si no viene.

## Marca y consulta previa

La marca es el uuid del servicio en el `external-id` del cliente. Un `external-id` que no es un uuid lo dejó otro sistema, por ejemplo el de facturación anterior, y no cuenta como marca: ese cliente se reconoce por el dominio, como uno sin marca. Con la marca:

- **Adopción.** Si el dominio ya es la suscripción de un cliente con la marca del servicio, el alta adopta ese cliente aunque su login sea otro, por ejemplo de un alta con otro prefijo.
- **Alta a medias.** `customer/add` y `webspace/add` van en el mismo paquete, pero Plesk puede crear el cliente y rechazar la suscripción, por ejemplo porque el usuario de FTP ya existe. Un reintento encuentra el cliente con la marca y sin suscripción, y manda solo `webspace/add`. Una baja de ese cliente lo borra.
- **Usuario tomado.** Si el cliente del login ya tiene otra suscripción, o no tiene la marca del servicio, el alta se frena con `user_taken` sin tocar nada. Incluye el caso de un alta anterior del mismo servicio con otro dominio: lo resuelve el staff.

La consulta previa pregunta por el dominio como suscripción (`webspace/get`) y como sitio adicional de otra (`site/get`), en un mismo paquete. El dueño de una suscripción sale de su `owner-id` con `customer/get` por ID. Si no es un cliente, porque es del administrador o de un revendedor, o si el dominio es un sitio adicional, el dominio está ocupado: `domain_taken`.

El plan del producto se controla antes de mandar el alta. Un plan que no existe da `invalid_config` sin crear nada, en vez de dejar un cliente sin suscripción.

## Suspensión y baja

La suspensión es de la suscripción, no del cliente: un cliente importado puede tener otras suscripciones que siguen andando, y el cliente puede seguir entrando al panel. El estado propio del cliente no se lee ni se cambia.

La baja borra el cliente, que se lleva su suscripción, si no tiene otra. Si tiene otras, borra solo la suscripción del dominio y el cliente queda. Antes de borrar se vuelve a leer la lista de suscripciones del cliente.

## Contraseña

`change_password` cambia en un solo paquete la contraseña del cliente, con la que entra al panel, y la del usuario de FTP de la suscripción, que el alta crea igual. Si una de las dos falla, el paquete vuelve rechazado y repetir con la misma contraseña es seguro. En un cliente importado con varias suscripciones, la contraseña del cliente vale para todas.

## Acceso directo

`server/create_session` crea una sesión del cliente atada a la IP del navegador, que va en `user_ip` en base64. El navegador la abre en `https://{host}:{port}/enterprise/rsession_init.php?PLESKSESSID={sesión}`. Por eso el acceso directo exige `input.client_ip`: sin ella da `invalid_input`. Plesk no informa el vencimiento, así que la respuesta no trae `expires_at`. La URL lleva la sesión y nunca va a los logs.

## Uso

De `webspace/get` con `gen_info` y `stat`: el disco (`disk_gb`) es `real_size` y la transferencia del mes (`transfer_gb`) es `stat/traffic`, los dos en bytes. El módulo los pasa a GB de 1024 MiB. Un valor que falta o que no se puede leer no va en la respuesta.

## Migración

Para quien migra a Hornero desde un sistema de facturación con servidores Plesk. Lo que sigue es el procedimiento para otro proveedor, con el mapa de módulos del importador (sección 3 de `diseno/importacion.md`).

**Qué hay en la copia.** El tipo de servidor y el `servertype` de los productos salen de `BillingOss.Importer distinct <copia> tblservers type` y de `distinct <copia> tblproducts servertype`. Para saber en qué columna `configoptionN` de los productos está el nombre del plan de servicio, se compara `distinct <copia> tblproducts servertype configoption1 configoption2` con los planes que devuelve `instance/test`. Son columnas de configuración, sin datos personales.

**Instancias.** Una por cada servidor Plesk del origen (`tblservers`), con `host` igual a su `hostname`, `port` si el origen usaba otro puerto e `ip_address` con la IP compartida del servidor. Se cargan en la base destino antes de importar. La clave no se importa: el origen guarda sus credenciales de servidor cifradas y no se descifran por fuera. El staff crea una clave nueva en Plesk para la IP del módulo y la carga desde la bóveda.

**Mapa.** Una entrada por `servertype`, o por `servertype:servidor` si hay más de un servidor:

```json
{
  "<servertype>:<servidor>": {
    "instance": "plesk1",
    "params": { "plan": "configoptionN" },
    "external_ids": { "username": "username", "domain": "domain" }
  }
}
```

| Dato de Hornero | De dónde sale |
|---|---|
| `ModuleInstanceId` | La instancia del servidor del servicio (`tblhosting.server`) |
| `ModuleParams.plan` | La columna del plan de servicio en el producto. Sin esto `create` y `change_plan` no reciben el plan |
| `ExternalIds.username` | `tblhosting.username`, que tiene que ser el login del cliente de Plesk dueño de la suscripción. Si el origen guardaba otro usuario, por ejemplo el de FTP, la conciliación lo muestra como ausente y se corrige con `services` en el mapa |
| `ExternalIds.domain` | `tblhosting.domain`, en minúsculas. Es el nombre de la suscripción y la prueba de que es del servicio |
| Contraseña del servicio | No se importa. La tiene Plesk, y si el cliente la necesita se usa `change_password` |

Los clientes importados conservan su login y no tienen marca: los reconoce el dominio de la suscripción. Si el sistema anterior les dejó su propio ID en el `external-id`, el módulo no lo toma como marca ni lo cambia. Si ese ID es un uuid, en cambio, parece la marca de otro servicio y las operaciones dan `manual_review` con `marker_mismatch`: la conciliación los muestra como `unknown` y se vacía el `external-id` en Plesk antes del corte. Solo las altas nuevas usan el usuario que sale del uuid. Los productos de revendedor de Plesk se importan como manuales: este módulo no los crea.

**Conciliación antes del corte.** `get_status` sobre cada servicio vivo importado, que solo lee, informando sin corregir nada: ausentes (`not_found`, que incluye un login que no es el del dueño de la suscripción), plan distinto del del producto (`details.plan`) y suspendidos que no coinciden con el origen. Aparte, `webspace/get` sin filtro de dueño (solo lectura) lista las suscripciones del servidor que no corresponden a ningún servicio vivo. Cada diferencia se resuelve o queda explicada antes del corte.

## Pruebas

```
dotnet test tests/BillingOss.Module.Plesk.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y lo conectan a un Plesk falso que simula las operaciones de la API XML sobre clientes, suscripciones, planes y sitios en memoria, con la forma de sus paquetes de respuesta. Cada test puede pisar la respuesta de un paquete o cortar la conexión antes o después de que Plesk lo ejecute. Cubren el manifiesto, la configuración que no sirve, la clave y los permisos rechazados, la forma de cada paquete (ruta, tipo de contenido, encabezado y operaciones), el ciclo completo, la consulta previa con suscripciones de otro cliente, del administrador y sitios adicionales, la adopción por marca, el alta a medias y su reintento, el usuario tomado, el plan inexistente, la baja de un cliente con otras suscripciones, los rechazos de Plesk con secretos tapados, las respuestas que no son de Plesk (también una con DTD), los 5xx en un cambio, el acceso directo con la IP del navegador, el uso y los secretos testigo.

### Prueba contra un Plesk de laboratorio

Nunca contra uno productivo. Sin las variables, la prueba queda inconclusa.

```
export PLESK_TEST_HOST=plesk-lab.example.com
export PLESK_TEST_KEY=...
export PLESK_TEST_IP=192.0.2.10
dotnet test tests/BillingOss.Module.Plesk.Tests --filter TestCategory=PleskReal
```

Opcionales: `PLESK_TEST_PORT`, `PLESK_TEST_FINGERPRINT`, `PLESK_TEST_PLAN` (por defecto `Default Domain`) y `PLESK_TEST_PLAN_2` para probar el cambio de plan. Crea un cliente con prefijo `bo` y una suscripción de un dominio `.test` inventado, la suspende y reactiva dos veces, cambia la contraseña, pide estado, vista, uso y un acceso directo atado a una IP de documentación, y borra el cliente al final, comprobando la ausencia.

## Pendiente de comprobar en el laboratorio

- El orden de los campos de `gen_info` en `customer/add` (`pname`, `login`, `passwd`, `status`, `email`, `external-id`) y de `gen_setup` y `hosting` en `webspace/add`, que Plesk valida contra su esquema.
- Que `webspace/get` acepte el filtro `owner-login` y conteste 1013 a un cliente sin suscripciones.
- Que `gen_info` de `customer/get` traiga `external-id`. Sin eso el módulo no ve la marca: un alta a medias da `user_taken` en vez de completarse, y nunca adopta.
- Que `site/get` con el filtro `name` conteste 1013 para un dominio que no es un sitio adicional, y que `gen_info` de `webspace/get` traiga `owner-id`.
- Que `service-plan/get` con filtro vacío devuelva los planes del administrador con `name` y `guid` como hijos de cada resultado.
- Que `webspace/get` con `subscriptions` traiga `subscription/plan/plan-guid`, y que la GUID de un plan adicional no coincida con la de ningún plan de servicio.
- Que `switch-subscription` acepte `plan-guid`.
- Los códigos de error: 1013 para lo que no existe, 1001 para la clave y 1006 para el permiso.
- Que `stat/traffic` sea la transferencia del mes en curso en bytes.
- El nombre del parámetro de `rsession_init.php` (`PLESKSESSID`) y que la sesión quede atada a `user_ip`.
- Que `webspace/set` con `hosting/vrt_hst` cambie la contraseña de FTP sin pedir el resto de las propiedades.
- Cómo nombra Plesk un dominio con eñes o acentos en el filtro `name`. El módulo lo manda en punycode.
- Que Plesk acepte las contraseñas que genera el núcleo con la política de contraseñas por defecto.

## Fuera de esta versión

- Clientes de revendedor, suscripciones sin cliente (del administrador), IPv6 y planes adicionales (add-ons).
- Suspender o reactivar el cliente entero. La suspensión es de la suscripción.
- La consulta previa por alias de dominio y por subdominios de otra suscripción. Si el dominio es uno de esos, Plesk rechaza la suscripción y el cliente queda con la marca, sin suscripción, hasta un reintento o una baja.
- Correo, bases de datos y DNS desde el portal: el cliente los maneja en Plesk con el acceso directo.
