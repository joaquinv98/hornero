using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.CryptoCloud;

/// <summary>
/// Cliente mínimo de la API v2 de CryptoCloud, con HttpClient y System.Text.Json. Todas las rutas son POST y se
/// autentican con el encabezado Authorization y el esquema Token.
/// </summary>
internal sealed class CryptoCloudApi(HttpClient http, ILogger logger)
{
    /// <summary>
    /// Fija a propósito: así la API key nunca viaja a un destino que alguien configure. Desde el cambio de nombre a
    /// Trybit (junio de 2026) la documentación oficial publica este host.
    /// </summary>
    private static readonly Uri BaseUrl = new("https://api.trybit.com/v2/");

    /// <summary>
    /// Un pedido a la API. Con mutating, un corte después de enviar deja el resultado incierto. La ruta va sin barra
    /// inicial, para que cuelgue de /v2/.
    /// </summary>
    public async Task<JsonNode> Post(string path, string apiKey, JsonNode? body, bool mutating, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
        request.Headers.UserAgent.ParseAdd("BillingOss-CryptoCloud/0.1");
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
        catch (HttpRequestException ex)
        {
            // Errores de la etapa de conexión: ahí el pedido seguro no salió.
            var sent = ex.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError);
            throw Cut($"No se pudo comunicar con CryptoCloud: {Short(ex.Message)}", sent && mutating);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Venció el plazo del HttpClient: el pedido ya pudo haber salido.
            throw Cut("CryptoCloud no respondió a tiempo.", mutating);
        }

        // CryptoCloud contesta { "status": "success", "result": ... } o { "status": "error", "result": { ... } }.
        var json = Parse(text);
        var error = json.Field("status").Text() == "error";
        if (status is >= 200 and < 300 && !error)
        {
            return json ?? throw Unexpected(mutating, $"HTTP {status} sin JSON");
        }

        // Las redirecciones no se siguen: llevarían la API key a otro destino.
        if (status is < 200 or (>= 300 and < 400))
        {
            throw Unexpected(mutating, $"HTTP {status}");
        }

        throw Failure(status, json, apiKey, retryAfter, path, mutating);
    }

    /// <summary>Un corte sin respuesta. Si el pedido pudo haberse ejecutado el resultado es incierto, y si no, se repite sin riesgo.</summary>
    private ModuleException Cut(string message, bool uncertain)
    {
        logger.LogWarning("CryptoCloud sin respuesta: {Message}", message);
        return uncertain
            ? Fail.Unknown($"{message} Puede haberse procesado.", "no_response")
            : Fail.Unavailable(message);
    }

    private static ModuleException Unexpected(bool mutating, string what) => mutating
        ? Fail.Unknown($"CryptoCloud respondió algo inesperado ({what}). Puede haberse procesado.", "unexpected_response")
        : Fail.Unavailable($"CryptoCloud respondió algo inesperado ({what}).");

    /// <summary>
    /// Convierte un error de CryptoCloud en el error del protocolo (sección 10). Un 2xx con status error también
    /// llega acá, y sale como rechazo. El error de autorización es el único documentado, y sin su código HTTP: se
    /// reconoce también por la clave authorization.
    /// </summary>
    private ModuleException Failure(int status, JsonNode? body, string apiKey, int? retryAfter, string what, bool mutating)
    {
        var codes = Codes(body, status, apiKey);
        logger.LogWarning("CryptoCloud respondió HTTP {Status} con error a {What}: {Codes}", status, what, string.Join(",", codes.Select(c => c.Code)));
        return status switch
        {
            _ when status is 401 or 403 || codes.Any(c => c.Code == "authorization") => Fail.InvalidConfig(
                ["api_key"],
                $"CryptoCloud rechazó la api_key (HTTP {status}). Revisar que sea la del proyecto y que siga vigente."),
            404 => new ModuleException(ErrorCodes.NotFound, $"CryptoCloud no encontró el recurso. {Fail.Describe(codes)}", details: Fail.Codes(status, codes)),
            429 => new ModuleException(ErrorCodes.RateLimited, "CryptoCloud limitó los pedidos.", retryAfter is > 0 ? retryAfter : 30),

            // Un 5xx no garantiza que el pedido no se ejecutó: puede venir de un proxy con la factura ya creada.
            >= 500 => mutating
                ? Fail.Unknown($"CryptoCloud respondió HTTP {status}. Puede haberse procesado.", "upstream_5xx")
                : Fail.Unavailable($"CryptoCloud respondió HTTP {status}."),

            // Un shop_id que no es del proyecto de la api_key es un problema de la instancia, no del pedido.
            _ when codes.Any(c => c.Code == "shop_id") => Fail.InvalidConfig(
                ["shop_id"],
                $"CryptoCloud rechazó el shop_id (HTTP {status}). {Fail.Describe(codes)}"),
            _ => new ModuleException(
                ErrorCodes.Rejected,
                $"CryptoCloud rechazó el pedido (HTTP {status}). {Fail.Describe(codes)}",
                details: Fail.Codes(status, codes)),
        };
    }

    /// <summary>
    /// Los errores que trae result: cada clave con su mensaje, que puede ser un texto o una lista de textos. Si no,
    /// el detail de la raíz o el código HTTP. Nunca arrastran la API key.
    /// </summary>
    private static List<Upstream> Codes(JsonNode? body, int status, string apiKey)
    {
        var list = new List<Upstream>();
        switch (body.Field("result"))
        {
            case JsonObject result:
                list.AddRange(result.Select(p => new Upstream(Clean(p.Key, apiKey), Clean(Message(p.Value), apiKey))));
                break;
            case JsonValue value:
                list.Add(new Upstream("error", Clean(value.Text(), apiKey)));
                break;
        }

        if (list.Count == 0 && body.Field("detail").Text() is { } detail)
        {
            list.Add(new Upstream("detail", Clean(detail, apiKey)));
        }

        if (list.Count == 0)
        {
            list.Add(new Upstream(status.ToString(CultureInfo.InvariantCulture), string.Empty));
        }

        return list;
    }

    private static string? Message(JsonNode? value) =>
        value is JsonArray list ? string.Join(" ", list.Select(v => v.Text())) : value.Text();

    private static string Clean(string? text, string apiKey) =>
        Short(text?.Replace(apiKey, "***", StringComparison.Ordinal));

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
}
