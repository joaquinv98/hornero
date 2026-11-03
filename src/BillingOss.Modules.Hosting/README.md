# Biblioteca de los módulos de paneles de hosting

Lo común de los tres módulos de aprovisionamiento para paneles de hosting compartido: [cPanel y WHM](../modules/BillingOss.Module.CPanel/README.md), [Plesk](../modules/BillingOss.Module.Plesk/README.md) y [DirectAdmin](../modules/BillingOss.Module.DirectAdmin/README.md). Cada módulo es un proceso aparte que habla el [protocolo de módulos v0](../../diseno/protocolo-modulos-v0.md) con la SDK. Esta biblioteca pone las operaciones del protocolo, la parte del manifiesto que no depende del panel, el cliente HTTP hacia los paneles y el arranque del proceso. Cada módulo pone solo su panel: una clase que implementa `IHostingPanel` contra la API pública de ese panel. Licencia Apache-2.0, como la SDK.

Se escribió de cero, con el diseño del módulo de Hestia como modelo: ninguna operación cambia algo sin leer antes la cuenta en el panel, y ningún cambio se da por hecho sin volver a leer.

## Capacidades

Los tres módulos declaran `provisioning.core`, `provisioning.change_plan`, `provisioning.status`, `provisioning.actions`, `provisioning.view`, `provisioning.sso` y `provisioning.usage`. La última es la del uso medido de la sección 9.1 del protocolo: `usage` lee el disco ocupado y la transferencia del mes, y el núcleo las guarda una vez por día para cobrar lo que pasa de lo que incluye el plan.

## Producto, pedido y datos personales

| Esquema | Campo | Qué es |
|---|---|---|
| `product_schema` | `plan` | El paquete o plan del panel. Obligatorio. Un plan inválido o que el panel no tiene es un error del producto: `invalid_config` |
| `order_schema` | `domain` | Dominio principal del hosting. Obligatorio. Se normaliza a minúsculas, sin punto final y en punycode si tiene eñes o acentos |

`data_scopes` pide `account.contact` porque los tres paneles exigen un correo de contacto al crear la cuenta. El nombre de la cuenta va limpio de símbolos y cortado a 50 caracteres a los paneles que lo guardan. No se piden domicilio ni otros datos.

## Credenciales

El manifiesto declara `credentials: ["password"]`. El núcleo genera la contraseña, la guarda cifrada antes de llamar y la manda en `input.credentials.password` de `create`. El módulo nunca la devuelve, ni en `view`.

La acción `change_password` declara su propio `credentials: ["password"]`, como pide la sección 5 del protocolo: el núcleo genera una contraseña nueva, la guarda y la manda en `input.credentials.password` de `run_action`.

## Marca externa y consulta previa

**La marca.** El usuario de las cuentas nuevas es `username_prefix` (de 1 a 4 minúsculas) seguido del SHA-256 del uuid del servicio en base32 en minúsculas, hasta 8 caracteres en total. Por ejemplo `cp4txcpw`. El uuid llega en `context.service.uuid` y no cambia nunca, así que el usuario sale siempre igual para el mismo servicio: un reintento, desde esta réplica o desde otra, y aunque se haya perdido el registro de idempotencia, encuentra la cuenta que dejó el intento anterior. A diferencia del ID numérico, no choca con el de otra instalación que comparte el panel. En cPanel y en DirectAdmin esa es la marca, porque no tienen dónde guardar otra. Plesk además guarda el uuid en el `external-id` del cliente.

Con un prefijo de una letra quedan 35 bits del hash. Con más letras quedan menos, y crece la chance de que el usuario de un servicio nuevo ya exista. Si pasa, el alta se frena con `user_taken` sin tocar nada.

**La consulta previa.** Antes de crear, el alta:

1. Lee la cuenta por su usuario. Si existe con el dominio del servicio, es la de un intento anterior: sigue desde ahí sin crear nada.
2. Si existe con otro dominio o con la marca de otro servicio, es de otro: `rejected` con `details.reason: "user_taken"`, sin tocarla, porque un `terminate` de este servicio la borraría.
3. Si no existe, pregunta al panel quién es el dueño del dominio. Si nadie, crea. Si es otra cuenta, el dominio es de otro: `rejected` con `details.reason: "domain_taken"`, sin crear nada. La excepción es un panel que guarda la marca (Plesk): si esa cuenta tiene el uuid del servicio, es un alta anterior con otro usuario, por ejemplo con otro prefijo, y el alta la adopta.

Un servicio importado sin marca en el panel se reconoce por su dominio. Lo mismo vale si el panel guarda un valor que no es un uuid, por ejemplo el ID que dejó el sistema de facturación anterior: no dice de qué servicio es, así que no cuenta como marca. Una cuenta con la marca de otro servicio nunca se toca, aunque tenga el dominio.

## Operaciones

Todas son `POST /v0/provisioning/{operación}` con el sobre del protocolo. Las que cambian algo exigen `Idempotency-Key`. La respuesta del panel a un cambio nunca alcanza: el resultado lo decide la lectura posterior. No hay vuelta atrás en ninguna.

| Operación | Qué hace | Espera del núcleo |
|---|---|---|
| `instance/test` | Lista los planes del panel. Prueba a la vez credenciales, IP permitida y certificado. Devuelve `{ "plans": [...] }` | 30 s |
| `validate` | Sin efectos: plan existente y dominio válido y libre en el panel. El correo de la cuenta se controla solo si viene, porque el pedido de alguien que todavía no tiene cuenta no lo trae. Un dominio ocupado da `invalid_input` con `domain_taken` | 30 s |
| `create` | Alta con la marca y la consulta previa. Si la cuenta ya está completa con otro plan, por ejemplo porque el staff repitió con otro plan un alta que falló, cambia el plan. Una cuenta del servicio que ya existe suspendida da `manual_review` | 120 s |
| `suspend`, `unsuspend` | Lee. Si ya está en el estado pedido termina sin mutar. Si no, cambia y exige que la relectura lo muestre | 90 s |
| `terminate` | Si la cuenta ya no está, termina sin mutar. Si está y es del servicio, la borra y exige la ausencia en la relectura. En los paneles que guardan la marca, una cuenta con la marca del servicio y sin su dominio también es suya: es un alta cortada a la mitad, y se borra | 120 s |
| `change_plan` | El plan nuevo llega en `context.service.params.plan`. Si ya lo tiene termina sin mutar. Si no, lo cambia y lo exige en la relectura | 90 s |
| `run_action` | Solo `change_password`. Una contraseña no se puede leer para verificarla: un corte da `outcome_unknown` y repetir con la misma clave y la misma contraseña es seguro | 60 s |
| `get_status` | Estado observado para la reconciliación | 30 s |
| `view` | Bloques para el portal | 30 s |
| `sso` | Acceso directo al panel | 30 s |
| `usage` | Disco y transferencia del mes para el uso medido | 30 s |

Las que operan sobre una cuenta existente la toman de `external_ids.username` y `external_ids.domain`, y exigen que sea del servicio: si no existe, `not_found`. Si tiene otro dominio o la marca de otro servicio, `manual_review` con `domain_mismatch` o `marker_mismatch`, sin tocar nada.

Cada operación con efecto que termina devuelve `observed_state` y `external_ids` con `username` y `domain`.

### get_status

| Estado en el panel | `observed_state` |
|---|---|
| Cuenta del servicio, activa | `active` |
| Cuenta del servicio, suspendida | `suspended` |
| Ausencia probada | `not_found` |
| Cuenta con otro dominio | `unknown` con `details.reason: "domain_mismatch"` |
| Cuenta con la marca de otro servicio | `unknown` con `details.reason: "marker_mismatch"` |

`details` trae además el plan (`plan`) y si está suspendida (`suspended`). Un panel que no responde o una respuesta que no se puede leer no son un estado: dan `upstream_unavailable`.

### view

Bloques de texto plano, en castellano o en inglés según el idioma de la cuenta, con la forma común del módulo manual del núcleo y de los demás módulos: el color de `status` y de `notice` en `level`, y las listas de `fields` y de `actions` en `items`:

```json
{
  "blocks": [
    { "type": "status", "level": "ok", "text": "Activo" },
    { "type": "fields", "items": [
      { "label": "Usuario del panel", "value": "cp4txcpw" },
      { "label": "Dominio", "value": "ejemplo.com.ar" },
      { "label": "Plan", "value": "basico" }
    ] },
    { "type": "link", "label": "Entrar al panel", "url": "https://panel.ejemplo.com.ar:2083/" },
    { "type": "actions", "items": ["change_password"] }
  ]
}
```

El enlace va al login del panel, sin credenciales. Una cuenta suspendida no ofrece acciones. Si la cuenta tiene otro dominio o la marca de otro servicio, salen solo el estado y un aviso: puede ser de otro cliente, así que no se muestran su plan ni el enlace.

### sso

Devuelve `{ "url": "...", "expires_at": "2026-10-29T15:00:00Z" }`. La URL es de un solo uso o de vida corta, y lleva la sesión: nunca va a los logs. Solo para una cuenta activa del servicio: suspendida da `conflict` con `reason: suspended`. La URL tiene que ser `https` y su host el de `host` de la instancia, que el núcleo tiene en la lista de hosts de la instancia. Si el panel devuelve otro host, `invalid_config` con `host` en `details.fields`. Si el núcleo manda `input.client_ip`, se valida y se pasa al panel. Plesk la exige porque ata la sesión a la IP del navegador.

### usage

El manifiesto declara dos métricas en `usage_metrics`, con su etiqueta en castellano y en inglés:

| Métrica | Tipo | Qué es |
|---|---|---|
| `disk_gb` | `gauge` | El disco que ocupa la cuenta, como lo cuenta el panel |
| `transfer_gb` | `counter` | Lo transferido en el mes calendario. El panel lo vuelve a cero cada mes, y el núcleo toma la lectura más baja como un contador que volvió a cero |

Los GB son de 1024 MiB, como los cuentan los paneles. La entrada trae los códigos que pide el núcleo, y la respuesta, la lectura de cada uno como texto decimal con hasta 6 decimales:

```json
{ "metrics": ["disk_gb", "transfer_gb"] }
{ "metrics": { "disk_gb": "1.5", "transfer_gb": "0.117676" } }
```

Sin la lista da `invalid_input`. Lo que el módulo no declara no va en la respuesta, y tampoco una medida que el panel contestó sin un valor que se pueda leer. Un panel que no responde o que contesta un error no es una lectura: da `upstream_unavailable` y el núcleo reintenta. Una cuenta suspendida también se lee, porque su disco sigue ocupado. El módulo no guarda nada.

Lo transferido entre la última lectura de un mes y el cambio de mes no llega a leerse, porque el panel vuelve a cero antes. Queda a favor del cliente.

## Operaciones largas

Si a una operación no le queda tiempo para mandar el próximo cambio, responde `pending` con `operation_id` igual a la operación más el uuid del servicio, por ejemplo `suspend-0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e11`. La consulta `POST /v0/operations/{id}` llega con el sobre completo y retoma la operación desde el estado del panel, así que la puede contestar cualquier réplica. Un ID que no es de ese servicio da `not_found`.

El alta pasa a `pending` solo con la cuenta completa, porque la consulta no trae la contraseña. Si se acaba el tiempo antes, responde `upstream_unavailable` y el núcleo la repite con la misma clave. `run_action` nunca queda en curso.

Si un cambio queda sin respuesta (corte, plazo vencido, un 5xx o un cuerpo que no es del panel) y la relectura no lo confirma, el panel puede seguir ejecutándolo, por ejemplo el borrado de una cuenta grande. Mandarlo de nuevo duplicaría el efecto. Por eso la operación responde `pending` con `operation_id` igual a la operación, el uuid, `-sent-` y el momento en que salió el cambio en segundos Unix. Durante 10 minutos desde ese momento la consulta solo relee: si el estado ya llegó termina, y si no, sigue en curso con el mismo ID. Pasada la ventana, la consulta corre la operación entera. El alta antes de completar la cuenta y `run_action`, que no pueden quedar en curso, responden `outcome_unknown`. El cambio tiene todo el tiempo de la operación menos 10 segundos para la relectura, y cada lectura 30 segundos.

## Errores

| Caso | Código | HTTP |
|---|---|---|
| Configuración inválida, credenciales o privilegios rechazados por el panel | `invalid_config` | 422 |
| Certificado que no es de una CA confiable ni coincide con `tls_fingerprint` | `invalid_config`, con `tls_fingerprint` en `details.fields` | 422 |
| Plan del producto inválido o inexistente | `invalid_config` | 422 |
| Dominio del pedido inválido u ocupado, correo, contraseña, uuid o lista de métricas de `usage` que faltan, `external_ids` incompletos | `invalid_input` con `details.fields` | 422 |
| Usuario o dominio de otro en `create`, o el panel rechaza el cambio y el estado no cambió | `rejected` con `details.upstream` | 422 |
| Cuenta con otro dominio o con la marca de otro servicio, o cuenta del servicio suspendida en `create` | `manual_review` | 409 |
| Ausencia probada en `suspend`, `unsuspend`, `change_plan`, `run_action`, `sso` o `usage` | `not_found` | 404 |
| Acceso directo a una cuenta suspendida | `conflict` | 409 |
| Conexión rechazada, sin respuesta en una lectura o una respuesta que no se puede leer, antes de mandar un cambio | `upstream_unavailable` | 503 |
| Corte, 5xx o respuesta que no es del panel después de mandar un cambio, sin que la relectura confirme el estado | `pending` con `-sent-` en el ID. En el alta antes de completar la cuenta y en `run_action`, `outcome_unknown` | 200 o 504 |
| Una relectura que falla después de un cambio que el panel aceptó | `outcome_unknown` con `details.reason: "unreadable_after_change"` | 504 |
| Acción que no es `change_password` | `not_supported` | 501 |

`details.reason` dice el caso concreto (por ejemplo `no_reply`, `not_converged`, `domain_taken` o `out_of_time`). Los mensajes son para el staff y nunca llevan secretos: si el panel repite una contraseña o un token en un error, se tapan con `***`. Los logs registran la función del panel y el código HTTP, nunca los argumentos ni el cuerpo.

## Transporte

TLS siempre validado: con la CA del sistema, o con la huella SHA-256 del certificado fijada en `tls_fingerprint` (en hexadecimal, con o sin dos puntos). No hay opción para desactivarlo. Las redirecciones no se siguen, porque llevarían las credenciales a otro destino. Los cuerpos de respuesta tienen un tope de 16 MiB. Un error al conectar dice que el pedido no salió, así que nunca es un resultado incierto.

## Cómo correr un módulo

Se configura con las variables de entorno de ASP.NET Core, o con un archivo por clave en `/run/secrets` (por ejemplo `/run/secrets/Module__SigningSecret`), que es como llegan los Docker secrets.

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma que el núcleo tiene en la instalación del módulo |
| `Module__PreviousSigningSecret` | Opcional. El secreto anterior, mientras dura una rotación |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo y el módulo no escribe en el disco. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `Module__IdempotencyStorePath` | Archivo SQLite del registro de idempotencia, para una sola réplica. Por defecto `data/idempotency.db`, desde el directorio del módulo. Tiene que sobrevivir a los reinicios |
| `ASPNETCORE_URLS` | Dónde escucha |

El protocolo exige TLS entre el núcleo y el módulo, salvo en el mismo host. La salida a internet necesaria es solo el host del panel en su puerto de API. Se corre con un usuario sin privilegios y con el sistema de archivos de solo lectura, salvo el directorio del registro si es SQLite.

## Alta disponibilidad

- **Sin estado en memoria.** Lo único que vive en el proceso es el caché de conexiones HTTP por huella de certificado, que no guarda nada de los pedidos. Cada instancia del panel vale para una sola llamada.
- **Idempotencia en dos capas.** El registro de la SDK evita repetir una llamada con la misma clave. Con varias réplicas va en PostgreSQL y lo comparten todas, como pide la sección 6 del protocolo. Si el registro no alcanza, por ejemplo con una réplica que murió entre el cambio y el guardado, o con una restauración de la base a un punto anterior, lo evita la lectura previa con la marca: una repetición encuentra la cuenta ya creada, ya suspendida o ya borrada y no vuelve a mutar.
- **Operaciones largas sin dueño.** El `operation_id` alcanza para retomar desde el estado del panel, así que la consulta la puede contestar cualquier réplica.
- **Concurrencia.** `max_in_flight` es 2 por instancia para no saturar el panel. Lo tiene que aplicar el núcleo.

## Pruebas

```
dotnet test tests/BillingOss.Modules.Hosting.Tests
```

Las operaciones se prueban contra un panel en memoria detrás de `IHostingPanel`, que respeta el plazo como el transporte de verdad y deja pisar cada cambio, cortar lecturas y adelantar el reloj. Cubren la marca y la consulta previa, la adopción por marca, las colisiones de usuario y de dominio, las altas cortadas, las consultas desde otra réplica, la ventana de relectura después de un cambio sin respuesta y los escenarios 1, 2, 3, 4, 5 y 6 de la conformidad. El transporte se prueba contra un Kestrel con HTTPS y un certificado autofirmado generado en el momento: certificado rechazado, huella fijada, conexión rechazada, redirección que no se sigue y corte después de mandar. Cada módulo suma sus pruebas contra un panel falso que habla la API de su panel, y una prueba contra un panel de laboratorio que queda inconclusa sin sus variables.

## Pendiente en el núcleo

El protocolo ya tiene `usage` y la IP del navegador en `sso` (sección 9.1). El núcleo todavía no llama a estas operaciones:

- `run_action` con el control de `audience`, la clave por acción y generación, y la contraseña nueva en `input.credentials` para las acciones que nombran `credentials`.
- `sso` por `POST` cuando el usuario aprieta el botón, con la IP del navegador en `input.client_ip`, y la redirección enseguida sin guardar la URL.
- `view` con el caché de un minuto y `validate` antes de confirmar un pedido. En la navegación clásica de la interfaz (sección 10 de `diseno/interfaz-y-temas.md`) cuelgan de la ficha del servicio: Servicios en el portal y Clientes, Servicios en el admin.
- La lectura diaria de `usage` para el uso medido, con las métricas que el producto tiene activas, y su página en la ficha del servicio.
- `instance/test` al guardar una instancia, en Configuración, Servidores.
- `max_in_flight` por instancia.
- Una instancia en solo lectura para el mes en paralelo de quien migra, que no mande cambios.
