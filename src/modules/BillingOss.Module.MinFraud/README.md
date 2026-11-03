# Módulo de MaxMind minFraud

Módulo de fraude (`fraud`) de Hornero para MaxMind minFraud v2.0. Es el ejemplo del tipo: un servicio aparte que
habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md), sección 9.6, y que el núcleo usa para
evaluar los pedidos ([fraude y acceso](../../../diseno/fraude-y-acceso.md)). Llama a minFraud con `HttpClient` y
`System.Text.Json`, sin la librería de MaxMind. No guarda configuración: el núcleo manda la de la instancia en cada
llamada.

**Estado:** probado solo contra un MaxMind falso, con la forma de la documentación pública de minFraud. La prueba
contra el servicio real (`RealApiTests`) está escrita y queda inconclusa sin credenciales. Hay que correrla con una
cuenta de MaxMind antes de usarlo en producción.

Capacidad: `fraud.core`. Pide `account.contact` y `account.address`.

## Configuración de la instancia

| Campo | Secreto | Qué es |
|---|---|---|
| `account_id` | No | Account ID de MaxMind, solo números |
| `license_key` | Sí | License key de MaxMind con permiso para minFraud |
| `service` | No | `score`, `insights` o `factors` (por defecto). Factors es el único que trae los motivos del puntaje |
| `environment` | No | `production` (por defecto) o `sandbox` |

Las URL son fijas (`minfraud.maxmind.com` y `sandbox.maxmind.com`) a propósito: así la license key nunca viaja a un
destino que alguien configure. Las redirecciones no se siguen. `instance/test` hace una consulta de Score con una IP de
la documentación de MaxMind para probar las credenciales, y esa consulta se cobra.

## Qué manda

| minFraud | Sale de | Con permiso |
|---|---|---|
| `device.ip_address`, `device.user_agent` | `input.request` | No |
| `event.transaction_id`, `event.time` | El ID y la fecha del pedido | No |
| `account.user_id` | El ID de la cuenta | No |
| `order.amount`, `order.currency` | El total de la factura del pedido | No |
| `shopping_cart` | Producto y cantidad de cada renglón | No |
| `billing.country` | El país de la cuenta | No |
| `email.address` (MD5 del correo en minúsculas) y `email.domain` | `context.contact.email` | `account.contact` |
| `billing.phone_number` | `context.contact.phone` | `account.contact` |
| `billing.address`, `billing.city`, `billing.postal` | `context.address` | `account.address` |

El correo nunca sale en claro: minFraud acepta su MD5.

## Qué devuelve

El puntaje de minFraud (`risk_score`, de 0,01 a 99, la probabilidad de fraude en por ciento) como `score`, el ID de la
evaluación como `external_id` y estos motivos, sin repetir:

| Motivo | De dónde |
|---|---|
| `minfraud.<código>` | `risk_score_reasons` de Factors |
| `ip.<código>` | `ip_address.risk_reasons` |
| `ip.anonymous` | `ip_address.traits.is_anonymous` de Insights y Factors |
| `email.disposable`, `email.high_risk` | `email` de Insights y Factors |
| `billing.not_in_ip_country` | `billing_address.is_in_ip_country` |
| `ip.risk` | `ip_address.risk` desde 10, si no hay otro motivo (Score) |

## Errores

| minFraud | Protocolo |
|---|---|
| HTTP 400 | `invalid_input`, con el código de MaxMind en `details.upstream` |
| HTTP 401 y 403 | `invalid_config` |
| HTTP 402 (sin fondos) | `rejected` |
| HTTP 429 | `rate_limited` |
| HTTP 5xx, corte o tiempo vencido | `upstream_unavailable`. Repetir una evaluación es seguro: a lo sumo cuesta otra consulta |

Con un error definitivo, el núcleo aplica su ajuste `FraudReviewOnError`: el pedido va a revisión o pasa.

## Cómo correrlo

Igual que el [módulo de Mercado Pago](../BillingOss.Module.MercadoPago/README.md): `Module__SigningSecret`,
`Module__IdempotencyStorePath` o `Module__IdempotencyConnectionString` y `ASPNETCORE_URLS`. La salida a internet
necesaria es solo `minfraud.maxmind.com` (o `sandbox.maxmind.com`) por el puerto 443.

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5193"
dotnet run --project src/modules/BillingOss.Module.MinFraud -c Release
```

## Prueba contra el servicio real

`tests/BillingOss.Module.MinFraud.Tests/RealApiTests.cs` corre solo con `MAXMIND_ACCOUNT_ID` y `MAXMIND_LICENSE_KEY`
(y `MAXMIND_SANDBOX=1` para el sandbox). Hace dos consultas, que se cobran.
