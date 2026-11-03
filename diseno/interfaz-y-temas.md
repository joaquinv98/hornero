# Interfaz, apariencia y plantillas

**Estado:** propuesta, 2026-10-28 · **Decisión:** [D-19](decisiones.md) · **Parte de:** [diseño del núcleo](README.md)

La meta es una interfaz tan buena, tan linda y tan fácil de adaptar que un proveedor se pregunte por qué sigue
pagando el sistema que usa hoy. Este documento fija cómo se logra: el sistema de apariencia y plantillas que el
proveedor edita sin programar, el sistema visual, el tema de fábrica y las pantallas y flujos que no pueden faltar.

Parte de D-10 y D-13 y de lo ya construido: el portal y el admin, los correos y el PDF fiscal.

**Enmienda del 29/10 (sección 10).** La navegación y el tono visual cambiaron: menú clásico con
las áreas donde las espera quien viene de otro sistema de facturación de hosting, simple y rápido. La sección 10
manda sobre lo que el resto del documento diga de la navegación y del tono.

**Fuentes.** Opiniones públicas de proveedores y clientes en foros y sitios de reseñas, material público de otras
alternativas y de portales de facturación, normas argentinas, W3C y documentación técnica (sección 9). No se usó
código, documentación, plantillas, capturas ni textos de WHMCS. Ningún nombre de la interfaz, ruta, clave, clase o
texto lleva su marca. No se usaron datos de clientes reales.

---

## 1. Resumen

- La interfaz sigue en Razor Pages con htmx y sin SPA (D-13). El proveedor nunca edita Razor desde el panel.
- La apariencia tiene **tres capas**: marca y fichas de diseño sin código, textos y plantillas Liquid con contrato,
  y paquetes exportables. Todo se guarda como diferencias sobre lo de fábrica en **revisiones inmutables**, y
  publicar es mover un puntero.
- **Lo legal y lo secreto lo arma el núcleo.** Los botones de la Disposición 954/2025, el canal de atención y el
  bloque fiscal del PDF no se editan. Los enlaces para restablecer la contraseña y para pagar y los códigos de
  constancia nunca pasan por una plantilla editable.
- Una sola compilación por revisión alimenta al portal, al admin, al correo y al PDF. El **contraste WCAG 2.2 AA
  queda garantizado por cálculo**, no por consejo.
- Varios nodos sin mensajería: caché por número de revisión y un puntero que se relee cada 5 segundos.
- Las actualizaciones del producto **no pisan nada** y avisan cuando cambió la plantilla de fábrica debajo de una
  personalizada.
- **Temas:** Hornero, neutro y de fábrica, y los de cada proveedor, que viven como paquete en su despliegue y
  nunca dentro del producto.
- **Pantallas:** pagar en dos clics desde el correo sin iniciar sesión, un inicio armado con lo que pide acción,
  la baja clara y distinta de "no renovar" y un admin organizado por trabajo pendiente.

La decisión va como **D-19**.

---


## 3. Apariencia y plantillas

### 3.1 Principios

1. El proveedor nunca copia un tema entero. Guarda solo lo que cambia, y todo lo que no tocó recibe las mejoras de
   cada versión.
2. Lo legal y lo secreto lo arma el núcleo. El proveedor les da estilo con las fichas, pero no los borra ni los
   mueve.
3. Razor no se edita desde el panel, porque compilarlo en caliente es ejecutar C#. Lo editable es Liquid (Fluid),
   texto, fichas e imágenes.
4. Nada se publica sin pasar el chequeo, y nada se edita sin vista previa.
5. Una sola marca en v0, con `brand_id` en todas las tablas para que varias marcas sean después un cambio chico.

### 3.2 Qué cambia el proveedor

| Capa | Para quién | Qué cambia | Cómo |
|---|---|---|---|
| 1. Marca y fichas | Cualquiera | Estilo base, acento, fondo, superficie, texto, radio, densidad, tipografía de una lista, logo claro y oscuro, ícono y modo (automático, solo claro o solo oscuro) | Formulario con vista previa en vivo. El servidor deriva el resto y mide el contraste |
| 2. Textos y plantillas | Quien quiere ajustar lo que se dice | Las claves de `Texts.resx` menos las legales, asunto y cuerpo de los 12 avisos, layout y pie del correo, tres ranuras del PDF y tres del portal | Editor con variables, vista previa aislada, envío de prueba, historial y vuelta a fábrica |
| 3. Paquetes | Quien pasa el diseño entre instalaciones o comparte un estilo | Una revisión entera con sus imágenes, en un ZIP | Exportar e importar. Importar crea un borrador, nunca publica |

**Datos de la marca**, aparte de las revisiones: nombre comercial, nombre del remitente, canal de atención y área
responsable. Los dos últimos son obligatorios. Reemplazan a `Ui:Brand`, a `NotificationOptions.FromName` (que hoy
trae un nombre fijo en el código del producto) y a `Ui:AttentionChannel`, que hoy es opcional. El nombre también
es el emisor del TOTP. No viajan en los paquetes, y cada cambio queda en la auditoría con el valor anterior y el
nuevo.

**Ranuras del portal:** `Portal.HomeNotice` (aviso en el inicio), `Portal.SignInNote` (texto en el ingreso) y
`Portal.FooterNote` (pie, debajo de lo legal). Son claves de texto vacías del catálogo y se muestran solo si tienen
algo. En v0 son texto plano.

**Partes del correo:** `email/layout` (estructura y cabecera, una para todos los idiomas, con `{{ content }}` y
`{{ block.legal_footer }}` obligatorios), `email/footer` por idioma y un cuerpo con asunto por aviso e idioma.

**Ranuras del PDF:** `pdf/header` (franja arriba del encabezado fiscal, con logo, nombre comercial y contacto),
`pdf/notes` (antes de las leyendas, por ejemplo cómo pagar) y `pdf/footer`.

**Fuera de las tres capas:** Razor, CSS y JavaScript propios. Quien necesite rediseñar la estructura tiene un fork o
una RCL que sobreescribe páginas al compilar su imagen, documentado como fuera del soporte de actualizaciones. El
CSS propio en una capa de cascada queda para después (sección 3.14).

### 3.3 Lo que nunca se edita

- Los botones de baja y de arrepentimiento, con sus nombres y su lugar en el encabezado, y el canal de atención en
  el pie. Viven en el layout de Razor.
- Las claves `Consumer.*` (16 hoy) y las leyendas legales. Quedan fuera de los textos editables.
- El bloque fiscal del PDF: letra y código, datos fiscales del emisor y del receptor, número, líneas, totales,
  transparencia fiscal de la Ley 27.743, CAE y su vencimiento, QR, leyenda de ARCA, leyenda del art. 10 ter de la
  Ley 24.240 y la marca de documento no válido. Hoy todo eso está mezclado con el diseño en `Comprobante.liquid`, y
  si se lo dejara editable el proveedor podría borrar el CAE. Pasa a ser un marco de fábrica, y las ranuras se
  insertan dentro.
- Los bloques del núcleo de la sección 3.4.

### 3.4 Bloques del núcleo y secretos

| Bloque | Dónde | Qué arma el núcleo | Si falta |
|---|---|---|---|
| `block.pay` | Avisos de factura | Botón "Pagar $ X" con el enlace de pago | Advertencia |
| `block.reset` | `password-reset` | Botón con el enlace para restablecer la contraseña | Error |
| `block.receipt` | Constancias de baja y de arrepentimiento | Código de constancia y fecha | Error |
| `block.legal_footer` | `email/layout` | Por qué le llega el correo y el canal de atención | Error |

Las variables `link`, `code` y `company` salen de los modelos editables. `company` pasa a ser `brand.name`. El rótulo
de cada botón es una clave de texto editable (`Email.Block.Pay`, `Email.Block.Reset`).

**Cómo se arma un bloque:**

1. Antes del render, el núcleo genera un valor al azar para ese render y pasa cada bloque como un marcador que lo
   contiene. Liquid ve solo el marcador. Nunca ve el enlace ni el código.
2. Después del render, el tokenizer recorre el HTML. Cada marcador tiene que aparecer una sola vez, como texto,
   fuera de toda etiqueta, atributo, comentario y bloque `style` o `title`.
3. Recién ahí el núcleo lo reemplaza por el HTML del bloque, armado con las fichas de la marca.
4. Si algo no se cumple, al publicar es un error. Al enviar, se usa la plantilla de fábrica y la falla queda
   registrada.

El valor al azar evita que un dato (por ejemplo, el nombre de una cuenta) traiga un marcador falso. La regla de
"solo como texto" cierra el caso de `<img src='https://otro/?{{ block.reset }}'>`, y el filtro de la sección 3.5 lo
cierra otra vez por otro lado.

### 3.5 Filtro de salida

Corre sobre todo lo editable que termina en un correo (layout, pie y cuerpos) y sobre las ranuras del PDF, al
publicar (los problemas son errores) y en cada render (si falla, sale la de fábrica). Usa el `HtmlTokenizer` de
MimeKit 4.18.1, que ya está como dependencia de MailKit, y vuelve a escribir desde cero solo lo permitido:

- **Etiquetas:** texto (`p`, `br`, `strong`, `em`, `b`, `i`, `u`, `small`, `span`, `div`, `h1` a `h3`,
  `blockquote`, `hr`), listas, tablas, `a` e `img`. En el layout además `html`, `head`, `body`, `title` y
  `meta charset`. Nunca `style`, `script`, `link`, `iframe`, `form`, `input`, `object`, `base`, `svg` ni
  `meta http-equiv`.
- **Atributos:** una lista cerrada (`href`, `src`, `alt`, `width`, `height`, `align`, `valign`, `style`, `colspan`,
  `rowspan`, `cellpadding`, `cellspacing`, `border`, `role`, `lang`, `dir`). Nunca `on*`, `srcset` ni `background`.
- **`href`:** `https:` a un host permitido, o `mailto:` y `tel:` sin consulta ni fragmento.
- **`src`:** un host permitido o `data:image/png` y `data:image/jpeg`. En el PDF, solo `data:`, porque Gotenberg
  corre sin red y falla si el documento pide algo de afuera.
- **`style`:** solo sin paréntesis ni barra invertida. Así quedan afuera `url()`, `expression()` y los escapes.
- **Texto:** se codifica de nuevo.

Lo que el filtro quita se informa al publicar, con la etiqueta y el atributo.

**Hosts permitidos:** el de `Billing:PublicUrl` y los de `Billing:LinkHosts`. Los define el operador en la
configuración, sin comodines ni subdominios implícitos. Quien edita en el panel no la amplía. Un proveedor que
quiere enlazar su página de estado o sus redes le pide al operador que sume ese host.

El marcador cierra la fuga de secretos. El filtro cierra el phishing desde el correo oficial, los píxeles de
seguimiento y la exfiltración por selectores CSS. Si el tokenizer no alcanza, el plan B es HtmlSanitizer (MIT), sin
tocar el resto.

### 3.6 Dónde se guarda

Todo en PostgreSQL, esquema `appearance`, sin disco compartido:

| Tabla | Qué guarda |
|---|---|
| `appearance.brands` | Nombre, remitente, canal de atención, área responsable y `published_revision_id`. Una fila en v0 |
| `appearance.revisions` | `content jsonb`, nota, autor, fecha, versión del producto y origen (panel, importación o estilo de fábrica). Solo agrega: un trigger rechaza `UPDATE` y `DELETE`, como D-05 con los documentos |
| `appearance.drafts` | Un borrador por marca, con su revisión base y un número de versión para la concurrencia |
| `appearance.assets` | Imágenes por `sha256`, con tipo, ancho, alto y bytes. Se guardan una vez y su URL no cambia |
| `appearance.render_failures` | Por revisión, parte e idioma: último error, cantidad y fecha de cuando una plantilla editada falló y salió la de fábrica |

Además, `notifications.outbound_emails` suma `revision_id` y `fallback`, y `billing.document_pdfs` suma
`revision_id`. La tabla `notifications.templates` desaparece: sus filas pasan a la primera revisión y la tabla se
quita en el segundo paso de la migración (D-14).

Cada revisión es una instantánea entera de lo que difiere de fábrica. Pesa unos KB, así que leer, exportar y volver
atrás no exige reconstruir nada. Cada sobreescritura guarda el texto de fábrica sobre el que se hizo:

```json
{
  "format": 1,
  "base_style": "neutro",
  "tokens": {"color.accent": "#0B6E69", "shape.radius": 6, "density": "comfortable", "font.body": "system", "mode": "auto"},
  "tokens_dark": {"color.accent": "#4FC1B9"},
  "assets": {"logo.light": "sha256:9f2c0a", "icon": "sha256:41ab7e"},
  "parts": {"email/invoice-issued": {"es-AR": {"subject": "Factura N.º {{ number }}", "body": "<p>Hola, {{ account_name }}:</p>{{ block.pay }}", "factory": "<p>Hola, {{ account_name }}:</p>"}}},
  "texts": {"es-AR": {"Home.NothingDue": {"value": "Estás al día", "factory": "No tenés facturas pendientes"}}}
}
```

### 3.7 Compilación por destino

`ThemeCompiler.Compile(content)` es una función determinista, con las claves siempre ordenadas, que devuelve todo lo
que necesita cada destino: el CSS claro y oscuro, el mapa de colores para el correo, el mapa y las fuentes para el
PDF, el diccionario de textos ya mezclado y las plantillas ya parseadas.

**Derivación de colores**, con la luminancia relativa de WCAG 2.x:

- **Texto sobre el acento:** negro o blanco puros, el que dé más contraste. El peor caso es 4,58:1, así que nunca
  falla. Un estilo puede fijar `color.on_accent` (por ejemplo un casi negro como #0F0E0D), y el chequeo exige 4,5:1.
- **Enlace:** si el acento no llega a 4,5:1 sobre el fondo y la superficie, se oscurece en OKLCH (o se aclara en
  oscuro) hasta que llegue. El panel avisa con el valor elegido.
- **Borde de campo:** se deriva hasta llegar a 3:1 sobre el fondo y la superficie.
- **Foco:** el color del enlace, en un anillo de 3 px con 2 px de separación.
- **Hover y tonos:** `color-mix()` en el CSS, Baseline desde mayo de 2023.
- **Estados** (ok, atención, peligro, info y neutro): fijos y medidos en cada estilo. No se editan, porque llevan
  significado.
- **Modo oscuro:** se propone a partir del estilo base y se puede ajustar a mano.

Los estilos de fábrica traen todas sus fichas medidas por una prueba del build. La derivación corre cuando el
proveedor cambia algo.

**Portal y admin:** `/apariencia/{revisión}.css`, cargado después de `site.css`. Tiene que ser un archivo y no un
estilo en línea, porque el CSP actual lo bloquea. El compilador solo escribe claves conocidas con valores validados
(hex de 6 dígitos, enteros en rango, nombres de una lista), así que no hay inyección de CSS posible.

**Correo:** las plantillas reciben `brand.*` con los hex ya resueltos y los escriben en estilos en línea. Las
variables CSS andan en alrededor del 45 % de los clientes de correo. El logo va por URL absoluta del dominio público
(`/apariencia/a/{sha256}.png`), en una celda con fondo propio para que un PNG transparente no desaparezca con la
inversión de colores. El correo no tiene modo oscuro: anda en menos de la mitad de los clientes y duplica las
pruebas.

**PDF:** un bloque `style` con los valores, las fuentes elegidas embebidas como `data:` (dos pesos por familia como
máximo, unos 20 KB cada uno) y el logo también como `data:`. El acento va solo en el logo y en un filete. El texto
fiscal es siempre negro sobre blanco.

### 3.8 Validación

Un solo chequeo, `ThemeCheck.Run(content)`, que devuelve problemas con nivel, ubicación y mensaje.

**Rápido, al guardar el borrador:** esquema (claves conocidas y tipos), parseo de cada plantilla, marcadores `{0}`
de los textos y topes de tamaño (asunto 300 caracteres, cuerpo 64 KB, texto 2 KB, borrador 1 MB).

**Completo, al publicar, importar, volver atrás y en el comando de migraciones:**

1. **Contraste** de todos los pares declarados, en claro y en oscuro: 4,5:1 para texto y 3:1 para bordes de campo
   y foco. Si no pasa, error con el valor más cercano que pasa.
2. **Render con muestras inventadas**, cada parte sobreescrita en cada idioma: un nombre muy largo, una factura de
   40 líneas, una nota de crédito, una factura en USD con cotización, un documento sin comprobante fiscal, uno sin
   vencimiento y listas vacías. Con un `TemplateOptions` aparte que tiene `StrictVariables` y `StrictFilters`, más el
   delegado `Undefined` para juntar las variables desconocidas: `{{ totla }}` da error y sugiere `total`.
3. **Bloques:** los obligatorios presentes, una sola vez y como texto (sección 3.4).
4. **Filtro de salida** (sección 3.5).
5. **Tiempo:** Fluid 2.40.0 no tiene `CancellationToken` en el contexto, ni `MaxOutputSize` ni `MaxCollectionSize`
   (verificado en el paquete). Se mantienen `MaxSteps`, `MaxRecursion` y el `LimitedWriter` de 256 KB, y el chequeo
   mide la muestra más pesada: si pasa de 200 ms, es error.
6. **Imágenes:** solo PNG y JPEG, controlados por los bytes de cabecera y no por el `Content-Type`. Hasta 512 KB y
   2048 px. Sin SVG, porque puede traer script.
7. **Textos:** las claves bloqueadas se rechazan, los marcadores tienen que coincidir con el original y hay un largo
   máximo.
8. **Accesibilidad básica del correo:** advertencia por cada `img` sin `alt`.

Los errores traban la publicación. Las advertencias se muestran y se aceptan con una casilla. En la ejecución real el
render es tolerante: si una parte editada falla, sale la de fábrica, se marca `fallback` y se suma a
`render_failures`.

### 3.9 Vista previa

- **Muestra:** `/admin/apariencia/muestra` junta todos los componentes en el layout del portal con el CSS del
  borrador: estados, tabla de facturas, alertas, un formulario con errores, botones, pasos de pago, los botones
  legales y el pie. Se ve en un iframe de 360, 768 o 1280 px, en claro y en oscuro, sin JavaScript para cambiar de
  ancho. Es también el banco de las pruebas automáticas de accesibilidad.
- **Mientras se edita:** `hx-trigger="input changed delay:500ms"`, el mismo patrón del buscador del admin, guarda el
  borrador y devuelve el informe de contraste con el iframe recargado. Sin JavaScript hay un botón "Ver vista
  previa".
- **Correo y PDF:** el HTML lo escribe el proveedor, así que va en una ruta aparte cargada en un
  `<iframe sandbox>` sin `allow-scripts` ni `allow-same-origin`. Nunca con `srcdoc`, que hereda el CSP del admin y
  rompe los estilos en línea. Esa ruta pisa los encabezados globales de `Ui.cs` (`X-Frame-Options: DENY` y
  `frame-ancestors 'none'`): usa `X-Frame-Options: SAMEORIGIN` y un CSP con `sandbox`, `default-src 'none'`,
  `style-src 'unsafe-inline'`, `img-src 'self' data:`, `font-src data:` y `frame-ancestors 'self'`. Una prueba lo
  controla. El visor de correos enviados del admin usa la misma ruta.
- **Mandarme una prueba:** manda el aviso con datos de muestra solo al correo del miembro del staff que mira, por la
  cola de salida, con "[Prueba]" en el asunto y un tope de envíos.
- **PDF de muestra:** sale por Gotenberg con un CUIT inventado y la marca "MUESTRA, SIN VALIDEZ FISCAL".
- **Portal real con el borrador** (fase B): desde "Ver como cliente", con una marca en la sesión del staff.
- **Modo de fábrica:** en "Ver como cliente", `?apariencia=fabrica` dibuja todo con lo de fábrica.

### 3.10 Versiones y permisos

- **Borrador:** uno por marca, con número de versión. Guardar con una versión vieja da 409 y avisa que otra persona
  lo cambió.
- **Publicar:** en una transacción corre el chequeo completo, inserta la revisión y mueve el puntero con una
  condición sobre el puntero esperado, para que dos publicaciones no se pisen.
- **Volver atrás:** publicar una revisión anterior. Se vuelve a chequear contra la versión actual del producto.
- **Volver a fábrica:** por elemento (un texto, un correo, el aspecto) o todo junto. Los textos no tocan la marca.
- **Diferencias:** entre dos revisiones y contra fábrica, con un diff de líneas propio. Las plantillas tienen
  decenas de líneas y alcanza con LCS, sin dependencias.
- **Permisos del staff:** `appearance.edit` para el borrador y las imágenes, `appearance.publish` para publicar,
  volver atrás e importar. Publicar pide otra vez el código TOTP. Scopes de API `appearance:read`,
  `appearance:write` y `appearance:publish`.
- **Auditoría:** cada publicación queda en `platform.audit` con la nota, las claves que cambiaron, los hosts nuevos
  que aparecen en los enlaces y los bloques que se quitaron.

### 3.11 Varios nodos

- Cada nodo guarda la fila de la marca, con su puntero, por 5 segundos.
- Lo caro se guarda en un `IMemoryCache` con tope, con el id de revisión como clave: el tema compilado, las
  plantillas parseadas (`IFluidTemplate` es seguro entre hilos), el CSS y los textos. Una revisión no cambia nunca,
  así que no hay nada que invalidar. La revisión publicada va con prioridad `NeverRemove`.
- El nodo que publica lo ve al instante y los demás en 5 segundos. Las URL llevan el id de revisión, así que una
  página nunca mezcla dos versiones.
- Sin `LISTEN/NOTIFY`, que no pasa por PgBouncer en modo transacción (D-07), y sin los tags de HybridCache, que no
  limpian la memoria de los otros nodos.
- El correo se arma al encolar y guarda su revisión, así que un reintento no cambia el contenido. El PDF fiscal se
  genera una vez (D-05) y guarda la suya: cambiar el logo no toca lo ya emitido, y el panel lo avisa en la pestaña
  Marca.

Hoy `TemplateRenderer` consulta la base y vuelve a parsear en cada aviso. Con esto hace una lectura en caché.

### 3.12 Actualizaciones del producto

1. **Contratos versionados:** las claves de fichas, el catálogo de partes con sus variables y bloques y el formato
   del paquete. Agregar es libre. Renombrar deja el nombre viejo como alias durante una versión mayor, con un mapa
   que se aplica al compilar sin reescribir revisiones. Quitar solo en una versión mayor y con aviso.
2. **Prueba de snapshot en el build** de los contratos de los 12 avisos y de las ranuras del PDF. Falla si cambian
   sin versión o sin alias, y exige un valor de muestra para cada variable. La referencia para quien edita
   plantillas se genera de ahí.
3. **La de fábrica cambió:** si una versión nueva cambia el texto de fábrica de una parte personalizada, se sigue
   usando la del proveedor. El panel la marca y muestra dos diferencias: qué cambió la fábrica y cómo está la del
   proveedor. "Marcar como revisada" actualiza el texto de fábrica guardado en el borrador. No hay fusión automática.
4. **`apariencia verificar`** corre dentro del comando de migraciones de D-14: dibuja la revisión publicada con las
   muestras de la versión nueva y lista qué partes van a caer a fábrica. Informa y no traba, salvo con
   `--estricto`.
5. **Fallas en ejecución:** van a `render_failures` y a una métrica de OpenTelemetry (D-15), y se ven en la portada
   de Apariencia y en la cola "Requiere atención" del admin.
6. **Formatos viejos de paquete:** una cadena de funciones de subida, con pruebas sobre contenidos guardados. Si la
   revisión publicada no carga, el nodo usa todo de fábrica y lo marca en la salud.

### 3.13 API y comando

Reemplaza a `/notifications/templates` de la rama de notificaciones, que todavía no salió a producción:

| Método y ruta | Para qué |
|---|---|
| `GET` y `PUT /api/v1/appearance/draft` | Leer y guardar el borrador, con `If-Match` |
| `POST /api/v1/appearance/draft/check` | Correr el chequeo completo |
| `POST /api/v1/appearance/draft/preview/{parte}` | Vista previa con muestras |
| `POST /api/v1/appearance/publish` | Publicar el borrador |
| `GET /api/v1/appearance/revisions` y `POST .../revisions/{id}/publish` | Historial y volver atrás |
| `GET .../revisions/{id}/package` y `POST /api/v1/appearance/packages` | Exportar e importar |
| `POST /api/v1/appearance/assets` | Subir una imagen |

`apariencia verificar` valida un paquete o la revisión publicada sin levantar la web. Sirve también en la
integración continua de quien diseña un estilo.

**Paquetes:** un ZIP con `tema.json` (formato, nombre, autor, licencia, versión mínima del producto y el contenido
de la revisión) y `assets/{sha256}.png`. Al importar: hasta 50 entradas y 5 MB descomprimidos con el largo
controlado mientras se lee, solo nombres esperados, lectura en memoria sin extraer a disco y el `sha256` de cada
imagen verificado.

### 3.14 Para después

| Qué | Cuándo |
|---|---|
| CSS propio en `@layer custom`, con las reglas de los botones legales en una capa anterior con `!important` | Cuando un proveedor lo pida. Antes hay que cerrar D-18, porque las clases `bo-*` pasan a ser contrato |
| Varias marcas por dominio y revendedores: resolución por host con lista permitida, marca por cuenta en los trabajos, permisos por marca | Cuando haya revendedores |
| Hosts de enlace editables por el dueño con `appearance.publish` | Si depender del operador molesta |
| Idiomas nuevos sin compilar, con control de completitud | Cuando un proveedor lo pida |
| Fuentes propias subidas y recodificación de imágenes con una librería permisiva | Cuando suban imágenes los revendedores |
| Negrita, listas y enlaces en las ranuras del portal | Con un saneador de licencia permisiva |
| Fluid 3 con `CancelAfter` | Cuando salga estable. Permite borrar `LimitedWriter` |
| Temas completos en Liquid para el portal | Solo con demanda real y una decisión que reabra D-13 |

---

## 4. Sistema visual

### 4.1 Fichas de diseño

Tres niveles: primitivos (la paleta cruda), semánticos (los que usan los componentes) y de componente, solo donde
hagan falta. Los semánticos mínimos:

| Grupo | Fichas |
|---|---|
| Superficies | `--bg`, `--surface`, `--surface-2`, `--line` (decorativo, sin mínimo) y `--control-border` (3:1) |
| Texto | `--text`, `--text-muted`, `--link` y `--link-hover` |
| Acción | `--action-bg`, `--action-text` y `--focus` |
| Estados | `ok`, `warn`, `danger`, `info` y `neutral`, cada uno con `-bg`, `-text` y `-dot` |
| Tipografía | `--font-body`, `--font-data` y la escala `--fs-*` |
| Espacio y forma | `--sp-1` a `--sp-8`, `--radius-*` y `--control-h` |
| Movimiento | `--dur-fast` (120 ms) y `--dur` (200 ms) |

**Claves editables**, que son contrato: `color.accent`, `color.background`, `color.surface`, `color.text`,
`color.on_accent` (opcional), `shape.radius`, `density`, `font.body`, `font.data`, `logo.light`, `logo.dark`,
`icon` y `mode`, más las de `tokens_dark`. Pocas y bien elegidas.

`site.css` pasa a capas: `@layer base, components, theme`. El CSS de la marca escribe en `theme`.

### 4.2 Tipografía

| Ficha | Tamaño y alto de línea | Uso |
|---|---|---|
| `--fs-xs` | 12 / 16 px | Píldoras y leyendas legales del PDF |
| `--fs-sm` | 14 / 20 px | Ayudas, encabezados de tabla y metadatos |
| `--fs-md` | 16 / 24 px | Texto y campos. Nunca menos en campos: Safari de iPhone hace zoom al enfocarlos |
| `--fs-lg` | 18 / 28 px | Bajadas y totales |
| `--fs-xl` | 22 / 30 px | h2 |
| `--fs-2xl` | 28 / 36 px | h1 |
| `--fs-3xl` | 40 / 44 px | El saldo a pagar en el detalle y en el pago |

- En `rem`, para respetar el zoom. Pesos 400 y 700, más 600 para etiquetas en el tema neutro.
- **Familias:** el tema neutro usa la pila del sistema, que no descarga nada, y `ui-monospace` para datos. La lista
  del panel en v0 es sistema, Outfit y JetBrains Mono, todas OFL y autoalojadas en woff2. Otras se suman revisando
  su licencia (D-01). Sin CDN de fuentes, por privacidad y porque el PDF se arma sin red.
- **Números tabulares** (`font-variant-numeric: tabular-nums`) en tablas y montos. Outfit trae `tnum`, pero sus
  dígitos por defecto son proporcionales: sin eso, las columnas de importes no alinean.
- Texto corrido de 65 caracteres como máximo.

### 4.3 Espacio, forma y controles

- **Espacios con base de 4 px:** 4, 8, 12, 16, 24, 32, 48 y 64.
- **Radios:** el neutro usa 4, 6 y 10 px. Un tema de marca puede usar otros, como 6, 8 y 12 px. Las píldoras, 999 px.
- **Controles:** 44 px de alto en densidad cómoda y 36 px en compacta, con 8 px entre botones vecinos. El mínimo de
  WCAG 2.2 (2.5.8) es 24 px.
- **Ancho:** 64 rem en el portal y 72 rem en el admin, con 16 px de margen lateral en el teléfono.
- **Quiebres por contenido:** a 40 rem las tablas pasan a tarjetas y los formularios a una columna. A 64 rem las
  tarjetas del inicio pasan a tres columnas.

### 4.4 Estados: punto, palabra y forma

Hoy `.status` distingue los estados solo por el color del borde. Pasa a `.bo-status` con `data-tone` y
`data-shape`, un punto `aria-hidden` y la palabra. La forma repite el significado: **lleno** es vigente o resuelto,
**hueco** es en curso o pendiente y **con anillo** pide acción.

| Tipo | Estado y forma |
|---|---|
| Factura | Borrador: neutro hueco. Por pagar: atención lleno. Pagada en parte: atención hueco. Pagada: ok lleno. Anulada: neutro lleno. **Vencida** (se calcula para mostrar, no es estado del dominio): peligro con anillo |
| Servicio | Pendiente: info hueco. En preparación: info hueco con pulso. Activo: ok lleno. Suspendido: atención lleno. Con falla: peligro con anillo. Cancelado y Dado de baja: neutro |
| Pago | Pendiente: info hueco. Aprobado: ok lleno. Rechazado: peligro con anillo. Devuelto: neutro. Devuelto en parte: atención hueco. En disputa: atención con anillo. Contracargo: peligro con anillo |

La píldora lleva un borde transparente de 1 px para verse en los colores forzados de Windows. El pulso se apaga con
`prefers-reduced-motion`. El estado es un adjetivo y la píldora no es un enlace.

### 4.5 Montos y fechas

Hoy hay tres funciones y cada una formatea distinto: `Ui.Money` da "12.100,00 ARS", `Notifier.Money` da
"ARS 12.100,00" y el PDF da "$ 12.100,00" con espacio común. Pasan a ser **una sola en `BillingOss.App`**, sin
depender de la cultura del sistema:

- ARS con "$", USD con "US$" y el resto con el código ISO. Entre símbolo e importe, espacio duro (U+00A0).
  Negativos con signo: "-$ 1.234,50". Saldos acreedores con la palabra "a favor", sin depender del rojo.
- `white-space: nowrap` en `.bo-money`, para que el "$" no quede en una línea y el número en otra.
- Importes alineados a la derecha, con números tabulares y siempre dos decimales. Si la tabla tiene una sola
  moneda, va en el encabezado. Si mezcla, en cada celda. El valor para máquinas va en `<data value="12100.00">`.
- **Fechas:** "28/10/2026" en tablas, "28 oct 2026" en tarjetas y la forma larga en el PDF y en las constancias,
  siempre con `<time datetime>`. Los plazos ("vence en 3 días", "venció ayer") son claves de texto propias.

Las plantillas reciben los montos ya formateados, así que portal, correo y PDF coinciden.

### 4.6 Componentes

- **Tabla de facturas:** comprobante (enlace), emisión, vencimiento, total, saldo, estado y "Pagar" en cada fila
  con saldo. Arriba, "Tenés $ 45.300,00 por pagar en 2 facturas" con "Pagar todo". Filtros Por pagar, Pagadas y
  Todas como enlaces GET con `aria-current`: con htmx se cambia solo la tabla y sin JavaScript funciona igual. En
  el teléfono cada fila se ve como tarjeta, con la misma tabla, `data-label` en cada celda y los roles de tabla
  explícitos para navegadores viejos.
- **Detalle de factura:** el saldo en `--fs-3xl` con "Pagar" al lado. En el teléfono, una barra fija abajo con el
  saldo y el botón, con `scroll-padding-bottom` para no tapar el foco.
- **Formularios:** etiqueta arriba, ayuda y error debajo, unidos con `aria-describedby` y `aria-invalid`. Al enviar
  con errores, un resumen arriba con `role="alert"` y el foco en él, un enlace por error y "Error:" al principio
  del título. Los mensajes dicen cómo arreglarlo ("Escribí el CUIT con 11 números"). `autocomplete` correcto,
  `inputmode="numeric"` en CUIT, DNI y códigos, el CUIT con o sin guiones. Sin bloquear el pegado y sin botones
  deshabilitados.
- **Alertas:** info, éxito, atención y peligro, con ícono, título corto y acción si corresponde. `role="alert"` solo
  para errores después de una acción y `role="status"` para confirmaciones. Lo importante (pago aprobado,
  constancia) no desaparece solo.
- **Pasos de pago:** Revisar, Medio de pago y Listo, en un `<ol>` con `aria-current="step"`. Cada medio es una
  tarjeta clickeable entera de 44 px o más, con una línea de qué pasa después ("Te llevamos a Mercado Pago"). Un
  solo botón con el monto ("Pagar $ 12.100,00") que se deshabilita al enviar para evitar el doble clic. La página
  de vuelta no cambia nada (D-10) y muestra aprobado, pendiente o rechazado con Reintentar y Cambiar de medio.
- **Vacíos:** dicen el estado, qué va a pasar y la acción si existe. Sin deuda, un bloque "Estás al día" con punto
  ok. En el admin, "No encontramos cuentas con «garcia»" y una sugerencia.
- **Errores:** ya existen 400, 403, 404 y 429. Faltan 500 y 503. Cada una con un h1 claro, el código chico en
  monoespaciada, una salida y el canal de atención. En el 429, el tiempo concreto si se conoce.

### 4.7 Accesibilidad (WCAG 2.2 AA)

**Hoy fallan tres cosas** en `site.css`: el borde de los campos (`--line` #D9DCDF da 1,38:1 sobre blanco y #343A40
da 1,42:1 en oscuro, y 1.4.11 pide 3:1), el borde de atención (#C98A00 da 2,95:1) y los estados, que se distinguen
solo por color.

**Criterios nuevos de 2.2 que tocan esta interfaz:**
- 2.4.11, foco no tapado: barras fijas con `scroll-padding` igual a su alto.
- 2.5.8, objetivo de 24 por 24 px: los enlaces de idioma y "Salir" necesitan relleno.
- 3.2.6, ayuda consistente: el canal de atención siempre en el mismo lugar del pie.
- 3.3.7, entrada redundante: datos de facturación precargados en el pago y en el alta.
- 3.3.8, autenticación accesible: pegar y usar el gestor de contraseñas en contraseña y TOTP. El TOTP va en un solo
  campo con `autocomplete="one-time-code"`, como ya está, nunca en seis casillas. Sin acertijos ni captchas.

**De 2.1 siguen:** reflujo a 320 px sin scroll horizontal fuera de las tablas, espaciado de texto, contenido al pasar
el mouse y propósito de cada campo. Además, `prefers-reduced-motion` y `forced-colors`.

**Cómo se prueba:** axe automático en los recorridos principales y sobre la Muestra, NVDA con Firefox y VoiceOver en
iPhone a mano antes del staging.

**Ya cumple:** salto al contenido, `lang`, `scope` y `caption`, `aria-label` en los `nav`, `:focus-visible` y el
aviso de "Ver como cliente" con `role="status"`.

### 4.8 Claro y oscuro

- El proveedor elige el modo en la marca: automático, solo claro o solo oscuro.
- En automático, la persona elige Sistema, Claro u Oscuro. Se guarda en una cookie por una ruta `/tema/{valor}`,
  como `/idioma`, y el servidor escribe `data-theme` en el `html`. Sin destello al cargar y sin JavaScript.
- Las fichas se redefinen bajo `[data-theme="dark"]` y bajo `prefers-color-scheme: dark` cuando no hay
  `data-theme="light"`. Con `meta name="color-scheme"` para que los controles nativos acompañen.
- Logo claro y oscuro con `<picture>`.
- Sin `light-dark()`: es Baseline recién desde mayo de 2024, y un navegador que no lo entiende pierde el color
  entero.

### 4.9 Textos

- Voseo en todo (Pagá, Ingresá, Probá). Botones con verbo y objeto ("Pagar la factura", "Descargar el PDF"), nunca
  "Aceptar" ni "Click acá".
- Mayúscula solo al inicio, salvo el estilo de los botones legales.
- Glosario único: Factura, Comprobante, Por pagar, Vencida, Saldo, Saldo a favor, Pago, Medio de pago, Servicio,
  Baja, Arrepentimiento, CUIT, CAE e IVA. "N.º" para números de comprobante.
- Donde hoy dice "Impuestos" y es IVA (`Invoice.Tax`), decir IVA con su alícuota.
- Sin jerga para el cliente: "En preparación", no "Aprovisionando".
- Los errores dicen qué hacer y no culpan. Sin "¡Ups!".
- En inglés, los nombres legales de los botones quedan en castellano, como ya se hace.

---

## 5. El tema de fábrica

Los contrastes de esta sección están medidos con la luminancia relativa de WCAG 2.x.

### 5.1 Hornero (estilo Neutro)

Sale de `site.css` con los arreglos de la sección 4.7. Grises fríos y un único acento verde petróleo sin
connotación de marca, para que cualquier proveedor lo use tal cual. Es el estilo por defecto.

| Ficha | Claro | Contraste | Oscuro | Contraste |
|---|---|---|---|---|
| `--bg` | #F7F7F5 | | #14171A | |
| `--surface` | #FFFFFF | | #1C2024 | |
| `--surface-2` | #EEF0F2 | | #252A30 | |
| `--line` | #E1E4E8 | Decorativo | #343A40 | Decorativo |
| `--text` | #1D2125 | 15,10 sobre bg y 16,20 sobre surface | #E8EAED | 14,93 y 13,60 |
| `--text-muted` | #5B6470 | 5,59, 6,00 y 5,25 sobre surface-2 | #A3ABB4 | 7,06 sobre surface y 6,22 sobre surface-2 |
| `--control-border` | #7B848F | 3,53, 3,79 y 3,32 | #747D89 | 4,32 sobre bg y 3,93 sobre surface |
| `--link`, `--action-bg`, `--focus` | #0B6E69 | 5,68 y 6,09 | #4FC1B9 | 8,28 y 7,54 |
| `--action-text` | #FFFFFF | 6,09 sobre el acento | #0D1113 | 8,73 sobre el acento |
| `--link-hover` | #085752 | 7,85 y 8,42 | | |

| Estado | Claro (texto sobre fondo) | Punto | Oscuro (texto sobre fondo) |
|---|---|---|---|
| ok | #17663F sobre #E6F4EE, 6,16 | #1B7A4B, 4,98 sobre bg | #6FD39B sobre #12301F, 7,81 |
| Atención | #8A5300 sobre #FFF4D6, 5,78 | #A86B00, 4,11 sobre bg | #F0C04D sobre #3A2F10, 7,75 |
| Peligro | #A8200D sobre #FDE8E8, 6,22 | #B42318, 6,57 sobre blanco | #FF8A80 sobre #3D1A1A, 6,76 |
| Info | #1A5A9E sobre #E7F0FA, 6,08 | | #8DBEF5 sobre #13263D, 7,88 |
| Neutro | #4A525C sobre #EEF0F2, 6,93 | | #C3C9D0 sobre #2A3036, 7,99 |

Botón de peligro: blanco sobre #A8200D, 7,30. Tipografía del sistema y radios de 4, 6 y 10 px.

En la fase A viene también un estilo **Alto contraste**. En la fase B se suman dos o tres más, todos en el mismo
formato de paquete.


## 6. Pantallas y flujos

### 6.1 Principios

1. Todo lo que pide acción, en la primera pantalla.
2. Pagar en dos clics desde el correo, sin iniciar sesión.
3. Precio y fecha de la próxima renovación siempre a la vista.
4. La baja tan fácil como el alta, y distinta de "no renovar".
5. El lenguaje del cliente, no el del sistema.
6. Velocidad y uso en el celular como requisitos medibles.
7. Marca y textos editables sin código, con vista previa, historial y vuelta a fábrica.
8. Un admin organizado por trabajo pendiente.

Los proveedores que siguen con el sistema dominante no lo eligen por gusto: se quedan porque migrar duele y faltan
módulos. Se quejan de áreas de clientes difíciles de personalizar, de lentitud, de personalizaciones que se pierden
al actualizar y de paneles de admin feos y con poca densidad. Los clientes finales sufren bajas escondidas, "no
renovar" confundido con "dar de baja" y renovaciones más caras avisadas tarde.

### 6.2 Imprescindibles

| Flujo | Qué tiene que tener | Hoy | Paso del plan |
|---|---|---|---|
| Pagar desde el correo | Cada aviso de factura con "Pagar $ X" hacia la página de esa factura con un enlace de un solo propósito: ver, pagar y bajar el PDF, nada más | Los correos llevan a la raíz del portal, que pide ingresar | 5.2 |
| Inicio | Facturas por pagar con "Pagar todo", servicios suspendidos con el motivo y cómo reactivarlos, vencimientos de 30 días con precio, tarjeta por vencer, trámites en curso, condición IVA faltante. Sin pendientes: "Estás al día" | Cuentas, por pagar, servicios activos y saldo a favor | 5.1 |
| Ficha de servicio | Estado en lenguaje llano, período pagado, próxima renovación con fecha y monto con IVA, datos de acceso, acceso al panel, acciones del módulo, cambio de plan con vista previa del prorrateo | Lista de servicios | 5.4 |
| Baja | "Dar de baja" con dos opciones explícitas: al fin del período pagado (por defecto) o ahora. Antes de confirmar dice la fecha de corte, qué datos se pierden y si hay reintegro. Constancia. Se deshace con un clic hasta la fecha de corte. "No renovar" es otra cosa y la pantalla lo dice. Motivo opcional. Una oferta de retención solo como opción que no frena | Trámite público con constancia. Falta la ejecución | 5.4 |
| Botones legales | Canal de atención y área responsable siempre (obligatorios en la marca). Ejecución del trámite. En el admin, los trámites con el tiempo que falta para el plazo de 24 horas | Botones y constancia hechos. El canal es opcional | 2.3 y 5.5 |
| Comprobantes en el portal | Tipo, punto de venta y número, CAE y vencimiento, estado de pago, PDF con QR y notas vinculadas a su factura | Detalle con CAE | 5.6 |
| Condición frente al IVA | Pedida en el alta antes de la primera factura, aviso en el inicio a quien no la tiene. Desde el 1/12/2026 ARCA rechaza el CAE sin `CondicionIVAReceptorId` | El dominio la guarda | 5.6 |
| Pago y moneda | Pasos de pago (4.6). Si una factura en dólares se paga en pesos: monto en pesos, cotización con fuente (BNA vendedor) y fecha, y hasta cuándo vale. El pago en pesos destacado | Un botón por pasarela | 5.3 |
| Medios locales | Mercado Pago, transferencia a CVU o alias, QR interoperable y PayPal para el exterior. Débito automático opcional, visible y apagable | Botones por pasarela | 5.3 |
| Cobranza amable | Aviso antes de cada cobro automático, de tarjeta por vencer, recordatorios con el enlace de pago, aviso de suspensión con fecha exacta y reactivación apenas entra el pago. Tono de ayuda | Plantillas de recordatorio, vencida y suspensión | 3.1 y 5.2 |
| Admin por trabajo pendiente | Portada "Requiere atención" con contadores y enlaces: aprovisionamientos fallidos, emisiones fiscales inciertas, pagos sin imputar, trámites con su plazo, tickets sin respuesta, webhooks rechazados, servicios por suspender hoy y fallas de apariencia. Buscador extendido a facturas, CAE, servicios, IP y dominios | Buscador de cuentas y ficha | 5.5 |
| Pedido | Una página: plan, ciclo, opciones, dominio y datos fiscales, con el total e IVA desde el principio, la cuenta creada en el mismo paso y enlaces directos a un plan ya elegido | No existe | 5.7 |
| Velocidad y celular | Metas medibles en las pruebas | Razor y htmx ya van en esa dirección | 5.8 |
| Ingreso accesible | Sección 4.7 | El TOTP ya va en un campo con `one-time-code`. Faltan el resumen de errores y los tamaños de objetivo | 1.4 |
| Apariencia | Sección 3 | Plantillas de correo editables por API, sin historial | Frentes 2 a 4 |

### 6.3 Los cuatro que más pesan

**Pagar desde el correo.** Es el cambio con más impacto en la cobranza y el más fácil de mostrar en una demo.

- Pieza nueva del núcleo: un enlace por factura con un token aleatorio de 32 bytes, guardado como SHA-256 igual que
  las API keys (D-11). Sirve solo para esa factura, no inicia sesión y vence 30 días después del vencimiento.
- La página `/pagar/{token}` muestra la factura, los medios de pago y el PDF. No muestra nada más de la cuenta.
- Si el enlace venció, pide el correo y manda uno nuevo, con la misma respuesta exista o no la cuenta. Usa el tope
  por IP que ya existe en los formularios públicos.
- `block.pay` apunta ahí. Mientras la pieza no exista, apunta a `/facturas/{id}`.
- Clic 1, el botón del correo. Clic 2, el medio de pago.

**Inicio.** Responde "¿tengo que hacer algo?". El orden: lo vencido, lo suspendido, lo que vence en 30 días, lo que
falta completar (condición IVA, tarjeta) y los trámites en curso. "Pagar todo" es un solo intento de cobro sobre
varias facturas, como ya permite el dominio. La navegación se arma por tareas: Pagar, Mis servicios, Facturas,
Soporte y Cuenta.

**Baja.** Lo pide la Disposición 954/2025 (sin trámites adicionales, constancia en 24 horas) y la Ley 24.240
(arts. 10 ter y 10 quáter, sin cargo ni preaviso). Para el cliente, es la diferencia entre confiar o no. El flujo de
dominio ya está en la sección 12.3 del [modelo](dominio.md). Lo que suma la interfaz es la pantalla que explica antes
de confirmar, el "deshacer" hasta la fecha de corte y "No renovar" separado.

**Admin por trabajo pendiente.** La portada del staff es una cola, no un buscador. Cada contador lleva a una lista
filtrada con acciones masivas y confirmación. Las tablas son densas, ordenables y filtrables. Toda acción queda en
la auditoría y se ve en la ficha. "Ver como cliente", que ya está hecho, se muestra como ventaja.

### 6.4 Diferenciales

| Qué | Por qué |
|---|---|
| Temas con contraste garantizado, vista previa en 3 anchos y actualizaciones que no pisan nada | Ninguna alternativa revisada publica un control de accesibilidad al cambiar colores |
| Vista previa de la próxima factura | Fecha, líneas y total estimado. Elimina la sorpresa de la renovación, la queja más repetida |
| Historial de mensajes enviados en el portal | Cada correo con la opción de reenviarlo. Resuelve "no me llegó la factura" y prueba los avisos. El outbox ya los guarda |
| Carpeta para el contador | ZIP con los PDF de un período y un CSV de comprobantes (tipo, número, CAE, neto por alícuota, IVA, total y cotización), para el cliente y para el proveedor |
| Datos fiscales desde el CUIT | Consulta a la constancia de inscripción de ARCA y completa razón social, domicilio y condición IVA. Requiere habilitar el servicio en el certificado |
| Ingreso sin contraseña | Enlace de un solo uso por correo. Passkeys cuando Identity traiga la interfaz fuera de Blazor (D-10). Las acciones sensibles piden verificación más fuerte |
| Soporte desde el servicio | Ticket con el servicio elegido, respuesta por correo que vuelve al ticket, artículos sugeridos, incidentes conocidos antes de enviar y PIN de soporte |
| Estado de la plataforma en el portal | Incidentes y mantenimientos en la tarjeta del servicio afectado y en el formulario de ticket |
| Pausar en vez de dar de baja | Donde el módulo lo permite, por producto, siempre como alternativa y nunca como paso obligatorio |
| Paleta de comandos en el admin | Ctrl+K busca cualquier entidad y lleva a sus acciones, con atajos para lo frecuente |
| Código abierto, sin cobrar por staff ni por facturación | Se decide en D-18 y se comunica en el sitio, junto con la alta disponibilidad |
| Migración sin dolor | El importador como parte del producto: simulación, IDs conservados, contraseñas que siguen andando y conciliación centavo por centavo |

### 6.5 Para después

Crédito prepago con carga desde el portal (hay que definir con el contador cómo se trata la carga), afiliados y
cupones de retención (siempre opcionales y sin frenar la baja), precios exclusivos y una app instalable con avisos
push.

---


## 8. Riesgos abiertos

- **Fluid 2.40.0 sin cancelación.** El tope de tiempo es aproximado: un filtro caro sobre un texto grande podría
  tardar más en producción que en la muestra. Se mitiga con modelos acotados que arma el núcleo, y se pasa a Fluid 3
  cuando sea estable.
- **El tokenizer no arma el árbol como un navegador.** Por eso se escribe de nuevo solo lo permitido, se prueba con
  un corpus de ataques y queda HtmlSanitizer como plan B.
- **Contenido engañoso.** Un texto editable puede contradecir lo legal con palabras, aunque el botón siga ahí. Queda
  como responsabilidad del proveedor, con auditoría, y se documenta.
- **Logo fijo en comprobantes emitidos** (D-05). Hay que avisarlo antes de la primera emisión.
- **Logo en correos internos.** Si `Billing:PublicUrl` no es pública, el logo no se ve en el correo.
- **Cotización y percepción.** La percepción del 30 % en consumos con tarjeta en moneda extranjera y el tratamiento
  de facturas en dólares cobradas en pesos se confirman con el contador.
- **Débito automático con Mercado Pago.** El cobro recurrente sin código de seguridad exige el checkout por API o
  Bricks. Las tarjetas guardadas en el checkout común vuelven a pedirlo.
- **Contrato `bo-*`.** Pasa a ser contrato recién con el CSS propio, y para entonces D-18 tiene que estar cerrada.
- **Un paquete de marca no puede entrar al producto.** Si se cuela entre los estilos de fábrica, el producto libre
  sale con la marca de un proveedor. Una prueba del build controla que ningún estilo de fábrica traiga imágenes.

---

## 9. Fuentes


**Normas:** Disposición 954/2025 (botones de baja y arrepentimiento), Ley 24.240 (arts. 10 ter y 10 quáter), RG 4892
(QR), Ley 27.743 y RG 5614 (transparencia fiscal), RG 5616 (cotización y condición IVA del receptor). Las fuentes
oficiales están en la sección 14 del [modelo de dominio](dominio.md).

**Accesibilidad y web:** W3C, WCAG 2.2 (novedades, 1.4.11 y 3.3.8). GOV.UK Design System (resumen de errores y
etiquetas de estado). MDN (`color-mix()`, `@layer`, `light-dark()`, `forced-colors` e `iframe sandbox`). Adrian
Roselli, tablas y `display`. Baymard, elección de medio de pago. caniemail (variables CSS, `prefers-color-scheme` e
imágenes en base64). OWASP, subida de archivos.

**Plantillas y temas:** README de Fluid y de Liquid. Shopify (ajustes de tema, presets, notificaciones editables y
la configuración del checkout solo con marca). Ghost (ajustes de diseño y su tope). Discourse (textos editables y
caché de temas). WordPress (revisiones de estilos globales). FOSSBilling (temas y `html_custom`). W3C Design Tokens
Format 2025.10.

**Plataforma:** PgBouncer (funciones por modo de pool), HybridCache, Razor Class Libraries y contenido localizable de
ASP.NET Core.

**Experiencia:** opiniones públicas de usuarios en LowEndTalk, LowEndSpirit, Reddit, Trustpilot, Capterra y
TuQuejaSuma. Material público de Upmind, Paymenter, FOSSBilling, HostBill y Blesta, y de los portales de Stripe,
Lemon Squeezy, Paddle, Hetzner y DigitalOcean. Mercado Pago (pagos automáticos y tarjetas guardadas), BCRA
(Transferencias 3.0) y ARCA (catálogo de web services).

---

## 10. Enmienda del 29/10: navegación clásica y conocida

Quien viene del sistema que usa hoy no tiene que sentir que entró en una nave espacial: un menú con las
opciones más o menos en el mismo lugar, clásico, simple y rápido, para que la mudanza no cueste. Esta sección manda
sobre lo que diga el resto del documento acerca de la navegación y del tono visual. Siguen igual el sistema de
apariencia y plantillas (sección 3), las fichas, la accesibilidad y el tema de fábrica (secciones 4 y 5), los flujos de
la sección 6 y la portada del staff por trabajo pendiente.

### 10.1 Límite legal

La organización por áreas y los nombres genéricos del rubro (clientes, pedidos, facturación, soporte, reportes,
utilidades, configuración) no son de nadie: los usa cualquier sistema de facturación. Lo que no se copia de ningún
producto: textos, íconos, colores, disposición exacta de pantallas, plantillas ni capturas. El mapa de esta sección
sale de cómo trabaja un proveedor de hosting. El lugar de cada opción sigue la organización habitual de los
sistemas de facturación de hosting, para que la mudanza no cueste: se toma solo qué opciones hay y dónde van. No se
usan textos, pantallas ni capturas de otro producto, y los nombres de 10.2 son propios o genéricos del rubro.

### 10.2 Admin

El menú tiene las opciones en los lugares donde las espera quien viene de otro sistema de facturación de hosting,
de izquierda a derecha y de arriba abajo, con nombres propios. Las tablas de esta sección mandan sobre el orden que
traiga `Navigation.cs`.

- **Barra superior fija**, de izquierda a derecha: el logo, que lleva a la portada por trabajo pendiente (6.3), el
  botón **Nuevo**, las siete áreas (Clientes, Pedidos, Facturación, Soporte, Reportes, Utilidades y Complementos) y,
  a la derecha, el indicador de la automatización, el de actualización, **Configuración**, el menú de la persona,
  **Ayuda** y el buscador global (cuentas, facturas, servicios, dominios, tickets, CAE e IP, también con Ctrl+K).
  Sin barra lateral. En pantallas angostas el menú se pliega en un botón.
- **Desplegables:** se abren al pasar o con un clic. Un ítem con submenú lo abre al costado al pasar, con un clic o
  con la tecla de dirección derecha, y en pantallas angostas lo despliega debajo. Cada subítem es la lista de su ítem
  con un filtro en la dirección, así se puede fijar como acceso rápido. Los extras de Hornero van al final de su
  desplegable, después de una línea.
- **Línea de pendientes** debajo de la barra: pedidos pendientes, facturas vencidas, tickets sin responder, tareas
  del staff y emisiones fiscales a revisar, cada uno con su número y enlace a la lista filtrada.

**Menú, en este orden.** En los subítems, la ruta que empieza con `?` es la del ítem de arriba con ese filtro.

| Lugar | Ítem | Ruta | Clave |
|---|---|---|---|
| Nuevo | Cliente | `/admin/cuentas/nueva` | `nuevo.cliente` |
| Nuevo | Pedido | `/admin/pedidos/nuevo` | `nuevo.pedido` |
| Nuevo | Factura | `/admin/facturas/nueva` | `nuevo.factura` |
| Nuevo | Presupuesto | `/admin/presupuestos/nuevo` | `nuevo.presupuesto` |
| Nuevo | Ticket | `/admin/soporte/nuevo` | `nuevo.ticket` |
| Clientes | Ver clientes | `/admin/cuentas` | `clientes.ver` |
| Clientes | Usuarios | `/admin/usuarios` | `clientes.usuarios` |
| Clientes | Agregar cliente | `/admin/cuentas/nueva` | `clientes.agregar` |
| Clientes | Servicios | `/admin/servicios` | `clientes.servicios` |
| Clientes | · un subítem por grupo de productos visible | `?grupo={id}` | `clientes.servicios.grupo-{id}` |
| Clientes | · Otros, los productos sin grupo | `?grupo=otros` | `clientes.servicios.otros` |
| Clientes | Complementos contratados | `/admin/servicios?tipo=complemento` | `clientes.complementos` |
| Clientes | Dominios | `/admin/dominios` | `clientes.dominios` |
| Clientes | Pedidos de baja | `/admin/bajas` | `clientes.bajas` |
| Clientes | Afiliados | `/admin/afiliados` | `clientes.afiliados` |
| Pedidos | Ver pedidos | `/admin/pedidos` | `pedidos.ver` |
| Pedidos | · Pendientes | `?estado=pendiente` | `pedidos.pendientes` |
| Pedidos | · Activos | `?estado=activos` | `pedidos.activos` |
| Pedidos | · En revisión por fraude | `/admin/pedidos/revision` | `pedidos.revision` |
| Pedidos | · Cancelados | `?estado=cancelado` | `pedidos.cancelados` |
| Pedidos | Agregar pedido | `/admin/pedidos/nuevo` | `pedidos.agregar` |
| Facturación | Pagos recibidos | `/admin/pagos` | `facturacion.pagos` |
| Facturación | Facturas | `/admin/facturas` | `facturacion.facturas` |
| Facturación | · Pagadas | `?estado=pagadas` | `facturacion.facturas.pagadas` |
| Facturación | · Borradores | `?estado=borradores` | `facturacion.facturas.borradores` |
| Facturación | · Por pagar | `?estado=por-pagar` | `facturacion.facturas.por-pagar` |
| Facturación | · Vencidas | `?estado=vencidas` | `facturacion.facturas.vencidas` |
| Facturación | · Anuladas | `?estado=anuladas` | `facturacion.facturas.anuladas` |
| Facturación | · Reembolsadas | `?estado=reembolsadas` | `facturacion.facturas.reembolsadas` |
| Facturación | · En gestión de cobro | `?estado=cobranza` | `facturacion.facturas.cobranza` |
| Facturación | · Con pago en curso | `?estado=pago-en-curso` | `facturacion.facturas.pago-en-curso` |
| Facturación | Ítems facturables | `/admin/items` | `facturacion.items` |
| Facturación | · Sin facturar | `?estado=sin-facturar` | `facturacion.items.sin-facturar` |
| Facturación | · Recurrentes | `?estado=recurrentes` | `facturacion.items.recurrentes` |
| Facturación | · Agregar ítem | `/admin/items/nuevo` | `facturacion.items.agregar` |
| Facturación | Presupuestos | `/admin/presupuestos` | `facturacion.presupuestos` |
| Facturación | · Vigentes | `?estado=vigentes` | `facturacion.presupuestos.vigentes` |
| Facturación | · Vencidos | `?estado=vencidos` | `facturacion.presupuestos.vencidos` |
| Facturación | · Crear presupuesto | `/admin/presupuestos/nuevo` | `facturacion.presupuestos.crear` |
| Facturación | Cobros automáticos | `/admin/cobros/automaticos` | `facturacion.cobros` |
| Facturación | Disputas | `/admin/disputas` | `facturacion.disputas` |
| Facturación | Registro de pasarelas | `/admin/registro-modulos?tipo=pasarelas` | `facturacion.registro-pasarelas` |
| Facturación | Notas de crédito (extra) | `/admin/facturas?tipo=nota-de-credito` | `facturacion.notas` |
| Facturación | Comprobantes fiscales (extra) | `/admin/comprobantes` | `facturacion.comprobantes` |
| Facturación | Pagos sin imputar (extra) | `/admin/pagos?estado=sin-imputar` | `facturacion.sin-imputar` |
| Soporte | Resumen | `/admin/soporte/resumen` | `soporte.resumen` |
| Soporte | Tickets | `/admin/soporte` | `soporte.tickets` |
| Soporte | · Asignados a mí | `?asignado=yo` | `soporte.tickets.mios` |
| Soporte | · Todos los activos | `?estado=abiertos` | `soporte.tickets.activos` |
| Soporte | · Nuevos | `?estado=nuevos` | `soporte.tickets.nuevos` |
| Soporte | · Respondidos | `?estado=esperando-al-cliente` | `soporte.tickets.respondidos` |
| Soporte | · Con respuesta del cliente | `?estado=respuesta-del-cliente` | `soporte.tickets.respuesta` |
| Soporte | · En pausa | `?estado=en-pausa` | `soporte.tickets.en-pausa` |
| Soporte | · En curso | `?estado=en-curso` | `soporte.tickets.en-curso` |
| Soporte | · Cerrados | `?estado=cerrados` | `soporte.tickets.cerrados` |
| Soporte | Abrir ticket | `/admin/soporte/nuevo` | `soporte.abrir` |
| Soporte | Respuestas predefinidas | `/admin/soporte/respuestas` | `soporte.respuestas` |
| Soporte | Anuncios | `/admin/anuncios` | `soporte.anuncios` |
| Soporte | Descargas | `/admin/descargas` | `soporte.descargas` |
| Soporte | Base de conocimiento | `/admin/conocimiento` | `soporte.conocimiento` |
| Soporte | Estado de la red | `/admin/estado-red` | `soporte.estado-red` |
| Soporte | · Abiertas | `?vista=abiertas` | `soporte.estado-red.abiertas` |
| Soporte | · Programadas | `?vista=programadas` | `soporte.estado-red.programadas` |
| Soporte | · Resueltas | `?vista=resueltas` | `soporte.estado-red.resueltas` |
| Soporte | · Nueva incidencia | `/admin/estado-red/nueva` | `soporte.estado-red.nueva` |
| Reportes | Todos los reportes | `/admin/reportes` | `reportes.indice` |
| Reportes | Rendimiento diario | `/admin/reportes/actividad-diaria` | `reportes.rendimiento-diario` |
| Reportes | Pronóstico de ingresos | `/admin/reportes/proyeccion` | `reportes.pronostico` |
| Reportes | Ingresos por mes | `/admin/reportes/ingresos` | `reportes.ingresos` |
| Reportes | Altas de clientes | `/admin/reportes/altas-clientes` | `reportes.altas-clientes` |
| Reportes | Satisfacción de tickets | `/admin/reportes/satisfaccion` | `reportes.satisfaccion` |
| Reportes | Carpeta de PDFs | `/admin/reportes/pdf-facturas` | `reportes.carpeta-pdf` |
| Reportes | Libro IVA Ventas (extra) | `/admin/reportes/libro-iva` | `reportes.libro-iva` |
| Reportes | Pie: Más reportes | `/admin/reportes` | `reportes.todos` |
| Utilidades | Actualizaciones | `/admin/actualizaciones` | `utilidades.actualizaciones` |
| Utilidades | Tareas automáticas | `/admin/tareas-automaticas` | `utilidades.tareas` |
| Utilidades | Cola de trabajos | `/admin/cola` | `utilidades.cola` |
| Utilidades | Sincronizar precios de dominios | `/admin/dominios/precios#importar` | `utilidades.precios-dominios` |
| Utilidades | Campañas de correo | `/admin/correo-masivo?tipo=campanas` | `utilidades.campanas` |
| Utilidades | Correo masivo | `/admin/correo-masivo` | `utilidades.correo-masivo` |
| Utilidades | Seguimiento de enlaces | `/admin/enlaces` | `utilidades.enlaces` |
| Utilidades | Calendario | `/admin/calendario` | `utilidades.calendario` |
| Utilidades | Tareas del staff | `/admin/tareas` | `utilidades.tareas-staff` |
| Utilidades | Consulta de dominios | `/admin/dominios/consulta` | `utilidades.whois` |
| Utilidades | Resolución de dominios | `/admin/dominios/resolucion` | `utilidades.resolucion` |
| Utilidades | Códigos para el sitio | `/admin/codigos` | `utilidades.codigos` |
| Utilidades | Sistema, que solo agrupa | | `utilidades.sistema` |
| Utilidades | · Estado de la base | `/admin/sistema/base` | `utilidades.sistema.base` |
| Utilidades | · Limpieza | `/admin/sistema/limpieza` | `utilidades.sistema.limpieza` |
| Utilidades | · Información del entorno | `/admin/sistema/entorno` | `utilidades.sistema.entorno` |
| Utilidades | · Compatibilidad | `/admin/actualizaciones#compatibilidad` | `utilidades.sistema.compatibilidad` |
| Utilidades | Conciliación (extra) | `/admin/conciliacion` | `utilidades.conciliacion` |
| Complementos | Extensiones instaladas | `/admin/complementos/extensiones` | `complementos.extensiones` |
| Complementos | Temas | `/admin/apariencia/paquetes` | `complementos.temas` |
| Complementos | Facturación electrónica ARCA | `/admin/fiscal` | `complementos.arca` |
| Complementos | Actualización masiva de precios | `/admin/catalogo/actualizar-precios` | `complementos.precios` |
| Complementos | Códigos de seguimiento | `/admin/apariencia/seguimiento` | `complementos.seguimiento` |
| Complementos | Mercado Pago | `/admin/cobros?modulo=mercadopago` | `complementos.mercadopago` |
| Complementos | Consulta de cobros de pasarelas | `/admin/conciliacion?vista=pasarelas` | `complementos.consulta-cobros` |
| Derecha | Indicador de la automatización | `/admin/tareas-automaticas` | `barra.automatizacion` |
| Derecha | Indicador de actualización | `/admin/actualizaciones` | `barra.actualizacion` |
| Configuración | Todos los ajustes | `/admin/configuracion` | `configuracion.todas` |
| Configuración | Aplicaciones e integraciones | `/admin/configuracion/integraciones` | `configuracion.integraciones` |
| Configuración | Staff | `/admin/staff` | `configuracion.staff` |
| Configuración | Estado y actualizaciones | `/admin/estado` | `configuracion.estado` |
| Configuración | Primeros pasos | `/admin/configuracion/primeros-pasos` | `configuracion.primeros-pasos` |
| Configuración | Registros del sistema | `/admin/registros` | `configuracion.registros` |
| Persona | Mi cuenta | `/admin/mi-cuenta` | `cuenta.mi-cuenta` |
| Persona | Mis notas | `/admin/mi-cuenta/notas` | `cuenta.notas` |
| Persona | Ir al portal | `/` | `cuenta.portal` |
| Persona | Salir | formulario a `/admin/salir` | `cuenta.salir` |
| Ayuda | Documentación | enlace configurable, o `/admin/ayuda` si no hay | `ayuda.documentacion` |
| Ayuda | Soporte del producto | enlace configurable | `ayuda.soporte` |
| Ayuda | Comunidad | enlace configurable | `ayuda.comunidad` |
| Ayuda | Novedades de la versión | `/admin/ayuda/novedades` | `ayuda.novedades` |
| Ayuda | Licencia | `/admin/ayuda/licencia` | `ayuda.licencia` |
| Derecha | Buscador global y búsqueda avanzada | `/admin/buscar` | `barra.buscar` |

- Cada área cuelga sus pantallas de este mapa con la ruta de la tabla. Si su ruta es otra, se cambia la ruta del
  ítem, nunca su lugar. Un ítem cuya función todavía no existe no se muestra, y uno que solo agrupa un submenú se
  muestra si alguno de sus subítems se ve.
- Las claves de esta tabla reemplazan a las anteriores porque el menú por rol todavía no se publicó. Una vez
  publicadas no cambian.
- Una extensión instalada con pantalla propia suma su ítem al final de Complementos.
- Respecto del mapa del 29/10: la portada pasa al logo, Afiliados a Clientes, Grupos de clientes a Todos los
  ajustes, Correo masivo a Utilidades, Registro de actividad, Correos enviados y Registro de módulos a Registros del
  sistema, Actualizaciones a Utilidades y a Estado y actualizaciones, y Configuración y Ayuda a la derecha.

**Todos los ajustes** (`/admin/configuracion`) muestra arriba Primeros pasos mientras falte algún paso y después
todas las páginas de ajustes en este orden, con un filtro por categoría: Todas, Sistema, Aplicaciones e
integraciones, Usuarios y staff, Productos y servicios, Soporte y Seguridad y API. Muestra solo las páginas que
existen y la persona puede abrir.

| Orden | Página | Categoría | Ruta |
|---|---|---|---|
| 1 | General | Sistema | `/admin/configuracion/general` |
| 2 | Aplicaciones e integraciones | Aplicaciones e integraciones | `/admin/configuracion/integraciones` |
| 3 | Automatización | Sistema | `/admin/automatizacion` |
| 4 | Productos y servicios | Productos y servicios | `/admin/catalogo` |
| 5 | Opciones configurables | Productos y servicios | `/admin/catalogo#opciones` |
| 6 | Complementos de productos | Productos y servicios | `/admin/catalogo#complementos` |
| 7 | Paquetes | Productos y servicios | `/admin/catalogo#paquetes` |
| 8 | Promociones y cupones | Productos y servicios | `/admin/catalogo/cupones` |
| 9 | Dominios y precios por extensión | Productos y servicios | `/admin/dominios/precios` |
| 10 | Servidores | Productos y servicios | `/admin/servidores` |
| 11 | Extensiones | Aplicaciones e integraciones | `/admin/complementos/extensiones` |
| 11 bis | Ajustes de soporte (extra) | Soporte | `/admin/soporte/ajustes` |
| 12 | Departamentos de soporte | Soporte | `/admin/soporte/departamentos` |
| 13 | Reglas de escalamiento | Soporte | `/admin/soporte/escalamiento` |
| 14 | Control de spam | Soporte | `/admin/soporte/correo` |
| 15 | Plantillas de correo | Sistema | `/admin/apariencia/correos` |
| 15 bis | Apariencia (extra) | Sistema | `/admin/apariencia` |
| 16 | Grupos de clientes | Usuarios y staff | `/admin/grupos` |
| 17 | Campos personalizados | Usuarios y staff | `/admin/campos` |
| 18 | Estados de pedido | Productos y servicios | no se muestra: el flujo es fijo y sus nombres se cambian en Apariencia, Textos |
| 19 | IPs bloqueadas | Seguridad y API | `/admin/seguridad/bloqueos` |
| 20 | Correos bloqueados | Seguridad y API | `/admin/seguridad/bloqueos#correos` |
| 21 | Ingreso con proveedores externos | Aplicaciones e integraciones | `/admin/seguridad/proveedores` |
| 22 | Avisos al equipo | Aplicaciones e integraciones | `/admin/configuracion/avisos` |
| 23 | Pasarelas de pago | Aplicaciones e integraciones | `/admin/cobros` |
| 24 | Registradores | Aplicaciones e integraciones | `/admin/registradores` |
| 25 | Monedas | Sistema | `/admin/monedas` |
| 26 | Impuestos | Sistema | `/admin/impuestos` |
| 26 bis | ARCA (extra) | Sistema | `/admin/fiscal` |
| 27 | Protección antifraude | Seguridad y API | `/admin/seguridad/ajustes#fraude` |
| 28 | Staff | Usuarios y staff | `/admin/staff` |
| 29 | Roles y permisos | Usuarios y staff | `/admin/staff/roles` |
| 29 bis | Menú por rol (extra) | Usuarios y staff | `/admin/staff/menu` |
| 30 | Segundo factor | Seguridad y API | `/admin/seguridad/ajustes#segundo-factor` |
| 31 | Estados de tickets | Soporte | no se muestra: son fijos |
| 32 | Preguntas de seguridad | Seguridad y API | no se construye: el segundo factor protege mejor |
| 33 | Llaves de la API y webhooks | Seguridad y API | `/admin/configuracion/webhooks` |
| 34 | Aplicaciones vinculadas | Seguridad y API | solo si se confirma que alguna se usa |
| 35 | Ingreso único para terceros | Seguridad y API | solo si se confirma que alguna se usa |
| 36 | Almacenamiento | Sistema | `/admin/sistema/almacenamiento` |
| 37 | Copias de la base | Sistema | `/admin/sistema/copias` |

- **General** tiene doce solapas, en este orden: General, Localización, Pedidos, Dominios, Correo, Soporte,
  Facturas, Crédito, Afiliados, Seguridad, Social y Otros. Afiliados y Seguridad enlazan además a
  `/admin/afiliados/programa` y a `/admin/seguridad/ajustes`, donde viven esos ajustes.
- **Automatización** tiene una sección por regla del ciclo diario: hora, suspensión y reactivación, baja por mora,
  facturas, recordatorios, recargo, anulación de impagas, cobro automático, cotizaciones, avisos y sincronización de
  dominios, tickets inactivos, limpieza, bajas pedidas, estadísticas de uso y poda de registros.
- **Registros del sistema** (`/admin/registros`) tiene solapas Actividad (`/admin/registro`), Ingresos
  (`/admin/seguridad/ingresos`), Correos enviados (`/admin/correos`), Módulos y pasarelas (`/admin/registro-modulos`),
  Correo entrante (`/admin/registros/correo-entrante`) y Consultas de dominios.

**Fichas.**

- **Ficha del cliente con catorce solapas:** Resumen, Perfil, Usuarios, Contactos, Servicios, Dominios, Ítems,
  Facturas, Presupuestos, Pagos, Tickets, Correos, Notas y Registro. El resumen muestra saldo, deuda, servicios
  activos y lo pendiente. Las demás solapas son páginas propias con la cabecera de la ficha arriba:
  `/admin/cuentas/{id}/usuarios`, `/contactos`, `/dominios`, `/items`, `/presupuestos`, `/tickets` y `/notas`.
- **Ficha del servicio** (`/admin/servicios/{id}`), **del pedido** (`/admin/pedidos/{id}`), **del dominio**
  (`/admin/dominios/{id}`), **de la factura** (`/admin/facturas/{id}`) y **del ticket** (`/admin/soporte/{id}`), cada
  una con la cabecera de su cliente y enlazada desde toda fila que la nombre.
- **Listas:** tablas densas con filas de 32 a 36 px, filtros arriba, orden por columna, paginado, acciones masivas
  con confirmación y exportación CSV.
- **Rápido:** páginas del servidor con htmx, sin pantallas de carga ni animaciones salvo el foco, con las metas de
  la sección 6.2.
- **Personalizable:** cada staff fija sus accesos rápidos, y el proveedor reordena u oculta ítems del menú por rol
  desde Configuración.

### 10.3 Portal del cliente

- Barra superior con el logo, el menú horizontal y a la derecha el carrito y la cuenta.
- Menú: Inicio, Servicios (mis servicios, contratar), Dominios (mis dominios, registrar, transferir), Facturación
  (facturas, presupuestos, medios de pago, cargar saldo), Soporte (tickets, abrir ticket, base de conocimiento,
  anuncios, descargas, estado de la red), Afiliados si está prendido, y Cuenta (perfil, contactos y usuarios,
  seguridad, correos recibidos, salir).
- El inicio sigue respondiendo "¿tengo que hacer algo?" (6.3), con paneles simples en vez de tarjetas grandes.

### 10.4 Tono visual

- Clásico y sobrio: fondo claro, tipografía del sistema de 14 px en el admin y 15 px en el portal, bordes finos,
  sin sombras grandes ni degradados, íconos solo donde ayudan a encontrar algo.
- El tema de fábrica (Hornero, sección 5.1) toma este tono. Un tema de marca lo viste sin cambiar la
  estructura. Los temas cambian colores, logo, tipografía y textos, nunca el lugar de las cosas.
