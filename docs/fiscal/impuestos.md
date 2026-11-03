# Configuración impositiva

**Estado:** v0, 2026-10-29 · **Diseño:** sección 3 de [fiscal-impuestos-y-cobros.md](../../diseno/fiscal-impuestos-y-cobros.md) (D-21) · **Código:**
`src/BillingOss.Domain/Catalog/Taxes.cs`, `src/BillingOss.Domain/Billing/TaxEngine.cs`,
`src/BillingOss.App/UseCases/Taxes.cs` y `src/BillingOss.Web/Pages/Admin/Taxes/`

La sección Impuestos del admin (`/admin/impuestos`, permiso `taxes.edit`) deja configurar todo lo impositivo sin un
programador al lado y sin poder equivocarse en lo legal: lo que la ley fija para el emisor configurado queda
bloqueado y explicado. Todo lo que cambia queda en la auditoría.

## Modelo

| Pieza | Qué es |
|---|---|
| Categoría (`TaxCategory`) | Qué se vende, a efectos del impuesto. Va en cada producto. De fábrica: servicios digitales y hosting (la alícuota general), telecomunicaciones (27 % a responsables inscriptos y monotributistas fuera de una vivienda), exento por ley y no gravado. El proveedor crea otras |
| Impuesto (`Tax`) | Un renglón del desglose: nombre, nivel (por renglón o sobre el comprobante), base (neto, neto más IVA o total), código en la tabla de tributos de ARCA, si suma al total o solo se informa. De fábrica: IVA, percepción de IVA RG 2126 (código 13) y la leyenda ISIB CABA |
| Regla (`TaxRule`) | Impuesto, país y subdivisión ISO 3166-2 del receptor, condición frente al IVA, si es empresa con número validado, categoría, cuenta, resultado, alícuota, motivo, leyenda y vigencia. Gana la más específica (cuenta 16, categoría 8, condición o empresa 4, subdivisión 2 y país 1). Sin ninguna, o con empate, el cálculo falla y nunca asume cero. Por eso una regla nueva con los mismos filtros que una vigente en fechas que se solapan no entra: primero se cierra la vigente |
| Regla de cuenta | Una regla con la cuenta puesta: gana siempre dentro de su alcance, con motivo obligatorio, vigencia, quién la cargó y la constancia adjunta. Cubre la exención con constancia, la exclusión de una percepción y el cliente del exterior que usa el servicio en el país |
| Ajustes (`TaxSettings`) | País del emisor, modo de carga por defecto por moneda, muestra del catálogo, umbral para identificar al consumidor final, mínimo de la factura de crédito electrónica MiPyME y los regímenes de ingresos brutos |
| Modo de carga (`PriceMode`) | En cada precio del plan y congelado en el servicio junto con el precio, y en el dominio junto con el precio de su renovación. Un pedido que mezcla modos sale en una factura por modo, y la renovación arma un borrador por modo. El precio por TLD todavía no lo lleva, porque ningún pedido lo usa: lo suma el pedido de dominios |

Cada renglón del documento congela categoría, alícuota, motivo y leyenda. El documento congela la lista de tributos
de comprobante y la de leyendas (cada tributo con las categorías que lo componen y sobre qué base se calculó, para que
una nota parcial lo reparta igual). Nada se edita: se cierra lo vigente y se abre otro, así un documento viejo siempre
se explica con lo que usó.

**Provincias.** La subdivisión del receptor va como código ISO 3166-2 (`ArgentinaProvinces`: `AR-C` la Ciudad, `AR-B`
Buenos Aires y así). El importador normaliza el texto del origen (nombres con o sin acento, CABA, Capital Federal, Bs
As, Buenos Aires (Provincia) y otros alias) y deja tal cual lo que no reconoce, contado en `provinces_unmapped` (solo la cantidad, sin el texto) y con
una incidencia. La ficha del admin y la del portal eligen de la lista, "Completar con ARCA" traduce la provincia del
padrón, y `AccountService` rechaza un texto que no sea de la tabla. El aviso `provincia_fuera_de_tabla` cuenta las
cuentas argentinas que quedaron con texto libre: ninguna regla por provincia ni régimen de ingresos brutos las alcanza.

**Filas anteriores a la migración.** `FiscalSql.Backfill` deja los servicios, precios y dominios que ya existían con
IVA incluido (el modo global de entonces). El emisor ya llega como responsable inscripto con el valor por defecto de la
migración Integracion. En la migración `ImpuestosArca`, `FiscalSql.ProductsToCategories` corre entre el alta de
`tax_category` y la baja de `tax_treatment`, y `FiscalSql.Seed`, `FiscalSql.Backfill` y la función de métricas van
después de las tablas y columnas nuevas. Si se regenera, ese orden se rehace a mano.

### Cómo se resuelve un renglón

1. El receptor congelado del documento y su evidencia (número de IVA validado).
2. La fecha fiscal: la de emisión del documento.
3. La categoría del renglón: la del producto. La renovación de un dominio, que no tiene producto, va como servicio
   digital.
4. El IVA: la regla más específica que coincide y está vigente. Un emisor monotributista o exento no cobra IVA, con el
   motivo `monotributo`. Una categoría exenta o no gravada no pasa por las reglas.
5. El cálculo por comprobante y alícuota (`TaxCalculator`), con el modo del precio.
6. Los impuestos de comprobante (percepciones) sobre ese resultado, cada uno con su regla. Sin regla no corresponden.
7. La clase de comprobante (A, B, C o E) y las leyendas.

Motivos estables (`TaxReasons`): `gravado`, `exportacion`, `exento_por_ley`, `no_gravado`, `inversion_sujeto_pasivo`,
`sin_registro`, `regla_de_cuenta`, `monotributo`, `percepcion` y `solo_se_informa`. El panel, la factura y la API los
muestran en castellano llano.

## Las páginas

| Página | Ruta | Qué tiene |
|---|---|---|
| Así cobrás hoy | `/admin/impuestos` | El simulador con los clientes tipo del diseño (consumidor final, responsable inscripto, telefonía, no categorizado y empresa del exterior), uno a medida por país, provincia, condición, categoría, precio, moneda y modo, y los avisos con un enlace para arreglarlos |
| Empezar | `/admin/impuestos/empezar` | Tres preguntas: país y condición del emisor y dónde vende (exterior, consumidores de la UE, del Reino Unido), más si percibe IVA a no categorizados y si paga ingresos brutos en CABA. Cada respuesta deja un preset completo. Cierra hoy las reglas generales vigentes, también las que tenían fin para más adelante, y abre las nuevas. Las reglas de cuenta y las de los regímenes de ingresos brutos quedan |
| Categorías y reglas | `/admin/impuestos/reglas` | Categorías con cuántos productos las usan, impuestos, reglas con filtros por impuesto, país, categoría y receptor con la columna "por qué", agregar y cerrar, y las percepciones: el estado de la RG 2126 y los regímenes de ingresos brutos |
| Precios y muestra | `/admin/impuestos/precios` | Modo de carga por defecto por moneda, qué ve el cliente en cada canal (con lo que fija la Res. 4/2025 SIC bloqueado y explicado), el redondeo y los umbrales legales |

En la ficha de cada cuenta (`/admin/cuentas/{id}`, bloque "Datos fiscales"): la condición frente al IVA con las
descripciones de la tabla de ARCA, "Completar con ARCA" desde la CUIT (sugiere la condición y guarda fuente y fecha),
la validación del número de IVA de la UE o del Reino Unido, la marca de empresa grande y las reglas de cuenta con su
constancia. La condición 7 (sujeto no categorizado) se ofrece y se acepta solo mientras rige una regla general de la
percepción de la RG 2126. Sin ella, `AccountService` rechaza el pase a la 7 con el motivo, en el admin, en el portal y
en la API. Una cuenta que ya la tenía cuando se cerró la percepción se sigue editando, y el aviso de Impuestos la
cuenta hasta que la corrijan. Corregir otro dato de la ficha, también el tipo de documento, abre una versión nueva que
conserva el domicilio (las fichas no lo editan y va en el receptor del comprobante) y hereda la constancia de VIES o
HMRC si el número y el país no cambiaron.

Del lado del cliente, `/cuenta/datos-fiscales` (titular o usuario con la capacidad de facturación) muestra y edita lo
mismo para cada cuenta: documento, razón social, condición (sin la 7 si no hay percepción), país, provincia de la
lista, "Completar con ARCA" con la condición a confirmar y la validación del número de IVA del exterior. Cada cambio
queda en la auditoría como `portal.tax_identity`.

## Percepciones

**IVA a no categorizados (RG 2126/2006).** El preset la prende para un emisor responsable inscripto: 10,5 % sobre neto
más IVA en general y 13,5 % en telecomunicaciones, a los receptores con condición 7. Alcanza solo los renglones
gravados: un producto exento o no gravado no entra en la base (arts. 1 y 5 de la RG, y la validación 10067 de WSFEv1
pide el tributo solo cuando neto más IVA es mayor a cero). Viaja a ARCA como tributo 13, con `ImpTrib` y el array de
tributos. Una nota de crédito que la revierte lleva el período asociado del mismo mes. Sin la percepción configurada,
una factura a un no categorizado se frena antes de llamar a ARCA, con el motivo.

**Notas de crédito.** Una nota parcial recalcula cada tributo sobre los renglones de sus categorías, con la misma base
que el original (neto para ingresos brutos, neto más IVA para la RG 2126) y la alícuota del original, con tope en lo
que falta revertir. La que completa el original toma exactamente el resto. Un tributo que la nota no toca no sale. Una
línea sin categoría (la API de reembolsos no la exige) toma la del renglón de su servicio en la factura, o la única
que la factura tenga.

**Ingresos brutos.** Solo si una jurisdicción designa al proveedor como agente. Cada régimen (`GrossIncomeRegime`:
jurisdicción ISO 3166-2, régimen, número de agente, alícuota fija, base, condiciones excluidas y vigencia) crea un
impuesto de comprobante con el código 7 de ARCA y una regla por la subdivisión del receptor, más una exclusión por
cada condición que el régimen deja afuera. Cargarlo de nuevo cierra, al empezar el nuevo, toda regla suya que siga
vigente, también las que ya tenían fin, porque dos vigentes a la vez empatan y el cálculo falla para toda la
jurisdicción. Uno cargado con fecha futura deja percibir al vigente hasta ese día. La base es del impuesto, así que
uno con fecha futura y otra base no entra mientras el vigente siga percibiendo: se carga el día que empieza. Cerrar
el régimen cierra sus reglas, y aplicar de nuevo el preset de Empezar no las toca. El padrón mensual de alícuotas por
CUIT queda para después: hoy la alícuota es fija.

**Leyenda ISIB CABA.** Un impuesto que solo se informa, con regla por `AR-C` y consumidor final desde el 01/01/2027,
que crea el preset cuando el proveedor paga ingresos brutos en la Ciudad.

## Clientes del exterior

- Las prestaciones que se usan en el exterior son exportación de servicios: la regla sin país las deja exentas con
  el motivo `exportacion` y van con letra E por WSFEX.
- Si el cliente del exterior usa el servicio en el país, el staff carga una regla de cuenta con IVA.
- Unión Europea y Reino Unido: el preset deja una regla de IVA por país (alícuotas vigentes al 2026-10-28) y una de
  inversión del sujeto pasivo para empresas con número validado. La validación corre contra VIES por su API REST
  pública (`ViesClient`), con el número de consulta como constancia. Las pruebas usan un VIES falso, y la prueba
  real (`ViesLiveTests`) corre solo con las variables `VIES_LIVE_COUNTRY` y `VIES_LIVE_NUMBER`.

## Frenos antes de llamar al fisco

`EmissionBrakes.Check` rechaza la emisión, con el motivo guardado en la emisión para que el staff corrija:

- Condición frente al IVA del receptor fuera de la tabla de ARCA (la RG 5616/2024 la exige en todo comprobante).
- Factura a un sujeto no categorizado sin la percepción de la RG 2126 (la ficha ya no la acepta sin percepción: el
  caso queda para fichas importadas o percepciones cerradas después). El motivo dice que la factura emitida se anula
  con nota de crédito y se vuelve a emitir con la percepción o la ficha corregidas.
- Cuenta marcada como empresa grande con un importe igual o superior al mínimo de la factura de crédito
  electrónica MiPyME: el comprobante tiene que ser FCE, que el sistema todavía no emite. El mínimo es en pesos: una
  factura en otra moneda se compara con la cotización que informa el fisco, antes de autorizar, y sin cotización
  (cancelación en la misma moneda) también se frena.

## Redondeo y muestra

- El IVA se calcula sobre el total de cada alícuota, con la mitad hacia arriba y el residuo al renglón mayor, como lo
  controla ARCA. El panel no lo deja cambiar para Argentina y lo explica.
- En Argentina el catálogo muestra el precio final y, en letra más chica, el precio sin impuestos nacionales
  (Res. 4/2025 SIC, art. 2). `SetDisplay` rechaza otra muestra para un emisor argentino. Al pagar siempre hay
  desglose, la factura la define su clase, el correo repite la factura y la API da neto, impuestos por tipo y total.
- Con precios con IVA incluido, un cliente exento paga lo mismo y sube el neto del proveedor. Es lo que compara el
  mes en paralelo.

## Pruebas

`TaxEngineTests` (dominio, de tabla), `TaxSetupTests` (presets y su reaplicación, simulador, avisos, frenos, VIES,
reglas de cuenta y su solapamiento, provincias, domicilio que la ficha no edita, ingresos brutos con fecha futura y
otra base, modos de carga, nota parcial sin categoría), `FiscalSetupTests` (alta guiada, pase a producción, y corte y
staging con el emisor del importador), `TaxUiTests` (las pantallas del admin y del portal) y `MigrationBackfillTests`
(las filas anteriores a la migración).
