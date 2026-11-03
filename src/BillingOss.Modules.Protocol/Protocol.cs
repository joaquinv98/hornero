using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BillingOss.Modules.Protocol;

/// <summary>JSON del protocolo de módulos v0: nombres en snake_case y sin nulos.</summary>
public static class ModuleJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

public static class ModuleHeaders
{
    public const string RequestId = "X-Request-Id";
    public const string Timestamp = "X-Timestamp";
    public const string Signature = "X-Signature";
    public const string IdempotencyKey = "Idempotency-Key";
}

public sealed record InstanceData(string Id, JsonObject Config, JsonNode? State = null);

/// <summary>Sobre de todas las operaciones (sección 6 del protocolo).</summary>
public sealed record ModuleRequest(InstanceData Instance, JsonObject Context, JsonObject Input);

public sealed record ModuleResponse(
    string Status,
    JsonNode? Result = null,
    JsonNode? InstanceState = null,
    string? OperationId = null,
    int? RetryAfter = null)
{
    public const string CompletedStatus = "completed";
    public const string PendingStatus = "pending";

    public static ModuleResponse Completed(JsonNode? result = null, JsonNode? instanceState = null) =>
        new(CompletedStatus, result, instanceState);

    public static ModuleResponse Pending(string operationId, int retryAfter = 10) =>
        new(PendingStatus, OperationId: operationId, RetryAfter: retryAfter);
}

public sealed record ModuleError(string Code, string Message, bool Retryable, int? RetryAfter = null, JsonObject? Details = null);

/// <summary>
/// Respuesta de error. Puede traer instance_state igual que una respuesta buena: un ticket obtenido en una llamada
/// que después falla no se pierde, y el núcleo lo guarda también en el error (sección 6 del protocolo).
/// </summary>
public sealed record ErrorBody(ModuleError Error, JsonNode? InstanceState = null);

/// <summary>Códigos de error de la sección 10 del protocolo.</summary>
public static class ErrorCodes
{
    public const string InvalidSignature = "invalid_signature";
    public const string InvalidConfig = "invalid_config";
    public const string InvalidInput = "invalid_input";
    public const string NotSupported = "not_supported";
    public const string NotFound = "not_found";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";
    public const string RateLimited = "rate_limited";
    public const string UpstreamUnavailable = "upstream_unavailable";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string Internal = "internal";

    /// <summary>Inconsistencia que tiene que revisar el staff, por ejemplo una VM sin la marca del servicio. No se reintenta.</summary>
    public const string ManualReview = "manual_review";

    public static int HttpStatus(string code) => code switch
    {
        InvalidSignature => 401,
        NotFound => 404,
        IdempotencyConflict or Conflict or ManualReview => 409,
        InvalidConfig or InvalidInput or Rejected => 422,
        RateLimited => 429,
        NotSupported => 501,
        UpstreamUnavailable => 503,
        OutcomeUnknown => 504,
        _ => 500,
    };

    /// <summary>Si conviene reintentar. outcome_unknown se reintenta con la misma clave, con cuidado.</summary>
    public static bool IsRetryable(string code) =>
        code is Conflict or RateLimited or UpstreamUnavailable or OutcomeUnknown or Internal;
}

/// <summary>
/// Nombres de dominio como viajan en el protocolo (sección 9.2): minúsculas, sin punto final y con los nombres
/// internacionalizados en punycode. El núcleo y los módulos registrar usan la misma regla.
/// </summary>
public static class DomainNames
{
    public static bool TryNormalize(string? text, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(text.Trim().TrimEnd('.').ToLowerInvariant());
        }
        catch (ArgumentException)
        {
            return false;
        }

        var labels = ascii.Split('.');
        if (ascii.Length > 253 || labels.Length < 2 || !labels.All(IsLabel) || labels[^1].All(char.IsAsciiDigit))
        {
            return false;
        }

        name = ascii;
        return true;
    }

    /// <summary>Lo registrable y su extensión: "ejemplo.com.ar" da ("ejemplo", "com.ar").</summary>
    public static (string Label, string Extension) Split(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? (name[..dot], name[(dot + 1)..]) : throw new ArgumentException($"{name} no tiene extensión.", nameof(name));
    }

    private static bool IsLabel(string label) =>
        label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-'
        && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}

/// <summary>
/// Un registro DNS como viaja en el protocolo (sección 9.2): Name relativo a la zona ("@" es la zona), Type A, AAAA,
/// CNAME, MX o TXT, Value, Ttl en segundos y Priority solo en MX.
/// </summary>
public sealed record DnsRecord(string Name, string Type, string Value, int Ttl = DnsRecords.DefaultTtl, int? Priority = null);

/// <summary>
/// La regla común de los registros DNS: el núcleo la aplica antes de encolar y el módulo al recibir, así los dos
/// comparan la zona igual. Solo se administran estos tipos: los demás (NS, SOA, SRV, CAA) quedan como están.
/// </summary>
public static class DnsRecords
{
    public const int Max = 100;

    public const int DefaultTtl = 3600;

    public static IReadOnlyList<string> Types { get; } = ["A", "AAAA", "CNAME", "MX", "TXT"];

    /// <summary>
    /// Limpia y valida los registros de la zona <paramref name="zone"/>: nombres relativos y en minúsculas, valores en su
    /// forma canónica, TTL de 60 a 86400 y sin repetidos, en un orden fijo para que la misma zona dé la misma entrada.
    /// </summary>
    public static bool TryNormalize(string zone, IEnumerable<DnsRecord> records, out IReadOnlyList<DnsRecord> clean, out string error)
    {
        ArgumentNullException.ThrowIfNull(records);
        clean = [];
        var list = new List<DnsRecord>();
        foreach (var r in records)
        {
            if (Normalize(zone, r, out var record, out error))
            {
                list.Add(record);
                continue;
            }

            return false;
        }

        list = [.. list.Distinct().OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Value, StringComparer.Ordinal)];
        error = list.Count > Max ? $"Van hasta {Max} registros."
            : list.FirstOrDefault(r => r.Type == "CNAME" && r.Name == "@") is not null ? "La zona no lleva CNAME en @: usá un registro A."
            : list.GroupBy(r => r.Name).FirstOrDefault(g => g.Any(r => r.Type == "CNAME") && g.Count() > 1) is { } mixed ? $"{mixed.Key} tiene un CNAME: no puede tener otros registros."
            : string.Empty;
        clean = error.Length == 0 ? list : [];
        return error.Length == 0;
    }

    /// <summary>
    /// Un registro como lo da un registrar: los que no se administran o no se entienden quedan afuera, y un TTL fuera
    /// de rango se lleva al más cercano (para sacarlo alcanzan el nombre, el tipo y el valor).
    /// </summary>
    public static DnsRecord? Read(string zone, string? name, string? type, string? value, int? ttl, int? priority) =>
        Normalize(zone, new DnsRecord(name ?? "@", type ?? string.Empty, value ?? string.Empty, ttl is > 0 ? Math.Clamp(ttl.Value, 60, 86400) : DefaultTtl, priority), out var record, out _)
            ? record
            : null;

    /// <summary>Nombre completo de un registro: "www" en "ejemplo.com" da "www.ejemplo.com" y "@" da la zona.</summary>
    public static string FullName(string zone, string name) => name == "@" ? zone : $"{name}.{zone}";

    private static bool Normalize(string zone, DnsRecord r, out DnsRecord record, out string error)
    {
        record = r;
        var type = (r.Type ?? string.Empty).Trim().ToUpperInvariant();
        var name = (r.Name ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        name = name.Length == 0 || name == "@" || name == zone ? "@" : name.EndsWith("." + zone, StringComparison.Ordinal) ? name[..^(zone.Length + 1)] : name;
        if (!Types.Contains(type))
        {
            error = $"{r.Type} no es un tipo de registro que se administre acá ({string.Join(", ", Types)}).";
            return false;
        }

        if (name != "@" && !name.Split('.').Select((l, i) => (l, i)).All(p => (p.i == 0 && p.l == "*") || IsLabel(p.l)))
        {
            error = $"{r.Name} no es un nombre de registro válido.";
            return false;
        }

        var value = (r.Value ?? string.Empty).Trim();
        int? priority = null;
        switch (type)
        {
            case "A" when IsIpv4(value):
                break;
            case "AAAA" when System.Net.IPAddress.TryParse(value, out var v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6:
                value = v6.ToString();
                break;
            case "CNAME" or "MX" when DomainNames.TryNormalize(value, out var host):
                value = host;
                priority = type == "MX" ? r.Priority ?? 10 : null;
                break;
            case "TXT" when Text(value) is { } text:
                value = text;
                break;
            default:
                error = $"{value} no es un valor válido para un registro {type}.";
                return false;
        }

        if (priority is < 0 or > 65535 || r.Ttl is not (0 or (>= 60 and <= 86400)))
        {
            error = "El TTL va de 60 a 86400 segundos y la prioridad, de 0 a 65535.";
            return false;
        }

        record = new DnsRecord(name, type, value, r.Ttl == 0 ? DefaultTtl : r.Ttl, priority);
        error = string.Empty;
        return true;
    }

    /// <summary>Etiqueta de un nombre de registro: como la de un dominio, más el guion bajo de _dmarc o _domainkey.</summary>
    private static bool IsLabel(string label) =>
        label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-'
        && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    private static bool IsIpv4(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p, CultureInfo.InvariantCulture) <= 255 && (p.Length == 1 || p[0] != '0'));
    }

    /// <summary>El texto sin las comillas con que se suele pegar, hasta 2048 caracteres y sin caracteres de control.</summary>
    private static string? Text(string text)
    {
        var unquoted = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
        return unquoted.Length is > 0 and <= 2048 && !unquoted.Any(char.IsControl) ? unquoted : null;
    }
}

/// <summary>Un reenvío de correo del dominio: la casilla (la parte antes de la arroba) y a dónde va.</summary>
public sealed record EmailForward(string Mailbox, string To);

/// <summary>La regla común de los reenvíos de correo (sección 9.2): una casilla, un destino.</summary>
public static class EmailForwards
{
    public const int Max = 50;

    public static bool TryNormalize(IEnumerable<EmailForward> forwards, out IReadOnlyList<EmailForward> clean, out string error)
    {
        ArgumentNullException.ThrowIfNull(forwards);
        clean = [];
        var list = new List<EmailForward>();
        foreach (var f in forwards)
        {
            var mailbox = (f.Mailbox ?? string.Empty).Trim().ToLowerInvariant();
            var to = (f.To ?? string.Empty).Trim();
            if (mailbox.Length is 0 or > 64 || mailbox[0] == '.' || mailbox[^1] == '.'
                || !mailbox.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or '+'))
            {
                error = $"{f.Mailbox} no es una casilla válida: va la parte antes de la arroba.";
                return false;
            }

            if (!System.Net.Mail.MailAddress.TryCreate(to, out var address) || address.Address != to || !address.Host.Contains('.', StringComparison.Ordinal))
            {
                error = $"{f.To} no es un correo válido.";
                return false;
            }

            list.Add(new EmailForward(mailbox, to));
        }

        list = [.. list.Distinct().OrderBy(f => f.Mailbox, StringComparer.Ordinal)];
        error = list.Count > Max ? $"Van hasta {Max} reenvíos."
            : list.GroupBy(f => f.Mailbox).FirstOrDefault(g => g.Count() > 1) is { } repeated ? $"{repeated.Key} reenvía a un solo destino."
            : string.Empty;
        clean = error.Length == 0 ? list : [];
        return error.Length == 0;
    }
}

/// <summary>Firma HMAC-SHA256 de los pedidos del núcleo (sección 4 del protocolo).</summary>
public static class ModuleSignature
{
    public const string Prefix = "v0=";

    public static TimeSpan Window { get; } = TimeSpan.FromSeconds(300);

    public static string Compute(
        ReadOnlySpan<byte> secret,
        long timestamp,
        string method,
        string path,
        string requestId,
        string? idempotencyKey,
        ReadOnlySpan<byte> body)
    {
        var text = string.Join(
            '\n',
            timestamp.ToString(CultureInfo.InvariantCulture),
            method.ToUpperInvariant(),
            path,
            requestId,
            idempotencyKey ?? string.Empty,
            Convert.ToHexStringLower(SHA256.HashData(body)));
        return Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Verifica contra cualquiera de los secretos vigentes, para permitir la rotación.</summary>
    public static bool Verify(
        IEnumerable<byte[]> secrets,
        string? signature,
        string? timestamp,
        DateTimeOffset now,
        string method,
        string path,
        string requestId,
        string? idempotencyKey,
        ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (signature is null
            || !signature.StartsWith(Prefix, StringComparison.Ordinal)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)
            || Math.Abs(now.ToUnixTimeSeconds() - ts) > Window.TotalSeconds
            || !IsValidPath(path))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(signature);
        foreach (var secret in secrets)
        {
            var actual = Encoding.ASCII.GetBytes(Compute(secret, ts, method, path, requestId, idempotencyKey, body));
            if (CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rutas de v0: solo minúsculas, dígitos, guion, guion bajo y barra, sin query.</summary>
    public static bool IsValidPath(string path) =>
        path.StartsWith("/v0/", StringComparison.Ordinal)
        && path.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '/');
}
