# Configuración y operación

Cómo se compila, qué claves de configuración lee la aplicación y qué operaciones del staff van por la API propia.

## Compilar y probar

Hace falta el SDK de .NET 10 (versión en `global.json`).

```powershell
./build.ps1
```

Las pruebas de integración usan el PostgreSQL de `BILLINGOSS_TEST_PG` y se saltean sin esa variable. Cada prueba
crea su base y la borra al terminar. Las conexiones van con pool, como en producción, y el pool de la base se cierra
antes de borrarla: sin pool cada conexión es un proceso nuevo del servidor. Solo las pruebas que pasan la base a solo
lectura abren sesiones nuevas sin pool, porque ese cambio vale para las sesiones que se abren después.

## Configuración

`BillingOss.Web` lee la configuración estándar de ASP.NET Core (archivo, variables de entorno con `__` o línea
de comandos) y además los archivos de `/run/secrets`, uno por clave (`ConnectionStrings__Main`), que es como
llegan los Docker secrets. Con el argumento `migrate` aplica las migraciones y termina, y con `probe URL` sale con 0
si la URL responde 2xx (el chequeo de salud de la imagen).

| Clave | Para qué |
|---|---|
| `ConnectionStrings:Main` | Base PostgreSQL. Obligatoria |
| `Secrets:KeyFile` | Archivo de llaves para cifrar secretos (D-09). Sin él no hay módulos: ni emisión fiscal ni pasarelas, ni el módulo manual |
| `Role` | `all` (por defecto), `web` o `worker` (D-03) |
| `Billing:PublicUrl` | URL pública del sistema, para la página de retorno y los avisos de las pasarelas |
| `Billing:TimeZone`, `Billing:InvoiceDueDays`, `Billing:RenewalLeadDays`, `Billing:RenewalLeadDaysByMonths`, `Billing:ContinuousInvoicing` | Fechas de negocio, vencimiento de las facturas de pedido y anticipación de las renovaciones |
| `Billing:GraceDays`, `Billing:CancelUnpaidAfterDays`, `Billing:TerminationDays` | Mora: suspensión, anulación de impagas y baja. El importador imprime los valores del sistema anterior |
| `Billing:DomainRenewalLeadDays`, `Billing:DomainGraceDays` | Anticipación de las renovaciones de dominios (7 por defecto) y días de gracia hasta que un dominio sin renovar pasa a vencido (45) |
| `Billing:LateFeeAfterDays`, `Billing:LateFeePercent`, `Billing:LateFeeFixed:{moneda}` | Recargo por mora: días después del vencimiento, porcentaje de lo que se debe y fijo por moneda. Sin días no hay recargo automático. Con porcentaje o fijo, el staff lo puede aplicar desde la factura ya vencida |
| `Billing:QuoteValidDays` | Vigencia de un presupuesto si no se dice otra. Por defecto 30 días |
| `Billing:TopUp:{moneda}:Min`, `Billing:TopUp:{moneda}:Max` | Carga de saldo a favor desde el portal, con su mínimo y su máximo por moneda. Sin ninguna moneda el portal no la ofrece |
| `Billing:ExchangeRates:Local`, `Billing:ExchangeRates:Currencies:{n}`, `Billing:ExchangeRates:Bna`, `Billing:ExchangeRates:BnaSeries`, `Billing:ExchangeRates:MaxAgeDays`, `Billing:ExchangeRates:IndexPrices` | Cotizaciones: moneda local (`ARS`), monedas que se cotizan (`USD`), traer las del Banco Nación una vez por hora (apagado), su tabla (`divisas` o `billetes`), cuántos días vale una (5) y precios en dólares para cuentas en pesos (apagado). Detalle en [facturación](facturacion/README.md) |
| `Billing:DomainNameservers:{n}` | Nameservers de los dominios nuevos cuando el pedido no trae otros |
| `Billing:DomainExpiryNoticeDays:{n}`, `Billing:DomainGraceNoticeDays:{n}` | Días de los avisos de vencimiento de dominios: antes (30, 7 y 1) y en la gracia (1 y 30) |
| `Billing:DomainLookup:*` | Consulta de disponibilidad y vencimientos por RDAP, con reintentos y el DNS como alternativa. Detalle en [dominios](dominios/README.md) |
| `CompatApi:Enabled`, `CompatApi:Currencies:{id}` | API compatible (apagada por defecto) y las monedas del sistema anterior |
| `Fiscal:ProductionEnabled` | Traba de producción fiscal, apagada por defecto. Mientras lo esté, el núcleo no llama a ninguna operación que pueda autorizar un comprobante en una instancia fiscal de producción (`environment` en `production`): la emisión queda pendiente con el motivo y su trabajo, muerto y visible. El módulo de ARCA tiene su propia traba, `Arca:AllowProduction`. Las dos se prenden recién al pasar a producción, con una decisión explícita del operador |
| `ForwardedHeaders:KnownProxies:{n}`, `ForwardedHeaders:KnownNetworks:{n}` | IPs (o redes CIDR) y redes CIDR de los proxies inversos y balanceadores de confianza delante de los nodos web. Solo a ellos se les cree `X-Forwarded-For` y `X-Forwarded-Proto`. Sin ninguno no se le cree a nadie, ni a loopback |
| `OTEL_EXPORTER_OTLP_ENDPOINT` y demás `OTEL_*` | Logs, métricas y trazas por OpenTelemetry (D-15). Sin destino queda apagado. Ver [observabilidad](observabilidad/README.md) |
| `Logging:Console:FormatterName` | `json` por defecto, una línea estructurada por evento. `simple` para leer a mano |
| `Ui:Brand`, `Ui:AttentionChannel` | Nombre en el portal y canal de atención al cliente que se publica |
| `Payments:SubscriptionsEnabled`, `Payments:TransfersAutoApply`, `Payments:AutoCharge:*`, `Payments:TestPaymentAmount` | Débito por suscripción (apagado por defecto), imputación sola de transferencias y política del débito con medio guardado. Ver [cobros](cobros/README.md) |
| `Staff:Oidc:Authority`, `Staff:Oidc:ClientId`, `Staff:Oidc:ClientSecret` | Login del staff por OIDC, opcional. El secreto va por variable de entorno |
| `Notifications:Send`, `Notifications:OnlyTo` | Si los avisos salen. Apagado (por defecto) se arman y quedan registrados sin salir, como en staging y en el mes en paralelo. `OnlyTo` limita la entrega a una lista de casillas |
| `Notifications:ProductionEnabled` | Traba de producción del correo, apagada por defecto como la fiscal. Mientras lo esté, solo reciben las casillas de `OnlyTo`: ningún cliente recibe un aviso desde staging, el mes en paralelo o una base restaurada de producción. Se mira al encolar y otra vez al mandar. Se prende recién al pasar a producción |
| `Notifications:Host`, `Notifications:Port`, `Notifications:Security`, `Notifications:User`, `Notifications:Password`, `Notifications:From`, `Notifications:FromName` | SMTP. Por defecto puerto 587 con STARTTLS obligatorio. La contraseña llega por variable de entorno (`Notifications__Password`). El remitente es la misma casilla que el usuario |
| `Notifications:ReminderDaysBefore`, `Notifications:OverdueDaysAfter`, `Notifications:FiscalWait` | Días antes del vencimiento en que sale un recordatorio y días después en que sale el aviso de vencida, como listas separadas por coma (`1` y `3` por defecto, por ejemplo `7,3,1` y `3,10,20`), y cuánto espera el aviso de factura al CAE (10 minutos) |
| `Affiliates:LandingUrl` | Adónde lleva el enlace de afiliado `/r/{código}` después de contar el clic. Por defecto, el inicio del portal. Ver [afiliados](afiliados.md) |
| `Content:MaxDownloadBytes`, `Content:AllowedDownloadExtensions:{n}`, `Content:ImageHosts:{n}` | Contenidos para clientes: tope del archivo de una descarga (50 MiB por defecto), extensiones permitidas (reemplazan la lista de fábrica) y hosts desde los que un artículo puede mostrar imágenes, por `https`. Ver [contenidos](contenidos/README.md) |
| `Pdf:GotenbergUrl` | Conversor de PDF (Gotenberg) para facturas y notas. Sin él, la descarga del PDF responde 503. Ver [PDF](pdf/README.md) |
| `Support:MaxAttachmentBytes`, `Support:MaxAttachments`, `Support:AllowedExtensions`, `Support:AutoCloseAfter`, `Support:MaxEmailBytes`, `Support:MaxEmailsPerSenderPerHour`, `Support:EmailCc` | Topes y tipos de los adjuntos, cierre automático de los tickets que esperan al cliente (apagado por defecto), topes del correo entrante y copias de los correos. Son los valores de fábrica: los ajustes de soporte del panel (`/admin/soporte/ajustes`) los reemplazan sin reiniciar. Ver [soporte](soporte.md) |

El importador desde el sistema anterior, su mapa de módulos y la conciliación están en
[diseno/importacion.md](../diseno/importacion.md).

Los avisos de pasarela llegan a `/webhooks/{instancia}`, con un tope de 64 KiB y de 120 pedidos por minuto
por instancia, se escriba el ID como se escriba (`1`, `01` o `+1` son la misma), y de 600 por minuto por IP
para todo `/webhooks`. Quedan guardados crudos hasta que el módulo los verifica: un aviso inválido se queda sin
cuerpo y se borra a los `Workers:KeepDone` días (30), y uno válido conserva la fila y vacía el cuerpo a los
`Workers:KeepWebhookBodies` días (90). La página de retorno es
`/pay/return/{referencia}`.

Ningún pedido puede traer un cuerpo de más de 1 MiB. La API propia (`/api/v1`) y la compatible
(`/includes/api.php`) admiten 1200 pedidos por minuto por IP, y cada API key tiene además su tope, de 600 por
minuto salvo que se le fije otro al emitirla. Pasado un tope responden 429, con Problem Details en la propia y
con el cuerpo de error de la compatible en `/includes/api.php`. La API propia valida la llave antes de leer el
cuerpo. Los topes se cuentan en cada nodo web. Los de IP y la lista de IP de las llaves usan la IP del cliente:
detrás de un proxy o balanceador hay que cargar su dirección en `ForwardedHeaders:KnownProxies` o su red en
`ForwardedHeaders:KnownNetworks`, o todos los clientes cuentan como la IP del proxy.

Detrás de proxies inversos, la IP del cliente sale de `X-Forwarded-For`, que se recorre de derecha a izquierda
mientras cada salto sea un proxy conocido. Hay que declarar todos los proxies del camino, también las redes de
una CDN si está adelante: si falta uno, queda la IP de ese proxy y una API key con lista de IP lo rechaza. Lo
que agregue un proxy no declarado no cuenta. `X-Forwarded-Proto` de un proxy conocido hace que la aplicación
tome el pedido como HTTPS aunque el TLS termine en el proxy.

El documento OpenAPI, `/openapi/v1.json`, se publica solo con el entorno `Development`.

`POST /api/v1/orders` acepta `Idempotency-Key`. La clave vale por API key y cuenta: la misma clave desde la misma
llave devuelve el pedido ya confirmado, con otro cuerpo responde 422, y desde otra llave confirma otro pedido. El
pedido guarda la huella SHA-256 de lo que llegó con la clave, no el texto, así el orden de las propiedades y el
formato del JSON no cuentan. Dos pedidos idénticos mandados al mismo tiempo con la misma clave confirman uno
solo: el que pierde devuelve el que quedó.

Las operaciones del staff sobre un servicio van por la API propia con el permiso `services:write`. Una suspensión
pedida por acá guarda su motivo y un pago no la levanta: solo la suspensión por mora se levanta sola.

| Ruta | Para qué |
|---|---|
| `POST /api/v1/services/{id}/suspend` | Suspensión propia. `reason` es opcional: sin él queda "Suspensión manual" |
| `POST /api/v1/services/{id}/unsuspend` | Reactivar, sea propia o por mora. Si la mora sigue, la vuelve a pedir en su próxima corrida |
| `POST /api/v1/services/{id}/terminate` | Baja inmediata |
| `POST /api/v1/services/{id}/cancel` | Baja a fin de período: desde `at` (por defecto el próximo vencimiento) no se factura y ese día la mora lo da de baja. Responde `cancel_at` |
| `DELETE /api/v1/services/{id}/cancel` | Anula la baja pedida: el servicio vuelve a renovar |
| `PATCH /api/v1/services/{id}` | `no_auto_suspend` exime de la suspensión automática por mora o la vuelve a aplicar. La baja por mora sigue corriendo |

`GET /api/v1/accounts/{id}/services` muestra `cancel_at`, `suspend_reason` y `no_auto_suspend`. La API compatible
hace lo mismo que las dos primeras rutas con `ModuleSuspend` (`accountid` y `suspendreason`) y `ModuleUnsuspend`
(`accountid`). El servicio va en `accountid`, que es como lo llama WHMCS, y también se acepta `serviceid`.
Los tickets se buscan y se abren sin duplicar en `/api/v1/tickets` (permisos `tickets:read` y `tickets:write`, con
`Idempotency-Key`), como lo usan los flujos de IVR y SMS. Ver [soporte](soporte.md).

Las plantillas de los avisos se leen y editan en `/api/v1/notifications/templates` (permisos `templates:read` y
`templates:write`). Una edición reemplaza a la de fábrica de `src/BillingOss.App/Templates/` y borrarla vuelve a
ella. Si una plantilla editada falla al armarse, sale la de fábrica.

El PDF de facturas y notas se baja por `/api/v1/documents/{id}/pdf` y, desde el portal, por
`/facturas/{id}/pdf`. Cómo se arma, qué leyendas lleva y cómo se despliega Gotenberg está en
[PDF](pdf/README.md).

Las emisiones fiscales frenadas (sección 7.1 del [modelo de dominio](../diseno/dominio.md)) las resuelve el staff por
la API propia, con los permisos `fiscal:read` y `fiscal:write`. Cada operación queda en la auditoría con sus datos, y
ninguna llama a una instancia de producción con `Fiscal:ProductionEnabled` apagada:

| Ruta de la API propia | Permiso | Para qué |
|---|---|---|
| `GET /api/v1/fiscal/emissions/{documentId}` | `fiscal:read` | Estado de la emisión, su último error y, si propuso un número, lo que el fisco tiene con ese número (`found`, con el código de autorización y si coincide con el documento) |
| `POST /api/v1/fiscal/emissions/{documentId}/adopt` | `fiscal:write` | Adoptar ese comprobante. Se confirma repitiendo el código de autorización que informa el fisco (`authorization_code`). Solo sobre una emisión pendiente, incierta o en conflicto, sin trabajo en curso y con el número libre |
| `POST /api/v1/fiscal/emissions/{documentId}/retry` | `fiscal:write` | Reintentar: una rechazada o en conflicto vuelve a pendiente y se encola. Una pendiente o incierta cuyo trabajo murió solo se encola, y la incierta se resuelve consultando |
| `GET /api/v1/fiscal/series` | `fiscal:read` | Series del emisor activo, con el último número registrado, cuándo se comprobó contra el fisco y si tienen emisiones en curso |
| `POST /api/v1/fiscal/series/{pointOfSale}/{typeCode}` | `fiscal:write` | Fijar el último número de la serie con `last_number` y `reason` (y `last_date` si hace falta). No se ajusta con emisiones en curso ni inciertas. La próxima emisión vuelve a consultar el último número al fisco |

## Productos sin módulo y dominios

Los productos sin módulo y los dominios sin registrar con API usan el módulo `manual`, que viene con el núcleo
(sección 9 del [protocolo](../diseno/protocolo-modulos-v0.md)). Cada operación con efecto abre una tarea para el
staff, y el servicio o el dominio queda esperando hasta que alguien la cierra:

| Ruta de la API propia | Permiso | Para qué |
|---|---|---|
| `GET /api/v1/staff-tasks` | `tasks:read` | Tareas abiertas, con las instrucciones del producto y los datos del pedido o del dominio |
| `POST /api/v1/staff-tasks/{id}/complete` | `tasks:write` | Cerrarla: `staff_ref`, `fields` y `secrets` en los servicios, `expires_on` y `nameservers` en los dominios |
| `POST /api/v1/staff-tasks/{id}/reject` | `tasks:write` | Rechazarla con `reason`. Un alta rechazada deja el servicio fallido |
| `GET /api/v1/domains?status=expired` | `domains:read` | Dominios por estado o cuenta, por ejemplo los que vencieron sin renovar |
| `POST /api/v1/services/{id}/{operation}` | `services:write` | `suspend`, `unsuspend`, `terminate` o `create` (reintento de un alta fallida). El cuerpo es opcional: `input` va al módulo, por ejemplo `{"input": {"force": true}}` en una baja que el módulo no hace sola |
| `POST /api/v1/services/{id}/actions/{action}` | `services:write` | Una acción del manifiesto del módulo que admite `staff` (reiniciar, reinstalar, cambiar la contraseña), con `params` opcionales. Las credenciales que la acción rota se generan y guardan antes de llamar |

En la misma lista de tareas aparecen las de tipo `review`: las deja un error definitivo de un módulo (`manual_review`,
`invalid_input`, `rejected` y los demás de la sección 10 del protocolo) sobre un servicio, con el código, el mensaje
y los detalles. Las del módulo manual no, porque su tarea rechazada ya es la decisión del staff. El núcleo no reintenta: el staff revisa y vuelve a pedir la operación por la API, que va con otra
generación de la clave del módulo y, si termina bien, cierra la revisión. Mientras tanto una baja que no salió deja
el servicio marcado para la baja, así no se le emiten más renovaciones. Si esa baja la pidió la mora y el cliente
paga, se levanta sola y la revisión se cierra. También deja una revisión (`state_mismatch`) la reconciliación
diaria, que le pregunta el estado de cada servicio activo o suspendido a los módulos que lo informan y guarda lo
observado: si el panel lo ve distinto, el staff decide si pide la operación o corrige el panel, y la revisión se
cierra sola cuando vuelven a coincidir.

Cada producto manual elige en sus parámetros `activation` (`on_payment` se activa al pagarse, `staff` espera la
tarea) y `lifecycle` (`state_only` solo cambia el estado al suspender o dar de baja, `staff` abre una tarea).
Los campos que carga el staff se declaran en `staff_fields`: los `secret` se guardan cifrados como credenciales
del servicio y nunca quedan en la tarea. Un producto con módulo y sin instancia nunca se da por hecho: el
trabajo falla y queda visible.

Los dominios se facturan en la corrida diaria con su precio congelado. Una renovación paga, o una de precio
cero, se le pide al registrar de su instancia. Pasado lo pago el dominio entra en gracia, y pasados los días
de gracia queda vencido. El importador recibe las zonas propias con `--own-zones`, por ejemplo
`--own-zones ejemplo.com.ar,ejemplo.net`, para mandar sus subdominios a la instancia de subdominios propios, y los
dominios de Internet.bs, ResellerClub, OpenProvider y Namecheap a la instancia de su módulo. Un producto con
servertype que el mapa de módulos marca como manual también usa el módulo `manual`. Los registrars con API, la
sincronización diaria, las pantallas y la API de dominios están en [dominios](dominios/README.md).

## Programa de afiliados

Los clientes se suman desde el portal (`/afiliados`), comparten su enlace y cobran comisiones por los servicios que
pagan sus referidos, como saldo a favor o con un retiro que el staff marca pagado. El staff lo administra en
`/admin/afiliados` y por la API propia en `/api/v1/affiliates`. Operación en [afiliados](afiliados.md) y
diseño en [diseno/afiliados.md](../diseno/afiliados.md).

## Mes en paralelo

El comando `daily` del importador corre un día completo del mes en paralelo: saca la línea base del volcado previo al
ciclo del sistema anterior, lo importa en modo sombra, simula el día de Hornero y lo compara contra el volcado
posterior, con un informe de diferencias clasificadas y su historial. `parallel` repite solo la simulación sobre una
base ya importada. Uso y criterios en [paralelo](paralelo.md).
