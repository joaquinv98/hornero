using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Reports;

/// <summary>
/// Reportes y salidas contables (docs/reportes). Leen de la réplica cuando hay una, porque toleran datos apenas viejos
/// y no cambian nada, y agregan en la base: nada queda en memoria entre pedidos, así que dan lo mismo desde cualquier
/// nodo. Los importes van en la moneda de cada documento, salvo el de impuestos, que va en pesos con la cotización de
/// cada comprobante.
/// </summary>
public sealed class ReportService([FromKeyedServices(Failover.Replica)] BillingDbContext db, IOptions<BillingOptions> options, TimeProvider time)
{
    /// <summary>
    /// Documentos emitidos con lo que vale de cada uno. Una factura importada anulada o reembolsada en el sistema
    /// anterior trae su crédito sin nota (D-17): esa parte va como anulada, en el mes de la factura. La fecha fiscal es
    /// la del primer comprobante del documento, o la de emisión si no tiene: un importado puede tener después la nota
    /// que lo anuló colgada de él, y esa nota no lo mueve de mes.
    /// </summary>
    private const string Effective = """
        WITH notes AS (
          SELECT related_document_id AS id, sum(total) AS total
          FROM billing.documents
          WHERE kind = 'CreditNote' AND issue_date IS NOT NULL AND related_document_id IS NOT NULL
          GROUP BY related_document_id
        ), fiscal AS (
          SELECT DISTINCT ON (document_id) document_id, date, type_code, point_of_sale, number, authorization_code
          FROM fiscal.documents
          ORDER BY document_id, date, id
        ), effective AS (
          SELECT d.id, d.kind, d.number, d.account_id, d.currency, d.issue_date, coalesce(f.date, d.issue_date) AS fiscal_date,
                 d.total, d.net_taxed + d.exempt + d.not_taxed AS net, d.exempt, d.not_taxed, d.aliquots, d.receiver, d.imported,
                 CASE WHEN d.kind = 'CreditNote' THEN -1 ELSE 1 END AS sign,
                 CASE WHEN d.kind = 'Invoice' THEN greatest(d.credited - coalesce(n.total, 0), 0) ELSE 0 END AS voided,
                 f.type_code, f.point_of_sale, f.number AS fiscal_number, f.authorization_code
          FROM billing.documents d
          LEFT JOIN notes n ON n.id = d.id
          LEFT JOIN fiscal f ON f.document_id = d.id
          WHERE d.issue_date IS NOT NULL
        )
        """;

    /// <summary>
    /// El precio actual de un servicio pasado a un mes, con descuento y sin separar impuestos: un anual cuenta la doceava
    /// parte. Vale para los ciclos de meses y de años.
    /// </summary>
    private const string Monthly = """
        round(s.unit_price * s.quantity * (1 - coalesce(s.discount_percent, 0) / 100)
              / (s.cycle_count * CASE s.cycle_unit WHEN 'Year' THEN 12 ELSE 1 END), 2)
        """;

    private static readonly IReadOnlyList<ReportColumn> RevenueColumns =
    [
        new("Month", CellKind.Month), new("Currency", CellKind.Text), new("Documents", CellKind.Count), new("Billed", CellKind.Money),
        new("CreditNotes", CellKind.Money), new("Voided", CellKind.Money), new("Net", CellKind.Money), new("Taxes", CellKind.Money),
        new("Total", CellKind.Money),
    ];

    /// <summary>El día de hoy en la zona horaria del proveedor: la deuda se mide a esta fecha.</summary>
    public DateOnly Today => options.Value.Today(time);

    private string TimeZone => options.Value.TimeZone;

    /// <summary>
    /// Ingresos por mes de emisión y moneda: lo facturado (facturas y notas de débito), las notas de crédito, lo anulado
    /// sin nota, y el neto, los impuestos y el total que quedan.
    /// </summary>
    public async Task<Report> Revenue(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rows = await Query(
            $"""
            {Effective}, parts AS (
              SELECT *, CASE WHEN total = 0 THEN 0 ELSE round(net * voided / total, 2) END AS net_voided FROM effective
            )
            SELECT date_trunc('month', issue_date)::date, currency,
                   count(*) FILTER (WHERE kind <> 'CreditNote')::int,
                   coalesce(sum(total) FILTER (WHERE kind <> 'CreditNote'), 0),
                   coalesce(sum(total) FILTER (WHERE kind = 'CreditNote'), 0),
                   sum(voided),
                   sum(sign * net - net_voided),
                   sum(sign * total - voided) - sum(sign * net - net_voided),
                   sum(sign * total - voided)
            FROM parts
            WHERE issue_date >= @from AND issue_date < @end
            GROUP BY 1, 2
            ORDER BY 1, 2
            """,
            r => new object?[] { r.GetFieldValue<DateOnly>(0), r.GetString(1), r.GetInt32(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8) },
            ct,
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        return new([Totals(new ReportTable("Revenue", RevenueColumns, rows))], []) { Charts = [new("Revenue", "Revenue", "Month", ["Total"])] };
    }

    /// <summary>
    /// Ingresos por producto: el neto de las líneas de los documentos emitidos en el período, por producto y moneda, con
    /// las notas de crédito restando y lo anulado sin nota descontado, como en los ingresos. El producto sale del servicio
    /// de cada línea, y las líneas sin servicio (cargos sueltos, ajustes, notas sin servicio) van juntas en "Sin
    /// producto". Una línea importada trae el importe con IVA: su neto se saca con la alícuota de la línea, así que el
    /// total puede diferir en centavos del de los ingresos. Los totales cuentan cada documento una vez.
    /// </summary>
    public async Task<Report> RevenueByProduct(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var table = await Grouped(
            "RevenueByProduct",
            [new("Group", CellKind.Text), new("Product", CellKind.Text, Empty: "ReportCol.NoProduct"), new("Currency", CellKind.Text), new("Documents", CellKind.Count), new("Net", CellKind.Money)],
            $"""
            {Effective}, lines AS (
              SELECT e.id, e.currency, e.sign, s.product_id,
                     e.sign * n.net - CASE WHEN e.total = 0 THEN 0 ELSE round(n.net * e.voided / e.total, 2) END AS net
              FROM effective e
              JOIN billing.document_lines l ON l.document_id = e.id
              CROSS JOIN LATERAL (
                SELECT CASE WHEN NOT e.imported THEN l.net WHEN l.treatment = 'Taxed' THEN round(l.amount / (1 + l.rate), 2) ELSE l.amount END AS net
              ) n
              LEFT JOIN services.services s ON s.id = l.service_id
              WHERE e.issue_date >= @from AND e.issue_date < @end
            )
            SELECT grouping(p.id)::int, pg.name, p.name, x.currency, count(DISTINCT x.id) FILTER (WHERE x.sign = 1)::int, sum(x.net)
            FROM lines x LEFT JOIN catalog.products p ON p.id = x.product_id
            LEFT JOIN catalog.product_groups pg ON pg.id = p.group_id
            GROUP BY GROUPING SETS ((p.id, pg.name, p.name, x.currency), (x.currency))
            ORDER BY 1, x.currency, 6 DESC, p.name, p.id
            """,
            ct,
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        return new([table], []) { Charts = [new("RevenueByProduct", "RevenueByProduct", "Product", ["Net"])] };
    }

    /// <summary>
    /// Ingresos por año: el neto de cada mes, el mismo de la columna Neto de los ingresos por mes, en una fila por moneda y
    /// año con una columna por mes, para comparar los años. Un mes fuera del período queda vacío.
    /// </summary>
    public async Task<Report> RevenueByYear(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var net = (await Query(
            $"""
            {Effective}
            SELECT date_trunc('month', issue_date)::date, currency,
                   sum(sign * net - CASE WHEN total = 0 THEN 0 ELSE round(net * voided / total, 2) END)
            FROM effective
            WHERE issue_date >= @from AND issue_date < @end
            GROUP BY 1, 2
            """,
            r => (Month: r.GetFieldValue<DateOnly>(0), Currency: r.GetString(1), Net: r.GetDecimal(2)),
            ct,
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false)).ToDictionary(n => (n.Month, n.Currency), n => n.Net);
        var rows = new List<object?[]>();
        foreach (var currency in net.Keys.Select(k => k.Currency).Distinct().Order(StringComparer.Ordinal))
        {
            for (var year = range.From.Year; year <= range.To.Year; year++)
            {
                var months = Enumerable.Range(1, 12).Select(m => new DateOnly(year, m, 1))
                    .Select(m => m < range.From || m > range.To ? null : (decimal?)net.GetValueOrDefault((m, currency))).ToList();
                rows.Add([currency, year.ToString(CultureInfo.InvariantCulture), .. months.Cast<object?>(), months.Sum()]);
            }
        }

        ReportColumn[] columns =
        [
            new("Currency", CellKind.Text), new("Year", CellKind.Text),
            new("Jan", CellKind.Money), new("Feb", CellKind.Money), new("Mar", CellKind.Money), new("Apr", CellKind.Money), new("May", CellKind.Money),
            new("Jun", CellKind.Money), new("Jul", CellKind.Money), new("Aug", CellKind.Money), new("Sep", CellKind.Money), new("Oct", CellKind.Money),
            new("Nov", CellKind.Money), new("Dec", CellKind.Money), new("Total", CellKind.Money),
        ];
        return new([Totals(new ReportTable("RevenueByYear", columns, rows))], []) { Charts = [new("RevenueByYear", "RevenueByYear", "Year", ["Total"])] };
    }

    private sealed record ForecastService(
        string Currency, BillingCycle Cycle, int AnchorDay, DateOnly NextDue, DateOnly? CancelAt, decimal UnitPrice, int Quantity, decimal? Discount,
        int? DiscountCycles, string? Server, string? Module);

    /// <summary>
    /// Proyección de ingresos: lo que van a facturar en el período las renovaciones de los servicios activos, cada una en el
    /// mes en que empieza su período, con el precio de hoy y sin separar impuestos, como el ingreso recurrente. El
    /// descuento vale mientras le queden ciclos y una baja programada corta las renovaciones desde esa fecha. Los períodos
    /// salen del día de anclaje de cada servicio, igual que en la facturación. Por mes y moneda y por servidor. Los
    /// suspendidos no cuentan, porque no renuevan hasta que paguen, y salen en una nota.
    /// </summary>
    public async Task<Report> Forecast(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var services = await Query(
            """
            SELECT s.currency, s.cycle_unit, s.cycle_count, s.anchor_day, s.next_due_date, s.cancel_at, s.unit_price, s.quantity, s.discount_percent,
                   s.discount_cycles_left, i.name, m.name
            FROM services.services s
            LEFT JOIN platform.module_instances i ON i.id = s.module_instance_id
            LEFT JOIN platform.module_installations m ON m.id = i.installation_id
            WHERE s.status = 'Active' AND s.next_due_date IS NOT NULL AND s.cycle_unit IN ('Month', 'Year')
            """,
            r => new ForecastService(
                r.GetString(0), new BillingCycle(Enum.Parse<CycleUnit>(r.GetString(1)), r.GetInt32(2)), r.GetInt32(3), r.GetFieldValue<DateOnly>(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5), r.GetDecimal(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetDecimal(8),
                r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11)),
            ct).ConfigureAwait(false);

        var months = new Dictionary<(DateOnly Month, string Currency), (int Renewals, decimal Amount)>();
        var servers = new Dictionary<(string? Server, string? Module, string Currency), (int Services, int Renewals, decimal Amount)>();
        foreach (var s in services)
        {
            var (discount, left, renewals, amount) = (s.Discount, s.DiscountCycles, 0, 0m);
            for (var start = s.NextDue; start < range.End && (s.CancelAt is null || start < s.CancelAt); start = BillingSchedule.PeriodStartingAt(start, s.Cycle, s.AnchorDay).End)
            {
                if (start >= range.From)
                {
                    var price = Rounding.Commercial(s.UnitPrice * s.Quantity * (1m - ((discount ?? 0m) / 100m)));
                    var key = (new DateOnly(start.Year, start.Month, 1), s.Currency);
                    var (n, sum) = months.GetValueOrDefault(key);
                    months[key] = (n + 1, sum + price);
                    renewals++;
                    amount += price;
                }

                // Como al facturar: el descuento vigente es el del período que sigue, y cada período consume un ciclo.
                if (left is > 0 && --left == 0)
                {
                    (discount, left) = (null, null);
                }
            }

            if (renewals > 0)
            {
                var key = (s.Server, s.Module, s.Currency);
                var (count, n, sum) = servers.GetValueOrDefault(key);
                servers[key] = (count + 1, n + renewals, sum + amount);
            }
        }

        var suspended = (await Query(
            "SELECT count(*)::int FROM services.services WHERE status = 'Suspended' AND cycle_unit IN ('Month', 'Year')",
            r => r.GetInt32(0),
            ct).ConfigureAwait(false))[0];
        var byMonth = months.OrderBy(m => m.Key.Month).ThenBy(m => m.Key.Currency, StringComparer.Ordinal)
            .Select(m => new object?[] { m.Key.Month, m.Key.Currency, m.Value.Renewals, m.Value.Amount }).ToList();
        var byServer = servers.OrderBy(s => s.Key.Currency, StringComparer.Ordinal).ThenByDescending(s => s.Value.Amount).ThenBy(s => s.Key.Server, StringComparer.Ordinal)
            .Select(s => new object?[] { s.Key.Server, s.Key.Module, s.Key.Currency, s.Value.Services, s.Value.Renewals, s.Value.Amount }).ToList();
        return new(
            [
                Totals(new ReportTable("ForecastMonths", [new("Month", CellKind.Month), new("Currency", CellKind.Text), new("Renewals", CellKind.Count), new("Amount", CellKind.Money)], byMonth)),
                Totals(new ReportTable(
                    "ForecastServers",
                    [new("Server", CellKind.Text, Empty: "ReportCol.NoServer"), new("Module", CellKind.Text), new("Currency", CellKind.Text), new("Services", CellKind.Count), new("Renewals", CellKind.Count), new("Amount", CellKind.Money)],
                    byServer)),
            ],
            suspended > 0 ? [new("ForecastSuspended", [suspended.ToString(CultureInfo.InvariantCulture)])] : [])
        {
            Charts = [new("Forecast", "ForecastMonths", "Month", ["Amount"]), new("ForecastServers", "ForecastServers", "Server", ["Amount"])],
        };
    }

    /// <summary>
    /// Actividad diaria: por cada día del período, las cuentas nuevas, los pedidos, las facturas y notas emitidas, los
    /// cobros, los servicios activados y dados de baja y los tickets abiertos, más lo facturado y lo cobrado por moneda.
    /// Los días son los del proveedor. Cuenta los cobros como los cobros por medio: todo pago que se cobró alguna vez.
    /// Sin el módulo de soporte la columna de tickets queda vacía.
    /// </summary>
    public async Task<Report> DailyActivity(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var support = await SupportInstalled(ct).ConfigureAwait(false);
        // Cuántos de la tabla por día del proveedor. Tablas y columnas fijas de acá: nada viene del pedido.
        static string Per(string table, string column, string filter = "") =>
            $"SELECT ({column} AT TIME ZONE @tz)::date AS day, count(*) AS n FROM {table} " +
            $"WHERE {filter}{column} >= @from::timestamp AT TIME ZONE @tz AND {column} < @end::timestamp AT TIME ZONE @tz GROUP BY 1";
        var activity = await Query(
            $"""
            WITH days AS (
              SELECT d::date AS day FROM generate_series(@from::timestamp, @end::timestamp - interval '1 day', interval '1 day') d
            ), accounts AS ({Per("accounts.accounts", "created_at")}),
            orders AS ({Per("orders.orders", "placed_at")}),
            documents AS (
              SELECT issue_date AS day, count(*) FILTER (WHERE kind <> 'CreditNote') AS invoices, count(*) FILTER (WHERE kind = 'CreditNote') AS notes
              FROM billing.documents WHERE issue_date >= @from AND issue_date < @end GROUP BY 1
            ), payments AS ({Per("payments.payments", "created_at", "status NOT IN ('Pending', 'Rejected') AND ")}),
            activated AS ({Per("services.services", "activated_at")}),
            terminated AS ({Per("services.services", "terminated_at")}),
            tickets AS ({(support ? Per("support.tickets", "created_at", "merged_into_id IS NULL AND ") : "SELECT NULL::date AS day, NULL::bigint AS n WHERE false")})
            SELECT day, coalesce(a.n, 0)::int, coalesce(o.n, 0)::int, coalesce(x.invoices, 0)::int, coalesce(x.notes, 0)::int, coalesce(p.n, 0)::int,
                   coalesce(v.n, 0)::int, coalesce(t.n, 0)::int, {(support ? "coalesce(k.n, 0)::int" : "NULL::int")}
            FROM days
            LEFT JOIN accounts a USING (day) LEFT JOIN orders o USING (day) LEFT JOIN documents x USING (day) LEFT JOIN payments p USING (day)
            LEFT JOIN activated v USING (day) LEFT JOIN terminated t USING (day) LEFT JOIN tickets k USING (day)
            ORDER BY day
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var amounts = await Query(
            """
            WITH documents AS (
              SELECT issue_date AS day, currency, coalesce(sum(total) FILTER (WHERE kind <> 'CreditNote'), 0) AS billed,
                     coalesce(sum(total) FILTER (WHERE kind = 'CreditNote'), 0) AS credited
              FROM billing.documents WHERE issue_date >= @from AND issue_date < @end GROUP BY 1, 2
            ), payments AS (
              SELECT (created_at AT TIME ZONE @tz)::date AS day, currency, sum(amount) AS collected
              FROM payments.payments
              WHERE status NOT IN ('Pending', 'Rejected') AND created_at >= @from::timestamp AT TIME ZONE @tz AND created_at < @end::timestamp AT TIME ZONE @tz
              GROUP BY 1, 2
            )
            SELECT day, currency, coalesce(d.billed, 0), coalesce(d.credited, 0), coalesce(p.collected, 0)
            FROM documents d FULL JOIN payments p USING (day, currency)
            ORDER BY 1, 2
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] counts =
        [
            new("Date", CellKind.Date), new("AccountsCreated", CellKind.Count), new("Orders", CellKind.Count), new("Invoices", CellKind.Count),
            new("CreditNotes", CellKind.Count), new("Payments", CellKind.Count), new("Activated", CellKind.Count), new("Terminated", CellKind.Count),
            new("Tickets", CellKind.Count),
        ];
        return new(
            [
                Totals(new ReportTable("DailyActivity", counts, activity)),
                Totals(new ReportTable("DailyAmounts", [new("Date", CellKind.Date), new("Currency", CellKind.Text), new("Billed", CellKind.Money), new("CreditNotes", CellKind.Money), new("Collected", CellKind.Money)], amounts)),
            ],
            [])
        {
            Charts = [new("DailyCollected", "DailyAmounts", "Date", ["Collected"]), new("DailyOrders", "DailyActivity", "Date", ["Orders"])],
        };
    }

    /// <summary>Cuántas cuentas lista por moneda el reporte de las que más facturan.</summary>
    public const int TopAccountsCount = 10;

    /// <summary>
    /// Las cuentas que más facturaron en el período, <see cref="TopAccountsCount"/> por moneda: el neto como en los
    /// ingresos, con las notas de crédito restando y lo anulado sin nota descontado, el total con impuestos y la parte de
    /// cada una en el neto de la moneda.
    /// </summary>
    public async Task<Report> TopAccounts(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rows = await Query(
            $"""
            {Effective}, per AS (
              SELECT account_id, currency, count(*) FILTER (WHERE kind <> 'CreditNote') AS documents,
                     sum(sign * net - CASE WHEN total = 0 THEN 0 ELSE round(net * voided / total, 2) END) AS net, sum(sign * total - voided) AS total
              FROM effective
              WHERE issue_date >= @from AND issue_date < @end
              GROUP BY 1, 2
            ), ranked AS (
              SELECT *, row_number() OVER (PARTITION BY currency ORDER BY net DESC, account_id) AS position, sum(net) OVER (PARTITION BY currency) AS all_net
              FROM per
            )
            SELECT r.currency, r.position::int, r.account_id, coalesce(nullif(a.company_name, ''), a.name), r.documents::int, r.net, r.total,
                   CASE WHEN r.all_net = 0 THEN NULL ELSE round(r.net * 100 / r.all_net, 2) END
            FROM ranked r JOIN accounts.accounts a ON a.id = r.account_id
            WHERE r.position <= @top
            ORDER BY 1, 2
            """,
            Row,
            ct,
            ("from", range.From),
            ("end", range.End),
            ("top", TopAccountsCount)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Currency", CellKind.Text), new("Position", CellKind.Count), new("Account", CellKind.Account), new("AccountName", CellKind.Text),
            new("Documents", CellKind.Count), new("Net", CellKind.Money), new("Total", CellKind.Money), new("Share", CellKind.Percent),
        ];
        return new([new ReportTable("TopAccounts", columns, rows)], []) { Charts = [new("TopAccounts", "TopAccounts", "AccountName", ["Net"])] };
    }

    /// <summary>El país de la identidad fiscal vigente de la cuenta de esa columna, o nulo si no tiene.</summary>
    private static string CountryOf(string account) =>
        $"(SELECT t.country FROM accounts.tax_identities t WHERE t.account_id = {account} AND t.valid_to IS NULL ORDER BY t.valid_from DESC, t.id DESC LIMIT 1)";

    /// <summary>
    /// Clientes por país: las cuentas de cada país, las que tienen hoy algún servicio activo o suspendido y las que se
    /// crearon en el período, más lo facturado en el período por país y moneda. El país de una cuenta es el de su
    /// identidad fiscal vigente. El de un documento, el del receptor que se congeló al emitirlo, y en uno importado sin
    /// receptor, el de la cuenta.
    /// </summary>
    public async Task<Report> Countries(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var accounts = await Query(
            $"""
            WITH c AS (
              SELECT a.created_at, {CountryOf("a.id")} AS country,
                     EXISTS (SELECT 1 FROM services.services s WHERE s.account_id = a.id AND s.status IN ('Active', 'Suspended')) AS live
              FROM accounts.accounts a
            )
            SELECT country, count(*)::int, count(*) FILTER (WHERE live)::int,
                   count(*) FILTER (WHERE created_at >= @from::timestamp AT TIME ZONE @tz AND created_at < @end::timestamp AT TIME ZONE @tz)::int
            FROM c
            GROUP BY 1
            ORDER BY 2 DESC, 1
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var revenue = await Query(
            $"""
            {Effective}, e AS (
              SELECT coalesce(nullif(e.receiver ->> 'Country', ''), {CountryOf("e.account_id")}) AS country, e.*
              FROM effective e
              WHERE e.issue_date >= @from AND e.issue_date < @end
            )
            SELECT country, currency, count(*) FILTER (WHERE kind <> 'CreditNote')::int,
                   sum(sign * net - CASE WHEN total = 0 THEN 0 ELSE round(net * voided / total, 2) END), sum(sign * total - voided)
            FROM e
            GROUP BY 1, 2
            ORDER BY 2, 4 DESC, 1
            """,
            Row,
            ct,
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var country = new ReportColumn("Country", CellKind.Text, Empty: "ReportCol.NoCountry");
        return new(
            [
                Totals(new ReportTable("AccountsByCountry", [country, new("Accounts", CellKind.Count), new("WithServices", CellKind.Count), new("NewAccounts", CellKind.Count)], accounts)),
                Totals(new ReportTable("RevenueByCountry", [country, new("Currency", CellKind.Text), new("Documents", CellKind.Count), new("Net", CellKind.Money), new("Total", CellKind.Money)], revenue)),
            ],
            [])
        {
            Charts = [new("AccountsByCountry", "AccountsByCountry", "Country", ["WithServices"]), new("RevenueByCountry", "RevenueByCountry", "Country", ["Net"])],
        };
    }

    /// <summary>
    /// Pedidos por mes del proveedor y moneda: cuántos se hicieron, en qué estado están hoy y lo que facturó su factura
    /// inicial. Un pedido de total cero no tiene factura y suma cero.
    /// </summary>
    public async Task<Report> Orders(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rows = await Query(
            """
            SELECT date_trunc('month', o.placed_at AT TIME ZONE @tz)::date, o.currency, count(*)::int,
                   count(*) FILTER (WHERE o.status = 'Pending')::int, count(*) FILTER (WHERE o.status = 'Accepted')::int,
                   count(*) FILTER (WHERE o.status = 'Completed')::int, count(*) FILTER (WHERE o.status = 'Cancelled')::int,
                   coalesce(sum(d.total), 0)
            FROM orders.orders o
            LEFT JOIN billing.documents d ON d.id = o.invoice_id
            WHERE o.placed_at >= @from::timestamp AT TIME ZONE @tz AND o.placed_at < @end::timestamp AT TIME ZONE @tz
            GROUP BY 1, 2
            ORDER BY 1, 2
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Month", CellKind.Month), new("Currency", CellKind.Text), new("Orders", CellKind.Count), new("OrdersPending", CellKind.Count),
            new("OrdersAccepted", CellKind.Count), new("OrdersCompleted", CellKind.Count), new("OrdersCancelled", CellKind.Count), new("Invoiced", CellKind.Money),
        ];
        return new([Totals(new ReportTable("OrdersByMonth", columns, rows))], []) { Charts = [new("Orders", "OrdersByMonth", "Month", ["Orders"])] };
    }

    /// <summary>
    /// Permanencia de los clientes al día de hoy, de las cuentas que contrataron algo: desde el primer servicio que se
    /// activó hasta hoy si siguen con alguno activo o suspendido, o hasta la última baja si se fueron. Por tramos de meses
    /// enteros, con la mediana, y por año de la primera alta con cuántas siguen. Una cuenta que se fue con bajas sin fecha
    /// (algunas importadas) no se puede medir y sale en un aviso.
    /// </summary>
    public async Task<Report> Retention(CancellationToken ct)
    {
        const string tenure = """
            WITH a AS (
              SELECT account_id, min(coalesce(activated_at, created_at)) AS since, bool_or(status IN ('Active', 'Suspended')) AS current,
                     max(terminated_at) AS until
              FROM services.services
              WHERE status IN ('Active', 'Suspended', 'Terminated')
              GROUP BY 1
            ), t AS (
              SELECT current, since, age(CASE WHEN current THEN @now ELSE until END, since) AS span
              FROM a
              WHERE current OR until IS NOT NULL
            ), m AS (
              SELECT current, since, (extract(year FROM span) * 12 + extract(month FROM span))::int AS months FROM t
            )
            """;
        const string buckets = """
            count(*) FILTER (WHERE months < 3)::int, count(*) FILTER (WHERE months >= 3 AND months < 6)::int,
            count(*) FILTER (WHERE months >= 6 AND months < 12)::int, count(*) FILTER (WHERE months >= 12 AND months < 24)::int,
            count(*) FILTER (WHERE months >= 24 AND months < 36)::int, count(*) FILTER (WHERE months >= 36)::int,
            round(percentile_cont(0.5) WITHIN GROUP (ORDER BY months))::int
            """;
        var now = time.GetUtcNow();
        var summary = await Grouped(
            "RetentionBuckets",
            [
                new("Customers", CellKind.Key), new("Accounts", CellKind.Count), new("Under3Months", CellKind.Count), new("From3To6Months", CellKind.Count),
                new("From6To12Months", CellKind.Count), new("From1To2Years", CellKind.Count), new("From2To3Years", CellKind.Count),
                new("Over3Years", CellKind.Count), new("MedianMonths", CellKind.Count),
            ],
            $"""
            {tenure}
            SELECT grouping(current)::int, CASE WHEN grouping(current) = 1 THEN NULL WHEN current THEN 'ReportCustomers.Current' ELSE 'ReportCustomers.Former' END,
                   count(*)::int, {buckets}
            FROM m
            GROUP BY GROUPING SETS ((current), ())
            ORDER BY 1, current DESC
            """,
            ct,
            ("now", now)).ConfigureAwait(false);
        var cohorts = await Query(
            $"""
            {tenure}
            SELECT extract(year FROM since AT TIME ZONE @tz)::int::text, count(*)::int, count(*) FILTER (WHERE current)::int,
                   round(count(*) FILTER (WHERE current) * 100.0 / count(*), 2), round(percentile_cont(0.5) WITHIN GROUP (ORDER BY months))::int
            FROM m
            GROUP BY 1
            ORDER BY 1
            """,
            Row,
            ct,
            ("now", now),
            ("tz", TimeZone)).ConfigureAwait(false);
        var unmeasured = (await Query(
            """
            SELECT count(*)::int FROM (
              SELECT account_id FROM services.services WHERE status IN ('Active', 'Suspended', 'Terminated')
              GROUP BY 1 HAVING NOT bool_or(status IN ('Active', 'Suspended')) AND max(terminated_at) IS NULL
            ) x
            """,
            r => r.GetInt32(0),
            ct).ConfigureAwait(false))[0];
        return new(
            [
                summary,
                new ReportTable(
                    "RetentionCohorts",
                    [new("Year", CellKind.Text), new("Started", CellKind.Count), new("StillActive", CellKind.Count), new("RetentionRate", CellKind.Percent), new("MedianMonths", CellKind.Count)],
                    cohorts),
            ],
            unmeasured > 0 ? [new("RetentionUndated", [unmeasured.ToString(CultureInfo.InvariantCulture)])] : [])
        {
            Charts =
            [
                new("Retention", "RetentionBuckets", null, ["Under3Months", "From3To6Months", "From6To12Months", "From1To2Years", "From2To3Years", "Over3Years"]),
                new("RetentionCohorts", "RetentionCohorts", "Year", ["RetentionRate"]),
            ],
        };
    }

    /// <summary>Si están las tablas del módulo de soporte (docs/reportes): hasta que llegue a esta rama, no.</summary>
    private Task<bool> SupportInstalled(CancellationToken ct) => Installed(ct, "support.tickets", "support.messages", "support.departments");

    /// <summary>
    /// Ventas con el IVA por alícuota, más exento y no gravado, por mes de la fecha fiscal y moneda, y el detalle por
    /// documento con una columna de neto y otra de IVA por cada alícuota del período. Las notas de crédito restan.
    /// </summary>
    public async Task<Report> Sales(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var summary = await Query(
            $"""
            {Effective}, parts AS (
              SELECT e.fiscal_date, e.currency, e.sign, e.voided, e.total, 'Taxed' AS treatment, (a ->> 'Rate')::numeric AS rate,
                     (a ->> 'Net')::numeric AS net, (a ->> 'Tax')::numeric AS tax
              FROM effective e CROSS JOIN LATERAL jsonb_array_elements(coalesce(e.aliquots, '[]'::jsonb)) a
              UNION ALL
              SELECT fiscal_date, currency, sign, voided, total, 'Exempt', NULL, exempt, 0 FROM effective WHERE exempt <> 0
              UNION ALL
              SELECT fiscal_date, currency, sign, voided, total, 'NotTaxed', NULL, not_taxed, 0 FROM effective WHERE not_taxed <> 0
            )
            SELECT date_trunc('month', fiscal_date)::date, currency, treatment, rate,
                   sum(sign * net - CASE WHEN total = 0 THEN 0 ELSE round(net * voided / total, 2) END),
                   sum(sign * tax - CASE WHEN total = 0 THEN 0 ELSE round(tax * voided / total, 2) END)
            FROM parts
            WHERE fiscal_date >= @from AND fiscal_date < @end AND (net <> 0 OR tax <> 0)
            GROUP BY 1, 2, 3, 4
            ORDER BY 1, 2, treatment <> 'Taxed', rate DESC, 3
            """,
            r => new object?[] { r.GetFieldValue<DateOnly>(0), r.GetString(1), "TaxTreatment." + r.GetString(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5) },
            ct,
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);

        var documents = await Query(
            $$"""
            {{Effective}}
            SELECT e.fiscal_date, e.kind, e.number, e.type_code, e.point_of_sale, e.fiscal_number, e.authorization_code, e.currency,
                   e.total, e.voided, e.exempt, e.not_taxed, e.sign, coalesce(e.aliquots, '[]'::jsonb)::text, coalesce(e.receiver, '{}'::jsonb)::text,
                   coalesce(nullif(a.company_name, ''), a.name)
            FROM effective e JOIN accounts.accounts a ON a.id = e.account_id
            WHERE e.fiscal_date >= @from AND e.fiscal_date < @end AND e.voided < e.total
            ORDER BY e.fiscal_date, e.id
            """,
            r => new SaleRow(
                r.GetFieldValue<DateOnly>(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetDecimal(9),
                r.GetDecimal(10), r.GetDecimal(11), r.GetInt32(12), JsonSerializer.Deserialize<List<AliquotTotal>>(r.GetString(13))!,
                r.GetString(14), r.GetString(15)),
            ct,
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);

        return new([Totals(new ReportTable("SalesByAliquot", SalesColumns, summary)), SalesDetail(documents)], []) { Charts = [new("Sales", "SalesByAliquot", "Month", ["Vat"])] };
    }

    private static readonly IReadOnlyList<ReportColumn> SalesColumns =
    [
        new("Month", CellKind.Month), new("Currency", CellKind.Text), new("Treatment", CellKind.Key), new("Rate", CellKind.Rate),
        new("Net", CellKind.Money), new("Vat", CellKind.Money),
    ];

    private sealed record SaleRow(
        DateOnly Date, string Kind, long Number, int? TypeCode, int? PointOfSale, long? FiscalNumber, string? Cae, string Currency, decimal Total,
        decimal Voided, decimal Exempt, decimal NotTaxed, int Sign, List<AliquotTotal> Aliquots, string Receiver, string AccountName);

    /// <summary>El libro de ventas: una fila por documento con lo que vale después de lo anulado sin nota.</summary>
    private static ReportTable SalesDetail(IReadOnlyList<SaleRow> rows)
    {
        var rates = rows.SelectMany(r => r.Aliquots.Select(a => a.Rate)).Distinct().OrderDescending().ToList();
        List<ReportColumn> columns =
        [
            new("Date", CellKind.Date), new("Document", CellKind.Key), new("Number", CellKind.Text), new("Voucher", CellKind.Text),
            new("Cae", CellKind.Text), new("Receiver", CellKind.Text), new("TaxId", CellKind.Text), new("VatCondition", CellKind.Count),
            new("Currency", CellKind.Text),
        ];
        foreach (var rate in rates)
        {
            columns.Add(new("NetAt", CellKind.Money, rate));
            columns.Add(new("VatAt", CellKind.Money, rate));
        }

        columns.AddRange([new("Exempt", CellKind.Money), new("NotTaxed", CellKind.Money), new("Voided", CellKind.Money), new("Total", CellKind.Money)]);
        var detail = rows.Select(r =>
        {
            var receiver = JsonSerializer.Deserialize<ReceiverSnapshot?>(r.Receiver.Length > 2 ? r.Receiver : "null");
            decimal Part(decimal amount) => r.Sign * (amount - (r.Total == 0m ? 0m : Rounding.Commercial(amount * r.Voided / r.Total)));
            var cells = new List<object?>
            {
                r.Date,
                "DocumentKind." + r.Kind,
                r.Number.ToString(CultureInfo.InvariantCulture),
                r.TypeCode is { } type ? Voucher(type, r.PointOfSale!.Value, r.FiscalNumber!.Value) : null,
                r.Cae,
                receiver is { LegalName.Length: > 0 } ? receiver.LegalName : r.AccountName,
                receiver is { DocumentNumber.Length: > 0 } ? receiver.DocumentNumber : null,
                receiver?.VatCondition,
                r.Currency,
            };
            foreach (var rate in rates)
            {
                var aliquot = r.Aliquots.Find(a => a.Rate == rate);
                cells.Add(aliquot is null ? null : Part(aliquot.Net));
                cells.Add(aliquot is null ? null : Part(aliquot.Tax));
            }

            cells.AddRange([Part(r.Exempt), Part(r.NotTaxed), r.Voided, (r.Sign * r.Total) - r.Voided]);
            return cells.ToArray();
        }).ToList();
        return new ReportTable("SalesDocuments", columns, detail);
    }

    /// <summary>Tipo, punto de venta y número del comprobante, como en el impreso: 006-00001-00000123.</summary>
    public static string Voucher(int typeCode, int pointOfSale, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"{typeCode:D3}-{pointOfSale:D5}-{number:D8}");

    /// <summary>
    /// Débito fiscal del período en pesos, por mes del comprobante: IVA por alícuota, exento, no gravado y las
    /// percepciones y otros tributos que informó cada comprobante. Cada importe se pasa a pesos con la cotización del
    /// comprobante y se redondea a centavos. Las notas de crédito restan.
    /// </summary>
    public async Task<Report> Taxes(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var vouchers = await Vouchers(range.From, range.End, ct).ConfigureAwait(false);
        var notes = new List<ReportNote>();
        var missingRate = vouchers.Where(v => v.ExchangeRate is null).ToList();
        if (missingRate.Count > 0)
        {
            notes.Add(new("NoExchangeRate", [Join(missingRate.Select(v => v.Label))]));
        }

        var sums = new SortedDictionary<(DateOnly Month, int Order, string Concept, string Detail, decimal? Rate), (decimal Base, decimal Amount)>();
        void Add(DateOnly month, int order, string concept, string detail, decimal? rate, decimal @base, decimal amount)
        {
            var key = (month, order, concept, detail, rate);
            var (b, a) = sums.GetValueOrDefault(key);
            sums[key] = (b + @base, a + amount);
        }

        foreach (var v in vouchers.Where(v => v.ExchangeRate is not null))
        {
            var month = new DateOnly(v.Date.Year, v.Date.Month, 1);
            decimal Ars(decimal amount) => v.Sign * Rounding.Commercial(amount * v.ExchangeRate!.Value);
            foreach (var a in v.Aliquots)
            {
                Add(month, 0, "ReportTax.Vat", string.Empty, a.Rate, Ars(a.Net), Ars(a.Tax));
            }

            if (v.Exempt != 0m)
            {
                Add(month, 1, "ReportTax.Exempt", string.Empty, null, Ars(v.Exempt), 0m);
            }

            if (v.NotTaxed != 0m)
            {
                Add(month, 2, "ReportTax.NotTaxed", string.Empty, null, Ars(v.NotTaxed), 0m);
            }

            foreach (var t in v.OtherTaxes)
            {
                Add(month, 3, "ReportTax.Other", t.Description, t.Rate, Ars(t.Base), Ars(t.Amount));
            }
        }

        notes.AddRange(await FiscalGaps(range.From, range.End, ct).ConfigureAwait(false));
        var rows = sums.Select(s => new object?[] { s.Key.Month, s.Key.Concept, s.Key.Detail, s.Key.Rate, s.Value.Base, s.Value.Amount }).ToList();
        ReportColumn[] columns = [new("Month", CellKind.Month), new("Concept", CellKind.Key), new("Detail", CellKind.Text), new("Rate", CellKind.Rate), new("Base", CellKind.Money), new("Amount", CellKind.Money)];
        return new([Totals(new ReportTable("Taxes", columns, rows))], notes) { Charts = [new("Taxes", "Taxes", "Month", ["Amount"])] };
    }

    /// <summary>
    /// Lo que deja incompleto un libro o un reporte fiscal del período: documentos propios emitidos que todavía no tienen
    /// comprobante. Quedan afuera las cuentas sin comprobante y las notas cuyo original no tiene, que nunca lo van a
    /// tener (sección 7.1 del modelo de dominio).
    /// </summary>
    private async Task<List<ReportNote>> FiscalGaps(DateOnly from, DateOnly end, CancellationToken ct)
    {
        var pending = (await Query(
            """
            SELECT count(*)::int
            FROM billing.documents d JOIN accounts.accounts a ON a.id = d.account_id
            WHERE d.issue_date >= @from AND d.issue_date < @end AND NOT d.imported AND NOT a.no_fiscal_document
              AND NOT EXISTS (SELECT 1 FROM fiscal.documents f WHERE f.document_id = d.id)
              AND (d.kind = 'Invoice' OR EXISTS (SELECT 1 FROM fiscal.documents f WHERE f.document_id = d.related_document_id))
            """,
            r => r.GetInt32(0),
            ct,
            ("from", from),
            ("end", end)).ConfigureAwait(false))[0];
        return pending > 0 ? [new("WithoutVoucher", [pending.ToString(CultureInfo.InvariantCulture)])] : [];
    }

    /// <summary>Exportación del Libro IVA Digital de ventas de un mes (RG 4597): los dos archivos y lo que hay que revisar.</summary>
    public async Task<LibroIva> LibroIva(DateOnly month, CancellationToken ct)
    {
        var from = new DateOnly(month.Year, month.Month, 1);
        var vouchers = await Vouchers(from, from.AddMonths(1), ct).ConfigureAwait(false);
        var libro = LibroIvaDigital.Build(vouchers);
        return libro with { Notes = [.. libro.Notes, .. await FiscalGaps(from, from.AddMonths(1), ct).ConfigureAwait(false)] };
    }

    /// <summary>
    /// Todos los comprobantes autorizados entre las fechas. Un documento importado puede tener más de uno (D-17), por
    /// ejemplo la factura y la nota que la anuló en el sistema anterior, y cada uno es un registro del libro. Los del
    /// núcleo toman los importes del documento comercial, que es lo que se autorizó, y los importados los que guardó el
    /// sistema anterior en cada comprobante. Si es factura o nota lo dice el tipo, según el mapa del emisor.
    /// </summary>
    private Task<List<FiscalVoucher>> Vouchers(DateOnly from, DateOnly end, CancellationToken ct) => Query(
        """
        SELECT f.date, f.type_code, f.point_of_sale, f.number, f.imported, f.payload::text,
               coalesce((SELECT k.key FROM fiscal.issuers i, jsonb_each(i.type_map) l, jsonb_each_text(l.value) k
                         WHERE i.id = f.issuer_id AND k.value = f.type_code::text LIMIT 1), d.kind),
               d.currency, coalesce(d.aliquots, '[]'::jsonb)::text, d.exempt, d.not_taxed, d.total,
               coalesce(d.receiver, '{}'::jsonb)::text, coalesce(nullif(a.company_name, ''), a.name)
        FROM fiscal.documents f
        JOIN billing.documents d ON d.id = f.document_id
        JOIN accounts.accounts a ON a.id = d.account_id
        WHERE f.date >= @from AND f.date < @end
        ORDER BY f.date, f.type_code, f.point_of_sale, f.number
        """,
        r => FiscalVoucher.From(
            r.GetFieldValue<DateOnly>(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetBoolean(4), r.GetString(5),
            Enum.Parse<DocumentKind>(r.GetString(6)), r.GetString(7), JsonSerializer.Deserialize<List<AliquotTotal>>(r.GetString(8))!,
            r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetString(12), r.GetString(13)),
        ct,
        ("from", from),
        ("end", end));

    /// <summary>
    /// Deuda por antigüedad al día de hoy: lo que falta cobrar de facturas y notas de débito emitidas, por días desde
    /// el vencimiento. Un resumen por moneda y el detalle por cuenta, de la mayor deuda a la menor.
    /// </summary>
    public async Task<Report> Aging(CancellationToken ct)
    {
        var accounts = await Query(
            """
            WITH open AS (
              SELECT d.account_id, d.currency, d.due_date, d.total - d.credited - d.allocated AS pending, @today - d.due_date AS late
              FROM billing.documents d
              WHERE d.issue_date IS NOT NULL AND d.kind <> 'CreditNote' AND d.total - d.credited - d.allocated > 0
            )
            SELECT o.account_id, coalesce(nullif(a.company_name, ''), a.name), o.currency, count(*)::int,
                   coalesce(sum(pending) FILTER (WHERE late <= 0), 0),
                   coalesce(sum(pending) FILTER (WHERE late BETWEEN 1 AND 30), 0),
                   coalesce(sum(pending) FILTER (WHERE late BETWEEN 31 AND 60), 0),
                   coalesce(sum(pending) FILTER (WHERE late BETWEEN 61 AND 90), 0),
                   coalesce(sum(pending) FILTER (WHERE late > 90), 0),
                   sum(pending), min(o.due_date)
            FROM open o JOIN accounts.accounts a ON a.id = o.account_id
            GROUP BY 1, 2, 3
            ORDER BY 3, 10 DESC, 1
            """,
            r => new object?[] { r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetFieldValue<DateOnly>(10) },
            ct,
            ("today", Today)).ConfigureAwait(false);

        ReportColumn[] buckets = [new("Current", CellKind.Money), new("Late30", CellKind.Money), new("Late60", CellKind.Money), new("Late90", CellKind.Money), new("LateMore", CellKind.Money), new("Total", CellKind.Money)];
        var summary = accounts.GroupBy(a => (string)a[2]!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            new object?[] { g.Key, g.Count(), g.Sum(a => (int)a[3]!) }.Concat(Enumerable.Range(4, 6).Select(i => (object?)g.Sum(a => (decimal)a[i]!))).ToArray()).ToList();
        return new(
            [
                new ReportTable("AgingSummary", [new("Currency", CellKind.Text), new("Accounts", CellKind.Count), new("Documents", CellKind.Count), .. buckets], summary),
                new ReportTable("AgingAccounts", [new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Currency", CellKind.Text), new("Documents", CellKind.Count), .. buckets, new("OldestDue", CellKind.Date)], accounts),
            ],
            [])
        {
            Charts = [new("Aging", "AgingSummary", null, ["Current", "Late30", "Late60", "Late90", "LateMore"])],
        };
    }

    /// <summary>
    /// Servicios suspendidos al día de hoy: por qué (el motivo del staff, o la falta de pago si no tiene), hasta dónde
    /// están pagos y los días de mora, la deuda de la cuenta en la moneda del servicio y cuándo se da de baja: lo pagado más el plazo de baja del
    /// producto o el general si es por falta de pago, o la baja programada si es antes. Los totales no suman la deuda,
    /// que es de la cuenta y se repetiría.
    /// </summary>
    public async Task<Report> Suspended(CancellationToken ct)
    {
        var rows = await Query(
            $"""
            SELECT s.id::text, s.account_id, coalesce(nullif(a.company_name, ''), a.name), p.name, s.label, s.suspend_reason, s.currency,
                   CASE WHEN s.cycle_unit IN ('Month', 'Year') THEN {Monthly} END, s.paid_through, greatest(@today - s.paid_through, 0),
                   (SELECT coalesce(sum(d.total - d.credited - d.allocated), 0) FROM billing.documents d
                    WHERE d.account_id = s.account_id AND d.currency = s.currency AND d.issue_date IS NOT NULL AND d.kind <> 'CreditNote'
                      AND d.total - d.credited - d.allocated > 0),
                   least(s.cancel_at, CASE WHEN s.suspend_reason IS NULL THEN s.paid_through + coalesce(p.termination_days, @termination::int) END)
            FROM services.services s
            JOIN accounts.accounts a ON a.id = s.account_id
            JOIN catalog.products p ON p.id = s.product_id
            WHERE s.status = 'Suspended'
            ORDER BY s.currency, s.paid_through NULLS LAST, s.id
            """,
            Row,
            ct,
            ("today", Today),
            ("termination", options.Value.TerminationDays)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Service", CellKind.Text), new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Product", CellKind.Text),
            new("Label", CellKind.Text), new("Reason", CellKind.Text, Empty: "ReportCol.ForNonPayment"), new("Currency", CellKind.Text),
            new("Mrr", CellKind.Money), new("PaidThrough", CellKind.Date), new("DaysLate", CellKind.Count), new("AccountDebt", CellKind.Money),
            new("TerminatesOn", CellKind.Date),
        ];
        return new([Totals(new ReportTable("SuspendedServices", columns, rows), "DaysLate", "AccountDebt")], [])
        {
            Charts = [new("Suspended", "SuspendedServices", "Product", ["Mrr"])],
        };
    }

    /// <summary>
    /// El pago como en los cobros por medio, con su ID en la pasarela: "Mercado Pago mp-1" o "transferencia (manual) t-1".
    /// Pide el pago como p y su instancia de pasarela como i.
    /// </summary>
    private const string PaymentReference = """
        CASE WHEN i.name IS NOT NULL THEN i.name
             WHEN position(':' IN p.channel) > 0 THEN split_part(p.channel, ':', 2) || ' (' || split_part(p.channel, ':', 1) || ')'
             ELSE p.channel END || coalesce(' ' || nullif(p.external_id, ''), '')
        """;

    private sealed record StatementEvent(DateOnly Day, string Concept, string? Reference, string Currency, decimal Debit, decimal Credit);

    /// <summary>
    /// Estado de cuenta de una cuenta en el período, por moneda: el saldo al empezar, cada movimiento con el saldo que
    /// deja y el saldo al cierre. Debe: las facturas y notas de débito y lo que volvió al pagador (reembolsos y
    /// contracargos completos, menos lo que no se pudo cubrir). Haber: las notas de crédito, lo anulado sin nota en el
    /// sistema anterior, los cobros (todo pago que se cobró alguna vez) y los ajustes de saldo a favor que no vienen de un
    /// pago ni de un documento. El saldo es lo que la cuenta debe menos su saldo a favor. Si el período llega a hoy, se
    /// compara con el saldo que dicen los documentos y el saldo a favor, y una diferencia (por ejemplo, de datos
    /// importados que no cierran) sale en un aviso.
    /// </summary>
    public async Task<Report> Statement(long accountId, MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var name = (await Query(
            "SELECT coalesce(nullif(company_name, ''), name) FROM accounts.accounts WHERE id = @account",
            r => r.GetString(0),
            ct,
            ("account", accountId)).ConfigureAwait(false)).SingleOrDefault();
        if (name is null)
        {
            return new([], [new("StatementNoAccount", [accountId.ToString(CultureInfo.InvariantCulture)])]);
        }

        var events = await Query(
            $"""
            {Effective}, events AS (
              SELECT issue_date AS day, 1 AS ord, id, 'DocumentKind.' || kind AS concept, number::text AS reference, currency,
                     CASE WHEN kind = 'CreditNote' THEN 0 ELSE total END AS debit, CASE WHEN kind = 'CreditNote' THEN total ELSE 0 END AS credit
              FROM effective WHERE account_id = @account
              UNION ALL
              SELECT issue_date, 2, id, 'ReportConcept.VoidedImported', number::text, currency, 0, voided FROM effective WHERE account_id = @account AND voided > 0
              UNION ALL
              SELECT (p.created_at AT TIME ZONE @tz)::date, 3, p.id, 'ReportConcept.Payment', {PaymentReference}, p.currency, 0, p.amount
              FROM payments.payments p LEFT JOIN platform.module_instances i ON p.channel = 'instance:' || i.id
              WHERE p.account_id = @account AND p.status NOT IN ('Pending', 'Rejected')
              UNION ALL
              SELECT (r.updated_at AT TIME ZONE @tz)::date, 4, r.id, CASE r.origin WHEN 'Chargeback' THEN 'ReportConcept.Chargeback' ELSE 'ReportConcept.Refund' END,
                     p.external_id, p.currency, r.amount - r.uncovered, 0
              FROM payments.refunds r JOIN payments.payments p ON p.id = r.payment_id
              WHERE p.account_id = @account AND r.status = 'Completed'
              UNION ALL
              SELECT (e.created_at AT TIME ZONE @tz)::date, 5, e.id, 'ReportConcept.CreditAdjustment', e.reason, e.currency, greatest(-e.amount, 0), greatest(e.amount, 0)
              FROM billing.credit_entries e
              WHERE e.account_id = @account AND e.payment_id IS NULL AND e.document_id IS NULL
            )
            SELECT day, concept, reference, currency, debit, credit FROM events WHERE day < @end ORDER BY currency, day, ord, id
            """,
            r => new StatementEvent(r.GetFieldValue<DateOnly>(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5)),
            ct,
            ("account", accountId),
            ("tz", TimeZone),
            ("end", range.End)).ConfigureAwait(false);
        var today = (await Query(
            """
            SELECT currency, sum(amount) FROM (
              SELECT currency, total - credited - allocated AS amount FROM billing.documents
              WHERE account_id = @account AND issue_date IS NOT NULL AND kind <> 'CreditNote'
              UNION ALL
              SELECT currency, -amount FROM billing.credit_entries WHERE account_id = @account
            ) x
            GROUP BY 1
            """,
            r => (Currency: r.GetString(0), Balance: r.GetDecimal(1)),
            ct,
            ("account", accountId)).ConfigureAwait(false)).ToDictionary(t => t.Currency, t => t.Balance);

        var current = range.End > Today;
        var movements = new List<object?[]>();
        var summary = new List<object?[]>();
        var notes = new List<ReportNote>();
        foreach (var currency in events.Select(e => e.Currency).Union(today.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var mine = events.Where(e => e.Currency == currency).ToList();
            var opening = mine.Where(e => e.Day < range.From).Sum(e => e.Debit - e.Credit);
            var period = mine.Where(e => e.Day >= range.From).ToList();
            var balance = opening;
            movements.Add([range.From, "ReportConcept.Opening", null, currency, null, null, opening]);
            foreach (var e in period)
            {
                balance += e.Debit - e.Credit;
                movements.Add([e.Day, e.Concept, e.Reference, currency, e.Debit == 0m ? null : e.Debit, e.Credit == 0m ? null : e.Credit, balance]);
            }

            var now = current ? today.GetValueOrDefault(currency) : (decimal?)null;
            summary.Add([accountId, name, currency, opening, period.Sum(e => e.Debit), period.Sum(e => e.Credit), balance, now]);
            if (now is { } ledger && ledger != balance)
            {
                notes.Add(new("StatementDifference", [currency, (ledger - balance).ToString("0.00", CultureInfo.InvariantCulture)]));
            }
        }

        return new(
            [
                new ReportTable(
                    "StatementSummary",
                    [
                        new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Currency", CellKind.Text), new("Opening", CellKind.Money),
                        new("Debit", CellKind.Money), new("Credit", CellKind.Money), new("Closing", CellKind.Money), new("BalanceToday", CellKind.Money),
                    ],
                    summary),
                new ReportTable(
                    "StatementMovements",
                    [
                        new("Date", CellKind.Date), new("Concept", CellKind.Key), new("Reference", CellKind.Text), new("Currency", CellKind.Text),
                        new("Debit", CellKind.Money), new("Credit", CellKind.Money), new("Balance", CellKind.Money),
                    ],
                    movements),
            ],
            notes);
    }

    /// <summary>
    /// Movimientos del saldo a favor en el período: por moneda, el saldo al empezar, lo que entró, lo que se usó o se
    /// devolvió y el saldo al cierre. Cada movimiento con su motivo, su documento y su pago, y el saldo de hoy de cada
    /// cuenta que tiene. Sale del libro de saldo a favor, que solo se agrega.
    /// </summary>
    public async Task<Report> Credit(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        const string window = "@from::timestamp AT TIME ZONE @tz";
        const string close = "@end::timestamp AT TIME ZONE @tz";
        var summary = await Query(
            $"""
            SELECT currency, coalesce(sum(amount) FILTER (WHERE created_at < {window}), 0),
                   coalesce(sum(amount) FILTER (WHERE created_at >= {window} AND created_at < {close} AND amount > 0), 0),
                   coalesce(sum(amount) FILTER (WHERE created_at >= {window} AND created_at < {close} AND amount < 0), 0),
                   coalesce(sum(amount) FILTER (WHERE created_at < {close}), 0)
            FROM billing.credit_entries
            GROUP BY 1
            HAVING bool_or(created_at < {close})
            ORDER BY 1
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var entries = await Query(
            $"""
            SELECT (e.created_at AT TIME ZONE @tz)::date, e.account_id, coalesce(nullif(a.company_name, ''), a.name), e.currency, e.amount, e.reason,
                   'DocumentKind.' || d.kind, d.number::text, {PaymentReference}
            FROM billing.credit_entries e
            JOIN accounts.accounts a ON a.id = e.account_id
            LEFT JOIN billing.documents d ON d.id = e.document_id
            LEFT JOIN payments.payments p ON p.id = e.payment_id
            LEFT JOIN platform.module_instances i ON p.channel = 'instance:' || i.id
            WHERE e.created_at >= {window} AND e.created_at < {close}
            ORDER BY e.created_at, e.id
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var balances = await Query(
            """
            SELECT e.account_id, coalesce(nullif(a.company_name, ''), a.name), e.currency, sum(e.amount)
            FROM billing.credit_entries e JOIN accounts.accounts a ON a.id = e.account_id
            GROUP BY 1, 2, 3
            HAVING sum(e.amount) <> 0
            ORDER BY 3, 4 DESC, 1
            """,
            Row,
            ct).ConfigureAwait(false);
        return new(
            [
                new ReportTable("CreditSummary", [new("Currency", CellKind.Text), new("Opening", CellKind.Money), new("CreditAdded", CellKind.Money), new("CreditUsed", CellKind.Money), new("Closing", CellKind.Money)], summary),
                Totals(new ReportTable(
                    "CreditEntries",
                    [
                        new("Date", CellKind.Date), new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Currency", CellKind.Text),
                        new("Amount", CellKind.Money), new("Reason", CellKind.Text), new("Document", CellKind.Key), new("Number", CellKind.Text), new("Payment", CellKind.Text),
                    ],
                    entries)),
                Totals(new ReportTable("CreditBalances", [new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Currency", CellKind.Text), new("Balance", CellKind.Money)], balances)),
            ],
            [])
        {
            Charts = [new("CreditBalances", "CreditBalances", "AccountName", ["Balance"])],
        };
    }

    /// <summary>
    /// Listado de clientes al día de hoy, para exportar: los datos de cada cuenta con su identidad fiscal vigente y cuántos
    /// servicios tiene activos o suspendidos.
    /// </summary>
    public async Task<Report> AccountList(CancellationToken ct)
    {
        var rows = await Query(
            """
            SELECT a.id, a.name, nullif(a.company_name, ''), a.email, a.phone, t.country, t.province, t.city, 'ReportDocType.' || t.document_type,
                   nullif(t.document_number, ''), t.vat_condition, 'AccountStatus.' || a.status, (a.created_at AT TIME ZONE @tz)::date, a.currency,
                   (SELECT count(*) FROM services.services s WHERE s.account_id = a.id AND s.status IN ('Active', 'Suspended'))::int
            FROM accounts.accounts a
            LEFT JOIN LATERAL (
              SELECT * FROM accounts.tax_identities x WHERE x.account_id = a.id AND x.valid_to IS NULL ORDER BY x.valid_from DESC, x.id DESC LIMIT 1
            ) t ON true
            ORDER BY a.id
            """,
            Row,
            ct,
            ("tz", TimeZone)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Company", CellKind.Text), new("Email", CellKind.Text),
            new("Phone", CellKind.Text), new("Country", CellKind.Text), new("Province", CellKind.Text), new("City", CellKind.Text),
            new("DocumentType", CellKind.Key), new("TaxId", CellKind.Text), new("VatCondition", CellKind.Count), new("Status", CellKind.Key),
            new("Created", CellKind.Date), new("AccountCurrency", CellKind.Text), new("LiveServices", CellKind.Count),
        ];
        return new([Totals(new ReportTable("AccountList", columns, rows), "VatCondition")], []);
    }

    /// <summary>
    /// Listado de servicios vivos al día de hoy (por activar, activos, suspendidos y con falla), para exportar: cuenta,
    /// producto, estado, ciclo, precio por ciclo con descuento, ingreso recurrente mensual, próximo vencimiento, hasta
    /// dónde está pago y servidor.
    /// </summary>
    public async Task<Report> ServiceList(CancellationToken ct)
    {
        var rows = await Query(
            $"""
            SELECT s.id::text, s.account_id, coalesce(nullif(a.company_name, ''), a.name), pg.name, p.name, s.label, 'ServiceStatus.' || s.status,
                   s.cycle_count, 'ReportCycle.' || s.cycle_unit, s.currency, round(s.unit_price * s.quantity * (1 - coalesce(s.discount_percent, 0) / 100), 2),
                   CASE WHEN s.cycle_unit IN ('Month', 'Year') THEN {Monthly} END, s.next_due_date, s.paid_through, i.name,
                   (s.created_at AT TIME ZONE @tz)::date, (s.activated_at AT TIME ZONE @tz)::date
            FROM services.services s
            JOIN accounts.accounts a ON a.id = s.account_id
            JOIN catalog.products p ON p.id = s.product_id
            LEFT JOIN catalog.product_groups pg ON pg.id = p.group_id
            LEFT JOIN platform.module_instances i ON i.id = s.module_instance_id
            WHERE s.status IN ('Pending', 'Provisioning', 'Active', 'Suspended', 'Failed')
            ORDER BY s.id
            """,
            Row,
            ct,
            ("tz", TimeZone)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Service", CellKind.Text), new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Group", CellKind.Text),
            new("Product", CellKind.Text), new("Label", CellKind.Text), new("Status", CellKind.Key), new("CycleCount", CellKind.Count),
            new("Cycle", CellKind.Key), new("Currency", CellKind.Text), new("Price", CellKind.Money), new("Mrr", CellKind.Money),
            new("NextDue", CellKind.Date), new("PaidThrough", CellKind.Date), new("Server", CellKind.Text, Empty: "ReportCol.NoServer"),
            new("Created", CellKind.Date), new("Activated", CellKind.Date),
        ];
        return new([Totals(new ReportTable("ServiceList", columns, rows), "CycleCount")], []);
    }

    /// <summary>
    /// Listado de facturas y notas emitidas en el período, para exportar: el comprobante fiscal si tiene, la cuenta, el
    /// total (las notas de crédito en negativo, así la columna suma lo facturado), lo acreditado, lo pagado, lo que falta
    /// y el estado, que sale de los importes como en el resto del sistema.
    /// </summary>
    public async Task<Report> InvoiceList(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rows = await Query(
            """
            SELECT d.issue_date, d.kind, d.number, f.type_code, f.point_of_sale, f.number, f.authorization_code, d.account_id,
                   coalesce(nullif(a.company_name, ''), a.name), d.currency, d.total, d.credited, d.allocated, d.due_date
            FROM billing.documents d
            JOIN accounts.accounts a ON a.id = d.account_id
            LEFT JOIN LATERAL (
              SELECT x.type_code, x.point_of_sale, x.number, x.authorization_code FROM fiscal.documents x WHERE x.document_id = d.id ORDER BY x.date, x.id LIMIT 1
            ) f ON true
            WHERE d.issue_date >= @from AND d.issue_date < @end
            ORDER BY d.issue_date, d.id
            """,
            r =>
            {
                var note = r.GetString(1) == nameof(DocumentKind.CreditNote);
                var (total, credited, allocated) = (r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12));
                return new object?[]
                {
                    r.GetFieldValue<DateOnly>(0), "DocumentKind." + r.GetString(1), r.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? null : Voucher(r.GetInt32(3), r.GetInt32(4), r.GetInt64(5)), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7), r.GetString(8),
                    r.GetString(9), note ? -total : total, note ? null : credited, note ? null : allocated,
                    note ? null : InvoiceRules.Pending(total, credited, allocated), r.GetFieldValue<DateOnly>(13),
                    note ? null : "InvoiceStatus." + InvoiceRules.Status(true, total, credited, allocated),
                };
            },
            ct,
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Date", CellKind.Date), new("Document", CellKind.Key), new("Number", CellKind.Text), new("Voucher", CellKind.Text), new("Cae", CellKind.Text),
            new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Currency", CellKind.Text), new("Total", CellKind.Money),
            new("Credited", CellKind.Money), new("Paid", CellKind.Money), new("Balance", CellKind.Money), new("DueDate", CellKind.Date), new("Status", CellKind.Key),
        ];
        return new([Totals(new ReportTable("InvoiceList", columns, rows))], []);
    }

    /// <summary>
    /// Listado de pagos recibidos en el período, en todos sus estados, para exportar: la cuenta, el canal como en los cobros
    /// por medio, el ID en la pasarela, el estado, lo cobrado, la comisión, lo devuelto, lo que queda y las facturas a las
    /// que está imputado hoy. Los totales suman solo lo que se cobró, como los cobros por medio: sin pendientes ni
    /// rechazados.
    /// </summary>
    public async Task<Report> PaymentList(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rows = await Query(
            """
            SELECT (p.created_at AT TIME ZONE @tz)::date, p.account_id, coalesce(nullif(a.company_name, ''), a.name), p.channel, coalesce(i.name, ''),
                   nullif(p.external_id, ''), p.status, p.currency, p.amount, p.fee, p.refunded,
                   (SELECT string_agg(d.number::text, ', ' ORDER BY d.number)
                    FROM payments.allocations l JOIN billing.documents d ON d.id = l.document_id
                    WHERE l.payment_id = p.id AND l.reversed_at IS NULL)
            FROM payments.payments p
            JOIN accounts.accounts a ON a.id = p.account_id
            LEFT JOIN platform.module_instances i ON p.channel = 'instance:' || i.id
            WHERE p.created_at >= @from::timestamp AT TIME ZONE @tz AND p.created_at < @end::timestamp AT TIME ZONE @tz
            ORDER BY p.created_at, p.id
            """,
            r => new object?[]
            {
                r.GetFieldValue<DateOnly>(0), r.GetInt64(1), r.GetString(2), Channel(r.GetString(3), r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5),
                "PaymentStatus." + r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10),
                r.GetDecimal(8) - r.GetDecimal(9) - r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetString(11),
            },
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Date", CellKind.Date), new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Channel", CellKind.Text),
            new("ExternalId", CellKind.Text), new("Status", CellKind.Key), new("Currency", CellKind.Text), new("Collected", CellKind.Money),
            new("Fees", CellKind.Money), new("Refunded", CellKind.Money), new("NetCollected", CellKind.Money), new("Invoices", CellKind.Text),
        ];
        string[] uncollected = ["PaymentStatus." + nameof(PaymentStatus.Pending), "PaymentStatus." + nameof(PaymentStatus.Rejected)];
        var collected = Totals(new ReportTable("PaymentList", columns, [.. rows.Where(r => !uncollected.Contains((string)r[5]!))]));
        return new([new ReportTable("PaymentList", columns, rows) { Totals = collected.Totals }], []);
    }

    /// <summary>Días hacia adelante de la lista de dominios por vencer.</summary>
    public const int DomainWindowDays = 90;

    /// <summary>
    /// Dominios al día de hoy: cuántos hay por estado y cuántos se renuevan solos, los vivos (pendientes, activos y en
    /// gracia) por registrador y TLD con los que vencen en 30 y en 90 días y lo que facturan por año los activos, y la
    /// lista de los activos o en gracia que vencen en los próximos <see cref="DomainWindowDays"/> días o ya vencieron.
    /// Lee las tablas del módulo de dominios. Mientras no estén, sale vacío con su aviso.
    /// </summary>
    public async Task<Report> Domains(CancellationToken ct)
    {
        if (!await Installed(ct, "domains.domains").ConfigureAwait(false))
        {
            return new([], [new("DomainsNotInstalled", [])]);
        }

        var statuses = await Query(
            """
            SELECT 'ReportDomainStatus.' || status, count(*)::int, count(*) FILTER (WHERE auto_renew)::int
            FROM domains.domains
            GROUP BY status
            ORDER BY array_position(ARRAY['Pending', 'Active', 'Grace', 'Expired', 'Cancelled'], status::text), 1
            """,
            Row,
            ct).ConfigureAwait(false);
        var registrars = await Query(
            """
            SELECT i.name, substring(d.name FROM position('.' IN d.name) + 1), d.currency, count(*)::int,
                   count(*) FILTER (WHERE d.expires_on >= @today AND d.expires_on < @today + 30)::int,
                   count(*) FILTER (WHERE d.expires_on >= @today AND d.expires_on < @today + 90)::int,
                   coalesce(sum(round(d.recurring_amount / greatest(d.years, 1), 2)) FILTER (WHERE d.status = 'Active'), 0)
            FROM domains.domains d JOIN platform.module_instances i ON i.id = d.registrar_instance_id
            WHERE d.status IN ('Pending', 'Active', 'Grace')
            GROUP BY 1, 2, 3
            ORDER BY 1, 4 DESC, 2, 3
            """,
            Row,
            ct,
            ("today", Today)).ConfigureAwait(false);
        var expiring = await Query(
            """
            SELECT d.name, d.account_id, coalesce(nullif(a.company_name, ''), a.name), 'ReportDomainStatus.' || d.status, d.expires_on, d.expires_on - @today,
                   CASE WHEN d.auto_renew THEN 'Reports.Yes' ELSE 'Reports.No' END, d.paid_through, i.name, d.currency, d.recurring_amount
            FROM domains.domains d
            JOIN accounts.accounts a ON a.id = d.account_id
            JOIN platform.module_instances i ON i.id = d.registrar_instance_id
            WHERE d.status IN ('Active', 'Grace') AND d.expires_on < @today + @window
            ORDER BY d.expires_on, d.name
            """,
            Row,
            ct,
            ("today", Today),
            ("window", DomainWindowDays)).ConfigureAwait(false);
        return new(
            [
                Totals(new ReportTable("DomainsByStatus", [new("Status", CellKind.Key), new("Domains", CellKind.Count), new("AutoRenew", CellKind.Count)], statuses)),
                Totals(new ReportTable(
                    "DomainsByRegistrar",
                    [
                        new("Registrar", CellKind.Text), new("Tld", CellKind.Text), new("Currency", CellKind.Text), new("Domains", CellKind.Count),
                        new("Expiring30", CellKind.Count), new("Expiring90", CellKind.Count), new("RecurringYearly", CellKind.Money),
                    ],
                    registrars)),
                new ReportTable(
                    "DomainsExpiring",
                    [
                        new("Domain", CellKind.Text), new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Status", CellKind.Key),
                        new("ExpiresOn", CellKind.Date), new("Days", CellKind.Count), new("AutoRenew", CellKind.Key), new("PaidThrough", CellKind.Date),
                        new("Registrar", CellKind.Text), new("Currency", CellKind.Text), new("Amount", CellKind.Money),
                    ],
                    expiring),
            ],
            [])
        {
            Charts = [new("DomainsByRegistrar", "DomainsByRegistrar", "Registrar", ["Domains"]), new("DomainsByTld", "DomainsByRegistrar", "Tld", ["Domains"])],
        };
    }

    /// <summary>
    /// Avisos de vencimiento de dominios del período: los que avisan que un dominio vence pronto y los que avisan que ya
    /// venció, por mes y en qué quedó cada uno en la cola de correo, y el detalle con el destinatario y el asunto, que nombra
    /// el dominio. Salen de la cola de correo con las plantillas del módulo de dominios.
    /// </summary>
    public async Task<Report> DomainNotices(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        const string notices = """
            WITH n AS (
              SELECT e.*, CASE e.template WHEN 'domain-expiring' THEN 'ReportDomainNotice.Expiring' ELSE 'ReportDomainNotice.Expired' END AS notice
              FROM notifications.outbound_emails e
              WHERE e.template IN ('domain-expiring', 'domain-expired')
                AND e.created_at >= @from::timestamp AT TIME ZONE @tz AND e.created_at < @end::timestamp AT TIME ZONE @tz
            )
            """;
        var months = await Query(
            $"""
            {notices}
            SELECT date_trunc('month', created_at AT TIME ZONE @tz)::date, notice, count(*)::int, count(*) FILTER (WHERE status = 'Sent')::int,
                   count(*) FILTER (WHERE status = 'Pending')::int, count(*) FILTER (WHERE status = 'Suppressed')::int, count(*) FILTER (WHERE status = 'Failed')::int
            FROM n
            GROUP BY 1, 2
            ORDER BY 1, 2 DESC
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var detail = await Query(
            $"""
            {notices}
            SELECT (n.created_at AT TIME ZONE @tz)::date, n.account_id, coalesce(nullif(a.company_name, ''), a.name), n.notice, n.recipient, n.subject, 'ReportEmailStatus.' || n.status
            FROM n LEFT JOIN accounts.accounts a ON a.id = n.account_id
            ORDER BY n.created_at, n.id
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        return new(
            [
                Totals(new ReportTable(
                    "DomainNoticesByMonth",
                    [
                        new("Month", CellKind.Month), new("Notice", CellKind.Key), new("Notices", CellKind.Count), new("Sent", CellKind.Count),
                        new("Queued", CellKind.Count), new("Suppressed", CellKind.Count), new("Failed", CellKind.Count),
                    ],
                    months)),
                new ReportTable(
                    "DomainNotices",
                    [
                        new("Date", CellKind.Date), new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Notice", CellKind.Key),
                        new("Recipient", CellKind.Text), new("Subject", CellKind.Text), new("Status", CellKind.Key),
                    ],
                    detail),
            ],
            [])
        {
            Charts = [new("DomainNotices", "DomainNoticesByMonth", "Month", ["Notices"])],
        };
    }

    /// <summary>
    /// Los PDF guardados de las facturas y notas emitidas en el mes, por tipo de documento: cuántos hay, cuántos tienen
    /// su PDF y cuántos todavía no (el PDF se arma cuando el documento tiene su comprobante fiscal). Lee la tabla de los
    /// PDF del núcleo. Mientras no esté, sale vacío con su aviso.
    /// </summary>
    public async Task<Report> InvoicePdfs(DateOnly month, CancellationToken ct)
    {
        if (!await Installed(ct, "billing.document_pdfs").ConfigureAwait(false))
        {
            return new([], [new("PdfNotInstalled", [])]);
        }

        var from = new DateOnly(month.Year, month.Month, 1);
        var rows = await Query(
            """
            SELECT 'DocumentKind.' || d.kind, count(*)::int, count(p.document_id)::int, (count(*) - count(p.document_id))::int
            FROM billing.documents d LEFT JOIN billing.document_pdfs p ON p.document_id = d.id
            WHERE d.issue_date >= @from AND d.issue_date < @end
            GROUP BY d.kind
            ORDER BY array_position(ARRAY['Invoice', 'DebitNote', 'CreditNote'], d.kind::text)
            """,
            Row,
            ct,
            ("from", from),
            ("end", from.AddMonths(1))).ConfigureAwait(false);
        var missing = rows.Sum(r => (int)r[3]!);
        return new(
            [Totals(new ReportTable("InvoicePdfs", [new("Document", CellKind.Key), new("Documents", CellKind.Count), new("WithPdf", CellKind.Count), new("WithoutPdf", CellKind.Count)], rows))],
            missing > 0 ? [new("PdfMissing", [missing.ToString(CultureInfo.InvariantCulture)])] : []);
    }

    /// <summary>
    /// Un ZIP con los PDF guardados de los documentos emitidos en el mes, en el orden de su fecha y su número, o nulo si
    /// la tabla de los PDF todavía no está. Dos documentos con el mismo nombre de archivo van con su número adelante.
    /// </summary>
    public async Task<byte[]?> InvoicePdfZip(DateOnly month, CancellationToken ct)
    {
        if (!await Installed(ct, "billing.document_pdfs").ConfigureAwait(false))
        {
            return null;
        }

        var from = new DateOnly(month.Year, month.Month, 1);
        var files = await Query(
            """
            SELECT d.number, p.file_name, p.content
            FROM billing.document_pdfs p JOIN billing.documents d ON d.id = p.document_id
            WHERE d.issue_date >= @from AND d.issue_date < @end
            ORDER BY d.issue_date, d.number, d.id
            """,
            r => (Number: r.GetInt64(0), Name: r.GetString(1), Content: r.GetFieldValue<byte[]>(2)),
            ct,
            ("from", from),
            ("end", from.AddMonths(1))).ConfigureAwait(false);

        // ponytail: el ZIP del mes se arma en memoria. Con miles de PDF por mes, escribirlo directo a la respuesta.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (number, name, content) in files)
            {
                var entry = zip.CreateEntry(names.Add(name) ? name : string.Create(CultureInfo.InvariantCulture, $"{number}-{name}"), CompressionLevel.Fastest);
                await using var stream = entry.Open();
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Respuestas del staff en el período, por persona: los mensajes que ve el cliente, en cuántos tickets y las notas
    /// internas. La persona es la del staff que escribió, y un mensaje importado sin ella va con el nombre que firmó. Lee
    /// las tablas del módulo de soporte. Mientras no estén, sale vacío con su aviso.
    /// </summary>
    public async Task<Report> StaffReplies(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!await SupportInstalled(ct).ConfigureAwait(false) || !await HasColumn("support", "messages", "staff_id", ct).ConfigureAwait(false))
        {
            return new([], [new("SupportNotInstalled", [])]);
        }

        var rows = await Query(
            """
            WITH m AS (
              SELECT m.ticket_id, m.internal, coalesce(u.name, m.author_name) AS who, coalesce(m.staff_id::text, 'name:' || m.author_name) AS who_key
              FROM support.messages m LEFT JOIN identity.staff_users u ON u.id = m.staff_id
              WHERE m.author = 'Staff' AND m.created_at >= @from::timestamp AT TIME ZONE @tz AND m.created_at < @end::timestamp AT TIME ZONE @tz
            )
            SELECT max(who), count(*) FILTER (WHERE NOT internal)::int, (count(DISTINCT ticket_id) FILTER (WHERE NOT internal))::int, count(*) FILTER (WHERE internal)::int
            FROM m
            GROUP BY who_key
            ORDER BY 2 DESC, 1
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] columns = [new("Staff", CellKind.Text), new("Replies", CellKind.Count), new("TicketsAnswered", CellKind.Count), new("InternalNotes", CellKind.Count)];
        return new([Totals(new ReportTable("StaffReplies", columns, rows), "TicketsAnswered")], []) { Charts = [new("StaffReplies", "StaffReplies", "Staff", ["Replies"])] };
    }

    /// <summary>
    /// Satisfacción de los clientes en el período, de la encuesta de cada ticket (del 0 al 10): por persona del staff que
    /// atendía el ticket al cerrarse, cuántas respuestas, el promedio, cuántas bajas (0 a 6), medias (7 y 8) y altas (9 y
    /// 10) y el índice neto, que es la parte de altas menos la de bajas. El pie lleva el promedio y el índice de todas. Más
    /// los comentarios con su ticket. Lee la tabla de la encuesta del módulo de soporte. Mientras no esté, sale vacío con
    /// su aviso.
    /// </summary>
    public async Task<Report> Satisfaction(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!await Installed(ct, "support.feedback", "support.tickets").ConfigureAwait(false))
        {
            return new([], [new("SupportNotInstalled", [])]);
        }

        const string answers = """
            WITH f AS (
              SELECT f.*, u.name AS staff
              FROM support.feedback f LEFT JOIN identity.staff_users u ON u.id = f.staff_id
              WHERE f.created_at >= @from::timestamp AT TIME ZONE @tz AND f.created_at < @end::timestamp AT TIME ZONE @tz
            )
            """;
        var staff = await Grouped(
            "SatisfactionByStaff",
            [
                new("Staff", CellKind.Text, Empty: "ReportCol.Unassigned"), new("Ratings", CellKind.Count), new("Average", CellKind.Decimal),
                new("LowRatings", CellKind.Count), new("MidRatings", CellKind.Count), new("HighRatings", CellKind.Count), new("NetScore", CellKind.Percent),
            ],
            $"""
            {answers}
            SELECT grouping(staff_id)::int, CASE WHEN grouping(staff_id) = 1 THEN NULL ELSE max(staff) END, count(*)::int, round(avg(rating), 1),
                   count(*) FILTER (WHERE rating <= 6)::int, count(*) FILTER (WHERE rating BETWEEN 7 AND 8)::int, count(*) FILTER (WHERE rating >= 9)::int,
                   round((count(*) FILTER (WHERE rating >= 9) - count(*) FILTER (WHERE rating <= 6)) * 100.0 / nullif(count(*), 0), 0)
            FROM f
            GROUP BY GROUPING SETS ((staff_id), ())
            ORDER BY 1, 3 DESC, 2
            """,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var comments = await Query(
            $"""
            {answers}
            SELECT (f.created_at AT TIME ZONE @tz)::date, t.number, f.staff, f.rating, f.comment
            FROM f JOIN support.tickets t ON t.id = f.ticket_id
            WHERE coalesce(f.comment, '') <> ''
            ORDER BY f.created_at DESC, f.id DESC
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        return new(
            [
                staff,
                new ReportTable(
                    "SatisfactionComments",
                    [new("Date", CellKind.Date), new("Ticket", CellKind.Text), new("Staff", CellKind.Text, Empty: "ReportCol.Unassigned"), new("Rating", CellKind.Count), new("Comment", CellKind.Text)],
                    comments),
            ],
            [])
        {
            Charts = [new("Satisfaction", "SatisfactionByStaff", null, ["LowRatings", "MidRatings", "HighRatings"]), new("SatisfactionAverage", "SatisfactionByStaff", "Staff", ["Average"])],
        };
    }

    /// <summary>
    /// Uso de los cupones: cada cupón con su tipo y valor, los usos que lleva y su tope, los pedidos y las cuentas que lo
    /// usaron en el período y los servicios activos o suspendidos que lo tienen hoy. De mayor a menor uso en el período.
    /// Lee los cupones del catálogo. Mientras no estén, sale vacío con su aviso.
    /// </summary>
    public async Task<Report> Coupons(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!await Installed(ct, "catalog.coupons").ConfigureAwait(false) || !await HasColumn("orders", "orders", "coupon_id", ct).ConfigureAwait(false)
            || !await HasColumn("services", "services", "coupon_id", ct).ConfigureAwait(false))
        {
            return new([], [new("CouponsNotInstalled", [])]);
        }

        var rows = await Query(
            """
            SELECT c.code, 'ReportCouponKind.' || c.kind, c.value, c.currency, c.uses, c.max_uses,
                   count(DISTINCT o.id)::int, count(DISTINCT o.account_id)::int,
                   (SELECT count(*) FROM services.services s WHERE s.coupon_id = c.id AND s.status IN ('Active', 'Suspended'))::int
            FROM catalog.coupons c
            LEFT JOIN orders.orders o ON o.coupon_id = c.id
                 AND o.placed_at >= @from::timestamp AT TIME ZONE @tz AND o.placed_at < @end::timestamp AT TIME ZONE @tz
            GROUP BY c.id
            ORDER BY 7 DESC, 9 DESC, c.code
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] columns =
        [
            new("Code", CellKind.Text), new("Kind", CellKind.Key), new("Value", CellKind.Money), new("CouponCurrency", CellKind.Text), new("Uses", CellKind.Count),
            new("MaxUses", CellKind.Count), new("Orders", CellKind.Count), new("Accounts", CellKind.Count), new("LiveServices", CellKind.Count),
        ];
        return new([new ReportTable("Coupons", columns, rows)], []) { Charts = [new("Coupons", "Coupons", "Code", ["Orders"])] };
    }

    /// <summary>
    /// Afiliados en el período: por afiliado, su código y estado, las visitas que trajo y las cuentas que se registraron
    /// por él, y por moneda las comisiones generadas en el período (sin lo revertido), las que esperan su plazo hoy y lo
    /// que se le pagó en el período. Lee las tablas del módulo de afiliados. Mientras no estén, sale vacío con su aviso.
    /// </summary>
    public async Task<Report> Affiliates(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!await Installed(ct, "affiliates.affiliates", "affiliates.clicks", "affiliates.referrals", "affiliates.commissions", "affiliates.payouts").ConfigureAwait(false))
        {
            return new([], [new("AffiliatesNotInstalled", [])]);
        }

        // Dentro del período, con los días del proveedor. Columnas fijas de acá: nada viene del pedido.
        static string In(string column) => $"{column} >= @from::timestamp AT TIME ZONE @tz AND {column} < @end::timestamp AT TIME ZONE @tz";
        var activity = await Query(
            $"""
            SELECT f.account_id, coalesce(nullif(a.company_name, ''), a.name), f.code, 'ReportAffiliateStatus.' || f.status,
                   (SELECT count(*) FROM affiliates.clicks k WHERE k.affiliate_id = f.id AND {In("k.at")})::int,
                   (SELECT count(*) FROM affiliates.referrals r WHERE r.affiliate_id = f.id AND {In("r.created_at")})::int
            FROM affiliates.affiliates f JOIN accounts.accounts a ON a.id = f.account_id
            ORDER BY 6 DESC, 5 DESC, f.code
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        var commissions = await Query(
            $"""
            WITH c AS (
              SELECT affiliate_id, currency, count(*) FILTER (WHERE {In("created_at")}) AS n,
                     coalesce(sum(amount - reversed) FILTER (WHERE {In("created_at")}), 0) AS earned,
                     coalesce(sum(amount - reversed) FILTER (WHERE status = 'Held'), 0) AS held
              FROM affiliates.commissions GROUP BY 1, 2
            ), p AS (
              SELECT affiliate_id, currency, sum(amount) AS paid FROM affiliates.payouts WHERE status = 'Paid' AND {In("decided_at")} GROUP BY 1, 2
            )
            SELECT f.account_id, coalesce(nullif(a.company_name, ''), a.name), coalesce(c.currency, p.currency), coalesce(c.n, 0)::int,
                   coalesce(c.earned, 0), coalesce(c.held, 0), coalesce(p.paid, 0)
            FROM c FULL JOIN p ON p.affiliate_id = c.affiliate_id AND p.currency = c.currency
            JOIN affiliates.affiliates f ON f.id = coalesce(c.affiliate_id, p.affiliate_id)
            JOIN accounts.accounts a ON a.id = f.account_id
            WHERE coalesce(c.n, 0) > 0 OR coalesce(c.held, 0) <> 0 OR coalesce(p.paid, 0) <> 0
            ORDER BY 3, 5 DESC, 1
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        return new(
            [
                Totals(new ReportTable(
                    "AffiliateActivity",
                    [new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Code", CellKind.Text), new("Status", CellKind.Key), new("Clicks", CellKind.Count), new("Referrals", CellKind.Count)],
                    activity)),
                Totals(new ReportTable(
                    "AffiliateCommissions",
                    [
                        new("Account", CellKind.Account), new("AccountName", CellKind.Text), new("Currency", CellKind.Text), new("Commissions", CellKind.Count),
                        new("Earned", CellKind.Money), new("Held", CellKind.Money), new("PaidOut", CellKind.Money),
                    ],
                    commissions)),
            ],
            [])
        {
            Charts = [new("AffiliateReferrals", "AffiliateActivity", "Code", ["Referrals"]), new("AffiliateEarned", "AffiliateCommissions", "AccountName", ["Earned"])],
        };
    }

    /// <summary>Si la columna está: una tabla de otro módulo puede llegar antes que la versión que el reporte lee.</summary>
    private async Task<bool> HasColumn(string schema, string table, string column, CancellationToken ct) => (await Query(
        "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table AND column_name = @column)",
        r => r.GetBoolean(0),
        ct,
        ("schema", schema),
        ("table", table),
        ("column", column)).ConfigureAwait(false))[0];

    /// <summary>Si están las tablas de otro módulo que el reporte lee (docs/reportes): hasta que llegue a esta rama, no.</summary>
    private async Task<bool> Installed(CancellationToken ct, params string[] tables) => (await Query(
        "SELECT " + string.Join(" AND ", tables.Select((_, i) => string.Create(CultureInfo.InvariantCulture, $"to_regclass(@t{i}) IS NOT NULL"))),
        r => r.GetBoolean(0),
        ct,
        [.. tables.Select((t, i) => (string.Create(CultureInfo.InvariantCulture, $"t{i}"), (object?)t))]).ConfigureAwait(false))[0];

    /// <summary>
    /// Ingreso recurrente mensual y bajas. Un servicio recurrente cuenta desde que se activó hasta que se dio de baja, con
    /// su precio actual pasado a un mes (un anual cuenta la doceava parte), con descuento y sin separar impuestos: con
    /// precios con IVA incluido, el ingreso recurrente lo incluye. Por mes y moneda: al cierre, altas, bajas y la tasa de
    /// bajas sobre lo que había al empezar el mes. Arriba, lo de hoy de los servicios activos y de los suspendidos.
    /// Cuentan los que están activos o suspendidos y los dados de baja con fecha: un cancelado nunca estuvo activo, y un
    /// dado de baja sin fecha (algunos importados) no se puede ubicar en el tiempo y sale en una nota.
    /// </summary>
    public async Task<Report> Recurring(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        const string services = $"""
            SELECT s.currency, s.status,
                   coalesce(s.activated_at, s.created_at) AS since,
                   CASE WHEN s.status = 'Terminated' THEN greatest(s.terminated_at, coalesce(s.activated_at, s.created_at)) END AS until,
                   {Monthly} AS monthly
            FROM services.services s
            WHERE s.cycle_unit IN ('Month', 'Year')
              AND (s.status IN ('Active', 'Suspended') OR (s.status = 'Terminated' AND s.terminated_at IS NOT NULL))
            """;
        var now = await Query(
            $"""
            SELECT currency,
                   count(*) FILTER (WHERE status = 'Active')::int, coalesce(sum(monthly) FILTER (WHERE status = 'Active'), 0),
                   count(*) FILTER (WHERE status = 'Suspended')::int, coalesce(sum(monthly) FILTER (WHERE status = 'Suspended'), 0)
            FROM ({services}) v
            WHERE status IN ('Active', 'Suspended')
            GROUP BY 1 ORDER BY 1
            """,
            r => new object?[] { r.GetString(0), r.GetInt32(1), r.GetDecimal(2), r.GetInt32(3), r.GetDecimal(4) },
            ct).ConfigureAwait(false);
        var undated = (await Query(
            """
            SELECT count(*)::int FROM services.services
            WHERE cycle_unit IN ('Month', 'Year') AND status = 'Terminated' AND terminated_at IS NULL
            """,
            r => r.GetInt32(0),
            ct).ConfigureAwait(false))[0];

        var months = await Query(
            $"""
            WITH months AS (
              SELECT m::date AS month, m::timestamp AT TIME ZONE @tz AS starts, (m + interval '1 month')::timestamp AT TIME ZONE @tz AS ends
              FROM generate_series(@from::timestamp, @to::timestamp, interval '1 month') m
            ), v AS ({services})
            SELECT m.month, v.currency,
                   coalesce(sum(v.monthly) FILTER (WHERE v.since < m.starts AND (v.until IS NULL OR v.until >= m.starts)), 0),
                   count(*) FILTER (WHERE v.since >= m.starts AND v.since < m.ends)::int,
                   coalesce(sum(v.monthly) FILTER (WHERE v.since >= m.starts AND v.since < m.ends), 0),
                   count(*) FILTER (WHERE v.until >= m.starts AND v.until < m.ends)::int,
                   coalesce(sum(v.monthly) FILTER (WHERE v.until >= m.starts AND v.until < m.ends), 0),
                   count(*) FILTER (WHERE v.since < m.ends AND (v.until IS NULL OR v.until >= m.ends))::int,
                   coalesce(sum(v.monthly) FILTER (WHERE v.since < m.ends AND (v.until IS NULL OR v.until >= m.ends)), 0)
            FROM months m CROSS JOIN v
            GROUP BY 1, 2
            ORDER BY 1, 2
            """,
            r =>
            {
                var start = r.GetDecimal(2);
                var churned = r.GetDecimal(6);
                return new object?[]
                {
                    r.GetFieldValue<DateOnly>(0), r.GetString(1), start, r.GetInt32(3), r.GetDecimal(4), r.GetInt32(5), churned,
                    start == 0m ? null : Math.Round(churned * 100m / start, 2, MidpointRounding.AwayFromZero), r.GetInt32(7), r.GetDecimal(8),
                };
            },
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("to", range.To)).ConfigureAwait(false);

        return new(
            [
                new ReportTable("RecurringNow", [new("Currency", CellKind.Text), new("ActiveServices", CellKind.Count), new("Mrr", CellKind.Money), new("SuspendedServices", CellKind.Count), new("SuspendedMrr", CellKind.Money)], now),
                new ReportTable(
                    "RecurringMonths",
                    [
                        new("Month", CellKind.Month), new("Currency", CellKind.Text), new("MrrStart", CellKind.Money), new("NewServices", CellKind.Count),
                        new("NewMrr", CellKind.Money), new("ChurnedServices", CellKind.Count), new("ChurnedMrr", CellKind.Money), new("ChurnRate", CellKind.Percent),
                        new("ActiveServices", CellKind.Count), new("Mrr", CellKind.Money),
                    ],
                    months),
            ],
            undated > 0 ? [new("UndatedTerminations", [undated.ToString(CultureInfo.InvariantCulture)])] : [])
        {
            Charts = [new("Recurring", "RecurringMonths", "Month", ["Mrr"])],
        };
    }

    /// <summary>
    /// Cobros por medio: lo cobrado por mes, moneda y canal (cada instancia de pasarela con su nombre, las cargas
    /// manuales y lo importado), con las comisiones, lo que volvió al pagador y lo que queda. Cuenta todo pago que se
    /// cobró alguna vez, aunque después se haya devuelto. Los pendientes y rechazados no.
    /// </summary>
    public async Task<Report> Collections(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rows = await Query(
            """
            SELECT date_trunc('month', p.created_at AT TIME ZONE @tz)::date, p.currency, p.channel, coalesce(i.name, ''), count(*)::int,
                   sum(p.amount), sum(p.fee), sum(p.refunded), sum(p.amount - p.fee - p.refunded)
            FROM payments.payments p
            LEFT JOIN platform.module_instances i ON p.channel = 'instance:' || i.id
            WHERE p.status NOT IN ('Pending', 'Rejected')
              AND p.created_at >= @from::timestamp AT TIME ZONE @tz AND p.created_at < @end::timestamp AT TIME ZONE @tz
            GROUP BY 1, 2, 3, 4
            ORDER BY 1, 2, 3
            """,
            r => new object?[] { r.GetFieldValue<DateOnly>(0), r.GetString(1), Channel(r.GetString(2), r.GetString(3)), r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8) },
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] columns = [new("Month", CellKind.Month), new("Currency", CellKind.Text), new("Channel", CellKind.Text), new("Payments", CellKind.Count), new("Collected", CellKind.Money), new("Fees", CellKind.Money), new("Refunded", CellKind.Money), new("NetCollected", CellKind.Money)];
        return new([Totals(new ReportTable("Collections", columns, rows))], []) { Charts = [new("Collections", "Collections", "Channel", ["NetCollected"])] };
    }

    /// <summary>
    /// Clientes y servicios al día de hoy: las cuentas por estado con cuántas tienen algún servicio activo o suspendido, y
    /// los servicios vivos por producto y por servidor (la instancia de módulo que los aprovisiona), con las cuentas que
    /// los tienen y el ingreso recurrente mensual de los activos, igual que en el reporte de recurrente. Los totales son
    /// por moneda y cuentan cada cuenta una vez, aunque tenga varios productos.
    /// </summary>
    public async Task<Report> Services(CancellationToken ct)
    {
        var accounts = await Query(
            """
            SELECT a.status, count(*)::int,
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM services.services s WHERE s.account_id = a.id AND s.status IN ('Active', 'Suspended')))::int
            FROM accounts.accounts a
            GROUP BY 1
            ORDER BY array_position(ARRAY['Active', 'Inactive', 'Closed'], a.status::text), 1
            """,
            r => new object?[] { "AccountStatus." + r.GetString(0), r.GetInt32(1), r.GetInt32(2) },
            ct).ConfigureAwait(false);

        const string live = $"""
            WITH s AS (
              SELECT s.account_id, s.product_id, s.module_instance_id, s.currency, s.status,
                     CASE WHEN s.status = 'Active' AND s.cycle_unit IN ('Month', 'Year') THEN {Monthly} ELSE 0 END AS monthly
              FROM services.services s
              WHERE s.status IN ('Pending', 'Provisioning', 'Active', 'Failed', 'Suspended')
            )
            """;
        const string counts = """
            count(DISTINCT s.account_id) FILTER (WHERE s.status IN ('Active', 'Suspended'))::int,
            count(*) FILTER (WHERE s.status = 'Active')::int,
            count(*) FILTER (WHERE s.status = 'Suspended')::int,
            count(*) FILTER (WHERE s.status IN ('Pending', 'Provisioning', 'Failed'))::int,
            sum(s.monthly)
            """;
        ReportColumn[] figures = [new("Currency", CellKind.Text), new("Accounts", CellKind.Count), new("Active", CellKind.Count), new("Suspended", CellKind.Count), new("Pending", CellKind.Count), new("Mrr", CellKind.Money)];
        var products = await Grouped(
            "ServicesByProduct",
            [new("Group", CellKind.Text), new("Product", CellKind.Text), .. figures],
            $"""
            {live}
            SELECT grouping(p.id)::int, pg.name, p.name, s.currency, {counts}
            FROM s JOIN catalog.products p ON p.id = s.product_id
            LEFT JOIN catalog.product_groups pg ON pg.id = p.group_id
            GROUP BY GROUPING SETS ((p.id, pg.name, p.name, s.currency), (s.currency))
            ORDER BY 1, pg.name NULLS FIRST, p.name, p.id, s.currency
            """,
            ct).ConfigureAwait(false);
        var servers = await Grouped(
            "ServicesByServer",
            [new("Server", CellKind.Text, Empty: "ReportCol.NoServer"), new("Module", CellKind.Text), .. figures],
            $"""
            {live}
            SELECT grouping(i.id)::int, i.name, m.name, s.currency, {counts}
            FROM s
            LEFT JOIN platform.module_instances i ON i.id = s.module_instance_id
            LEFT JOIN platform.module_installations m ON m.id = i.installation_id
            GROUP BY GROUPING SETS ((i.id, i.name, m.name, s.currency), (s.currency))
            ORDER BY 1, i.id IS NULL, i.name, i.id, s.currency
            """,
            ct).ConfigureAwait(false);

        return new(
            [new ReportTable("AccountsByStatus", [new("Status", CellKind.Key), new("Accounts", CellKind.Count), new("WithServices", CellKind.Count)], accounts), products, servers],
            [])
        {
            Charts = [new("ServicesByProduct", "ServicesByProduct", "Product", ["Active"]), new("ServicesByServer", "ServicesByServer", "Server", ["Active"])],
        };
    }

    /// <summary>
    /// Altas de clientes por mes: las cuentas creadas en cada mes, cuántas contrataron algo (un servicio que llegó a estar
    /// activo, aunque después se haya dado de baja) y cuántas tienen hoy alguno activo o suspendido. Una cuenta importada
    /// cuenta en el mes en que se creó en el sistema anterior.
    /// </summary>
    public async Task<Report> NewAccounts(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rows = await Query(
            """
            SELECT date_trunc('month', a.created_at AT TIME ZONE @tz)::date, count(*)::int,
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM services.services s WHERE s.account_id = a.id AND s.status IN ('Active', 'Suspended', 'Terminated')))::int,
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM services.services s WHERE s.account_id = a.id AND s.status IN ('Active', 'Suspended')))::int
            FROM accounts.accounts a
            WHERE a.created_at >= @from::timestamp AT TIME ZONE @tz AND a.created_at < @end::timestamp AT TIME ZONE @tz
            GROUP BY 1
            ORDER BY 1
            """,
            r => new object?[] { r.GetFieldValue<DateOnly>(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3) },
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);
        ReportColumn[] columns = [new("Month", CellKind.Month), new("NewAccounts", CellKind.Count), new("Contracted", CellKind.Count), new("WithServicesToday", CellKind.Count)];
        return new([Totals(new ReportTable("NewAccounts", columns, rows))], []) { Charts = [new("NewAccounts", "NewAccounts", "Month", ["NewAccounts"])] };
    }

    /// <summary>
    /// Tickets por departamento y por mes, de los abiertos en el período: cuántos se abrieron, cuántos tuvieron respuesta
    /// del staff, cuántos siguen sin respuesta y cuántos están cerrados, con la primera respuesta (mediana y promedio) y
    /// la resolución (mediana) en horas corridas. La primera respuesta es el primer mensaje del staff que ve el cliente,
    /// en los tickets que abrió el cliente: uno que abre el staff no espera respuesta y no cuenta en esas columnas, y
    /// una nota interna no es una respuesta. Uno unido a otro no cuenta: sus mensajes pasaron al que lo recibió. Lee las
    /// tablas del módulo de soporte (docs/reportes). Si todavía no están, sale vacío con su nota.
    /// </summary>
    public async Task<Report> Support(MonthRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ReportColumn[] figures =
        [
            new("Opened", CellKind.Count), new("Answered", CellKind.Count), new("Unanswered", CellKind.Count), new("Closed", CellKind.Count),
            new("FirstResponseMedian", CellKind.Hours), new("FirstResponseAverage", CellKind.Hours), new("ResolutionMedian", CellKind.Hours),
        ];
        var installed = await SupportInstalled(ct).ConfigureAwait(false);
        var rows = !installed ? [] : await Query(
            """
            WITH t AS (
              SELECT t.department_id, t.status, t.created_at, t.closed_at,
                     date_trunc('month', t.created_at AT TIME ZONE @tz)::date AS month,
                     (SELECT m.author FROM support.messages m WHERE m.ticket_id = t.id ORDER BY m.created_at, m.id LIMIT 1) = 'Customer' AS from_customer,
                     (SELECT min(m.created_at) FROM support.messages m WHERE m.ticket_id = t.id AND m.author = 'Staff' AND NOT m.internal) AS answered_at
              FROM support.tickets t
              WHERE t.merged_into_id IS NULL
                AND t.created_at >= @from::timestamp AT TIME ZONE @tz AND t.created_at < @end::timestamp AT TIME ZONE @tz
            )
            SELECT grouping(d.id, t.month)::int, d.name, t.month,
                   count(*)::int,
                   count(*) FILTER (WHERE t.from_customer AND t.answered_at IS NOT NULL)::int,
                   count(*) FILTER (WHERE t.from_customer AND t.answered_at IS NULL AND t.status <> 'Closed')::int,
                   count(*) FILTER (WHERE t.status = 'Closed')::int,
                   round(extract(epoch FROM percentile_cont(0.5) WITHIN GROUP (ORDER BY t.answered_at - t.created_at) FILTER (WHERE t.from_customer)) / 3600, 1),
                   round(extract(epoch FROM avg(t.answered_at - t.created_at) FILTER (WHERE t.from_customer)) / 3600, 1),
                   round(extract(epoch FROM percentile_cont(0.5) WITHIN GROUP (ORDER BY t.closed_at - t.created_at) FILTER (WHERE t.status = 'Closed')) / 3600, 1)
            FROM t JOIN support.departments d ON d.id = t.department_id
            GROUP BY GROUPING SETS ((d.id, d.name, d.sort_order), (t.month), ())
            ORDER BY 1, d.sort_order, d.name, d.id, t.month
            """,
            Row,
            ct,
            ("tz", TimeZone),
            ("from", range.From),
            ("end", range.End)).ConfigureAwait(false);

        // grouping da 1 en las filas por departamento, 2 en las por mes y 3 en la del total.
        object?[] Cells(object?[] row, int key) => [row[key], .. row[3..]];
        var total = rows.Where(r => (int)r[0]! == 3).Select(r => Cells(r, 1)).ToList();
        return new(
            [
                new ReportTable("TicketsByDepartment", [new("Department", CellKind.Text), .. figures], [.. rows.Where(r => (int)r[0]! == 1).Select(r => Cells(r, 1))]) { Totals = total },
                new ReportTable("TicketsByMonth", [new("Month", CellKind.Month), .. figures], [.. rows.Where(r => (int)r[0]! == 2).Select(r => Cells(r, 2))]) { Totals = total },
            ],
            installed ? [] : [new("SupportNotInstalled", [])])
        {
            Charts =
            [
                new("TicketsByDepartment", "TicketsByDepartment", "Department", ["Opened"]),
                new("FirstResponse", "TicketsByDepartment", "Department", ["FirstResponseMedian"]),
                new("TicketsByMonth", "TicketsByMonth", "Month", ["Opened"]),
            ],
        };
    }

    /// <summary>
    /// Una tabla armada con GROUPING SETS: la primera columna de la consulta es grouping(), 0 en las filas y 1 en los
    /// totales por moneda, que van al pie.
    /// </summary>
    private async Task<ReportTable> Grouped(string id, IReadOnlyList<ReportColumn> columns, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        var rows = await Query(sql, Row, ct, args).ConfigureAwait(false);
        return new ReportTable(id, columns, [.. rows.Where(r => (int)r[0]! == 0).Select(r => r[1..])])
        {
            Totals = [.. rows.Where(r => (int)r[0]! == 1).Select(r => r[1..])],
        };
    }

    /// <summary>La fila tal cual la da la base: int, decimal, string o DateOnly según la columna, y null si está vacía.</summary>
    private static object?[] Row(DbDataReader r) =>
        [.. Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetDataTypeName(i) == "date" ? r.GetFieldValue<DateOnly>(i) : r.GetValue(i))];

    /// <summary>"Mercado Pago (instance:3)", "transferencia (manual)" o "mercadopago_1 (import)".</summary>
    private static string Channel(string channel, string instanceName)
    {
        var (kind, name) = channel.IndexOf(':', StringComparison.Ordinal) is > 0 and var i ? (channel[..i], channel[(i + 1)..]) : (string.Empty, channel);
        return kind switch
        {
            "instance" when instanceName.Length > 0 => $"{instanceName} ({channel})",
            "" => channel,
            _ => $"{name} ({kind})",
        };
    }

    /// <summary>
    /// Agrega los totales del período: por moneda si la tabla tiene la columna Currency, y uno solo si no. Suma las
    /// cantidades y los importes, salvo las columnas de <paramref name="skip"/>, que no se pueden sumar. El resto de las
    /// celdas queda vacío.
    /// </summary>
    private static ReportTable Totals(ReportTable table, params string[] skip)
    {
        var currency = table.Columns.ToList().FindIndex(c => c.Id == "Currency");
        var totals = table.Rows.GroupBy(r => currency < 0 ? null : (string?)r[currency]).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            table.Columns.Select((c, i) => i == currency ? g.Key : skip.Contains(c.Id) ? null : c.Kind switch
            {
                CellKind.Money => g.Sum(r => (decimal?)r[i] ?? 0m),
                CellKind.Count => (object?)g.Sum(r => Convert.ToInt64(r[i] ?? 0, CultureInfo.InvariantCulture)),
                _ => null,
            }).ToArray()).ToList();
        return table with { Totals = totals };
    }

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 20 ? string.Join(", ", list) : string.Join(", ", list.Take(20)) + string.Create(CultureInfo.InvariantCulture, $" (+{list.Count - 20})");
    }

    private async Task<List<T>> Query<T>(string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = await Sql.Command(db, sql, ct, args).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
