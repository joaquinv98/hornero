using System.Globalization;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Content;

public sealed record ComponentInput(
    long? ParentId,
    ComponentKind Kind,
    string? Slug,
    int SortOrder,
    bool Hidden,
    long[] ProductIds,
    IReadOnlyDictionary<string, ContentText> Texts,
    int? Revision = null);

public sealed record ComponentImpact(long ComponentId, ComponentStatus Impact);

/// <param name="Title">El título en cada idioma.</param>
/// <param name="Message">El primer mensaje, en cada idioma (en el cuerpo).</param>
public sealed record IncidentInput(
    IReadOnlyDictionary<string, ContentText> Title,
    IReadOnlyDictionary<string, ContentText> Message,
    IReadOnlyList<ComponentImpact> Components,
    StatusEventState State = StatusEventState.Investigating,
    DateTimeOffset? StartedAt = null);

public sealed record MaintenanceInput(
    IReadOnlyDictionary<string, ContentText> Title,
    IReadOnlyList<long> ComponentIds,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

/// <param name="State">El estado en que queda el incidente. Nulo lo deja como está.</param>
/// <param name="Message">El mensaje, en cada idioma (en el cuerpo).</param>
/// <param name="Components">Si viene, reemplaza los componentes afectados y su impacto.</param>
public sealed record StatusUpdateInput(IReadOnlyDictionary<string, ContentText> Message, StatusEventState? State = null, IReadOnlyList<ComponentImpact>? Components = null, int? Revision = null);

public sealed record ComponentRow(StatusComponent Component, IReadOnlyDictionary<string, ContentText> Texts, ComponentStatus Status);

public sealed record ComponentView(long Id, long? ParentId, ComponentKind Kind, string Slug, string Name, string? Description, ComponentStatus Status, int SortOrder, IReadOnlyList<ComponentView> Children);

public sealed record EventComponent(long ComponentId, string Name, ComponentStatus Impact);

public sealed record UpdateView(long Id, StatusEventState State, DateTimeOffset PostedAt, string Html, string Text, string Language);

public sealed record EventView(
    long Id,
    StatusEventKind Kind,
    StatusEventState State,
    MaintenancePhase? Phase,
    string Title,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<EventComponent> Components,
    IReadOnlyList<UpdateView> Updates,
    string Language,
    bool Fallback,
    int Revision = 0);

public sealed record StatusOverview(
    ComponentStatus Overall,
    IReadOnlyList<ComponentView> Components,
    IReadOnlyList<EventView> Active,
    IReadOnlyList<EventView> Upcoming,
    IReadOnlyList<EventView> Recent);

/// <summary>Nombres de los trabajos del estado de la red.</summary>
public static class StatusJobs
{
    /// <summary>Arma los avisos de un evento para los suscriptores. Clave de entidad: <c>content.status:{evento}</c>.</summary>
    public const string Notify = "content.status-notify";
}

/// <summary>
/// Estado de la red (sección 6 de diseno/contenidos.md): componentes, incidentes y mantenimientos. El estado de un
/// componente se calcula con los eventos vigentes, nunca se guarda. Los avisos a los suscriptores salen de un trabajo por
/// la cola, que se encola en la misma transacción que el cambio.
/// </summary>
public sealed class StatusService(BillingDbContext db, TimeProvider time, AuditLog audit, JobQueue jobs, IOptions<ContentOptions> options)
{
    /// <summary>Cuántos días atrás se muestran los eventos terminados en la página principal.</summary>
    public const int RecentDays = 14;

    /// <summary>Con cuántos días de anticipación se muestra un mantenimiento próximo.</summary>
    public const int UpcomingDays = 30;

    // ---- Componentes (staff) ----

    public async Task<IReadOnlyList<ComponentRow>> Components(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var statuses = await Statuses(now, ct).ConfigureAwait(false);
        var all = await db.Set<StatusComponent>().AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var children = all.Where(c => c.ParentId is not null).GroupBy(c => c.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(c => statuses.GetValueOrDefault(c.Id)).ToList());
        return [.. all.Select(c => new ComponentRow(c, ContentTexts.Parse(c.Texts), c.Kind == ComponentKind.Group ? StatusRules.Worst(children.GetValueOrDefault(c.Id) ?? []) : statuses.GetValueOrDefault(c.Id)))];
    }

    public async Task<long> CreateComponent(ComponentInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = await CheckComponent(input, null, ct).ConfigureAwait(false);
        var slug = await ContentDb.FreeSlug(input.Slug, texts, s => db.Set<StatusComponent>().AnyAsync(c => c.Slug == s, ct)).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var component = new StatusComponent { Kind = input.Kind, Slug = slug, CreatedAt = now, UpdatedAt = now };
        Fill(component, input, texts);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(component);
        await ContentDb.Save(db, "el componente", ct).ConfigureAwait(false);
        await audit.Record("status.component.create", $"component:{component.Id}", new { slug }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return component.Id;
    }

    public async Task UpdateComponent(long id, ComponentInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var component = await db.Set<StatusComponent>().SingleOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el componente {id}.");
        ContentDb.CheckRevision(input.Revision, component.Revision, "el componente");
        if (input.Kind != component.Kind)
        {
            throw new ValidationException("El tipo de un componente no se cambia: creá otro.");
        }

        var texts = await CheckComponent(input, id, ct).ConfigureAwait(false);
        if (input.Slug is { Length: > 0 } slug && slug != component.Slug)
        {
            component.Slug = await ContentDb.FreeSlug(slug, null, s => db.Set<StatusComponent>().AnyAsync(c => c.Slug == s, ct)).ConfigureAwait(false);
        }

        Fill(component, input, texts);
        component.Revision++;
        component.UpdatedAt = time.GetUtcNow();
        await ContentDb.Save(db, "el componente", ct).ConfigureAwait(false);
        await audit.Record("status.component.update", $"component:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Un componente con historial o con hijos no se borra: se oculta.</summary>
    public async Task DeleteComponent(long id, CancellationToken ct = default)
    {
        if (!await db.Set<StatusComponent>().AnyAsync(c => c.Id == id, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe el componente {id}.");
        }

        if (await db.Set<StatusComponent>().AnyAsync(c => c.ParentId == id, ct).ConfigureAwait(false) || await db.Set<StatusEventComponent>().AnyAsync(l => l.ComponentId == id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("El componente tiene hijos o historial: ocultalo en lugar de borrarlo.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Set<StatusComponent>().Where(c => c.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await audit.Record("status.component.delete", $"component:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ---- Incidentes (staff) ----

    /// <summary>Abre un incidente con su primer mensaje y avisa a los suscriptores de los componentes que toca.</summary>
    public async Task<long> OpenIncident(IncidentInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.State == StatusEventState.Resolved || !StatusRules.IsIncidentState(input.State))
        {
            throw new ValidationException("Un incidente se abre investigando, identificado o monitoreando.");
        }

        var title = CheckTitle(input.Title);
        var message = CheckMessage(input.Message);
        var links = await CheckImpacts(StatusEventKind.Incident, input.Components, ct).ConfigureAwait(false);
        var now = Micro(time.GetUtcNow());
        var started = input.StartedAt is { } at ? Micro(at) : now;
        if (started > now.AddMinutes(5))
        {
            throw new ValidationException("Un incidente no puede empezar en el futuro: programá un mantenimiento.");
        }

        var e = new StatusEvent { Kind = StatusEventKind.Incident, State = input.State, StartsAt = started, Texts = ContentTexts.Serialize(title), CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(e);
        await ContentDb.Save(db, "el incidente", ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            db.Add(new StatusEventComponent { EventId = e.Id, ComponentId = link.ComponentId, Impact = link.Impact });
        }

        var update = new StatusUpdate { EventId = e.Id, State = input.State, PostedAt = now, Texts = ContentTexts.Serialize(message), Author = Actor.Current.Id };
        db.Add(update);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await NotifyLater(e.Id, "opened", $"u{update.Id}", null, ct).ConfigureAwait(false);
        await audit.Record("status.incident.open", $"event:{e.Id}", new { components = links.Select(l => l.ComponentId) }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return e.Id;
    }

    /// <summary>
    /// Publica una novedad sobre un incidente o un mantenimiento. En un incidente puede cambiar el estado (al resolverse queda
    /// la hora de fin y los componentes dejan de estar afectados) y los componentes con su impacto.
    /// </summary>
    public async Task PostUpdate(long eventId, StatusUpdateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var message = CheckMessage(input.Message);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var e = await db.Set<StatusEvent>().SingleOrDefaultAsync(x => x.Id == eventId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el evento {eventId}.");
        ContentDb.CheckRevision(input.Revision, e.Revision, "el evento");
        var now = Micro(time.GetUtcNow());
        if (!StatusRules.IsOpen(e) || (e.Kind == StatusEventKind.Maintenance && StatusRules.Phase(e, now) == MaintenancePhase.Completed))
        {
            throw new InvalidOperationException(e.Kind == StatusEventKind.Incident ? "El incidente ya está resuelto: si vuelve a pasar, abrí otro." : "El mantenimiento ya terminó o se canceló.");
        }

        var state = input.State ?? e.State;
        var machine = e.Kind == StatusEventKind.Incident ? StatusRules.IncidentStates : StatusRules.MaintenanceStates;
        if (!machine.CanMove(e.State, state) || (e.Kind == StatusEventKind.Maintenance && state != StatusEventState.Scheduled))
        {
            throw new InvalidOperationException($"No se puede pasar de {e.State} a {state}.");
        }

        if (input.Components is not null)
        {
            var links = await CheckImpacts(e.Kind, input.Components, ct).ConfigureAwait(false);
            await db.Set<StatusEventComponent>().Where(l => l.EventId == eventId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            foreach (var link in links)
            {
                db.Add(new StatusEventComponent { EventId = eventId, ComponentId = link.ComponentId, Impact = link.Impact });
            }
        }

        e.State = state;
        if (state == StatusEventState.Resolved)
        {
            e.EndsAt = now < e.StartsAt ? e.StartsAt : now;
        }

        e.Revision++;
        e.UpdatedAt = now;
        var update = new StatusUpdate { EventId = eventId, State = state, PostedAt = now, Texts = ContentTexts.Serialize(message), Author = Actor.Current.Id };
        db.Add(update);
        await ContentDb.Save(db, "el evento", ct).ConfigureAwait(false);
        await NotifyLater(eventId, state == StatusEventState.Resolved ? "resolved" : "updated", $"u{update.Id}", null, ct).ConfigureAwait(false);
        await audit.Record(e.Kind == StatusEventKind.Incident ? "status.incident.update" : "status.maintenance.update", $"event:{eventId}", new { state = state.ToString() }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Corrige el título de un evento. No es una novedad y no avisa a nadie.</summary>
    public async Task Retitle(long eventId, IReadOnlyDictionary<string, ContentText> title, int? revision = null, CancellationToken ct = default)
    {
        var clean = CheckTitle(title);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var e = await db.Set<StatusEvent>().SingleOrDefaultAsync(x => x.Id == eventId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el evento {eventId}.");
        ContentDb.CheckRevision(revision, e.Revision, "el evento");
        var summary = ContentTexts.Parse(e.Texts);
        e.Texts = ContentTexts.Serialize(clean.ToDictionary(t => t.Key, t => t.Value with { Summary = summary.GetValueOrDefault(t.Key)?.Summary, Body = summary.GetValueOrDefault(t.Key)?.Body }));
        e.Revision++;
        e.UpdatedAt = time.GetUtcNow();
        await ContentDb.Save(db, "el evento", ct).ConfigureAwait(false);
        await audit.Record("status.event.retitle", $"event:{eventId}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ---- Mantenimientos (staff) ----

    /// <summary>Programa un mantenimiento. Avisa ya, al empezar y al terminar.</summary>
    public async Task<long> ScheduleMaintenance(MaintenanceInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var title = CheckTitle(input.Title);
        var now = Micro(time.GetUtcNow());
        var starts = Micro(input.StartsAt);
        var ends = Micro(input.EndsAt);
        CheckWindow(starts, ends, now);
        var links = await CheckImpacts(StatusEventKind.Maintenance, [.. input.ComponentIds.Distinct().Select(c => new ComponentImpact(c, ComponentStatus.Maintenance))], ct).ConfigureAwait(false);
        var e = new StatusEvent { Kind = StatusEventKind.Maintenance, State = StatusEventState.Scheduled, StartsAt = starts, EndsAt = ends, Texts = ContentTexts.Serialize(title), CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(e);
        await ContentDb.Save(db, "el mantenimiento", ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            db.Add(new StatusEventComponent { EventId = e.Id, ComponentId = link.ComponentId, Impact = link.Impact });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await NotifyLater(e.Id, "scheduled", "c", null, ct).ConfigureAwait(false);
        await ScheduleBoundaryJobs(e, now, ct).ConfigureAwait(false);
        await audit.Record("status.maintenance.schedule", $"event:{e.Id}", new { starts, ends }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return e.Id;
    }

    /// <summary>Cambia el desde y el hasta de un mantenimiento próximo o en curso. Los trabajos de inicio y fin viejos quedan sin efecto.</summary>
    public async Task Reschedule(long eventId, DateTimeOffset startsAt, DateTimeOffset endsAt, int? revision = null, CancellationToken ct = default)
    {
        var now = Micro(time.GetUtcNow());
        var starts = Micro(startsAt);
        var ends = Micro(endsAt);
        CheckWindow(starts, ends, now);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var e = await Maintenance(eventId, now, ct).ConfigureAwait(false);
        ContentDb.CheckRevision(revision, e.Revision, "el mantenimiento");
        e.StartsAt = starts;
        e.EndsAt = ends;
        e.Revision++;
        e.UpdatedAt = now;
        await ContentDb.Save(db, "el mantenimiento", ct).ConfigureAwait(false);
        await NotifyLater(eventId, "rescheduled", $"r{e.Revision}", null, ct).ConfigureAwait(false);
        await ScheduleBoundaryJobs(e, now, ct).ConfigureAwait(false);
        await audit.Record("status.maintenance.reschedule", $"event:{eventId}", new { starts, ends }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task Cancel(long eventId, int? revision = null, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = Micro(time.GetUtcNow());
        var e = await Maintenance(eventId, now, ct).ConfigureAwait(false);
        ContentDb.CheckRevision(revision, e.Revision, "el mantenimiento");
        e.State = StatusEventState.Cancelled;
        e.Revision++;
        e.UpdatedAt = now;
        await ContentDb.Save(db, "el mantenimiento", ct).ConfigureAwait(false);
        await NotifyLater(eventId, "cancelled", "x", null, ct).ConfigureAwait(false);
        await audit.Record("status.maintenance.cancel", $"event:{eventId}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Da por terminado un mantenimiento que ya empezó, antes de la hora de fin.</summary>
    public async Task Complete(long eventId, int? revision = null, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = Micro(time.GetUtcNow());
        var e = await Maintenance(eventId, now, ct).ConfigureAwait(false);
        ContentDb.CheckRevision(revision, e.Revision, "el mantenimiento");
        if (StatusRules.Phase(e, now) != MaintenancePhase.InProgress)
        {
            throw new InvalidOperationException("El mantenimiento todavía no empezó: cancelalo en lugar de darlo por terminado.");
        }

        e.State = StatusEventState.Completed;
        e.EndsAt = now;
        e.Revision++;
        e.UpdatedAt = now;
        await ContentDb.Save(db, "el mantenimiento", ct).ConfigureAwait(false);
        await NotifyLater(eventId, "completed", "x", null, ct).ConfigureAwait(false);
        await audit.Record("status.maintenance.complete", $"event:{eventId}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<StatusEvent> Maintenance(long eventId, DateTimeOffset now, CancellationToken ct)
    {
        var e = await db.Set<StatusEvent>().SingleOrDefaultAsync(x => x.Id == eventId && x.Kind == StatusEventKind.Maintenance, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el mantenimiento {eventId}.");
        if (e.State != StatusEventState.Scheduled || StatusRules.Phase(e, now) == MaintenancePhase.Completed)
        {
            throw new InvalidOperationException("El mantenimiento ya terminó o se canceló.");
        }

        return e;
    }

    private static void CheckWindow(DateTimeOffset starts, DateTimeOffset ends, DateTimeOffset now)
    {
        if (ends <= starts)
        {
            throw new ValidationException("El mantenimiento tiene que terminar después de empezar.");
        }

        if (ends <= now)
        {
            throw new ValidationException("El mantenimiento ya terminó: elegí un horario que no haya pasado.");
        }
    }

    /// <summary>Los avisos de inicio y de fin salen a su hora por la cola. Llevan el horario con que se programaron: si cambia, no hacen nada.</summary>
    private async Task ScheduleBoundaryJobs(StatusEvent e, DateTimeOffset now, CancellationToken ct)
    {
        var starts = e.StartsAt.UtcTicks;
        var ends = e.EndsAt!.Value.UtcTicks;
        if (e.StartsAt > now)
        {
            await NotifyLater(e.Id, "started", $"s{starts}", e.StartsAt, ct, starts, ends).ConfigureAwait(false);
        }

        await NotifyLater(e.Id, "completed", $"e{ends}", e.EndsAt, ct, starts, ends).ConfigureAwait(false);
    }

    /// <summary>
    /// Encola el aviso. Los inmediatos de un mismo evento salen en orden (misma clave de entidad). Los que esperan una hora no
    /// llevan clave: un trabajo pendiente para el lunes no puede frenar el aviso de una cancelación del viernes.
    /// </summary>
    private Task NotifyLater(long eventId, string what, string key, DateTimeOffset? runAt, CancellationToken ct, long? startsTicks = null, long? endsTicks = null) =>
        jobs.Enqueue(
            StatusJobs.Notify,
            new { event_id = eventId, what, key, starts_ticks = startsTicks, ends_ticks = endsTicks },
            entityKey: runAt is null ? $"content.status:{eventId.ToString(CultureInfo.InvariantCulture)}" : null,
            dedupKey: $"{StatusJobs.Notify}:{eventId.ToString(CultureInfo.InvariantCulture)}:{what}:{key}",
            runAt: runAt,
            ct: ct);

    // ---- Lectura (staff y público) ----

    /// <summary>El estado de cada componente con los eventos vigentes a esa hora. Un grupo toma el más grave de sus hijos.</summary>
    private async Task<Dictionary<long, ComponentStatus>> Statuses(DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.Set<StatusEvent>().AsNoTracking()
            .Where(e => (e.Kind == StatusEventKind.Incident && e.State != StatusEventState.Resolved)
                || (e.Kind == StatusEventKind.Maintenance && e.State == StatusEventState.Scheduled && e.StartsAt <= now))
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = open.Where(e => StatusRules.Affects(e, now)).Select(e => e.Id).ToArray();
        var links = await db.Set<StatusEventComponent>().AsNoTracking().Where(l => ids.Contains(l.EventId)).ToListAsync(ct).ConfigureAwait(false);
        return links.GroupBy(l => l.ComponentId).ToDictionary(g => g.Key, g => StatusRules.Worst(g.Select(l => l.Impact)));
    }

    /// <summary>La página principal: estado global, componentes, eventos vigentes, mantenimientos próximos y lo terminado en los últimos días.</summary>
    public async Task<StatusOverview> Overview(string? language, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var all = await db.Set<StatusComponent>().AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var hidden = all.Where(c => c.Hidden).Select(c => c.Id).ToHashSet();
        var statuses = await Statuses(now, ct).ConfigureAwait(false);
        var tree = Tree(all.Where(c => !c.Hidden && (c.ParentId is null || !hidden.Contains(c.ParentId.Value))).ToList(), null, statuses, language);
        var overall = StatusRules.Worst(tree.Select(c => c.Status));

        var since = now.AddDays(-RecentDays);
        var events = await db.Set<StatusEvent>().AsNoTracking()
            .Where(e => (e.Kind == StatusEventKind.Incident && (e.State != StatusEventState.Resolved || e.EndsAt >= since))
                || (e.Kind == StatusEventKind.Maintenance && e.State != StatusEventState.Cancelled && (e.EndsAt >= since) && e.StartsAt <= now.AddDays(UpcomingDays)))
            .OrderByDescending(e => e.StartsAt).ThenByDescending(e => e.Id).ToListAsync(ct).ConfigureAwait(false);
        var views = await Views(events, hidden, language, now, withUpdates: true, ct).ConfigureAwait(false);
        var active = views.Where(v => v.Kind == StatusEventKind.Incident ? v.State != StatusEventState.Resolved : v.Phase == MaintenancePhase.InProgress).ToList();
        var upcoming = views.Where(v => v is { Kind: StatusEventKind.Maintenance, Phase: MaintenancePhase.Upcoming }).OrderBy(v => v.StartsAt).ToList();
        var recent = views.Where(v => !active.Contains(v) && !upcoming.Contains(v)).ToList();
        return new StatusOverview(overall, tree, active, upcoming, recent);
    }

    private static List<ComponentView> Tree(List<StatusComponent> all, long? parent, Dictionary<long, ComponentStatus> statuses, string? language)
    {
        var nodes = new List<ComponentView>();
        foreach (var c in all.Where(c => c.ParentId == parent))
        {
            var children = Tree(all, c.Id, statuses, language);
            var status = c.Kind == ComponentKind.Group ? StatusRules.Worst(children.Select(x => x.Status)) : statuses.GetValueOrDefault(c.Id);
            var text = ContentTexts.Pick(ContentTexts.Parse(c.Texts), language);
            nodes.Add(new ComponentView(c.Id, c.ParentId, c.Kind, c.Slug, text?.Title ?? c.Slug, text?.Summary, status, c.SortOrder, children));
        }

        return nodes;
    }

    /// <summary>Un evento con todas sus novedades. Un evento que solo toca componentes ocultos no se muestra al público.</summary>
    public async Task<EventView?> Event(long id, string? language, bool includeHidden = false, CancellationToken ct = default)
    {
        var e = await db.Set<StatusEvent>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (e is null)
        {
            return null;
        }

        var hidden = includeHidden ? [] : await db.Set<StatusComponent>().AsNoTracking().Where(c => c.Hidden).Select(c => c.Id).ToHashSetAsync(ct).ConfigureAwait(false);
        return (await Views([e], hidden, language, time.GetUtcNow(), withUpdates: true, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <summary>El título de un evento en cada idioma, tal como se guardó: para corregirlo desde el panel. Nulo si no existe.</summary>
    public async Task<IReadOnlyDictionary<string, ContentText>?> EventTitles(long id, CancellationToken ct = default)
    {
        var texts = await db.Set<StatusEvent>().AsNoTracking().Where(e => e.Id == id).Select(e => e.Texts).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return texts is null ? null : ContentTexts.Parse(texts);
    }

    /// <summary>
    /// Los eventos terminados de un mes (incidentes resueltos y mantenimientos que terminaron), los más nuevos primero. El mes
    /// empieza y termina a medianoche en <paramref name="zone"/>, que es la del proveedor: sin ella vale UTC.
    /// </summary>
    public Task<IReadOnlyList<EventView>> History(int year, int month, string? language, TimeZoneInfo? zone = null, CancellationToken ct = default) =>
        Finished(year, month, language, zone, staff: false, ct);

    /// <summary>
    /// Lo mismo que <see cref="History"/> para el staff: suma los mantenimientos cancelados y no esconde los eventos que solo
    /// tocan componentes ocultos.
    /// </summary>
    public Task<IReadOnlyList<EventView>> FinishedForStaff(int year, int month, string? language, TimeZoneInfo? zone = null, CancellationToken ct = default) =>
        Finished(year, month, language, zone, staff: true, ct);

    private async Task<IReadOnlyList<EventView>> Finished(int year, int month, string? language, TimeZoneInfo? zone, bool staff, CancellationToken ct)
    {
        var tz = zone ?? TimeZoneInfo.Utc;
        var from = MonthStart(year, month, tz);
        var to = MonthStart(month == 12 ? year + 1 : year, month == 12 ? 1 : month + 1, tz);
        var now = time.GetUtcNow();
        var inMonth = db.Set<StatusEvent>().AsNoTracking().Where(e => e.StartsAt >= from && e.StartsAt < to);
        var query = staff
            ? inMonth.Where(e => (e.Kind == StatusEventKind.Incident && e.State == StatusEventState.Resolved)
                || (e.Kind == StatusEventKind.Maintenance && (e.EndsAt <= now || e.State == StatusEventState.Completed || e.State == StatusEventState.Cancelled)))
            : inMonth.Where(e => e.State != StatusEventState.Cancelled
                && ((e.Kind == StatusEventKind.Incident && e.State == StatusEventState.Resolved) || (e.Kind == StatusEventKind.Maintenance && (e.EndsAt <= now || e.State == StatusEventState.Completed))));
        var events = await query.OrderByDescending(e => e.StartsAt).ThenByDescending(e => e.Id).ToListAsync(ct).ConfigureAwait(false);
        var hidden = staff ? [] : await db.Set<StatusComponent>().AsNoTracking().Where(c => c.Hidden).Select(c => c.Id).ToHashSetAsync(ct).ConfigureAwait(false);
        return await Views(events, hidden, language, now, withUpdates: false, ct).ConfigureAwait(false);
    }

    /// <summary>Todos los eventos del staff, los más nuevos primero, con filtro por tipo y por si siguen abiertos.</summary>
    public async Task<IReadOnlyList<EventView>> List(StatusEventKind? kind = null, bool openOnly = false, int limit = 100, string? language = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var query = db.Set<StatusEvent>().AsNoTracking();
        if (kind is { } k)
        {
            query = query.Where(e => e.Kind == k);
        }

        if (openOnly)
        {
            query = query.Where(e => (e.Kind == StatusEventKind.Incident && e.State != StatusEventState.Resolved) || (e.Kind == StatusEventKind.Maintenance && e.State == StatusEventState.Scheduled && e.EndsAt > now));
        }

        var events = await query.OrderByDescending(e => e.StartsAt).ThenByDescending(e => e.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct).ConfigureAwait(false);
        return await Views(events, [], language, now, withUpdates: false, ct).ConfigureAwait(false);
    }

    /// <summary>Los eventos vigentes y los mantenimientos de la próxima semana que tocan componentes de esos productos.</summary>
    public async Task<IReadOnlyList<EventView>> IssuesForProducts(IReadOnlyCollection<long> productIds, string? language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        var ids = productIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var components = await db.Set<StatusComponent>().AsNoTracking().Where(c => !c.Hidden).ToListAsync(ct).ConfigureAwait(false);
        var mine = components.Where(c => c.ProductIds.Intersect(ids).Any()).Select(c => c.Id).ToArray();
        if (mine.Length == 0)
        {
            return [];
        }

        var eventIds = await db.Set<StatusEventComponent>().AsNoTracking().Where(l => mine.Contains(l.ComponentId)).Select(l => l.EventId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var week = now.AddDays(7);
        var events = await db.Set<StatusEvent>().AsNoTracking()
            .Where(e => eventIds.Contains(e.Id) && ((e.Kind == StatusEventKind.Incident && e.State != StatusEventState.Resolved) || (e.Kind == StatusEventKind.Maintenance && e.State == StatusEventState.Scheduled && e.EndsAt > now && e.StartsAt <= week)))
            .OrderBy(e => e.StartsAt).ToListAsync(ct).ConfigureAwait(false);
        var hidden = components.Where(c => c.Hidden).Select(c => c.Id).ToHashSet();
        return await Views(events, hidden, language, now, withUpdates: false, ct).ConfigureAwait(false);
    }

    private async Task<List<EventView>> Views(List<StatusEvent> events, HashSet<long> hidden, string? language, DateTimeOffset now, bool withUpdates, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var ids = events.Select(e => e.Id).ToArray();
        var links = (await db.Set<StatusEventComponent>().AsNoTracking().Where(l => ids.Contains(l.EventId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(l => l.EventId);
        var componentIds = links.SelectMany(g => g).Select(l => l.ComponentId).Distinct().ToArray();
        var components = await db.Set<StatusComponent>().AsNoTracking().Where(c => componentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var updates = withUpdates
            ? (await db.Set<StatusUpdate>().AsNoTracking().Where(u => ids.Contains(u.EventId)).OrderBy(u => u.PostedAt).ThenBy(u => u.Id).ToListAsync(ct).ConfigureAwait(false)).ToLookup(u => u.EventId)
            : null;
        var views = new List<EventView>();
        foreach (var e in events)
        {
            var own = links[e.Id].ToList();
            if (own.Count > 0 && own.All(l => hidden.Contains(l.ComponentId)))
            {
                continue;
            }

            var picked = ContentTexts.PickWithLanguage(ContentTexts.Parse(e.Texts), language);
            if (picked is not { } found)
            {
                continue;
            }

            var eventComponents = own.Where(l => !hidden.Contains(l.ComponentId) && components.ContainsKey(l.ComponentId))
                .Select(l => new EventComponent(l.ComponentId, ContentTexts.Pick(ContentTexts.Parse(components[l.ComponentId].Texts), language)?.Title ?? components[l.ComponentId].Slug, l.Impact))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var updateViews = updates is null ? [] : updates[e.Id].Select(u => ToUpdate(u, language)).OfType<UpdateView>().ToList();
            views.Add(new EventView(
                e.Id,
                e.Kind,
                e.State,
                e.Kind == StatusEventKind.Maintenance ? StatusRules.Phase(e, now) : null,
                found.Text.Title,
                found.Text.Body is { Length: > 0 } body ? Markdown.ToHtml(body, options.Value.Rendering) : null,
                e.StartsAt,
                e.EndsAt,
                eventComponents,
                updateViews,
                found.Language,
                found.Language != ContentTexts.Reader(language),
                e.Revision));
        }

        return views;
    }

    private UpdateView? ToUpdate(StatusUpdate u, string? language)
    {
        var picked = ContentTexts.PickWithLanguage(ContentTexts.Parse(u.Texts), language);
        if (picked is not { } found)
        {
            return null;
        }

        var body = found.Text.Body ?? string.Empty;
        return new UpdateView(u.Id, u.State, u.PostedAt, Markdown.ToHtml(body, options.Value.Rendering), Markdown.ToPlainText(body, 1000), found.Language);
    }

    // ---- Validaciones ----

    private static DateTimeOffset MonthStart(int year, int month, TimeZoneInfo zone)
    {
        var local = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateTimeOffset Micro(DateTimeOffset at) => new(at.UtcTicks - (at.UtcTicks % 10), TimeSpan.Zero);

    private static Dictionary<string, ContentText> CheckTitle(IReadOnlyDictionary<string, ContentText> title)
    {
        var clean = ContentTexts.Clean(title ?? new Dictionary<string, ContentText>());
        if (ContentTexts.Error(clean) is { } error)
        {
            throw new ValidationException(error);
        }

        return clean;
    }

    /// <summary>El mensaje de una novedad: hace falta el cuerpo en al menos un idioma. El título no se usa.</summary>
    private static Dictionary<string, ContentText> CheckMessage(IReadOnlyDictionary<string, ContentText> message)
    {
        var clean = ContentTexts.Clean(message ?? new Dictionary<string, ContentText>());
        if (clean.Count == 0 || clean.Any(m => !ContentTexts.Languages.Contains(m.Key)) || clean.Any(m => m.Value.Body is not { Length: > 0 } || m.Value.Body.Length > 20_000))
        {
            throw new ValidationException("Falta el mensaje, con hasta 20.000 caracteres, en al menos un idioma.");
        }

        return clean.ToDictionary(m => m.Key, m => new ContentText(string.Empty, null, m.Value.Body));
    }

    private async Task<List<ComponentImpact>> CheckImpacts(StatusEventKind kind, IReadOnlyList<ComponentImpact> impacts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(impacts);
        var distinct = impacts.GroupBy(i => i.ComponentId).Select(g => g.Last()).ToList();
        if (distinct.Count == 0)
        {
            throw new ValidationException("Elegí al menos un servidor o servicio que afecte.");
        }

        foreach (var impact in distinct)
        {
            if (!StatusRules.ValidImpact(kind, impact.Impact))
            {
                throw new ValidationException(kind == StatusEventKind.Incident ? "El impacto de un incidente es degradado, interrupción parcial o interrupción total." : "Un mantenimiento marca sus componentes en mantenimiento.");
            }
        }

        var ids = distinct.Select(i => i.ComponentId).ToArray();
        var found = await db.Set<StatusComponent>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Kind, ct).ConfigureAwait(false);
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var componentKind))
            {
                throw new ValidationException($"No existe el componente {id}.");
            }

            if (componentKind == ComponentKind.Group)
            {
                throw new ValidationException($"El componente {id} es un grupo: elegí los servidores o servicios que lo forman.");
            }
        }

        return distinct;
    }

    private async Task<Dictionary<string, ContentText>> CheckComponent(ComponentInput input, long? ownId, CancellationToken ct)
    {
        var texts = ContentTexts.Clean(input.Texts ?? new Dictionary<string, ContentText>());
        if (ContentTexts.Error(texts, titleMax: 100, summaryMax: 500) is { } error)
        {
            throw new ValidationException(error);
        }

        if (input.ParentId is { } parentId)
        {
            var parent = await db.Set<StatusComponent>().AsNoTracking().Where(c => c.Id == parentId).Select(c => new { c.Kind, c.ParentId }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (parent is null || parentId == ownId)
            {
                throw new ValidationException($"No existe el grupo {parentId}.");
            }

            if (parent.Kind != ComponentKind.Group || input.Kind == ComponentKind.Group)
            {
                throw new ValidationException("Un componente cuelga de un grupo, y un grupo no cuelga de otro.");
            }
        }

        if (input.Kind == ComponentKind.Group && ownId is { } own && await db.Set<StatusEventComponent>().AnyAsync(l => l.ComponentId == own, ct).ConfigureAwait(false))
        {
            throw new ValidationException("Un grupo no puede tener historial propio.");
        }

        await ContentDb.CheckAudience(db, ContentAudience.Public, input.ProductIds ?? [], [], ct).ConfigureAwait(false);
        return texts;
    }

    private static void Fill(StatusComponent component, ComponentInput input, Dictionary<string, ContentText> texts)
    {
        component.ParentId = input.ParentId;
        component.SortOrder = input.SortOrder;
        component.Hidden = input.Hidden;
        component.ProductIds = [.. input.ProductIds.Distinct().Order()];
        component.Texts = ContentTexts.Serialize(texts);
    }
}
