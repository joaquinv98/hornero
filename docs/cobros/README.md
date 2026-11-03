# Cobros incluidos

**Parte de:** la documentación de Hornero · **Diseño:** sección 4 de
[fiscal-impuestos-y-cobros.md](../../diseno/fiscal-impuestos-y-cobros.md) (D-21) y sección 8 del
[modelo de dominio](../../diseno/dominio.md)

Qué trae Hornero para cobrar sin programar nada: las pasarelas incluidas, el asistente que las configura, la
transferencia con conciliación por CUIT, los medios guardados con débito automático y el débito por suscripción de
Mercado Pago. Todo se prueba contra pasarelas falsas en proceso y las pruebas contra los sandbox reales quedan
escritas y omitidas mientras no estén sus variables de entorno.

## Qué viene incluido

| Medio | Dónde vive | Qué cubre |
|---|---|---|
| Mercado Pago | Módulo `mercadopago` ([README](../../src/modules/BillingOss.Module.MercadoPago/README.md)) | Checkout Pro, reembolsos, conciliación y suscripciones como débito automático |
| Stripe | Módulo `stripe` ([README](../../src/modules/BillingOss.Module.Stripe/README.md)) | Checkout alojado, medios guardados con cobro sin el cliente, reembolsos, disputas y conciliación por movimientos de saldo |
| PayPal | Módulo `paypal` ([README](../../src/modules/BillingOss.Module.PayPal/README.md)) | Orders v2 por redirección, captura, reembolsos, disputas y conciliación, para clientes del exterior y nunca en pesos |
| Cripto | Módulo `cryptocloud` ([README](../../src/modules/BillingOss.Module.CryptoCloud/README.md)) | Facturas de CryptoCloud (Trybit) en dólares, sin reembolsos ni débito automático |
| Transferencia a CBU, CVU o alias | Núcleo, sin módulo | Datos para transferir, Ya transferí con comprobante, importación del extracto y conciliación por el CUIT del ordenante |

Los cuatro módulos vienen en la receta por defecto, con el mismo bloque. Stripe no abre cuentas en Argentina: viene para
los proveedores de los países donde opera. PayPal y CryptoCloud traen en su manifiesto los hosts del navegador por
ambiente, y PayPal los eventos de su webhook y el ambiente en la prueba de conexión, para el asistente.

## Alta de los módulos

La receta de despliegue da de alta cada módulo al correr `migrate`, con su URL, su secreto de firma y la huella de
su certificado ([receta chica](../despliegue/receta-chica.md), sección 6). Nadie copia huellas ni secretos de
firma. Si una versión nueva del módulo cambia sus esquemas o pide más datos personales, queda pendiente y Pasarelas
de pago muestra "Aprobar la versión nueva". Mientras tanto vale la aceptada (sección 5 del
[protocolo](../../diseno/protocolo-modulos-v0.md)).

## Cobros en el admin

Hace falta el permiso `payments.manage`:

```sh
dotnet BillingOss.Web.dll staff add cobros@example.com "Cobranzas" payments.manage
```

Las pantallas cuelgan del menú clásico de la sección 10 del diseño de interfaz:

| Menú | Ruta | Qué tiene |
|---|---|---|
| Configuración, Pasarelas de pago | `/admin/cobros` | Las pasarelas incluidas, el asistente de cada instancia y el acceso a Cuentas para transferir (`/admin/cobros/cuentas-para-transferir`) |
| Facturación, Pagos sin imputar | `/admin/cobros/transferencias` | La importación del extracto, la bandeja Sin identificar y los avisos de Ya transferí |
| Facturación, Cobros automáticos | `/admin/cobros/automaticos` | Los débitos con medio guardado y su estado (en curso, sin respuesta cierta, rechazado con qué sigue, cobrado), con filtro, páginas y CSV, más las cuentas con el débito prendido, las suscripciones activas y los débitos apagados por una disputa en los últimos 90 días |
| Facturación, Registro de pasarelas | `/admin/registro-modulos?tipo=pasarelas` | Los avisos de cada pasarela tal como llegaron (encabezados, parámetros y cuerpo), si el módulo los verificó y los eventos que dejaron, con filtro por pasarela y estado. Un aviso inválido casi siempre es un secreto de avisos que no coincide con el de la pasarela |

En el portal, Medios de pago (`/cuenta/medios-de-pago`) cuelga de Facturación.

Pasarelas de pago muestra una tarjeta por módulo de cobros con sus instancias y su estado:

| Estado | Cuándo |
|---|---|
| Sin configurar | La instancia se creó y todavía no pasó la prueba de conexión |
| Con error | La última prueba falló, o una consulta de pagos falló por la configuración (una clave revocada o sin un permiso). Se muestra el mensaje del módulo y queda en la auditoría (`payments.gateway_config_error`). El pago de cada aviso se vuelve a consultar cada seis horas durante unos dos días y la conciliación vuelve a probar cada hora, así nada se pierde mientras el staff arregla la clave |
| En prueba | Instancia de prueba que pasó la prueba |
| Lista para activar | Instancia de producción probada y todavía no ofrecida en el portal |
| En producción | Instancia de producción ofrecida en el portal |
| Fuera del asistente | Instancia creada antes del asistente: se ofrece como antes, en todas las monedas |

Avisa también cuando hay cuentas en una moneda que ningún medio activo cobra y cuando una instancia de producción
todavía no recibió ningún aviso verificado.

### El asistente de una instancia

Prueba y producción son instancias distintas, como en ARCA. Cada una se crea desde la tarjeta del módulo y su
ambiente no cambia después.

| Paso | Qué hace |
|---|---|
| Credenciales | Formulario armado con el `config_schema` del módulo, con la ayuda y el enlace al panel de la pasarela de cada campo (`x-help`). Los campos `x-secret` se guardan cifrados y no se vuelven a mostrar: vacío conserva el guardado |
| Avisos | La URL `{Billing:PublicUrl}/webhooks/{instancia}` y los eventos que declara el manifiesto (`webhook_events`) |
| Probar la conexión | `instance/test` con `webhook_url`, para que el módulo compruebe que el aviso apunta al núcleo. Muestra cuenta, modo y lo que falte. Credenciales de otro ambiente que la instancia no pasan la prueba y, si la instancia se ofrecía, la apagan. Guardar credenciales en una instancia ofrecida las prueba en el momento |
| Pago de prueba | Solo en instancias de prueba. Abre un checkout por `Payments:TestPaymentAmount`. No se registra como pago, no toca facturas ni saldo y no emite comprobante. Cuando llega el aviso queda como pago verificado |
| Activar en el portal | Nombre y orden en el portal, monedas (las del manifiesto, o las que se escriban si declara `*`), si cancela en la misma moneda extranjera (RG 5616) y si ofrece el débito automático. Para ofrecerla hacen falta una prueba correcta y al menos una moneda |

Los hosts a los que el navegador puede ir salen del manifiesto (`browser_hosts`), así el staff no los escribe.

Un pago del ambiente de prueba (`livemode` en `false`) en una instancia de producción no se registra: con credenciales de
prueba cargadas por error, un cliente pagaría con una tarjeta de prueba y se le darían las facturas por pagas. Queda en el
log como error, para revisar las credenciales.

## Pagar desde el portal

`/pagar?factura={id}` (con varias `factura` paga todas juntas, de una cuenta y una moneda) sigue los pasos de D-19:
qué se paga, el medio y el botón con el monto. Los medios son las pasarelas activas en la moneda, en el orden del
asistente, y la transferencia si hay una cuenta para transferir en esa moneda.

Si alguna pasarela ofrece guardar el medio, aparece la casilla de consentimiento, destildada y separada de todo lo
demás, con el enlace a `/debito-automatico`. Al pagar con la casilla tildada se guarda la constancia: quién, cuándo,
desde qué IP y navegador, el texto exacto en su idioma y su huella SHA-256.

Mientras hay un débito automático en curso sobre una factura, ni la factura ni `/pagar` ofrecen otro medio. El pago del
cliente y el débito toman la misma traba de las facturas al abrir su intento, así no se cruzan aunque corran a la vez en
dos nodos.

## Transferencias

### Cuentas para transferir

En `/admin/cobros/cuentas-para-transferir`, desde Pasarelas de pago: banco, titular, CUIT (11 números), CBU o CVU (22 números con sus dígitos
verificadores), alias (6 a 20 letras, números, puntos o guiones), moneda y el mapa de columnas del extracto. El
portal muestra las activas de la moneda con el importe exacto, el número de factura y desde qué CUIT transferir.

### Ya transferí

El cliente avisa la fecha, el número de operación y, si quiere, el comprobante: un PDF, PNG o JPG de hasta 5 MB,
cuyo tipo sale del contenido y no del nombre. El aviso no es un pago: queda para el staff y se cierra solo cuando
entra un movimiento de la misma cuenta por el mismo importe. Una cuenta tiene a lo sumo diez avisos abiertos, así nadie
llena la base con comprobantes.

### Extracto

Un CSV del home banking, desde el admin o por la API:

```sh
curl -X POST -H "Authorization: Bearer $LLAVE" -H "Content-Type: text/csv" \
  --data-binary @extracto.csv https://facturacion.example.com/api/v1/transfer-accounts/1/statements
```

La llave necesita `payments:write`. Cada crédito entra una sola vez por cuenta y referencia del banco, así
reimportar no duplica, y los débitos no se importan. Las filas que no se entienden vuelven con su número de línea.
Una referencia que ya está en la base, de una importación anterior, con la misma fecha, el mismo importe y el mismo
ordenante es el mismo movimiento: se cuenta en "que ya estaban". Con otros datos, o repetida en el mismo archivo (un
extracto no lista dos veces un movimiento), es otro crédito con una referencia que no es unívoca, casi siempre porque
el mapa apunta a una columna como el tipo de operación: no entra y vuelve como error con su línea, así ninguna
transferencia se pierde en silencio. El resultado (filas, nuevos, imputados, en la bandeja, que ya estaban y errores)
es el mismo en la pantalla y en la API. Pasados los 50 errores, el último renglón dice cuántas líneas más fallaron.

El mapa de columnas es un JSON en la cuenta. Cada dato es el nombre de la columna en el encabezado (sin distinguir
mayúsculas) o su número desde 0:

| Campo | Por defecto | Qué es |
|---|---|---|
| `delimiter` | `,` | Separador de columnas |
| `has_header` | `true` | Si la primera fila es el encabezado |
| `date` y `date_format` | `fecha` y `dd/MM/yyyy` | Fecha del movimiento |
| `amount` y `decimal_separator` | `importe` y `,` | Importe con signo: los negativos son débitos |
| `reference` | `referencia` | La referencia unívoca de la transferencia (BCRA, punto 3.2.1.2) |
| `payer_cuit` | `cuit` | CUIT o CUIL del ordenante |
| `payer_name` y `concept` | `ordenante` y `concepto` | Opcionales |

### Conciliación

Cada movimiento nuevo busca la cuenta por el CUIT o CUIL del ordenante en la identidad fiscal vigente y propone las
facturas impagas de esa cuenta en la moneda cuyo saldo suma exactamente el importe: las más viejas en orden, o si no
una sola con ese saldo.

- Con `Payments:TransfersAutoApply` prendido, la coincidencia exacta se imputa sola y la cuenta identificada sin
  factura queda como saldo a favor.
- Lo demás va a la bandeja Sin identificar. El staff lo imputa a las facturas sugeridas, a las impagas más viejas o
  todo a saldo a favor de la cuenta que elija, o lo marca como que no es un cobro.
- Cada imputación entra como pago manual con el canal `manual:transferencia` y el ID externo
  `bank-{cuenta}-{referencia}`, el mismo camino que `POST /api/v1/payments`. Repetirla no duplica.

## Débito automático con medio guardado

Con la capacidad `payment.saved_methods` (Stripe la declara), el núcleo decide cuándo y cuánto cobrar y comprueba la
factura antes de cobrar.

1. **Guardar.** El cliente paga una factura con la casilla tildada. El módulo pide a la pasarela que guarde el medio
   para cobros iniciados por el comercio. Cuando el pago se aprueba, el núcleo guarda los IDs de la pasarela y lo
   que se muestra (marca, últimos 4 y vencimiento), con la constancia, y ese medio pasa a ser el del débito de la
   cuenta. Hay a lo sumo uno por cuenta.
2. **Avisar.** El correo de cada factura dice cuándo y con qué medio se va a debitar.
3. **Cobrar.** Un barrido diario, a la hora de `Payments:AutoCharge:Hour` en la zona del proveedor, encola un
   trabajo por factura que vence en un día de la política. Ese trabajo comprueba en una transacción corta que la
   factura siga con saldo, que el débito siga prendido, que no haya otro intento abierto sobre la factura (con
   cualquier medio) ni un pago del cliente en curso, abre el intento por el saldo y encola el cobro, que llama a
   `charge_saved` con la clave `autocharge-{intento}`.
4. **Resultado.** Aprobado sigue el camino de siempre. Pendiente espera el aviso o la conciliación. Rechazado mira
   `details.retry`: `later` vuelve el próximo día de la política, `never` vence el medio y le pide otro al cliente y
   `customer_action` le manda el enlace para confirmar. Al cliente se le muestra un rechazo genérico: el motivo de un
   fraude o de una tarjeta robada no se muestra. El enlace de la pasarela va solo si es https y de un host de la
   instancia: si no, el correo lleva el de la factura en el portal.
5. **Sin respuesta cierta.** El intento queda anotado como enviado antes de pedir: si el nodo se apaga antes de la
   respuesta, el reintento lo trata como uno que pudo haber cobrado. Un error que dice que el pedido no salió (sin
   conexión con el módulo, `upstream_unavailable`, frenado por la pasarela, `rate_limited`, o rechazado por el módulo
   antes de pedir nada: firma, configuración, datos, una operación o un medio que no existe) le saca esa marca si no
   había salido otro antes. Si es reintentable se reintenta, y si dura todo el trabajo el intento falla con `later`: la
   factura vuelve otro día de la política y el portal la ofrece. Cualquier otro error (sin respuesta a tiempo, otro
   pedido en curso con la misma clave o una falla del módulo) deja el intento incierto desde ese pedido, porque pudo
   haber llegado. Antes de cada pedido, también de cada reintento, se revisa otra vez la factura y el medio: si el
   cliente pagó por otro medio o quitó la tarjeta, no se pide nada. Si ningún pedido del intento salió, se cierra. Si alguno pudo haber cobrado (quedó incierto o pendiente en la pasarela), no
   se cierra: lo que cobró lo registran el aviso o la conciliación, y mientras tanto no se abre otro intento sobre la
   factura aunque la cuenta tenga otro medio, porque la cobraría dos veces. El barrido repasa cada débito abierto hace
   más de una hora (incierto, pendiente en la pasarela o con su trabajo muerto) con la misma clave, que es lo seguro:
   el módulo devuelve lo que pasó o lo hace una sola vez. Nunca se pide con otra clave a ciegas. Mientras tanto, la
   factura no se ofrece por otro medio.
6. **Plazo.** A los dos días el débito abierto no se repite más, porque la pasarela puede haber olvidado la clave
   (Stripe, desde las 24 horas) y el módulo busca el cobro del intento hasta tres días atrás. Queda en Cobros
   automáticos como "Sin respuesta cierta, vencido" y en la auditoría (`payments.autocharge_uncertain`), la factura ya
   no se debita sola y el portal la vuelve a ofrecer. Si la pasarela cobró, el aviso o la conciliación lo registran
   contra la factura, o como saldo a favor si ya estaba paga.

Reglas extra:

- Una cuenta tiene el débito con medio guardado o el débito por suscripción, uno de los dos: con los dos, cada factura
  se cobraría dos veces. Mientras hay una suscripción vigente (o esperando la aprobación del cliente), el portal no
  ofrece guardar el medio para el débito, no se puede prender el de un medio y el barrido no debita a esa cuenta. Y con
  el débito de un medio prendido, la suscripción no se crea.
- Una disputa o un contracargo de un débito apaga el débito de la cuenta y queda en la auditoría
  (`payments.autocharge_stopped`): casi siempre el cliente desconoció el cargo.
- La baja corta el débito (Disposición 954/2025): una factura con un servicio dado de baja, o con un período que
  empieza desde la baja pedida, no se debita sola ni lo anuncia el correo. Sigue el camino de siempre.
- 30 días antes de que venza la tarjeta del débito sale un aviso, uno por tarjeta y vencimiento. Vencida, el barrido
  la marca y deja de usarla.
- La pasarela puede avisar que la red actualizó la tarjeta o que el cliente la borró: el núcleo la consulta con
  `get_method` y la actualiza o la da de baja.
- **Revocación.** En `/cuenta/medios-de-pago` el cliente apaga el débito o quita el medio, y vale en el momento (Ley
  24.240, art. 10 ter). Quitar encola `delete_method` en la pasarela. Prender el débito con otro medio pide la
  casilla de nuevo y deja otra constancia.
- `/debito-automatico` muestra las condiciones sin iniciar sesión, con los días y la hora de la política.

## Débito por suscripción de Mercado Pago

Apagado por defecto con `Payments:SubscriptionsEnabled`, así un staging con credenciales reales no crea suscripciones
de verdad. Se prende en el corte. Apagado, el portal no las ofrece y el núcleo no cambia nada en la pasarela (ni altas
ni montos). Lo que una suscripción vigente ya cobró se registra igual: si el ajuste se apaga con suscripciones
vigentes, Mercado Pago sigue debitando, y esa plata no puede quedar sin registrar con la cuenta en mora. El cliente la
sigue viendo en Medios de pago y la puede desactivar, y el trabajo diario de montos deja un aviso en el log mientras
haya alguna.

- El cliente lo activa en Medios de pago, con la casilla de consentimiento. El núcleo crea la suscripción por el
  total mensual de la cuenta (los servicios mensuales en su moneda que van a renovar, con descuento e IVA, calculados
  como la factura de renovación), con inicio en el próximo vencimiento de esos mismos servicios, y lleva al cliente a
  aprobarla en Mercado Pago. Un servicio anual que vence antes no adelanta el primer débito.
- Una cuenta tiene a lo sumo una suscripción vigente: dos pedidos a la vez (un doble clic o dos nodos) se ordenan con
  el lock de la cuenta y el segundo ve la del primero. Si la pasarela falla al crearla, la suscripción queda dada de
  baja en el núcleo, porque el cliente nunca vio la página para aprobarla, y puede volver a intentar.
- Cada cobro llega como pago de la suscripción: el núcleo la reconoce por el ID que le puso la pasarela o, si el pago
  no lo trae, por la referencia del núcleo, que Mercado Pago copia en el `external_reference` de cada cobro. Va
  primero a las facturas impagas de la cuenta en esa moneda con un servicio mensual, que son las que la suscripción
  paga, y después a las demás, las más viejas primero. Así una factura anual anterior no se lleva la cuota y la
  renovación mensual no queda en mora. Lo que sobra queda como saldo a favor. Un pago de una suscripción ajena no se
  registra.
- Un trabajo diario encola, por suscripción, el cambio de monto cuando cambia el total. Sin servicios que renueven
  la da de baja. La clave de cada cambio es otra aunque el total vuelva a un valor de antes, así la pasarela nunca se
  queda con el monto viejo.
- Si Mercado Pago la da de baja sola (por ejemplo después de cuotas rechazadas), se le avisa al cliente.
- Desactivar en Medios de pago vale en el momento en el núcleo y un trabajo la cancela en Mercado Pago.

## Ajustes

| Clave | Por defecto | Qué hace |
|---|---|---|
| `Payments:SubscriptionsEnabled` | `false` | Ofrece el débito por suscripción y lo cambia en la pasarela. Los cobros de una vigente se registran siempre |
| `Payments:TransfersAutoApply` | `false` | Imputa sola la transferencia con una sola coincidencia exacta |
| `Payments:TestPaymentAmount` | `1` | Monto del pago de prueba del asistente |
| `Payments:AutoCharge:Days:{n}` | El día del vencimiento y a los 1, 3 y 5 días | Días de cada intento respecto del vencimiento. Con gracia corta conviene antes, por ejemplo -3, -2, -1 y 0 |
| `Payments:AutoCharge:Hour` | `10` | Hora del barrido, en la zona del proveedor |
| `Payments:AutoCharge:MaxAmount:{moneda}` | Sin tope | Una factura con más saldo no se debita sola |

## Trabajos

Todo lo que llama a una pasarela sale de un trabajo de la cola, fuera de cualquier transacción y con su clave de
entidad, así corre en cualquier nodo sin pisarse:

| Trabajo | Clave de entidad | Qué hace |
|---|---|---|
| `payments.autocharge-scan` | Ninguna, una vez por día | Vence tarjetas, avisa las que vencen, repite los débitos inciertos y encola el de cada factura |
| `payments.autocharge-prepare` y `payments.autocharge` | `autocharge:{factura}` | Abre el intento y cobra con `charge_saved` |
| `payments.method-sync` y `payments.method-delete` | `method:{instancia}:{medio}` | Actualiza o borra un medio guardado |
| `payments.subscription-sync`, `payments.subscription-cancel` y `payments.subscription-update` | `subscription:{suscripción}` | Estado, baja y monto de una suscripción |
| `payments.subscription-amounts` | Ninguna, una vez por día | Encola el cambio de monto de cada suscripción |
| `payments.reconcile` | `gateway:{instancia}` | Conciliación horaria, solo en instancias que concilian y ya pasaron la prueba |

## Pruebas

- Núcleo: `SavedMethodFlowTests`, `AutoChargeFlowTests`, `SubscriptionFlowTests`, `TransferFlowTests`,
  `GatewaySetupTests` e `IncludedModulesTests` contra una pasarela falsa en proceso y el PostgreSQL local de pruebas.
- Pantallas: `PaymentUiTests` recorre el pago, la transferencia, los medios guardados, las condiciones y el
  asistente, las transferencias, los cobros automáticos y el registro de avisos del admin.
- API: `ApiPropia_ExtractoDelBanco_SeImportaUnaSolaVez`.
- Módulos: las pruebas de cada módulo contra su API falsa. Las de los sandbox reales quedan omitidas sin
  `STRIPE_TEST_API_KEY` o `MP_TEST_ACCESS_TOKEN` (y `MP_TEST_PAYER_EMAIL` para las suscripciones).

## Pendiente

- Que el débito espere la conciliación de pagos después de un failover (HA 7 del endurecimiento, igual que las
  emisiones fiscales). La rama endurecer-ha detecta la promoción por la línea de tiempo de PostgreSQL y encola la
  conciliación: al mezclarla, el barrido del débito tiene que esperar a que esa conciliación termine.
- Pagos sin imputar: acá lleva a la bandeja de transferencias (`/admin/cobros/transferencias`) y en el mapa de
  navegación de interfaz-temas a la lista de pagos filtrada (`/admin/pagos?estado=sin-imputar`). Al mezclar, la
  bandeja de movimientos sin identificar tiene que quedar a un clic de esa lista.
- Excepción de la política del débito por producto (días, tope y hora), cuando el catálogo tenga su pantalla de
  productos donde cargarla. Hoy vale la política del proveedor para todas las facturas.
- Relevar en el importador las suscripciones vigentes del sistema anterior.
- Segunda etapa: `payment.method_setup` para cambiar el medio sin pagar una factura y `payment.webhook_setup` para que
  el asistente cree el aviso en la pasarela.
- Orden por columna y acciones masivas en Cobros automáticos y Pagos sin imputar, cuando la interfaz traiga el
  componente de listas de la sección 10.2 del diseño de interfaz. Hoy tienen filtro, páginas y CSV (Cobros
  automáticos) o la bandeja completa (Pagos sin imputar).
