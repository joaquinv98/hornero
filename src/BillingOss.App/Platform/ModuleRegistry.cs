using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Platform;

/// <summary>Una instancia lista para llamar: destino, sobre de la instancia y manifiesto.</summary>
public sealed record ResolvedInstance(ModuleEndpoint Endpoint, ModuleInstance Instance, JsonObject Manifest, InstanceData Data)
{
    public TimeSpan Timeout(string operation) =>
        TimeSpan.FromSeconds(Math.Clamp(Manifest["timeouts"]?[operation]?.GetValue<int>() ?? 30, 1, 120));

    /// <summary>El módulo manual corre dentro del núcleo: no hay firma ni HTTP.</summary>
    public bool InCore => Endpoint.BaseUrl.OriginalString == ManualModule.Address;
}

/// <summary>
/// Carga instalaciones e instancias de módulos, las resuelve con sus secretos descifrados y guarda su estado
/// (secciones 4 y 6 del protocolo). El secreto de firma, los secretos y el estado de cada instancia van cifrados
/// atados a la URL de la instalación: cambiarla exige volver a cargarlos con <see cref="ChangeBaseUrl"/>.
/// </summary>
public sealed class ModuleRegistry(BillingDbContext db, SecretBox box, TimeProvider time)
{
    private const string InstallationsTable = "platform.module_installations";
    private const string InstancesTable = "platform.module_instances";

    public async Task<ResolvedInstance> Resolve(long instanceId, CancellationToken ct)
    {
        var instance = await db.ModuleInstances.SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
        var installation = await db.ModuleInstallations.SingleAsync(i => i.Id == instance.InstallationId, ct).ConfigureAwait(false);
        if (installation.BaseUrl.Length == 0)
        {
            // Instalación creada por el importador para un módulo que todavía no se desplegó, por ejemplo internetbs.
            throw new PermanentFailureException($"La instalación {installation.Name} no tiene URL: falta desplegar el módulo.");
        }

        // El módulo manual corre dentro del núcleo: no tiene URL de red ni secreto de firma.
        var inCore = installation.BaseUrl == ManualModule.Address;
        var url = inCore
            ? new Uri(ManualModule.Address)
            : ModuleEndpoint.TryParseBaseUrl(installation.BaseUrl)
                ?? throw new PermanentFailureException($"La instalación {installation.Name} tiene una URL que no se acepta ({installation.BaseUrl}). {ModuleEndpoint.BaseUrlRule}");
        var destination = url.AbsoluteUri;
        var secret = inCore ? string.Empty : Open(installation.SigningSecret, InstallationsTable, "signing_secret", installation.Id);
        var endpoint = new ModuleEndpoint(url, Encoding.UTF8.GetBytes(secret), installation.TlsFingerprint);
        var config = InstanceConfig.Merge(instance.Config, instance.Secrets is null ? null : Open(instance.Secrets, InstancesTable, "secrets", instance.Id));
        var state = instance.State is null ? null : JsonNode.Parse(Open(instance.State, InstancesTable, "state", instance.Id));
        var manifest = JsonNode.Parse(installation.Manifest)?.AsObject() ?? [];
        return new ResolvedInstance(endpoint, instance, manifest, new InstanceData(instance.Id.ToString(CultureInfo.InvariantCulture), config, state));

        string Open(byte[] blob, string table, string column, long rowId)
        {
            try
            {
                return box.Decrypt(blob, table, column, rowId, destination);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new PermanentFailureException(
                    $"Los secretos de la instalación {installation.Name} no descifran para {destination}: si cambió la URL hay que volver a cargar el secreto de firma y los secretos de sus instancias (ChangeBaseUrl).");
            }
        }
    }

    /// <summary>
    /// Guarda el estado con control de versión: si otra llamada lo cambió antes, gana esa. Devuelve la instancia
    /// lista para la próxima llamada de la misma corrida, con el estado nuevo en el sobre.
    /// </summary>
    public async Task<ResolvedInstance> SaveState(ResolvedInstance resolved, JsonNode state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(state);
        var instance = resolved.Instance;
        var blob = box.Encrypt(state.ToJsonString(), InstancesTable, "state", instance.Id, resolved.Endpoint.BaseUrl.AbsoluteUri);

        // En la transacción del contexto, o en una propia: así pasa por la cerca del trabajo y por la guarda del COMMIT.
        int updated;
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await using (own)
        {
            updated = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE platform.module_instances SET state = {blob}, state_version = state_version + 1 WHERE id = {instance.Id} AND state_version = {instance.StateVersion}",
                ct).ConfigureAwait(false);
            if (own is not null)
            {
                await own.CommitAsync(ct).ConfigureAwait(false);
            }
        }

        if (updated == 1)
        {
            // La versión nueva ya está en la base: se ajusta también el original para que EF no la vuelva a escribir.
            var version = db.Entry(instance).Property(i => i.StateVersion);
            version.OriginalValue = version.CurrentValue = instance.StateVersion + 1;
        }

        return resolved with { Data = resolved.Data with { State = state.DeepClone() } };
    }

    /// <summary>Carga una instalación: valida la URL y deja el secreto de firma cifrado y atado a ella.</summary>
    public async Task<ModuleInstallation> Install(string name, string baseUrl, string signingSecret, JsonObject manifest, string? tlsFingerprint = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var url = ParseBaseUrl(baseUrl);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var installation = new ModuleInstallation { Name = name, BaseUrl = url.AbsoluteUri, TlsFingerprint = tlsFingerprint, Manifest = manifest.ToJsonString(), CreatedAt = time.GetUtcNow() };
        db.ModuleInstallations.Add(installation);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        installation.SigningSecret = box.Encrypt(signingSecret, InstallationsTable, "signing_secret", installation.Id, url.AbsoluteUri);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return installation;
    }

    /// <summary>Carga una instancia de una instalación, con sus campos x-secret cifrados y atados a la URL de la instalación.</summary>
    public async Task<ModuleInstance> AddInstance(long installationId, string name, string config = "{}", string? secrets = null, CancellationToken ct = default)
    {
        var installation = await db.ModuleInstallations.SingleAsync(i => i.Id == installationId, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var instance = new ModuleInstance { InstallationId = installationId, Name = name, Config = config, CreatedAt = time.GetUtcNow() };
        db.ModuleInstances.Add(instance);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (secrets is not null)
        {
            instance.Secrets = box.Encrypt(secrets, InstancesTable, "secrets", instance.Id, installation.BaseUrl);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return instance;
    }

    /// <summary>
    /// Reemplaza la configuración de una instancia: los campos visibles y, si vienen, los x-secret, cifrados y atados a
    /// la URL de la instalación. Sin <paramref name="secrets"/> los guardados no se tocan.
    /// </summary>
    public async Task ReplaceConfig(long instanceId, string config, string? secrets, CancellationToken ct = default)
    {
        var instance = await db.ModuleInstances.SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
        var installation = await db.ModuleInstallations.AsNoTracking().SingleAsync(i => i.Id == instance.InstallationId, ct).ConfigureAwait(false);
        instance.Config = config;
        if (secrets is not null)
        {
            instance.Secrets = box.Encrypt(secrets, InstancesTable, "secrets", instance.Id, installation.BaseUrl);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Vuelve a cargar el secreto de firma y la huella de una instalación con la misma URL, por ejemplo al rotarlos.</summary>
    public async Task Rekey(long installationId, string signingSecret, string? tlsFingerprint, CancellationToken ct = default)
    {
        var installation = await db.ModuleInstallations.SingleAsync(i => i.Id == installationId, ct).ConfigureAwait(false);
        installation.SigningSecret = box.Encrypt(signingSecret, InstallationsTable, "signing_secret", installation.Id, installation.BaseUrl);
        installation.TlsFingerprint = tlsFingerprint;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cambia la URL de una instalación. Exige volver a cargar el secreto de firma y los secretos de cada instancia
    /// que los tiene (sección 4 del protocolo), y descarta el estado de las instancias, que era del destino anterior.
    /// </summary>
    public async Task ChangeBaseUrl(long installationId, string baseUrl, string signingSecret, IReadOnlyDictionary<long, string> instanceSecrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instanceSecrets);
        var url = ParseBaseUrl(baseUrl);
        var installation = await db.ModuleInstallations.SingleAsync(i => i.Id == installationId, ct).ConfigureAwait(false);
        var instances = await db.ModuleInstances.Where(i => i.InstallationId == installationId).ToListAsync(ct).ConfigureAwait(false);
        var missing = instances.Where(i => i.Secrets is not null && !instanceSecrets.ContainsKey(i.Id)).Select(i => i.Id).ToList();
        if (missing.Count > 0)
        {
            throw new ValidationException($"Para cambiar la URL hay que volver a cargar los secretos de las instancias {string.Join(", ", missing)}.");
        }

        installation.BaseUrl = url.AbsoluteUri;
        installation.SigningSecret = box.Encrypt(signingSecret, InstallationsTable, "signing_secret", installation.Id, url.AbsoluteUri);
        foreach (var instance in instances)
        {
            instance.Secrets = instanceSecrets.TryGetValue(instance.Id, out var secrets) ? box.Encrypt(secrets, InstancesTable, "secrets", instance.Id, url.AbsoluteUri) : null;
            instance.State = null;
            instance.StateVersion++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reemplaza los secretos de una instancia sin cambiar la URL de la instalación: es lo que hace activar una
    /// credencial fiscal. Con <paramref name="state"/> deja también el estado que devolvió la prueba de la credencial
    /// nueva, y sin él descarta el anterior, que era del certificado viejo. Quien llama lo deja en la auditoría.
    /// </summary>
    public async Task ReplaceSecrets(long instanceId, string secrets, JsonNode? state = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var instance = await db.ModuleInstances.SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
        var installation = await db.ModuleInstallations.SingleAsync(i => i.Id == instance.InstallationId, ct).ConfigureAwait(false);
        instance.Secrets = box.Encrypt(secrets, InstancesTable, "secrets", instance.Id, installation.BaseUrl);
        instance.State = state is null ? null : box.Encrypt(state.ToJsonString(), InstancesTable, "state", instance.Id, installation.BaseUrl);
        instance.StateVersion++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cambia la configuración visible de una instancia. El ambiente de una instancia fiscal no cambia: homologación y
    /// producción no comparten emisor, series ni comprobantes (sección 2.10 del diseño fiscal). Para el otro ambiente
    /// se crea otra instancia.
    /// </summary>
    public async Task ChangeConfig(long instanceId, string config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var instance = await db.ModuleInstances.SingleAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
        var current = JsonNode.Parse(instance.Config)?["environment"]?.ToString();
        var next = JsonNode.Parse(config)?["environment"]?.ToString();
        if (current is not null && next != current)
        {
            throw new ValidationException($"El ambiente de la instancia {instance.Name} es {current} y no se cambia: para {next ?? "otro ambiente"} se crea otra instancia.");
        }

        instance.Config = config;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static Uri ParseBaseUrl(string baseUrl) =>
        ModuleEndpoint.TryParseBaseUrl(baseUrl) ?? throw new ValidationException($"{ModuleEndpoint.BaseUrlRule} Recibida: {baseUrl}");
}
