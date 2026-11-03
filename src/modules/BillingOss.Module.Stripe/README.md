# Módulo de Stripe

Módulo de cobros (`payment`) de Hornero para Stripe, con la página de pago alojada de Stripe (Checkout). Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md), sección 9.3. Llama a la API de Stripe con `HttpClient` y `System.Text.Json`, sin la SDK de Stripe. No guarda configuración: el núcleo manda la de la instancia en cada llamada.

Capacidades: `payment.core`, `payment.checkout`, `payment.webhooks`, `payment.refund`, `payment.reconcile` y `payment.saved_methods`. No declara `payment.subscriptions`: quien factura es el núcleo, así que no se usan Stripe Billing, Stripe Tax ni las facturas de Stripe.

Stripe no abre cuentas en Argentina (en Latinoamérica opera en Brasil y México). Viene de fábrica para los proveedores de los países donde Stripe opera, y su sandbox es donde se prueba el débito con medio guardado. El diseño está en la sección 4.4 de [fiscal, impuestos y cobros](../../../diseno/fiscal-impuestos-y-cobros.md).

## Configuración de la instancia

| Campo | Secreto | De dónde sale |
|---|---|---|
| `api_key` | Sí | Clave restringida (`rk_test_` o `rk_live_`) creada en el panel de Stripe, Desarrolladores, Claves de API. Escritura en Checkout Sessions, PaymentIntents, Customers, PaymentMethods y Refunds. Lectura en Charges, Disputes, Balance y Webhook endpoints. La clave secreta (`sk_`) se acepta y la prueba de la instancia avisa que conviene la restringida |
| `webhook_secret` | Sí | El `whsec_` del destino de avisos que apunta a `/webhooks/{instancia}` del núcleo |

- El ambiente sale del prefijo de la clave, e `instance/test` lo informa como `livemode`.
- La URL de la API es fija (`https://api.stripe.com`) y las redirecciones no se siguen, así la clave nunca viaja a otro destino.
- Cada pedido lleva `Stripe-Version: 2026-09-30.endive`, la versión vigente al escribir el módulo. Subir la versión mensual es un parche y pasar a una mayor, una versión nueva de Hornero.
- Salida solo hacia `api.stripe.com` por 443. El navegador del cliente va a `checkout.stripe.com`.

El manifiesto trae lo que usa el asistente de cobros del panel: `x-help` con el lugar del panel de Stripe de donde sale cada campo, `currencies` (`*`, porque Stripe cobra en más de 135 monedas), `browser_hosts` por ambiente y `webhook_events`, la lista de eventos que tiene que tener el destino de avisos.

## Cómo correrlo

Igual que el de Mercado Pago: variables de entorno de ASP.NET Core o un archivo por clave en `/run/secrets`.

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Obligatoria. El secreto de firma que el núcleo tiene en la instalación de este módulo |
| `Module__PreviousSigningSecret` | Opcional. El secreto anterior, mientras dura una rotación |
| `Module__IdempotencyStorePath` | Archivo SQLite del registro de idempotencia. Por defecto `data/idempotency.db` |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro, para correr más de una réplica ([docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md)) |
| `ASPNETCORE_URLS` | Dónde escucha |

En la receta de despliegue viene incluido y se da de alta solo en el núcleo ([docs/despliegue/receta-chica.md](../../../docs/despliegue/receta-chica.md)).

## Operaciones

Todas son `POST /v0/payment/{operación}` con el sobre del protocolo. `create_checkout`, `refund`, `charge_saved` y `delete_method` exigen `Idempotency-Key`, que el módulo le pasa a Stripe tal cual. Los importes viajan como texto decimal con moneda y el módulo los pasa a la unidad menor de cada moneda: 10.99 USD va como 1099, las monedas sin decimales (JPY, CLP, PYG y las demás de la tabla de `Currencies.cs`) van tal cual y un importe con más decimales de los que admite su moneda es `invalid_input`.

### create_checkout

Crea una sesión de Checkout en modo pago con `POST /v1/checkout/sessions`.

- `attempt_id` viaja como `client_reference_id`, en la metadata de la sesión y en la del PaymentIntent, así `get_payment` lo devuelve como `external_reference`.
- Cada ítem es una línea con `price_data`. La suma tiene que dar `amount.value` y si no, la respuesta es `invalid_input` sin llamar a Stripe. Con más de 100 ítems va una sola línea por el total.
- `return_urls.success` es obligatoria y va como `success_url`. `return_urls.failure`, o la de éxito si falta, va como `cancel_url`. `notification_url` se ignora: en Stripe los avisos son de la cuenta.
- La sesión vence a las 24 horas, que es lo que pone Stripe por defecto. Sin precios adaptativos ni impuestos automáticos, para que moneda y monto sean los del intento.
- El idioma sale de `language` o del idioma de la cuenta en el contexto: castellano va como `es-419`.
- Con `save_method: true` el pago guarda el medio para cobros sin el cliente presente (`setup_future_usage=off_session`, solo tarjetas). Si el núcleo no manda `customer_external_id`, el módulo crea un cliente en Stripe sin datos personales, con el ID de la cuenta en la metadata y la clave `{clave}:customer`, y lo devuelve en `external_ids.customer`.

Respuesta: `redirect_url` (solo si es https) y, con cliente, `external_ids.customer`. No devuelve `checkout_external_id`: el núcleo lo pasaría a `get_payment` en el barrido de checkouts, y `get_payment` acepta el PaymentIntent, no la sesión. Un pago cuyo aviso no llegó lo encuentra la conciliación.

### get_payment

Entrada `{ "payment_external_id": "pi_..." }`. Lee el PaymentIntent con su cargo, su movimiento de saldo y su medio, y sus disputas. Devuelve también `livemode`: en una instancia de producción el núcleo no registra un pago del ambiente de prueba.

| Stripe | Núcleo |
|---|---|
| `succeeded` sin reembolsos ni disputas | `approved` |
| `succeeded` con una parte reembolsada | `partially_refunded` |
| `succeeded` con todo reembolsado | `refunded` |
| Disputa en `needs_response`, `under_review`, `warning_needs_response` o `warning_under_review` | `disputed` |
| Disputa `lost` | `charged_back` |
| Disputa `won`, `warning_closed` o `prevented` | El estado que corresponde sin la disputa |
| `requires_payment_method` con su error en un cobro sin el cliente (`metadata.off_session`) | `rejected` |
| `processing`, `requires_action`, `requires_confirmation`, `requires_payment_method` o `requires_capture` | `pending` |
| `canceled` | `rejected` |

- `amount` es `amount_received` si cobró, `refunded` es el `amount_refunded` del cargo y `approved_at` es la hora del cargo.
- `fee` es la comisión del movimiento de saldo si está en la moneda del pago. Es nula mientras el movimiento no existe o si se liquidó en otra moneda.
- `gateway_status_detail` es el `decline_code` del último error.
- Si el pago guardó el medio, `saved_method` trae `method_external_id`, `customer_external_id`, `type`, `brand`, `last4`, `exp_month` y `exp_year`. Nada más de la tarjeta.
- Un estado que el módulo no conoce no se adivina: `upstream_unavailable`.

### verify_webhook

Verificación local del encabezado `Stripe-Signature`: HMAC-SHA256 en hexadecimal, con `webhook_secret` como clave, de `t`, un punto y el cuerpo crudo, comparado en tiempo constante con cada firma `v1` (durante una rotación Stripe firma con los dos secretos). Con `received_at` la marca tiene que estar a 5 minutos de la recepción, y sin él no hay ventana, como en Mercado Pago.

| Evento | Qué devuelve |
|---|---|
| `checkout.session.completed`, `async_payment_succeeded`, `async_payment_failed` y `expired` | `payment_external_id` del `payment_intent` de la sesión. Sin PaymentIntent no hay evento |
| `payment_intent.succeeded`, `payment_failed`, `processing` y `canceled` | `payment_external_id` del objeto |
| `charge.refunded`, `refund.*` y `charge.dispute.*` | `payment_external_id` del `payment_intent` del objeto |
| `payment_method.detached` | `method.deleted` con `method_external_id` |
| `payment_method.automatically_updated` | `method.updated` con `method_external_id` |

El ID de evento es el `evt_` que Stripe repite en cada reintento. Stripe no garantiza el orden y a veces manda dos eventos por el mismo cambio: lo absorbe que el núcleo siempre confirma con `get_payment`.

### refund

Consulta el PaymentIntent y compara su moneda con la pedida: si difiere es `invalid_input` y no se reembolsa. Después, `POST /v1/refunds` con el monto en la unidad menor (sin monto es total) y la clave del núcleo. `succeeded` es `approved`, `pending` y `requires_action` son `pending`, y `failed` y `canceled` son `rejected` con `failure_reason` en `details.upstream`.

### list_payments

Concilia por movimientos de saldo: `GET /v1/balance_transactions` del rango, de a 20 por página. Los cobros, reembolsos y disputas llevan a su PaymentIntent, que vuelve con la forma de `get_payment`, una vez por página. Stripe pagina con cursor: `next_cursor` es el último movimiento de la página, y el núcleo lo manda en `cursor` para pedir la siguiente. Es `null` en la última.

### charge_saved

Cobra sin el cliente presente con un medio guardado. Entrada: `attempt_id`, `amount`, `description`, `customer_external_id` y `method_external_id`.

1. Busca entre los PaymentIntents del cliente de los últimos 3 días uno con ese `attempt_id`. El listado es inmediato, así que si un pedido anterior cobró y se perdió la respuesta, lo encuentra y lo devuelve sin cobrar de nuevo. Si ese cobro había sido rechazado, devuelve el rechazo.
2. Si no existe, `POST /v1/payment_intents` con `off_session=true`, `confirm=true`, `metadata[off_session]=true` y la clave del núcleo (`autocharge-{intento}`). La marca hace que `get_payment` informe `rejected` un cobro rechazado, que sin ella sería `pending` como en el checkout.

La respuesta tiene la forma de `get_payment`. Un rechazo de la tarjeta es `rejected` con los códigos de Stripe y `details.retry`:

| `details.retry` | Cuándo |
|---|---|
| `later` | Fondos insuficientes, rechazo genérico, error del emisor o `advice_code` igual a `try_again_later` |
| `never` | Tarjeta vencida, perdida, robada, fraudulenta o restringida, o `advice_code` igual a `do_not_try_again` |
| `customer_action` | El banco pide autenticación (`authentication_required`) o `advice_code` igual a `confirm_card_data` |

Stripe guarda bajo la clave de idempotencia también las respuestas 500, así que repetir con la misma clave devolvería el mismo 500 hasta que Stripe la olvide. Ante un 500 (o un 424 u otro 5xx) el módulo vuelve a buscar el cobro del intento como en el paso 1. Si lo encuentra lo devuelve, y si no existe pide con una clave derivada de la hora (`autocharge-{intento}:aaaammddhh`), así cada reintento posterior tiene otra oportunidad. Si también da 500, el resultado es `outcome_unknown` y el núcleo repite con su clave más tarde. Nunca pide con otra clave sin buscar antes, que es lo que evita cobrar dos veces.

### get_method y delete_method

`get_method` devuelve el medio con `status` en `active`, o `deleted` si ya no está en ningún cliente o no existe. `delete_method` lo desprende del cliente con `POST /v1/payment_methods/{id}/detach`. Si ya estaba desprendido o no existe, también es éxito.

### instance/test

`GET /v1/balance` valida la clave y da el modo. Después pide un listado de uno de cada recurso que usan las operaciones
(PaymentIntents, Charges, Disputes, Customers, PaymentMethods, Refunds, Checkout Sessions y Balance transactions):
a una clave restringida a la que le falte un permiso de lectura Stripe le contesta 403, y la prueba responde
`invalid_config` con `api_key` y el recurso en el mensaje. Así una clave incompleta no se activa para después dejar
sin registrar los avisos y la conciliación. La escritura la prueba el pago de prueba del asistente. Con `webhook_url` revisa en `GET /v1/webhook_endpoints` que haya un destino activo hacia esa URL con todos los eventos de `webhook_events` (o con `*`). Si falta, responde `invalid_config` con `webhook_secret`. Devuelve `livemode`, `warnings`, `webhook_url` y `event_types`.

## Errores

| Stripe | Código del protocolo |
|---|---|
| 401 | `invalid_config` con `api_key` |
| 403, permiso que le falta a la clave restringida | `invalid_config` con `api_key` y el permiso en el mensaje |
| 404 | `not_found` |
| 400 con `param` | `invalid_input` con el campo en `details.fields` |
| 400 `amount_too_small` | `invalid_input` con `amount` |
| 400 `idempotency_error` | `idempotency_conflict` |
| Otros 400, como `charge_already_refunded` | `rejected` con el código en `details.upstream` |
| 402 `card_error` | `rejected` con `code`, `decline_code`, `advice_code` y `network_decline_code` |
| 409, otro pedido en curso con la misma clave | `conflict`, reintentable |
| 429 | `rate_limited` con `Retry-After` o 30 segundos |
| 424, 5xx, corte o respuesta que no es JSON después de enviar | `outcome_unknown` si el pedido mueve algo, `upstream_unavailable` si es una consulta |

La clave se tapa si Stripe la repite en un mensaje, y nunca sale en respuestas ni en logs.

## Pruebas

```
dotnet test tests/BillingOss.Module.Stripe.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y reemplazan la API por un `HttpMessageHandler` falso que comprueba cada pedido y responde con las formas de la referencia de Stripe. La firma de referencia de los avisos se calculó aparte con `openssl dgst -sha256 -hmac`.

### Prueba contra el sandbox

Hace falta una clave de prueba (`rk_test_` o `sk_test_`). Sin la variable, o con una clave de producción, la prueba queda inconclusa.

```
export STRIPE_TEST_API_KEY=la-clave-de-prueba
dotnet test tests/BillingOss.Module.Stripe.Tests --filter TestCategory=StripeReal
```

`STRIPE_TEST_CURRENCY` cambia la moneda de la prueba (`USD` por defecto). La prueba valida la clave, crea una sesión de Checkout con medio guardado (no mueve plata), lista los movimientos del último día y comprueba que una clave inventada da `invalid_config`.

## Fuera de esta versión

Stripe Billing y Stripe Tax, pago embebido con Elements, débitos bancarios con mandato (SEPA o ACH), Connect, avisos tempranos de fraude, presentación de evidencia de disputas y datos del pagador, que pedirían `account.contact`.

## Pendiente de comprobar en el sandbox

- La versión `2026-09-30.endive` y el nombre del ajuste que apaga los precios adaptativos (`adaptive_pricing[enabled]`).
- Si una sesión vencida cancela su PaymentIntent, y si un reembolso que falla después de aprobado baja `amount_refunded`.
- Cuánto tarda el movimiento de saldo con la captura asíncrona, y si los destinos de avisos creados con la API v2 aparecen en `/v1/webhook_endpoints`.
- El texto del acuerdo de reutilización del medio en `es-419`.
