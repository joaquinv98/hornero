namespace BillingOss.Importer;

/// <summary>El comando <c>inspect</c>: por tabla, la cantidad de filas y las columnas con su tipo.</summary>
public static class InspectCommand
{
    public static async Task RunAsync(DumpReader dump, TextWriter output)
    {
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var row in dump.ReadRowsAsync())
        {
            rows[row.Table.Name] = rows.GetValueOrDefault(row.Table.Name) + 1;
        }

        long total = 0;
        foreach (var table in dump.Tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var count = rows.GetValueOrDefault(table.Name);
            total += count;
            await output.WriteLineAsync($"{table.Name}: {Count(count, "fila", "filas")}");
            var width = table.Columns.Count == 0 ? 0 : table.Columns.Max(c => c.Name.Length);
            foreach (var column in table.Columns)
            {
                await output.WriteLineAsync($"  {column.Name.PadRight(width)}  {column.Type}");
            }
        }

        await output.WriteLineAsync($"{Count(dump.Tables.Count, "tabla", "tablas")}, {Count(total, "fila", "filas")}");
    }

    private static string Count(long n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
