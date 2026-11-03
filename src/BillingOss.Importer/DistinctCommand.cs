namespace BillingOss.Importer;

/// <summary>
/// Valores distintos de columnas de enumeración, con su cantidad, para escribir el mapeo. Usarlo solo sobre
/// columnas que no tienen datos personales: tipos, estados, ciclos, monedas y nombres de configuración.
/// </summary>
public static class DistinctCommand
{
    public static async Task RunAsync(DumpReader dump, string table, IReadOnlyList<string> columns, TextWriter output, int limit = 60)
    {
        ArgumentNullException.ThrowIfNull(dump);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(output);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var row in dump.ReadRowsAsync().ConfigureAwait(false))
        {
            if (!string.Equals(row.Table.Name, table, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = string.Join(" | ", columns.Select(c => row[c] switch { null => "NULL", byte[] => "<bytes>", var v => v.ToString() }));
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var (value, count) in counts.OrderByDescending(kv => kv.Value).Take(limit))
        {
            await output.WriteLineAsync($"{count,8}  {value}").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"{counts.Count} combinaciones distintas").ConfigureAwait(false);
    }
}
