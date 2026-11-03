# Decisiones de arquitectura v0

**Estado:** borrador v0 · **Parte de:** [diseño del núcleo](README.md)

Cada decisión dice qué se eligió, por qué y qué implica. Las **aceptadas** están firmes, las **propuestas** son
del diseño y se pueden discutir, y las **abiertas** esperan una decisión de los mantenedores.

| ID | Decisión | Estado |
|---|---|---|
| D-01 | Plataforma: .NET 10 LTS | Aceptada |
| D-02 | Base de datos: PostgreSQL | Aceptada |
| D-03 | Monolito modular | Propuesta |
| D-04 | Dinero, monedas e impuestos | Propuesta |
| D-05 | Documentos inmutables y comprobantes fiscales aparte | Propuesta |
| D-06 | Cobros y webhooks | Propuesta |
| D-07 | Outbox, cola de trabajos y planificador sin líder | Propuesta |
| D-08 | Módulos fuera de proceso | Aceptada |
| D-09 | Secretos y cifrado por campo | Propuesta |
| D-10 | Identidad y acceso | Propuesta |
| D-11 | API propia | Propuesta |
| D-12 | API compatible | Aceptada |
| D-13 | Interfaz, plantillas, PDF e idiomas | Propuesta |
| D-14 | Despliegue y alta disponibilidad | Propuesta |
| D-15 | Observabilidad | Propuesta |
| D-16 | Pruebas | Propuesta |
| D-17 | Importación con IDs preservados | Aceptada |
| D-18 | Licencia y nombre | Aceptada |
| D-19 | Apariencia y plantillas editables | Propuesta |
| D-20 | Actualizaciones y seguridad | Propuesta |
| D-21 | Fiscal, impuestos y cobros incluidos | Propuesta |

---

## D-01. Plataforma: .NET 10 LTS

**Estado:** aceptada.

**Decisión:** C# sobre .NET 10 LTS y ASP.NET Core. Se pasa a cada LTS nueva dentro de los 6 meses de su
salida. Una sola solución con el núcleo, el importador, los módulos de referencia y las herramientas.

**Por qué:** `decimal` nativo para el dinero, firma CMS de fábrica para ARCA, SDK oficiales de pagos y
protecciones por defecto (antiforgery, Data Protection, Identity).

**Consecuencias:**
- Política de dependencias escrita: primero lo de Microsoft, nada de las librerías que se volvieron
  comerciales (MediatR, AutoMapper, MassTransit, FluentAssertions), archivos de bloqueo y origen de paquetes
  fijo.
- Toda dependencia nueva se justifica en el pedido de cambio, con su licencia.

---

## D-02. Base de datos: PostgreSQL

**Estado:** aceptada.

**Decisión:** PostgreSQL 17 o posterior (la 17 es la de Debian 13) con Npgsql y EF Core 10. Cada módulo del
núcleo tiene su propio esquema (`billing`, `services`, `fiscal` y así). Instantes en `timestamptz`, fechas de
negocio en `date`, dinero en `numeric` y datos flexibles (parámetros de módulos, campos personalizados) en
`jsonb` validado por esquema.

**Por qué:** conector al día, replicación y failover con herramientas libres.

**Consecuencias:**
- Las invariantes van primero en la base: restricciones únicas y de exclusión, claves foráneas y checks. El
  código del dominio las repite para dar buenos errores, pero la base es la última palabra.
- Sin aislamiento por fila en v0: el hosting gestionado usa una base por proveedor, que es más simple y aísla
  mejor. Se revisa si llega a cientos de proveedores chicos.

---

## D-03. Monolito modular

**Estado:** propuesta.

**Decisión:** un solo ejecutable con los módulos del [modelo de dominio](dominio.md). Cada módulo es un
proyecto con su esquema de base, expone casos de uso y publica eventos. El mismo binario corre con un rol:
`web`, `worker` o `all`.

**Por qué:** un equipo chico no puede operar microservicios. Los límites entre módulos dan casi
todo el beneficio de separarlos sin la red en el medio.

**Consecuencias:**
- Un módulo nunca lee tablas de otro. Lo vigilan tests de arquitectura en cada build.
- La excepción son los reportes: un módulo de solo lectura que agrega sobre los esquemas de los demás, desde la
  réplica cuando hay una ([docs/reportes](../docs/reportes/README.md)).
- Casos de uso como clases simples, sin mediador.
- Si un módulo necesita escalar aparte, se separa por su frontera ya existente.

---

## D-04. Dinero, monedas e impuestos

**Estado:** propuesta. Se valida contra la homologación de ARCA.

**Decisión:**
- Tipo `Money` con monto `decimal` y moneda ISO 4217. Sumar monedas distintas es un error, nunca una
  conversión implícita.
- Impuestos **por factura y por alícuota**, con las reglas de la sección 6.2 del [modelo de dominio](dominio.md)
  y redondeo comercial con la mitad hacia arriba. Las líneas solo muestran su parte.
- Reglas de impuesto con vigencia, por país y provincia del receptor, su condición frente al IVA y la clase
  del producto. Sin regla aplicable, el cálculo falla.
- Cotizaciones guardadas con fuente, fecha y hora. Cada documento guarda la que usó.
- Un cobro en otra moneda que la factura congela en el intento el monto convertido y la cotización, y se
  imputa con ella. El saldo a favor se lleva por moneda.

**Por qué:** WSFEv1 no recibe líneas y valida el IVA de cada alícuota con una tolerancia chica (un centavo
según su manual, a confirmar en homologación). En una simulación del revisor fiscal, con precios de 1 a 30 USD
e IVA incluido, el redondeo por línea fallaba en el 10 % de las facturas de 3 líneas y en el 46 % de las de 15.
Por factura y alícuota, en ninguna, y es el criterio que ARCA acepta en producción. La simulación se reproduce en los
tests de tabla.

**Consecuencias:** tests de tabla con casos reales (IVA incluido y aparte, varias alícuotas, moneda
extranjera, la tolerancia de ARCA) desde el primer día.

---

## D-05. Documentos inmutables y comprobantes fiscales aparte

**Estado:** propuesta.

**Decisión:**
- Factura, nota de crédito y nota de débito son documentos comerciales. Se emiten con un cierre explícito y
  quedan congelados: un trigger de la base rechaza cambios en importes, líneas, impuestos y receptor de un
  documento emitido.
- El estado de la factura se deriva de importes, notas e imputaciones, no se guarda.
- Los períodos de renovación se reservan en una tabla con restricción de exclusión. Anular una factura por
  completo los libera, sin tocar la factura.
- El comprobante fiscal es otra entidad, uno por documento comercial cuando corresponde, con su número y su
  diario de emisión (sección 7 del [modelo de dominio](dominio.md)).
- Si el fisco rechaza por datos del receptor, la factura se anula con una nota de crédito sin comprobante y
  se emite otra con la ficha corregida.
- El PDF se genera una vez, cuando el documento queda completo (con CAE si corresponde), y se guarda.

**Por qué:** separar lo comercial de lo fiscal deja el núcleo útil fuera de Argentina, y la inmutabilidad es
lo que exige cualquier fisco.

**Consecuencias:** las correcciones son siempre notas. La interfaz no ofrece "editar factura" una vez emitida.

---

## D-06. Cobros y webhooks

**Estado:** propuesta.

**Decisión:**
- Recepción de webhooks en dos pasos. Lo que llega a `/webhooks/{instancia}` se guarda crudo y sin
  unicidad, con tope de tamaño y de pedidos, y se responde enseguida. Un trabajo lo hace verificar por el
  módulo, y recién los eventos verificados tienen restricción única por instancia e ID de evento. Si la
  unicidad se aplicara al recibir, un pedido falso podría ocupar el ID de un evento real.
- El webhook nunca alcanza para dar algo por pagado: se consulta el pago a la pasarela
  (sección 9.3 del [protocolo](protocolo-modulos-v0.md)).
- Restricción única por instancia e ID externo de pago. Monto, moneda y referencia se validan contra el
  intento.
- Los estados del pago siguen el diagrama de la sección 8.1 del modelo, con disputa y contracargo. Un aviso
  que no encaja se registra y se ignora.
- Imputaciones, notas y movimientos de saldo toman los locks de la sección 13 del modelo.
- En un reembolso, primero la nota de crédito y después la plata.
- Conciliación diaria con `list_payments` donde la pasarela lo permite.
- Débito automático con medio guardado (`payment.saved_methods`): el núcleo decide cuándo y cuánto cobrar, con
  consentimiento explícito y constancia, clave `autocharge-{intento}` y nunca otra clave a ciegas. La transferencia
  se concilia en el núcleo por el CUIT del ordenante. Detalle en la [guía de cobros](../docs/cobros/README.md).

**Por qué:** es el camino del dinero, donde un error llega directo a los clientes.

**Consecuencias:** tests con webhooks grabados: válido, firma mala, falso, repetido, fuera de orden y dos a la
vez.

---

## D-07. Outbox, cola de trabajos y planificador sin líder

**Estado:** propuesta.

**Decisión:**
- **Outbox:** los eventos se guardan en la misma transacción que el cambio. Implementado en v0 como un trabajo
  por suscriptor en la misma cola: no hace falta un despachador aparte, y cada suscriptor corre al menos una
  vez, con la clave de entidad que necesite.
- **Trabajos:** cola en una tabla, con prioridad, fecha de ejecución, intentos, espera creciente y estado final
  visible para el staff. Un trabajo se toma con un lease (`locked_until`) y se ejecuta fuera de toda
  transacción. Si el proceso muere, el lease vence y otro lo retoma. Los de una misma entidad corren en orden
  y de a uno.
- **Planificador sin líder:** ciclo de facturación, mora y reconciliaciones corren en intervalos cortos en
  cualquier nodo. Cada intervalo toma `pg_try_advisory_xact_lock` y solo encola trabajos con
  `INSERT ... ON CONFLICT DO NOTHING`. La corrección la dan las claves únicas. El lock solo evita trabajo
  repetido y funciona a través de un pooler en modo transacción.
- **Series fiscales:** se toman con un lease, una fila con dueño y vencimiento, nunca con un lock abierto
  durante la llamada al fisco.
- Sin broker de mensajes en v0.

**Por qué:** una sola pieza de infraestructura para operar, y nada que se rompa si dos nodos creen que les
toca. Un líder por lock de sesión se pierde en silencio al reconectar y no viaja en un failover.

**Consecuencias:** un failover a mitad de una corrida no duplica nada, y el reintento emite los borradores que
quedaron sin emitir. Si algún día el volumen lo pide, el
despachador pasa a un broker sin cambiar a los consumidores.

---

## D-08. Módulos fuera de proceso

**Estado:** aceptada.

**Decisión:** los módulos son servicios aparte que hablan HTTPS con JSON según el
[protocolo v0](protocolo-modulos-v0.md): firma HMAC que cubre también el ID del pedido y la clave de
idempotencia, manifiesto con capacidades y esquemas, idempotencia obligatoria con registro durable,
credenciales generadas por el núcleo cuando el sistema externo lo permite, operaciones largas por consulta y
datos personales por permiso. En v0
solo corren módulos propios, escritos con la SDK de C#.

**Por qué:** aísla fallas y licencias, permite cualquier lenguaje y saca del núcleo el código de terceros, que
es el problema típico de los plugins en proceso.

**Consecuencias:**
- Cada módulo se despliega y se actualiza aparte. Las recetas de despliegue los incluyen.
- Un módulo ve los secretos de sus instancias. Por eso el fiscal va en su propia instalación, y cambiar el
  destino de una instancia exige volver a cargar sus secretos.
- Los módulos de terceros, con catálogo, firma y SDK en otros lenguajes, quedan para la fase 3.

---

## D-09. Secretos y cifrado por campo

**Estado:** propuesta.

**Decisión:**
- Los secretos (credenciales de módulos, certificado y clave de ARCA, semillas de 2FA, códigos EPP, estado de
  instancias y credenciales de servicios) se cifran por campo con AES-256-GCM. Cada valor guarda el ID de la
  llave de datos, el nonce y el texto cifrado, y va atado a su tabla, columna y fila como dato asociado. Así un
  cifrado copiado a otra fila no se puede leer. Los secretos que viajan a un módulo (secreto de firma, secretos y
  estado de cada instancia) van atados además a la URL de la instalación: cambiarla exige volver a cargarlos.
- Las llaves viven afuera de la base. En v0 son llaves versionadas en un archivo, una por línea, con una copia
  aparte en la bóveda del proveedor y una prueba periódica de restauración con esa copia. OpenBao (motor
  Transit) llega con la receta de alta disponibilidad.
- Rotar es agregar una versión: lo nuevo se cifra con ella y un trabajo re-cifra lo viejo en segundo plano. Las
  versiones viejas se conservan hasta que vence el último backup cifrado con ellas.
- ASP.NET Data Protection se usa solo para cookies, antiforgery y tokens de vida corta. Su anillo de llaves se
  guarda en la base, protegido por la llave maestra.
- Los secretos nunca vuelven al navegador ni a los logs. Mostrar una credencial de servicio al cliente pide
  confirmación y queda en la auditoría.

**Por qué:** la documentación de Data Protection aclara que no está pensado principalmente para guardar datos
por tiempo indefinido: si una llave se revoca, lo cifrado con ella no se puede leer. Y un backup robado de la
base no tiene que alcanzar para leer secretos.

**Consecuencias:**
- Perder la llave maestra y su copia es perder los secretos. Por eso la copia y la prueba de restauración son
  parte de la instalación, no un extra.
- Los datos personales comunes (nombre, correo, CUIT) no se cifran por campo, porque hacen falta para buscar.
  Los protegen el volumen cifrado y el control de acceso.

---

## D-10. Identidad y acceso

**Estado:** propuesta.

**Decisión:**
- ASP.NET Core Identity, con dos esquemas separados: clientes en el portal y staff en `/admin`, cada uno con
  su cookie.
- Cookies `HttpOnly` y `Secure`. La del portal usa `SameSite=Lax`, que viaja en la vuelta de la pasarela por
  `GET`, y la página de retorno igual no cambia nada. La del staff usa `Strict`.
- Antiforgery en todo formulario. 2FA con TOTP desde v0, obligatorio para el staff. Passkeys después: la API
  ya está en Identity de .NET 10, pero la interfaz lista viene solo en la plantilla de Blazor.
- Bloqueo y límite de intentos por cuenta y por IP.
- Permisos finos para el staff, por política. La propiedad de los recursos del cliente se chequea en un solo
  lugar y lo ajeno da 404.
- "Ver como cliente" para el staff, con aviso visible y registro en la auditoría.
- Login del staff por OIDC opcional (por ejemplo con Authentik o Keycloak).
- Las contraseñas importadas conservan su hash de origen. Si es bcrypt, se verifica al primer login y se
  vuelve a hashear con el algoritmo de Identity.

**Por qué:** son las protecciones de fábrica de ASP.NET Core, sin piezas propias que mantener.

**Consecuencias:** nada de llaves por defecto en el código ni atajos de demo que se puedan activar en
producción.

**Implementación:** la base está en [docs/interfaz](../docs/interfaz/README.md). El registro de clientes y el ingreso con
Google están en [ingreso-clientes.md](ingreso-clientes.md):
OIDC con código y PKCE contra los proveedores que carga el staff, con el secreto cifrado por campo y el vínculo
confirmado por el usuario.

---

## D-11. API propia

**Estado:** propuesta.

**Decisión:**
- REST con JSON en `/api/v1`, organizada por casos de uso (`POST /api/v1/orders`,
  `POST /api/v1/services/{id}/suspend`, `POST /api/v1/invoices/{id}/issue`), no por tablas.
- Sin `PUT` ni `DELETE` sobre documentos emitidos.
- Documento OpenAPI generado en el build con el soporte de ASP.NET Core.
- Errores con Problem Details (RFC 9457), paginación por cursor y `Idempotency-Key` en los `POST` con efecto.
- API keys con prefijo visible y 32 bytes aleatorios, guardadas como SHA-256 y mostradas una sola vez, con
  permisos por recurso, lista de IP, vencimiento y límite de pedidos.
- Webhooks salientes firmados según la especificación abierta Standard Webhooks.

**Por qué:** la API es el contrato con las integraciones y con la API compatible. Tiene que ser estable y
segura de reintentar.

**Consecuencias:** el portal y el admin usan los mismos casos de uso que la API, así que no hay reglas de
negocio que existan en un solo lado.

**Implementación de los webhooks (2026-10-29):** destinos con secreto cifrado y una entrega por evento y destino,
creada en la misma transacción que el cambio, en orden por entidad y con 30 reintentos por la cola, más la
pantalla de Configuración, API y webhooks. Las extensiones son módulos del tipo `extension` que reciben eventos y
validan pedidos. El detalle está en [webhooks y extensiones](webhooks-y-extensiones.md).

---

## D-12. API compatible

**Estado:** aceptada.

**Decisión:** una capa de traducción que recibe pedidos con la forma de la API externa de WHMCS y llama a los
mismos casos de uso que la API propia. v0 cubre los comandos más usados por las integraciones,
detallados en [api-compatible.md](api-compatible.md).

**Por qué:** permite que las integraciones del cliente sigan andando el día de la migración.

**Consecuencias:**
- Viene apagada y se prende por instalación.
- Usa credenciales propias, emitidas por el núcleo, con los mismos controles que las API keys.
- En código, paquetes e interfaz se llama "API compatible", sin la marca.
- Cada comando tiene tests de contrato escritos desde integraciones reales. Nunca se prueba contra una
  instalación de WHMCS.

---

## D-13. Interfaz, plantillas, PDF e idiomas

**Estado:** propuesta.

**Decisión:**
- Interfaz renderizada en el servidor con Razor Pages y htmx, sin SPA.
- Plantillas de correo y de documentos con sintaxis tipo Liquid (Fluid, MIT). Codifican HTML por defecto,
  reciben modelos planos hechos para la plantilla (nunca objetos del dominio) y corren con límites fijos de
  pasos, recursión y tamaño de salida.
- PDF desde HTML con Chromium sin interfaz en un contenedor aparte (Gotenberg, MIT), sin salida a la red: todo
  lo que el documento muestra va embebido. Incluye el QR fiscal cuando corresponde.
- Textos en archivos de recursos por cultura, con control en el build de que no falte ninguna clave. `es-AR`
  y `en` desde el día 1.
- Consumidor: botones de baja y de arrepentimiento visibles desde el primer acceso, sin iniciar sesión, con la
  constancia en pantalla y por correo (Disposición 954/2025). Las facturas llevan la leyenda con la regla de
  rescisión (Ley 24.240, art. 10 ter). Las fuentes están en la sección 14 del [modelo de dominio](dominio.md).

**Por qué:** menos piezas que mantener, mejor seguridad por defecto (antiforgery, sin API para el navegador) y
plantillas que el proveedor puede editar sin riesgo de ejecutar código ni de inyectar HTML.

**Consecuencias:** las plantillas propias se escriben de cero. No se copia ninguna plantilla, tema ni texto de
WHMCS.

**Implementación de los correos (2026-10-27):**
- Las plantillas de fábrica están en `src/BillingOss.App/Templates/`, una por aviso e idioma, con el asunto en
  la primera línea y un diseño común (`layout`). El catálogo está en la sección 9.1 del
  [modelo de dominio](dominio.md).
- Un solo motor para los correos y el PDF (`src/BillingOss.App/Platform/Liquid.cs`), con los mismos topes:
  50.000 pasos, 16 niveles de recursión y 1 MiB por salida y por variable, que alcanza para un comprobante largo
  con el QR embebido. Sin acceso a archivos.
- El envío sale por SMTP con MailKit (MIT), con STARTTLS obligatorio por defecto, pensado para el relay de
  correo propio. Fluid y MailKit tienen licencia MIT, igual que sus dependencias (Parlot, MimeKit, TimeZoneConverter
  y BouncyCastle.Cryptography).
- Con el envío apagado, que es el valor por defecto, cada aviso se arma y queda registrado sin salir. Así corren
  staging y el mes en paralelo, y el registro permite comparar la cantidad de avisos por tipo.
- Traba de producción del correo (`Notifications:ProductionEnabled`), apagada por defecto como la fiscal. Mientras
  lo esté, aun con el envío prendido solo reciben las casillas de `Notifications:OnlyTo`: ningún cliente recibe un
  aviso desde staging, el mes en paralelo o una base de producción restaurada. Se mira al encolar y otra vez al
  mandar, y se prende recién en el corte.
- Los correos de acceso y de los trámites de baja y arrepentimiento de la interfaz (`IAccountMail`) salen por la
  misma cola, con sus plantillas (`password-reset`, `consumer-code` y las dos constancias).

**Implementación del PDF:** [docs/pdf](../docs/pdf/README.md) tiene los datos y leyendas que lleva cada clase de
comprobante, el QR de la RG 4892, el guardado del PDF con CAE en la base y la receta de Gotenberg sin salida a la red.

---

## D-14. Despliegue y alta disponibilidad

**Estado:** propuesta.

**Decisión:**
- Imagen de contenedor para Linux x64 y ejecutable autocontenido para quien no use contenedores.
- Receta chica con Docker Compose: núcleo, PostgreSQL, conversor de PDF y módulos.
- Adjuntos detrás de una interfaz de almacenamiento: disco local en la receta chica, almacenamiento compatible
  con S3 cuando hay más de una instancia.
- Preparado para alta disponibilidad desde el día 1: la aplicación no guarda estado, lo durable está en
  PostgreSQL y los adjuntos en S3. Se puede correr así desde el MVP con Patroni.
- Con alta disponibilidad, la replicación es sincrónica (`synchronous_mode` de Patroni). La asincrónica, que
  es la opción por defecto, puede perder transacciones ya confirmadas en un failover: una emisión fiscal o un
  pago ya informado a la pasarela.
- Después de cada promoción, antes de volver a emitir y cobrar, se concilian las series fiscales y los pagos.
- Las migraciones de base corren con un comando aparte antes de actualizar, y se escriben en dos pasos
  (agregar y después quitar) para actualizar sin cortar.
- Las recetas son oficiales desde el principio, en `deploy/` con sus guías en
  [docs/despliegue/](../docs/despliegue/README.md): la receta chica, la HA en VMs (Patroni con quórum
  sincrónico estricto, etcd y pgBackRest con PITR) y la base externa con contrato. La aplicación apunta a una
  base o a varias con la misma clave: la cadena de varios hosts de Npgsql sigue a la primaria.
- Kubernetes entra por el contrato de base externa (CloudNativePG con replicación sincrónica lo cumple). Una
  receta propia para Kubernetes queda para cuando haya un cliente que la use. ARM64, cuando alguien lo pida.

**Por qué:** da cifrado, replicación y alta disponibilidad sin obligar a una instalación chica a pagar esa
complejidad.

**Consecuencias:** la restauración de backups se prueba sola, con regularidad, en la receta de alta
disponibilidad.

---

## D-15. Observabilidad

**Estado:** propuesta.

**Decisión:**
- OpenTelemetry para logs, métricas y trazas, exportadas por OTLP a donde elija el proveedor.
- Logs estructurados, sin secretos ni datos de tarjetas.
- Endpoints de salud separados: vivo y listo.
- Métricas de negocio: trabajos pendientes y fallidos, duración del ciclo de facturación, emisiones fiscales
  por estado y webhooks sin procesar.
- La auditoría es una tabla del núcleo, no un log.
- Telemetría hacia el proyecto apagada por defecto y solo con aceptación explícita.

**Por qué:** facturación y aprovisionamiento fallan en silencio si nadie mira la cola. Telemetría encendida
sin aviso rompe la confianza de quien instala.

**Consecuencias:** las recetas de despliegue traen paneles y alertas básicos.

**Implementación:** la guía está en [docs/observabilidad](../docs/observabilidad/README.md). Las métricas de
negocio salen de la función `platform.metrics()` para que den lo mismo desde cualquier nodo, y la auditoría es
la tabla `platform.audit_events`, que solo se agrega.

---

## D-16. Pruebas

**Estado:** propuesta.

**Decisión:** desde el día 1, en la integración continua:
- Tests de arquitectura que vigilan los límites entre módulos.
- Integración contra PostgreSQL real con Testcontainers.
- Tests de tabla para impuestos por alícuota, la tolerancia de ARCA, redondeo y cotizaciones.
- Webhooks grabados por pasarela.
- Concurrencia: dos webhooks iguales a la vez, dos intervalos del planificador a la vez, y una nota de crédito
  junto con un consumo de saldo.
- Pruebas de conformidad contra los módulos de referencia.
- Tests de contrato de la API propia y de la API compatible.
- Homologación de ARCA, en una tarea programada con el certificado de prueba guardado como secreto de la
  integración continua.

**Por qué:** en facturación, un error llega directo a la plata de los clientes.

**Consecuencias:** cada caso de uso tiene al menos un test, y un test de arquitectura controla que así sea.

---

## D-17. Importación con IDs preservados

**Estado:** aceptada.

**Decisión:**
- El importador es una herramienta de línea de comandos aparte. Lee un dump de la base del cliente sin levantar
  ningún servidor, y si hace falta usa la API del cliente con sus credenciales. Nunca corre código dentro de
  WHMCS.
- El próximo período a facturar de cada servicio sale de la fecha de próxima factura del origen, no de su
  próximo vencimiento: así no se vuelven a facturar períodos ya facturados que siguen impagos. El día de
  anclaje es el del vencimiento vigente, no el del alta. En los dominios es al revés, porque el origen no
  actualiza su fecha de próxima factura: el próximo período sale del vencimiento, y si ese período ya tiene
  renglón facturado, el siguiente.
- Ata cada servicio vivo a su instancia de módulo con un mapa que arma el staff, con los parámetros y los IDs
  externos que espera cada módulo ([importacion.md](importacion.md)). Un servicio de un producto con módulo que
  queda sin instancia no se opera en silencio: sus operaciones fallan a la vista.
- Los productos sin módulo, o que el mapa marca como manuales, quedan ligados a una instancia del módulo
  manual. Los dominios van a la instancia de su registrar, o a una del registrar manual si no tiene API (.ar,
  subdominios propios o un registrar sin módulo).
- Los comprobantes cuya factura o cuenta ya no está en el origen se importan igual, colgados de documentos o
  cuentas de reemplazo cerrados, porque son registros legales. Las series fiscales arrancan en el último
  número importado.
- Conserva los IDs de origen de cuentas, servicios, pedidos, facturas, tickets y dominios, y también los de
  productos, opciones configurables, departamentos y monedas, que la API compatible recibe por número. Conserva
  los números de factura. Las secuencias del núcleo arrancan arriba del máximo importado.
- Llena las tablas de equivalencias de pasarelas y ciclos con los valores que encuentra.
- Primero simula y entrega un reporte: qué entra, qué se descarta y por qué, y la conciliación de saldos,
  facturas y saldo a favor.
- Se puede repetir hasta el corte: en v0, cada corrida va a una base nueva, en segundos.
- Nunca destructivo por defecto.
- Los campos cifrados en el origen no se descifran: los exporta el cliente o se vuelven a cargar.
- Los documentos importados entran emitidos, inmutables, con la marca `imported` y con su estado de origen.
  El total de cabecera es la verdad, porque el sistema anterior redondeaba distinto: las líneas toman el
  tratamiento de IVA de la cabecera y, si no suman, se agrega una línea de ajuste por tratamiento. La unicidad de un comprobante fiscal por documento vale para los nuevos. Si una factura
  vieja tiene más de uno, se importan todos y salen en el reporte.

**Por qué:** el criterio del MVP es importar un viernes, operar el lunes y poder volver atrás.

**Consecuencias:** el mapeo se escribe leyendo la estructura de la copia de datos del cliente, nunca el
código ni la documentación de WHMCS. Se prueba con copias anonimizadas.

---

## D-18. Licencia y nombre

**Estado:** aceptada.

**Decisión:** el proyecto se publica como **Hornero**, con licencia AGPL-3.0 ([LICENSE](../LICENSE)). El nombre no lleva "WHMCS" ni se le parece. Los nombres internos
(`BillingOss` en los proyectos de .NET y `billing-oss` en imágenes, servicios y bases) se mantienen.

**Licencias:** el núcleo va con AGPL-3.0, solo la versión 3. El SDK y el protocolo de módulos,
`BillingOss.Modules.Hosting` y los módulos incluidos en `src/modules` van con Apache-2.0, cada uno con su
`LICENSE`, para que los módulos de terceros elijan la suya.

---

## D-19. Apariencia y plantillas editables

**Estado:** propuesta (2026-10-28). Amplía D-13. El detalle, el sistema visual, los dos temas, las pantallas y el
plan están en [interfaz-y-temas.md](interfaz-y-temas.md).

**Decisión:**
- La interfaz sigue en Razor Pages con htmx (D-13). El proveedor no edita Razor desde el panel.
- Apariencia en tres capas: marca y fichas de diseño sin código, textos y plantillas Liquid con contrato, y paquetes
  exportables. Todo se guarda como diferencias sobre lo de fábrica en revisiones inmutables, y publicar es mover un
  puntero.
- Lo legal (botones de la Disposición 954/2025, canal de atención y bloque fiscal del PDF) y lo secreto (enlaces
  para restablecer la contraseña y para pagar, códigos de constancia) lo arma el núcleo. Los secretos entran a la
  plantilla como un marcador que se reemplaza después del render, y todo lo editable del correo pasa por un filtro
  de salida con hosts permitidos por el operador.
- Una sola compilación por revisión para el portal, el admin, el correo y el PDF, con el contraste de WCAG 2.2 AA
  calculado.
- Temas: Hornero, neutro y de fábrica, y los de cada proveedor como paquete de su despliegue, nunca dentro del
  producto.

**Por qué:** da marca propia y cada palabra editable sin poder
romper un correo, un PDF fiscal ni una obligación legal, y las actualizaciones no pisan lo personalizado.

**Consecuencias:**
- Esquema `appearance`. `notifications.templates` y `/notifications/templates` se reemplazan antes de salir a
  producción.
- Permisos `appearance.edit` y `appearance.publish`. Publicar pide otra vez el segundo factor y queda en la
  auditoría.
- El verificador de apariencia corre con las migraciones de D-14. Informa y solo traba con `--estricto`.
- `Ui:Brand`, `Ui:AttentionChannel` y el nombre del remitente pasan a los datos de la marca. El canal de atención
  pasa a ser obligatorio.

---

## D-20. Actualizaciones y seguridad

**Estado:** propuesta. Objetivo: actualizar con un clic desde la web y una tarea diaria que vigile
vulnerabilidades de las dependencias y prepare la versión.

**Decisión:** las guías están en [docs/versiones](../docs/versiones/README.md) y en [el actualizador](../docs/despliegue/actualizador.md).
- Renovate abre los PR de versión. Un vigía diario de la integración continua audita NuGet con las transitivas, compara el
  runtime contra la metadata oficial de .NET y vuelve a escanear los SBOM de lo ya publicado. Lo que una
  suba de versión no resuelve queda como un pedido de cambio preparado, que nunca se fusiona, etiqueta ni firma solo.
- Una versión es el conjunto de núcleo, módulos, Gotenberg y, en la receta chica, PostgreSQL, por digest, con
  SBOM y proveniencia. Un mantenedor crea la etiqueta y la versión entra al canal JSON, en un repo aparte, por un
  PR que aprueba otro. Recién ahí se firma, con cosign y una llave en el vault, y el canal se vuelve a firmar cada
  día con vencimiento a 7.
- Un actualizador aparte, uno por nodo, se actualiza primero a sí mismo y aplica solo versiones del canal
  verificado: chequeos previos, marca de recuperación, migraciones que la versión anterior soporta, nodo por nodo y
  vuelta atrás del binario. La web solo deja el pedido y nunca toca el host.
- SemVer, una minor por trimestre y parches cuando hagan falta. Como máximo tres líneas soportadas, ninguna más
  allá del .NET que la sostiene.

**Por qué:** el runtime de .NET viaja dentro de nuestra imagen y de nuestro ejecutable, así que cada parche de .NET
es una versión de Hornero, y en 2026 fueron de seguridad 11 de las 13 versiones de servicio de .NET 10. Sin un
circuito automático y un actualizador confiable, los proveedores quedan atrás.

**Consecuencias:**
- Antes del mes en paralelo: auditoría que no traba el build, CI, Renovate, vigía, publicación con canal firmado y
  actualizador por consola. El panel, las ramas de versión y `SECURITY.md`.
- Un parche nunca quita ni endurece nada del esquema: eso va en la minor siguiente.
- La actualización automática es optativa, solo de parches, espera 24 horas desde que ve la versión y nunca corre
  durante el ciclo de facturación ni las emisiones fiscales.
- El panel nunca dice "al día" con datos viejos, y un parche crítico avisa también por correo.

**Implementación (2026-10-29):** pasos A1 a A8 y el panel con su API, sin publicar ni firmar nada de verdad. Las
guías están en [docs/versiones](../docs/versiones/README.md) y [el actualizador](../docs/despliegue/actualizador.md).

---

## D-21. Fiscal, impuestos y cobros incluidos

**Estado:** propuesta (2026-10-28). Objetivo: ARCA, Mercado Pago, PayPal, Stripe y
las pasarelas principales incluidas por defecto, un alta de ARCA fácil, con el certificado y una guía paso a paso, y
una sección de impuestos completa. Amplía D-04, D-05 y D-06. El detalle está en
[fiscal-impuestos-y-cobros.md](fiscal-impuestos-y-cobros.md).

**Decisión:**
- ARCA se conecta con un asistente de siete pasos. El núcleo genera la clave RSA y el pedido de certificado con el
  sujeto que pide ARCA, guarda la clave cifrada y no la devuelve nunca. Una guía nombra cada pantalla de ARCA, el
  certificado se valida localmente con un mensaje por caso, y una prueba de solo lectura comprueba servicios,
  delegación de la CUIT, puntos de venta y series antes de emitir. El staff recibe avisos a 30, 15, 7 y 1 día del
  vencimiento del certificado, que se renueva sin cortar la emisión. El padrón completa fichas y nunca obliga.
- Homologación y producción no comparten emisor ni instancia.
- Impuestos: categoría en el producto, reglas con vigencia por impuesto, receptor, categoría y cuenta, impuestos de
  comprobante para las percepciones, fecha fiscal única, modo de carga en cada precio congelado en el servicio,
  muestra por canal con lo legal fijo y clase C. El cálculo sigue por comprobante y alícuota, y sin regla falla.
- Cobros de fábrica: Mercado Pago, PayPal, Stripe y cripto dentro de la receta, dados de alta solos, y la
  transferencia en el núcleo con conciliación por el CUIT del ordenante. Un asistente prueba cada conexión.
- Débito automático: la suscripción de Mercado Pago en Argentina, y una capacidad aditiva del protocolo,
  `payment.saved_methods`, con Stripe como primera implementación, consentimiento propio y reintentos acotados.

**Por qué:** las piezas técnicas ya están (WSAA, WSFEv1, WSFEX, reglas con vigencia, cálculo por alícuota y módulos de
cobro). Falta que un proveedor las configure solo y sin poder equivocarse en lo legal. Y
hay huecos que cuestan plata: cambiar el modo de precio global reinterpreta todos los precios congelados, y una B a
un no categorizado sale hoy sin la percepción de la RG 2126.

**Consecuencias:**
- Para el mes en paralelo: guía y pedido del certificado de homologación, reporte fiscal del importador para el
  contador, modo de precio congelado, fecha fiscal única, credencial fiscal, prueba sin emitir y módulos de cobro en
  la receta.
- Para el corte: condición del receptor validada, categorías, tributos y percepción (o freno a la condición 7),
  asistente de ARCA con alias propio, avisos del certificado, catálogo con el precio sin impuestos nacionales,
  suscripción de Mercado Pago y conciliación de transferencias.
- Stripe no abre cuentas en Argentina: viene de fábrica para proveedores de otros países.
- Decididas el 2026-10-28: la traba de ARCA en tres posiciones (`off`, `read` y `on`), que contra producción, aun
  en `read`, se usa recién con el certificado de producción y una decisión explícita del operador. Un alias propio
  en producción va en la guía de producción. Stripe queda en el producto y dLocal Go va después. Las consultas
  impositivas abiertas siguen el criterio del documento, con freno donde una respuesta distinta cambie el
  comprobante.

**Implementación de los cobros:** la guía está en [docs/cobros](../docs/cobros/README.md). Los
cuatro módulos de cobro (Mercado Pago, Stripe, PayPal y CryptoCloud) están en la receta con el de ARCA, y `migrate` los da
de alta solos. El asistente arma las credenciales con el `config_schema`, prueba la conexión y no deja ofrecer una
instancia con credenciales de otro ambiente, y el núcleo no toma como plata un pago del ambiente de prueba en una
instancia de producción. El débito con medio guardado revisa la factura y el medio antes de cada pedido, también en
los reintentos, no repite con la misma clave pasados dos días y no abre otro intento sobre una factura mientras uno
anterior pudo haber cobrado, aunque el cliente haya cambiado la tarjeta. La suscripción de Mercado Pago se ofrece
detrás de `Payments:SubscriptionsEnabled`, apagado, y los cobros de una vigente se registran igual.
