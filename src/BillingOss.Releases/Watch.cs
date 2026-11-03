using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BillingOss.Updates;

namespace BillingOss.Releases;

/// <summary>Un hallazgo del vigía en una línea soportada.</summary>
/// <param name="Id">El aviso (GHSA-... o CVE-...), o dotnet-runtime para un runtime al que le falta un parche de seguridad.</param>
/// <param name="Source">nuget, trivy o dotnet.</param>
/// <param name="References">Enlaces del aviso o, para el runtime, las CVE de las versiones que faltan.</param>
public sealed record Finding(string Line, string Id, string Source, string Package, string Version, string Severity, string? Fixed, IReadOnlyList<string> References)
{
    /// <summary>Se deduplica por aviso y por línea (sección 3.2 de D-20).</summary>
    public string Key => $"{Id}@{Line}";
}

/// <summary>
/// El vigía diario (D-20, sección 3.2): junta lo que reportan NuGet con las transitivas, Trivy y la metadata oficial de
/// .NET para cada línea soportada, y lo deja en issues de Forgejo. Es determinista: no decide nada, solo reporta.
/// </summary>
public static partial class Watch
{
    public static Uri OfficialMetadata { get; } = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/");

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>low, medium, high o critical. Moderate, unknown o vacía cuentan como medium.</summary>
    public static string Severity(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "LOW" => "low",
        "HIGH" => "high",
        "CRITICAL" => "critical",
        _ => "medium",
    };

    /// <summary>La salida de dotnet list package --vulnerable --include-transitive --format json.</summary>
    public static IReadOnlyList<Finding> FromNuGet(string line, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var findings = new List<Finding>();
        foreach (var framework in Items(doc.RootElement, "projects").SelectMany(p => Items(p, "frameworks")))
        {
            foreach (var package in Items(framework, "topLevelPackages").Concat(Items(framework, "transitivePackages")))
            {
                foreach (var vulnerability in Items(package, "vulnerabilities"))
                {
                    var url = Text(vulnerability, "advisoryurl") ?? string.Empty;
                    findings.Add(new Finding(line, AdvisoryId(url), "nuget", Text(package, "id") ?? "?", Text(package, "resolvedVersion") ?? "?", Severity(Text(vulnerability, "severity")), null, [url]));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// La salida de trivy fs (o trivy sbom) en JSON, corrida sobre la carpeta que tiene una subcarpeta por línea: cada
    /// resultado es de la línea cuya carpeta encabeza su Target. Con una sola línea, todo es de ella.
    /// </summary>
    public static IReadOnlyList<Finding> FromTrivy(IReadOnlyDictionary<string, string> linesByFolder, string json)
    {
        ArgumentNullException.ThrowIfNull(linesByFolder);
        using var doc = JsonDocument.Parse(json);
        var findings = new List<Finding>();
        foreach (var result in Items(doc.RootElement, "Results"))
        {
            var folder = (Text(result, "Target") ?? string.Empty).Replace('\\', '/').Split('/')[0];
            var line = linesByFolder.TryGetValue(folder, out var owner) ? owner : linesByFolder.Count == 1 ? linesByFolder.Values.Single() : null;
            if (line is null)
            {
                continue;
            }

            foreach (var vulnerability in Items(result, "Vulnerabilities"))
            {
                // El mismo aviso que reporta NuGet viene con su GHSA entre los VendorIDs: así se deduplica.
                var ghsa = Items(vulnerability, "VendorIDs").Select(v => v.GetString()).FirstOrDefault(v => v?.StartsWith("GHSA-", StringComparison.Ordinal) == true);
                findings.Add(new Finding(
                    line,
                    ghsa ?? Text(vulnerability, "VulnerabilityID") ?? "?",
                    "trivy",
                    Text(vulnerability, "PkgName") ?? "?",
                    Text(vulnerability, "InstalledVersion") ?? "?",
                    Severity(Text(vulnerability, "Severity")),
                    Text(vulnerability, "FixedVersion") is { Length: > 0 } fixedVersion ? fixedVersion : null,
                    Text(vulnerability, "PrimaryURL") is { } url ? [url] : []));
            }
        }

        return findings;
    }

    /// <summary>
    /// El runtime que trae la imagen de una línea: la versión exacta de la etiqueta de RUNTIME_IMAGE en deploy/Dockerfile
    /// (aspnet:X.Y.Z-...). Renovate la mueve con cada parche de .NET.
    /// </summary>
    public static Version Runtime(string dockerfile)
    {
        ArgumentNullException.ThrowIfNull(dockerfile);
        var match = RuntimeTag().Match(dockerfile);
        return match.Success
            ? Version.Parse(match.Groups[1].Value)
            : throw new UpdateException("deploy/Dockerfile no fija el runtime con una versión X.Y.Z en ARG RUNTIME_IMAGE.");
    }

    /// <summary>
    /// Compara el runtime de una línea con la metadata oficial de .NET (sección 3.2, paso 2). NuGetAudit no ve el runtime
    /// porque el SDK poda del grafo los paquetes del framework compartido. Si hay versiones más nuevas de su canal marcadas
    /// security, devuelve un hallazgo con sus cve-list.
    /// </summary>
    public static async Task<Finding?> FromDotnetMetadata(string line, Version runtime, HttpClient http, Uri metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(http);
        var channel = string.Create(CultureInfo.InvariantCulture, $"{runtime.Major}.{runtime.Minor}");
        var index = await http.GetFromJsonAsync<JsonElement>(new Uri(metadata, "releases-index.json"), ct);
        var entry = Items(index, "releases-index").Where(e => Text(e, "channel-version") == channel).Select(e => (JsonElement?)e).FirstOrDefault()
            ?? throw new UpdateException($"La metadata de .NET no tiene el canal {channel}.");
        var releasesUrl = Text(entry, "releases.json") is { } url ? new Uri(url) : new Uri(metadata, $"{channel}/releases.json");
        var releases = await http.GetFromJsonAsync<JsonElement>(releasesUrl, ct);

        var missing = Items(releases, "releases")
            .Where(r => r.TryGetProperty("security", out var security) && security.ValueKind == JsonValueKind.True)
            .Select(r => (Release: r, Version: System.Version.TryParse(r.TryGetProperty("runtime", out var rt) ? Text(rt, "version") : Text(r, "release-version"), out var v) ? v : null))
            .Where(r => r.Version is not null && r.Version > runtime)
            .ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        var cves = missing.SelectMany(r => Items(r.Release, "cve-list")).Select(c => Text(c, "cve-id")).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var latest = missing.Max(r => r.Version)!;
        return new Finding(line, "dotnet-runtime", "dotnet", "Microsoft.NETCore.App y Microsoft.AspNetCore.App", runtime.ToString(3), "high", latest.ToString(3), cves);
    }

    /// <summary>Un hallazgo por aviso y línea, con la severidad más alta y todas las referencias.</summary>
    public static IReadOnlyList<Finding> Deduplicate(IEnumerable<Finding> findings) =>
        [.. findings
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var worst = g.OrderByDescending(f => Rank(f.Severity)).First();
                return worst with
                {
                    Source = string.Join("+", g.Select(f => f.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                    Fixed = worst.Fixed ?? g.Select(f => f.Fixed).FirstOrDefault(f => f is not null),
                    References = [.. g.SelectMany(f => f.References).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal)],
                };
            })
            .OrderBy(f => f.Line, StringComparer.Ordinal)
            .ThenByDescending(f => Rank(f.Severity))
            .ThenBy(f => f.Id, StringComparer.Ordinal)];

    /// <summary>
    /// Plazo objetivo para publicar la versión (sección 3.4): seguridad de .NET en 2 días hábiles, alta o crítica en 7
    /// días, y media o baja en la próxima versión, sin fecha.
    /// </summary>
    public static DateTimeOffset? Deadline(Finding finding, DateTimeOffset opened)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Source == "dotnet")
        {
            var day = opened;
            for (var added = 0; added < 2;)
            {
                day = day.AddDays(1);
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    added++;
                }
            }

            return day;
        }

        return finding.Severity is "high" or "critical" ? opened.AddDays(7) : null;
    }

    public static int Rank(string severity) => severity switch
    {
        "critical" => 3,
        "high" => 2,
        "medium" => 1,
        _ => 0,
    };

    internal static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static IEnumerable<JsonElement> Items(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    /// <summary>GHSA-xxxx-xxxx-xxxx del final de la URL del aviso, o la URL si no lo tiene.</summary>
    private static string AdvisoryId(string url) => url.TrimEnd('/').Split('/')[^1] is { Length: > 0 } last ? last : url;

    [GeneratedRegex(@"^ARG RUNTIME_IMAGE=\S+/aspnet:(\d+\.\d+\.\d+)-", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeTag();
}

/// <summary>Lo que dejó la sincronización con Forgejo.</summary>
public sealed record SyncResult(int Created, int Updated, int Closed, int Overdue, long StatusIssue);

/// <summary>
/// Issues del vigía en Forgejo (API v1): uno fijado con el estado y la fecha de la última corrida, que mira el monitoreo, y
/// uno por hallazgo con etiquetas de severidad y de línea. Si un hallazgo pasa su plazo objetivo sin versión, lleva la
/// etiqueta vencido y un comentario que menciona a los mantenedores, y Forgejo les manda el correo. Cuando deja de
/// aparecer, se cierra.
/// </summary>
/// <param name="http">Con BaseAddress en la raíz de la API (https://git.example/api/v1/) y el token del trabajo.</param>
public sealed partial class ForgejoIssues(HttpClient http, string repo)
{
    public const string Label = "vigia";

    public const string OverdueLabel = "vencido";

    private const string StatusKey = "estado";

    public async Task<SyncResult> Sync(IReadOnlyList<Finding> findings, IReadOnlyList<string> lines, IReadOnlyList<string> maintainers, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(maintainers);
        var labels = await Labels(ct);
        var issues = new Dictionary<string, Issue>(StringComparer.Ordinal);
        foreach (var issue in await Paged<Issue>($"repos/{repo}/issues?state=open&type=issues&labels={Label}", ct))
        {
            if (Marker().Match(issue.Body ?? string.Empty) is { Success: true } m)
            {
                issues[m.Groups[1].Value] = issue;
            }
        }

        var pulls = await Paged<Pull>($"repos/{repo}/pulls?state=open", ct, pages: 2);
        int created = 0, updated = 0, closed = 0, overdue = 0;
        foreach (var finding in findings)
        {
            var renovate = pulls.FirstOrDefault(p => finding.Source == "dotnet"
                ? p.Title.Contains(".NET servicing", StringComparison.OrdinalIgnoreCase)
                : p.Title.Contains(finding.Package, StringComparison.OrdinalIgnoreCase));
            if (!issues.TryGetValue(finding.Key, out var issue))
            {
                var wanted = new[] { Label, $"severidad/{finding.Severity}", $"linea/{finding.Line}" };
                var ids = new List<long>();
                foreach (var name in wanted)
                {
                    ids.Add(await Ensure(labels, name, ct));
                }

                var body = Body(finding, renovate, Watch.Deadline(finding, now));
                await Send(HttpMethod.Post, $"repos/{repo}/issues", new { title = $"[vigía] {finding.Id} en {finding.Package} ({finding.Line})", body, labels = ids }, ct);
                created++;
                continue;
            }

            var deadline = Watch.Deadline(finding, issue.CreatedAt);
            var current = Body(finding, renovate, deadline);
            if (current != issue.Body)
            {
                await Send(HttpMethod.Patch, $"repos/{repo}/issues/{issue.Number}", new { body = current }, ct);
                updated++;
            }

            if (deadline is { } due && now > due && !issue.Labels.Any(l => l.Name == OverdueLabel))
            {
                await Send(HttpMethod.Post, $"repos/{repo}/issues/{issue.Number}/labels", new { labels = new[] { await Ensure(labels, OverdueLabel, ct) } }, ct);
                var mentions = maintainers.Count == 0 ? string.Empty : " " + string.Join(" ", maintainers.Select(m => "@" + m));
                await Send(HttpMethod.Post, $"repos/{repo}/issues/{issue.Number}/comments", new { body = $"Venció el plazo objetivo ({due:yyyy-MM-dd HH:mm} UTC) sin versión publicada.{mentions}" }, ct);
                overdue++;
            }
        }

        var keys = findings.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, issue) in issues.Where(i => i.Key != StatusKey && !keys.Contains(i.Key)))
        {
            await Send(HttpMethod.Post, $"repos/{repo}/issues/{issue.Number}/comments", new { body = $"Ya no aparece en la corrida del {now:yyyy-MM-dd}: se cierra." }, ct);
            await Send(HttpMethod.Patch, $"repos/{repo}/issues/{issue.Number}", new { state = "closed" }, ct);
            closed++;
        }

        var status = Status(findings, lines, now);
        long statusNumber;
        if (issues.TryGetValue(StatusKey, out var existing))
        {
            statusNumber = existing.Number;
            await Send(HttpMethod.Patch, $"repos/{repo}/issues/{existing.Number}", new { body = status }, ct);
        }
        else
        {
            var issue = await Send<Issue>(HttpMethod.Post, $"repos/{repo}/issues", new { title = "Vigía: estado de las líneas soportadas", body = status, labels = new[] { await Ensure(labels, Label, ct) } }, ct);
            statusNumber = issue.Number;

            // Fijarlo es una comodidad: una versión de Forgejo sin esta API no frena al vigía.
            using var pin = await http.PostAsync(new Uri($"repos/{repo}/issues/{issue.Number}/pin", UriKind.Relative), null, ct);
        }

        return new SyncResult(created, updated, closed, overdue, statusNumber);
    }

    /// <summary>El cuerpo del issue fijado: la fecha de la última corrida en una línea fácil de leer para el monitoreo.</summary>
    public static string Status(IReadOnlyList<Finding> findings, IReadOnlyList<string> lines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(lines);
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<!-- vigia:{StatusKey} -->\n")
            .Append(CultureInfo.InvariantCulture, $"Última corrida: {now.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\n\n")
            .Append("| Línea | Hallazgos | Altos o críticos |\n|---|---|---|\n");
        foreach (var line in lines)
        {
            var mine = findings.Where(f => f.Line == line).ToList();
            text.Append(CultureInfo.InvariantCulture, $"| {line} | {mine.Count} | {mine.Count(f => Watch.Rank(f.Severity) >= 2)} |\n");
        }

        return text.Append("\nLo actualiza el vigía diario (D-20). Si esta fecha tiene más de un día, el vigía no está corriendo.\n").ToString();
    }

    private static string Body(Finding finding, Pull? renovate, DateTimeOffset? deadline)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<!-- vigia:{finding.Key} -->\n")
            .Append(CultureInfo.InvariantCulture, $"**{finding.Package} {finding.Version}** en la línea `{finding.Line}`: {finding.Id}, severidad {finding.Severity}.\n\n")
            .Append(CultureInfo.InvariantCulture, $"- Lo vio: {finding.Source}\n")
            .Append(CultureInfo.InvariantCulture, $"- Arreglo: {finding.Fixed ?? "sin versión con arreglo todavía"}\n")
            .Append(CultureInfo.InvariantCulture, $"- Plazo objetivo: {(deadline is { } d ? d.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "la próxima versión")}\n")
            .Append(CultureInfo.InvariantCulture, $"- PR de Renovate: {(renovate is null ? "ninguno todavía" : $"#{renovate.Number} {renovate.Title}")}\n");
        if (finding.References.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"- Referencias: {string.Join(" ", finding.References)}\n");
        }

        return text.Append("\nLo abrió el vigía diario (D-20). Se cierra solo cuando el hallazgo deja de aparecer.\n").ToString();
    }

    private async Task<Dictionary<string, long>> Labels(CancellationToken ct) =>
        (await Paged<RepoLabel>($"repos/{repo}/labels", ct)).ToDictionary(l => l.Name, l => l.Id, StringComparer.Ordinal);

    private async Task<long> Ensure(Dictionary<string, long> labels, string name, CancellationToken ct)
    {
        if (!labels.TryGetValue(name, out var id))
        {
            var color = name == OverdueLabel || name.EndsWith("critical", StringComparison.Ordinal) ? "#b42318" : name.StartsWith("severidad/", StringComparison.Ordinal) ? "#d97706" : "#1a5a9e";
            id = (await Send<RepoLabel>(HttpMethod.Post, $"repos/{repo}/labels", new { name, color }, ct)).Id;
            labels[name] = id;
        }

        return id;
    }

    private async Task<List<T>> Paged<T>(string path, CancellationToken ct, int pages = 20)
    {
        var all = new List<T>();
        for (var page = 1; page <= pages; page++)
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var batch = await http.GetFromJsonAsync<List<T>>(new Uri($"{path}{separator}limit=50&page={page}", UriKind.Relative), Watch.Json, ct) ?? [];
            all.AddRange(batch);
            if (batch.Count < 50)
            {
                break;
            }
        }

        return all;
    }

    private async Task Send(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var response = await SendRaw(method, path, body, ct);
    }

    private async Task<T> Send<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var response = await SendRaw(method, path, body, ct);
        return await response.Content.ReadFromJsonAsync<T>(Watch.Json, ct) ?? throw new UpdateException($"Forgejo no devolvió nada en {path}.");
    }

    private async Task<HttpResponseMessage> SendRaw(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: Watch.Json) };
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new UpdateException($"Forgejo contestó {(int)response.StatusCode} en {method} {path}: {detail}");
        }

        return response;
    }

    [GeneratedRegex(@"<!-- vigia:(\S+) -->", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    private sealed record RepoLabel(long Id, string Name);

    private sealed record Issue(long Number, string? Body, [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt, IReadOnlyList<RepoLabel> Labels);

    private sealed record Pull(long Number, string Title);
}
