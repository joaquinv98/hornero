using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Platform;

/// <summary>Un módulo registrado: URL base, secreto de firma (cifrado) y manifiesto aceptado (sección 2 del protocolo).</summary>
public sealed class ModuleInstallation
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Secreto de firma cifrado con <see cref="SecretBox"/>.</summary>
    public byte[] SigningSecret { get; set; } = [];

    /// <summary>SHA-256 del certificado del módulo, si usa una CA propia.</summary>
    public string? TlsFingerprint { get; set; }

    /// <summary>Manifiesto aceptado. JSON.</summary>
    public string Manifest { get; set; } = "{}";

    /// <summary>
    /// Manifiesto nuevo que cambia esquemas o pide más datos personales: espera a que el staff lo apruebe, y mientras
    /// tanto vale el aceptado (sección 5 del protocolo). JSON.
    /// </summary>
    public string? PendingManifest { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Una configuración concreta de una instalación, con sus secretos y su estado cifrados.</summary>
public sealed class ModuleInstance
{
    public long Id { get; private set; }

    public long InstallationId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Campos no secretos de la configuración. JSON.</summary>
    public string Config { get; set; } = "{}";

    /// <summary>Campos x-secret, en un JSON cifrado.</summary>
    public byte[]? Secrets { get; set; }

    /// <summary>instance_state que devuelve el módulo, cifrado.</summary>
    public byte[]? State { get; set; }

    public int StateVersion { get; set; }

    /// <summary>Hosts que el navegador puede visitar desde URLs del módulo. JSON.</summary>
    public string BrowserHosts { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Credenciales de un servicio que el cliente necesita ver. Cifradas, se muestran con confirmación.</summary>
public sealed class ServiceSecret
{
    public long Id { get; private set; }

    public long ServiceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte[] Value { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Destino de un módulo. Como cada llamada lleva secretos, solo https, o http hacia loopback en el mismo host
/// (sección 4 del protocolo). No se puede armar uno con otra URL: así ningún camino manda secretos por un canal abierto.
/// </summary>
public sealed record ModuleEndpoint
{
    public const string BaseUrlRule = "La URL de un módulo tiene que ser https, o http solo hacia loopback.";

    public ModuleEndpoint(Uri baseUrl, byte[] signingSecret, string? tlsFingerprint)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        BaseUrl = IsAllowed(baseUrl) || baseUrl.OriginalString == UseCases.ManualModule.Address ? baseUrl : throw new UseCases.PermanentFailureException($"{BaseUrlRule} Recibida: {baseUrl}");
        SigningSecret = signingSecret;
        TlsFingerprint = tlsFingerprint;
    }

    public Uri BaseUrl { get; }

    public byte[] SigningSecret { get; }

    public string? TlsFingerprint { get; }

    public static bool IsAllowed(Uri url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));

    /// <summary>La URL tal como se guarda, o null si no cumple la regla.</summary>
    public static Uri? TryParseBaseUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && IsAllowed(url) ? url : null;
}

/// <summary>
/// Lo que pasó con una llamada: respuesta, error tipado o resultado incierto por corte. InstanceState es el estado
/// que el módulo devolvió, venga en la respuesta o en el error: el núcleo lo guarda en los dos casos, así un ticket
/// obtenido en una llamada que después falló no queda solo en la memoria de una réplica (sección 6 del protocolo).
/// </summary>
public sealed record ModuleCallResult(ModuleResponse? Response, ModuleError? Error, JsonNode? ErrorState = null)
{
    public bool IsUncertain => Error?.Code == ErrorCodes.OutcomeUnknown;

    public JsonNode? InstanceState => Response?.InstanceState ?? ErrorState;
}

/// <summary>
/// Cliente del protocolo v0 del lado del núcleo: arma el sobre, firma y clasifica la respuesta. La firma usa el
/// reloj real, no el de negocio: el módulo compara contra su propio reloj (sección 4 del protocolo).
/// </summary>
public sealed class ModuleClient(IServiceScopeFactory scopes)
{
    /// <summary>Tope de la respuesta de un módulo (sección 4 del protocolo).</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private readonly Dictionary<string, HttpClient> clients = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    private const string OperationsPath = "/v0/operations/";

    /// <summary>
    /// Llama al módulo. Las operaciones con efecto, las que llevan clave de idempotencia, quedan en la auditoría
    /// con el actor que las pidió y el resultado (D-15). Una operación larga queda como pending, y su resultado
    /// final se audita cuando el sondeo de /v0/operations/{id} deja de dar pending.
    /// </summary>
    public async Task<ModuleCallResult> Call(ModuleEndpoint endpoint, string path, ModuleRequest? body, string? idempotencyKey, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(path);
        var result = await Send(endpoint, path, body, idempotencyKey, timeout, ct).ConfigureAwait(false);
        var outcome = result.Error?.Code ?? (result.Response?.Status == ModuleResponse.PendingStatus ? "pending" : "ok");
        var poll = path.StartsWith(OperationsPath, StringComparison.Ordinal);
        if (idempotencyKey is not null || (poll && outcome != "pending"))
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<AuditLog>() is not { } audit)
            {
                // Sin núcleo registrado (pruebas del cliente solo): no hay dónde auditar.
                return result;
            }

            await audit.TryRecord(
                $"module {path}",
                endpoint.BaseUrl.ToString(),
                poll
                    ? new { operation_id = path[OperationsPath.Length..], outcome }
                    : new { idempotency_key = idempotencyKey, outcome }).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>El manifiesto del módulo (GET /v0/manifest firmado), tal cual, sin sobre (sección 5 del protocolo).</summary>
    public async Task<JsonObject> Manifest(ModuleEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var request = Signed(endpoint, "/v0/manifest", null, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var response = await Client(endpoint).SendAsync(request, cts.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return response.IsSuccessStatusCode && JsonNode.Parse(text) is JsonObject manifest
            ? manifest
            : throw new UseCases.PermanentFailureException($"El módulo no devolvió su manifiesto (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>Arma el pedido con la firma de la sección 4 del protocolo. Sin cuerpo es un GET.</summary>
    private static HttpRequestMessage Signed(ModuleEndpoint endpoint, string path, byte[]? body, string? idempotencyKey)
    {
        var method = body is null ? HttpMethod.Get : HttpMethod.Post;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        // Relativa a la base, así una base con prefijo (https://host/modulos/proxmox/) lo conserva.
        var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl, path.TrimStart('/')));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, ts.ToString(CultureInfo.InvariantCulture));
        if (idempotencyKey is not null)
        {
            request.Headers.Add(ModuleHeaders.IdempotencyKey, idempotencyKey);
        }

        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(endpoint.SigningSecret, ts, method.Method, path, requestId, idempotencyKey, body ?? []));
        return request;
    }

    private async Task<ModuleCallResult> Send(ModuleEndpoint endpoint, string path, ModuleRequest? body, string? idempotencyKey, TimeSpan timeout, CancellationToken ct)
    {
        using var request = Signed(endpoint, path, body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, ModuleJson.Options), idempotencyKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await Client(endpoint).SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
        {
            return new(null, new ModuleError(ErrorCodes.UpstreamUnavailable, "No se pudo conectar con el módulo.", true));
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            // El módulo respondió, así que no es un resultado incierto: es una falla suya.
            return new(null, new ModuleError(ErrorCodes.Internal, "La respuesta del módulo pasa de 1 MiB.", true));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // El pedido pudo haber llegado: el resultado es incierto (sección 7 del protocolo).
            return new(null, new ModuleError(ErrorCodes.OutcomeUnknown, "El módulo no respondió a tiempo.", true));
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                if (response.IsSuccessStatusCode)
                {
                    return new(JsonSerializer.Deserialize<ModuleResponse>(text, ModuleJson.Options), null);
                }

                var failure = JsonSerializer.Deserialize<ErrorBody>(text, ModuleJson.Options);
                return new(null, failure?.Error ?? new ModuleError(ErrorCodes.Internal, $"HTTP {(int)response.StatusCode} sin cuerpo de error.", true), failure?.InstanceState);
            }
            catch (JsonException)
            {
                return new(null, new ModuleError(ErrorCodes.Internal, $"Respuesta inválida del módulo (HTTP {(int)response.StatusCode}).", response.StatusCode >= HttpStatusCode.InternalServerError));
            }
        }
    }

    private HttpClient Client(ModuleEndpoint endpoint)
    {
        var cacheKey = $"{endpoint.BaseUrl}|{endpoint.TlsFingerprint}";
        lock (gate)
        {
            if (!clients.TryGetValue(cacheKey, out var client))
            {
                var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
                if (endpoint.TlsFingerprint is { } pin)
                {
                    // CA propia del módulo: vale solo el certificado de la huella. Otro con una cadena válida no
                    // alcanza. Nunca hay opción para no validar (sección 4 del protocolo).
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) => PinMatches(pin, cert);
                }

                client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };
                clients[cacheKey] = client;
            }

            return client;
        }
    }

    /// <summary>El certificado es el de la huella (SHA-256 en hexadecimal, con o sin dos puntos) y está vigente.</summary>
    public static bool PinMatches(string pin, X509Certificate? certificate)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var now = DateTime.Now;
        return certificate is X509Certificate2 cert
            && now >= cert.NotBefore
            && now <= cert.NotAfter
            && Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).Equals(pin.Replace(":", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Una contraseña aleatoria para credenciales que genera el núcleo (sección 6 del protocolo).</summary>
public static class Passwords
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.";

    public static string New(int length = 20)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return sb.ToString();
    }
}

/// <summary>Une la configuración visible y los secretos de una instancia en el objeto que va en el sobre.</summary>
public static class InstanceConfig
{
    public static JsonObject Merge(string config, string? secrets)
    {
        var merged = JsonNode.Parse(config)?.AsObject() ?? [];
        if (secrets is not null && JsonNode.Parse(secrets) is JsonObject s)
        {
            foreach (var (k, v) in s.ToList())
            {
                s.Remove(k);
                merged[k] = v;
            }
        }

        return merged;
    }
}
