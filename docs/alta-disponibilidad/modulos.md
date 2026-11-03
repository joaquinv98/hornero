# Módulos con varias réplicas

Los módulos son servicios aparte que el núcleo llama por HTTPS (decisión D-08 y [protocolo v0](../../diseno/protocolo-modulos-v0.md)). Esta guía dice cómo correr un módulo escrito con la [SDK](../../src/BillingOss.Modules.Sdk/README.md) en más de una réplica sin repetir un efecto: una VM, un cobro, un reembolso o un comprobante.

La regla es una sola: **todas las réplicas comparten el registro de idempotencia en PostgreSQL**. Con el registro en SQLite, cada réplica tiene el suyo, y un reintento que cae en otra réplica repite el efecto.

## Qué garantiza

- El mismo pedido (misma `Idempotency-Key`, operación e `input`) se ejecuta una sola vez entre todas las réplicas. Si dos lo reciben a la vez, una ejecuta y la otra contesta `409 conflict` reintentable con `retry_after` de 5 segundos. Cuando la primera termina, cualquier réplica devuelve la respuesta guardada.
- La misma clave con otra entrada da `409 idempotency_conflict`, en cualquier réplica.
- Las horas salen de la base, así que un reloj corrido en una réplica no cambia cuándo vence una clave.
- Si una réplica se cae a mitad de una ejecución, la clave queda en curso. A los 10 minutos otra réplica la puede retomar, y la ejecución vieja, si seguía viva, ya no pisa ni borra lo que guarde la nueva.
- Si la base no responde justo después del efecto, por ejemplo en plena conmutación, la réplica devuelve igual la respuesta al núcleo y deja un error en el log. Así el núcleo no reintenta algo que ya se hizo.
- La clave se suelta solo cuando el manejador devuelve un error reintentable del protocolo (`upstream_unavailable`, `rate_limited`, `conflict`): ahí el módulo sabe que el efecto no ocurrió. Ante cualquier otra falla o cancelación la clave queda en curso 10 minutos, con un error en el log, y el reintento del núcleo recibe `409 conflict` en cualquier réplica en vez de repetir el efecto.
- El manejador de una operación con efecto no se corta cuando el núcleo se desconecta. Su token (`call.Aborted`) vence al doble del plazo que declara el manifiesto para la operación. Si vence, la respuesta es `504 outcome_unknown` y la clave sigue en curso.
- Las claves se guardan y se olvidan con un Command Timeout de 0: con replicación sincrónica, un COMMIT cancelado por timeout queda confirmado solo en la primaria y PostgreSQL lo informa como éxito con un WARNING. La SDK escucha ese aviso: si llega al tomar la clave, no ejecuta y contesta `409 conflict` para que el núcleo reintente. Si llega al guardar, deja un error en el log porque el reintento tras la conmutación repetiría el efecto.
- Las credenciales que el sistema externo genera (`result.secrets`) se guardan cifradas con AES-GCM y una llave derivada del secreto de firma, atadas a la clave. El registro se replica y entra en los backups, y ahí nunca hay credenciales en claro. Después de una rotación, una respuesta guardada con el secreto ya retirado sale sin `result.secrets` y con un error en el log.
- Un pedido con dos encabezados `Idempotency-Key` se rechaza, para que nadie repita un pedido capturado cambiándole la clave.

Las pruebas de `tests/BillingOss.Modules.Tests` lo demuestran con dos instancias del módulo contra la misma base: un mismo pedido repartido entre las dos, veinte pedidos iguales a la vez, una ejecución en curso en una mientras llega a la otra, una toma abandonada que retoma la otra y una base que se cae después del efecto. Cada caso corre también con SQLite, para mostrar que la semántica es la misma.

## Qué no cubre

- **Una réplica que muere después del efecto y antes de guardar.** El núcleo recibe un 409 mientras la clave está en curso, y pasados los 10 minutos el reintento vuelve a ejecutar. La defensa está en el módulo: en `create` y `register`, la marca en el sistema externo y la consulta antes de crear (sección 6, punto 4 del protocolo). En los reembolsos, la clave que se pasa a la pasarela, como hace Mercado Pago con `X-Idempotency-Key`.
- **Una restauración a un punto anterior (PITR).** Rebobina el registro junto con el núcleo: las claves ejecutadas después de ese punto se olvidan y el reintento repite el efecto. El registro no es defensa contra eso. Lo son la marca externa y la conciliación de D-14.
- **Un estado de instancia que el núcleo no llega a guardar.** Una respuesta de error lleva `instance_state` igual que una buena, y el núcleo lo guarda en los dos casos: un ticket de WSAA que ARCA obtiene en una llamada que después falla llega a la base y la llamada siguiente lo lleva, caiga en la réplica que caiga. Lo que queda afuera es que el núcleo no pueda guardarlo (una versión de estado pisada por otra llamada, una conexión cortada): ahí el ticket queda en la memoria de la réplica que lo pidió, y otra réplica espera a WSAA hasta que venza. Es una demora, no un duplicado.

## Requisitos

1. **Una base y un rol propios del módulo**, nunca la base del núcleo (D-08). El módulo fiscal va en su propia base, separada de la de los demás módulos. Pueden estar en el mismo clúster de PostgreSQL que el núcleo, con la replicación sincrónica de D-14: así una conmutación no pierde una clave ya confirmada. Una restauración del clúster a un punto anterior sí rebobina el registro, y las claves ejecutadas después de ese punto se olvidan. Ahí defienden la marca en el sistema externo y la conciliación posterior de D-14.
2. **Cadena de conexión que siga a la primaria.** Con Patroni u otro clúster de varios nodos, se ponen todos los hosts y `Target Session Attributes=read-write`, y la conexión va siempre a la primaria, también después de una conmutación. Si hay una IP virtual o un balanceador que apunta siempre a la primaria, alcanza con ese nombre.
3. **El mismo secreto de firma en todas las réplicas.** En una rotación, todas reciben el secreto nuevo antes de cambiar el núcleo.
4. **El mismo certificado TLS.** Si la instalación del módulo en el núcleo tiene huella, el núcleo compara el certificado exacto y todas las réplicas tienen que presentar el mismo. Si el certificado lo firma una CA en la que confía el núcleo, alcanza con que sea válido para el nombre.
5. **Reloj sincronizado por NTP.** La firma se rechaza si la hora difiere en más de 300 segundos.
6. **Un nombre delante de las réplicas**: un balanceador o un nombre DNS con varias direcciones. Esa es la URL de la instalación en el núcleo. El núcleo renueva sus conexiones cada 5 minutos, así que una réplica nueva empieza a recibir pedidos sola.
7. **Tiempo para apagar.** Al actualizar o mover una réplica, el orquestador le da un plazo de apagado que alcance para terminar lo que está en curso. El host .NET espera por defecto 30 segundos a que terminen los pedidos abiertos (`HostOptions.ShutdownTimeout`), mucho menos de lo que puede durar una llamada de ARCA. La regla: `DOTNET_shutdownTimeoutSeconds` va por encima del doble del plazo más largo que el manifiesto declara para una operación con efecto (el manejador tiene ese doble, ver arriba) y también por encima del plazo más largo de una operación de lectura. En ARCA, `authorize` declara 90 s y corre hasta 180, y `exchange_rate` 120, así que `200`. El plazo del orquestador va por encima de ese valor: `stop_grace_period: 210s` en Compose, `docker stop -t 210`, `terminationGracePeriodSeconds: 210` en Kubernetes, `TimeoutStopSec=210` en systemd. Una réplica que se mata a mitad de una ejecución deja la clave en curso por 10 minutos, con el núcleo recibiendo `409 conflict` hasta entonces.
8. **Sin timeouts que cancelen un COMMIT.** En el rol del módulo, `ALTER ROLE mod_x SET statement_timeout = 0` y `SET synchronous_commit = on`. Si hay PgBouncer delante, `query_timeout = 0`. Con cualquiera de esos cancelando la espera de la réplica, una clave confirmada solo en la primaria se pierde en la conmutación.

## Preparar la base

Con un usuario administrador del clúster, por ejemplo para Mercado Pago:

```sql
CREATE ROLE mod_mercadopago LOGIN;
\password mod_mercadopago
CREATE DATABASE billing_mod_mercadopago OWNER mod_mercadopago;
REVOKE ALL ON DATABASE billing_mod_mercadopago FROM PUBLIC;
ALTER ROLE mod_mercadopago SET statement_timeout = 0;
ALTER ROLE mod_mercadopago SET synchronous_commit = on;
```

`\password` de `psql` pide la contraseña sin dejarla en el historial ni en un archivo. La contraseña se guarda en la bóveda. El `REVOKE` deja la base solo para su rol: sin él, el núcleo y los otros módulos podrían conectarse (D-08).

El permiso de red va en `pg_hba.conf`, una línea por módulo y solo desde las réplicas:

```
hostssl billing_mod_mercadopago mod_mercadopago 192.0.2.21/32 scram-sha-256
hostssl billing_mod_mercadopago mod_mercadopago 192.0.2.22/32 scram-sha-256
```

Con Patroni no se edita `pg_hba.conf` a mano, porque lo pisa: la línea va en `postgresql.pg_hba` de `patroni.yml` o en la configuración dinámica (`patronictl edit-config`), y se recarga sin reiniciar.

El rol es dueño de su base porque la SDK crea la tabla `module_idempotency` la primera vez que arranca. No hace falta más: cada réplica la crea si falta, bajo un lock, y al arrancar borra las claves vencidas.

## Configurar las réplicas

Cada réplica recibe la misma configuración por variables de entorno:

| Variable | Valor |
|---|---|
| `Module__IdempotencyConnectionString` | La cadena de conexión a la base del módulo. Tiene la contraseña: sale de la bóveda al desplegar y nunca va en un archivo del repo |
| `Module__SigningSecret` | El secreto de firma de la instalación, el mismo en todas |
| `Module__PreviousSigningSecret` | Solo durante una rotación |
| `ASPNETCORE_URLS` | Dónde escucha cada réplica |

La cadena de conexión tiene esta forma:

```
Host=pg1.interna,pg2.interna,pg3.interna;Database=billing_mod_mercadopago;Username=mod_mercadopago;Passfile=/run/secrets/pgpass;Target Session Attributes=read-write;SSL Mode=VerifyFull;Root Certificate=/run/secrets/pg-ca.crt
```

La contraseña no va en la cadena: `Passfile` apunta a un archivo `pgpass` (`host:puerto:base:usuario:contraseña`, modo 0600) montado como secreto desde la bóveda. Si se prefiere ponerla en la cadena, es `Password="<de la bóveda>"`, con comillas si tiene `;` o `=`. `Root Certificate` es la CA que firmó el certificado del clúster: con una CA interna, sin ese archivo el módulo falla al arrancar por TLS. Solo si lo firma una CA pública alcanza con el almacén del sistema.

La SDK toma `Module__IdempotencyConnectionString` de la configuración de la aplicación por su cuenta, así que vale para todo módulo hecho con ella, no solo para ARCA y Mercado Pago. Con la variable puesta, el módulo ignora `Module__IdempotencyStorePath` y no crea el directorio del archivo, así puede correr con el sistema de archivos de solo lectura. La SDK arma la conexión con `Command Timeout=0` aunque la cadena diga otra cosa. Al arrancar deja en el log `Registro de idempotencia en PostgreSQL: admite varias réplicas.`

El chequeo de salud del balanceador es `GET /v0/ready`, sin firma: consulta el registro con un plazo de 2 segundos y da 503 si esa réplica no llega a su base, porque entonces contestaría 500 a toda operación con efecto y le gastaría intentos al núcleo. `GET /v0/health` solo dice que el proceso vive, para reiniciarlo.

## Pasar de SQLite a PostgreSQL

El registro nuevo arranca vacío y no se copian las claves del archivo. Por eso el núcleo deja de llamar al módulo antes del cambio, para que ninguna clave ya ejecutada vuelva a llegar a un registro que no la conoce:

1. Parar los nodos del núcleo con `Role=worker`. Si el módulo también recibe llamadas desde los nodos web (pasarelas de cobro), hacerlo en una ventana de mantenimiento con los nodos web parados.
2. Confirmar que no quedó nada a medias: en `platform.jobs`, trabajos con `kind` en `fiscal.emit`, `services.operation` o `payments.*` y `status` en `Pending` o `Running`, o `Pending` con `attempts > 0`. Esos son los que van a reintentar con una clave que el registro viejo ya ejecutó: se esperan o se revisan antes de seguir.
3. Parar en orden la réplica con SQLite (con el plazo de apagado del requisito 7).
4. Arrancar las réplicas con `Module__IdempotencyConnectionString`.
5. Volver a levantar los nodos del núcleo.
6. Guardar el archivo SQLite viejo hasta que pase la retención de las claves (8 días), por si hace falta revisar una respuesta, y borrarlo después: guarda respuestas.

## SQLite

Sigue siendo la opción de la receta chica, con una sola réplica. El archivo va en un disco local persistente con permisos solo para el usuario del servicio. Nunca en NFS ni en SMB: SQLite no garantiza los locks sobre una carpeta compartida por la red, y dos réplicas sobre el mismo archivo remoto podrían ejecutar las dos.
