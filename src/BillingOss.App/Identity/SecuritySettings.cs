using System.Security.Cryptography;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Identity;

/// <summary>
/// Ajustes de seguridad y fraude que el staff edita en el panel (diseno/fraude-y-acceso.md, sección 2). Sin fila en la
/// base valen estos valores de fábrica.
/// </summary>
public sealed record SecuritySettings
{
    /// <summary>Roles del portal que pueden tener el segundo factor obligatorio.</summary>
    public static IReadOnlyList<string> Roles { get; } = ["owner", "billing", "technical"];

    public int MinLength { get; init; } = 10;

    public int StaffMinLength { get; init; } = 12;

    public bool RequireUpper { get; init; }

    public bool RequireLower { get; init; }

    public bool RequireDigit { get; init; }

    public bool RequireSymbol { get; init; }

    public bool RejectCommon { get; init; } = true;

    public int AccountAttempts { get; init; } = 5;

    public int AccountMinutes { get; init; } = 15;

    /// <summary>Fallas seguidas que bloquean una cuenta del staff, con su propio umbral.</summary>
    public int StaffAccountAttempts { get; init; } = 5;

    public int StaffAccountMinutes { get; init; } = 15;

    public int IpAttempts { get; init; } = 20;

    public int IpWindowMinutes { get; init; } = 15;

    public int IpMinutes { get; init; } = 30;

    /// <summary>
    /// IPs o redes en notación CIDR que el límite de intentos nunca bloquea, como la oficina o la VPN del proveedor. Las
    /// listas de bloqueo que carga el staff valen igual.
    /// </summary>
    public IReadOnlyList<string> TrustedNetworks { get; init; } = [];

    /// <summary>Roles de <see cref="Roles"/> con el segundo factor obligatorio. Para el staff siempre lo es.</summary>
    public IReadOnlyList<string> TwoFactorRoles { get; init; } = [];

    public bool RequireVerifiedEmail { get; init; }

    public bool AlertNewLocation { get; init; } = true;

    public int KeepLoginDays { get; init; } = 365;

    public bool FraudEnabled { get; init; }

    public long? FraudInstanceId { get; init; }

    /// <summary>Desde este puntaje, de 0 a 100, el pedido va a revisión.</summary>
    public decimal FraudReviewScore { get; init; } = 50m;

    /// <summary>Si el módulo falla del todo, el pedido va a revisión. Apagado, pasa.</summary>
    public bool FraudReviewOnError { get; init; } = true;

    /// <summary>
    /// Los pedidos de una cuenta que ya tiene un servicio activo o suspendido no se le mandan al módulo, que cobra cada
    /// consulta. Las listas de bloqueo y el correo verificado valen igual.
    /// </summary>
    public bool FraudSkipExisting { get; init; }

    /// <summary>
    /// El portal deja crear cuentas, con correo y contraseña o con un proveedor externo (diseno/ingreso-clientes.md,
    /// sección 2). Apagado, las cuentas las da de alta el staff.
    /// </summary>
    public bool RegistrationEnabled { get; init; } = true;

    /// <summary>Altas desde una IP (o su red /64 en IPv6) dentro de la ventana. Las redes de confianza no tienen tope.</summary>
    public int RegistrationIpLimit { get; init; } = 5;

    public int RegistrationIpHours { get; init; } = 24;

    /// <summary>Moneda que viene elegida en el alta. El cliente puede elegir otra si el catálogo vende en varias.</summary>
    public string RegistrationCurrency { get; init; } = "ARS";

    /// <summary>
    /// Desafío anti robots (diseno/ingreso-clientes.md, sección 7): "none", "pow" (prueba de trabajo propia, invisible) o
    /// "turnstile" (Cloudflare, invisible o de un clic, con sus claves).
    /// </summary>
    public string ChallengeProvider { get; init; } = ChallengeNone;

    /// <summary>Los formularios que lo piden, de <see cref="ChallengeFormNames"/>. Nunca los botones de baja y arrepentimiento.</summary>
    public IReadOnlyList<string> ChallengeForms { get; init; } = [];

    /// <summary>El umbral de la prueba de trabajo: bits en cero al principio del SHA-256. Cada bit duplica el trabajo.</summary>
    public int ChallengeBits { get; init; } = 16;

    /// <summary>La clave pública del proveedor, que va en la página.</summary>
    public string? ChallengeSiteKey { get; init; }

    /// <summary>La clave secreta del proveedor, cifrada con <see cref="Platform.SecretBox"/> y en base64. Nunca vuelve a la pantalla.</summary>
    public string? ChallengeSecret { get; init; }

    public const string ChallengeNone = "none";

    public const string ChallengeWork = "pow";

    public const string ChallengeTurnstile = "turnstile";

    public static IReadOnlyList<string> ChallengeProviders { get; } = [ChallengeNone, ChallengeWork, ChallengeTurnstile];

    /// <summary>Ingreso, recuperar la contraseña, registro, contacto y ticket sin cuenta.</summary>
    public static IReadOnlyList<string> ChallengeFormNames { get; } = ["login", "forgot", "register", "contact", "ticket"];

    /// <summary>El desafío que pide ese formulario, o null si no pide ninguno.</summary>
    public string? ChallengeFor(string form) => ChallengeProvider != ChallengeNone && ChallengeForms.Contains(form) ? ChallengeProvider : null;

    public const int MaxTrustedNetworks = 50;

    /// <summary>La IP está en una de las redes de confianza.</summary>
    public bool Trusted(System.Net.IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        var address = Networks.Normalize(ip);
        return TrustedNetworks.Any(t => Networks.Parse(t) is { } network && network.Contains(address));
    }

    /// <summary>Lo que está fuera de rango, como claves de texto de la interfaz. Vacío si se puede guardar.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        Check(MinLength is >= PasswordRules.Floor and <= PasswordRules.MaxLength, "MinLength");
        Check(StaffMinLength >= MinLength && StaffMinLength <= PasswordRules.MaxLength, "StaffMinLength");
        Check(AccountAttempts is >= 3 and <= 50, "AccountAttempts");
        Check(AccountMinutes is >= 1 and <= 1440, "AccountMinutes");
        Check(StaffAccountAttempts is >= 3 and <= 50, "StaffAccountAttempts");
        Check(StaffAccountMinutes is >= 1 and <= 1440, "StaffAccountMinutes");
        Check(IpAttempts is >= 5 and <= 1000, "IpAttempts");
        Check(IpWindowMinutes is >= 1 and <= 1440, "IpWindowMinutes");
        Check(IpMinutes is >= 1 and <= 10080, "IpMinutes");
        Check(TrustedNetworks.Count <= MaxTrustedNetworks && TrustedNetworks.All(t => Networks.Parse(t) is not null), "TrustedNetworks");
        Check(TwoFactorRoles.All(Roles.Contains), "TwoFactorRoles");
        Check(KeepLoginDays is >= 30 and <= 3650, "KeepLoginDays");
        Check(FraudReviewScore is >= 0m and <= 100m && decimal.Round(FraudReviewScore, 2) == FraudReviewScore, "FraudReviewScore");
        Check(!FraudEnabled || FraudInstanceId is not null, "FraudInstanceId");
        Check(RegistrationIpLimit is >= 1 and <= 1000, "RegistrationIpLimit");
        Check(RegistrationIpHours is >= 1 and <= 720, "RegistrationIpHours");
        Check(RegistrationCurrency is { Length: 3 } c && c.All(char.IsAsciiLetterUpper), "RegistrationCurrency");
        Check(ChallengeProviders.Contains(ChallengeProvider), "ChallengeProvider");
        Check(ChallengeForms.All(ChallengeFormNames.Contains), "ChallengeForms");
        Check(ChallengeBits is >= 8 and <= 24, "ChallengeBits");
        Check(ChallengeProvider != ChallengeTurnstile || (ChallengeSiteKey is { Length: > 0 and <= 200 } && ChallengeSecret is not null), "ChallengeKeys");
        return problems;

        void Check(bool ok, string field)
        {
            if (!ok)
            {
                problems.Add("SecuritySettings." + field);
            }
        }
    }
}

/// <summary>Lee y guarda los ajustes. Dentro de un pedido se leen una sola vez.</summary>
public sealed class SecuritySettingsStore(BillingDbContext db, AuditLog audit, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private (SecuritySettings Settings, int Version)? current;

    public async Task<SecuritySettings> Get(CancellationToken ct = default) => (await Current(ct).ConfigureAwait(false)).Settings;

    /// <summary>Los ajustes y su versión, para guardarlos después sin pisar a otro.</summary>
    public async Task<(SecuritySettings Settings, int Version)> Current(CancellationToken ct = default)
    {
        if (current is { } cached)
        {
            return cached;
        }

        var row = await db.Set<SecuritySettingsRow>().AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        current = row is null ? (new SecuritySettings(), 0) : (JsonSerializer.Deserialize<SecuritySettings>(row.Settings, Json) ?? new(), row.Version);
        return current.Value;
    }

    /// <summary>
    /// Guarda si la versión sigue siendo <paramref name="version"/>. Si otra persona los cambió antes, da
    /// InvalidOperationException. Queda en la auditoría con los nombres de lo que cambió, nunca con valores.
    /// </summary>
    public async Task Save(SecuritySettings settings, int version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Problems() is { Count: > 0 } problems)
        {
            throw new ValidationException("Ajustes fuera de rango: " + string.Join(", ", problems));
        }

        if (settings.FraudEnabled && !await IsFraudInstance(settings.FraudInstanceId!.Value, ct).ConfigureAwait(false))
        {
            throw new ValidationException("La instancia elegida no es de un módulo de fraude.");
        }

        var before = (await Current(ct).ConfigureAwait(false)).Settings;
        var json = JsonSerializer.Serialize(settings, Json);
        var now = time.GetUtcNow();
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.security_settings (id, settings, version, updated_at, updated_by)
            VALUES ({SecuritySettingsRow.SingleId}, {json}::jsonb, 1, {now}, {Actor.Current.Id})
            ON CONFLICT (id) DO UPDATE SET settings = EXCLUDED.settings, version = security_settings.version + 1,
              updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
            WHERE security_settings.version = {version}
            """, ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new InvalidOperationException("Otra persona cambió los ajustes mientras los editabas: volvé a abrirlos.");
        }

        current = null;
        await audit.Record("security.settings", "security:settings", new { changed = Changed(before, settings) }, ct).ConfigureAwait(false);
    }

    /// <summary>Las instancias de módulos que declaran el tipo fraud, para elegir en los ajustes.</summary>
    public async Task<IReadOnlyList<(long Id, string Name)>> FraudInstances(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<FraudInstanceRow>("""
            SELECT i.id AS "Id", i.name AS "Name" FROM platform.module_instances i
            JOIN platform.module_installations m ON m.id = i.installation_id
            WHERE m.manifest -> 'kinds' @> '["fraud"]'::jsonb
            ORDER BY i.id
            """).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => (r.Id, r.Name))];
    }

    private async Task<bool> IsFraudInstance(long id, CancellationToken ct) => (await FraudInstances(ct).ConfigureAwait(false)).Any(i => i.Id == id);

    private static string[] Changed(SecuritySettings before, SecuritySettings after)
    {
        var a = JsonSerializer.SerializeToElement(before, Json);
        var b = JsonSerializer.SerializeToElement(after, Json);
        return [.. b.EnumerateObject().Where(p => !a.TryGetProperty(p.Name, out var old) || old.GetRawText() != p.Value.GetRawText()).Select(p => p.Name)];
    }

    private sealed record FraudInstanceRow(long Id, string Name);
}

/// <summary>
/// Política de contraseñas (diseno/fraude-y-acceso.md, sección 7), con NIST SP 800-63B: largo, composición solo si el
/// proveedor la pide, y rechazo de las comunes y de las que contienen el correo o la marca.
/// </summary>
public static class PasswordRules
{
    /// <summary>Nadie baja de 8, como pide NIST.</summary>
    public const int Floor = 8;

    public const int MaxLength = 128;

    /// <summary>Lista propia, escrita a mano: las más usadas en castellano y en inglés, sin los números del final.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "passw0rd", "contraseña", "contrasena", "clave", "miclave", "secreto", "qwerty", "qwertyuiop", "asdfghjkl",
        "zxcvbnm", "qazwsx", "iloveyou", "teamo", "admin", "administrador", "administrator", "root", "usuario", "user",
        "welcome", "bienvenido", "bienvenida", "letmein", "monkey", "dragon", "master", "sunshine", "princess", "football",
        "futbol", "baseball", "superman", "batman", "pokemon", "starwars", "argentina", "buenosaires", "boca", "bocajuniors",
        "river", "riverplate", "racing", "independiente", "sanlorenzo", "huracan", "velez", "estudiantes", "rosario", "cordoba",
        "mendoza", "messi", "maradona", "diego", "lionel", "martin", "maria", "juan", "carlos", "jorge", "lucas", "sofia",
        "valentina", "hosting", "servidor", "server", "billing", "factura", "cliente", "default", "changeme", "cambiame",
        "access", "acceso", "login", "ingresar", "abc", "abcd", "abcdef", "abcdefg", "abcdefgh", "trustno1", "shadow", "michael",
        "jennifer", "hello", "hola", "holamundo", "solo", "freedom", "whatever", "ninja", "mustang", "charlie", "computer",
        "internet", "google", "facebook", "instagram", "samsung", "iphone", "windows", "linux", "ubuntu", "debian",
    };

    /// <summary>
    /// Una contraseña al azar que cumple la política: el largo mínimo (nunca menos de 20) y un carácter de cada clase, así
    /// sirve aunque el proveedor prenda las reglas de composición. Es la inicial que muestra el alta del staff.
    /// </summary>
    public static string Generate(SecuritySettings settings, bool staff)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string[] classes = ["abcdefghjkmnpqrstuvwxyz", "ABCDEFGHJKLMNPQRSTUVWXYZ", "23456789", "-_.!#%+="];
        var length = Math.Max(20, staff ? settings.StaffMinLength : settings.MinLength);
        var chars = RandomNumberGenerator.GetString(string.Concat(classes), length - classes.Length).ToList();
        foreach (var set in classes)
        {
            chars.Insert(RandomNumberGenerator.GetInt32(chars.Count + 1), set[RandomNumberGenerator.GetInt32(set.Length)]);
        }

        return new string([.. chars]);
    }

    /// <summary>Los problemas como códigos de Identity (empiezan con "Password"). Vacío si la contraseña sirve.</summary>
    public static IReadOnlyList<string> Problems(SecuritySettings settings, string? password, string? email, string? brand, bool staff)
    {
        ArgumentNullException.ThrowIfNull(settings);
        password ??= string.Empty;
        var problems = new List<string>();
        var min = Math.Max(Floor, staff ? settings.StaffMinLength : settings.MinLength);
        if (password.Length < min)
        {
            problems.Add("PasswordTooShort");
        }

        if (password.Length > MaxLength)
        {
            problems.Add("PasswordTooLong");
        }

        if (settings.RequireUpper && !password.Any(char.IsUpper))
        {
            problems.Add("PasswordRequiresUpper");
        }

        if (settings.RequireLower && !password.Any(char.IsLower))
        {
            problems.Add("PasswordRequiresLower");
        }

        if (settings.RequireDigit && !password.Any(char.IsDigit))
        {
            problems.Add("PasswordRequiresDigit");
        }

        if (settings.RequireSymbol && password.All(char.IsLetterOrDigit))
        {
            problems.Add("PasswordRequiresNonAlphanumeric");
        }

        if (settings.RejectCommon && password.Length > 0 && IsWeak(password, email, brand))
        {
            problems.Add("PasswordCommon");
        }

        return problems;
    }

    /// <summary>
    /// Común si, sin espacios, signos ni los números del final, está en la lista o es un solo carácter repetido o una
    /// secuencia. También si contiene la parte del correo antes de la arroba o la marca, de 4 letras o más.
    /// </summary>
    private static bool IsWeak(string password, string? email, string? brand)
    {
        var lower = password.ToLowerInvariant();
        var letters = new string([.. lower.Where(char.IsLetterOrDigit)]);
        var core = letters.TrimEnd("0123456789".ToCharArray());
        // Solo números y cortos: un DNI, un teléfono o una fecha.
        if ((core.Length == 0 && letters.Length < 12) || Common.Contains(core) || Common.Contains(letters) || letters.Distinct().Count() <= 2 || IsSequence(letters))
        {
            return true;
        }

        var local = email?.Split('@')[0].ToLowerInvariant();
        return (local is { Length: >= 4 } && lower.Contains(local, StringComparison.Ordinal))
            || (brand?.ToLowerInvariant() is { Length: >= 4 } b && lower.Contains(b, StringComparison.Ordinal));
    }

    /// <summary>Cada carácter es el anterior más uno o menos uno: "123456789", "abcdefgh" o "987654321".</summary>
    private static bool IsSequence(string text) =>
        text.Length >= 4 && (Enumerable.Range(1, text.Length - 1).All(i => text[i] - text[i - 1] == 1)
                             || Enumerable.Range(1, text.Length - 1).All(i => text[i] - text[i - 1] == -1));
}
