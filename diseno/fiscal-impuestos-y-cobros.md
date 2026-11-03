# Fiscal, impuestos y cobros incluidos

**Estado:** propuesta v0, 2026-10-28 · **Parte de:** [diseño del núcleo](README.md) · **Decisión:**
[D-21](decisiones.md#d-21-fiscal-impuestos-y-cobros-incluidos)

Objetivos:

- Que ARCA, Mercado Pago, PayPal, Stripe y las pasarelas principales vengan incluidas por defecto.
- Que conectar ARCA sea fácil: un lugar donde se genera y se carga el certificado, una guía paso a paso, todo
  incluido.
- Una sección de configuración impositiva completa: cuánto IVA, si los precios se cargan con IVA o sin IVA, si se
  muestran con el IVA incluido o discriminado, y todo lo de ese estilo.

Este documento dice cómo. Sale de tres relevamientos propios del 2026-10-28 (ARCA, impuestos y cobros), hechos solo
con documentación pública y sin llamar a ningún servicio de ARCA ni de las pasarelas, ni siquiera de homologación.
Las fuentes, con su fecha, están al final.

**Fuentes y reglas.** El diseño no se armó mirando nada de WHMCS: sale de las
normas (Boletín Oficial, InfoLEG, BOCBA y la normativa de ARCA), de la documentación pública de ARCA y de las
pasarelas, y de material público de otros sistemas (Stripe Tax, Odoo, Shopify y Blesta).

---

## Resumen

| Pieza | Qué es |
|---|---|
| Asistente de ARCA | Siete pasos en el panel: datos del emisor, clave y pedido de certificado generados en el servidor, guía con cada pantalla de ARCA, carga del certificado con validaciones propias, prueba sin emitir y activación |
| Vida del certificado | Avisos a 30, 15, 7 y 1 día del vencimiento y renovación guiada sin cortar la emisión |
| Padrón de ARCA | Completa la ficha del cliente y los datos del emisor desde el CUIT. Ayuda, nunca obliga |
| Impuestos | Categoría en el producto, reglas con vigencia por receptor, categoría y cuenta, modo de carga en cada precio y congelado en el servicio, percepciones, clase C, muestra por canal y simulador |
| Cobros de fábrica | Mercado Pago, PayPal, Stripe y cripto como módulos dentro de la receta, que se dan de alta solos, más la transferencia en el núcleo, con un asistente que prueba cada conexión |
| Débito automático | En Argentina, la suscripción de Mercado Pago. Para el producto, una capacidad nueva del protocolo con medios guardados, y Stripe como primera implementación |

La idea de fondo: casi todo existe en piezas (WSAA, WSFEv1 y WSFEX, reglas con vigencia, cálculo por factura y
alícuota, módulos de cobro). Lo que falta es lo que convierte esas piezas en algo que un proveedor configura solo,
sin un programador al lado y sin poder equivocarse en lo legal.

---


## 2. Alta guiada de ARCA

### 2.1 Qué pide ARCA

ARCA resume el alta para facturar por web service en siete acciones: leer la documentación, identificar el servicio
de negocio (`wsfe` para A, B y C, `wsfex` para E), generar el certificado, asociarlo al servicio y delegarlo,
desarrollar el acceso a WSAA y al servicio, empadronarse solo si la norma lo exige y dar de alta el punto de venta.
Aclara que no hace falta un certificado por servicio: uno alcanza para todos.

El desarrollo ya está hecho (el módulo). Lo que el producto no puede hacer es entrar a ARCA con la clave fiscal del
contribuyente: esos pasos los hace la persona. El asistente le dice qué pantalla abrir, qué elegir y qué pegar, y
después comprueba que quedó bien.

| Ambiente | Dónde se pide el certificado | Clave fiscal | Dónde se autoriza cada servicio |
|---|---|---|---|
| Homologación | WSASS, la autogestión de certificados de homologación | De persona física, nivel 2 o más. WSASS no es delegable | En WSASS, "Crear Autorización a Servicio", una vez por servicio |
| Producción | "Administración de Certificados Digitales" | Nivel 3 | En el Administrador de Relaciones de Clave Fiscal, "Nueva Relación", una vez por servicio |

Homologación y producción son dos recorridos del mismo asistente, con instancias y emisores separados (2.10).

### 2.2 El asistente

Vive en Ajustes, Fiscal, "Conectar con ARCA". Guarda el avance: se puede cortar en cualquier paso, por ejemplo para
esperar los días hábiles del punto de venta, y retomar. Cada paso termina con una verificación automática cuando se
puede.

| Paso | Qué ve la persona | Qué hace el producto | Verificación |
|---|---|---|---|
| 1. Qué vas a hacer | Ambiente (probar o facturar de verdad), si vende al exterior (factura E) y si quiere completar datos con ARCA (padrón) | Arma la lista de servicios a autorizar: `wsfe`, `wsfex` si exporta y `ws_sr_constancia_inscripcion` si usa el padrón | Que tenga la clave fiscal y el nivel que pide el ambiente |
| 2. Datos del emisor | CUIT, razón social, condición frente al IVA, si ARCA lo habilitó para A o para A con leyenda, domicilio comercial, ingresos brutos e inicio de actividades | Valida la CUIT con su dígito verificador (`TaxIdentity.IsValidCuit`). La condición decide las clases de comprobante (3.6) | CUIT válida y datos del impreso completos (RG 1415, anexo II) |
| 3. Clave y pedido de certificado | El alias propuesto, la CUIT con la que va a entrar a ARCA y el pedido (CSR) para copiar o bajar | Genera la clave y el pedido en el servidor (2.4) | El pedido se lee y tiene el sujeto que pide ARCA |
| 4. Trámites en ARCA | Tres guías con cada pantalla de ARCA: certificado, servicios y punto de venta (2.3), cada una con "Ya lo hice" | Muestra los nombres exactos de las pantallas y los datos para pegar | La hace el paso 6 |
| 5. Cargar el certificado | Un lugar para pegar o subir el certificado que dio ARCA | Valida localmente, sin llamar a ARCA (2.5) | Par, CUIT, alias y vigencia, cada uno con su mensaje |
| 6. Probar sin emitir | Una lista que se va completando: conexión, servicios, delegación de la CUIT, puntos de venta, series y datos del emisor según ARCA | Compone consultas del módulo (2.6) y ofrece los puntos de venta que devuelve ARCA en vez de pedir un número a mano | Cada punto con su remedio en castellano |
| 7. Activar | El resumen y el estado de las trabas de producción | Asigna la instancia al emisor y crea las series que falten con el último número que informa ARCA. Si un punto de venta nuevo ya tiene comprobantes, avisa que otro sistema lo usa y tiene que dejar de hacerlo, porque cada punto es exclusivo (sección 7 del [modelo](dominio.md)) | En producción, las dos trabas (2.9). El asistente no las prende |

Así se ve el paso 3:

> **Pedido de certificado**
>
> Alias: `billingoss` (cambiar) · CUIT con la que vas a entrar a ARCA: la del emisor (cambiar)
>
> [Copiar pedido] [Bajar pedido .csr]
>
> La clave privada quedó guardada y cifrada en este servidor, y no sale de acá. No la necesitás: ARCA solo pide el
> pedido.

### 2.3 Guía paso a paso en ARCA

La guía vive en los textos del asistente y se publica también en `docs/fiscal/alta-arca.md`, para quien la siga por
consola antes de que exista el panel. Usa los nombres de pantalla de los documentos oficiales. Los que solo citan
guías de terceros van marcados para confirmar en pantalla.

**Homologación (WSASS).**

| # | Dónde | Qué hacer | Qué se pega de Hornero |
|---|---|---|---|
| 1 | Requisito | Clave fiscal de persona física con nivel 2 o más. Si en las pruebas se factura por una empresa, la persona la representa en la autorización del punto 4 | Nada |
| 2 | Administrador de Relaciones de Clave Fiscal | "Adherir servicio", grupo "Servicios interactivos", WSASS y "Continuar". Salir de ARCA y volver a entrar para que aparezca | Nada |
| 3 | WSASS, "Nuevo Certificado" | "Nombre simbólico del DN": el alias, que no puede existir. "CUIT del contribuyente": se completa sola con la de quien entró y tiene que ser la del pedido. Pegar el pedido y "Crear DN y Obtener Certificado". ARCA muestra el certificado en PEM para copiar | Alias y pedido |
| 4 | WSASS, "Crear Autorización a Servicio" | Alias, CUIT del DN, "CUIT representado" (la que factura en las pruebas) y el servicio. Una vez por servicio: `wsfe`, `wsfex` si exporta y `ws_sr_constancia_inscripcion` si usa el padrón | Lista de servicios |
| 5 | Punto de venta | Los documentos leídos no dicen si en homologación hace falta darlo de alta. El paso 6 lo comprueba: si ARCA no devuelve ninguno, el asistente acepta un número a mano y lo prueba con el último número | Nada |

En WSASS los certificados no se pueden borrar, ni vencidos, y se pueden crear todos los que hagan falta. En
homologación la CUIT del certificado suele ser la de quien programa y no la que factura, y es válido: la validación
avisa y no frena.

**Producción.**

| # | Dónde | Qué hacer | Qué se pega de Hornero |
|---|---|---|---|
| 1 | Requisito | Clave fiscal nivel 3 (la pantalla dice "Nivel de seguridad mínimo requerido 3") | Nada |
| 2 | "Administración de Certificados Digitales" | Si no aparece entre los servicios: Administrador de Relaciones, "Nueva Relación", elegir el servicio y el representante, confirmar, salir y volver a entrar. Si sigue sin verse, aceptarlo en "Aceptación de Designación" | Nada |
| 3 | "Administración de Certificados Digitales" | Elegir el contribuyente, "Agregar alias", escribir el alias, subir el pedido (PKCS#10) y confirmar. Después, "Ver" en el alias y bajar el certificado | Alias y archivo `.csr` |
| 4 | Administrador de Relaciones, "Nueva Relación" | Representado: la empresa. Si se elige a la persona, el servicio queda autorizado para la persona y no para la empresa. "Buscar" el servicio en el grupo de web services de ARCA ("Facturación Electrónica" es `wsfe`). Representante: el computador fiscal del alias. "Confirmar" dos veces, y queda la constancia F. 3283/E. Se repite para exportación y para el padrón, cuyos nombres en pantalla no figuran en los documentos leídos | Alias y lista de servicios |
| 5 | "Administración de Puntos de Venta y Domicilios", "A/B/M de Puntos de Venta", "Agregar" | Número, nombre de fantasía opcional, sistema de facturación y domicilio, que tiene que estar dado de alta antes en "Sistema Registral". Para `wsfe`, un punto de web service: "RECE para aplicativo y web services" para responsables inscriptos y "Factura Electrónica - Monotributo - Web Services" para monotributistas (a confirmar en pantalla). Para `wsfex`, otro punto de "Comprobantes de Exportación - Web Services": la factura E necesita el suyo | El sistema que corresponde a la condición del paso 2 |

El alta del punto de venta se informa con al menos 3 días hábiles de anticipación al inicio de las operaciones (art.
47 de la RG 1415, con el texto de la RG 5866/2026, BO 29/06/2026). El asistente lo dice antes de empezar, porque es
el único paso que no se resuelve en el momento.

Si el certificado es de otra CUIT (por ejemplo, un estudio contable que factura por la empresa), la empresa tiene que
aceptar la designación y asignar su propio computador fiscal, y la cadena no puede seguir hacia otra empresa.

Corrección pendiente: el README del módulo dice que el certificado de producción se pide con el Administrador de
Relaciones. Se pide en "Administración de Certificados Digitales", y el Administrador de Relaciones solo lo asocia a
los servicios y lo delega.

### 2.4 Clave y pedido de certificado en el servidor

- ARCA pide una clave RSA de al menos 2048 bits y este sujeto: `C=AR`, `O=<razón social>`, `CN=<alias>` y
  `serialNumber=CUIT <11 dígitos>`, con la palabra CUIT, un espacio y el número sin guiones (WSASS, "Cómo generar
  una solicitud de certificado").
- .NET 10 lo resuelve con su biblioteca estándar, sin openssl ni dependencias nuevas: `RSA.Create(2048)`,
  `X500DistinguishedNameBuilder` (país, organización, nombre común y el OID 2.5.4.5 como PrintableString) y
  `CertificateRequest` con SHA-256 y PKCS#1, que arma el PEM con `CreateSigningRequestPem()`. El relevamiento lo
  probó en local con .NET 10.0.14 y sin red: el sujeto sale `C=AR, O=..., CN=..., SERIALNUMBER=CUIT <número>` y el
  PEM es un `CERTIFICATE REQUEST`.
- La CUIT del sujeto es la de quien pide el certificado. En producción es la del contribuyente que se elige en
  "Agregar alias". En WSASS tiene que coincidir con la de quien entra, que suele ser una persona.
- Alias: solo letras minúsculas y números, como el ejemplo del manual de WSAA. El asistente propone `billingoss` y
  deja cambiarlo. Organización y alias van sin acentos ni eñes: los ejemplos de ARCA son ASCII y no hay razón para
  arriesgar un rechazo.
- La clave se cifra con `SecretBox` (D-09) en el mismo pedido que la genera, atada a su tabla, columna y fila. No
  vuelve al navegador, no se ofrece para bajar y no pasa por los logs.
- No hay exportación de la clave. Si se pierde la instalación, se restaura con el backup y el archivo de llaves
  (D-09). Si eso tampoco está, se pide otro certificado, que no cuesta nada y lleva minutos. La única excepción es de
  homologación y por consola: el comando de generación puede dejar la clave en un archivo para las
  pruebas, porque con ella no se emite nada real. Con ambiente de producción el comando rechaza esa opción
  aunque se la pidan, y un test lo cubre: la combinación no existe.
- Quien migra con un certificado propio lo puede subir con su clave: PEM (PKCS#1 o PKCS#8, con contraseña o sin
  ella), DER o PKCS#12. El núcleo la pasa a PKCS#8 sin contraseña antes de cifrarla, porque el módulo la pide así, y
  no guarda ni registra la contraseña, que viaja solo en el cuerpo del pedido y nunca en la URL. Igual se recomienda
  un certificado nuevo con alias propio (2.9).

**Credencial fiscal** (`FiscalCredential`), tabla nueva del módulo Fiscal, una fila por par de claves:

| Campo | Qué es |
|---|---|
| Emisor y ambiente | De qué emisor es y para qué ambiente |
| Alias y CUIT del sujeto | Lo que va en el pedido |
| Estado | `pending` (espera el certificado), `ready` (certificado cargado y validado), `active` o `retired` |
| Clave privada | Cifrada. Se borra de esta fila al pasar a `active`, porque queda en los secretos de la instancia, o a `retired` |
| Pedido y certificado | En PEM. No son secretos |
| Metadatos del certificado | CUIT y alias del certificado, emisor, número de serie, huella SHA-256, vigencia desde y hasta |

Hay una sola credencial `active` por emisor y ambiente (índice único parcial). Los metadatos sirven para mostrar y
para avisar sin descifrar nada. Activar una credencial copia certificado y clave a los secretos de la instancia del
módulo con una operación nueva del registro de módulos, `ReplaceSecrets`, que pide reautenticación y queda en la
auditoría. Hoy no hay forma de reemplazar los secretos de una instancia sin cambiar la URL de la instalación
(`ModuleRegistry.ChangeBaseUrl`, rama integracion).

### 2.5 Carga y validación del certificado

Antes de llamar a ARCA, el núcleo valida y explica cada caso por separado. Hoy el módulo devuelve un único mensaje
para una clave de otro par, una clave cifrada o un PEM mal formado, porque .NET lanza la misma excepción en los tres
casos (el relevamiento lo probó con .NET 10.0.14).

| Control | Resultado | Mensaje |
|---|---|---|
| Se lee como X.509 (PEM, DER o PKCS#12) | Error | "Esto no es un certificado. Bajalo de nuevo desde ARCA con el botón Ver del alias." |
| Su clave pública es la de la clave guardada (se compara el `SubjectPublicKeyInfo`) | Error | "Este certificado es de otro pedido. Usá el que te dio ARCA para el alias {alias} con el pedido de este asistente." |
| `serialNumber` con la forma `CUIT` y 11 dígitos, con dígito verificador válido | Error | "El certificado no trae una CUIT válida." |
| La CUIT del certificado es la del emisor | Aviso | "El certificado es de la CUIT {x} y vas a facturar con la {y}. Funciona solo si {y} autorizó a {x} en ARCA. En homologación es lo normal." |
| El nombre común es el alias esperado | Aviso | "El alias del certificado es {a} y el del pedido es {b}." |
| Vigencia: no empieza en el futuro y no venció | Error | "El certificado venció el {fecha}. Generá un pedido nuevo con Renovar." |
| Días hasta el vencimiento | Aviso si faltan menos de 30 | "Vence en {n} días." |
| Emisor del certificado | Se muestra | El ambiente no se deduce con seguridad del certificado. Si es del otro ambiente, WSAA responde que no lo emitió una AC de confianza o que el computador no está autorizado (manual de WSAA, preguntas 10.2 y 10.3), y la prueba del paso 6 lo detecta |

Un certificado que pasa queda como credencial `ready`, con sus metadatos.

### 2.6 Prueba sin emitir

Hoy `instance/test` hace `FEDummy` y el login de `wsfe`, y da bien aunque la CUIT no esté delegada: la falla aparece
recién al emitir (600, 601, 1000 o 1001, que el módulo devuelve como `invalid_config`). La prueba nueva la compone el
núcleo con consultas que, según los manuales, no autorizan ningún comprobante:

| # | Qué prueba | Cómo | Qué detecta |
|---|---|---|---|
| 1 | Conexión, certificado y ambiente | `instance/test`: `FEDummy` y login de WSAA para `wsfe` | Certificado del otro ambiente, vencido o sin autorizar para `wsfe`, y reloj corrido |
| 2 | Servicios y delegación de la CUIT | `parameters`, que hace llamadas autenticadas de WSFEv1 y, si el emisor exporta, las de WSFEX con su login | Servicio sin asociar y CUIT que el certificado no puede representar |
| 3 | Puntos de venta | `parameters` suma `points_of_sale` (`FEParamGetPtosVenta`) y, con exportación, los de `FEXGetPARAM_PtoVenta` | Punto inexistente, de tipo CAEA, bloqueado o dado de baja, y falta del punto de exportación |
| 4 | Series al día | `last_number` de cada serie (punto de venta y tipo del mapa del emisor) | Que el último número de ARCA coincida con el último registrado. En la migración, que coincida con el último importado (D-17) |
| 5 | Condiciones del receptor | `parameters` ya trae `receiver_vat_conditions` con su clase | La tabla que usa la validación de la ficha (3.6) |
| 6 | Datos del emisor según ARCA | `taxpayer` con la CUIT del emisor, si autorizó el padrón (2.7) | Condición distinta de la cargada, domicilio fiscal y constancia bloqueada |

Detalles:
- No hace falta leer las relaciones que viajan dentro del ticket: las llamadas autenticadas de los puntos 2 a 4 ya
  fallan si la CUIT no está delegada, y el módulo traduce el error con su remedio.
- El módulo no suma operaciones para la prueba. `parameters` suma dos tablas (puntos de venta y tipos de tributo,
  que también pide la sección 3.8). `instance/test` acepta `fresh_login`, que pide un ticket nuevo sin usar el
  guardado ni el que el módulo tiene en memoria (sin eso, renovar el certificado podría probarse con el ticket del
  viejo), y suma `authorize_enabled` a la respuesta, para que el panel muestre la traba del módulo sin adivinarla.
- WSAA rechaza por unos minutos otro ticket del mismo computador y servicio (manual de WSAA, pregunta 10.6). El
  módulo ya lo devuelve como reintentable con `retry_after` (150 segundos en producción y 660 en homologación), y la
  pantalla muestra la espera en vez de un error. El núcleo guarda el `instance_state` de cada respuesta, como pide
  el README del módulo.
- La prueba queda guardada con su fecha y su resultado, y se repite desde la ficha del emisor.
- La traba del núcleo sigue frenando la emisión. La prueba es otro caso de uso que solo llama consultas, y contra
  producción pasa solo si el módulo lo deja (2.9).

### 2.7 Padrón de ARCA

- Servicio: `ws_sr_constancia_inscripcion` (manual 4.1, marzo de 2026), método `getPersona_v2`. Reemplaza al padrón
  A5, que el catálogo de ARCA da por deprecado. Entra como la operación `taxpayer` de la capacidad `fiscal.taxpayer`,
  que el protocolo ya reserva (sección 9.4).
- Devuelve nombre o razón social, tipo de persona, domicilio fiscal, impuestos con su estado (30 es IVA y 20 es
  monotributo), categoría de monotributo y actividades. El módulo deduce una condición sugerida: con IVA activo,
  responsable inscripto (1). Con monotributo activo, monotributista (6, 13 o 16). Con IVA exento, exento (4). Sin
  ninguno, deja elegir entre consumidor final y no categorizado.
- En la ficha del cliente, "Completar con ARCA" aparece después de la CUIT. La condición es una sugerencia que
  confirma el staff o el cliente, y se guarda en la versión nueva de la identidad fiscal con su fuente y su fecha
  (`TaxIdentity` suma `Source` y `VerifiedAt`).
- En los datos del emisor, precarga razón social y domicilio fiscal. Ingresos brutos e inicio de actividades no
  vienen en la constancia y se cargan a mano.
- No se consulta antes de cada emisión: alcanza con verificar al cargar o cambiar la ficha. Si ARCA rechaza igual,
  sigue el camino de D-05 (nota de crédito sin comprobante y otra factura con la ficha corregida).
- La constancia puede venir bloqueada (sin domicilio fiscal electrónico, con requerimientos pendientes y los demás
  casos del anexo del manual). Por eso el padrón ayuda pero nunca es obligatorio: siempre queda la carga a mano.
- Privacidad: se guardan solo nombre, domicilio fiscal y condición, con fuente y fecha. Las caracterizaciones, como
  la categoría de riesgo, no se guardan ni se muestran.
- Requiere autorizar el certificado al servicio del padrón (2.3). Falta confirmar que aparezca en el Administrador de
  Relaciones para cualquier contribuyente, porque el catálogo avisa que algunos servicios piden acuerdos especiales.
- Después: `ws_sr_padron_a13` (manual 1.4, 14/08/2026), que devuelve las CUIT de un DNI, para identificar al
  consumidor final desde el umbral de la sección 3.6.

### 2.8 Vencimiento y renovación del certificado

- Las páginas oficiales leídas no fijan la vigencia. El dato es el `NotAfter` del certificado, que queda en los
  metadatos de la credencial.
- Un trabajo diario del planificador (D-07), con clave por credencial y día, avisa al staff a los 30, 15, 7 y 1 día.
  Suma el aviso a la portada "Requiere atención" del admin y una métrica de días restantes para las alertas de D-15.
- Si vence, WSAA responde `cms.cert.expired`, el módulo devuelve `invalid_config` y las emisiones quedan frenadas con
  el trabajo muerto y visible. La ficha del emisor lo muestra arriba de todo.
- "Renovar" crea una credencial nueva con el mismo sujeto (alias, CUIT y razón social) y otra clave, y lleva a los
  pasos 4 a 6 con la guía de renovación:
  - Homologación: WSASS, "Agregar Certificado a Alias", que ignora el sujeto del pedido y devuelve otro certificado
    del mismo alias.
  - Producción: falta confirmar si "Administración de Certificados Digitales" deja sumar un certificado a un alias
    existente. Si deja, las relaciones siguen, porque son del computador fiscal. Si no, se usa un alias nuevo y se
    repite la asociación de los servicios.
- Para activar la renovación, el núcleo prueba la credencial candidata con `instance/test` y `fresh_login`, armando
  el sobre con la configuración actual y el certificado y la clave nuevos, sin guardarlos. Si pasa, reemplaza los
  secretos de la instancia y guarda el estado que devolvió la prueba, todo junto. La credencial vieja pasa a
  `retired`, y su clave deja de existir con el reemplazo. Hasta ese momento la vieja sigue emitiendo.
- El ticket vigente, de hasta 12 horas, es del computador y del servicio, así que cambiar el certificado no corta la
  emisión.

### 2.9 Custodia de la clave y trabas de producción

**Custodia en tres escalones.**
1. **Ahora:** la clave se genera en el núcleo, se cifra en el acto, nunca va al navegador ni se descarga, y el
   módulo corre en su propia instalación (D-08), en el mismo host y por la red interna, como ya hace la receta. ARCA
   pide conservar la clave en un lugar seguro (WSASS, "Cómo generar una solicitud de certificado"). Mientras
   `private_key_pem` viaje en cada llamada, ni el núcleo ni el módulo escriben en sus logs el cuerpo de esos pedidos,
   y un test del módulo lo comprueba.
2. **Después:** firmar el login de WSAA en el núcleo, el pendiente de la sección 13 del protocolo. En vez
   de `private_key_pem`, el núcleo manda en cada llamada un CMS ya firmado por servicio (`wsaa_cms`), que vale
   minutos y solo sirve para pedir un ticket. El módulo lo usa solo cuando necesita un ticket nuevo. La clave deja de
   salir del núcleo.
3. **Con la receta de alta disponibilidad:** firma OpenBao Transit (D-09) y la clave no está ni en el núcleo.

Un atajo que no sirve: Afip SDK manda certificado y clave a su API alojada.

**Trabas de producción.** `Fiscal:ProductionEnabled` frena en el núcleo toda emisión contra
una instancia de producción, y `Arca:AllowProduction` hace que el módulo rechace toda llamada de producción, incluida
`instance/test`, antes de leer el certificado. Con las dos puestas, el asistente de producción solo puede hacer las
validaciones locales (2.5), y el login de WSAA en producción se prueba recién al pasar a producción.

**Prueba de solo lectura contra producción (decidido el 2026-10-28).** La traba del módulo pasa a tres valores,
`Arca:Production` con `off` (por defecto), `read` y `on`. `read` deja pasar solo una lista cerrada de operaciones de
consulta (el login de WSAA, `FEDummy`, `FEParamGet*`, `FECompUltimoAutorizado`, `FECompConsultar`, `FEXGetPARAM*`,
`FEXGetLast_CMP`, `FEXGetLast_ID`, `FEXGetCMP` y el padrón) y rechaza todo lo demás con `invalid_config`:
`FECAESolicitar`, `FEXAuthorize` y cualquier operación que se sume después (CAEA, por ejemplo) hasta que alguien la
agregue a la lista. Es una lista de permitidos y no de prohibidos, para que una operación nueva no pase por omisión, y
un test pide `authorize` contra una instancia de producción en `read` y espera el rechazo. Con `read`, la prueba sin
emitir corre contra producción antes de emitir de verdad. La traba del núcleo no cambia. Contra producción, aunque
sea en `read`, se usa recién con el certificado de producción y una decisión explícita del operador. Conviene
`read` en staging, así el primer día no es la primera vez que se habla con ARCA de verdad. `Arca__AllowProduction`
en `true`, la clave anterior, vale como `on` si `Arca__Production` no está.

**Alias propio en producción (decidido el 2026-10-28).** Un alias propio de Hornero, y no solo un certificado
nuevo del alias que usa el sistema anterior, porque el manual de WSAA habla del computador, que es el alias, y así
los tickets de los dos sistemas no se pisan en el mes en paralelo ni en el corte. El alias nuevo exige hacer una
vez la asociación de `wsfe` y `wsfex` en el Administrador de Relaciones.

El asistente no prende las trabas. En el paso 7 muestra su estado y qué cambiar en el `.env` de la receta
(`Fiscal__ProductionEnabled=true` y `Arca__AllowProduction=true`, que pasa a `Arca__Production=on` cuando se integre
la traba de tres posiciones). Es el único paso fuera del panel, a propósito: emitir
comprobantes reales lo decide quien opera el servidor.

### 2.10 Homologación y producción no comparten emisor

`FiscalSeries` es única por emisor, punto de venta y tipo, y `FiscalDocument` por emisor, punto, tipo y número, sin
ambiente. Si un emisor prueba en homologación y después pasa a producción, la serie arrastra el último número de
homologación, un documento con CAE de homologación ya no puede tener otro (D-05) y saldría en el PDF con QR como si
valiera.

Reglas:
- El `environment` de una instancia fiscal no cambia. Para el otro ambiente se crea otra instancia.
- Un emisor con series o comprobantes no pasa a una instancia de otro ambiente.
- La homologación vive en otra instalación (un staging) o en otro emisor. El PDF de un emisor de homologación
  sale con la marca de documento no válido que el diseño de interfaz ya prevé.

### 2.11 La ficha del emisor

Ajustes, Fiscal muestra una tarjeta por emisor:

| Bloque | Qué muestra | Acciones |
|---|---|---|
| Estado | Listo, falta el certificado, certificado por vencer en N días, emisión frenada por la traba o por un error | Conectar con ARCA o seguir el asistente |
| Certificado | Alias, CUIT, emisor, vencimiento y días que faltan | Renovar, probar de nuevo |
| Puntos de venta | Número, uso (A y B, C o E) y estado en ARCA según la última prueba | Cambiar, eligiendo de la lista de ARCA |
| Series | Último número propio y de ARCA, y la fecha de la última conciliación | Ver las emisiones |
| Trabas | Estado de las dos trabas de producción | Qué cambiar y dónde |
| Datos del impreso | Razón social, domicilio, condición, ingresos brutos e inicio de actividades | Editar, completar con ARCA |

---

## 3. Configuración impositiva

### 3.1 Qué se mantiene y qué cambia

Se mantiene, porque es correcto y ya tiene tests:
- El cálculo por comprobante y alícuota, con la mitad hacia arriba y el residuo al renglón mayor (`TaxCalculator`).
  Es lo que hace Stripe Tax en su cálculo automático y queda dentro de la tolerancia que publica el manual de WSFEv1
  4.7: error relativo de hasta 0,01 % o absoluto de hasta 0,01 por alícuota, y 0,01 por alícuota informada en las
  sumas. El manual además no controla las alícuotas en las notas A y B (tipos 2, 3, 7, 8, 52 y 53). Esto responde
  el pendiente de la tolerancia del [README del diseño](README.md), y la prueba de homologación lo confirma en la
  práctica.
- Las reglas con vigencia que fallan si no hay ninguna o si hay empate (`TaxRule.Resolve`).
- Los topes de las notas por alícuota, exento y no gravado (`CreditNoteRules`).
- La identidad fiscal versionada, con la condición en códigos de ARCA, y el receptor y las alícuotas congelados en el
  documento.
- Gravado, exento y no gravado (`TaxTreatment`), que van directo a los campos de ARCA. Gravado al 0 % es gravado con
  alícuota cero (código 3 de ARCA).
- Las clases A, B y E, con las condiciones de clase A (1, 6, 13 y 16) que coinciden con la tabla del manual.

Cambia:

| Hoy | Problema | Cambio |
|---|---|---|
| Modo de precio global (`BillingOptions.PriceMode`, con IVA incluido) | Ni el precio del plan ni el congelado en el servicio dicen si son brutos o netos. Si alguien cambia la opción, todas las renovaciones cambian de sentido y un cliente que pagaba con IVA incluido pasa a pagar 21 % más | Modo en cada precio, congelado en el servicio (3.4) |
| El producto solo tiene tratamiento y la regla no mira el producto | No se puede expresar el 27 % de las telecomunicaciones | Categoría en el producto y en la regla (3.3) |
| Un impuesto por renglón y `other_taxes` fijo en cero | No hay percepciones, y el módulo de ARCA rechaza cualquier tributo | Impuestos de comprobante y lista de tributos (3.8) |
| El emisor no tiene condición frente al IVA | No hay clase C ni preset de monotributo | La condición del emisor y la clase C (3.6) |
| Códigos de alícuota fijos en el núcleo (`VatIds`) | El protocolo dice que salen de `parameters` | Se validan contra `parameters`, con la vigencia de cada alícuota (`FEParamGetTiposIva`) |
| La renovación resuelve la regla con la fecha del período y el pedido con la del día | Dos fechas para lo mismo, y ninguna es la del comprobante | Fecha fiscal única: la de emisión (3.2) |
| La regla forzada por cuenta de la sección 2 del [modelo](dominio.md) no existe en el código | No hay excepciones por cuenta | Regla de cuenta (3.2) |
| La provincia es texto libre | No se pueden comparar zonas ni reportar por jurisdicción | Código ISO 3166-2 (`AR-C`, `AR-B` y así) |
| La muestra al cliente no está modelada | Lo legal depende de quién mira | Reglas de muestra por canal (3.5) |

### 3.2 Modelo

| Pieza | Qué es | Nueva o existente | Cuándo |
|---|---|---|---|
| Condición del emisor (`IssuerProfile.VatCondition`) | Responsable inscripto, monotributista o exento. Es el registro de IVA en Argentina y decide la clase de comprobante | Existente | Corte |
| Categoría (`TaxCategory`) | Qué es lo que se vende, a efectos del impuesto. Va en el producto, con códigos externos opcionales para un motor de cálculo. Reemplaza al tratamiento del producto | Nueva | Corte |
| Impuesto (`Tax`) | Lo que aparece como renglón del desglose: nombre visible, nivel (renglón o comprobante), base (neto, o neto más los impuestos de renglón), código del fisco y si suma al total o solo se informa | Nueva | Corte, con el IVA y la percepción de la RG 2126 |
| Regla (`TaxRule`) | La de hoy, ampliada: impuesto, país y subdivisión ISO del receptor, condición frente al IVA, si es empresa con número fiscal validado, categoría, cuenta, resultado, alícuota, motivo, leyenda y vigencia | Existente, ampliada | Corte |
| Registro fiscal (`TaxRegistration`) | Dónde más tributa el proveedor: ingresos brutos (local o Convenio Multilateral), agente de percepción, OSS no Unión, Reino Unido, un estado de EE. UU., o el impuesto principal de un proveedor de otro país. Jurisdicción, impuesto, régimen, número, redondeo y vigencia. Sin registro vigente, ese impuesto no se cobra y el renglón lo dice | Nueva | Otros proveedores |
| Muestra por canal | Cómo se presenta el precio en catálogo, checkout, factura, correo y API | Nueva, casi toda fija por ley | Corte para Argentina, después el resto |
| Evidencia del receptor | Número fiscal validado (VIES o HMRC, con su número de consulta) y dos datos de ubicación que no se contradicen | Nueva, en la identidad fiscal | Otros proveedores |

No hay entidad de zona: la regla ya tiene país y subdivisión, y "el resto del mundo" es una regla sin país. Para la
Unión Europea, el preset crea una regla por país, que igual hacen falta porque cada país tiene su alícuota.

**Cambios en las tablas.**

| Entidad | Cambio |
|---|---|
| `TaxCategory` (nueva) | Nombre, explicación, códigos externos opcionales y vigencia |
| `Tax` (nueva) | Código estable, nombre visible, nivel (renglón o comprobante), base (neto, o neto más los impuestos de renglón), código del fisco, si suma al total y grupo para mostrar |
| `TaxRule` | Suma impuesto, categoría, cuenta, si el receptor es empresa con número validado, motivo, leyenda, quién la cargó y la constancia adjunta. `Province` pasa a `Subdivision`, con código ISO 3166-2. Siguen país, condición, resultado, alícuota y vigencia |
| `Product` | Categoría en lugar de `TaxTreatment` |
| `PlanPrice` y el precio de cada TLD | `PriceMode` |
| `Service` | `PriceMode`, congelado junto con `UnitPrice` |
| `DocumentLine` | Categoría y motivo, congelados como la alícuota |
| `Document` | Total de otros tributos y las listas de tributos y de leyendas, congeladas. La fecha fiscal es `IssueDate`, que ya existe |
| `TaxIdentity` | Subdivisión ISO 3166-2 (el importador normaliza el texto de hoy), `Source` y `VerifiedAt`. Para la UE y el Reino Unido, la validación del número fiscal (fuente, fecha y número de consulta) y la evidencia de ubicación |
| `IssuerProfile` | Condición frente al IVA, domicilio, ingresos brutos e inicio de actividades, para el PDF fiscal |
| `FiscalCredential` (nueva) | La de la sección 2.4 |
| `TaxRegistration` (nueva) | Jurisdicción, impuesto, régimen, número, redondeo y vigencia |
| `BillingOptions.PriceMode` | Desaparece. Queda un modo por defecto por moneda, que solo usan los precios nuevos |

**Cómo se resuelve un renglón**, en este orden:
1. El receptor congelado del documento (la versión de su identidad fiscal) y su evidencia.
2. La **fecha fiscal**, que es la de emisión del documento: elige regla, alícuota y registro. En la renovación es
   cambiar la fecha del período por la del día de la corrida.
3. La **categoría** del renglón: la del producto. El cargo de alta, el recargo por mora y el cambio de plan llevan la
   del servicio al que acompañan, porque la Ley de IVA (art. 10) suma intereses, recargos y recuperos de gastos al
   precio neto gravado aunque se facturen aparte. Un renglón manual elige la suya. El cupón baja el precio antes del
   cálculo, como ya hace el núcleo y como hace Stripe Tax.
4. El **impuesto principal** (IVA o VAT): la regla más específica que coincide y está vigente. Con empate o sin
   ninguna, el cálculo falla, como hoy. La especificidad suma cuenta 16, categoría 8, condición o empresa 4,
   subdivisión 2 y país 1.
5. El monto según el **modo del precio** (3.4) y el cálculo por comprobante y alícuota, sin cambios.
6. Los **impuestos de comprobante** (percepciones) sobre ese resultado, cada uno con su regla (3.8). Para estos, no
   tener regla quiere decir que no corresponden.
7. La **clase de comprobante** y las **leyendas** (3.5 y 3.6).

Cada renglón guarda, además de su alícuota, un **motivo** con una palabra estable (`exportacion`, `exento_por_ley`,
`no_gravado`, `inversion_sujeto_pasivo`, `sin_registro`, `regla_de_cuenta`, `monotributo`) que la factura, el
simulador y la API muestran en castellano llano. Es la idea del motivo por renglón de Stripe Tax. El documento suma
la lista de tributos de comprobante y la de leyendas, las dos congeladas al emitir.

**Regla de cuenta.** Es una regla con la cuenta puesta, que gana siempre dentro de su alcance, con motivo
obligatorio, vigencia, quién la cargó y, si la hay, la constancia adjunta con su número. Cubre:
- La exención con constancia de ARCA, provincial o un certificado de reventa.
- La exclusión de una percepción (3.8).
- El cliente del exterior que usa el servicio en el país y paga IVA (sección 6.2 del [modelo](dominio.md)).
- El 21 % para una telecomunicación que se presta en una vivienda, si el contador confirma el 27 % para el resto
  (3.3).

Vence sola y avisa antes. Reemplaza a la "regla de impuesto forzada" de la sección 2 del modelo, que nunca llegó al
código. Stripe aclara que no valida certificados de exención: el sistema los guarda para mostrarlos ante una
inspección, y quien los acepta es el staff.

### 3.3 Categorías

Vienen de fábrica, con su explicación. El proveedor puede crear otras.

| Categoría | Qué va | Resultado en Argentina |
|---|---|---|
| Servicios digitales y hosting (por defecto) | Hosting, VPS, game servers, bare metal y servicios gestionados | 21 %, la alícuota general (art. 28, primer párrafo, de la Ley de IVA). La ley nombra el suministro y alojamiento de sitios informáticos y páginas web entre los servicios digitales (art. 3, inc. e, punto 21, inc. m, apartado 1) y los servicios de computación, software incluido (mismo punto, inc. c). Lo que el contador confirma es solo si algo de la lista es una telecomunicación (fila siguiente) |
| Telecomunicaciones | Telefonía y enlaces, si el contador los ubica acá | 27 % a responsables inscriptos y monotributistas cuando no se presta en una vivienda, y 21 % al resto (art. 28) |
| Exento por ley | Lo que la ley exime | Exento (`ImpOpEx` en ARCA) |
| No gravado | Lo que no es una operación gravada | No gravado (`ImpTotConc`) |

- El 10,5 % es para una lista cerrada del art. 28 que no le toca a un proveedor de hosting. ARCA codifica 0 %, 2,5 %,
  5 %, 10,5 %, 21 % y 27 % (códigos 3, 9, 8, 4, 5 y 6), y `FEParamGetTiposIva` da la vigencia de cada uno.
- La vivienda no es otra dimensión de la regla: es una regla de cuenta. Así el caso raro no complica a todos.
- Migración: los productos `Taxed` pasan a "Servicios digitales y hosting", los `Exempt` a "Exento por ley" y los
  `NotTaxed` a "No gravado". Las dos reglas que siembra el importador (Argentina al 21 % y el resto exento) no
  cambian, así el mes en paralelo da lo mismo que hoy.

### 3.4 Modos de carga

- Cada precio dice si es con impuesto incluido o sin impuesto: el del plan con su cargo de alta y el de cada TLD. Las
  diferencias de las opciones configurables siguen el modo del precio del plan. El valor por defecto se fija una vez
  por moneda, por ejemplo pesos con IVA incluido y dólares sin impuestos, y cada precio se puede cambiar. Stripe lo
  resuelve igual con su `tax_behavior` por precio, que no se puede cambiar una vez fijado, y propone por defecto
  precios sin impuesto en USD y CAD y con impuesto en las demás monedas.
- El servicio congela el modo junto con el precio (`Service.PriceMode` al lado de `UnitPrice`). Cambiar un ajuste
  nunca cambia lo que paga un cliente que ya existe.
- El documento sigue con un solo modo, como hoy (`Document.PriceMode`). La clave del borrador de renovación suma el
  modo a la cuenta, la moneda, el vencimiento y el medio de pago. Como cada moneda suele tener un solo modo, en la
  práctica no parte facturas, salvo en la transición después de cambiar el modo de una moneda. Un pedido que mezcla
  modos sale en dos facturas.
- El importador escribe el modo de cada servicio (por ejemplo, con IVA incluido) y `BillingOptions.PriceMode`
  desaparece.

**Cuando el impuesto del cliente no es el que se supuso.** Un plan de $12.100 con IVA incluido supone el 21 %. Hay
dos comportamientos posibles:

| Comportamiento | Cliente del exterior exento | Quién lo usa |
|---|---|---|
| Mantener el precio final | Paga $12.100 y el neto del proveedor sube | Lo habitual en hosting argentino. Stripe con la inversión del sujeto pasivo y con los clientes exentos |
| Mantener el neto | Paga $10.000 | Shopify con su precio dinámico con impuesto incluido, Odoo con posiciones fiscales que reemplazan el impuesto |

Queda el primero, que es lo habitual y lo que compara el mes en paralelo. Con precios sin impuesto los
dos coinciden. El segundo necesita que cada precio guarde la alícuota que supuso, y se suma cuando un proveedor lo
pida (por ejemplo, para vender en euros con el IVA de cada país y el mismo neto).

### 3.5 Modos de muestra

Lo que ve el cliente no es una preferencia libre: para un emisor responsable inscripto que vende a consumidores en
Argentina, la ley fija casi todo. El panel bloquea lo que la ley fija para el emisor configurado y explica por qué.

| Canal | Qué se muestra | Por qué |
|---|---|---|
| Catálogo y carrito, visitante sin identificar en el país por defecto del proveedor (Argentina) | Precio final en pesos y, en letra más chica, el precio sin IVA ni otros impuestos nacionales indirectos con la leyenda PRECIO SIN IMPUESTOS NACIONALES. Si el precio está en dólares, además el equivalente en pesos con la cotización, su fuente y su fecha | Res. 4/2025 de la Secretaría de Industria y Comercio (BO 17/01/2025), art. 2, incisos a, b y c, este último desde el 01/04/2025 (art. 7). Alcanza a los anuncios por cualquier medio. Solo los servicios prestados desde, hacia o en el exterior pueden mostrarse solo en dólares (inc. h) |
| Catálogo de un cliente identificado como responsable inscripto | Puede elegir ver neto más IVA | Le sirve para su crédito fiscal. Es una preferencia de la cuenta |
| Checkout | Desglose por impuesto (neto, IVA por alícuota, percepciones), leyendas y total antes de pagar. En las suscripciones, el total por período | La Directiva 2011/83/UE (art. 6.1.e) lo pide a quien vende a consumidores europeos, y mostrarlo siempre no cuesta nada |
| Factura y PDF | No se configura: lo define la clase. A con el IVA discriminado. B con el total y el bloque del Régimen de Transparencia Fiscal al Consumidor (IVA contenido y otros impuestos nacionales indirectos). C sin IVA. E sin IVA y con su leyenda | Ley 27.743 (BO 08/07/2024), arts. 98 y 99, y RG 5614/2024 (BO 13/12/2024), obligatoria para todos desde el 01/04/2025 |
| Correo | El mismo resumen que la factura | Que no haya dos números distintos |
| API y webhooks salientes | Siempre neto, impuestos por tipo y total, nunca un solo número ambiguo | Las integraciones no tienen que adivinar |
| Otros países | UE: precio con el IVA del país del visitante. EE. UU.: sin impuesto y con el aviso de que se suma | Llega con los registros (3.9) |

El art. 39 de la Ley de IVA, con el texto del art. 98 de la Ley 27.743, obliga a discriminar el IVA al responsable
inscripto que vende a consumidores finales. Un monotributista no tiene IVA que discriminar, y una pregunta frecuente
de ARCA, citada por el Consejo Profesional de Ciencias Económicas de Salta (fuente secundaria), dice que el régimen
no incluye al Régimen Simplificado: en ese preset el bloque queda apagado y con aviso, y el contador lo confirma. El
PDF ya imprime el bloque en la clase B, con dos dudas para confirmar con un contador.

### 3.6 Clase de comprobante y condición del receptor

| Emisor | Receptor | Clase | Fuente |
|---|---|---|---|
| Responsable inscripto | Responsable inscripto o monotributista (1, 6, 13 y 16) | A. A un monotributista, con la leyenda sobre el crédito fiscal | RG 1415, art. 15, y RG 5003/2021 (BO 02/06/2021) |
| Responsable inscripto | Exento, consumidor final, no categorizado y los demás (4, 5, 7, 8, 9, 10 y 15) | B | RG 1415, art. 15 |
| Monotributista o exento | Cualquiera | C, sin IVA | RG 1415, art. 16 |
| Cualquiera | Exportación de servicios | E, con numeración propia | RG 1415, art. 17 |

- `FiscalTypes` suma la clase C según la condición del emisor. En C, `ImpNeto` es el subtotal, `ImpTotConc`,
  `ImpOpEx` e `ImpIVA` van en cero, no se informa el array de IVA y el total es `ImpNeto` más `ImpTrib`
  (validaciones 10043, 10044, 10045, 10047 y 10048 del manual de WSFEv1 4.7, la 10046 es de bienes usados). Las
  reglas de un emisor monotributista no cobran IVA, con el motivo `monotributo`.
- La A con leyenda (tipos 51 a 54) es configuración, no código: si ARCA habilitó al emisor solo para esa variante,
  su mapa de tipos usa esos códigos. El asistente lo pregunta en el paso 2.
- La ficha del cliente solo acepta los códigos de la tabla de ARCA, con su descripción oficial, y la condición es
  obligatoria antes de la primera factura. La exige la RG 5616/2024, art. 2 (BO 18/12/2024), en todo comprobante
  electrónico, con la versión nueva del web service obligatoria desde el 15/04/2025 (art. 4, inc. a), y el manual
  4.7 ya trae la validación que la rechaza cuando falta, citando esa RG. El módulo ya la exige. La fecha del
  01/12/2026 que circula en la prensa es la del manual 4.8, no la de la obligación.
- Un consumidor final se identifica con DNI, CUIL o CDI cuando la operación es igual o superior a $10.000.000 (RG
  1415, Anexo II, apartado A, título II, inc. d, con el texto del art. 1, inc. f, de la RG 5866/2026, vigente desde
  el 01/07/2026). El checkout lo pide al pasar el umbral, que es un ajuste con vigencia porque se actualiza.
- Las cuentas con condición 7 (no categorizado) siguen la sección 3.8.
- La factura de crédito electrónica MiPyME no es opcional: cuando el receptor figura en el listado de empresas
  grandes de ARCA y el importe es igual o superior al mínimo ($5.549.862 desde el 14/04/2026, según el micrositio de
  ARCA), el comprobante tiene que ser FCE (tipos 201, 206 y 211), por la Ley 27.440 y la RG 4367/2018. Hasta que
  exista la FCE (paso 28), la ficha del cliente tiene la marca "empresa grande según ARCA", que carga el staff
  mirando ese listado, y una emisión a una cuenta marcada por un importe igual o superior al mínimo se rechaza antes
  de llamar a ARCA, con el motivo. El mínimo es un ajuste con vigencia, como el umbral del consumidor final. Si
  alguna cuenta importada está en el listado lo dice el reporte fiscal del importador.

Una discrepancia para el contador, verificada en las dos fuentes: el art. 15, inc. a, de la RG 1415 (texto de la RG
5003/2021) fija la leyenda "El crédito fiscal discriminado en el presente comprobante, sólo podrá ser computado a
efectos del Régimen de Sostenimiento e Inclusión Fiscal para Pequeños Contribuyentes de la Ley Nº 27.618", y la
observación 10217 del manual de WSFEv1 4.7 la escribe como "...a efectos del Procedimiento permanente de transición
al Régimen General". La 10217 es una observación, no un rechazo. El PDF imprime la de la RG, que es la
norma, salvo que el contador diga otra cosa.

### 3.7 Redondeo

| Decisión | Hoy | Propuesta |
|---|---|---|
| Dónde se redondea | Por comprobante y alícuota | Lo declara la jurisdicción: Argentina, por comprobante y alícuota. Por renglón queda como opción de otros registros cuando haga falta (Stripe la ofrece con tasas manuales y Odoo la trae por defecto) |
| El medio centavo | Hacia arriba (`AwayFromZero`) | Igual. ARCA declara que usa Round Half Even: en un medio centavo exacto puede haber un centavo de diferencia, que entra en la tolerancia. Un test de tabla lo cubre |
| El residuo | Al renglón mayor de cada alícuota | Igual. Así los renglones suman exacto |
| La tolerancia | 0,01 por alícuota (`TaxCalculator.ArcaTolerance`), más estricta que la del manual | Igual: más estricta es más segura |

El panel no lo deja cambiar para Argentina. Lo muestra con una frase: "El IVA se calcula sobre el total de cada
alícuota, que es como lo controla ARCA. Por eso el IVA de un renglón puede diferir en un centavo de la cuenta a mano.
El total no cambia."

### 3.8 Percepciones e impuestos que solo se informan

Una percepción es un anticipo de impuesto que el vendedor designado como agente suma a la factura y deposita, y que
el cliente descuenta de su propio impuesto. No es parte del precio ni del IVA del vendedor, y va a nivel de
comprobante.

**IVA a no categorizados (RG 2126/2006, texto actualizado en InfoLEG).**
- Todo responsable inscripto es agente cuando vende o presta servicios gravados a quien no acredita ser responsable
  inscripto, exento, no alcanzado o monotributista (arts. 1 y 3). No corresponde si el cliente entrega la
  constancia que acredita alguna de esas condiciones (art. 2, inc. a) o si se declara consumidor final y el vendedor
  no puede presumir razonablemente lo contrario (art. 2, inc. b, con el texto de la RG 5738/2025, BO 31/07/2025). La
  presunción de consumidor final por monto, hasta $10.000.000, vale solo para los códigos de actividad que lista ese
  inciso (comercio de alimentos y bebidas, supermercados), que no son los de un proveedor de hosting.
- Alícuota: 10,5 % en general, 13,5 % en las prestaciones del segundo párrafo del art. 28 de la Ley de IVA (las del
  27 %, como las telecomunicaciones) y 5,25 % en las de alícuota reducida (art. 5).
- Base: precio neto más IVA (art. 5). Se suma al total de la factura (art. 6) y se ingresa por SICORE (art. 7),
  fuera de Hornero, que da el reporte.
- En ARCA va en el array de tributos, con el código de percepción de IVA a no categorizados que devuelve
  `FEParamGetTiposTributos` (13, "Percepción de IVA No Categorizado según la RG 2126/2006", manual 4.7). La
  validación 10067 del mismo manual lo exige en una B (tipos 6, 7 y 8) emitida a la CUIT genérica 23000000000 de no
  categorizado cuando neto más IVA es mayor a cero: tiene que ir el tributo 13 con importe mayor a cero.
- En el modelo es el impuesto "Percepción de IVA RG 2126", de comprobante y con base neto más IVA, con dos reglas
  para un emisor responsable inscripto y un receptor con condición 7: 13,5 % para la categoría de telecomunicaciones
  y 10,5 % para el resto. Sale un renglón de tributo por alícuota.
- Hoy el importador lleva "Sujeto No Categorizado" a la condición 7, así que una B a esas cuentas saldría sin la
  percepción. Hasta que el contador decida si se factura a no categorizados, hay tres frenos: el reporte del
  importador lista esas cuentas, la ficha no ofrece la condición 7 mientras el emisor no tenga la percepción, y una
  emisión a condición 7 sin percepción se rechaza antes de llamar a ARCA, con el motivo.

**Ingresos brutos.** Solo si una jurisdicción designa al proveedor como agente. ARBA obliga a quienes facturaron más
de $7.800.000.000 el año anterior (RN 7/2026, 29/01/2026), con un padrón mensual de alícuotas por CUIT, 8 % si la CUIT
no figura y 3 % si el padrón no se puede consultar. CABA tiene su régimen general (Res. 296/AGIP/2019, retomada por la
352/AGIP/2022) con su padrón mensual. Un proveedor chico o mediano difícilmente sea agente, así que viene
apagado. Lo que pide configurar cada régimen:

| Dato | Para qué |
|---|---|
| Jurisdicción, régimen, número de agente y vigencia de la designación | Saber si corresponde percibir |
| Fuente de la alícuota: padrón mensual importado, alícuota fija o tabla | Calcular |
| Alícuota si la CUIT no figura y si el padrón no responde | Los dos casos que fijan las normas |
| Base: neto sin IVA o total | Calcular |
| Mínimos y condiciones de receptor excluidas | No percibir de más |
| Cómo se decide la jurisdicción del cliente | Domicilio fiscal o de facturación |
| Código de tributo de ARCA y exportación para la declaración del agente | Informar y depositar |

**En el comprobante y en el módulo.**
- El documento suma el total de otros tributos y su lista (código, descripción, base, alícuota e importe), congelados
  al emitir.
- La nota de crédito recalcula la percepción sobre su propia base con la alícuota del original, con tope en lo que
  falta revertir, como ya hace `CreditNoteRules` por alícuota. ARCA pide que una nota que revierte percepciones
  informe un período asociado del mismo mes (manual 4.7), así que el módulo suma `PeriodoAsoc`, que hoy no
  implementa.
- El módulo de ARCA suma `taxes` (lista con `id`, `description`, `base`, `rate` y `amount`), arma `ImpTrib` y el
  array de tributos, y `parameters` devuelve los tipos de tributo. `other_taxes` queda como total, para no romper el
  protocolo, y tiene que coincidir con la suma.

**Alícuota de ISIB en CABA desde 2027.** Quien paga ingresos brutos en la Ciudad, salvo el Régimen Simplificado,
tiene que mostrar la alícuota en las facturas a consumidores finales por operaciones en la Ciudad, con la leyenda
ALÍCUOTA ISIB CABA y el porcentaje (la menor, si hay varias). En Convenio Multilateral suma APLICABLE SOBRE INGRESOS
BRUTOS ATRIBUIDOS A CABA, y hay leyendas propias para operaciones promovidas y exentas (Res. 169/AGIP/2026, BOCBA
30/04/2026). La adecuación se prorrogó al 01/01/2027 (Res. 339/AGIP/2026, BOCBA 08/09/2026). En el modelo es un
impuesto que solo se informa: una regla por subdivisión del receptor (`AR-C`) y condición consumidor final, con
leyenda y sin importe, que no suma al total ni va a ARCA. La crea el preset cuando el proveedor dice que paga
ingresos brutos en la Ciudad, con el texto de Convenio Multilateral si corresponde. Otras provincias pueden seguir el
mismo camino, que abrió el art. 99 de la Ley 27.743, con la misma pieza. Si el proveedor paga ingresos brutos en CABA lo
confirma su contador.

### 3.9 Internacional

**Clientes del exterior desde Argentina.**
- Las prestaciones cuya utilización o explotación efectiva es en el exterior no se consideran hechas en el país (Ley
  de IVA, art. 1, inc. b) y tienen tratamiento de exportación (art. 43). Van con letra E (RG 1415, art. 17) por
  WSFEX, que el módulo ya cubre.
- Si el cliente del exterior usa el servicio en el país, no hay exportación: va en B, con IVA y con la condición 9
  (rama integracion). Qué cuenta como uso en el exterior lo define el contador por cuenta, con una regla de cuenta:
  el país del domicilio no alcanza.
- Para ingresos brutos en Convenio Multilateral, las ventas a distancia se atribuyen al domicilio del adquirente
  (Comisión Arbitral, RG 14/2017 y RG 5/2021). El sistema da las ventas por provincia del cliente, con código ISO, y
  las exportaciones aparte.

**Unión Europea.**
- El hosting es un servicio prestado por vía electrónica (Directiva 2006/112/CE, anexo II, y Reglamento 282/2011,
  anexo I). A consumidores tributa en el país del cliente (art. 58 de la Directiva), sin umbral para quien no está
  establecido en la UE: el de EUR 10.000 es solo para los establecidos. Se declara en la ventanilla única, régimen no
  Unión: registro en un solo Estado, declaración trimestral hasta el fin del mes siguiente aunque no haya ventas y
  registros por 10 años.
- A empresas tributa donde está el cliente (art. 44) y lo liquida el cliente (art. 196): la factura va sin IVA y con
  la mención de inversión del sujeto pasivo. Alcanza el número de IVA validado en VIES sin indicios en contra
  (Reglamento 282/2011, art. 18), y VIES devuelve un número de consulta que sirve de constancia si quien consulta
  informa su propio número.
- La ubicación del consumidor se presume con dos datos que no se contradigan (art. 24b, d), entre los del art. 24f:
  domicilio de facturación, IP, datos bancarios y otros.
- Las alícuotas cambian (Estonia 24 % desde julio de 2025, Rumania 21 % desde el 01/08/2025, Finlandia 25,5 %), así
  que van como reglas por país con vigencia.
- Hace falta el registro OSS, las reglas por país, la validación del número con su constancia, la evidencia de
  ubicación y un reporte trimestral por país y alícuota. ViDA (Directiva (UE) 2025/516, del 11/03/2025) hace de la
  factura electrónica la regla y suma un reporte digital por etapas hasta 2035: va en un módulo fiscal futuro, no en
  el núcleo.
- Cómo convive ese IVA con la factura E argentina, que no informa impuestos del exterior, lo responde el contador
  antes de vender a consumidores europeos.

**Reino Unido.** El mismo esquema, por separado. Los servicios digitales a consumidores británicos pagan IVA
británico y un proveedor no establecido se registra en HMRC sin umbral (VAT Notice 741A, sección 2.6, actualizada el
29/09/2022). A empresas rige la inversión del sujeto pasivo (secciones 5.1 y 6.3). Sin número de IVA, el cliente es
consumidor salvo otra prueba. Alícuotas de 20 %, 5 % y 0 %. Los números GB ya no se validan en VIES: HMRC tiene su
API "Check a UK VAT number" 2.0, que con el número propio devuelve una referencia.

**Estados Unidos.** No hay IVA federal: cada estado, y además condados y ciudades, cobra su impuesto sobre ventas
con alícuota por domicilio. Desde South Dakota v. Wayfair (21/06/2018) un vendedor sin presencia física puede quedar
obligado si supera los umbrales de un estado. Si el hosting está gravado depende del estado: Texas lo trata como
procesamiento de datos y lo grava sobre el 80 % del precio. Recomendación: no cargar miles de jurisdicciones en el
núcleo. Un tipo de módulo nuevo, `tax`, con una operación `calculate` que devuelve los impuestos por renglón y
jurisdicción, deja enchufar Stripe Tax, Avalara o similares, y el núcleo congela el resultado como hoy congela las
alícuotas. Corre solo donde el proveedor declaró un registro, como Stripe, que calcula cero donde no hay registro.
Las exenciones con certificado (reventa, entidad exenta) son reglas de cuenta.

**Latinoamérica.** Todos los países tienen comprobantes que autoriza o valida el fisco, así que entran como módulos
fiscales con el protocolo actual: CFDI 4.0 timbrado por un PAC en México, documentos tributarios electrónicos y
boleta en Chile (formato 2.5, de febrero de 2026), validación previa de la DIAN en Colombia (Res. 165 de 2023), OSE en
Perú, CFE con rangos de numeración en Uruguay y NFS-e con la reforma de IBS y CBS por etapas en Brasil. El núcleo deja
de dar por sentadas tres cosas: que él propone el número (en algunos países el fisco asigna rangos o folios, y hace
falta una capacidad `fiscal.numbering`), que hay un solo documento por operación (puede haber versión para
consumidor y para empresa) y que el redondeo y los tributos son los de Argentina (los declara cada módulo).

### 3.10 Vigencias y cambios

- Reglas, impuestos, categorías, registros y reglas de cuenta llevan vigencia. Nada se edita: se cierra lo vigente y
  se abre otro, así un documento viejo siempre se explica con lo que usó.
- Un cambio con fecha futura muestra, antes de guardarlo, a cuántos servicios afecta y cómo queda la próxima
  renovación de cada uno. Con precios con IVA incluido el cliente paga lo mismo y cambia el neto del proveedor. Con
  precios sin impuesto cambia lo que paga el cliente, y el sistema prepara el aviso.
- `FEParamGetTiposIva` da la vigencia de cada alícuota, y el panel avisa si una regla usa una que ARCA ya no tiene.
- Lo emitido no se toca: cada renglón guarda su alícuota y su motivo, y el documento, su desglose.

### 3.11 Cómo se ve en el panel

Sección nueva "Impuestos" en el admin, con su propio permiso (`taxes.edit`) y todo en la auditoría.

| Página | Qué tiene |
|---|---|
| Empezar | Asistente de tres preguntas: país del emisor, condición (responsable inscripto, monotributista o exento) y dónde vende (solo en Argentina, también al exterior, a consumidores de la UE o del Reino Unido, en EE. UU.). Cada respuesta deja un preset completo de categorías, impuestos y reglas. Sin reglas el cálculo falla, y eso no puede ser lo primero que vea quien recién instala |
| Así cobrás hoy | El simulador abierto con los clientes tipo, para ver de un vistazo qué paga cada uno |
| Dónde tributás | La condición del emisor y los registros, con número y vigencia |
| Categorías | Cada una con su explicación y cuántos productos la usan |
| Reglas | Tabla con filtros por impuesto, país, categoría y receptor, una columna "por qué" en castellano llano y el historial |
| Percepciones | Las que corresponden al emisor, con los datos de agente y los padrones |
| Precios y muestra | Modo de carga por moneda y lo que se muestra en cada canal, con lo que fija la ley bloqueado y explicado |
| Simulador | Se elige un cliente tipo y un producto, y se ve la clase de comprobante, los impuestos por renglón, las percepciones, las leyendas y el total, con el motivo de cada cosa |
| Avisos | Lo que puede estar mal, con un enlace para arreglarlo |

Así se ve el simulador con un plan de $12.100 con IVA incluido:

| Cliente tipo | Clase | Neto | Impuestos | Total | Por qué |
|---|---|---|---|---|---|
| Consumidor final en CABA | B | $10.000,00 | IVA 21 % $2.100,00 | $12.100,00 | "21 % porque es un servicio digital vendido en Argentina. La factura muestra el IVA contenido, como pide la Ley 27.743." |
| Responsable inscripto en Córdoba | A | $10.000,00 | IVA 21 % $2.100,00 | $12.100,00 | "A porque el cliente es responsable inscripto." |
| Responsable inscripto, telefonía fuera de una vivienda | A | $9.527,56 | IVA 27 % $2.572,44 | $12.100,00 | "27 % porque es una telecomunicación a un responsable inscripto. Con precios con IVA incluido el cliente paga lo mismo y baja tu neto." |
| No categorizado | B | $10.000,00 | IVA 21 % $2.100,00 y percepción RG 2126 10,5 % $1.270,50 | $13.370,50 | "Percepción porque el cliente no acreditó su condición frente al IVA." |
| Empresa del exterior, plan de USD 100 sin impuestos | E | USD 100,00 | Ninguno | USD 100,00 | "Sin IVA porque es una exportación de servicios." |

Cada opción del panel dice qué significa, trae un ejemplo con números y qué elegir si no sabés. Las decisiones
legales abiertas llevan "Consultalo con tu contador".

**Avisos activos:**
- Emisor monotributista con reglas que cobran IVA.
- Producto de telecomunicaciones sin regla del 27 %.
- País con ventas y sin regla propia, que cae en "el resto del mundo".
- Cuentas con condición 7 sin percepción configurada.
- Regla de cuenta o certificado de ARCA por vencer.
- Regla con una alícuota que ARCA ya no tiene vigente.
- Monotributista cerca del tope de su categoría: el acumulado de 12 meses contra el tope, que ARCA ya observa al
  emitir.

**Del lado del cliente.** "Datos fiscales" en la cuenta, en el portal y en el admin: la condición con las
descripciones oficiales de ARCA, la CUIT con "Completar con ARCA", la carga de una constancia (que crea una regla de
cuenta pendiente de aprobación del staff) y, para clientes del exterior, el país, el número fiscal y su validación
cuando haya registros de la UE o del Reino Unido.

**En el producto.** Un selector de categoría con la explicación de cada una y "Si no sabés, elegí Servicios digitales
y hosting".

El diseño de la interfaz (D-19, [interfaz-y-temas.md](interfaz-y-temas.md)) ya prevé una carpeta para el contador con un CSV de neto por alícuota. Es la base de los
reportes de IVA, percepciones, ventas por provincia y OSS.


## 4. Cobros

### 4.1 Qué viene incluido

| Medio | Para quién | Qué cubre | Estado |
|---|---|---|---|
| Mercado Pago | Argentina y Latinoamérica | Checkout Pro por redirección (tarjetas, dinero en cuenta, Rapipago y Pago Fácil), reembolsos y conciliación. Débito automático por suscripción | Incluido, con las suscripciones (4.5) detrás de `Payments:SubscriptionsEnabled` |
| Transferencia a CBU, CVU o alias | Argentina | Pago manual con conciliación asistida por el CUIT del ordenante | Núcleo, sin módulo (sección 9.3 del protocolo), con la conciliación (4.6) |
| PayPal | Clientes del exterior | Orders v2 por redirección, captura, reembolsos, disputas y conciliación en USD y otras monedas, nunca en pesos | Incluido |
| Stripe | Proveedores de los países donde Stripe abre cuentas | Checkout alojado, medios guardados, reembolsos, disputas y más de 135 monedas | Incluido (4.4), con medios guardados (4.5) |
| Cripto | Cualquiera | CryptoCloud (Trybit desde junio de 2026), solo USD, sin reembolsos ni recurrencia | Incluido |

Criterio para venir de fábrica: API pública con página de pago alojada (el proveedor nunca toca datos de tarjeta),
avisos firmados, consulta del pago por ID, sandbox y alta de cuenta sin acuerdo comercial previo. Lo que pide
contrato o habilitación (Payway, dLocal para empresas, los Pagos Automáticos de Mercado Pago) va como módulo
opcional, y MODO solo llega a través de un procesador (4.7). La lista cubre lo imprescindible en Argentina (Mercado Pago,
transferencia y PayPal) y suma Stripe y cripto.

**Stripe en Argentina.** Stripe no abre cuentas en Argentina: en Latinoamérica solo opera en Brasil y México (página
de disponibilidad global de Stripe, consultada el 2026-10-28). Un proveedor argentino no lo puede habilitar con su
sociedad, y usarlo con una entidad de otro país es una decisión del contador,
ante ARCA. Además, un cliente argentino que paga con tarjeta a un comercio del exterior suma la percepción del 30 %
de la RG 5617/2024. Igual viene de fábrica: es el medio principal de los
proveedores de hosting de Estados Unidos, Europa, Brasil y México, y su sandbox no pide contrato, así que es donde se
construye y se prueba el débito con medio guardado. Cobra en pesos (sin American Express), así que un proveedor del
exterior puede facturar en pesos a clientes argentinos.

### 4.2 Receta y alta automática

Hoy la receta chica trae solo `arca` y `mercadopago`, y el alta de cada instalación es manual:
URL, secreto de firma y la huella que imprime `preparar.sh`.

- `compose.yaml` suma `paypal`, `stripe` y `cryptocloud` con el mismo bloque que `mercadopago`: imagen
  `${BILLING_<MÓDULO>_IMAGE}:${BILLING_VERSION}`, contenedor endurecido, secreto de firma y certificado TLS propios,
  volumen de datos y dos redes, la interna hacia el núcleo y la de salida. El núcleo suma cada red interna a las
  suyas.
- `preparar.sh` genera el secreto y el certificado de cada módulo en los bucles que ya tiene.
- **Alta automática.** El núcleo recibe como archivos los secretos de firma y los certificados de los módulos
  incluidos. En `migrate` crea o actualiza cada instalación con su URL, secreto y huella, y lee `/v0/manifest`. El
  staff nunca copia huellas ni secretos de firma.
- Un manifiesto que cambia esquemas o pide más datos personales queda pendiente de aprobación del staff, como dice la
  sección 5 del protocolo.
- Todos los módulos corren aunque no tengan instancias: sin credenciales no hacen nada, y la web nunca toca el host
  para prenderlos. Si pesan demasiado, se prueba el recolector de estación (`DOTNET_gcServer=0`) y se mide.
- La salida por destino se limita con el firewall del host o con un proxy de salida, porque compose solo separa
  redes. Todo por 443: `api.mercadopago.com`, `api-m.paypal.com` y `api-m.sandbox.paypal.com`, `api.stripe.com` y
  `api.trybit.com`.
- Con varias réplicas, el registro de idempotencia compartido en PostgreSQL (rama integracion) y la clave que el
  módulo pasa a la pasarela evitan repetir un efecto. Stripe y PayPal aceptan la clave, y Mercado Pago en pagos y
  reembolsos. CryptoCloud no, pero crear una factura de cripto no mueve plata.

### 4.3 Asistente de cobros

Sección Cobros del admin, con una tarjeta por pasarela incluida y su estado: sin configurar, en prueba, en
producción o con error.

| Paso | Qué pasa |
|---|---|
| 1. Ambiente | Prueba y producción son instancias distintas, como en ARCA. Stripe lo deduce del prefijo de la clave, PayPal y CryptoCloud lo eligen en la configuración y Mercado Pago usa credenciales de prueba |
| 2. Credenciales | El formulario sale del `config_schema` del módulo, y los campos secretos no se vuelven a mostrar. Cada campo dice de dónde sale, con el enlace al panel de la pasarela (`x-help` en el manifiesto, con el texto que hoy está en cada README) |
| 3. Avisos | La URL exacta `{BILLING_PUBLIC_URL}/webhooks/{instancia}` para copiar y la lista de eventos que declara el manifiesto (`webhook_events`) |
| 4. Probar conexión | `instance/test`, que el protocolo ya exige, con `webhook_url` para que el módulo compruebe que el aviso apunta al núcleo. Muestra cuenta, modo, si encontró el aviso y qué permisos faltan |
| 5. Pago de prueba | Solo en instancias de prueba. Abre un checkout por el mínimo, espera el aviso y muestra el pago verificado. No se registra como pago, no toca facturas ni saldo y no emite comprobante |
| 6. Activar | Qué monedas ofrece (las del manifiesto, campo `currencies`, cruzadas con las del proveedor), nombre y orden en el portal, si cancela en la misma moneda extranjera para la RG 5616 (lo define el contador) y, si el módulo lo declara, el débito automático con su política |

Los hosts que el navegador puede visitar los propone el manifiesto por ambiente (`browser_hosts`), así el staff no
los escribe. El asistente avisa cuando PayPal no aparece en facturas en pesos, cuando hay una clave de prueba en una
instancia de producción y cuando una instancia de producción no tiene el aviso verificado.

Mientras Hornero y WHMCS usen la misma cuenta de una pasarela (un staging con credenciales reales, o el corte
con órdenes viejas en curso): PayPal marca sus órdenes con `reference_id` igual a `billing-oss` y no captura las del
otro sistema, CryptoCloud necesita un proyecto aparte y ningún módulo registra pagos sin intento propio. En Mercado
Pago, los avisos del panel van a una sola URL, así que Hornero se apoya en el `notification_url` de cada
preferencia y en la conciliación horaria. Falta comprobar que esos avisos por preferencia lleven firma.

### 4.4 Módulo de Stripe

**Contrato.** El mismo que el README de Mercado Pago: un servicio aparte que habla la sección 9.3 del protocolo, con
`HttpClient` y `System.Text.Json`, sin la SDK de Stripe y sin configuración propia. Capacidades: `payment.core`,
`payment.checkout`, `payment.webhooks`, `payment.refund` y `payment.reconcile`, más `payment.saved_methods` cuando se
apruebe (4.5). No declara `payment.subscriptions`: quien factura es el núcleo, así que no se usan Stripe Billing,
Stripe Tax ni las facturas de Stripe.

**Configuración de la instancia.**

| Campo | Secreto | De dónde sale |
|---|---|---|
| `api_key` | Sí | Clave restringida (`rk_live_` o `rk_test_`) creada en el panel de Stripe, con escritura en Checkout Sessions, PaymentIntents, Customers, PaymentMethods y Refunds, y lectura en Charges, Disputes, saldo y movimientos y Webhook endpoints. Stripe desaconseja la clave secreta `sk_` para integraciones nuevas: se acepta con un aviso |
| `webhook_secret` | Sí | El `whsec_` del destino de eventos que apunta a `/webhooks/{instancia}` |

- El ambiente sale del prefijo de la clave, e `instance/test` lo informa como `livemode`.
- URL fija `https://api.stripe.com`, sin seguir redirecciones, así la clave no viaja a otro destino.
- Versión de API fijada en cada pedido con `Stripe-Version: 2026-09-30.endive`, la vigente al 2026-10-28.
- Salida solo a `api.stripe.com` por 443. Host para el navegador: `checkout.stripe.com`.

**`create_checkout`.** `POST /v1/checkout/sessions` con `mode=payment` y la página alojada de Stripe.
- `attempt_id` viaja como `client_reference_id` (hasta 200 caracteres), en `metadata` y en
  `payment_intent_data.metadata`, así el PaymentIntent lo lleva y `get_payment` lo devuelve como
  `external_reference`.
- Cada ítem es una línea con `price_data` (moneda en minúsculas, `unit_amount` en unidades menores y el título como
  nombre). Stripe admite hasta 100 líneas: con más, va una sola por el total. La suma tiene que dar `amount.value`, y
  si no, la respuesta es `invalid_input` sin llamar a Stripe.
- `success_url` es `return_urls.success` y `cancel_url` es `return_urls.failure`, o la de éxito si falta.
  `notification_url` se ignora, porque en Stripe los avisos son de la cuenta.
- La sesión vence a las 24 horas, el máximo y el valor por defecto (el mínimo es 30 minutos).
- Sin precios adaptativos ni impuestos automáticos, para que moneda y monto sean los del intento. El idioma sale de
  `Accept-Language` (`es-419` para castellano).
- El `Idempotency-Key` del núcleo pasa a Stripe tal cual.
- Con `save_method` (4.5): `payment_intent_data.setup_future_usage=off_session`, solo tarjetas, y el `customer` que
  manda el núcleo o uno nuevo creado sin datos personales (solo el ID de cuenta en `metadata`), que vuelve en
  `external_ids.customer`. Con ese ajuste, Checkout le avisa al cliente que el medio se guarda para pagos futuros.
- Respuesta: `redirect_url` (solo si es https) y `checkout_external_id` (el `cs_` de la sesión).

**`get_payment`.** Entrada `{ "payment_external_id": "pi_..." }`. Lee `GET /v1/payment_intents/{id}` expandiendo
`latest_charge.balance_transaction` y `payment_method`, y `GET /v1/disputes` con `payment_intent`.
- `amount` es `amount_received` si cobró, `refunded` es `amount_refunded` del cargo y `approved_at` es la hora del
  cargo.
- `fee` es la comisión del movimiento de saldo si está en la moneda del pago. Es nula mientras el movimiento no
  existe (la captura por defecto es asíncrona) o si se liquidó en otra moneda, igual que en PayPal.
- `external_reference` es `metadata.attempt_id`, `gateway_status` es el estado del PaymentIntent y
  `gateway_status_detail`, el `decline_code` del último error.
- Si el pago guardó un medio, `saved_method` trae `method_external_id` (`pm_`), `customer_external_id` (`cus_`),
  `type`, `brand`, `last4`, `exp_month` y `exp_year`, y nada más de la tarjeta.

| Stripe | Núcleo |
|---|---|
| `succeeded` sin reembolsos ni disputas | `approved` |
| `succeeded` con una parte reembolsada | `partially_refunded` |
| `succeeded` con todo reembolsado | `refunded` |
| Disputa en `needs_response`, `under_review`, `warning_needs_response` o `warning_under_review` | `disputed` |
| Disputa `lost` | `charged_back` |
| Disputa `won`, `warning_closed` o `prevented` | El estado que corresponde sin la disputa |
| `processing`, `requires_action`, `requires_confirmation` o `requires_payment_method` | `pending` |
| `canceled` | `rejected` |

Un estado desconocido da `upstream_unavailable`, como en Mercado Pago. `requires_payment_method` es `pending` porque
el cliente puede reintentar mientras la sesión está abierta, y el núcleo no deja pasar de rechazado a aprobado.

**`verify_webhook`.** Misma entrada que en Mercado Pago (encabezados, query y `body_base64`). Verificación local:
1. Del encabezado `Stripe-Signature` se toman `t` y todas las firmas `v1`. Los otros esquemas se ignoran.
2. HMAC-SHA256 en hexadecimal, con `webhook_secret` como clave, sobre `t`, un punto y el cuerpo crudo, comparado en
   tiempo constante con cada `v1`. Al rotar el secreto, Stripe firma con los dos hasta 24 horas, así que alcanza un
   solo secreto en la configuración.
3. Stripe sugiere rechazar marcas de más de 5 minutos, pero el núcleo verifica después de recibir: con
   `received_at`, que el núcleo manda con cuándo llegó el aviso, se aplican 5 minutos contra la hora de recepción,
   y sin él no hay ventana, como en Mercado Pago (que con `received_at` aplica 24 horas).

| Evento | `payment_external_id` |
|---|---|
| `checkout.session.completed`, `async_payment_succeeded`, `async_payment_failed` y `expired` | `data.object.payment_intent`. Sin PaymentIntent no hay evento |
| `payment_intent.succeeded`, `payment_failed`, `processing` y `canceled` | `data.object.id` |
| `charge.refunded`, `refund.created`, `refund.updated`, `refund.failed` y `charge.dispute.created`, `updated`, `closed`, `funds_withdrawn` y `funds_reinstated` | `data.object.payment_intent` |
| `payment_method.detached` y `automatically_updated` | `method_external_id`, si declara medios guardados |

El ID de evento es el `evt_` que Stripe repite en cada reintento (hasta 3 días en producción). Stripe no garantiza el
orden y a veces manda dos eventos por el mismo cambio: lo absorbe que el núcleo siempre confirma con `get_payment`.

**`refund`.** Consulta el PaymentIntent y compara su moneda con la pedida: si difiere es `invalid_input` y no se
reembolsa. Después, `POST /v1/refunds` con `payment_intent`, `amount` en unidades menores (sin monto es total) y el
`Idempotency-Key` del núcleo. `succeeded` es `approved`, `pending` y `requires_action` son `pending`, y `failed` y
`canceled` son `rejected` con `failure_reason` en `details.upstream`: la plata no salió. Errores como
`charge_already_refunded` también dan `rejected`.

**`list_payments`.** Concilia por movimientos de saldo, como PayPal por transacciones: `GET /v1/balance_transactions`
con `created[gte]`, `created[lt]`, `limit=100` y `expand[]=data.source`. Los tipos `charge` y `payment` son cobros,
`refund`, `payment_refund` y `payment_failure_refund` llevan al reembolso y `adjustment` a la disputa, y todos llevan
a su PaymentIntent, que se informa con la forma de `get_payment`. Stripe pagina con cursor (`starting_after`), así
que hace falta el campo opcional `next_cursor` en el protocolo.

**Disputas.** Stripe saca la plata del saldo al abrirse la disputa y la devuelve si se gana. El núcleo revierte
imputaciones recién con `charged_back`, así que mientras dura la disputa la factura figura paga y la plata no está:
el panel muestra el pago en disputa. La evidencia se presenta en el panel de Stripe, y la comisión por disputa no
entra en `fee`. Las consultas previas (`warning_*`) no mueven plata, salen como `disputed` y el staff puede
reembolsar mientras el cargo lo permita.

**`instance/test`.** `GET /v1/balance` para validar la clave y conocer el modo, y `GET /v1/webhook_endpoints` para
comprobar que hay un destino activo hacia el `webhook_url` de la instancia con los eventos de la tabla. Devuelve
`livemode`, `webhook_url` y `event_types`. Si falta el destino o un evento, responde `invalid_config` con
`webhook_secret`, como hace PayPal con su `webhook_id`.

**Monedas.** Stripe espera enteros en la unidad menor:
- Dos decimales por defecto: 10.99 USD va como 1099.
- Sin decimales: BIF, CLP, DJF, GNF, JPY, KMF, KRW, MGA, PYG, RWF, VND, VUV, XAF, XOF y XPF.
- ISK y UGX van con dos decimales siempre en cero y no admiten fracciones. HUF y TWD se cobran con dos decimales.
- Un importe con más decimales que los de su moneda es `invalid_input`, como en PayPal.
- Los mínimos dependen de la moneda de liquidación y el módulo no los replica: el `amount_too_small` de Stripe sale
  como `invalid_input` en `amount`. El manifiesto declara todas las monedas y la tabla de decimales vive en el módulo.

**Errores.**

| Stripe | Protocolo |
|---|---|
| 401 | `invalid_config` con `api_key` |
| 403, permiso que le falta a la clave restringida | `invalid_config` con `api_key` y el permiso en el mensaje |
| 404 | `not_found` |
| 400 `invalid_request_error` con `param` | `invalid_input` con el campo en `details.fields` |
| 400 por reglas de negocio, como `charge_already_refunded` | `rejected` con el código en `details.upstream` |
| 402 `card_error` | `rejected` con `code`, `decline_code`, `advice_code` y `network_decline_code` en `details.upstream` |
| `idempotency_error`, misma clave con otros parámetros | `idempotency_conflict` |
| 409, otro pedido en curso con la misma clave | `conflict`, reintentable |
| 429 | `rate_limited` con `Retry-After` o 30 segundos |
| 424, 5xx, corte o respuesta que no es JSON después de enviar | `outcome_unknown` si el pedido mueve algo, `upstream_unavailable` si es una consulta |

Un detalle que cambia el reintento: Stripe también guarda las respuestas 500 bajo la clave de idempotencia, así que
repetir con la misma clave devuelve el mismo 500. Ante un 500 en un pedido con efecto, el módulo busca primero si el
objeto existe (los PaymentIntents del cliente por fecha con el listado, que es inmediato) y solo si no existe pide
con una clave derivada. Stripe puede borrar las claves desde las 24 horas: pasado ese plazo el núcleo consulta antes
de repetir, como dice la sección 6 del protocolo.

**Fuera de esta versión:** Stripe Billing y Stripe Tax, pago embebido con Elements, débitos bancarios con mandato
(SEPA o ACH), Connect, avisos tempranos de fraude, presentación de evidencia de disputas y datos del pagador, que
pedirían `account.contact`.

**A comprobar en el sandbox de Stripe:** el nombre exacto del ajuste que apaga los precios adaptativos, si una sesión
vencida cancela su PaymentIntent, si un reembolso que falla después de aprobado baja `amount_refunded`, cuánto tarda
el movimiento de saldo con la captura asíncrona, si los destinos creados con la API v2 aparecen en el listado de
`/v1/webhook_endpoints` y el texto del acuerdo de reutilización en `es-419`.

### 4.5 Cobro automático de renovaciones

**Qué se puede hacer hoy en Argentina.**

| Camino | Cómo funciona | Para quién |
|---|---|---|
| Suscripción de Mercado Pago | El cliente aprueba en Mercado Pago y Mercado Pago cobra solo cada período, con dinero en cuenta, tarjeta, línea de crédito, Rapipago o Pago Fácil. Si una cuota se rechaza, reintenta hasta 4 veces en 10 días, y después de 3 cuotas rechazadas da de baja la suscripción | Proveedores argentinos |
| Pagos Automáticos de Mercado Pago | Tarjeta guardada sin pedir el código de seguridad en cada cobro, con cobros iniciados por el comercio. Pide autorización del equipo comercial y capturar la tarjeta en la página del proveedor (Checkout API o Bricks) | Cuando Mercado Pago lo habilite y el portal admita el pago embebido |
| PayPal | Guardar medios no está disponible para cuentas argentinas: solo en Estados Unidos, Canadá, Australia, Japón, Singapur, Hong Kong, China y 28 países europeos | Proveedores de esos países |
| dLocal Go | Acepta empresas registradas en Argentina (artículo de su centro de ayuda del 29/07/2026) y guarda un token en su página alojada, que sirve para cobros recurrentes | Candidato a evaluar |

**Suscripción de Mercado Pago.** Usa `payment.subscriptions`, que el protocolo ya tiene.
- El cliente elige "Débito automático con Mercado Pago" en el portal, con la casilla de consentimiento (abajo). El
  núcleo crea la suscripción sin plan, mensual, por el total recurrente de la cuenta en pesos y con inicio en el
  próximo vencimiento de esos servicios mensuales, y redirige a Mercado Pago para que el cliente la apruebe. Los
  ejemplos oficiales llevan el correo del pagador, así que el módulo pide `account.contact` en `data_scopes`.
- Cada cobro llega como pago con la referencia de la suscripción. Hoy un pago sin intento propio no se registra: el
  núcleo registra el de una suscripción suya (por el ID de Mercado Pago o, si no viene, por el `external_reference`)
  y lo imputa primero a las facturas impagas de esa cuenta en pesos con un servicio mensual y después a las demás, las
  más viejas primero. Lo que sobra queda como saldo a favor. Lo registra aunque el débito por suscripción esté apagado:
  el ajuste corta ofrecerlas y cambiarlas, no la plata que una vigente ya cobró.
- La fecha la pone Mercado Pago, así que el núcleo no puede comprobar la factura justo antes de cobrar (la regla de
  la sección 8 del modelo vale para los cobros que inicia el núcleo). Si el cliente pagó por otro medio, el débito
  queda como saldo a favor y se usa en la próxima factura.
- La suscripción cubre los servicios mensuales en pesos de la cuenta. Un servicio de otro ciclo o en otra moneda se
  paga con su factura, como hoy. Si los servicios mensuales vencen en días distintos, el débito igual entra como pago
  o como saldo a favor, que se aplica al emitir la factura siguiente (en la rama integracion, el saldo a favor se
  consume al emitir).
- Cuando cambia el total (un servicio nuevo, un precio, un servicio en dólares facturado en pesos con la cotización
  del dólar MEP, que es la que se usa para facturar), un trabajo actualiza el monto antes del próximo cobro. Falta comprobar que la API de Mercado Pago lo permite sobre la
  suscripción existente: si no, hay que crear otra con un consentimiento nuevo. Esa operación es una capacidad nueva,
  `payment.subscription_update`.
- La baja de la suscripción por rechazos llega como evento y se le avisa al cliente.
- El importador releva las suscripciones vigentes del sistema anterior, para que sus cobros se imputen por referencia
  después del corte. Esa imputación se activa recién en el corte, con un ajuste apagado por defecto: un staging con
  credenciales reales de Mercado Pago no registra cobros de verdad.

**Capacidad `payment.saved_methods`, para el producto.** La sección 13 del protocolo deja fuera de v0 los medios
guardados. Para que el núcleo decida cuándo y cuánto cobrar, y compruebe la factura antes de cobrar, se suma una
capacidad. Es aditiva según la sección 3 del protocolo: campos opcionales y operaciones nuevas detrás de una
capacidad nueva.
- `create_checkout` acepta `save_method: true` y `customer_external_id`, si el núcleo ya tiene uno para esa cuenta e
  instancia. El módulo le pide a la pasarela que guarde el medio para cobros iniciados por el comercio y devuelve el
  cliente creado en `external_ids.customer`. El consentimiento lo junta el núcleo antes de redirigir.
- `get_payment` devuelve `saved_method` cuando el pago dejó un medio reutilizable. Nunca el número completo ni el
  código de seguridad.
- `charge_saved` (nueva) cobra sin el cliente presente. Entrada: `attempt_id`, `amount`, `description`,
  `customer_external_id` y `method_external_id`, con `Idempotency-Key` igual a `autocharge-{intento}`. Devuelve lo
  mismo que `get_payment`. Si la pasarela rechaza, responde `rejected` con sus códigos, `details.retry` (`later`,
  `never` o `customer_action`) y, si la pasarela da una página para que el cliente resuelva, `customer_action_url`
  (https y con un host de la instancia).
- `delete_method` (nueva) borra el medio en la pasarela. Repetirla, o pedirla sobre uno ya borrado, da éxito.
- `verify_webhook` puede devolver eventos de medio (`method.updated`, `method.deleted`), por ejemplo cuando la red
  actualiza la tarjeta o el cliente la quita desde la pasarela.
- `charge_saved` mueve plata, así que vale el punto 5 de la sección 6 del protocolo: el módulo pasa la clave a la
  pasarela y, si no tiene respuesta cierta, busca el cobro por la referencia del intento antes de repetir. Un módulo
  que no puede hacer ninguna de las dos cosas no declara la capacidad.
- Segunda etapa, cada una con su capacidad: `payment.method_setup` para agregar o cambiar el medio sin pagar una
  factura, y `payment.webhook_setup` para que el asistente cree el aviso en la pasarela y guarde su secreto.

| Pasarela | Guardar | Cobrar sin el cliente |
|---|---|---|
| Stripe | Checkout con `setup_future_usage=off_session` | PaymentIntent con `off_session=true` y `confirm=true` |
| PayPal, solo cuentas de los países del vault | Token de configuración con aprobación por enlace, o `store_in_vault` al pagar | Orders v2 con `payment_source.paypal.vault_id` |
| dLocal Go | Pago con `allow_recurring: true`, que devuelve `merchant_checkout_token` | `POST /v1/payments/recurring/{token}`, que si falla devuelve una URL para el cliente |
| Mollie | Primer pago con `sequenceType=first`, que crea un mandato | Pago con `sequenceType=recurring` y el mandato |
| Mercado Pago, Pagos Automáticos | Captura embebida con habilitación comercial | `POST /v1/payments` con `CREDENTIAL_ON_FILE` e iniciador `merchant` |

**En el núcleo.**
- Medio guardado (`SavedPaymentMethod`): cuenta, instancia, IDs externos del cliente y del medio, tipo, marca,
  últimos 4 dígitos, vencimiento, estado (activo, vencido o borrado) y el consentimiento. Único por instancia e ID
  externo del medio. La cuenta elige uno para el débito automático.
- El intento de cobro suma el medio usado y quién inicia el cobro (cliente o comercio). La imputación no cambia,
  porque el pago se encuentra por la referencia del intento.
- Política de débito por proveedor, con excepción por producto: días de los intentos respecto del vencimiento, tope
  y hora del día.

**Flujo**, según D-07: el planificador encola con clave única y el trabajo corre por factura, de a uno. Toda llamada a
la pasarela sale de un trabajo, fuera de la transacción (sección 13 del modelo).
1. El correo de la factura de renovación dice cuánto se va a debitar, cuándo y con qué medio (por ejemplo, Visa
   terminada en 4242).
2. El día del intento, en una transacción corta, se comprueba que la factura siga con saldo, que el débito siga
   activo y que no haya otro intento abierto sobre la factura, con ningún medio. Se crea el intento por el saldo
   pendiente, y un trabajo llama a `charge_saved` con `autocharge-{intento}`.
3. Con `approved`, el pago sigue el camino de siempre. Con `pending`, se espera el aviso o la conciliación. Si el
   último intento del trabajo sigue sin respuesta cierta, el intento queda incierto y lo resuelve la conciliación.
   Nunca se pide con otra clave a ciegas. Un intento que pudo haber cobrado no se cierra aunque el cliente cambie la
   tarjeta o pague por otro medio, así no se abre otro sobre la misma factura. El intento queda anotado antes de
   pedir, así un nodo que se apaga en el medio del pedido lo deja como uno que pudo haber cobrado. Si el pedido no
   salió (sin conexión con el módulo o frenado por la pasarela) el intento no queda incierto: falla y la factura
   vuelve otro día.
4. Con `rejected` se mira `details.retry`: `later` reprograma según la política, `never` corta y le pide al cliente
   otro medio, y `customer_action` corta y le manda el enlace de pago o el de la pasarela. Un fraude o una tarjeta
   perdida o robada se le muestran al cliente como rechazo genérico, como pide Stripe.
5. Agotados los intentos, la factura sigue la mora de la sección 5.2 del modelo.

**Reintentos.** Por defecto, el día del vencimiento y a los 1, 3 y 5 días: 4 intentos, y solo ante rechazos que
admiten reintento. Es la mitad del máximo de 8 que recomienda Stripe y se parece a lo que hace Mercado Pago en sus
suscripciones. Con una gracia de 1 día, la política va 3, 2 y 1 día antes del vencimiento y el mismo día,
así un rechazo pasajero no suspende el servicio.

**Reglas extra.**
- Un contracargo o una disputa sobre un débito automático apaga el débito de esa cuenta y avisa al staff: casi
  siempre el cliente desconoció el cargo.
- Después de un failover, el débito espera la conciliación de pagos de D-14, igual que las emisiones fiscales.
- Mientras hay un intento automático en curso, el portal no ofrece pagar esa factura por otro medio. Si igual entran
  dos pagos, el segundo queda como saldo a favor.
- El reembolso de un débito sigue D-05: primero la nota de crédito y después la plata.
- El importador no trae medios guardados, salvo IDs de la misma cuenta de la pasarela que estén en claro en la copia.
  En ese caso el consentimiento queda marcado como importado.

**Consentimiento.** Stripe es la más explícita de las pasarelas: términos que digan cuándo, con qué frecuencia y cómo
se calcula el monto, uso del medio solo para eso, consentimiento explícito y constancia guardada. El aviso que muestra
Checkout no reemplaza la casilla propia.
1. En el portal, antes de redirigir, una casilla destildada y separada de los términos generales: "Autorizo a
   {proveedor} a cobrar automáticamente con este medio las facturas de mis servicios en cada vencimiento, por el
   importe de cada factura. Me van a avisar por correo antes de cada débito. Puedo quitar el medio o desactivar el
   débito cuando quiera desde Medios de pago." Con un enlace a las condiciones del débito automático.
2. Se guarda quién aceptó, cuándo, desde qué IP y navegador, la huella del texto exacto y el medio, en la auditoría y
   mientras exista el medio, más el plazo de conservación de la cuenta. Es la prueba ante una disputa: el titular de
   una tarjeta puede cuestionar el resumen dentro de los 30 días de recibido (Ley 25.065, art. 26).
3. Revocación por el mismo medio y sin trámites (Ley 24.240, art. 10 ter): "Quitar" en Medios de pago llama a
   `delete_method` y apaga el débito en el momento. La baja de la Disposición 954/2025 también corta el débito de los
   servicios dados de baja.
4. Aviso previo en cada débito, dentro del correo de la factura, y otro 30 días antes de que venza la tarjeta.
5. Para clientes del Espacio Económico Europeo, el primer cobro se hace con autenticación y los siguientes como
   iniciados por el comercio. Si el banco igual pide autenticación, el cliente recibe el enlace para pagar.

El token y los últimos 4 dígitos son datos personales, y Stripe y PayPal los procesan en Estados Unidos, que la AAIP
no lista como país adecuado. Lo tiene que decir la política de privacidad del
proveedor: es un tema para su abogado, no para el código.

### 4.6 Transferencias

El texto ordenado del BCRA sobre transferencias (al 07/08/2025, con la Comunicación A 8295) obliga a informar al que
recibe el importe, la fecha, el nombre y el CUIT, CUIL o CDI del ordenante y una referencia unívoca (punto 3.2.1.2),
disponibles en los canales electrónicos dentro de las 24 horas (3.2.2.3). Las transferencias inmediatas se resuelven
en no más de 15 segundos (2.2.12), y entre los conceptos está FAC, facturas (3.1.2). Esa es la llave de conciliación,
y no hace falta módulo, porque la sección 9.3 del protocolo ya dice que los pagos manuales no lo usan.

1. Cuentas para transferir, en los ajustes: banco, titular, CUIT, CBU o CVU de 22 dígitos, alias y moneda. El portal
   las muestra con el importe exacto y el número de factura, y pide transferir desde una cuenta del CUIT o CUIL de la
   ficha, con concepto FAC.
2. "Ya transferí" deja un aviso para el staff, con comprobante opcional. Ese aviso no es un pago.
3. Importación del extracto desde el panel o por la API, en CSV con un mapa de columnas por banco. Cada movimiento
   entra por `POST /api/v1/payments` como pago manual de transferencia, con la referencia unívoca del banco como ID
   externo, así reimportar no duplica.
4. Conciliación sugerida: si el CUIT del ordenante es el de una cuenta y el importe coincide con el saldo de una o
   varias facturas, se propone la imputación. Con una sola coincidencia exacta se imputa sola, si el proveedor lo
   habilita. Lo demás va a una bandeja de pagos sin identificar, y lo que identifica la cuenta pero no la factura
   queda como saldo a favor de esa cuenta.

Para después, ninguno de fábrica porque piden contrato: un CVU por cliente con un proveedor de servicios de pago (un
módulo con `payment.core`, `payment.webhooks` y `payment.reconcile`) y el DEBIN recurrente, que el pagador puede
desconocer durante 30 días.

### 4.7 Otras pasarelas a evaluar

| Pasarela | Qué es | Encaje |
|---|---|---|
| dLocal Go | Cobros para pymes, abre a empresas de 17 países, Argentina incluida. Pago con página alojada, consulta, reembolsos, contracargos, suscripciones y pagos recurrentes con token. Avisos firmados con HMAC-SHA256 que se reintentan cada 10 minutos hasta 30 días | El mejor candidato para débito con tarjeta guardada en Argentina por redirección. Faltan tarifas, medios locales y si acepta clave de idempotencia en el cobro recurrente. Se evalúa después del corte (decidido el 2026-10-28) |
| Payway (Prisma) | Adquirente argentino. API REST con captura de tarjeta en el navegador si el comercio no tiene PCI, links de pago y devoluciones | Pide alta comercial. Por redirección, solo con links de pago. Sin recurrencia documentada |
| MODO | Billetera de los bancos, sin portal propio legible | Solo a través de un procesador, no como módulo propio |
| dLocal para empresas | Cobros en mercados emergentes, con contrato | Para proveedores grandes, a pedido |
| Mollie | Europa, con mandatos y suscripciones | Segundo módulo si aparecen proveedores europeos |
| BTCPay Server | Cripto sin custodia, licencia MIT, se aloja aparte. Facturas por API Greenfield, avisos firmados y reembolso por API | Alternativa libre a CryptoCloud. La URL es configurable, así que vale la regla de reautenticar de la sección 4 del protocolo |

### 4.8 Actualizaciones de los módulos de cobro

Siguen el diseño de actualizaciones (D-20): los módulos propios salen en la misma
versión que el núcleo, por el mismo canal firmado, y un manifiesto que cambia esquemas o pide más datos frena la
actualización automática hasta que el staff lo aprueba. Lo propio de los cobros:
- **Versión de API fijada en el módulo.** Stripe va con `Stripe-Version` en cada pedido y con la misma versión al
  crear el destino de avisos, porque si no los eventos salen con la versión por defecto de la cuenta. Stripe saca una
  versión por mes sin cambios incompatibles y dos mayores por año: subir la mensual es un parche, y pasar a una mayor,
  una minor de Hornero. PayPal y Mercado Pago versionan por ruta.
- **Cambios del lado de la pasarela** que obligan a un parche aunque el núcleo no cambie, como el paso de CryptoCloud
  a Trybit en junio de 2026, con otro host.
- **Detección temprana.** Las pruebas contra el sandbox de cada módulo (`MercadoPagoReal`, `PayPalReal`,
  `CryptoCloudReal` y un `StripeReal` nuevo) corren en una tarea programada de la integración continua, con
  credenciales de prueba como secretos, igual que la homologación de ARCA en D-16.
- **Sin pérdida durante la actualización.** Los avisos llegan al núcleo, que los guarda crudos, y la verificación es
  un trabajo que reintenta. Además las pasarelas reintentan: Stripe hasta 3 días, PayPal 25 veces en 3 días, Mercado
  Pago cada 15 minutos y dLocal Go cada 10 minutos hasta 30 días.
- **Sin cobros dobles.** Un `charge_saved` cortado por la actualización se repite con la misma clave.
- Los tokens de los medios viven en la pasarela: actualizar o volver atrás un módulo no los toca.
- **Rotación de credenciales** desde el asistente, sin corte. Stripe deja convivir la clave vieja y la nueva hasta 7
  días, y firma los avisos con los dos secretos hasta 24 horas.

### 4.9 Cambios aditivos al protocolo

Todos son campos opcionales, operaciones nuevas detrás de capacidades nuevas o datos del manifiesto (sección 3 del
protocolo):

| Cambio | Para qué |
|---|---|
| `parameters` del tipo fiscal suma `points_of_sale` y `tax_types` | Prueba sin emitir y tributos (2.6 y 3.8) |
| `instance/test` acepta `fresh_login` y `webhook_url`, y devuelve `authorize_enabled` | Renovar el certificado, comprobar avisos y mostrar la traba del módulo |
| `authorize` del tipo fiscal suma `taxes` | Percepciones (3.8) |
| `wsaa_cms` en lugar de la clave, detrás de una capacidad del módulo de ARCA | Firma del login en el núcleo (2.9) |
| `fiscal.numbering` | Fiscos que asignan el número (3.9) |
| Tipo `tax` con `calculate` | Motor externo de impuestos (3.9) |
| `payment.saved_methods` con `charge_saved` y `delete_method` | Débito con medio guardado (4.5) |
| `payment.subscription_update` | Cambiar el monto de una suscripción (4.5) |
| `payment.method_setup` y `payment.webhook_setup` | Segunda etapa del débito y del asistente |
| `next_cursor` en `list_payments` y `received_at` en `verify_webhook` | Stripe (4.4). `GatewayPayments.Reconcile` ya recorre páginas, así que es seguir el cursor cuando venga |
| `currencies`, `browser_hosts`, `webhook_events` y `x-help` en el manifiesto | Asistente de cobros (4.3) |

---


## 6. A confirmar

- En ARCA: los nombres en pantalla de los servicios de exportación y de padrón en el Administrador de Relaciones, los
  sistemas de facturación del alta de punto de venta, si en homologación hace falta dar de alta el punto de venta, si
  en producción se puede sumar un certificado a un alias existente y que el padrón esté disponible para cualquier
  contribuyente.
- En la prueba de homologación: el código de tributo de la percepción de no categorizados y la tolerancia por
  alícuota en la práctica.
- Con un contador: las consultas impositivas abiertas, que el régimen de transparencia no alcance a los monotributistas
  (3.5) y qué muestran como precio sin impuestos nacionales.
- En Mercado Pago: si la suscripción deja cambiar el monto y si los avisos por preferencia llevan firma.
- En el sandbox de Stripe: la lista del final de 4.4.

---

## Fuentes

Consultadas el 2026-10-28, salvo que se indique otra fecha.

**ARCA: certificados, WSAA y factura electrónica**

| Fuente | Fecha |
|---|---|
| Acciones a realizar para consumir un Webservice de Factura Electrónica: `https://www.arca.gob.ar/fe/documentos/AccionesarealizarparaconsumirunWebservicedeFacturaElectr.pdf` | Sin fecha |
| WSASS, manual del usuario (cómo generar una solicitud, crear un certificado, crear una autorización y agregar un certificado a un alias): `https://www.afip.gob.ar/ws/WSASS/html/index.html` y `https://www.afip.gob.ar/ws/WSASS/html/generarcsr.html` | Copyright 2017 |
| WSASS: cómo adherirse al servicio: `https://www.arca.gob.ar/ws/WSASS/WSASS_como_adherirse.pdf` | Publicación 19.9.25 |
| ¿Cómo obtener el Certificado Digital para entorno de producción?: `https://www.afip.gob.ar/ws/WSAA/wsaa_obtener_certificado_produccion.pdf` | Sin fecha |
| ¿Cómo asociar el Certificado Digital a un WSN?: `https://www.afip.gob.ar/ws/WSAA/wsaa_asociar_certificado_a_wsn_produccion.pdf` | Sin fecha |
| Documento técnico Delegación de Webservices con el Administrador de Relaciones, versión 1.1.0: `https://www.arca.gob.ar/ws/WSAA/ADMINREL.DelegarWS.pdf` | 17/05/2011 |
| WSAA, manual del desarrollador: `https://www.afip.gob.ar/ws/WSAA/WSAAmanualDev.pdf` | Publicación 20.2.19 |
| Certificados digitales para programadores: `https://www.arca.gob.ar/ws/programadores/certificados-digitales.asp` | Leída el 2026-10-28 |
| Manual del desarrollador de WSFEv1, versión 4.7: `https://www.afip.gob.ar/ws/documentacion/manuales/manual-desarrollador-ARCA-COMPG.pdf` | Revisión del 01/09/2026 |
| Manual del desarrollador de WSFEv1, versión 4.8, en homologación externa: `https://www.afip.gob.ar/fe/ayuda/homologacion_externa.asp` | Revisión del 01/12/2026 |
| Manual del desarrollador de WSFEX, versión 3.1.1: `https://www.afip.gob.ar/ws/documentacion/manuales/WSFEX-Manualparaeldesarrollador_V3.1.1_ARCA.pdf` | Portada del 18/08/2025 |
| Consulta a padrón, constancia de inscripción (`ws_sr_constancia_inscripcion`), versión 4.1: `https://arca.gob.ar/ws/WSCI/manual_ws_sr_ws_constancia_inscripcion.pdf` | Marzo de 2026 |
| Consulta a padrón alcance 13, versión 1.4: `https://arca.gob.ar/ws/ws-padron-a13/manual-ws-sr-padron-a13-v1.4.pdf` | 14/08/2026 |
| Catálogo de web services: `https://arca.gob.ar/ws/documentacion/catalogo.asp` | Leído el 2026-10-28 |
| ¿Qué datos deben contener los comprobantes?: `https://www.arca.gob.ar/fe/emision-autorizacion/datos-comprobantes.asp` | Leída el 2026-10-28 |
| Especificaciones del QR: `https://www.afip.gob.ar/fe/qr/documentos/QRespecificaciones.pdf` | 03/02/2025 |
| Guía de factura electrónica y alta de punto de venta: `https://arca.gob.ar/guias/paso-a-paso/Guia-Como-emito-factura-electronica-y-punto-de-venta.pdf` | Sin fecha |
| Micrositio de la Factura de Crédito Electrónica MiPyME, monto mínimo y listado de empresas grandes: `https://servicioscf.afip.gob.ar/facturadecreditoelectronica/default.asp` | Consultado el 2026-10-28: $5.549.862 desde el 14/04/2026 |

**Normas argentinas**

| Fuente | Fecha |
|---|---|
| Ley de IVA, texto ordenado por el Decreto 280/1997, arts. 1, 3, 10, 28, 39 y 43: `https://www.argentina.gob.ar/normativa/nacional/norma-42701/actualizacion` | Texto actualizado al 2026-10-28 |
| RG 1415/2003, texto actualizado, arts. 15, 16, 17 y 47 y anexo II: `https://www.argentina.gob.ar/normativa/nacional/resoluci%C3%B3n-1415-2003-81316/actualizacion` | Texto actualizado al 2026-10-28 |
| RG 5866/2026 de ARCA (sustituye el art. 47 y el anexo II, título II, apartado A, inc. d de la RG 1415 y abroga la RG 5824): `https://www.boletinoficial.gob.ar/detalleAviso/primera/343656/20260629` | BO 29/06/2026, vigente desde el 01/07/2026 |
| RG 5616/2024 de ARCA: `https://www.argentina.gob.ar/normativa/nacional/norma-407369/texto` | BO 18/12/2024 |
| RG 5614/2024 de ARCA, Régimen de Transparencia Fiscal al Consumidor: `https://www.argentina.gob.ar/normativa/nacional/resoluci%C3%B3n-5614-2024-407183/texto` | BO 13/12/2024 |
| Ley 27.743, título VII: `https://www.argentina.gob.ar/normativa/nacional/ley-27743-401268/texto` | BO 08/07/2024 |
| Res. 4/2025 de la Secretaría de Industria y Comercio: `https://www.boletinoficial.gob.ar/detalleAviso/primera/319787/20250117` | BO 17/01/2025 |
| RG 5003/2021: `https://www.boletinoficial.gob.ar/detalleAviso/primera/245186/20210602` | BO 02/06/2021 |
| RG 2126/2006, texto actualizado (art. 2, inc. b, con el texto de la RG 5738/2025, BO 31/07/2025): `https://servicios.infoleg.gob.ar/infolegInternet/anexos/115000-119999/119791/texact.htm` | Texto actualizado al 2026-10-28 |
| RG 4367/2018, Factura de Crédito Electrónica MiPyME: `https://biblioteca.afip.gob.ar/dcp/REAG01004367_2018_12_19` | BO 20/12/2018 |
| ARBA, RN 7/2026: `https://www.arba.gov.ar/archivos/Tramites/RN%207-2026.pdf` | 29/01/2026 |
| ARBA, pregunta frecuente para agentes de régimen general: `https://web.arba.gov.ar/preguntas-frecuentes/como-pueden-consultar-las-alicuotas-vigentes-los-agentes-de-recaudacion-de` | Actualizada el 28/01/2026 |
| AGIP, Res. 296/AGIP/2019, anexo I: `https://imagenes.agip.gob.ar/filemanager/source/Normativas/2019/20191120-Resol296-AGIP-19-Anexo-I.pdf` | 20/11/2019 |
| AGIP, Res. 169/AGIP/2026: `https://boletinoficial.buenosaires.gob.ar/normativaba/norma/851654` | BOCBA 30/04/2026 |
| AGIP, Res. 339/AGIP/2026: `https://boletinoficial.buenosaires.gob.ar/normativaba/norma/875535` | BOCBA 08/09/2026 |
| Comisión Arbitral, RG 14/2017: `https://www.argentina.gob.ar/normativa/nacional/norma-282070/texto` | Vista por búsqueda |
| BCRA, texto ordenado de transferencias: `https://www.bcra.gob.ar/Pdfs/Texord/t-snp-tr.pdf` | Al 07/08/2025 (Com. A 8295) |
| Ley 25.065 de Tarjetas de Crédito: `https://servicios.infoleg.gob.ar/infolegInternet/anexos/55000-59999/55556/texact.htm` | Texto actualizado al 2026-10-28 |
| Ley 24.240 y Disposición 954/2025 | Las de la sección 14 del [modelo de dominio](dominio.md) |

**Otros países**

| Fuente | Fecha |
|---|---|
| Comisión Europea, Explanatory notes on the 2015 place of supply changes: `https://taxation-customs.ec.europa.eu/system/files/2016-09/explanatory_notes_2015_en.pdf` | 03/04/2014 |
| Your Europe, One Stop Shop: `https://europa.eu/youreurope/business/finance-and-tax/vat/one-stop-shop/index_en.htm` | Last checked 13/05/2026 |
| Your Europe, VAT rules and rates: `https://europa.eu/youreurope/business/taxation/vat/vat-rules-rates/index_en.htm` | Last checked 13/07/2026 |
| Your Europe, Check a VAT number (VIES): `https://europa.eu/youreurope/business/finance-and-tax/vat/check-vat-number-vies/index_en.htm` | Last checked 18/06/2026 |
| Comisión Europea, VAT in the Digital Age (Directiva (UE) 2025/516): `https://taxation-customs.ec.europa.eu/taxation/vat/vat-digital-age-vida_en` | Directiva del 11/03/2025 |
| Directiva 2011/83/UE, consolidada: `https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=CELEX%3A02011L0083-20260927` | Consolidada al 27/09/2026 |
| HMRC, VAT Notice 741A: `https://www.gov.uk/guidance/vat-place-of-supply-of-services-notice-741a` | Actualizada el 29/09/2022 |
| HMRC, VAT rules for supplies of digital services to consumers: `https://www.gov.uk/guidance/the-vat-rules-if-you-supply-digital-services-to-private-consumers` | Actualizada el 28/03/2022 |
| HMRC, Check a UK VAT number API 2.0: `https://developer.service.hmrc.gov.uk/api-documentation/docs/api/service/vat-registered-companies-api/2.0` | Vista el 2026-10-28 |
| Corte Suprema de EE. UU., South Dakota v. Wayfair, No. 17-494: `https://www.supremecourt.gov/opinions/17pdf/17-494_j4el.pdf` | 21/06/2018 |
| Texas Comptroller, Publication 94-127: `https://comptroller.texas.gov/taxes/publications/94-127.php` | Vista por búsqueda |
| Streamlined Sales Tax Governing Board: `https://www.streamlinedsalestax.org/` | Vista el 2026-10-28 |
| SII Chile, formato DTE 2.5: `https://www.sii.cl/factura_electronica/factura_mercado/formato_dte_202602.pdf` | Febrero de 2026 |
| DIAN, Resolución 165 de 2023: `https://normograma.dian.gov.co/dian/compilacion/docs/resolucion_dian_0165_2023.htm` | 2023 |
| SAT, preguntas sobre el Anexo 20 versión 4.0, SUNAT (OSE), DGI Uruguay (CFE) y portal NFS-e de Brasil | Vistos por búsqueda el 2026-10-28 |

**Pasarelas y otros sistemas**

| Fuente | Fecha |
|---|---|
| Stripe, Global availability: `https://stripe.com/global` | Consultada el 2026-10-28 |
| Stripe Docs: Checkout Sessions, PaymentIntents, Refunds, Disputes, Balance Transactions, Webhooks, Event types, Idempotent requests, Versioning, Errors, Declines, Currencies, API keys y Search, en `https://docs.stripe.com/api` y `https://docs.stripe.com` | Consultadas el 2026-10-28 |
| Stripe Docs: Save payment details during payment, Save and reuse, Specify product tax codes and tax behavior, Calculate tax y Zero tax amounts and reverse charges: `https://docs.stripe.com/payments/save-during-payment`, `https://docs.stripe.com/payments/save-and-reuse`, `https://docs.stripe.com/tax/products-prices-tax-codes-tax-behavior`, `https://docs.stripe.com/tax/calculating` y `https://docs.stripe.com/tax/zero-tax` | Consultadas el 2026-10-28 |
| Mercado Pago Developers, Suscripciones: `https://www.mercadopago.com.ar/developers/es/docs/subscriptions/landing` | Consultada el 2026-10-28, sin fecha visible |
| Mercado Pago Developers, Pagos Automáticos: `https://www.mercadopago.com.ar/developers/es/docs/automatic-payments/landing` | Consultada el 2026-10-28, sin fecha visible |
| PayPal Developer, Save payment methods: `https://developer.paypal.com/docs/checkout/save-payment-methods/` | Consultada el 2026-10-28 |
| dLocal Go, registro de empresas: `https://helpcenter.dlocalgo.com/en/articles/7240022-where-does-my-company-have-to-be-registered-if-i-want-to-open-a-dlocal-go-account` | 29/07/2026 |
| dLocal Go API, notificaciones y pagos recurrentes: `https://docs.dlocalgo.com/integration-api/welcome-to-dlocal-go-api/payments/recurring-payments.md` | Consultada el 2026-10-28 |
| Mollie, Recurring payments: `https://docs.mollie.com/docs/recurring-payments` | Consultada el 2026-10-28 |
| BTCPay Server, Greenfield API v1: `https://docs.btcpayserver.org/API/Greenfield/v1/swagger.json` | Consultada el 2026-10-28 |
| Payway, documentación de Ventas Online: `https://documentacion-ventasonline.payway.com.ar/` | Actualizada el 16/10/2026 |
| Odoo 18.0, Taxes y Fiscal positions: `https://www.odoo.com/documentation/18.0/applications/finance/accounting/taxes.html` | Consultada el 2026-10-28 |
| Odoo 19, localización Argentina: `https://www.odoo.com/documentation/19.0/applications/finance/fiscal_localizations/argentina.html` | Consultada el 2026-10-28 |
| Shopify Help Center, Dynamic tax-inclusive pricing: `https://help.shopify.com/en/manual/international/pricing/dynamic-tax-inclusive-pricing` | Vista por búsqueda |
| Blesta, Company Settings para desarrolladores: `https://docs.blesta.com/developers/settings/company-settings/` | Consultada el 2026-10-28 |

**Secundarias, no oficiales:** iProfesional, nota del 09/09/2026 sobre el rechazo desde el 01/12/2026:
`https://www.iprofesional.com/impuestos/464104-nuevo-requisito-arca-puede-trabar-emision-facturas`. Consejo
Profesional de Ciencias Económicas de Salta, consulta sobre facturación 15, que cita la pregunta frecuente de ARCA
sobre el Régimen de Transparencia Fiscal y los monotributistas:
`https://www.consejosalta.org.ar/wp-content/uploads/CONSULTA-Facturacion-15.pdf`.

**Revisión del 2026-10-28.** Se cotejaron contra la fuente oficial las clases de comprobante (RG 1415, arts. 15, 16 y
17), el art. 47 y el umbral del consumidor final (RG 5866/2026), la base y las alícuotas de la RG 2126, los arts. 98 y
99 de la Ley 27.743 y las fechas de la RG 5614, la Res. 4/2025, la leyenda de la RG 5003, los arts. 1, 3, 10, 28 y 39 de
la Ley de IVA, los requisitos de WSASS, la RN 7/2026 de ARBA, la Res. 169/AGIP/2026 y, en el manual de WSFEv1 4.7, los
tipos 51 a 54, el tributo 13 y la validación 10067, las validaciones de la clase C, la tolerancia, el redondeo y la
observación 10217.

