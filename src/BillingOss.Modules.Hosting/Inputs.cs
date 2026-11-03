using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Modules.Hosting;

// Entradas de las operaciones. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta, en
// vez de dejar que el deserializador falle sin decir qué campo era.

/// <summary>El contexto que manda el núcleo (sección 8 del protocolo). Solo lo que usan estos módulos.</summary>
public sealed class CallContext
{
    public ServiceContext? Service { get; init; }

    public AccountContext? Account { get; init; }
}

public sealed class ServiceContext
{
    public long? Id { get; init; }

    public string? Uuid { get; init; }

    public JsonObject? ExternalIds { get; init; }

    public JsonObject? Params { get; init; }

    public JsonObject? Order { get; init; }
}

/// <summary>Nombre y correo llegan por el permiso account.contact del manifiesto.</summary>
public sealed class AccountContext
{
    public string? Language { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }
}

/// <summary>Entrada de create, run_action, sso y usage. Las credenciales las genera y las guarda el núcleo.</summary>
public sealed class OperationInput
{
    public string? Action { get; init; }

    public JsonObject? Credentials { get; init; }

    /// <summary>La IP del navegador que pidió el acceso directo, si el núcleo la manda.</summary>
    public string? ClientIp { get; init; }

    /// <summary>Los códigos de las métricas de uso que pide el núcleo.</summary>
    public IReadOnlyList<string?>? Metrics { get; init; }
}

/// <summary>Los campos de configuración que comparten los paneles. Cada módulo suma sus credenciales.</summary>
public class PanelConfig
{
    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? TlsFingerprint { get; init; }

    public string? UsernamePrefix { get; init; }
}

/// <summary>Dónde está el panel, ya validado: host en minúsculas, puerto, huella sin dos puntos y prefijo de usuarios.</summary>
public sealed record PanelEndpoint(string Host, int Port, string? Fingerprint, string UsernamePrefix)
{
    public Uri Url(string path, int? port = null) => new UriBuilder(Uri.UriSchemeHttps, Host, port ?? Port, path).Uri;
}

/// <summary>Validación de la configuración de la instancia. Nada sale a la red con un campo que no sirve.</summary>
public static class Settings
{
    /// <summary>Lee la configuración. Un tipo mal puesto es invalid_config con la ruta, nunca con el valor.</summary>
    public static T Read<T>(ModuleCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return call.Config<T>();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidConfig([ex.Path ?? "config"]);
        }
    }

    /// <summary>Valida host, puerto, huella y prefijo. Los que no sirven se suman a <paramref name="bad"/>.</summary>
    public static PanelEndpoint Endpoint(PanelConfig config, int defaultPort, List<string> bad)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(bad);
        var host = config.Host?.Trim().ToLowerInvariant();
        if (host is null || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            bad.Add("host");
        }

        var port = config.Port ?? defaultPort;
        if (port is < 1 or > 65535)
        {
            bad.Add("port");
        }

        string? fingerprint = null;
        if (!string.IsNullOrWhiteSpace(config.TlsFingerprint))
        {
            fingerprint = config.TlsFingerprint.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
            if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
            {
                bad.Add("tls_fingerprint");
            }
        }

        var prefix = config.UsernamePrefix ?? "c";
        if (prefix.Length is < 1 or > 4 || !prefix.All(char.IsAsciiLetterLower))
        {
            bad.Add("username_prefix");
        }

        return new PanelEndpoint(host ?? string.Empty, port, fingerprint, prefix);
    }

    /// <summary>
    /// Un usuario, una clave o un token: sin espacios ni caracteres de control, que casi siempre son un error al copiar, y
    /// solo ASCII, porque viaja en un encabezado HTTP.
    /// </summary>
    public static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 512 } && value.All(c => char.IsAscii(c) && !char.IsWhiteSpace(c) && !char.IsControl(c));

    /// <summary>Una IPv4 en su forma de texto.</summary>
    public static bool IsIPv4(string? value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.ToString() == value;
}

/// <summary>
/// El usuario de las cuentas nuevas: el prefijo más el SHA-256 del uuid del servicio en base32, hasta 8 caracteres.
/// Es la marca en los paneles que no tienen dónde guardar otra: sale siempre igual para el mismo servicio y no choca
/// con el de otra instalación que comparte el panel, como pasaría con el ID numérico.
/// </summary>
public static class Usernames
{
    public const int Length = 8;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string For(string prefix, string uuid)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(uuid);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(uuid.ToLowerInvariant()));
        var name = new StringBuilder(prefix, Length);
        for (var bit = 0; name.Length < Length; bit += 5)
        {
            // Cinco bits por letra, leídos de izquierda a derecha.
            var index = (((hash[bit / 8] << 8) | hash[(bit / 8) + 1]) >> (11 - (bit % 8))) & 31;
            name.Append(Alphabet[index]);
        }

        return name.ToString();
    }
}

/// <summary>Formatos de dominio, correo, plan, nombre y contraseña.</summary>
public static class Names
{
    /// <summary>El dominio en ASCII y minúsculas, sin punto final, o null si no es un hostname válido.</summary>
    public static string? Domain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        var labels = ascii.Split('.');
        return ascii.Length <= 253
            && labels.Length >= 2
            && labels.All(l => l.Length is >= 1 and <= 63 && l[0] != '-' && l[^1] != '-' && l.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            ? ascii
            : null;
    }

    public static bool IsEmail(string? value) =>
        value is { Length: > 0 and <= 254 } && !value.Any(char.IsWhiteSpace) && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;

    /// <summary>Un plan o paquete: letras, dígitos, espacio, guion, guion bajo y punto, sin espacios en los extremos.</summary>
    public static bool IsPlan(string? value) =>
        value is { Length: >= 1 and <= 128 } && value.Trim() == value && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.');

    /// <summary>Un usuario de panel: empieza con una letra o un dígito y sigue con letras, dígitos, guion, guion bajo o punto.</summary>
    public static bool IsUsername(string? value) =>
        value is { Length: >= 1 and <= 64 } && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>Una credencial que manda el núcleo: sin espacios ni caracteres de control.</summary>
    public static bool IsPassword(string? value) =>
        value is { Length: >= 8 and <= 128 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>Un uuid en su forma de texto, en minúsculas, o null.</summary>
    public static string? Uuid(string? value) => Guid.TryParseExact(value, "D", out var uuid) && uuid != Guid.Empty ? uuid.ToString() : null;

    /// <summary>Si el idioma de la cuenta es inglés. Todo lo demás se muestra en castellano.</summary>
    public static bool IsEnglish(string? language) => language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>El nombre para el panel: solo letras, dígitos, espacios, puntos y guiones, hasta 50 caracteres. Null si no queda nada.</summary>
    public static string? Display(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var clean = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-')
            {
                clean.Append(c);
            }
            else if (char.IsWhiteSpace(c) && clean.Length > 0 && clean[^1] != ' ')
            {
                clean.Append(' ');
            }
        }

        var text = clean.ToString().Trim();
        return text.Length == 0 ? null : text[..Math.Min(text.Length, 50)].Trim();
    }
}

/// <summary>Armado de los errores del protocolo con los detalles que usan estos módulos.</summary>
public static class Fail
{
    public static ModuleException InvalidInput(IReadOnlyCollection<string> fields, string? message = null, string? reason = null) => new(
        ErrorCodes.InvalidInput,
        message ?? $"Faltan datos o son inválidos: {string.Join(", ", fields)}.",
        details: Details(reason, fields));

    public static ModuleException InvalidConfig(IReadOnlyCollection<string> fields, string? message = null, string? upstream = null) => new(
        ErrorCodes.InvalidConfig,
        message ?? $"La configuración de la instancia no sirve: {string.Join(", ", fields)}.",
        details: Details(null, fields, upstream));

    /// <summary>El panel no respondió, o fue una lectura: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, string? reason = null, string? upstream = null) =>
        new(ErrorCodes.UpstreamUnavailable, message, 30, Details(reason, null, upstream));

    /// <summary>Resultado incierto: el cambio pudo haberse aplicado. El núcleo repite con la misma clave y la lectura previa decide qué falta.</summary>
    public static ModuleException Unknown(string message, string reason) =>
        new(ErrorCodes.OutcomeUnknown, message, 60, Details(reason, null));

    /// <summary>El panel rechazó el cambio por sus reglas. details.upstream trae su texto.</summary>
    public static ModuleException Rejected(string message, string reason, string? upstream) =>
        new(ErrorCodes.Rejected, message, details: Details(reason, null, upstream));

    public static ModuleException Conflict(string message, string reason) =>
        new(ErrorCodes.Conflict, message, 60, Details(reason, null));

    public static ModuleException NotFound(string message) =>
        new(ErrorCodes.NotFound, message, details: Details("absent", null));

    /// <summary>Algo con el nombre del servicio que no se puede probar como suyo. No se tocó nada: lo revisa el staff.</summary>
    public static ModuleException Review(string message, string reason) =>
        new(ErrorCodes.ManualReview, message, details: Details(reason, null));

    private static JsonObject Details(string? reason, IEnumerable<string>? fields, string? upstream = null)
    {
        var details = new JsonObject();
        if (reason is not null)
        {
            details["reason"] = reason;
        }

        if (fields is not null)
        {
            details["fields"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        }

        if (upstream is not null)
        {
            details["upstream"] = upstream;
        }

        return details;
    }
}

/// <summary>Lectura tolerante de JSON que no controlamos: lo que no es del tipo esperado da null en vez de una excepción.</summary>
public static class JsonRead
{
    public static JsonNode? Field(this JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    /// <summary>El texto de un valor. Los números y los booleanos salen tal cual vinieron.</summary>
    public static string? Text(this JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    /// <summary>Un número entero, aunque venga como texto. Null si no es uno.</summary>
    public static long? Long(this JsonNode? node) =>
        long.TryParse(node.Text(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Bytes en un MiB.</summary>
    public const decimal MiB = 1024 * 1024;

    /// <summary>Un tamaño en MiB, aunque venga como texto con decimales o con la unidad pegada ("120M"). Null si no es uno.</summary>
    public static decimal? Mebibytes(string? text)
    {
        text = text?.Trim().TrimEnd('M', 'm').Trim();
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}

/// <summary>Textos del panel antes de mostrarlos: cortos y sin los secretos de la llamada.</summary>
public static class Upstream
{
    public static string Describe(int status, string? text, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        text ??= string.Empty;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"HTTP {status} sin texto");
        }

        return text.Length <= 300 ? text : text[..300] + "...";
    }
}
