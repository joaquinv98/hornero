using System.Globalization;
using static BillingOss.Importer.Import.SourceImport;

namespace BillingOss.Importer.Import;

/// <summary>
/// Línea base sacada de un volcado del origen, con el formato de la que se sacó de la copia en el servidor (D-17).
/// Cuenta las filas tal como están en el origen, sin pasar por las reglas del importador: así la conciliación ve
/// lo que la importación perdió o contó distinto. Sirve para conciliar la importación de cada día del paralelo y
/// suma lo que la línea base de la copia no traía: pagos por tipo de movimiento, notas nativas, bajas pedidas sin
/// ejecutar y servicios vivos por mes de vencimiento y con el período del vencimiento ya facturado.
/// </summary>
public static class Baseline
{
    private static readonly HashSet<string> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        "tblcurrencies", "tblclients", "tblhosting", "tbldomains", "tblinvoices", "tblinvoiceitems", "tblaccounts",
        "tblbillingnotes", "tblcancelrequests", "mod_afip_invoices",
    };

    public static async Task Write(DumpReader dump, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dump);
        ArgumentNullException.ThrowIfNull(output);
        var rows = Tables.ToDictionary(t => t, _ => new List<DumpRow>(), StringComparer.OrdinalIgnoreCase);
        await foreach (var row in dump.ReadRowsAsync(ct).ConfigureAwait(false))
        {
            if (rows.TryGetValue(row.Table.Name, out var list))
            {
                list.Add(row);
            }
        }

        var codes = rows["tblcurrencies"].ToDictionary(r => Long(r, "id"), r => Str(r, "code")?.ToUpperInvariant() ?? string.Empty);
        var clientCurrency = rows["tblclients"].ToDictionary(r => Long(r, "id"), r => codes.GetValueOrDefault(Long(r, "currency"), string.Empty));
        string Of(DumpRow r) => clientCurrency.GetValueOrDefault(Long(r, "userid"), string.Empty);
        var lines = new List<string> { "# section\tkeys...\tvalues..." };
        void Add(string section, IEnumerable<(string Key, string Values)> groups) =>
            lines.AddRange(groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{section}\t{g.Key}\t{g.Values}"));

        lines.Add($"clients_total\t{rows["tblclients"].Count}");
        Add("clients_by_status", rows["tblclients"].GroupBy(r => Str(r, "status") ?? string.Empty).Select(g => (g.Key, N(g.Count()))));
        Add("services_by_status", rows["tblhosting"].GroupBy(r => Str(r, "domainstatus") ?? string.Empty).Select(g => (g.Key, $"{g.Count()}\t{Money(g.Sum(r => Dec(r, "amount")))}")));
        Add("domains_by_status", rows["tbldomains"].GroupBy(r => Str(r, "status") ?? string.Empty).Select(g => (g.Key, N(g.Count()))));
        Add("invoices_by_status_currency(count,subtotal,credit,tax,tax2,total)", rows["tblinvoices"]
            .GroupBy(r => $"{Str(r, "status")}\t{Of(r)}")
            .Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "subtotal")}\t{Sum(g, "credit")}\t{Sum(g, "tax")}\t{Sum(g, "tax2")}\t{Sum(g, "total")}")));

        // Los movimientos sin moneda (currency en 0) van en la de la cuenta, como en la línea base de la copia.
        var moves = rows["tblaccounts"];
        Add("payments_by_currency(count,amountin,fees,amountout)", moves
            .GroupBy(r => codes.GetValueOrDefault(Long(r, "currency")) ?? Of(r))
            .Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "amountin")}\t{Sum(g, "fees")}\t{Sum(g, "amountout")}")));
        Add("payments_by_gateway(count,amountin)", moves.GroupBy(r => Str(r, "gateway") ?? string.Empty).Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "amountin")}")));
        Add("payments_by_type(count,amountin)", moves.Where(r => Dec(r, "amountin") > 0m)
            .GroupBy(r => Blank(Str(r, "type")) ?? "(sin tipo)").Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "amountin")}")));
        Add("credit_balance_by_currency(clients_with_credit,total)", rows["tblclients"].Where(r => Dec(r, "credit") > 0m)
            .GroupBy(r => codes.GetValueOrDefault(Long(r, "currency"), string.Empty)).Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "credit")}")));
        // Las notas que abrió un movimiento de saldo van aparte, con el tipo del movimiento: no son documentos.
        var balanceNotes = BalanceNotes(moves, rows["mod_afip_invoices"]);
        Add("billing_notes_by_type_currency(count,total)", rows["tblbillingnotes"].Where(r => Str(r, "status") != "draft")
            .GroupBy(r => $"{Str(r, "note_type")}{(balanceNotes.TryGetValue(Long(r, "id"), out var move) ? $":{move}" : string.Empty)}\t{clientCurrency.GetValueOrDefault(Long(r, "client_id"), string.Empty)}")
            .Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "total")}")));
        Add("mod_afip_invoices_by_type(voucher_type,ws,moneda,doc_tipo,count,total,first,last)", rows["mod_afip_invoices"]
            .GroupBy(r => $"{Str(r, "voucher_type")}\t{Str(r, "ws_service")}\t{Str(r, "moneda")}\t{Str(r, "doc_tipo")}")
            .Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "monto_total")}\t{Day(g.Min(r => Date(r, "fecha_emision")))}\t{Day(g.Max(r => Date(r, "fecha_emision")))}")));

        // Servicios vivos: por mes de vencimiento, con su pedido de baja sin ejecutar (el último de cada uno) y con el
        // período que empieza en el vencimiento ya facturado sin cobrar (renglón de una factura impaga, en gestión de
        // cobro o anulada).
        var alive = rows["tblhosting"].Where(r => Str(r, "domainstatus") is "Active" or "Suspended").ToList();
        var aliveIds = alive.Select(r => Long(r, "id")).ToHashSet();
        Add("services_alive_by_due_month(count,amount)", alive
            .GroupBy(r => Date(r, "nextduedate")?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? "sin_vencimiento")
            .Select(g => (g.Key, $"{g.Count()}\t{Sum(g, "amount")}")));
        Add("cancel_requests_pending_by_type(count)", rows["tblcancelrequests"].Where(r => aliveIds.Contains(Long(r, "relid")))
            .GroupBy(r => Long(r, "relid")).Select(g => g.OrderByDescending(r => Str(r, "date"), StringComparer.Ordinal).ThenByDescending(r => Long(r, "id")).First())
            .GroupBy(r => Str(r, "type") ?? string.Empty).Select(g => (g.Key, N(g.Count()))));
        var open = rows["tblinvoices"].Where(r => Str(r, "status") is "Unpaid" or "Collections" or "Cancelled").Select(r => Long(r, "id")).ToHashSet();
        var billed = rows["tblinvoiceitems"].Where(i => Str(i, "type") == "Hosting" && open.Contains(Long(i, "invoiceid")) && Date(i, "duedate") is not null)
            .Select(i => (Long(i, "relid"), Date(i, "duedate")!.Value)).ToHashSet();
        Add("services_billed_unpaid(count)", alive
            .Where(r => Str(r, "billingcycle") is not ("One Time" or "Free Account") && Date(r, "nextduedate") is { } due && billed.Contains((Long(r, "id"), due)))
            .GroupBy(r => Str(r, "domainstatus")!).Select(g => (g.Key, N(g.Count()))));

        foreach (var line in lines)
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static string N(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static string Sum(IEnumerable<DumpRow> rows, string column) => Money(rows.Sum(r => Dec(r, column)));

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Day(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
}
