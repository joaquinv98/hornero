# Facturación avanzada

**Estado:** 2026-10-31 · **Modelo:** secciones 6.3 a 6.7 de [dominio.md](../../diseno/dominio.md) · **Interfaz:**
sección 10 de la decisión de interfaz (D-19)

Lo que se suma a la factura de pedido y a la de renovación: facturas manuales con renglones propios, presupuestos que
se convierten en factura o en pedido, ítems facturables sueltos, recargo por mora, recordatorios configurables, pago
de varias facturas juntas, carga de saldo a favor desde el portal, ajustes de saldo con auditoría y cotizaciones del
dólar para cobrar facturas en dólares en pesos y para vender en pesos precios en dólares. Todo sale de los mismos
casos de uso en el admin, en el portal y en la API propia.

El código está en `src/BillingOss.App/UseCases` (`Invoicing.cs`, `Quotes.cs`, `BillableItems.cs`, `ExchangeRates.cs`,
el recargo en `Billing.cs` y la carga de saldo en `Gateways.cs`), las pantallas del admin en
`src/BillingOss.Web/Pages/Admin/Billing` y las del portal en `src/BillingOss.Web/Pages/Portal`.

## Pantallas

Cada pantalla cuelga del menú clásico (sección 10.2 y 10.3 de la decisión de interfaz) y solo aparece si la persona
la puede abrir.

| Menú | Ruta | Quién | Qué hace |
|---|---|---|---|
| Facturación, Facturas | `/admin/facturas` | Staff con `billing.read` | Facturas y notas de todas las cuentas, con filtros por estado (`borradores`, `por-pagar`, `vencidas`, `pagadas`, `anuladas`), tipo (`factura`, `nota-de-credito`, `nota-de-debito`), cuenta y número |
| Facturación, Notas de crédito | `/admin/facturas?tipo=nota-de-credito` | Staff con `billing.read` | La misma lista, filtrada |
| Botón "Nueva factura" | `/admin/facturas/nueva?cuenta={id}` | Staff con `billing.write` | Factura manual con renglones propios: borrador o emitida enseguida |
| Factura | `/admin/facturas/{id}` | Staff con `billing.read` | Renglones, importes y notas relacionadas. Con `billing.write`: emitir o descartar un borrador manual, pagarla con el saldo a favor y, ya vencida, aplicarle el recargo por mora |
| Facturación, Presupuestos | `/admin/presupuestos` | Staff con `billing.read` | Abiertos primero, con filtro por estado (`abiertos`, `vigentes`, `vencidos` sin respuesta, `aceptados`, `rechazados`, `todos`) y por cuenta. También es la solapa Presupuestos de la ficha: `/admin/cuentas/{id}/presupuestos` |
| Presupuesto | `/admin/presupuestos/nuevo`, `/admin/presupuestos/{id}` | Staff con `billing.read`, para armar `billing.write` | Armar y editar mientras está abierto, mandar al cliente, convertir en factura o en pedido, marcar rechazado, borrar un borrador y duplicar cualquiera en un borrador nuevo |
| Facturación, Ítems facturables | `/admin/items` | Staff con `billing.read`, para cargar `billing.write` | Pendientes, facturados y cancelados por cuenta. Cargar uno, cancelarlo y facturar ya los pendientes de la cuenta |
| Configuración, Monedas | `/admin/monedas` | Staff con `billing.read`, para cargar `billing.write` | La cotización que vale hoy para cada moneda, las últimas, la carga manual y el pedido al Banco Nación |
| Ficha del cliente | `/admin/cuentas/{id}` | Staff con `accounts.read` y `billing.read` | Accesos a sus facturas, presupuestos e ítems, las exenciones de recargo y de recordatorios (`billing.write`) y el saldo a favor con su libro. El ajuste de saldo pide `credit.adjust` |
| Facturación, Facturas | `/pagar` | Cliente con la capacidad de facturas | Pagar una factura, varias elegidas (`?f=12&f=13`) o todas las que debe una cuenta en una moneda. Usa primero el saldo a favor si lo quiere, y cada medio dice cuánto cobra y, si cobra en otra moneda, con qué cotización |
| Facturación, Presupuestos | `/presupuestos`, `/presupuestos/{id}` | Cliente con la capacidad de facturas | Los presupuestos mandados, con el equivalente en la moneda local si están en otra. Aceptar o rechazar uno vigente |
| Facturación, Cargar saldo | `/cargar-saldo` | Cliente con la capacidad de facturas | Cargar saldo a favor con cualquier pasarela que cobre en la moneda elegida, dentro de los límites. Sin límites configurados la página no existe y el menú no la muestra |

Los textos están en castellano y en inglés (`Texts.resx` y `Texts.en.resx`).

## Permisos del staff

| Permiso | Para qué |
|---|---|
| `billing.read` | Ver facturas, presupuestos, ítems facturables, saldos a favor y cotizaciones |
| `billing.write` | Armar y emitir facturas manuales, presupuestos e ítems, aplicar saldo y recargos, eximir cuentas y cargar cotizaciones |
| `credit.adjust` | Sumar o restar saldo a favor a mano. Va aparte porque mueve plata sin documento |

Se dan con el comando `staff add` (ver [docs/interfaz](../interfaz/README.md)). Cada acción mira su permiso: un
formulario armado a mano sin el permiso da 403 y no cambia nada.

## API propia

Los mismos casos de uso, en `/api/v1`, con el permiso de la API key entre paréntesis.

| Método y ruta | Qué hace |
|---|---|
| `POST /invoices` (`documents:write`) | Factura manual: cuenta, renglones, moneda, vencimiento y `draft` para dejarla en borrador |
| `POST /invoices/{id}/issue`, `DELETE /invoices/{id}` (`documents:write`) | Emitir o descartar un borrador manual. Un documento emitido no se borra |
| `POST /invoices/{id}/late-fee` (`documents:write`) | El recargo por mora a pedido, sin esperar los días de la corrida pero con la factura vencida. 422 si no corresponde |
| `GET /accounts/{id}/credit` (`payments:read`) | Saldo a favor por moneda y los últimos 500 movimientos |
| `POST /accounts/{id}/credit/adjustments` (`credit:write`) | Ajuste del staff: moneda, importe (negativo resta) y motivo |
| `POST /accounts/{id}/credit/apply` (`payments:write`) | Paga con el saldo a favor los documentos pedidos |
| `POST /accounts/{id}/credit/top-up` (`payments:write`) | Carga de saldo por una pasarela: devuelve la URL a la que va el navegador |
| `POST /accounts/{id}/billing-exemptions` (`accounts:write`) | Exime a la cuenta del recargo, de los recordatorios o de los dos |
| `GET`, `POST /accounts/{id}/billable-items` (`documents:read`, `documents:write`) | Ítems facturables de la cuenta y alta de uno |
| `POST /accounts/{id}/billable-items/invoice` (`documents:write`) | Factura ya los pendientes pedidos, o todos los de la cuenta |
| `POST /billable-items/{id}/cancel` (`documents:write`) | Cancela uno pendiente |
| `GET /accounts/{id}/quotes`, `GET /quotes/{id}` (`documents:read`) | Presupuestos |
| `POST /quotes`, `PUT /quotes/{id}`, `DELETE /quotes/{id}` (`documents:write`) | Alta, reemplazo mientras está abierto y borrado de un borrador |
| `POST /quotes/{id}/send`, `/decline`, `/convert` (`documents:write`) | Mandar, rechazar y convertir (`{"to": "Invoice"}` o `{"to": "Order"}`) |
| `POST /quotes/{id}/duplicate` (`documents:write`) | Un borrador nuevo con lo mismo, también desde uno cerrado |
| `GET /exchange-rates?currency=USD` (cualquier key) | La cotización vigente y las últimas 30 |
| `POST /exchange-rates`, `POST /exchange-rates/refresh` (`rates:write`) | Cargar una a mano o pedir la del banco, que la trae un trabajo |
| `POST /payments/checkout` (`payments:write`) | Ya existía: ahora toma varias facturas de la misma cuenta y moneda, y la moneda del cobro |
| `POST /payments` (`payments:write`) | Ya existía: un pago manual en otra moneda lleva `rate` (positiva, con ocho decimales como máximo), o usa la cotización vigente. Sin ninguna, 422 y el pago no se registra |

`GET /accounts/{id}/services` muestra `index_currency` e `indexed_unit_price` en los servicios con precio pactado en
otra moneda. Ahí `unit_price` es el de la última factura.

## Ajustes

Van en la sección `Billing` de la configuración (variables de entorno con `__`, por ejemplo
`Billing__TopUp__ARS__Min=1000`). La tabla completa está en el [README](../../README.md).

```json
{
  "Billing": {
    "LateFeeAfterDays": 10,
    "LateFeePercent": 5,
    "LateFeeFixed": { "ARS": 500 },
    "QuoteValidDays": 30,
    "TopUp": { "ARS": { "Min": 1000, "Max": 1000000 }, "USD": { "Min": 5, "Max": 2000 } },
    "ExchangeRates": { "Local": "ARS", "Currencies": [ "USD" ], "Bna": true, "BnaSeries": "divisas", "MaxAgeDays": 5, "IndexPrices": false }
  },
  "Notifications": { "ReminderDaysBefore": "7,3,1", "OverdueDaysAfter": "3,10,20" }
}
```

- El recargo automático se prende con `LateFeeAfterDays`. Con porcentaje o fijo y sin días, solo lo aplica el staff.
- Las monedas de `LateFeeFixed`, `TopUp` y `ExchangeRates:Currencies` no distinguen mayúsculas: `ars` es `ARS`.
- Una cotización cargada a mano lleva una fecha dentro de `MaxAgeDays`: una más vieja no valdría y se rechaza.
- `BnaSeries` en `divisas` es la tabla que pide la RG 5616 para el comprobante. La página del banco no tiene API
  oficial: si deja de entenderse, el trabajo queda muerto y visible y la cotización se carga a mano.
- `IndexPrices` vende en la moneda de la cuenta los planes que solo tienen precio en una de `Currencies`, y factura
  cada renovación con la cotización de su día. Hay que prenderlo junto con una fuente de cotizaciones: sin una
  vigente el pedido falla y la renovación espera. El servicio conserva el modo de carga del precio pactado.

## Impuestos

Los renglones propios (facturas manuales, presupuestos e ítems) pasan por el mismo motor que los productos
([docs/fiscal/impuestos.md](../fiscal/impuestos.md)): gravado es la categoría de servicios digitales y exento y no
gravado las suyas, con las reglas generales y las de la cuenta. Sus precios van en el modo de carga por defecto de la
moneda (Impuestos, Precios y muestra), de fábrica con IVA en pesos y sin IVA en dólares. Un presupuesto que pasa a
pedido crea sus servicios en ese modo. Un renglón por N de un producto que no se pide por cantidad crea N servicios
de una unidad con el mismo precio, y el plan de un complemento no se cotiza: va colgado de un servicio.

El total de un presupuesto sale del mismo motor que la factura que lo cobra: un renglón con plan lleva la categoría del
producto del plan, como lo factura el pedido (telefonía al 27 %, exento), y el total suma los tributos de comprobante,
como una percepción. Así el importe que el cliente acepta es el que se le factura. Un presupuesto que no cierra (un
plan exento con una bonificación gravada) no se guarda, y un pedido imposible es un error del pedido y no una falla.

La nota de débito del recargo copia la categoría y la leyenda de los renglones de la factura en cada alícuota, y el
motor le suma los tributos de comprobante que correspondan. Por eso el porcentaje va sobre lo que se debe sin los
tributos de comprobante de la factura: la nota es el porcentaje de todo lo que se debe. Cada parte es un bruto, así la
nota cierra en el recargo también con precios sin IVA, donde el IVA es la diferencia como en un precio con IVA. La nota
de una factura importada va con el receptor de la cuenta, porque la importada no lo trae. Si la factura no tiene
comprobante fiscal todavía (emisor al primer cobro), la emisión de la nota espera pendiente y sale en cuanto la
factura tiene el suyo. El recargo impago de una factura anulada se anula con ella, la anule la mora, el staff o un reembolso, y uno
cobrado queda para el staff, que lo ve en la factura con su estado. Una nota de débito no recibe recargo, pero sí los
recordatorios y los avisos de vencida, que la nombran como tal.

## Varios nodos

Nada de esto guarda estado en memoria. Cada paso toma en la base lo que cambia, en el orden de la sección 13 del
modelo: el presupuesto antes de convertirlo, la cuenta y cada ítem antes de facturarlo, la factura antes de recargarla
o de aplicarle saldo, y el saldo de la cuenta en esa moneda antes de ajustarlo. La consulta al Banco Nación es un
trabajo con la clave de entidad `exchange-rates`, de a uno entre todos los nodos, y el pedido manual comparte la clave
del minuto, así dos clics son un solo trabajo. La web nunca sale a la red por una cotización.

## Pruebas

| Clase | Qué cubre |
|---|---|
| `AdvancedBillingTests` | Facturas manuales, saldo aplicado a pedido, ajustes con auditoría, ítems en la corrida y en el momento, recargo por mora y recordatorios por día |
| `QuoteTests` | Presupuestos: impuestos, edición, envío, aceptación y rechazo desde el portal, conversión a factura y a pedido con el precio pactado, un renglón por varias unidades que pasa a un servicio por unidad, complementos rechazados, conversiones a la vez y duplicados |
| `ExchangeRateTests` | Banco Nación con su página falsa, carga manual, la vigente de cada día y precios en dólares facturados en pesos. `BnaReal_LaPaginaSeLeeYTraeElDolar` lee la página real y solo corre con `BILLINGOSS_BNA_REAL=1` |
| `MultiInvoicePaymentTests` | Varias facturas en un intento, facturas en dólares cobradas en pesos y en pesos cobradas en dólares, contracargo de un pago convertido, carga de saldo y sus límites |
| `BillingApiTests` | La API de todo lo anterior y sus permisos |
| `BillingUiTests` | Las pantallas del admin y del portal de punta a punta, con la pasarela falsa |

## Pendiente

- Al mezclar con la rama de la interfaz clásica: `/admin/facturas` y `/pagar` existen también en otras ramas
  (interfaz-temas y pasarelas). Queda una página de cada una, con lo mejor de las dos, y la ficha del cliente pasa a
  la cabecera con solapas. Los valores de los filtros ya son los mismos que usa el menú.
- Al mezclar con la rama del catálogo: el equivalente en pesos de los precios en dólares en el catálogo y en el
  carrito (Res. 4/2025 SIC) usa `ExchangeRateService.Current`, y un cambio de plan de un servicio con precio pactado
  en otra moneda tiene que prorratear en esa moneda.
- Un presupuesto en dólares con planes para una cuenta en pesos se convierte hoy solo en factura, también cuando lo
  acepta el cliente. Con `IndexPrices` podría crear servicios con precio pactado en dólares.
- Un pago convertido (una factura en dólares cobrada en pesos) no se reembolsa por la pasarela, que devuelve en la
  moneda del pago: la nota de crédito deja los dólares como saldo a favor y la plata la devuelve el staff a mano.
- El modo "al cargar" del saldo a favor (sección 6.3 del modelo) sigue sin construir: lo decide el contador.
- Un renglón propio elige su tratamiento (gravado, exento o no gravado) y no una categoría del motor: una factura
  manual de telefonía, que lleva la categoría de telecomunicaciones (27 %), todavía no se puede armar. Falta elegir la
  categoría en el editor de renglones, en los ítems y en la API.
- Pagos recibidos y Pagos sin imputar (`/admin/pagos`) los arma la rama interfaz-temas, con su lista, sus filtros y la
  exportación. El alta a mano de un pago (medio, referencia, facturas o todo a saldo, y la cotización si es en otra
  moneda) ya está en `POST /payments`: su pantalla va con la conciliación de transferencias o con
  la ficha de la factura del admin.
- Sin construir, a propósito: ítems facturables que se repiten (un cargo que vuelve cada mes es un servicio de un
  producto manual, que ya tiene ciclo, mora y baja), presupuestos para alguien que todavía no es cliente, el PDF del
  presupuesto y una factura por renglón.
