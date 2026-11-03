using BillingOss.Domain.Shared;

namespace BillingOss.App.Platform;

public enum JobStatus
{
    Pending,
    Running,
    Done,
    Dead,
}

/// <summary>
/// Trabajo de la cola en PostgreSQL (D-07). Los eventos de dominio también son trabajos, uno por suscriptor, así
/// que la cola cumple la función del outbox: se escriben en la misma transacción que el cambio.
/// </summary>
public sealed class Job
{
    private Job()
    {
    }

    public Job(string kind, string payload, DateTimeOffset runAt, DateTimeOffset now, string? entityKey = null, string? dedupKey = null, int maxAttempts = 10)
    {
        Kind = kind;
        Payload = payload;
        RunAt = runAt;
        EntityKey = entityKey;
        DedupKey = dedupKey;
        MaxAttempts = maxAttempts;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    /// <summary>Datos del trabajo. JSON.</summary>
    public string Payload { get; private set; } = "{}";

    /// <summary>Los trabajos con la misma clave de entidad corren en orden y de a uno.</summary>
    public string? EntityKey { get; private set; }

    /// <summary>Encolar dos veces lo mismo no duplica: la clave es única.</summary>
    public string? DedupKey { get; private set; }

    public JobStatus Status { get; set; }

    public DateTimeOffset RunAt { get; set; }

    public int Attempts { get; set; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LockedBy { get; set; }

    public string? LastError { get; set; }

    /// <summary>Traza W3C de quien lo encoló: el trabajo la continúa (D-15).</summary>
    public string? TraceParent { get; private set; }

    /// <summary>Quién lo pidió, como en la auditoría: el trabajo corre a su nombre.</summary>
    public ActorKind ActorKind { get; private set; }

    public string? Actor { get; private set; }

    public System.Net.IPAddress? ActorIp { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Saldo a favor por cuenta y moneda. La fila es también el lock del saldo (sección 13 del modelo).</summary>
public sealed class CreditBalance
{
    private CreditBalance()
    {
    }

    public CreditBalance(long accountId, Currency currency)
    {
        AccountId = accountId;
        Currency = currency;
    }

    public long AccountId { get; private set; }

    public Currency Currency { get; private set; }

    public decimal Balance { get; set; }
}
