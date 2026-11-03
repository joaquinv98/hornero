namespace BillingOss.Domain.Content;

/// <summary>Los dos árboles de categorías: la base de conocimiento y las descargas.</summary>
public enum CategoryKind
{
    KnowledgeBase,
    Downloads,
}

/// <summary>Estado de publicación de artículos, novedades y descargas (sección 2.3 de diseno/contenidos.md).</summary>
public enum PublishState
{
    Draft,
    Published,
    Archived,
}

/// <summary>
/// Categoría de la base de conocimiento o de las descargas, con hasta tres niveles. El nombre y la descripción son el
/// título y el resumen de sus textos.
/// </summary>
public sealed class ContentCategory
{
    public const int MaxDepth = 3;

    private ContentCategory()
    {
    }

    public ContentCategory(CategoryKind kind, string slug, DateTimeOffset now)
    {
        Kind = kind;
        Slug = slug;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public long Id { get; private set; }

    public CategoryKind Kind { get; private set; }

    public long? ParentId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Textos por idioma, JSON (<see cref="ContentTexts"/>).</summary>
    public string Texts { get; set; } = "{}";

    /// <summary>Sube con cada cambio. Guardar con una revisión atrasada falla.</summary>
    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Para conservar el ID de origen en una importación (D-17).</summary>
    public void SetImportedId(long id) => Id = id;
}

/// <summary>Un artículo de la base de conocimiento (sección 3 de diseno/contenidos.md). Sus textos están en <see cref="ArticleText"/>.</summary>
public sealed class Article : IAudienced
{
    public long Id { get; private set; }

    public long CategoryId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public PublishState Status { get; set; }

    /// <summary>Aparece en la portada de la ayuda.</summary>
    public bool Featured { get; set; }

    public int SortOrder { get; set; }

    public ContentAudience Audience { get; set; }

    public long[] ProductIds { get; set; } = [];

    public long[] GroupIds { get; set; } = [];

    /// <summary>Lecturas, los votos que le sirvieron y los que no. Suben con SQL atómico, nunca desde una copia leída.</summary>
    public long Views { get; set; }

    public long Helpful { get; set; }

    public long Unhelpful { get; set; }

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public void SetImportedId(long id) => Id = id;

    public void SetStatus(PublishState status, DateTimeOffset now)
    {
        if (status == PublishState.Published && Status != PublishState.Published)
        {
            PublishedAt = now;
        }

        Status = status;
    }
}

/// <summary>El título, resumen, cuerpo (Markdown) y etiquetas de un artículo en un idioma.</summary>
public sealed class ArticleText
{
    public long ArticleId { get; set; }

    public string Language { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>Palabras que alimentan la búsqueda aunque no estén en el texto, separadas por comas.</summary>
    public string Tags { get; set; } = string.Empty;

    public IReadOnlyList<string> TagList => Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Si el artículo le sirvió a quien lo votó. Uno por artículo y votante: votar de nuevo reemplaza el voto.</summary>
public sealed class ArticleVote
{
    public long ArticleId { get; set; }

    /// <summary>"user:{id}" para un usuario del portal o "visit:{hash}" para una visita con su cookie.</summary>
    public string Voter { get; set; } = string.Empty;

    public bool Helpful { get; set; }

    public DateTimeOffset VotedAt { get; set; }
}
