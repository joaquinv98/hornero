# API compatible v0

**Estado:** v0 · **Parte de:** [diseño del núcleo](README.md) · **Decisión:** D-12

La API compatible deja que las integraciones de un proveedor que viene de WHMCS sigan andando el día de la
migración con cambios mínimos: la URL y las credenciales. Es una capa de traducción propia: recibe pedidos con la
forma de la API externa de WHMCS y llama a los mismos casos de uso que la [API propia](decisiones.md) (D-11).

WHMCS es una marca de su dueño. Esta API es una implementación independiente y no tiene relación con él.

---

## 1. De dónde sale

La especificación sale de integraciones reales que usan la API (qué comandos llaman, con qué parámetros y qué
campos leen de la respuesta) y de clientes libres de la API publicados con licencia permisiva. No se usó
documentación, paquete, base ni instalación de WHMCS. Lo que no sale de esas fuentes está marcado como **diseño
propio**.

Clientes libres consultados:

| Repo | Licencia |
|---|---|
| `darthsoup/php-whmcs-api` | MIT |
| `juniwalk/api-whmcs` | MIT |
| `zmingee/python-whmcs` | MIT |
| `PeteBishwhip/WHMCSAPI` | MIT |
| `microweber/microweber` (módulo `whmcs.class.php`) | MIT |
| `BetterCorp/WHMCS-Tools` | MIT |
| `technobros-dev/whmcs-client` | MIT |

---

## 2. Forma de los pedidos

| Tema | Comportamiento |
|---|---|
| Ruta | `POST /includes/api.php` |
| Cuerpo | `application/x-www-form-urlencoded`. Sin query |
| Comando | Campo `action` |
| Formato de respuesta | Campo `responsetype`. Solo `json`. Cualquier otro valor da error |
| Credencial | Campos `identifier` y `secret` en el cuerpo |
| Credencial en encabezado | `Authorization: Bearer` con una credencial de la API compatible. Diseño propio, para flujos como los de n8n |
| Listas | `limitstart` y `limitnum` |
| Arreglos | Índices en el nombre (`pid[0]`, `billingcycle[0]`). También se acepta el valor suelto (`pid`) |
| Booleanos | `true`, `false`, `1` y `0`, sin importar mayúsculas. Vacío es falso |
| Opciones configurables | `configoptions[0]` en base64 de un arreglo PHP serializado de ID a valor |
| Campos personalizados | `customfields[0]` en base64, de JSON o de un arreglo PHP serializado |

**Diseño propio:** el arreglo PHP serializado se lee con un parser mínimo: solo arreglos de claves y valores
enteros o de texto, con profundidad acotada. Cualquier otra cosa da error. No se instancia nada.

**Respuestas.** JSON con `result` igual a `success` o `error`. En error, `message` con un texto propio.
**Diseño propio:** los errores de negocio vuelven con HTTP 200, los de credencial con 403 y los de una credencial
que pasó su límite de pedidos con 429, siempre con el mismo cuerpo. También lo llevan el 429 del límite por IP, el
413 de un cuerpo de más de 1 MiB y el 400 de un formulario que no se puede leer. Los tests de contrato (sección 5)
comprueban esos casos.

**Campos de respuesta.** Solo se devuelven campos que alguna fuente lee y que el modelo de dominio tiene.

---

## 3. Comandos de v0

Entran los 8 comandos de uso más frecuente en las integraciones relevadas. Buscar y abrir tickets sin duplicar y
bajar el PDF de un comprobante se resuelven mejor con la API propia: `/api/v1/tickets` con `Idempotency-Key` y
`/api/v1/documents/{id}/pdf`.

Cada comando traduce a un caso de uso propio del [modelo de dominio](dominio.md), sin lógica de negocio
aparte. Los errores de la tabla son **diseño propio**.

| Comando | Caso de uso propio | Parámetros | Respuesta | Errores |
|---|---|---|---|---|
| `GetClientsDetails` | Leer cuenta por ID o correo | `clientid` o `email`. `stats` se acepta y se ignora | `userid`, `client_id`, `clientid`, `id`, `firstname`, `lastname`, `fullname`, `companyname`, `email`, `address1`, `city`, `state`, `postcode`, `country`, `currency_code`, `status` | Cuenta inexistente |
| `GetClientsProducts` | Listar servicios de una cuenta, o uno por ID | `clientid`, `serviceid`, `limitstart`, `limitnum` | `totalresults`, `startnumber`, `numreturned`, `products.product[]` con `id`, `clientid`, `pid`, `name`, `groupname`, `domain`, `status`, `billingcycle`, `recurringamount`, `regdate`, `nextduedate` | Cuenta o servicio inexistente |
| `GetInvoice` | Leer factura | `invoiceid` | `invoiceid`, `invoicenum`, `userid`, `date`, `duedate`, `datepaid`, `subtotal`, `tax`, `total`, `status`, `items.item[]` | Factura inexistente |
| `AddClient` | Alta de cuenta con su usuario | `firstname`, `lastname`, `email`, `password2`, `country`, `currency`, `phonenumber`, `noemail`, `skipvalidation` | `clientid` | Correo ya registrado, datos inválidos |
| `UpdateClient` | Cambiar datos de la cuenta, o la contraseña del usuario titular | `clientid`, `password2`, `skipvalidation` y los campos de perfil de `AddClient` | `result` | Cuenta inexistente, datos inválidos |
| `AddOrder` | `PlaceOrder` | `clientid`, `pid[]`, `billingcycle[]`, `paymentmethod`, `configoptions[]`, `customfields[]` | `orderid`, `invoiceid` | Cuenta, producto, ciclo, opción o pasarela inexistente |
| `AddInvoicePayment` | Registrar un pago con referencia externa e imputarlo | `invoiceid`, `transid`, `gateway`, `amount`, `noemail` | `result` | Factura inexistente o anulada, falta `transid`, monto mayor que el saldo |
| `AcceptOrder` | Aceptar pedido y, si `autosetup`, aprovisionar | `orderid`, `autosetup`, `sendemail` | `result` | Pedido inexistente o ya aceptado |
| `GetProducts` | Leer el catálogo ([catalogo.md](catalogo.md)) | `pid`, `gid` | `totalresults`, `products.product[]` con `pid`, `gid`, `name`, `description`, `groupname`, `hidden` (diseño propio), `pricing.{moneda}` con el precio y el cargo de alta de cada ciclo (`monthly` y `msetupfee` hasta `triennially` y `tsetupfee`, -1.00 si no se vende) y `configoptions.configoption[]` con `id`, `name`, `type` y `options.option[]` con `id`, `name` y su `pricing` | Un `pid` o un `gid` que no es un número. Un producto que no existe da una lista vacía |
| `ModuleSuspend` | Suspensión propia del staff (`ServiceAdmin.Suspend`): guarda el motivo y un pago no la levanta | `accountid` (el servicio, como lo llama WHMCS, o `serviceid`), `suspendreason` | `result` | Servicio inexistente, o que no está activo ni suspendido |
| `ModuleUnsuspend` | Reactivar (`ServiceAdmin.Unsuspend`) | `accountid` (o `serviceid`) | `result` | Servicio inexistente o no suspendido |

`ModuleSuspend` y `ModuleUnsuspend` se sumaron después, para que la suspensión del staff quede distinta de la de
mora. `GetProducts` entró con el catálogo ([catalogo.md](catalogo.md)): es la forma de leer el catálogo con precios
y opciones que ya conocen las herramientas de diagnóstico.

Detalles que cambian el comportamiento:

- **IDs por número.** `pid`, las opciones de `configoptions` y `currency` llegan como números del sistema
  anterior. La importación conserva esos IDs (D-17), así que la API compatible los usa tal cual.
- **Nombres de pasarela y ciclos.** `paymentmethod`, `gateway` y `billingcycle` llegan como textos, por ejemplo
  `monthly` o `free`. Cada instalación tiene una tabla de equivalencias editable, que el importador llena con los
  valores que encuentra en la base del proveedor.
- **Estados.** Los estados propios se traducen a los textos que conocen las integraciones. Para facturas, el
  núcleo produce `Draft`, `Unpaid`, `Paid`, `Cancelled` y `Refunded`. Una factura anulada sale `Refunded` cuando
  sus notas de crédito devolvieron algo ya pagado, como reembolso o como saldo a favor, y `Cancelled` cuando no.
  Las importadas conservan el `Refunded` del origen. Para cuentas, `Active`, `Inactive` y `Closed`. Los de
  servicios siguen los textos que leen las integraciones.
- **Diseño propio:** `AddInvoicePayment` es idempotente. El mismo `transid` con el mismo `gateway` no acredita
  dos veces, porque el pago tiene restricción única por pasarela e ID externo
  ([modelo de dominio](dominio.md), sección 8). Por eso `transid` es obligatorio: sin él, dos pagos distintos
  del mismo `gateway` serían el mismo. Si falta `amount`, se imputa el saldo de la factura, y un `amount` mayor
  que el saldo es un error en vez de ir a saldo a favor.
- **Diseño propio:** `AcceptOrder` no espera al módulo. Encola el aprovisionamiento y responde. El aviso con
  los datos de acceso sale cuando el módulo confirma ([modelo de dominio](dominio.md), sección 5).
- **Diseño propio:** un pedido de total cero se acepta sin factura y `invoiceid` vuelve vacío.
- **Diseño propio:** `skipvalidation` deja crear o cambiar la cuenta sin los datos que no son obligatorios
  para facturar.

### 3.1 Comandos agregados

Los nombres de parámetros y campos son los que ya usan los comandos de v0 cuando significan lo mismo. Lo marcado
como **diseño propio** no tiene fuente y se confirma en los tests de contrato. El porqué de cada uno está en
[webhooks y extensiones](webhooks-y-extensiones.md), sección 5.

| Comando | Caso de uso propio | Parámetros | Respuesta | Errores |
|---|---|---|---|---|
| `GetClients` | Buscar cuentas | `limitstart`, `limitnum`, `status` (`Active`, `Inactive` o `Closed`) y `search`, que busca en nombre, empresa y correo (diseño propio) | `totalresults`, `startnumber`, `numreturned` y `clients.client[]` con los campos de `GetClientsDetails`: `id`, `firstname`, `lastname`, `companyname`, `email` y `status` | Estado desconocido |
| `GetProducts` | Listar el catálogo | `pid` y `gid` | La de la sección 3: el precio vigente y el cargo de alta de cada ciclo por moneda, y `hidden` | Un `pid` o un `gid` que no es un número |
| `GetInvoices` | Listar facturas | `userid` o `clientid`, `status` (los cinco de la sección 3), `limitstart` y `limitnum` | `totalresults`, `startnumber`, `numreturned` y `invoices.invoice[]` con los campos de `GetInvoice` salvo los renglones, y `id` en lugar de `invoiceid` | Estado desconocido |
| `DeleteClient` | Borrar una cuenta sin historia | `clientid` | `result` | Cuenta inexistente. Cuenta con documentos, servicios, pedidos o pagos: esa se cierra desde el admin y nunca se borra |
| `UpdateClientProduct` | Cambiar el nombre visible del servicio | `serviceid` y `domain` | `result` | Servicio inexistente. Un campo que no se soporta da error en vez de ignorarse en silencio |
| `ModuleCreate` | Aprovisionar ya un servicio pendiente, aunque no esté pago, o reintentar uno fallido con una generación nueva | `serviceid` | `result`. No espera al módulo, como `AcceptOrder` | Servicio inexistente o en otro estado |
| `ModuleTerminate` | Dar de baja un servicio activo o suspendido | `serviceid` | `result`. No espera al módulo | Servicio inexistente o en otro estado. Uno pendiente se cancela con `CancelOrder` |
| `ModuleSuspend` | Suspender un servicio activo. Queda con un motivo propio, así el pago no la levanta: solo la de mora se levanta sola | `serviceid` | `result`. No espera al módulo | Servicio inexistente o que no está activo |
| `ModuleUnsuspend` | Reactivar un servicio suspendido, del staff o por mora | `serviceid` | `result`. No espera al módulo | Servicio inexistente o que no está suspendido |
| `CancelOrder` | Cancelar un pedido: anula sus facturas (una por modo de carga del precio) con una nota de crédito, cancela sus servicios pendientes y devuelve el stock y el uso del cupón. Repetirlo no hace nada | `orderid` | `result` | Pedido inexistente. Factura con algo pagado, que se resuelve con un reembolso. Un servicio que ya no está pendiente |
| `GetEmailTemplates` | Listar las plantillas de los avisos | Ninguno | `totalresults` y `emailtemplates.emailtemplate[]` con `name`, `language`, `subject` y `custom` (diseño propio) | Ninguno |

**Diseño propio en las listas nuevas:** `limitnum` va de 1 a 250 y vale 25 si no viene. La envoltura de cada lista
(`clients.client[]`, `invoices.invoice[]`, `emailtemplates.emailtemplate[]`) sigue la forma de
`products.product[]` de `GetClientsProducts`.

---

## 4. Credenciales y permisos

- Las emite el núcleo, con los mismos controles que las API keys propias (D-11): se guardan como hash, se
  muestran una sola vez y tienen lista de IP, vencimiento y límite de pedidos.
- Cada credencial tiene la lista de comandos que puede usar. Por defecto, ninguno.
- Las credenciales del sistema anterior no se importan: se emiten nuevas y se cargan en cada integración.
- Cada llamada queda en la auditoría con la credencial y el comando.
- La API compatible viene apagada y se prende por instalación.

---

## 5. Pruebas

- **Tests de contrato.** Por cada comando hay un test que manda los parámetros que usan las integraciones,
  comprueba que vuelvan los campos que ellas leen y que maneje los errores.
- Nunca contra una instalación de WHMCS.

---

## 6. Después de v0

- Los comandos de tickets (`GetTickets`, `OpenTicket`, `UpdateTicket` y `GetSupportDepartments`), `SendEmail`,
  `SendAdminEmail`, `CreateSsoToken` y `GetStaffOnline`, si alguna integración los necesita, relevados desde su
  código y desde clientes libres.
- `dedicatedip` en `GetClientsProducts`, cuando el servicio guarde la IP que informa el módulo.
- Una matriz pública con los comandos y campos soportados.
- Fuera de alcance: respuestas en XML y la autenticación por usuario y contraseña.
