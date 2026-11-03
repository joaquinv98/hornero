using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.Content;

public sealed record SubscriberRow(long Id, string Email, string Language, DateTimeOffset? ConfirmedAt, long[] ComponentIds, DateTimeOffset CreatedAt);

/// <summary>
/// Suscripción por correo a los avisos de la red (sección 6.5 de diseno/contenidos.md). Doble confirmación: una casilla no
/// recibe nada hasta que abre el enlace que se le manda, y la respuesta es la misma exista o no.
/// </summary>
public sealed class SubscriptionService(BillingDbContext db, TimeProvider time, Notifier notifier, BillingCore core, AuditLog audit)
{
    /// <summary>Las suscripciones sin confirmar de más de estos días se borran al suscribir a otra persona.</summary>
    public const int PendingDays = 7;

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>La dirección pública de una ruta del sitio, con la URL pública configurada.</summary>
    public static string Url(BillingCore core, string path)
    {
        ArgumentNullException.ThrowIfNull(core);
        return (core.Options.PublicUrl?.TrimEnd('/') ?? string.Empty) + path;
    }

    /// <summary>
    /// Suscribe una casilla. Si ya está confirmada no cambia nada ni manda nada. Si está sin confirmar, actualiza el idioma y los
    /// componentes y vuelve a mandar el enlace, a lo sumo una vez por hora. Con componentes vacíos recibe todos.
    /// </summary>
    public async Task Subscribe(string? email, string? language, IReadOnlyCollection<long>? componentIds, CancellationToken ct = default)
    {
        var address = email?.Trim() ?? string.Empty;
        if (address.Length is 0 or > 320 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            throw new ValidationException("Escribí un correo válido, por ejemplo nombre@ejemplo.com.");
        }

        var wanted = (componentIds ?? []).Distinct().Order().ToArray();
        if (wanted.Length > 0)
        {
            var found = await db.Set<StatusComponent>().AsNoTracking().Where(c => wanted.Contains(c.Id) && !c.Hidden).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
            if (wanted.Except(found).Any())
            {
                throw new ValidationException("Alguno de los componentes elegidos no existe.");
            }
        }

        var now = time.GetUtcNow();
        var culture = ContentTexts.Reader(language);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Set<StatusSubscriber>().Where(s => s.ConfirmedAt == null && s.CreatedAt < now.AddDays(-PendingDays)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var lower = address.ToLowerInvariant();
        var subscriber = await db.Set<StatusSubscriber>().SingleOrDefaultAsync(s => s.Email.ToLower() == lower, ct).ConfigureAwait(false);
        if (subscriber is { ConfirmedAt: not null })
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        if (subscriber is null)
        {
            subscriber = new StatusSubscriber { Email = address, Language = culture, Token = NewToken(), ComponentIds = wanted, CreatedAt = now };
            db.Add(subscriber);
        }
        else
        {
            subscriber.Language = culture;
            subscriber.ComponentIds = wanted;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Otra suscripción de la misma casilla ganó la carrera: ya recibe el enlace que corresponde.
            return;
        }

        var model = new Dictionary<string, object?>
        {
            ["confirm_url"] = Url(core, $"/estado-red/suscripcion/confirmar/{subscriber.Token}"),
            ["unsubscribe_url"] = Url(core, $"/estado-red/suscripcion/baja/{subscriber.Token}"),
            ["subscriber"] = true,
        };
        await notifier.Queue(TemplateKeys.StatusConfirm, subscriber.Email, subscriber.Language, model, string.Create(CultureInfo.InvariantCulture, $"{TemplateKeys.StatusConfirm}:{subscriber.Id}:{now:yyyyMMddHH}"), null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Confirma la casilla del enlace. Nulo si el enlace no existe o ya se dio de baja.</summary>
    public async Task<StatusSubscriber?> Confirm(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64)
        {
            return null;
        }

        var subscriber = await db.Set<StatusSubscriber>().SingleOrDefaultAsync(s => s.Token == token, ct).ConfigureAwait(false);
        if (subscriber is { ConfirmedAt: null })
        {
            subscriber.ConfirmedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return subscriber;
    }

    /// <summary>Si el enlace corresponde a una suscripción. La página de baja lo mira antes de pedir la confirmación.</summary>
    public async Task<bool> Exists(string? token, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(token) && token.Length <= 64 && await db.Set<StatusSubscriber>().AnyAsync(s => s.Token == token, ct).ConfigureAwait(false);

    /// <summary>Borra la suscripción del enlace. Devuelve false si ya no estaba.</summary>
    public async Task<bool> Unsubscribe(string? token, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(token) && token.Length <= 64 && await db.Set<StatusSubscriber>().Where(s => s.Token == token).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

    // ---- Staff ----

    /// <summary>Los suscriptores sin su token: el enlace de baja no sale del panel.</summary>
    public async Task<IReadOnlyList<SubscriberRow>> List(bool? confirmed = null, int limit = 500, CancellationToken ct = default)
    {
        var query = db.Set<StatusSubscriber>().AsNoTracking();
        if (confirmed is { } yes)
        {
            query = yes ? query.Where(s => s.ConfirmedAt != null) : query.Where(s => s.ConfirmedAt == null);
        }

        return await query.OrderByDescending(s => s.Id).Take(Math.Clamp(limit, 1, 5000))
            .Select(s => new SubscriberRow(s.Id, s.Email, s.Language, s.ConfirmedAt, s.ComponentIds, s.CreatedAt)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<(int Confirmed, int Pending)> Counts(CancellationToken ct = default) =>
        (await db.Set<StatusSubscriber>().CountAsync(s => s.ConfirmedAt != null, ct).ConfigureAwait(false), await db.Set<StatusSubscriber>().CountAsync(s => s.ConfirmedAt == null, ct).ConfigureAwait(false));

    public async Task Remove(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (await db.Set<StatusSubscriber>().Where(s => s.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"No existe el suscriptor {id}.");
        }

        await audit.Record("status.subscriber.remove", $"subscriber:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Arma los avisos de un evento de la red para cada suscriptor confirmado que corresponde (sección 10 de
/// diseno/contenidos.md). Cada correo lleva su propia clave de deduplicación, así repetir el trabajo no manda dos veces. Los
/// trabajos de inicio y de fin de un mantenimiento ven si el horario con que se programaron sigue vigente.
/// </summary>
public sealed class StatusNotifyHandler(BillingDbContext db, Notifier notifier, BillingCore core, TimeProvider time) : IJobHandler
{
    private const int PageSize = 200;

    private const string RecipientsSql = """
        SELECT * FROM content.subscribers x
        WHERE x.confirmed_at IS NOT NULL AND x.id > @after AND (cardinality(x.component_ids) = 0 OR x.component_ids && @ids)
        ORDER BY x.id LIMIT 200
        """;

    public string Kind => StatusJobs.Notify;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var json = JsonDocument.Parse(job.Payload);
        var p = json.RootElement;
        var eventId = p.GetProperty("event_id").GetInt64();
        var what = p.GetProperty("what").GetString()!;
        var key = p.GetProperty("key").GetString()!;
        long? starts = p.TryGetProperty("starts_ticks", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
        long? ends = p.TryGetProperty("ends_ticks", out var e2) && e2.ValueKind == JsonValueKind.Number ? e2.GetInt64() : null;

        var e = await db.Set<StatusEvent>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId, ct).ConfigureAwait(false);
        if (e is null || Stale(e, what, starts, ends, time.GetUtcNow()))
        {
            return JobResult.Done;
        }

        var links = await db.Set<StatusEventComponent>().AsNoTracking().Where(l => l.EventId == eventId).ToListAsync(ct).ConfigureAwait(false);
        var linkedIds = links.Select(l => l.ComponentId).ToArray();
        var components = await db.Set<StatusComponent>().AsNoTracking().Where(c => linkedIds.Contains(c.Id)).ToListAsync(ct).ConfigureAwait(false);

        // Quien se suscribió a un grupo recibe lo de sus hijos.
        var filter = linkedIds.Concat(components.Where(c => c.ParentId is not null).Select(c => c.ParentId!.Value)).Distinct().ToArray();
        var (message, state) = await Message(e, what, key, ct).ConfigureAwait(false);
        var perCulture = new Dictionary<string, Dictionary<string, object?>>();
        long after = 0;
        while (true)
        {
            var page = await db.Set<StatusSubscriber>().FromSqlRaw(RecipientsSql, new NpgsqlParameter("after", after), new NpgsqlParameter("ids", filter)).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            foreach (var subscriber in page)
            {
                var culture = TemplateRenderer.CultureFor(subscriber.Language);
                if (!perCulture.TryGetValue(culture, out var model))
                {
                    perCulture[culture] = model = Model(e, what, state, message, links, components, culture);
                }

                var personal = new Dictionary<string, object?>(model)
                {
                    ["unsubscribe_url"] = SubscriptionService.Url(core, $"/estado-red/suscripcion/baja/{subscriber.Token}"),
                };
                var dedup = string.Create(CultureInfo.InvariantCulture, $"{TemplateKeys.StatusEvent}:{eventId}:{what}:{key}:{subscriber.Id}");
                await notifier.Queue(TemplateKeys.StatusEvent, subscriber.Email, culture, personal, dedup, null, ct).ConfigureAwait(false);
            }

            if (page.Count < PageSize)
            {
                return JobResult.Done;
            }

            after = page[^1].Id;
        }
    }

    /// <summary>El inicio y el fin de un mantenimiento ya no corresponden si se reprogramó, se canceló o se terminó antes de hora.</summary>
    private static bool Stale(StatusEvent e, string what, long? starts, long? ends, DateTimeOffset now)
    {
        if (what is "started" or "completed" && starts is not null)
        {
            var sameWindow = e.Kind == StatusEventKind.Maintenance && e.State == StatusEventState.Scheduled && e.StartsAt.UtcTicks == starts && e.EndsAt?.UtcTicks == ends;
            return !sameWindow || (what == "started" && e.EndsAt <= now);
        }

        return false;
    }

    /// <summary>
    /// El texto de la novedad que motiva el aviso (o la descripción del mantenimiento), por idioma, y el estado del evento en
    /// ese momento: el que dejó la novedad, no el de hoy. Si el trabajo corre tarde, el incidente puede estar ya resuelto y el
    /// aviso de "identificamos la causa" tiene que seguir diciendo eso.
    /// </summary>
    private async Task<(Dictionary<string, ContentText> Message, StatusEventState State)> Message(StatusEvent e, string what, string key, CancellationToken ct)
    {
        if (key.StartsWith('u') && long.TryParse(key.AsSpan(1), CultureInfo.InvariantCulture, out var updateId))
        {
            var update = await db.Set<StatusUpdate>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == updateId, ct).ConfigureAwait(false);
            return update is null ? ([], e.State) : (ContentTexts.Parse(update.Texts), update.State);
        }

        return (what is "scheduled" or "rescheduled" or "started" or "completed" ? ContentTexts.Parse(e.Texts) : [], e.State);
    }

    private Dictionary<string, object?> Model(StatusEvent e, string what, StatusEventState state, Dictionary<string, ContentText> message, List<StatusEventComponent> links, List<StatusComponent> components, string culture)
    {
        var title = ContentTexts.Pick(ContentTexts.Parse(e.Texts), culture)?.Title ?? string.Empty;
        var text = ContentTexts.Pick(message, culture)?.Body;
        var named = links
            .Select(l => components.Find(c => c.Id == l.ComponentId) is { } c ? new Dictionary<string, object?> { ["name"] = ContentTexts.Pick(ContentTexts.Parse(c.Texts), culture)?.Title ?? c.Slug, ["impact"] = l.Impact.ToString().ToLowerInvariant() } : null)
            .OfType<Dictionary<string, object?>>()
            .OrderBy(c => (string)c["name"]!, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new Dictionary<string, object?>
        {
            ["kind"] = e.Kind.ToString().ToLowerInvariant(),
            ["event"] = what,
            ["state"] = state.ToString().ToLowerInvariant(),
            ["title"] = title,
            ["message"] = string.IsNullOrWhiteSpace(text) ? null : Markdown.ToPlainText(text, 1000),
            ["components"] = named,
            ["starts"] = Local(e.StartsAt, culture),
            ["ends"] = e.EndsAt is { } end ? Local(end, culture) : null,
            ["url"] = SubscriptionService.Url(core, $"/estado-red/{e.Id.ToString(CultureInfo.InvariantCulture)}"),
            ["subscriber"] = true,
        };
    }

    /// <summary>Fecha y hora en la zona del proveedor, con el desfase a UTC.</summary>
    private string Local(DateTimeOffset at, string culture)
    {
        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(core.Options.TimeZone));
        var text = culture == "en" ? local.ToString("MMM d, yyyy HH:mm", CultureInfo.GetCultureInfo("en")) : local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        return $"{text} (UTC{local.ToString("zzz", CultureInfo.InvariantCulture)})";
    }
}
