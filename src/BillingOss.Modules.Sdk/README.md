# SDK de módulos

Biblioteca de C# (Apache-2.0) para escribir módulos de Hornero según el [protocolo de módulos v0](../../diseno/protocolo-modulos-v0.md). Resuelve la firma de los pedidos, el sobre, los errores y el registro de idempotencia. El autor del módulo escribe solo las operaciones de negocio.

## Uso

```csharp
var module = new ModuleDefinition(manifest)
    .Operation("provisioning", "create", Create)
    .Operation("provisioning", "get_status", GetStatus, mutating: false);

var store = Path.GetFullPath(app.Configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db", app.Environment.ContentRootPath);
app.MapModule(module, new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = store });
```

La SDK resuelve la ruta del archivo y crea su carpeta. Una ruta relativa se toma desde el directorio de trabajo, por eso el ejemplo la resuelve contra el directorio del módulo, como hacen ARCA y Mercado Pago. Con `Module__IdempotencyConnectionString` en la configuración, la SDK la toma por su cuenta y el archivo no se usa.

Las operaciones registradas con `mutating: true` (el valor por defecto) exigen `Idempotency-Key` y pasan por el registro de idempotencia. Las consultas no.

## Opciones

| Opción | Para qué |
|---|---|
| `SigningSecrets` | Obligatoria. Los secretos de firma vigentes, dos durante una rotación |
| `IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Hace falta para correr más de una réplica. Si está, se usa en lugar del archivo. Si falta, la SDK lee `Module:IdempotencyConnectionString` de la configuración de la aplicación |
| `IdempotencyStorePath` | Archivo SQLite del registro, para una sola réplica. Se usa si no hay cadena de conexión |
| `KeyRetention` | Cuánto se recuerda cada clave. Por defecto 8 días: el plazo de la operación más 7 días (sección 6 del protocolo) |
| `StaleRun` | Cuánto espera una clave en curso antes de darla por abandonada por un proceso caído. Por defecto 10 minutos, por encima del doble del plazo más largo del manifiesto. Se baja solo en pruebas |
| `MaxBodyBytes` | Tope del cuerpo del pedido. Por defecto 1 MiB |

Sin ninguna de las dos opciones del registro, `MapModule` falla al arrancar. Al arrancar, el módulo deja en el log cuál usa.

## Registro de idempotencia

Los dos registros tienen la misma semántica, y las pruebas de la SDK corren cada caso con los dos:

- La misma clave con la misma operación e `input` ejecuta una sola vez y devuelve siempre la misma respuesta.
- La misma clave con otra entrada da `409 idempotency_conflict`.
- Mientras una ejecución sigue en curso, otro pedido con la misma clave recibe `409 conflict`, reintentable y con `retry_after` de 5 segundos.
- Una clave en curso por más de `StaleRun` (10 minutos) se toma como abandonada por un proceso caído, y el siguiente pedido la vuelve a ejecutar. Cada toma queda marcada con su hora, así la ejecución vieja, si seguía viva, no pisa ni borra la respuesta de la nueva.
- Los errores definitivos se guardan. Un `ModuleException` reintentable (`upstream_unavailable`, `rate_limited`, `conflict`) suelta la clave, porque el módulo sabe que el efecto no ocurrió. Cualquier otra excepción o cancelación deja la clave en curso con un error en el log: el reintento recibe `409 conflict` hasta que venza, en vez de repetir un efecto que pudo ocurrir.
- El manejador de una operación con efecto recibe en `call.Aborted` un token que vence al doble del plazo que el manifiesto declara para la operación (`timeouts`, 30 segundos si no lo declara, tope 120), nunca el del pedido HTTP: que el núcleo corte no cancela un efecto a medias. Si vence, la respuesta es `504 outcome_unknown` y la clave sigue en curso.
- El registro no guarda `instance_state`: la respuesta repetida sale sin él. Un `ModuleException` con `InstanceState` lo manda en el cuerpo del error, así el núcleo guarda un ticket obtenido en una llamada que después falló.
- `result.secrets` se guarda cifrado con AES-GCM y una llave derivada del primer secreto de firma, atado a la clave. Al devolver la respuesta guardada se descifra con cualquiera de los secretos vigentes. Si se cifró con un secreto ya retirado, la respuesta sale sin `secrets` y queda un error en el log.
- Si el registro no responde después del efecto (por ejemplo, durante una conmutación de la base), la respuesta sale igual y queda un error en el log. La clave sigue en curso hasta que vence.
- Si al guardar o al soltar la fila ya no era de esta ejecución (otra réplica la retomó), queda un error en el log: el efecto pudo repetirse.

**SQLite** sirve para una sola réplica. El archivo va en un disco local persistente, nunca en una carpeta compartida por la red.

**PostgreSQL** sirve para varias réplicas. Tomar la clave es un solo `INSERT ... ON CONFLICT`, así que dos réplicas que reciben el mismo pedido a la vez no lo ejecutan las dos. Las horas salen de la base y no del reloj de cada réplica. La SDK crea la tabla `module_idempotency` al arrancar, con un lock de la base para que dos réplicas que arrancan juntas no choquen. La conexión va con `Command Timeout=0` porque ese timeout también cancela el COMMIT, y con replicación sincrónica un COMMIT cancelado queda confirmado solo en la primaria: la SDK escucha el WARNING con que PostgreSQL lo avisa, no ejecuta si llegó al tomar la clave y deja un error si llegó al guardar. Cómo armar la base y correr las réplicas está en [docs/alta-disponibilidad/modulos.md](../../docs/alta-disponibilidad/modulos.md).

## Salud

`GET /v0/health` contesta `ok` sin mirar nada: dice que el proceso vive. `GET /v0/ready` consulta el registro de idempotencia con un plazo de 2 segundos y da 503 si no responde: es el chequeo para el balanceador, porque una réplica que no llega a su base contesta 500 a toda operación con efecto. Los dos van sin firma.

## Firma

Cada pedido trae la firma HMAC del núcleo, que cubre la hora, el método, la ruta, `X-Request-Id`, `Idempotency-Key` y el cuerpo (sección 4 del protocolo). Un pedido con más de una línea de `X-Request-Id`, `X-Timestamp`, `X-Signature` o `Idempotency-Key` se rechaza con `401 invalid_signature`, porque la firma cubre una sola clave: si no, alguien podría repetir un pedido capturado sumándole otra.

## Pruebas

```
dotnet test tests/BillingOss.Modules.Tests
```

Las pruebas con PostgreSQL crean una base propia por prueba sobre la de `BILLINGOSS_TEST_PG` y la borran al terminar. Sin esa variable quedan inconclusas y las de SQLite corren igual.
