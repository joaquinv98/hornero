namespace BillingOss.Domain.Content;

/// <summary>La fase de una novedad, que se deduce con la hora y no se guarda (sección 4.2 de diseno/contenidos.md).</summary>
public enum AnnouncementPhase
{
    Draft,
    Scheduled,
    Live,
    Expired,
    Archived,
}

/// <summary>Una novedad para los clientes. Los textos usan el título y el cuerpo.</summary>
public sealed class Announcement : IAudienced
{
    public long Id { get; private set; }

    public PublishState Status { get; set; }

    /// <summary>Desde cuándo se ve. Con <see cref="PublishState.Published"/> una fecha futura la deja programada.</summary>
    public DateTimeOffset? PublishAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Se muestra arriba de las demás mientras esté vigente.</summary>
    public bool Pinned { get; set; }

    public ContentAudience Audience { get; set; }

    public long[] ProductIds { get; set; } = [];

    public long[] GroupIds { get; set; } = [];

    public string Texts { get; set; } = "{}";

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public void SetImportedId(long id) => Id = id;

    public AnnouncementPhase Phase(DateTimeOffset now) => Status switch
    {
        PublishState.Draft => AnnouncementPhase.Draft,
        PublishState.Archived => AnnouncementPhase.Archived,
        _ when PublishAt is { } at && at > now => AnnouncementPhase.Scheduled,
        _ when ExpiresAt is { } end && end <= now => AnnouncementPhase.Expired,
        _ => AnnouncementPhase.Live,
    };

    public bool IsLive(DateTimeOffset now) => Phase(now) == AnnouncementPhase.Live;

    /// <summary>Qué tiene mal el vencimiento, o null si se puede guardar.</summary>
    public string? DateError() => PublishAt is { } from && ExpiresAt is { } to && to <= from ? "El vencimiento tiene que ser posterior a la publicación." : null;
}
