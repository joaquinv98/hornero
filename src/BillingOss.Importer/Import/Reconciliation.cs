using System.Globalization;

namespace BillingOss.Importer.Import;

/// <summary>
/// Compara lo importado con la línea base que se sacó de la copia (D-17): cuentas, servicios, dominios, facturas,
/// pagos, saldo a favor y comprobantes. Las facturas se comparan por su importe bruto (neto más impuestos), que es el total del
/// documento en el modelo propio: el saldo a favor aplicado es una imputación, no un descuento. Ninguna sección de la
/// línea base pasa en silencio: se compara, se omite con su motivo o se lista como faltante. Una sección que no
/// conoce cuenta como diferencia.
/// </summary>
public static class Reconciliation
{
    /// <summary>Secciones que no se comparan a propósito, con el motivo.</summary>
    private static readonly Dictionary<string, string> Skipped = new(StringComparer.Ordinal)
    {
        ["table_rows"] = "conteo crudo de filas por tabla del origen",
        ["non_empty"] = "columnas del origen que no se importan",
        ["invoice_items"] = "incluye las líneas de los borradores, los importes se comparan por factura",
        ["credit_movements"] = "el saldo a favor entra como un movimiento por cuenta y se compara el saldo",
        ["mod_afip_invoices_homologacion_by_type"] = "comprobantes de homologación, no se importan",
        ["mod_afip_invoices_homologacion_doc_nro_shape"] = "comprobantes de homologación, no se importan",
        ["mod_afip_invoices_doc_nro_shape"] = "forma de los documentos, sin importes",
        ["customfield_tax_shape"] = "forma de los documentos, sin importes",
        ["tblclients_tax_id_shape"] = "forma de los documentos, sin importes",
    };

    /// <summary>Lo que el origen tiene y el importador todavía no trae: no es diferencia de lo importado, pero se lista.</summary>
    private static readonly HashSet<string> NotImported = new(StringComparer.Ordinal);

    /// <summary>
    /// Cifras del importador que se muestran cuando la línea base no las trae (la de la copia sacada en el servidor no
    /// las tiene). Si la trae se comparan clave por clave, y una clave que solo tiene lo importado también es diferencia.
    /// </summary>
    private static readonly HashSet<string> Informative = new(StringComparer.Ordinal)
    {
        "payments_by_type", "cancel_requests_pending_by_type", "billing_notes_by_type_currency", "services_alive_by_due_month", "services_billed_unpaid",
        "provinces_unmapped", "tickets_by_status", "support_control", "affiliate_balance_by_currency", SourceImport.ContentImported, "catalog",
    };

    public static async Task<int> Compare(SourceImport import, string baselinePath, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(output);
        var diffs = 0;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var compared = new HashSet<(string, string)>();
        var afip = new Dictionary<string, (long Count, decimal Amount)>(StringComparer.Ordinal);

        foreach (var line in await File.ReadAllLinesAsync(baselinePath).ConfigureAwait(false))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var f = line.Split('\t');
            var section = f[0].Split('(')[0];
            switch (section)
            {
                case "clients_total":
                    var accounts = import.Figures.Where(x => x.Key.Section == "accounts_by_status").Sum(x => x.Value.Count);
                    diffs += await Check(output, "cuentas en total", Long(f[1]), 0, (accounts, 0m), compareAmount: false).ConfigureAwait(false);
                    break;
                case "clients_by_status":
                    diffs += await Check(output, $"cuentas {f[1]}", Long(f[2]), 0, Get(import, "accounts_by_status", f[1]), compareAmount: false).ConfigureAwait(false);
                    break;
                case "services_by_status":
                    diffs += await Check(output, $"servicios {f[1]}", Long(f[2]), Dec(f[3]), Get(import, "services_by_status", f[1])).ConfigureAwait(false);
                    break;
                case "domains_by_status":
                    diffs += await Check(output, $"dominios {f[1]}", Long(f[2]), 0, Get(import, "domains_by_status", f[1]), compareAmount: false).ConfigureAwait(false);
                    break;
                case "invoices_by_status_currency" when f[1] == "Draft":
                    await output.WriteLineAsync($"OMIT facturas Draft {f[2]}: {f[3]} borradores, no se importan").ConfigureAwait(false);
                    break;
                case "invoices_by_status_currency":
                    var gross = Dec(f[4]) + Dec(f[6]) + Dec(f[7]);
                    diffs += await Check(output, $"facturas {f[1]} {f[2]}", Long(f[3]), gross, Get(import, "invoices_by_status_currency", $"{f[1]}\t{f[2]}")).ConfigureAwait(false);
                    break;
                case "payments_by_currency":
                    diffs += await Check(output, $"pagos {f[1]}", Long(f[2]), Dec(f[3]), Get(import, "payments_by_currency", f[1])).ConfigureAwait(false);
                    break;
                case "payments_by_gateway":
                    diffs += await Check(output, $"pagos por pasarela '{f[1]}'", Long(f[2]), Dec(f[3]), Get(import, "payments_by_gateway", f[1])).ConfigureAwait(false);
                    break;
                case "payments_by_type":
                    seen.Add(section);
                    compared.Add((section, f[1]));
                    diffs += await Check(output, $"pagos por tipo '{f[1]}'", Long(f[2]), Dec(f[3]), Get(import, "payments_by_type", f[1])).ConfigureAwait(false);
                    break;
                case "billing_notes_by_type_currency":
                    seen.Add(section);
                    compared.Add((section, $"{f[1]}\t{f[2]}"));
                    diffs += await Check(output, $"notas nativas {f[1]} {f[2]}", Long(f[3]), Dec(f[4]), Get(import, section, $"{f[1]}\t{f[2]}")).ConfigureAwait(false);
                    break;
                case "cancel_requests_pending_by_type":
                    seen.Add(section);
                    compared.Add((section, f[1]));
                    diffs += await Check(output, $"bajas pedidas sin ejecutar '{f[1]}'", Long(f[2]), 0, Get(import, section, f[1]), compareAmount: false).ConfigureAwait(false);
                    break;
                case "services_alive_by_due_month":
                    seen.Add(section);
                    compared.Add((section, f[1]));
                    diffs += await Check(output, $"servicios vivos que vencen en {f[1]}", Long(f[2]), Dec(f[3]), Get(import, section, f[1])).ConfigureAwait(false);
                    break;
                case "services_billed_unpaid":
                    seen.Add(section);
                    compared.Add((section, f[1]));
                    diffs += await Check(output, $"servicios {f[1]} con el período del vencimiento ya facturado", Long(f[2]), 0, Get(import, section, f[1]), compareAmount: false).ConfigureAwait(false);
                    break;
                case "credit_balance_by_currency":
                    diffs += await Check(output, $"saldo a favor {f[1]}", Long(f[2]), Dec(f[3]), Get(import, "credit_balance_by_currency", f[1])).ConfigureAwait(false);
                    break;
                case "customfield_iva_tickbox" when import.Figures.ContainsKey(("customfield_values", $"{f[1]}\t{f[2]}")):
                    diffs += await Check(output, $"marca {f[1]} '{f[2]}'", Long(f[3]), 0, Get(import, "customfield_values", $"{f[1]}\t{f[2]}"), compareAmount: false).ConfigureAwait(false);
                    break;
                case "customfield_iva_tickbox":
                    // Los valores de la condición de IVA se traducen a un código y no se cuentan uno por uno.
                    if (skipped.Add($"{section} {f[1]}"))
                    {
                        await output.WriteLineAsync($"OMIT {section} campo {f[1]}: el importador lo traduce, no lo usa como marca").ConfigureAwait(false);
                    }

                    break;
                case "table_rows" when SourceImport.SupportTables.Contains(f[1]):
                    // El soporte se importa fila por fila: cada fila del origen tiene que estar contada.
                    diffs += await Check(output, $"filas de {f[1]}", Long(f[2]), 0, Get(import, "support_rows", f[1]), compareAmount: false).ConfigureAwait(false);
                    break;
                case "mod_afip_invoices_by_type":
                    var key = $"{f[1]}\t{f[3]}";
                    var current = afip.GetValueOrDefault(key);
                    afip[key] = (current.Count + Long(f[5]), current.Amount + Dec(f[6]));
                    break;
                case "table_rows" when SourceImport.AffiliateRows.Contains(f[1]):
                    diffs += await Check(output, $"filas de {f[1]}", Long(f[2]), 0, Get(import, "affiliate_rows", f[1]), compareAmount: false).ConfigureAwait(false);
                    break;
                case "table_rows" when f[1] == SourceImport.ImagesTable:
                    // El volcado trae solo los nombres de los archivos de las imágenes: se cargan a mano en un host permitido.
                    missing.Add("imágenes de la base de conocimiento");
                    await output.WriteLineAsync($"FALTA {f[1]} {f[2]}: el volcado no trae los archivos, las imágenes se cargan en un host de Content:ImageHosts").ConfigureAwait(false);
                    break;
                case "table_rows" when SourceImport.ContentTables.Contains(f[1]):
                    // Cada fila de contenidos se importa, se une como traducción o se descarta con su incidencia: ninguna se pierde.
                    diffs += await Check(output, $"contenidos {f[1]}", Long(f[2]), 0, Get(import, SourceImport.ContentRows, f[1]), compareAmount: false).ConfigureAwait(false);
                    break;
                case var s when NotImported.Contains(s):
                    missing.Add(s);
                    await output.WriteLineAsync($"FALTA {s} {string.Join(' ', f[1..])}: no se importa todavía").ConfigureAwait(false);
                    break;
                case var s when Skipped.TryGetValue(s, out var reason):
                    if (skipped.Add(s))
                    {
                        await output.WriteLineAsync($"OMIT {s}: {reason}").ConfigureAwait(false);
                    }

                    break;
                default:
                    diffs++;
                    await output.WriteLineAsync($"DIF  sección sin comparar: {line}").ConfigureAwait(false);
                    break;
            }
        }

        foreach (var (key, expected) in afip.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            diffs += await Check(output, $"comprobantes tipo {key.Replace('\t', ' ')}", expected.Count, expected.Amount, Get(import, "fiscal_by_type", key)).ConfigureAwait(false);
        }

        // Lo importado que la línea base no tiene, en las secciones que trae: también es diferencia.
        foreach (var figure in import.Figures.Where(x => seen.Contains(x.Key.Section) && !compared.Contains((x.Key.Section, x.Key.Key))).OrderBy(x => x.Key.Section, StringComparer.Ordinal).ThenBy(x => x.Key.Key, StringComparer.Ordinal))
        {
            diffs++;
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"DIF  {figure.Key.Section} {figure.Key.Key.Replace('\t', ' ')}: {figure.Value.Count} importados y la línea base no lo tiene")).ConfigureAwait(false);
        }

        // Cifras propias que la línea base todavía no trae: se muestran, no se comparan.
        foreach (var figure in import.Figures.Where(x => Informative.Contains(x.Key.Section) && !seen.Contains(x.Key.Section)).OrderBy(x => x.Key.Section, StringComparer.Ordinal).ThenBy(x => x.Key.Key, StringComparer.Ordinal))
        {
            var amount = figure.Value.Amount == 0m ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({figure.Value.Amount:0.00})");
            await output.WriteLineAsync($"INFO {figure.Key.Section} {figure.Key.Key.Replace('\t', ' ')}: {figure.Value.Count}{amount}").ConfigureAwait(false);
        }

        // Control propio, sin línea base: las imputaciones de un documento nunca suman más que lo imputado en su cabecera.
        if (import.Figures.ContainsKey(("documents_control", "imputaciones_sobre_cabecera")))
        {
            diffs += await Check(output, "documentos con imputaciones por encima de su cabecera", 0, 0m,
                Get(import, "documents_control", "imputaciones_sobre_cabecera"), compareAmount: false).ConfigureAwait(false);
        }

        // Control propio, sin línea base: ningún vivo recurrente puede quedar anclado fuera del día de su vencimiento.
        if (import.Figures.ContainsKey(("services_anchor", "fuera_del_vencimiento")))
        {
            var other = Get(import, "services_anchor", "alta_en_otro_dia").Count;
            diffs += await Check(output, $"servicios vivos anclados fuera del día de su vencimiento ({other} con alta en otro día)", 0, 0m,
                Get(import, "services_anchor", "fuera_del_vencimiento"), compareAmount: false).ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            diffs > 0 ? $"Conciliación: {diffs} diferencias."
            : missing.Count > 0 ? $"Conciliación: sin diferencias en lo importado. Sin importar todavía: {string.Join(", ", missing)}."
            : "Conciliación: sin diferencias.").ConfigureAwait(false);
        return diffs;
    }

    private static (long Count, decimal Amount) Get(SourceImport import, string section, string key) =>
        import.Figures.GetValueOrDefault((section, key));

    private static async Task<int> Check(TextWriter output, string label, long count, decimal amount, (long Count, decimal Amount) ours, bool compareAmount = true)
    {
        var ok = ours.Count == count && (!compareAmount || ours.Amount == amount);
        var detail = compareAmount
            ? string.Create(CultureInfo.InvariantCulture, $"{ours.Count} / {count}, {ours.Amount:0.00} / {amount:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"{ours.Count} / {count}");
        await output.WriteLineAsync($"{(ok ? "OK  " : "DIF ")} {label}: {detail}").ConfigureAwait(false);
        return ok ? 0 : 1;
    }

    private static long Long(string s) => long.Parse(s, CultureInfo.InvariantCulture);

    private static decimal Dec(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);
}
