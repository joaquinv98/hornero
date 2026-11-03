# Reportes y salidas contables

**Estado:** v0, 2026-10-31 · **Código:** `src/BillingOss.App/Reports` (consultas, Libro IVA Digital) y
`src/BillingOss.Web` (`Pages/Admin/Reports`, `ReportFormat.cs`, `ReportCharts.cs`)

Los reportes del admin se arman en el momento con los datos de la base: no hay tablas de reportes ni nada guardado
entre pedidos, así que dan lo mismo desde cualquier nodo. Cada pantalla tiene su período, avisos de lo que hay que
revisar, gráficos y tablas, y cada tabla se descarga en CSV.

## Dónde están

En el menú superior del admin, **Reportes** despliega la lista agrupada de la sección 10 de
[interfaz-y-temas.md](../../diseno/interfaz-y-temas.md) y lleva al índice, que muestra la misma lista. Los grupos, el
orden y los nombres son los de la navegación clásica (sección 10 del [diseño de la interfaz](../../diseno/interfaz-y-temas.md)). Cada reporte
tiene su permiso y el menú y el índice muestran solo los que el staff puede ver (ver [Permisos](#permisos-y-auditoría)).

| Grupo | Ruta | Reporte | Período |
|---|---|---|---|
| Ingresos | `/admin/reportes/ingresos` | Ingresos por mes | Meses, por defecto los últimos doce |
| Ingresos | `/admin/reportes/ingresos-producto` | Ingresos por producto | Meses, por defecto los últimos doce |
| Ingresos | `/admin/reportes/ingresos-anuales` | Ingresos por año | Meses, por defecto los últimos tres años |
| Ingresos | `/admin/reportes/mejores-clientes` | Clientes que más facturan | Meses, por defecto los últimos doce |
| Ingresos | `/admin/reportes/proyeccion` | Proyección de ingresos | Meses, por defecto los doce que vienen |
| Ingresos | `/admin/reportes/actividad-diaria` | Actividad diaria | Meses, por defecto el actual, día por día |
| Impuestos y Libro IVA | `/admin/reportes/libro-iva` | Libro IVA Ventas | Un mes, por defecto el anterior |
| Impuestos y Libro IVA | `/admin/reportes/ventas` | IVA por alícuota | Meses, por defecto el actual |
| Impuestos y Libro IVA | `/admin/reportes/impuestos` | Impuestos y percepciones | Meses, por defecto los últimos doce |
| Deuda | `/admin/reportes/deuda` | Deuda por antigüedad | Al día de hoy |
| Deuda | `/admin/reportes/deuda#t-AgingAccounts` | Clientes con deuda, la tabla por cuenta del anterior | Al día de hoy |
| Deuda | `/admin/reportes/suspendidos` | Servicios suspendidos | Al día de hoy |
| Deuda | `/admin/reportes/estado-cuenta?cuenta=N` | Estado de cuenta | Meses, por defecto los últimos doce |
| Recurrente y bajas | `/admin/reportes/recurrente` | Ingreso recurrente mensual y bajas | Meses, por defecto los últimos doce |
| Recurrente y bajas | `/admin/reportes/permanencia` | Permanencia de los clientes | Al día de hoy |
| Cobros | `/admin/reportes/cobros` | Cobros por medio de pago | Meses, por defecto los últimos doce |
| Cobros | `/admin/reportes/saldo-favor` | Movimientos de saldo a favor | Meses, por defecto los últimos doce |
| Clientes y servicios | `/admin/reportes/altas-clientes` | Altas de clientes | Meses, por defecto los últimos doce |
| Clientes y servicios | `/admin/reportes/clientes-servicios` | Servicios por producto y servidor | Al día de hoy |
| Clientes y servicios | `/admin/reportes/clientes-pais` | Clientes por país | Meses, por defecto los últimos doce |
| Clientes y servicios | `/admin/reportes/pedidos` | Pedidos por mes | Meses, por defecto los últimos doce |
| Clientes y servicios | `/admin/reportes/cupones` | Uso de cupones | Meses, por defecto los últimos doce |
| Clientes y servicios | `/admin/reportes/afiliados` | Afiliados | Meses, por defecto los últimos doce |
| Dominios | `/admin/reportes/dominios` | Dominios por registrador y vencimiento | Al día de hoy |
| Dominios | `/admin/reportes/avisos-dominios` | Avisos de vencimiento de dominios | Meses, por defecto los últimos doce |
| Soporte | `/admin/reportes/soporte` | Tickets por departamento y tiempos de respuesta | Meses, por defecto los últimos doce |
| Soporte | `/admin/reportes/respuestas-staff` | Respuestas del staff | Meses, por defecto los últimos doce |
| Soporte | `/admin/reportes/satisfaccion` | Satisfacción de los clientes | Meses, por defecto los últimos doce |
| Listados y exportaciones | `/admin/reportes/listado-clientes` | Listado de clientes | Al día de hoy |
| Listados y exportaciones | `/admin/reportes/listado-servicios` | Listado de servicios | Al día de hoy |
| Listados y exportaciones | `/admin/reportes/listado-facturas` | Listado de facturas | Meses, por defecto el actual |
| Listados y exportaciones | `/admin/reportes/listado-cobros` | Listado de cobros | Meses, por defecto el actual |
| Listados y exportaciones | `/admin/reportes/pdf-facturas` | PDF de facturas por lote | Un mes, por defecto el anterior |

Del prototipo no están "Bajas y sus motivos" ni "Cobros automáticos rechazados": el sistema todavía no guarda el
motivo de una baja ni hace cobros automáticos, y un ítem sin su función no se muestra (sección 10.2).

El período va en la dirección (`?desde=2026-05&hasta=2026-10`), así un reporte se comparte o se guarda como
favorito. Tiene atajos (este mes, el anterior, este año y los últimos doce meses) y un tope de 120 meses. Con htmx
cambia solo el reporte y sin JavaScript anda igual. Los meses y "hoy" son los de la zona horaria del proveedor
(`Billing:TimeZone`, por defecto `America/Argentina/Buenos_Aires`).

## Cómo se cuenta cada uno

Los importes van en la moneda de cada documento y nunca se suman monedas distintas: cada tabla tiene una fila por
moneda donde hace falta y los totales del pie son por moneda. Las notas de crédito restan.

**Ingresos por período.** Por mes de emisión y moneda: lo facturado (facturas y notas de débito), las notas de
crédito, lo anulado sin nota y lo que queda en neto, impuestos y total. Lo anulado sin nota es el crédito que trae
una factura importada que el sistema anterior anuló o reembolsó sin emitir la nota (D-17): cuenta en el mes de la
factura.

**Ingresos por producto.** El neto de las líneas de los documentos emitidos en el período, por producto y moneda,
de mayor a menor. Las notas de crédito restan y lo anulado sin nota se descuenta en proporción, como en los ingresos.
El producto es el del servicio de cada línea. Las líneas sin servicio (cargos sueltos, ajustes y notas de crédito que
no nombran un servicio) van juntas en "Sin producto". Una línea importada trae el importe con IVA, como lo guardaba el
sistema anterior, y su neto sale con la alícuota de la línea: por eso el total puede diferir en centavos del de
ingresos. Documentos cuenta facturas y notas de débito, y el pie cuenta cada documento una vez aunque tenga varios
productos.

**Ingresos por año.** El neto de cada mes, el mismo de la columna Neto de los ingresos por mes, con una fila por
moneda y año y una columna por mes, para comparar cada mes con el de los años anteriores. Un mes fuera del período
queda vacío.

**Clientes que más facturan.** Las diez cuentas con más neto facturado en el período en cada moneda, con el neto
como en los ingresos, el total con impuestos y la parte de cada una en el neto de la moneda.

**Proyección de ingresos.** Lo que van a facturar las renovaciones de los servicios activos de ciclo mensual o anual,
cada una en el mes en que empieza su período:
- Los períodos salen del próximo a facturar de cada servicio y de su día de anclaje, igual que en la facturación. Uno
  atrasado renueva desde el primer período que cae en el rango.
- Cada renovación vale el precio de hoy con el descuento mientras le queden ciclos, sin separar impuestos, como el
  ingreso recurrente.
- Una baja programada corta las renovaciones desde esa fecha. Los suspendidos no renuevan hasta que se paguen: no
  cuentan y salen en un aviso.
- Una tabla por mes y moneda y otra por servidor, con los servicios, las renovaciones y el importe.

**Actividad diaria.** Cada día del período, también los que no tuvieron nada: cuentas nuevas, pedidos, facturas y notas
de débito, notas de crédito, cobros, servicios activados y dados de baja y tickets abiertos. Aparte, lo facturado, lo
acreditado y lo cobrado por día y moneda. Los cobros cuentan como en los cobros por medio, y sin el módulo de soporte
la columna de tickets queda vacía.

**IVA por alícuota.** El libro de ventas: neto e IVA por alícuota, exento y no gravado, por mes de la
fecha fiscal y moneda, más el detalle documento por documento con una columna de neto y otra de IVA por cada
alícuota del período, el comprobante, el CAE y el receptor que se informó. La fecha fiscal es la del primer
comprobante del documento, o la de emisión si todavía no tiene. Un documento anulado entero sin nota no aparece.

**Impuestos y percepciones.** El débito fiscal en pesos, por mes del comprobante: IVA por alícuota, exento, no
gravado y los otros tributos que informó cada comprobante (percepciones de IIBB, nacionales, municipales e
internos), con su base. Cada importe pasa a pesos con la cotización de su comprobante y se redondea a centavos. Avisa
los comprobantes en moneda extranjera sin cotización, que no cuentan, y los documentos del período que todavía no
tienen comprobante.

**Libro IVA Ventas.** Los dos archivos de ventas del Libro IVA Digital, ver abajo.

**Deuda por antigüedad.** Lo que falta cobrar de facturas y notas de débito emitidas, por días desde el vencimiento
al día de hoy: sin vencer, de 1 a 30, de 31 a 60, de 61 a 90 y más de 90. Un resumen por moneda y el detalle por
cuenta, de la mayor deuda a la menor, con el vencimiento más viejo.

**Servicios suspendidos.** Los suspendidos de hoy con el motivo del staff, o "Falta de pago" si no tiene, hasta dónde
están pagos, los días de mora, el ingreso recurrente, la deuda de la cuenta en la moneda del servicio y la baja
prevista: lo pagado más el plazo de baja del producto o el general (`Billing:TerminationDays`) si es por falta de pago,
o la baja programada si es antes. El pie no suma la deuda ni los días: la deuda es de la cuenta y se repetiría.

**Estado de cuenta.** Los movimientos de una cuenta en el período, por moneda, con el saldo que deja cada uno: el saldo
al empezar, las facturas y notas de débito y lo que volvió al pagador (reembolsos y contracargos completos, menos lo que
no se pudo cubrir) en el debe, y las notas de crédito, lo anulado sin nota en el sistema anterior, los cobros y los
ajustes de saldo a favor que no vienen de un pago ni de un documento en el haber. El saldo es lo que la cuenta debe
menos su saldo a favor, así que uno negativo es a favor del cliente. Si el período llega a hoy, el cierre se compara
con lo que dicen las facturas y el saldo a favor, y una diferencia, por ejemplo de datos importados que no cierran, sale
en un aviso con su importe. Se abre desde la ficha del cliente o con el número de cuenta, que va en la dirección.

**Ingreso recurrente y bajas.** Cuenta los servicios mensuales y anuales activos, suspendidos y dados de baja con
fecha. Cada uno vale su precio actual pasado a un mes, con descuento y sin separar impuestos: un anual cuenta la
doceava parte. Por mes y moneda: lo que había al empezar, las altas, las bajas, la tasa de bajas sobre lo del
comienzo y lo que queda al cierre. Arriba, lo de hoy de los activos y de los suspendidos. Un cancelado nunca estuvo
activo y no cuenta. Un dado de baja sin fecha (algunos importados) no se puede ubicar en el tiempo y sale en un aviso.

**Cobros por medio.** Lo cobrado por mes, moneda y canal: cada instancia de pasarela con su nombre, las cargas
manuales y lo importado. Con la comisión, lo devuelto y lo que queda. Cuenta todo pago que se cobró alguna vez,
aunque después se haya devuelto. Los pendientes y los rechazados no.

**Movimientos de saldo a favor.** Del libro de saldo a favor, que solo se agrega: por moneda, el saldo al empezar el
período, lo que entró, lo que salió y el saldo al cierre. Cada movimiento con su motivo, su documento y su pago, y el
saldo de hoy de cada cuenta que tiene.

**Altas de clientes.** Por mes de creación de la cuenta: cuántas se crearon, cuántas contrataron algo (un servicio
que llegó a estar activo, aunque después se haya dado de baja) y cuántas tienen hoy alguno activo o suspendido. Una
cuenta importada cuenta en el mes en que se creó en el sistema anterior.

**Clientes por país.** Las cuentas de cada país, las que tienen hoy algún servicio activo o suspendido y las creadas en
el período, más lo facturado en el período por país y moneda. El país de una cuenta es el de su identidad fiscal
vigente, y "Sin país" junta las que no tienen. El de un documento es el del receptor que se congeló al emitirlo, y en
un importado sin receptor, el de la cuenta. Así se ven también las ventas al exterior por país.

**Pedidos por mes.** Los pedidos de cada mes y moneda, en qué estado están hoy (pendientes, aceptados, completados y
cancelados) y lo que facturó su factura inicial. Un pedido de total cero no tiene factura y suma cero.

**Permanencia de los clientes.** Al día de hoy, de las cuentas que contrataron algo: desde el primer servicio que se
activó hasta hoy si siguen con alguno activo o suspendido, o hasta la última baja si se fueron, en meses enteros. Por
tramos (menos de 3 meses, de 3 a 6, de 6 a 12, de 1 a 2 años, de 2 a 3 y más de 3) con la mediana, y por año de la
primera alta con cuántas siguen. Una cuenta que se fue con bajas sin fecha no se puede medir y sale en un aviso.

**Servicios por producto y servidor.** Al día de hoy:
- Las cuentas por estado, con cuántas tienen algún servicio activo o suspendido.
- Los servicios vivos por producto y por servidor. Servidor es la instancia de módulo que aprovisiona el servicio
  (un cluster de Proxmox, un panel de Hestia) y "Sin servidor" junta los manuales y los que todavía no tienen una.
  Columnas: cuentas con algún servicio activo o suspendido, activos, suspendidos, por activar (pendientes, en
  preparación o con falla) y el ingreso recurrente mensual de los activos, igual que en el reporte de recurrente.
- Los totales del pie son por moneda y cuentan cada cuenta una vez, aunque tenga varios productos: salen de la base
  con `GROUPING SETS`, no de sumar las filas.

**Uso de cupones.** Cada cupón con su tipo y valor, los usos que lleva y su tope, los pedidos y las cuentas que lo
usaron en el período y los servicios activos o suspendidos que lo tienen hoy, de mayor a menor uso.

**Afiliados.** Por afiliado, su código y estado, las visitas que trajo y las cuentas que se registraron por él en el
período. Aparte, por moneda, las comisiones generadas en el período sin lo revertido, las que hoy esperan su plazo y lo
que se le pagó en el período.

**Dominios por registrador y vencimiento.** Al día de hoy: cuántos dominios hay por estado y cuántos se renuevan solos,
los vivos (pendientes, activos y en gracia) por registrador y TLD con los que vencen en 30 y en 90 días y lo que facturan
por año los activos (uno de dos años cuenta la mitad), y la lista de los activos o en gracia que vencen en los próximos
90 días o ya vencieron, con los días que faltan. El TLD es lo que sigue al primer punto del nombre.

**Avisos de vencimiento de dominios.** Los avisos de dominio por vencer y de dominio vencido que se armaron en el
período, por mes y por cómo quedaron en la cola de correo (enviados, en la cola, sin enviar porque el envío está apagado
o la traba de producción no lo deja, y fallidos), y el detalle con el destinatario y el asunto, que nombra el dominio.

**Tickets por departamento y tiempos de respuesta.** De los tickets abiertos en el período, por departamento y por
mes: abiertos, con respuesta, sin respuesta, cerrados, la primera respuesta (mediana y promedio) y la resolución
(mediana), en horas corridas.
- La primera respuesta es el primer mensaje del staff que ve el cliente. Una nota interna no es una respuesta.
- Solo cuentan para la respuesta los tickets que abrió el cliente: uno que abre el staff no espera respuesta.
- Sin respuesta es un ticket del cliente sin mensaje del staff que no está cerrado.
- La resolución va del alta al cierre de los que están cerrados. Uno que se reabrió no está cerrado.
- El total del pie tiene la mediana y el promedio de todos los tickets, no la suma de las filas.

**Respuestas del staff.** Por persona del staff, los mensajes que ve el cliente, en cuántos tickets y las notas
internas del período. Un mensaje importado sin persona va con el nombre que firmó. El pie no suma los tickets: uno con
dos personas se contaría dos veces.

**Satisfacción de los clientes.** De la encuesta de cada ticket, del 0 al 10, por persona del staff que atendía el
ticket al cerrarse: cuántas respuestas, el promedio, cuántas bajas (0 a 6), medias (7 y 8) y altas (9 y 10) y el índice
neto, que es la parte de altas menos la de bajas, de -100 a 100. El pie tiene el promedio y el índice de todas. Debajo,
los comentarios con su ticket, del más nuevo al más viejo.

El reporte de tickets lee las tablas del módulo de soporte (`support.departments`, `support.tickets` y `support.messages`, de la
migración `Soporte`). Es el único reporte de soporte: el resumen del soporte (`/admin/soporte/resumen`) muestra la última
semana con la misma definición de la primera respuesta y enlaza acá. La prueba
`Soporte_PorDepartamentoYMes_PrimeraRespuestaSinNotasInternasYResolucion` corre contra las tablas de verdad.

**Listados.** Para exportar en CSV, una fila por cosa, con los mismos criterios que los reportes:
- Clientes: todas las cuentas con su identidad fiscal vigente, estado, alta, moneda y cuántos servicios tienen activos
  o suspendidos.
- Servicios: los vivos (por activar, activos, suspendidos y con falla) con su cuenta, producto, estado, ciclo, precio
  por ciclo con descuento, ingreso recurrente, próximo vencimiento, hasta dónde están pagos y servidor.
- Facturas: las facturas y notas emitidas en el período con su comprobante, lo acreditado, lo pagado, lo que falta y el
  estado, que sale de los importes como en el resto del sistema. Las notas de crédito van en negativo, así la columna
  del total suma lo facturado.
- Cobros: los pagos del período en todos sus estados, con el canal como en los cobros por medio, el ID en la pasarela,
  la comisión, lo devuelto y las facturas a las que están imputados hoy. El pie suma solo lo cobrado, sin pendientes
  ni rechazados.

En pantalla van las primeras mil filas y el CSV lleva todas.

## PDF de facturas por lote

La página de los PDF por lote muestra, para un mes, las facturas y notas emitidas por tipo, cuántas tienen su PDF y
cuántas todavía no, y baja un ZIP con los PDF guardados, en el orden de su fecha y su número. El PDF se arma una vez,
cuando el documento tiene su comprobante fiscal (D-05), así que uno sin comprobante todavía no está y sale en un aviso.
Si dos documentos tienen el mismo nombre de archivo, el segundo va con su número adelante. Cada descarga queda en la
auditoría como las demás.

El ZIP se arma en memoria: alcanza para cientos de comprobantes por mes. Con miles, conviene escribirlo
directo a la respuesta.

## Tablas de otros módulos

Algunos reportes leen tablas de módulos de otras ramas. Mientras no están, la pantalla lo avisa y sale vacía, y se
completa sola cuando llegan. Las de dominios y de los PDF ya están, con la migración `Integracion`:

| Reporte | Tablas | Módulo |
|---|---|---|
| Tickets por departamento, actividad diaria (tickets) | `support.departments`, `support.tickets`, `support.messages` | Soporte (migración `Soporte`) |
| Respuestas del staff | las de soporte y la columna `support.messages.staff_id` | Soporte (migración `Soporte`) |
| Satisfacción de los clientes | `support.feedback` y `support.tickets` | Soporte (migración `Soporte`) |
| Uso de cupones | `catalog.coupons` y las columnas `coupon_id` de pedidos y servicios | Catálogo |
| Afiliados | `affiliates.affiliates`, `clicks`, `referrals`, `commissions` y `payouts` | Afiliados |
| Dominios por registrador y vencimiento | `domains.domains` | Dominios (migración `Integracion`) |
| PDF de facturas por lote | `billing.document_pdfs` | PDF fiscal (migración `Integracion`) |

Las pruebas arman esas tablas con las columnas de su migración si no están, y con el módulo mezclado corren contra las
tablas de verdad.

## Libro IVA Digital (RG 4597)

La página del Libro IVA Digital arma los dos archivos de ventas de un mes con los diseños de registro de ARCA:
`LIBRO_IVA_DIGITAL_VENTAS_CBTE_aaaamm.txt`, de 266 posiciones por comprobante, y
`LIBRO_IVA_DIGITAL_VENTAS_ALICUOTAS_aaaamm.txt`, de 62 posiciones por alícuota. Van en ISO 8859-1, con CRLF al final
de cada registro y los dos en el mismo orden. Arriba, un resumen por tipo de comprobante y moneda y los avisos.

- Cada comprobante autorizado del mes es un registro, también los importados: un documento importado puede tener la
  factura y la nota que la anuló en el sistema anterior, y van las dos con sus propios importes.
- Los importes van en la moneda del comprobante con su cotización de seis decimales. Al importar en ARCA se cargan
  los dos archivos juntos y se elige "Los importes están expresados en la Moneda Original del Comprobante".
- El comprador va con el tipo y el número de documento que se informó. Al consumidor final sin identificar le
  corresponden el código 99, el número en cero y "CONSUMIDOR FINAL". En una exportación va la CUIT genérica del país
  con el código 80.
- Los otros tributos van a su campo según el código de ARCA: 13 a percepción a no categorizados, 1 y 6 a
  nacionales, 2, 5 y 7 a ingresos brutos, 3 y 8 a municipales, 4 a internos y el resto a otros tributos.
- Alícuotas con los códigos de WSFEv1: 0 % es 3, 2,5 % es 9, 5 % es 8, 10,5 % es 4, 21 % es 5 y 27 % es 6. Un
  comprobante sin IVA lleva un registro de alícuota cero, y el código de operación dice por qué: X exportación, E
  exento y N no gravado. Con IVA el código va en blanco.
- La fecha de vencimiento del pago va en ceros: solo la piden las liquidaciones de servicios públicos.
- Los textos que no entran en ISO 8859-1 salen con "?", de a un carácter, así cada registro mide lo mismo.
- Un comprobante con una alícuota que no está en la tabla queda afuera, con su aviso. Uno en moneda extranjera sin
  cotización va con cotización cero y su aviso, para completarla en ARCA.
- La página avisa también cuántos documentos del mes todavía no tienen comprobante: el libro está incompleto hasta
  que se emitan.

Las pruebas `LibroIvaDigitalTests` revisan el formato campo por campo, y `LibroIva_ConElComprobanteQueAutorizoElFisco`
lo arma con el comprobante que autoriza el módulo fiscal falso.

## CSV

Cada tabla se baja en CSV (RFC 4180) con el botón de su encabezado:

- UTF-8 con BOM, para que las planillas tomen los acentos, coma como separador y CRLF al final de cada fila.
- Encabezados en el idioma del pedido y valores invariables: importes con punto y dos decimales, fechas ISO, meses
  como `2026-10`. Las alícuotas y los porcentajes van en porcentaje, con "(%)" en el encabezado, y las horas con
  "(h)".
- Sin la fila de totales, así la planilla suma sin contar dos veces.
- Un texto que empieza como fórmula (`=`, `+`, `-`, `@`) lleva un apóstrofo adelante: el nombre de una cuenta lo
  escribe el cliente.
- El archivo se llama `{reporte}-{tabla}-{período}.csv`.

## Gráficos

Cada reporte declara sus gráficos sobre sus propias tablas (`ReportChart`), así un gráfico nunca dice otra cosa que la
tabla de abajo, que es además su equivalente en texto.

- Por mes van columnas, con todos los meses del período: uno sin filas va en cero, así el tiempo no se achica. Con
  más de doce meses el nombre va solo en los que abren el trimestre, el semestre o el año.
- Por día, lo mismo con los días: hasta un mes va cada día, hasta tres uno por semana y con más el primero de cada mes,
  con el mes debajo cuando cambia.
- El resto va en barras horizontales de mayor a menor, con las doce mayores. Las demás quedan en la tabla, con un
  aviso.
- Los importes van en un gráfico por moneda. Un gráfico que tendría una barra sola o todas en cero no se muestra.
- Un solo color, el acento del tema, barras finas con el extremo de los datos redondeado, grilla de un pelo y
  textos con los colores del texto. Cada columna muestra su mes y su valor al pasar el puntero.
- SVG con atributos y sin JavaScript: el CSP del sitio no deja estilos en línea.

| Reporte | Gráficos |
|---|---|
| Ingresos por mes | Total facturado por mes |
| Ingresos por producto | Neto por producto |
| Ingresos por año | Neto por año |
| Clientes que más facturan | Neto por cuenta |
| Proyección de ingresos | Facturación prevista por mes y por servidor |
| Actividad diaria | Cobrado por día y pedidos por día |
| IVA por alícuota | IVA por mes |
| Impuestos | Impuestos por mes, en pesos |
| Deuda | Deuda por antigüedad |
| Servicios suspendidos | Ingreso recurrente suspendido por producto |
| Recurrente | Ingreso recurrente al cierre de cada mes |
| Permanencia de los clientes | Clientes por tramo y los que siguen por año de la primera alta |
| Cobros | Cobrado neto por medio |
| Movimientos de saldo a favor | Saldo a favor por cuenta |
| Altas de clientes | Altas de clientes por mes |
| Servicios por producto y servidor | Servicios activos por producto y por servidor |
| Dominios por registrador y vencimiento | Dominios por registrador y por TLD |
| Avisos de vencimiento de dominios | Avisos por mes |
| Clientes por país | Cuentas con servicios y neto por país |
| Pedidos por mes | Pedidos por mes |
| Soporte | Tickets abiertos por departamento y por mes, y la primera respuesta por departamento |
| Respuestas del staff | Respuestas por persona |
| Satisfacción de los clientes | Respuestas por puntaje y promedio por persona |
| Uso de cupones | Pedidos con cupón |
| Afiliados | Cuentas por afiliado y comisiones del período |

## Permisos y auditoría

Cada reporte tiene su permiso, que deja verlo y bajar sus archivos, así se puede dar solo una parte: al contador los de
impuestos, a soporte el de tickets. `reports.read` alcanza para todos. Sin el permiso, el menú y el índice no ofrecen ese
reporte y su dirección da 403. Un grupo sin reportes para ese staff no aparece, y sin ninguno no aparece Reportes.

| Permiso | Reporte |
|---|---|
| `reports.revenue` | Ingresos por mes |
| `reports.revenue-by-product` | Ingresos por producto |
| `reports.revenue-by-year` | Ingresos por año |
| `reports.forecast` | Proyección de ingresos |
| `reports.daily-activity` | Actividad diaria |
| `reports.top-accounts` | Clientes que más facturan |
| `reports.vat-book` | Libro IVA Ventas |
| `reports.sales` | IVA por alícuota |
| `reports.taxes` | Impuestos y percepciones |
| `reports.aging` | Deuda por antigüedad y Clientes con deuda |
| `reports.suspended` | Servicios suspendidos |
| `reports.statement` | Estado de cuenta, también el enlace de la ficha del cliente |
| `reports.recurring` | Ingreso recurrente y bajas |
| `reports.retention` | Permanencia de los clientes |
| `reports.collections` | Cobros por medio |
| `reports.credit` | Movimientos de saldo a favor |
| `reports.new-accounts` | Altas de clientes |
| `reports.services` | Servicios por producto y servidor |
| `reports.countries` | Clientes por país |
| `reports.orders` | Pedidos por mes |
| `reports.coupons` | Uso de cupones |
| `reports.affiliates` | Afiliados |
| `reports.domains` | Dominios por registrador y vencimiento |
| `reports.domain-notices` | Avisos de vencimiento de dominios |
| `reports.support` | Tickets por departamento y tiempos de respuesta |
| `reports.staff-replies` | Respuestas del staff |
| `reports.satisfaction` | Satisfacción de los clientes |
| `reports.account-list` | Listado de clientes |
| `reports.service-list` | Listado de servicios |
| `reports.invoice-list` | Listado de facturas |
| `reports.payment-list` | Listado de cobros |
| `reports.invoice-pdfs` | PDF de facturas por lote |

Los permisos se dan al crear el staff (`staff add correo "Nombre" reports.taxes,reports.vat-book`).
Cada descarga, de un CSV o de un archivo del libro, queda en `platform.audit_events` con la acción `report.export`, la
entidad `report:{reporte}` y la tabla o el archivo con su período en el detalle: los detalles llevan nombres y CUIT
de clientes.

## Varios nodos y réplicas

Los reportes leen del contexto de réplicas (`ConnectionStrings:Replica`, ver
[configuracion.md](../alta-disponibilidad/configuracion.md)) cuando hay una: toleran datos apenas viejos y no cambian
nada. Agregan en la base y no guardan nada en memoria entre pedidos. Son el módulo de solo lectura que lee los
esquemas de los demás (D-03).

## Pendiente

- Importar en ARCA un mes real del libro durante el mes en paralelo, con el contador, antes del corte.
- Horas hábiles en los tiempos de soporte, si el proveedor carga su horario de atención.
- El Libro IVA de compras no está: el sistema no registra compras.
- Al mezclar el catálogo: el descuento fijo de un servicio (`discount_amount`) en el ingreso recurrente, la proyección y
  el listado de servicios, que hoy toman solo el porcentaje.
- Al mezclar los dominios: las renovaciones de dominios en la proyección de ingresos.
- Fuera por ahora, porque el sistema no tiene los datos o todavía no hacen falta: el uso de disco y de tráfico por servidor
  (falta que los módulos informen el uso), los débitos directos (no hay débito bancario), el origen de los clientes
  (falta el campo, que llega con los campos personalizados), las etiquetas de tickets, el IVA
  de servicios digitales de otros países y el control de certificados de los
  dominios de los clientes, que es tarea del monitoreo.
