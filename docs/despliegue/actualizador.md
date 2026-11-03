# El actualizador

**Parte de:** [despliegue](README.md) · **Decisión:** [D-20](../../diseno/decisiones.md#d-20-actualizaciones-y-seguridad)

El actualizador aplica versiones nuevas de Hornero con un clic en el panel o con un comando, nodo por nodo y
sin cortar. Es un programa aparte, `billing-oss-actualizador`, que corre en cada nodo como servicio de systemd. La
web nunca toca el host ni el Docker: solo deja el pedido en la base, y el actualizador lo valida otra vez contra el
canal firmado antes de tocar nada.

Esta guía es para la variante de Compose: la receta chica y la de alta disponibilidad en VMs. Para hacerlo a mano,
sin el actualizador, está [actualizaciones.md](actualizaciones.md).

## Qué hace en cada versión

1. **Plan.** Los parches de una misma minor se saltean y va directo al último. Las minors van de a una.
2. **Chequeos previos**, que frenan si fallan: el canal verificado y vigente, los nodos encendidos en la misma
   versión, ningún pedido a medias, ninguna corrida del ciclo de facturación ni emisión fiscal en curso, el reloj
   sincronizado con la base, réplica sincrónica en HA, archivado de WAL al día si la marca es con PITR, módulos
   compatibles, y en cada nodo espacio en disco y Docker 24 o posterior. Mientras dura, el planificador no encola
   corridas nuevas. La pausa vence sola si el actualizador se cae.
3. **Imágenes.** Cada nodo baja por digest las imágenes de la versión, nunca por etiqueta.
4. **Marca de recuperación.** En la receta chica, un `pg_dump` que queda en `DumpDirectory`, legible solo por root,
   y se conservan los dos últimos. En HA con pgBackRest, `pg_create_restore_point` con la versión y el número de
   pedido en el nombre.
5. **Migraciones.** Una sola vez, con el rol dueño y la imagen nueva. Si fallan por `lock_timeout` reintenta con
   espera creciente. Cualquier otra falla frena sin tocar los nodos: lo aplicado lo soporta la versión que sigue
   atendiendo.
6. **Nodo por nodo.** Escribe `compose.version.yaml` con los digests, solo `image:` de servicios que ya están en la
   receta, y recrea los contenedores. Espera a que el nodo esté listo, el período de prueba y las pruebas de humo, y
   pasa al siguiente. En la receta chica, la minor de PostgreSQL va antes que el núcleo y nunca cambia de major.
7. **Si un nodo falla,** vuelven a los digests anteriores ese nodo y los que ya habían cambiado, la versión queda
   bloqueada en la instalación y el panel muestra el motivo con los registros. No reintenta solo.

Cada paso queda en la base: si el proceso se corta, retoma desde ahí. Un nodo que estuvo apagado mientras pasaba
una versión, o mientras la instalación volvía atrás, se pone al día solo con los digests de la instalación, para
arriba o para abajo, y el núcleo no arranca si el esquema ya no soporta su versión. Si un nodo no llega a volver
antes del plazo de la vuelta atrás (apagado o sin Docker), el pedido termina con la versión bloqueada, el nodo
queda **con falla** en el panel y en `status` con el motivo, y su actualizador lo pone al día cuando vuelve.

## Instalar

En cada nodo, como root:

1. El binario, que adjunta cada versión de Forgejo (`billing-oss-actualizador-linux-x64`), en
   `/opt/billing-oss/actualizador/billing-oss-actualizador`, con permiso de ejecución solo para root. Compará su
   SHA-256 con `sha256sums.txt` de la misma versión.
2. La configuración, desde [`deploy/actualizador/actualizador.json.example`](../../deploy/actualizador/actualizador.json.example),
   en `/etc/billing-oss/actualizador.json` con modo 600. `Node` tiene que ser distinto en cada nodo. Las llaves
   públicas del canal van en `PublicKeys`: la vigente y la siguiente.
3. La cadena de conexión: `preparar.sh` arma `secrets/db_actualizador`, con el rol dueño y la CA por su ruta en el
   host. En la receta chica, `compose.postgres.yaml` publica PostgreSQL solo en `127.0.0.1` para esto
   (`BILLING_PG_PUERTO`, 5432 por defecto). En una instalación anterior a esta guía, correr como superusuario:

   ```sql
   GRANT EXECUTE ON FUNCTION pg_create_restore_point(text) TO billing_owner;
   GRANT pg_monitor TO billing_owner;
   ```

4. Las dos unidades de [`deploy/actualizador/`](../../deploy/actualizador) en `/etc/systemd/system/`, y después
   `systemctl daemon-reload && systemctl enable --now billing-oss-actualizador`.

En HA, en la configuración van `"RecoveryMark": "restore-point"` y `"RequireSyncReplica": true`, y `Shutdown:DrainDelay`
del rol web tiene que cubrir el intervalo de chequeo del balanceador.

## El canal

Sin `ChannelUrl` el chequeo de versiones está apagado y el canal se carga a mano. Prenderlo lo decide el operador: es un GET
anónimo de un archivo estático, sin identificadores ni versión instalada.

```sh
billing-oss-actualizador check --file estable.json --signature estable.json.sig
```

El actualizador rechaza un canal con la firma mala, con un serial menor al último visto, vencido o emitido en el
futuro respecto del reloj del nodo. Un canal rechazado no pisa al último bueno, y el panel dice **sin datos** con el
motivo.

## Actualizar

**Desde el panel:** Configuración, Actualizaciones. Solo lo ve el staff con el permiso `system.updates`. Muestra el
estado (al día, hay versión nueva, actualizá ya o sin datos), la fecha del último chequeo, las CVE que faltan, cada
versión con su camino y si pide ventana, los nodos y los últimos pedidos. El botón **Actualizar a X.Y.Z** pide el
código del segundo factor y queda en la auditoría (`updates.requested`). La página del pedido muestra el avance.

**Desde la API:** `GET /api/v1/updates` (permiso `updates:read`), `POST /api/v1/updates/requests` con
`{"version": "X.Y.Z"}` (permiso `updates:write`) y `GET /api/v1/updates/requests/{id}`.

**Desde la consola**, con el mismo motor:

| Comando | Qué hace |
|---|---|
| `status` | Versión instalada, canal, nodos y pedido en curso |
| `plan X.Y.Z` | El camino desde la versión instalada y los nodos |
| `apply X.Y.Z` | Deja el pedido y muestra el avance hasta que termina |
| `block X.Y.Z` y `unblock X.Y.Z` | Bloquea o libera una versión en esta instalación |
| `check` | Baja y verifica el canal ahora, o lo carga de un archivo con `--file` |

## Volver atrás

- **El binario** vuelve a la versión anterior con un pedido a esa versión, mientras no haya otra minor en el medio:
  no corre migraciones, porque la versión anterior soporta el esquema nuevo.
- **Desde el panel nunca por debajo del último parche de seguridad de la línea.** Si un parche de seguridad rompe
  algo, se vuelve desde la consola del host con `apply`, y queda en el pedido con origen `console`.
- **Restaurar la base a la marca** sirve solo si una migración dañó datos, nunca es automático y pierde lo que los
  nodos escribieron mientras tanto. Lo decide una persona, después va la conciliación de series fiscales y pagos de
  [backups.md](backups.md), y antes de prender los actualizadores se bloquea otra vez la versión con `block`.

## Si el panel dice sin datos

| Motivo | Qué mirar |
|---|---|
| Todavía no hay un chequeo | El canal no se cargó: `check --file`, o falta `ChannelUrl` |
| El actualizador no da señales | `systemctl status billing-oss-actualizador` en cada nodo y `journalctl -u billing-oss-actualizador` |
| El último canal no verificó | El último intento en el panel: firma, serial, vencimiento o reloj |
| El canal venció o el chequeo tiene más de 48 horas | Quien sirve el canal no lo está renovando, o el nodo no llega |
| Formato nuevo | El actualizador se pone al día solo con el binario del canal. Si no puede, se reemplaza a mano |
