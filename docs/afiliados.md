# Programa de afiliados

Guía de operación del programa de afiliados. El diseño, con el modelo, los estados, las reglas y los trabajos, está en
[diseno/afiliados.md](../diseno/afiliados.md).

## Prenderlo

El programa viene apagado. Se prende en el admin, en **Afiliados, Programa**. Ahí se fijan:

- si cada alta espera la aprobación del staff,
- los días que vale un clic para atribuir un pedido (60 por defecto),
- los días de espera de cada comisión antes de pasar al saldo (30 por defecto, pensados para cubrir reembolsos y
  contracargos),
- el retiro mínimo por moneda, por ejemplo `ARS 5000, USD 5` (también `ARS 1000,50, USD 5`),
- el bono de alta por moneda, que se acredita al saldo la primera vez que el afiliado queda activo, en la moneda de su
  cuenta. Conviene con la aprobación previa, porque el afiliado lo puede pasar a saldo a favor en el momento,
- la casilla del equipo que recibe un aviso por cada retiro pedido, por ejemplo la de facturación,
- el material para compartir: textos o código para la web con `{enlace}` donde va el enlace de cada afiliado y una
  línea con `---` entre pieza y pieza. El afiliado lo ve en su panel listo para copiar, como texto,
- si cada afiliado activo recibe el día 1 el resumen del mes anterior,
- la regla del programa y las reglas por producto: porcentaje, montos fijos por moneda o sin comisión, una sola vez o
  en cada factura.

Sin la regla del programa, solo pagan los productos y afiliados con regla propia. Apagado, el programa no cuenta clics
ni genera comisiones nuevas, y lo ya ganado se sigue liberando y cobrando. El ítem Afiliados del portal aparece con el
programa prendido o para las cuentas que ya son afiliadas.

Los dos avisos del programa salen por la cola de correo, con su traba de producción: `affiliate-payout-requested` al
equipo y `affiliate-monthly` a cada afiliado. Se editan como el resto de las plantillas.

## Permisos del staff

| Permiso | Qué permite |
|---|---|
| `affiliates.read` | Ver afiliados, referidos, comisiones, retiros y el programa |
| `affiliates.manage` | Aprobar, rechazar, suspender y reactivar, cambiar reglas y programa, asignar referidos y resolver retiros |

Se dan al crear el usuario, por ejemplo `staff add persona@example.com "Nombre" accounts.read,affiliates.read,affiliates.manage`.
La API propia usa `affiliates:read` y `affiliates:write` (sección 9 del diseño).

## Configuración

| Clave | Para qué |
|---|---|
| `Affiliates:LandingUrl` | Adónde lleva el enlace `/r/{código}` después de contar el clic, por ejemplo la portada del sitio comercial. Por defecto, el inicio del portal. La fija el operador y no se edita en el panel, así el enlace no se puede usar para mandar a cualquier sitio |

El afiliado puede llevar a una página del propio sitio con `?ir=/ruta`. El enlace tiene un tope de 60 pedidos por
minuto por IP en cada nodo.

## Cómo llega un referido

1. Alguien abre el enlace y recibe la cookie `bo_ref`, que dura los días de la ventana.
2. Cuando entra al portal con su sesión, sus cuentas de titular quedan atribuidas al afiliado. Si crea la cuenta y
   confirma el pedido en el mismo paso, la página del pedido llama a `AffiliateCookie.Attribute` con la cuenta nueva
   antes de confirmar. Una tienda propia que no corre en el portal informa la cuenta y el código con
   `POST /api/v1/affiliates/attributions`.
3. Los pedidos que la cuenta confirma dentro de la ventana dejan sus servicios como referidos.
4. Cada factura paga por completo de un servicio referido genera la comisión, que queda en espera.

El staff puede asignar un servicio a un afiliado a mano desde la ficha del afiliado.

## Liberación, reversas y cobros

- Una vez por día se liberan las comisiones vencidas, después de revisar su factura. Lo liberado pasa al saldo.
- Una nota de crédito, un reembolso o un contracargo revierten la comisión de esa factura, aunque ya esté liberada. Si
  el saldo queda negativo, se compensa con las comisiones siguientes.
- El afiliado pasa saldo a su saldo a favor en el momento, sin mínimo, o pide un retiro con los datos para la
  transferencia. El staff también puede pasarlo a saldo a favor desde la ficha del afiliado. Los retiros pedidos
  aparecen en **Afiliados, Cobros** y, si el programa tiene la casilla, llega un aviso con el número y el importe,
  sin los datos de la transferencia: el staff transfiere y los marca pagados con la referencia, o los rechaza con el
  motivo y el importe vuelve al saldo. La lista de afiliados muestra por cada uno el saldo, lo cobrado, los clics y
  las altas.
- La comisión sale del neto sin impuestos de los renglones del servicio en la factura, sin la mora. Un descuento del
  servicio baja la base, y una nota parcial que llegó antes del pago o lo completó la deja en proporción.

## Importación

El importador trae el programa de la copia: los ajustes, las reglas por producto y de cada afiliado, los afiliados
con su ID como código, los servicios referidos, las comisiones pagadas y en espera, los retiros y los clics. Al final
controla que el saldo de cada afiliado dé el del origen y la conciliación compara las filas de cada tabla.

Los enlaces que ya repartieron los afiliados apuntan a la instalación anterior con el ID del afiliado. Para que sigan
sumando, el proxy de esa dirección redirige a `/r/{ID}`: el código de un afiliado importado es su ID.

El importador trae el programa con sus ajustes: porcentaje, recurrencia, días de espera, retiro mínimo, bono y
resumen mensual. Si el origen mandaba los retiros a un departamento de soporte, sale como incidencia para cargar
en el programa la casilla que recibe el aviso.

## Pendiente con el contador

- Cómo se trata ante el fisco la comisión que el afiliado pasa a saldo a favor.
- Si el retiro de un afiliado necesita un comprobante del afiliado antes de pagarlo.
