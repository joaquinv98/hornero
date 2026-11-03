# Catálogo, opciones, cupones y cambios de plan

**Estado:** v0, 2026-10-29 · **Parte de:** [diseño del núcleo](README.md) · **Modelo:** sección 3 del
[modelo de dominio](dominio.md)

Qué se vende y cómo se cobra: grupos de productos, productos con su tabla de precios, opciones configurables con
precio por moneda y ciclo, complementos, stock, pruebas gratis, paquetes, cupones y cambios de plan o de opciones
con prorrateo. Lo usan el pedido (`PlaceOrder`), la renovación, la API propia, la API compatible, el panel del staff y
el importador. Es diseño propio: los nombres y las reglas no siguen al sistema anterior, del que solo se leyó la
estructura de los datos para importarlos ([importacion.md](importacion.md)).

---

## 1. Conceptos

| Concepto | Qué es | Reglas |
|---|---|---|
| Grupo de productos (`ProductGroup`) | Ordena la tienda y el catálogo, con nombre, bajada, orden y si se muestra | Conserva su ID al importar |
| Producto (`Product`) | Lo que se vende: aprovisionado por un módulo, manual, dominio o venta única. Grupo, orden, descripción sin HTML, visibilidad, categoría de impuesto, stock, días de prueba, días de mora hasta la baja, cuándo se aprovisiona, instancia de módulo y sus parámetros | Un producto con servicios no se borra: se oculta |
| Plan (`Plan`) | Un ciclo en que se vende el producto: mensual, cada 3 o 6 meses, anual, cada 2 o 3 años, pago único o gratis | Un ciclo que se saca de la tabla de precios deja de venderse y el plan queda inactivo |
| Precio (`PlanPrice`) | Importe y cargo de alta de un plan en una moneda, con fecha de vigencia y su modo de carga (con impuesto incluido o sin él) | Un precio distinto del vigente abre una vigencia nueva desde hoy, o corrige la de hoy. Una moneda que se saca de un ciclo cierra su vigencia hoy y el ciclo deja de venderse en ella. Lo contratado no cambia |
| Grupo de opciones (`OptionGroup`) | Un conjunto de opciones que comparten varios productos, por ejemplo "VPS" o "Minecraft" | Un producto ofrece las opciones de todos sus grupos |
| Opción (`Option`) | Algo que el cliente elige: lista, botones de opción, sí o no, o cantidad con mínimo y máximo. Tiene orden, puede estar oculta y tiene una clave de módulo | Una opción oculta no se le ofrece al cliente y toma su valor por defecto |
| Valor (`OptionValue`) | Un valor de una opción, con nombre, valor para el módulo, orden y si está oculto | Una opción de sí o no o de cantidad tiene un solo valor, que lleva el precio |
| Precio de un valor (`OptionPrice`) | Importe por período y cargo de alta del valor en una moneda y un ciclo | Un valor con precios no se ofrece donde no tiene. Un valor sin ningún precio no cobra |
| Opción elegida (`ServiceOption`) | Lo que el servicio tiene de cada opción: el valor y la cantidad | Una por opción y servicio |
| Paquete (`Bundle`) | Planes que se venden juntos, con sus unidades y un descuento porcentual por línea durante ciertos períodos o para siempre | El pedido trae exactamente sus planes. Sus líneas no toman cupón |
| Cupón (`Coupon`) | Descuento porcentual o fijo por unidad, para ciertos productos y ciclos, con vigencia, usos máximos y reglas de cuenta | Los usos nunca pasan el tope, aunque lleguen dos pedidos a la vez |
| Cambio (`ServiceChange`) | Pedido de cambiar el producto, el ciclo o las opciones de un servicio, con su prorrateo y el documento que lo cobra o lo acredita | Un servicio tiene a lo sumo uno esperando el pago |

## 2. Opciones configurables

**Qué se cobra.** El precio unitario de un servicio es el del plan más el de cada opción elegida, en la moneda de la
cuenta y el ciclo del plan. Las opciones siguen el modo de carga del precio del plan, y el servicio congela los dos
(diseño fiscal, sección de impuestos). Un pedido que mezcla modos sale en una factura por modo. El impuesto de cada
renglón sale de la categoría del producto y de las reglas de la sección Impuestos. Una lista o botones cobran el precio del valor elegido. Una cantidad cobra el precio de
su único valor por cada unidad. Un sí o no cobra el precio de su valor si se elige. El cargo de alta es el del plan
más el de cada opción, una vez por línea del pedido. En el ciclo gratis las opciones no cobran.

**Lo que no se pide.** Toma su valor por defecto: el primer valor visible de una lista, el mínimo de una cantidad y
"no" en un sí o no. Un valor oculto no es el defecto, pero se acepta si lo pide el staff o la API. El cliente desde el
portal no elige una opción oculta ni un valor oculto, aunque arme el formulario a mano: solo conserva el valor oculto
que su servicio ya tiene. Una opción o un valor que no es
del producto, una cantidad fuera de rango o un valor sin precio en ese ciclo y esa moneda se rechazan con un texto
que dice qué falló.

**Parámetros del módulo.** Cada opción con clave de módulo pone lo elegido en esa clave de los parámetros del
servicio, encima de los del producto, con puntos para anidar (`variables.SERVER_SLOTS`). Una lista manda el valor
para el módulo (un entero va como número, "1.20" queda como texto) o, si no tiene, el nombre. Una cantidad manda el
número y un sí o no, verdadero o falso. Una opción sin clave solo cobra. Los parámetros se recalculan al contratar y
en cada cambio de opciones, y el cambio encola `change_plan` para el módulo.

**Factura.** La línea de renovación dice el producto con lo elegido, por ejemplo "VPS · RAM: 4 GB, IP extra: 2".
Las opciones ocultas y las cantidades en cero no se nombran.

## 3. Complementos, stock, prueba gratis y paquetes

- **Complemento.** Un producto marcado como complemento se contrata colgado de un servicio vivo de la misma cuenta,
  o de otra línea del mismo pedido, como servicio hijo. Puede limitarse a ciertos productos padre. Tiene su propio
  ciclo y sus propias operaciones de módulo, y se da de baja con el padre.
- **Stock.** Sin valor no hay control. Con valor, cada pedido descuenta sus unidades con un `UPDATE` condicionado y
  una restricción de la base impide que quede negativo: dos pedidos a la vez nunca pasan el último. Cancelar el
  pedido lo devuelve.
- **Prueba gratis.** Con días de prueba en un producto recurrente, el primer pedido de una cuenta para ese producto
  no factura ni cobra el cargo de alta, se aprovisiona enseguida y el primer período facturado arranca cuando
  termina la prueba. Una por cuenta y producto, con un índice único en la base.
- **Paquete.** Lista planes con sus unidades (un plan repetido lleva dos) y un descuento de 0 a 100 % por línea, por
  uno o más períodos contando el primero o para siempre. El pedido dice qué paquete pide y marca sus líneas, cada una
  con sus opciones, su etiqueta y, si es un complemento, la línea de la que cuelga, como cualquier otra. Las líneas
  marcadas tienen que ser exactamente los planes del paquete con sus unidades, ni más ni menos. Llevan el descuento
  del paquete sobre el precio con opciones, como un cupón que guarda el servicio, y no toman cupón: el paquete ya es
  su promoción. El cargo de alta no se descuenta. El pedido guarda el paquete, y uno apagado no se puede pedir. El
  sistema anterior no tiene paquetes, así que no hay nada que importar.

**Cuándo se aprovisiona.** Cada producto dice cuándo se crea el servicio de un pedido, en su módulo o en el
núcleo si no tiene. Al pagar es lo de siempre: espera que se pague la factura del pedido, salvo que no tenga nada
para pagar ahora (una prueba gratis o un plan gratis). Al pedir se aprovisiona apenas se confirma el pedido y la
factura queda igual para cobrar. Cuando lo acepta el staff ni el pago lo activa: el pedido queda pendiente, aunque
no tenga nada para pagar, hasta que el staff lo acepta. El resto de las líneas del mismo pedido sigue su propio modo.

## 4. Cupones

| Campo | Qué hace |
|---|---|
| Código | Lo que escribe el cliente. Único sin distinguir mayúsculas |
| Tipo y valor | Porcentaje de 0 a 100, o monto fijo por unidad. El fijo puede tener moneda. Sin moneda vale el mismo número en cualquiera |
| Recurrente y períodos | Si sigue en las renovaciones y cuántos períodos descuenta contando el primero. Sin períodos es para siempre. Uno no recurrente descuenta solo la primera factura |
| Productos y ciclos | Dónde vale. Vacío es en todos |
| Desde y hasta | Vigencia. Hasta es el último día en que vale |
| Usos máximos | Pedidos que lo pueden usar. El uso se suma con un `UPDATE` condicionado y una restricción lo respalda, y cancelar el pedido lo devuelve |
| Una vez por cuenta, una vez por pedido | Por cuenta mira los pedidos no cancelados que lo usaron. Por pedido descuenta solo la primera línea donde vale |
| Cuentas nuevas o existentes | Solo para cuentas sin pedidos, o solo para las que ya compraron |
| Activo | Apagado no se puede usar, aunque esté vigente |

El servicio guarda el descuento (porcentaje o monto por unidad) y los períodos que le quedan. El contador baja con
cada período que se factura y, al llegar a cero, el descuento se termina para lo que se facture después. El período
que lo consumió lo conserva hasta su fin, así una baja de plan en ese período acredita lo que se cobró con descuento y
no el precio lleno. Un período que el descuento deja en cero no se factura, ni en el pedido ni en la renovación: corre
igual, consume su ciclo y queda pago, así la mora no lo toma por impago y el siguiente se factura como siempre. El
pedido guarda el cupón que usó.

## 5. Cambios de plan y de opciones

Se pide un producto, un ciclo o unas opciones nuevas. Lo que no viene queda como está, y las opciones pedidas pisan
solo las suyas. Primero se puede calcular sin hacer nada (`preview`).

**Qué se puede cambiar.**
- Solo un servicio activo, y de a un cambio pendiente por servicio.
- El producto nuevo tiene que ser de la misma instancia de módulo y del mismo tipo (complemento o no): otro recurso
  es una baja y un alta.
- El cliente solo puede pasar a los productos que el suyo permite, y cambiar opciones si el producto lo deja. El
  staff puede las dos cosas siempre.
- El ciclo se cambia solo, sin tocar el producto ni las opciones, y vale desde la próxima renovación, sin prorrateo.
  Un servicio recurrente no pasa a pago único ni a gratis.

**Precio nuevo.** Otro producto u otro ciclo toman el precio de lista con sus opciones. En el mismo producto y el
mismo ciclo el precio nuevo es el que el servicio ya paga más la diferencia de las opciones a los precios de hoy: un
precio viejo o importado, con su promoción adentro, no se pierde por agregar una IP. Un cambio cobra solo la
diferencia de precio: el cargo de alta de un valor nuevo de una opción no se cobra (decidido el 2/11), y
tampoco el del producto nuevo.

**Descuento.** El descuento del servicio (el de su cupón o el de su paquete) sigue en el mismo producto y el mismo
ciclo, también con opciones nuevas. Otro producto u otro ciclo lo conservan solo si su cupón vale ahí: el de un paquete
es de sus planes y no pasa a otros. Si no vale, lo que queda del período va del precio que se pagó con descuento al
precio lleno del plan nuevo, el cambio termina el descuento al aplicarse y la cotización lo avisa. Uno terminado no
vuelve aunque el servicio vuelva después a un producto del cupón.

**Prorrateo.** La diferencia de precio por unidad, con el descuento que tuvo cada período y por la cantidad del
servicio, se prorratea por días desde hoy (o desde el fin de la prueba gratis) hasta el próximo período a facturar,
período por período.
- **Subir:** se emite una factura con una línea de cambio de plan y la clave `change:{cambio}`. Como toda factura, el
  saldo a favor la paga si alcanza. El cambio se aplica cuando la factura queda paga, en la misma transacción que la
  imputación. Si mientras tanto se facturó otro período con el precio viejo, un trabajo aparte factura esa diferencia
  con la clave `change:{cambio}:gap`, desde lo que cobró el renglón de ese período.
- **Bajar:** se emite una nota de crédito contra cada factura que cobró los períodos afectados, nunca por más de lo
  que le queda por acreditar, y el cambio se aplica enseguida. Lo ya pagado queda como saldo a favor (sección 6.3
  del modelo).
- **Sin diferencia** o sin prorrateo pedido: se aplica enseguida.

**Cancelar.** Un cambio que espera el pago se cancela anulando su factura impaga con una nota de crédito. Si la
factura ya tiene algo pagado, lo resuelve el staff con una nota. Una factura de cambio que se anula por cualquier
camino (por ejemplo la anulación de impagas de la mora) cancela el cambio.

## 6. API propia

Con los permisos `catalog:read` y `catalog:write` de la API key (D-11):

| Ruta | Qué hace |
|---|---|
| `GET, POST /api/v1/product-groups` y `PUT /api/v1/product-groups/{id}` | Grupos de productos |
| `GET, POST /api/v1/products`, `GET, PUT /api/v1/products/{id}` | Productos con su categoría de impuesto (`tax_category`, un código de la sección Impuestos), cuándo se aprovisionan (`activation`: `on_payment`, `on_order` o `manual`) y su tabla de precios (`prices`: ciclo, moneda, importe y cargo de alta) y sus grupos de opciones. La lectura trae los precios vigentes y las opciones con sus valores y precios |
| `GET, POST /api/v1/option-groups`, `GET, PUT /api/v1/option-groups/{id}` | Grupos de opciones con sus opciones, valores, precios y productos |
| `POST /api/v1/products/{id}/copy` y `POST /api/v1/option-groups/{id}/copy` | Duplican un producto (oculto, con sus precios de hoy, sus grupos de opciones y su módulo) o un grupo de opciones (con sus opciones, valores y precios, sin productos). El nombre (`name`) es opcional: sin él lleva el del original con "(copia)" |
| `GET, POST /api/v1/coupons` y `PUT /api/v1/coupons/{id}` | Cupones |
| `GET, POST /api/v1/bundles`, `GET, PUT /api/v1/bundles/{id}` | Paquetes con sus planes (`items`: `plan_id` y `quantity`), el descuento y sus períodos. La lectura dice el producto y el ciclo de cada plan y el primer período en cada moneda donde se venden todos, con lo menos que cobran sus opciones, con el descuento y sin él, y los cargos de alta de cada unidad |

Con `services:read` y `services:write`:

| Ruta | Qué hace |
|---|---|
| `GET /api/v1/services/{id}` | El servicio con lo elegido de cada opción, su descuento y su cambio pendiente |
| `POST /api/v1/services/{id}/changes/preview` | Calcula un cambio sin hacerlo: precio nuevo, prorrateo, desde y hasta cuándo y si el descuento del servicio se termina (`discount_ends`) |
| `POST /api/v1/services/{id}/changes` | Pide el cambio. Devuelve la factura o las notas de crédito que emitió |
| `GET /api/v1/service-changes/{id}` y `POST /api/v1/service-changes/{id}/cancel` | Lee o cancela un cambio |

Los precios de un producto dicen su `plan_id`, que es lo que se pide. El pedido (`POST /api/v1/orders`) recibe por
línea las opciones elegidas (`options`: opción, valor o cantidad), el servicio padre o la línea padre de un
complemento y si la línea es del paquete (`bundled`), y para todo el pedido el código de cupón (`coupon_code`) y el
paquete (`bundle_id`). Una línea lleva una unidad salvo que el producto se pida por cantidad: cada unidad de un VPS
es su propio servicio y va en su propia línea.

`POST /api/v1/orders/quote`, con el permiso `orders:write`, recibe el mismo pedido y lo calcula con las mismas reglas
sin tocar nada: ni stock, ni usos del cupón, ni servicios, ni factura. Devuelve cada línea con su precio por unidad y
período, con y sin el descuento del cupón o del paquete, el cargo de alta y el fin de la prueba gratis, y la factura
que saldría hoy con su neto, su impuesto y su total. Rechaza lo mismo que el pedido, con el mismo texto. Total cero es
un pedido que se acepta sin factura.

## 7. API compatible

- `GetProducts` devuelve el catálogo con el precio y el cargo de alta de cada ciclo por moneda, con -1.00 en un ciclo
  que no se vende, y las opciones configurables con sus valores y precios. Acepta `pid` y `gid`. Sale de las
  integraciones que lo leen ([api-compatible.md](api-compatible.md)).
- `AddOrder` recibe `configoptions` como mapa de ID de opción a lo elegido: el ID del valor en una lista y el número
  en una cantidad o un sí o no. Los IDs son los del sistema anterior, que la importación conserva.

## 8. Panel del staff y portal del cliente

El catálogo cuelga de Configuración en el menú clásico del admin (sección 10 de la decisión de interfaz), con dos
ítems. Con el permiso `catalog.read` el staff ve Productos y servicios en `/admin/catalogo` (productos por grupo con
su precio base, complementos, grupos de opciones y paquetes) y Promociones y cupones en `/admin/catalogo/cupones`.
Con `catalog.write` además los crea y los edita en `/admin/catalogo/productos/{id}`, `/admin/catalogo/grupos/{id}`,
`/admin/catalogo/opciones/{id}`, `/admin/catalogo/paquetes/{id}` y `/admin/catalogo/cupones/{id}`, donde `nuevo` es
un alta. Los formularios usan los mismos casos de uso que la API y cada cambio queda en la auditoría. La ficha de un producto
y la de un grupo de opciones tienen Duplicar, que crea la copia y la abre para editarla. Los precios y
los planes de un paquete se cargan en filas, y para agregar se completa una fila vacía, así todo anda sin
JavaScript. Los importes viajan con punto decimal en cualquier idioma, y el navegador los muestra con el separador
del idioma.

**Agregar pedido.** Con `accounts.read` y `orders.write` el staff pide por una cuenta desde Pedidos, Agregar pedido,
en `/admin/pedidos/nuevo`, o desde la ficha del cliente. Elige cualquier producto, también los que la tienda no muestra,
el ciclo con su precio en la moneda de la cuenta, todas las opciones (también las ocultas), la cantidad si el
producto se pide por cantidad, el nombre o el servicio del que cuelga un complemento y el cupón. Primero calcula con
la cotización y después confirma con el pedido, como el portal, y el formulario lleva una referencia al azar: un doble
envío desde cualquier nodo da un solo pedido. Puede marcar que se acepte enseguida: el servicio se aprovisiona sin
esperar el pago y la factura queda igual para cobrar. Cada pedido queda en la auditoría como `order.placed_by_staff`.

**Cambios de plan.** Con `services.change` el staff cambia un servicio activo desde la ficha del cliente, en
`/admin/servicios/{id}/cambio`: elige el producto (de la misma instancia de módulo), el ciclo y las opciones,
incluidas las ocultas, calcula, confirma y puede cancelar un cambio que espera el pago. El cliente lo hace desde
Servicios en el portal, en `/servicios/{id}/cambio`, si ve los servicios y las facturas de la cuenta: pasa a los
productos que permite el suyo y cambia las opciones visibles si el producto lo deja, en el mismo ciclo y siempre con
prorrateo. Subir le deja la factura con el enlace para pagarla y bajar acredita la diferencia. Las dos pantallas
muestran el precio de cada valor, comparten la vista de las opciones y usan el mismo caso de uso que la API. Cada
cambio pedido y cada cancelación quedan en la auditoría con quién lo hizo.

**Contratar.** El portal tiene la tienda bajo Servicios, como pide el menú del portal (sección 10.3 de la decisión
de interfaz). Contrata quien ve los servicios y las facturas de una cuenta activa, porque el pedido crea los dos, y
los precios salen en la moneda de la cuenta. En `/contratar` están los productos visibles de los grupos visibles que
se pueden pedir en esa moneda, los paquetes activos con su primer período con descuento y sin él, y los
complementos. Cada producto dice desde cuánto sale en su ciclo más corto: el plan más lo menos que cobran sus
opciones (el valor más barato de cada lista que ve el cliente, el mínimo de una cantidad, "no" en un sí o no y el
defecto de una opción oculta), así uno que cobra por sus opciones, como los game servers importados, no sale en cero.
El paquete suma lo mismo por unidad. Un producto sin stock se muestra sin el botón.
- `/contratar/{id}` pide un producto: el ciclo con su precio desde, las opciones visibles (las ocultas toman su
  defecto), un nombre o dominio, el cupón y, si es un complemento, el servicio activo de la cuenta del que cuelga. Si no lo es, ofrece los
  complementos visibles que lo admiten, plan por plan, y los elegidos van en el mismo pedido colgados de su línea,
  con las opciones por defecto.
- `/contratar/paquete/{id}` pide un paquete: una línea por unidad de cada plan, cada una con sus opciones y su
  nombre. Un complemento del paquete cuelga de la primera línea de un producto que lo admite. Sin cupón, porque el
  paquete ya es su promoción.
- Primero se calcula con la cotización y después se confirma con el pedido, los dos casos de uso de la API. Si al
  confirmar el total ya no es el que se mostró (otras opciones, un precio nuevo o un cupón que se agotó), no se pide
  nada y se muestra el total nuevo para confirmar otra vez.
- El formulario lleva una referencia al azar que va como referencia del pedido: enviarlo dos veces, desde cualquier
  nodo, devuelve el mismo pedido.
- Confirmado, el cliente va a la factura para pagarla. Un pedido sin nada para pagar (una prueba gratis o un plan
  gratis) vuelve a Servicios con el aviso de que se está activando.

## 9. Varios nodos

Nada del catálogo vive en memoria entre pedidos: cada pedido lee los precios, las opciones y el paquete de la base. El stock y
los usos de cupón se descuentan con `UPDATE` condicionados, la prueba gratis tiene un índice único, el cambio
pendiente también, y la factura de diferencia sale por la cola con la clave de entidad de la cuenta.

## 10. Pendiente

- Un carrito con varios productos en el mismo pedido. Hoy la tienda pide un producto o un paquete por vez, y la API
  pide cualquier combinación.
- Un tope de cambios por servicio y día en el portal, si algún cliente abusa: cada cambio emite una factura o notas
  de crédito.
- La clave de módulo de las opciones importadas de Proxmox, que el mapa de módulos tiene que traducir cuando esté
  la especificación del módulo.
- Precios de opciones por cantidad escalonada.
- Un cupón que exige otro producto contratado, que hoy entra inactivo al importar.
- Los otros tipos de cupón del mapa del panel: precio fijo, alta gratis y el que vale para los cambios de plan.
- De los paquetes: vigencia, usos máximos, permitir cupones, mostrar en un grupo y orden.
- Del producto: lema, destacado, color, límite de ciclos con baja al cumplirlo, prorrateo a un día del mes, venta
  cruzada y campos propios del pedido. El correo de bienvenida, el dominio gratis y las descargas van con sus
  propios diseños.
- El modo de carga de cada precio en las lecturas de la API (`price_mode`). Hoy se lee en la sección Impuestos.
- Los complementos sin módulo no pasan por el módulo manual del núcleo como los productos: se activan en el núcleo
  según su alta.
