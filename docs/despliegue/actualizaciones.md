# Actualizaciones sin corte

**Parte de:** [despliegue](README.md)

Con el [actualizador](actualizador.md) (D-20) todo esto lo hace solo, nodo por nodo, desde el panel o la consola. Esta
guía queda para entender qué hace y para hacerlo a mano.

## Migraciones en dos pasos

La regla que permite actualizar nodo por nodo: **la versión anterior de la aplicación tiene que funcionar con el
esquema nuevo.** Durante una actualización conviven nodos viejos y nuevos sobre la misma base, y volver atrás es
volver a la imagen anterior sin tocar el esquema. Por eso cada cambio incompatible se parte en dos versiones:

| Cambio | Versión N (agregar) | Versión N+1 (quitar) |
|---|---|---|
| Columna nueva | Se agrega nula o con valor por defecto. El código nuevo la escribe | Si hace falta, `NOT NULL` (ver abajo) |
| Renombrar una columna | Columna nueva. El código escribe las dos y lee la nueva. Un trabajo copia lo viejo | Se borra la vieja |
| Borrar una columna o tabla | El código deja de usarla. La migración no la toca | La migración la borra |
| Cambiar un tipo | Columna nueva con el tipo nuevo, como en un renombre | Se borra la vieja |
| `NOT NULL` en una tabla grande | `CHECK (col IS NOT NULL) NOT VALID` y después `VALIDATE CONSTRAINT`, que no bloquea escrituras | `SET NOT NULL`, que usa el check validado, y se borra el check |
| Tipo de trabajo nuevo | El worker nuevo sabe correrlo, pero nadie lo encola todavía | Se empieza a encolar, cuando ya no quedan workers viejos |

Al escribir una migración en EF Core:

- **Índices** con `CREATE INDEX CONCURRENTLY`, solos en su migración y fuera de la transacción:
  `migrationBuilder.Sql("CREATE INDEX CONCURRENTLY ...", suppressTransaction: true)`.
- **Tiempo de espera de locks:** una migración que altera una tabla existente empieza con
  `migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'")`. Si una transacción larga tiene la tabla, la migración
  falla y se reintenta, en lugar de frenar a la aplicación detrás de ella.
- **Nunca** se vuelve atrás una migración en producción (`Down`). Se arregla con otra migración hacia adelante.
- Lo que quita o endurece algo va en una minor y declara la versión mínima del núcleo con
  `migrationBuilder.RequireCoreVersion("X.Y.0")`, y un índice concurrente que se pueda repetir va con
  `migrationBuilder.CreateIndexConcurrently(...)`. Detalle en [docs/versiones](../versiones/README.md#migraciones).
- La revisión del cambio verifica la regla: ¿la versión anterior anda con este esquema?

## Actualizar Hornero en HA

1. Construir las imágenes nuevas y llevarlas a todos los nodos (mismo digest).
2. Desde un nodo, con la versión nueva en `.env`: `docker compose run --rm migrate`. Corre contra la primaria,
   porque la cadena tiene `Target Session Attributes=read-write`.
3. Nodo por nodo:

   ```sh
   # Con HAProxy: drenar el nodo para que no reciba pedidos nuevos.
   echo "set server billing_web/billnode01 state drain" | socat stdio /run/haproxy/admin.sock
   docker compose up -d                 # recrea web, worker y módulos con BILLING_VERSION nueva
   curl -fsS http://192.0.2.11:8080/health/ready
   echo "set server billing_web/billnode01 state ready" | socat stdio /run/haproxy/admin.sock
   ```

   Con un proxy sin API de drenado (NPM), el nodo deja de aceptar conexiones al apagarse y el proxy pasa el
   pedido a otro, porque la conexión no se abrió.
4. Esperar a que el nodo esté listo antes de pasar al siguiente. Mientras no esté el apagado ordenado del worker,
   esperar además a que termine el apagado ordenado (devuelve a la cola lo que no terminó en `Workers:ShutdownGrace`).

**Volver atrás:** la etiqueta anterior en `BILLING_VERSION` y el mismo recorrido. El esquema queda como está.

## Módulos

Se actualizan con la aplicación, nodo por nodo. El protocolo v0 solo admite cambios que agregan: un campo nuevo
siempre es opcional, así un núcleo viejo y un módulo nuevo se entienden. Mientras el registro de idempotencia sea
SQLite, se conserva el volumen del módulo: no se borra al actualizar.

## PostgreSQL

**Versión menor** (por ejemplo 18.6 a 18.7), sin corte salvo la conmutación:

1. En cada réplica: `apt upgrade postgresql-18` y `patronictl restart billing billnode02`.
2. `patronictl switchover` hacia una réplica ya actualizada.
3. Actualizar la vieja primaria, que ahora es réplica, igual que en el paso 1.

**Versión mayor**, con una ventana anunciada de minutos:

- **`pg_upgrade --link`** con el clúster frenado, siguiendo el procedimiento de Patroni para versiones mayores:
  se actualiza el líder, se saca el clúster de etcd, se arranca con la versión nueva y las réplicas se rehacen
  desde un respaldo nuevo. Ojo: el clúster nuevo tiene que tener los checksums de datos igual que el viejo
  (PostgreSQL 18 los activa por defecto al crear la base).
- **Azul y verde** con replicación lógica, si la ventana no se puede. Las secuencias no se replican: se adelantan a
  mano antes de pasar, como en la [conciliación después de restaurar](backups.md#después-de-restaurar).

Una versión mayor nueva entra cuando sale su segunda versión menor y Patroni y pgBackRest la declaran soportada.

## Patroni, etcd y pgBackRest

De a un nodo por vez, empezando por las réplicas y con un `switchover` antes de tocar la primaria. Después de
cada nodo, `patronictl list` y `etcdctl endpoint health --cluster` en verde antes de seguir.

**Reemplazar o sumar un nodo de base:**

1. `etcdctl member add` con el nodo nuevo, y arrancar etcd ahí con `ETCD_INITIAL_CLUSTER_STATE=existing`.
2. Patroni en el nodo nuevo, que se clona solo.
3. En los nodos de aplicación, agregar la IP nueva a `Host` en `secrets/db_app` y `secrets/db_dueno`, y recrear
   los contenedores nodo por nodo.
4. Para sacar un nodo, el camino inverso: primero de las cadenas, después Patroni y por último
   `etcdctl member remove`.

## Sistema operativo

Parches del sistema y de Docker, nodo por nodo: primero los que no tienen la primaria y, en el de la primaria, un
`switchover` antes de reiniciar. Nunca migrar en vivo una VM de base a un host que ya tiene otra. Conviene una
ventana lejos de los primeros días del mes, cuando corren las renovaciones.

## Llave de los secretos (D-09)

Rotar es agregar una línea con una versión nueva al archivo `llaves`. Hoy la versión vigente es la mayor del
archivo, así que un nodo que arranca con la llave nueva cifra con ella y los que todavía no la tienen no pueden
leer eso. Hasta que exista `Secrets:ActiveVersion`:

1. Agregar la línea nueva a `secrets/llaves` en todos los nodos, y a la bóveda.
2. Recrear web y worker de todos los nodos casi a la vez (`docker compose up -d --force-recreate web worker`).
3. Conservar las versiones viejas en el archivo hasta que venza el último backup cifrado con ellas.

## Certificados internos

Los de los nodos y los módulos duran 825 días y la CA 10 años. Se renuevan antes con
[`certificados.sh`](../../deploy/certificados.sh), borrando el certificado viejo del directorio de trabajo:

- **Nodos de base:** un nodo por vez y `systemctl reload patroni-billing` (PostgreSQL relee el certificado). etcd
  se reinicia.
- **Módulos:** el certificado nuevo tiene otra huella, así que primero se carga la huella nueva en la instalación
  del núcleo y después se reemplaza el certificado en todos los nodos.

## Secreto de firma de un módulo

El módulo acepta el secreto nuevo y el anterior a la vez durante una rotación (`Module:PreviousSigningSecret`):

1. En los módulos, el nuevo como `Module__SigningSecret` y el viejo como `Module__PreviousSigningSecret`.
2. En el núcleo, el secreto nuevo en la instalación.
3. Cuando no queda nada en vuelo, sacar el anterior de los módulos.
