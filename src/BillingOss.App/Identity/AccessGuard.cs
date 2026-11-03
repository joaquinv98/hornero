using System.Globalization;
using System.Net;
using System.Net.Sockets;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Identity;

/// <summary>
/// Listas de bloqueo, historial de ingresos, límite de intentos por IP y aviso de un lugar nuevo
/// (diseno/fraude-y-acceso.md, secciones 4 y 6). Todo en la base, así vale igual en cualquier nodo.
/// </summary>
public sealed class AccessGuard(
    BillingDbContext db,
    SecuritySettingsStore settings,
    IAccountMail mail,
    AuditLog audit,
    IOptions<BillingOptions> options,
    TimeProvider time,
    ILogger<AccessGuard> logger)
{
    public Task<SecuritySettings> Settings(CancellationToken ct = default) => settings.Get(ct);

    /// <summary>La IP está en una entrada vigente de la lista, cargada a mano o automática por intentos.</summary>
    public async Task<bool> IsBlocked(IPAddress? ip, CancellationToken ct = default)
    {
        if (ip is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var address = Networks.Normalize(ip);
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM identity.blocked_entries
              WHERE kind = 'Ip' AND network >>= {address} AND (expires_at IS NULL OR expires_at > {now})) AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>El correo o su dominio están en una entrada vigente de la lista.</summary>
    public async Task<bool> IsBlocked(string? email, CancellationToken ct = default) =>
        (await EmailReasons(email, ct).ConfigureAwait(false)).Count > 0;

    /// <summary>Los motivos de revisión de un pedido por las listas: la IP, los correos y sus dominios.</summary>
    public async Task<List<FraudReason>> BlockReasons(IPAddress? ip, IEnumerable<string?> emails, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var reasons = new List<FraudReason>();
        if (await IsBlocked(ip, ct).ConfigureAwait(false))
        {
            reasons.Add(new("blocklist.ip", $"La IP {Networks.Normalize(ip!)} está en la lista de bloqueo."));
        }

        foreach (var email in emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim().ToLowerInvariant()).Distinct())
        {
            reasons.AddRange(await EmailReasons(email, ct).ConfigureAwait(false));
        }

        return [.. reasons.DistinctBy(r => r.Message)];
    }

    /// <summary>
    /// Carga una entrada en la lista, o actualiza la que ya está con ese valor. Valida y normaliza el valor: la IP o la
    /// red en notación CIDR, el correo y el dominio en minúsculas.
    /// </summary>
    public async Task<long> Block(BlockKind kind, string value, string reason, DateTimeOffset? expiresAt, BlockSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (normalized, network) = Normalize(kind, value);
        reason = (reason ?? string.Empty).Trim();
        if (reason.Length is 0 or > 300)
        {
            throw new ValidationException("El motivo es obligatorio y tiene hasta 300 caracteres.");
        }

        var now = time.GetUtcNow();
        if (expiresAt is { } until && until <= now)
        {
            throw new ValidationException("El vencimiento tiene que ser futuro.");
        }

        var kindText = kind.ToString();
        var sourceText = source.ToString();
        // Un INSERT no se puede componer: se trae la lista sin más operadores.
        var id = (await db.Database.SqlQuery<long>($"""
            INSERT INTO identity.blocked_entries (kind, value, network, reason, source, created_by, created_at, expires_at)
            VALUES ({kindText}, {normalized}, {network}, {reason}, {sourceText}, {Actor.Current.Id}, {now}, {expiresAt})
            ON CONFLICT (kind, value) DO UPDATE SET reason = EXCLUDED.reason, source = EXCLUDED.source,
              created_by = EXCLUDED.created_by, created_at = EXCLUDED.created_at, expires_at = EXCLUDED.expires_at
            RETURNING id
            """).ToListAsync(ct).ConfigureAwait(false)).Single();
        await audit.Record("security.block", string.Create(CultureInfo.InvariantCulture, $"block:{id}"), new { kind = kindText, value = normalized, expires_at = expiresAt }, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Saca una entrada de la lista. Devuelve false si no existía.</summary>
    public async Task<bool> Unblock(long id, CancellationToken ct = default)
    {
        var entry = await db.Set<BlockedEntry>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return false;
        }

        await db.Set<BlockedEntry>().Where(e => e.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await audit.Record("security.unblock", string.Create(CultureInfo.InvariantCulture, $"block:{id}"), new { kind = entry.Kind.ToString(), value = entry.Value }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Anota un intento de ingreso. Si es una falla y la IP juntó el tope de la ventana, la bloquea por un rato con una
    /// entrada automática. Si es un ingreso exitoso desde una red nueva para ese usuario, lo marca y, con el ajuste
    /// prendido, le avisa por correo. El primer ingreso de un usuario no avisa.
    /// </summary>
    public async Task<LoginEvent> Record(LoginAudience audience, LoginUser? user, IPAddress? ip, string? userAgent, LoginResult result, LoginMethod method, CancellationToken ct = default)
    {
        var s = await settings.Get(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var address = ip is null ? null : Networks.Normalize(ip);
        var login = new LoginEvent
        {
            Audience = audience,
            UserId = user?.Id,
            Ip = address,
            Network = address is null ? null : Networks.Location(address),
            UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
            Result = result,
            Method = method,
            CreatedAt = now,
        };

        if (result == LoginResult.Success && user is not null && login.Network is { } network)
        {
            var successes = db.Set<LoginEvent>().Where(e => e.Audience == audience && e.UserId == user.Id && e.Result == LoginResult.Success);
            login.NewLocation = await successes.AnyAsync(ct).ConfigureAwait(false)
                && !await successes.AnyAsync(e => e.Network == network, ct).ConfigureAwait(false);
        }

        db.Set<LoginEvent>().Add(login);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (login.Failed && address is not null)
        {
            await LimitIp(address, s, now, ct).ConfigureAwait(false);
        }

        if (login.NewLocation && s.AlertNewLocation && user is not null)
        {
            await Alert(audience, user, login, ct).ConfigureAwait(false);
        }

        return login;
    }

    /// <summary>Los últimos ingresos de un usuario, del más nuevo al más viejo.</summary>
    public Task<List<LoginEvent>> History(LoginAudience audience, long userId, int take = 20, CancellationToken ct = default) =>
        db.Set<LoginEvent>().AsNoTracking().Where(e => e.Audience == audience && e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(take).ToListAsync(ct);

    /// <summary>
    /// El segundo factor es obligatorio para este usuario del portal: lo tenía en el sistema anterior, o tiene en alguna
    /// cuenta un rol de los ajustes (titular, facturación o técnico).
    /// </summary>
    public async Task<bool> TwoFactorRequired(long userId, CancellationToken ct = default)
    {
        if (await db.Set<PortalUser>().AnyAsync(u => u.Id == userId && u.TwoFactorRequired, ct).ConfigureAwait(false))
        {
            return true;
        }

        var roles = (await settings.Get(ct).ConfigureAwait(false)).TwoFactorRoles;
        if (roles.Count == 0)
        {
            return false;
        }

        var owner = roles.Contains("owner");
        var billing = roles.Contains("billing");
        var technical = roles.Contains("technical");
        return await db.Set<AccountUser>().AnyAsync(m => m.UserId == userId
            && ((owner && m.IsOwner) || (billing && m.Capabilities.Contains(AccountCapabilities.Invoices)) || (technical && m.Capabilities.Contains(AccountCapabilities.Services))), ct).ConfigureAwait(false);
    }

    /// <summary>Lo tiene obligatorio y todavía no lo activó.</summary>
    public async Task<bool> TwoFactorPending(long userId, CancellationToken ct = default) =>
        !await db.Set<PortalUser>().AnyAsync(u => u.Id == userId && u.TwoFactorEnabled, ct).ConfigureAwait(false)
        && await TwoFactorRequired(userId, ct).ConfigureAwait(false);

    /// <summary>
    /// Tope de altas por IP (diseno/ingreso-clientes.md, sección 2.3). Va dentro de la transacción del alta: toma un lock
    /// de la red, así dos altas a la vez desde la misma red no pasan el tope en ningún nodo, y cuenta las altas de la
    /// ventana en la auditoría. En IPv6 cuenta la red /64, como el límite de intentos.
    /// </summary>
    public async Task<bool> RegistrationAllowed(IPAddress? ip, CancellationToken ct = default)
    {
        var s = await settings.Get(ct).ConfigureAwait(false);
        if (ip is null || s.Trusted(ip))
        {
            return true;
        }

        var network = Networks.Limit(ip);
        await using (var lockCmd = await Sql.Command(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", ct, ("key", "registration:" + network)).ConfigureAwait(false))
        {
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var since = time.GetUtcNow().AddHours(-s.RegistrationIpHours);
        var count = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM platform.audit_events
            WHERE action = {RegistrationAction} AND created_at > {since} AND ip <<= {network}
            """).SingleAsync(ct).ConfigureAwait(false);
        return count < s.RegistrationIpLimit;
    }

    /// <summary>La acción de la auditoría que deja cada alta desde el portal, y que cuenta el tope por IP.</summary>
    public const string RegistrationAction = "portal.registered";

    /// <summary>Valida y normaliza el valor de una entrada. La red de un tipo IP sale con los bits de host en cero.</summary>
    public static (string Value, IPNetwork? Network) Normalize(BlockKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim().ToLowerInvariant();
        switch (kind)
        {
            case BlockKind.Ip:
                var network = Networks.Parse(text) ?? throw new ValidationException("Escribí una IP o una red, por ejemplo 203.0.113.7 o 203.0.113.0/24.");
                return (network.ToString(), network);
            case BlockKind.Email:
                var at = text.IndexOf('@', StringComparison.Ordinal);
                if (at <= 0 || at != text.LastIndexOf('@') || !IsDomain(text[(at + 1)..]) || text.Length > 320 || text.Any(char.IsWhiteSpace))
                {
                    throw new ValidationException("Escribí un correo completo, por ejemplo alguien@ejemplo.com.");
                }

                return (text, null);
            case BlockKind.Domain:
                text = text.TrimStart('@');
                return IsDomain(text) ? (text, null) : throw new ValidationException("Escribí un dominio, por ejemplo ejemplo.com.");
            default:
                throw new ValidationException("El tipo es ip, email o domain.");
        }
    }

    private static bool IsDomain(string text) =>
        text.Length is > 2 and <= 253 && text.Contains('.', StringComparison.Ordinal) && !text.StartsWith('.') && !text.EndsWith('.')
        && text.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' || char.IsLetter(c));

    private async Task<List<FraudReason>> EmailReasons(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return [];
        }

        var normalized = email.Trim().ToLowerInvariant();
        var domain = normalized[(normalized.LastIndexOf('@') + 1)..];
        var now = time.GetUtcNow();
        var hits = await db.Set<BlockedEntry>().AsNoTracking()
            .Where(e => ((e.Kind == BlockKind.Email && e.Value == normalized) || (e.Kind == BlockKind.Domain && e.Value == domain))
                        && (e.ExpiresAt == null || e.ExpiresAt > now))
            .Select(e => e.Kind).ToListAsync(ct).ConfigureAwait(false);
        return [.. hits.Distinct().Select(k => k == BlockKind.Email
            ? new FraudReason("blocklist.email", $"El correo {normalized} está en la lista de bloqueo.")
            : new FraudReason("blocklist.domain", $"El dominio {domain} está en la lista de bloqueo."))];
    }

    /// <summary>
    /// Bloqueo temporal de la IP si juntó el tope de fallas en la ventana. En IPv6 cuenta y bloquea la red /64, que es lo
    /// mínimo que recibe cualquier conexión: por dirección se esquivaría cambiando de dirección. Nunca acorta una entrada
    /// permanente ni una que vence más tarde, y nunca bloquea una red de confianza de los ajustes.
    /// </summary>
    private async Task LimitIp(IPAddress ip, SecuritySettings s, DateTimeOffset now, CancellationToken ct)
    {
        if (s.Trusted(ip))
        {
            return;
        }

        var network = Networks.Limit(ip);
        var since = now.AddMinutes(-s.IpWindowMinutes);
        var failures = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM identity.login_events
            WHERE ip <<= {network} AND created_at > {since} AND result NOT IN ('Success', 'BlockedIp')
            """).SingleAsync(ct).ConfigureAwait(false);
        if (failures < s.IpAttempts)
        {
            return;
        }

        var value = network.ToString();
        var until = now.AddMinutes(s.IpMinutes);
        var reason = string.Create(CultureInfo.InvariantCulture, $"{failures} ingresos fallidos en {s.IpWindowMinutes} minutos.");
        var created = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.blocked_entries (kind, value, network, reason, source, created_by, created_at, expires_at)
            VALUES ('Ip', {value}, {network}, {reason}, 'Automatic', 'system', {now}, {until})
            ON CONFLICT (kind, value) DO UPDATE SET expires_at = GREATEST(blocked_entries.expires_at, EXCLUDED.expires_at)
            WHERE blocked_entries.expires_at IS NOT NULL
            """, ct).ConfigureAwait(false);
        if (created > 0)
        {
            logger.LogWarning("IP {Ip} bloqueada hasta {Until} por {Failures} ingresos fallidos.", value, until, failures);
        }
    }

    private async Task Alert(LoginAudience audience, LoginUser user, LoginEvent login, CancellationToken ct)
    {
        try
        {
            var path = audience == LoginAudience.Staff ? "/admin/seguridad" : "/cuenta/seguridad";
            var link = options.Value.PublicUrl is { Length: > 0 } url ? new Uri(url.TrimEnd('/') + path) : null;
            await mail.SendNewLogin(user.Email, user.Language, login.CreatedAt, login.Ip?.ToString() ?? string.Empty, Devices.Describe(login.UserAgent), link, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El aviso no frena el ingreso: queda en el log y en el historial, marcado como lugar nuevo.
            logger.LogError(ex, "No se pudo armar el aviso de ingreso desde un lugar nuevo del usuario {UserId}.", user.Id);
        }
    }
}

/// <summary>Redes de las IPs: la del "lugar" de un ingreso y la de una entrada de la lista.</summary>
public static class Networks
{
    /// <summary>Una IPv4 que llega como IPv6 mapeada (::ffff:a.b.c.d) se trata como IPv4.</summary>
    public static IPAddress Normalize(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    /// <summary>El lugar de un ingreso: la red /24 en IPv4 y la /48 en IPv6.</summary>
    public static IPNetwork Location(IPAddress ip) => Of(Normalize(ip), ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv4MappedToIPv6 ? 48 : 24);

    /// <summary>Lo que cuenta el límite de intentos por IP: la IP sola en IPv4 (/32) y la red /64 en IPv6.</summary>
    public static IPNetwork Limit(IPAddress ip)
    {
        var address = Normalize(ip);
        return address.AddressFamily == AddressFamily.InterNetwork ? new IPNetwork(address, 32) : Of(address, 64);
    }

    /// <summary>"203.0.113.7", "203.0.113.7/24" o "2001:db8::/32". Los bits de host se ponen en cero. Null si no es válida.</summary>
    public static IPNetwork? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip))
        {
            return null;
        }

        ip = Normalize(ip);
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (parts.Length == 1)
        {
            return new IPNetwork(ip, max);
        }

        return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) && prefix >= 8 && prefix <= max ? Of(ip, prefix) : null;
    }

    private static IPNetwork Of(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return new IPNetwork(new IPAddress(bytes), prefix);
    }
}

/// <summary>Nombre corto del navegador y del sistema, para el historial y el aviso: "Chrome / Windows".</summary>
public static class Devices
{
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "?";
        }

        var ua = userAgent;
        var browser = ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : "?";
        var system = ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) ? "iOS"
            : ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : "?";
        return $"{browser} / {system}";
    }
}
