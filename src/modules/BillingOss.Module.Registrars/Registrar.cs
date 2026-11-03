using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Registrars;

/// <summary>Un dominio ya normalizado (punycode y minúsculas), con su etiqueta y su extensión.</summary>
internal sealed record DomainName(string Name)
{
    public string Label => DomainNames.Split(Name).Label;

    /// <summary>Sin el punto inicial: "com.ar".</summary>
    public string Extension => DomainNames.Split(Name).Extension;

    public override string ToString() => Name;
}

/// <summary>El dominio sobre el que se opera, con los IDs que devolvió el registrar la última vez.</summary>
internal sealed record DomainRef(DomainName Name, JsonObject ExternalIds)
{
    public string? Id(string key) => ExternalIds[key]?.ToString() is { Length: > 0 } v ? v : null;
}

/// <summary>Titular del dominio como lo manda el núcleo en context.contacts.registrant (sección 9.2 del protocolo).</summary>
internal sealed record Contact(
    string FirstName,
    string LastName,
    string? Company,
    string Email,
    string Phone,
    string Address1,
    string? Address2,
    string City,
    string? State,
    string PostalCode,
    string Country)
{
    /// <summary>"+54.1155551234" en código de país y número.</summary>
    public (string CountryCode, string Number) SplitPhone()
    {
        var dot = Phone.IndexOf('.', StringComparison.Ordinal);
        return dot > 1 ? (Phone[1..dot], Phone[(dot + 1)..]) : ("54", Phone.TrimStart('+'));
    }

    /// <summary>Provincia o, si falta, la ciudad: hay registrars que la piden siempre.</summary>
    public string Region => string.IsNullOrWhiteSpace(State) ? City : State;
}

internal sealed record Availability(DomainName Name, bool? Available, bool Premium = false, decimal? PremiumPrice = null, string? Currency = null, string? Reason = null);

/// <summary>
/// Lo que dice el registrar de un dominio. Status: active, pending, pending_transfer, expired, redemption,
/// transferred_away, failed, not_found o unknown. Privacy es la protección de identidad, nula si no se sabe.
/// </summary>
internal sealed record DomainState(string Status, DateOnly? ExpiresOn, IReadOnlyList<string> Nameservers, bool? Locked, JsonObject ExternalIds, bool? Privacy = null)
{
    public static DomainState NotFound { get; } = new("not_found", null, [], null, []);
}

/// <summary>Costo de un TLD en el registrar para un período en años. Null es que no se ofrece.</summary>
internal sealed record CostPrice(string Tld, string Currency, int Years, decimal? Register, decimal? Renew, decimal? Transfer);

internal sealed record RegisterOrder(DomainName Name, int Years, IReadOnlyList<string> Nameservers, Contact Registrant, bool Privacy);

internal sealed record TransferOrder(DomainName Name, string AuthCode, int Years, IReadOnlyList<string> Nameservers, Contact Registrant);

/// <summary>Una llamada en curso: configuración de la instancia, estado guardado por el núcleo y el estado nuevo.</summary>
internal sealed class Session(ModuleCall call)
{
    public JsonObject Config => call.Request.Instance.Config;

    public JsonNode? State => call.Request.Instance.State;

    /// <summary>Lo que el módulo quiere que el núcleo guarde cifrado y le devuelva en la próxima llamada.</summary>
    public JsonNode? NewState { get; set; }

    public CancellationToken Ct => call.Aborted;

    public string Text(string key) =>
        Config[key]?.ToString() is { Length: > 0 } value ? value : throw Fail.Config($"Falta {key} en la configuración de la instancia.", key);

    public bool Flag(string key) => Config[key] is JsonValue v && v.GetValueKind() == JsonValueKind.True;
}

/// <summary>
/// Un registrar con API. Cada uno traduce las operaciones de la sección 9.2 del protocolo a su API, y lo que no
/// sabe hacer da not_supported y no figura en sus capacidades.
/// </summary>
internal abstract class Registrar
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract JsonObject ConfigSchema { get; }

    /// <summary>Además de registrar.core, que es obligatoria.</summary>
    public abstract IReadOnlyList<string> Extras { get; }

    public abstract Task Test(Session s);

    public abstract Task<IReadOnlyList<Availability>> Check(Session s, IReadOnlyList<DomainName> names);

    /// <summary>Registra. Si el registro no es inmediato devuelve el estado pending.</summary>
    public abstract Task<DomainState> Register(Session s, RegisterOrder order);

    /// <summary>Renueva y devuelve el vencimiento nuevo.</summary>
    public abstract Task<DateOnly> Renew(Session s, DomainRef domain, int years, DateOnly currentExpiry);

    /// <summary>Estado del dominio en la cuenta. Si no está en la cuenta, <see cref="DomainState.NotFound"/>.</summary>
    public abstract Task<DomainState> Info(Session s, DomainRef domain);

    public abstract Task SetNameservers(Session s, DomainRef domain, IReadOnlyList<string> nameservers);

    public virtual Task<DomainState> Transfer(Session s, TransferOrder order) => throw Fail.Unsupported(Name, "transfer");

    public virtual Task SetLock(Session s, DomainRef domain, bool locked) => throw Fail.Unsupported(Name, "set_lock");

    public virtual Task<string> AuthCode(Session s, DomainRef domain) => throw Fail.Unsupported(Name, "get_auth_code");

    public virtual Task SetContacts(Session s, DomainRef domain, Contact registrant) => throw Fail.Unsupported(Name, "set_contacts");

    public virtual Task<IReadOnlyList<CostPrice>> Prices(Session s, string? currency) => throw Fail.Unsupported(Name, "get_prices");

    /// <summary>Los registros de la zona de los tipos que se administran (<see cref="DnsRecords.Types"/>).</summary>
    public virtual Task<IReadOnlyList<DnsRecord>> Dns(Session s, DomainRef domain) => throw Fail.Unsupported(Name, "get_dns");

    /// <summary>
    /// Deja la zona con estos registros: saca lo que sobra y agrega lo que falta, sin tocar los tipos que no se
    /// administran. Como parte de lo que hay, repetirlo después de un corte termina el trabajo.
    /// </summary>
    public virtual Task SetDns(Session s, DomainRef domain, IReadOnlyList<DnsRecord> records) => throw Fail.Unsupported(Name, "set_dns");

    public virtual Task<IReadOnlyList<EmailForward>> Forwards(Session s, DomainRef domain) => throw Fail.Unsupported(Name, "get_email_forwards");

    /// <summary>Deja estos reenvíos, igual que <see cref="SetDns"/>.</summary>
    public virtual Task SetForwards(Session s, DomainRef domain, IReadOnlyList<EmailForward> forwards) => throw Fail.Unsupported(Name, "set_email_forwards");

    /// <summary>Prende o apaga la protección de los datos del titular. Hay registrars que reenvían al correo del titular.</summary>
    public virtual Task SetPrivacy(Session s, DomainRef domain, bool enabled, string? registrantEmail) => throw Fail.Unsupported(Name, "set_privacy");

    /// <summary>
    /// Un reenvío como lo da un registrar: la casilla sale del origen ("info@ejemplo.com" o "info") y, si hay varios
    /// destinos, vale el primero. Lo que no se entiende queda afuera.
    /// </summary>
    protected static EmailForward? ReadForward(string zone, string? source, string? destination)
    {
        var mailbox = (source ?? string.Empty).Trim().ToLowerInvariant();
        if (mailbox.Split('@') is [var local, var host])
        {
            if (host != zone)
            {
                return null;
            }

            mailbox = local;
        }

        var to = (destination ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return EmailForwards.TryNormalize([new EmailForward(mailbox, to)], out var clean, out _) ? clean[0] : null;
    }

    /// <summary>Qué sacar y qué agregar para pasar de lo que hay a lo pedido. Un cambio de TTL saca el viejo y agrega el nuevo.</summary>
    protected static (IReadOnlyList<T> Remove, IReadOnlyList<T> Add) Diff<T>(IEnumerable<T> current, IReadOnlyList<T> desired)
    {
        var now = current.ToList();
        return ([.. now.Where(c => !desired.Contains(c))], [.. desired.Where(d => !now.Contains(d))]);
    }

    protected static DateOnly? Date(string? text, params string[] formats)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Solo la fecha: algunos agregan la hora ("2027-10-28 10:00:00").
        var day = text.Trim().Split(' ', 'T')[0];
        return DateOnly.TryParseExact(day, formats.Length > 0 ? formats : ["yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    protected static decimal? Money(JsonNode? node) =>
        node?.ToString() is { Length: > 0 } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// El tramo HTTP con el registrar. Un corte antes de enviar se repite sin riesgo, y uno después de enviar un pedido
/// con efecto deja el resultado incierto (sección 7 del protocolo).
/// </summary>
internal sealed class RegistrarHttp(HttpClient http, ILogger logger)
{
    public async Task<(int Status, string Body, int? RetryAfter)> Send(string registrar, HttpRequestMessage request, bool mutating, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.UserAgent.ParseAdd("BillingOss-Registrars/0.1");
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                // Nunca se siguen: llevarían las credenciales a otro destino.
                throw mutating
                    ? Fail.Unknown($"{registrar} respondió una redirección (HTTP {status}). Puede haberse procesado.")
                    : Fail.Unavailable($"{registrar} respondió una redirección (HTTP {status}).");
            }

            if (status == 429)
            {
                throw Fail.Limited($"{registrar} limitó los pedidos.", (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60);
            }

            return (status, body, (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            var notSent = ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;
            logger.LogWarning("{Registrar} sin respuesta: {Error}", registrar, ex.HttpRequestError);
            throw mutating && !notSent
                ? Fail.Unknown($"Se cortó la comunicación con {registrar}. Puede haberse procesado.")
                : Fail.Unavailable($"No se pudo comunicar con {registrar}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("{Registrar} no respondió a tiempo", registrar);
            throw mutating
                ? Fail.Unknown($"{registrar} no respondió a tiempo. Puede haberse procesado.")
                : Fail.Unavailable($"{registrar} no respondió a tiempo.");
        }
    }

    /// <summary>Un error HTTP sin cuerpo que el registrar explique.</summary>
    public static ModuleException Status(string registrar, int status, bool mutating) => status switch
    {
        401 or 403 => Fail.Config($"{registrar} rechazó las credenciales (HTTP {status})."),
        404 => new ModuleException(ErrorCodes.NotFound, $"{registrar} no encontró el recurso."),
        >= 500 when mutating => Fail.Unknown($"{registrar} respondió HTTP {status}. Puede haberse procesado."),
        >= 500 => Fail.Unavailable($"{registrar} respondió HTTP {status}."),
        _ => Fail.Rejected($"{registrar} rechazó el pedido (HTTP {status}).", null, status.ToString(CultureInfo.InvariantCulture)),
    };
}

/// <summary>Errores del protocolo (sección 10) con los datos que el núcleo muestra al staff. Nunca llevan credenciales.</summary>
internal static class Fail
{
    public static ModuleException Config(string message, params string[] fields) =>
        new(ErrorCodes.InvalidConfig, message, details: fields.Length == 0 ? null : new JsonObject { ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)f)]) });

    public static ModuleException Input(string message, params string[] fields) =>
        new(ErrorCodes.InvalidInput, message, details: new JsonObject { ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)f)]) });

    /// <summary>El registro dijo que no, por sus reglas. reason es propio y estable: taken, invalid_auth_code, failed.</summary>
    public static ModuleException Rejected(string message, string? reason, string? code = null) =>
        new(ErrorCodes.Rejected, message, details: new JsonObject { ["upstream"] = new JsonObject { ["reason"] = reason, ["code"] = code } });

    public static ModuleException Taken(string registrar, DomainName name, string? code = null) =>
        Rejected($"{registrar} dice que {name} no está disponible.", "taken", code);

    public static ModuleException NotFound(string message) => new(ErrorCodes.NotFound, message);

    public static ModuleException Unavailable(string message, int? retryAfter = 60) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    public static ModuleException Unknown(string message) => new(ErrorCodes.OutcomeUnknown, message, 60);

    public static ModuleException Limited(string message, int retryAfter) => new(ErrorCodes.RateLimited, message, retryAfter);

    public static ModuleException Unsupported(string registrar, string operation) =>
        new(ErrorCodes.NotSupported, $"{registrar} no tiene {operation} en su API.");

    /// <summary>El texto del registrar, recortado. Lo que muestra nunca viene de la configuración.</summary>
    public static string Short(string? text) => text is null ? string.Empty : text.Length > 300 ? text[..300] : text;
}
