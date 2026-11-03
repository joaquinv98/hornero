# Dominios y registrars

Guía de operación de los dominios: cómo se piden, se facturan, se renuevan y se sincronizan, qué registrars hay,
cómo se configuran y qué ven el cliente y el staff. El contrato con los módulos está en la sección 9.2 del
[protocolo](../../diseno/protocolo-modulos-v0.md) y las reglas del modelo, en la sección 9 del
[modelo de dominio](../../diseno/dominio.md).

## Cómo funciona

**Pedido.** El cliente busca un nombre, elige los años con el precio de cada plazo a la vista, revisa el titular y
confirma. En una transacción se crea el dominio pendiente con el precio de renovación congelado, se anota la
operación sin mandarla y se emite la factura. El registro o la transferencia van al registrar recién cuando la
factura queda paga, o enseguida si el total es cero. Un nombre vive en una sola cuenta a la vez: dos pedidos del
mismo dominio no pueden convivir. El precio sigue el modo de carga de su moneda en los ajustes de impuestos (con
impuesto incluido o sin impuesto), que queda congelado en el dominio como en un servicio, y sus renglones van como
servicios digitales, con la fecha fiscal del día en que se emiten. Si la factura del pedido se anula entera (la mora o
el staff), el pedido se cierra como al cancelarlo y el nombre queda libre para volver a pedirlo.

**Transferencia.** Es el mismo camino con el código EPP que da el proveedor actual. El código se guarda cifrado
(D-09), viaja solo en la llamada al registrar y nunca queda en la operación. Queda en curso hasta que el registro de
origen la suelta, se consulta cada hora y, al completarse, el código se borra porque ya se usó. Solo se vende si el
registrar de la extensión declara `registrar.transfer`.

**Renovación.** La corrida diaria de facturación factura cada dominio con renovación automática cuando entra en su
anticipación (`Billing:DomainRenewalLeadDays`, 7 días), con su precio congelado, en el mismo borrador que los
servicios de la cuenta con el mismo vencimiento. Al pagarse, se pide `renew` al registrar con el vencimiento que se
renueva, y la respuesta trae el vencimiento nuevo. Una renovación de precio cero avanza el período sin factura y se
pide igual, porque el registro la cobra. Si la renovación no se pagó, el dominio pasa a gracia y, pasados
`Billing:DomainGraceDays` (45 días), a vencido, que ya no se factura y lo decide el staff. Un vencido que paga tarde
la factura que tenía abierta avanza lo pago y pide la renovación igual: si el registro la acepta vuelve a activo, y si
la rechaza (por ejemplo un .ar que NIC Argentina ya liberó) queda vencido en la cola de fallas, donde el staff la
reintenta o la anula con la nota de crédito. El pago toma la fila del dominio como la corrida, así dos pagos del mismo
dominio en dos nodos no se pisan.

- **Renovar ahora** factura el próximo período en el momento, por ejemplo un dominio sin renovación automática o en
  gracia. Usa la misma clave de renglón que la corrida, así ese período nunca se factura dos veces. Se ofrece mientras
  la renovación no tenga una factura abierta, también cuando la mora anuló la suya.
- **Una renovación anulada** por el staff con una nota de crédito vuelve a estar por facturar, como los períodos de los
  servicios: el próximo a facturar retrocede a su inicio y, si estaba pago, lo pago también. Vale para la nota que anula
  toda la factura y para la que acredita entero el renglón del dominio en una factura compartida, por ejemplo porque el
  cliente no lo renueva: pagar el resto de esa factura ya no lo renueva. Un renglón sin clave de período, como los
  importados o uno hecho a mano, es el del período que sigue a lo pago, salvo que otra factura abierta lo cubra:
  entonces es de un período anterior, como una importada ya paga que se reintegra, y no libera nada. Los renglones
  emitidos están congelados, así que la factura nueva del mismo período lleva la clave con cuántas hubo antes
  (`domain:{id}:{inicio}:1`) y el texto y el precio del renglón anulado: una opción contratada después ya se cobró
  aparte hasta el fin de ese período. Si el período ya empezó, la factura vence a los días de la emisión.
- **Lo que anula la mora** (`Billing:CancelUnpaidAfterDays`) no vuelve a estar por facturar: la corrida no lo reemite,
  porque la factura nueva vencería enseguida y la mora la volvería a anular cada día hasta el vencimiento. Lo vuelve a
  facturar Renovar ahora, desde el portal o el admin, con vencimiento a los días de hoy, o la reemisión cuando el cliente
  paga después de la anulación y su saldo a favor la cubre, con la clave y el renglón de la anulada. Un renglón sin clave
  de período, como los importados, no entra en esa reemisión: lo vuelve a facturar Renovar ahora.
- **No renovar** es distinto de dar de baja: el dominio sigue activo hasta su vencimiento y no se factura más. Se
  vuelve a prender hasta esa fecha, y uno en gracia que vuelve a renovarse entra en la corrida siguiente.

**Sincronización diaria.** Una vez por día el planificador encola un trabajo por cada dominio activo o en gracia, con
la clave de entidad del dominio (`domain:{id}`), así no se cruza con sus operaciones y lo corre cualquier nodo. El
trabajo pide `get_domain` y guarda el estado en el registro, el vencimiento, los nameservers y el bloqueo. El estado de
facturación no cambia: sale de lo pago. Solo un dominio que se fue a otro registrar (`transferred_away`) deja de
renovarse y de facturarse, y el renglón de su renovación en cada factura sin pagar se acredita con una nota de crédito.
Lo demás de una factura compartida se sigue debiendo, y una ya paga, con la renovación pedida al registrar, la decide
el staff. Los .ar del registrar manual no tienen API: el vencimiento sale del RDAP de NIC Argentina y,
si no contesta, se prueba al día siguiente sin abrir tareas. El staff puede pedir una sincronización en el momento.

**Operaciones.** Nameservers, bloqueo de transferencia, código EPP y titular son operaciones por la cola, en el orden
del dominio y de a una. Cada pedido es una intención nueva. Solo se ofrece lo que el registrar declara en su
manifiesto. Mostrar el código EPP pide confirmación y queda en la auditoría (`domain.auth_code.reveal`).

**DNS y correo.** Si el registrar administra la zona (`registrar.dns`), el cliente y el staff editan los registros A,
AAAA, CNAME, MX y TXT en una tabla. Se leen del registrar en el momento y se guardan enteros por la cola: el módulo
saca lo que no viene y agrega lo que falta, así repetir el pedido deja lo mismo, y los tipos que no se administran
(NS, SOA, SRV, CAA) quedan como están. Cada fila se valida antes de encolar con la misma regla que el módulo, y una
fila mal no guarda nada. Mientras un cambio está en la cola la página muestra lo pedido. Los reenvíos de correo
(`registrar.email_forwarding`) van igual: una casilla del dominio, un destino. Los registros solo se ven en internet
si el dominio usa los servidores DNS del registrar.

**Protección de identidad.** Con `registrar.privacy` el cliente la pide al registrar el dominio o la prende y la
apaga desde la ficha. Al terminar queda en el dominio (`IdProtection`) y la sincronización diaria la vuelve a leer, por
si alguien la cambió en el panel del registrar.

**Opciones con precio.** La gestión de DNS, el reenvío de correo y la protección de identidad son opciones del dominio
(`DomainOptions`) y se ofrecen si el registrar declara su capacidad y la extensión no la apagó. Cada una puede tener un precio por año y por
moneda, con vigencia como los de las extensiones (`catalog.domain_option_prices`). Sin precio es gratis: la gestión de
DNS y el reenvío están siempre que se ofrecen y la protección se prende y se apaga sin cargo. Con precio se contratan:

- **Al pedir el dominio**, su precio por los años del pedido va en la misma línea del registro o la transferencia.
- **Desde la ficha**, se factura por día la parte que queda hasta el vencimiento ya facturado, y la opción se aplica
  cuando esa factura se paga (`add_option`, que espera el pago como el registro de un pedido). Pedirla de nuevo con la
  factura sin pagar devuelve la misma factura, y si esa factura se anuló se hace otra.
- **En la renovación**, el precio anual de cada opción contratada queda congelado en el dominio (`OptionPrices`) y
  sumado al de la renovación, con su nombre en la línea ("+ gestión de DNS").

Darla de baja la apaga enseguida y saca su precio de la próxima renovación, sin devolver lo pagado. Sin contratar, la
tabla de DNS o de reenvíos no se lee ni se cambia y la página lleva a contratarla. Un dominio importado con la gestión
de DNS la conserva con el precio que traía dentro de la renovación: darla de baja no lo baja, lo corrige el staff.

**Opciones por extensión.** Cada extensión puede dejar de ofrecer alguna opción (`catalog.tld_options`), por ejemplo la
protección donde el registro no la permite. Sin cargar, ofrece todas las que declara su registrar. Una opción que la
extensión no ofrece no se pide al registrar el dominio ni se contrata desde la ficha, y un dominio que ya la tiene la
conserva para usarla o darla de baja. El importador trae las marcas de cada extensión de la copia: qué ofrece cada una entre la
gestión de DNS, el reenvío y la protección.

**Avisos de vencimiento.** Una corrida diaria (`domains.notices`) avisa a los dominios que no se renuevan solos: sin
renovación automática o con la renovación facturada sin pagar. Antes del vencimiento, en los días de
`Billing:DomainExpiryNoticeDays` (30, 7 y 1), y en la gracia, en los de `Billing:DomainGraceNoticeDays` (1 y 30). Uno
con renovación automática que todavía no se facturó no recibe aviso: lo avisa la factura cuando sale. La facturación
del día va en un trabajo por cuenta que puede correr después de los avisos: el dominio que se factura ese día recibe la
factura enseguida y el aviso de su umbral al día siguiente. Cada umbral es un aviso por vencimiento, con la clave `domain-expiring:{dominio}:{vencimiento}:{umbral}`. Una corrida repetida o
atrasada no duplica, y si un día no corrió el aviso sale al siguiente. El correo lo arma el módulo de notificaciones
con las plantillas `domain-expiring` y `domain-expired` (`src/BillingOss.App/Templates`), por `IDomainMail`, que en el
núcleo es `NotifierDomainMail`: arma el correo en el idioma de la cuenta, con la fecha y el monto ya formateados, y lo deja
en la cola de correo saliente en la misma transacción. Las seis plantillas de dominios están en el catálogo de avisos,
así que el proveedor las edita desde la API de plantillas como las demás.

**Avisos de lo que terminó en el registrar.** El cliente recibe un correo cuando termina el registro de un pedido
(`domain-registered`), la transferencia (`domain-transferred`) o una renovación (`domain-renewed`, con el vencimiento
nuevo), y cuando una transferencia falla (`domain-transfer-failed`). Este último lleva el motivo estable del
registrar (`invalid_auth_code`, `taken` o `failed`), nunca su mensaje, y con un código EPP rechazado le pide al
cliente uno nuevo para reintentarla. El aviso se pide en la misma transacción que deja el dominio como quedó, así
que sale con lo que pasó o no sale, y su clave es `{plantilla}:{dominio}:{operación}`: un trabajo que se repite no
manda otro. Los cambios de nameservers, bloqueo, titular, zona o protección no avisan, porque los pide el mismo
cliente o el staff.

**Fallas.** La última operación de cada tipo que falló en el registrar y no se volvió a pedir queda en la ficha, y se
reintenta como intención nueva con otra clave: el registro o la transferencia de un pedido (la transferencia, con otro
código EPP) y también la renovación, los nameservers y las demás de un dominio activo, en gracia o vencido, por ejemplo
una renovación que falló porque el módulo todavía no estaba desplegado o el revendedor no tenía saldo. En la cola del
admin una falla queda 30 días, y una renovación fallida queda mientras lo pago pase el vencimiento en el registro: sale
al reintentarla, con la nota de crédito o cuando la sincronización trae el vencimiento nuevo. Un pedido que falló
también se cancela: el dominio queda cancelado, la operación que esperaba el pago no sale nunca y la factura del pedido
se anula con una nota de crédito, con lo pagado como saldo a favor. Lo que ya está en curso en el registrar se cancela
solo después de fallar, porque si no puede completarse igual.

## Registrars

Los dominios importados que no tienen un registrar con módulo (por ejemplo los .ar de NIC Argentina, los
subdominios de las zonas propias y los de registrars sin módulo) van al registrar manual. El importador trae
también los precios por extensión y las opciones que ofrece cada una: gestión de DNS, reenvío de correo y
protección de identidad. El módulo `registrars` cubre Internet.bs y ResellerClub y suma OpenProvider y Namecheap,
de los más usados por los proveedores.

| `Module:Registrar` | Capacidades además de `registrar.core` | Configuración de la instancia | Notas |
|---|---|---|---|
| `internetbs` | `transfer`, `auth_code`, `lock`, `contacts`, `pricing`, `dns`, `email_forwarding`, `privacy` | `api_key` y `password` (secretos), `sandbox` | Registrar de TLD genéricos |
| `resellerclub` | `transfer`, `auth_code`, `lock`, `contacts`, `dns`, `privacy` | `reseller_id`, `api_key` (secreto), `customer_id`, `sandbox` | Plataforma LogicBoxes, la misma de otras marcas de revendedores. Sin precios por TLD en la API: se cargan por CSV. El reenvío de correo es otro producto con su orden y no se declara. Prende el DNS administrado de la orden antes de cambiar la zona |
| `openprovider` | `transfer`, `auth_code`, `lock`, `contacts`, `pricing`, `dns`, `privacy` | `username`, `password` (secreto), `sandbox` | Guarda su token como estado de la instancia, cifrado por el núcleo. Crea la zona con el primer cambio y después la cambia en un solo pedido. Sin reenvío de correo en su API |
| `namecheap` | `transfer`, `lock`, `contacts`, `pricing`, `dns`, `email_forwarding`, `privacy` | `api_user`, `api_key` (secreto), `username`, `client_ip`, `sandbox` | Exige la IP de salida habilitada en la cuenta. No da el código EPP por API: se pide en su panel. Su zona se reemplaza entera, así que conserva los tipos que no se administran. La protección es una suscripción del dominio |
| `manual` (en el núcleo) | Solo `registrar.core` | `registry`: `nic.ar`, `own_dns`, `manual_registrar` o `none` | Cada operación con efecto abre una tarea para el staff (sección 9.2 del protocolo) |

Un registro cuya respuesta se perdió se repite con la misma clave. Si el registrar contesta que el nombre está tomado y
el dominio ya figura en la cuenta, el módulo lo da por registrado solo si vence lo que pide el pedido contado desde hoy,
con un día de margen. La cuenta del revendedor es de todos sus clientes: un dominio de otro que ya estaba en ella vence
otro día y sigue tomado.
**Despliegue.** Un proceso por registrar, con la misma imagen. Se configura por variables de entorno:
`Module__Registrar` elige el registrar, `Module__SigningSecret` es el secreto de firma que tiene el núcleo en la
instalación (sale del vault), `Module__PreviousSigningSecret` lo acompaña durante una rotación y
`Module__IdempotencyStorePath` es el archivo del registro de idempotencia, en un volumen que sobrevive a los
reinicios. Las credenciales del registrar nunca van en el módulo: son secretos de la instancia, que el núcleo guarda
cifrados y manda en cada llamada. El importador deja las instalaciones de los registrars con módulo sin URL hasta que
se despliegan.

## Disponibilidad y RDAP

La búsqueda consulta en este orden y para en la primera respuesta que sirve:

1. El sistema: un nombre vivo en cualquier cuenta no está libre.
2. El registrar que vende la extensión, con `check`, una llamada por registrar con todos sus nombres.
3. El RDAP del registro (RFC 9082 y 9083), cuando el registrar no sabe, como el manual de .ar. Se reintenta un 500, un
   429 o un corte con espera creciente. Solo un 404 dice que el nombre está libre, aunque venga en HTML como en NIC
   Argentina. Un 200 que no es un dominio en JSON es la página de un intermediario y se reintenta.
4. El DNS, si el RDAP no contesta: un nombre delegado está registrado y uno sin delegar queda sin confirmar, porque
   puede estar registrado sin delegar. Una falla nunca da un nombre por libre.

Las consultas al registro de una búsqueda van todas a la vez y tienen un presupuesto total
(`Billing:DomainLookup:SearchBudgetSeconds`, 8 segundos): lo que no contestó a tiempo queda sin confirmar, así una
búsqueda con NIC Argentina caído no deja la página cargando. Los registrars se consultan de a uno, con su espera de 10
segundos cada uno, porque comparten la conexión a la base. Los reintentos largos quedan para la sincronización diaria.

Un nombre sin confirmar se puede pedir: el registrar lo verifica al registrarlo (en .ar, el staff al hacer el
trámite) y, si no estaba libre, el staff cancela el pedido con la nota de crédito. Los nombres premium no se ofrecen
al precio de la extensión.

Cada pedido, también los que llegan por la API, vuelve a consultar el nombre antes de facturar: no se factura el
registro de un nombre tomado o premium ni la transferencia de uno que está libre. Si nadie sabe (el registrar no
contesta y el RDAP tampoco), el pedido sigue como un nombre sin confirmar.

| Clave | Para qué |
|---|---|
| `Billing:DomainLookup:Enabled` | Apagado, ningún nombre se consulta afuera. Prendido por defecto |
| `Billing:DomainLookup:BootstrapUrl` | Lista de servidores RDAP por TLD de IANA (RFC 9224). Cada nodo la guarda en memoria un día: es una copia de un dato público, no un estado |
| `Billing:DomainLookup:Servers:{sufijo}` | Servidor RDAP propio para un sufijo, por encima de la lista de IANA, por ejemplo `Servers:ar` |
| `Billing:DomainLookup:Attempts`, `RetryDelayMilliseconds`, `TimeoutSeconds` | Intentos por consulta (3), primera espera (400 ms, se duplica) y espera por intento (5 segundos) |
| `Billing:DomainLookup:SearchBudgetSeconds` | Presupuesto total de las consultas al registro de una búsqueda o un pedido (8 segundos) |
| `Billing:DomainLookup:DnsResolvers:{n}` | Resolvedores para la consulta de NS, como `1.1.1.1`. Vacío, los del sistema |
| `Billing:DomainNameservers:{n}` | Nameservers de los dominios nuevos cuando el pedido no trae otros |
| `Billing:DomainExpiryNoticeDays:{n}`, `Billing:DomainGraceNoticeDays:{n}` | Días de los avisos antes del vencimiento (30, 7 y 1) y en la gracia (1 y 30). Configurados, reemplazan a esos |

## Precios por TLD

Un precio es por extensión, instancia de registrar, moneda y años, con registro, renovación y transferencia. Tiene
vigencia: un cambio es una fila nueva desde su fecha, la misma fecha corrige la fila, y los dominios ya contratados
conservan su precio congelado. Si dos instancias venden la misma extensión, gana la de vigencia más reciente y, a
igual vigencia, la de menor número. Se cargan de tres formas, todas desde el admin y la API:

- **A mano**, de a una extensión.
- **Desde el registrar** (`get_prices`): el costo pasa a precio de venta con la cotización a la moneda de venta, el
  margen, el impuesto (si los precios se publican con IVA incluido) y el redondeo hacia arriba al múltiplo pedido.
- **Desde un CSV**, para los registrars que no publican costos: una fila por extensión y años con el encabezado `tld`,
  `years`, `register`, `renew` y `transfer`. Acepta punto y coma con coma decimal, como exporta una planilla. Una
  celda vacía es que esa operación no se ofrece.

Las dos importaciones primero muestran la propuesta y recién guardan cuando se confirma.

## Pantallas

Cuelgan del menú clásico de la sección 10 de `interfaz-y-temas.md`: en el portal, Dominios (Mis dominios, Registrar
un dominio y Transferir un dominio), y en el admin, Clientes, Dominios, Utilidades, Consulta de dominios, y
Configuración, Dominios y precios por TLD y Registradores.

**Portal**, con la capacidad `domains` de la cuenta (el rol técnico la incluye):

| Ruta | Qué tiene |
|---|---|
| `/dominios` | Los dominios con su estado, vencimiento, renovación y precio |
| `/dominios/nuevo` | Búsqueda, elección de los años, titular de la cuenta o propio, la protección de identidad y las opciones con precio si el registrar las ofrece, y la transferencia con su código EPP. Lleva a pagar la factura |
| `/dominios/{id}` | Renovación con fecha y monto, renovar ahora, no renovar, nameservers, bloqueo, código EPP, las opciones del dominio con su precio (contratar, dar de baja y la factura que espera el pago), titular, reintento de una transferencia que falló y lo que está en curso |
| `/dominios/{id}/dns` | Registros DNS y reenvíos de correo en una tabla, con filas vacías para agregar y una casilla para quitar. Si la opción tiene precio y no está contratada, lleva a contratarla |

El inicio suma los dominios en gracia, vencidos o que vencen en 30 días.

**Admin**, con los permisos `domains.read` (ver) y `domains.manage` (operar y cambiar precios):

| Ruta | Qué tiene |
|---|---|
| `/admin/dominios` | Cola por trabajo pendiente: operaciones fallidas (30 días, y las renovaciones pagas sin renovar hasta que se resuelven), pedidos pendientes, en gracia, por vencer, vencidos y sin renovación. Búsqueda por nombre, número de dominio o de cuenta |
| `/admin/dominios/{id}` | Estado de facturación y del registro, historial de operaciones con sus errores, tarea abierta del registrar manual y las acciones, incluidas las opciones del dominio, reintentar y cancelar un pedido |
| `/admin/dominios/{id}/dns` | La misma tabla de DNS y correo del portal. Con `domains.read` se ve y con `domains.manage` se cambia |
| `/admin/dominios/consulta` | Un nombre: si está libre según la búsqueda del portal, qué dice el registro por RDAP (vencimiento y estados) y en qué cuentas está |
| `/admin/dominios/precios` | Precios vigentes por moneda, carga a mano, importación desde el registrar y desde un CSV, el precio por año de las opciones del dominio y las opciones que ofrece cada extensión |
| `/admin/registradores` | Las instancias de registradores con su módulo, dónde corre, sus capacidades, cuántos dominios y extensiones atiende, y la prueba de la configuración contra el registrar |

La ficha de la cuenta lista sus dominios. Las tareas del registrar manual se cierran por la API de tareas
(`/api/v1/staff-tasks`, en el [README](../../README.md)).

## API

| Ruta | Permiso | Para qué |
|---|---|---|
| `GET /api/v1/domains` | `domains:read` | Por `status` o `account_id` |
| `GET /api/v1/domains/{id}` | `domains:read` | Detalle con sus operaciones, sus opciones (`options`) y el precio congelado de las contratadas (`option_prices`). Del código EPP solo dice si hay uno |
| `POST /api/v1/domains/check` | `domains:read` | Disponibilidad de `names` con el precio en `currency` |
| `POST /api/v1/domains/register`, `/transfer` | `domains:write` | Pedido con `account_id`, `name`, `years`, `nameservers`, `auth_code`, `registrant`, `privacy`, `dns_management` y `email_forwarding`. Devuelve `domain_id` e `invoice_id` |
| `POST /api/v1/domains/{id}/nameservers`, `/lock`, `/auth-code`, `/contacts`, `/sync` | `domains:write` | Operaciones por la cola. Responden 202 |
| `POST /api/v1/domains/{id}/options`, `/privacy` | `domains:write` | Prende o da de baja una opción (`option` y `enabled`, o solo `enabled` para la protección). Sin cargo responde 202, y una contratación con precio responde 200 con su `invoice_id` |
| `GET /api/v1/domains/{id}/dns`, `/email-forwards` | `domains:read` | La zona (`records`) y los reenvíos (`forwards`), leídos del registrar en el momento |
| `PUT /api/v1/domains/{id}/dns`, `/email-forwards` | `domains:write` | Cambia la zona o los reenvíos enteros por la cola. Responden 202 |
| `POST /api/v1/domains/{id}/auto-renew`, `/renew`, `/retry`, `/cancel` | `domains:write` | Renovación automática, renovar ahora (devuelve `invoice_id`), reintentar la última operación fallida con `auth_code` opcional en una transferencia y cancelar con `reason` |
| `GET /api/v1/tld-prices` | `domains:read` | Precios vigentes en `currency` |
| `PUT /api/v1/tld-prices` | `pricing:write` | Cargar o corregir un precio desde `valid_from` |
| `POST /api/v1/tld-prices/import`, `/csv` | `pricing:write` | Importar desde el registrar o desde un CSV. Sin `apply` devuelve la propuesta |
| `GET /api/v1/domain-option-prices` | `domains:read` | Precio por año vigente de cada opción en `currency`. Cero es gratis |
| `PUT /api/v1/domain-option-prices` | `pricing:write` | Cargar o corregir el precio de una opción (`option`, `currency`, `price` y `valid_from`) |
| `GET /api/v1/tld-options` | `domains:read` | Las extensiones con sus opciones cargadas (`offered`). Las que no figuran ofrecen todas |
| `PUT /api/v1/tld-options` | `pricing:write` | Qué opciones ofrece una extensión (`tld` y `offered`, con `dns`, `email_forwarding` y `privacy`) |

El código EPP nunca sale por la API: se muestra en el portal o en el admin, con confirmación y auditoría.

## Pruebas

Todo se prueba contra sistemas falsos, sin red hacia afuera:

- **Registrar falso del núcleo** (`FakeRegistrarModule`): habla el protocolo con la SDK sobre un registro en memoria, y
  sirve para los pedidos, los pagos, las operaciones, la corrida, la sincronización, la API y las pantallas.
- **APIs falsas de cada registrar** en las pruebas del módulo, sobre un registro simulado común, con los escenarios de
  conformidad contra los cuatro, incluidas la zona, los reenvíos y la protección.
- **Correo de avisos capturado** (`CapturedDomainMail`), con la misma regla que la cola de correo: una clave, un aviso.
- **RDAP y DNS falsos**, con fallas a pedido. Los hosts de prueba dejan el RDAP apagado y apuntado al falso.
- **Sandboxes reales**, escritas y omitidas sin sus variables: `INTERNETBS_TEST_API_KEY` y `INTERNETBS_TEST_PASSWORD`,
  `RESELLERCLUB_TEST_RESELLER_ID`, `RESELLERCLUB_TEST_API_KEY` y `RESELLERCLUB_TEST_CUSTOMER_ID`,
  `OPENPROVIDER_TEST_USERNAME` y `OPENPROVIDER_TEST_PASSWORD`, y `NAMECHEAP_TEST_API_USER`, `NAMECHEAP_TEST_API_KEY`,
  `NAMECHEAP_TEST_USERNAME` y `NAMECHEAP_TEST_CLIENT_IP`. Van siempre contra el sandbox, y con
  `REGISTRAR_TEST_REGISTER=1` además registran y renuevan un nombre al azar ahí, cambian su zona y prenden la protección.

## Pendiente

- Conectar los sandboxes reales de Internet.bs y ResellerClub con las credenciales de prueba, y el RDAP real en el
  staging. La forma exacta de las respuestas de DNS, reenvíos y protección de cada API se confirma ahí: los falsos
  siguen la documentación pública de cada registrar.
- Cargar la dirección de los módulos y las credenciales de las instancias desde el admin: hoy Registradores las muestra
  y las prueba, y se cargan fuera del panel. La pantalla va sobre `ModuleRegistry.Install`, `AddInstance` y
  `ChangeBaseUrl`, con los secretos atados a la URL, y sirve para todos los tipos de módulo: es del panel de
  administración, con los servidores y sus instancias.
