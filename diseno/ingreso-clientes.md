# Registro e ingreso de los clientes

**Estado:** v0, 2026-11-01 · **Parte de:** [diseño del núcleo](README.md) · **Decisiones:** D-09 y D-10

Este documento cubre cómo un cliente se crea su propia cuenta en el portal y cómo entra con Google u otro proveedor
OpenID Connect. Muchos clientes entran hoy con Google al sistema que usan: sin esto, el día del corte tendrían que
restablecer la contraseña. Es diseño propio: sale de D-09 y D-10, de las especificaciones públicas de
OpenID Connect Core 1.0, OAuth 2.0 (RFC 6749) y PKCE (RFC 7636), de la documentación pública de Google para OpenID
Connect y de la de ASP.NET Core. No se usó nada de WHMCS. De los datos del sistema anterior se leyó solo la estructura
de las tablas de vínculos.

---

## 1. Principios

- **Nunca se vincula solo.** Que un proveedor diga que alguien tiene el correo de un usuario no alcanza para entrar a
  su cuenta: el vínculo se confirma con la contraseña o con un enlace a ese correo, y solo con un correo que el
  proveedor verificó.
- **El segundo factor propio sigue valiendo.** Entrar con Google no saltea el TOTP de quien lo tiene activo ni el
  obligatorio por rol.
- **Varios nodos.** Los proveedores y los vínculos están en PostgreSQL, el viaje al proveedor va en una cookie
  protegida con el anillo de llaves de la base y el tope de altas por IP se cuenta en la base con un lock. Un cambio en
  el panel vale enseguida en todos los nodos, sin reiniciar.
- **Lo legal no se bloquea.** Los botones de baja y de arrepentimiento siguen sin pasar por nada de esto.
- **Secretos cifrados.** El secreto de cada proveedor se cifra por campo (D-09), atado a su fila y a la dirección del
  proveedor, y nunca vuelve al navegador ni a los logs.

---

## 2. Registro

### 2.1 Flujo

"Crear cuenta" está en `/registro` y se llega desde el ingreso del portal. Un pedido que pide iniciar sesión lleva su
dirección en `ReturnUrl`, que pasa del ingreso al registro.

1. La persona elige Google (u otro proveedor prendido) o completa nombre, empresa y teléfono opcionales, correo,
   contraseña con su confirmación y, si el catálogo vende en varias monedas, la moneda de la cuenta.
2. En una sola transacción se crean el usuario, la cuenta con su correo e idioma y el vínculo de titular. Con un
   proveedor, también el vínculo con él (sección 5). Si algo falla, no queda nada a medias.
3. Con contraseña, sale el enlace para verificar el correo con la plantilla `email-verification` (sección 5 de
   [fraude y acceso](fraude-y-acceso.md)). Con un proveedor, el correo queda verificado porque lo verificó él.
4. El alta es el primer ingreso: queda en el historial y la sesión abre enseguida.
5. Sigue a los datos fiscales de la cuenta nueva (`/cuenta/datos-fiscales`, de impuestos-arca) con el formulario
   abierto. Guardarlos, o "Seguir" sin cargarlos, lleva al pedido que la trajo. Sin datos fiscales se factura como
   consumidor final.

El idioma de la cuenta y del usuario es el de la interfaz en la que se registró. El nombre va a la cuenta y
también al usuario.

### 2.2 Ajustes

En los ajustes de seguridad (`identity.security_settings`, sección 2 de [fraude y acceso](fraude-y-acceso.md)), con
la versión que evita pisar a otro:

| Ajuste | Por defecto | Qué hace |
|---|---|---|
| `RegistrationEnabled` | Sí | El portal deja crear cuentas. Apagado, no se ve el enlace ni se puede crear, ni con un proveedor |
| `RegistrationIpLimit` y `RegistrationIpHours` | 5 en 24 horas | Altas desde una IP en la ventana |
| `RegistrationCurrency` | ARS | La moneda que viene elegida. Se ofrecen además las de los precios del catálogo |

Se editan en `/admin/seguridad/ajustes#registro`. En el menú de 10.2 el ajuste va en Configuración, General, solapa
Otros: esa página enlaza a la sección del registro, como la solapa Seguridad enlaza a
los ajustes de seguridad.

### 2.3 Frenos

- **Listas de bloqueo:** una IP o red de la lista no puede registrarse, y un correo o su dominio en la lista tampoco.
- **Tope por IP:** se cuentan las altas de la auditoría (`portal.registered`) desde esa IP en la ventana. En IPv6 se
  cuenta la red `/64`, como el límite de intentos. El conteo va dentro de la transacción del alta con un lock de la
  red (`pg_advisory_xact_lock`), así dos altas a la vez desde la misma red no pasan el tope en ningún nodo. Las redes
  de confianza no tienen tope.
- **Tope de formularios:** el registro está en el tope por IP de los formularios de ingreso.
- **Política de contraseñas:** la de los ajustes, la misma del cambio y de la recuperación.
- **Correo repetido:** si ya hay un usuario o una cuenta con ese correo, sin distinguir mayúsculas, no se crea y se
  ofrece ingresar o recuperar la contraseña. El índice único de cada tabla decide si dos altas llegan a la vez. Una
  cuenta sin usuario (cargada por el staff o importada) también frena: el acceso se lo da el staff.

Detrás de un proxy, la IP del cliente sale de `ForwardedHeaders` (sección de identidad de
[docs/interfaz](../docs/interfaz/README.md)). Si el proxy no está declarado, el tope cuenta la IP del proxy y frena el
registro de todos a la quinta alta del día.

Decir que un correo ya tiene cuenta deja saber quién es cliente. Se acepta, como en el sistema actual, porque el
registro sin eso obliga a salir del pedido a buscar un correo. Lo acotan el tope por IP y el de los formularios.

---

## 3. Proveedores externos

### 3.1 Tabla

`identity.external_providers`, una fila por proveedor:

| Columna | Qué es |
|---|---|
| `key` | Clave en la dirección de vuelta (`/ingresar/externo/{key}/vuelta`). Google es `google`. Minúsculas, números y guiones |
| `kind` | `Google` u `Oidc` |
| `name` | El nombre del botón: "Continuar con Google" |
| `authority` | El emisor OpenID Connect, de donde sale `.well-known/openid-configuration`. Google tiene la suya fija |
| `client_id` | El ID del cliente que da el proveedor |
| `client_secret` | Cifrado con AES-256-GCM (D-09), atado a la tabla, la columna, la fila y el emisor |
| `enabled` | Muestra el botón. Solo se prende con el cliente y el secreto cargados |
| `version` | Para que dos personas del staff no se pisen |

Cambiar el emisor obliga a cargar el secreto de nuevo: el cifrado está atado a él, así nadie con acceso a la base
puede mandar el secreto a otro servidor cambiando solo la dirección.

### 3.2 Pantalla

"Ingreso con proveedores externos" (`/admin/seguridad/proveedores`, permiso `security.manage`) es la página 21 de
Todos los ajustes, en Aplicaciones e integraciones. Google aparece primero aunque no esté cargado, y abajo se agrega
otro proveedor OpenID Connect con su clave y su emisor. Cada uno muestra su estado, la dirección de vuelta que hay
que registrar en el proveedor (armada con `Billing:PublicUrl`), cuántos usuarios tiene vinculados y "Probar la
conexión". El secreto se escribe y nunca se muestra: vacío deja el que estaba. Cada cambio queda en la auditoría
(`security.external_provider`) con los nombres de lo que cambió, nunca con los valores.

"Probar la conexión" lee la configuración del proveedor y canjea un código inventado. Si el proveedor contesta que el
código no vale (`invalid_grant`), reconoció el cliente y el secreto. Si contesta `invalid_client`, están mal.

Un proveedor que no es Google se lee por HTTPS desde la dirección que carga el staff, sin seguir redirecciones, y la
pantalla solo dice si anduvo: no muestra lo que contestó. Igual, cargar uno es poder hacer que el servidor llame a esa
dirección, y por eso pide `security.manage`.

---

## 4. Ingreso con un proveedor

### 4.1 La ida

El botón "Continuar con Google" del ingreso, del registro o de la seguridad del cliente manda un formulario con
antiforgery. Una IP de la lista de bloqueo no sale. El núcleo lee la configuración del proveedor por HTTPS, sin
seguir redirecciones, y la guarda en memoria del nodo con el refresco de `ConfigurationManager` (es un caché de datos
públicos). Guarda en la cookie del viaje (`bo_portal_oidc`, esquema `portal.oidc`, Lax, 10 minutos, solo bajo
`/ingresar/externo`) un `state`, un `nonce` y el verificador de PKCE, de 32 bytes al azar cada uno, con el propósito
y la dirección de vuelta ya saneada. Manda al proveedor con `response_type=code`, `scope=openid email profile`, el
`code_challenge` S256 y, en Google, `prompt=select_account`.

### 4.2 La vuelta

`/ingresar/externo/{key}/vuelta` lee y borra la cookie del viaje. El `state` tiene que ser el de la cookie: una vuelta
en otro navegador, o con un `state` ajeno, no sigue (así nadie hace entrar a otro a su cuenta). Después canjea el
código con el secreto y el verificador, y valida el ID token:

- Firma asimétrica con una llave del proveedor. Las firmadas con el secreto del cliente no valen. Si la llave no está,
  se vuelve a leer la configuración una vez, por si rotó.
- Emisor igual al de la configuración. En Google vale además sin el esquema.
- Destinatario igual al ID del cliente, y `azp` igual si viene.
- Vencimiento, con dos minutos de tolerancia.
- `nonce` igual al de la cookie.
- `sub` presente, de hasta 255 caracteres.

El correo cuenta como verificado solo si el token trae `email_verified` en verdadero. Lo validado va a la cookie
externa (`bo_portal_ext`, esquema `portal.external`, Lax, 10 minutos) y la página sigue a `/ingresar/externo` con una
navegación desde el propio sitio: la vuelta llega desde el proveedor y en esa cadena no viajaría la cookie Strict del
paso del segundo factor.

### 4.3 Qué sigue

`/ingresar/externo` decide con la cookie externa, que sirve una sola vez:

| Caso | Qué pasa |
|---|---|
| Esa persona del proveedor ya está vinculada | Entra. Con el segundo factor activo pasa al paso del código, como con contraseña. Un usuario deshabilitado no entra |
| Correo sin verificar por el proveedor | No vincula ni registra |
| Hay un usuario con ese correo | Pide confirmar con la contraseña o con un enlace al correo (sección 5.2) |
| No hay usuario y el registro está abierto | Completa el alta con el nombre que dio el proveedor (sección 2) |
| No hay usuario y el registro está cerrado | Se lo dice, sin crear nada |

Cada intento queda en el historial de ingresos con el método `External` (o `Totp` si termina con el código) y un
ingreso desde una red nueva manda el aviso por correo, igual que con la contraseña. Una IP de la lista no pasa en
ningún paso, y si el staff apaga el proveedor mientras la persona decide, no sigue. El bloqueo por intentos de
contraseña no frena el ingreso con un proveedor vinculado, que no adivina nada, pero el paso del código sí lo mira, y
entrar reinicia el contador como con la contraseña.

---

## 5. Vínculos

### 5.1 Tabla

`identity.external_logins`: proveedor, identificador de la persona en el proveedor (`subject`, el `sub`), usuario,
el correo que tenía en el proveedor al vincularse, alta y último uso. Una persona del proveedor está vinculada a un
solo usuario (índice único por proveedor y `subject`). Un usuario puede tener varias, también del mismo proveedor,
por ejemplo dos cuentas de Google.

### 5.2 Confirmar

- **Con la contraseña:** la del usuario con ese correo, o la del usuario de la sesión si vincula desde su seguridad.
  Pasa por las listas, el bloqueo por intentos y el historial como un ingreso: una contraseña mala cuenta como falla.
- **Con un enlace al correo:** sirve también para quien no tiene contraseña. El enlace (`external-link`, en es-AR y
  en) lleva el usuario, el proveedor, el `sub` y el sello del usuario, protegido con Data Protection y con una hora de
  vida. Abrirlo muestra un botón y solo el botón vincula, así un lector de correo que abre los enlaces no vincula
  nada. No abre sesión: después se ingresa con el proveedor. Cambiar la contraseña lo invalida.

El vínculo queda en la auditoría (`portal.external_linked`, con cómo se confirmó). Si dos pedidos vinculan a la vez la
misma persona del proveedor a usuarios distintos, el índice único deja uno solo.

### 5.3 Desde la seguridad del cliente

`/cuenta/seguridad` lista los vínculos, deja vincular otro proveedor prendido (va al proveedor y vuelve a confirmar con
la contraseña o el correo) y quitar uno (`portal.external_unlinked`). Quien no tiene contraseña no puede quitar el
único vínculo: primero elige una con el enlace de recuperar la contraseña. "Ver como cliente" no vincula ni desvincula.

### 5.4 Desde el admin

La seguridad de la ficha (`/admin/cuentas/{id}/seguridad`) muestra los vínculos de cada usuario. Con
`security.manage` se quita uno (`security.external_unlinked`), por ejemplo si el cliente perdió esa cuenta.

---

## 6. Importación

El importador trae los vínculos de Google del sistema actual, y algunos usuarios tienen dos. Cada fila del proveedor
de Google entra
el correo de su metadata y el usuario con su mismo ID (D-17). Un vínculo sin su usuario no entra y queda en los
incidentes: ese cliente confirma una vez con su contraseña o con el correo. El proveedor queda cargado y apagado, con el
ID del cliente si tiene la forma de uno de Google. El secreto se carga en el panel.

Google identifica a cada persona con el mismo `sub` para todos los clientes OAuth de un proyecto. Usando el cliente que
ya tiene el proveedor, con la dirección de vuelta nueva agregada, los vínculos importados sirven sin que el cliente haga
nada. Los pasos están en la [guía](../docs/ingreso-clientes/README.md).

---

## 7. Desafío anti robots

Opcional por formulario, apagado de fábrica. Se elige en los ajustes de seguridad (`/admin/seguridad/ajustes#desafio`):

| Ajuste | Por defecto | Qué hace |
|---|---|---|
| `ChallengeProvider` | `none` | `pow`, la prueba de trabajo propia, o `turnstile`, el de Cloudflare |
| `ChallengeForms` | Ninguno | `login`, `forgot`, `register`, `contact` y `ticket` |
| `ChallengeBits` | 16 | El umbral de la prueba de trabajo, de 8 a 24 bits |
| `ChallengeSiteKey` y `ChallengeSecret` | Vacías | Las claves de Turnstile. La secreta se cifra por campo (D-09), atada a la fila de los ajustes y a la dirección de validación, y nunca vuelve a la pantalla: vacía deja la que estaba |

**Prueba de trabajo propia.** Invisible y sin ningún servicio de afuera. La página lleva un desafío firmado con Data
Protection, atado al formulario y con 15 minutos de vida. Antes de mandar el formulario, `wwwroot/js/challenge.js`
busca un número que, con el desafío, dé un SHA-256 con tantos bits en cero al principio como el umbral: con 16 tarda
menos de un segundo en un teléfono, y cada bit duplica el trabajo. El servidor comprueba la firma, el formulario, el
vencimiento y el hash, y anota el desafío en `identity.challenge_uses`: cada uno sirve una sola vez en cualquier nodo, y
la retención borra los vencidos.

**Turnstile.** Invisible o de un clic, según lo configure el proveedor en Cloudflare. La página carga su script y solo
esas páginas suman su origen a la política de contenido. El servidor valida el token en Cloudflare con la clave secreta
y la IP del cliente. Si Cloudflare no contesta, el formulario no pasa.

El ingreso, recuperar la contraseña y el registro lo piden si están en la lista. El contacto y el ticket sin cuenta
llaman a `ChallengeGuard.For` para mostrarlo y a `ChallengeGuard.Verify` al recibirlo, con `contact` y
`ticket`. Los botones de baja y de arrepentimiento nunca lo piden, aunque estén todos los formularios en la lista. El
tope por IP, las listas y el bloqueo por intentos siguen valiendo con desafío o sin él.

---

## 8. Pantallas y menú

| Ruta | Quién | Qué hace |
|---|---|---|
| `/registro` | Cualquiera | Crear cuenta con correo y contraseña o con un proveedor |
| `/ingresar` | Cualquiera | Suma los botones de los proveedores y el enlace al registro |
| `/ingresar/externo/{key}/vuelta` | Cualquiera | La vuelta del proveedor |
| `/ingresar/externo` | Quien volvió del proveedor | Entrar, confirmar el vínculo o completar el alta |
| `/ingresar/externo/confirmar` | Cualquiera con el enlace | Confirma el vínculo con el enlace del correo |
| `/cuenta/seguridad` | Cliente | Suma los vínculos, vincular y quitar |
| `/cuenta/datos-fiscales` | Cliente | Con `volver`, el paso del alta que sigue al pedido |
| `/admin/seguridad/proveedores` | `security.manage` | Los proveedores, con su secreto cifrado y la prueba |
| `/admin/seguridad/ajustes#registro` y `#desafio` | `security.manage` | Los ajustes del registro y del desafío anti robots |
| `/admin/cuentas/{id}/seguridad` | `accounts.read` | Suma los vínculos de cada usuario. Quitarlos pide `security.manage` |

En el menú de la sección 10.2 de [interfaz-y-temas.md](interfaz-y-temas.md), sin reordenarlo: los proveedores son la
página 21 de Todos los ajustes (Aplicaciones e integraciones, `/admin/seguridad/proveedores`) y el registro va en
General, solapa Otros. En el portal, "Crear cuenta" cuelga del ingreso y los vínculos de Cuenta, Seguridad. Mientras
no esté el menú de 10.2, la seguridad propia del staff enlaza a los proveedores.

---

## 9. Pruebas

Todo corre contra un proveedor falso en proceso, que hace también de Google sin salir a la red: atiende la
configuración, las llaves, la autorización con PKCE y el canje del código, y firma los ID tokens. Las pruebas cubren la
ida y la vuelta, el `state` de otro navegador, tokens con otra llave, otro `nonce`, otro destinatario o vencidos, el
registro con y sin proveedor, el vínculo con la contraseña y con el correo, el segundo factor, la pantalla del staff,
la importación y el desafío anti robots, con la prueba de trabajo de un solo uso y Turnstile contra un validador falso. La prueba contra Google real (`GoogleLiveTests`) prueba la conexión con un cliente OAuth de prueba y
corre solo con `BILLINGOSS_GOOGLE_CLIENT_ID` y `BILLINGOSS_GOOGLE_CLIENT_SECRET`: sin ellas queda inconclusa.

---

## 10. Fuera de v0

- Passkeys, que la API de Identity de .NET 10 ya trae: van con su propia pantalla.
- Ingresar con el proveedor del staff (por ejemplo Authentik) para clientes: se carga como un proveedor más si hace falta.
- Pedir los datos fiscales dentro del mismo formulario del alta: hoy son el paso siguiente, con "Completar con ARCA".
- Aceptar términos y condiciones con su constancia en el alta, si el proveedor los publica.

Lo que no se construye: que Hornero sea el proveedor de ingreso de otras aplicaciones (ingreso único para terceros
y aplicaciones vinculadas). No hace falta mientras ninguna aplicación entre con las cuentas de los clientes.
