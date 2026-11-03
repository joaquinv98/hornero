# Contenidos para clientes

**Estado:** construido, 2026-11-01 · **Diseño:** [contenidos.md](../../diseno/contenidos.md)

La base de conocimiento, los anuncios, las descargas y el estado de la red: lo que un proveedor publica para sus
clientes además de las facturas. Este documento dice cómo se usa, cómo se configura y cómo se opera. Las reglas y el
modelo están en el [diseño](../../diseno/contenidos.md).

El código está en `src/BillingOss.Domain/Content` (reglas, sin base de datos), `src/BillingOss.App/Content` (servicios y
trabajos) y `src/BillingOss.Web` (`Content/`, `Api/ContentApi.cs` y las páginas de `Help`, `News`, `Downloads`, `NetStatus`
y `Admin/Contenidos`). Las tablas viven en el esquema `content`, en la migración `Contenidos`.

## Lo que ve el cliente

Las páginas no piden sesión. Si hay una del portal, el cliente ve además lo que le corresponde por sus productos y
grupos. Lo que no corresponde da 404, igual que si no existiera.

| Ruta | Qué muestra |
|---|---|
| `/conocimiento`, `/conocimiento?q=` | Buscador, categorías con su cantidad de artículos, destacados y últimos. Los resultados van por relevancia |
| `/conocimiento/c/{slug}` | Una categoría con sus subcategorías, artículos y migas de pan |
| `/conocimiento/{slug}` | El artículo, con el voto de utilidad y otros de la categoría. `/conocimiento/{id}` redirige al `slug` |
| `/anuncios`, `/anuncios/{id}` | Los anuncios vigentes, los fijados arriba |
| `/anuncios.rss` | Canal RSS 2.0 con los anuncios públicos vigentes. `?lang=en` lo da en inglés |
| `/descargas` | Las descargas visibles por categoría, con tamaño, fecha, SHA-256 y cantidad de descargas |
| `/descargas/{id}/archivo` | El archivo, siempre como adjunto |
| `/estado-red`, `/estado-red/{id}`, `/estado-red/historial` | Estado global, componentes, eventos vigentes y próximos, el detalle de un evento y el historial por mes |
| `/estado-red/suscribirse` | Suscripción por correo con doble confirmación. Los enlaces de confirmar y de baja viajan en los correos |

El portal suma una tarjeta de **incidentes y mantenimientos conocidos** en `/servicios` y en el inicio. Muestra los
eventos vigentes y los mantenimientos de la próxima semana que tocan componentes con alguno de los productos que el cliente
tiene contratados (ver "Estado de la red" más abajo).

El menú público tiene los cuatro enlaces (Base de conocimiento, Anuncios, Descargas y Estado de la red), con o sin sesión.

### Direcciones del sistema anterior

Los enlaces que el proveedor ya difundió en su sitio y en sus correos siguen andando: responden un 301 a la dirección nueva y no
consultan la base. Los artículos y las categorías importados conservan su número, y las páginas nuevas aceptan ese número y
redirigen a la dirección corta.

| Dirección anterior | Va a |
|---|---|
| `/index.php?rp=/knowledgebase` | `/conocimiento` |
| `/index.php?rp=/knowledgebase/{n}/` | `/conocimiento/{n}`, que termina en `/conocimiento/{slug}` |
| `/index.php?rp=/knowledgebase/{n}/{nombre}` | `/conocimiento/c/{n}`, que termina en `/conocimiento/c/{slug}` |
| `/index.php?rp=/knowledgebase/tag/{etiqueta}` | `/conocimiento?q={etiqueta}` |
| `/index.php?rp=/knowledgebase/search` | `/conocimiento`, porque la búsqueda viajaba por POST y el término no llega |
| `/knowledgebase.php?action=displayarticle&catid={c}&id={n}` | `/conocimiento/{n}` |
| `/index.php?rp=/announcements` y `/announcements/{n}/{nombre}` | `/anuncios` y `/anuncios/{n}` |
| `/index.php?rp=/download` y lo que cuelga de ahí | `/descargas` |
| `/serverstatus.php` y `/networkissues.php` | `/estado-red` |
| `/submitticket.php` | `/soporte/nuevo`, el formulario de tickets del portal. El departamento y el asunto todavía no se arrastran |

Un número solo es un artículo y un número con un nombre atrás es una categoría, que es como las armaba el sistema anterior. Las
demás rutas que lleva `index.php` se suman en `LegacyUrls.Target`: las que no están dan 404.

## Panel del staff

Las pantallas son las del menú de Soporte: Base de conocimiento, Anuncios, Descargas y Estado de la red. `/admin/contenidos` es
la portada con lo pendiente y las junta en solapas. Los formularios llevan los dos idiomas (`es-AR`
es el de base), la revisión con que se abrieron y, en los textos largos, una vista previa del Markdown que se actualiza
al escribir.

| Ruta | Permiso | Qué hace |
|---|---|---|
| `/admin/contenidos` | `content.read` | Portada con lo pendiente: borradores, anuncios programados, descargas sin archivo, incidentes abiertos y suscriptores |
| `/admin/conocimiento` | `content.read` | Artículos con filtros por texto, categoría y estado, y las categorías |
| `/admin/conocimiento/articulos/{id}` | `content.write` | Editor de un artículo. Sin ID crea. Publicar, retirar, archivar y borrar |
| `/admin/{conocimiento o descargas}/categorias/{id}` | `content.write` | Editor de una categoría, con su madre, su orden y si se oculta |
| `/admin/anuncios`, `/admin/anuncios/{id o nueva}` | `content.read` y `content.write` | Lista y editor con fijado, vencimiento y publicación programada |
| `/admin/descargas`, `/admin/descargas/{id o nueva}` | `content.read` y `content.write` | Lista y editor con la subida del archivo, el SHA-256 y la bajada sin sumar al contador |
| `/admin/estado-red` | `status.write` | Estado de ahora, los eventos en tres vistas (`?vista=abiertas`, `programadas` y `resueltas`, esta con `&mes=2026-10`), componentes y suscriptores |
| `/admin/estado-red/nueva` | `status.write` | Nueva incidencia: abrir un incidente que pasa ahora o programar un mantenimiento con fecha |
| `/admin/estado-red/eventos/{id}` | `status.write` | Historial de un evento, novedad con cambio de estado y de impacto, corrección del título y, en un mantenimiento, reprogramar, terminar y cancelar |
| `/admin/estado-red/componentes/{id}` | `status.write` | Editor de un servidor, un servicio o un grupo |

Borrar pide marcar una casilla de confirmación. Un componente con historial no se borra: se oculta. Las horas de los
formularios son las de la zona del proveedor (`Billing:TimeZone`) y se guardan en UTC.

### Permisos

| Permiso del staff | Qué permite |
|---|---|
| `content.read` | Ver la administración de contenidos, con borradores y archivados |
| `content.write` | Crear, editar, publicar, archivar y borrar artículos, categorías, anuncios y descargas |
| `status.write` | Abrir y actualizar incidentes y mantenimientos, editar los componentes y ver y borrar suscriptores |

Quien solo tiene `status.write` entra directo al estado de la red desde el menú. La portada de contenidos es de quien tiene
`content.read`.

## Configuración

| Clave | Para qué |
|---|---|
| `Content:MaxDownloadBytes` | Tope del archivo de una descarga, en bytes. 52428800 (50 MiB) por defecto |
| `Content:AllowedDownloadExtensions:{n}` | Extensiones permitidas, sin punto. Reemplaza la lista de fábrica. `html`, `svg` y `js` no están en ella y no conviene sumarlos |
| `Content:ImageHosts:{n}` | Hosts desde los que un artículo o un anuncio puede mostrar imágenes, siempre por `https` |
| `Billing:PublicUrl` | Los enlaces del RSS y de los correos de la red salen de acá |
| `Notifications:*` | Los avisos de la red usan la cola de notificaciones: SMTP, `Send`, `OnlyTo` y la traba `ProductionEnabled` |

Con variables de entorno: `Content__ImageHosts__0=img.ejemplo.com` y `Content__MaxDownloadBytes=104857600`.

Cada pedido del sitio tiene un tope de 1 MiB. Los endpoints de subida lo suben solo hasta `MaxDownloadBytes`, y la subida del
panel suma 1 MiB más para los campos del formulario.
La subida desde el panel viaja como formulario y ASP.NET lo limita a 128 MiB: para archivos más grandes se usa la API
(`PUT /api/v1/downloads/{id}/file`), que no tiene ese tope, o el almacenamiento externo de abajo.

Las páginas de artículos y de anuncios llevan una política de contenido que suma a las imágenes del propio sitio las de
`Content:ImageHosts`. Un host fuera de la lista no se ve: el artículo muestra el texto alternativo.

## Archivos de las descargas

Los bytes se guardan en PostgreSQL (`content.download_files`), detrás de `IContentFileStore`. En una receta con varios nodos
se replican con la base y entran en los backups, así que el tope y la cantidad de archivos pesan en el tamaño de la base.
Un almacenamiento compatible con S3 entra implementando la misma interfaz y registrándola antes de `AddContent`.

- El archivo se lee en flujo desde su propia conexión, sin cargarlo entero en memoria, y cada pedido completo suma uno al
  contador con un `UPDATE` atómico. Un `HEAD` responde los datos sin contar.
- Sale siempre como adjunto, con el tipo fijo de su extensión, `X-Content-Type-Options: nosniff` y sin caché compartida.
- El nombre se limpia a letras, números, punto, guion y guion bajo, y se calcula el SHA-256 al subir.
- Subir otro archivo reemplaza el anterior. La descarga conserva su estado y su contador.

## Estado de la red

El estado de un componente no se guarda: se calcula con los eventos vigentes, así nunca queda desfasado. Gana el más grave, y
un grupo toma el más grave de sus hijos.

- Un incidente pide al menos un componente con su impacto y un primer mensaje. Cada novedad puede cambiar el estado y el
  impacto. Al resolverlo se fija la hora de fin y no se reabre: si vuelve a pasar, se abre otro.
- Un mantenimiento tiene desde y hasta. Se puede reprogramar, terminar antes de hora si ya empezó y cancelar.
- La lista del panel tiene tres vistas. Abiertas trae los incidentes sin resolver y los mantenimientos en curso, Programadas los
  que todavía no empezaron y Resueltas los terminados de un mes (con los mantenimientos cancelados), que se elige con el campo
  de mes. Sin vista, la portada junta lo abierto y lo programado con los últimos terminados, los componentes y los
  suscriptores. Abrir un incidente o programar un mantenimiento se hace en Nueva incidencia.
- Cada componente puede decir qué productos afecta. Los clientes con un servicio de esos productos ven sus problemas en el
  portal. `GET /api/v1/status/issues?product_id=` lo ofrece a Soporte para el formulario de ticket.

### Avisos por correo

El trabajo `content.status-notify` arma un correo por cada suscriptor confirmado que corresponde. Se encola en la misma
transacción que el cambio, con la clave de entidad `content.status:{evento}` para que los avisos de un evento salgan en
orden, y cada correo lleva su clave de deduplicación: repetir el trabajo no manda dos veces. Los avisos de inicio y de fin de
un mantenimiento se encolan con su hora y llevan el horario con que se programaron. Si se reprogramó, se canceló o se terminó
antes, el trabajo viejo no manda nada.

Las plantillas son `status-event` y `status-confirm`, en `es-AR` y en `en`, editables como el resto desde
`/api/v1/notifications/templates`. Los avisos respetan la traba de producción del correo: mientras
`Notifications:ProductionEnabled` esté apagada, solo reciben las casillas de `Notifications:OnlyTo`.

La suscripción tiene doble confirmación. La respuesta es la misma exista o no la casilla, el enlace de confirmación sale a lo
sumo una vez por hora para la misma casilla, las suscripciones sin confirmar de más de siete días se borran al suscribir a otra
persona y el enlace de baja pide un botón: abrirlo no da de baja a nadie. El formulario tiene el tope por IP de los formularios
públicos.

## Varios nodos

Nada que importe vive en la memoria de un nodo. Las lecturas, los votos y las descargas suben con `UPDATE ... SET n = n + 1`.
Cada contenido editable lleva un número de revisión que sube con cada cambio: guardar con uno atrasado da 409 en la API y un
mensaje en el panel. La programación de un anuncio se deduce con la hora, sin ningún trabajo que cambie filas, así que a la
hora indicada empieza a verse en todos los nodos.

## Seguridad

- El Markdown es acotado y nunca deja pasar HTML. Los enlaces aceptan `http`, `https`, `mailto`, `tel`, rutas del propio sitio
  y anclas, y los de otro sitio llevan `rel="noopener noreferrer"`. Un cuerpo no pasa de 200.000 caracteres y la conversión
  es lineal.
- La audiencia se resuelve en un solo lugar (`ContentViewer`) y la usan las páginas, la API y la búsqueda. Una categoría se
  muestra si tiene algún contenido visible para quien mira.
- Las acciones del staff quedan en la auditoría con el formato `content.article.publish`, `content.download.upload`,
  `status.incident.open` y semejantes, con datos chicos del cambio y nunca el texto completo.
- Los formularios públicos (votar, suscribirse y darse de baja) tienen el tope por IP de la interfaz. La búsqueda y las páginas
  de lectura no tienen uno propio: ese tope va en el balanceador, como el global de la API.
- Los tokens de confirmar y de baja son de 32 bytes aleatorios y de un solo propósito. No inician sesión ni muestran datos, y
  la API de suscriptores no los devuelve.

## API

Todo cuelga de `/api/v1`, con API key, Problem Details y estos permisos: `kb:read` y `kb:write`, `announcements:read` y
`:write`, `downloads:read` y `:write`, `status:read` y `:write`. Las rutas están en la sección 8 del
[diseño](../../diseno/contenidos.md). Los errores son 404 si no existe, 409 si el cambio choca, 413 y 415 en la subida y 422 si
los datos no valen.

```
# Crear un artículo en borrador, con los dos idiomas, y publicarlo
curl -X POST https://billing.ejemplo.com/api/v1/kb/articles -H "Authorization: Bearer $CLAVE" -H "Content-Type: application/json" -d '{
  "category_id": 3, "audience": "public", "featured": true,
  "texts": { "es-AR": { "title": "Cambiar la contraseña", "body": "Entrá a **Mi cuenta**.", "tags": "clave, acceso" },
             "en":    { "title": "Change your password", "body": "Go to **My account**." } } }'
curl -X POST https://billing.ejemplo.com/api/v1/kb/articles/12/publish -H "Authorization: Bearer $CLAVE"

# Abrir un incidente desde el monitoreo
curl -X POST https://billing.ejemplo.com/api/v1/status/incidents -H "Authorization: Bearer $CLAVE" -H "Content-Type: application/json" -d '{
  "title": { "es-AR": { "title": "Lentitud en el nodo 1" } },
  "message": { "es-AR": { "title": "x", "body": "Estamos investigando." } },
  "components": [ { "component_id": 5, "impact": "partial_outage" } ] }'

# Subir el archivo de una descarga
curl -X PUT "https://billing.ejemplo.com/api/v1/downloads/7/file?name=agente.zip" -H "Authorization: Bearer $CLAVE" --data-binary @agente.zip
```

Las horas con desfase (`2031-05-20T10:30:00-03:00`) se aceptan y se guardan en UTC.

### Sugerencias al abrir un ticket

`GET /api/v1/kb/suggestions?q=<asunto y texto>&account_id=<cuenta>&language=es` devuelve hasta cinco artículos publicados y
visibles para esa cuenta. Busca con "o" entre las palabras con contenido, hasta doce, así que no hace falta que aparezcan todas.
Un texto sin palabras útiles devuelve una lista vacía. Soporte lo llama desde el formulario de ticket del portal.

## Importación

El importador trae la estructura y las filas de la copia, nunca el código ni la documentación del sistema anterior, y el
reporte solo cuenta: no reproduce ningún texto. Las reglas por tabla están en la sección 11 del
[diseño](../../diseno/contenidos.md) y la conciliación en [importacion.md](../../diseno/importacion.md).

Después de importar, queda trabajo a mano que el reporte lista:

1. **Descargas:** quedan en borrador y sin archivo, porque el volcado no trae los bytes. Subir el de cada una y publicarla.
2. **Imágenes de los artículos:** el volcado trae solo los nombres. Cargarlas en un host de `Content:ImageHosts` y corregir
   los enlaces. Mientras tanto se ve el texto alternativo.
3. **Componentes de la red:** se crean uno por servidor o por texto de lo afectado. Renombrarlos y agruparlos.
4. **Incidentes abiertos:** los que el origen tenía sin resolver se importan abiertos y la página de estado los muestra.
   Revisarlos antes del corte.
5. **Categorías de descargas:** las que tenían un ID ya usado por una categoría de la base de conocimiento toman uno nuevo.

## Pruebas

| Clase | Qué cubre |
|---|---|
| `ContentRulesTests`, `MarkdownTests`, `StatusRulesTests` | Reglas del dominio: audiencia, textos, direcciones cortas, Markdown seguro, estados |
| `ContentModelTests` | La migración y las invariantes que la base garantiza |
| `ContentKnowledgeTests`, `ContentAnnouncementTests`, `ContentDownloadTests`, `ContentStatusTests` | Los servicios con su concurrencia, su audiencia y sus avisos |
| `ContentPagesTests` | Las páginas para clientes de punta a punta, con visitas, un cliente de un grupo y otro de otro |
| `ContentAdminTests` | Las pantallas del panel: permisos, formularios, revisiones, vista previa, subida y eventos de la red |
| `ContentApiTests` | La API con sus permisos y sus códigos de error |
| `ContentImportTests`, `HtmlMarkdownTests` | La importación de los cuatro contenidos y el paso del HTML al Markdown |

## Para después

Las imágenes de los artículos cargadas en el propio sitio, el almacenamiento S3 de las descargas grandes, el historial de
cambios de cada artículo, los comentarios, el aviso por correo de los anuncios y el estado de la red alimentado por el
monitoreo. La API ya permite abrir y resolver incidentes con una API key.
