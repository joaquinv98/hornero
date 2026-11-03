using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Platform;

/// <summary>Un módulo que trae la receta de despliegue (sección Modules:Included:{nombre}).</summary>
public sealed class IncludedModule
{
    /// <summary>URL base, por ejemplo https://stripe:8443/.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>El secreto de firma. En la receta llega como archivo de /run/secrets.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>Archivo PEM con el certificado TLS del módulo: el núcleo fija su huella.</summary>
    public string? Certificate { get; set; }
}

/// <summary>
/// Alta automática de los módulos incluidos (sección 4.2 del diseño de cobros). En migrate crea o actualiza cada
/// instalación con su URL, secreto de firma y huella, y lee su manifiesto: el staff nunca copia huellas ni secretos de
/// firma. Un manifiesto que cambia esquemas o pide más datos personales queda pendiente de aprobación y mientras tanto
/// vale el aceptado (sección 5 del protocolo). Un módulo que no responde no frena las migraciones: se intenta en la
/// próxima.
/// </summary>
public sealed class IncludedModules(BillingDbContext db, ModuleRegistry registry, ModuleClient client, ILogger<IncludedModules> logger)
{
    /// <summary>Intentos por módulo: recién arrancado, un contenedor puede tardar unos segundos en escuchar.</summary>
    public const int Attempts = 3;

    public Task Sync(IReadOnlyDictionary<string, IncludedModule> modules, CancellationToken ct = default) =>
        Sync(modules, TimeSpan.FromSeconds(5), ct);

    public async Task Sync(IReadOnlyDictionary<string, IncludedModule> modules, TimeSpan retryDelay, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var (name, module) in modules.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await SyncOne(name, module, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt < Attempts && ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UseCases.PermanentFailureException or UseCases.ValidationException or IOException or CryptographicException)
                {
                    logger.LogWarning("No se pudo dar de alta el módulo incluido {Name}: {Error}", name, ex.Message);
                    break;
                }
            }
        }
    }

    /// <summary>El manifiesto nuevo cambia algún esquema o pide un dato personal más que el aceptado.</summary>
    public static bool NeedsApproval(JsonObject accepted, JsonObject candidate)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(candidate);
        if (new[] { "config_schema", "product_schema", "order_schema" }.Any(s => !JsonNode.DeepEquals(accepted[s], candidate[s])))
        {
            return true;
        }

        return Strings(candidate["data_scopes"]).Except(Strings(accepted["data_scopes"]), StringComparer.Ordinal).Any();

        static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray a ? a.Select(x => x?.GetValue<string>()).OfType<string>() : [];
    }

    /// <summary>Huella SHA-256 de un certificado PEM, como la imprime deploy/certificados.sh.</summary>
    public static string Fingerprint(string pem)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        return Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));
    }

    private async Task SyncOne(string name, IncludedModule module, CancellationToken ct)
    {
        var url = ModuleEndpoint.TryParseBaseUrl(module.Url) ?? throw new UseCases.ValidationException($"{ModuleEndpoint.BaseUrlRule} Recibida: {module.Url}");
        var secret = module.SigningSecret?.Trim();
        if (string.IsNullOrEmpty(secret))
        {
            throw new UseCases.ValidationException("Falta el secreto de firma.");
        }

        var fingerprint = module.Certificate is { Length: > 0 } path ? Fingerprint(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) : null;
        var manifest = await client.Manifest(new ModuleEndpoint(url, Encoding.UTF8.GetBytes(secret), fingerprint), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var installation = await db.ModuleInstallations.SingleOrDefaultAsync(i => i.Name == name, ct).ConfigureAwait(false);
        if (installation is null)
        {
            await registry.Install(name, url.AbsoluteUri, secret, manifest, fingerprint, ct).ConfigureAwait(false);
            logger.LogInformation("Módulo incluido {Name} dado de alta en {Url}.", name, url);
            return;
        }

        if (installation.BaseUrl != url.AbsoluteUri)
        {
            // Cambiar la URL exige volver a cargar los secretos de cada instancia: lo hace el staff.
            logger.LogWarning("El módulo incluido {Name} cambió de URL ({Old} a {New}): no se toca. Lo cambia el staff volviendo a cargar sus secretos.", name, installation.BaseUrl, url);
            return;
        }

        await registry.Rekey(installation.Id, secret, fingerprint, ct).ConfigureAwait(false);
        var accepted = JsonNode.Parse(installation.Manifest) as JsonObject ?? [];
        if (NeedsApproval(accepted, manifest))
        {
            installation.PendingManifest = manifest.ToJsonString();
            logger.LogWarning("El manifiesto nuevo del módulo {Name} cambia esquemas o pide más datos: espera la aprobación del staff.", name);
        }
        else
        {
            installation.Manifest = manifest.ToJsonString();
            installation.PendingManifest = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
