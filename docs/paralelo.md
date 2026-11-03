# Mes en paralelo

**Estado:** v1, 2026-10-31 · **Diseño:** [importación](../diseno/importacion.md)

Durante el mes en paralelo, el sistema anterior sigue facturando y Hornero corre el mismo día sobre una copia,
sin efectos afuera. Todos los días se comparan los dos resultados. El mes termina cuando no quedan defectos y lo que
quedó para revisar está decidido.

Nunca emite comprobantes, cobra, manda correos ni llama a un módulo o a una pasarela: el día corre dentro del
importador, sin el archivo de llaves de secretos, así que el núcleo no tiene cómo (D-09). El emisor importado queda
inactivo y el correo queda con sus trabas apagadas (`Notifications:Send` y `Notifications:ProductionEnabled`). Cada
operación de un servicio (alta, suspensión, reactivación o baja) se da por confirmada en el acto, como si el módulo
o el staff respondiera, así el estado del servicio avanza.

## La corrida diaria

El comando `daily` del importador hace el día completo. Lo corre un trabajo programado del
entorno de prueba y también se puede correr a mano:

```bash
export BILLINGOSS_IMPORT_PG=...   # o BILLINGOSS_IMPORT_PG_FILE con la ruta de un archivo: nunca en la línea de comandos
BillingOss.Importer daily --day 2026-11-24 --before 2026-11-24-previo.sql.gz --after 2026-11-24-posterior.sql.gz \
    --modules modulos.json --history historial
```

Hacen falta dos volcados del origen por día: el previo, antes del ciclo diario del origen, y el posterior,
cuando el ciclo terminó. Con un ciclo a las 00:00, por ejemplo a las 23:50 y a las 00:50.

1. **Línea base.** Cuenta el volcado previo directo, sin las reglas del importador (comando `baseline`).
2. **Importación.** Borra y vuelve a crear la base importada (`--base`, por defecto `billing_oss_paralelo`) e importa
   el volcado previo en modo sombra con el mapa de módulos, como día de la copia el anterior al simulado. Concilia
   contra la línea base del paso 1.
3. **El día.** Clona la base importada a la de trabajo (la importada con `_dia` al final), corre la facturación y la
   mora del día con los ajustes del origen, más la anulación de impagas a los 7 días, que en algunos orígenes es
   código propio y no un ajuste. Después repite los cobros con pasarela que el origen registró y vuelve a correr los
   trabajos, así se prueba también el camino de cobro, imputación y reactivación.
4. **Comparación.** Contra el volcado posterior, con cada diferencia en una clase: esperada, a revisar o defecto.
5. **Informe e historial.** Deja todo en `<historial>/<día>/` y suma una línea a `<historial>/historial.tsv`.

| Archivo | Qué tiene |
|---|---|
| `linea-base.tsv` | La línea base del volcado previo |
| `importacion.txt` | Incidencias de la importación y la conciliación, sección por sección |
| `dia.txt` | Ajustes del origen que se usaron, renovaciones de cada lado y lo facturado en renovaciones por moneda |
| `diferencias.tsv` | Todas las diferencias, primero los defectos, después las que hay que revisar y al final las esperadas |
| `resumen.txt` | Cuántas hay de cada clase y tipo, los primeros 50 defectos y todas las que hay que revisar |
| `historial.tsv` | Una línea por día: día, hora de la corrida, versión, conciliación, esperadas, a revisar, defectos y salida |

La versión es la del importador con su commit, así se sabe con qué código corrió cada día. Sale con 0 sin defectos y
con la importación conciliada, con 3 si hay defectos o diferencias de conciliación y con 1 o 2 si no pudo correr. Un
día que no pudo correr también deja su línea en el historial, con la conciliación en `falló`.

Los informes llevan solo IDs, fechas, importes y estados. No llevan nombres, correos, dominios ni descripciones.

Los comandos sueltos siguen sirviendo para repetir un paso: `baseline` saca la línea base de un volcado, `import`
importa (con `--shadow-instances` y `--as-of` hace lo mismo que el paso 2) y `parallel` corre los pasos 3 y 4 sobre
una base ya importada.

| Opción de `daily` | Para qué |
|---|---|
| `--day` | Día que se simula, en la zona horaria del proveedor. El reloj queda en las 00:05 de ese día |
| `--before` y `--after` | Los volcados previo y posterior, en `.sql` o `.sql.gz` |
| `--modules` | El mapa de módulos. Es obligatorio: sin él, los servicios con módulo quedan sin instancia y sus operaciones fallan |
| `--history` | Carpeta de los informes y del historial |
| `--base` | Base importada. Tiene que empezar con `billing_oss_` |
| `--cancel-unpaid-days` | Días para anular impagas. Por defecto 7. `none` la apaga |
| `--own-zones` | Dominios propios: sus subdominios van a la instancia de subdominios propios |

## Mapa de módulos del paralelo

El mapa (`--modules`) ata cada servicio vivo a una instancia de módulo, con el formato de la sección 3 del
[diseño de la importación](../diseno/importacion.md). El comando `daily` importa siempre en modo sombra: cada
instancia que nombra el mapa se crea sin URL, así el servicio queda atado y su operación nunca puede llegar a un
módulo. Si una instancia con ese nombre ya existe con URL, la importación se frena. Con `params` vacío no se copia
ningún `configoptionN` del producto, porque algunos guardan una clave (por ejemplo la API key de Tactical RMM).

```json
{
  "pterodactyl": { "instance": "juegos", "params": {} },
  "proxmox_custom": { "instance": "cluster", "params": {} },
  "vesta": { "instance": "hosting", "params": {} },
  "vesta:0": { "manual": true },
  "modulo_propio": { "manual": true },
  "tacticalrmm": { "instance": "tacticalrmm", "params": {} }
}
```

**Cómo se mantiene.** Antes de arrancar el mes y cuando el origen sume un producto o un servidor:

1. `BillingOss.Importer distinct <volcado> tblproducts servertype` y `distinct <volcado> tblhosting server domainstatus`
   dicen qué hay. Cada `servertype` va en el mapa, y un servidor que no es el de siempre va con su clave
   `servertype:servidor`.
2. La importación del día no tiene que dejar ninguna incidencia "quedan sin instancia de módulo" ni "atados a una sola
   instancia". Si aparece, se corrige el mapa, no el importador.

**No es el mapa del corte.** En el corte cada entrada lleva sus `params` y sus `external_ids` según la sección de
migración del README de su módulo, y las instancias se dan de alta antes, con su URL y sus secretos. Algunas
conversiones que piden esos README no entran en el formato del mapa (por ejemplo la velocidad de Proxmox en Mbps dividida por 8 o los puertos adicionales de Pterodactyl).

## Bajas que el origen no ejecutó

Un servicio vivo cuya baja ya vencía en el volcado y el origen no la hizo no se da de baja solo el primer día. El
importador le abre una tarea de revisión del staff y, mientras está abierta, la mora no lo da de baja. Así el paralelo
queda alineado con el origen y el corte no ejecuta ninguna baja de estas sin que alguien la mire.

| Caso | Qué hace el importador |
|---|---|
| Baja a fin de período pedida para un día que ya pasó | Queda con su fecha de baja (`CancelAt`) y con la revisión |
| Baja inmediata pedida y no ejecutada | Queda en su estado, porque el servidor sigue andando, con `CancelAt` en el día del pedido y con la revisión. Antes se importaba dada de baja sin avisar a nadie |
| Baja por mora vencida (lo pagado más el plazo de baja ya pasó) | Queda en su estado y con la revisión |
| Baja a fin de período para un día que no llegó | Solo `CancelAt`: ese día la mora lo da de baja, como el origen |

La revisión es una tarea del staff con `kind` igual a `review` y sujeto `review:service:{id}`, en la cola del módulo
manual. Se ve en `GET /api/v1/staff-tasks` con el motivo, el estado en el origen y las instrucciones:

- **Completarla** deja que Hornero siga sus reglas en el acto: la mora corre enseguida y, si la baja sigue
  correspondiendo, la hace.
- **Rechazarla** deja sin efecto la baja pedida. Una baja por mora no se rechaza: se registra el pago y se completa.

En la comparación, una baja retenida que el origen hizo en el día es esperada.

## Qué compara

| Qué | Origen | Hornero |
|---|---|---|
| Renovaciones | Renglones de servicio de las facturas nuevas que no son de un pedido | Períodos facturados de los documentos nuevos |
| Promoción recurrente | Renglón del servicio a precio pleno más uno `PromoHosting` negativo | Un solo renglón con el precio neto. Se compara el renglón del origen neto de su promoción |
| Renovaciones de dominios | Renglones de dominio de las facturas nuevas, con el período desde su vencimiento | Renglones de renovación de dominio de los documentos nuevos, con el inicio que lleva su clave |
| Factura de cada renovación | Cuenta, fecha, vencimiento, bruto, IVA y saldo a favor aplicado | Lo mismo del documento propio |
| Anulaciones | Facturas abiertas en la base que el origen pasó a anuladas | Notas de crédito del día |
| Estado de los servicios | Activo, suspendido o baja en el volcado posterior | Estado después de la mora y de los cobros |
| Pagado hasta | Próximo vencimiento del volcado posterior | Hasta dónde está pago cada servicio |
| Estado de los dominios | Activo, en gracia o vencido en el volcado posterior | Estado después de la corrida, que pasa a gracia y a vencidos los impagos |
| Pagado hasta de los dominios | Próximo vencimiento del dominio en el volcado posterior | Hasta dónde está pago cada dominio |
| Cobros | Estado de la factura cobrada en el volcado posterior | Estado del documento después de repetir el cobro |

Las renovaciones se emparejan por servicio (o dominio) e inicio del período, nunca por número de factura, porque
cada sistema numera por su cuenta. Las claves pueden repetirse y se emparejan como multiconjunto. Después se agrupan
por factura: si los dos lados agrupan las mismas renovaciones se compara la cabecera, y si no, es un defecto de
agrupación. El fin del período del origen sale del rango que escribe en el renglón, con el último día incluido. Los
dominios se renuevan con la anticipación del origen (`CreateDomainInvoiceDaysBefore`), y una renovación de precio
cero del origen sale como faltante, porque Hornero la avanza sin factura. `dia.txt` suma además lo facturado en
renovaciones por moneda de cada lado.

Los cobros del día se repiten con la moneda que decide el importador: un cobro sin moneda por diez veces o más el
bruto de una factura en otra moneda son pesos de una pasarela local. Si la moneda no es la de la factura, se repite
lo que la factura debía, en su moneda, como lo imputa la importación, porque Hornero todavía no convierte cobros
(sección 8 del [modelo de dominio](../diseno/dominio.md)).

## Clasificación

Cada regla está en el comparador y tiene su prueba. Nada se decide a mano.

**Esperadas.** Se listan y no cuentan:

- **Redondeo del IVA.** Mismo total y hasta un centavo de IVA distinto: Hornero redondea por factura y alícuota
  (D-04).
- **Entradas del día.** Facturas de pedidos, de cambios de plan, de cargas de saldo y manuales, cobros de facturas
  que Hornero no tiene, pedidos de baja nuevos y las anulaciones que esos pedidos hicieron en el origen. Nacen de
  una acción del cliente o del staff, no de la corrida.
- **Baja retenida que el origen hizo.** Hornero la tiene en una revisión del staff y el origen la ejecutó en el día.
- **Regla propia de los dominios.** Hornero pasa a gracia un dominio impago el día después de lo pago y lo da por
  vencido pasada la gracia (45 días), porque ningún registrar sincroniza todavía. El origen cambia el estado por su
  cuenta. Es esperada solo el día en que vence: desde el día siguiente es una acción vencida, para revisar.
- **Servicio dado de alta el mismo día.** Solo aparece si la base importada salió de un volcado posterior al ciclo:
  el origen lo renueva en su ciclo siguiente.
- **Servicio o dominio que no estaba en la base importada.** El origen renovó algo que Hornero no tiene.

**A revisar.** Tampoco cuentan como defecto, pero alguien las tiene que mirar antes del corte, porque ese día
Hornero las va a hacer:

- **Acción vencida en el origen.** Una suspensión, una anulación o el paso de un dominio a gracia o a vencido que
  vencía antes del día simulado y el origen no hizo. Hornero la pone al día en su primera corrida. Si la factura
  está en gestión de cobro (`Collections`), el motivo lo dice.

**Defectos.** Todo lo demás: renovación de más o faltante, importe, fin del período, total, IVA, vencimiento, fecha
de emisión, cuenta, saldo a favor aplicado, agrupación, anulación distinta, estado distinto de un servicio o de un
dominio (también el que el origen cambió por su cuenta), pagado hasta distinto, cobro que quedó en otro estado,
renovación de dominio de más o faltante y cualquier trabajo del núcleo que haya fallado en la simulación.

## Conciliación de la importación de cada día

La línea base de cada día sale del volcado previo, contada directo y sin las reglas del importador. Así la
conciliación ve lo que la importación perdió o contó distinto, también en las cifras que la línea base de la copia
del servidor no traía:

| Sección | Qué cuenta |
|---|---|
| `payments_by_type` | Movimientos con entrada de dinero por tipo, incluidos los que no son cobros (contrapartida de notas nativas y saldo aplicado) |
| `billing_notes_by_type_currency` | Notas nativas de crédito y de débito por moneda, con su total. Las que el origen abrió por un movimiento de saldo (aplicado o quitado de una factura) van aparte, con el tipo del movimiento: no son documentos |
| `cancel_requests_pending_by_type` | Pedidos de baja sin ejecutar de los servicios vivos, el último de cada uno, por tipo |
| `services_alive_by_due_month` | Servicios activos y suspendidos por mes de `nextduedate`, con su importe |
| `services_billed_unpaid` | Vivos recurrentes con el período que empieza en `nextduedate` ya facturado sin cobrar, por estado |

En las secciones que trae la línea base, una clave que solo tiene lo importado también es diferencia, y una sección
que la conciliación no conoce cuenta como diferencia. Con la línea base de la copia del servidor, estas cinco
secciones se muestran como `INFO`.
