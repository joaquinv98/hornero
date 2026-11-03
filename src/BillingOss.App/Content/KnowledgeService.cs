using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BillingOss.App.Content;

public sealed record ArticleTextInput(string Title, string? Summary, string Body, string? Tags = null);

/// <param name="Texts">Si viene, reemplaza todos los textos del artículo: los idiomas que faltan se sacan. Nulo los deja como están.</param>
public sealed record ArticleInput(
    long CategoryId,
    string? Slug,
    bool Featured,
    int SortOrder,
    ContentAudience Audience,
    long[] ProductIds,
    long[] GroupIds,
    IReadOnlyDictionary<string, ArticleTextInput>? Texts = null,
    int? Revision = null);

public sealed record ArticleFilter(long? CategoryId = null, PublishState? Status = null, string? Query = null, int Limit = 200);

/// <summary>Un artículo con todos sus textos, para el staff.</summary>
public sealed record ArticleDetail(Article Article, IReadOnlyList<ArticleText> Texts);

/// <summary>Una fila de la lista del staff, con el título en el idioma de base.</summary>
public sealed record ArticleRow(Article Article, string Title, IReadOnlyList<string> Languages);

/// <summary>Un artículo en una lista: lo que ve quien lo busca. <paramref name="Fallback"/> dice que no hay texto en el idioma pedido.</summary>
public sealed record ArticleCard(long Id, string Slug, string Title, string? Summary, long CategoryId, string Language, bool Fallback, bool Featured, long Helpful, long Unhelpful, DateTimeOffset UpdatedAt);

public sealed record CategoryCrumb(long Id, string Slug, string Name);

public sealed record CategoryNode(long Id, string Slug, string Name, string? Description, int Count, IReadOnlyList<CategoryNode> Children);

public sealed record CategoryPage(CategoryNode Category, IReadOnlyList<CategoryCrumb> Crumbs, IReadOnlyList<ArticleCard> Articles);

public sealed record ArticleView(ArticleCard Card, string Html, IReadOnlyList<string> Tags, IReadOnlyList<CategoryCrumb> Crumbs, IReadOnlyList<ArticleCard> Related);

public sealed record VoteResult(long Helpful, long Unhelpful, bool Changed);

public sealed class SearchHit
{
    public long Id { get; set; }

    public float Rank { get; set; }
}

/// <summary>
/// Base de conocimiento (sección 3 de diseno/contenidos.md): el trabajo del staff sobre los artículos y lo que ve el
/// cliente, con la audiencia aplicada en un solo lugar.
/// </summary>
public sealed partial class KnowledgeService(BillingDbContext db, TimeProvider time, AuditLog audit, IOptions<ContentOptions> options)
{
    /// <summary>Artículos publicados y visibles para quien mira, con alias x.</summary>
    private const string VisibleSql = "SELECT x.* FROM content.articles x WHERE x.status = 'Published' AND " + ContentViewer.Predicate;

    private const string Fold = "translate({0}, 'áéíóúüÁÉÍÓÚÜ', 'aeiouuAEIOUU')";

    private const string HitsTemplate = """
        SELECT hit.article_id AS "Id", max(hit.rank) AS "Rank"
        FROM (
          SELECT t.article_id, ts_rank_cd(t.search, q.query) AS rank
          FROM content.article_texts t, (SELECT {es} AS query) q
          WHERE t.language = 'es-AR' AND t.search @@ q.query
          UNION ALL
          SELECT t.article_id, ts_rank_cd(t.search, q.query) AS rank
          FROM content.article_texts t, (SELECT {en} AS query) q
          WHERE t.language = 'en' AND t.search @@ q.query) hit
        JOIN content.articles x ON x.id = hit.article_id
        WHERE x.status = 'Published' AND {visible}
        GROUP BY hit.article_id
        ORDER BY max(hit.rank) DESC, hit.article_id
        LIMIT @limit
        """;

    private static readonly string FoldedQuery = string.Format(System.Globalization.CultureInfo.InvariantCulture, Fold, "@q");

    private static readonly string SearchSql = HitsTemplate
        .Replace("{es}", $"websearch_to_tsquery('spanish', {FoldedQuery})", StringComparison.Ordinal)
        .Replace("{en}", $"websearch_to_tsquery('english', {FoldedQuery})", StringComparison.Ordinal)
        .Replace("{visible}", ContentViewer.Predicate, StringComparison.Ordinal);

    private static readonly string SuggestSql = HitsTemplate
        .Replace("{es}", "to_tsquery('spanish', @q)", StringComparison.Ordinal)
        .Replace("{en}", "to_tsquery('english', @q)", StringComparison.Ordinal)
        .Replace("{visible}", ContentViewer.Predicate, StringComparison.Ordinal);

    private IQueryable<Article> Visible(ContentViewer viewer) => db.Set<Article>().FromSqlRaw(VisibleSql, ContentDb.Params(viewer)).AsNoTracking();

    // ---- Staff ----

    public async Task<long> Create(ArticleInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Texts is not { Count: > 0 })
        {
            throw new ValidationException("Falta el texto en al menos un idioma.");
        }

        var texts = CheckTexts(input.Texts);
        await CheckFields(input, ct).ConfigureAwait(false);
        var slug = await ContentDb.FreeSlug(input.Slug, texts.ToDictionary(t => t.Key, t => new ContentText(t.Value.Title)), s => db.Set<Article>().AnyAsync(a => a.Slug == s, ct)).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var article = new Article
        {
            CategoryId = input.CategoryId,
            Slug = slug,
            Featured = input.Featured,
            SortOrder = input.SortOrder,
            Audience = input.Audience,
            ProductIds = [.. input.ProductIds.Distinct().Order()],
            GroupIds = [.. input.GroupIds.Distinct().Order()],
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(article);
        await ContentDb.Save(db, "el artículo", ct).ConfigureAwait(false);
        foreach (var (language, text) in texts)
        {
            db.Add(NewText(article.Id, language, text, now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("content.article.create", $"article:{article.Id}", new { slug, category = input.CategoryId }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return article.Id;
    }

    public async Task Update(long id, ArticleInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = input.Texts is null ? null : CheckTexts(input.Texts);
        await CheckFields(input, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var article = await db.Set<Article>().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el artículo {id}.");
        ContentDb.CheckRevision(input.Revision, article.Revision, "el artículo");
        if (input.Slug is { Length: > 0 } slug && slug != article.Slug)
        {
            article.Slug = await ContentDb.FreeSlug(slug, null, s => db.Set<Article>().AnyAsync(a => a.Slug == s, ct)).ConfigureAwait(false);
        }

        article.CategoryId = input.CategoryId;
        article.Featured = input.Featured;
        article.SortOrder = input.SortOrder;
        article.Audience = input.Audience;
        article.ProductIds = [.. input.ProductIds.Distinct().Order()];
        article.GroupIds = [.. input.GroupIds.Distinct().Order()];
        var now = time.GetUtcNow();
        if (texts is not null)
        {
            await ReplaceTexts(article.Id, texts, now, ct).ConfigureAwait(false);
        }

        Touch(article, now);
        await EnsurePublishable(article, ct).ConfigureAwait(false);
        await ContentDb.Save(db, "el artículo", ct).ConfigureAwait(false);
        await audit.Record("content.article.update", $"article:{id}", new { article.Slug }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetText(long id, string language, ArticleTextInput text, int? revision = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lang = ContentTexts.Canonical(language) ?? throw new ValidationException($"Idioma no soportado: {language}.");
        var clean = CheckTexts(new Dictionary<string, ArticleTextInput> { [lang] = text });
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var article = await db.Set<Article>().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el artículo {id}.");
        ContentDb.CheckRevision(revision, article.Revision, "el artículo");
        var now = time.GetUtcNow();
        await ReplaceTexts(id, clean, now, ct, keepOthers: true).ConfigureAwait(false);
        Touch(article, now);
        await EnsurePublishable(article, ct).ConfigureAwait(false);
        await ContentDb.Save(db, "el artículo", ct).ConfigureAwait(false);
        await audit.Record("content.article.text", $"article:{id}", new { language = lang }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveText(long id, string language, int? revision = null, CancellationToken ct = default)
    {
        var lang = ContentTexts.Canonical(language) ?? throw new ValidationException($"Idioma no soportado: {language}.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var article = await db.Set<Article>().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el artículo {id}.");
        ContentDb.CheckRevision(revision, article.Revision, "el artículo");
        if (await db.Set<ArticleText>().CountAsync(t => t.ArticleId == id && t.Language != lang, ct).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("El artículo se queda sin texto: tiene que quedar al menos un idioma.");
        }

        await db.Set<ArticleText>().Where(t => t.ArticleId == id && t.Language == lang).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        Touch(article, time.GetUtcNow());
        await EnsurePublishable(article, ct).ConfigureAwait(false);
        await ContentDb.Save(db, "el artículo", ct).ConfigureAwait(false);
        await audit.Record("content.article.text-remove", $"article:{id}", new { language = lang }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task Publish(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Published, "publish", revision, ct);

    public Task Unpublish(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Draft, "unpublish", revision, ct);

    public Task Archive(long id, int? revision = null, CancellationToken ct = default) => Move(id, PublishState.Archived, "archive", revision, ct);

    public async Task Delete(long id, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var slug = await db.Set<Article>().AsNoTracking().Where(a => a.Id == id).Select(a => a.Slug).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el artículo {id}.");
        await db.Set<Article>().Where(a => a.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await audit.Record("content.article.delete", $"article:{id}", new { slug }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ArticleDetail?> Get(long id, CancellationToken ct = default)
    {
        var article = await db.Set<Article>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
        return article is null ? null : new ArticleDetail(article, await Texts(id, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ArticleRow>> List(ArticleFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = db.Set<Article>().AsNoTracking();
        if (filter.CategoryId is { } category)
        {
            query = query.Where(a => a.CategoryId == category);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        if (filter.Query?.Trim() is { Length: > 0 } text)
        {
            var like = ContentDb.Like(text);
            var number = long.TryParse(text, out var n) ? n : -1;
            query = query.Where(a => a.Id == number || a.Slug.Contains(text.ToLower())
                || db.Set<ArticleText>().Any(t => t.ArticleId == a.Id && (EF.Functions.ILike(t.Title, like) || EF.Functions.ILike(t.Tags, like))));
        }

        var articles = await query.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Id).Take(Math.Clamp(filter.Limit, 1, 1000)).ToListAsync(ct).ConfigureAwait(false);
        var ids = articles.Select(a => a.Id).ToArray();
        var texts = (await db.Set<ArticleText>().AsNoTracking().Where(t => ids.Contains(t.ArticleId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(t => t.ArticleId);
        return [.. articles.Select(a => new ArticleRow(a, (texts[a.Id].FirstOrDefault(t => t.Language == ContentTexts.DefaultLanguage) ?? texts[a.Id].FirstOrDefault())?.Title ?? string.Empty, [.. texts[a.Id].Select(t => t.Language).OrderBy(l => ContentTexts.Languages.ToList().IndexOf(l))]))];
    }

    // ---- Quien lee ----

    /// <summary>Las categorías con artículos visibles, anidadas y con la cantidad que tiene cada una (la suya y la de sus subcategorías).</summary>
    public async Task<IReadOnlyList<CategoryNode>> Tree(ContentViewer viewer, string? language, CancellationToken ct = default)
    {
        var categories = await db.Set<ContentCategory>().AsNoTracking().Where(c => c.Kind == CategoryKind.KnowledgeBase && !c.Hidden).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var counts = await Visible(viewer).GroupBy(a => a.CategoryId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        return Nest(categories, null, language, counts);
    }

    private static List<CategoryNode> Nest(List<ContentCategory> all, long? parent, string? language, Dictionary<long, int> counts)
    {
        var nodes = new List<CategoryNode>();
        foreach (var category in all.Where(c => c.ParentId == parent))
        {
            var children = Nest(all, category.Id, language, counts);
            var total = counts.GetValueOrDefault(category.Id) + children.Sum(c => c.Count);
            if (total == 0)
            {
                continue;
            }

            var text = ContentTexts.Pick(ContentTexts.Parse(category.Texts), language);
            nodes.Add(new CategoryNode(category.Id, category.Slug, text?.Title ?? category.Slug, text?.Summary, total, children));
        }

        return nodes;
    }

    /// <summary>
    /// Una categoría con sus artículos y las migas de pan. La clave es su slug o su número de origen, como en los artículos: una
    /// dirección nunca es solo un número, así que no se confunden.
    /// </summary>
    public async Task<CategoryPage?> Category(ContentViewer viewer, string slug, string? language, CancellationToken ct = default)
    {
        var tree = await Tree(viewer, language, ct).ConfigureAwait(false);
        long? id = long.TryParse(slug, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
        var path = PathTo(tree, slug, id);
        if (path is null)
        {
            return null;
        }

        var node = path[^1];
        var articles = await Cards(viewer, language, ct, q => q.Where(a => a.CategoryId == node.Id)).ConfigureAwait(false);
        return new CategoryPage(node, [.. path.Select(n => new CategoryCrumb(n.Id, n.Slug, n.Name))], articles);
    }

    private static List<CategoryNode>? PathTo(IReadOnlyList<CategoryNode> nodes, string slug, long? id)
    {
        foreach (var node in nodes)
        {
            if (node.Slug == slug || node.Id == id)
            {
                return [node];
            }

            if (PathTo(node.Children, slug, id) is { } below)
            {
                below.Insert(0, node);
                return below;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ArticleCard>> Featured(ContentViewer viewer, string? language, int limit = 6, CancellationToken ct = default) =>
        await Cards(viewer, language, ct, q => q.Where(a => a.Featured).Take(limit)).ConfigureAwait(false);

    public async Task<IReadOnlyList<ArticleCard>> Latest(ContentViewer viewer, string? language, int limit = 6, CancellationToken ct = default) =>
        await Cards(viewer, language, ct, q => q.OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.Id).Take(limit), ordered: true).ConfigureAwait(false);

    /// <summary>
    /// El artículo publicado y visible con esa dirección corta o, si es un número, con ese ID (los enlaces que conservan el ID
    /// de origen). Nulo si no existe o no corresponde. Una dirección nunca es solo un número, así que no se confunden.
    /// </summary>
    public async Task<(long Id, string Slug)?> Find(ContentViewer viewer, string? key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > Slugs.MaxLength)
        {
            return null;
        }

        var visible = Visible(viewer);
        var found = long.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? await visible.Where(a => a.Id == id).Select(a => new { a.Id, a.Slug }).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : await visible.Where(a => a.Slug == key).Select(a => new { a.Id, a.Slug }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return found is null ? null : (found.Id, found.Slug);
    }

    /// <summary>
    /// Lee un artículo publicado y visible, con su cuerpo ya convertido a HTML, y suma una lectura. Nulo si no existe o no
    /// corresponde: igual que lo ajeno, da 404.
    /// </summary>
    public async Task<ArticleView?> Read(ContentViewer viewer, string slug, string? language, CancellationToken ct = default)
    {
        var article = await Visible(viewer).SingleOrDefaultAsync(a => a.Slug == slug, ct).ConfigureAwait(false);
        if (article is null)
        {
            return null;
        }

        var texts = await Texts(article.Id, ct).ConfigureAwait(false);
        var picked = ContentTexts.PickWithLanguage(Dictionary(texts), language);
        if (picked is null)
        {
            return null;
        }

        var (text, shown) = picked.Value;
        var tree = await Tree(viewer, language, ct).ConfigureAwait(false);
        var crumbs = PathToCategory(tree, article.CategoryId);
        var related = await Cards(viewer, language, ct, q => q.Where(a => a.CategoryId == article.CategoryId && a.Id != article.Id).Take(5)).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE content.articles SET views = views + 1 WHERE id = {article.Id}", ct).ConfigureAwait(false);
        var tags = texts.FirstOrDefault(t => t.Language == shown)?.TagList ?? [];
        return new ArticleView(Card(article, shown, text, language), Markdown.ToHtml(text.Body, options.Value.Rendering), tags, crumbs, related);
    }

    private static List<CategoryCrumb> PathToCategory(IReadOnlyList<CategoryNode> tree, long id)
    {
        foreach (var node in tree)
        {
            if (node.Id == id)
            {
                return [new(node.Id, node.Slug, node.Name)];
            }

            var below = PathToCategory(node.Children, id);
            if (below.Count > 0)
            {
                below.Insert(0, new(node.Id, node.Slug, node.Name));
                return below;
            }
        }

        return [];
    }

    /// <summary>Busca con la sintaxis de búsqueda web: frases entre comillas, "-" para excluir y "or". Los más relevantes primero.</summary>
    public async Task<IReadOnlyList<ArticleCard>> Search(ContentViewer viewer, string? query, string? language, int limit = 20, CancellationToken ct = default)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var parameters = new List<NpgsqlParameter>(ContentDb.Params(viewer)) { new("q", text.Length > 300 ? text[..300] : text), new("limit", Math.Clamp(limit, 1, 100)) };
        return await Ranked(viewer, SearchSql, parameters, language, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Artículos que pueden servirle a quien está abriendo un ticket con ese asunto y ese texto (sección 3.5 de
    /// diseno/contenidos.md). Busca con "o" entre las palabras con contenido, hasta doce, y no hace falta que aparezcan todas.
    /// </summary>
    public async Task<IReadOnlyList<ArticleCard>> Suggest(ContentViewer viewer, string? text, string? language, int limit = 5, CancellationToken ct = default)
    {
        var terms = Terms(text);
        if (terms.Count == 0)
        {
            return [];
        }

        var parameters = new List<NpgsqlParameter>(ContentDb.Params(viewer)) { new("q", string.Join(" | ", terms)), new("limit", Math.Clamp(limit, 1, 20)) };
        return await Ranked(viewer, SuggestSql, parameters, language, ct).ConfigureAwait(false);
    }

    /// <summary>Las palabras de un texto libre que valen para buscar: sin tildes, de tres letras o más, las más largas primero.</summary>
    public static IReadOnlyList<string> Terms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var folded = Fold2(text.Length > 2000 ? text[..2000] : text).ToLowerInvariant();
        return [.. Word().Matches(folded).Select(m => m.Value).Distinct().OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).Take(12)];
    }

    private static string Fold2(string text) => text.Normalize(System.Text.NormalizationForm.FormC)
        .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u').Replace('ü', 'u')
        .Replace('Á', 'A').Replace('É', 'E').Replace('Í', 'I').Replace('Ó', 'O').Replace('Ú', 'U').Replace('Ü', 'U');

    [GeneratedRegex(@"[\p{L}\p{N}]{3,}")]
    private static partial Regex Word();

    private async Task<IReadOnlyList<ArticleCard>> Ranked(ContentViewer viewer, string sql, List<NpgsqlParameter> parameters, string? language, CancellationToken ct)
    {
        var hits = await db.Database.SqlQueryRaw<SearchHit>(sql, [.. parameters]).ToListAsync(ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return [];
        }

        var ids = hits.Select(h => h.Id).ToArray();
        var articles = await db.Set<Article>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);
        var texts = (await db.Set<ArticleText>().AsNoTracking().Where(t => ids.Contains(t.ArticleId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(t => t.ArticleId);
        return [.. hits.Where(h => articles.ContainsKey(h.Id)).Select(h => ToCard(articles[h.Id], texts[h.Id], language))];
    }

    /// <summary>Si alguien le sirvió el artículo. Un voto por votante: repetirlo no suma y cambiarlo mueve el conteo. Nulo si no corresponde.</summary>
    public async Task<VoteResult?> Vote(ContentViewer viewer, long articleId, string voter, bool helpful, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voter);
        if (!await Visible(viewer).AnyAsync(a => a.Id == articleId, ct).ConfigureAwait(false))
        {
            return null;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "content.articles", [articleId], ct).ConfigureAwait(false);
        var previous = await db.Set<ArticleVote>().SingleOrDefaultAsync(v => v.ArticleId == articleId && v.Voter == voter, ct).ConfigureAwait(false);
        var changed = previous is null || previous.Helpful != helpful;
        if (changed)
        {
            var up = (helpful ? 1 : 0) - (previous is { Helpful: true } ? 1 : 0);
            var down = (helpful ? 0 : 1) - (previous is { Helpful: false } ? 1 : 0);
            if (previous is null)
            {
                db.Add(new ArticleVote { ArticleId = articleId, Voter = voter, Helpful = helpful, VotedAt = time.GetUtcNow() });
            }
            else
            {
                previous.Helpful = helpful;
                previous.VotedAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE content.articles SET helpful = helpful + {up}, unhelpful = unhelpful + {down} WHERE id = {articleId}", ct).ConfigureAwait(false);
        }

        var counts = await db.Set<Article>().AsNoTracking().Where(a => a.Id == articleId).Select(a => new { a.Helpful, a.Unhelpful }).SingleAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new VoteResult(counts.Helpful, counts.Unhelpful, changed);
    }

    // ---- Internos ----

    private async Task<List<ArticleText>> Texts(long articleId, CancellationToken ct) =>
        await db.Set<ArticleText>().AsNoTracking().Where(t => t.ArticleId == articleId).OrderBy(t => t.Language).ToListAsync(ct).ConfigureAwait(false);

    private static Dictionary<string, ContentText> Dictionary(IEnumerable<ArticleText> texts) =>
        texts.ToDictionary(t => t.Language, t => new ContentText(t.Title, t.Summary, t.Body));

    private async Task<IReadOnlyList<ArticleCard>> Cards(ContentViewer viewer, string? language, CancellationToken ct, Func<IQueryable<Article>, IQueryable<Article>> shape, bool ordered = false)
    {
        var query = Visible(viewer);
        query = shape(ordered ? query : query.OrderBy(a => a.SortOrder).ThenBy(a => a.Id));
        var articles = await query.ToListAsync(ct).ConfigureAwait(false);
        var ids = articles.Select(a => a.Id).ToArray();
        var texts = (await db.Set<ArticleText>().AsNoTracking().Where(t => ids.Contains(t.ArticleId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(t => t.ArticleId);
        return [.. articles.Select(a => ToCard(a, texts[a.Id], language))];
    }

    private static ArticleCard ToCard(Article article, IEnumerable<ArticleText> texts, string? language)
    {
        var picked = ContentTexts.PickWithLanguage(Dictionary(texts), language);
        return picked is { } p ? Card(article, p.Language, p.Text, language) : new ArticleCard(article.Id, article.Slug, article.Slug, null, article.CategoryId, ContentTexts.DefaultLanguage, true, article.Featured, article.Helpful, article.Unhelpful, article.UpdatedAt);
    }

    private static ArticleCard Card(Article article, string shown, ContentText text, string? requested) => new(
        article.Id,
        article.Slug,
        text.Title,
        text.Summary ?? Markdown.ToPlainText(text.Body, 160),
        article.CategoryId,
        shown,
        shown != ContentTexts.Reader(requested),
        article.Featured,
        article.Helpful,
        article.Unhelpful,
        article.UpdatedAt);

    private static Dictionary<string, ArticleTextInput> CheckTexts(IReadOnlyDictionary<string, ArticleTextInput> input)
    {
        var clean = new Dictionary<string, ArticleTextInput>();
        foreach (var (language, text) in input)
        {
            var lang = ContentTexts.Canonical(language) ?? throw new ValidationException($"Idioma no soportado: {language}. Los válidos son {string.Join(", ", ContentTexts.Languages)}.");
            ArgumentNullException.ThrowIfNull(text);
            clean[lang] = new ArticleTextInput(text.Title?.Trim() ?? string.Empty, string.IsNullOrWhiteSpace(text.Summary) ? null : text.Summary.Trim(), text.Body?.Trim() ?? string.Empty, Tags(text.Tags));
        }

        var check = clean.ToDictionary(t => t.Key, t => new ContentText(t.Value.Title, t.Value.Summary, t.Value.Body));
        if (ContentTexts.Error(check) is { } error)
        {
            throw new ValidationException(error);
        }

        return clean;
    }

    /// <summary>Hasta 20 etiquetas de 40 caracteres, sin repetir, separadas por comas.</summary>
    private static string Tags(string? tags) => string.Join(", ", (tags ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => t.Length <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(20));

    private async Task CheckFields(ArticleInput input, CancellationToken ct)
    {
        var kind = await db.Set<ContentCategory>().AsNoTracking().Where(c => c.Id == input.CategoryId).Select(c => (CategoryKind?)c.Kind).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (kind != CategoryKind.KnowledgeBase)
        {
            throw new ValidationException($"No existe la categoría {input.CategoryId} de la base de conocimiento.");
        }

        await ContentDb.CheckAudience(db, input.Audience, input.ProductIds ?? [], input.GroupIds ?? [], ct).ConfigureAwait(false);
    }

    private static ArticleText NewText(long articleId, string language, ArticleTextInput text, DateTimeOffset now) => new()
    {
        ArticleId = articleId,
        Language = language,
        Title = text.Title,
        Summary = text.Summary,
        Body = text.Body,
        Tags = text.Tags ?? string.Empty,
        UpdatedAt = now,
    };

    private async Task ReplaceTexts(long id, Dictionary<string, ArticleTextInput> texts, DateTimeOffset now, CancellationToken ct, bool keepOthers = false)
    {
        var existing = await db.Set<ArticleText>().Where(t => t.ArticleId == id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var old in existing.Where(t => !texts.ContainsKey(t.Language)))
        {
            if (!keepOthers)
            {
                db.Remove(old);
            }
        }

        foreach (var (language, text) in texts)
        {
            if (existing.Find(t => t.Language == language) is { } found)
            {
                found.Title = text.Title;
                found.Summary = text.Summary;
                found.Body = text.Body;
                found.Tags = text.Tags ?? string.Empty;
                found.UpdatedAt = now;
            }
            else
            {
                db.Add(NewText(id, language, text, now));
            }
        }
    }

    private static void Touch(Article article, DateTimeOffset now)
    {
        article.Revision++;
        article.UpdatedAt = now;
    }

    /// <summary>Un artículo publicado necesita al menos un texto con título y cuerpo.</summary>
    private async Task EnsurePublishable(Article article, CancellationToken ct)
    {
        if (article.Status != PublishState.Published)
        {
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var ready = await db.Set<ArticleText>().AnyAsync(t => t.ArticleId == article.Id && t.Title != string.Empty && t.Body != string.Empty, ct).ConfigureAwait(false);
        if (!ready)
        {
            throw new ValidationException("Un artículo publicado necesita al menos un texto con título y cuerpo.");
        }
    }

    private async Task Move(long id, PublishState state, string action, int? revision, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var article = await db.Set<Article>().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe el artículo {id}.");
        ContentDb.CheckRevision(revision, article.Revision, "el artículo");
        var now = time.GetUtcNow();
        article.SetStatus(state, now);
        Touch(article, now);
        await EnsurePublishable(article, ct).ConfigureAwait(false);
        await ContentDb.Save(db, "el artículo", ct).ConfigureAwait(false);
        await audit.Record($"content.article.{action}", $"article:{id}", new { article.Slug }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
