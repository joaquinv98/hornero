# Diseño del núcleo v0

**Estado:** borrador v0

Diseño del núcleo de Hornero: un producto independiente, con las mismas funciones que un sistema de facturación y
aprovisionamiento para proveedores de hosting, fácil de migrar y con una API compatible.

| Documento | Qué tiene |
|---|---|
| [dominio.md](dominio.md) | Módulos del núcleo, conceptos, estados, invariantes, eventos y flujos principales |
| [protocolo-modulos-v0.md](protocolo-modulos-v0.md) | Cómo habla el núcleo con los módulos de aprovisionamiento, registrars, pasarelas y fisco |
| [decisiones.md](decisiones.md) | Las 21 decisiones de arquitectura, con su estado |
| [api-compatible.md](api-compatible.md) | Los comandos de la API compatible y la forma de sus pedidos |
| [importacion.md](importacion.md) | Cómo corre el importador, sus reglas, el mapa de módulos y la conciliación |
| [contenidos.md](contenidos.md) | Base de conocimiento, anuncios, descargas y estado de la red para los clientes |
| [catalogo.md](catalogo.md) | Opciones configurables, complementos, stock, prueba gratis, paquetes, cupones y cambios de plan con prorrateo |
| [interfaz-y-temas.md](interfaz-y-temas.md) | Apariencia y plantillas editables, sistema visual, temas y pantallas (D-19) |
| [fiscal-impuestos-y-cobros.md](fiscal-impuestos-y-cobros.md) | Alta guiada de ARCA, configuración impositiva completa, pasarelas de fábrica y débito automático (D-21) |
| [webhooks-y-extensiones.md](webhooks-y-extensiones.md) | Webhooks salientes firmados, extensiones por módulo y los comandos nuevos de la API compatible (D-11 y D-12) |
| [fraude-y-acceso.md](fraude-y-acceso.md) | Fraude de pedidos con módulos, listas de bloqueo, ingresos, intentos, contraseñas y segundo factor |
| [ingreso-clientes.md](ingreso-clientes.md) | Registro de clientes en el portal e ingreso con Google y otros proveedores OpenID Connect, con vínculos confirmados |
| [uso-medido.md](uso-medido.md) | Facturación por uso: métricas por producto, muestras diarias, excedente en la factura y avisos |
| [afiliados.md](afiliados.md) | Programa de afiliados: enlace, atribución, comisiones con espera y reversa, cobros e importación |

Se lee en ese orden. Todo es v0: puede cambiar.

---

## Reglas de origen

**Fuentes que sí se usan:**
- Las normas argentinas (manuales de ARCA, Ley de IVA, resoluciones generales, Ley 24.240 y Disposición
  954/2025) y la documentación pública de .NET, PostgreSQL, Patroni, PgBouncer y las pasarelas.
- Ideas y patrones de otros proyectos libres, nunca su código sin respetar su licencia.
- La experiencia de operar un sistema de facturación real: doble facturación por crédito, redondeo del IVA,
  emisión sin idempotencia.

**Fuentes que no se usan:** código, documentación, plantillas, textos, esquema de base o paquete de WHMCS. Los
nombres, estados y estructuras del modelo de dominio y del protocolo son propios y no siguen a los de WHMCS.

**API compatible.** Es la única parte que tiene la forma de la API de WHMCS. Su especificación sale de
integraciones reales y de clientes libres con licencia permisiva. La implementación es propia y traduce a los
casos de uso de la API propia (decisión D-12).

---

## Temas abiertos

- Con un contador: el saldo a favor "al cargar", las facturas en dólares cobradas en pesos y si el hosting entra
  en los servicios continuos de la RG 5866.
- En la prueba de ARCA: la tolerancia exacta por alícuota y la fecha de los comprobantes en reintentos.
- Los textos de estado de servicios de la API compatible.
