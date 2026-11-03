using System.Net;
using System.Text.Json;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Orders;

public enum ScreeningStatus
{
    /// <summary>Esperando al módulo de fraude. El pedido está retenido.</summary>
    Pending,

    /// <summary>El puntaje quedó bajo el umbral.</summary>
    Passed,

    /// <summary>Retenido hasta que decida el staff.</summary>
    Review,

    /// <summary>El staff lo aprobó.</summary>
    Approved,

    /// <summary>El staff lo rechazó: el pedido se cancela.</summary>
    Rejected,
}

/// <summary>Un motivo del puntaje o de la revisión, para el staff. El cliente nunca lo ve.</summary>
public sealed record FraudReason(string Code, string Message);

/// <summary>
/// Evaluación de fraude de un pedido (diseno/fraude-y-acceso.md, sección 3.2). Mientras está pendiente o en revisión el
/// pedido está retenido: ningún servicio suyo se aprovisiona.
/// </summary>
public sealed class FraudCheck
{
    private static readonly StateMachine<ScreeningStatus> Machine = new(
        (ScreeningStatus.Pending, ScreeningStatus.Passed),
        (ScreeningStatus.Pending, ScreeningStatus.Review),
        (ScreeningStatus.Pending, ScreeningStatus.Approved),
        (ScreeningStatus.Pending, ScreeningStatus.Rejected),
        (ScreeningStatus.Review, ScreeningStatus.Approved),
        (ScreeningStatus.Review, ScreeningStatus.Rejected));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private FraudCheck()
    {
    }

    /// <summary>Pendiente si hace falta el módulo, o en revisión con los motivos de una regla propia.</summary>
    public FraudCheck(long orderId, long accountId, IReadOnlyList<FraudReason> reasons, IPAddress? ip, long? userId, long? moduleInstanceId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        OrderId = orderId;
        AccountId = accountId;
        Ip = ip;
        UserId = userId;
        ModuleInstanceId = moduleInstanceId;
        CreatedAt = now;
        Reasons = JsonSerializer.Serialize(reasons, Json);
        if (reasons.Count > 0)
        {
            Status = ScreeningStatus.Review;
            EvaluatedAt = now;
        }
    }

    public long Id { get; private set; }

    public long OrderId { get; private set; }

    public long AccountId { get; private set; }

    public ScreeningStatus Status { get; private set; }

    /// <summary>De 0 a 100, más alto es más riesgo. Vacío si no lo evaluó un módulo.</summary>
    public decimal? Score { get; private set; }

    /// <summary>Lista de <see cref="FraudReason"/>. JSON.</summary>
    public string Reasons { get; private set; } = "[]";

    public long? ModuleInstanceId { get; private set; }

    /// <summary>ID de la evaluación en el servicio de puntaje.</summary>
    public string? ExternalId { get; private set; }

    /// <summary>IP de quien hizo el pedido.</summary>
    public IPAddress? Ip { get; private set; }

    /// <summary>Usuario del portal que hizo el pedido, si se sabe.</summary>
    public long? UserId { get; private set; }

    /// <summary>Error del módulo, si falló.</summary>
    public string? Error { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? EvaluatedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Quién decidió, como en la auditoría: "staff:3" o "apikey:...".</summary>
    public string? DecidedBy { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Retiene el pedido: sus servicios no se aprovisionan.</summary>
    public bool Holds => Status is ScreeningStatus.Pending or ScreeningStatus.Review;

    public IReadOnlyList<FraudReason> ReasonList => JsonSerializer.Deserialize<List<FraudReason>>(Reasons, Json) ?? [];

    /// <summary>
    /// Guarda lo que devolvió el módulo, o su error. Si el staff ya decidió, el resultado queda anotado y la decisión no
    /// cambia. Devuelve true si la evaluación pasó.
    /// </summary>
    public bool Evaluated(decimal? score, IReadOnlyList<FraudReason> reasons, string? externalId, string? error, bool review, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        Score = score;
        Reasons = JsonSerializer.Serialize(reasons, Json);
        ExternalId = externalId is { Length: > 100 } ? externalId[..100] : externalId;
        Error = error is { Length: > 500 } ? error[..500] : error;
        EvaluatedAt = now;
        if (Status != ScreeningStatus.Pending)
        {
            return false;
        }

        Status = Machine.Move(Status, review ? ScreeningStatus.Review : ScreeningStatus.Passed);
        return Status == ScreeningStatus.Passed;
    }

    /// <summary>La decisión del staff. Repetir la misma no cambia nada. La contraria a una ya tomada es un error.</summary>
    public bool Decide(bool approve, string by, string? note, DateTimeOffset now)
    {
        var target = approve ? ScreeningStatus.Approved : ScreeningStatus.Rejected;
        if (Status == target)
        {
            return false;
        }

        if (!Machine.CanMove(Status, target))
        {
            throw new InvalidOperationException($"El pedido ya está {Status} y no se puede {(approve ? "aprobar" : "rechazar")}.");
        }

        Status = target;
        DecidedAt = now;
        DecidedBy = by;
        var trimmed = note?.Trim();
        Note = string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 1000 ? trimmed[..1000] : trimmed;
        return true;
    }
}
