# Webhooks salientes, extensiones y API compatible ampliada

**Estado:** v0, 2026-10-29 · **Parte de:** [diseño del núcleo](README.md) · **Decisiones:** D-07, D-08, D-11 y D-12

Este documento cubre la API y las extensiones: cómo el núcleo avisa a las integraciones del proveedor
cuando pasa algo, cómo el proveedor personaliza el comportamiento sin tocar el código y qué comandos suma la
[API compatible](api-compatible.md) después de v0. Todo es diseño propio. No sigue a los hooks ni a los
webhooks de ningún otro sistema.

---

## 1. Qué resuelve

| Necesidad | Pieza | Ejemplo |
|---|---|---|
| Que una integración se entere de algo sin preguntar a cada rato | Webhooks salientes firmados (sección 3) | Un flujo de n8n que avisa por WhatsApp cuando se paga una factura |
| Personalizar sin tocar el código del núcleo | Extensiones: módulos del tipo `extension` que reciben eventos y validan pedidos (sección 4) | Una sola prueba gratis por cliente, que hoy es un hook de carrito |
| Que las integraciones que vienen del sistema anterior sigan andando | Comandos nuevos de la API compatible (sección 5) | Buscar clientes, listar facturas, cancelar un pedido |

---

## 2. Catálogo de eventos públicos

Los eventos de dominio salen por la cola en la misma transacción que el cambio (D-07). Los que interesan afuera
tienen un nombre público, estable, que es el que ven los webhooks y las extensiones. El nombre interno puede
cambiar sin romper a nadie.

| Tipo público | Evento interno | `data` | Cuándo |
|---|---|---|---|
| `order.placed` | `OrderPlaced` | `order_id` | Se confirmó un pedido |
| `invoice.issued` | `InvoiceIssued` | `invoice_id` | Se emitió una factura (la factura "creada" para quien integra) |
| `invoice.paid` | `InvoicePaid` | `invoice_id` | La factura quedó paga |
| `credit_note.issued` | `CreditNoteIssued` | `credit_note_id` | Se emitió una nota de crédito |
| `payment.received` | `PaymentReceived` | `payment_id` | Entró un pago |
| `fiscal_document.authorized` | `FiscalDocumentAuthorized` | `document_id` | El fisco autorizó el comprobante de una factura o nota |
| `service.activated` | `ServiceActivated` | `service_id` | El módulo confirmó el alta (el servicio "creado") |
| `service.reactivated` | `ServiceReactivated` | `service_id` | Se levantó una suspensión |
| `service.suspended` | `ServiceSuspended` | `service_id` | Se suspendió |
| `service.terminated` | `ServiceTerminated` | `service_id` | Se dio de baja |
| `ticket.opened` | `TicketOpened` | `ticket_id`, `message_id` | Se abrió un ticket (lo publica el módulo de soporte) |
| `ticket.replied` | `TicketReplied` | `ticket_id`, `message_id` | Hubo una respuesta en un ticket |

**Eventos finos.** `data` lleva solo identificadores. Quien integra lee el resto con la API propia o con la
compatible, con una llave que tiene los permisos que corresponden. Así un webhook nunca lleva datos personales,
un receptor no ve una foto vieja si la entrega se demora y no hace falta versionar el contenido de cada evento.

**Clave de entidad.** Cada evento tiene una entidad: `document:{id}`, `service:{id}`, `order:{id}`,
`payment:{id}` o `ticket:{id}`. La usan las entregas para salir en orden (secciones 3.4 y 4.3).

---

## 3. Webhooks salientes

### 3.1 Modelo

**Destino (`WebhookEndpoint`, `platform.webhook_endpoints`)**

| Campo | Qué es |
|---|---|
| `url` | Adónde se manda. `https`, o `http` solo hacia loopback, la misma regla que los módulos |
| `description` | Para qué es, a elección del staff |
| `events` | Tipos públicos a los que se suscribe. Al menos uno |
| `secret` | Secreto de firma `whsec_` con 32 bytes al azar, cifrado por campo (D-09) y atado a su tabla, columna y fila. Se muestra una sola vez, al crearlo y al rotarlo |
| `active` | Un destino apagado no recibe nada y sus entregas pendientes se cancelan |
| `created_at`, `updated_at` | Instantes en UTC |

**Entrega (`WebhookDelivery`, `platform.webhook_deliveries`)**: una por evento y destino, que es el registro de
entregas que ve el staff.

| Campo | Qué es |
|---|---|
| `endpoint_id` | El destino |
| `event_type` | Tipo público, o `webhook.test` para la prueba del panel |
| `data` | Lo que va en `data`, congelado al publicar |
| `entity_key` | La entidad del evento, para el orden |
| `occurred_at` | Cuándo pasó |
| `status` | Sección 3.2 |
| `attempts` | Intentos hechos |
| `last_status_code`, `last_error`, `last_attempt_at` | Lo que pasó en el último intento, sin cuerpos ni secretos |
| `delivered_at` | Cuándo respondió con 2xx |

### 3.2 Estados de la entrega

| Estado | Significa | Pasa a |
|---|---|---|
| `Pending` | Esperando su turno o su próximo intento | `Delivered` con un 2xx. `Failed` cuando se acaban los intentos o la URL ya no se acepta. `Cancelled` si el destino se apagó o se borró |
| `Delivered` | El receptor respondió 2xx | `Pending` si el staff la reenvía |
| `Failed` | Se acabaron los intentos. Su trabajo queda muerto y visible | `Pending` si el staff la reenvía |
| `Cancelled` | El destino estaba apagado al momento de mandar | `Pending` si el staff la reenvía |

### 3.3 Mensaje

Sigue la especificación abierta Standard Webhooks (D-11), así cualquier receptor puede verificarlo con las
librerías de esa especificación:

| Encabezado | Contenido |
|---|---|
| `webhook-id` | `msg_` y el ID de la entrega. No cambia entre reintentos ni en un reenvío: el receptor lo usa para no procesar dos veces |
| `webhook-timestamp` | Segundos Unix del intento, con el reloj real |
| `webhook-signature` | `v1,` y el HMAC-SHA256 en base64 de `{webhook-id}.{webhook-timestamp}.{cuerpo}`, con los bytes del secreto que siguen a `whsec_` en base64 |
| `Content-Type` | `application/json` |
| `User-Agent` | `billing-oss-webhooks` |

Cuerpo:

```json
{ "type": "invoice.paid", "timestamp": "2026-11-02T15:00:00.000Z", "data": { "invoice_id": 4521 } }
```

`timestamp` es cuándo pasó el evento. El receptor rechaza un `webhook-timestamp` que difiere del suyo en más de
cinco minutos, para que no le repitan un mensaje capturado.

### 3.4 Entrega

- **Al publicar.** En la misma transacción que el cambio, por cada destino activo suscripto al tipo se crea la
  entrega y su trabajo `webhooks.deliver`. Si la transacción se cae no queda nada, y si confirma no se pierde
  nada, aunque se caiga el nodo.
- **Orden.** El trabajo lleva la clave de entidad `webhook:{destino}:{entidad}`. Lo que le pasa a una misma
  factura llega en orden a cada destino (emitida antes que pagada), y lo demás sale en paralelo desde cualquier
  nodo. Un destino caído demora solo las entregas de esa entidad y ese destino.
- **Intento.** Un `POST` con 15 segundos de tope, sin seguir redirecciones (un 307 podría mandar el mensaje a
  otro lado) y sin leer el cuerpo de la respuesta. Un 2xx es entregado. Cualquier otra respuesta, un corte o una
  demora vuelve a la cola con la espera creciente de D-07.
- **Intentos.** 30, que con la espera creciente de la cola cubren casi un día. Con el último la entrega queda
  `Failed` y su trabajo muerto, a la vista del staff.
- **Al menos una vez.** Si el nodo cae entre la respuesta y la marca de entregado, el reintento manda el mismo
  `webhook-id`. Es lo máximo que permite HTTP, y por eso el receptor deduplica.
- **Reenvío.** El staff reenvía una entrega entregada, fallida o cancelada. Vuelve a `Pending` con un trabajo
  nuevo y el mismo `webhook-id`. Una pendiente no se reenvía: ya tiene su trabajo.
- **Prueba.** El panel manda un `webhook.test` con `data` vacío a un solo destino, para probar la URL y la firma.
- **Retención.** El trabajo diario de retención borra las entregas de más de `Workers:KeepDone` (30 días), igual
  que los trabajos hechos.
- **Sin estado en memoria.** Destinos y entregas viven en la base. Cualquier nodo con llaves (`Secrets:KeyFile`)
  manda cualquier entrega.

### 3.5 Permisos y auditoría

- El permiso del staff `integrations.manage` da acceso a webhooks, extensiones y llaves de la API. Como deja emitir
  una llave con cualquier permiso, equivale a administrar todo y se da solo a quien ya puede todo lo demás.
- Crear, cambiar, rotar el secreto, borrar, reenviar y probar quedan en la auditoría, con la URL y los eventos
  pero nunca el secreto.
- El secreto aparece en la página una sola vez, con `Cache-Control: no-store`, y nunca vuelve al navegador.

### 3.6 Pantalla

En **Configuración, API y webhooks** (`/admin/configuracion/webhooks`):

- Las llaves de la API (D-11) con su nombre, prefijo, permisos, límites y estado (vigente, vencida o revocada), el
  botón para revocar cada una y el formulario para emitir una nueva. Los permisos que se ofrecen son los que
  exige algún endpoint de la API propia y un `compat:` por cada comando de la compatible. La llave se muestra una
  sola vez, con `Cache-Control: no-store`, y la emisión y la revocación quedan en la auditoría (`apikeys.issue` y
  `apikeys.revoke`) sin el secreto.
- La lista de destinos con su URL, eventos, estado y el resultado de la última entrega, y el formulario para
  agregar uno.
- La ficha de cada destino (`/admin/configuracion/webhooks/{id}`), con el formulario para cambiarlo, los botones
  para rotar el secreto, mandar una prueba, apagarlo y borrarlo, y sus últimas 50 entregas con el estado, el
  código HTTP, los intentos y el botón de reenvío.

---

## 4. Extensiones

Una extensión es un módulo del protocolo v0 (D-08) del tipo `extension`. Corre fuera del núcleo, en cualquier
lenguaje, y no toca la base. El núcleo la llama cuando pasa algo o antes de confirmar un pedido. Para actuar
sobre el núcleo usa la API propia con una llave propia, como cualquier integración. El tipo se define en la
sección 9.5 del [protocolo](protocolo-modulos-v0.md).

### 4.1 Ganchos

| Gancho | Operación del módulo | Capacidad | Cómo corre |
|---|---|---|---|
| Un tipo del catálogo (sección 2) | `extension/on_event` | `extension.events` | Por la cola, después del commit, con reintentos. Nunca frena al que publica |
| `order.validate` | `extension/validate_order` | `extension.validate_order` | En el momento, antes de abrir la transacción del pedido. Puede rechazarlo |

### 4.2 Modelo

**Gancho (`ExtensionHook`, `platform.extension_hooks`)**

| Campo | Qué es |
|---|---|
| `instance_id` | La instancia del módulo de extensión |
| `event` | Tipo público del catálogo, o `order.validate` |
| `required` | Solo en `order.validate`: si la extensión no responde, el pedido se rechaza (prendido, el valor por defecto) o pasa con un aviso en el log y en la auditoría (apagado) |
| `active` | Apagado no se llama |
| `created_at` | Instante en UTC |

Único por instancia y evento.

### 4.3 Eventos

- Al publicar, en la misma transacción, se encola un trabajo `extensions.event` por cada gancho activo del
  tipo. La clave de entidad es `extension:{instancia}:{entidad}`: los eventos de una misma entidad llegan en
  orden a cada extensión.
- El trabajo llama a `on_event` con la clave de idempotencia `hook-{gancho}-{evento}`, así un reintento nunca
  repite el efecto. El ID del evento es el mismo para todos los ganchos y webhooks de esa publicación.
- Un error reintentable del módulo vuelve a la cola con la espera que pidió o la creciente. Uno definitivo deja
  el trabajo muerto y visible. Son 30 intentos, como los webhooks.

### 4.4 Validación de pedidos

- `PlaceOrder`, el único caso de uso que confirma pedidos (portal, API propia y API compatible), llama a cada
  gancho `order.validate` activo, de a uno y en el orden en que se crearon, antes de abrir su transacción. Ninguna
  llamada a un módulo ocurre dentro de una transacción de base.
- Si una extensión responde `allowed: false`, el pedido no se confirma: no queda pedido, servicio, factura ni
  trabajo, y el motivo que devolvió la extensión (hasta 300 caracteres, en el idioma de la cuenta) vuelve como
  error de validación.
- Si no responde (error, corte o más de 10 segundos), decide `required`. El rechazo por falta de respuesta usa un
  texto propio, que no culpa al cliente.
- Recibe lo mínimo: la cuenta (ID, idioma, país y sus servicios con producto y estado) y las líneas (producto,
  plan, ciclo, cantidad, opciones y datos del pedido). Nada de contacto ni de pagos.
- No reserva nada: como corre fuera de la transacción, dos pedidos de la misma cuenta a la vez pueden pasar los dos.
  Una extensión a la que eso le importa revisa también `order.placed` y cancela el sobrante con la API.

### 4.5 Alta

En **Complementos, Extensiones** (`/admin/complementos/extensiones`) el staff carga el nombre, la URL y el secreto
de firma. El núcleo lee el manifiesto firmado, exige el tipo `extension`, guarda la instalación y su instancia
con la configuración, y separa los campos `x-secret` del esquema para cifrarlos (D-09). Después, sin transacción
abierta, llama a `/v0/instance/test`: si el módulo rechaza la configuración o no tiene la prueba, se borran la
instalación y la instancia y el motivo vuelve como error de validación. Una extensión mal configurada que valida
pedidos de forma obligatoria rechazaría todos los pedidos. En la ficha de la extensión elige los eventos y la
validación de pedidos, según las capacidades que declara el manifiesto. Todo queda en la auditoría.

### 4.6 Ejemplo completo

`src/modules/BillingOss.Module.OneTrial` es una extensión real, con su prueba de punta a punta: una sola prueba
gratis por cuenta. La guía paso a paso está en [docs/integraciones](../docs/integraciones/README.md).

---

## 5. API compatible: comandos agregados

Los nombres de los comandos salen de integraciones reales, como los de v0 ([api-compatible.md](api-compatible.md),
sección 1). Los parámetros y campos usan nombres que el repo ya tenía para otros comandos y, donde no hay ninguno,
son **diseño propio** y se confirman en los tests de contrato. El detalle de cada comando está en la sección 3.1
de [api-compatible.md](api-compatible.md).

| Comando | Caso de uso propio |
|---|---|
| `GetClients` | Buscar cuentas |
| `GetProducts` | Listar el catálogo con los precios vigentes |
| `GetInvoices` | Listar facturas por cuenta y estado |
| `DeleteClient` | Borrar una cuenta sin historia |
| `UpdateClientProduct` | Cambiar el nombre visible de un servicio |
| `ModuleCreate` | Aprovisionar un servicio pendiente o reintentar uno fallido |
| `ModuleTerminate` | Dar de baja un servicio activo o suspendido |
| `ModuleSuspend` | Suspender un servicio activo con un motivo propio, que el pago no levanta |
| `ModuleUnsuspend` | Reactivar un servicio suspendido |
| `CancelOrder` | Cancelar un pedido sin pagar ni aprovisionar, con la anulación de su factura |
| `GetEmailTemplates` | Listar las plantillas de los avisos |

**Quedan para después**, con su motivo:

| Comando | Por qué |
|---|---|
| `GetTickets`, `OpenTicket`, `UpdateTicket`, `GetSupportDepartments` | Buscar y abrir tickets va por la API propia (`/api/v1/tickets`). Entran sobre los casos de uso de soporte si una integración los necesita |
| `SendEmail`, `SendAdminEmail` | El repo no tiene los nombres de sus parámetros, y suelen usarse desde hooks que corren dentro del sistema anterior y no se migran. Si una integración los necesita, se relevan de su código |
| `ModuleChangePw`, `ModuleChangePackage`, `ModuleCustom`, `UpgradeProduct` | El núcleo todavía no tiene sus casos de uso: el protocolo no tiene cambio de contraseña, y el cambio de plan y las acciones del módulo no tienen caso de uso |
| `CreateSsoToken` | La descarga del PDF por la API propia lo reemplaza (sección 3 de api-compatible.md) |
| `GetStaffOnline` | El núcleo no registra quién del staff está conectado |
| Comandos de dominios | Ninguna fuente del repo nombra uno. Los dominios se leen con `GET /api/v1/domains` |

---

## 6. Trabajos

| Tipo | Clave de entidad | Intentos | Lo encola |
|---|---|---|---|
| `webhooks.deliver` | `webhook:{destino}:{entidad}` | 30 | La publicación de un evento, la prueba y el reenvío del panel |
| `extensions.event` | `extension:{instancia}:{entidad}` | 30 | La publicación de un evento |

Los dos necesitan las llaves de `Secrets:KeyFile`: un nodo sin llaves no los toma y quedan para otro.

---

## 7. Pendientes

- La API propia para administrar webhooks (`/api/v1/webhooks`), para que una integración se suscriba sola.
- Apagar solo un destino que responde 410, como sugiere Standard Webhooks.
- Que una extensión pueda pedirle cambios al núcleo en la respuesta a un evento. En v0 usa la API propia.
- Validar los campos de la configuración de una extensión contra su `config_schema` al cargarla. En v0 los valida
  el propio módulo en `/v0/instance/test`, que el núcleo llama al darla de alta (sección 4.5).
