# Módulo de CryptoCloud

Módulo de cobros (`payment`) de Hornero para CryptoCloud, que en junio de 2026 pasó a llamarse Trybit. Cobra en cripto por redirección: crea una factura de CryptoCloud y el cliente paga en la página de la factura. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md), sección 9.3, con el mismo contrato que el [módulo de Mercado Pago](../BillingOss.Module.MercadoPago/README.md). Llama a la API v2 con `HttpClient` y `System.Text.Json`. No guarda configuración: el núcleo manda la de la instancia en cada llamada.

Capacidades: `payment.core`, `payment.checkout`, `payment.webhooks` y `payment.reconcile`.

**Lo que CryptoCloud no tiene.** La API no tiene reembolsos (solo retiros del saldo) ni débitos recurrentes. Por eso el manifiesto no declara `payment.refund` ni `payment.subscriptions`, y `refund`, `create_subscription` y `cancel_subscription` contestan `501 not_supported` sin llamar a CryptoCloud. Un reembolso se hace a mano desde el panel de CryptoCloud y el staff lo carga en el núcleo.

## Configuración de la instancia

| Campo | Secreto | De dónde sale |
|---|---|---|
| `shop_id` | No | Identificador del proyecto en el panel de CryptoCloud |
| `api_key` | Sí | API key del proyecto, en Integración y API |
| `secret_key` | Sí | Secret key del proyecto, la que firma los avisos de pago |
| `invoice_hours` | No | Horas que vive cada factura, de 1 a 720. Sin valor, 24 horas |
| `allow_test_invoices` | No | `true` para tomar como reales las facturas en modo prueba. Por defecto `false`. Solo para probar la instancia |

Se validan antes de usarlos: `shop_id` y `api_key` en las llamadas a CryptoCloud, `secret_key` en `verify_webhook` e `invoice_hours` donde se usa. `instance/test` revisa todos. Si faltan, o tienen espacios o caracteres de control (casi siempre un error al copiarlos), la respuesta es `invalid_config` con el campo en `details.fields`. El tope de 720 horas es propio: CryptoCloud no documenta uno.

La URL de la API es fija (`https://api.trybit.com/v2/`, la que publica la documentación desde el cambio de nombre) a propósito: así la API key nunca viaja a un destino que alguien configure. Las redirecciones no se siguen. La API key va en el encabezado `Authorization` con el esquema `Token`.

En el núcleo, la lista de hosts que puede visitar el navegador de la instancia tiene que incluir el del link de pago, `pay.trybit.com` según la documentación.

### Configuración del proyecto en CryptoCloud

Las URLs de éxito, de error y de aviso de pago son del proyecto, no de cada factura, así que el módulo ignora `return_urls` y `notification_url` de `create_checkout`. En el panel se configuran:

- La URL de aviso (postback) apuntando a `/webhooks/{instancia}` del núcleo. Va a una sola URL por proyecto: durante el paralelo con el sistema actual hace falta un proyecto aparte para Hornero, o el cambio se hace en el corte.
- Las URLs de éxito y de error apuntando a la página de retorno del núcleo, si se quiere que el cliente vuelva.
- El formato del aviso, JSON o formulario. El módulo lee los dos, pero en JSON el aviso trae el estado de la factura (ver `verify_webhook`).

**Modo prueba.** Todo proyecto nuevo arranca en modo prueba hasta que CryptoCloud aprueba la salida. En ese modo el staff confirma una factura desde el panel sin que entre plata, la factura pasa a `paid` y CryptoCloud manda el aviso. Las facturas traen `test_mode: true`, y el módulo las informa como `rejected` con `gateway_status_detail` igual a `test_mode`, así el núcleo nunca imputa un cobro que no existió. Para probar el circuito completo con un proyecto de prueba se habilita `allow_test_invoices` en una instancia de prueba, nunca en la productiva.

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
$env:Module__IdempotencyStorePath = ".\datos\cryptocloud\idempotency.db"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5192"
dotnet run --project src/modules/BillingOss.Module.CryptoCloud -c Release
```

El protocolo exige TLS entre el núcleo y el módulo, salvo en el mismo host. En producción va un proxy con TLS delante, o se configura el certificado de Kestrel. La salida a internet necesaria es solo `api.trybit.com` por el puerto 443. Se corre con un usuario sin privilegios y con el sistema de archivos de solo lectura salvo el directorio del registro, que con el registro en PostgreSQL no hace falta.

## Operaciones

Todas son `POST /v0/payment/{operación}` con el sobre del protocolo. `create_checkout` exige `Idempotency-Key`. Los importes viajan como texto decimal con moneda: `{ "value": "34.00", "currency": "USD" }`. Las fechas salen en UTC.

### create_checkout

Crea una factura en `POST /v2/invoice/create`.

```json
{
  "attempt_id": "0f3c2a9be1d84c7a9e5b6d4c3b2a1908",
  "amount": { "value": "34.00", "currency": "USD" }
}
```

- `attempt_id` viaja como `order_id` (hasta 256 caracteres). Es lo que vuelve en `get_payment` para encontrar el intento.
- **Solo USD.** CryptoCloud crea facturas en muchas monedas, pero informa las comisiones en USD y el núcleo toma la comisión en la moneda del pago. Un intento en otra moneda da `invalid_input` en `amount.currency` y no llama a CryptoCloud.
- El monto tiene que ser mayor que cero y con hasta 2 decimales. Viaja como número.
- Siempre se manda `add_fields.time_to_pay` con `invoice_hours`, o 24 horas sin valor. Así la vida de la factura no depende de lo que tenga el proyecto.
- `items`, `return_urls` y `notification_url` se ignoran: CryptoCloud cobra un monto y las URLs son del proyecto. Tampoco se manda el correo del cliente, porque el módulo no pide datos personales.

Respuesta: `{ "redirect_url": "https://pay.trybit.com/89UX09KA", "checkout_external_id": "INV-89UX09KA" }`. `redirect_url` es el `link` de la factura y solo se devuelve si es `https`, porque lo sigue el navegador del cliente (sección 9.1 del protocolo). `checkout_external_id` es el uuid de la factura, el mismo ID que después usa `get_payment`.

CryptoCloud no acepta una clave de idempotencia. Si la respuesta se pierde después de enviar, el resultado es `outcome_unknown` y el reintento del núcleo crea otra factura. No mueve plata: el cliente solo ve el link de la que le llegó, y la otra vence sin pagarse.

### get_payment

Entrada: `{ "payment_external_id": "INV-89UX09KA" }`. Acepta el uuid con o sin el prefijo `INV-` (el aviso de pago lo manda sin él) y siempre contesta con el prefijo. Consulta `POST /v2/invoice/merchant/info` con ese uuid.

```json
{
  "payment_external_id": "INV-89UX09KA",
  "status": "approved",
  "amount": { "value": "34.00", "currency": "USD" },
  "refunded": { "value": "0.00", "currency": "USD" },
  "fee": { "value": "0.48", "currency": "USD" },
  "created_at": "2026-10-25T19:00:00Z",
  "approved_at": "2026-10-25T19:12:10Z",
  "external_reference": "0f3c2a9be1d84c7a9e5b6d4c3b2a1908",
  "gateway_status": "paid",
  "gateway_status_detail": "success"
}
```

- `amount` es `amount_in_fiat` en `fiat_currency`, la moneda en que se creó la factura, redondeado a centavos porque llega como número de punto flotante. Si falta `fiat_currency`, la factura es en USD (la moneda por defecto) y sin `amount_in_fiat` se usa `amount_usd`.
- `fee` es lo que paga el comercio: la comisión de la red (`fee_usd`) y la del servicio (`service_fee_usd`), cada una salvo que su lado (`side_commission` y `side_commission_cc`) diga `client`. Solo se informa en facturas en USD.
- `refunded` es siempre cero: no hay reembolsos por API.
- `approved_at` es `date_finished` cuando el estado es `approved`, y `null` si no.
- `external_reference` es el `order_id`. CryptoCloud manda el texto `None` cuando no hay, y eso da `null`.
- `gateway_status` y `gateway_status_detail` son `status` e `invoice_status` de CryptoCloud, sin traducir.
- No se devuelven datos del pagador (correo, teléfono) ni la dirección de la billetera.

- Una factura en modo prueba (`test_mode`) sale como `rejected` con `gateway_status_detail` igual a `test_mode`, salvo que la instancia tenga `allow_test_invoices`.

Si CryptoCloud no devuelve esa factura, la respuesta es `not_found`. Si contesta sin la lista de facturas, es `upstream_unavailable`: `not_found` es definitivo para el núcleo y una respuesta rara no alcanza para decirlo. Un ID que no tiene la forma de un uuid de factura, como los `ccp_` de cobros viejos importados, da `invalid_input`.

Estados:

| Estado de CryptoCloud | Estado del núcleo |
|---|---|
| `paid` | `approved` |
| `overpaid` | `approved`. El núcleo imputa el monto de la factura y el excedente queda en el saldo de CryptoCloud: lo revisa el staff |
| `created`, `partial` | `pending` |
| `canceled` | `rejected`. Si la factura recibió un pago parcial antes de vencer, esa plata queda en el saldo de CryptoCloud y la revisa el staff |

Un estado que el módulo no conoce no se adivina: la respuesta es `upstream_unavailable` con el estado en el mensaje.

### verify_webhook

Entrada: lo que llegó al núcleo, sin tocar. Del pedido solo se usa el cuerpo, porque la firma va adentro.

```json
{
  "headers": { "content-type": "application/json" },
  "query": {},
  "body_base64": "eyJzdGF0dXMiOiJzdWNjZXNzIiwiaW52b2ljZV9pZCI6..."
}
```

El aviso de pago (postback) llega en JSON o como formulario, según el proyecto. Trae `status`, `invoice_id` (el uuid sin `INV-`), `amount_crypto`, `currency`, `order_id` y el campo `token`, y en JSON además `invoice_info` con la factura. El módulo prueba primero JSON y después formulario. La verificación es local, no llama a CryptoCloud.

El `token` es un JWT HS256 firmado con la `secret_key`. El módulo lo verifica a mano: la cabecera tiene que declarar `HS256` (un `none` u otro algoritmo no pasa) y la firma se compara en tiempo constante. El código está en `PostbackToken.cs`.

- Un `exp` de más de 7 días atrás no vale. El token vence a los 5 minutos, pero el núcleo verifica después de recibir, con un trabajo que puede demorar. La tolerancia alcanza para eso y acota lo que sirve un token que se filtre. Un token sin `exp` vale si la firma vale.
- La firma no cubre el cuerpo. Las claims no están documentadas y el ejemplo oficial solo trae un `id` numérico y `exp`, sin la factura. Por eso la firma prueba que el aviso salió de CryptoCloud, y la factura sale del cuerpo sin garantía. Alcanza porque el webhook nunca da algo por pagado: el núcleo consulta `get_payment` y valida monto, moneda y referencia contra el intento.
- Una firma que no vale, o que falta, no es un error: la respuesta es `{ "valid": false, "events": [] }`.

Respuesta:

```json
{
  "valid": true,
  "events": [
    { "event_id": "INV-ILRAJE1Q:3f9a0c2b7d41e865", "type": "invoice.paid", "payment_external_id": "INV-ILRAJE1Q" }
  ]
}
```

- `payment_external_id` es `invoice_id` con el prefijo `INV-`, o el `uuid` de `invoice_info` si no vino `invoice_id`. Si ninguno tiene forma de uuid de factura, la firma vale pero no hay eventos.
- `event_id` es el uuid más los primeros 16 caracteres hexadecimales del SHA-256 de la firma del token. CryptoCloud firma un token nuevo por aviso, así que cada aviso es un evento. Un ID armado solo con datos del cuerpo, que la firma no cubre, dejaría que alguien con un token filtrado ocupara de antemano el ID de un aviso real y el núcleo lo descartara como repetido. Con la firma, repetir un token da IDs que no son de avisos futuros. Un reenvío con otro token es otro evento, y el núcleo lo absorbe porque el registro del pago es idempotente.
- `type` es `invoice.` más el estado de la factura de `invoice_info.status`. En formulario no viene el estado y se usa `paid`, que es el único aviso documentado.

### list_payments

Entrada: `{ "from": "2026-10-25T00:00:00Z", "to": "2026-10-26T00:00:00Z", "offset": 0, "by": "created" }`. Consulta `POST /v2/invoice/merchant/list`.

- CryptoCloud filtra por días enteros (`dd.mm.yyyy`) y por la fecha de creación. El módulo pide los días UTC que cubren el rango, con `to` exclusivo, así que puede traer facturas de más pero no de menos.
- `by` puede ser `created` (por defecto), `approved` o `updated`. Para los dos últimos el rango empieza 720 horas antes, lo más que puede vivir una factura: lo que se pagó o cambió en el rango se creó a lo sumo ese tiempo antes, aunque `invoice_hours` haya bajado después. El núcleo concilia con `updated`. Con el volumen de cobros en cripto, mirar 30 días para atrás no pesa.
- Una factura que no se puede leer (estado desconocido, sin uuid o sin monto) se omite con un aviso en el log, solo con su uuid. Así una factura rara, quizás de otro sistema que usa el mismo proyecto, no traba la conciliación del resto. `total` y `next_offset` no cambian.
- Cada página trae hasta 100 facturas, el máximo de CryptoCloud. En la API, `limit` es la posición del último registro y no la cantidad: el módulo manda `offset` más 100.

```json
{ "payments": [ { "payment_external_id": "INV-89UX09KA", "status": "approved" } ], "total": 150, "offset": 0, "next_offset": 100 }
```

Cada pago tiene la forma de `get_payment`. `total` es `all_count` de CryptoCloud. `next_offset` es `null` en la última página.

### instance/test

Valida la API key con `POST /v2/merchant/wallet/balance/all`, que no tiene efectos, y revisa `shop_id`, `secret_key` e `invoice_hours`. Devuelve `{}`: los saldos no hacen falta y no se devuelven. El `shop_id` no tiene una consulta sin efectos, así que si no es del proyecto se ve en el primer `create_checkout`, como `invalid_config`.

## Errores

CryptoCloud contesta `{ "status": "success", "result": ... }` o `{ "status": "error", "result": { ... } }`.

| CryptoCloud | Código del protocolo |
|---|---|
| 401 y 403, o cualquier error con la clave `authorization` (el único documentado, sin su código HTTP) | `invalid_config`, con `api_key` en `details.fields` |
| 404 | `not_found` |
| 429 | `rate_limited`, con `retry_after` del encabezado `Retry-After` o 30 segundos |
| 5xx | `outcome_unknown` en `create_checkout`, porque puede venir de un proxy con la factura ya creada, y `upstream_unavailable` en las consultas |
| Otro 4xx, o un 2xx con `status` igual a `error`, que nombra a `shop_id` | `invalid_config`, con `shop_id` en `details.fields` |
| Otro 4xx, o un 2xx con `status` igual a `error` | `rejected`, con `details.http_status` y los errores de CryptoCloud en `details.upstream` |
| Sin conexión antes de enviar | `upstream_unavailable` |
| Corte o plazo vencido después de enviar | `outcome_unknown` en `create_checkout`, y `upstream_unavailable` en las consultas |
| Respuesta que no es JSON, o una redirección | Lo mismo que el corte después de enviar |
| `create_checkout` con un 2xx sin el uuid o sin un link `https` | `outcome_unknown`: la factura pudo haberse creado |

- `details.upstream` sale de `result`: cada clave es el `code` y su texto, o la lista de textos unida, es el `message`. Si `result` es un texto, el código es `error`. Si no hay `result`, se usa el `detail` de la raíz, y si tampoco, el código HTTP. La API key se tapa si CryptoCloud la repite en un mensaje.
- `create_checkout` no se corta cuando el núcleo se desconecta: si termina, la SDK guarda el resultado por la clave de idempotencia y el reintento lo recupera.

## Pruebas

```
dotnet test tests/BillingOss.Module.CryptoCloud.Tests
```

Los tests levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y reemplazan la API por un `HttpMessageHandler` falso que comprueba cada pedido y responde con las formas de la documentación. El JWT de referencia de los avisos lo calculó aparte Python con HMAC directo, y los tests arman los demás a mano, así que no dependen del código del módulo.

### Prueba contra la API real

Hacen falta el `shop_id` y la API key de un proyecto de prueba. Sin las variables, la prueba queda inconclusa.

```
export CRYPTOCLOUD_TEST_SHOP_ID=el-shop-id
export CRYPTOCLOUD_TEST_API_KEY=la-api-key
dotnet test tests/BillingOss.Module.CryptoCloud.Tests --filter TestCategory=CryptoCloudReal
```

En PowerShell, `$env:CRYPTOCLOUD_TEST_API_KEY = '...'`. La prueba usa `allow_test_invoices`, porque el proyecto de prueba está en modo prueba. Valida la API key con `instance/test`, crea una factura de 1 USD que vive una hora (no mueve plata), la consulta con `get_payment` y comprueba estado, monto y referencia, la busca en `list_payments` y comprueba que ahí también venga la referencia, y comprueba que una API key inventada da `invalid_config`. No prueba los avisos de pago: eso pide un pago real.

## Fuera de esta versión

- Reembolsos y suscripciones: la API no los tiene.
- Cobros en otra moneda que USD. Hace falta que el núcleo tome la comisión con su propia moneda.
- Elegir las criptomonedas de la factura (`available_currencies`) o fijar una (`cryptocurrency`).
- Correo del pagador en la factura. Pediría `account.contact` en `data_scopes`.
- Avisar al staff de un excedente (`overpaid`) o de un pago parcial que venció. Hoy queda en `gateway_status`.

## Pendiente de comprobar con un proyecto de prueba

Lo que no se pudo confirmar en la documentación al escribir el módulo (docs.trybit.com, a donde redirige docs.cryptocloud.plus):

- Que `invoice/merchant/info` y `invoice/merchant/list` devuelvan `order_id`. Aparece en los ejemplos pero no en la tabla de campos, y sin él el núcleo no encuentra el intento del pago. La prueba real lo comprueba.
- Que `fiat_currency` venga en las facturas: está en la tabla de campos y no en los ejemplos.
- Qué fecha filtra `invoice/merchant/list`, si acepta un rango de 32 días y si `limit` es de verdad la posición final y no la cantidad, como dice la documentación.
- Que `test_mode` venga en las facturas de `invoice/merchant/info` e `invoice/merchant/list` como booleano. Está en los ejemplos de `invoice/create` y de la consulta.
- Los errores de la API: la documentación solo muestra el de autorización. Tampoco está claro qué contesta `invoice/merchant/info` con un uuid que no existe.
- Las claims del JWT del aviso y si hay avisos en `partial` o en `overpaid`, y reintentos.
- El nombre del campo del lado de la comisión del servicio, que la documentación llama `side_commission_cc` en una página y `side_commission_service` en otra. El módulo lee los dos.
- Si `api.cryptocloud.plus` sigue respondiendo. El módulo usa solo `api.trybit.com`.
