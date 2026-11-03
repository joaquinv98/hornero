# Conmutación y fallas

**Parte de:** [despliegue](README.md) · **Para:** la [HA en VMs](ha-vms.md)

Qué hacer en cada caso. En todos, `patronictl` se corre en cualquier nodo de base como
`patronictl -c /etc/patroni/billing.yml ...`, abreviado acá como `patronictl`.

## Conmutación planificada

Antes de reiniciar el host o la VM de la primaria, o para una actualización menor de PostgreSQL:

```sh
patronictl list                                       # todo en streaming y sin atraso
patronictl switchover --leader billnode01 --candidate billnode02 --force
patronictl list                                       # billnode02 líder, línea de tiempo nueva
```

Hay de 5 a 10 segundos sin escrituras. Los pedidos en vuelo pueden fallar y el cliente los repite. Los trabajos
fallan una vez y vuelven a la cola. Después del reinicio, la vieja primaria vuelve sola como réplica. Para volver a
la ubicación preferida, otro `switchover` con los nombres al revés.

## Caída de la primaria

**Qué pasa solo:** a los 30 segundos como máximo vence la llave de líder en etcd y Patroni promueve una de las
réplicas sincrónicas, que tiene todo lo confirmado. Si la vieja primaria sigue viva pero aislada, el watchdog la
reinicia, y al volver Patroni la suma como réplica con `pg_rewind`. La aplicación encuentra la primaria nueva en
la próxima conexión.

**Qué mirar en los primeros 5 minutos:**

1. `patronictl list`: un líder, la línea de tiempo nueva y las réplicas que quedaron.
2. `/health/ready` de cada nodo de aplicación, por la entrada.
3. Las consultas de [después de una promoción](monitoreo.md#después-de-una-promoción): emisiones fiscales
   inciertas o en conflicto, trabajos muertos y avisos de pasarelas sin procesar.

**Qué no hacer:** arrancar a mano la vieja primaria fuera de Patroni, borrar la llave de líder en etcd o apagar el
modo sincrónico estricto para "destrabar".

Con la replicación sincrónica estricta una promoción no pierde datos. Las emisiones que estaban en vuelo quedan
inciertas y se resuelven solas consultando al fisco. Además, el planificador ve la línea de tiempo nueva y
encola la conciliación (`platform.reconcile`) desde la última marca, un minuto antes como mucho: con nada
perdido termina sin hallazgos y queda en la auditoría. Si deja hallazgos, o las consultas muestran algo raro,
se trata como una restauración con pérdida ([backups.md](backups.md#después-de-restaurar)).

## Doble falla: la primaria y la réplica que confirmaba, a la vez

Dos hipervisores sin luz, un switch que deja fuera dos VMs. Con quórum `ANY 1`, la réplica que queda no puede probar
que tiene el último commit, porque Patroni compara su LSN contra otro votante, así que no promueve sola y el
clúster queda en solo lectura. La conmutación es manual y puede perder lo que esa réplica no recibió:

```sh
patronictl list                                        # una sola réplica viva, sin líder
# En esa réplica, qué llegó a recibir:
sudo -u postgres psql -Atc "SELECT pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn()"
```

Si el LSN recibido coincide con el último que se vio en la primaria (en el monitoreo, o en `pg_stat_replication`
de la última lectura), la promoción no pierde nada. Si no se puede saber, se asume pérdida:

```sh
patronictl failover --candidate billnode03 --force
```

Después, obligatorio, `reconcile` como dice [después de restaurar](backups.md#después-de-restaurar): series
fiscales contra el fisco, pagos contra las pasarelas y aprovisionamiento contra los módulos, antes de volver a
emitir y cobrar. Lo mismo vale para `failoverQuorum` de CloudNativePG, que tiene el mismo límite.

## Caída de una réplica

Con una réplica caída, la otra confirma y no hay corte. Patroni la vuelve a sumar cuando el nodo vuelve. Si quedó
muy atrás o con el disco dañado:

```sh
patronictl reinit billing billnode03
```

La recrea desde el respaldo de pgBackRest, sin cargar a la primaria.

## Las dos réplicas caídas

Con el modo sincrónico estricto PostgreSQL no tiene con quién confirmar y cada COMMIT se queda esperando. Confirmar
en un solo nodo es lo que después se pierde, así que la aplicación frena las escrituras, con una ventana que hay que
conocer:

- El vigía del clúster (`Database:Durability=sync`) ve en segundos que no hay réplica sincrónica y cierra el freno de
  las escrituras: los nodos worker no toman trabajos y la API contesta 503 con `Retry-After` a toda escritura. Las
  lecturas y la salud siguen, y `/health/cluster` queda en rojo.
- Lo que ya estaba en vuelo cuando cayeron las réplicas espera hasta el `Command Timeout` (90 s). Ahí PostgreSQL
  cancela la espera y confirma solo en la primaria, con un aviso que la aplicación convierte en falla pasajera: el
  trabajo vuelve a la cola, la API da 503 y queda en el log como `CommitUncertainException`. Esos datos existen en la
  primaria y en ninguna réplica. Si la primaria se pierde antes de que vuelva una réplica, se pierden, con el
  comprobante en ARCA o el cobro en la pasarela ya hechos: en ese caso se corre la lista de
  [después de restaurar](backups.md#después-de-restaurar). Ningún otro plazo acorta esa espera: un cliente que
  corta, el lease perdido o el apagado no cancelan un COMMIT, solo ese timeout.

1. Recuperar una réplica: es lo único que destraba sin riesgo. Apenas vuelve a estar en streaming, el vigía abre el
   freno solo.
2. Si la caída va a ser larga y el negocio no puede esperar, la decisión de seguir sin réplica es del responsable
   del servicio, nunca automática:

   ```sh
   patronictl edit-config -s synchronous_mode_strict=false
   ```

   Desde ese momento PostgreSQL deja de esperar, el freno se abre solo y lo confirmado puede perderse si cae la
   primaria. Apenas vuelve una réplica, se vuelve a poner en `true`.

## etcd sin quórum

Con `failsafe_mode`, la primaria sigue aceptando escrituras mientras pueda hablar con todos los nodos por la API de
Patroni. Si no puede, pasa a solo lectura hasta que etcd recupere el quórum. Recuperar los miembros de etcd
(`systemctl status etcd`, el disco y la red) y verificar con `etcdctl endpoint health --cluster`.

## Partición de red

Sigue el lado con mayoría de etcd. Una primaria que quedó sola pierde la llave a los 30 segundos y Patroni la
degrada. Si Patroni está colgado, la reinicia el watchdog. Mientras tanto sus commits esperan a una réplica que no
los confirma. Los nodos de aplicación de ese lado cierran el freno de las escrituras en cuanto el vigía ve que no hay
réplica sincrónica, y un COMMIT que ya estaba en vuelo sigue esperando: la degradación (ttl 30 más loop_wait 10)
llega antes que el `Command Timeout` de 90 s, así que termina con la conexión cortada y nunca confirmado. Los nodos
de aplicación aislados no llegan a ninguna primaria, dan listo en rojo y la entrada los saca.

Al volver la red, el nodo aislado se suma como réplica con `pg_rewind`. Si no puede, `patronictl reinit`.

## Conmutación forzada

`patronictl failover --candidate billnode02 --force` promueve aunque Patroni no quiera, por ejemplo con las dos
réplicas atrasadas. Puede perder lo que la réplica no tenía, así que se trata como una restauración con pérdida:
antes de volver a emitir y cobrar, `reconcile` como dice [después de restaurar](backups.md#después-de-restaurar).

## Caída de un nodo de aplicación, un módulo o la entrada

- **Nodo de aplicación:** la entrada lo saca en unos 6 segundos y los otros atienden. Los trabajos que tenía
  tomados vuelven a la cola cuando vence su lease (`Workers:Lease`, 2 minutos, medido con el reloj de la base).
  Un apagado ordenado los devuelve enseguida.
- **Un módulo en un nodo:** Docker lo reinicia. Mientras tanto, las llamadas de ese nodo fallan con un error
  reintentable y el trabajo vuelve a la cola.
- **La entrada:** keepalived mueve la IP virtual a otro nodo en 3 a 5 segundos. Con un proxy externo, depende de
  su propia HA.

## Volver a la ubicación preferida

Cuando el nodo preferido vuelve como réplica y está al día (`patronictl list` sin atraso), un `switchover` hacia
él en un horario tranquilo.
