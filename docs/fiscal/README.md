# Fiscal

Guías de lo fiscal: cómo se conecta con ARCA y cómo se configuran los impuestos.

| Documento | Qué tiene |
|---|---|
| [alta-arca.md](alta-arca.md) | El asistente de siete pasos, la guía paso a paso en ARCA para homologación y producción, los controles del certificado, la prueba sin emitir, la renovación, el pase de homologación a producción con otro emisor, el corte y el staging con el emisor del importador y las trabas de producción |
| [impuestos.md](impuestos.md) | Categorías, impuestos y reglas, reglas de cuenta con constancia, percepciones (RG 2126 e ingresos brutos), provincias como ISO 3166-2, clientes del exterior, frenos antes de llamar al fisco, redondeo y muestra, las páginas del panel y los datos fiscales del portal |

El módulo de ARCA tiene su [README](../../src/modules/BillingOss.Module.Arca/README.md) con las operaciones y la
configuración del proceso.

En el menú clásico (secciones 10.2 y 10.3 de [interfaz-y-temas.md](../../diseno/interfaz-y-temas.md)) las pantallas
cuelgan de Configuración, Impuestos (`/admin/impuestos`) y Configuración, ARCA (`/admin/fiscal`) en el admin, y de
Cuenta, Datos fiscales (`/cuenta/datos-fiscales`) en el portal. Hasta que llegue el armazón con desplegables van como
enlaces de la barra, cada uno con su permiso.
