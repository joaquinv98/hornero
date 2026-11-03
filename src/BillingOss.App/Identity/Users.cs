namespace BillingOss.App.Identity;

/// <summary>
/// Lo común a los dos tipos de usuario con contraseña (D-10). Clientes y staff van en tablas separadas y con cookies
/// separadas: un ID de una tabla nunca sirve en la otra. La semilla de TOTP y los códigos de recuperación se guardan
/// cifrados por campo (D-09).
/// </summary>
public abstract class LoginUser
{
    public long Id { get; private set; }

    public string Email { get; set; } = string.Empty;

    public string NormalizedEmail { get; set; } = string.Empty;

    public bool EmailConfirmed { get; set; }

    /// <summary>Hash de Identity o, si vino de una importación, el hash de origen tal cual hasta el primer ingreso.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Cambia con la contraseña o el segundo factor y corta las otras sesiones.</summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public DateTimeOffset? LockoutEnd { get; set; }

    public int AccessFailedCount { get; set; }

    public bool TwoFactorEnabled { get; set; }

    /// <summary>Semilla de TOTP cifrada con <see cref="Platform.SecretBox"/>.</summary>
    public byte[]? AuthenticatorKey { get; set; }

    /// <summary>Códigos de recuperación cifrados, separados por punto y coma.</summary>
    public byte[]? RecoveryCodes { get; set; }

    /// <summary>Idioma de la interfaz de esta persona: "es" o "en".</summary>
    public string Language { get; set; } = "es";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Un usuario deshabilitado no entra y pierde sus sesiones.</summary>
    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>Para conservar el ID de origen en una importación (D-17).</summary>
    public void SetImportedId(long id) => Id = id;
}

/// <summary>Quien entra al portal (sección 2 del modelo de dominio). Opera una o varias cuentas.</summary>
public sealed class PortalUser : LoginUser
{
    /// <summary>
    /// Tiene el segundo factor obligatorio aunque su rol no lo pida: lo tenía en el sistema anterior (las semillas no se
    /// migran, así que enrola uno nuevo apenas entra) o se lo exigió el staff.
    /// </summary>
    public bool TwoFactorRequired { get; set; }
}

/// <summary>Personal del proveedor. TOTP obligatorio y permisos finos (D-10).</summary>
public sealed class StaffUser : LoginUser
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Permisos de <see cref="StaffPermissions"/>.</summary>
    public string[] Permissions { get; set; } = [];

    /// <summary>"sub" del proveedor OIDC (por ejemplo Authentik). Lo vincula un administrador, nunca el login.</summary>
    public string? OidcSubject { get; set; }
}

public static class StaffPermissions
{
    public const string AccountsRead = "accounts.read";

    public const string Impersonate = "accounts.impersonate";

    /// <summary>Ver, responder y gestionar tickets.</summary>
    public const string SupportTickets = "support.tickets";

    /// <summary>Departamentos, casillas de correo, filtros y respuestas predefinidas.</summary>
    public const string SupportSettings = "support.settings";

    /// <summary>Ver facturas, presupuestos, ítems facturables, saldos a favor y cotizaciones.</summary>
    public const string BillingRead = "billing.read";

    /// <summary>Armar y emitir facturas manuales, presupuestos e ítems, aplicar saldo y recargos, eximir cuentas y cargar cotizaciones.</summary>
    public const string BillingWrite = "billing.write";

    /// <summary>Sumar o restar saldo a favor a mano. Cada ajuste queda en el libro y en la auditoría.</summary>
    public const string CreditAdjust = "credit.adjust";

    /// <summary>Pasarelas, cuentas para transferir, extractos y la bandeja de pagos sin identificar.</summary>
    public const string PaymentsManage = "payments.manage";

    /// <summary>Ver los dominios, sus operaciones y los precios por TLD.</summary>
    public const string DomainsRead = "domains.read";

    /// <summary>Operar dominios (nameservers, bloqueo, código EPP, reintentos y cancelaciones) y cambiar los precios.</summary>
    public const string DomainsManage = "domains.manage";

    /// <summary>Ver el catálogo en el panel: productos, opciones y cupones.</summary>
    public const string CatalogRead = "catalog.read";

    /// <summary>Cambiar el plan, el ciclo o las opciones de un servicio, con su prorrateo.</summary>
    public const string ServicesChange = "services.change";

    /// <summary>Hacer un pedido por una cuenta desde el panel, también de lo que la tienda no muestra, y aceptarlo sin el pago.</summary>
    public const string OrdersWrite = "orders.write";

    /// <summary>
    /// Webhooks salientes, extensiones y llaves de la API. Deja emitir una llave con cualquier permiso: equivale a
    /// administrar todo.
    /// </summary>
    public const string Integrations = "integrations.manage";

    /// <summary>Ver la administración de contenidos, con borradores y archivados (diseno/contenidos.md, sección 7).</summary>
    public const string ContentRead = "content.read";

    /// <summary>Crear, editar, publicar y borrar artículos, categorías, anuncios y descargas.</summary>
    public const string ContentWrite = "content.write";

    /// <summary>Abrir y actualizar incidentes y mantenimientos, editar componentes y ver y borrar suscriptores.</summary>
    public const string StatusWrite = "status.write";

    /// <summary>La sección Impuestos del admin y los datos fiscales de las cuentas (sección 3.11 del diseño fiscal).</summary>
    public const string TaxesEdit = "taxes.edit";

    /// <summary>El asistente de ARCA: credenciales, certificado, prueba sin emitir y activación.</summary>
    public const string FiscalEdit = "fiscal.edit";

    /// <summary>Ver las versiones y pedir una actualización (D-20): solo administradores, con reautenticación.</summary>
    public const string SystemUpdates = "system.updates";

    /// <summary>Ver el historial de ingresos de todos (diseno/fraude-y-acceso.md, sección 9).</summary>
    public const string SecurityRead = "security.read";

    /// <summary>Editar los ajustes de seguridad y fraude, las listas de bloqueo y quitarle el segundo factor a un cliente.</summary>
    public const string SecurityManage = "security.manage";

    /// <summary>Ver la cola de pedidos retenidos y aprobarlos o rechazarlos.</summary>
    public const string OrdersReview = "orders.review";

    /// <summary>Cambiar el catálogo, con cada cambio en la auditoría. En el uso medido, las métricas de un producto y sus precios.</summary>
    public const string CatalogWrite = "catalog.write";

    /// <summary>Ver afiliados, referidos, comisiones, retiros y el programa.</summary>
    public const string AffiliatesRead = "affiliates.read";

    /// <summary>Aprobar y suspender afiliados, reglas, programa, referidos a mano y retiros.</summary>
    public const string AffiliatesManage = "affiliates.manage";

    /// <summary>
    /// Todos los reportes y salidas contables, con sus exportaciones (docs/reportes): alcanza para cada uno de
    /// <see cref="ReportPermissions"/>.
    /// </summary>
    public const string ReportsRead = "reports.read";

    /// <summary>Nombre de la política que pide algún reporte, para el índice y el menú. No es un permiso que se da.</summary>
    public const string AnyReport = "reports.any";

    public static IReadOnlyList<string> All { get; } = [AccountsRead, Impersonate, DomainsRead, DomainsManage, BillingRead, BillingWrite, CreditAdjust, PaymentsManage, ContentRead, ContentWrite, StatusWrite, SupportTickets, SupportSettings, Integrations, TaxesEdit, FiscalEdit, SystemUpdates, CatalogRead, CatalogWrite, ServicesChange, OrdersWrite, AffiliatesRead, AffiliatesManage, SecurityRead, SecurityManage, OrdersReview, ReportsRead, .. ReportPermissions.All];

    /// <summary>Los permisos que alcanzan para <paramref name="permission"/>: el mismo y, si es de un reporte, reports.read.</summary>
    public static string[] Granting(string permission) =>
        ReportPermissions.All.Contains(permission) ? [permission, ReportsRead] : [permission];
}

/// <summary>
/// Un permiso por reporte (docs/reportes), para dar solo algunos: deja ver el reporte y bajar sus archivos. Con
/// reports.read se ven todos.
/// </summary>
public static class ReportPermissions
{
    public const string Revenue = "reports.revenue";

    public const string RevenueByProduct = "reports.revenue-by-product";

    public const string RevenueByYear = "reports.revenue-by-year";

    public const string Forecast = "reports.forecast";

    public const string DailyActivity = "reports.daily-activity";

    public const string TopAccounts = "reports.top-accounts";

    public const string VatBook = "reports.vat-book";

    public const string Sales = "reports.sales";

    public const string Taxes = "reports.taxes";

    public const string Aging = "reports.aging";

    public const string Suspended = "reports.suspended";

    public const string Statement = "reports.statement";

    public const string Recurring = "reports.recurring";

    public const string Retention = "reports.retention";

    public const string Collections = "reports.collections";

    public const string Credit = "reports.credit";

    public const string NewAccounts = "reports.new-accounts";

    public const string Services = "reports.services";

    public const string Countries = "reports.countries";

    public const string Orders = "reports.orders";

    public const string Coupons = "reports.coupons";

    public const string Affiliates = "reports.affiliates";

    public const string Domains = "reports.domains";

    public const string DomainNotices = "reports.domain-notices";

    public const string Support = "reports.support";

    public const string StaffReplies = "reports.staff-replies";

    public const string Satisfaction = "reports.satisfaction";

    public const string AccountList = "reports.account-list";

    public const string ServiceList = "reports.service-list";

    public const string InvoiceList = "reports.invoice-list";

    public const string PaymentList = "reports.payment-list";

    public const string InvoicePdfs = "reports.invoice-pdfs";

    public static IReadOnlyList<string> All { get; } =
        [
            Revenue, RevenueByProduct, RevenueByYear, TopAccounts, Forecast, DailyActivity, VatBook, Sales, Taxes, Aging, Suspended, Statement, Recurring, Retention,
            Collections, Credit, NewAccounts, Services, Countries, Orders, Coupons, Affiliates, Domains, DomainNotices, Support,
            StaffReplies, Satisfaction, AccountList, ServiceList, InvoiceList, PaymentList, InvoicePdfs,
        ];
}

/// <summary>
/// Acceso de un usuario a una cuenta. El titular puede todo. Los demás tienen un conjunto de capacidades con nombre
/// propio, y los roles (facturación, técnico) son atajos que llenan ese conjunto.
/// </summary>
public sealed class AccountUser
{
    public long Id { get; private set; }

    public long AccountId { get; set; }

    public long UserId { get; set; }

    public bool IsOwner { get; set; }

    /// <summary>Capacidades de <see cref="AccountCapabilities"/>. Al titular no le hace falta ninguna.</summary>
    public string[] Capabilities { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

public static class AccountCapabilities
{
    public const string Services = "services";

    public const string Invoices = "invoices";

    /// <summary>Ver y abrir los tickets de la cuenta.</summary>
    public const string Support = "support";

    /// <summary>Programa de afiliados de la cuenta: sumarse, ver el panel y cobrar el saldo.</summary>
    public const string Affiliate = "affiliate";

    /// <summary>Ver y operar los dominios de la cuenta, y pedir dominios nuevos.</summary>
    public const string Domains = "domains";

    public static IReadOnlyList<string> Billing { get; } = [Invoices, Support];

    public static IReadOnlyList<string> Technical { get; } = [Services, Support, Domains];
}
