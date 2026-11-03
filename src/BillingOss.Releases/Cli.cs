using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using BillingOss.Updates;

namespace BillingOss.Releases;

/// <summary>La línea de comandos de billing-oss-versiones. Ninguno firma ni publica: eso lo hacen los trabajos de Forgejo.</summary>
public static class Cli
{
    public const string Usage = """
        Uso: billing-oss-versiones COMANDO
          vigia --linea NOMBRE=CARPETA... [--trivy ARCHIVO] [--metadata URL] [--salida ARCHIVO]
                [--forgejo URL --repo DUEÑO/REPO [--mantenedores a,b]]
              Hallazgos de cada línea: CARPETA/vigia-nuget.json (dotnet list package --vulnerable --include-transitive
              --format json), el runtime de CARPETA/deploy/Dockerfile contra la metadata de .NET y, si se pasa, Trivy
              corrido sobre la carpeta de las líneas. Con --forgejo los deja en issues (token en FORGEJO_TOKEN).
              Sale con 1 si hay hallazgos.
          manifiesto --version X.Y.Z --imagen NOMBRE=REF|METADATA.json... --receta CARPETA --dockerfile ARCHIVO
                [--actualizador ARCHIVO --actualizador-url URL] [--notas URL] --salida ARCHIVO
          entrada --canal ARCHIVO --manifiesto ARCHIVO [--seguridad] [--severidad low|medium|high|critical]
                [--cve CVE-AAAA-NNNN[:origen]]... [--desde X.Y.Z] [--ventana] [--escalonado 0..100]
          retirar --canal ARCHIVO X.Y.Z
          renovar --canal ARCHIVO --salida ARCHIVO
              Emitido ahora, vence a los 7 días y serial nuevo: lo que se firma después.
        """;

    public static async Task<int> Run(string[] args, HttpClient http, TextWriter output, TimeProvider clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        var argv = args.ToList();
        try
        {
            switch (argv.FirstOrDefault())
            {
                case "vigia":
                    return await WatchCommand(argv, http, output, clock.GetUtcNow(), ct);

                case "manifiesto":
                    var images = Values(argv, "--imagen").Select(Pair).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    var manifest = Manifests.Build(
                        Required(argv, "--version"),
                        images,
                        Required(argv, "--receta"),
                        Required(argv, "--dockerfile"),
                        Value(argv, "--actualizador"),
                        Value(argv, "--actualizador-url") is { } updaterUrl ? new Uri(updaterUrl) : null,
                        Value(argv, "--notas") is { } notes ? new Uri(notes) : null,
                        DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
                    await File.WriteAllTextAsync(Required(argv, "--salida"), manifest.ToJson(), ct);
                    await output.WriteLineAsync($"Manifiesto de {manifest.Release.ReleaseVersion}: runtime {manifest.Release.DotnetRuntime}, {manifest.Release.Images.All().Count()} imágenes por digest.");
                    return 0;

                case "entrada":
                    var file = Required(argv, "--canal");
                    var channel = await Load(file, ct);
                    var entry = ReleaseManifest.Parse(await File.ReadAllTextAsync(Required(argv, "--manifiesto"), ct));
                    var added = Manifests.Add(
                        channel,
                        entry,
                        argv.Contains("--seguridad"),
                        Value(argv, "--severidad"),
                        [.. Values(argv, "--cve").Select(Manifests.ParseCve)],
                        Value(argv, "--desde"),
                        argv.Contains("--ventana"),
                        Value(argv, "--escalonado") is { } rollout ? int.Parse(rollout, NumberStyles.None, CultureInfo.InvariantCulture) : 100);
                    await File.WriteAllTextAsync(file, added.ToJson(), ct);
                    await output.WriteLineAsync($"{file}: suma {entry.Release.ReleaseVersion}. Abrí el PR con tu cuenta: lo aprueba otro mantenedor.");
                    return 0;

                case "retirar" when argv.Count >= 4:
                    var target = Required(argv, "--canal");
                    var withdrawn = Manifests.Withdraw(await Load(target, ct), Versions.Parse(argv[^1]));
                    await File.WriteAllTextAsync(target, withdrawn.ToJson(), ct);
                    await output.WriteLineAsync($"{target}: {argv[^1]} retirada. La última ofrecida queda en {withdrawn.LatestRelease ?? "ninguna"}.");
                    return 0;

                case "renovar":
                    var renewed = Manifests.Renew(await Load(Required(argv, "--canal"), ct), clock.GetUtcNow());
                    await File.WriteAllTextAsync(Required(argv, "--salida"), renewed.ToJson(), ct);
                    await output.WriteLineAsync($"Canal {renewed.Name}: serial {renewed.Serial}, vence {renewed.Expires:yyyy-MM-dd HH:mm} UTC.");
                    return 0;

                default:
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }
        catch (UpdateException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static async Task<int> WatchCommand(List<string> argv, HttpClient http, TextWriter output, DateTimeOffset now, CancellationToken ct)
    {
        var lines = Values(argv, "--linea").Select(Pair).ToList();
        if (lines.Count == 0)
        {
            throw new UpdateException("Falta al menos una --linea NOMBRE=CARPETA.");
        }

        var metadata = Value(argv, "--metadata") is { } url ? new Uri(url.EndsWith('/') ? url : url + "/") : Watch.OfficialMetadata;
        var findings = new List<Finding>();
        foreach (var (line, folder) in lines)
        {
            findings.AddRange(Watch.FromNuGet(line, await File.ReadAllTextAsync(Path.Combine(folder, "vigia-nuget.json"), ct)));
            var runtime = Watch.Runtime(await File.ReadAllTextAsync(Path.Combine(folder, "deploy", "Dockerfile"), ct));
            if (await Watch.FromDotnetMetadata(line, runtime, http, metadata, ct) is { } dotnet)
            {
                findings.Add(dotnet);
            }
        }

        if (Value(argv, "--trivy") is { } trivy)
        {
            var byFolder = lines.ToDictionary(l => Path.GetFileName(l.Value.TrimEnd('/', '\\')), l => l.Key, StringComparer.Ordinal);
            findings.AddRange(Watch.FromTrivy(byFolder, await File.ReadAllTextAsync(trivy, ct)));
        }

        var unique = Watch.Deduplicate(findings);
        foreach (var f in unique)
        {
            await output.WriteLineAsync($"{f.Line}: {f.Id} en {f.Package} {f.Version}, {f.Severity}, arreglo {f.Fixed ?? "sin versión"} ({f.Source}).");
        }

        await output.WriteLineAsync(unique.Count == 0 ? "Sin hallazgos." : $"{unique.Count} hallazgos.");
        if (Value(argv, "--salida") is { } file)
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { corrida = now, lineas = lines.Select(l => l.Key), hallazgos = unique }, Watch.Json), ct);
        }

        if (Value(argv, "--forgejo") is { } forgejo)
        {
            var token = Environment.GetEnvironmentVariable("FORGEJO_TOKEN");
            if (string.IsNullOrEmpty(token))
            {
                throw new UpdateException("Falta FORGEJO_TOKEN para dejar los issues.");
            }

            using var api = new HttpClient { BaseAddress = new Uri(forgejo.TrimEnd('/') + "/api/v1/"), Timeout = TimeSpan.FromMinutes(2) };
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
            var maintainers = (Value(argv, "--mantenedores") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await new ForgejoIssues(api, Required(argv, "--repo")).Sync(unique, [.. lines.Select(l => l.Key)], maintainers, now, ct);
            await output.WriteLineAsync($"Issues: {result.Created} nuevos, {result.Updated} actualizados, {result.Closed} cerrados, {result.Overdue} vencidos. Estado en #{result.StatusIssue}.");
        }

        return unique.Count == 0 ? 0 : 1;
    }

    private static async Task<ReleaseChannel> Load(string file, CancellationToken ct) =>
        File.Exists(file) ? ReleaseChannel.Parse(await File.ReadAllTextAsync(file, ct)) : Manifests.Empty(Path.GetFileNameWithoutExtension(file));

    private static KeyValuePair<string, string> Pair(string text)
    {
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        return eq > 0 ? new(text[..eq], text[(eq + 1)..]) : throw new UpdateException($"«{text}» no tiene la forma NOMBRE=VALOR.");
    }

    private static string? Value(List<string> argv, string name) => Values(argv, name).LastOrDefault();

    private static string Required(List<string> argv, string name) => Value(argv, name) ?? throw new UpdateException($"Falta {name}.");

    private static IEnumerable<string> Values(List<string> argv, string name)
    {
        for (var i = 1; i + 1 < argv.Count; i++)
        {
            if (argv[i] == name)
            {
                yield return argv[i + 1];
            }
        }
    }
}
