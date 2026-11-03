using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Platform;

/// <summary>
/// Un evento que ven los webhooks y las extensiones: el nombre público estable y qué campos del evento interno salen
/// en data, con su nombre público (sección 2 de diseno/webhooks-y-extensiones.md).
/// </summary>
public sealed record PublicEvent(string Internal, string Type, IReadOnlyList<(string From, string To)> Fields)
{
    public JsonObject Data(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var data = new JsonObject();
        foreach (var (from, to) in Fields)
        {
            if (payload[from] is { } value)
            {
                data[to] = value.DeepClone();
            }
        }

        return data;
    }
}

/// <summary>
/// El catálogo público y el reparto de cada evento a los webhooks y a las extensiones suscriptas. El reparto va en la
/// misma transacción que el cambio, como el resto de los suscriptores (D-07): si la transacción se cae no queda nada,
/// y si confirma, cada entrega tiene su fila y su trabajo aunque se caiga el nodo.
/// </summary>
public static class PublicEvents
{
    /// <summary>Gancho de las extensiones que valida un pedido antes de confirmarlo. No es un evento de la cola.</summary>
    public const string ValidateOrder = "order.validate";

    /// <summary>La prueba del panel: va a un solo destino, con data vacío.</summary>
    public const string Test = "webhook.test";

    /// <summary>Intentos de una entrega o de un evento de extensión: con la espera creciente de la cola, casi un día.</summary>
    public const int Attempts = 30;

    public static IReadOnlyList<PublicEvent> All { get; } =
    [
        new(Events.OrderPlaced, "order.placed", [("order_id", "order_id")]),
        new(Events.InvoiceIssued, "invoice.issued", [("document_id", "invoice_id")]),
        new(Events.InvoicePaid, "invoice.paid", [("document_id", "invoice_id")]),
        new(Events.CreditNoteIssued, "credit_note.issued", [("document_id", "credit_note_id")]),
        new(Events.PaymentReceived, "payment.received", [("payment_id", "payment_id")]),
        new(Events.FiscalDocumentAuthorized, "fiscal_document.authorized", [("document_id", "document_id")]),
        new(Events.ServiceActivated, "service.activated", [("service_id", "service_id")]),
        new(Events.ServiceReactivated, "service.reactivated", [("service_id", "service_id")]),
        new(Events.ServiceSuspended, "service.suspended", [("service_id", "service_id")]),
        new(Events.ServiceTerminated, "service.terminated", [("service_id", "service_id")]),
        new(Events.TicketOpened, "ticket.opened", [("ticket_id", "ticket_id"), ("message_id", "message_id")]),
        new(Events.TicketReplied, "ticket.replied", [("ticket_id", "ticket_id"), ("message_id", "message_id")]),
    ];

    private static readonly (string Field, string Prefix)[] Entities =
        [("document_id", "document"), ("service_id", "service"), ("order_id", "order"), ("payment_id", "payment"), ("ticket_id", "ticket")];

    public static PublicEvent? ByInternal(string eventName) => All.FirstOrDefault(e => e.Internal == eventName);

    public static bool IsType(string type) => All.Any(e => e.Type == type);

    /// <summary>La entidad del evento, por ejemplo "document:42": lo de una misma entidad sale en orden.</summary>
    public static string Entity(JsonObject payload, string fallback)
    {
        ArgumentNullException.ThrowIfNull(payload);
        foreach (var (field, prefix) in Entities)
        {
            if (payload[field] is JsonValue id)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{prefix}:{id.ToJsonString()}");
            }
        }

        return fallback;
    }

    /// <summary>Instante en el formato del cuerpo de los webhooks y de los eventos de extensión, siempre en UTC.</summary>
    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Por cada destino activo suscripto al tipo, una entrega y su trabajo, y por cada gancho de extensión activo, su
    /// trabajo. Una sola ida a la base, en la transacción del que publica.
    /// </summary>
    internal static async Task FanOut(BillingDbContext db, string eventName, JsonObject payload, DateTimeOffset now, string? trace, Actor actor, CancellationToken ct)
    {
        if (ByInternal(eventName) is not { } evt)
        {
            return;
        }

        var eventId = Guid.NewGuid().ToString("N");
        var data = evt.Data(payload).ToJsonString();
        var entity = Entity(payload, $"event:{eventId}");
        var occurred = Iso(now);
        var kind = actor.Kind.ToString();
        var ip = actor.Ip?.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH d AS (
              INSERT INTO platform.webhook_deliveries (endpoint_id, event_type, data, entity_key, occurred_at, status, attempts, created_at)
              SELECT e.id, {evt.Type}, {data}::jsonb, {entity}, {now}, 'Pending', 0, {now}
              FROM platform.webhook_endpoints e WHERE e.active AND {evt.Type} = ANY(e.events)
              RETURNING id, endpoint_id),
            w AS (
              INSERT INTO platform.jobs (kind, payload, entity_key, status, run_at, attempts, max_attempts, created_at, updated_at, trace_parent, actor_kind, actor, actor_ip)
              SELECT {JobKinds.WebhookDeliver}, jsonb_build_object('delivery_id', d.id), 'webhook:' || d.endpoint_id || ':' || {entity}, 'Pending',
                     {now}, 0, {Attempts}, {now}, {now}, {trace}, {kind}, {actor.Id}, {ip}::inet
              FROM d)
            INSERT INTO platform.jobs (kind, payload, entity_key, status, run_at, attempts, max_attempts, created_at, updated_at, trace_parent, actor_kind, actor, actor_ip)
            SELECT {JobKinds.ExtensionEvent},
                   jsonb_build_object('hook_id', h.id, 'event_id', {eventId}, 'type', {evt.Type}, 'occurred_at', {occurred}, 'data', {data}::jsonb),
                   'extension:' || h.instance_id || ':' || {entity}, 'Pending', {now}, 0, {Attempts}, {now}, {now}, {trace}, {kind}, {actor.Id}, {ip}::inet
            FROM platform.extension_hooks h WHERE h.active AND h.event = {evt.Type}
            """, ct).ConfigureAwait(false);
    }
}
