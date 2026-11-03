# Alta guiada de ARCA

**Estado:** v0, 2026-10-29 · **Diseño:** sección 2 de [fiscal-impuestos-y-cobros.md](../../diseno/fiscal-impuestos-y-cobros.md) (D-21) · **Código:**
`src/BillingOss.App/UseCases/FiscalSetup.cs`, `src/BillingOss.Web/Pages/Admin/Fiscal/` y el
[módulo de ARCA](../../src/modules/BillingOss.Module.Arca/README.md)

Conectar con ARCA es un asistente de siete pasos en el admin (`/admin/fiscal/arca`, permiso `fiscal.edit`). Guarda
el avance en el emisor, así se puede cortar en cualquier paso, por ejemplo para esperar los días hábiles del punto de
venta, y retomar. Nada de lo que hace el asistente emite un comprobante. Esta guía repite lo que dice cada pantalla,
para quien la siga por consola o antes de tener el panel.

## Qué hace cada paso

| Paso | La persona | El sistema | Verificación |
|---|---|---|---|
| 1. Qué vas a hacer | Elige el ambiente (homologación o producción), si vende al exterior (factura E) y si quiere completar fichas con el padrón. Elige o crea la instancia del módulo de ARCA | Arma la lista de servicios a autorizar: `wsfe`, `wsfex` si exporta y `ws_sr_constancia_inscripcion` si usa el padrón. Crea la instancia con la CUIT del emisor y el ambiente, que no cambia más | Avisa el nivel de clave fiscal que pide el ambiente. En la instalación que factura de verdad se elige producción. Quien probó en homologación pasa a producción desde la ficha del emisor (abajo) |
| 2. Datos del emisor | CUIT, razón social, condición frente al IVA, si ARCA lo habilitó solo para la A con leyenda, domicilio, ingresos brutos e inicio de actividades | Valida la CUIT con su dígito verificador y arma el mapa de tipos de comprobante según la condición. "Completar con ARCA" precarga razón social y domicilio desde la constancia | CUIT válida y condición de emisor (responsable inscripto, monotributista o exento) |
| 3. Clave y pedido | Alias (por defecto `billingoss`), CUIT con la que va a entrar a ARCA y razón social en ASCII | Genera la clave RSA de 2048 bits y el pedido con el sujeto que pide ARCA (`C=AR, O=razón social, CN=alias, serialNumber=CUIT 11 dígitos`). La clave se cifra en el acto, atada a su fila, y nunca vuelve al navegador. El pedido se copia o se baja como `.csr` | El pedido se lee y tiene el sujeto esperado (`FiscalSetupTests`) |
| 4. Trámites en ARCA | Sigue la guía de abajo, con "Ya lo hice" al final | Muestra los nombres de pantalla, el alias, el pedido y la lista de servicios | La hace el paso 6 |
| 5. Cargar el certificado | Pega o sube el certificado que dio ARCA | Lo valida sin llamar a ARCA (tabla de abajo). Si pasa, "Activar" pide el segundo factor y copia certificado y clave a los secretos de la instancia (`ReplaceSecrets`). Si la instancia ya tiene secretos (un certificado cargado a mano o una credencial anterior), antes prueba la candidata con un login nuevo y, si no pasa, no toca nada. La clave deja la credencial | Cada control con su mensaje |
| 6. Probar sin emitir | Corre la prueba y, con la lista que devolvió ARCA, elige los puntos de venta | Compone consultas que no autorizan nada: `instance/test` con login nuevo, `parameters`, `last_number` de cada serie y el padrón del propio emisor | Cada punto con su remedio |
| 7. Activar | Mira el resumen y el estado de las trabas | Crea las series que falten con el último número que informa ARCA y deja el emisor activo. Si un punto de venta ya tiene comprobantes, avisa que otro sistema lo usa | Las trabas no se tocan |

## Controles del certificado (paso 5)

| Control | Resultado | Mensaje |
|---|---|---|
| Se lee como X.509 (PEM, DER o PKCS#12) | Error | "Esto no es un certificado. Bajalo de nuevo desde ARCA con el botón Ver del alias." |
| Su clave pública es la de la clave guardada | Error | "Este certificado es de otro pedido. Usá el que te dio ARCA para el alias {alias} con el pedido de este asistente." |
| `serialNumber` con la forma `CUIT` y 11 dígitos válidos | Error | "El certificado no trae una CUIT válida." |
| La CUIT del certificado es la del emisor | Aviso | "El certificado es de la CUIT {x} y vas a facturar con la {y}. Funciona solo si {y} autorizó a {x} en ARCA. En homologación es lo normal." |
| El nombre común es el alias esperado | Aviso | "El alias del certificado es {a} y el del pedido es {b}." |
| Vigencia | Error | "El certificado venció el {fecha}. Generá un pedido nuevo con Renovar." |
| Menos de 30 días | Aviso | "Vence en {n} días." |
| Emisor del certificado | Se muestra | El ambiente no se deduce con seguridad del certificado: si es del otro, la prueba del paso 6 lo detecta |

Quien migra con un certificado propio lo sube con su clave (PEM con o sin contraseña, DER o PKCS#12). El núcleo pasa
la clave a PKCS#8 sin contraseña antes de cifrarla y no guarda la contraseña. Igual conviene un certificado nuevo con
alias propio: WSAA habla del computador, que es el alias, y así los tickets de dos sistemas no se pisan.

## Guía en ARCA (paso 4)

### Homologación, en WSASS

| # | Dónde | Qué hacer | Qué se pega |
|---|---|---|---|
| 1 | Requisito | Clave fiscal de persona física con nivel 2 o más. Si en las pruebas se factura por una empresa, la persona la representa en la autorización del punto 4 | Nada |
| 2 | Administrador de Relaciones de Clave Fiscal | "Adherir servicio", grupo "Servicios interactivos", WSASS y "Continuar". Salir de ARCA y volver a entrar para que aparezca | Nada |
| 3 | WSASS, "Nuevo Certificado" | "Nombre simbólico del DN": el alias, que no puede existir. "CUIT del contribuyente": se completa sola con la de quien entró y tiene que ser la del pedido. Pegar el pedido y "Crear DN y Obtener Certificado". ARCA muestra el certificado en PEM para copiar | Alias y pedido |
| 4 | WSASS, "Crear Autorización a Servicio" | Alias, CUIT del DN, "CUIT representado" (la que factura en las pruebas) y el servicio. Una vez por servicio | Lista de servicios |
| 5 | Punto de venta | Los documentos leídos no dicen si en homologación hace falta darlo de alta. El paso 6 lo comprueba: si ARCA no devuelve ninguno, el asistente acepta un número a mano | Nada |

En WSASS los certificados no se pueden borrar, ni vencidos, y se pueden crear todos los que hagan falta. En
homologación la CUIT del certificado suele ser la de quien programa y no la que factura, y es válido.

### Producción

| # | Dónde | Qué hacer | Qué se pega |
|---|---|---|---|
| 1 | Requisito | Clave fiscal nivel 3 | Nada |
| 2 | "Administración de Certificados Digitales" | Si no aparece entre los servicios: Administrador de Relaciones, "Nueva Relación", elegir el servicio y el representante, confirmar, salir y volver a entrar. Si sigue sin verse, aceptarlo en "Aceptación de Designación" | Nada |
| 3 | "Administración de Certificados Digitales" | Elegir el contribuyente, "Agregar alias", escribir el alias, subir el pedido (PKCS#10) y confirmar. Después, "Ver" en el alias y bajar el certificado | Alias y archivo `.csr` |
| 4 | Administrador de Relaciones, "Nueva Relación" | Representado: la empresa. Si se elige a la persona, el servicio queda autorizado para la persona y no para la empresa. "Buscar" el servicio en el grupo de web services de ARCA ("Facturación Electrónica" es `wsfe`). Representante: el computador fiscal del alias. "Confirmar" dos veces, y queda la constancia F. 3283/E. Se repite por cada servicio. Los nombres en pantalla de exportación y padrón se confirman en pantalla | Alias y lista de servicios |
| 5 | "Administración de Puntos de Venta y Domicilios", "A/B/M de Puntos de Venta", "Agregar" | Número, nombre de fantasía opcional, sistema de facturación y domicilio, dado de alta antes en "Sistema Registral". Para `wsfe`, un punto de web service ("RECE para aplicativo y web services" para responsables inscriptos, "Factura Electrónica - Monotributo - Web Services" para monotributistas, a confirmar en pantalla). Para `wsfex`, otro punto de "Comprobantes de Exportación - Web Services" | El sistema que corresponde a la condición del paso 2 |

El alta del punto de venta se informa con al menos 3 días hábiles de anticipación al inicio de las operaciones (art.
47 de la RG 1415, con el texto de la RG 5866/2026). Es el único paso que no se resuelve en el momento.

Si el certificado es de otra CUIT (un estudio contable que factura por la empresa), la empresa tiene que aceptar la
designación y asignar su propio computador fiscal, y la cadena no puede seguir hacia otra empresa.

## Prueba sin emitir (paso 6)

| # | Qué prueba | Cómo | Qué detecta |
|---|---|---|---|
| 1 | Conexión, certificado y ambiente | `instance/test` con `fresh_login`: `FEDummy` y login de WSAA | Certificado del otro ambiente, vencido o sin autorizar, y reloj corrido |
| 2 | Servicios y delegación de la CUIT | `parameters`, con WSFEX si el emisor exporta | Servicio sin asociar y CUIT que el certificado no puede representar |
| 3 | Puntos de venta | `points_of_sale` y, con exportación, `export_points_of_sale` | Punto inexistente, bloqueado o dado de baja, y falta del punto de exportación |
| 4 | Series al día | `last_number` de cada serie | Que el último número de ARCA coincida con el registrado o importado |
| 5 | Condiciones del receptor | `receiver_vat_conditions` | Una condición que el sistema no conoce |
| 6 | Datos del emisor según ARCA | `taxpayer` con la CUIT del emisor, si usa el padrón | Condición de otra clase de comprobante que la cargada (el padrón sugiere 1, 6 o 4, y un monotributista social o promovido es de la misma clase C que el 6) y constancia bloqueada |

WSAA rechaza por unos minutos otro ticket del mismo computador y servicio: el módulo lo devuelve como reintentable
con la espera, y la pantalla la muestra en vez de un error. La prueba queda guardada con fecha y resultado, y se
repite desde la ficha del emisor (`/admin/fiscal`).

## Vencimiento y renovación

- Un trabajo diario del planificador (`fiscal.certificates`, con clave por día) avisa a los 30, 15, 7 y 1 día del
  vencimiento de cada credencial activa o lista, una sola vez cada umbral, como evento de auditoría
  (`fiscal.certificate.expiring`). La sección Impuestos lo muestra entre los avisos y la métrica
  `billing.fiscal.certificate_days` publica los días que le quedan a cada certificado, negativos si venció.
- "Renovar" en la ficha del emisor crea una credencial nueva con el mismo sujeto y otra clave, y lleva al paso 3 con
  el pedido nuevo. En homologación se usa "Agregar Certificado a Alias" de WSASS. En producción, si "Administración
  de Certificados Digitales" deja sumar un certificado a un alias existente, las relaciones siguen. Si no, alias
  nuevo y se repite la asociación.
- Activar la renovación prueba la credencial candidata con un login nuevo, armando el sobre con la configuración
  actual y el certificado y la clave nuevos sin guardarlos. Si pasa, reemplaza los secretos de la instancia y guarda
  el estado que devolvió la prueba, todo junto. La vieja pasa a retirada. Hasta ese momento sigue emitiendo.

## Custodia de la clave y trabas de producción

- La clave se genera en el núcleo, se cifra en el acto con `SecretBox` atada a su tabla, columna y fila, no se
  descarga, no vuelve al navegador y no pasa por los logs. Se borra de la credencial al activarla, porque queda en
  los secretos de la instancia. Sin exportación: ante una pérdida se restaura con el backup y el archivo de llaves,
  o se pide otro certificado.
- Homologación y producción no comparten emisor: la serie arrastraría el último número y un documento con CAE de
  homologación saldría como si valiera. El ambiente de una instancia fiscal no cambia (`ModuleRegistry.ChangeConfig`
  lo rechaza) y un emisor con series o comprobantes no pasa a una instancia de otro ambiente. "Pasar a producción"
  en la ficha del emisor (`StartNewIssuer`) crea otro emisor con los mismos datos del impreso y la misma configuración,
  sin instancia, series ni comprobantes, inactivo, y abre el asistente en producción con él. Sale solo del emisor
  activo de homologación: uno de producción no pasa a otro, porque lo que todavía espera comprobante es real y sale
  con él. Mientras tanto el de homologación sigue activo. Al terminar el paso 7 el nuevo queda activo y el anterior
  inactivo, con sus series, y el nuevo guarda desde cuándo emite (`ActiveSince`). Lo emitido antes es de homologación
  y no sale nunca con el de producción: ni la emisión ni el barrido diario lo toman, aunque siga sin comprobante (una
  factura que esperaba el cobro, por ejemplo). Una nota asocia solo comprobantes del emisor activo o importados del
  sistema anterior, así que la nota de una factura de prueba se rechaza con el motivo en vez de asociar en producción
  un comprobante de homologación.
- El emisor que deja el importador queda inactivo, con la numeración, los comprobantes, el momento de emisión y los
  ajustes de exportación del sistema anterior. No cuenta como en preparación ni frena el pase a producción, y mientras
  haya un emisor activo el asistente no lo toma. Sin ninguno activo, que es el corte de una migración en una base nueva
  (D-17), el asistente sigue con él: la prueba del paso 6 compara sus series con el último número de ARCA y el paso 7
  lo deja activo con lo que trajo, en vez de empezar con un emisor vacío. Su numeración es la real del sistema
  anterior, así que cuenta como de producción y no pasa a una instancia de homologación. Si el asistente arranca en
  homologación con él, por ejemplo en un staging con la copia importada, sigue con otro emisor con sus datos y
  su configuración, sin series, y el del importador queda inactivo y sin instancia para el corte.
- Dos trabas, apagadas por defecto, que el asistente muestra y nunca prende:

| Traba | Dónde | Qué hace |
|---|---|---|
| `Fiscal:ProductionEnabled` | Núcleo (`Fiscal__ProductionEnabled` en el `.env`) | Apagada, el núcleo no llama a ninguna operación que pueda autorizar un comprobante en una instancia de producción. La emisión queda pendiente con el motivo |
| `Arca:Production` | Módulo (`Arca__Production`) | `off` rechaza toda llamada de producción antes de leer el certificado. `read` deja solo la lista cerrada de consultas (login de WSAA, `FEDummy`, `FEParamGet*`, `FECompUltimoAutorizado`, `FECompConsultar`, `FEXGetPARAM*`, `FEXGetLast_CMP`, `FEXGetLast_ID`, `FEXGetCMP` y el padrón) y rechaza `FECAESolicitar`, `FEXAuthorize` y cualquier operación nueva. `on` emite. `Arca__AllowProduction=true` vale como `on` para las recetas anteriores |

Con `read`, la prueba sin emitir corre contra producción antes del corte. Las dos se prenden en el servidor, con el
OK de quien factura, y nunca desde el panel. Las pruebas que lo demuestran: `FiscalFlowTests` y `OperationTests` para
las trabas, `SetupTests` del módulo para las tres posiciones y la lista cerrada, `FiscalSetupTests` para el asistente
y `TaxUiTests` para las pantallas.
