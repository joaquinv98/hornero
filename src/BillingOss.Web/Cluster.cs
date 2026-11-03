using System.Xml.Linq;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BillingOss.Web;

/// <summary>Lo que necesita un proceso para correr al lado de otros iguales (docs/alta-disponibilidad).</summary>
public static class Cluster
{
    /// <summary>
    /// Data Protection compartido por todos los nodos web (D-09): mismo nombre de aplicación y anillo de llaves en
    /// platform.data_protection_keys, cifrado con la llave maestra. Así una cookie o un formulario que emitió un nodo
    /// lo acepta cualquier otro, y un backup de la base no alcanza para falsificarlos.
    /// </summary>
    public static IServiceCollection AddBillingDataProtection(this IServiceCollection services, SecretBox secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        services.AddDataProtection().SetApplicationName("billing-oss");
        services.AddOptions<KeyManagementOptions>().Configure<IServiceScopeFactory>((o, scopes) =>
        {
            o.XmlRepository = new DatabaseXmlRepository(scopes);
            o.XmlEncryptor = new SecretBoxXmlEncryptor(secrets);
        });
        return services;
    }
}

/// <summary>El anillo de llaves en la base primaria. Data Protection lo llama sin async.</summary>
internal sealed class DatabaseXmlRepository(IServiceScopeFactory scopes) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return [.. db.DataProtectionKeys.AsNoTracking().OrderBy(k => k.Id).Select(k => k.Xml).AsEnumerable().Select(x => XElement.Parse(x))];
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        db.DataProtectionKeys.Add(new DataProtectionKey(friendlyName, element.ToString(SaveOptions.DisableFormatting)));
        db.SaveChanges();
    }
}

/// <summary>
/// Cifra el secreto de cada llave de Data Protection con SecretBox. Hace falta porque al elegir dónde se guardan las
/// llaves, ASP.NET deja de cifrarlas por su cuenta.
/// </summary>
public sealed class SecretBoxXmlEncryptor(SecretBox box) : IXmlEncryptor
{
    internal const string Table = "platform.data_protection_keys";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var blob = box.Encrypt(plaintextElement.ToString(SaveOptions.DisableFormatting), Table, "xml", 0);
        return new EncryptedXmlInfo(new XElement("secretBox", Convert.ToBase64String(blob)), typeof(SecretBoxXmlDecryptor));
    }
}

/// <summary>Data Protection lo crea por reflexión con este constructor, a partir del nombre guardado en la llave.</summary>
public sealed class SecretBoxXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var box = services.GetRequiredService<SecretBox>();
        return XElement.Parse(box.Decrypt(Convert.FromBase64String(encryptedElement.Value), SecretBoxXmlEncryptor.Table, "xml", 0));
    }
}

/// <summary>
/// Arranque: el esquema de la base está al día con este binario. Las migraciones corren aparte antes de actualizar
/// (D-14), así que un nodo con una versión nueva no recibe tráfico ni toma trabajos hasta que terminan. Cuando dio bien
/// una vez no vuelve a mirar.
/// </summary>
internal sealed class SchemaCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    private volatile bool current;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (current)
        {
            return HealthCheckResult.Healthy();
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var pending = await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pending.Any())
            {
                return HealthCheckResult.Unhealthy($"Faltan migraciones: {string.Join(", ", pending)}.");
            }

            current = true;
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("No hay conexión con la base.", ex);
        }
    }
}

/// <summary>
/// Listo: el proceso no se está apagando y la base a la que apunta acepta escrituras. Una réplica o una primaria vieja
/// que pasó a solo lectura dan 503, así el balanceador no le manda pedidos a un nodo sin primaria. Vivo no mira nada de
/// esto, para que una conmutación de la base no reinicie los nodos.
/// </summary>
internal sealed class PrimaryCheck(BillingDbContext db, IHostApplicationLifetime life) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (life.ApplicationStopping.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Se está apagando.");
        }

        try
        {
            var writable = await db.Database
                .SqlQueryRaw<bool>("SELECT NOT pg_is_in_recovery() AND current_setting('transaction_read_only') = 'off' AS \"Value\"")
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            return writable ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("La base no acepta escrituras: es una réplica o una primaria vieja.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failover.Recover(ex);
            return HealthCheckResult.Unhealthy("No hay conexión con la base primaria.", ex);
        }
    }
}
