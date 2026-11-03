# Alta disponibilidad de Hornero

Cómo corre el núcleo en varios nodos y contra una base con conmutación sin perder ni duplicar un comprobante ni un pago (D-07 y D-14). Las recetas de instalación están en [docs/despliegue](../despliegue/README.md). Acá va lo que la aplicación espera y lo que hace ante cada falla.

- [configuracion.md](configuracion.md): claves de configuración, cola de trabajos, salud, apagado, Data Protection y avisos.
- [modulos.md](modulos.md): módulos con varias réplicas y el registro de idempotencia en PostgreSQL.

## Topologías soportadas

| Topología | Base | Para qué |
|---|---|---|
| Receta chica | Un PostgreSQL con TLS, sin réplica, con copias periódicas y PITR | Una instalación chica. Sin conmutación automática: ante una caída se restaura y se corre la conciliación |
| Alta disponibilidad en VMs | Patroni con replicación sincrónica estricta, etcd, pgBackRest, HAProxy o IP virtual, dos o más nodos web y worker | Cualquier proveedor que emite comprobantes. Es la única con RPO 0 |
| Base externa | Patroni o CloudNativePG del cliente, o un servicio administrado con HA sincrónica | Cuando la base la opera otro. Se exige el contrato de [base-externa.md](../despliegue/base-externa.md) |

Dos nodos con datos más un árbitro, con el modo estricto apagado, no sirven para una instalación que emite comprobantes: cuando cae el standby la primaria sigue confirmando con una sola copia y lo confirmado en esa ventana se pierde si la primaria pierde el disco. Queda fuera del soporte.

Los nodos de aplicación son homogéneos: todos corren el mismo binario, tienen la llave de los secretos y toman los mismos tipos de trabajo. Los nodos web y worker se separan por rol, no por versión.

## Qué pasa ante cada falla

| Falla | Qué hace la aplicación | Qué se pierde |
|---|---|---|
| Cae la primaria y Patroni promueve una réplica sincrónica | Las conexiones se cortan (57P01 o clase 08). Los trabajos en curso vuelven a la cola en 5 segundos sin gastar intento, el pool se vacía y las conexiones nuevas buscan la primaria por `Target Session Attributes=read-write`. La API responde 503 con `Retry-After` | Nada confirmado. Una emisión o un cobro a medias se retoman: ARCA y la pasarela son el oráculo (`find` y `get_payment`) |
| La primaria vieja queda en pie, aislada, y pasa a solo lectura | Cada escritura da 25006: falla pasajera, misma respuesta que arriba. `/health/ready` da 503 porque `pg_is_in_recovery()` o `transaction_read_only` lo dicen, y el balanceador saca al nodo que todavía la mira | Nada |
| Un COMMIT cancelado mientras espera a la réplica (`Command Timeout`, `statement_timeout`, un operador) | PostgreSQL lo confirma solo en la primaria y avisa con un WARNING. `CommitGuard` lo convierte en falla pasajera: el trabajo se reintenta y la API da 503. Ningún token de un pedido o de un trabajo cancela un COMMIT (un cliente que corta, el lease perdido o el apagado no lo tocan), el `Command Timeout` no baja de 90 s y el rol tiene `statement_timeout = 0` | Lo confirmado en esa ventana existe solo en la primaria y puede desaparecer con la promoción. El reintento lo rehace con la misma clave, y si la primaria se pierde en esa ventana se corre la lista de después de restaurar |
| Las dos réplicas caídas, o la primaria aislada de las dos | El vigía del clúster (`Database:Durability=sync`) cierra el freno de las escrituras en segundos: el corredor no toma trabajos y la API da 503 con `Retry-After` a las escrituras. Lo que ya estaba en vuelo espera hasta el `Command Timeout` y sigue la fila de arriba ([conmutacion.md](../despliegue/conmutacion.md#las-dos-réplicas-caídas)) | Nada de lo que se arranca con el freno cerrado. Lo que estaba en vuelo, solo si la primaria se pierde antes de que vuelva una réplica |
| Un nodo se cuelga o se aísla con un trabajo tomado | Vence el lease (2 minutos por el reloj de la base) y otro nodo lo retoma. Si el nodo viejo despierta, su latido ve que perdió el lease y corta el manejador, y la cerca de escrituras no deja que confirme nada | Nada: el trabajo corre una sola vez con efecto |
| Reloj corrido en un nodo | El lease y la toma usan `now()` de la base. `/health/cluster` avisa si el desvío pasa `Database:MaxClockSkew` | Nada. Los módulos rechazan la firma si el desvío pasa 300 s |
| Un trabajo tomado cuya fila no se puede leer (un dato que el código no entiende) | La toma lo devuelve a la cola como intento fallido, con el error en `last_error` y espera creciente, y sigue con el próximo. Al agotar los intentos queda muerto y visible. Nunca queda en `Running` hasta vencer el lease una y otra vez | Nada: el trabajo se reintenta o lo ve el staff |
| Despliegue rodante | Con la señal el nodo no toma nada nuevo, da `Workers:ShutdownGrace` a lo que corre, devuelve lo que cortó sin gastar intento y `/health/ready` pasa a 503 durante `Shutdown:DrainDelay`. Un nodo viejo no toma tipos de trabajo nuevos. Un nodo nuevo no da listo hasta que las migraciones terminaron | Nada |
| Un nodo sin la llave de los secretos | No arranca con ningún rol: el rol web la necesita para que las cookies y los formularios que firma valgan en todos los nodos | Nada: antes marcaba hechas las emisiones sin emitir, y un nodo web sin llave dejaba caer las sesiones al rotar |
| Cae un módulo o una de sus réplicas | El trabajo reintenta con espera creciente. Con el registro de idempotencia en PostgreSQL, el reintento que cae en otra réplica no repite el efecto, y un ticket obtenido en una llamada que falló viaja en el error y el núcleo lo guarda igual ([modulos.md](modulos.md)) | Nada mientras el registro no se rebobine |
| Restauración a un punto anterior (PITR) o promoción asincrónica | Los comprobantes y pagos posteriores al punto existen en ARCA y en la pasarela y no en la base. El planificador ve la línea de tiempo nueva y encola la conciliación desde la última marca, y el comando `reconcile` la corre a mano con `--since`, `--counter-gap` y `--dry-run` ([backups.md](../despliegue/backups.md#después-de-restaurar)): adopta los comprobantes que coinciden con un solo documento, registra los huérfanos y los ambiguos en `platform.reconcile_findings`, deja los pagos sin intento en `payments.unmatched`, salta la numeración comercial y lista lo que los módulos tienen con marca y sin servicio | Lo posterior al punto que ningún sistema externo recuerda, por ejemplo un pedido que nunca llegó a cobrarse. Lo demás queda a la vista con alerta hasta que el staff lo resuelve |

## Cómo se prueba

`tests/BillingOss.App.Tests` corre contra un PostgreSQL de prueba y simula las conmutaciones con la base misma: cortar todas las sesiones (`pg_terminate_backend`), pasar la base a solo lectura y volver cuando el corredor choca con ella al cerrar el trabajo (un orden y no un plazo, así la prueba no depende de lo cargada que esté la máquina), cortar justo después de un COMMIT, dos nodos compitiendo por la cola con la primaria cayendo en el medio, un COMMIT que avisa que no llegó a la réplica, un lease robado mientras el manejador escribe, el apagado con trabajos en curso, y el registro de idempotencia de los módulos con dos instancias sobre la misma base. Las pruebas de laboratorio sobre un clúster real (docker pause, partición, `pg_cancel_backend`) están en la guía de despliegue.
