using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Platform;

/// <summary>
/// API key (D-11): prefijo visible y 32 bytes aleatorios, guardada como SHA-256 y mostrada una sola vez, con
/// permisos, lista de IP, vencimiento y límite de pedidos. La API compatible usa el prefijo como identifier y el
/// resto como secret (D-12).
/// </summary>
public sealed class ApiKey
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Parte visible, por ejemplo "bk_7f3a9c21". Sirve para encontrar la llave sin guardar el secreto.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 del secreto, en hexadecimal.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>Permisos: "accounts:read", "orders:write" o, en la API compatible, "compat:GetClientsDetails". JSON.</summary>
    public string Scopes { get; set; } = "[]";

    /// <summary>IPs o redes CIDR permitidas. Vacía es ninguna restricción. JSON.</summary>
    public string AllowedIps { get; set; } = "[]";

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Tope de pedidos por minuto de esta llave. Vacío es el tope por defecto.</summary>
    public int? RequestsPerMinute { get; set; }

    public bool Allows(string scope) => JsonSerializer.Deserialize<string[]>(Scopes)!.Contains(scope);

    public bool AllowsIp(IPAddress? ip)
    {
        var allowed = JsonSerializer.Deserialize<string[]>(AllowedIps)!;
        if (allowed.Length == 0)
        {
            return true;
        }

        // Kestrel con socket dual entrega a un cliente IPv4 como ::ffff:a.b.c.d, y la lista puede traer cualquiera de
        // las dos formas: un cliente IPv4 se prueba como IPv4 y como mapeada.
        IPAddress[] forms = ip is null ? [] : ip.AddressFamily == AddressFamily.InterNetwork || ip.IsIPv4MappedToIPv6 ? [ip.MapToIPv4(), ip.MapToIPv6()] : [ip];
        return forms.Any(client => allowed.Any(a => IPNetwork.TryParse(a, out var net) ? net.Contains(client) : IPAddress.TryParse(a, out var single) && single.Equals(client)));
    }

    public static string HashSecret(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

public sealed record IssuedApiKey(long Id, string Prefix, string Secret)
{
    /// <summary>La llave completa para el encabezado Authorization: se muestra una sola vez.</summary>
    public string Token => $"{Prefix}.{Secret}";
}

public sealed class ApiKeyService(BillingDbContext db, AuditLog audit, TimeProvider time)
{
    /// <summary>
    /// Emite una llave. El secreto vuelve una sola vez y queda solo su hash. La emisión queda en la auditoría con el
    /// nombre, el prefijo y los permisos, nunca con el secreto.
    /// </summary>
    public async Task<IssuedApiKey> Issue(string name, IEnumerable<string> scopes, IEnumerable<string>? allowedIps = null, DateTimeOffset? expiresAt = null, int? requestsPerMinute = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (requestsPerMinute < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerMinute), requestsPerMinute, "El tope de pedidos tiene que ser 1 o más.");
        }

        var label = name?.Trim() ?? string.Empty;
        if (label.Length is 0 or > 100)
        {
            throw new ValidationException("El nombre de la llave va de 1 a 100 caracteres.");
        }

        var granted = scopes.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (granted.Length == 0)
        {
            throw new ValidationException("Elegí al menos un permiso: una llave sin permisos no sirve para nada.");
        }

        var ips = (allowedIps ?? []).Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (ips.FirstOrDefault(i => !IPNetwork.TryParse(i, out _) && !IPAddress.TryParse(i, out _)) is { } wrong)
        {
            throw new ValidationException($"No se entiende la IP {wrong}. Escribí una IP, como 203.0.113.7, o una red, como 203.0.113.0/24.");
        }

        var now = time.GetUtcNow();
        if (expiresAt <= now)
        {
            throw new ValidationException("El vencimiento tiene que ser una fecha futura.");
        }

        var prefix = "bk_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var key = new ApiKey
        {
            Name = label,
            Prefix = prefix,
            SecretHash = ApiKey.HashSecret(secret),
            Scopes = JsonSerializer.Serialize(granted),
            AllowedIps = JsonSerializer.Serialize(ips),
            ExpiresAt = expiresAt,
            RequestsPerMinute = requestsPerMinute,
            CreatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("apikeys.issue", Entity(key.Id), new { name = label, prefix, scopes = granted, allowed_ips = ips, expires_at = expiresAt, requests_per_minute = requestsPerMinute }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new IssuedApiKey(key.Id, prefix, secret);
    }

    /// <summary>Revoca la llave: el próximo pedido con ella ya no pasa, en cualquier nodo. Revocar dos veces no cambia nada.</summary>
    public async Task Revoke(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la llave {id}.");
        if (key.RevokedAt is not null)
        {
            return;
        }

        key.RevokedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("apikeys.revoke", Entity(id), new { name = key.Name, prefix = key.Prefix }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static string Entity(long id) => string.Create(CultureInfo.InvariantCulture, $"apikey:{id}");

    /// <summary>Valida prefijo y secreto en tiempo constante, vigencia e IP. Devuelve null si no vale.</summary>
    public async Task<ApiKey?> Authenticate(string prefix, string secret, IPAddress? ip, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Prefix == prefix, ct).ConfigureAwait(false);
        if (key is null || key.RevokedAt is not null || (key.ExpiresAt is { } exp && exp <= time.GetUtcNow()) || !key.AllowsIp(ip))
        {
            return null;
        }

        var expected = Encoding.ASCII.GetBytes(key.SecretHash);
        var actual = Encoding.ASCII.GetBytes(ApiKey.HashSecret(secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? key : null;
    }

    /// <summary>Separa "prefijo.secreto" del encabezado Bearer.</summary>
    public static bool TryParseToken(string? token, out string prefix, out string secret)
    {
        var parts = token?.Split('.', 2) ?? [];
        (prefix, secret) = parts.Length == 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
        return parts.Length == 2 && prefix.StartsWith("bk_", StringComparison.Ordinal);
    }

    public static string Describe(ApiKey key) =>
        string.Create(CultureInfo.InvariantCulture, $"apikey:{key.Id}:{key.Prefix}");
}
