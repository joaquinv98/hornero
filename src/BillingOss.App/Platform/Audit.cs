using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Platform;

public enum ActorKind
{
    /// <summary>El propio núcleo: planificador y trabajos.</summary>
    System,
    Staff,
    Api,

    /// <summary>Un módulo que informa algo por su cuenta, por ejemplo un aviso de pasarela.</summary>
    Module,
    Customer,
}

/// <summary>
/// Quién está actuando. Viaja con el flujo asincrónico, como la traza: lo fija la autenticación de la API, del
/// portal o del admin, y lo que no lo fija corre como el sistema.
/// </summary>
public sealed record Actor(ActorKind Kind, string Id, IPAddress? Ip = null)
{
    private static readonly AsyncLocal<Actor?> Ambient = new();

    public static Actor Node { get; } = new(ActorKind.System, $"system:{Environment.MachineName}:{Environment.ProcessId}");

    public static Actor Current
    {
        get => Ambient.Value ?? Node;
        set => Ambient.Value = value;
    }
}

/// <summary>
/// Registro de auditoría del núcleo (D-15): quién hizo qué, sobre qué y cuándo. Solo se agrega: la base rechaza
/// cambios y borrados. Nunca lleva secretos ni datos personales en los detalles.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public ActorKind ActorKind { get; private set; }

    /// <summary>"apikey:12:bk_7f3a9c21", "staff:5" o "system:host:pid".</summary>
    public string Actor { get; private set; } = string.Empty;

    /// <summary>"POST /api/v1/orders", "compat AddOrder", "module /v0/provisioning/suspend" o "billing.run".</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Sobre qué se actuó: la ruta pedida, "job:42" o la URL del módulo.</summary>
    public string? Entity { get; private set; }

    public IPAddress? Ip { get; private set; }

    /// <summary>JSON chico con el resultado y los IDs involucrados.</summary>
    public string? Details { get; private set; }

    /// <summary>Traza en la que pasó, para cruzar con logs y trazas.</summary>
    public string? TraceId { get; private set; }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events", "platform");
        b.Property(x => x.Actor).HasMaxLength(200);
        b.Property(x => x.Action).HasMaxLength(200);
        b.Property(x => x.Entity).HasMaxLength(500);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.Property(x => x.TraceId).HasMaxLength(32);
        b.HasIndex(x => new { x.Action, x.CreatedAt });
    }
}

public sealed class AuditLog(BillingDbContext db, TimeProvider time, ILogger<AuditLog> logger)
{
    /// <summary>
    /// Agrega un evento con el actor actual, en la conexión y transacción del contexto: dentro de un caso de uso
    /// queda o se va junto con el cambio. Sin transacción abre una propia, así pasa por la cerca del trabajo y por la
    /// guarda del COMMIT.
    /// </summary>
    public async Task Record(string action, string? entity = null, object? details = null, CancellationToken ct = default)
    {
        var actor = Actor.Current;
        var now = time.GetUtcNow();
        var json = details is null ? null : JsonSerializer.Serialize(details);
        var traceId = Activity.Current?.TraceId.ToHexString();
        var kind = actor.Kind.ToString();
        var ip = actor.Ip?.ToString();
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await using (own)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO platform.audit_events (created_at, actor_kind, actor, action, entity, ip, details, trace_id)
                VALUES ({now}, {kind}, {actor.Id}, {action}, {entity}, {ip}::inet, {json}::jsonb, {traceId})
                """, ct).ConfigureAwait(false);
            if (own is not null)
            {
                await own.CommitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Para registrar después de algo que ya pasó afuera (una llamada a un módulo, un pedido ya confirmado): si la
    /// base falla, se avisa en el log y no se rompe lo que ya está hecho.
    /// </summary>
    public async Task TryRecord(string action, string? entity = null, object? details = null)
    {
        try
        {
            await Record(action, entity, details, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo registrar {Action} en la auditoría", action);
        }
    }
}
