using System.Globalization;
using System.Text;
using System.Xml.Linq;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BillingOss.App.Content;

public sealed record AnnouncementInput(
    ContentAudience Audience,
    long[] ProductIds,
    long[] GroupIds,
    bool Pinned,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string, ContentText> Texts,
    int? Revision = null);

/// <summary>Una novedad con sus textos ya leídos, para el staff.</summary>
public sealed record AnnouncementRow(Announcement Announcement, IReadOnlyDictionary<string, ContentText> Texts, AnnouncementPhase Phase);

/// <summary>Una novedad como la ve el cliente: con el cuerpo ya convertido a HTML.</summary>
public sealed record AnnouncementView(long Id, string Title, string Html, string Summary, string Language, bool Fallback, bool Pinned, DateTimeOffset PublishedAt);

/// <summary>Novedades (sección 4 de diseno/contenidos.md): borrador, publicación programada, fijadas, vencimiento y RSS.</summary>
public sealed class AnnouncementService(BillingDbContext db, TimeProvider time, AuditLog audit, IOptions<ContentOptions> options)
{
    /// <summary>Publicadas y vigentes ahora para quien mira, con alias x.</summary>
    private const string LiveSql = "SELECT x.* FROM content.announcements x WHERE x.status = 'Published' AND x.publish_at <= @now AND (x.expires_at IS NULL OR x.expires_at > @now) AND " + ContentViewer.Predicate;

    private IQueryable<Announcement> Live(ContentViewer viewer) =>
        db.Set<Announcement>().FromSqlRaw(LiveSql, [.. ContentDb.Params(viewer), new NpgsqlParameter("now", time.GetUtcNow())]).AsNoTracking();

    // ---- Staff ----

    public async Task<long> Create(AnnouncementInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = await Check(input, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var announcement = new Announcement { CreatedAt = now, UpdatedAt = now };
        Fill(announcement, input, texts);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(announcement);
        await ContentDb.Save(db, "la novedad", ct).ConfigureAwait(false);
        await audit.Record("content.announcement.create", $"announcement:{announcement.Id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return announcement.Id;
    }

    public async Task Update(long id, AnnouncementInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = await Check(input, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var announcement = await db.Set<Announcement>().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la novedad {id}.");
        ContentDb.CheckRevision(input.Revision, announcement.Revision, "la novedad");
        Fill(announcement, input, texts);
        if (announcement.DateError() is { } error)
        {
            throw new ValidationException(error);
        }

        Touch(announcement);
        await ContentDb.Save(db, "la novedad", ct).ConfigureAwait(false);
        await audit.Record("content.announcement.update", $"announcement:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Publica ya, o programa si <paramref name="at"/> es futura. Pide título y cuerpo en al menos un idioma.</summary>
    public async Task Publish(long id, DateTimeOffset? at = null, int? revision = null, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var announcement = await db.Set<Announcement>().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la novedad {id}.");
        ContentDb.CheckRevision(revision, announcement.Revision, "la novedad");
        if (ContentTexts.Error(ContentTexts.Parse(announcement.Texts), bodyRequired: true) is { } missing)
        {
            throw new ValidationException(missing);
        }

        announcement.Status = PublishState.Published;
        announcement.PublishAt = (at ?? time.GetUtcNow()).ToUniversalTime();
        if (announcement.DateError() is { } error)
        {
            throw new ValidationException(error);
        }

        Touch(announcement);
        await ContentDb.Save(db, "la novedad", ct).ConfigureAwait(false);
        await audit.Record("content.announcement.publish", $"announcement:{id}", new { announcement.PublishAt }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task Unpublish(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Draft, "unpublish", revision, ct);

    public Task Archive(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Archived, "archive", revision, ct);

    public async Task Delete(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (await db.Set<Announcement>().Where(a => a.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"No existe la novedad {id}.");
        }

        await audit.Record("content.announcement.delete", $"announcement:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<AnnouncementRow?> Get(long id, CancellationToken ct = default)
    {
        var announcement = await db.Set<Announcement>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
        return announcement is null ? null : Row(announcement);
    }

    public async Task<IReadOnlyList<AnnouncementRow>> List(PublishState? status = null, int limit = 200, CancellationToken ct = default)
    {
        var query = db.Set<Announcement>().AsNoTracking();
        if (status is { } state)
        {
            query = query.Where(a => a.Status == state);
        }

        var rows = await query.OrderByDescending(a => a.Pinned).ThenByDescending(a => a.PublishAt ?? a.CreatedAt).ThenByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(Row)];
    }

    // ---- Quien lee ----

    /// <summary>Las vigentes para quien mira: las fijadas arriba y después las más nuevas.</summary>
    public async Task<IReadOnlyList<AnnouncementView>> Visible(ContentViewer viewer, string? language, int limit = 50, CancellationToken ct = default)
    {
        var rows = await Live(viewer).OrderByDescending(a => a.Pinned).ThenByDescending(a => a.PublishAt).ThenByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(a => View(a, language)).OfType<AnnouncementView>()];
    }

    /// <summary>Una novedad vigente y visible, o nulo: lo que no corresponde da 404.</summary>
    public async Task<AnnouncementView?> Read(ContentViewer viewer, long id, string? language, CancellationToken ct = default)
    {
        var announcement = await Live(viewer).SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
        return announcement is null ? null : View(announcement, language);
    }

    /// <summary>
    /// El canal RSS 2.0 con las novedades públicas vigentes. Es anónimo, así que solo lleva las de audiencia pública. Los
    /// enlaces salen de <paramref name="baseUrl"/>.
    /// </summary>
    public async Task<string> Rss(string? language, string siteTitle, Uri baseUrl, int limit = 30, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var culture = ContentTexts.Reader(language);
        var items = await Visible(ContentViewer.Anonymous, culture, limit, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var self = new Uri(baseUrl, culture == "en" ? "/anuncios.rss?lang=en" : "/anuncios.rss");
        var channel = new XElement(
            "channel",
            new XElement("title", $"{siteTitle}: {(culture == "en" ? "announcements" : "anuncios")}"),
            new XElement("link", new Uri(baseUrl, "/anuncios").AbsoluteUri),
            new XElement("description", culture == "en" ? "Announcements and notices" : "Anuncios y avisos"),
            new XElement("language", culture),
            new XElement("lastBuildDate", Rfc822(items.Count == 0 ? now : items.Max(i => i.PublishedAt))),
            new XElement(atom + "link", new XAttribute("href", self.AbsoluteUri), new XAttribute("rel", "self"), new XAttribute("type", "application/rss+xml")));
        foreach (var item in items)
        {
            var link = new Uri(baseUrl, $"/anuncios/{item.Id.ToString(CultureInfo.InvariantCulture)}").AbsoluteUri;
            channel.Add(new XElement(
                "item",
                new XElement("title", item.Title),
                new XElement("link", link),
                new XElement("guid", new XAttribute("isPermaLink", "true"), link),
                new XElement("pubDate", Rfc822(item.PublishedAt)),
                new XElement("description", item.Html)));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "atom", atom), channel));
        return document.Declaration + Environment.NewLine + document.Root;
    }

    private static string Rfc822(DateTimeOffset at) => at.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    // ---- Internos ----

    private AnnouncementRow Row(Announcement a) => new(a, ContentTexts.Parse(a.Texts), a.Phase(time.GetUtcNow()));

    private AnnouncementView? View(Announcement a, string? language)
    {
        var picked = ContentTexts.PickWithLanguage(ContentTexts.Parse(a.Texts), language);
        if (picked is not { } found)
        {
            return null;
        }

        var body = found.Text.Body ?? string.Empty;
        return new AnnouncementView(
            a.Id,
            found.Text.Title,
            Markdown.ToHtml(body, options.Value.Rendering),
            found.Text.Summary ?? Markdown.ToPlainText(body, 200),
            found.Language,
            found.Language != ContentTexts.Reader(language),
            a.Pinned,
            a.PublishAt ?? a.CreatedAt);
    }

    private async Task<Dictionary<string, ContentText>> Check(AnnouncementInput input, CancellationToken ct)
    {
        var texts = ContentTexts.Clean(input.Texts ?? new Dictionary<string, ContentText>());
        if (ContentTexts.Error(texts) is { } error)
        {
            throw new ValidationException(error);
        }

        await ContentDb.CheckAudience(db, input.Audience, input.ProductIds ?? [], input.GroupIds ?? [], ct).ConfigureAwait(false);
        return texts;
    }

    private static void Fill(Announcement announcement, AnnouncementInput input, Dictionary<string, ContentText> texts)
    {
        announcement.Audience = input.Audience;
        announcement.ProductIds = [.. input.ProductIds.Distinct().Order()];
        announcement.GroupIds = [.. input.GroupIds.Distinct().Order()];
        announcement.Pinned = input.Pinned;
        announcement.ExpiresAt = input.ExpiresAt?.ToUniversalTime();
        announcement.Texts = ContentTexts.Serialize(texts);
    }

    private void Touch(Announcement announcement)
    {
        announcement.Revision++;
        announcement.UpdatedAt = time.GetUtcNow();
    }

    private async Task Move(long id, PublishState state, string action, int? revision, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var announcement = await db.Set<Announcement>().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la novedad {id}.");
        ContentDb.CheckRevision(revision, announcement.Revision, "la novedad");
        announcement.Status = state;
        Touch(announcement);
        await ContentDb.Save(db, "la novedad", ct).ConfigureAwait(false);
        await audit.Record($"content.announcement.{action}", $"announcement:{id}", null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
