using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.MercadoPago;

/// <summary>Cliente mínimo de la API de Mercado Pago, con HttpClient y System.Text.Json y sin la SDK de Mercado Pago.</summary>
internal sealed class MercadoPagoApi(HttpClient http, ILogger logger)
{
    /// <summary>Fija a propósito: así el access token nunca viaja a un destino que alguien configure.</summary>
    private static readonly Uri BaseUrl = new("https://api.mercadopago.com/");

    public Task<JsonNode> Get(string path, string token, CancellationToken ct) =>
        Send(HttpMethod.Get, path, token, null, null, ct);

    /// <summary>POST que cambia algo en Mercado Pago: si se corta después de enviar, el resultado es incierto.</summary>
    public Task<JsonNode> Post(string path, string token, JsonNode? body, string? idempotencyKey, CancellationToken ct) =>
        Send(HttpMethod.Post, path, token, body, idempotencyKey, ct);

    /// <summary>PUT que cambia algo en Mercado Pago, como el estado o el monto de una suscripción.</summary>
    public Task<JsonNode> Put(string path, string token, JsonNode body, string? idempotencyKey, CancellationToken ct) =>
        Send(HttpMethod.Put, path, token, body, idempotencyKey, ct);

    private async Task<JsonNode> Send(HttpMethod method, string path, string token, JsonNode? body, string? idempotencyKey, CancellationToken ct)
    {
        var mutating = method != HttpMethod.Get;
        using var request = new HttpRequestMessage(method, new Uri(BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("BillingOss-MercadoPago/0.1");
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
        }

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
            throw Cut($"No se pudo comunicar con Mercado Pago: {Short(ex.Message)}", sent && mutating);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Venció el plazo del HttpClient: el pedido ya pudo haber salido.
            throw Cut("Mercado Pago no respondió a tiempo.", mutating);
        }

        var json = Parse(text);
        if (status is >= 200 and < 300)
        {
            return json ?? throw Unexpected(mutating, $"HTTP {status} sin JSON");
        }

        // Las redirecciones no se siguen: llevarían el access token a otro destino.
        if (status < 400)
        {
            throw Unexpected(mutating, $"HTTP {status}");
        }

        throw Failure(status, json, token, retryAfter, $"{method} {path}");
    }

    /// <summary>Un corte sin respuesta. Si el pedido pudo haberse ejecutado el resultado es incierto, y si no, se repite sin riesgo.</summary>
    private ModuleException Cut(string message, bool uncertain)
    {
        logger.LogWarning("Mercado Pago sin respuesta: {Message}", message);
        return uncertain
            ? Fail.Unknown($"{message} Puede haberse procesado.", "no_response")
            : Fail.Unavailable(message);
    }

    private static ModuleException Unexpected(bool mutating, string what) => mutating
        ? Fail.Unknown($"Mercado Pago respondió algo inesperado ({what}). Puede haberse procesado.", "unexpected_response")
        : Fail.Unavailable($"Mercado Pago respondió algo inesperado ({what}).");

    /// <summary>Convierte un error HTTP de Mercado Pago en el error del protocolo (sección 10).</summary>
    private ModuleException Failure(int status, JsonNode? body, string token, int? retryAfter, string what)
    {
        var causes = Causes(body, status, token);
        logger.LogWarning("Mercado Pago respondió HTTP {Status} a {What}: {Codes}", status, what, string.Join(",", causes.Select(c => c.Code)));
        return status switch
        {
            401 or 403 => Fail.InvalidConfig(
                ["access_token"],
                $"Mercado Pago rechazó el access_token (HTTP {status}). Revisar que sea el de la aplicación y que no haya vencido."),
            404 => new ModuleException(ErrorCodes.NotFound, $"Mercado Pago no encontró el recurso. {Fail.Describe(causes)}", details: Fail.Codes(status, causes)),
            409 when causes.Any(c => c.Code == "idempotency_key_already_used") => new ModuleException(
                ErrorCodes.IdempotencyConflict,
                "Mercado Pago ya usó esa clave de idempotencia con otro pedido.",
                details: Fail.Codes(status, causes)),
            429 => new ModuleException(ErrorCodes.RateLimited, "Mercado Pago limitó los pedidos.", retryAfter is > 0 ? retryAfter : 30),
            >= 500 => Fail.Unavailable($"Mercado Pago respondió HTTP {status}."),
            _ => new ModuleException(
                ErrorCodes.Rejected,
                $"Mercado Pago rechazó el pedido (HTTP {status}). {Fail.Describe(causes)}",
                details: Fail.Codes(status, causes)),
        };
    }

    /// <summary>
    /// Los códigos del error: cause[] (API de pagos) o errors[] (API de órdenes) si vienen, y si no el code o el error
    /// de la raíz con su message, que es lo que contesta el filtro de políticas de Mercado Pago. Nunca arrastran el
    /// access token.
    /// </summary>
    private static List<Upstream> Causes(JsonNode? body, int status, string token)
    {
        var list = new List<Upstream>();
        if (body.Field("cause") is JsonArray causes)
        {
            list.AddRange(causes.Select(c => new Upstream(Clean(c.Field("code").Text(), token), Clean(c.Field("description").Text(), token))));
        }

        if (body.Field("errors") is JsonArray errors)
        {
            list.AddRange(errors.Select(e => new Upstream(Clean(e.Field("code").Text(), token), Clean(e.Field("message").Text(), token))));
        }

        if (list.Count == 0)
        {
            var code = Clean(body.Field("code").Text() ?? body.Field("error").Text(), token);
            list.Add(new Upstream(code.Length > 0 ? code : status.ToString(CultureInfo.InvariantCulture), Clean(body.Field("message").Text(), token)));
        }

        return list;
    }

    private static string Clean(string? text, string token) =>
        Short(text?.Replace(token, "***", StringComparison.Ordinal));

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
