using System.Globalization;
using BillingOss.Importer;
using BillingOss.Importer.Import;

const string Usage = """
    Uso:
      BillingOss.Importer inspect <dump>
      BillingOss.Importer distinct <dump> <tabla> <columna> [columna...]   (solo columnas sin datos personales)
      BillingOss.Importer baseline <dump> [--out <linea-base.tsv>]
        Línea base contada directo del volcado, con el formato de la de la copia, para conciliar la importación.
      BillingOss.Importer import <dump> [--baseline <linea-base.tsv>] [--modules <mapa.json>] [--simulate] [--recreate-db <billing_oss_nombre>]
          [--own-zones <zona,zona>] [--shadow-instances] [--as-of <AAAA-MM-DD>] [--support-files <carpeta>]
        La base destino sale de BILLINGOSS_IMPORT_PG (o del archivo de BILLINGOSS_IMPORT_PG_FILE), nunca de la línea de
        comandos. Tiene que estar sin cuentas. El mapa de módulos ata los servicios vivos a sus instancias
        (diseno/importacion.md). --own-zones son los dominios propios: sus subdominios van a la instancia de subdominios
        propios. --shadow-instances crea sin URL las instancias del mapa que faltan, para el paralelo. --as-of es el día
        de la copia (por defecto hoy): lo que vencía hasta ese día y el origen no hizo queda retenido. --support-files
        es la carpeta de adjuntos del soporte del sistema anterior: los que estén entran con su archivo.
      BillingOss.Importer parallel <volcado-posterior> --base <base-importada> --day <AAAA-MM-DD>
          [--work <base-de-trabajo>] [--report <diferencias.tsv>] [--cancel-unpaid-days <días|none>]
        Un día del mes en paralelo: clona la base importada, corre el día y compara contra el volcado posterior.
        Sale con 3 si hay defectos. Ver docs/paralelo.md.
      BillingOss.Importer daily --day <AAAA-MM-DD> --before <volcado-previo> --after <volcado-posterior> --modules <mapa.json>
          --history <carpeta> [--base <billing_oss_nombre>] [--cancel-unpaid-days <días|none>] [--own-zones <zona,zona>]
        El día completo del paralelo real: línea base e importación del volcado previo en modo sombra, el día y el
        informe en <carpeta>/<día>, con una línea en <carpeta>/historial.tsv. Sale con 3 si hay defectos o diferencias
        de conciliación.
    """;

try
{
    switch (args)
    {
        case ["inspect", var path]:
            {
                await using var dump = DumpReader.Open(path);
                await InspectCommand.RunAsync(dump, Console.Out);
                return 0;
            }

        case ["distinct", var path, var table, .. var columns] when columns.Length > 0:
            {
                await using var dump = DumpReader.Open(path);
                await DistinctCommand.RunAsync(dump, table, columns, Console.Out);
                return 0;
            }

        case ["baseline", var path, .. var rest]:
            {
                await using var dump = DumpReader.Open(path);
                if (Option(rest, "--out") is { } file)
                {
                    await using var output = new StreamWriter(file);
                    await Baseline.Write(dump, output, CancellationToken.None);
                }
                else
                {
                    await Baseline.Write(dump, Console.Out, CancellationToken.None);
                }

                return 0;
            }

        case ["import", var path, .. var rest]:
            return await Import(path, rest);

        case ["parallel", var path, .. var rest]:
            return await Parallel(path, rest);

        case ["daily", .. var rest]:
            return await Daily(rest);

        default:
            await Console.Error.WriteLineAsync(Usage);
            return 2;
    }
}
catch (Exception e) when (e is DumpFormatException or IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
{
    await Console.Error.WriteLineAsync($"Error: {e.Message}");
    return 1;
}

static string? Option(string[] options, string name) => options.SkipWhile(o => o != name).Skip(1).FirstOrDefault();

// La cadena del servidor sale del entorno o de un archivo legible solo por quien corre el importador.
static string? Connection() =>
    Environment.GetEnvironmentVariable("BILLINGOSS_IMPORT_PG")
    ?? (Environment.GetEnvironmentVariable("BILLINGOSS_IMPORT_PG_FILE") is { Length: > 0 } file ? File.ReadAllText(file).Trim() : null);

// La anulación de impagas puede venir de código propio del origen y no estar en sus ajustes: por defecto, 7 días.
static int? CancelDays(string[] options) => Option(options, "--cancel-unpaid-days") switch
{
    null => 7,
    "none" => null,
    var d => int.Parse(d, CultureInfo.InvariantCulture),
};

static IReadOnlyList<string> Zones(string[] options) =>
    (Option(options, "--own-zones") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static async Task<int> Parallel(string path, string[] options)
{
    var connection = Connection();
    var baseDb = Option(options, "--base");
    if (connection is null || baseDb is null || !DateOnly.TryParseExact(Option(options, "--day"), "yyyy-MM-dd", out var day))
    {
        await Console.Error.WriteLineAsync("Faltan BILLINGOSS_IMPORT_PG, --base o --day (AAAA-MM-DD).");
        return 2;
    }

    var run = new ParallelRun(connection, Console.Out);
    IReadOnlyList<Difference> diffs;
    try
    {
        diffs = await run.Run(path, baseDb, Option(options, "--work") ?? $"{baseDb}_dia", day, CancelDays(options), CancellationToken.None);
    }
    catch (ArgumentException e)
    {
        await Console.Error.WriteLineAsync(e.Message);
        return 2;
    }

    if (Option(options, "--report") is { } report)
    {
        await using var file = new StreamWriter(report);
        await ParallelRun.WriteTsv(diffs, file);
    }

    await DailyRun.Summary(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, diffs, Console.Out);
    return diffs.Any(d => d.Error) ? 3 : 0;
}

static async Task<int> Daily(string[] options)
{
    var connection = Connection();
    var (before, after, history, map) = (Option(options, "--before"), Option(options, "--after"), Option(options, "--history"), Option(options, "--modules"));
    if (connection is null || before is null || after is null || history is null || map is null
        || !DateOnly.TryParseExact(Option(options, "--day"), "yyyy-MM-dd", out var day))
    {
        await Console.Error.WriteLineAsync("Faltan BILLINGOSS_IMPORT_PG, --day (AAAA-MM-DD), --before, --after, --modules o --history.");
        return 2;
    }

    try
    {
        return await DailyRun.Run(connection, new DailyOptions(before, after, day, history)
        {
            Modules = ModuleMapping.Parse(await File.ReadAllTextAsync(map)),
            BaseDb = Option(options, "--base") ?? "billing_oss_paralelo",
            CancelUnpaidAfterDays = CancelDays(options),
            OwnZones = Zones(options),
        }, Console.Out, CancellationToken.None);
    }
    catch (ArgumentException e)
    {
        await Console.Error.WriteLineAsync(e.Message);
        return 2;
    }
}

static async Task<int> Import(string path, string[] options)
{
    var connection = Connection();
    if (connection is null)
    {
        await Console.Error.WriteLineAsync("Falta BILLINGOSS_IMPORT_PG con la base destino.");
        return 2;
    }

    DateOnly? asOf = null;
    if (Option(options, "--as-of") is { } text)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", out var parsed))
        {
            await Console.Error.WriteLineAsync("--as-of va como AAAA-MM-DD.");
            return 2;
        }

        asOf = parsed;
    }

    var supportFiles = Option(options, "--support-files");
    if (supportFiles is not null && !Directory.Exists(supportFiles))
    {
        await Console.Error.WriteLineAsync($"No existe la carpeta de adjuntos: {supportFiles}");
        return 2;
    }

    return await ImportRun.Run(connection, new ImportOptions(path)
    {
        Baseline = Option(options, "--baseline"),
        Modules = Option(options, "--modules") is { } map ? ModuleMapping.Parse(await File.ReadAllTextAsync(map)) : null,
        Simulate = options.Contains("--simulate"),
        RecreateDb = Option(options, "--recreate-db"),
        OwnZones = Zones(options),
        ShadowInstances = options.Contains("--shadow-instances"),
        AsOf = asOf,
        SupportFiles = supportFiles,
    }, Console.Out, CancellationToken.None);
}
