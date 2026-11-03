# Backups, restauración y prueba periódica

**Parte de:** [despliegue](README.md) · **Para:** la [HA en VMs](ha-vms.md). La receta chica tiene su propia copia
diaria, en [receta-chica.md](receta-chica.md#7-copias-y-restauración).

## Qué se respalda y dónde

| Qué | Cómo | Dónde |
|---|---|---|
| La base (núcleo, y los registros de los módulos cuando estén en PostgreSQL) | pgBackRest: archivo continuo de WAL y respaldos completos y diferenciales | repo1 en el sitio y repo2 fuera del sitio, los dos cifrados |
| La llave de D-09 (`secrets/llaves`) | A mano, al instalar y en cada rotación | La bóveda, y una copia impresa guardada aparte. Nunca junto a la base |
| Los demás secretos (`secrets/`, `secretos.env`, la clave de la CA) | A mano | La bóveda |
| Recetas, configuración e inventario sin secretos | Git | El repo |

Las VMs de base no van a un backup de VM: se reconstruyen con la receta y se restauran con pgBackRest. Así un
backup robado nunca trae juntas la base y la llave.

## pgBackRest

- **Versión:** 2.60 o posterior. Nunca una anterior a 2.59.3: esas versiones podían generar subclaves de cifrado
  débiles, y un repositorio creado con ellas se verifica con el script del proyecto y, si hace falta, se rehace.
  Su mantenimiento (lo sostiene una coalición de sponsors) se revisa cada trimestre. WAL-G (Apache-2.0) es la
  alternativa cuando declare soporte de PostgreSQL 18.
- **Archivo continuo:** `archive_command` con `archive-push` asincrónico y `archive_timeout` de 60 segundos, en
  la configuración de Patroni. Solo archiva la primaria.
- **Dos repositorios** con `aes-256-cbc` y claves distintas en la bóveda: repo1 en el sitio, en un host que no es
  nodo de base, y repo2 fuera del sitio en S3. **repo2 es condición para producción:** sin él, perder el sitio es
  perder la facturación.
- **Calendario:** completo el domingo y diferencial de lunes a sábado, a las 03:30, con los timers de
  [`deploy/ha/systemd/`](../../deploy/ha/systemd). Están en los tres nodos y solo corren donde Patroni dice que
  está la primaria.
- **Retención:** 5 completos en repo1 (unas 5 semanas de PITR) y 8 en repo2.
- **Réplicas nuevas** se arman desde el respaldo (`create_replica_methods`), sin cargar a la primaria.
- **Copia lógica mensual**, independiente del formato de pgBackRest y legible en otra versión de PostgreSQL:
  `pg_dump -Fc billing_oss`, cifrada, a repo2. El plazo de guarda de los comprobantes lo fija el contador, y los
  comprobantes y emisiones nunca se borran de la base.

## Prueba de restauración

Automática y semanal, en una VM descartable que no es parte del clúster, sin salida a la red salvo hacia el
repositorio. [`prueba-restauracion.sh`](../../deploy/ha/prueba-restauracion.sh):

1. Restaura desde repo2 el último respaldo más el WAL hasta 10 minutos antes.
2. Arranca PostgreSQL solo por socket, sin TLS, archivo ni réplicas.
3. Verifica páginas, tablas e índices con `pg_amcheck`.
4. Cuenta documentos, comprobantes, pagos y trabajos, para comparar con lo que publica el monitoreo.
5. Si todo da bien, escribe la fecha en `/var/lib/billing-restore/ultimo-ok`.

La VM tiene PostgreSQL 18, pgBackRest, el mismo `pgbackrest.conf` y un `secretos.env` solo con la clave de repo2 y
una credencial de S3 de solo lectura. Se instala con
[`prueba-restauracion.service` y `.timer`](../../deploy/ha/systemd). El monitoreo alerta si `ultimo-ok` tiene más
de 8 días.

**Falta en el núcleo**, y se suma a la prueba cuando exista: descifrar una muestra de secretos con la copia de la
llave de D-09 que está en la bóveda (D-09 la pide), y correr la conciliación en modo de prueba. Mientras tanto, la
llave se prueba a mano en cada rotación.

## Restaurar a un punto en el tiempo

Para un error de operación, una migración mala o un borrado. Se restaura en un clúster nuevo, nunca encima del que
está andando.

1. **Elegir el instante,** en UTC, antes del error.
2. **Frenar la aplicación** en todos los nodos: `docker compose stop web worker`.
3. **Frenar Patroni** en los tres nodos y sacar el clúster de etcd:

   ```sh
   systemctl stop patroni-billing                      # en los tres
   patronictl -c /etc/patroni/billing.yml remove billing
   ```

4. **Apartar los datos viejos** en los tres nodos, sin borrarlos:
   `mv /var/lib/postgresql/18/billing /var/lib/postgresql/18/billing.antes-pitr`.
5. **Arrancar desde el respaldo.** En el nodo elegido, sumar a `/etc/patroni/billing.yml` un método de arranque y
   arrancar Patroni solo ahí:

   ```yaml
   bootstrap:
     method: pitr
     pitr:
       command: /usr/bin/pgbackrest --stanza=billing --type=time "--target=2026-10-27 18:00:00+00" --target-action=promote restore
       keep_existing_recovery_conf: true
       no_params: true
   ```

   Patroni restaura, recupera hasta el instante y queda como líder en una línea de tiempo nueva. Después se
   arranca Patroni en los otros dos, que se clonan solos, y se saca el método `pitr` del archivo.
6. **Respaldo completo nuevo** y `pgbackrest check`.
7. **Conciliar** (siguiente sección) con la aplicación todavía frenada.
8. **Arrancar la aplicación** y, cuando todo esté verificado, borrar los directorios `billing.antes-pitr`.

Tiempo estimado para una base mediana: de 1 a 2 horas, a medir en la primera prueba.

## Después de restaurar

Cuando se vuelve a un punto anterior, el fisco, las pasarelas y los sistemas aprovisionados siguieron adelante y la
base no. La conciliación (`reconcile`) lo pone a la vista y resuelve sola lo que no tiene ambigüedad. Corre de dos
formas:

- **Sola.** El planificador guarda en `platform.timeline` la línea de tiempo de PostgreSQL que vio en cada vuelta,
  con la hora. Después de una restauración o de una promoción la línea de tiempo es otra, y el primer nodo que corre
  el planificador encola el trabajo `platform.reconcile` desde la hora de la última marca, que en una base
  restaurada es anterior al punto (un minuto antes como mucho). No se cruza con una emisión: mientras está pendiente
  o corriendo, los trabajos fiscales de todas las series esperan, y ella espera a que terminen los que ya corrían.
  Corre una sola vez por línea de tiempo. No salta la numeración comercial: eso es solo a mano.
- **A mano**, con la aplicación frenada (paso 7 de la restauración), primero en modo de prueba:

  ```sh
  docker compose run --rm worker reconcile --since 2026-10-27T18:00:00Z --counter-gap 100 --dry-run
  docker compose run --rm worker reconcile --since 2026-10-27T18:00:00Z --counter-gap 100
  ```

  `--since` es el instante al que se restauró, en UTC. Sin `--since` usa la hora de la última marca si la línea de
  tiempo cambió, y si no cambió pide la fecha. `--counter-gap` salta cada contador de la numeración comercial
  (`billing.counters`) esa cantidad, para que una factura nueva no repita un número que ya viajó por correo después
  del punto: el valor va por encima de lo que pudo haberse emitido después del punto, con margen. `--dry-run` informa
  y no escribe nada. Sale con 0 sin hallazgos, 2 con hallazgos y 1 si no pudo correr. Necesita `Secrets:KeyFile`,
  como el rol worker: sin llaves no habla con los módulos.

Qué hace, en orden:

1. **Series fiscales.** Por cada punto de venta y tipo del mapa del emisor, exista o no la serie en la base (restaurada
   a antes de la primera emisión de una serie, la fila no está y el fisco igual tiene comprobantes), le pide el último
   número al módulo fiscal. Si el fisco está
   adelante, trae cada comprobante que falta con `find` y busca el documento local sin comprobante que coincida con
   la comparación estricta de la emisión (fecha, total, receptor, período, vencimiento, moneda, concepto y desglose
   de IVA). Con un solo candidato lo adopta: queda en `fiscal.documents` con su CAE, sin aviso al cliente, porque el
   aviso de entonces ya viajó. Sin candidato queda el hallazgo `fiscal.orphan`, y con dos o más, `fiscal.ambiguous`
   con los documentos candidatos. En los dos casos la serie avanza igual: la emisión sigue con el número siguiente y
   nunca vuelve a pedir uno que el fisco ya entregó. Las emisiones que habían frenado en `Conflict` por la serie
   fuera de sincronía vuelven a pendientes y el barrido diario las reemite. Con `Fiscal:ProductionEnabled` apagado no
   se le pregunta nada al fisco real.
2. **Numeración comercial.** Con `--counter-gap`, salta cada contador y deja el hallazgo `counter.gap` con el
   desde y el hasta: es el registro del hueco.
3. **Pagos.** A cada pasarela con `payment.reconcile` le pide los pagos desde `--since`. Los que tienen intento
   propio se registran como siempre (es la misma conciliación horaria). Los que no, quedan en `payments.unmatched`
   con monto, moneda, estado y referencia, sin registrarse como pago.
4. **Aprovisionamiento.** A cada módulo de aprovisionamiento que declara `provisioning.list` le pide los recursos que
   llevan la marca del núcleo. Un recurso cuyo servicio no existe en la base queda como `provisioning.orphan`, con
   sus `external_ids`. Un módulo que no sabe listar deja `provisioning.unlisted`: lo que se creó, suspendió o borró
   después del punto se revisa en su panel.

Todo queda en la auditoría (`platform.audit_events`, acción `reconcile`, con cuántos comprobantes adoptó, cuántos
hallazgos dejó y cuántos pagos quedaron sin casar). Los hallazgos abiertos están en `platform.reconcile_findings` y
los pagos sin casar en `payments.unmatched`. Las dos tablas salen en `platform.metrics()` y tienen alerta en la
plantilla de [docs/observabilidad](../observabilidad/README.md): `billing-oss: hay hallazgos de conciliación sin
resolver` y `Hornero: hay pagos sin casar`. Un hallazgo abierto no se repite aunque la conciliación corra varias
veces.

### Qué hacer con cada hallazgo

Hasta que exista la pantalla, se miran y se cierran con el dueño de la base:

```sql
SELECT id, kind, subject, details, created_at FROM platform.reconcile_findings WHERE resolved_at IS NULL ORDER BY id;
SELECT id, module_instance_id, payment_external_id, external_reference, status, amount, currency, seen_at
  FROM payments.unmatched WHERE resolved_at IS NULL ORDER BY id;
UPDATE platform.reconcile_findings SET resolved_at = now() WHERE id = <id>;
UPDATE payments.unmatched SET resolved_at = now() WHERE id = <id>;
```

- **`fiscal.orphan`**, un comprobante que el fisco autorizó y ningún documento explica. `subject` es punto de venta,
  tipo y número, y `details` trae fecha, total, moneda y CAE. La única salida legal es una nota de crédito en el
  fisco por el total, con el mismo receptor y el comprobante huérfano como asociado. Se emite fuera de Hornero,
  porque no hay documento local al que atarla: en Comprobantes en línea de ARCA con el mismo CUIT, desde un punto de
  venta que no sea el de web services (un comprobante asociado puede ser de otro punto de venta), o con el sistema
  del contador. Si la venta fue real, se vuelve a cargar en Hornero (pedido o factura) y sale con un comprobante
  nuevo, con la fecha del día: el cliente recibe la factura nueva y la nota de crédito cancela la vieja. Con
  `missing: true` el fisco informó un último número que después no devuelve al consultarlo: se repite la
  conciliación más tarde antes de dar nada por perdido.
- **`fiscal.ambiguous`**, un comprobante con dos o más documentos locales iguales (mismo cliente, fecha y total, en
  `candidates`). Nadie lo adopta solo y los documentos se emiten de nuevo con el número siguiente. Si uno de los
  candidatos tiene la emisión frenada con ese número propuesto, el staff ve lo que el fisco tiene en
  `GET /api/v1/fiscal/emissions/{documentId}` y la adopta con `POST /api/v1/fiscal/emissions/{documentId}/adopt` y el
  código de autorización. Si no, se trata como huérfano, con la nota de crédito, y cada documento sale con su
  comprobante nuevo. El último número de una serie se fija a mano con `POST /api/v1/fiscal/series/{pointOfSale}/{typeCode}`
  y el motivo (la API propia está en el README).
- **`counter.gap`**, el salto de `--counter-gap` en un contador: `details` trae `from` y `to`. Es el registro del
  hueco, se cierra sin más.
- **`provisioning.orphan`**, un recurso con la marca del núcleo (`service_id` en `details`) cuyo servicio no está en
  la base. Si la venta fue real y el recurso se puede recrear, se borra en el panel y se vuelve a cargar el pedido,
  que lo crea de nuevo. Si el cliente ya lo usa, se carga el pedido y se le atan los `external_ids` del recurso al
  servicio nuevo con el dueño de la base, para que lo gobierne sin crear otro. Si la venta no fue real, se borra el
  recurso en el panel.
- **`provisioning.unlisted`**, un módulo que no declara `provisioning.list`. Se revisa su panel: lo creado después
  del punto sin servicio, y lo suspendido o borrado que la base cree activo.
- **Pago sin casar**, un pago que la pasarela informó y no corresponde a ningún intento. Si es de otro sistema que
  usa la misma cuenta de la pasarela, se cierra. Si es de un cliente, se registra a mano contra su factura
  (`AddInvoicePayment` de la API compatible o la pantalla del cliente) y se cierra.

Recién después se vuelve a emitir y cobrar. Con la replicación sincrónica, una conmutación normal no pierde datos:
la conciliación automática termina sin hallazgos y alcanza con mirar las consultas de
[monitoreo.md](monitoreo.md#después-de-una-promoción).

## Pérdida del sitio

Lo que tiene que estar afuera antes: repo2, la bóveda (con la llave de D-09), este repo y el DNS.

1. Crear VMs nuevas en otro lugar con [ha-vms.md](ha-vms.md), pasos 1 a 5, con certificados nuevos si cambian
   las IP.
2. Arrancar el primer nodo desde repo2, con el método `pitr` de arriba con `--repo=2` y sin `--type` ni
   `--target` (hasta el último WAL archivado).
3. Traer de la bóveda `secrets/` y montar la aplicación (pasos 8 y 9).
4. Conciliar como en la sección anterior.
5. Apuntar el DNS a la entrada nueva. Si cambian las IP de salida, avisar a la pasarela y revisar si el fisco las
   tiene registradas.

Se pierde lo que no llegó a repo2 (unos 60 segundos de WAL más la cola sin subir). Objetivo de tiempo: de 4 a 8
horas.
