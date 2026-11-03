using BillingOss.App.Persistence;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Content;

/// <summary>Lo pendiente de los contenidos para la portada del panel (sección 9 de diseno/contenidos.md).</summary>
public sealed record ContentSummaryRow(
    int DraftArticles,
    int PublishedArticles,
    int ScheduledAnnouncements,
    int LiveAnnouncements,
    int DraftDownloads,
    int DownloadsWithoutFile,
    int OpenIncidents,
    int UpcomingMaintenances,
    int ConfirmedSubscribers,
    int PendingSubscribers);

public sealed class ContentSummary(BillingDbContext db, TimeProvider time)
{
    public async Task<ContentSummaryRow> Get(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var articles = db.Set<Article>().AsNoTracking();
        var announcements = db.Set<Announcement>().AsNoTracking();
        var downloads = db.Set<Download>().AsNoTracking();
        var events = db.Set<StatusEvent>().AsNoTracking();
        var subscribers = db.Set<StatusSubscriber>().AsNoTracking();
        return new ContentSummaryRow(
            await articles.CountAsync(a => a.Status == PublishState.Draft, ct).ConfigureAwait(false),
            await articles.CountAsync(a => a.Status == PublishState.Published, ct).ConfigureAwait(false),
            await announcements.CountAsync(a => a.Status == PublishState.Published && a.PublishAt > now, ct).ConfigureAwait(false),
            await announcements.CountAsync(a => a.Status == PublishState.Published && a.PublishAt <= now && (a.ExpiresAt == null || a.ExpiresAt > now), ct).ConfigureAwait(false),
            await downloads.CountAsync(d => d.Status == PublishState.Draft, ct).ConfigureAwait(false),
            await downloads.CountAsync(d => d.Status != PublishState.Archived && d.Size == null, ct).ConfigureAwait(false),
            await events.CountAsync(e => e.Kind == StatusEventKind.Incident && e.State != StatusEventState.Resolved, ct).ConfigureAwait(false),
            await events.CountAsync(e => e.Kind == StatusEventKind.Maintenance && e.State == StatusEventState.Scheduled && e.EndsAt > now, ct).ConfigureAwait(false),
            await subscribers.CountAsync(s => s.ConfirmedAt != null, ct).ConfigureAwait(false),
            await subscribers.CountAsync(s => s.ConfirmedAt == null, ct).ConfigureAwait(false));
    }
}
