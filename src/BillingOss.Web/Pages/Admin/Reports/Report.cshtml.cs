using System.Globalization;
using System.Security.Claims;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.Reports;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Reports;

/// <summary>
/// Una entrada del índice de reportes: su dirección, las claves de su título y su descripción, su grupo y el permiso que
/// lo deja ver. Con <paramref name="Table"/>, lleva a esa tabla de un reporte que ya está en la lista, como los clientes
/// con deuda, y pide el permiso de ese reporte.
/// </summary>
public sealed record ReportEntry(string Slug, string Title, string Help, string Group, string Permission, string? Table = null)
{
    public string Href => "/admin/reportes/" + Slug + (Table is null ? null : "#t-" + Table);
}

/// <summary>Una tabla con la dirección de su CSV, para el parcial que la dibuja.</summary>
public sealed record ReportTableView(ReportTable Table, string? CsvUrl);

/// <summary>
/// Un reporte con su período, sus tablas, lo que hay que revisar y la descarga de cada tabla en CSV (docs/reportes).
/// Todo se arma en cada pedido desde la base, así da lo mismo en cualquier nodo.
/// </summary>
[Authorize(Policy = StaffPermissions.AnyReport)]
public sealed class ReportModel(ReportService reports, AuditLog audit, IStringLocalizer<Texts> texts) : PageModel
{
    public const string Aging = "deuda";

    public const string Sales = "ventas";

    public const string Services = "clientes-servicios";

    public const string RevenueByYear = "ingresos-anuales";

    public const string Forecast = "proyeccion";

    public const string Daily = "actividad-diaria";

    public const string Retention = "permanencia";

    public const string Suspended = "suspendidos";

    public const string Statement = "estado-cuenta";

    public const string AccountList = "listado-clientes";

    public const string ServiceList = "listado-servicios";

    public const string Domains = "dominios";

    /// <summary>Los que tienen su propia página, con sus archivos: no los arma esta.</summary>
    private static readonly string[] OwnPages = ["libro-iva", "pdf-facturas"];

    /// <summary>
    /// Los reportes en el orden del índice, agrupados y nombrados como en el menú de Reportes del prototipo de la
    /// navegación clásica (sección 10 de la interfaz). El Libro IVA tiene su propia página.
    /// </summary>
    public static IReadOnlyList<ReportEntry> Catalog { get; } =
    [
        new("ingresos", "Reports.Revenue", "Reports.RevenueHelp", "ReportGroup.Revenue", ReportPermissions.Revenue),
        new("ingresos-producto", "Reports.RevenueByProduct", "Reports.RevenueByProductHelp", "ReportGroup.Revenue", ReportPermissions.RevenueByProduct),
        new(RevenueByYear, "Reports.RevenueByYear", "Reports.RevenueByYearHelp", "ReportGroup.Revenue", ReportPermissions.RevenueByYear),
        new("mejores-clientes", "Reports.TopAccounts", "Reports.TopAccountsHelp", "ReportGroup.Revenue", ReportPermissions.TopAccounts),
        new(Forecast, "Reports.Forecast", "Reports.ForecastHelp", "ReportGroup.Revenue", ReportPermissions.Forecast),
        new(Daily, "Reports.DailyActivity", "Reports.DailyActivityHelp", "ReportGroup.Revenue", ReportPermissions.DailyActivity),
        new("libro-iva", "Reports.LibroIva", "Reports.LibroIvaHelp", "ReportGroup.Taxes", ReportPermissions.VatBook),
        new(Sales, "Reports.Sales", "Reports.SalesHelp", "ReportGroup.Taxes", ReportPermissions.Sales),
        new("impuestos", "Reports.Taxes", "Reports.TaxesHelp", "ReportGroup.Taxes", ReportPermissions.Taxes),
        new(Aging, "Reports.Aging", "Reports.AgingHelp", "ReportGroup.Debt", ReportPermissions.Aging),
        new(Aging, "Reports.Debtors", "Reports.DebtorsHelp", "ReportGroup.Debt", ReportPermissions.Aging, "AgingAccounts"),
        new(Suspended, "Reports.Suspended", "Reports.SuspendedHelp", "ReportGroup.Debt", ReportPermissions.Suspended),
        new(Statement, "Reports.Statement", "Reports.StatementHelp", "ReportGroup.Debt", ReportPermissions.Statement),
        new("recurrente", "Reports.Recurring", "Reports.RecurringHelp", "ReportGroup.Recurring", ReportPermissions.Recurring),
        new(Retention, "Reports.Retention", "Reports.RetentionHelp", "ReportGroup.Recurring", ReportPermissions.Retention),
        new("cobros", "Reports.Collections", "Reports.CollectionsHelp", "ReportGroup.Collections", ReportPermissions.Collections),
        new("saldo-favor", "Reports.Credit", "Reports.CreditHelp", "ReportGroup.Collections", ReportPermissions.Credit),
        new("altas-clientes", "Reports.NewAccounts", "Reports.NewAccountsHelp", "ReportGroup.Customers", ReportPermissions.NewAccounts),
        new(Services, "Reports.Services", "Reports.ServicesHelp", "ReportGroup.Customers", ReportPermissions.Services),
        new("clientes-pais", "Reports.Countries", "Reports.CountriesHelp", "ReportGroup.Customers", ReportPermissions.Countries),
        new("pedidos", "Reports.Orders", "Reports.OrdersHelp", "ReportGroup.Customers", ReportPermissions.Orders),
        new("cupones", "Reports.Coupons", "Reports.CouponsHelp", "ReportGroup.Customers", ReportPermissions.Coupons),
        new("afiliados", "Reports.Affiliates", "Reports.AffiliatesHelp", "ReportGroup.Customers", ReportPermissions.Affiliates),
        new(Domains, "Reports.Domains", "Reports.DomainsHelp", "ReportGroup.Domains", ReportPermissions.Domains),
        new("avisos-dominios", "Reports.DomainNotices", "Reports.DomainNoticesHelp", "ReportGroup.Domains", ReportPermissions.DomainNotices),
        new("soporte", "Reports.Support", "Reports.SupportHelp", "ReportGroup.Support", ReportPermissions.Support),
        new("respuestas-staff", "Reports.StaffReplies", "Reports.StaffRepliesHelp", "ReportGroup.Support", ReportPermissions.StaffReplies),
        new("satisfaccion", "Reports.Satisfaction", "Reports.SatisfactionHelp", "ReportGroup.Support", ReportPermissions.Satisfaction),
        new(AccountList, "Reports.AccountList", "Reports.AccountListHelp", "ReportGroup.Exports", ReportPermissions.AccountList),
        new(ServiceList, "Reports.ServiceList", "Reports.ServiceListHelp", "ReportGroup.Exports", ReportPermissions.ServiceList),
        new("listado-facturas", "Reports.InvoiceList", "Reports.InvoiceListHelp", "ReportGroup.Exports", ReportPermissions.InvoiceList),
        new("listado-cobros", "Reports.PaymentList", "Reports.PaymentListHelp", "ReportGroup.Exports", ReportPermissions.PaymentList),
        new("pdf-facturas", "Reports.InvoicePdfs", "Reports.InvoicePdfsHelp", "ReportGroup.Exports", ReportPermissions.InvoicePdfs),
    ];

    [BindProperty(SupportsGet = true)]
    public string? Desde { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Hasta { get; set; }

    /// <summary>La cuenta del estado de cuenta.</summary>
    [BindProperty(SupportsGet = true)]
    public long? Cuenta { get; set; }

    /// <summary>Lo que el reporte pide además del período, para los atajos y el CSV: la cuenta del estado de cuenta.</summary>
    public string Extra => Entry.Slug == Statement && Cuenta is { } account ? string.Create(CultureInfo.InvariantCulture, $"&cuenta={account}") : string.Empty;

    /// <summary>El estado de cuenta sin cuenta todavía: la página pide el número.</summary>
    public bool NeedsAccount => Entry.Slug == Statement && Cuenta is null;

    public ReportEntry Entry { get; private set; } = Catalog[0];

    public MonthRange? Range { get; private set; }

    public Report? Result { get; private set; }

    public bool InvalidPeriod { get; private set; }

    /// <summary>
    /// La deuda, los clientes y servicios, la permanencia, los suspendidos, los dominios y los listados de clientes y de
    /// servicios son al día de hoy, sin período.
    /// </summary>
    public bool HasPeriod => Entry.Slug is not (Aging or Services or Retention or Suspended or AccountList or ServiceList or Domains);

    public DateOnly Today => reports.Today;

    /// <summary>El texto de "al día de hoy" de un reporte sin período.</summary>
    public string AsOfKey => Entry.Slug switch
    {
        Aging => "Reports.AsOf",
        Services => "Reports.ServicesAsOf",
        _ => "Reports.AsOfToday",
    };

    /// <summary>Atajos de período, con el que coincide con el pedido marcado. La proyección mira para adelante.</summary>
    public IReadOnlyList<(string Key, MonthRange Range)> Shortcuts => Entry.Slug switch
    {
        Forecast =>
        [
            ("Reports.Next3Months", new(Today, Today.AddMonths(2))),
            ("Reports.Next12Months", new(Today, Today.AddMonths(11))),
            ("Reports.RestOfYear", new(Today, new DateOnly(Today.Year, 12, 1))),
        ],
        RevenueByYear =>
        [
            ("Reports.ThisYear", new(new DateOnly(Today.Year, 1, 1), Today)),
            ("Reports.LastThreeYears", new(new DateOnly(Today.Year - 2, 1, 1), Today)),
        ],
        _ =>
        [
            ("Reports.ThisMonth", new(Today, Today)),
            ("Reports.LastMonth", new(Today.AddMonths(-1), Today.AddMonths(-1))),
            ("Reports.ThisYear", new(new DateOnly(Today.Year, 1, 1), Today)),
            ("Reports.LastYear", MonthRange.LastYear(Today)),
        ],
    };

    /// <summary>Si el staff puede ver el reporte: con su permiso o con el de todos.</summary>
    public static bool Allowed(ClaimsPrincipal user, ReportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(entry);
        return StaffPermissions.Granting(entry.Permission).Any(p => user.HasClaim(Claims.Permission, p));
    }

    /// <summary>Lo que el menú y el índice le ofrecen a este staff, en el orden del catálogo.</summary>
    public static IEnumerable<ReportEntry> Visible(ClaimsPrincipal user) => Catalog.Where(e => Allowed(user, e));

    public async Task<IActionResult> OnGet(string kind, CancellationToken ct)
    {
        if (Find(kind) is not { } entry)
        {
            return NotFound();
        }

        if (!Allowed(User, entry))
        {
            return Forbid(Schemes.Staff);
        }

        await Load(entry, ct);
        return Page();
    }

    /// <summary>Una tabla del reporte en CSV. Queda en la auditoría: los detalles llevan nombres y CUIT de clientes.</summary>
    public async Task<IActionResult> OnGetCsv(string kind, string? tabla, CancellationToken ct)
    {
        if (Find(kind) is not { } entry)
        {
            return NotFound();
        }

        if (!Allowed(User, entry))
        {
            return Forbid(Schemes.Staff);
        }

        await Load(entry, ct);

        if (InvalidPeriod)
        {
            return BadRequest();
        }

        if (Result?.Tables.FirstOrDefault(t => t.Id == tabla) is not { } table)
        {
            return NotFound();
        }

        var period = Range is { } r ? $"{MonthRange.Format(r.From)}_{MonthRange.Format(r.To)}" : Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await audit.Record("report.export", "report:" + Entry.Slug, new { table = table.Id, period, account = Cuenta }, ct);
        return File(ReportFormat.Csv(table, texts), "text/csv; charset=utf-8", $"{Entry.Slug}-{table.Id}-{period}.csv");
    }

    public string? CsvUrl(ReportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var period = Range is { } r ? $"&desde={MonthRange.Format(r.From)}&hasta={MonthRange.Format(r.To)}" : string.Empty;
        return $"/admin/reportes/{Entry.Slug}?handler=csv&tabla={table.Id}{period}{Extra}";
    }

    /// <summary>El reporte de esa dirección que arma esta página, o nulo.</summary>
    private static ReportEntry? Find(string kind) => Catalog.FirstOrDefault(e => e.Slug == kind && e.Table is null && !OwnPages.Contains(kind));

    private async Task Load(ReportEntry entry, CancellationToken ct)
    {
        Entry = entry;
        var kind = entry.Slug;
        if (!HasPeriod)
        {
            Result = kind switch
            {
                Aging => await reports.Aging(ct),
                Retention => await reports.Retention(ct),
                Suspended => await reports.Suspended(ct),
                AccountList => await reports.AccountList(ct),
                ServiceList => await reports.ServiceList(ct),
                Domains => await reports.Domains(ct),
                _ => await reports.Services(ct),
            };
            return;
        }

        // Ventas trae el detalle por documento y la actividad va día por día: por defecto el mes en curso. La proyección,
        // los doce meses que vienen, los ingresos por año, los últimos tres años, y el resto, los últimos doce meses.
        var fallback = kind switch
        {
            Sales or Daily or "listado-facturas" or "listado-cobros" => new MonthRange(Today, Today),
            Forecast => new MonthRange(Today, Today.AddMonths(11)),
            RevenueByYear => new MonthRange(new DateOnly(Today.Year - 2, 1, 1), Today),
            _ => MonthRange.LastYear(Today),
        };
        var from = Desde is null ? fallback.From : MonthRange.ParseMonth(Desde);
        var to = Hasta is null ? fallback.To : MonthRange.ParseMonth(Hasta);
        try
        {
            Range = from is { } f && to is { } t ? new MonthRange(f, t) : throw new ArgumentException("Mes inválido.");
        }
        catch (ArgumentException)
        {
            InvalidPeriod = true;
            return;
        }

        Result = kind switch
        {
            "ingresos" => await reports.Revenue(Range, ct),
            "ingresos-producto" => await reports.RevenueByProduct(Range, ct),
            RevenueByYear => await reports.RevenueByYear(Range, ct),
            "mejores-clientes" => await reports.TopAccounts(Range, ct),
            "saldo-favor" => await reports.Credit(Range, ct),
            "listado-facturas" => await reports.InvoiceList(Range, ct),
            "listado-cobros" => await reports.PaymentList(Range, ct),
            "avisos-dominios" => await reports.DomainNotices(Range, ct),
            "respuestas-staff" => await reports.StaffReplies(Range, ct),
            "satisfaccion" => await reports.Satisfaction(Range, ct),
            "cupones" => await reports.Coupons(Range, ct),
            "afiliados" => await reports.Affiliates(Range, ct),
            Statement => Cuenta is { } account ? await reports.Statement(account, Range, ct) : null,
            "clientes-pais" => await reports.Countries(Range, ct),
            "pedidos" => await reports.Orders(Range, ct),
            Forecast => await reports.Forecast(Range, ct),
            Daily => await reports.DailyActivity(Range, ct),
            "altas-clientes" => await reports.NewAccounts(Range, ct),
            Sales => await reports.Sales(Range, ct),
            "impuestos" => await reports.Taxes(Range, ct),
            "recurrente" => await reports.Recurring(Range, ct),
            "soporte" => await reports.Support(Range, ct),
            _ => await reports.Collections(Range, ct),
        };
    }
}
