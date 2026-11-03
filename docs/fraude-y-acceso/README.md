# Fraude y seguridad de acceso

**Diseño:** [fraude-y-acceso.md](../../diseno/fraude-y-acceso.md) · **Decisiones:** D-08 y D-10

Esta guía dice cómo se usa lo que cuida los pedidos y los ingresos: la evaluación de fraude con un módulo, la cola de
revisión, las listas de bloqueo, el historial de ingresos, los límites de intentos, la política de contraseñas, el
correo verificado y el segundo factor. El modelo, los estados y las reglas están en el diseño.

Todo vive en PostgreSQL: vale igual con un nodo que con varios. Los ajustes se cambian desde el admin, sin reiniciar.

| Pieza | Dónde | Permiso |
|---|---|---|
| Ajustes | `/admin/seguridad/ajustes` | `security.manage` |
| Listas de bloqueo | `/admin/seguridad/bloqueos` y `/api/v1/security/blocklist` | `security.manage`, o `security:read` y `security:write` en la API |
| Historial de ingresos | `/admin/seguridad/ingresos`, y por cuenta en `/admin/cuentas/{id}/seguridad` | `security.read` |
| Revisión de pedidos | `/admin/pedidos/revision` y `/api/v1/orders/review` | `orders.review`, o `orders:review` en la API |
| Seguridad propia | `/cuenta/seguridad` (cliente) y `/admin/seguridad` (staff) | La sesión |

En el menú del admin, la revisión va en Pedidos, los ajustes y las listas en Configuración (Seguridad y fraude) y el
historial en Utilidades. El mapa completo está en la sección 11 del diseño.

---

## Evaluación de fraude

### Prenderla

1. Desplegar un módulo del tipo `fraud`, por ejemplo [`minfraud`](../../src/modules/BillingOss.Module.MinFraud/README.md),
   con su secreto de firma.
2. Cargar la instalación y una instancia con sus credenciales, como cualquier otro módulo (`ModuleRegistry.Install` y
   `AddInstance`). En `minfraud` la instancia lleva `account_id` y la `license_key` como secreto.
3. En los ajustes, prender "Evaluar los pedidos con el módulo de fraude", elegir la instancia y el puntaje desde el que
   un pedido va a revisión. Solo aparecen las instancias de módulos que declaran el tipo `fraud`.
4. Si conviene ahorrar consultas, prender "No mandar al módulo los pedidos de cuentas que ya tienen un servicio
   activo". Las listas de bloqueo y el correo verificado se miran igual.

### Qué pedidos se evalúan

- Los que hace un cliente en el portal: se toma su IP y su usuario.
- Los de la API propia que mandan `client_ip` en `POST /api/v1/orders`.
- Los que carga el staff o una integración sin IP no se evalúan. La API compatible tampoco: `AddOrder` no manda la IP.

Antes del módulo corren las reglas propias, sin red: si la IP, el correo de la cuenta o el del usuario, o su dominio,
están en una lista de bloqueo, o si el ajuste de correo verificado está prendido y el usuario no lo verificó, el
pedido va directo a revisión.

### Retención y revisión

Un pedido pendiente de evaluación o en revisión está retenido: sus servicios no se aprovisionan aunque la factura se
pague. La factura se emite igual y se puede pagar.

- **Aprobar** aprovisiona lo que ya está pago, o todo si el pedido era de total cero. Lo que falta pagar se aprovisiona
  cuando se pague, como siempre. Si el pedido salió en una factura por modo de carga, cada servicio sigue a la suya.
- **Rechazar** cancela el pedido y sus servicios pendientes. Cada factura sin nada pagado se anula con una nota de
  crédito. Si una tiene algo pagado, queda como está y la pantalla lo avisa: el staff decide si lo devuelve con un
  reembolso o lo deja a favor del cliente. No se pasa solo a saldo a favor porque esa plata puede ser de una tarjeta
  robada.
- Se puede decidir sin esperar al módulo. Lo que el módulo devuelva después queda anotado y no cambia la decisión.
- La aceptación manual (`AcceptOrder` de la API compatible) no acepta un pedido retenido: devuelve el error y el
  pedido se decide en la revisión.

Cada decisión queda en la auditoría (`fraud.approved` y `fraud.rejected`), igual que el resultado del módulo
(`fraud.screened`) y la revisión por reglas propias (`fraud.review`).

### Si el módulo falla

Un error reintentable (el servicio no responde, límite de pedidos) vuelve a la cola con espera creciente, hasta 5
intentos. Un error definitivo (credenciales rechazadas, sin fondos) o el último intento aplican el ajuste "Si el
módulo falla, mandar el pedido a revisión": prendido (por defecto) el pedido va a revisión con el motivo
`fraud.module_error`, y apagado pasa. El trabajo es `fraud.screen`.

---

## Listas de bloqueo

| Tipo | Valor | Qué frena |
|---|---|---|
| IP o red | `203.0.113.7` o `203.0.113.0/24` (IPv4 o IPv6, de /8 para abajo) | El ingreso al portal y al admin, también el paso del código. Sus pedidos van a revisión |
| Correo | `alguien@ejemplo.com` | El enlace para recuperar la contraseña. Sus pedidos van a revisión |
| Dominio | `ejemplo.com` | Lo mismo que el correo, para todo el dominio |

Cada entrada lleva un motivo y puede vencer. Las vencidas no cuentan y se borran a los 30 días. Los botones de baja y
de arrepentimiento nunca pasan por las listas: la Disposición 954/2025 exige que estén al alcance sin obstáculos.

La lista de IP también frena el ingreso al admin. Por eso la pantalla no deja cargar una red que incluye la IP de quien
la carga. Si igual queda todo el staff afuera (por ejemplo, por una entrada de la API), se saca la entrada en la base:
`DELETE FROM identity.blocked_entries WHERE id = ...`.

Una integración puede cargar entradas por la API, por ejemplo desde un detector de abusos:

```http
POST /api/v1/security/blocklist
Authorization: Bearer bk_...
Content-Type: application/json

{ "kind": "ip", "value": "203.0.113.0/24", "reason": "Escaneo de puertos", "expires_at": "2026-11-05T00:00:00Z" }
```

---

## Ingresos e intentos

Cada intento de ingreso queda en `identity.login_events`: a quién (si existe), IP, red, navegador, resultado y método.
No se guarda el correo de quien no tiene usuario ni ninguna contraseña. El historial se borra pasado su ajuste (365
días por defecto) con la retención diaria.

- **Bloqueo por cuenta:** 5 fallas seguidas (contraseña o código) bloquean la cuenta 15 minutos. El mensaje dice los
  minutos del ajuste. El staff tiene su propio umbral en los ajustes, por si conviene uno más estricto. No corta las
  sesiones abiertas.
- **Bloqueo por IP:** 20 fallas en 15 minutos desde una IP la bloquean 30 minutos, con una entrada automática en la
  lista que el staff ve y puede sacar. En IPv6 cuenta y bloquea la red `/64` entera, porque cualquier conexión tiene
  una y cambiar de dirección dentro de ella es gratis. Nunca acorta una entrada permanente del staff. Con muchos
  clientes detrás de una misma IP (un operador móvil) conviene subir el tope.
- **Redes de confianza:** las IPs o redes del ajuste "Redes de confianza" (la oficina, la VPN del staff) nunca se
  bloquean por intentos. Sus fallas quedan en el historial, y las listas que carga el staff valen igual.
- **Aviso de un lugar nuevo:** un ingreso exitoso desde una red (`/24` en IPv4, `/48` en IPv6) desde la que el usuario
  nunca había entrado le manda un correo con la fecha, la IP y el navegador (plantilla `new-login`). El primer ingreso
  no avisa. El aviso sale por la cola de correo, con sus trabas.

El tope en memoria de los formularios (20 envíos cada 5 minutos por IP y por nodo) sigue como primera barrera.

---

## Contraseñas

La política sale de los ajustes y vale al crear el usuario, al restablecer y al cambiar la contraseña:

- Largo mínimo de 10 para clientes y 12 para el staff, nunca menos de 8, hasta 128.
- Rechazo de las comunes (una lista propia en castellano y en inglés, secuencias, repeticiones y números cortos) y de
  las que contienen la parte del correo antes de la arroba o la marca (`Ui:Brand`).
- Reglas de composición (mayúscula, minúscula, número, signo) apagadas por defecto, como recomienda NIST SP 800-63B.

El cliente y el staff cambian la suya desde su página de seguridad, con la actual. Cambiarla corta las demás sesiones.
La contraseña inicial que muestra `staff add` sale con el largo del staff (20 o más) y un carácter de cada clase, así
cumple la política aunque la composición esté prendida.

---

## Correo verificado

El cliente pide el enlace desde `/cuenta/seguridad` y le llega por correo (plantilla `email-verification`). El enlace
vence en 24 horas y se confirma con un botón en `/cuenta/verificar`, sin iniciar sesión: abrirlo no confirma nada, así un
lector de correo que revisa los enlaces no lo hace por la persona. Restablecer la contraseña también confirma el correo.
Necesita `Billing:PublicUrl`.

---

## Segundo factor

- **Obligatorio por rol:** en los ajustes se eligen los roles del portal que lo tienen obligatorio (titulares,
  facturación, técnicos). Quien tenga alguno en alguna cuenta no ve otra página del portal que la de seguridad hasta
  activarlo, y no lo puede desactivar. Vale también para lo que no es una página, como el PDF de una factura. Para el
  staff es siempre obligatorio.
- **Enrolar:** la página muestra un QR para escanear con la app, armado en el servidor, y la clave en texto para
  cargarla a mano.
- **Exigirlo a un cliente:** desde `/admin/cuentas/{id}/seguridad`, el staff con `security.manage` le exige el segundo
  factor a un usuario o se lo deja de exigir. Hasta activarlo, ese usuario solo ve la página de seguridad del portal.
- **Códigos de recuperación:** 8 al activarlo. La página de seguridad dice cuántos quedan y avisa con 2 o menos. Con un
  código de la app se generan otros 8 y los anteriores dejan de valer. Al ingresar se aceptan en minúsculas.
- **Perdió el teléfono y los códigos:** el staff con `security.manage` le quita el segundo factor a un cliente desde
  `/admin/cuentas/{id}/seguridad`. Para el staff, el comando:

```powershell
dotnet BillingOss.Web.dll staff reset-2fa persona@example.com
```

En los dos casos se cortan sus sesiones y queda en la auditoría (`security.2fa_reset` desde la ficha, `staff.2fa_reset`
desde el comando). En el próximo ingreso enrola un TOTP nuevo.

La seguridad propia del staff (`/admin/seguridad`) dice si el ingreso por OIDC está configurado y si su usuario está
vinculado. Antes de usarlo en producción hay que probarlo contra el proveedor de identidad en un staging.

## Admin solo desde redes propias

Hornero no filtra el admin por red: lo hace el proxy inverso, que es el que ve la conexión primero. En NPM es
una lista de acceso en el host del admin, y en HAProxy una ACL por `path_beg /admin` con las redes de la oficina y de la
VPN del staff. Conviene dejar una salida de emergencia documentada (la VPN o una IP fija de respaldo), porque un
filtro mal cargado deja a todo el staff afuera. Las redes de confianza de los ajustes son otra cosa: solo evitan que
el límite de intentos bloquee esas redes.

---

## Auditoría

| Acción | Cuándo |
|---|---|
| `security.settings` | Se guardaron los ajustes. Lleva los nombres de lo que cambió, nunca valores |
| `security.block`, `security.unblock` | Entrada cargada o sacada de una lista |
| `security.2fa_reset` | El staff le quitó el segundo factor a un cliente |
| `security.2fa_required` | El staff le exigió el segundo factor a un cliente o se lo dejó de exigir |
| `staff.2fa_reset` | El comando `staff reset-2fa` le quitó el segundo factor a alguien del staff |
| `portal.password_changed`, `staff.password_changed` | Cambio de contraseña con la sesión |
| `portal.recovery_codes`, `staff.recovery_codes` | Códigos de recuperación nuevos |
| `portal.email_verified` | Correo verificado |
| `fraud.review`, `fraud.screened`, `fraud.approved`, `fraud.rejected` | Evaluación y revisión de pedidos |
