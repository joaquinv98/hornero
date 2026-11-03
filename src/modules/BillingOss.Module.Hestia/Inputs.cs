using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Hestia;

// Entradas de las operaciones. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta,
// en vez de dejar que el deserializador falle sin decir qué campo era.

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class HestiaConfig
{
    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? TlsFingerprint { get; init; }

    public string? AccessKeyId { get; init; }

    public string? SecretKey { get; init; }

    public string? DefaultLanguage { get; init; }

    public string? UsernamePrefix { get; init; }
}

/// <summary>El contexto que manda el núcleo (sección 8 del protocolo). Solo lo que usa este módulo.</summary>
internal sealed class CallContext
{
    public ServiceContext? Service { get; init; }

    public AccountContext? Account { get; init; }
}

internal sealed class ServiceContext
{
    public long? Id { get; init; }

    /// <summary>El uuid estable del servicio (context.service.uuid). Null mientras el núcleo no lo mande.</summary>
    public string? Uuid { get; init; }

    public JsonObject? ExternalIds { get; init; }

    public JsonObject? Params { get; init; }

    public JsonObject? Order { get; init; }
}

/// <summary>Nombre y correo llegan por el permiso account.contact del manifiesto.</summary>
internal sealed class AccountContext
{
    public string? Language { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }
}

/// <summary>Entrada de create y de run_action. Las credenciales las genera y las guarda el núcleo.</summary>
internal sealed class OperationInput
{
    public string? Action { get; init; }

    public JsonObject? Credentials { get; init; }
}

/// <summary>La configuración de la instancia, ya validada.</summary>
internal sealed record Settings(
    string Host,
    int Port,
    string? Fingerprint,
    string AccessKeyId,
    string SecretKey,
    string DefaultLanguage,
    string UsernamePrefix)
{
    public Uri ApiUrl => new UriBuilder(Uri.UriSchemeHttps, Host, Port, "/api/").Uri;

    /// <summary>El login del panel, sin credenciales: Hestia exige un token CSRF que no se puede mandar desde afuera.</summary>
    public string LoginUrl => new UriBuilder(Uri.UriSchemeHttps, Host, Port, "/login/").Uri.AbsoluteUri;

    public static Settings From(ModuleCall call)
    {
        HestiaConfig config;
        try
        {
            config = call.Config<HestiaConfig>();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidConfig([ex.Path ?? "config"]);
        }

        var bad = new List<string>();
        var host = config.Host?.Trim().ToLowerInvariant();
        if (host is null || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            bad.Add("host");
        }

        var port = config.Port ?? 8083;
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

        if (!IsToken(config.AccessKeyId) || config.AccessKeyId!.Contains(':', StringComparison.Ordinal))
        {
            bad.Add("access_key_id");
        }

        if (!IsToken(config.SecretKey))
        {
            bad.Add("secret_key");
        }

        var language = config.DefaultLanguage ?? "es";
        if (Names.Language(language) != language)
        {
            bad.Add("default_language");
        }

        var prefix = config.UsernamePrefix ?? "c";
        if (prefix.Length is < 1 or > 8 || !char.IsAsciiLetterLower(prefix[0]) || !prefix.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
        {
            bad.Add("username_prefix");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidConfig(bad);
        }

        return new Settings(host!, port, fingerprint, config.AccessKeyId!, config.SecretKey!, language, prefix);
    }

    /// <summary>Sin espacios ni caracteres de control, que casi siempre son un error al copiar.</summary>
    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 256 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}

/// <summary>La cuenta de Hestia de un servicio: el usuario y el dominio que prueba que es suya.</summary>
internal sealed record HestiaAccount(string Username, string Domain);

/// <summary>Formatos de usuario, paquete, dominio, idioma, correo y nombre.</summary>
internal static class Names
{
    /// <summary>^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$, la forma que acepta el módulo para un usuario de Hestia.</summary>
    public static bool IsUsername(string? value) =>
        value is { Length: >= 1 and <= 32 } && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$, el pattern de package en product_schema.</summary>
    public static bool IsPackage(string? value) =>
        value is { Length: >= 1 and <= 64 } && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

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

    /// <summary>El idioma principal de una etiqueta como es-AR, que es la forma de los idiomas de Hestia.</summary>
    public static string? Language(string? tag)
    {
        var primary = tag?.Split('-', '_')[0].Trim().ToLowerInvariant();
        return primary is { Length: 2 or 3 } && primary.All(char.IsAsciiLetterLower) ? primary : null;
    }

    public static bool IsEmail(string? value) =>
        value is { Length: > 0 and <= 254 } && !value.Any(char.IsWhiteSpace) && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;

    /// <summary>
    /// El nombre para el campo NAME de Hestia: solo letras, dígitos, espacios, puntos y guiones, hasta 50 caracteres.
    /// Hestia no lo exige, así que si no queda nada no se manda.
    /// </summary>
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

    /// <summary>Una credencial que manda el núcleo: sin espacios ni caracteres de control.</summary>
    public static bool IsPassword(string? value) =>
        value is { Length: >= 8 and <= 128 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}

/// <summary>Armado de los errores del protocolo con los detalles que usa este módulo.</summary>
internal static class Fail
{
    public static ModuleException InvalidInput(IReadOnlyCollection<string> fields, string? message = null, string? reason = null) => new(
        ErrorCodes.InvalidInput,
        message ?? $"Faltan datos o son inválidos: {string.Join(", ", fields)}.",
        details: Details(reason, fields));

    public static ModuleException InvalidConfig(IReadOnlyCollection<string> fields, string? message = null, string? upstream = null) => new(
        ErrorCodes.InvalidConfig,
        message ?? $"La configuración de la instancia no sirve: {string.Join(", ", fields)}.",
        details: Details(null, fields, upstream));

    /// <summary>Hestia no respondió, o fue una lectura: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, string? reason = null, string? upstream = null) =>
        new(ErrorCodes.UpstreamUnavailable, message, 30, Details(reason, null, upstream));

    /// <summary>Resultado incierto: el cambio pudo haberse aplicado. El núcleo repite con la misma clave y la lectura previa decide qué falta.</summary>
    public static ModuleException Unknown(string message, string reason) =>
        new(ErrorCodes.OutcomeUnknown, message, 30, Details(reason, null));

    /// <summary>Hestia rechazó el cambio por sus reglas. details.upstream trae su texto.</summary>
    public static ModuleException Rejected(string message, string reason, string? upstream) =>
        new(ErrorCodes.Rejected, message, details: Details(reason, null, upstream));

    public static ModuleException Conflict(string message, string reason, JsonObject? extra = null)
    {
        var details = Details(reason, null);
        foreach (var (name, value) in extra ?? [])
        {
            details[name] = value?.DeepClone();
        }

        return new ModuleException(ErrorCodes.Conflict, message, 60, details);
    }

    public static ModuleException NotFound(string message) =>
        new(ErrorCodes.NotFound, message, details: Details("absent", null));

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

internal static class Inputs
{
    /// <summary>Lee una parte del sobre. Un tipo mal puesto sale como invalid_input con la ruta, nunca con el valor.</summary>
    public static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidInput([ex.Path ?? "input"]);
        }
    }
}

/// <summary>Lectura tolerante de JSON que no controlamos: lo que no es del tipo esperado da null en vez de una excepción.</summary>
internal static class JsonRead
{
    public static JsonNode? Field(this JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    /// <summary>El texto de un valor. Los números y los booleanos salen tal cual vinieron.</summary>
    public static string? Text(this JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };
}
