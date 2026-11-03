using System.Globalization;

namespace BillingOss.Updates;

/// <summary>
/// Camino de actualización (sección 7.4): los parches de una misma minor se saltean y se va directo al último, las minors
/// van de a una, cada una con su vuelta completa, y "from" dice desde qué versión se llega directo.
/// </summary>
public static class UpdatePlanner
{
    public static IReadOnlyList<Release> Plan(ReleaseChannel channel, Version installed, Version target, IReadOnlyCollection<string>? blocked = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(target);
        var offered = Offered(channel, blocked);
        if (!offered.Any(r => r.Version == target))
        {
            throw new UpdateException($"La versión {target.Text()} no está en el canal, fue retirada o está bloqueada en esta instalación.");
        }

        if (target <= installed)
        {
            throw new UpdateException($"La instalación ya está en {installed.Text()}: {target.Text()} no es posterior.");
        }

        var path = new List<Release>();
        var current = installed;
        while (current < target)
        {
            // La línea siguiente: la del objetivo si ya se llegó a ella, si no la próxima que exista.
            var line = current.Line() == target.Line()
                ? target.Line()
                : offered.Select(r => r.Version.Line()).Where(l => l.CompareTo(current.Line()) > 0).Min();
            var step = Best(offered, line, current, target) ?? Best(offered, current.Line(), current, target)
                ?? throw new UpdateException($"No hay camino desde {current.Text()} hasta {target.Text()}: ninguna versión acepta llegar desde {current.Text()}.");
            path.Add(step);
            current = step.Version;
        }

        return path;
    }

    /// <summary>Lo que se puede pedir: ni retirado ni bloqueado en la instalación.</summary>
    public static IReadOnlyList<Release> Offered(ReleaseChannel channel, IReadOnlyCollection<string>? blocked = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return [.. channel.Releases.Where(r => !r.Withdrawn && !(blocked?.Contains(r.ReleaseVersion) ?? false)).OrderBy(r => r.Version)];
    }

    /// <summary>
    /// El último parche de seguridad de la línea de una versión. Desde la web nunca se baja de ahí (sección 7.5): volver a
    /// una versión con una falla conocida es justo lo que buscaría quien tomó una cuenta de admin.
    /// </summary>
    public static Version? SecurityFloor(ReleaseChannel channel, Version version)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(version);
        return channel.Releases.Where(r => r.Security && !r.Withdrawn && r.Version.Line() == version.Line()).Select(r => r.Version).DefaultIfEmpty().Max();
    }

    private static Release? Best(IReadOnlyList<Release> offered, (int, int) line, Version current, Version target) =>
        offered.Where(r => r.Version.Line() == line && r.Version > current && r.Version <= target && r.FromVersion <= current).MaxBy(r => r.Version);
}

public enum UpdateState
{
    /// <summary>No hay nada más nuevo.</summary>
    UpToDate,

    /// <summary>Hay una versión nueva.</summary>
    Available,

    /// <summary>Falta un parche de seguridad, la versión instalada fue retirada o su línea terminó su soporte.</summary>
    Urgent,

    /// <summary>Sin datos confiables: con datos viejos el panel nunca dice "al día".</summary>
    NoData,
}

/// <summary>Lo que el actualizador dejó en la base sobre el canal y los nodos.</summary>
public sealed record ChannelSnapshot(ReleaseChannel? Channel, DateTimeOffset? CheckedAt, DateTimeOffset? AttemptAt, string? Error, bool Rejected, DateTimeOffset? UpdaterSeenAt);

/// <param name="Reason">Clave del motivo: NoCheck, UpdaterDown, Format, Stale, Rejected, Expired, Withdrawn, Security, EndOfLife, NewVersion o Current.</param>
public sealed record UpdateSummary(UpdateState State, string Reason, Version Installed, IReadOnlyList<Release> Newer, IReadOnlyList<Cve> Cves, bool PostgresBehind);

/// <summary>Estado del panel (sección 7.2), como el distintivo de GitLab más "sin datos".</summary>
public static class UpdateStatus
{
    /// <summary>Si el actualizador no dio señales en este tiempo, no corre.</summary>
    public static TimeSpan UpdaterTimeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Un chequeo más viejo que esto ya no alcanza para decir nada.</summary>
    public static TimeSpan MaxAge { get; } = TimeSpan.FromHours(48);

    /// <param name="serverVersionNum">server_version_num de la base, para avisar si está debajo de la mínima recomendada.</param>
    public static UpdateSummary Summarize(ChannelSnapshot snapshot, Version installed, DateTimeOffset now, int? serverVersionNum = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(installed);
        var channel = snapshot.Channel;
        var newer = channel is null ? [] : UpdatePlanner.Offered(channel).Where(r => r.Version > installed).OrderByDescending(r => r.Version).ToList();
        var security = newer.Where(r => r.Security).ToList();
        var cves = security.SelectMany(r => r.CveList).DistinctBy(c => c.CveId).ToList();
        var behind = channel is not null && serverVersionNum is { } num && PostgresBehind(channel, num);
        UpdateSummary Result(UpdateState state, string reason) => new(state, reason, installed, newer, cves, behind);

        if (channel is null || snapshot.CheckedAt is not { } checkedAt)
        {
            return Result(UpdateState.NoData, "NoCheck");
        }

        if (snapshot.UpdaterSeenAt is not { } seen || now - seen > UpdaterTimeout)
        {
            return Result(UpdateState.NoData, "UpdaterDown");
        }

        // Un canal en un formato que este núcleo no conoce no dice nada confiable: el actualizador se pone al día solo.
        if (channel.Format != ChannelVerifier.Format)
        {
            return Result(UpdateState.NoData, "Format");
        }

        if (snapshot.Rejected && snapshot.AttemptAt > checkedAt)
        {
            return Result(UpdateState.NoData, "Rejected");
        }

        if (now > channel.Expires)
        {
            return Result(UpdateState.NoData, "Expired");
        }

        if (now - checkedAt > MaxAge)
        {
            return Result(UpdateState.NoData, "Stale");
        }

        var lines = channel.Releases.Select(r => r.Version.Line()).ToList();
        if (channel.Find(installed) is { Withdrawn: true })
        {
            return Result(UpdateState.Urgent, "Withdrawn");
        }

        if (security.Count > 0)
        {
            return Result(UpdateState.Urgent, "Security");
        }

        var unsupported = lines.Count > 0 && !lines.Contains(installed.Line()) && lines.All(l => l.CompareTo(installed.Line()) > 0);
        if (unsupported || channel.EolDate is { } eol && DateOnly.FromDateTime(now.UtcDateTime) > eol)
        {
            return Result(UpdateState.Urgent, "EndOfLife");
        }

        return newer.Count > 0 ? Result(UpdateState.Available, "NewVersion") : Result(UpdateState.UpToDate, "Current");
    }

    /// <summary>La base corre una minor más vieja que la mínima que recomienda el canal para su major.</summary>
    public static bool PostgresBehind(ReleaseChannel channel, int serverVersionNum)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var major = serverVersionNum / 10000;
        if (channel.Minimums?.Postgres is not { } minimums || !minimums.TryGetValue(major.ToString(CultureInfo.InvariantCulture), out var minimum))
        {
            return false;
        }

        var parts = minimum.Split('.');
        return parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && serverVersionNum % 10000 < minor;
    }
}
