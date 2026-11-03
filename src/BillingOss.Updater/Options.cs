using System.Text.Json;
using System.Text.Json.Serialization;
using BillingOss.Updates;

namespace BillingOss.Updater;

/// <summary>
/// Configuración del host, en /etc/billing-oss/actualizador.json: la URL del canal y las llaves van acá y no en la base,
/// así una web comprometida no las cambia (sección 7.3).
/// </summary>
public sealed record UpdaterOptions
{
    /// <summary>Nombre del nodo: uno por host, el mismo en cada arranque.</summary>
    public string Node { get; init; } = Environment.MachineName.ToLowerInvariant();

    /// <summary>La carpeta de la receta: compose.yaml, .env y secrets.</summary>
    public string ComposeDirectory { get; init; } = "/opt/billing-oss/deploy/compose";

    /// <summary>Cadena de conexión del rol dueño, desde el host (secrets/db_actualizador, que arma preparar.sh).</summary>
    public string? ConnectionStringFile { get; init; }

    /// <summary>Para pruebas: la cadena en claro.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Sin URL, el chequeo está apagado y el canal se carga a mano con check --file.</summary>
    public Uri? ChannelUrl { get; init; }

    /// <summary>La firma de cosign sign-blob. Por defecto, la URL del canal con ".sig".</summary>
    public Uri? SignatureUrl { get; init; }

    /// <summary>PEM de las llaves públicas del canal: la vigente y la siguiente, en ese orden.</summary>
    public IReadOnlyList<string> PublicKeys { get; init; } = [];

    public string Docker { get; init; } = "docker";

    /// <summary>Listo del nodo: el actualizador espera a que dé 2xx después de recrear.</summary>
    public Uri HealthUrl { get; init; } = new("http://127.0.0.1:8080/health/ready");

    /// <summary>Pruebas de humo de solo lectura, después del período de prueba.</summary>
    public IReadOnlyList<Uri> SmokeUrls { get; init; } = [new("http://127.0.0.1:8080/health/live")];

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Período de prueba de cada nodo antes de pasar al siguiente.</summary>
    public TimeSpan SoakPeriod { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Cuánto se espera a que todos los nodos bajen las imágenes.</summary>
    public TimeSpan PullTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>dump (receta chica), restore-point (PITR con pgBackRest) o none.</summary>
    public string RecoveryMark { get; init; } = "dump";

    public string DumpDirectory { get; init; } = "/var/lib/billing-oss/marcas";

    public string Database { get; init; } = "billing_oss";

    /// <summary>En HA, la réplica sincrónica tiene que estar presente antes de tocar nada (D-14).</summary>
    public bool RequireSyncReplica { get; init; }

    public string DiskPath { get; init; } = "/var/lib/docker";

    public long MinFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public Version MinDockerVersion { get; init; } = new(24, 0);

    /// <summary>El binario que se reemplaza al autoactualizarse. Por defecto, el que corre.</summary>
    public string? BinaryPath { get; init; } = Environment.ProcessPath;

    public string ResolveConnectionString() =>
        ConnectionString
        ?? (File.Exists(ConnectionStringFile ?? Path.Combine(ComposeDirectory, "secrets", "db_actualizador"))
            ? File.ReadAllText(ConnectionStringFile ?? Path.Combine(ComposeDirectory, "secrets", "db_actualizador")).Trim()
            : throw new UpdateException("Falta la cadena de conexión del actualizador (ConnectionStringFile)."));

    /// <summary>Lee {"Updater": {...}} o el objeto solo.</summary>
    public static UpdaterOptions Load(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement.TryGetProperty("Updater", out var section) ? section : doc.RootElement;
        return root.Deserialize<UpdaterOptions>(Json) ?? new UpdaterOptions();
    }

    /// <summary>La sección se vuelve a leer desde su texto, con los comentarios adentro: también acá se saltean.</summary>
    private static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
