# Módulo de PayPal

Módulo de cobros (`payment`) de Hornero para PayPal con Orders v2, por redirección. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md), sección 9.3, con el mismo contrato que el [módulo de Mercado Pago](../BillingOss.Module.MercadoPago/README.md). Llama a la API REST de PayPal con `HttpClient` y `System.Text.Json`, sin la SDK de PayPal. No guarda configuración: el núcleo manda la de la instancia en cada llamada.

Capacidades: `payment.core`, `payment.checkout`, `payment.webhooks`, `payment.refund` y `payment.reconcile`. No incluye suscripciones (`payment.subscriptions`), medios guardados ni cobro con tarjeta embebido.

PayPal no cobra en pesos: una factura en ARS no se puede pagar por este módulo (sección "Monedas").

## Cómo se cobra

Con Orders v2 la plata no se mueve cuando el cliente aprueba el pago, sino cuando el vendedor captura la orden aprobada. El protocolo v0 no tiene ese paso, así que el módulo lo resuelve con las operaciones que ya existen y sin cambios en el núcleo:

1. `create_checkout` crea la orden con `intent` `CAPTURE` y devuelve la URL de PayPal para aprobarla.
2. El cliente aprueba en PayPal y vuelve a `/pay/return/{intento}`, que no cambia nada.
3. PayPal manda el webhook `CHECKOUT.ORDER.APPROVED`. `verify_webhook` devuelve un evento con `payment_external_id` igual a `order:` más el ID de la orden.
4. El núcleo pide ese pago con `get_payment`. El módulo lee la orden y, si está aprobada, la captura con `POST /v2/checkout/orders/{id}/capture`. Devuelve la captura, con su propio ID como `payment_external_id`.
5. Desde ahí el pago es la captura: `PAYMENT.CAPTURE.*`, `refund` y `list_payments` hablan de su ID.

Capturar es seguro de repetir. El pedido lleva `PayPal-Request-Id` igual a `capture-` más el ID de la orden, así que un reintento devuelve la misma captura, y si otra réplica ya capturó, PayPal contesta `ORDER_ALREADY_CAPTURED` y el módulo lee la orden. PayPal no captura dos veces una orden con `intent` `CAPTURE`.

Solo se capturan las órdenes de este módulo, las que tienen `reference_id` igual a `billing-oss` en la unidad de compra. Si otro sistema usa la misma cuenta de PayPal (por ejemplo WHMCS durante el paralelo), sus órdenes las captura él.

Consecuencias:

- PayPal da 6 horas desde la redirección del cliente para aprobar y capturar la orden (el account manager de la cuenta puede ampliarlo). Los reintentos del webhook no extienden ese plazo. Si la aprobación no llega a tiempo, por ejemplo con el núcleo caído, la verificación fallando o la cola atrasada, la orden vence sin cobrar y el cliente que aprobó en PayPal queda con la factura impaga. No se pierde plata, porque nada se movió, pero el cliente cree que pagó. Por eso el núcleo tiene que barrer los intentos abiertos (ver "Lo que falta en el núcleo").
- El núcleo no vuelve a mirar las facturas justo antes de capturar. Si se pagaron por otro medio entre la aprobación y la captura, el cobro entra igual y el núcleo lo deja como saldo a favor, como cualquier pago de más.
- Una captura rechazada por PayPal (por ejemplo `INSTRUMENT_DECLINED`) da `rejected` en `get_payment` y el intento sigue abierto. El cliente puede abrir un intento nuevo.

Una operación `complete_checkout` en el protocolo, con su propia clave de idempotencia, dejaría al núcleo decidir cuándo capturar. Queda como propuesta para la fase 1.

## Configuración de la instancia

| Campo | Secreto | De dónde sale |
|---|---|---|
| `environment` | No | `live` o `sandbox`. Fija la URL de la API: `https://api-m.paypal.com` o `https://api-m.sandbox.paypal.com` |
| `client_id` | No | Client ID de la app REST en el panel de desarrolladores de PayPal |
| `client_secret` | Sí | Secret de la misma app |
| `webhook_id` | No | ID del webhook de la app (letras y dígitos, hasta 50) |

La URL de la API sale del entorno a propósito: así las credenciales nunca viajan a un destino que alguien configure. Las redirecciones no se siguen. Si faltan datos, o las credenciales tienen espacios o caracteres de control, la respuesta es `invalid_config` con el campo en `details.fields`. `webhook_id` se valida en `verify_webhook` y en `instance/test`.

En la app de PayPal hace falta:

- Un webhook que apunte a `https://{núcleo}/webhooks/{instancia}` con al menos `CHECKOUT.ORDER.APPROVED`, `PAYMENT.CAPTURE.COMPLETED`, `PAYMENT.CAPTURE.REFUNDED` y `PAYMENT.CAPTURE.REVERSED`. Conviene sumar `PAYMENT.CAPTURE.PENDING`, `PAYMENT.CAPTURE.DECLINED` y `CUSTOMER.DISPUTE.CREATED`, `UPDATED` y `RESOLVED`, o elegir todos los eventos.
- La función Transaction search activada, para `list_payments`. Sin ella PayPal contesta 403 `NOT_AUTHORIZED` y el módulo da `invalid_config`.
- La función de disputas, para que `get_payment` informe `disputed` y `charged_back` (ver `get_payment`). Sin ella se informa la captura con su propio estado y queda un aviso en el log.

En la instancia del núcleo, los hosts del navegador son `www.paypal.com` en live y `www.sandbox.paypal.com` en sandbox.

El token OAuth se pide con `POST /v1/oauth2/token` y las credenciales en Basic, y se guarda en memoria por app hasta cinco minutos antes de que venza. Cada réplica tiene el suyo. Si PayPal contesta 401 con un token guardado, se pide otro y se repite el pedido una vez.

## Cómo correrlo

Se configura con las variables de entorno de ASP.NET Core:

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma que el núcleo tiene en la instalación de este módulo |
| `Module__PreviousSigningSecret` | Opcional. El secreto anterior, mientras dura una rotación |
| `Module__IdempotencyStorePath` | Archivo SQLite del registro de idempotencia. Por defecto `data/idempotency.db`. Tiene que sobrevivir a los reinicios |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo y el módulo no crea el directorio. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `ASPNETCORE_URLS` | Dónde escucha |

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:Module__IdempotencyStorePath = ".\datos\paypal\idempotency.db"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5192"
dotnet run --project src/modules/BillingOss.Module.PayPal -c Release
```

El protocolo exige TLS entre el núcleo y el módulo, salvo en el mismo host. En producción va un proxy con TLS delante, o se configura el certificado de Kestrel. La salida a internet necesaria es solo `api-m.paypal.com` (o `api-m.sandbox.paypal.com`) por el puerto 443. Se corre con un usuario sin privilegios y con el sistema de archivos de solo lectura salvo el directorio del registro, que con el registro en PostgreSQL no hace falta.

Con varias réplicas, todas comparten el registro de idempotencia en PostgreSQL. Aun sin un registro común, por ejemplo después de una restauración a un punto anterior, lo que evita repetir un efecto es el `PayPal-Request-Id` de cada pedido que cambia algo, que es el mismo en todas.

## Operaciones

Todas son `POST /v0/payment/{operación}` con el sobre del protocolo. `create_checkout` y `refund` exigen `Idempotency-Key`. Los importes viajan como texto decimal con moneda: `{ "value": "30.50", "currency": "USD" }`. Las fechas salen en UTC. Cada operación declara su plazo en el manifiesto (`timeouts`), porque puede pedir el token antes de su llamada.

### create_checkout

Crea una orden en `POST /v2/checkout/orders`.

```json
{
  "attempt_id": "att-1042",
  "amount": { "value": "30.50", "currency": "USD" },
  "items": [
    { "id": "doc-42", "title": "Factura A-0001-00000042", "quantity": 1, "unit_price": "20.00" },
    { "id": "doc-43", "title": "Factura A-0001-00000043", "quantity": 1, "unit_price": "10.50" }
  ],
  "return_urls": { "success": "https://...", "pending": "https://...", "failure": "https://..." },
  "notification_url": "https://.../webhooks/inst-3"
}
```

- `attempt_id` viaja como `custom_id`, que es lo que vuelve como `external_reference`, y como `invoice_id`, que PayPal no deja repetir en un cobro de la cuenta: un intento no se puede pagar dos veces. Admite letras, dígitos, `-`, `_` y `.`, hasta 127.
- PayPal cobra la suma de `quantity` por `unit_price`. Esa suma tiene que ser `amount.value`. Si no lo es, o falta algo, la respuesta es `invalid_input` y no se llama a PayPal. Los importes admiten los decimales de la moneda.
- `return_urls.success` es obligatoria (sin ella el cliente queda en PayPal después de aprobar) y es la `return_url` de la orden. `return_urls.failure` es la `cancel_url`, y si falta se usa la de éxito. Tienen que ser `http` o `https`.
- `notification_url` se ignora: PayPal no tiene aviso por orden, los webhooks se configuran en la app.
- La orden lleva `reference_id` `billing-oss`, una descripción con los títulos (cortada a 127 caracteres), los ítems como `DIGITAL_GOODS` con `sku` igual a `id`, `user_action` `PAY_NOW` y `shipping_preference` `NO_SHIPPING`.
- `PayPal-Request-Id` es `checkout-` más el intento: una repetición devuelve la misma orden.

Respuesta:

```json
{ "redirect_url": "https://www.paypal.com/checkoutnow?token=5O190127TN364715T", "checkout_external_id": "5O190127TN364715T" }
```

`redirect_url` es el enlace `payer-action` de la orden (o `approve`, en la forma vieja) y solo se devuelve si es `https`. Si no viene, la respuesta es `outcome_unknown`, porque la orden pudo haberse creado. `checkout_external_id` es el ID de la orden. El núcleo lo tiene que guardar en el intento: con él consulta las órdenes aprobadas que no trajo el webhook (ver "Lo que falta en el núcleo").

### get_payment

Entrada: `{ "payment_external_id": "3C679366HH908993F" }`, el ID de una captura, o `{ "payment_external_id": "order:5O190127TN364715T" }`, el de una orden (sección "Cómo se cobra"). Los IDs son letras y dígitos.

Con una captura consulta `GET /v2/payments/captures/{id}`. Con una orden consulta `GET /v2/checkout/orders/{id}`: si está `APPROVED` la captura, si está `COMPLETED` lee su captura, y en cualquier otro estado (`CREATED`, `PAYER_ACTION_REQUIRED`, `VOIDED`) da `not_found`. Una orden sin la marca de este módulo también da `not_found` y no se captura.

```json
{
  "payment_external_id": "3C679366HH908993F",
  "status": "approved",
  "amount": { "value": "100.00", "currency": "USD" },
  "refunded": { "value": "0.00", "currency": "USD" },
  "fee": { "value": "3.98", "currency": "USD" },
  "created_at": "2026-10-25T19:01:10Z",
  "approved_at": "2026-10-25T19:01:10Z",
  "external_reference": "att-1042",
  "gateway_status": "COMPLETED",
  "gateway_status_detail": null
}
```

- `amount` es el monto de la captura.
- `fee` es `seller_receivable_breakdown.paypal_fee`, la comisión de PayPal en la moneda del pago. Es `null` mientras la captura está pendiente, porque PayPal no la informa, y si llegara en otra moneda, para no mezclar monedas.
- `refunded` es el monto completo si la captura está `REFUNDED` y `0.00` si no tiene reembolsos. Con `PARTIALLY_REFUNDED` sale de la orden de la captura: la suma de sus reembolsos `COMPLETED` que apuntan a esta captura. Si la captura no tiene orden, o la orden es de otra app de la cuenta y esta no la puede leer, es `null`.
- `approved_at` es la hora de la captura, que es cuando se movió la plata, y es `null` si la captura no se completó.
- `external_reference` es el `custom_id` de la captura, o el de la unidad de compra si la captura viene dentro de la orden.
- `gateway_status` y `gateway_status_detail` (`status_details.reason`) son los de PayPal, sin traducir.
- No se devuelven datos del pagador ni del vendedor.

Estados:

| Estado de la captura | Estado del núcleo |
|---|---|
| `COMPLETED` | `approved` |
| `PENDING` con motivo `BUYER_COMPLAINT` o `CHARGEBACK` | `disputed` |
| `PENDING` con otro motivo | `pending` |
| `DECLINED`, `DENIED`, `FAILED` | `rejected` |
| `PARTIALLY_REFUNDED` | `partially_refunded` |
| `REFUNDED` | `refunded` |
| `REVERSED` | `charged_back` |

`REVERSED` no está en la lista de estados de la referencia de PayPal, pero existe el evento `PAYMENT.CAPTURE.REVERSED`: si llega, PayPal devolvió la plata al comprador. Un estado que el módulo no conoce no se adivina: la respuesta es `upstream_unavailable` con el estado en el mensaje.

La captura sola no muestra las disputas: con una abierta puede seguir `COMPLETED`, y con un contracargo perdido queda `REFUNDED` como un reembolso cualquiera. Por eso, si la captura cobró (`COMPLETED`, `PENDING`, `PARTIALLY_REFUNDED` o `REFUNDED`), consulta además `GET /v1/customer/disputes?disputed_transaction_id={captura}`. Una disputa abierta da `disputed`. Una resuelta a favor del comprador (`RESOLVED_BUYER_FAVOUR` o `ACCEPTED` en `dispute_outcome.outcome_code`, que trae solo la disputa completa, `GET /v1/customer/disputes/{id}`) da `charged_back`. Si la app no tiene la función de disputas o una disputa no se puede leer, se informa la captura con su propio estado.

### verify_webhook

Entrada: lo que llegó al núcleo, sin tocar.

```json
{
  "headers": {
    "paypal-auth-algo": "SHA256withRSA",
    "paypal-cert-url": "https://api.paypal.com/v1/notifications/certs/CERT-...",
    "paypal-transmission-id": "69cd13f0-...",
    "paypal-transmission-sig": "...",
    "paypal-transmission-time": "2026-10-25T19:01:13Z"
  },
  "query": {},
  "body_base64": "eyJpZCI6IldILTU4..."
}
```

Cada valor de `headers` puede ser un texto o una lista de textos y los nombres se comparan sin distinguir mayúsculas. La firma la verifica PayPal: el módulo llama a `POST /v1/notifications/verify-webhook-signature` con los cinco encabezados, el `webhook_id` de la instancia y el evento, y vale si contesta `verification_status` `SUCCESS`.

- PayPal pide el evento exactamente como llegó. El módulo pega el cuerpo crudo en el pedido, sin volver a serializarlo, después de comprobar que es un único objeto JSON en UTF-8. Un cuerpo que no lo es, o que intenta salirse del campo, da `valid: false` sin llamar.
- Si falta un encabezado o no tiene el formato que exige PayPal (`paypal-cert-url` como URL `https`, `paypal-transmission-time` como fecha, y el ID y la firma de la transmisión con el patrón de PayPal), la respuesta es `{ "valid": false, "events": [] }` sin llamar a PayPal. También si PayPal contesta `FAILURE`. No es un error.
- Con los formatos ya comprobados, un 4xx de PayPal es un problema del lado de Hornero (el `webhook_id`, la app o el pedido) y sale como `rejected`. Así el núcleo deja el aviso sin procesar y el trabajo fallido a la vista, en vez de descartarlo para siempre como una firma falsa. El módulo lo registra con nivel de error, porque mientras dure no se procesa ningún aviso.
- No hay ventana de tiempo. La defensa contra repeticiones es la unicidad por ID de evento del núcleo y que el pago siempre se confirma con `get_payment`.
- Si PayPal no responde, es `upstream_unavailable` y el núcleo reintenta la verificación.

Respuesta:

```json
{
  "valid": true,
  "events": [
    { "event_id": "WH-58D329510W468432D-8HN650336L201105X", "type": "PAYMENT.CAPTURE.COMPLETED", "payment_external_id": "3C679366HH908993F" }
  ]
}
```

| Evento | `payment_external_id` |
|---|---|
| `CHECKOUT.ORDER.APPROVED` de una orden de este módulo | `order:` más el ID de la orden |
| `PAYMENT.CAPTURE.REFUNDED`, `PAYMENT.CAPTURE.REVERSED` | La captura del enlace `up` del reembolso. Sin ese enlace no hay evento |
| Otros `PAYMENT.CAPTURE.*` | El `id` de la captura |
| `CUSTOMER.DISPUTE.*` | Cada `disputed_transactions[].seller_transaction_id` |

- `event_id` es el `id` del evento, que PayPal repite en sus reintentos. Si una disputa reclama varias capturas, cada una es un evento con `event_id` igual al `id` más `:` y la captura, para la unicidad del núcleo.
- `type` es el `event_type` de PayPal.
- De los demás eventos, como los de órdenes de otro sistema, se verifica la firma y se devuelve `valid: true` sin eventos.
- Los eventos del simulador de webhooks no se pueden verificar con esta API (su webhook es `WEBHOOK_ID`): dan `valid: false`. Para probar hay que usar el sandbox de verdad.

### refund

Entrada: `{ "payment_external_id": "3C679366HH908993F", "amount": { "value": "30.00", "currency": "USD" } }`. Sin `amount` es un reembolso total y el cuerpo que se manda es `{}`, con lo que PayPal reembolsa lo que queda sin reembolsar. Solo se reembolsan capturas, no órdenes.

Llama a `POST /v2/payments/captures/{id}/refund` con `PayPal-Request-Id` igual a `refund-` más la captura y el `Idempotency-Key` del núcleo. Si la respuesta se pierde, el reintento con la misma clave no reembolsa dos veces. A diferencia de Mercado Pago, la moneda va en el pedido y PayPal rechaza la que no es la de la captura, así que no se consulta la captura antes.

```json
{
  "refund_id": "1JU08902781691411",
  "payment_external_id": "3C679366HH908993F",
  "status": "approved",
  "amount": { "value": "30.00", "currency": "USD" },
  "fee_refunded": { "value": "0.12", "currency": "USD" },
  "total_refunded": { "value": "30.00", "currency": "USD" },
  "gateway_status": "COMPLETED"
}
```

- `status` es `approved` con `COMPLETED` y `pending` en cualquier otro caso, como en Mercado Pago. El estado final del pago se confirma con `get_payment`.
- Un reembolso que PayPal deja `CANCELLED` o `FAILED` no movió plata: da `rejected`, como un 422, con el estado en `details.upstream` y `details.gateway_status` y el ID en `details.refund_id`. La misma clave devuelve el mismo error sin volver a pedir.
- `fee_refunded` es la parte de la comisión que PayPal devuelve al vendedor (`seller_payable_breakdown.paypal_fee`), que puede no ser proporcional a la cobrada.
- `total_refunded` es lo reembolsado en total de la captura, contando este.
- Los rechazos de PayPal, como `REFUND_AMOUNT_EXCEEDED`, `REFUND_TIME_LIMIT_EXCEEDED`, `CAPTURE_FULLY_REFUNDED` o `TRANSACTION_DISPUTED`, dan `rejected` con el código en `details.upstream`. La plata no se movió y el núcleo deja el importe como saldo a favor.

### list_payments

Entrada: `{ "from": "2026-10-25T00:00:00Z", "to": "2026-10-27T00:00:00Z", "offset": 0, "by": "updated" }`.

Busca las transacciones del rango en `GET /v1/reporting/transactions` y consulta cada captura que aparece:

- Un cobro recibido por la API de pagos o por el botón (`T0006` o `T0007`, con monto positivo) es la captura misma. Los otros `T00xx` son cobros de otro tipo de la cuenta compartida y no se consultan.
- Un reembolso o una reversión (`T11xx`) y un ajuste o contracargo (`T12xx`) apuntan a la captura en `paypal_reference_id`.

Así un reembolso o un contracargo de un pago viejo aparece en el rango en que se hizo, con el estado actual de la captura. Por eso `by` (`created`, `approved` o `updated`) da lo mismo: PayPal solo filtra por la fecha de la transacción, y un cambio es una transacción nueva. Una transacción que no es una captura de la API de órdenes (la consulta da 404), o una captura de otra app de la cuenta que esta no puede leer (403 con `PERMISSION_DENIED`), se saltea: si cortara la página, la conciliación fallaría en cada corrida.

- Cada página son 20 transacciones de PayPal, así que `offset` va de 20 en 20. Una página puede traer menos pagos que transacciones.
- El rango es de hasta 31 días. Las transacciones tardan hasta 3 horas en aparecer y PayPal guarda 3 años.
- `total` es la cantidad de transacciones del rango y `next_offset` es `null` en la última página.

```json
{ "payments": [ { "payment_external_id": "3C679366HH908993F", "status": "approved" } ], "total": 4, "offset": 0, "next_offset": null }
```

Cada pago tiene la forma de `get_payment`.

### instance/test

Pide el token (valida `client_id`, `client_secret` y el entorno) y lee el webhook con `GET /v1/notifications/webhooks/{webhook_id}`. Si el webhook no existe en la app, o le falta alguno de los cuatro eventos obligatorios (y no tiene todos, `*`), la respuesta es `invalid_config` con `webhook_id`. Devuelve `{ "environment": "live", "webhook_url": "...", "event_types": [...] }`.

## Monedas

PayPal cobra en 25 monedas: AUD, BRL, CAD, CNY, CZK, DKK, EUR, HKD, HUF, ILS, JPY, MYR, MXN, TWD, NZD, NOK, PHP, PLN, GBP, RUB, SGD, SEK, CHF, THB y USD. ARS no está. Otra moneda da `invalid_input` sin llamar a PayPal, tanto en `create_checkout` como en `refund`.

HUF, JPY y TWD van sin decimales y las demás con 2. Un importe con más decimales de los que acepta la moneda es `invalid_input`. A PayPal se le manda con exactamente esos decimales (`"30.50"`, `"2500"`). BRL, CNY y MYR solo valen para cuentas de ese país: eso lo rechaza PayPal.

La comisión se informa en la moneda del pago. Si la cuenta de PayPal recibe en otra moneda, PayPal convierte, y el módulo no informa lo convertido.

## Errores

| PayPal | Código del protocolo |
|---|---|
| 401 en el token, o 401 dos veces seguidas en la API | `invalid_config`, con `client_id` y `client_secret` en `details.fields` |
| 403 `NOT_AUTHORIZED` sin el issue `PERMISSION_DENIED` (la app no tiene la función) | `invalid_config`, con `client_id` |
| 403 con el issue `PERMISSION_DENIED` (un recurso de otra app o de otro sistema de la misma cuenta) | `rejected` |
| 404 | `not_found` |
| 409, como `PREVIOUS_REQUEST_IN_PROGRESS` | `conflict`, reintentable a los 30 segundos |
| 429 | `rate_limited`, con `retry_after` del encabezado `Retry-After` o 30 segundos |
| 5xx | `upstream_unavailable` |
| Otros 4xx | `rejected`, con `details.http_status`, `details.debug_id` y los códigos de PayPal en `details.upstream` |
| Sin conexión antes de enviar | `upstream_unavailable` |
| Corte o plazo vencido después de enviar | `outcome_unknown` si el pedido cambia algo (crear la orden, capturarla o reembolsar), y `upstream_unavailable` si es una consulta |
| Respuesta que no es JSON, o una redirección | Lo mismo que el corte después de enviar |

- Los códigos de `details.upstream` son el `issue` de cada `details[]` del error, y si no viene ninguno, el `name` y el `message` de la raíz (o `error` y `error_description` del pedido de token). `debug_id` es lo que pide el soporte de PayPal.
- El token y el `client_secret` se tapan si PayPal los repite en un mensaje.
- Los pedidos que cambian algo no se cortan cuando el núcleo se desconecta: si terminan, la SDK guarda el resultado por la clave de idempotencia y el reintento lo recupera.

## Pruebas

```
dotnet test tests/BillingOss.Module.PayPal.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y reemplazan la API por un `HttpMessageHandler` falso que comprueba cada pedido y responde con las formas de la documentación pública de PayPal (las especificaciones OpenAPI de Orders v2, Payments v2, Webhooks v1 y Transaction search v1). Los tokens y el `client_secret` de las pruebas se generan al correr.

### Prueba contra el sandbox

Hace falta una app REST de sandbox. Sin las variables, la prueba queda inconclusa.

```
export PAYPAL_TEST_CLIENT_ID=el-client-id-de-sandbox
export PAYPAL_TEST_CLIENT_SECRET=el-secret-de-sandbox
export PAYPAL_TEST_WEBHOOK_ID=el-id-del-webhook   # opcional
dotnet test tests/BillingOss.Module.PayPal.Tests --filter TestCategory=PayPalReal
```

En PowerShell, `$env:PAYPAL_TEST_CLIENT_ID = '...'`. La prueba usa siempre el entorno `sandbox`: con el webhook prueba `instance/test`, crea una orden de USD 1 que nadie aprueba (no mueve plata), comprueba que esa orden no tiene pago, lista las capturas de los últimos 3 días y consulta la primera, y comprueba que un secret inventado da `invalid_config`. El listado necesita Transaction search activado en la app.

## Fuera de esta versión

- Suscripciones, medios guardados y cobro automático. Los medios PayPal guardados en WHMCS se pierden: esos clientes pagan por redirección.
- Tarjeta sin cuenta de PayPal embebida en la página. Por redirección PayPal ya ofrece pagar con tarjeta como invitado donde lo permite.
- Verificar la firma localmente con el certificado de `paypal-cert-url`, que PayPal recomienda. Ahorra un pedido por webhook pero obliga a descargar y validar el certificado.
- Datos del pagador en la orden, la marca y el idioma de la página de PayPal.

## Pendiente de comprobar con el sandbox

- El recurso de `PAYMENT.CAPTURE.REFUNDED` y de `PAYMENT.CAPTURE.REVERSED`: se asume que es el reembolso con un enlace `up` a la captura, como el reembolso de la API. La reversión no tiene ejemplo en la referencia.
- El estado de una captura revertida (se asume `REVERSED`) y el de una captura con una disputa abierta (se asume `PENDING` con `BUYER_COMPLAINT`).
- Que `status_details.reason` llegue en las capturas pendientes por disputa y no solo en las de la API de pagos v1.
- Cuánto tiempo guarda PayPal el `PayPal-Request-Id` de órdenes, capturas y reembolsos. La referencia no lo dice. Pasado ese plazo, una captura repetida da `ORDER_ALREADY_CAPTURED` (que el módulo resuelve) y un reembolso repetido podría reembolsar de nuevo: por eso el núcleo no repite con la misma clave después del plazo de la sección 6 del protocolo.
- Que `paypal_reference_id` de un reembolso (`T1107`) en la búsqueda sea la captura y venga con tipo `TXN`.
- Que la búsqueda acepte `start_date` y `end_date` en UTC con `Z`, como dice el patrón de la referencia.
- Que las capturas de Orders v2 aparezcan en la búsqueda con el código `T0006`.
- Qué contesta `GET /v2/payments/captures/{id}` para una transacción de otra integración de la misma cuenta, como WHMCS durante el paralelo. Se asume 404, o 403 con `PERMISSION_DENIED`, y las dos se saltean. Otra respuesta cortaría la página de `list_payments`.

## Lo que falta en el núcleo

- Guardar `checkout_external_id` en el intento de pago. Hoy no se guarda.
- Un barrido periódico, por ejemplo cada 15 minutos, de los intentos abiertos de instancias de PayPal con menos de 6 horas, que encole el trabajo de sincronización del pago con `order:` más `checkout_external_id`. El módulo ya lo resuelve: con la orden aprobada la captura, de forma idempotente con `capture-` más la orden, y si todavía no se aprobó da `not_found`, que el barrido toma como todavía sin pago y no como una falla definitiva. Opcionalmente, `/pay/return/{intento}` encola el mismo trabajo con el `token` de la URL.
