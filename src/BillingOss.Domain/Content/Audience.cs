namespace BillingOss.Domain.Content;

/// <summary>Quién ve un contenido (sección 2.2 de diseno/contenidos.md).</summary>
public enum ContentAudience
{
    /// <summary>Cualquiera, sin iniciar sesión.</summary>
    Public,

    /// <summary>Cualquier cliente con sesión.</summary>
    Customers,

    /// <summary>Clientes con un servicio vivo de ciertos productos o en ciertos grupos de clientes.</summary>
    Targeted,
}

/// <summary>Un contenido con audiencia: artículos, novedades y descargas.</summary>
public interface IAudienced
{
    ContentAudience Audience { get; }

    long[] ProductIds { get; }

    long[] GroupIds { get; }
}

/// <summary>
/// Quien mira: una visita sin sesión o un cliente con los productos de sus servicios vivos y los grupos de sus cuentas.
/// Es la única regla de audiencia del módulo. Las listas la repiten en SQL con el mismo sentido (<see cref="Predicate"/>).
/// </summary>
public sealed class ContentViewer
{
    public static ContentViewer Anonymous { get; } = new(false, [], []);

    public ContentViewer(bool isCustomer, IEnumerable<long> productIds, IEnumerable<long> groupIds)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        ArgumentNullException.ThrowIfNull(groupIds);
        IsCustomer = isCustomer;
        ProductIds = isCustomer ? [.. productIds.Distinct().Order()] : [];
        GroupIds = isCustomer ? [.. groupIds.Distinct().Order()] : [];
    }

    public bool IsCustomer { get; }

    /// <summary>Productos de los servicios vivos de las cuentas que ve la sesión.</summary>
    public long[] ProductIds { get; }

    /// <summary>Grupos de clientes de esas cuentas.</summary>
    public long[] GroupIds { get; }

    /// <summary>
    /// El predicado SQL de la audiencia sobre una tabla con alias <c>x</c> y las columnas audience, product_ids y group_ids.
    /// Parámetros: customer, products y groups. Es una constante para que el análisis de EF vea que no se arma con datos.
    /// </summary>
    public const string Predicate =
        "(x.audience = 'Public' OR (@customer AND (x.audience = 'Customers' OR (x.audience = 'Targeted' AND (x.product_ids && @products OR x.group_ids && @groups)))))";

    public bool CanSee(IAudienced item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Audience switch
        {
            ContentAudience.Public => true,
            ContentAudience.Customers => IsCustomer,
            ContentAudience.Targeted => IsCustomer && (item.ProductIds.Intersect(ProductIds).Any() || item.GroupIds.Intersect(GroupIds).Any()),
            _ => false,
        };
    }

    /// <summary>Qué tiene mal una audiencia, o null si se puede guardar: <c>Targeted</c> necesita productos o grupos.</summary>
    public static string? Error(ContentAudience audience, long[] productIds, long[] groupIds)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        ArgumentNullException.ThrowIfNull(groupIds);
        return audience == ContentAudience.Targeted && productIds.Length + groupIds.Length == 0
            ? "Una audiencia dirigida necesita al menos un producto o un grupo de clientes."
            : null;
    }
}
