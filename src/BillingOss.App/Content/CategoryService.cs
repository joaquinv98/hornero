using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Content;

public sealed record CategoryInput(long? ParentId, string? Slug, int SortOrder, bool Hidden, IReadOnlyDictionary<string, ContentText> Texts, int? Revision = null);

/// <summary>Una categoría con sus textos ya leídos y la cantidad de artículos o descargas que tiene.</summary>
public sealed record CategoryRow(ContentCategory Category, IReadOnlyDictionary<string, ContentText> Texts, int Items);

/// <summary>Categorías de la base de conocimiento y de las descargas, con las mismas reglas (sección 3.1 de diseno/contenidos.md).</summary>
public sealed class CategoryService(BillingDbContext db, TimeProvider time, AuditLog audit)
{
    public async Task<IReadOnlyList<CategoryRow>> List(CategoryKind kind, CancellationToken ct = default)
    {
        var categories = await db.Set<ContentCategory>().AsNoTracking().Where(c => c.Kind == kind).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var counts = kind == CategoryKind.KnowledgeBase
            ? await db.Set<Article>().AsNoTracking().GroupBy(a => a.CategoryId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false)
            : await db.Set<Download>().AsNoTracking().GroupBy(d => d.CategoryId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        return [.. categories.Select(c => new CategoryRow(c, ContentTexts.Parse(c.Texts), counts.GetValueOrDefault(c.Id)))];
    }

    public async Task<CategoryRow?> Get(CategoryKind kind, long id, CancellationToken ct = default) =>
        (await List(kind, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Category.Id == id);

    public async Task<long> Create(CategoryKind kind, CategoryInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = Check(input);
        var all = await Load(kind, ct).ConfigureAwait(false);
        CheckParent(all, null, input.ParentId);
        var slug = await ContentDb.FreeSlug(input.Slug, texts, s => db.Set<ContentCategory>().AnyAsync(c => c.Kind == kind && c.Slug == s, ct)).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var category = new ContentCategory(kind, slug, now) { ParentId = input.ParentId, SortOrder = input.SortOrder, Hidden = input.Hidden, Texts = ContentTexts.Serialize(texts) };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(category);
        await ContentDb.Save(db, "la categoría", ct).ConfigureAwait(false);
        await audit.Record("content.category.create", $"category:{category.Id}", new { kind = kind.ToString(), slug }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return category.Id;
    }

    public async Task Update(CategoryKind kind, long id, CategoryInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var texts = Check(input);
        var all = await Load(kind, ct).ConfigureAwait(false);
        var category = await db.Set<ContentCategory>().SingleOrDefaultAsync(c => c.Id == id && c.Kind == kind, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la categoría {id}.");
        ContentDb.CheckRevision(input.Revision, category.Revision, "la categoría");
        CheckParent(all, id, input.ParentId);
        if (input.Slug is { Length: > 0 } slug && slug != category.Slug)
        {
            category.Slug = await ContentDb.FreeSlug(slug, null, s => db.Set<ContentCategory>().AnyAsync(c => c.Kind == kind && c.Slug == s, ct)).ConfigureAwait(false);
        }

        category.ParentId = input.ParentId;
        category.SortOrder = input.SortOrder;
        category.Hidden = input.Hidden;
        category.Texts = ContentTexts.Serialize(texts);
        category.Revision++;
        category.UpdatedAt = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await ContentDb.Save(db, "la categoría", ct).ConfigureAwait(false);
        await audit.Record("content.category.update", $"category:{id}", new { kind = kind.ToString() }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Una categoría con subcategorías, artículos o descargas no se borra: primero se mueve o se borra lo que tiene.</summary>
    public async Task Delete(CategoryKind kind, long id, CancellationToken ct = default)
    {
        var category = await db.Set<ContentCategory>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.Kind == kind, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la categoría {id}.");
        var used = await db.Set<ContentCategory>().AnyAsync(c => c.ParentId == id, ct).ConfigureAwait(false)
            || (kind == CategoryKind.KnowledgeBase ? await db.Set<Article>().AnyAsync(a => a.CategoryId == id, ct).ConfigureAwait(false) : await db.Set<Download>().AnyAsync(d => d.CategoryId == id, ct).ConfigureAwait(false));
        if (used)
        {
            throw new InvalidOperationException("La categoría tiene subcategorías o contenido: primero hay que moverlos o borrarlos.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Set<ContentCategory>().Where(c => c.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await audit.Record("content.category.delete", $"category:{id}", new { kind = kind.ToString(), category.Slug }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static Dictionary<string, ContentText> Check(CategoryInput input)
    {
        var texts = ContentTexts.Clean(input.Texts ?? new Dictionary<string, ContentText>());
        if (ContentTexts.Error(texts, titleMax: 100, summaryMax: 500) is { } error)
        {
            throw new ValidationException(error);
        }

        return texts;
    }

    private async Task<Dictionary<long, long?>> Load(CategoryKind kind, CancellationToken ct) =>
        await db.Set<ContentCategory>().AsNoTracking().Where(c => c.Kind == kind).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct).ConfigureAwait(false);

    /// <summary>La madre existe, no es la propia categoría ni una de sus descendientes, y el árbol no pasa de tres niveles.</summary>
    private static void CheckParent(Dictionary<long, long?> parents, long? id, long? parentId)
    {
        if (parentId is not { } parent)
        {
            return;
        }

        if (!parents.ContainsKey(parent))
        {
            throw new ValidationException($"No existe la categoría madre {parent}.");
        }

        var depth = 1;
        for (long? up = parent; up is { } current; up = parents.GetValueOrDefault(current))
        {
            if (current == id)
            {
                throw new ValidationException("Una categoría no puede colgar de sí misma ni de una de sus subcategorías.");
            }

            depth++;
        }

        var below = id is { } own ? Height(parents, own) : 0;
        if (depth + below > ContentCategory.MaxDepth)
        {
            throw new ValidationException($"Las categorías llegan a {ContentCategory.MaxDepth} niveles.");
        }
    }

    /// <summary>Cuántos niveles de subcategorías tiene debajo.</summary>
    private static int Height(Dictionary<long, long?> parents, long id)
    {
        var children = parents.Where(p => p.Value == id).Select(p => p.Key).ToList();
        return children.Count == 0 ? 0 : 1 + children.Max(c => Height(parents, c));
    }
}
