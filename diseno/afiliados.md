# Programa de afiliados

**Estado:** v0, 2026-10-29 · **Parte de:** [diseño del núcleo](README.md) 

Un cliente se suma al programa desde el portal, comparte su enlace y cobra una comisión por lo que pagan los
clientes que llegaron por él. El staff aprueba a los afiliados, fija las comisiones por producto y paga los retiros.
Es diseño propio, armado desde cómo funciona un programa de afiliados en un proveedor de hosting y desde la
estructura de los datos que hay que importar.

---

## 1. Resumen

- El programa viene apagado. Se prende en el admin, con aprobación previa o sin ella. Apagado no cuenta clics ni
  genera comisiones nuevas, y lo ya ganado se sigue liberando y cobrando.
- El enlace `/r/{código}` cuenta el clic, deja una cookie con la ventana del programa y lleva a la página de
  destino que fija el operador.
- La cookie se vuelve **atribución de la cuenta** cuando el cliente entra al portal, cuando el pedido de una página
  crea la cuenta o cuando una integración la informa por la API.
- Un pedido hecho dentro de la ventana deja sus servicios como **referidos** del afiliado.
- Cada factura **paga por completo** de un servicio referido genera una comisión, que queda **en espera** el plazo
  del programa y después se **libera** al saldo del afiliado.
- Una nota de crédito, un reembolso o un contracargo la **revierten**: entera si la factura deja de estar paga y en
  proporción si una nota parcial la deja paga. Si ya estaba liberada, el saldo se descuenta.
- El afiliado cobra su saldo **como saldo a favor** de su cuenta, al instante, o pide un **retiro** que el staff
  marca pagado o rechaza. Cada retiro pedido avisa por correo a la casilla del equipo que fija el programa.
- Opcionales del programa: un **bono de alta** por moneda que se acredita la primera vez que el afiliado queda
  activo, el **material para compartir** que el afiliado copia con su enlace ya puesto y el **resumen mensual** que
  recibe cada afiliado activo el día 1.
- Todo vive en el esquema `affiliates`, con la plata en `decimal`, moneda explícita y redondeo comercial.

---

## 2. Conceptos

| Concepto | Qué es | Invariantes |
|---|---|---|
| Programa (`AffiliateProgram`) | Una sola fila con los ajustes: prendido, aprobación previa, días de la ventana de la cookie, días de espera, retiro mínimo y bono de alta por moneda, casilla que recibe los retiros pedidos, material para compartir y resumen mensual | Ventana de 1 a 365 días y espera de 0 a 365. Importes por moneda con centavos y sin negativos. El material tiene hasta 4.000 caracteres |
| Regla de comisión (`CommissionRule`) | Porcentaje o monto fijo, una vez o recurrente. Sin producto ni afiliado es la regla por defecto. Con producto vale para ese producto y con afiliado para ese afiliado. Un producto puede quedar sin comisión | Una regla por combinación de producto, afiliado y moneda. El monto fijo lleva moneda y el porcentaje no |
| Afiliado (`Affiliate`) | Una cuenta que se sumó al programa, con su código público y su estado | Una cuenta tiene a lo sumo un afiliado. El código es único sin distinguir mayúsculas |
| Clic (`AffiliateClick`) | Una visita por el enlace, con la fecha y el host del sitio de origen | Sin IP ni datos de la persona |
| Atribución (`AffiliateAttribution`) | Qué afiliado trajo a una cuenta y desde cuándo corre la ventana | Una por cuenta. Nunca la propia cuenta del afiliado |
| Referido (`AffiliateReferral`) | Un servicio que llegó por un afiliado, con el pedido que lo creó | Un servicio tiene a lo sumo un referido |
| Comisión (`AffiliateCommission`) | Lo ganado por una factura y un servicio referido: base, regla aplicada, importe, lo revertido, estado y fecha de liberación | Una sola comisión vigente por factura y servicio. Lo revertido nunca pasa el importe |
| Movimiento (`AffiliateLedgerEntry`) | Libro del saldo del afiliado por moneda, solo de altas | El saldo es la suma de los movimientos. Se compensa, nunca se edita |
| Pago (`AffiliatePayout`) | Saldo a favor o retiro, con su estado | El importe sale del saldo al pedirlo. Un retiro rechazado lo devuelve |

---

## 3. Cómo se gana una comisión

1. **Clic.** `GET /r/{código}` busca un afiliado activo con el programa prendido. Si lo hay, guarda el clic y deja
   la cookie `bo_ref`, protegida con Data Protection, con el afiliado y la hora del clic, `HttpOnly`, `Secure` y
   `SameSite=Lax`, que vence con la ventana. Un navegador que ya trae la cookie del mismo afiliado no suma otro
   clic: se renueva la ventana. El enlace de otro afiliado suma su clic y reemplaza la cookie: en el navegador manda
   el último enlace, y en la cuenta, la primera atribución dentro de la ventana (paso 2). Haya o no afiliado, redirige a `Affiliates:LandingUrl` (el inicio por defecto) o, con
   `?ir=/ruta`, a esa ruta del propio sitio. Así un código inventado no se distingue de uno real.
2. **Atribución.** La cookie se lee:
   - en cada pedido del portal con sesión del cliente (no en "Ver como cliente"), para las cuentas de las que la
     persona es titular. Después se borra la cookie.
   - en el pedido de una página, que crea la cuenta y confirma en el mismo paso: llama a
     `AffiliateCookie.Attribute` con la cuenta nueva antes de confirmar el pedido.
   - por la API, `POST /api/v1/affiliates/attributions`, para una tienda que no corre en el portal.

   Una cuenta sin atribución, con una vencida o con una del mismo afiliado toma la nueva. Una con la ventana
   vigente de otro afiliado la conserva: gana el primer clic dentro de la ventana. La cuenta del propio afiliado
   nunca se atribuye.
3. **Referidos.** Cuando se confirma un pedido (`OrderPlaced`), cada servicio del pedido queda referido si la cuenta
   tiene atribución, el afiliado sigue activo y el pedido se hizo entre la hora del clic y el fin de la ventana. Se
   vuelve a mirar al pagarse la factura, por si el pago entró antes que el aviso del pedido. El staff puede asignar
   a mano un servicio a un afiliado.
4. **Comisión.** Cuando una factura queda paga (`InvoicePaid`), por cada servicio referido que tenga renglones en
   ella, con el afiliado activo:
   - la base es el neto sin impuestos de los renglones del servicio, sin los recargos por mora ni los ajustes de
     redondeo de los importados. Un descuento del servicio, como la promoción importada, baja la base. Sin base
     positiva no hay comisión, tampoco de monto fijo.
   - la regla sale de la sección 4. Sin regla o con "sin comisión", no hay comisión.
   - una regla de una sola vez paga solo si el referido no tiene otra comisión vigente ni cobró antes de importarse.
   - el porcentaje se aplica a la base y se redondea a dos decimales con la mitad hacia arriba. El monto fijo se paga
     una vez por factura y servicio.
   - si una nota parcial ya acreditó parte de la factura, antes del pago o completándolo, la comisión nace con lo
     revertido del paso 6. Así da lo mismo que la nota llegue antes o después del pago.
   - la comisión queda en espera hasta la fecha de hoy más los días de espera del programa.
5. **Liberación.** Un trabajo diario revisa cada comisión vencida contra su factura y libera lo que queda: suma al
   saldo con un movimiento.
6. **Reversa.** Una nota de crédito (`CreditNoteIssued`) o una imputación revertida por reembolso o contracargo de la
   pasarela (`AllocationReversed`) revisa las comisiones de esa factura. Si la factura ya no está paga por completo,
   lo que se debe es cero. Si sigue paga con notas parciales, se debe la proporción todavía no acreditada:
   `importe por (total menos acreditado) dividido total`. La diferencia se revierte. Si la comisión ya estaba
   liberada, sale del saldo con un movimiento de reversa, aunque el saldo quede negativo: se compensa con las
   comisiones siguientes. Lo revertido no vuelve: si la factura se paga de nuevo, nace otra comisión.
7. **Cobro.** Con el afiliado activo y saldo suficiente:
   - **Saldo a favor:** el importe pasa en el momento al saldo a favor de la cuenta del afiliado, en la misma moneda,
     con un movimiento del libro de saldo (sección 6.3 del [modelo](dominio.md)). El pago nace pagado.
   - **Retiro:** pide un importe igual o mayor al mínimo de la moneda, con los datos para el pago. Sale del saldo al
     pedirlo. Si el programa tiene una casilla para los retiros, en la misma transacción sale el aviso
     `affiliate-payout-requested` con el número, la cuenta y el importe, sin los datos de la transferencia, que se ven
     en el admin. El staff lo marca pagado con su referencia o lo rechaza con el motivo, y entonces el importe vuelve.

Las comisiones solo nacen de facturas pagas por completo: un pago parcial no genera nada y la factura que vuelve a
deber por un contracargo pierde su comisión hasta que se pague otra vez.

**Bono de alta.** Si el programa tiene un bono en la moneda de la cuenta del afiliado, se acredita al saldo
disponible con un movimiento `Bonus` la primera vez que el afiliado queda activo: al sumarse sin aprobación previa o
al aprobarlo, aunque antes lo hayan rechazado. Reactivar a un suspendido no da otro, y un índice único por afiliado lo
garantiza en la base. Los afiliados importados ya estaban activos y no lo reciben. Conviene usarlo con aprobación
previa, porque se puede pasar a saldo a favor en el momento.

**Material para compartir.** Textos o código para la web que escribe el operador, con `{enlace}` donde va el enlace
del afiliado y una línea con `---` entre pieza y pieza. El portal muestra cada pieza con el enlace puesto, como texto
para copiar: nunca interpreta el HTML.

**Resumen mensual.** Con el resumen prendido, el planificador encola `affiliates.monthly` el día 1 y cada afiliado
activo recibe `affiliate-monthly` con lo del mes anterior en la zona del proveedor: clics, servicios referidos
nuevos, comisiones del mes por moneda y su saldo de hoy. La clave del aviso es por afiliado y mes, así que repetir el
trabajo no manda otro.

---

## 4. Qué regla se aplica

Para un afiliado, el producto del servicio y la moneda de la factura, se busca en este orden y gana el primer nivel
que tenga una regla usable:

1. La del afiliado para ese producto.
2. La del producto marcada "sin comisión": el producto no paga, aunque el afiliado tenga una regla propia.
3. La del afiliado, para cualquier producto.
4. La del producto.
5. La del programa.

Dentro de un nivel, una regla de la moneda de la factura gana a una sin moneda. Un monto fijo en otra moneda no es
usable y se sigue con el nivel siguiente, porque nunca se convierte una moneda en otra (D-04).

---

## 5. Estados

**Afiliado**

| Desde | Hacia | Quién |
|---|---|---|
| (alta) | Pendiente | El cliente, con aprobación previa |
| (alta) | Activo | El cliente, sin aprobación previa |
| Pendiente | Activo | El staff aprueba |
| Pendiente | Rechazado | El staff rechaza |
| Rechazado | Activo | El staff aprueba después |
| Activo | Suspendido | El staff |
| Suspendido | Activo | El staff |

Solo un afiliado activo suma clics, atribuciones, referidos y comisiones nuevas, y cobra. Las comisiones en espera de
un suspendido se siguen liberando: ya se ganaron.

**Comisión**

| Desde | Hacia | Cuándo |
|---|---|---|
| (alta) | En espera | La factura quedó paga |
| En espera | Liberada | Llegó la fecha y algo sigue debiéndose |
| En espera | Revertida | La factura dejó de estar paga, o llegó la fecha sin nada que liberar |
| Liberada | Revertida | La factura dejó de estar paga después de liberarse |

Una reversa parcial deja la comisión en su estado, con lo revertido sumado.

**Pago**

| Método | Desde | Hacia | Quién |
|---|---|---|---|
| Saldo a favor | (alta) | Pagado | El afiliado o el staff |
| Retiro | (alta) | Pedido | El afiliado |
| Retiro | Pedido | Pagado | El staff, con la referencia de la transferencia |
| Retiro | Pedido | Rechazado | El staff, con el motivo |

---

## 6. Invariantes en la base

- `affiliates.affiliates`: `account_id` único y `code` único, que se guarda siempre en minúsculas.
- `affiliates.commission_rules`: único por `(product_id, affiliate_id, currency)` sin distinguir nulos. Un monto fijo
  tiene moneda y un porcentaje no. El porcentaje va de 0 a 100.
- `affiliates.referrals`: `service_id` único.
- `affiliates.commissions`: único por `(document_id, service_id)` entre las que no están revertidas, importe
  positivo y `0 <= reversed <= amount`.
- `affiliates.ledger_entries` y `affiliates.clicks` solo agregan: un trigger rechaza cambios y borrados.
- `affiliates.payouts`: importe positivo.
- Importes de comisiones, movimientos y pagos en `numeric(18,2)`. Porcentajes y montos de reglas en `numeric(18,4)`.

---

## 7. Trabajos, eventos y locks

| Trabajo | Lo dispara | Qué hace |
|---|---|---|
| `affiliates.order` | `OrderPlaced` | Deja referidos los servicios del pedido si corresponde |
| `affiliates.paid` | `InvoicePaid` | Referidos que falten y comisiones de la factura |
| `affiliates.review` | `CreditNoteIssued` y `AllocationReversed` | Revisa las comisiones de la factura |
| `affiliates.release` | El planificador, una vez por día | Revisa y libera las comisiones vencidas, factura por factura |
| `affiliates.monthly` | El planificador, el día 1 de cada mes | Deja en la cola el resumen del mes anterior de cada afiliado activo, si el programa lo pide |

`AllocationReversed` es un evento nuevo del núcleo: lo publica `PaymentService` cada vez que un reembolso o un
contracargo saca una imputación de una factura.

- Todo lo que toca las comisiones de una factura toma primero el lock de la factura (sección 13 del modelo) y
  después las filas de comisiones. Así el pago, la nota y la liberación de una misma factura van de a uno, en
  cualquier nodo.
- Los cobros, y la devolución de un retiro rechazado, toman antes el lock de la fila del afiliado: dos cobros a la
  vez no pasan el saldo. El saldo a favor toma después el lock del saldo de la cuenta, como cualquier movimiento de
  saldo. La liberación y la reversa escriben su movimiento con el lock de la factura y no necesitan el del afiliado,
  porque una reversa puede dejar el saldo negativo (paso 6 de la sección 3).
- Repetir un trabajo no duplica: la comisión vigente es única por factura y servicio, el referido por servicio, y la
  liberación cambia el estado y escribe el movimiento en la misma transacción.
- Nada queda en memoria entre pedidos. La cookie es la única pieza fuera de la base y se valida con el anillo de
  llaves compartido de Data Protection.

---

## 8. Permisos

| Quién | Permiso | Qué permite |
|---|---|---|
| Staff | `affiliates.read` | Ver afiliados, referidos, comisiones, retiros y el programa |
| Staff | `affiliates.manage` | Aprobar, rechazar, suspender, reactivar, reglas, programa, asignar referidos y marcar retiros |
| API | `affiliates:read` y `affiliates:write` | Lo mismo por la API propia |
| Portal | Capacidad `affiliate` de la cuenta, implícita para el titular | Sumarse, ver el panel, pasar a saldo a favor y pedir retiros |

Cada acción del staff y de la API queda en `platform.audit_events`. El afiliado nunca ve el nombre ni los datos de sus
referidos: solo el producto, la fecha y el estado.

---

## 9. API propia

| Método y ruta | Permiso | Para qué |
|---|---|---|
| `GET /api/v1/affiliates?status=` | `affiliates:read` | Afiliados con su saldo por moneda (disponible, en espera y cobrado), clics y referidos |
| `GET /api/v1/affiliates/{id}` | `affiliates:read` | Uno, con sus comisiones y pagos |
| `POST /api/v1/affiliates/{id}/{approve, reject, suspend, reactivate}` | `affiliates:write` | Cambiar el estado |
| `POST /api/v1/affiliates/attributions` | `affiliates:write` | Atribuir una cuenta a un código, `{account_id, code}` |
| `GET /api/v1/affiliates/payouts?status=` | `affiliates:read` | Pagos, por estado |
| `POST /api/v1/affiliates/payouts/{id}/paid` y `.../reject` | `affiliates:write` | Marcar un retiro pagado, `{reference}`, o rechazarlo, `{reason}` |

---

## 10. Pantallas

- **Portal, `/afiliados`:** sin afiliado, las condiciones del programa, con el bono si hay, y el botón para sumarse.
  Pendiente o rechazado, el estado. Activo, el enlace para copiar, el material para compartir, clics de los últimos
  30 días y totales, referidos, comisiones con su estado y fecha de liberación, saldo disponible, en espera y cobrado
  por moneda, los pagos y los dos formularios de cobro. El ítem Afiliados del menú del portal aparece con el programa
  prendido o si la cuenta ya es afiliada.
- **Admin, `/admin/afiliados`, en Clientes:** la lista con los pendientes primero y, por afiliado, saldo, cobrado,
  clics y altas. La ficha de cada afiliado con sus acciones (estado, reglas propias, referidos a mano y pasar saldo a
  saldo a favor). La cola de retiros pedidos. Y el programa, que es la solapa Afiliados de los ajustes generales:
  prendido, aprobación previa, ventana, espera, retiro mínimo, bono de alta, casilla de los retiros, material, resumen
  mensual y las reglas por producto.

---


## 12. Para después

- Avisos al afiliado por correo: alta aprobada, comisión liberada y retiro pagado.
- El retiro como pedido de soporte en un departamento, cuando exista el soporte.
- El informe de afiliados de Reportes, con período, totales por moneda y CSV.
- Tope de meses para las comisiones recurrentes.
- Retiros por la pasarela, sin que el staff transfiera a mano.
- Comisiones de dominios, que no son servicios.
- Con el contador: cómo se trata ante el fisco la comisión pagada como saldo a favor y si el retiro de un afiliado
  necesita comprobante del afiliado.
