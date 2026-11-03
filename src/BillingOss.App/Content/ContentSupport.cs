using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Content;
using BillingOss.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.Content;

/// <summary>Ajustes de los contenidos (sección Content, diseno/contenidos.md sección 12).</summary>
public sealed class ContentOptions
{
    /// <summary>Tope del archivo de una descarga, 50 MiB por defecto.</summary>
    public long MaxDownloadBytes { get; set; } = DownloadRules.DefaultMaxBytes;

    /// <summary>Extensiones permitidas, sin punto. Vacía vale la lista de fábrica: una lista configurada la reemplaza.</summary>
    public IList<string> AllowedDownloadExtensions { get; set; } = [];

    /// <summary>Hosts desde los que un artículo puede mostrar imágenes, por https.</summary>
    public IList<string> ImageHosts { get; set; } = [];

    public IReadOnlyCollection<string> Extensions => AllowedDownloadExtensions.Count == 0
        ? DownloadRules.DefaultExtensions
        : [.. AllowedDownloadExtensions.Select(e => e.Trim().TrimStart('.').ToLowerInvariant())];

    public MarkdownOptions Rendering => new([.. ImageHosts]);
}

/// <summary>Quién mira: una visita sin sesión o un cliente con los productos de sus servicios vivos y los grupos de sus cuentas.</summary>
public sealed class ContentViewers(BillingDbContext db)
{
    public static ContentViewer Anonymous => ContentViewer.Anonymous;

    /// <summary>
    /// Un cliente con sesión que opera esas cuentas. Cuentan los servicios que no están terminados ni cancelados y el grupo
    /// de cada cuenta. Sin cuentas sigue siendo un cliente, pero sin productos ni grupos.
    /// </summary>
    public async Task<ContentViewer> ForAccounts(IEnumerable<long> accountIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var accounts = accountIds.Distinct().ToArray();
        if (accounts.Length == 0)
        {
            return new ContentViewer(true, [], []);
        }

        var products = await db.Services.AsNoTracking()
            .Where(s => accounts.Contains(s.AccountId) && s.Status != ServiceStatus.Terminated && s.Status != ServiceStatus.Cancelled)
            .Select(s => s.ProductId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var groups = await db.Accounts.AsNoTracking()
            .Where(a => accounts.Contains(a.Id) && a.GroupId != null)
            .Select(a => a.GroupId!.Value).Distinct().ToListAsync(ct).ConfigureAwait(false);
        return new ContentViewer(true, products, groups);
    }
}

/// <summary>Piezas que comparten los servicios de contenidos.</summary>
internal static class ContentDb
{
    /// <summary>Los parámetros de <see cref="ContentViewer.Predicate"/>.</summary>
    public static NpgsqlParameter[] Params(ContentViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return [new("customer", viewer.IsCustomer), new("products", viewer.ProductIds), new("groups", viewer.GroupIds)];
    }

    /// <summary>Guarda y traduce los choques de la base a errores que el staff entiende: 409 con un texto claro.</summary>
    public static async Task Save(BillingDbContext db, string subject, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException($"Otra persona modificó {subject} al mismo tiempo. Recargá y volvé a intentar.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException($"Ya existe {subject} con esa dirección o esos datos.");
        }
    }

    /// <summary>Guardar con una revisión atrasada falla: alguien cambió el contenido desde que se abrió el formulario.</summary>
    public static void CheckRevision(int? expected, int actual, string subject)
    {
        if (expected is { } revision && revision != actual)
        {
            throw new InvalidOperationException($"Otra persona modificó {subject}. Recargá y volvé a intentar.");
        }
    }

    /// <summary>Los productos y grupos de una audiencia dirigida existen. Son arreglos sin clave foránea: la base no puede comprobarlo.</summary>
    public static async Task CheckAudience(BillingDbContext db, ContentAudience audience, long[] products, long[] groups, CancellationToken ct)
    {
        if (ContentViewer.Error(audience, products, groups) is { } error)
        {
            throw new ValidationException(error);
        }

        var productIds = products.Distinct().ToArray();
        if (productIds.Length > 0)
        {
            var found = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct).ConfigureAwait(false);
            if (productIds.Except(found).FirstOrDefault() is var missing and not 0)
            {
                throw new ValidationException($"No existe el producto {missing}.");
            }
        }

        var groupIds = groups.Distinct().ToArray();
        if (groupIds.Length > 0)
        {
            var found = await db.Set<AccountGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct).ConfigureAwait(false);
            if (groupIds.Except(found).FirstOrDefault() is var missing and not 0)
            {
                throw new ValidationException($"No existe el grupo de clientes {missing}.");
            }
        }
    }

    /// <summary>El slug pedido, o uno armado con el título que no choque con otro: "ayuda", "ayuda-2", "ayuda-3".</summary>
    public static async Task<string> FreeSlug(string? requested, IReadOnlyDictionary<string, ContentText>? texts, Func<string, Task<bool>> exists)
    {
        if (requested is { Length: > 0 })
        {
            if (!Slugs.IsValid(requested))
            {
                throw new ValidationException("La dirección corta va en minúscula, con letras, números y guiones, y no puede ser solo un número.");
            }

            return await exists(requested).ConfigureAwait(false) ? throw new InvalidOperationException($"La dirección corta {requested} ya está en uso.") : requested;
        }

        var title = texts is null ? null : ContentTexts.Pick(texts, ContentTexts.DefaultLanguage)?.Title;
        var baseSlug = Slugs.From(title);
        for (var n = 1; n < 100; n++)
        {
            var candidate = Slugs.Numbered(baseSlug, n);
            if (!await exists(candidate).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No se pudo armar una dirección corta libre.");
    }

    public static string Like(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
