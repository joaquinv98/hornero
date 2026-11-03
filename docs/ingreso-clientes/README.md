# Registro e ingreso con Google

**Diseño:** [ingreso-clientes.md](../../diseno/ingreso-clientes.md) · **Decisiones:** D-09 y D-10

Esta guía dice cómo se prende el registro de clientes en el portal y el ingreso con Google u otro proveedor OpenID
Connect, y qué hacer el día del corte para que los clientes que hoy entran con Google sigan entrando igual. El modelo y
las reglas están en el diseño.

| Pieza | Dónde | Permiso |
|---|---|---|
| Ajustes del registro | `/admin/seguridad/ajustes#registro` | `security.manage` |
| Proveedores externos | `/admin/seguridad/proveedores` | `security.manage` |
| Vínculos de un cliente | `/admin/cuentas/{id}/seguridad` | `accounts.read`, y quitarlos `security.manage` |
| Crear cuenta | `/registro`, enlazado desde `/ingresar` | Cualquiera |
| Vínculos propios | `/cuenta/seguridad` | La sesión del cliente |

Hace falta `Billing:PublicUrl`: con ella se arman la dirección de vuelta del proveedor y los enlaces de los correos,
nunca con el Host del pedido. Y la llave de secretos (`Secrets:KeyFile`) en los nodos web, que cifra el secreto de cada
proveedor.

---

## Registro

Viene prendido. En los ajustes se apaga, se cambia el tope de cuentas nuevas por IP (5 en 24 horas por defecto, por red
`/64` en IPv6) y la moneda que viene elegida (ARS). Las listas de bloqueo de IPs, correos y dominios valen también para
el registro, y las redes de confianza no tienen tope.

Quien se registra con correo y contraseña recibe el enlace para verificar el correo. Si el ajuste "mandar a revisión
los pedidos de quien no verificó el correo" está prendido, su primer pedido espera la revisión hasta que lo verifique.
Quien se registra con Google ya tiene el correo verificado.

Después del alta sigue a los datos fiscales de la cuenta y de ahí al pedido que lo trajo. Puede seguir sin cargarlos:
mientras tanto se le factura como consumidor final.

---

## Google

### Prenderlo

1. En la consola de Google Cloud del proyecto del proveedor, en Credenciales, usar el cliente OAuth "Aplicación web" que ya
   usa el sistema actual, o crear uno. Usar el mismo cliente conserva los vínculos: Google identifica a cada persona
   con el mismo `sub` para todos los clientes del proyecto.
2. Agregar en "URI de redireccionamiento autorizados" la dirección de vuelta que muestra la pantalla, por ejemplo
   `https://portal.example.com/ingresar/externo/google/vuelta`. Sin esto Google rechaza la ida con
   `redirect_uri_mismatch`.
3. En `/admin/seguridad/proveedores`, cargar el ID del cliente y el secreto, prender y guardar.
4. "Probar la conexión" tiene que decir que el proveedor reconoció el ID del cliente y el secreto.
5. Probar el ingreso con una cuenta de prueba: el botón "Continuar con Google" aparece en el ingreso y en el registro.

El secreto se guarda cifrado y no vuelve a mostrarse. Para rotarlo, se carga el nuevo y se guarda.

### Qué ve el cliente

- **Ya vinculado:** entra. Si tiene el segundo factor activo, se lo pide después de Google.
- **Con un usuario con el mismo correo, sin vínculo:** confirma una sola vez con su contraseña o con un enlace que le
  llega a ese correo (vence en una hora). Nunca se vincula solo.
- **Sin usuario:** completa nombre, empresa y teléfono y crea la cuenta, si el registro está abierto.
- **Con un correo que Google no verificó:** no vincula ni crea nada, y entra o se registra con contraseña.

Desde su página de seguridad puede vincular otra cuenta de Google o quitar una. Quien se registró con Google no tiene
contraseña: si quiere una, la pide con "Recuperar la contraseña".

### Otro proveedor

En la misma pantalla, "Otro proveedor OpenID Connect" pide una clave (va en la dirección de vuelta), el nombre del
botón, la dirección del emisor (HTTPS), el ID del cliente y el secreto. Cambiar después la dirección pide cargar el
secreto otra vez. El proveedor tiene que mandar `email_verified` para vincular o registrar por correo.

---

## Día del corte

La importación trae los vínculos de Google del sistema actual (`tblauthn_account_links`) atados a los usuarios
importados, y deja el proveedor Google cargado pero apagado. Ese día:

1. Agregar la dirección de vuelta nueva al cliente OAuth que ya usa el sistema actual (paso 2 de arriba).
2. Cargar el secreto en `/admin/seguridad/proveedores`, probar la conexión y prender.
3. El informe del importador dice cuántos vínculos entraron y cuántos quedaron afuera porque su usuario no se importó.

Los clientes vinculados entran con Google sin hacer nada. Si un vínculo no entró, el cliente confirma una vez con su
contraseña o con el enlace al correo.

---

## Desafío anti robots

Viene apagado. En `/admin/seguridad/ajustes#desafio` se elige el desafío y los formularios que lo piden (ingreso,
recuperar la contraseña, registro, contacto y ticket sin cuenta). Los botones de baja y de arrepentimiento nunca lo
piden.

- **Prueba de trabajo propia:** invisible y sin servicios de afuera. El umbral de 16 bits tarda menos de un segundo en
  un teléfono. Subirlo frena más a un robot y también a la persona: cada bit duplica el trabajo. Necesita JavaScript.
- **Turnstile:** crear el widget en el panel de Cloudflare con el dominio del portal, en modo invisible o administrado,
  y cargar la clave pública y la secreta (sale del vault). La secreta se guarda cifrada y no vuelve a mostrarse.

---

## Pruebas

Todo se prueba contra un proveedor falso en proceso (`FakeOidcProvider`): ida con PKCE, vuelta con el state y el nonce,
tokens con otra llave, otro destinatario o vencidos, vínculo confirmado, registro, segundo factor y la pantalla del
staff (`ExternalLoginUiTests` y `RegistrationUiTests`). El desafío se prueba con la prueba de trabajo de un solo uso y
con un validador de Turnstile falso (`ChallengeUiTests`). La prueba contra Google real, `GoogleLiveTests`, corre solo con
`BILLINGOSS_GOOGLE_CLIENT_ID` y `BILLINGOSS_GOOGLE_CLIENT_SECRET`, y sin ellas queda inconclusa.
