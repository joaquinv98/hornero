# Uso medido

**Estado:** v0, 2026-10-29 · **Diseño:** [diseno/uso-medido.md](../diseno/uso-medido.md) · **Protocolo:** capacidad
`provisioning.usage` (sección 9.1 de [protocolo-modulos-v0.md](../diseno/protocolo-modulos-v0.md))

Cómo se cobra el excedente de lo que usa un servicio: qué hace falta para prenderlo, qué corre solo, qué ven el
cliente y el staff, y cómo se revisa.

## Cómo se prende

1. **El módulo declara sus métricas.** En su manifiesto suma la capacidad `provisioning.usage` y la lista
   `usage_metrics`, con el tipo de cada una: `gauge` para un nivel (disco ocupado, casillas, dispositivos) y
   `counter` para un acumulado (bytes transferidos, segundos de CPU). Con la SDK de C# alcanza con
   `ModuleDefinition.Usage`, que devuelve la lectura actual de cada métrica pedida.
2. **El staff elige qué se cobra.** Con el permiso `catalog.write`, en `/admin/catalogo/productos/{id}/uso` (o por la API)
   elige las métricas del producto, lo incluido, el paso de facturación, cuándo se cobra y los umbrales de aviso.
3. **Carga el precio del excedente** en cada moneda en que vende el producto, con la fecha desde la que vale. Se
   lee en el modo de carga del servicio, con IVA incluido o aparte, igual que el precio de su plan.

Una métrica se apaga y se vuelve a prender sin perder lo que ya midió. Apagada no se lee ni se factura, y al
prenderla un contador arranca de una base nueva: lo que subió mientras estaba apagada no se cobra.

Lo incluido de lo que se suma (transferencia, CPU) es por mes: si se cobra con la renovación, un plan anual incluye
doce veces eso y uno trimestral, tres. Si una ventana quedó abierta más de un mes, por ejemplo porque faltaba el
precio, incluye lo de cada mes que duró. Lo que es un nivel (disco, casillas, dispositivos) incluye lo mismo en
cualquier plan.

Ejemplos:

| Producto | Métrica | Tipo | Incluido | Paso | Se cobra |
|---|---|---|---|---|---|
| VPS | Transferencia (GB) | Contador, suma | 1000 GB | 1 GB | Con la renovación |
| Hosting anual | Disco (GB) | Medidor, máximo | 10 GB | 1 GB | Por mes calendario |
| Servicio gestionado | Dispositivos | Medidor, máximo | 10 | 1 | Con la renovación |
| Central telefónica | Internos | Medidor, máximo | 5 | 1 | Con la renovación |

## Qué corre solo

| Cuándo | Trabajo | Qué hace |
|---|---|---|
| Al empezar cada día | `usage.sweep` | Encola una lectura por servicio activo o suspendido que mide uso |
| Enseguida | `usage.read` | Pide `usage` al módulo, guarda la muestra del día que terminó y avisa al cliente si pasó un umbral |
| En la corrida de facturación | `billing.run` | Al renovar, suma el excedente de las métricas con la renovación, cortado en el día anterior |
| El día 1, 6 horas después del primer intervalo | `usage.close` | Factura el mes que cerró: métricas mensuales y lo último de los servicios que ya no renuevan |

Nada de esto corre si ningún producto tiene métricas activas.

## Qué ve el cliente

- En `/servicios`, el enlace "Ver el uso" en los servicios que miden.
- En `/servicios/{id}/uso`, cada métrica del período en curso: lo usado, lo incluido con una barra, el excedente y
  su costo estimado, y los períodos ya cerrados con su factura.
- En la factura, un renglón aparte por métrica: "Uso de Transferencia de VPS 2 GB (vps-01) del 07/11/2026 al
  06/12/2026: 120 GB medidos, 100 GB incluidos", con el excedente como cantidad y el precio por unidad.
- Un correo cuando llega al 80 % y al 100 % de lo incluido (o los umbrales que se configuren), con la plantilla
  `usage-threshold`, que se edita como las demás desde la API de plantillas.

## Qué ve el staff

- En `/admin/cuentas/{id}`, el enlace al uso de cada servicio que mide.
- En `/admin/servicios/{id}/uso`, lo mismo que el cliente más las lecturas diarias, cuáles ya se facturaron y el
  aviso de una métrica con excedente sin precio en la moneda del servicio.
- En `/admin/catalogo/productos/{id}/uso`, las métricas del producto con sus precios, para cambiarlas.

## Revisar

Lecturas que no se pudieron hacer (módulo sin la capacidad, lectura inválida o módulo caído más de los reintentos):

```sql
SELECT id, payload, last_error, updated_at FROM platform.jobs
WHERE kind = 'usage.read' AND status = 'Dead' ORDER BY id DESC;
```

Ventanas que quedaron abiertas en el último cierre, por ejemplo por falta de precio:

```sql
SELECT s.service_id, s.code, min(s.date), max(s.date), count(*)
FROM services.usage_samples s
WHERE s.charge_id IS NULL AND s.date < date_trunc('month', now() AT TIME ZONE 'America/Argentina/Buenos_Aires')::date
GROUP BY s.service_id, s.code;
```

Para cobrar una ventana que quedó sin precio, se carga el precio con una fecha de vigencia anterior al inicio de la
ventana. Sale en el próximo corte.

## Lo que no hace

- No suspende un servicio por una factura de uso vencida. La factura sigue con el recordatorio y el aviso de
  vencida, y la anulación automática de impagas no la toca.
- Una factura de uso anulada con una nota de crédito no se vuelve a facturar.
- El precio del excedente vale por vigencia para todos los servicios del producto: no se congela al contratar.

## API

`GET /api/v1/services/{id}/usage` (`services:read`), `GET /api/v1/products/{id}/usage-metrics` (`catalog:read`),
`PUT /api/v1/products/{id}/usage-metrics/{código}` y `POST /api/v1/products/{id}/usage-metrics/{código}/prices`
(`catalog:write`). El detalle está en la sección 9 del [diseño](../diseno/uso-medido.md).

## Pruebas

| Prueba | Qué cubre |
|---|---|
| `UsageTests` (dominio) | Contadores con base y reinicio, resumen, excedente con paso, umbrales, lo incluido según el plan y los meses de la ventana, reglas de la métrica, desde cuándo está activa y precio vigente |
| `ModuleSdkTests.Uso_*` | La operación `usage` de la SDK, el formato de las lecturas y el manifiesto |
| `UsageFlowTests` | Lectura diaria por la cola con el módulo falso, reintento y lecturas inválidas, métrica que se apaga y se vuelve a prender, excedente en la renovación y al cierre del mes, muestras que llegan tarde, plan anual, precio por moneda y modo de carga, ventana sin precio durante dos meses, servicios dados de baja, período fiscal, mora y avisos con el precio de la ventana |
| `UsageApiTests` | Permisos, validación contra el manifiesto, precios repetidos y el uso de un servicio |
| `UiTests.Uso_*` | Portal y admin: lo ajeno da 404, la capacidad `services`, las lecturas del staff y la configuración con coma o punto decimal |
