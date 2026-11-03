using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.PayPal;

/// <summary>
/// Cliente mínimo de la API REST de PayPal, con HttpClient y System.Text.Json y sin la SDK de PayPal. Pide el token
/// OAuth con las credenciales de la app y lo guarda en memoria hasta poco antes de que venza.
/// </summary>
internal sealed class PayPalApi(HttpClient http, ILogger logger, TimeProvider time)
{
    /// <summary>Fijas a propósito: así las credenciales nunca viajan a un destino que alguien configure.</summary>
    public static readonly Uri Live = new("https://api-m.paypal.com/");

    public static readonly Uri Sandbox = new("https://api-m.sandbox.paypal.com/");

    // ponytail: un token por réplica y por app, en memoria. Alcanza porque dura horas. Compartirlo entre réplicas pediría instance_state.
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset Expires)> tokens = new(StringComparer.Ordinal);

    public Task<JsonNode> Get(PayPalAccess access, string path, CancellationToken ct) =>
        Send(access, HttpMethod.Get, path, null, null, false, ct);

    /// <summary>
    /// POST con cuerpo JSON. mutating dice si cambia algo en PayPal: en ese caso un corte después de enviar deja el
    /// resultado incierto. requestId viaja como PayPal-Request-Id, para que repetir el pedido no repita el efecto.
    /// </summary>
    public Task<JsonNode> Post(PayPalAccess access, string path, string json, string? requestId, bool mutating, CancellationToken ct) =>
        Send(access, HttpMethod.Post, path, json, requestId, mutating, ct);

    private async Task<JsonNode> Send(PayPalAccess access, HttpMethod method, string path, string? json, string? requestId, bool mutating, CancellationToken ct)
    {
        var token = await Token(access, ct).ConfigureAwait(false);
        var response = await Exchange(access, method, path, json, requestId, token, mutating, ct).ConfigureAwait(false);
        if (response.Status == 401)
        {
            // El token pudo revocarse antes de tiempo. Un 401 no ejecutó nada, así que se pide otro y se repite una vez.
            tokens.TryRemove(access.CacheKey, out _);
            token = await Token(access, ct).ConfigureAwait(false);
            response = await Exchange(access, method, path, json, requestId, token, mutating, ct).ConfigureAwait(false);
        }

        return Result(response, mutating, $"{method} {path}", [token, access.ClientSecret]);
    }

    /// <summary>POST /v1/oauth2/token con las credenciales de la app (client credentials). Repetirlo no cambia nada.</summary>
    private async Task<string> Token(PayPalAccess access, CancellationToken ct)
    {
        if (tokens.TryGetValue(access.CacheKey, out var cached) && cached.Expires > time.GetUtcNow())
        {
            return cached.Value;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(access.BaseUrl, "/v1/oauth2/token"))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{access.ClientId}:{access.ClientSecret}")));
        var response = await Exchange(request, false, ct).ConfigureAwait(false);
        if (response.Status is 400 or 401)
        {
            throw Fail.InvalidConfig(
                ["client_id", "client_secret"],
                $"PayPal rechazó las credenciales de la app (HTTP {response.Status}). Revisar el client_id, el client_secret y el entorno.");
        }

        var body = Result(response, false, "POST /v1/oauth2/token", [access.ClientSecret]);
        var value = body.Field("access_token").Text();
        var seconds = body.Field("expires_in").Dec() ?? 0m;
        if (string.IsNullOrEmpty(value) || seconds <= 0m)
        {
            throw Fail.Unavailable("PayPal no devolvió un token usable.");
        }

        // Se renueva cinco minutos antes de que venza, para que no se venza a mitad de una operación.
        var expires = time.GetUtcNow().AddSeconds((double)Math.Max(seconds - 300m, 0m));
        tokens[access.CacheKey] = (value, expires);
        return value;
    }

    private Task<Response> Exchange(PayPalAccess access, HttpMethod method, string path, string? json, string? requestId, string token, bool mutating, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, new Uri(access.BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("BillingOss-PayPal/0.1");
        if (requestId is not null)
        {
            request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
        }

        if (json is not null)
        {
            // Sin esto PayPal contesta solo id, estado y enlaces: hacen falta los montos.
            request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return Exchange(request, mutating, ct);
    }

    private async Task<Response> Exchange(HttpRequestMessage request, bool mutating, CancellationToken ct)
    {
        using (request)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            try
            {
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new Response((int)response.StatusCode, (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds, text);
            }
            catch (HttpRequestException ex)
            {
                // Errores de la etapa de conexión: ahí el pedido seguro no salió.
                var sent = ex.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                    or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError);
                throw Cut($"No se pudo comunicar con PayPal: {Short(ex.Message)}", sent && mutating);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Venció el plazo del HttpClient: el pedido ya pudo haber salido.
                throw Cut("PayPal no respondió a tiempo.", mutating);
            }
        }
    }

    private JsonNode Result(Response response, bool mutating, string what, string[] secrets)
    {
        var json = Parse(response.Text);
        if (response.Status is >= 200 and < 300)
        {
            return json ?? throw Unexpected(mutating, $"HTTP {response.Status} sin JSON");
        }

        // Las redirecciones no se siguen: llevarían el token a otro destino.
        if (response.Status < 400)
        {
            throw Unexpected(mutating, $"HTTP {response.Status}");
        }

        throw Failure(response, json, secrets, what);
    }

    /// <summary>Un corte sin respuesta. Si el pedido pudo haberse ejecutado el resultado es incierto, y si no, se repite sin riesgo.</summary>
    private ModuleException Cut(string message, bool uncertain)
    {
        logger.LogWarning("PayPal sin respuesta: {Message}", message);
        return uncertain
            ? Fail.Unknown($"{message} Puede haberse procesado.", "no_response")
            : Fail.Unavailable(message);
    }

    private static ModuleException Unexpected(bool mutating, string what) => mutating
        ? Fail.Unknown($"PayPal respondió algo inesperado ({what}). Puede haberse procesado.", "unexpected_response")
        : Fail.Unavailable($"PayPal respondió algo inesperado ({what}).");

    /// <summary>Convierte un error HTTP de PayPal en el error del protocolo (sección 10).</summary>
    private ModuleException Failure(Response response, JsonNode? body, string[] secrets, string what)
    {
        var status = response.Status;
        var name = body.Field("name").Text();
        var codes = Codes(body, status, secrets);
        var details = Fail.Codes(status, codes, body.Field("debug_id").Text());
        logger.LogWarning(
            "PayPal respondió HTTP {Status} a {What}: {Codes} (debug_id {DebugId})",
            status,
            what,
            string.Join(",", codes.Select(c => c.Code)),
            body.Field("debug_id").Text());
        return status switch
        {
            401 => Fail.InvalidConfig(
                ["client_id", "client_secret"],
                "PayPal rechazó el token de la app (HTTP 401). Revisar las credenciales y el entorno."),

            // Todo 403 de PayPal se llama NOT_AUTHORIZED. Sin más detalle es la app sin la función (por ejemplo Transaction
            // search). Con el issue PERMISSION_DENIED es el recurso, que es de otra app o de otro sistema de la misma cuenta.
            403 when name == "NOT_AUTHORIZED" && !codes.Any(c => c.Code == "PERMISSION_DENIED") => Fail.InvalidConfig(
                ["client_id"],
                $"La app de PayPal no tiene permiso para {what}. Revisar las funciones activadas en la app."),
            404 => new ModuleException(ErrorCodes.NotFound, $"PayPal no encontró el recurso. {Fail.Describe(codes)}", details: details),
            409 => new ModuleException(ErrorCodes.Conflict, $"PayPal tiene otro pedido en curso sobre el recurso. {Fail.Describe(codes)}", 30, details),
            429 => new ModuleException(ErrorCodes.RateLimited, "PayPal limitó los pedidos.", response.RetryAfter is > 0 ? response.RetryAfter : 30),
            >= 500 => Fail.Unavailable($"PayPal respondió HTTP {status}."),
            _ => new ModuleException(ErrorCodes.Rejected, $"PayPal rechazó el pedido (HTTP {status}). {Fail.Describe(codes)}", details: details),
        };
    }

    /// <summary>
    /// Los códigos del error: el issue de cada details[] si vienen, y si no el name y el message de la raíz, o el error y
    /// el error_description del pedido de token. Nunca arrastran el token ni el secret.
    /// </summary>
    private static List<Upstream> Codes(JsonNode? body, int status, string[] secrets)
    {
        var list = body.Field("details").Items()
            .Select(d => new Upstream(Clean(d.Field("issue").Text(), secrets), Clean(d.Field("description").Text(), secrets)))
            .Where(u => u.Code.Length > 0)
            .ToList();
        if (list.Count == 0)
        {
            var code = Clean(body.Field("name").Text() ?? body.Field("error").Text(), secrets);
            list.Add(new Upstream(
                code.Length > 0 ? code : status.ToString(CultureInfo.InvariantCulture),
                Clean(body.Field("message").Text() ?? body.Field("error_description").Text(), secrets)));
        }

        return list;
    }

    private static string Clean(string? text, string[] secrets) =>
        Short(secrets.Aggregate(text, (t, s) => t?.Replace(s, "***", StringComparison.Ordinal)));

    private static string Short(string? text) =>
        text is null ? string.Empty : text.Length <= 300 ? text.Trim() : text[..300].Trim() + "...";

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Response(int Status, int? RetryAfter, string Text);
}
