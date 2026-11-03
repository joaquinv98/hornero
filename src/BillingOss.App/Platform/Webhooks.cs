using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Platform;

/// <summary>
/// Destino de webhooks del proveedor (sección 3 de diseno/webhooks-y-extensiones.md). El secreto de firma va cifrado
/// por campo (D-09) y se muestra una sola vez.
/// </summary>
public sealed class WebhookEndpoint
{
    public long Id { get; private set; }

    public string Url { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Tipos públicos a los que se suscribe (<see cref="PublicEvents"/>).</summary>
    public string[] Events { get; set; } = [];

    /// <summary>"whsec_" y 32 bytes en base64, cifrado con <see cref="SecretBox"/>.</summary>
    public byte[] Secret { get; set; } = [];

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum WebhookDeliveryStatus
{
    Pending,
    Delivered,
    Failed,
    Cancelled,
}

/// <summary>Una entrega por evento y destino: el registro de entregas que ve el staff. Nunca guarda cuerpos de respuesta.</summary>
public sealed class WebhookDelivery
{
    public long Id { get; private set; }

    public long EndpointId { get; set; }

    public string EventType { get; set; } = string.Empty;

    /// <summary>Lo que va en data, congelado al publicar. JSON.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>La entidad del evento: lo de una misma entidad llega en orden a cada destino.</summary>
    public string EntityKey { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public WebhookDeliveryStatus Status { get; set; }

    public int Attempts { get; set; }

    public int? LastStatusCode { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>webhook-id de Standard Webhooks: el mismo en cada reintento y en un reenvío.</summary>
    public string MessageId => string.Create(CultureInfo.InvariantCulture, $"msg_{Id}");

    /// <summary>El cuerpo: tipo, cuándo pasó y data. Se arma igual en cada intento.</summary>
    public byte[] Body() => JsonSerializer.SerializeToUtf8Bytes(new JsonObject
    {
        ["type"] = EventType,
        ["timestamp"] = PublicEvents.Iso(OccurredAt),
        ["data"] = JsonNode.Parse(Data),
    });
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.ToTable("webhook_endpoints", "platform", t => t.HasCheckConstraint("ck_webhook_endpoints_events", "cardinality(events) > 0"));
        b.Property(x => x.Url).HasMaxLength(500);
        b.Property(x => x.Description).HasMaxLength(200);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("webhook_deliveries", "platform");
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.Data).HasColumnType("jsonb");
        b.Property(x => x.EntityKey).HasMaxLength(80);
        b.Property(x => x.LastError).HasMaxLength(500);
        b.Ignore(x => x.MessageId);
        b.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId);
        b.HasIndex(x => new { x.EndpointId, x.Id });
        b.HasIndex(x => x.CreatedAt);
    }
}

/// <summary>Firma de Standard Webhooks: HMAC-SHA256 de "{id}.{timestamp}.{cuerpo}" con los bytes del secreto.</summary>
public static class WebhookSignature
{
    public const string SecretPrefix = "whsec_";

    public static string NewSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string secret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var key = Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret);
        var head = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{messageId}.{timestamp}."));
        var content = new byte[head.Length + body.Length];
        head.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(head.Length));
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, content));
    }
}

/// <summary>
/// Manda un mensaje: POST con tope de 15 segundos, sin seguir redirecciones (un 307 lo llevaría a otro destino) y
/// sin leer el cuerpo de la respuesta. El tiempo del encabezado es el reloj real: el receptor lo compara con el suyo.
/// </summary>
public sealed class WebhookSender : IDisposable
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(15);

    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public async Task<(int? Status, string? Error)> Send(Uri url, string messageId, byte[] body, string secret, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("webhook-id", messageId);
        request.Headers.Add("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("webhook-signature", WebhookSignature.Sign(secret, messageId, timestamp, body));
        request.Headers.UserAgent.ParseAdd("billing-oss-webhooks");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, null);
        }
        catch (HttpRequestException)
        {
            return (null, "No se pudo conectar con el destino.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "El destino no respondió en 15 segundos.");
        }
    }

    public void Dispose() => http.Dispose();
}

public sealed record WebhookInput(string Url, string? Description, IReadOnlyCollection<string> Events, bool Active = true);

/// <summary>
/// Alta, cambios, rotación, borrado, prueba y reenvío de webhooks desde el panel. Todo queda en la auditoría con la URL
/// y los eventos, nunca con el secreto.
/// </summary>
public sealed class WebhookService(BillingDbContext db, SecretBox box, JobQueue jobs, AuditLog audit, TimeProvider time)
{
    public const string Table = "platform.webhook_endpoints";

    /// <summary>Crea el destino y devuelve el secreto, que no se vuelve a mostrar.</summary>
    public async Task<(long Id, string Secret)> Create(WebhookInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (url, description, events) = Validate(input);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var endpoint = new WebhookEndpoint { Url = url, Description = description, Events = events, Active = input.Active, CreatedAt = now, UpdatedAt = now };
        db.Add(endpoint);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var secret = WebhookSignature.NewSecret();
        endpoint.Secret = box.Encrypt(secret, Table, "secret", endpoint.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("webhooks.create", Entity(endpoint.Id), new { url, events }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (endpoint.Id, secret);
    }

    public async Task Update(long id, WebhookInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (url, description, events) = Validate(input);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var endpoint = await Find(id, ct).ConfigureAwait(false);
        endpoint.Url = url;
        endpoint.Description = description;
        endpoint.Events = events;
        endpoint.Active = input.Active;
        endpoint.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("webhooks.update", Entity(id), new { url, events, active = input.Active }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reemplaza el secreto en el momento. Lo que falle mientras el receptor no lo cambie se reintenta solo.</summary>
    public async Task<string> RotateSecret(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var endpoint = await Find(id, ct).ConfigureAwait(false);
        var secret = WebhookSignature.NewSecret();
        endpoint.Secret = box.Encrypt(secret, Table, "secret", id);
        endpoint.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("webhooks.rotate", Entity(id), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return secret;
    }

    /// <summary>Borra el destino con su registro de entregas. Un trabajo que quedó pendiente no encuentra su entrega y termina.</summary>
    public async Task Delete(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var endpoint = await Find(id, ct).ConfigureAwait(false);
        await db.Set<WebhookDelivery>().Where(d => d.EndpointId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.Remove(endpoint);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("webhooks.delete", Entity(id), new { url = endpoint.Url }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Un webhook.test con data vacío, solo a este destino, aunque esté apagado.</summary>
    public async Task<long> SendTest(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Find(id, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var delivery = new WebhookDelivery { EndpointId = id, EventType = PublicEvents.Test, EntityKey = "test", OccurredAt = now, CreatedAt = now };
        db.Add(delivery);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await Enqueue(delivery, ct).ConfigureAwait(false);
        await audit.Record("webhooks.test", Entity(id), new { delivery_id = delivery.Id }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return delivery.Id;
    }

    /// <summary>Vuelve a mandar una entrega entregada, fallida o cancelada, con el mismo webhook-id.</summary>
    public async Task Resend(long deliveryId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "platform.webhook_deliveries", [deliveryId], ct).ConfigureAwait(false);
        var delivery = await db.Set<WebhookDelivery>().SingleOrDefaultAsync(d => d.Id == deliveryId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la entrega {deliveryId}.");
        if (delivery.Status == WebhookDeliveryStatus.Pending)
        {
            throw new InvalidOperationException($"La entrega {deliveryId} ya está pendiente: sale sola.");
        }

        delivery.Status = WebhookDeliveryStatus.Pending;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await Enqueue(delivery, ct).ConfigureAwait(false);
        await audit.Record("webhooks.resend", Entity(delivery.EndpointId), new { delivery_id = deliveryId }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private Task Enqueue(WebhookDelivery delivery, CancellationToken ct) =>
        jobs.Enqueue(JobKinds.WebhookDeliver, new { delivery_id = delivery.Id }, string.Create(CultureInfo.InvariantCulture, $"webhook:{delivery.EndpointId}:{delivery.EntityKey}"), maxAttempts: PublicEvents.Attempts, ct: ct);

    private async Task<WebhookEndpoint> Find(long id, CancellationToken ct) =>
        await db.Set<WebhookEndpoint>().SingleOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el webhook {id}.");

    private static string Entity(long id) => string.Create(CultureInfo.InvariantCulture, $"webhook:{id}");

    private static (string Url, string? Description, string[] Events) Validate(WebhookInput input)
    {
        var url = input.Url?.Trim() ?? string.Empty;
        if (url.Length is 0 or > 500 || ModuleEndpoint.TryParseBaseUrl(url) is null)
        {
            throw new ValidationException("La URL tiene que ser https, o http solo hacia este mismo equipo, y de hasta 500 caracteres.");
        }

        var events = input.Events.Select(e => e.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (events.Length == 0)
        {
            throw new ValidationException("Elegí al menos un evento.");
        }

        if (events.FirstOrDefault(e => !PublicEvents.IsType(e)) is { } unknown)
        {
            throw new ValidationException($"Evento desconocido: {unknown}.");
        }

        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        return description is { Length: > 200 }
            ? throw new ValidationException("La descripción va hasta 200 caracteres.")
            : (url, description, events);
    }
}

/// <summary>
/// Manda una entrega (sección 3.4 del diseño). Un 2xx la deja entregada. Cualquier otra respuesta, un corte o una
/// demora la devuelve a la cola con la espera creciente, y con el último intento queda fallida y su trabajo muerto.
/// </summary>
public sealed class WebhookDeliveryHandler(BillingDbContext db, SecretBox box, WebhookSender sender, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.WebhookDeliver;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var id = JsonDocument.Parse(job.Payload).RootElement.GetProperty("delivery_id").GetInt64();
        var delivery = await db.Set<WebhookDelivery>().SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (delivery is not { Status: WebhookDeliveryStatus.Pending })
        {
            // Ya entregada (un reintento después de un corte) o borrada con su destino.
            return JobResult.Done;
        }

        var endpoint = await db.Set<WebhookEndpoint>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == delivery.EndpointId, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (endpoint is null || (!endpoint.Active && delivery.EventType != PublicEvents.Test))
        {
            delivery.Status = WebhookDeliveryStatus.Cancelled;
            delivery.LastError = "El destino estaba apagado.";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        if (ModuleEndpoint.TryParseBaseUrl(endpoint.Url) is not { } url)
        {
            delivery.Status = WebhookDeliveryStatus.Failed;
            delivery.LastError = "La URL ya no se acepta: https, o http solo hacia este mismo equipo.";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw new PermanentFailureException(delivery.LastError);
        }

        var secret = box.Decrypt(endpoint.Secret, WebhookService.Table, "secret", endpoint.Id);
        var (status, error) = await sender.Send(url, delivery.MessageId, delivery.Body(), secret, ct).ConfigureAwait(false);
        delivery.Attempts++;
        delivery.LastAttemptAt = now;
        delivery.LastStatusCode = status;
        if (status is >= 200 and < 300)
        {
            delivery.Status = WebhookDeliveryStatus.Delivered;
            delivery.DeliveredAt = now;
            delivery.LastError = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        delivery.LastError = error ?? string.Create(CultureInfo.InvariantCulture, $"El destino respondió {status}.");
        if (job.Attempts >= job.MaxAttempts)
        {
            delivery.Status = WebhookDeliveryStatus.Failed;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        throw new RetryLaterException(delivery.LastError);
    }
}
