using System.Globalization;
using BillingOss.App.Content;
using BillingOss.Domain.Content;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Content;

/// <summary>
/// Un estado para mostrar (sección 4.4 de la decisión de interfaz): el punto, la palabra y la forma. <paramref name="Tone"/> es
/// ok, warn, danger, info o neutral, y <paramref name="Shape"/> es solid (lleno: vigente o resuelto), hollow (hueco: en curso o
/// pendiente) o ring (con anillo: pide acción). Nunca se distingue solo por el color.
/// </summary>
public sealed record Pill(string Tone, string Shape, string Text);

/// <summary>Una lista de eventos de la red para mostrar. <paramref name="ShowLatest"/> agrega la última novedad de cada uno.</summary>
public sealed record EventList(IReadOnlyList<EventView> Items, TimeZoneInfo Zone, bool ShowLatest = false);

/// <summary>Piezas de presentación que comparten las páginas de contenidos.</summary>
public static class ContentUi
{
    public static Pill ComponentPill(ComponentStatus status, IStringLocalizer c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return status switch
        {
            ComponentStatus.Operational => new("ok", "solid", c["Net.Operational"]),
            ComponentStatus.Maintenance => new("info", "hollow", c["Net.Maintenance"]),
            ComponentStatus.Degraded => new("warn", "hollow", c["Net.Degraded"]),
            ComponentStatus.PartialOutage => new("warn", "ring", c["Net.PartialOutage"]),
            _ => new("danger", "ring", c["Net.MajorOutage"]),
        };
    }

    /// <summary>El estado de un incidente o la fase de un mantenimiento.</summary>
    public static Pill EventPill(EventView e, IStringLocalizer c)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(c);
        if (e.Kind == StatusEventKind.Maintenance)
        {
            return e.Phase switch
            {
                MaintenancePhase.InProgress => new("info", "hollow", c["Net.PhaseInProgress"]),
                MaintenancePhase.Completed => new("ok", "solid", c["Net.PhaseCompleted"]),
                MaintenancePhase.Cancelled => new("neutral", "solid", c["Net.PhaseCancelled"]),
                _ => new("neutral", "hollow", c["Net.PhaseUpcoming"]),
            };
        }

        return IncidentPill(e.State, c);
    }

    public static Pill IncidentPill(StatusEventState state, IStringLocalizer c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return state switch
        {
            StatusEventState.Resolved => new("ok", "solid", c["Net.StateResolved"]),
            StatusEventState.Monitoring => new("info", "hollow", c["Net.StateMonitoring"]),
            StatusEventState.Identified => new("warn", "hollow", c["Net.StateIdentified"]),
            StatusEventState.Investigating => new("warn", "ring", c["Net.StateInvestigating"]),
            StatusEventState.Scheduled => new("neutral", "hollow", c["Net.PhaseUpcoming"]),
            StatusEventState.Completed => new("ok", "solid", c["Net.PhaseCompleted"]),
            _ => new("neutral", "solid", c["Net.PhaseCancelled"]),
        };
    }

    public static Pill PublishPill(PublishState state, IStringLocalizer c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return state switch
        {
            PublishState.Published => new("ok", "solid", c["Cms.StatePublished"]),
            PublishState.Archived => new("neutral", "solid", c["Cms.StateArchived"]),
            _ => new("neutral", "hollow", c["Cms.StateDraft"]),
        };
    }

    public static Pill PhasePill(AnnouncementPhase phase, IStringLocalizer c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return phase switch
        {
            AnnouncementPhase.Live => new("ok", "solid", c["Cms.PhaseLive"]),
            AnnouncementPhase.Scheduled => new("info", "hollow", c["Cms.PhaseScheduled"]),
            AnnouncementPhase.Expired => new("warn", "solid", c["Cms.PhaseExpired"]),
            AnnouncementPhase.Archived => new("neutral", "solid", c["Cms.StateArchived"]),
            _ => new("neutral", "hollow", c["Cms.StateDraft"]),
        };
    }

    /// <summary>Fecha y hora en la zona del proveedor, con el formato corto de la cultura del pedido.</summary>
    public static string When(DateTimeOffset at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(at, zone).ToString("g", CultureInfo.CurrentCulture);
    }

    public static string Day(DateTimeOffset at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(at, zone).ToString("d", CultureInfo.CurrentCulture);
    }

    /// <summary>La fecha y hora para un campo datetime-local, en la zona del proveedor.</summary>
    public static string ToInput(DateTimeOffset? at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return at is { } value ? TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>Lo que escribió el staff en un campo datetime-local, en la zona del proveedor, como instante en UTC. Vacío o ilegible es nulo.</summary>
    public static DateTimeOffset? FromInput(string? text, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!DateTime.TryParseExact(text?.Trim(), ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(zone.IsInvalidTime(unspecified) ? unspecified.AddHours(1) : unspecified)).ToUniversalTime();
    }

    /// <summary>"1,5 MiB" en la cultura del pedido.</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{value:0} {units[unit]}")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>
    /// La política de contenido de las páginas con artículos: las imágenes del propio sitio y las de los hosts configurados
    /// (<c>Content:ImageHosts</c>), siempre por https.
    /// </summary>
    public static string ImagePolicy(IEnumerable<string> imageHosts)
    {
        ArgumentNullException.ThrowIfNull(imageHosts);
        var hosts = imageHosts.Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns).Select(h => "https://" + h.ToLowerInvariant() + " https://*." + h.ToLowerInvariant());
        return "default-src 'self'; img-src 'self' data: " + string.Join(' ', hosts) + "; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    }
}
