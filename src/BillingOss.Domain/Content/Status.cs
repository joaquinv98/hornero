using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Content;

public enum ComponentKind
{
    /// <summary>Junta a otros componentes. No lo toca ningún evento: toma el estado de sus hijos.</summary>
    Group,
    Server,
    Service,
}

/// <summary>Estado de un componente, del más leve al más grave (sección 6.2 de diseno/contenidos.md). El orden del enum es la gravedad.</summary>
public enum ComponentStatus
{
    Operational,
    Maintenance,
    Degraded,
    PartialOutage,
    MajorOutage,
}

public enum StatusEventKind
{
    Incident,
    Maintenance,
}

/// <summary>Los estados de los incidentes y de los mantenimientos. Cada tipo usa los suyos.</summary>
public enum StatusEventState
{
    Investigating,
    Identified,
    Monitoring,
    Resolved,
    Scheduled,
    Completed,
    Cancelled,
}

/// <summary>La fase que ve el público de un mantenimiento, deducida con la hora.</summary>
public enum MaintenancePhase
{
    Upcoming,
    InProgress,
    Completed,
    Cancelled,
}

/// <summary>Un servidor, un servicio o un grupo con estado. Los textos usan el título como nombre y el resumen como descripción.</summary>
public sealed class StatusComponent
{
    public long Id { get; private set; }

    public long? ParentId { get; set; }

    public ComponentKind Kind { get; set; }

    public string Slug { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>No se muestra en la página pública. Un componente con historial se oculta, no se borra.</summary>
    public bool Hidden { get; set; }

    /// <summary>Productos que se ven afectados cuando el componente tiene problemas.</summary>
    public long[] ProductIds { get; set; } = [];

    public string Texts { get; set; } = "{}";

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public void SetImportedId(long id) => Id = id;
}

/// <summary>Un incidente o un mantenimiento programado (sección 6 de diseno/contenidos.md). El título va en los textos.</summary>
public sealed class StatusEvent
{
    public long Id { get; private set; }

    public StatusEventKind Kind { get; set; }

    public StatusEventState State { get; set; }

    /// <summary>Cuándo empezó el incidente o cuándo empieza el mantenimiento.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>Cuándo se resolvió el incidente o cuándo termina el mantenimiento. Un incidente abierto no lo tiene.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    public string Texts { get; set; } = "{}";

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public void SetImportedId(long id) => Id = id;
}

/// <summary>Qué componente toca un evento y con qué impacto: degradado, interrupción parcial o total en un incidente, y mantenimiento en un mantenimiento.</summary>
public sealed class StatusEventComponent
{
    public long EventId { get; set; }

    public long ComponentId { get; set; }

    public ComponentStatus Impact { get; set; }
}

/// <summary>Cada mensaje que se publica sobre un evento, con el estado en que lo deja. Solo se agregan. El mensaje va en el cuerpo de los textos.</summary>
public sealed class StatusUpdate
{
    public long Id { get; private set; }

    public long EventId { get; set; }

    public StatusEventState State { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    public string Texts { get; set; } = "{}";

    /// <summary>Quién lo publicó, como en la auditoría. No se muestra al público.</summary>
    public string Author { get; set; } = string.Empty;
}

/// <summary>Una casilla suscripta a los avisos de la red. No recibe nada hasta confirmarla.</summary>
public sealed class StatusSubscriber
{
    public long Id { get; private set; }

    public string Email { get; set; } = string.Empty;

    public string Language { get; set; } = ContentTexts.DefaultLanguage;

    /// <summary>Valor aleatorio de un solo propósito: confirmar o dar de baja esta casilla. No inicia sesión.</summary>
    public string Token { get; set; } = string.Empty;

    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Componentes que le interesan. Vacío son todos.</summary>
    public long[] ComponentIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

public static class StatusRules
{
    /// <summary>Un incidente se mueve libremente entre los estados abiertos y al resolverse no vuelve.</summary>
    public static StateMachine<StatusEventState> IncidentStates { get; } = new(
        (StatusEventState.Investigating, StatusEventState.Identified),
        (StatusEventState.Investigating, StatusEventState.Monitoring),
        (StatusEventState.Investigating, StatusEventState.Resolved),
        (StatusEventState.Identified, StatusEventState.Investigating),
        (StatusEventState.Identified, StatusEventState.Monitoring),
        (StatusEventState.Identified, StatusEventState.Resolved),
        (StatusEventState.Monitoring, StatusEventState.Investigating),
        (StatusEventState.Monitoring, StatusEventState.Identified),
        (StatusEventState.Monitoring, StatusEventState.Resolved));

    public static StateMachine<StatusEventState> MaintenanceStates { get; } = new(
        (StatusEventState.Scheduled, StatusEventState.Completed),
        (StatusEventState.Scheduled, StatusEventState.Cancelled));

    public static bool IsIncidentState(StatusEventState state) => state is StatusEventState.Investigating or StatusEventState.Identified or StatusEventState.Monitoring or StatusEventState.Resolved;

    /// <summary>Si el evento todavía puede cambiar: un incidente sin resolver o un mantenimiento programado.</summary>
    public static bool IsOpen(StatusEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind == StatusEventKind.Incident ? e.State != StatusEventState.Resolved : e.State == StatusEventState.Scheduled;
    }

    public static MaintenancePhase Phase(StatusEvent e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.State switch
        {
            StatusEventState.Cancelled => MaintenancePhase.Cancelled,
            StatusEventState.Completed => MaintenancePhase.Completed,
            _ when e.EndsAt is { } end && end <= now => MaintenancePhase.Completed,
            _ when e.StartsAt > now => MaintenancePhase.Upcoming,
            _ => MaintenancePhase.InProgress,
        };
    }

    /// <summary>Si el evento cambia hoy el estado de sus componentes: un incidente abierto o un mantenimiento en curso.</summary>
    public static bool Affects(StatusEvent e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind == StatusEventKind.Incident ? e.State != StatusEventState.Resolved : Phase(e, now) == MaintenancePhase.InProgress;
    }

    public static ComponentStatus Worst(IEnumerable<ComponentStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return statuses.DefaultIfEmpty(ComponentStatus.Operational).Max();
    }

    /// <summary>El impacto que corresponde a un evento: un incidente marca degradado o interrupción, un mantenimiento marca mantenimiento.</summary>
    public static bool ValidImpact(StatusEventKind kind, ComponentStatus impact) => kind == StatusEventKind.Incident
        ? impact is ComponentStatus.Degraded or ComponentStatus.PartialOutage or ComponentStatus.MajorOutage
        : impact == ComponentStatus.Maintenance;

    /// <summary>Qué tiene mal el evento, o null si se puede guardar.</summary>
    public static string? Error(StatusEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind == StatusEventKind.Maintenance)
        {
            return e.EndsAt is not { } end ? "Un mantenimiento necesita la hora de fin."
                : end <= e.StartsAt ? "El mantenimiento tiene que terminar después de empezar."
                : e.State is not (StatusEventState.Scheduled or StatusEventState.Completed or StatusEventState.Cancelled) ? "Un mantenimiento está programado, terminado o cancelado."
                : null;
        }

        return !IsIncidentState(e.State) ? "Un incidente se investiga, se identifica, se monitorea o se resuelve."
            : e.State == StatusEventState.Resolved && e.EndsAt is null ? "Un incidente resuelto necesita la hora en que se resolvió."
            : e.State != StatusEventState.Resolved && e.EndsAt is not null ? "Un incidente abierto no tiene hora de fin."
            : e.EndsAt is { } closed && closed < e.StartsAt ? "El incidente no puede resolverse antes de empezar."
            : null;
    }
}
