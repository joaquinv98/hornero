using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.TacticalRmm;

/// <summary>
/// Cliente mínimo de la API REST de Tactical RMM, con HttpClient y System.Text.Json. Autentica con el encabezado
/// X-API-KEY. Nunca loguea cuerpos ni la key, y nunca arrastra una respuesta cruda a un mensaje.
/// </summary>
internal sealed class TacticalApi(HttpClient http, ILogger logger)
{
    public Task<JsonNode> Get(TacticalSettings settings, string path, CancellationToken ct) =>
        Send(settings, HttpMethod.Get, path, null, ct);

    /// <summary>Pedido que cambia algo en Tactical: si se corta después de enviar, el resultado es incierto.</summary>
    public Task<JsonNode> Post(TacticalSettings settings, string path, JsonNode body, CancellationToken ct) =>
        Send(settings, HttpMethod.Post, path, body, ct);

    public Task<JsonNode> Delete(TacticalSettings settings, string path, CancellationToken ct) =>
        Send(settings, HttpMethod.Delete, path, null, ct);

    private async Task<JsonNode> Send(TacticalSettings settings, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var mutating = method != HttpMethod.Get;
        using var request = new HttpRequestMessage(method, new Uri(settings.ApiUrl, path));
        request.Headers.TryAddWithoutValidation("X-API-KEY", settings.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("BillingOss-TacticalRmm/0.1");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        int status;
        int? retryAfter;
        string text;
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;
            retryAfter = (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds;
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            // Un certificado que no valida no se arregla reintentando, y nunca se desactiva la validación.
            logger.LogWarning("Tactical RMM: TLS rechazado en {Method} {Path}", method, path);
            throw Fail.Config(["api_url"], "No se pudo validar el certificado TLS de Tactical RMM.", "tls");
        }
        catch (HttpRequestException ex)
        {
            // Errores de la etapa de conexión: ahí el pedido seguro no salió.
            var sent = ex.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.ProxyTunnelError);
            throw Cut($"No se pudo comunicar con Tactical RMM ({ex.HttpRequestError}).", sent && mutating, method, path);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Venció el plazo del HttpClient: el pedido ya pudo haber salido.
            throw Cut("Tactical RMM no respondió a tiempo.", mutating, method, path);
        }

        var json = Parse(text);
        if (status is >= 200 and < 300)
        {
            // Tactical contesta JSON hasta en los textos de éxito. Un 200 sin JSON es otra cosa delante (una página
            // de error, un portal) y no prueba que el pedido se haya ejecutado.
            return json ?? throw Unexpected(mutating, $"HTTP {status} sin JSON");
        }

        // Las redirecciones no se siguen: llevarían la API key a otro destino.
        if (status < 400)
        {
            throw Unexpected(mutating, $"HTTP {status}");
        }

        logger.LogWarning("Tactical RMM respondió HTTP {Status} a {Method} {Path}", status, method, path);
        throw Failure(status, json, settings.ApiKey, retryAfter, mutating);
    }

    private ModuleException Cut(string message, bool uncertain, HttpMethod method, string path)
    {
        logger.LogWarning("Tactical RMM sin respuesta en {Method} {Path}", method, path);
        return uncertain
            ? Fail.Unknown($"{message} Puede haberse procesado.", "no_response")
            : Fail.Unavailable(message);
    }

    private static ModuleException Unexpected(bool mutating, string what) => mutating
        ? Fail.Unknown($"Tactical RMM respondió algo inesperado ({what}). Puede haberse procesado.", "unexpected_response")
        : Fail.Unavailable($"Tactical RMM respondió algo inesperado ({what}).");

    /// <summary>
    /// Convierte un error HTTP de Tactical en el error del protocolo (sección 10). Un 403 o un 404 sin JSON no son de
    /// Tactical: los contesta el proxy de delante o la URL está mal, y por eso no se toman como permiso ni como
    /// recurso inexistente. Así una baja nunca da por borrado un cliente que el módulo no pudo ver.
    /// </summary>
    private static ModuleException Failure(int status, JsonNode? json, string key, int? retryAfter, bool mutating) => status switch
    {
        401 => Fail.Config(["api_key"], "Tactical RMM rechazó la API key (HTTP 401).", "api_key"),
        403 when json is not null => Fail.Config(
            ["api_key"],
            "Tactical RMM rechazó el pedido (HTTP 403): la API key no vale o a su rol le falta un permiso.",
            "api_key_o_permisos"),
        403 => Fail.Config(
            ["api_url"],
            "El proxy de Tactical RMM rechazó el pedido (HTTP 403 sin JSON): la IP del módulo no está entre las de administración.",
            "ip_no_permitida"),
        404 when json is not null => new ModuleException(ErrorCodes.NotFound, "Tactical RMM no encontró el recurso."),
        404 => Fail.Config(["api_url"], "La ruta no existe (HTTP 404 sin JSON): revisar api_url.", "api_url"),
        429 => new ModuleException(ErrorCodes.RateLimited, "Tactical RMM limitó los pedidos.", retryAfter is > 0 ? retryAfter : 30),
        >= 500 when mutating => Fail.Unknown($"Tactical RMM respondió HTTP {status}. Puede haberse procesado.", "server_error"),
        >= 500 => Fail.Unavailable($"Tactical RMM respondió HTTP {status}."),
        _ => Rejected(status, Upstream(json, status, key)),
    };

    private static ModuleException Rejected(int status, List<(string Code, string Message)> upstream)
    {
        var text = string.Join(" ", upstream.Select(u => $"[{u.Code}] {u.Message}"));
        return new ModuleException(
            ErrorCodes.Rejected,
            Short($"Tactical RMM rechazó el pedido (HTTP {status}). {text}", 500),
            details: new JsonObject
            {
                ["http_status"] = status,
                ["upstream"] = new JsonArray(upstream
                    .Select(u => (JsonNode?)new JsonObject { ["code"] = u.Code, ["message"] = u.Message })
                    .ToArray()),
            });
    }

    /// <summary>
    /// Los errores de Tactical (Django REST Framework): un texto suelto, una lista de textos o un objeto con un texto
    /// o una lista por campo. Se tapan la key y los textos largos, y se toman hasta diez.
    /// </summary>
    private static List<(string Code, string Message)> Upstream(JsonNode? json, int status, string key)
    {
        var code = status.ToString(CultureInfo.InvariantCulture);
        var list = json switch
        {
            JsonObject obj => obj.Select(p => (p.Key, Join(p.Value))).ToList(),
            JsonArray or JsonValue => [(code, Join(json))],
            _ => [(code, string.Empty)],
        };
        return list.Take(10).Select(u => (Clean(u.Item1, key), Clean(u.Item2, key))).ToList();
    }

    private static string Join(JsonNode? node) => node switch
    {
        JsonArray items => string.Join(" ", items.Select(Join)),
        JsonValue value => value.Text() ?? string.Empty,
        _ => string.Empty,
    };

    private static string Clean(string text, string key) => Short(text.Replace(key, "***", StringComparison.Ordinal), 200);

    private static string Short(string text, int max) => text.Length <= max ? text.Trim() : text[..max].Trim() + "...";

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
}
