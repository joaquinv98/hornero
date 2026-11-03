# Soporte

**Estado:** v0, 2026-10-29 · **Diseño:** sección 9 del [modelo de dominio](../diseno/dominio.md) ·
**Interfaz:** sección 10 de la decisión de interfaz (navegación clásica)

Tickets con departamentos, respuestas, notas internas, adjuntos, estados, prioridades, asignación, respuestas
predefinidas, unión de tickets, escalamiento, cierre automático, encuesta de satisfacción y correo entrante por IMAP.
El código está en `src/BillingOss.App/Support/` y las pantallas en `src/BillingOss.Web/Pages/Admin/Tickets/`,
`Pages/Portal/Tickets/` y `Pages/Support/`. Las tablas van en el esquema `support`, en la migración `Soporte`.

## Conceptos

| Concepto | Tabla | Qué es |
|---|---|---|
| Departamento | `support.departments` | Nombre, casilla, visibilidad en el portal, si atiende solo a clientes, confirmación automática, casilla del equipo para avisos, encuesta y casilla IMAP |
| Ticket | `support.tickets` | Cuenta (o nadie, si escribió alguien sin cuenta), solicitante, copias, departamento, servicio, número público, estado, prioridad, asignado, ficha del hilo, unión y ficha de la encuesta |
| Mensaje | `support.messages` | Del cliente, del staff o del sistema, con el origen (portal, correo, panel, API o importación). Una nota interna es un mensaje que el cliente nunca ve |
| Adjunto | `support.attachments` y `support.attachment_contents` | Nombre, tipo y tamaño en una tabla y el contenido en otra, que nunca se carga con la lista |
| Respuesta predefinida | `support.saved_replies` | Texto con nombre y categoría que el staff inserta al responder |
| Filtro del correo | `support.mail_filters` | Remitente (casilla, dominio o comodín), asunto o frase. Lo que coincide se rechaza sin contestar |
| Correo entrante | `support.inbound_emails` | Cada correo leído, con lo que se hizo y por qué. El Message-ID es único |
| Regla de escalamiento | `support.escalation_rules` | Condiciones y acciones, ver más abajo |
| Escalación | `support.escalations` | Cada vez que una regla se aplicó a un ticket, única por regla, ticket y espera |
| Encuesta | `support.feedback` | La calificación del 0 al 10 y el comentario, una por ticket |

El número público tiene seis dígitos al azar, distintos del ID interno, y pasa a ocho si los de seis se llenan. Un
lock de la transacción hace que dos nodos no elijan el mismo a la vez.

## Estados

| Estado | Espera a | Qué pasa |
|---|---|---|
| Abierto | Staff | Nuevo, o con un mensaje del cliente. Un mensaje del cliente reabre también uno cerrado |
| En curso | Staff | El staff lo está trabajando |
| Esperando al cliente | Cliente | El staff respondió. Es el estado por defecto después de una respuesta y el único que se cierra solo |
| En pausa | Nadie | Depende de un tercero |
| Cerrado | Nadie | Lo cerró el cliente, el staff, el cierre automático o una unión |

Las prioridades son baja, normal, alta y urgente. La cola del staff muestra primero lo urgente y después lo que más
espera.

Los estados son fijos y no se definen por proveedor. Si el sistema anterior no tiene estados propios, solo sus seis de
fábrica, y el importador los pasa a estos cinco. Dos de aquellos, el ticket nuevo y el que tiene
respuesta del cliente, acá son dos vistas del mismo estado abierto: las separa si el ticket ya tiene un mensaje del
staff que el cliente vio. Así el cierre automático, el escalamiento y la cola no dependen de una lista que se edita.

## Casos de uso

`TicketService` tiene los mismos casos de uso para el portal, el panel, la API y el correo entrante:

- **Abrir.** Con una referencia del cliente (la `Idempotency-Key` de la API) abrir dos veces devuelve el mismo ticket,
  también si los dos pedidos llegan a la vez a nodos distintos. El portal no ofrece departamentos ocultos y el
  servicio tiene que ser de la cuenta. Si lo abre el staff, queda esperando al cliente y asignado a quien lo abrió. El
  staff elige la cuenta por su número o por su correo (el de la cuenta o el de un usuario con soporte en una sola
  cuenta), suma copias y puede abrirlo sin avisar: el cliente lo ve en el portal y le llega la próxima respuesta. El
  correo de quien consulta y las copias quedan como casillas sueltas, sin el nombre: a ellas van los avisos y con ellas
  se reconocen sus respuestas. Un correo que no es válido no abre el ticket, y una copia que no lo es se descarta.
- **Responder o anotar.** La primera respuesta del staff a un ticket sin asignar se lo asigna. Una nota interna no
  avisa a nadie ni mueve la fecha del último mensaje. Un cliente no puede escribir notas internas.
- **Cambiar** estado, prioridad, departamento y asignado. Queda en la auditoría con el antes y el después.
- **Cerrar.** Lo puede hacer el cliente desde el portal. Si después escribe, se reabre.
- **Unir.** Los mensajes de un ticket, con sus adjuntos, pasan a otro. El que se une queda cerrado y apuntando al que lo
  recibe, quien consultaba en él pasa a las copias del otro y queda una marca visible. Solo se une a un ticket de la
  misma cuenta, o desde uno sin cuenta (el correo de alguien que el staff reconoce) a cualquiera. Los que ya se habían
  unido al primero pasan a apuntar al segundo, así nunca hay que seguir más de un paso. El enlace, el número o la ficha
  del hilo del unido llevan al que lo recibió.
- **Calificar.** La encuesta se responde una sola vez por ticket, del 0 al 10, y guarda quién atendía.

## Ajustes de soporte

Configuración, Ajustes de soporte (`/admin/soporte/ajustes`, con `support.settings`) edita en el panel lo que sigue:
las extensiones permitidas, el tope de cada adjunto y cuántos van por mensaje, el cierre automático en horas, el tope de
cada correo entrante, los correos por remitente y por hora y si las otras casillas de un correo pasan a las copias. Se
guardan en `support.settings`, una sola fila con su versión: valen en seguida en todos los nodos, sin reiniciar, y dos
personas que editan a la vez no se pisan, porque guarda la que tenía la versión vigente. Cada cambio queda en la
auditoría con los nombres de lo que cambió. Mientras no se guarden, rigen las claves `Support:*` de la configuración,
que siguen siendo los valores de fábrica. Los adjuntos de un mensaje no pueden pasar juntos de 60 MB, así entran en el
tope de 64 MiB de cada pedido.

## Adjuntos

| Clave | Por defecto | Para qué |
|---|---|---|
| `Support:MaxAttachmentBytes` | 10 MiB | Tope de cada adjunto, del portal, del panel o de un correo |
| `Support:MaxAttachments` | 5 | Adjuntos por mensaje |
| `Support:AllowedExtensions` | `.pdf,.png,.jpg,.jpeg,.gif,.webp,.txt,.log,.csv` | Extensiones permitidas, separadas por comas. Reemplaza la lista entera |

Son los valores de fábrica: los ajustes de soporte del panel los reemplazan. El portal y el panel rechazan el formulario con el motivo. Lo que no pasa en un correo se omite y queda nombrado en
el mensaje. El nombre se limpia de carpetas y caracteres de control. Los adjuntos se bajan siempre como archivo y
solo desde un mensaje que quien mira puede ver. El contenido va en la base: así lo ven todos los nodos sin un volumen
compartido. Los formularios con adjuntos aceptan hasta 64 MiB por pedido (`SupportUi.MaxRequestBytes`). Si se suben
los topes, ese va con ellos.

## Avisos

Salen por la cola de notificaciones, con las plantillas `ticket-*` en `es-AR` y `en` y el idioma de la cuenta. Cada
aviso de un ticket lleva el hilo (In-Reply-To y References con la ficha secreta del ticket) y la casilla del
departamento en Reply-To: lo que el cliente responda vuelve al ticket por IMAP.

| Aviso | Plantilla | A quién | Cuándo |
|---|---|---|---|
| Confirmación | `ticket-opened` | Solicitante | El cliente abrió un ticket y el departamento confirma. Lleva `Auto-Submitted` para que un contestador no conteste |
| Respuesta | `ticket-reply` | Solicitante y copias | El staff respondió o abrió el ticket por el cliente |
| Mensaje del cliente | `ticket-staff-alert` | Asignado, o la casilla del equipo | El cliente escribió |
| Escalamiento | `ticket-escalated` | Asignado, o la casilla del equipo | Una regla con aviso escaló el ticket |
| Encuesta | `ticket-feedback` | Solicitante | El ticket se cerró y el departamento pide encuesta |

Las notas internas, los mensajes del sistema y los importados nunca avisan. Cada aviso tiene su clave de
deduplicación (el mensaje, la escalación o el ticket), así que un reintento no lo duplica.

## Correo entrante

Cada departamento puede leer una casilla por IMAP (SSL, STARTTLS o, solo para pruebas locales, sin cifrar). La
contraseña se cifra por campo atada a la fila y al servidor (D-09) y nunca vuelve al navegador. Cambiar el servidor, el
puerto o el cifrado obliga a cargarla de nuevo: la guardada no viaja a otro lado ni sin el cifrado con que se cargó. El
botón de probar se conecta con lo que está en el formulario sin guardar nada, y usa la guardada solo si el servidor, el
puerto y el cifrado son los mismos.

- La casilla se abre en solo lectura y nunca se borra ni se marca nada. Un cursor por UID dice hasta dónde se leyó.
  Cada correo y el cursor van en la misma transacción, así una caída no pierde ni duplica, y si el servidor cambia el
  UIDVALIDITY la relectura no duplica por el Message-ID.
- Se lee cada dos minutos, de a un trabajo por casilla (su clave de entidad) y hasta 50 correos por turno.
- Una respuesta encuentra su ticket por la ficha del hilo, por el Message-ID que cita o por el número del asunto. Por
  la ficha alcanza. Por lo demás, quien escribe tiene que participar: solicitante, copia, correo de la cuenta o
  usuario del portal con soporte en la cuenta.
- El número del asunto va entre corchetes, con o sin rótulo delante (`[#123456]` en los avisos propios). Así también
  encuentran su ticket importado las respuestas a los correos del sistema anterior, que nombran el número con su
  propio rótulo.
- Lo que no es respuesta abre un ticket, también de quien no es cliente, salvo que el departamento atienda solo a
  clientes. Las demás casillas del correo (Para y CC) pasan a las copias al abrir el ticket y en las respuestas que
  traen la ficha del hilo. Una respuesta encontrada por el número del asunto o por un Message-ID no suma copias: para
  eso alcanza con conocer el número, y un remitente falso podría sumar su casilla a las respuestas del staff.
- Se descartan sin contestar las respuestas automáticas (RFC 3834 y marcas de uso común), los rebotes y los correos
  de casillas propias. Se rechazan sin contestar los que coinciden con un filtro, los que pasan el tope por remitente
  y los demasiado grandes. Todo queda en el registro del correo entrante, con el motivo.
- Los filtros son el control de spam: remitente (casilla, dominio o `*@dominio`), asunto o frase del cuerpo. Bloquear
  al remitente desde el registro, o desde la cola con la acción masiva, suma un filtro de remitente con su casilla, una
  sola vez. Cada alta y cada baja quedan en la auditoría, sin el valor, que puede ser la casilla de una persona. A un
  remitente bloqueado no se le pide la encuesta al cerrar su ticket.
- Del HTML se toma el texto. Se corta en la marca "Reply above this line" de los avisos y se saca lo citado al final.

| Clave | Por defecto | Para qué |
|---|---|---|
| `Support:MaxEmailBytes` | 25 MiB | Los correos más grandes no se bajan: quedan rechazados en el registro |
| `Support:MaxEmailsPerSenderPerHour` | 15 | Corta los bucles con contestadores automáticos |
| `Support:EmailCc` | `true` | Las demás casillas de un correo pasan a las copias del ticket. Apagado, las copias las carga solo el staff |

El correo saliente es el de `Notifications:*` (README): con `Notifications:Send` apagado los avisos se arman y quedan
registrados sin salir, y la traba `Notifications:ProductionEnabled` vale también para el soporte.

## Cierre automático

Las horas del cierre automático de los ajustes de soporte (de fábrica, `Support:AutoCloseAfter`, un `TimeSpan` como
`5.00:00:00`) cierran cada hora, de a 500 por vez, los tickets que esperan al cliente desde hace más de ese tiempo, con
una marca del sistema que el cliente ve. Vacío, que es el valor por defecto, nunca cierra solo. Antes del corte hay que
cargar el valor que usaba el sistema anterior: con miles de importados esperando al cliente, uno mal cargado los cierra en un día. Si el cliente responde, el ticket se reabre. Cada cierre pide la encuesta como cualquier
otro, si el departamento la quiere.

## Escalamiento

Una regla mira los tickets de sus departamentos, estados y prioridades (vacío es todos, y en estados son los que
esperan al staff) que pasan cierta cantidad de minutos sin un mensaje nuevo que vea el cliente. Al aplicarse puede
cambiar el departamento, la prioridad, el estado y el asignado, avisar al asignado o a la casilla del equipo y además
a las personas del staff que la regla nombre (un aviso por casilla), dejar una nota interna y responderle al cliente.
Las notas internas no cuentan como mensaje nuevo.

La respuesta al cliente sale a nombre del departamento, con el aviso de una respuesta al solicitante y a las copias.
Es un mensaje del sistema que el cliente ve: no mueve la espera, así la regla no se vuelve a aplicar por su propia
respuesta, no cambia el estado (salvo que la regla lo pida) y no cuenta como primera respuesta del staff en el reporte.

Corre cada cinco minutos en cualquier nodo, solo si hay reglas prendidas. Cada ticket va en su transacción con su
lock, y la escalación tiene una clave única por regla, ticket y espera (el último mensaje): dos nodos o dos corridas
no la aplican dos veces, y un mensaje nuevo la habilita otra vez. Cada aplicación queda en la auditoría. Una regla no
se borra: se apaga, así su historial queda.

## Encuesta de satisfacción

Si el departamento la pide, al cerrarse un ticket sale un correo al solicitante con un enlace a
`/soporte/encuesta/{ficha}`, que sirve sin iniciar sesión (también para quien escribió sin ser cliente). Sale una sola
vez por ticket: no se repite si se reabre y se vuelve a cerrar, y no sale si se reabrió o se unió a otro antes del
aviso, ni si no hay `Billing:PublicUrl`. La ficha se crea una sola vez aunque dos nodos lleguen a la vez. El portal
ofrece el mismo enlace en el ticket cerrado mientras no esté respondida.

## Reportes de soporte

Están en el grupo Soporte de Reportes ([docs/reportes](reportes/README.md)), por mes y con su CSV, cada uno con su
permiso o con `reports.read`. Soporte (`/admin/reportes/soporte`, `reports.support`) da por departamento y por mes los
tickets abiertos, con respuesta, sin respuesta y cerrados, y la mediana y el promedio de la primera respuesta: desde
que el cliente abrió el ticket hasta el primer mensaje del staff que el cliente ve, sin contar notas internas ni los
que abrió el staff. Respuestas del staff (`/admin/reportes/respuestas-staff`, `reports.staff-replies`) cuenta los
mensajes de cada persona, y Satisfacción (`/admin/reportes/satisfaccion`, `reports.satisfaction`) da la encuesta por
persona con su promedio y los comentarios. Los unidos a otro no cuentan, porque sus mensajes están en el que los
recibió.

## Pantallas

| Ruta | Quién | Menú |
|---|---|---|
| `/soporte`, `/soporte/{id}` | Cliente con soporte en la cuenta | Soporte, Mis tickets |
| `/soporte/nuevo` | Cliente con soporte en la cuenta | Soporte, Abrir ticket |
| `/soporte/encuesta/{ficha}` | Cualquiera con el enlace | Desde el correo o el ticket cerrado |
| `/admin/soporte/resumen` | Staff con `support.tickets` | Soporte, Resumen |
| `/admin/soporte`, `/admin/soporte/{id}` | Staff con `support.tickets` | Soporte, Tickets |
| `/admin/soporte/nuevo` | Staff con `support.tickets` | Soporte, Abrir ticket. También desde la ficha del cliente |
| `/admin/cuentas/{id}/tickets` | Staff con `support.tickets` | La solapa Tickets de la ficha del cliente |
| `/admin/soporte/respuestas` | Staff con `support.settings` | Soporte, Respuestas predefinidas |
| `/admin/reportes/soporte`, `/admin/reportes/respuestas-staff`, `/admin/reportes/satisfaccion` | Staff con el permiso de cada reporte o `reports.read` | Reportes, Soporte |
| `/admin/soporte/correo` | Staff con `support.settings` | Configuración, Control de spam: los filtros de remitente, asunto y frase |
| `/admin/registros/correo-entrante` | Staff con `support.settings` | Registros del sistema, Correo entrante (hoy también en Utilidades): el registro con búsqueda por remitente o asunto, filtros por resultado y departamento, páginas de a 50 y el botón de bloquear al remitente |
| `/admin/soporte/ajustes` | Staff con `support.settings` | Configuración, Ajustes de soporte |
| `/admin/soporte/departamentos` | Staff con `support.settings` | Configuración, Departamentos de soporte |
| `/admin/soporte/escalamiento` | Staff con `support.settings` | Configuración, Reglas de escalamiento |

Lo ajeno da 404 en el portal, igual que lo que no existe, y mirando como cliente se lee nada más. La capacidad
`support` del portal la tienen el titular y los roles de facturación y técnico.

La cola del staff es una lista densa, como pide la sección 10.2 de la decisión de interfaz:

- **Filtros arriba**, como parámetros del enlace: estado (abiertos, que es todo lo que no está cerrado, cada estado
  por separado, los nuevos y los que tienen respuesta del cliente, cerrados o todos), departamento, prioridad, asignado
  (a mí, a nadie o a una persona del staff) y una búsqueda por número, asunto, cliente, solicitante o correo. Un número
  busca en todos los estados, también los unidos a otro, que llevan al que los recibió. Un ticket para responder dice
  en su fila si es nuevo o si tiene respuesta del cliente.
- **Orden por columna**, primero ascendente y con un segundo clic al revés. Sin elegir, lo de movimiento más reciente
  arriba.
- **Paginado** de a 50, con cuántos hay en total.
- **Acciones masivas** sobre lo marcado: asignarme, cerrar, unir al más antiguo de lo elegido y bloquear al remitente
  y cerrar. Bloquear suma un filtro del control de spam por cada remitente y deja afuera los tickets de una cuenta, así
  nunca se bloquea a un cliente por error. Como toca la configuración, pide también `support.settings`. Primero muestran a cuáles les cambia algo y piden confirmar, y después
  cambian uno por uno, cada uno en su transacción y en la auditoría. Repetirlas no cambia nada más.
- **Exportación CSV** de todo lo filtrado, sin el paginado y hasta 10.000 filas, con BOM para que la planilla lea los
  acentos y un apóstrofo delante de lo que la planilla tomaría como fórmula.

La solapa Tickets de la ficha del cliente es la misma lista con la cuenta fija, y la ficha muestra los últimos diez.

El menú Soporte trae, en este orden y con las rutas de la sección 10.2 de la decisión de interfaz, el resumen, Tickets
con su submenú (asignados a mí, todos los activos, nuevos, respondidos, con respuesta del cliente, en pausa, en curso y
cerrados, cada uno la cola con su filtro en el enlace), abrir ticket y respuestas predefinidas. Los ajustes de soporte
van a la solapa Soporte de Configuración, General, y el cierre de inactivos a Automatización, cuando existan: hoy
están en `/admin/soporte/ajustes`.

## Resumen

`/admin/soporte/resumen` es la entrada al soporte. Una tarjeta por vista de la cola (nuevos, con respuesta del cliente,
en curso, esperando al cliente, en pausa, mis tickets y los que esperan respuesta sin asignar), cada una con su número
y el enlace a la cola ya filtrada. Abajo, los cinco tickets que más esperan al staff, desde su último mensaje, los
activos por departamento y por persona, la última semana (abiertos, cerrados y la mediana de la primera respuesta, con
la misma definición que el reporte de soporte) y las últimas encuestas. Quien puede ver ese reporte tiene además el
enlace.

## API

La API propia (`/api/v1`, README) trae lo que usan los flujos de IVR y SMS: buscar tickets y abrirlos sin duplicar.

| Pedido | Permiso | Qué hace |
|---|---|---|
| `GET /api/v1/tickets` | `tickets:read` | Hasta 100, los de movimiento más reciente primero. Filtros: `account`, `email` (el de quien consulta), `number` y `status` (`open`, que es todo lo que no está cerrado y el valor por defecto, `closed` o `all`). Los unidos a otro aparecen solo buscados por su número, con el que los recibió en `merged_into_id`, y el número busca en todos los estados |
| `GET /api/v1/tickets/{id}` | `tickets:read` | El ticket con sus mensajes, también las notas internas, marcadas con `internal` |
| `POST /api/v1/tickets` | `tickets:write` | Abre a nombre de una cuenta (`account_id`) o de quien escribió (`requester_email`, y `requester_name` si se sabe), con `department_id`, `subject`, `message` y opcionales `priority`, `service_id` y `cc`. Avisa como un ticket que abre el cliente |

Con la misma `Idempotency-Key`, de hasta 150 caracteres, abrir dos veces devuelve el mismo ticket, también desde dos
nodos a la vez. La clave vale por cuenta, o por correo si no hay cuenta, así dos integraciones con la misma no se pisan.
Los errores van en Problem Details: 422 si falta el correo o el departamento no existe, 404 si no existe la cuenta y
400 si la clave es demasiado larga o el estado no se conoce.

## Varios nodos

Nada importa en memoria entre pedidos. Los avisos, la lectura de casillas, el escalamiento y el cierre automático son
trabajos de la cola con su clave: un trabajo por casilla y por intervalo, uno por ticket para los avisos (en orden) y
uno por intervalo para el escalamiento. Las claves únicas de la base (número, ficha del hilo, referencia del cliente,
Message-ID, escalación y encuesta) son la última palabra cuando dos nodos hacen lo mismo a la vez.

## Importación

El importador trae los departamentos y los tickets con sus IDs y sus números, los mensajes y las notas internas, las
respuestas predefinidas, los filtros, las reglas de escalamiento y las encuestas. Las personas del staff que ya tienen
usuario con el mismo correo conservan sus asignaciones, su firma, sus encuestas y sus reglas: conviene cargar el staff
antes de importar. Los archivos de los adjuntos entran si se le pasa la carpeta de adjuntos del sistema anterior con
`--support-files`. El detalle está en la sección 2 de
[importacion.md](../diseno/importacion.md).

## Pruebas

`SupportTests`, `SupportFlowTests` y `SupportMailTests` cubren los casos de uso, los avisos, la unión, la encuesta
(también que no sale a un bloqueado), el escalamiento con varios nodos, con respuesta al cliente y aviso a varias
personas, y el correo entrante contra un servidor IMAP falso en proceso, con el número del asunto de otro rótulo y las
copias solo con la ficha del hilo. `SupportUiTests` recorre el portal, el panel (el resumen, la cola con sus vistas,
filtros, orden, paginado, acciones masivas con su permiso y CSV, la solapa de la cuenta, abrir por número o correo,
con copias y sin aviso, y cambiar un ticket, las respuestas predefinidas, los ajustes de soporte con su versión, el
control de spam, el registro del correo entrante, la casilla con el botón de probar y los reportes de soporte sobre los
tickets de verdad) y la encuesta sobre el
host web. `SupportApiTests` prueba la API de tickets. `SupportImportTests` importa un origen sintético, con el staff por
su correo, un nombre repetido y la carpeta de adjuntos. La prueba contra una casilla real (`Casilla_Real_LeeSinErrores`) queda escrita y se omite sin
`BILLINGOSS_TEST_IMAP_HOST`, `BILLINGOSS_TEST_IMAP_USER` y `BILLINGOSS_TEST_IMAP_PASSWORD` (con `_PORT` y `_SECURITY`
opcionales).
