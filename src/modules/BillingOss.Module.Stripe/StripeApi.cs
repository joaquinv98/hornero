using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Stripe;

/// <summary>
/// Cliente mínimo de la API de Stripe, con HttpClient y System.Text.Json y sin la SDK de Stripe. Los pedidos van como
/// formulario (application/x-www-form-urlencoded) con la notación de corchetes de Stripe, y las respuestas son JSON.
/// </summary>
internal sealed class StripeApi(HttpClient http, ILogger logger)
{
    /// <summary>Versión de la API fijada en cada pedido, así una versión nueva de la cuenta no cambia las respuestas.</summary>
    public const string Version = "2026-09-30.endive";

    /// <summary>Fija a propósito: así la clave nunca viaja a un destino que alguien configure.</summary>
    private static readonly Uri BaseUrl = new("https://api.stripe.com/");

    public Task<JsonNode> Get(string path, string apiKey, CancellationToken ct, params (string Name, string Value)[] query) =>
        Send(HttpMethod.Get, Query(path, query), apiKey, null, null, ct);

    /// <summary>POST que cambia algo en Stripe: si se corta después de enviar, el resultado es incierto.</summary>
    public Task<JsonNode> Post(string path, string apiKey, IEnumerable<(string Name, string Value)> form, string? idempotencyKey, CancellationToken ct) =>
        Send(HttpMethod.Post, path, apiKey, form, idempotencyKey, ct);

    private static string Query(string path, (string Name, string Value)[] query) =>
        query.Length == 0 ? path : $"{path}?{string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"))}";

    private async Task<JsonNode> Send(HttpMethod method, string path, string apiKey, IEnumerable<(string Name, string Value)>? form, string? idempotencyKey, CancellationToken ct)
    {
        var mutating = method != HttpMethod.Get;
        using var request = new HttpRequestMessage(method, new Uri(BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("BillingOss-Stripe/0.1");
        request.Headers.TryAddWithoutValidation("Stripe-Version", Version);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
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
            throw Cut($"No se pudo comunicar con Stripe: {Short(ex.Message)}", sent && mutating);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Venció el plazo del HttpClient: el pedido ya pudo haber salido.
            throw Cut("Stripe no respondió a tiempo.", mutating);
        }

        var json = Parse(text);
        if (status is >= 200 and < 300)
        {
            return json ?? throw Unexpected(mutating, $"HTTP {status} sin JSON");
        }

        // Las redirecciones no se siguen: llevarían la clave a otro destino.
        if (status < 400)
        {
            throw Unexpected(mutating, $"HTTP {status}");
        }

        throw Failure(status, json, apiKey, retryAfter, mutating, $"{method} {path.Split('?')[0]}");
    }

    /// <summary>Un corte sin respuesta. Si el pedido pudo haberse ejecutado el resultado es incierto, y si no, se repite sin riesgo.</summary>
    private ModuleException Cut(string message, bool uncertain)
    {
        logger.LogWarning("Stripe sin respuesta: {Message}", message);
        return uncertain ? Fail.Unknown($"{message} Puede haberse procesado.", "no_response") : Fail.Unavailable(message);
    }

    private static ModuleException Unexpected(bool mutating, string what) => mutating
        ? Fail.Unknown($"Stripe respondió algo inesperado ({what}). Puede haberse procesado.", "unexpected_response")
        : Fail.Unavailable($"Stripe respondió algo inesperado ({what}).");

    /// <summary>Convierte un error HTTP de Stripe en el error del protocolo (sección 10).</summary>
    private ModuleException Failure(int status, JsonNode? body, string apiKey, int? retryAfter, bool mutating, string what)
    {
        var error = body.Field("error");
        var type = error.Field("type").Text();
        var code = Clean(error.Field("code").Text(), apiKey);
        var message = Clean(error.Field("message").Text(), apiKey);
        var param = Clean(error.Field("param").Text(), apiKey);
        logger.LogWarning("Stripe respondió HTTP {Status} a {What}: {Type} {Code}", status, what, type, code);
        var details = new JsonObject
        {
            ["http_status"] = status,
            ["upstream"] = new JsonArray(new JsonObject { ["code"] = code.Length > 0 ? code : type ?? status.ToString(CultureInfo.InvariantCulture), ["message"] = message }),
        };
        foreach (var name in (string[])["decline_code", "advice_code", "network_decline_code"])
        {
            if (error.Field(name).Text() is { Length: > 0 } value)
            {
                details[name] = Clean(value, apiKey);
            }
        }

        if (error.Field("payment_intent").Field("id").Text() is { } intent)
        {
            details["payment_external_id"] = intent;
        }

        return status switch
        {
            401 => Fail.InvalidConfig(["api_key"], "Stripe rechazó la clave (HTTP 401). Revisar que sea la de esta cuenta y que no la hayan revocado."),
            403 => Fail.InvalidConfig(["api_key"], $"A la clave restringida le falta un permiso (HTTP 403). {message}".Trim()),
            404 => new ModuleException(ErrorCodes.NotFound, $"Stripe no encontró el recurso. {message}".Trim(), details: details),
            409 => new ModuleException(ErrorCodes.Conflict, "Stripe tiene en curso otro pedido con la misma clave.", 5, details),
            429 => new ModuleException(ErrorCodes.RateLimited, "Stripe limitó los pedidos.", retryAfter is > 0 ? retryAfter : 30),
            400 when type == "idempotency_error" => new ModuleException(ErrorCodes.IdempotencyConflict, "Stripe ya usó esa clave de idempotencia con otros parámetros.", details: details),
            400 when code == "amount_too_small" => Fail.InvalidInput(["amount"], $"Stripe rechaza el monto por chico. {message}".Trim()),
            400 when param.Length > 0 => Fail.InvalidInput([param], $"Stripe rechazó el parámetro {param}. {message}".Trim()),
            424 or >= 500 => mutating
                ? Fail.Unknown($"Stripe respondió HTTP {status}. Puede haberse procesado.", "upstream_error")
                : Fail.Unavailable($"Stripe respondió HTTP {status}."),
            _ => new ModuleException(ErrorCodes.Rejected, $"Stripe rechazó el pedido (HTTP {status}). {message}".Trim(), details: details),
        };
    }

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
