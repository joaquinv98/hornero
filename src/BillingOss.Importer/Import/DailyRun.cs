using System.Globalization;
using System.Reflection;

namespace BillingOss.Importer.Import;

/// <summary>Un día del paralelo real.</summary>
public sealed record DailyOptions(string Before, string After, DateOnly Day, string History)
{
    public IReadOnlyDictionary<string, ModuleMapping>? Modules { get; init; }

    /// <summary>Base importada del volcado previo. La de trabajo es la misma con _dia al final.</summary>
    public string BaseDb { get; init; } = "billing_oss_paralelo";

    public int? CancelUnpaidAfterDays { get; init; } = 7;

    public IReadOnlyList<string> OwnZones { get; init; } = [];
}

/// <summary>
/// Un día del paralelo real de punta a punta. Saca la línea base del volcado previo al ciclo y lo importa
/// en una base nueva, con el mapa de módulos en modo sombra. Corre la facturación y la mora del día en modo sombra
/// y compara contra el volcado posterior. Deja el informe del día en una carpeta propia y una línea en el
/// historial. Nunca emite, cobra, manda correos ni llama a un módulo: ver <see cref="ParallelRun"/>.
/// </summary>
public static class DailyRun
{
    public const string HistoryFile = "historial.tsv";

    /// <summary>
    /// Sale con 0 sin defectos y con la importación conciliada, con 3 si hay defectos o diferencias de conciliación y
    /// con 1 o 2 si no pudo correr. Un día que no pudo correr también queda en el historial, así no falta ningún día.
    /// </summary>
    public static async Task<int> Run(string admin, DailyOptions options, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        var day = options.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            return await RunDay(admin, options, day, output, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await AppendHistory(options.History, day, "falló", null, 1).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> RunDay(string admin, DailyOptions options, string day, TextWriter output, CancellationToken ct)
    {
        var folder = Path.Combine(options.History, day);
        Directory.CreateDirectory(folder);

        // La línea base sale del mismo volcado que se importa, contada sin las reglas del importador.
        var baseline = Path.Combine(folder, "linea-base.tsv");
        await using (var dump = DumpReader.Open(options.Before))
        await using (var file = new StreamWriter(baseline))
        {
            await Baseline.Write(dump, file, ct).ConfigureAwait(false);
        }

        // El volcado previo es de antes del ciclo del día: lo que vencía hasta el día anterior y el origen no hizo
        // queda retenido en una revisión del staff, igual que en el corte.
        int imported;
        await using (var log = new StreamWriter(Path.Combine(folder, "importacion.txt")))
        {
            imported = await ImportRun.Run(admin, new ImportOptions(options.Before)
            {
                Baseline = baseline,
                Modules = options.Modules,
                RecreateDb = options.BaseDb,
                ShadowInstances = true,
                AsOf = options.Day.AddDays(-1),
                OwnZones = options.OwnZones,
            }, log, ct).ConfigureAwait(false);
        }

        if (imported is not (0 or 3))
        {
            await output.WriteLineAsync($"Paralelo {day}: la importación no pudo correr (salida {imported}). Ver {Path.Combine(folder, "importacion.txt")}.").ConfigureAwait(false);
            await AppendHistory(options.History, day, "falló", null, imported).ConfigureAwait(false);
            return imported;
        }

        IReadOnlyList<Difference> diffs;
        await using (var log = new StreamWriter(Path.Combine(folder, "dia.txt")))
        {
            diffs = await new ParallelRun(admin, log).Run(options.After, options.BaseDb, $"{options.BaseDb}_dia", options.Day, options.CancelUnpaidAfterDays, ct).ConfigureAwait(false);
        }

        await using (var file = new StreamWriter(Path.Combine(folder, "diferencias.tsv")))
        {
            await ParallelRun.WriteTsv(diffs, file).ConfigureAwait(false);
        }

        var code = imported == 3 || diffs.Any(d => d.Error) ? 3 : 0;
        await using (var file = new StreamWriter(Path.Combine(folder, "resumen.txt")))
        {
            await Summary(day, imported, diffs, file).ConfigureAwait(false);
        }

        await Summary(day, imported, diffs, output).ConfigureAwait(false);
        await AppendHistory(options.History, day, imported == 0 ? "ok" : "diferencias", diffs, code).ConfigureAwait(false);
        return code;
    }

    /// <summary>Cuántas hay de cada clase y tipo, los defectos y las que hay que revisar. Solo IDs, fechas, importes y estados.</summary>
    public static async Task Summary(string day, int? imported, IReadOnlyList<Difference> diffs, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(diffs);
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteLineAsync($"Paralelo del {day}, billing-oss {Version()}.").ConfigureAwait(false);
        if (imported is not null)
        {
            await output.WriteLineAsync(imported == 0 ? "Importación conciliada." : "La conciliación de la importación tiene diferencias: ver importacion.txt.").ConfigureAwait(false);
        }
        foreach (var g in diffs.GroupBy(d => (d.Class, d.ClassName, d.Kind)).OrderByDescending(g => g.Key.Class).ThenBy(g => g.Key.Kind, StringComparer.Ordinal))
        {
            await output.WriteLineAsync($"{g.Key.ClassName,-10} {g.Key.Kind}: {g.Count()}").ConfigureAwait(false);
        }

        var shown = diffs.Where(d => d.Class == DifferenceClass.Defect).Take(50).Concat(diffs.Where(d => d.Class == DifferenceClass.Review)).ToList();
        if (shown.Count > 0)
        {
            await ParallelRun.WriteTsv(shown, output).ConfigureAwait(false);
        }

        var defects = diffs.Count(d => d.Error);
        await output.WriteLineAsync(defects == 0 ? "Paralelo: sin defectos." : $"Paralelo: {defects} defectos.").ConfigureAwait(false);
    }

    private static async Task AppendHistory(string folder, string day, string reconciliation, IReadOnlyList<Difference>? diffs, int code)
    {
        var path = Path.Combine(folder, HistoryFile);
        var header = !File.Exists(path);
        await using var file = new StreamWriter(path, append: true);
        if (header)
        {
            await file.WriteLineAsync("dia\tcorrida\tversion\tconciliacion\tesperadas\ta_revisar\tdefectos\tsalida").ConfigureAwait(false);
        }

        string Count(DifferenceClass c) => diffs is null ? string.Empty : diffs.Count(d => d.Class == c).ToString(CultureInfo.InvariantCulture);
        await file.WriteLineAsync(string.Join('\t', day, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), Version(), reconciliation,
            Count(DifferenceClass.Expected), Count(DifferenceClass.Review), Count(DifferenceClass.Defect), code.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    /// <summary>La versión del importador con el commit, así se sabe qué versión corrió cada día.</summary>
    private static string Version() =>
        typeof(DailyRun).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "sin versión";
}
