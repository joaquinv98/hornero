using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BillingOss.Updates;

namespace BillingOss.Updater;

/// <summary>Un comando, siempre como lista de argumentos: nunca pasa por un shell.</summary>
public sealed record Command(IReadOnlyList<string> Args, string? StdoutFile = null, IReadOnlyDictionary<string, string>? Env = null);

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Variante de Compose (sección 8): el actualizador escribe su propio compose.version.yaml con los digests del canal, solo
/// con image: y solo de servicios que ya están en la receta. Nunca aplica un Compose bajado: uno firmado igual podría
/// montar el socket o el disco del host. El .env sigue siendo del proveedor.
/// </summary>
public sealed partial class ComposeHost(UpdaterOptions options, Func<Command, CancellationToken, Task<CommandResult>> run)
{
    public const string FileName = "compose.version.yaml";

    private const string MigrateFile = "compose.migrate.yaml";

    private string Dir => options.ComposeDirectory;

    private string VersionFile => Path.Combine(Dir, FileName);

    /// <summary>Lo que corre este nodo: la versión y las imágenes que dejó el actualizador o, antes, BILLING_VERSION del .env.</summary>
    public (Version? Version, Dictionary<string, string>? Images) Current()
    {
        if (!File.Exists(VersionFile))
        {
            return (ReadEnv().TryGetValue("BILLING_VERSION", out var tag) && Versions.TryParse(tag, out var fromEnv) ? fromEnv : null, null);
        }

        Version? version = null;
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        string? service = null;
        foreach (var line in File.ReadAllLines(VersionFile))
        {
            if (line.StartsWith("# version: ", StringComparison.Ordinal) && Versions.TryParse(line[11..].Trim(), out var v))
            {
                version = v;
            }
            else if (ServiceLine().Match(line) is { Success: true } s)
            {
                service = s.Groups[1].Value;
            }
            else if (service is not null && ImageLine().Match(line) is { Success: true } i)
            {
                images[service] = i.Groups[1].Value;
            }
        }

        return (version, images.Count == 0 ? null : images);
    }

    /// <summary>Los servicios de la receta, tal como los arma Compose con los archivos del proveedor.</summary>
    public async Task<IReadOnlyList<string>> Services(CancellationToken ct)
    {
        var result = await Docker(ct, "compose", "config", "--services");
        if (!result.Ok)
        {
            throw new UpdateException($"docker compose config falló: {Tail(result.Output)}");
        }

        return [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => ServiceName().IsMatch(s)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// La imagen de cada servicio de la receta que la versión trae. PostgreSQL, solo la de la major que ya corre: los datos
    /// de una 17 no arrancan con una 18.
    /// </summary>
    public static Dictionary<string, string> ImagesFor(IEnumerable<string> services, ReleaseImages images, int? postgresMajor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(images);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            var image = service == "postgres" ? postgresMajor is { } major ? images.PostgresFor(major) : null : images.For(service);
            if (image is not null)
            {
                result[service] = ImageRefs.IsDigest(image) ? image : throw new UpdateException($"La imagen de {service} no va por digest.");
            }
        }

        return result;
    }

    public async Task Pull(IEnumerable<string> images, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);
        foreach (var image in images.Distinct(StringComparer.Ordinal))
        {
            var result = await Docker(ct, "pull", "--quiet", ImageRefs.IsDigest(image) ? image : throw new UpdateException("Solo se baja por digest."));
            if (!result.Ok)
            {
                throw new UpdateException($"No se pudo bajar {image}: {Tail(result.Output)}");
            }
        }
    }

    /// <summary>
    /// Escribe compose.version.yaml. Sin imágenes queda vacío y la receta vuelve a las etiquetas del .env, que es como
    /// arrancó la instalación. La primera vez lo suma a COMPOSE_FILE del .env.
    /// </summary>
    public void Write(Version? version, IReadOnlyDictionary<string, string>? images)
    {
        var temp = VersionFile + ".nuevo";
        File.WriteAllText(temp, Yaml(version, images));
        File.Move(temp, VersionFile, overwrite: true);
        IncludeInEnv();
    }

    /// <summary>Recrea lo que cambió. PostgreSQL primero, si cambia, para que el núcleo arranque contra la base nueva.</summary>
    public async Task Up(bool postgresFirst, CancellationToken ct)
    {
        if (postgresFirst)
        {
            var db = await Docker(ct, "compose", "up", "-d", "postgres");
            if (!db.Ok)
            {
                throw new UpdateException($"No arrancó PostgreSQL: {Tail(db.Output)}");
            }
        }

        var result = await Docker(ct, "compose", "up", "-d");
        if (!result.Ok)
        {
            throw new UpdateException($"docker compose up falló: {Tail(result.Output)}");
        }
    }

    /// <summary>
    /// Las migraciones de la versión nueva, una vez y con el rol dueño: el servicio migrate de la receta con la imagen
    /// nueva en un archivo aparte, solo para esta corrida. compose.version.yaml no cambia hasta el turno del nodo.
    /// </summary>
    public async Task<CommandResult> Migrate(string nucleo, CancellationToken ct)
    {
        var file = Path.Combine(Dir, MigrateFile);
        File.WriteAllText(file, Yaml(null, new Dictionary<string, string>(StringComparer.Ordinal) { ["migrate"] = nucleo }));
        try
        {
            var files = ComposeFiles().Where(f => f != FileName || File.Exists(VersionFile)).Append(MigrateFile);
            var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["COMPOSE_FILE"] = string.Join(Separator(), files) };
            return await run(new Command([options.Docker, "compose", "run", "--rm", "migrate"], Env: env), ct);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public async Task<string> Logs(CancellationToken ct)
    {
        var result = await Docker(ct, "compose", "logs", "--no-color", "--tail", "40", "web", "worker");
        return Tail(result.Output, 1500);
    }

    /// <summary>Marca de recuperación de la receta chica: pg_dump en formato custom, como respaldo.sh.</summary>
    public async Task Dump(string file, string database, CancellationToken ct)
    {
        var result = await run(new Command([options.Docker, "compose", "exec", "-T", "-u", "postgres", "postgres", "pg_dump", "-Fc", "-d", database], file), ct);
        if (!result.Ok)
        {
            File.Delete(file);
            throw new UpdateException($"pg_dump falló: {Tail(result.Output)}");
        }
    }

    public async Task<Version?> DockerVersion(CancellationToken ct)
    {
        var result = await Docker(ct, "version", "--format", "{{.Server.Version}}");
        return result.Ok && Version.TryParse(result.Output.Trim().Split('-')[0], out var version) ? version : null;
    }

    /// <summary>Corre un proceso sin shell, en la carpeta de la receta, con salida a un archivo si se pide.</summary>
    public static Func<Command, CancellationToken, Task<CommandResult>> Processes(string workingDirectory, TimeSpan timeout) => async (command, ct) =>
    {
        ArgumentNullException.ThrowIfNull(command);
        var start = new ProcessStartInfo(command.Args[0])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in command.Args.Skip(1))
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in command.Env ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start) ?? throw new UpdateException($"No arrancó {command.Args[0]}.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            var output = string.Empty;
            if (command.StdoutFile is { } file)
            {
                // Solo el dueño lee la marca: trae todos los datos.
                await using var target = PrivateFile.Create(file, executable: false);
                await process.StandardOutput.BaseStream.CopyToAsync(target, limit.Token);
            }
            else
            {
                output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            }

            await process.WaitForExitAsync(limit.Token);
            return new CommandResult(process.ExitCode, output + await error);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    };

    public static string Tail(string text, int max = 500)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : "…" + trimmed[^max..];
    }

    private static string Yaml(Version? version, IReadOnlyDictionary<string, string>? images)
    {
        var yaml = new StringBuilder()
            .Append("# Lo escribe el actualizador de billing-oss (D-20) con los digests del canal verificado. No se edita a mano:\n")
            .Append("# se reescribe en cada actualización.\n");
        if (version is not null)
        {
            yaml.Append("# version: ").Append(version.Text()).Append('\n');
        }

        if (images is null || images.Count == 0)
        {
            return yaml.Append("services: {}\n").ToString();
        }

        yaml.Append("services:\n");
        foreach (var (service, image) in images.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            // Nombre y valor con juego de caracteres cerrado: no pueden sumar otra clave al YAML.
            if (!ServiceName().IsMatch(service) || !ImageRefs.IsDigest(image))
            {
                throw new UpdateException($"Servicio o imagen inválidos: «{service}».");
            }

            yaml.Append("  ").Append(service).Append(":\n    image: \"").Append(image).Append("\"\n");
        }

        return yaml.ToString();
    }

    private Task<CommandResult> Docker(CancellationToken ct, params string[] args) => run(new Command([options.Docker, .. args]), ct);

    private Dictionary<string, string> ReadEnv()
    {
        var file = Path.Combine(Dir, ".env");
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(file))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && !line.TrimStart().StartsWith('#'))
                {
                    env[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
                }
            }
        }

        return env;
    }

    private string Separator() => ReadEnv().TryGetValue("COMPOSE_PATH_SEPARATOR", out var sep) && sep.Length > 0 ? sep : ":";

    /// <summary>Los archivos de la receta: COMPOSE_FILE del .env o, sin él, compose.yaml y el override si existe.</summary>
    private List<string> ComposeFiles() =>
        ReadEnv().TryGetValue("COMPOSE_FILE", out var list)
            ? [.. list.Split(Separator(), StringSplitOptions.RemoveEmptyEntries)]
            : File.Exists(Path.Combine(Dir, "compose.override.yaml")) ? ["compose.yaml", "compose.override.yaml"] : ["compose.yaml"];

    /// <summary>Suma compose.version.yaml a COMPOSE_FILE una sola vez, conservando los archivos que ya usa el proveedor.</summary>
    private void IncludeInEnv()
    {
        var files = ComposeFiles();
        if (files.Contains(FileName, StringComparer.Ordinal))
        {
            return;
        }

        var file = Path.Combine(Dir, ".env");
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var entry = "COMPOSE_FILE=" + string.Join(Separator(), files.Append(FileName));
        var index = lines.FindIndex(l => l.TrimStart().StartsWith("COMPOSE_FILE=", StringComparison.Ordinal));
        if (index >= 0)
        {
            lines[index] = entry;
        }
        else
        {
            lines.Add(entry);
        }

        File.WriteAllLines(file, lines);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^  ([a-z0-9][a-z0-9_.-]{0,62}):\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceLine();

    [GeneratedRegex(@"^    image: ""([^""]+)""\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageLine();
}

/// <summary>Archivos y carpetas que solo lee el dueño: la marca trae todos los datos y el binario es root en el nodo.</summary>
internal static class PrivateFile
{
    public static FileStream Create(string path, bool executable)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write);
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | (executable ? UnixFileMode.UserExecute : UnixFileMode.None);
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = mode });
    }

    public static void Directory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else
        {
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// <summary>Imágenes por servicio, como las guarda la base.</summary>
internal static class ImageMap
{
    public static string? ToJson(IReadOnlyDictionary<string, string>? images) => images is null ? null : JsonSerializer.Serialize(images);

    public static Dictionary<string, string>? FromJson(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
}
