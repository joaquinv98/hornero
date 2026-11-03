# Integraciones: webhooks, extensiones y API compatible

Esta guía es para quien conecta Hornero con otros sistemas: un flujo de n8n, un CRM, un bot o un chat. Hay
tres formas, de la más simple a la más potente:

| Necesitás | Usá | Sección |
|---|---|---|
| Enterarte de lo que pasa (una factura paga, un servicio suspendido) | Un webhook saliente | 1 |
| Cambiar cómo se comporta el sistema sin tocar su código (frenar un pedido, reaccionar a un evento con lógica propia) | Una extensión | 2 |
| Que una integración que hablaba con el sistema anterior siga andando | La API compatible | 3 |

Las tres usan una llave de la API cuando tienen que leer o cambiar algo en Hornero: se emite desde el mismo
panel, como cuenta la sección 4.

El diseño completo, con el modelo, los estados y los trabajos, está en
[diseno/webhooks-y-extensiones.md](../../diseno/webhooks-y-extensiones.md).

---

## 1. Webhooks salientes

### 1.1 Suscribirse

1. Entrá al admin con un usuario del staff que tenga el permiso `integrations.manage` (se da con
   `staff add correo "Nombre" integrations.manage`).
2. Abrí **API y webhooks** en el menú (`/admin/configuracion/webhooks`).
3. Cargá la URL de destino, una descripción y los eventos, y apretá **Agregar el webhook**.
4. Copiá el secreto `whsec_...` que aparece en ese momento y guardalo en tu integración. No se vuelve a mostrar.
   Si lo perdés, rotalo desde la ficha del webhook.

La URL tiene que ser `https`. `http` se acepta solo hacia el mismo equipo (`127.0.0.1`), para pruebas. No hace
falta esconder un token en la URL: la que autentica cada mensaje es la firma, y la URL se ve en el panel y en la
auditoría.

### 1.2 Eventos

| Tipo | `data` | Cuándo |
|---|---|---|
| `order.placed` | `order_id` | Se confirmó un pedido |
| `invoice.issued` | `invoice_id` | Se emitió una factura |
| `invoice.paid` | `invoice_id` | La factura quedó paga |
| `credit_note.issued` | `credit_note_id` | Se emitió una nota de crédito |
| `payment.received` | `payment_id` | Entró un pago |
| `fiscal_document.authorized` | `document_id` | El fisco autorizó el comprobante de una factura o nota |
| `service.activated` | `service_id` | El servicio quedó activo después del alta |
| `service.reactivated` | `service_id` | Se levantó una suspensión |
| `service.suspended` | `service_id` | Se suspendió |
| `service.terminated` | `service_id` | Se dio de baja |
| `ticket.opened` | `ticket_id`, `message_id` | Se abrió un ticket |
| `ticket.replied` | `ticket_id`, `message_id` | Hubo una respuesta en un ticket |

Los mensajes llevan solo identificadores, nunca datos personales. El resto se lee con la API propia
(`GET /api/v1/documents/{id}`, `GET /api/v1/accounts/{id}`) o con la compatible (`GetInvoice`,
`GetClientsDetails`), con una API key que tenga el permiso que corresponde.

### 1.3 El mensaje

Un `POST` con `Content-Type: application/json`, el `User-Agent` `billing-oss-webhooks` y estos encabezados, que
siguen la especificación abierta Standard Webhooks:

| Encabezado | Contenido |
|---|---|
| `webhook-id` | `msg_` y el número de la entrega. Es el mismo en cada reintento y en un reenvío |
| `webhook-timestamp` | Segundos Unix del momento en que salió |
| `webhook-signature` | `v1,` y la firma en base64 |

```json
{ "type": "invoice.paid", "timestamp": "2026-11-02T15:00:00.000Z", "data": { "invoice_id": 4521 } }
```

`timestamp` dice cuándo pasó el evento, que puede ser antes que el envío si hubo reintentos.

### 1.4 Verificar la firma

La firma es el HMAC-SHA256 de `{webhook-id}.{webhook-timestamp}.{cuerpo}`, con los bytes del secreto que siguen a
`whsec_` decodificados de base64. El cuerpo se firma tal cual llegó: verificá antes de parsearlo. Cualquier
librería de Standard Webhooks lo hace sola. A mano, en C#:

```csharp
static bool Verify(string secret, string id, string timestamp, byte[] body, string signatures)
{
    if (!long.TryParse(timestamp, out var ts) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
    {
        return false; // fuera de los cinco minutos: puede ser un mensaje repetido por otro
    }

    var key = Convert.FromBase64String(secret["whsec_".Length..]);
    var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.").Concat(body).ToArray();
    var expected = Encoding.ASCII.GetBytes("v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signed)));
    return signatures.Split(' ').Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), expected));
}
```

Y en Python, para un nodo de código de n8n o un script:

```python
import base64, hashlib, hmac, time

def verify(secret, msg_id, timestamp, body, signatures):
    if abs(time.time() - int(timestamp)) > 300:
        return False
    key = base64.b64decode(secret[len("whsec_"):])
    digest = hmac.new(key, f"{msg_id}.{timestamp}.".encode() + body, hashlib.sha256).digest()
    expected = "v1," + base64.b64encode(digest).decode()
    return any(hmac.compare_digest(expected, s) for s in signatures.split(" "))
```

### 1.5 Responder, reintentos y orden

- Respondé un 2xx apenas guardes el mensaje y procesalo después. Billing-oss espera 15 segundos y no lee la
  respuesta.
- Cualquier otra respuesta, un corte o una demora se reintentan con espera creciente: 30 intentos, casi un día.
  Después la entrega queda **Fallida** y se puede reenviar desde el panel.
- No se siguen redirecciones: si cambiaste la URL, actualizala en el panel.
- La entrega es **al menos una vez**. Usá `webhook-id` para no procesar dos veces el mismo mensaje.
- Lo que le pasa a una misma entidad llega en orden: `invoice.issued` antes que `invoice.paid` de la misma
  factura. Entre entidades distintas no hay orden.
- Un webhook apagado no recibe nada, y lo que tenía pendiente queda **Cancelado**.

### 1.6 Probar y diagnosticar

En la ficha de cada webhook:

- **Mandar una prueba** manda un `webhook.test` con `data` vacío, solo a ese destino.
- **Entregas** muestra las últimas 50, con el estado, los intentos, el código HTTP y el último error. Las
  entregas se guardan 30 días.
- **Reenviar** vuelve a mandar una entrega entregada, fallida o cancelada, con el mismo `webhook-id`.
- **Rotar el secreto** lo reemplaza en el momento. Lo que falle hasta que lo cambies en tu integración se reintenta
  solo.

Todo cambio queda en la auditoría (`webhooks.create`, `webhooks.update`, `webhooks.rotate`, `webhooks.test`,
`webhooks.resend` y `webhooks.delete`), nunca con el secreto.

---

## 2. Extensiones

Una extensión es un módulo propio, del tipo `extension` del [protocolo de módulos](../../diseno/protocolo-modulos-v0.md)
(sección 9.5). Corre aparte, en cualquier lenguaje, y Hornero la llama:

- **Cuando pasa algo** (`on_event`), con los mismos eventos de los webhooks. Corre después del cambio, por la cola,
  con reintentos. Nunca frena al que lo originó.
- **Antes de confirmar un pedido** (`validate_order`). Puede rechazarlo con un motivo que ve el cliente.

Conviene una extensión en lugar de un webhook cuando hace falta decidir algo (frenar un pedido) o cuando la lógica
no entra en un flujo de n8n. Para cambiar datos del sistema, la extensión usa la API propia con su propia API key,
como cualquier integración.

### 2.1 El contrato

Todas las llamadas van firmadas con el secreto de la instalación, como las de cualquier módulo. La SDK de C#
(`BillingOss.Modules.Sdk`) resuelve la firma, el sobre y la idempotencia: el autor escribe solo las funciones.

**`on_event`** recibe el evento en `input` y el contexto vacío:

```json
{ "id": "6f1c0a3e9b7d4c2a8e5f1b3d7c9a2e4f", "type": "service.activated", "occurred_at": "2026-11-02T15:00:00.000Z", "data": { "service_id": 3307 } }
```

Lleva la clave de idempotencia `hook-{gancho}-{evento}`: si Hornero reintenta, la SDK devuelve lo mismo sin
repetir el efecto. Responde `completed`. Un error reintentable (`upstream_unavailable`, `rate_limited`, `conflict` o
`internal`) vuelve a la cola, y uno definitivo (`invalid_input`, `invalid_config`) deja el evento con error y a la
vista en la ficha de la extensión.

**`validate_order`** recibe la cuenta y las líneas, sin datos de contacto ni de pagos:

```json
{
  "context": { "account": { "id": 812, "language": "es", "country": "AR", "services": [ { "id": 3307, "product_id": 41, "status": "active" } ] } },
  "input": { "items": [ { "product_id": 41, "plan_id": 77, "cycle": { "unit": "free", "count": 1 }, "quantity": 1, "options": {}, "order": {} } ] }
}
```

Responde `completed` con `{ "allowed": true }` o `{ "allowed": false, "reason": "..." }`. El motivo lo ve el
cliente: escribilo en el idioma de `account.language`, en texto plano y en menos de 300 caracteres. Tiene que
contestar rápido: Hornero espera lo que declare el manifiesto en `timeouts.validate_order`, 5 segundos si no
declara nada y 10 como máximo.

Si la extensión no contesta, decide la casilla **Si la extensión no responde, rechazar el pedido** de su ficha:
prendida, el pedido se rechaza con "No pudimos validar el pedido en este momento. Probá de nuevo en unos minutos."
Apagada, el pedido se confirma igual. En los dos casos queda `extensions.validate_failed` en la auditoría, y cada
rechazo queda como `extensions.order_rejected`.

### 2.2 Ejemplo completo: una sola prueba gratis por cuenta

Un caso común: una sola prueba gratis por cliente, que en otros sistemas suele resolver un hook de carrito. Con
Hornero es la extensión [`src/modules/BillingOss.Module.OneTrial`](../../src/modules/BillingOss.Module.OneTrial),
que sirve de plantilla para cualquier otra.

**El manifiesto** declara el tipo, las dos capacidades y la configuración que necesita:

```csharp
["kinds"] = new JsonArray("extension"),
["capabilities"] = new JsonArray("extension.validate_order", "extension.events"),
["config_schema"] = /* trial_product_ids: los productos que son prueba gratis */,
["timeouts"] = new JsonObject { ["validate_order"] = 3 },
```

**La validación** cuenta cuántas pruebas pide el pedido y si la cuenta ya tuvo una que no se canceló:

```csharp
private static Task<ModuleResponse> ValidateOrder(ModuleCall call)
{
    var trials = Trials(call);
    var account = call.Request.Context["account"];
    var english = account?["language"]?.GetValue<string>()?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
    var asked = (call.Request.Input["items"] as JsonArray ?? [])
        .Where(i => i?["product_id"] is JsonValue p && trials.Contains(p.GetValue<long>()))
        .Sum(i => i!["quantity"]?.GetValue<int>() ?? 1);
    var used = (account?["services"] as JsonArray ?? [])
        .Any(s => s?["product_id"] is JsonValue p && trials.Contains(p.GetValue<long>()) && s["status"]?.GetValue<string>() != "cancelled");

    string? reason = asked switch
    {
        0 => null,
        > 1 => english ? "The free trial is only one." : "La prueba gratis es una sola.",
        _ when used => english ? "You already used your free trial. Choose a paid plan." : "Ya usaste la prueba gratis. Elegí un plan pago.",
        _ => null,
    };
    return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["allowed"] = reason is null, ["reason"] = reason }));
}
```

**Los eventos** quedan en su log. Ahí una extensión real avisaría a un CRM o a un chat:

```csharp
private static Task<ModuleResponse> OnEvent(ModuleCall call, ILogger logger)
{
    var input = call.Request.Input;
    logger.LogInformation("Evento {Type} {Id} con {Data}", input["type"]?.GetValue<string>(), input["id"]?.GetValue<string>(), input["data"]?.ToJsonString());
    return Task.FromResult(ModuleResponse.Completed());
}
```

**Correrla.** Necesita el secreto de firma y un lugar para su registro de idempotencia, que tiene que sobrevivir a
los reinicios. Con una sola réplica alcanza un archivo SQLite. Con varias, una base PostgreSQL propia en
`Module:IdempotencyConnectionString`.

```bash
export Module__SigningSecret="$(openssl rand -hex 32)"   # el mismo que se carga en el panel
export Module__IdempotencyStorePath=/var/lib/one-trial/idempotency.db
export ASPNETCORE_URLS=http://127.0.0.1:5310
dotnet run --project src/modules/BillingOss.Module.OneTrial
```

Detrás de un proxy con TLS, o en el mismo equipo que Hornero. El núcleo no acepta `http` hacia otro equipo,
porque cada llamada lleva la configuración de la instancia.

**Darla de alta.** En el admin, **Extensiones** (`/admin/complementos/extensiones`):

1. Nombre: `Prueba única`. URL: la del módulo. Secreto de firma: el de `Module__SigningSecret`.
2. Configuración: `{ "trial_product_ids": [41] }`, con el número del producto de prueba gratis. Si el manifiesto
   marca un campo con `x-secret`, ese campo se guarda cifrado y no se vuelve a mostrar.
3. **Agregar la extensión**. Billing-oss lee el manifiesto firmado y le pide al módulo que pruebe la configuración
   (`/v0/instance/test`, obligatoria en todo módulo). Si el secreto no coincide, el módulo no es del tipo
   `extension` o la prueba falla, lo dice con el motivo que dio el módulo y no guarda nada.
4. En la ficha, prendé **Validar los pedidos antes de confirmarlos** y **Si la extensión no responde, rechazar el
   pedido**, y elegí `service.activated` en los eventos. **Guardar los ganchos**.

**Probarla.** Un pedido con la prueba gratis se confirma. El segundo de la misma cuenta, desde el portal, la API
propia o la compatible, vuelve con "El pedido no se puede confirmar: Ya usaste la prueba gratis. Elegí un plan
pago." Un pedido pago sigue pasando. La prueba automática que hace lo mismo es
`ExtensionTests.EjemploDeLaGuia_UnaSolaPruebaGratisPorCuenta`.

### 2.3 Buenas prácticas

- **Rápida en `validate_order`:** el cliente está esperando. Lo lento va en un evento.
- **La validación no reserva nada:** dos pedidos de la misma cuenta que llegan a la vez pueden pasar los dos, porque
  cada uno se valida antes de confirmarse. Si un duplicado importa, la extensión lo detecta al recibir `order.placed` y
  lo cancela con `CancelOrder` de la API compatible, con su propia llave.
- **Idempotente en `on_event`:** la SDK lo garantiza mientras uses su registro, que se comparte entre réplicas.
- **Sin secretos en el log:** la SDK oculta los campos `x-secret`, pero lo que escribas vos es tu responsabilidad.
- **Sin estado en memoria que importe:** puede haber varias réplicas, y Hornero llama a cualquiera.
- **Con su propia API key** si tiene que cambiar algo en Hornero, con los permisos justos.

---

## 3. API compatible: comandos agregados

Se suman a los ocho de v0. Cada uno pide su permiso en la credencial, `compat:` y el nombre del comando, y los que
cambian algo quedan en la auditoría. El detalle de parámetros, respuestas y errores está en la sección 3.1 de
[diseno/api-compatible.md](../../diseno/api-compatible.md).

| Comando | Qué hace |
|---|---|
| `GetClients` | Busca cuentas por nombre, empresa o correo, con filtro de estado y páginas de hasta 250 |
| `GetProducts` | Lista el catálogo con el precio vigente y el cargo de alta, por moneda y ciclo |
| `GetInvoices` | Lista facturas de una cuenta o de todas, por estado |
| `DeleteClient` | Borra una cuenta sin historia. Una con documentos, servicios, pedidos o pagos se cierra desde el admin |
| `UpdateClientProduct` | Cambia el nombre visible del servicio (`domain`). Cualquier otro campo da error |
| `ModuleCreate` | Aprovisiona ya un servicio pendiente o reintenta uno fallido |
| `ModuleTerminate` | Da de baja un servicio activo o suspendido |
| `ModuleSuspend` | Suspende un servicio activo. Es una suspensión del staff: el pago de la cuenta no la levanta |
| `ModuleUnsuspend` | Reactiva un servicio suspendido, también uno suspendido por falta de pago |
| `CancelOrder` | Cancela un pedido sin pagar ni aprovisionar y anula su factura con una nota de crédito |
| `GetEmailTemplates` | Lista las plantillas de los avisos en cada idioma |

Los comandos de tickets llegan con el módulo de soporte. `SendEmail`, `SendAdminEmail`, `CreateSsoToken` y
`GetStaffOnline` no están, y la sección 5 del [diseño](../../diseno/webhooks-y-extensiones.md) dice por qué.

La API compatible viene apagada. Se prende por instalación con `CompatApi:Enabled=true` (la variable
`CompatApi__Enabled`), y mientras está apagada `/includes/api.php` responde 404 aunque la llave tenga sus comandos.

---

## 4. Llaves de la API

Cada integración usa su propia llave, con los permisos justos, así se puede revocar una sin tocar las otras.

### 4.1 Emitir una llave

En **API y webhooks** (`/admin/configuracion/webhooks`), con el permiso `integrations.manage`. Ese permiso deja emitir
llaves con cualquier permiso, así que equivale a administrar todo: dáselo solo a quien ya puede todo lo demás.

1. Un nombre que diga quién la usa, por ejemplo `n8n de cobranzas`.
2. Los permisos. Los de la API propia (`accounts:read`, `orders:write` y los demás) salen de los endpoints que
   existen en la instalación. Los de la API compatible son un permiso por comando: una llave que solo busca
   clientes lleva `GetClients` y `GetClientsDetails`, y nada más.
3. Opcional: las IPs desde las que se acepta (una IP o una red, como `203.0.113.0/24`), el vencimiento, que vale
   hasta el final de ese día en la zona horaria de la instalación, y un tope de pedidos por minuto propio. Sin
   tope, rige el de la instalación (600 por minuto).
4. **Emitir la llave**. Se muestra una sola vez, en esa respuesta, que no queda en ningún caché. Billing-oss
   guarda solo su hash.

### 4.2 Usarla

| API | Cómo va la llave |
|---|---|
| Propia (`/api/v1`) | `Authorization: Bearer bk_xxxxxxxx.secreto` |
| Compatible (`/includes/api.php`) | `identifier=bk_xxxxxxxx` y `secret=secreto` en el formulario, o el mismo `Authorization: Bearer` |

Un pedido sin permiso vuelve con 403, uno que pasó el tope con 429 y una llave revocada, vencida o desde una IP
que no está en su lista, con 401 en la API propia y 403 en la compatible.

### 4.3 Revocarla

**Revocar la llave**, en la lista, la corta en el momento en todos los nodos: el próximo pedido con ella ya no
pasa. La emisión y la revocación quedan en la auditoría (`apikeys.issue` y `apikeys.revoke`) con el nombre, el
prefijo y los permisos, nunca con el secreto.
