# Módulo de Mercado Pago

Módulo de cobros (`payment`) de Hornero para Mercado Pago con Checkout Pro, por redirección. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md), sección 9.3. Llama a la API de Mercado Pago con `HttpClient` y `System.Text.Json`, sin la SDK de Mercado Pago. No guarda configuración: el núcleo manda la de la instancia en cada llamada.

Capacidades: `payment.core`, `payment.checkout`, `payment.webhooks`, `payment.refund`, `payment.reconcile`, `payment.subscriptions` y `payment.subscription_update`.

El manifiesto pide `account.contact` en `data_scopes`, solo para el correo del pagador al crear una suscripción, y trae lo que usa el asistente de cobros del panel: `x-help` con el lugar del panel de Mercado Pago de donde sale cada campo, `currencies`, `browser_hosts` por ambiente y `webhook_events` (`payment` y `subscription_preapproval`, que en el panel son Pagos y Planes y suscripciones).

## Configuración de la instancia

| Campo | Secreto | De dónde sale |
|---|---|---|
| `access_token` | Sí | Credenciales de la aplicación en Tus integraciones de Mercado Pago. Para probar, el access token de prueba |
| `webhook_secret` | Sí | Clave secreta de la aplicación en Webhooks, Configurar notificaciones |

Se validan antes de usarlos: `access_token` en las llamadas a Mercado Pago y `webhook_secret` en `verify_webhook`. `instance/test` revisa los dos. Si faltan, o tienen espacios o caracteres de control (casi siempre un error al copiarlos), la respuesta es `invalid_config` con el campo en `details.fields`.

La URL de la API es fija (`https://api.mercadopago.com`) a propósito: así el access token nunca viaja a un destino que alguien configure. Las redirecciones no se siguen.

## Cómo correrlo

Se configura con las variables de entorno de ASP.NET Core, o con un archivo por clave en `/run/secrets` (por ejemplo `/run/secrets/Module__SigningSecret`), que es como llegan los Docker secrets. La imagen y la receta están en [docs/despliegue](../../../docs/despliegue/README.md):

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma que el núcleo tiene en la instalación de este módulo |
| `Module__PreviousSigningSecret` | Opcional. El secreto anterior, mientras dura una rotación |
| `Module__IdempotencyStorePath` | Archivo SQLite del registro de idempotencia. Por defecto `data/idempotency.db` dentro del directorio del módulo. Tiene que sobrevivir a los reinicios |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `ASPNETCORE_URLS` | Dónde escucha |

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:Module__IdempotencyStorePath = ".\datos\mercadopago\idempotency.db"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5191"
dotnet run --project src/modules/BillingOss.Module.MercadoPago -c Release
```

El protocolo exige TLS entre el núcleo y el módulo, salvo en el mismo host. En producción va un proxy con TLS delante, o se configura el certificado de Kestrel. La salida a internet necesaria es solo `api.mercadopago.com` por el puerto 443. Se corre con un usuario sin privilegios y con el sistema de archivos de solo lectura salvo el directorio del registro.

## Operaciones

Todas son `POST /v0/payment/{operación}` con el sobre del protocolo. `create_checkout` y `refund` exigen `Idempotency-Key`. Los importes viajan como texto decimal con moneda: `{ "value": "12100.00", "currency": "ARS" }`. Las fechas salen en UTC.

### create_checkout

Crea una preferencia de Checkout Pro en `POST /checkout/preferences`.

```json
{
  "attempt_id": "att-1042",
  "amount": { "value": "12100.00", "currency": "ARS" },
  "items": [
    { "id": "svc-77", "title": "VPS Básico, octubre 2026", "quantity": 1, "unit_price": "10000.00" },
    { "title": "IVA 21 %", "quantity": 1, "unit_price": "2100.00" }
  ],
  "return_urls": { "success": "https://...", "pending": "https://...", "failure": "https://..." },
  "notification_url": "https://.../webhooks/inst-3"
}
```

- `attempt_id` viaja como `external_reference` (hasta 256 caracteres). Es lo que vuelve en `get_payment` para encontrar el intento.
- Mercado Pago cobra la suma de `quantity` por `unit_price`. Esa suma tiene que ser `amount.value`. Si no lo es, o falta algo, la respuesta es `invalid_input` y no se llama a Mercado Pago. Los importes admiten hasta 2 decimales.
- `return_urls` y `notification_url` son opcionales y tienen que ser `http` o `https`. Con `return_urls.success` se pide `auto_return` con valor `approved`.
- La preferencia no vence.
- La preferencia no excluye ningún medio de pago: el cliente elige entre todos los que Mercado Pago ofrece en la cuenta (tarjetas, dinero en cuenta, Rapipago y Pago Fácil). Es a propósito.

Respuesta: `{ "redirect_url": "https://www.mercadopago.com.ar/checkout/v1/redirect?pref_id=..." }`. Es el `init_point` de la preferencia y solo se devuelve si es `https`, porque la sigue el navegador del cliente (sección 9.1 del protocolo). El host tiene que estar en la lista de hosts de la instancia del núcleo.

### get_payment

Entrada: `{ "payment_external_id": "123456789" }`. Solo dígitos. Consulta `GET /v1/payments/{id}`. `livemode` sale de `live_mode`: en una instancia de producción el núcleo no registra un pago del ambiente de prueba.

```json
{
  "payment_external_id": "123456789",
  "status": "approved",
  "amount": { "value": "12100.00", "currency": "ARS" },
  "refunded": { "value": "0.00", "currency": "ARS" },
  "fee": { "value": "435.60", "currency": "ARS" },
  "created_at": "2026-10-25T19:00:00Z",
  "approved_at": "2026-10-25T19:01:10Z",
  "external_reference": "att-1042",
  "gateway_status": "approved",
  "gateway_status_detail": "accredited"
}
```

- `amount` es `transaction_amount`, sin los intereses de financiación que paga el comprador.
- `fee` es la suma de `fee_details` que paga el vendedor (`fee_payer` igual a `collector`).
- `approved_at` es `null` mientras no se aprobó.
- `gateway_status` y `gateway_status_detail` son los de Mercado Pago, sin traducir.
- No se devuelven datos del pagador.

Estados:

| Estado de Mercado Pago | Estado del núcleo |
|---|---|
| `approved` | `approved` |
| `approved` con `status_detail` igual a `partially_refunded`, o con algo reembolsado | `partially_refunded` |
| `approved` con todo reembolsado | `refunded` |
| `pending`, `in_process`, `authorized` | `pending` |
| `rejected`, `cancelled` | `rejected` |
| `in_mediation` | `disputed` |
| `charged_back` | `charged_back` |
| `refunded` | `refunded` |

Un estado que el módulo no conoce no se adivina: la respuesta es `upstream_unavailable` con el estado en el mensaje.

### verify_webhook

Entrada: lo que llegó al núcleo, sin tocar, y cuándo llegó.

```json
{
  "headers": { "x-signature": "ts=1742505638683,v1=...", "x-request-id": "bb56a2f1-..." },
  "query": { "data.id": "123456789", "type": "payment" },
  "body_base64": "eyJhY3Rpb24iOi...",
  "received_at": "2025-03-20T21:20:40Z"
}
```

Cada valor de `headers` y de `query` puede ser un texto o una lista de textos. Los nombres se comparan sin distinguir mayúsculas. `received_at` es cuándo recibió el núcleo el aviso, en UTC, y es opcional: un núcleo anterior no lo manda. La verificación es local, no llama a Mercado Pago.

La firma es el HMAC-SHA256 en hexadecimal, con `webhook_secret` como clave, de un texto armado con `id:` y el `data.id` de la URL en minúsculas, `request-id:` y el encabezado `x-request-id`, y `ts:` y el `ts` de `x-signature`, cada parte terminada en punto y coma. Lo que no llegó se quita del texto. Se compara con `v1` en tiempo constante. El texto exacto está en `WebhookSignature.cs`.

- Con `received_at` hay ventana de tiempo: un `ts` firmado más de 24 horas anterior a ese instante no vale, así una notificación capturada no se puede repetir para siempre. Se mide contra la hora de recepción y no contra la de verificación, porque el núcleo verifica después de recibir, con un trabajo que puede demorar. El `ts` se acepta en segundos o en milisegundos Unix. Sin `received_at` no hay ventana, y quedan la unicidad por ID de evento del núcleo y que el pago siempre se confirma con `get_payment`.
- La firma cubre el `data.id` de la URL y no el cuerpo. Por eso el pago del evento sale de la URL, y si el cuerpo nombra otro, la notificación no vale.
- Una firma que no vale, o que falta, no es un error: la respuesta es `{ "valid": false, "events": [] }`.

Respuesta:

```json
{
  "valid": true,
  "events": [
    { "event_id": "123456789:1742505638683", "type": "payment.updated", "payment_external_id": "123456789" }
  ]
}
```

- `event_id` es el pago más el `ts` firmado: lo único que la firma cubre y que cambia entre notificaciones. El `id` de la notificación no se usa porque no está firmado, y una notificación capturada y repetida con otro `id` no es un evento nuevo. Si Mercado Pago reintenta con otra firma, el reintento es otro evento y el núcleo vuelve a consultar el pago, lo que no cambia nada.
- `type` es el `action` de la notificación, o `payment`.
- Las notificaciones de pagos (`type` igual a `payment`) dan un evento con el pago. Las de suscripciones (`subscription_preapproval`) dan `subscription.updated` con `subscription_external_id`, y el núcleo la consulta con `get_subscription`. Todo sale de lo firmado: la suscripción es el `data.id` de la URL y el ID de evento es `subscription:{id}:{ts}`. Sin `data.id` no hay evento, y un cuerpo que habla de otra suscripción no vale. De las demás, como órdenes comerciales, cobros autorizados de una suscripción o contracargos, se verifica la firma y se devuelve `valid: true` sin eventos: el cobro de una suscripción llega también como pago.

### refund

Entrada: `{ "payment_external_id": "123456789", "amount": { "value": "5000.00", "currency": "ARS" } }`. Sin `amount` es un reembolso total y el cuerpo que se manda es `{}`.

1. Consulta el pago para saber su moneda. Un `amount` en otra moneda es `invalid_input` y no se reembolsa, porque Mercado Pago no pide la moneda y movería plata equivocada.
2. Llama a `POST /v1/payments/{id}/refunds` con el `Idempotency-Key` del núcleo como `X-Idempotency-Key`. Si la respuesta se pierde, el reintento con la misma clave no reembolsa dos veces.

```json
{
  "refund_id": "5550001",
  "payment_external_id": "123456789",
  "status": "approved",
  "amount": { "value": "5000.00", "currency": "ARS" },
  "gateway_status": "approved"
}
```

`status` es `approved` si Mercado Pago aprobó el reembolso y `pending` en cualquier otro caso. El estado final del pago se confirma con `get_payment`.

### list_payments

Entrada: `{ "from": "2026-10-25T00:00:00Z", "to": "2026-10-26T00:00:00Z", "offset": 0, "by": "created" }`.

- `by` elige la fecha que filtra: `created` (por defecto), `approved` o `updated`. Para encontrar cambios que el núcleo no recibió, como un reembolso o un contracargo de un pago viejo, conviene `updated`.
- Cada página trae hasta 100 pagos, ordenados por ID. Mercado Pago solo devuelve los últimos 12 meses.

```json
{ "payments": [ { "payment_external_id": "111", "status": "approved" } ], "total": 250, "offset": 0, "next_offset": 100 }
```

Cada pago tiene la forma de `get_payment`. `next_offset` es `null` en la última página.

### create_subscription, get_subscription, cancel_subscription y update_subscription

Débito automático que administra Mercado Pago: una suscripción sin plan (`preapproval`). El cliente la aprueba en Mercado Pago y desde ahí Mercado Pago cobra solo cada período, con dinero en cuenta, tarjeta, Rapipago o Pago Fácil. Si una cuota se rechaza, Mercado Pago reintenta y después de varias rechazadas da de baja la suscripción.

`create_subscription` (con `Idempotency-Key`):

```json
{
  "subscription_id": "e3b0c44298fc1c14",
  "amount": { "value": "12100.00", "currency": "ARS" },
  "interval": "month",
  "interval_count": 1,
  "start_date": "2026-11-05",
  "reason": "Débito automático de tus servicios",
  "back_url": "https://facturacion.example.com/cuenta/medios-de-pago"
}
```

- `POST /preapproval` con `status` igual a `pending`, `subscription_id` como `external_reference` y el correo del pagador de `context.account.contact.email`. Sin ese correo la respuesta es `invalid_input` y no se llama a Mercado Pago.
- `start_date` es una fecha y va al mediodía UTC, así no cambia de día en ninguna zona de Latinoamérica.
- Respuesta: `subscription_external_id`, `status`, `amount`, `external_reference`, `next_charge_at` y `redirect_url`, la URL https donde el cliente la aprueba. El host tiene que estar en los hosts de la instancia.

`get_subscription` lee `GET /preapproval/{id}`. Los estados de Mercado Pago van así: `pending` a `pending`, `authorized` a `active`, `paused` a `paused` y `cancelled` a `cancelled`. Un estado que el módulo no conoce no se adivina.

`cancel_subscription` consulta la suscripción y, si no está cancelada, manda `PUT /preapproval/{id}` con `status` igual a `cancelled`. Repetirla sobre una ya cancelada da lo mismo.

`update_subscription` (`payment.subscription_update`) cambia el monto de los próximos cobros con `PUT /preapproval/{id}` y `auto_recurring.transaction_amount`.

Cada cobro de una suscripción es un pago: `get_payment` y `list_payments` lo devuelven con `subscription_external_id`, que sale de `point_of_interaction.transaction_data.subscription_id` o, si no está, de `metadata.preapproval_id`. El núcleo lo imputa por esa suscripción aunque no tenga un intento propio, y si el pago no trae ninguno de los dos, por su `external_reference`, que Mercado Pago copia de la suscripción y es la referencia que le puso el núcleo. Lo registra siempre, también con el ajuste `Payments:SubscriptionsEnabled` apagado.

### instance/test

Valida el access token con `GET /users/me` y revisa que haya `webhook_secret`. Devuelve `{ "account_id": "...", "site_id": "MLA", "livemode": false }` y nada más de la cuenta. `livemode` es `false` con un token `TEST-` y `null` con uno `APP_USR-`, que puede ser de producción o de un usuario de prueba: el ambiente lo elige el staff en el asistente.

## Errores

| Mercado Pago | Código del protocolo |
|---|---|
| 401 y 403 | `invalid_config`, con `access_token` en `details.fields` |
| 404 | `not_found` |
| 409 con `idempotency_key_already_used` | `idempotency_conflict` |
| 429 | `rate_limited`, con `retry_after` del encabezado `Retry-After` o 30 segundos |
| 5xx | `upstream_unavailable` |
| Otros 4xx | `rejected`, con `details.http_status` y los códigos de Mercado Pago en `details.upstream` |
| Sin conexión antes de enviar | `upstream_unavailable` |
| Corte o plazo vencido después de enviar | `outcome_unknown` si el pedido cambia algo (`create_checkout` y `refund`), y `upstream_unavailable` si es una consulta |
| Respuesta que no es JSON, o una redirección | Lo mismo que el corte después de enviar |

- Los códigos de `details.upstream` salen de `cause[]` (API de pagos), de `errors[]` (API de órdenes) o de `code` y `error` en la raíz. El access token se tapa si Mercado Pago lo repite en un mensaje.
- Con un access token que no existe, Mercado Pago contesta 403 en `/users/me` y en `/checkout/preferences`, y 401 en `/v1/payments/{id}`. Los dos dan `invalid_config`.
- Los reembolsos dan `upstream_unavailable` ante un 5xx, aunque Mercado Pago pudo haberlo ejecutado. Es seguro repetirlo con la misma clave por el `X-Idempotency-Key`.
- Los pedidos que cambian algo no se cortan cuando el núcleo se desconecta: si terminan, la SDK guarda el resultado por la clave de idempotencia y el reintento lo recupera.

## Pruebas

```
dotnet test tests/BillingOss.Module.MercadoPago.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y reemplazan la API por un `HttpMessageHandler` falso que comprueba cada pedido y responde con las formas de la documentación. Las firmas de los webhooks de referencia las calculó aparte Python con HMAC directo, así que el test no depende del código del módulo.

### Prueba contra la API real

Hace falta un access token de prueba de una cuenta vendedor de prueba. Sin la variable, la prueba queda inconclusa.

```
export MP_TEST_ACCESS_TOKEN=el-access-token-de-prueba
dotnet test tests/BillingOss.Module.MercadoPago.Tests --filter TestCategory=MercadoPagoReal
```

En PowerShell, `$env:MP_TEST_ACCESS_TOKEN = '...'`. Con una cuenta de otro país, `MP_TEST_CURRENCY` cambia la moneda de la prueba (`ARS` por defecto). La prueba valida el token con `instance/test`, crea una preferencia (no mueve plata), lista los pagos de las últimas 24 horas, consulta el primero y comprueba que un token inventado da `invalid_config`.

La prueba de suscripciones pide además `MP_TEST_PAYER_EMAIL`, el correo de un usuario comprador de prueba. Crea una suscripción pendiente de aprobación (no cobra nada hasta que el pagador la aprueba), la consulta y la cancela.

## Fuera de esta versión

- Datos del pagador en la preferencia. Mercado Pago los usa para aprobar más pagos. El permiso `account.contact` está pedido, pero hoy solo se usa para la suscripción.
- Suscripciones con plan, pausar y reanudar desde el núcleo, y Pagos Automáticos (tarjeta guardada con cobros iniciados por el comercio), que piden habilitación comercial de Mercado Pago.
- Vencimiento de la preferencia y descriptor del resumen de la tarjeta.
- La API de órdenes, QR y Point.

## Pendiente de comprobar con credenciales de prueba

Lo que no se pudo confirmar en la documentación al escribir el módulo:

- El texto que se firma en `x-signature` (id, request-id y ts). Lo leído confirma el HMAC-SHA256 en hexadecimal con la clave secreta, que `ts` va en milisegundos, que lo que falta se quita y que el `data.id` alfanumérico va en minúsculas. La plantilla exacta salió de esa descripción y de la documentación conocida del formato. Conviene comprobarla con una notificación real o con el simulador de webhooks del panel.
- La forma de la respuesta de `POST /v1/payments/{id}/refunds` y de sus errores.
- Los campos `id` y `site_id` de `GET /users/me`.
- La referencia de búsqueda de pagos marca `external_reference` entre los parámetros obligatorios y el módulo no lo manda. Tampoco está comprobado el formato de las fechas con `Z` en `begin_date` y `end_date`.
- Los valores de `status_detail` de un pago en `charged_back` (`settled` o `reimbursed`) no se distinguen: ambos dan `charged_back` y el detalle queda en `gateway_status_detail`.
- De las suscripciones: que `POST /preapproval` acepte `X-Idempotency-Key`, que el cobro de una suscripción traiga `point_of_interaction.transaction_data.subscription_id` (o `metadata.preapproval_id`) y su `external_reference`, que `PUT /preapproval/{id}` cambie el monto de una suscripción ya aprobada sin pedir otra aprobación, y que las notificaciones de `subscription_preapproval` lleguen con la misma firma `x-signature` y el ID de la suscripción en `data.id`.
