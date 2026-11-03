# Facturación por uso medido

**Estado:** v0, 2026-10-29 · **Parte de:** [diseño del núcleo](README.md) · **Protocolo:** capacidad `provisioning.usage`
([sección 9.1](protocolo-modulos-v0.md))

Cobra lo que el cliente usa por encima de lo que incluye su plan: disco, transferencia, CPU o cantidad de cosas
(casillas, dispositivos, internos). Sirve, por ejemplo, para la transferencia de una VPS, los
dispositivos de un servicio gestionado, los internos de una central telefónica o el disco de un hosting. Es diseño propio.

El uso lo lee el núcleo del módulo del servicio una vez por día y lo guarda como muestras. Al facturar, resume las
muestras de cada métrica, descuenta lo incluido y cobra el excedente en un renglón aparte, con el detalle de lo
medido. El cliente lo ve en el portal y recibe un aviso cuando se acerca a lo incluido o lo pasa.

---

## 1. Modelo

| Concepto | Qué es | Invariantes |
|---|---|---|
| Métrica de uso (`UsageMetric`) | Por producto: el código de la métrica en el módulo, nombre, unidad, tipo (medidor o contador), cómo se resume el período (máximo, promedio o suma), lo incluido (por mes en lo que se suma, sección 4), el paso de facturación, el momento de cobro, los umbrales de aviso y si está activa | Única por producto y código. El producto tiene un módulo que declara `provisioning.usage` y esa métrica con ese tipo en su manifiesto. Lo incluido es cero o más y el paso es mayor que cero, los dos con 4 decimales como máximo. Un contador siempre se suma |
| Precio de excedente (`UsageMetricPrice`) | Precio por unidad de excedente en una moneda, con vigencia | Único por métrica, moneda y fecha de vigencia. Cero o más, con 6 decimales como el resto de los precios unitarios. Se agrega, no se edita. Se lee en el modo de carga del servicio (con IVA incluido o aparte), como su renovación |
| Muestra (`UsageSample`) | Lectura diaria de una métrica de un servicio: la lectura cruda del módulo, el uso que se le atribuye al día y el cargo que la facturó | Una por servicio, métrica y día. Lectura y uso son cero o más. Una muestra se factura una sola vez |
| Cargo de uso (`UsageCharge`) | El cierre de una ventana de una métrica de un servicio: desde, hasta, lo medido, lo incluido, el excedente, el precio y el renglón que lo cobra | Único por servicio, métrica y fecha de corte. Sin excedente se guarda igual, sin renglón, para cerrar la ventana |

Las muestras y los cargos se identifican por servicio y código de métrica, no por la fila de la métrica: si el
servicio cambia a otro producto con la misma métrica, la ventana sigue.

### 1.1 Medidores y contadores

El módulo declara el tipo de cada métrica en su manifiesto:

- **Medidor (`gauge`):** un nivel, como el disco ocupado o la cantidad de casillas. El uso del día es la lectura.
  El período se resume con el máximo (por defecto) o con el promedio de sus muestras.
- **Contador (`counter`):** un acumulado que solo sube, como los bytes transferidos o los segundos de CPU, y que
  puede volver a cero (la VM se reinició o el panel corta por mes). El uso del día es la diferencia con la lectura
  anterior. Si la lectura bajó, el contador volvió a cero y el uso es la lectura entera. La primera lectura de un
  servicio es la base y vale cero: así un servicio importado con un contador de meses no se cobra entero. También
  es base la primera después de que la métrica vuelve a estar activa, así lo que subió mientras no se medía no se
  cobra. El período se resume con la suma.

Con contadores, las diferencias se encadenan: la suma de los usos de todas las muestras es lo que subió el
contador. Un día que no se pudo leer no se pierde, porque la lectura siguiente lo incluye.

## 2. Lectura desde el módulo

Capacidad nueva del protocolo, `provisioning.usage`, con la operación `usage`. Es una lectura sin efecto, así que
va sin `Idempotency-Key`, como `get_status`. El manifiesto declara las métricas en `usage_metrics`, con su `id`, su
`kind` (`gauge` o `counter`), su `unit` y su `label` por idioma. La entrada lleva los códigos que quiere el núcleo
y la respuesta trae cada lectura como texto decimal, igual que el dinero. El detalle está en la sección 9.1 del
[protocolo](protocolo-modulos-v0.md).

El núcleo desconfía de la respuesta: cada valor tiene que ser un decimal sin signo, con 12 enteros y 6 decimales
como máximo. Un valor que no cumple deja el trabajo muerto y visible para el staff, porque reintentar no lo
arregla. Una métrica que el módulo no devuelve no tiene muestra ese día. Lo que no se pidió se ignora.

La SDK de C# lo resuelve con `ModuleDefinition.Usage`: el autor devuelve un decimal por métrica y la SDK arma la
respuesta, controla que no haya negativos y exige que el manifiesto declare la capacidad.

## 3. Muestras diarias

| Trabajo | Lo encola | Clave de entidad | Clave de deduplicación | Qué hace |
|---|---|---|---|---|
| `usage.sweep` | El planificador, una vez por día si hay métricas activas | Ninguna | `usage.sweep:{día}` | Encola una lectura por cada servicio activo o suspendido, con módulo, cuyo producto tenga métricas activas |
| `usage.read` | `usage.sweep` | `usage:{servicio}` | `usage.read:{servicio}:{día}` | Llama a `usage`, guarda una muestra por métrica activa y revisa los umbrales de aviso |
| `usage.close` | El planificador, una vez por mes, 6 horas después de su primer intervalo | `usage-close` | `usage.close:{aaaa-mm}` | Cierra el mes calendario y factura el excedente, por cuenta, moneda y modo de carga |

- El día de la muestra es el que terminó: el planificador pide la del día anterior al empezar cada día, y la lectura
  queda con esa fecha aunque corra más tarde.
- Las lecturas de un servicio corren de a una, por la clave de entidad. La muestra es única por servicio, métrica y
  día: una lectura repetida no guarda otra ni corre la base del contador.
- Un módulo caído reintenta con la espera creciente de la cola. Si se agotan los intentos, el trabajo queda muerto
  y visible, y en los contadores la próxima lectura recupera lo que faltó.
- Un servicio suspendido se sigue leyendo: el disco que ocupa sigue ocupado. Uno dado de baja deja de leerse.

## 4. Ventanas y excedente

Una **ventana** es lo que se factura junto: las muestras de una métrica de un servicio que siguen sin facturar y
tienen fecha anterior al corte. Cada corte guarda su cargo y marca sus muestras, así cada muestra se cobra una sola
vez aunque llegue tarde: una muestra que llega después de su corte entra en la ventana siguiente. Las ventanas van
seguidas: cada una empieza en el corte anterior, o en la primera muestra si es la primera.

Para cada métrica de la ventana:

1. **Medido:** el resumen de sus muestras (máximo, promedio o suma).
2. **Excedente:** lo medido menos lo incluido, si es positivo, redondeado hacia arriba a un múltiplo del paso. Con
   paso 1 se cobran unidades enteras empezadas.
3. **Precio:** el vigente en la moneda del servicio el día que empieza la ventana. Un cambio de precio vale para las
   ventanas que empiezan desde su fecha, así no cobra con el precio nuevo un uso que ya pasó. Si no hay precio en
   esa moneda, la ventana no se corta: las muestras siguen sin facturar, el panel lo muestra y el excedente sale en
   el próximo corte después de cargar el precio (con vigencia desde antes, si corresponde cobrar lo anterior).
4. **Importe:** excedente por precio, con el redondeo comercial del renglón (D-04).

Lo incluido vale por ventana. En lo que se resume con la suma (los contadores) es por mes: la ventana incluye uno
por cada mes que dura, redondeado (de 28 a 45 días es uno, de 46 a 76 son dos) y como mínimo uno, y con la
renovación nunca menos que los meses del plan, así la de un plan trimestral incluye tres veces lo incluido y la de
uno anual, doce. Una ventana que quedó abierta más de lo normal, por falta de precio o porque la renovación esperó un
pago, incluye también esos meses y no cobra como excedente lo que el plan incluía. Un medidor incluye lo mismo en
cualquier ventana. No se prorratea en una ventana más corta, como la primera después del alta: ahí el cliente sale
ganando.

### 4.1 Momento de cobro

Cada métrica elige el suyo:

- **Con la renovación** (`renewal`, por defecto): la corrida de facturación, al renovar el servicio, corta la
  ventana en el día anterior al de la corrida (la muestra de ayer puede estar llegando) y suma los renglones de
  excedente al borrador de la renovación, en su misma transacción. Un servicio de precio cero que mide uso también
  renueva así: el borrador lleva solo el excedente. Si la renovación no se factura (baja programada, períodos
  impagos sin facturación continua), la ventana sigue abierta.
- **Por mes calendario** (`calendar_month`): `usage.close` corta el día 1 de cada mes y emite una factura de uso
  por cuenta, moneda y modo de carga, con motivo `usage` y el vencimiento de siempre. Sirve para cobrar el uso cada mes aunque el
  plan sea anual, y para lo que se cobra solo por uso. Las 6 horas de espera dejan llegar las lecturas del último día
  del mes.

Los servicios que ya no renuevan (dados de baja, o con un plan gratis o de pago único) también pasan por el cierre
del mes con sus métricas de renovación, así lo último que usaron no queda sin cobrar.

Con la renovación, el excedente forma parte de esa factura: un pago que cubre solo el precio del plan es un pago
parcial y la renovación queda impaga. Eso pesa en el débito automático por un monto fijo, como la suscripción de
Mercado Pago ([D-21](fiscal-impuestos-y-cobros.md), sección 4.5): cuando se construya, el trabajo que actualiza el
monto antes del cobro tiene que sumar el excedente de la factura, o esas métricas se cobran por mes calendario.

## 5. Renglón en la factura

Tipo de renglón `Usage`, con el servicio, la cantidad igual al excedente, el precio unitario igual al precio de
excedente y la categoría de impuesto del producto, con la regla vigente el día de la corrida, como la renovación
(fecha fiscal única de [D-21](fiscal-impuestos-y-cobros.md)). Lleva la clave `usage:{servicio}:{código}:{corte}`.
La descripción tiene el detalle, por ejemplo: "Uso de Transferencia de VPS 2 GB (vps-01) del 07/11/2026 al
06/12/2026: 120 GB medidos, 100 GB incluidos".

- **Comprobante fiscal:** el período de servicio va desde el inicio más temprano hasta el fin más tardío de sus
  renglones, contando las ventanas de uso además de los períodos facturados.
- **Pago:** pagar una factura de uso no reactiva un servicio suspendido por mora. Solo la renovación pagada mueve
  lo pagado del servicio (sección 5.2 del [modelo de dominio](dominio.md)).
- **Mora:** la anulación automática de facturas impagas no toca las facturas de uso, porque el consumo ya se
  prestó y no hay períodos que liberar. Siguen con el recordatorio y el aviso de vencida. El excedente que va en
  una renovación sigue la suerte de esa factura.
- **Notas de crédito:** una factura anulada no vuelve a facturar su excedente. El staff decide.

## 6. Avisos por umbral

Cada métrica tiene sus umbrales, en porcentaje de lo incluido (80 y 100 por defecto). Después de cada lectura, si
el uso de la ventana abierta pasó un umbral, se avisa el mayor alcanzado con la plantilla `usage-threshold`, en el
idioma de la cuenta. Hay uno solo por servicio, métrica, ventana y umbral, por la clave del correo saliente. Las
métricas sin incluido no avisan. El aviso dice el precio del excedente que va a cobrar el corte, el vigente al
empezar la ventana, y lo usado y lo incluido se cuentan igual que en el corte.

## 7. Estados

- **Métrica:** activa o inactiva, con la fecha desde la que está activa. Una inactiva no se lee ni se factura, y sus
  muestras sin facturar quedan a la vista del staff y se cobran en el próximo corte después de prenderla. Una métrica
  no se borra.
- **Muestra:** sin facturar o facturada, con su cargo.
- **Cargo:** con renglón, si hubo excedente, o sin renglón.

## 8. Permisos

| Quién | Qué ve o hace |
|---|---|
| Cliente con la capacidad `services` | El uso de sus servicios en `/servicios/{id}/uso`: la ventana abierta y los excedentes facturados. Lo ajeno da 404 |
| Staff con `accounts.read` | El uso, las lecturas diarias y los cargos de un servicio en `/admin/servicios/{id}/uso` |
| Staff con `catalog.write` | Las métricas de un producto y sus precios en `/admin/catalogo/productos/{id}/uso` |
| API con `services:read` | `GET /api/v1/services/{id}/usage` |
| API con `catalog:read` o `catalog:write` | Leer y cambiar las métricas y los precios |

Los cambios de métricas y precios quedan en la auditoría (`usage.metric.save` y `usage.price.add`).

## 9. API

| Método y ruta | Permiso | Qué hace |
|---|---|---|
| `GET /api/v1/services/{id}/usage` | `services:read` | Ventana abierta por métrica (medido, incluido, excedente y estimado) y cargos facturados |
| `GET /api/v1/products/{id}/usage-metrics` | `catalog:read` | Métricas del producto con sus precios |
| `PUT /api/v1/products/{id}/usage-metrics/{código}` | `catalog:write` | Crea o cambia una métrica: `name`, `unit`, `aggregation`, `included`, `step`, `cycle`, `alert_percents` y `active` |
| `POST /api/v1/products/{id}/usage-metrics/{código}/prices` | `catalog:write` | Agrega un precio: `currency`, `unit_price` y `valid_from` |

Una métrica que el módulo no declara, un tipo distinto o un paso inválido dan 422. Un precio repetido para la misma
moneda y fecha da 409.

## 10. Varios nodos

- Nada en memoria entre pedidos. Las lecturas y los cierres van por la cola, con su clave de entidad.
- Los cortes toman el lock de facturación de la cuenta, como la renovación (sección 13 del
  [modelo de dominio](dominio.md)). El cargo es único por corte y el renglón tiene clave: dos corridas a la vez no
  facturan dos veces.
- Una muestra que llega mientras se corta su ventana queda para la ventana siguiente: nunca se pierde ni se cobra
  dos veces.

## 11. Fuera de v0

- Suspender un servicio por una factura de uso vencida.
- Congelar el precio de excedente de cada servicio al contratar. Hoy vale la vigencia del precio.
- Prorratear lo incluido en una ventana corta.
- Depurar muestras viejas ya facturadas.
- Volver a facturar el excedente de una factura anulada.
