using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BillingOss.App.UseCases;
using Microsoft.Extensions.Primitives;

namespace BillingOss.Web.Api;

/// <summary>Lo que ven las pasarelas: los avisos de pago (sección 9.3 del protocolo).</summary>
internal static class Gateways
{
    public const string WebhookLimiter = "webhooks";

    /// <summary>Tope del cuerpo de un aviso: lo que pasa de acá no se guarda. Las pasarelas reales mandan pocos KiB.</summary>
    public const int MaxWebhookBody = 64 * 1024;

    public static void MapGateways(this WebApplication app)
    {
        app.MapPost("/webhooks/{instanceId:long}", async (long instanceId, HttpRequest request, WebhookInbox inbox, WebhookInstances known, CancellationToken ct) =>
        {
            using var body = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (body.Length + read > MaxWebhookBody)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                body.Write(chunk, 0, read);
            }

            // Sin Cookie ni Authorization: no le sirven al módulo y no se guardan credenciales ajenas.
            var headers = Collect(request.Headers.Where(h => !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)));
            if (!await inbox.Receive(instanceId, headers, Collect(request.Query), body.ToArray(), ct))
            {
                return Results.NotFound();
            }

            known.Add(instanceId);
            return Results.Ok();
        }).RequireRateLimiting(WebhookLimiter);

        // La vuelta del cliente desde la pasarela, /pay/return/{referencia}, es una página de la interfaz
        // (Pages/PayReturn): solo muestra el estado, el pago se confirma por aviso o por consulta.
    }

    /// <summary>Un texto por nombre, o una lista si el nombre vino repetido, como lo pide verify_webhook.</summary>
    private static JsonObject Collect(IEnumerable<KeyValuePair<string, StringValues>> values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            result[name] = value.Count == 1 ? value[0] : new JsonArray([.. value.Select(v => (JsonNode?)v)]);
        }

        return result;
    }
}

/// <summary>
/// Instancias que ya recibieron un aviso válido en este nodo. Cada una tiene su partición del limitador y el resto
/// comparte una sola, así que la memoria del limitador queda acotada por las instancias reales.
/// </summary>
internal sealed class WebhookInstances
{
    public const string Unknown = "desconocida";

    private readonly ConcurrentDictionary<long, byte> known = new();

    public bool Contains(long instanceId) => known.ContainsKey(instanceId);

    public void Add(long instanceId) => known.TryAdd(instanceId, 0);
}
