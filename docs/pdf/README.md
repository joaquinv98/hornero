# PDF de facturas y notas

**Decisiones:** D-05 y D-13 de [decisiones.md](../../diseno/decisiones.md)

El sistema arma el PDF de cada factura, nota de crédito y nota de débito emitida. El HTML sale de una plantilla
propia en Liquid, con el mismo motor y los mismos topes que los avisos por correo (`Liquid` en
`src/BillingOss.App/Platform`), y lo convierte a PDF Gotenberg, que corre Chromium sin interfaz en un contenedor
aparte y sin salida a la red. Todo lo que muestra el documento, incluido el código QR, va embebido en el HTML.

## Qué muestra

| Bloque | Cuándo | Fuente |
|---|---|---|
| Emisor: razón social, domicilio comercial, CUIT, ingresos brutos, condición frente al IVA e inicio de actividades | Siempre | RG 1415, anexo II, apartado A, punto I |
| Letra, código del comprobante, punto de venta, número y "ORIGINAL" | Con comprobante fiscal | RG 1415, anexo II, I.b y I.c |
| Receptor con su leyenda ("A CONSUMIDOR FINAL", "RESPONSABLE MONOTRIBUTO", "IVA RESPONSABLE INSCRIPTO" y las demás) | Siempre | RG 1415, anexo II, punto II |
| Receptor del exterior con su identificación fiscal, la CUIT del país y la leyenda "IVA EXENTO OPERACIÓN DE EXPORTACIÓN" | Factura E y sus notas | RG 1415, anexo II, II.g |
| Moneda y tipo de cambio, y si se cancela en la misma moneda | Moneda distinta del peso | RG 1415, anexo II, III.d (texto de la RG 5616) |
| Neto gravado, IVA por alícuota, exento y no gravado, con la alícuota de cada renglón | Clase A | RG 1415, anexo II, IV.a.1 |
| Subtotal sin discriminar, en lugar de exento y no gravado | Clase C | Validaciones 10043 a 10048 del manual de WSFEv1 |
| Cada tributo del comprobante que suma al total, con su alícuota (la percepción de IVA de la RG 2126, la de ingresos brutos) | Cuando el documento los lleva | RG 1415, anexo II, y RG 2126 |
| "Régimen de Transparencia Fiscal al Consumidor (Ley 27.743)" con "IVA Contenido" y "Otros Impuestos Nacionales Indirectos", abajo a la izquierda | Clase B | RG 5614/2024, art. 2, puntos 4 a 7 |
| CAE, su vencimiento y el código QR | Con comprobante fiscal | RG 1415, anexo II, I.a.10 y I.a.11, y RG 4892/2020 |
| Leyenda del crédito fiscal para monotributistas | Clase A a un monotributista | RG 1415, art. 15, inc. a |
| Las leyendas de las reglas de impuesto, como la inversión del sujeto pasivo, y la de un tributo que solo se informa con su alícuota (ALÍCUOTA ISIB CABA) | Cuando el documento las lleva | [docs/fiscal/impuestos.md](../fiscal/impuestos.md) y Res. 169/AGIP/2026 |
| Texto del art. 10 ter de la Ley 24.240 (rescisión por el mismo medio) | Siempre | Ley 24.240, art. 10 ter |
| "DOCUMENTO NO VÁLIDO COMO FACTURA" | Cuando se espera un comprobante fiscal que todavía no se autorizó | Diseño propio |

Los textos legales salen de las normas publicadas en `argentina.gob.ar/normativa`. El QR sigue la especificación de
ARCA (versión 1): `https://www.arca.gob.ar/fe/qr/?p=` más el JSON del comprobante en Base64, con `ver`, `fecha`,
`cuit`, `ptoVta`, `tipoCmp`, `nroCmp`, `importe`, `moneda`, `ctz`, `tipoDocRec` y `nroDocRec` (si el receptor está
identificado), `tipoCodAut` y `codAut`. La prueba `Qr_ArmaElEjemploDeLaEspecificacion` reproduce el ejemplo de la
especificación carácter por carácter.

## Descarga

| Ruta | Quién | Permiso |
|---|---|---|
| `GET /api/v1/documents/{id}/pdf` | Integraciones con API key | `documents:read` |
| `GET /facturas/{id}/pdf` | Cliente con sesión en el portal, o el staff que mira como cliente | El documento es de una cuenta que la sesión puede ver con la capacidad de facturas |

El portal usa su sesión (D-10, [docs/interfaz](../interfaz/README.md)) y las cuentas de `PortalAccess`, como el resto
de sus páginas: sin sesión lleva al ingreso, y un documento de otra cuenta da 404, igual que uno que no existe. El
detalle de la factura en el portal tiene el enlace. Las dos rutas responden con `Cache-Control: private, no-store`
porque el PDF lleva datos personales.

Errores: 404 si el documento no existe o es un borrador, 409 si falta un dato que el QR exige (por ejemplo la
cotización que registró el fisco en moneda extranjera) o un dato del emisor (abajo), y 503 si Gotenberg no está
configurado o no responde. La API devuelve el motivo. El portal muestra su página de error y deja el motivo en el log.

## Cuándo se genera y dónde se guarda

- **Con comprobante fiscal** (CAE): se genera la primera vez que alguien lo pide y se guarda en
  `billing.document_pdfs`. Desde ahí sale siempre el mismo archivo, aunque cambie la plantilla (D-05).
- **Sin comprobante fiscal:** se genera en cada pedido y no se guarda, porque el CAE puede llegar después. Si el
  emisor tiene módulo fiscal y la cuenta no está marcada sin comprobante, sale con la marca de no válido como factura.

Con varios nodos web, dos pedidos a la vez del mismo documento pueden generarlo los dos. El primero que guarda
gana (`INSERT ... ON CONFLICT DO NOTHING`) y los dos devuelven el guardado. Nada del PDF vive en el disco de un nodo.

## Datos del emisor

El PDF lee el perfil del emisor (`fiscal.issuers`). Además de CUIT y razón social hay que cargar:

| Columna | Qué va |
|---|---|
| `vat_condition` | Condición frente al IVA con el código de ARCA. Por defecto 1, responsable inscripto |
| `address` | Domicilio comercial |
| `gross_income_number` | Número de ingresos brutos, o el texto que corresponda (por ejemplo convenio multilateral) |
| `activity_start` | Fecha de inicio de actividades |

El PDF con CAE se guarda para siempre, así que no se genera si al emisor del comprobante le falta un dato que pide
la RG 1415: razón social real, CUIT, domicilio comercial, ingresos brutos o inicio de actividades. La descarga
responde 409 con lo que falta y no guarda nada.

El importador crea el emisor con razón social "Emisor importado" y sin estos datos, porque el sistema anterior no
los guardaba. Por eso el primer paso, antes de configurar `Pdf__GotenbergUrl`, es cargarlos. Hasta que exista la
pantalla del staff se cargan con SQL, sobre el emisor de los comprobantes importados y sobre el activo. Los emisores
se ven con su ID, CUIT y si están activos:

```sql
SELECT id, legal_name, tax_id, active, module_instance_id FROM fiscal.issuers ORDER BY id;

UPDATE fiscal.issuers
SET legal_name = '<razón social>', vat_condition = 1, address = '<domicilio comercial>',
    gross_income_number = '<ingresos brutos>', activity_start = DATE '<AAAA-MM-DD>'
WHERE id = <id del emisor>;
```

Si un dato se cargó mal y ya hay PDF guardados con él, se borran los de ese emisor para que se vuelvan a generar
con el dato corregido en el próximo pedido:

```sql
DELETE FROM billing.document_pdfs
WHERE fiscal_document_id IN (SELECT id FROM fiscal.documents WHERE issuer_id = <id del emisor>);
```

## Gotenberg

Configuración del núcleo: `Pdf:GotenbergUrl` (variable `Pdf__GotenbergUrl`), por ejemplo `http://gotenberg:3000`.
Sin ella el resto del sistema anda igual y la descarga del PDF responde 503.

El núcleo llama a `POST /forms/chromium/convert/html` con el tamaño de página de la plantilla (A4) y pide que la
conversión falle si el documento intenta cargar un recurso. Así, si alguien agrega a la plantilla una imagen o una
fuente de afuera, el PDF no sale en vez de salir incompleto.

Receta con Docker Compose. La red `pdf` es interna: Gotenberg no tiene salida a internet ni a la red del proveedor,
y solo lo alcanzan los nodos del núcleo.

```yaml
services:
  gotenberg:
    image: gotenberg/gotenberg:8.37.0
    command:
      - gotenberg
      - --chromium-disable-javascript=true
      - --chromium-deny-public-ips=true
      - --chromium-deny-private-ips=true
      - --api-disable-download-from=true
      - --webhook-disable=true
      - --libreoffice-disable-routes=true
      - --api-timeout=30s
    tmpfs:
      - /tmp
    networks: [pdf]
    restart: unless-stopped

  billing-web:
    environment:
      Pdf__GotenbergUrl: http://gotenberg:3000
    networks: [default, pdf]

networks:
  pdf:
    internal: true
```

Gotenberg no guarda estado: cada conversión es independiente. Para alta disponibilidad alcanza con dos o más
réplicas detrás del nombre del servicio (o un Gotenberg al lado de cada nodo web). Si una réplica se cae a mitad de
una conversión, el pedido responde 503 y el siguiente intento va a otra. La salud se mira con `GET /health`.

Versión: se fija una etiqueta exacta. Al subirla se corre la prueba real (abajo) contra la nueva imagen antes de
desplegarla.

## Pruebas

- `DocumentPdfTests` arma el modelo y el HTML de una factura B a consumidor final, una A a un monotributista, una B
  con percepción y alícuota que solo se informa, una C, una E en dólares cancelada en la misma moneda, una nota de
  crédito, un comprobante importado y un documento sin CAE, y
  controla el JSON del QR contra la especificación. Las de punta a punta usan la API con un Gotenberg de mentira,
  sobre PostgreSQL (`BILLINGOSS_TEST_PG`), y prueban que con el emisor incompleto no se guarda nada.
- `UiTests.Portal_BajaElPdfDeLoSuyo_LoAjenoDa404` baja el PDF desde el portal con una sesión de verdad.
- `GotenbergReal_ConvierteElComprobante` convierte una factura con un Gotenberg de verdad. Se saltea si no está
  `BILLINGOSS_TEST_GOTENBERG`, por ejemplo `http://localhost:3000`. Conviene correrla contra un contenedor con los
  mismos parámetros de la receta.

## Pendientes

- Los comprobantes E importados en moneda extranjera sin cotización (el sistema anterior guardaba 1) no se pueden
  imprimir con un QR válido: la descarga responde 409. Hay que consultar en ARCA la cotización registrada o
  conservar el PDF original. Queda por decidir.
- Si una factura importada tiene colgados varios comprobantes (por ejemplo su nota de crédito), el PDF muestra el
  que corresponde al tipo del documento. Los demás todavía no tienen PDF propio.
- Los importados no traen la copia del receptor: se imprime la identidad fiscal vigente de la cuenta.
- Abiertos con el contador: si el bloque de transparencia va también en las notas B (hoy va en toda la clase B) y
  si el "IVA Contenido" de una B en dólares se muestra en dólares (hoy va en la moneda del comprobante).
- El PDF guardado vive en la base (unos 100 KB por comprobante), así lo replica el clúster sin otra pieza. Pasa al
  almacenamiento de adjuntos de D-14 cuando exista o si el volumen lo pide.
- La plantilla es fija y va dentro del binario. La edición por el proveedor puede usar la de los correos, que ya
  comparte el motor.
