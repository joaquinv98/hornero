# Módulo fiscal de ARCA

Módulo de tipo `fiscal` de Hornero. Es un servicio aparte que habla el [protocolo de módulos v0](../../../diseno/protocolo-modulos-v0.md) (sección 9.4) y conecta el núcleo con los web services de ARCA (ex AFIP):

- **WSAA** para obtener el ticket de acceso.
- **WSFEv1** (manual 4.7) para facturas, notas de débito y de crédito A, B y C.
- **WSFEX** (manual 3.1.1) para la factura de exportación de servicios (tipo 19) y sus notas de débito (20) y de crédito (21).

Declara las capacidades `fiscal.core`, `fiscal.exchange_rate`, `fiscal.taxpayer` (el padrón, `ws_sr_constancia_inscripcion`) e `instance_state`. No toca la base del núcleo, no guarda configuración propia y recibe el certificado y la clave privada en cada llamada, así que tiene que correr en su propia instalación, con una sola instancia por emisor y salida solo hacia ARCA.

## Configuración de la instancia

El núcleo manda esta configuración en cada llamada. Los dos campos con `x-secret` se guardan cifrados en el núcleo y el módulo nunca los devuelve ni los escribe en los logs.

| Campo | Qué es |
|---|---|
| `cuit` | CUIT del emisor, 11 dígitos sin guiones |
| `environment` | `homologation` o `production` |
| `certificate_pem` | Certificado X.509 en PEM (secreto) |
| `private_key_pem` | Clave privada del certificado en PEM, sin contraseña (secreto) |

El ambiente `production` viene trabado con `Arca__Production`, que tiene tres posiciones:

| Valor | Qué pasa con una instancia de producción |
|---|---|
| `off` (por defecto) | Toda llamada se rechaza con `invalid_config` (campo `environment`) antes de leer el certificado o de pedir un ticket al WSAA real |
| `read` | Pasan solo las operaciones de la lista cerrada de consultas (`ReadOnlyOperations`): el login de WSAA, `FEDummy`, `FEParamGet*`, `FECompUltimoAutorizado`, `FECompConsultar`, `FEXGetPARAM*`, `FEXGetLast_CMP`, `FEXGetLast_ID`, `FEXGetCMP` y el padrón. `FECAESolicitar`, `FEXAuthorize` y cualquier operación que se sume después se rechazan con `invalid_config` antes del login. Es una lista de permitidos, no de prohibidos: así la prueba sin emitir del núcleo corre contra producción antes del corte y ninguna operación nueva pasa por omisión |
| `on` | Se emite |

`Arca__AllowProduction` en `true`, la clave de las recetas anteriores, vale como `on` si `Arca__Production` no está. Es la segunda traba, aparte de `Fiscal:ProductionEnabled` del núcleo, y las dos se prenden recién al pasar a producción, con una decisión explícita del operador. `instance/test` devuelve `authorize_enabled` y `production_mode`, para que el panel muestre el estado de la traba sin adivinarla.

Los certificados de homologación y de producción no se pueden mezclar: ARCA responde `cms.cert.untrusted`. El certificado de homologación se pide con WSASS y el de producción en "Administración de Certificados Digitales" (el Administrador de Relaciones de Clave Fiscal solo lo asocia a los servicios y lo delega), y en los dos casos hay que asociarlo a los servicios `wsfe` (Facturación Electrónica), `wsfex` (Factura Electrónica de exportación) y, si se usa el padrón, `ws_sr_constancia_inscripcion`. La guía paso a paso está en [docs/fiscal/alta-arca.md](../../../docs/fiscal/alta-arca.md). Las URLs de ARCA van fijas en el código (`ArcaEndpoints`) y no se configuran, así la clave privada nunca viaja a un destino que alguien elija.

Conviene usar un certificado propio de este módulo. WSAA no entrega un ticket nuevo mientras haya otro vigente para el mismo certificado y servicio, así que si otro sistema usa el mismo certificado, las llamadas fallan con un error reintentable hasta que WSAA lo permita.

## Cómo correrlo

Se configura con las variables de entorno de ASP.NET Core, o con un archivo por clave en `/run/secrets` (por ejemplo `/run/secrets/Module__SigningSecret`), que es como llegan los Docker secrets. La imagen y la receta están en [docs/despliegue](../../../docs/despliegue/README.md).

| Variable | Para qué |
|---|---|
| `Module__SigningSecret` | Secreto de firma de la instalación (obligatorio). Es el mismo que tiene el núcleo para este módulo |
| `Module__PreviousSigningSecret` | El secreto anterior, solo durante una rotación (la SDK acepta los dos a la vez) |
| `Module__IdempotencyStorePath` | Archivo SQLite del registro de idempotencia, por defecto `data/idempotency.db` dentro del directorio del módulo. Tiene que estar en un disco que sobreviva a los reinicios y con permisos solo para el usuario del servicio, porque guarda las respuestas de las operaciones. No guarda el `instance_state`, así que los tickets de WSAA no quedan en el registro |
| `Arca__Production` | Traba de producción: `off` por defecto, `read` deja solo las consultas y `on` deja emitir. `Arca__AllowProduction` en `true` vale como `on` |
| `Module__IdempotencyConnectionString` | Base PostgreSQL propia del módulo para el registro de idempotencia. Si está, se usa en lugar del archivo. Hace falta para correr más de una réplica: ver [docs/alta-disponibilidad/modulos.md](../../../docs/alta-disponibilidad/modulos.md). Lleva la contraseña, así que sale de la bóveda |
| `ASPNETCORE_URLS` | Dónde escucha |

El protocolo exige HTTPS con el certificado validado. Sin TLS solo vale por loopback o por un socket del mismo host, así que en producción hay que poner un proxy con TLS delante o configurar el certificado de Kestrel (`Kestrel__Certificates__Default__Path` y `Kestrel__Certificates__Default__KeyPath`).

```powershell
$env:Module__SigningSecret = "<el secreto de la instalación>"
$env:Module__IdempotencyStorePath = ".\datos\arca\idempotency.db"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5190"
dotnet run --project src/modules/BillingOss.Module.Arca -c Release
```

La salida a internet solo necesita el puerto 443 hacia estos hosts:

| Ambiente | Hosts |
|---|---|
| Homologación | `wsaahomo.afip.gov.ar` (WSAA), `wswhomo.afip.gob.ar` (WSFEv1 y WSFEX) y `awshomo.afip.gov.ar` (padrón) |
| Producción | `wsaa.afip.gov.ar`, `servicios1.afip.gov.ar` y `aws.afip.gov.ar` (padrón) |

Ojo con los dominios: los manuales dicen `afip.gov.ar` para todo, pero los WSDL de homologación de WSFEv1 y WSFEX, leídos el 2026-10-26, declaran `afip.gob.ar` como dirección del servicio, y la página "Servicios migrados" de ARCA lista `wswhomo.afip.gob.ar`. En producción `servicios1.afip.gob.ar` presenta un certificado solo para `afip.gov.ar`, así que ahí sigue el dominio viejo. ARCA está migrando, y si cambia algo se corrige en `ArcaEndpoints`.

El reloj tiene que estar sincronizado por NTP (por ejemplo `time.afip.gov.ar`): WSAA rechaza pedidos con la hora corrida.

## Operaciones

Todas van por `POST /v0/fiscal/{operación}` con el sobre del protocolo. `authorize` es la única con efecto y exige `Idempotency-Key`. Los importes viajan como texto decimal y las fechas como `yyyy-MM-dd`. Los códigos (tipos de comprobante, de documento, alícuotas, condiciones de IVA, monedas) salen de `parameters` y no se fijan en el código.

| Operación | Método de ARCA | Entrada | Resultado |
|---|---|---|---|
| `parameters` | `FEParamGetTiposCbte`, `TiposDoc`, `TiposIva`, `TiposMonedas`, `CondicionIvaReceptor`, `TiposTributos` y `PtosVenta`. Con `export` en `true`, también `FEXGetPARAM_PtoVenta` con su propio login | `{}` o `{ "export": true }` | `voucher_types`, `document_types`, `vat_rates`, `currencies`, `receiver_vat_conditions`, `tax_types` y `points_of_sale` (`id`, `emission_type`, `blocked` y `retired_on`). Con `export`, además `export_points_of_sale`. Sin puntos de venta (602) las listas vienen vacías |
| `last_number` | `FECompUltimoAutorizado` y, si el número es mayor que 0, `FECompConsultar` para la fecha. Para los tipos 19 a 21, `FEXGetLast_CMP` y `FEXGetLast_ID` | `point_of_sale`, `type_code` | `number` y `date` (nula si todavía no hay comprobantes). Para los tipos 19 a 21, además `last_request_id` |
| `authorize` | `FECAESolicitar` o, para los tipos 19 a 21, `FEXAuthorize` | `document` (ver abajo) | `authorization_code`, `authorization_due`, `point_of_sale`, `type_code`, `number` y `observations` |
| `find` | `FECompConsultar` o, para los tipos 19 a 21, `FEXGetCMP` | `point_of_sale`, `type_code`, `number` | `document` con los datos, `authorization_code`, `authorization_due` y `observations`. Si no existe, error `not_found` |
| `exchange_rate` | `FEParamGetCotizacion` o, con `type_code` 19, 20 o 21, `FEXGetPARAM_Ctz`. Con `date` retrocede hasta 10 días si ARCA no tiene cotización | `currency`, y `date` y `type_code` opcionales | `currency`, `rate` y `date` (la de la cotización que encontró ARCA) |
| `taxpayer` | `getPersona_v2` de `ws_sr_constancia_inscripcion`, con su propio login | `tax_id` (11 dígitos) | `name`, `person_type`, `status`, `address` (`street`, `city`, `postal_code`, `province`), `taxes` (`id` y `description`), `activities`, `monotax_category`, `suggested_vat_condition` (1 con el impuesto 30, 6 con el 20, 4 con el 32), `blocked` y `notes`. Una CUIT inexistente es `not_found` |
| `instance/test` | `FEDummy` y un login de WSAA. Con `fresh_login` en `true` pide un ticket nuevo sin usar el guardado ni el de memoria, para probar un certificado renovado | `{}` o `{ "fresh_login": true }` | `servers`, `ticket_expires_at`, `certificate_expires_on`, `authorize_enabled` y `production_mode` |

Los tipos 19, 20 y 21 van por WSFEX en `last_number`, `find` y `authorize`. En `exchange_rate` van por WSFEX los que pasen por `type_code`.

`last_request_id` es el último ID de pedido que recibió ARCA para el CUIT (`FEXGetLast_ID`), y el núcleo propone el `wsfex_id` siguiente sumándole uno. Es un entero único por CUIT, no por punto de venta ni por tipo, y vale 0 si todavía no hubo pedidos. Se pide después del número y con el mismo ticket, y un error de ARCA en cualquiera de las dos consultas se informa igual que los demás errores de WSFEX. Con los tipos de WSFEv1 la respuesta no cambia y no trae este campo.

Con `type_code` 19, 20 o 21, `exchange_rate` consulta la base aduanera de ARCA en WSFEX (`FEXGetPARAM_Ctz`). Con otro `type_code`, o sin él, usa WSFEv1 (`FEParamGetCotizacion`). Las dos devuelven `currency`, `rate` y `date`. La fecha opcional viaja como `yyyyMMdd` en WSFEv1 y como `yyyy-MM-dd` en WSFEX (error 1003 del manual). Sin fecha hay una sola consulta y ARCA devuelve la última cotización.

**Retroceso de fechas.** ARCA no tiene cotización los fines de semana ni los feriados, y el núcleo emite todos los días. Con `date`, si ARCA no tiene cotización para ese día, el módulo prueba el día anterior, y así hasta 10 días hacia atrás (11 consultas como máximo), todas con el mismo ticket. Funciona igual en los dos servicios.

- `date` en la respuesta es la fecha que informa ARCA para la cotización que encontró, no la pedida.
- Retrocede cuando ARCA responde sin cotización (`not_found`: el 602 de WSFEv1 o una cotización vacía o en cero) y cuando responde con un rechazo de negocio, porque el manual no dice qué código devuelve cuando falta la cotización.
- Corta enseguida y devuelve el error como siempre cuando ARCA responde un error de configuración (`invalid_config`), no está disponible (`upstream_unavailable`) o falla de cualquier otra forma, y cuando rechaza la moneda (12000 y 12001 de WSFEv1), porque con otro día seguiría igual.
- Si ninguna de las 11 fechas tiene cotización, el error es `not_found`, con la respuesta del último intento de ARCA en el mensaje y su código en `details`.
- Son consultas en serie, así que el manifiesto declara 120 s para `exchange_rate`, que es lo máximo que respeta el núcleo.

Queda abierto qué fecha conviene pedir: la del comprobante o la del día hábil anterior. ARCA exige en el comprobante la cotización del día hábil anterior a la emisión (error 2053 de WSFEX y 10038 de WSFEv1 con `CanMisMonExt` en `S`), y el manual llama orientativa a la que devuelven estos métodos sin decir si la de una fecha es la registrada ese día o la vigente para ese día. Se resuelve con la prueba en homologación. Mientras tanto el módulo devuelve la primera cotización que encuentra desde la fecha que le pasan hacia atrás.

### `authorize` en WSFEv1

`document` tiene la forma del ejemplo de la sección 9.4 del protocolo. `service_period` es inclusivo, tal como lo pide ARCA. `concept` vale `products`, `services` o `products_and_services`, y para los dos últimos hacen falta `service_period` y `payment_due`. `CbteDesde` y `CbteHasta` son `number`.

```json
{
  "document": {
    "point_of_sale": 3, "type_code": 6, "number": 1488, "date": "2026-10-25",
    "concept": "services",
    "service_period": { "from": "2026-10-01", "to": "2026-10-31" },
    "payment_due": "2026-11-05",
    "receiver": { "doc_type": 96, "doc_number": "30111222", "vat_condition": 5 },
    "currency": "PES", "exchange_rate": "1",
    "amounts": {
      "net_taxed": "10000.00", "net_untaxed": "0.00", "exempt": "0.00",
      "vat": [{ "rate_id": 5, "base": "10000.00", "amount": "2100.00" }],
      "other_taxes": "0.00", "total": "12100.00"
    },
    "associated": [], "optional": []
  }
}
```

- `receiver.vat_condition` es obligatorio. ARCA lo vuelve obligatorio el 2026-12-01 y desde ahora lo observa.
- `associated` lleva `type_code`, `point_of_sale`, `number` y, opcionales, `tax_id` (el CUIT del emisor del comprobante asociado) y `date`. Se usa en notas de débito y de crédito.
- `optional` lleva pares `id` y `value` de texto, los datos opcionales del manual.
- En moneda extranjera, `same_currency_payment` (`true` o `false`) informa `CanMisMonExt` (RG 5616). Con pesos no se envía.
- `exchange_rate` es obligatorio, salvo en moneda extranjera con `same_currency_payment` en `true`: ahí se omite `MonCotiz` y ARCA registra la cotización del día hábil anterior (validación 10038 del manual 4.7). El núcleo la consulta después con `find`, para el QR y para las notas.
- `other_taxes` es `ImpTrib` y tiene que ser la suma de `amounts.taxes`, la lista de tributos con `id` (código de `tax_types`), `description`, `base`, `rate` (porcentaje, hasta dos decimales) y `amount`. Sin tributos van cero y una lista vacía.
- `period_assoc` (`from` y `to`) es el `PeriodoAsoc` que ARCA pide en una nota que revierte percepciones, del mismo mes. Es opcional.

### `authorize` en WSFEX

Es la factura E de servicios (tipo 19) y sus notas de débito (20) y de crédito (21), siempre con `Tipo_expo` 2. Suma `items`, los datos del cliente del exterior y, según el tipo, la fecha de pago o el comprobante asociado. Hay un campo más, `wsfex_id`, fuera de `document`.

```json
{
  "wsfex_id": 4501,
  "document": {
    "point_of_sale": 4, "type_code": 19, "number": 12, "date": "2026-10-25",
    "payment_due": "2026-11-05",
    "receiver": { "name": "Joao Da Silva", "address": "Rua 76 km 34.5 Alagoas", "country_code": 203, "country_tax_id": 50000000016, "tax_id": "PJ54482221-l" },
    "currency": "DOL", "exchange_rate": "1450.25", "same_currency_payment": false,
    "payment_terms": "Transferencia", "language": "en",
    "amounts": { "total": "120.00" },
    "items": [
      { "code": "HOST1", "description": "Hosting mensual", "quantity": "1", "unit_code": 7, "unit_price": "100.00", "total": "100.00" },
      { "description": "Soporte", "quantity": "1", "unit_code": 7, "unit_price": "20.00", "total": "20.00" }
    ]
  }
}
```

La nota de crédito sobre esa factura cambia el tipo, el número y los renglones, y suma `associated`. `payment_due` y `same_currency_payment` pueden venir igual: en las notas el módulo no los envía.

```json
{
  "wsfex_id": 4502,
  "document": {
    "point_of_sale": 4, "type_code": 21, "number": 3, "date": "2026-10-26",
    "payment_due": "2026-11-05",
    "receiver": { "name": "Joao Da Silva", "address": "Rua 76 km 34.5 Alagoas", "country_code": 203, "country_tax_id": 50000000016, "tax_id": "PJ54482221-l" },
    "currency": "DOL", "exchange_rate": "1450.25", "same_currency_payment": false,
    "payment_terms": "Transferencia", "language": "en",
    "amounts": { "total": "20.00" },
    "associated": [{ "type_code": 19, "point_of_sale": 4, "number": 12 }],
    "items": [{ "description": "Soporte", "quantity": "1", "unit_code": 7, "unit_price": "20.00", "total": "20.00" }]
  }
}
```

Qué lleva cada tipo en WSFEX:

| Campo | Factura (19) | Notas de débito y de crédito (20 y 21) |
|---|---|---|
| `payment_due` | Obligatorio y no anterior a `date`. Va como `Fecha_pago` | No se valida ni se envía |
| `same_currency_payment` | Opcional en moneda extranjera. Va como `CanMisMonExt` y con pesos no se envía | No se envía |
| `exchange_rate` | Obligatorio, salvo con `same_currency_payment` en `true` y moneda extranjera: ahí se omite `Moneda_ctz` y ARCA usa la suya | Siempre obligatorio, y ARCA exige que sea el del comprobante asociado |
| `associated` | Tiene que venir vacío | Uno y solo uno. Va como `Cmps_asoc` |
| `payment_terms` | Obligatorio. Va como `Forma_pago` | Opcional |
| `wsfex_id`, `receiver`, `language`, `items` y `amounts.total` | Obligatorios | Obligatorios |

- `wsfex_id` es el ID de pedido de WSFEX (`Cmp.Id`, numérico de hasta 15 dígitos). **Lo genera el núcleo, lo guarda antes de llamar y manda el mismo valor en cada reintento de la misma emisión.** Un ID repetido en otro comprobante devuelve el comprobante viejo, y el módulo lo detecta y lo informa como `rejected`. El ID es único por CUIT y no por serie, así que el núcleo parte de `last_request_id`.
- `payment_due` en la factura va como `Fecha_pago`, obligatoria para servicios y no anterior a la fecha del comprobante. ARCA rechaza una fecha de pago en las notas (error 1673).
- `receiver` pide `name`, `address`, `country_code` (el código de país de ARCA) y al menos uno entre `country_tax_id` (el CUIT del país) y `tax_id`.
- `payment_terms` es `Forma_pago` (hasta 50 caracteres) y `language` es `es`, `en` o `pt`. ARCA exige `language` siempre y `payment_terms` solo en la factura.
- `unit_code` es el código de unidad de medida de ARCA y vale 7 (unidades) si el renglón no lo trae. Un renglón de descuento usa el código 99 y total negativo.
- Si ARCA ya autorizó el pedido, repetirlo con el mismo `wsfex_id` devuelve el mismo CAE y `reprocessed` en `true`.

`associated` de una nota lleva `type_code`, `point_of_sale`, `number` y, opcional, `tax_id`. Va en `Cmps_asoc/Cmp_asoc` como `Cbte_tipo`, `Cbte_punto_vta`, `Cbte_nro` y `Cbte_cuit`, entre `Imp_total` y `Forma_pago`. `date` no se usa en WSFEX.

- La nota de débito asocia una factura (19). La de crédito, una factura (19) o una nota de débito (20), nunca una de crédito (errores 2040 a 2046). Se informa uno solo y es obligatorio (1754 y 2047).
- `tax_id` es el CUIT del emisor del asociado, de 11 dígitos. Si falta se envía el CUIT de la instancia. ARCA exige que coincida con el de quien emite (errores 2031 y 2032), así que lo normal es omitirlo.
- El resto de las reglas del asociado las valida ARCA y vuelven como `rejected`: que exista y sea electrónico (2048), de servicios (2049), con el mismo país (2050) y la misma moneda o pesos (2051), y que su fecha no sea posterior a la de la nota (2052).

## Errores

Según la sección 9.4 del protocolo, ante un resultado incierto el núcleo consulta con `find` antes de repetir. El módulo nunca confunde un resultado incierto con un rechazo: el núcleo toma cualquier error no reintentable de `authorize`, salvo `idempotency_conflict`, como que no se autorizó nada y marca la emisión como rechazada.

| Situación | Error | Reintentable |
|---|---|---|
| WSFEv1 rechaza por reglas (por ejemplo 10048) | `rejected`, con `details.upstream` (código y mensaje de ARCA) | No |
| **WSFEv1 10016** (número de comprobante que no sigue al último) | `outcome_unknown`, con `details.reason` igual a `number_out_of_sequence` | Con cuidado |
| **WSFEX 1535** (el comprobante no sigue en secuencia) | `outcome_unknown` | Con cuidado |
| ARCA con error interno (500, 501, 502) al emitir | `outcome_unknown` | Con cuidado |
| Corte, plazo vencido, HTTP de error o respuesta ilegible **después** de enviar | `outcome_unknown` | Con cuidado |
| Falla **antes** de enviar (sin DNS, sin conexión, TLS, WSAA caído) | `upstream_unavailable` | Sí |
| `coe.alreadyAuthenticated` de WSAA (ya hay un ticket vigente y el módulo no lo tiene) | `upstream_unavailable` con `retry_after` de 150 s en producción y 660 s en homologación | Sí |
| Token o CUIT no aceptados (600, 601 o 1000, 1001), certificado vencido, no autorizado o de otro ambiente | `invalid_config` | No |
| Entrada con campos faltantes o inválidos | `invalid_input`, con `details.fields` | No |
| Comprobante inexistente en `find` (602 de WSFEv1, 1020 de WSFEX) | `not_found` | No |
| Sin cotización en la fecha pedida ni en los 10 días anteriores (`exchange_rate`) | `not_found`, con la respuesta del último intento en el mensaje y su código en `details` | No |
| Reloj del módulo corrido respecto de ARCA | `internal` | Sí |

Las consultas (`parameters`, `last_number`, `find`, `exchange_rate`) nunca devuelven `outcome_unknown`, porque repetirlas no tiene riesgo.

## Estado de la instancia

Con `instance_state` el módulo guarda el ticket de WSAA (token, firma y vencimiento) por servicio, para el CUIT y el ambiente de la instancia. Un ticket dura 12 horas y WSAA no entrega otro mientras el anterior siga vigente, así que el módulo lo reusa hasta 60 segundos antes de que venza.

```json
{ "tickets": { "wsfe": { "token": "...", "sign": "...", "expires_at": "2026-10-27T01:56:14-03:00", "cuit": "20000000001", "environment": "homologation" } } }
```

Tres cosas que tiene que respetar el núcleo:

- **Guardar el `instance_state` de cada respuesta que lo traiga**, incluida la de `instance/test`. Si no lo guarda, el ticket que pidió la prueba se pierde y las operaciones siguientes fallan con el error reintentable de `coe.alreadyAuthenticated` hasta que WSAA acepte otro.
- Una respuesta de error del protocolo no puede llevar `instance_state`. Para que un ticket pedido en una llamada que después falla no se pierda, el módulo guarda en memoria el último ticket de cada CUIT, ambiente y servicio y lo reusa hasta que el núcleo lo reciba en una respuesta exitosa. Si el proceso se reinicia en ese lapso, vale lo anterior.
- Una respuesta repetida por idempotencia (misma clave, misma entrada) sale del registro sin `instance_state`. El registro no guarda tickets, y así una repetición no pisa en el núcleo un estado más nuevo.

## Pruebas

```powershell
dotnet test tests/BillingOss.Module.Arca.Tests
```

Casi todas las pruebas corren sin red. Levantan el módulo en un `TestServer`, firman los pedidos como el núcleo y usan una ARCA falsa con respuestas SOAP sintéticas armadas según los manuales. La firma CMS se prueba con un certificado autofirmado que generan las propias pruebas, y no hay ningún secreto en el código.

La prueba de homologación real (`HomologationTests`) solo corre si están las tres variables de entorno. Si falta alguna, queda inconclusa.

| Variable | Contenido |
|---|---|
| `ARCA_HOMO_CERT` | El certificado de homologación en PEM, o la ruta al archivo |
| `ARCA_HOMO_KEY` | La clave privada en PEM, o la ruta al archivo |
| `ARCA_HOMO_CUIT` | El CUIT del emisor |
| `ARCA_HOMO_PTOVTA` | Opcional: un punto de venta habilitado para web services, para probar también `last_number` |
| `ARCA_HOMO_CBTE_TIPO` | Opcional: el tipo de comprobante de esa prueba, 6 por defecto |

```powershell
$env:ARCA_HOMO_CERT = ".\arca\homologacion.crt"
$env:ARCA_HOMO_KEY = ".\arca\homologacion.key"
$env:ARCA_HOMO_CUIT = "20123456789"
dotnet test tests/BillingOss.Module.Arca.Tests --filter "TestCategory=Homologacion"
```

La prueba hace `instance/test`, pide las cinco tablas con el ticket recién obtenido, consulta la cotización del dólar y, si hay punto de venta, el último número. No emite comprobantes. Si WSAA ya entregó un ticket para ese certificado hace pocos minutos, la prueba queda inconclusa y hay que esperar unos 10 minutos.

## Límites conocidos

- No informa otros tributos (percepciones): `other_taxes` tiene que ser cero.
- `parameters` trae las tablas de WSFEv1. Las de WSFEX (países, unidades de medida, idiomas) no.
- En WSFEX solo emite servicios (`Tipo_expo` 2): no informa permisos de embarque, incoterms, datos opcionales ni actividades, y como asociados no admite remitos de tabaco ni de harina.
- WSFEX se probó solo con respuestas sintéticas armadas según el manual 3.1.1 y el WSDL. Lo que más conviene confirmar contra la homologación real es `FEXAuthorize` de una nota de crédito, `FEXGetLast_ID`, la fecha `yyyy-MM-dd` de `FEXGetPARAM_Ctz` y qué responde ARCA, en los dos servicios, cuando no hay cotización para un día, que es lo que dispara el retroceso de `exchange_rate`.
- No implementa CAEA (contingencia), `PeriodoAsoc` ni el padrón de contribuyentes (`fiscal.taxpayer`).
- Cada pedido a ARCA tiene un plazo de 30 segundos. Un `authorize` con login incluido puede llegar a 60, y el manifiesto declara 90.
