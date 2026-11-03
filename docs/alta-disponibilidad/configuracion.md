# Configuración de la aplicación para varios nodos

Qué ajustes mira el núcleo cuando corre en más de un nodo y contra una base con conmutación (D-07, D-09, D-14). Las recetas de infraestructura están en [docs/despliegue](../despliegue/README.md). Para los módulos con réplicas, ver [modulos.md](modulos.md).

## Conexión a la base

| Clave | Qué hace |
|---|---|
| `ConnectionStrings:Main` | La cadena de escritura. Con varios hosts (`Host=pg1,pg2,pg3`) la aplicación fija `Target Session Attributes=read-write` si no viene, y rechaza al arrancar cualquier otro valor que no sea `read-write` o `primary`. Detrás de una IP virtual o de HAProxy alcanza con un solo host. El `Command Timeout` sube a 90 s si viene menor, porque también acota el COMMIT: con replicación sincrónica, un COMMIT cancelado por timeout queda confirmado solo en la primaria. Con `0` queda sin límite. Es lo único que puede cancelar un COMMIT: `CommitGuard` lo corre siempre sin el token del pedido o del trabajo, también el implícito de `SaveChanges` |
| `ConnectionStrings:Replica` | Opcional. Cadena para leer de réplicas con `[FromKeyedServices(Failover.Replica)] BillingDbContext`. Con varios hosts prefiere un standby y reparte las conexiones, y la sesión es de solo lectura aunque caiga en la primaria. Solo para listados, reportes y exportaciones: nunca para casos de uso, trabajos, cobros, lo fiscal ni la autenticación, y una prueba de arquitectura lo verifica |
| `Database:Durability` | `single` (sin réplica), `sync` (Patroni o CloudNativePG con réplica sincrónica, se comprueba en `pg_stat_replication`) o `managed` (servicio administrado). Con `sync`, `/health/cluster` da 503 si no hay ninguna réplica sincrónica, y el vigía del clúster cierra el freno de las escrituras mientras PostgreSQL espere a una réplica que no tiene (ver abajo). Hace falta `pg_read_all_stats` en el rol de la aplicación, que `deploy/sql/roles.sql` ya da |
| `Database:MaxClockSkew` | Desvío tolerado entre el reloj del nodo y el de la base, 2 s por defecto. Lo mira `/health/cluster` |

Valores recomendados para la cadena principal con Patroni: `Timeout=5`, `Keepalive=10`, `Host Recheck Seconds=2` y `No Reset On Close=true` si hay PgBouncer. Nunca `Enable Retry On Failure` de EF: las transacciones explícitas lo rechazan y un cuerpo con commit incierto se repetiría.

En el rol de la aplicación, `ALTER ROLE billing SET statement_timeout = 0` y `SET synchronous_commit = on`. El código no cambia ninguno de los dos, no usa locks de sesión, `LISTEN`, `SET` sin `LOCAL`, `PREPARE`, cursores `WITH HOLD` ni tablas temporales, y una prueba de arquitectura lo exige: así PgBouncer en modo transacción sigue siendo compatible. Con PgBouncer, `query_timeout = 0`.

## Qué hace la aplicación ante una conmutación

- Las fallas de una conmutación (servidor apagado 57P01, 57P02 y 57P03, conexión rota o commit incierto de la clase 08, base en solo lectura 25006, ningún host que acepte escrituras) se reconocen como pasajeras. Un trabajo que las sufre vuelve a la cola en 5 segundos sin gastar un intento, y el pool de conexiones se vacía para que las nuevas busquen la primaria actual. En la API responden `503` con `Retry-After: 5`: el cliente repite con la misma clave de idempotencia. Nunca se reintenta adentro del cuerpo de un caso de uso. Los timeouts no entran: pueden ser una consulta pesada y van por los reintentos comunes.
- Ningún token de un pedido o de un trabajo cancela un COMMIT. Un cliente que corta, el latido que pierde el lease o el apagado cancelarían la espera de la réplica sincrónica y el COMMIT quedaría confirmado solo en la primaria, así que `CommitGuard` lo corre siempre con `CancellationToken.None`, también el implícito de `SaveChanges`. Solo lo acota el `Command Timeout` de la cadena (90 s o más), que supera lo que tarda el clúster en degradar una primaria aislada.
- Un COMMIT que PostgreSQL confirmó mientras cancelaba la espera de la réplica sincrónica (por ese timeout, `statement_timeout` o `pg_cancel_backend`) llega con un WARNING en el mismo COMMIT. `CommitGuard` lo escucha en cada transacción y lo convierte en `CommitUncertainException`, que es una falla pasajera: el trabajo se reintenta y la API da 503. Todo `SaveChanges` corre en una transacción explícita para que el aviso tenga dónde llegar. Lo confirmado así existe en la primaria y en ninguna réplica: si la primaria se pierde antes de que vuelva una, se corre la lista de [después de restaurar](../despliegue/backups.md#después-de-restaurar).
- Freno de las escrituras: con `Database:Durability=sync`, el vigía del clúster (`ClusterWatch`) mira cada 5 segundos si PostgreSQL está esperando a una réplica sincrónica que no tiene (`synchronous_standby_names` con valor y ninguna réplica en `sync` ni `quorum` en `pg_stat_replication`). Mientras sea así, el corredor no toma trabajos y la API contesta 503 con `Retry-After: 5` a toda escritura (todo lo que no sea GET, HEAD u OPTIONS): lo que se arrancara terminaría esperando hasta el `Command Timeout` y confirmado solo en la primaria. Las lecturas y la salud siguen. Se abre solo cuando vuelve una réplica o cuando el operador apaga el modo estricto, porque entonces la base deja de esperar. Lo que ya estaba en vuelo al cerrarse sigue el camino del punto anterior.
- Cada escritura de un trabajo va cercada: al confirmar cualquier transacción del manejador, `JobFence` comprueba en esa misma transacción que el trabajo sigue siendo de esta toma (`locked_by`, `attempts`, estado `Running` y lease vigente por el reloj de la base). Si no, el COMMIT no sale y el trabajo vuelve a la cola. Un nodo que perdió el lease (pausa de VM, partición, reloj corrido) no pisa lo que ya hizo el nodo que lo retomó: servicios, intentos de cobro, avisos, borradores, emisiones y series incluidas. Las escrituras compartidas (encolar, publicar un evento, auditar, guardar el estado de una instancia de módulo) abren su propia transacción si no hay ninguna, y el SQL suelto que un manejador corra sin transacción (`ExecuteSql`, `ExecuteUpdate`, `ExecuteDelete`) se comprueba contra la toma justo antes de ejecutarse.

## Trabajadores

Sección `Workers`:

| Clave | Por defecto | Qué hace |
|---|---|---|
| `Lease` | `00:02:00` | Lease de un trabajo tomado, medido con el reloj de la base (`now()`), así un nodo con el reloj corrido no retoma antes de tiempo ni retiene trabajos ajenos. Un latido lo renueva cada tercio. Si pasan dos tercios sin poder renovarlo, el manejador se corta. Es lo que tarda otro nodo en retomar lo de un nodo muerto |
| `ShutdownGrace` | `00:00:20` | Al apagar, cuánto pueden seguir los trabajos en curso antes de cortarlos y devolverlos a la cola sin gastar intento. Va por debajo de `Shutdown:Timeout` |
| `KeepDone` | `30.00:00:00` | Cuánto se guardan los trabajos hechos y los avisos de pasarela inválidos, que ya quedaron sin cuerpo al verificarse y no tienen eventos. Un trabajo diario (`platform.retention`) los borra en lotes. Los trabajos muertos quedan hasta que los resuelva el staff, y los avisos sin procesar también: son los que muestra el monitoreo |
| `KeepWebhookBodies` | `90.00:00:00` | Cuánto se guarda el cuerpo de los avisos de pasarela válidos ya procesados. Después la fila queda, con su estado y sus fechas, y el mismo trabajo diario vacía el cuerpo, que es lo que crece sin tope. Los avisos sin procesar conservan el cuerpo |
| `KeepGatewayEvents` | `400.00:00:00` | Cuánto se guardan los eventos verificados de las pasarelas, que son la unicidad de los avisos. Un aviso que se repita después de ese plazo se vuelve a procesar, y el pago único por canal e ID externo lo absorbe |

Reglas de la cola que no se configuran:

- Cada toma tiene su propia marca en `locked_by`. Terminar, fallar, reprogramar, extender y soltar solo tocan el trabajo si sigue siendo de esa toma. Un dueño viejo que termina tarde no cierra lo que ya corre otro nodo.
- Un nodo solo toma los tipos de trabajo que su binario sabe correr. En un despliegue rodante un nodo viejo no mata los trabajos nuevos, y un nodo sin llaves (`Secrets:KeyFile`) no arranca con ningún rol: el rol web las necesita para que las cookies y los formularios que firma valgan en todos los nodos.
- La clave de deduplicación evita duplicar un trabajo pendiente, en curso o hecho. Si el anterior murió, encolar la misma clave lo revive con los intentos en cero: una suspensión o una baja que falló de forma permanente no queda trabada para siempre.
- La corrida de facturación y la mora comparten la clave de entidad `billing`: corren de a una entre todos los nodos. La corrida (`billing.run`) solo reparte: encola un trabajo `billing.account` por cada cuenta con algo que renovar, con clave de deduplicación por fecha y cuenta, así una corrida repetida el mismo día no vuelve a encolar las cuentas ya repartidas. Esos trabajos corren en paralelo en todos los nodos y de a uno por cuenta (clave de entidad `account:{id}`). Cada uno renueva los servicios y los dominios de la cuenta y emite todos sus borradores de renovación, también los que dejó un intento cortado, así el reintento los termina. Una cuenta que falla reintenta sola y, si muere, queda a la vista del staff sin frenar a las demás.
- Las emisiones fiscales, el control diario de cada serie y la cotización pendiente de un comprobante (`fiscal.rate`) llevan la clave de su serie, `fiscal:{emisor}:{punto de venta}:{tipo}` (sección 9.4 del protocolo): una serie emite de a uno y en orden, y dos series no se esperan entre sí. La clave suelta `fiscal` queda para un documento sin serie resuelta, por ejemplo sin emisor activo. La conciliación (`platform.reconcile`), que el planificador encola cuando ve una línea de tiempo nueva en la base ([backups.md](../despliegue/backups.md#después-de-restaurar)), no se cruza con ellas: mientras está pendiente o corriendo, los trabajos fiscales de todas las series esperan, y ella espera a que terminen los que ya corrían.
- Los índices de la cola son parciales sobre lo vivo (`Pending` y `Running`), con el mismo predicado que la toma: no crecen con el historial.

## Apagado

| Clave | Por defecto | Qué hace |
|---|---|---|
| `Shutdown:Timeout` | `00:00:30` | `HostOptions.ShutdownTimeout`. Mayor que `Workers:ShutdownGrace`. El plazo del orquestador (`stop_grace_period`, `TimeoutStopSec`, `terminationGracePeriodSeconds`) va por encima de los dos |
| `Shutdown:DrainDelay` | `00:00:00` | Con la señal de apagado, `/health/ready` pasa a 503 y el proceso sigue atendiendo este rato para que el balanceador lo saque antes de cerrar. Va por encima de lo que el balanceador tarda en dar por caído al nodo: en HAProxy, `inter` por `fall` más el timeout del chequeo (2 s por 3 en la receta, así que 8 s en Compose) |

Con la señal, el corredor no toma nada nuevo, da `ShutdownGrace` a lo que corre y devuelve a la cola lo que tuvo que cortar, ya y sin contar el intento. El cierre de cada trabajo se guarda sin token, aunque el proceso se esté apagando, y reintenta unos segundos si la base está conmutando.

## Salud

| Ruta | Qué mira | Para qué |
|---|---|---|
| `/health/live` | Nada | Vivo. Una conmutación de la base no reinicia los nodos |
| `/health/startup` | Esquema al día con el binario (sin migraciones pendientes) y el clúster | Un nodo nuevo no recibe tráfico ni toma trabajos hasta que las migraciones terminaron (D-14, migraciones en dos pasos) |
| `/health/ready` | Esquema, que la base acepte escrituras (`pg_is_in_recovery()` falso y `transaction_read_only` apagado) y que el proceso no se esté apagando | El balanceador no manda pedidos a un nodo sin primaria |
| `/health/cluster` | Réplica sincrónica con `Database:Durability=sync` y desvío de reloj | Solo para el arranque y la alerta, nunca en listo: si fallara en listo sacaría a todos los nodos del balanceador a la vez |

## Data Protection

Con `Secrets:KeyFile`, el anillo de llaves de ASP.NET Data Protection (cookies, antiforgery, tokens de vida corta) se guarda en `platform.data_protection_keys`, con el secreto de cada llave cifrado con la llave maestra (D-09) y el nombre de aplicación `billing-oss`. Una cookie que emitió un nodo la acepta cualquier otro, y un backup de la base no alcanza para falsificarla. El anillo se lee de la primaria y se refresca cada 24 horas, y las llaves se crean dos días antes de activarse: alcanza. La tabla va en la misma migración que el resto, así que el listo con migraciones pendientes la cubre.

## Avisos de pasarela

`Webhooks:PermitLimitPerNode` (120 por defecto) es el tope de avisos por instancia, por minuto y por nodo. Con N nodos el tope total es N veces ese valor, y el global va en el balanceador. Solo las instancias que ya recibieron un aviso válido en el nodo tienen partición propia: un ID inventado en la URL cae en una sola partición compartida y no crea limitadores. Además hay un tope por IP para todo `/webhooks`. Un aviso rechazado no se pierde: la pasarela lo reintenta y la conciliación horaria lo encuentra.

Cada aviso guarda hasta 64 KiB de cuerpo hasta que el módulo lo verifica, y la URL es pública (va en cada preferencia de cobro). Al tope, una instancia puede meter 120 avisos de 64 KiB por minuto por nodo: unos 10 GiB por día por instancia y por nodo, en la primaria y en las réplicas, mientras el módulo que verifica esté caído. Con eso se dimensiona el disco de la base. Verificados, los inválidos quedan sin cuerpo y se borran a los `Workers:KeepDone` días, y los válidos vacían el cuerpo a los `Workers:KeepWebhookBodies` días.

## Encabezados reenviados

Detrás de un proxy que termina TLS, `ForwardedHeaders:KnownProxies` (IP o red CIDR) o `ForwardedHeaders:KnownNetworks` (redes CIDR) dicen a quién creerle `X-Forwarded-For` y `X-Forwarded-Proto`. Sin ninguno configurado no se le cree a nadie. Hace falta para la IP de las listas de las API keys y los topes por IP, y para que la interfaz vea el pedido como HTTPS (las cookies con `SecurePolicy=Always` dan 500 si el pedido llega por HTTP). Va antes del limitador y de la interfaz.
