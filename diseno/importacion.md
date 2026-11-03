# Importación desde el sistema anterior

**Estado:** v0, 2026-10-27, revisado el 2026-10-31 · **Parte de:** [diseño del núcleo](README.md) · **Decisión:** [D-17](decisiones.md)

Cómo corre el importador, qué hace con cada parte de la copia y qué necesita de cada módulo para dejar los
servicios vivos atados a su instancia. El mapeo sale de la estructura de una copia de datos del sistema
anterior, nunca de su código ni de su documentación.

---

## 1. Cómo se corre

```
BillingOss.Importer import <dump> --baseline <linea-base.tsv> [--modules <mapa.json>] [--simulate] [--recreate-db billing_oss_<nombre>]
    [--own-zones <zona,zona>] [--shadow-instances] [--as-of <AAAA-MM-DD>]
BillingOss.Importer baseline <dump> [--out <linea-base.tsv>]
```

- La base destino sale de `BILLINGOSS_IMPORT_PG` (o del archivo de `BILLINGOSS_IMPORT_PG_FILE`), nunca de la línea
  de comandos.
- `--recreate-db` borra y crea la base nombrada (solo nombres `billing_oss_*`), aplica las migraciones e importa.
- Sin `--recreate-db`, la base tiene que estar sin cuentas. Puede tener ya cargadas las instancias de módulo,
  que es lo que hace falta para atar servicios (sección 3).
- `--simulate` hace todo dentro de una transacción y la descarta.
- `--shadow-instances` es el modo sombra del paralelo (sección 3).
- `--as-of` es el día de la copia, por defecto hoy: lo que vencía hasta ese día y el origen no hizo es una acción que
  el origen dejó sin ejecutar (sección 2).
- `baseline` cuenta la línea base directo del volcado, sin las reglas del importador, con el formato de la que se sacó
  de la copia en el servidor y con las cifras que esa no traía (sección 4). Es la que usa el paralelo cada día.
- Sale con 0 si la conciliación no tiene diferencias y con 3 si las tiene.

El día completo del mes en paralelo (línea base, importación en modo sombra, corrida y comparación) es el comando
`daily`, en [docs/paralelo.md](../docs/paralelo.md).

---

## 2. Reglas de la importación

**Servicios.**
- El próximo período a facturar es `nextinvoicedate` y lo pagado llega hasta `nextduedate`. Si el período que
  empieza en `nextduedate` ya tiene renglón facturado, el próximo a facturar es el siguiente.
- El día de anclaje es el del vencimiento vigente (`nextduedate`), no el del alta. El origen factura ese día
  aunque el alta haya sido otro. El día del alta queda solo cuando el vencimiento cayó en el último día de un
  mes más corto (un 31 que en noviembre vence el 30), para que vuelva al 31 sin deriva.
- Los vivos (activos, suspendidos y pendientes) se atan a su instancia de módulo según el mapa (sección 3).
  Los terminados y cancelados quedan como historia, sin instancia ni IDs externos.
- Un servicio vivo de un producto con módulo que queda sin instancia sale en el reporte. Sus operaciones
  (suspender, reactivar, dar de baja) fallan a la vista del staff y no se dan por hechas en el núcleo, así que
  nunca cambia el estado sin que el servidor se entere.
- Solo cuentan como ya facturados los renglones de facturas impagas, en gestión de cobro o anuladas. Un renglón
  pagado en el vencimiento vigente es un vencimiento que el origen no avanzó al cobrar: ese período se vuelve a
  facturar y el reporte lo dice.
- Una solicitud de baja a fin de período que el origen no ejecutó queda como fecha de baja programada
  (`CancelAt`) en el vencimiento: el servicio no renueva desde esa fecha y la corrida de mora lo da de baja ese
  día. Una solicitud de baja inmediata sin ejecutar queda en su estado, porque el servidor sigue andando, con
  `CancelAt` en el día del pedido.
- Una baja que ya vencía al día de la copia y el origen no ejecutó (la pedida, o la de la mora porque lo pagado
  más el plazo de baja ya pasó) queda retenida en una tarea de revisión del staff, de `kind` igual a `review` y
  sujeto `review:service:{id}`. Mientras está abierta la mora no da de baja ese servicio. Completarla deja que la
  mora siga con sus reglas en el acto, y rechazarla deja sin efecto la baja pedida. El reporte lista los servicios.
- El motivo de suspensión del origen pasa a `SuspendReason` cuando no es la mora. Esa suspensión no la levanta
  el pago: la levanta el staff. La exención de la suspensión automática (`overideautosuspend` o una fecha
  vigente en `overidesuspenduntil`) pasa a `NoAutoSuspend`.
- Lo elegido de cada opción configurable (`tblhostingconfigoptions`) entra como opción del servicio: el valor de
  una lista y la cantidad de una opción de cantidad o de sí o no. En los vivos además va a los parámetros del
  módulo, cada opción en su clave, igual que en un pedido nuevo. Una fila cuyo valor ya no existe o es de otra
  opción no entra y sale en el reporte, igual que una cantidad viva fuera del rango de su opción.
- El servicio recuerda el cupón con el que se contrató (`promoid`). El importe del origen ya viene neto de una
  promoción recurrente. Si es de porcentaje y para siempre, el servicio guarda el descuento y el precio lleno (el
  importe dividido por lo que deja la promoción): cobra lo mismo y una opción que se agregue también lleva el
  descuento. Si es por períodos queda con el importe neto y sin descuento propio, porque no se sabe cuántos le
  quedan, y el reporte cuenta los vivos para que el staff los pase al precio lleno cuando termine.
- El fin de la prueba gratis sale de una tabla propia del origen (`mod_pruebas_gratis`, con el servicio y el día
  en que vence). La guarda el servicio que la tuvo, vivo o terminado, para que la cuenta no pida otra del mismo
  producto: una por cuenta y producto del servicio, la última. Una prueba de un servicio que no se importa no
  bloquea nada, y el reporte dice de qué productos son las pruebas para que el staff cargue sus días.

**Catálogo.** El detalle del modelo está en [catalogo.md](catalogo.md).
- Grupos de productos, productos, grupos de opciones, opciones, valores y cupones conservan su ID. Los complementos
  y sus servicios toman IDs nuevos, arriba de los importados, porque el origen los numera aparte.
- Lo que va antes de la barra en el nombre de una opción o de un valor del origen (`memory|RAM`, `4096|4 GB`) es la
  clave de módulo de la opción y el valor de módulo del valor. Lo que va después es el nombre. El mapa de módulos
  puede traducir esa clave (sección 3).
- Los precios de las opciones salen de `tblpricing` con tipo `configoptions`, por valor, moneda y ciclo. Un -1 es un
  ciclo que no se ofrece. Si el grupo de la opción está en un producto de pago único, la columna mensual da también
  el precio de pago único, como en el producto. Un valor sin ningún precio no cobra.
- Del producto entran además la descripción sin HTML, el stock, si se pide por cantidad, a qué productos puede
  pasar el cliente (`tblproduct_upgrade_products`) y si puede cambiar sus opciones (`configoptionsupgrade`).
- Cuándo se aprovisiona sale de `autosetup` en el producto y de `autoactivate` en el complemento: "order" es al
  pedir, "payment" al pagar, y "on" (al aceptar el pedido) o vacío (nunca solo) quedan para el staff. Lo que va al
  módulo manual del núcleo (sin módulo, o con el mapa en manual) se crea al pagar, o al pedir con "order": el módulo
  manual ya decide con su parámetro `activation` si abre una tarea para el staff, y no hace falta frenarlo dos veces. Un origen sin la columna queda al pagar.
- Los complementos (`tbladdons`) son productos que cuelgan de los de su lista, con su precio de `tblpricing` con
  tipo `addon`. Uno "recurring" se vende en todos los ciclos con precio, uno de ciclo fijo solo en ese y uno "free"
  en el ciclo gratis. Los contratados (`tblhostingaddons`) son servicios hijos de su servicio. Los renglones de
  factura de un complemento apuntan a ese servicio hijo.
- Los cupones (`tblpromotions`) nunca entran más amplios que en el origen: si su lista de productos o de ciclos no
  tiene nada que exista acá (por ejemplo, solo dominios) o si exigían otro producto contratado, entran inactivos.
  Un código repetido sin distinguir mayúsculas queda con el número del origen al final. Los pedidos que lo usaron
  (`promocode`) lo recuerdan, así "una vez por cuenta" sigue valiendo después de migrar. El monto fijo está en la
  moneda base del origen (`tblcurrencies` con `default`), que lo convertía al cobrar en otra moneda: entra en esa
  moneda, así en otra no descuenta hasta que el staff lo revise, en lugar de descontar el mismo número.
- Un cambio de opciones o de plan sin pagar (`tblupgrades` pendiente con la factura del pedido impaga) entra como
  cambio pendiente de esa factura: si el cliente la paga acá, el cambio se aplica, y si se anula, se cancela. De
  varios pedidos sin pagar del mismo servicio queda el último, y uno que pasa de gratis a mensual no se puede
  representar: los dos salen en el reporte.
- Los renglones de cambio de plan del origen traen el número del pedido de cambio, no el del servicio: el servicio
  sale de ese pedido.

La conciliación compara lo que el catálogo importado cobra en cada servicio vivo recurrente (plan más opciones, en
su moneda y su ciclo) con lo que cobraba el origen. Un servicio con una promoción de porcentaje recurrente para
siempre guarda el descuento y el precio lleno, y uno que paga menos que el catálogo de hoy sin promoción conserva su
precio propio, porque lo contratado no cambia.

**Cuentas.** El campo de cliente cuyo nombre empieza con "Sin comprobante fiscal" (la marca de cortesía de
un addon fiscal del origen, que lo busca igual, por prefijo) en "on" pasa a `NoFiscalDocument`. Una cuenta
del exterior queda con condición 9 (no alcanzado) aunque el campo diga otra cosa, como hizo el addon en todos
sus comprobantes E, y el reporte lo dice.

**Facturas.**
- La cabecera es la verdad (D-17). Si hay comprobante en la misma moneda, el reparto de IVA sale del
  comprobante, si no de la cabecera.
- Las líneas toman el tratamiento de la cabecera: en una factura E todas son exentas, y con comprobante al 21 %
  las líneas gravadas van al 21 %. Una línea que el origen marca como no gravada va al no gravado de la cabecera
  si lo tiene, y si no al balde mayor. Lo que no cierra en cada balde va en una línea de ajuste con ese mismo
  tratamiento. Así una nota de crédito armada con las líneas (la anulación automática de impagas lo hace)
  cierra contra la cabecera.
- El documento guarda el estado del origen (`SourceStatus`). Los importes dicen el estado salvo en dos casos:
  un total cero (el núcleo nunca emite uno así) toma pagada o anulada según el origen, y una reembolsada se ve
  como anulada en el núcleo y como `Refunded` en la API compatible.
- Lo cobrado de una impaga se toma neto de lo devuelto o revertido, y solo en la moneda de la factura.
- Las notas de crédito y de débito nativas (`tblbillingnotes`) entran como documentos propios, con la factura
  del primer renglón como original y sus comprobantes colgados de la nota, no de la factura. Lo acreditado por
  nota entra en la cabecera de la factura, que sigue pagada si la nota es parcial. Una anulada o reembolsada sin
  nota nativa (anterior a septiembre de 2026) conserva el crédito sintético. Las notas no conservan su id del
  origen y la numeración de notas de cada tipo sigue desde la última del origen.
- Las notas que el origen abre por un movimiento de saldo (una de crédito al aplicar saldo a favor a una factura y
  una de débito al quitarlo, cada una con su fila de `tblaccounts`) no son documentos: el saldo entra como
  imputación (sección de pagos). Una nota con comprobante fiscal siempre es un documento.
- Las facturas en borrador no se importan y salen en el reporte.
- El reporte avisa si alguna factura queda con un estado derivado distinto del de origen o con líneas que no
  dan los importes de la cabecera.

**Pagos.**
- Un movimiento sin moneda en el origen (currency en 0) va en la moneda de la cuenta, salvo un importe de 10
  veces o más el bruto de su factura en otra moneda: son pesos que cobró una pasarela local. Ese pago entra en
  ARS, y la imputación va en la moneda de la factura por lo que debía, con la cotización implícita.
- Entre 1,5 y 10 veces queda en la moneda de la cuenta y sale en el reporte para revisar.
- Solo los movimientos de `tblaccounts` que son plata se importan como pagos. La contrapartida de una nota de
  crédito nativa (`invoice_billing_adjustment_credit`) no se importa: ya está en la factura como acreditado. El
  saldo a favor aplicado a una factura (`invoice_credit_applied`, o el crédito de su cabecera) entra como
  imputación de saldo con su contrapartida en el libro, así el libro suma lo mismo que el saldo de la cuenta,
  que el origen ya tiene neto de esos consumos. Lo que se le quitó a la factura (`invoice_credit_removed`) se resta
  de lo aplicado y tampoco es plata que salió.
- Un pago con una salida por el mismo importe (una fila con `refundid` o una salida de la misma factura) se
  importa devuelto, sin imputación.
- Cada imputación se topea a lo que la cabecera de la factura dice imputado, y una anulada o reembolsada no
  recibe ninguna: las filas de imputación nunca suman más que la cabecera, y la conciliación lo controla.

**Dominios.** Se importan con su ID, estado, precio congelado de renovación y medio de pago. Cada uno va a la
instancia de su registrar: los de internetbs a la suya, que queda sin URL hasta que se despliegue su módulo, y los
que no tienen registrar con API a una instancia del registrar manual (`nic-ar-manual` para los .ar,
`subdominios-propios-manual` para los subdominios de las zonas de `--own-zones`, `{registrar}-manual` para un
registrar sin módulo y `sin-registrar` para el resto). El próximo período sale de `nextduedate`, que en los
dominios es el vencimiento, y si ese período ya tiene renglón facturado, el siguiente. Los precios por TLD salen
de `tbldomainpricing` y `tblpricing`. La conciliación compara la cantidad por estado. Un dominio vivo con lo pago
vencido al día de la copia sale en el reporte, con cuántos pasaron la gracia: Hornero los pasa a gracia y a
vencidos en su primera corrida, y el origen los cambia por su cuenta.

**Listas de bloqueo.** Los dominios de correo bloqueados (`tblbannedemails`) y las IPs bloqueadas (`tblbannedips`)
entran a las listas de bloqueo ([fraude y acceso](fraude-y-acceso.md), sección 4) con `import` como autor. Una IP
conserva su motivo y su vencimiento, una ya vencida no se trae y una sin fecha queda permanente. Lo que no es un
dominio o una IP válida no se trae y el log dice cuántas fueron.

**Fraude.** Un pedido con estado de fraude en el origen se importa cancelado y con su evaluación rechazada (autor
`import`, motivo `import.fraud`), así se ve en la revisión y en la seguridad de la ficha. El log imprime además los
ajustes de seguridad equivalentes para cargar en el panel: los minutos de bloqueo por intentos
(`InvalidLoginBanLength`) y si el antifraude saltea a los clientes existentes (`SkipFraudForExisting`).

**Soporte** ([docs/soporte.md](../docs/soporte.md)).
- Departamentos y tickets conservan su ID, y los tickets su número público. Un número vacío o repetido queda como
  `I` más el ID, con incidencia. Las secuencias arrancan arriba de lo importado.
- La casilla de cada departamento entra con su servidor y su usuario, apagada: la contraseña no se importa y el staff
  la carga. "Solo respuestas por correo" no tiene equivalente y sale
  como incidencia. Un departamento que usan tickets y el origen ya no tiene queda oculto, de reemplazo.
- Estados: abierto y con respuesta del cliente pasan a abierto, respondido a esperando al cliente, en curso y en
  pausa a lo suyo, y cerrado a cerrado. Un estado propio del origen se decide por sus marcas: si no cuenta como
  activo es cerrado, si cuenta como esperando es abierto, y si no, en curso. La urgencia baja, media y alta pasa a
  prioridad baja, normal y alta.
- Un ticket de una cuenta toma el nombre y el correo de la cuenta cuando el ticket no los trae. Uno sin cuenta queda
  como de alguien que escribió por correo. El servicio se ata si es un servicio importado. Los de un dominio todavía no
  tienen a qué atarse y se cuentan.
- El primer mensaje de cada ticket está en el ticket del origen. Con las respuestas y las notas internas entran como
  mensajes importados, que nunca avisan. Quien escribió es del staff si el origen lo marca así.
- El staff no se importa (sus contraseñas no se descifran) y se carga antes del corte. Quien ya tiene usuario con el
  mismo correo que en el origen conserva sus tickets asignados, la firma de sus mensajes, sus encuestas y las reglas de
  escalamiento que lo asignan. Los demás se cuentan en los controles del soporte.
- Un ticket unido en el origen queda cerrado y apuntando al último de su cadena, y sus mensajes pasan a ese con la
  marca de la unión, igual que en una unión nueva.
- Los adjuntos entran con su nombre. El archivo no está en la copia de la base: con `--support-files` y la carpeta de
  adjuntos copiada del servidor anterior, cada uno que está entra con su contenido, hasta 50 MB. Un nombre guardado con
  carpetas no se busca, así nunca se lee fuera de esa carpeta. Los que faltan se listan y no se bajan, y los controles
  del soporte cuentan cuántos entraron con archivo y cuántos sin él.
- La base destino conserva el staff que se cargó antes de importar, salvo con `--recreate-db`, que la vuelve a crear
  vacía: en ese caso el staff se carga y la importación se repite sin esa opción.
- Respuestas predefinidas con su categoría, filtros del correo y reglas de escalamiento. De una regla, la persona a
  la que asignaba queda si tiene usuario, y si no sale como incidencia. La respuesta al cliente pasa a la
  respuesta de la regla, con una incidencia para revisar el texto antes del corte.
- Encuestas: una por ticket, la última si el origen tiene más, con la persona del staff si tiene usuario y sin la IP.
- La conciliación compara la cantidad de filas de cada tabla del soporte de la línea base con las que el importador
  leyó, y muestra como información lo que no entró y por qué (respuestas o encuestas sin ticket, encuestas repetidas,
  tickets unidos y de dominios) y los tickets por estado del origen.


**Afiliados.** El programa, las reglas por producto y de cada afiliado, los afiliados con su ID como código, los
servicios referidos, las comisiones pagadas y en espera, los retiros y los clics, con el detalle en la sección 11 de
[afiliados.md](afiliados.md). El saldo del libro de cada afiliado tiene que dar el del origen, y la conciliación
compara las filas de cada tabla del programa.

**Contenidos para clientes.** La base de conocimiento (categorías, artículos con sus traducciones, enlaces y etiquetas), los
anuncios, las descargas con sus productos y los incidentes y mantenimientos de la red se importan con las reglas de la sección
11 de [contenidos.md](contenidos.md). Conservan los IDs de origen salvo las categorías de descargas que chocan con una de la
base de conocimiento, que comparten tabla. El HTML pasa al Markdown acotado del sitio. Las descargas quedan en borrador y sin
archivo y los artículos enlazan imágenes que no están: el volcado trae solo los nombres de los archivos. Los componentes de la
red se arman por servidor o por texto de lo afectado y los incidentes sin resolver se importan abiertos. El reporte solo cuenta,
nunca reproduce un texto.

**Ingreso con Google.** Los vínculos de `tblauthn_account_links` del proveedor de Google entran a los vínculos de
ingreso externo ([ingreso de clientes](ingreso-clientes.md), sección 6), con su identificador del proveedor, el correo
de su metadata y el usuario con su mismo ID. Corren después de los usuarios: un vínculo
sin su usuario no se trae y queda en los incidentes. El proveedor Google queda cargado y apagado, con el ID del cliente
de `tblauthn_config` si tiene la forma de uno. El secreto no se importa: se carga en el panel.

---

## 3. Mapa de módulos

Un JSON que arma el staff y se pasa con `--modules`. Ata cada producto y cada servicio vivo a una instancia
de módulo, con los parámetros y los IDs externos que el módulo espera. El importador nunca carga secretos y, salvo
en modo sombra, no crea instancias: se dan de alta antes en la base destino, por nombre, y sus secretos salen del
vault. En modo sombra (`--shadow-instances`, el del paralelo) crea sin URL las que faltan, así los servicios quedan
atados y ninguna operación llega a un módulo, y se frena si alguna ya existe con URL.

```json
{
  "pterodactyl": {
    "instance": "juegos",
    "params": { "memory_mb": "configoption1", "egg_id": "configoption8" },
    "external_ids": { "address": "dedicatedip" }
  },
  "vesta:2": { "instance": "hosting", "params": { "package": "configoption1" }, "external_ids": { "username": "username", "domain": "domain" } },
  "proxmox_custom": {
    "instance": "cluster",
    "external_ids": { "vmid": "campo:VMID", "public_ip": "dedicatedip", "username": "username" },
    "options": { "Amount of RAM": "memory_gb", "IP Addresses": "network.extra_ips" }
  },
  "modulo_propio": { "manual": true }
}
```

| Clave | Qué es |
|---|---|
| Nombre de la entrada | El `servertype` del producto. Con `servertype:servidor` vale solo para los servicios de ese servidor del origen, y gana sobre la general |
| `instance` | Nombre de la instancia de módulo en la base destino. Si no existe o está repetida, sale en el reporte y lo que la nombra queda sin vincular |
| `manual` | El producto pasa a manual: va al módulo manual del núcleo, como los productos sin módulo, con `activation` y `lifecycle` (sección 9.1 del [protocolo](protocolo-modulos-v0.md)) |
| `params` | Parámetro del módulo y columna del producto de donde sale. Sin `params`, quedan los `configoptionN` del origen tal cual, con la clave `module`. Con `params` vacío no va ninguno, tampoco un secreto guardado en un `configoptionN` |
| `external_ids` | ID externo y de dónde sale: una columna del servicio (`dedicatedip`, `username`, `domain`) o `campo:Nombre`, un campo de producto (el nombre antes de la barra) |
| `services` | IDs externos fijos por servicio, para datos que no están en la copia |
| `options` | Clave de módulo de las opciones configurables de los productos de esa entrada: la parte técnica del nombre de la opción en el origen (lo que va antes de la barra) y dónde va su valor en los parámetros, con puntos para anidar. Vacío la deja sin clave, y la opción solo cobra. Si dos entradas le dan claves distintas a la misma opción, queda la del origen y sale en el reporte |

Reglas que el importador aplica siempre:
- Un campo de producto de tipo contraseña o con nombre de secreto (pass, token, secret, key, licen, clave)
  nunca va a `external_ids`: sale en el reporte y se carga cifrado aparte.
- El producto toma la instancia y los parámetros de la entrada general de su `servertype`.
- Si los servicios de un mismo `servertype` están en más de un servidor del origen y el mapa no los separa,
  sale en el reporte.
- Sin `--modules`, ningún servicio queda atado y el reporte lista cuántos vivos quedan sin instancia por módulo.
  Sus operaciones fallan a la vista en vez de tocar nada.
- Para el mes en paralelo eso no sirve, porque cada suspensión o baja del día saldría como un trabajo con falla. Va
  un mapa del paralelo en modo sombra: ata cada vivo a una instancia sin URL o al módulo manual, sin `params`, y
  ningún módulo se llama ([docs/paralelo.md](../docs/paralelo.md)). El mapa con instancias reales, `params` y
  `external_ids` de la sección 3.1 es el del corte.

### 3.1 Lo que espera cada módulo

Sale de las especificaciones de cada módulo, que se escriben en paralelo. Si una cambia, se cambia el mapa, no
el importador.

| `servertype` | Módulo | Instancia | `params` | `external_ids` |
|---|---|---|---|---|
| `pterodactyl` | `pterodactyl` | La del panel de game servers | Los de su `product_schema`, desde `configoption1` a `configoption21` | `address` desde `dedicatedip`. El ID del servicio y el de la cuenta ya se conservan. Los IDs internos del panel los resuelve el módulo con `get_status` |
| `proxmox` o un módulo propio de Proxmox | `proxmox` | La del cluster | Los de su `product_schema` (las opciones configurables todavía no se importan) | `vmid` desde `campo:VMID`, `public_ip` desde `dedicatedip`, `username`. Marcador y red se completan con `get_status` |
| `vesta` o `hestia` | `hestia` | `vesta:N` a la del servidor N. Los de servidor 0 quedan sin instancia y salen en el reporte | `package` desde `configoption1` | `username` y `domain` |
| `tacticalrmm` | `tacticalrmm` | La de Tactical RMM | Ninguno. La API Key del producto nunca va a `params` | `client_id`, `site_id`, `role_id` y `user_id` con `campo:` |
| Un módulo propio sin sistema externo | `manual` | `manual: true`, a la instancia de servicios manuales | `activation` y `lifecycle` | Ninguno |
| `cpanel`, `plesk` o `directadmin` | El del mismo nombre | Una por servidor del panel en el origen | `plan` desde la columna del paquete o plan del producto | `username` y `domain`. El procedimiento está en la sección de migración del README de cada módulo |

Las opciones configurables elegidas por servicio (RAM, CPU, disco) ya entran a los parámetros con la clave de cada
opción. Lo que el importador todavía no trae y piden algunos módulos: las direcciones IP asignadas además de la
dedicada y los datos de las tablas propias de cada módulo del origen. Cuando se agreguen, van a `params` o a
`external_ids` del servicio con el mismo mapa.

---

## 4. Conciliación

Compara lo importado con la línea base de la copia, sección por sección. Ninguna pasa en silencio:

| Marca | Qué quiere decir |
|---|---|
| `OK` | Coincide en cantidad (y en importe, donde lo hay) |
| `DIF` | No coincide, o es una sección de la línea base que el importador no conoce. Cuenta como diferencia |
| `OMIT` | Se omite a propósito, con el motivo (conteos crudos de tablas, salvo las del soporte, formas de documentos, homologación, borradores) |
| `FALTA` | El origen lo tiene y el importador todavía no lo trae (hoy, ninguna sección). No es diferencia de lo importado, pero se lista y se repite en el resumen |
| `INFO` | Cifra propia del importador que la línea base no trae (pagos por tipo de movimiento, bajas pedidas por tipo, notas nativas por tipo y moneda, servicios vivos por mes de vencimiento y con el período del vencimiento ya facturado, tickets por estado, los controles del soporte y lo que entró del catálogo: grupos de opciones, opciones, valores y sus precios, complementos, cupones, opciones elegidas, cambios pendientes y pruebas gratis). Se muestra y no se compara. La línea base del comando `baseline` trae las del dinero y los servicios, que entonces se comparan como el resto: una clave que solo tiene lo importado también es `DIF`. Las del catálogo se muestran siempre |

De los contenidos para clientes compara la cantidad de filas de cada tabla de la línea base (`table_rows`): cada fila se importa,
se une como traducción de otra o se descarta con su incidencia, así que ninguna se pierde en silencio. Las imágenes de la base de
conocimiento salen como `FALTA`, y lo importado de cada contenido como `INFO`.

Además de la línea base controla el anclaje (ningún servicio vivo recurrente puede quedar anclado fuera del día
de su vencimiento, y dice cuántos tenían el alta en otro día) y las imputaciones por documento (las filas nunca
suman más que lo imputado en la cabecera). Los pagos se comparan por la moneda que dice el
origen (la de la cuenta cuando no la dice), así que un pago en pesos de una factura en dólares cuenta en dólares
en la conciliación aunque se importe en pesos. El reporte de incidencias dice cuántos son.
