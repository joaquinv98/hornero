# Portal del cliente y admin

**Estado:** v0, 2026-10-27 · **Decisiones:** [D-10](../../diseno/decisiones.md) y [D-13](../../diseno/decisiones.md)

La interfaz es Razor Pages renderizado en el servidor, con htmx donde suma y sin SPA. La arma solo el proceso con
rol `web` o `all`. El código está en `src/BillingOss.Web` (`Ui.cs`, `Identity/` y `Pages/`) y las tablas en
`src/BillingOss.App` (`Identity/`, en la migración `Integracion`).

## Páginas

| Ruta | Quién | Qué hace |
|---|---|---|
| `/ingresar`, `/ingresar/codigo` | Cualquiera | Ingreso del cliente con contraseña o con un proveedor externo y, si lo tiene, segundo factor |
| `/registro` | Cualquiera | Crear cuenta con correo y contraseña o con Google ([registro e ingreso con Google](../ingreso-clientes/README.md)) |
| `/ingresar/externo`, `/ingresar/externo/{clave}/vuelta`, `/ingresar/externo/confirmar` | Cualquiera | La vuelta del proveedor externo, el vínculo confirmado con la contraseña o el correo y el alta con el proveedor |
| `/recuperar`, `/recuperar/nueva` | Cualquiera | Restablecer la contraseña con un enlace por correo que vence en una hora |
| `/baja`, `/arrepentimiento` | Cualquiera | Botón de baja de servicio y botón de arrepentimiento, con constancia |
| `/` | Cliente | Cuentas, facturas por pagar, servicios activos y saldo a favor |
| `/servicios` | Cliente | Servicios de las cuentas que puede ver |
| `/dominios`, `/dominios/nuevo`, `/dominios/{id}`, `/dominios/{id}/dns` | Cliente con `domains` | Dominios, pedido, ficha y DNS y correo, detallados en [docs/dominios](../dominios/README.md) |
| `/contratar`, `/contratar/{id}`, `/contratar/paquete/{id}` | Cliente que ve los servicios y las facturas de una cuenta activa | Contratar: productos, paquetes y complementos con su precio en la moneda de la cuenta, y el pedido de cada uno con sus opciones y el cupón, calculado antes de confirmar ([catálogo](../../diseno/catalogo.md), sección 8) |
| `/servicios/{id}/cambio` | Cliente que ve los servicios y las facturas de la cuenta | Cambiar el plan, a los productos que permite el suyo, o las opciones visibles si el producto lo deja, con prorrateo ([catálogo](../../diseno/catalogo.md), sección 5) |
| `/facturas`, `/facturas/{id}` | Cliente | Documentos emitidos, detalle con CAE, cuándo se debita sola, el enlace para pagar y el PDF |
| `/facturas/{id}/pdf` | Cliente | PDF del documento ([docs/pdf](../pdf/README.md)) |
| `/afiliados` | Cliente con la capacidad `affiliate`, con el programa prendido o ya afiliado | Programa de afiliados: sumarse, el enlace, el material para compartir, las comisiones y los retiros ([docs/afiliados.md](../afiliados.md)) |
| `/servicios/{id}/uso` | Cliente con la capacidad `services` | Uso medido del servicio: la ventana abierta de cada métrica y los períodos ya facturados ([docs/uso-medido.md](../uso-medido.md)) |
| `/pagar`, `/pagar/transferencia` | Cliente con `invoices` | Pagar una o varias facturas de una cuenta y una moneda (sin elegirlas, todas las de esa moneda): con el saldo a favor, con una pasarela, también en otra moneda con la cotización del día, guardando el medio con consentimiento, o por transferencia, con Ya transferí ([docs/cobros](../cobros/README.md)) |
| `/presupuestos`, `/presupuestos/{id}`, `/cargar-saldo` | Cliente | Aceptar o rechazar presupuestos y cargar saldo a favor ([docs/facturacion](../facturacion/README.md)) |
| `/cuenta/medios-de-pago` | Cliente con `invoices` | Medios guardados, débito automático y débito por suscripción |
| `/debito-automatico` | Cualquiera | Condiciones del débito automático |
| `/cuenta/seguridad` | Cliente | Correo verificado, contraseña, segundo factor con sus códigos de recuperación y últimos ingresos ([fraude y acceso](../fraude-y-acceso/README.md)) |
| `/cuenta/verificar` | Cualquiera | Confirma el correo con el enlace que le llegó |
| `/cuenta/datos-fiscales` | Cliente titular o con la capacidad de facturación | Datos fiscales de cada cuenta: condición frente al IVA con las descripciones de ARCA (la 7 solo con la percepción de la RG 2126), documento, provincia como código ISO 3166-2, "Completar con ARCA" y el número de IVA del exterior con su validación ([docs/fiscal/impuestos.md](../fiscal/impuestos.md)) |
| `/salir` | Cliente | Cierra la sesión, o la vista de cliente del staff |
| `/admin/ingresar`, `/admin/ingresar/codigo` | Staff | Contraseña u OIDC, y siempre TOTP. Si no lo tiene, lo enrola ahí |
| `/admin` | Staff con `accounts.read` | Buscar cuentas por número, nombre, empresa o correo |
| `/admin/cuentas/{id}` | Staff con `accounts.read` | Cuenta con usuarios, servicios, dominios, documentos, pagos y trámites. "Ver como cliente" con `accounts.impersonate`. El bloque "Datos fiscales" (condición frente al IVA, "Completar con ARCA", número de IVA, empresa grande y reglas de cuenta) se edita con `taxes.edit`. Con `billing.read`, el saldo a favor con su libro, y con `billing.write` las exenciones de recargo y de recordatorios. "Estado de cuenta" con `reports.statement` o `reports.read` |
| `/admin/dominios`, `/admin/dominios/{id}`, `/admin/dominios/{id}/dns`, `/admin/dominios/precios`, `/admin/dominios/consulta`, `/admin/registradores` | Staff con `domains.read`, y `domains.manage` para operar | Cola de dominios por trabajo pendiente, ficha con sus operaciones, DNS y correo, precios por TLD, consulta de un nombre y registradores |
| `/admin/pedidos/nuevo` | Staff con `accounts.read` y `orders.write` | Pedidos, Agregar pedido: pide por una cuenta cualquier producto, también los que la tienda no muestra, con todas sus opciones y el cupón, calculado antes de confirmar, y puede aceptarlo para aprovisionar sin esperar el pago ([catálogo](../../diseno/catalogo.md), sección 8) |
| `/admin/servicios/{id}/cambio` | Staff con `services.change` | Cambiar el producto, el ciclo o las opciones de un servicio, con o sin prorrateo, y cancelar un cambio que espera el pago |
| `/admin/catalogo` | Staff con `catalog.read` | Configuración, Productos y servicios: productos por grupo con su precio base, complementos, grupos de opciones y paquetes ([catálogo](../../diseno/catalogo.md)) |
| `/admin/catalogo/cupones` | Staff con `catalog.read` | Configuración, Promociones y cupones: primero los que se pueden usar hoy |
| `/admin/catalogo/productos/{id}`, `/admin/catalogo/grupos/{id}`, `/admin/catalogo/opciones/{id}`, `/admin/catalogo/paquetes/{id}`, `/admin/catalogo/cupones/{id}` | Staff con `catalog.read`, y `catalog.write` para guardar | Alta (`nuevo`) y edición de productos con su tabla de precios, grupos de productos, grupos de opciones con sus valores y precios, paquetes y cupones |
| `/admin/facturas`, `/admin/presupuestos`, `/admin/items`, `/admin/monedas` | Staff con `billing.read` | Facturas manuales, presupuestos, ítems facturables y cotizaciones, del menú Facturación y Configuración ([docs/facturacion](../facturacion/README.md)) |
| `/admin/afiliados`, `/admin/afiliados/{id}`, `/admin/afiliados/retiros`, `/admin/afiliados/programa` | Staff con `affiliates.read`, y `affiliates.manage` para cambiar algo | Afiliados con su saldo, referidos y comisiones, los retiros y los ajustes del programa |
| `/admin/reportes`, `/admin/reportes/{reporte}`, `/admin/reportes/libro-iva`, `/admin/reportes/pdf-facturas` | Staff con el permiso de cada reporte o `reports.read` | Reportes y salidas contables con gráficos, CSV, el Libro IVA Digital y los PDF por lote ([docs/reportes](../reportes/README.md)) |
| `/soporte`, `/soporte/nuevo`, `/soporte/{id}` | Cliente con soporte en la cuenta | Tickets: abrir con adjuntos, responder, cerrar y calificar ([docs/soporte.md](../soporte.md)) |
| `/soporte/encuesta/{ficha}` | Cualquiera con el enlace | Encuesta de satisfacción de un ticket, sin iniciar sesión |
| `/admin/soporte` y sus páginas | Staff con `support.tickets` o `support.settings` | Resumen del soporte, cola de tickets con las vistas del submenú, filtros, orden, paginado, acciones masivas (asignar, cerrar, unir y bloquear al remitente) y CSV, ticket con notas internas y unión, ajustes de soporte, departamentos y casillas, respuestas predefinidas, reglas de escalamiento, control de spam y registro del correo entrante |
| `/admin/cuentas/{id}/tickets` | Staff con `support.tickets` | La solapa Tickets de la ficha del cliente: la misma cola con la cuenta fija |
| `/admin/servicios/{id}/uso` | Staff con `accounts.read` | Uso medido del servicio con las lecturas diarias |
| `/admin/catalogo/productos/{id}/uso` | Staff con `catalog.write` | Métricas de uso del producto, lo incluido y los precios del excedente |
| `/conocimiento`, `/anuncios`, `/descargas`, `/estado-red` | Cualquiera | Base de conocimiento, anuncios, descargas y estado de la red, con lo que le corresponde a cada cliente ([docs/contenidos](../contenidos/README.md)) |
| `/admin/contenidos`, `/admin/conocimiento`, `/admin/anuncios`, `/admin/descargas`, `/admin/estado-red` | Staff con `content.read`, `content.write` o `status.write` | Administración de esos cuatro contenidos: la portada con lo pendiente y una pantalla por área ([docs/contenidos](../contenidos/README.md)) |
| `/admin/cobros`, `/admin/cobros/{instancia}` | Staff con `payments.manage` | Configuración, Pasarelas de pago: las pasarelas incluidas y el asistente de cada instancia ([guía de cobros](../cobros/README.md)) |
| `/admin/cobros/cuentas-para-transferir` | Staff con `payments.manage` | Cuentas para transferir con el mapa de columnas de su extracto, desde Pasarelas de pago |
| `/admin/cobros/transferencias` | Staff con `payments.manage` | Facturación, Pagos sin imputar: extractos, bandeja sin identificar y avisos de Ya transferí |
| `/admin/cobros/automaticos` | Staff con `payments.manage` | Facturación, Cobros automáticos: los débitos con medio guardado con su estado, filtro, páginas y CSV |
| `/admin/registro-modulos` | Staff con `payments.manage` | Facturación, Registro de pasarelas: los avisos de cada pasarela tal como llegaron y lo que dejaron. En el menú es también la solapa Módulos y pasarelas de Registros del sistema |
| `/admin/impuestos`, `/admin/impuestos/empezar`, `/admin/impuestos/reglas`, `/admin/impuestos/precios` | Staff con `taxes.edit` | Configuración, Impuestos: simulador y avisos, preset de tres preguntas, categorías, reglas y percepciones, precios y muestra ([docs/fiscal/impuestos.md](../fiscal/impuestos.md)) |
| `/admin/fiscal`, `/admin/fiscal/arca/{paso}` | Staff con `fiscal.edit` | Configuración, ARCA: ficha del emisor y el asistente "Conectar con ARCA" de siete pasos ([docs/fiscal/alta-arca.md](../fiscal/alta-arca.md)) |
| `/admin/configuracion/webhooks` | Staff con `integrations.manage` | Configuración, API y webhooks: llaves de la API y webhooks salientes ([docs/integraciones](../integraciones/README.md)) |
| `/admin/configuracion/webhooks/{id}` | Staff con `integrations.manage` | Un webhook: cambios, prueba, secreto, borrado y sus últimas entregas con reenvío |
| `/admin/complementos/extensiones`, `/admin/complementos/extensiones/{id}` | Staff con `integrations.manage` | Complementos, Extensiones: alta, ganchos y baja |
| `/admin/seguridad`, `/admin/pedidos/revision` | Staff | Seguridad propia, ajustes, listas de bloqueo, ingresos y revisión de pedidos, cada una con su permiso ([fraude y acceso](../fraude-y-acceso/README.md)) |
| `/admin/seguridad/proveedores` | Staff con `security.manage` | Proveedores externos del portal, con el secreto cifrado y la prueba de la conexión ([registro e ingreso con Google](../ingreso-clientes/README.md)) |
| `/admin/actualizaciones` | Staff con `system.updates` | Configuración, Actualizaciones ([actualizador](../despliegue/actualizador.md)) |
| `/estado/{código}` | Cualquiera | Errores de la interfaz (400, 403, 404, 429), con el código original. Abierta directo responde con el código de la ruta, y uno que no es de error vale como 404 |
| `/idioma/{es-AR o en}` | Cualquiera | Guarda el idioma en una cookie y vuelve a la página |

El menú del admin sigue el mapa de la navegación clásica (sección 10 de la decisión de interfaz, enmienda del
29/10): cada pantalla cuelga de su área y un ítem sin pantalla no se muestra. Las áreas con varios ítems son
desplegables que se abren con un clic, sin JavaScript. Configuración tiene Productos y servicios, Promociones y
cupones y Actualizaciones, cada uno con su permiso. Agregar pedido cuelga de Pedidos y también de la ficha del
cliente. El cambio de plan cuelga de la ficha del cliente en el admin y de Servicios en el portal. En el portal,
Servicios es un desplegable con Mis servicios y Contratar.

La API, los avisos de pasarelas, la API compatible y la salud no cambian: no pasan por las páginas de error ni
por los encabezados de seguridad de la interfaz.

## Menú

La navegación sigue la sección 10 de la decisión de interfaz (enmienda del 29/10): una barra superior con áreas que
despliegan sus ítems con un clic, sin barra lateral, para que quien viene de otro sistema encuentre las cosas donde
las busca. Cada área es un `<details class="bo-menu">` con su lista, así se abre con clic, toque o teclado sin
JavaScript. El atributo `name` deja una sola abierta, y `wwwroot/js/menu.js` solo la cierra al hacer clic afuera o con
Escape. Cada staff ve solo los ítems que su permiso le deja usar, un ítem cuya función todavía no existe no se
muestra, y el de la página abierta lleva `aria-current`.

| Menú | Áreas e ítems de hoy |
|---|---|
| Admin | Clientes (Ver clientes, Dominios y Afiliados), Pedidos (Agregar pedido), Facturación (facturas, presupuestos, ítems y notas de crédito, y Pagos sin imputar, Cobros automáticos y Registro de pasarelas), Soporte, Contenidos, la revisión de pedidos, Reportes, Utilidades (Correo entrante y Consulta de dominios), Complementos (Extensiones), Seguridad y Configuración (Productos y servicios, Promociones y cupones, Dominios y precios por TLD, Registradores, Pasarelas de pago, Impuestos, ARCA, Monedas, Webhooks, Actualizaciones y los ajustes del soporte), cada ítem con su permiso, con el nombre del staff y Salir a la derecha |
| Portal | Inicio, Servicios (Mis servicios y Contratar), Dominios (Mis dominios, Registrar un dominio y Transferir un dominio), Facturación (Facturas, Presupuestos, Medios de pago y Cargar saldo), Soporte, Afiliados si está prendido, los contenidos y, a la derecha, Cuenta (Datos fiscales, Seguridad y Salir) |

El tono es el de la sección 10.4: tipografía del sistema de 15 px en el portal y 14 px en el admin, bordes finos y
sin sombras. En pantallas angostas los ítems de cada área se abren debajo, en el lugar.

## Identidad

Dos ASP.NET Core Identity, uno por tabla de usuarios: `identity.portal_users` para clientes y
`identity.staff_users` para el staff. Un ID de una tabla nunca sirve en la otra, y cada uno tiene su cookie:

| Cookie | Esquema | Ruta | SameSite | Dura |
|---|---|---|---|---|
| `bo_portal` | `portal` | `/` | Lax, para la vuelta de la pasarela por GET | 8 horas que se renuevan con el uso |
| `bo_portal_2fa` | `portal.2fa` | `/` | Strict | 5 minutos, entre la contraseña y el código |
| `bo_staff` | `staff` | `/admin` | Strict | 8 horas que se renuevan con el uso |
| `bo_staff_2fa` | `staff.2fa` | `/admin` | Strict | 5 minutos |
| `bo_staff_ext` | `staff.external` | `/admin` | Lax, para la vuelta del proveedor OIDC | 5 minutos |
| `bo_portal_oidc` | `portal.oidc` | `/ingresar/externo` | Lax, para la vuelta del proveedor externo del cliente | 10 minutos, con el state, el nonce y el verificador de PKCE |
| `bo_portal_ext` | `portal.external` | `/ingresar/externo` | Lax | 10 minutos, entre la vuelta del proveedor y el ingreso, el vínculo o el alta |
| `bo_af` | Antiforgery | `/` | Strict | La sesión del navegador |

Todas son `HttpOnly` y `Secure`. El sitio se sirve solo por HTTPS. Detrás de un proxy que termina TLS, la
aplicación ve el pedido como HTTPS solo si el proxy está declarado en `ForwardedHeaders:KnownProxies` o
`ForwardedHeaders:KnownNetworks` y manda `X-Forwarded-Proto: https`. Si no, la interfaz responde 400 con el aviso de
que hace falta HTTPS o declarar el proxy, y lo deja en el log una vez por proceso, en vez del 500 que daba el
antiforgery ([docs/despliegue](../despliegue/README.md)). La API, los avisos y la salud no dependen de TLS. Las pruebas
`DetrasDeUnProxyQueTerminaTls_LaInterfazAndaYVeAlCliente` y `SinTls_LaInterfazAvisaQueHaceFaltaHttpsOElProxy_EnVezDe500`
lo cubren.

- **Contraseñas:** la política sale de los ajustes del panel. Por defecto 10 caracteres o más para clientes y 12 para
  el staff, sin reglas de composición y sin las comunes ([fraude y acceso](../fraude-y-acceso/README.md)). Hash de
  Identity (PBKDF2).
- **Bloqueo:** 5 intentos fallidos de contraseña o de código bloquean la cuenta 15 minutos, un valor
  habitual. Los dos números se cambian en los ajustes, y además hay un bloqueo temporal por IP. El bloqueo no corta
  las sesiones abiertas, para que nadie pueda echar a otro fallando a propósito.
- **Tope por IP:** 20 envíos cada 5 minutos por IP en los formularios de ingreso, código, recuperación y trámites.
  Es por nodo. Con varios nodos el tope global va en el balanceador.
- **Sesiones:** cada pedido confirma contra la base que el usuario sigue habilitado y que su sello no cambió.
  Cambiar la contraseña o el segundo factor corta las demás sesiones en todos los nodos.
- **Segundo factor:** TOTP (RFC 6238) con cualquier app de autenticación, más 8 códigos de recuperación de un solo
  uso. Para el cliente es opcional, salvo que lo tuviera en el sistema anterior o que su rol lo pida en los ajustes de
  seguridad: hasta activarlo solo ve la página de seguridad y la salida, y después no lo puede desactivar
  ([fraude y acceso](../fraude-y-acceso/README.md)). Para el staff es obligatorio: sin TOTP no entra, y en el primer
  ingreso lo enrola. La semilla y los códigos se guardan cifrados por campo con la llave maestra (D-09), así que sin
  `Secrets:KeyFile` no se puede activar el segundo factor y el staff no puede entrar.
- **Antiforgery:** en todos los formularios. Un envío sin token da 400.
- **Recuperación:** el enlace se arma con `Billing:PublicUrl`, nunca con el Host del pedido. La respuesta es la
  misma exista o no el correo.

### Contraseñas importadas

El importador guarda el hash de origen tal cual en `password_hash`. Si es bcrypt (`$2a$`, `$2b$` o `$2y$`), se
verifica en el primer ingreso y en ese mismo pedido se reemplaza por el hash de Identity. Lo habitual es bcrypt
`$2y$` de costo 10. Cualquier otro formato no verifica y esa persona entra restableciendo la
contraseña. Falta confirmar en staging, con una cuenta de contraseña conocida, que el sistema anterior no
transformaba la contraseña antes del bcrypt.

Los usuarios que tenían TOTP llegan con `two_factor_required`: las semillas no se migran, y al ingresar se los
manda a activar uno nuevo.

### Usuarios y cuentas

Un usuario del portal puede operar varias cuentas (`identity.account_users`). El titular puede todo y hay uno
solo por cuenta. Los demás tienen capacidades con nombre propio (`services`, `invoices`, `domains`). Los roles de
facturación y técnico son atajos que llenan ese conjunto.

La propiedad se decide en un solo lugar, `PortalAccess.AccountIds`, y toda consulta del portal se filtra con esa
subconsulta. Lo ajeno da 404, igual que lo que no existe.

### Staff

El staff no se importa. El primero se crea con el comando:

```powershell
dotnet BillingOss.Web.dll staff add persona@example.com "Nombre" accounts.read,accounts.impersonate
```

Muestra la contraseña inicial una sola vez. Otros comandos: `staff oidc <correo> <sub>` vincula el usuario del
proveedor OIDC y `staff disable <correo>` lo deshabilita y corta sus sesiones.

Permisos de v0: `accounts.read` (buscar y ver cuentas, y el uso medido de sus servicios), `accounts.impersonate` (ver
como cliente), `orders.write` (hacer un pedido por una cuenta y aceptarlo sin el pago), `services.change`
(cambiar el plan, el ciclo o las opciones de un servicio), `domains.read` (ver dominios y precios por TLD), `domains.manage` (operar dominios y cambiar
precios), `billing.read` (ver la facturación), `billing.write` (armar y emitir facturas manuales, presupuestos e
ítems, aplicar saldo y recargos, eximir cuentas y cargar cotizaciones), `credit.adjust` (ajustar el saldo a favor a
mano), `payments.manage` (pasarelas, cuentas para transferir, extractos, pagos sin imputar y cobros automáticos),
`support.tickets` (ver, responder y gestionar tickets), `support.settings` (departamentos, casillas, filtros, respuestas
predefinidas y reglas de escalamiento), `content.read`, `content.write` y `status.write` (los contenidos para clientes,
ver [docs/contenidos](../contenidos/README.md)), `affiliates.read` y `affiliates.manage` (el programa de afiliados, ver
[docs/afiliados.md](../afiliados.md)), `catalog.read` (ver el catálogo), `catalog.write`
(cambiarlo, también las métricas de uso de un producto y sus precios),
`integrations.manage` (llaves de la API, webhooks y extensiones), `taxes.edit` (impuestos y datos fiscales de las
cuentas), `fiscal.edit` (el asistente de ARCA), `system.updates` (actualizaciones), `security.read`, `security.manage` y
`orders.review` ([fraude y acceso](../fraude-y-acceso/README.md)), uno por reporte (`reports.revenue`, `reports.taxes` y
los demás de [docs/reportes](../reportes/README.md#permisos-y-auditoría)) y `reports.read`, que alcanza para todos los
reportes. Los de soporte (`reports.support`, `reports.staff-replies` y `reports.satisfaction`) leen los tickets y la
encuesta. Un formulario armado a mano sin el permiso da 403.

### Login del staff por OIDC

Opcional. Se prende configurando `Staff:Oidc:Authority`, `Staff:Oidc:ClientId` y `Staff:Oidc:ClientSecret`. El
secreto sale del vault por variable de entorno (`Staff__Oidc__ClientSecret`), nunca de un archivo del repo. En
Authentik se crea un proveedor OAuth2/OpenID confidencial con la URL de vuelta
`https://<billing>/admin/oidc/callback` y los alcances `openid`, `email` y `profile`.

El usuario se busca por el `sub` que vinculó un administrador con `staff oidc`. Nunca se vincula solo por correo.
Después del proveedor sigue el TOTP propio, como con contraseña.

### Ver como cliente

Desde la ficha de la cuenta, con `accounts.impersonate`. Abre una sesión del portal atada a la cuenta, no a un
usuario, que dura una hora y se renueva con el uso mientras el staff siga habilitado y con el permiso. Todas las
páginas muestran el aviso con la cuenta y quién mira, y la página de seguridad no existe en esa vista. El
comienzo y el fin quedan en la auditoría (`portal.impersonate` y `portal.impersonate_end`).

## Auditoría

`platform.audit_events` es la tabla de auditoría del núcleo (D-15, [docs/observabilidad](../observabilidad/README.md)):
instante, tipo de actor y actor (`staff:3`, `user:12`, `public`), acción, entidad (`account:5`), IP, detalle en
JSON sin secretos y traza. La base solo deja agregar filas. Hoy la interfaz registra el ingreso del staff, el
enrolado del TOTP, el segundo factor del cliente, "ver como cliente", los trámites de baja y arrepentimiento y cada
descarga de un reporte (`report.export`).

El actor lo fija la interfaz en cada pedido, a partir de la sesión: el staff con la suya o mirando como cliente
(`staff:3`), el cliente con la suya (`user:12`) y `public` sin sesión. Viaja con los trabajos que se encolan desde
la página, igual que el de la API.

## Baja y arrepentimiento

Los dos botones están en el encabezado de todas las páginas del portal, también antes de ingresar, con los nombres
de la Disposición 954/2025. El trámite no pide sesión:

1. La persona escribe el correo de la cuenta y, si quiere, qué servicio.
2. Si el correo es de alguna cuenta, le llega un código de 6 dígitos que vence en 30 minutos. La pantalla dice lo
   mismo exista o no la cuenta.
3. Con el código queda el pedido en `services.cancellation_requests` con un código de constancia (`BAJA-...` o
   `ARRE-...`), que se muestra en pantalla y se manda por correo. Cada código admite cinco intentos.

El staff ve los trámites en la ficha de la cuenta. La ejecución (no renovar, dar de baja con el módulo, anular con
nota de crédito lo ya emitido y devolver lo cobrado en un arrepentimiento) es parte del flujo de servicios, todavía
no construido. El canal y el horario de atención que pide el art. 6 se muestran si se configura
`Ui:AttentionChannel`.

## Correo

La interfaz no manda correos: llama a `IAccountMail` (`src/BillingOss.App/Identity/AccountMail.cs`), que
implementan las notificaciones (`NotifierAccountMail`) poniendo cada aviso en la cola de salida con reintentos.
Son cinco avisos: enlace para restablecer la contraseña, código del trámite, constancia del trámite, enlace para
verificar el correo e ingreso desde un lugar nuevo, con las plantillas `password-reset`, `consumer-code`,
`cancellation-receipt`, `withdrawal-receipt`, `email-verification` y `new-login`. La constancia tiene que
salir dentro de las 24 horas: los reintentos de la cola cubren casi un día. Valen las trabas del correo
(`Notifications:Send` y `Notifications:ProductionEnabled`, ver el README): sin ellas, el aviso queda registrado sin
salir. El enlace y el código quedan en el correo armado (`notifications.outbound_emails`) hasta que vencen, nunca en
el log.

## Idiomas y textos

Los textos están en `src/BillingOss.Web/Resources/Texts.resx` (es-AR) y `Texts.en.resx`. La cultura sale de la
cookie de idioma o del navegador, con es-AR por defecto. Los nombres legales de los botones quedan en castellano
también en inglés. Una prueba controla que los dos archivos tengan las mismas claves y que exista toda clave que
usan las páginas, incluidas las de los estados.

## htmx

El buscador del admin usa htmx 1.9.12 (`wwwroot/lib/htmx`, BSD-2-Clause) para traer los resultados mientras se
escribe, tomando el bloque de resultados de la misma página. Sin JavaScript, el formulario funciona igual.

## Varios nodos

La interfaz no guarda estado en memoria: sesiones, bloqueos, códigos y trámites están en PostgreSQL. El anillo de
llaves de Data Protection (cookies, antiforgery y enlaces de recuperación) está en `platform.data_protection_keys`,
cifrado con la llave maestra ([docs/alta-disponibilidad](../alta-disponibilidad/configuracion.md)), así que una cookie
emitida en un nodo se lee en cualquier otro. Para eso cada nodo web necesita `Secrets:KeyFile`: sin él, el nodo
usa llaves propias y sus cookies no sirven en los demás.

## Configuración

| Clave | Para qué |
|---|---|
| `Ui:Brand` | Nombre que muestran el encabezado y la app de TOTP. Por defecto `billing-oss` |
| `Ui:AttentionChannel` | Canal y horario de atención al cliente, en el pie y en los trámites |
| `Staff:Oidc:Authority`, `Staff:Oidc:ClientId`, `Staff:Oidc:ClientSecret` | Login del staff por OIDC. Sin `Authority` queda apagado |
| `Billing:PublicUrl` | También arma el enlace para restablecer la contraseña |
| `Secrets:KeyFile` | Obligatorio para el segundo factor y para compartir las llaves de las cookies entre nodos |

## Pendiente

- Ingreso con Google para clientes. Sin eso, el día del corte quienes entraban con Google tienen que restablecer la
  contraseña.
- Registro, invitaciones a operar una cuenta, verificación de correo y cambio de contraseña con sesión.
- Código QR para enrolar el TOTP: hoy se muestra la clave y el enlace `otpauth`.
- Passkeys, cuando la interfaz de Identity las traiga fuera de Blazor.
- Más permisos del staff a medida que crezca el admin.
