using System.Globalization;
using BillingOss.App.Content;
using BillingOss.Domain.Content;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Api;

public sealed record CategoryRequest(long? ParentId, string? Slug, int SortOrder, bool Hidden, Dictionary<string, ContentText>? Texts, int? Revision = null);

public sealed record ArticleTextRequest(string? Title, string? Summary, string? Body, string? Tags = null, int? Revision = null);

public sealed record ArticleRequest(
    long CategoryId,
    string? Slug,
    bool Featured,
    int SortOrder,
    ContentAudience Audience,
    long[]? ProductIds,
    long[]? GroupIds,
    Dictionary<string, ArticleTextRequest>? Texts,
    int? Revision = null);

public sealed record AnnouncementRequest(
    ContentAudience Audience,
    long[]? ProductIds,
    long[]? GroupIds,
    bool Pinned,
    DateTimeOffset? ExpiresAt,
    Dictionary<string, ContentText>? Texts,
    int? Revision = null);

public sealed record PublishRequest(DateTimeOffset? PublishAt = null, int? Revision = null);

public sealed record DownloadRequest(
    long CategoryId,
    int SortOrder,
    ContentAudience Audience,
    long[]? ProductIds,
    long[]? GroupIds,
    Dictionary<string, ContentText>? Texts,
    int? Revision = null);

public sealed record ComponentRequest(long? ParentId, ComponentKind Kind, string? Slug, int SortOrder, bool Hidden, long[]? ProductIds, Dictionary<string, ContentText>? Texts, int? Revision = null);

public sealed record ImpactRequest(long ComponentId, ComponentStatus Impact);

/// <param name="Message">El primer mensaje: el texto va en el cuerpo de cada idioma.</param>
public sealed record IncidentRequest(Dictionary<string, ContentText>? Title, Dictionary<string, ContentText>? Message, List<ImpactRequest>? Components, StatusEventState? State = null, DateTimeOffset? StartedAt = null);

public sealed record StatusUpdateRequest(Dictionary<string, ContentText>? Message, StatusEventState? State = null, List<ImpactRequest>? Components = null, int? Revision = null);

public sealed record MaintenanceRequest(Dictionary<string, ContentText>? Title, List<long>? ComponentIds, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record RescheduleRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int? Revision = null);

/// <summary>
/// API de los contenidos para clientes (sección 8 de diseno/contenidos.md): base de conocimiento, novedades, descargas y
/// estado de la red. Cuelga de /api/v1 con API key y Problem Details. Las listas del staff traen los borradores, y la
/// búsqueda y las sugerencias muestran lo que vería un cliente.
/// </summary>
internal static class ContentApi
{
    public static void MapContentApi(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        MapKnowledge(api);
        MapAnnouncements(api);
        MapDownloads(api);
        MapStatus(api);
    }

    // ---- Base de conocimiento ----

    private static void MapKnowledge(RouteGroupBuilder api)
    {
        MapCategories(api, "/kb/categories", CategoryKind.KnowledgeBase, "kb");

        api.MapGet("/kb/articles", async ([FromQuery(Name = "category_id")] long? categoryId, string? status, string? q, int? limit, KnowledgeService kb, CancellationToken ct) =>
        {
            if (!TryState(status, out var state))
            {
                return UnknownState(status);
            }

            var rows = await kb.List(new ArticleFilter(categoryId, state, q, limit ?? 200), ct);
            return Results.Ok(rows.Select(r => new { r.Article.Id, r.Article.Slug, r.Article.CategoryId, r.Article.Status, r.Article.Featured, r.Article.SortOrder, r.Article.Audience, r.Article.Views, r.Article.Helpful, r.Article.Unhelpful, r.Article.Revision, r.Article.UpdatedAt, r.Article.PublishedAt, r.Title, r.Languages }));
        }).WithMetadata(new RequireScope("kb:read"));

        api.MapGet("/kb/articles/{id:long}", async (long id, KnowledgeService kb, CancellationToken ct) =>
            await kb.Get(id, ct) is { } detail ? Results.Ok(AsArticle(detail)) : NotFound("el artículo", id))
            .WithMetadata(new RequireScope("kb:read"));

        api.MapPost("/kb/articles", async (ArticleRequest r, KnowledgeService kb, CancellationToken ct) =>
        {
            var id = await kb.Create(ToInput(r), ct);
            return Results.Created($"/api/v1/kb/articles/{id}", new { id });
        }).WithMetadata(new RequireScope("kb:write"));

        api.MapPut("/kb/articles/{id:long}", async (long id, ArticleRequest r, KnowledgeService kb, CancellationToken ct) =>
        {
            await kb.Update(id, ToInput(r), ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("kb:write"));

        api.MapDelete("/kb/articles/{id:long}", async (long id, KnowledgeService kb, CancellationToken ct) =>
        {
            await kb.Delete(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("kb:write"));

        api.MapPut("/kb/articles/{id:long}/texts/{language}", async (long id, string language, ArticleTextRequest r, KnowledgeService kb, CancellationToken ct) =>
        {
            await kb.SetText(id, language, new ArticleTextInput(r.Title ?? string.Empty, r.Summary, r.Body ?? string.Empty, r.Tags), r.Revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("kb:write"));

        api.MapDelete("/kb/articles/{id:long}/texts/{language}", async (long id, string language, int? revision, KnowledgeService kb, CancellationToken ct) =>
        {
            await kb.RemoveText(id, language, revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("kb:write"));

        foreach (var (action, move) in new (string, Func<KnowledgeService, long, int?, CancellationToken, Task>)[]
        {
            ("publish", (kb, id, revision, ct) => kb.Publish(id, revision, ct)),
            ("unpublish", (kb, id, revision, ct) => kb.Unpublish(id, revision, ct)),
            ("archive", (kb, id, revision, ct) => kb.Archive(id, revision, ct)),
        })
        {
            api.MapPost($"/kb/articles/{{id:long}}/{action}", async (long id, int? revision, KnowledgeService kb, CancellationToken ct) =>
            {
                await move(kb, id, revision, ct);
                return Results.NoContent();
            }).WithMetadata(new RequireScope("kb:write"));
        }

        api.MapGet("/kb/search", async (string? q, string? language, [FromQuery(Name = "account_id")] long? accountId, int? limit, ContentViewers viewers, KnowledgeService kb, CancellationToken ct) =>
            Results.Ok(await kb.Search(await Viewer(viewers, accountId, ct), q, language, limit ?? 20, ct)))
            .WithMetadata(new RequireScope("kb:read"));

        // Para el formulario de ticket: asunto y texto de lo que escribe quien consulta.
        api.MapGet("/kb/suggestions", async (string? q, string? language, [FromQuery(Name = "account_id")] long? accountId, int? limit, ContentViewers viewers, KnowledgeService kb, CancellationToken ct) =>
            Results.Ok(await kb.Suggest(await Viewer(viewers, accountId, ct), q, language, limit ?? 5, ct)))
            .WithMetadata(new RequireScope("kb:read"));
    }

    /// <summary>Quién mira en la búsqueda y en las sugerencias: un cliente de esa cuenta, o una visita sin sesión.</summary>
    private static async Task<ContentViewer> Viewer(ContentViewers viewers, long? accountId, CancellationToken ct) =>
        accountId is { } id ? await viewers.ForAccounts([id], ct) : ContentViewers.Anonymous;

    private static ArticleInput ToInput(ArticleRequest r) => new(
        r.CategoryId,
        r.Slug,
        r.Featured,
        r.SortOrder,
        r.Audience,
        r.ProductIds ?? [],
        r.GroupIds ?? [],
        r.Texts?.ToDictionary(t => t.Key, t => new ArticleTextInput(t.Value.Title ?? string.Empty, t.Value.Summary, t.Value.Body ?? string.Empty, t.Value.Tags)),
        r.Revision);

    private static object AsArticle(ArticleDetail d) => new
    {
        d.Article.Id,
        d.Article.Slug,
        d.Article.CategoryId,
        d.Article.Status,
        d.Article.Featured,
        d.Article.SortOrder,
        d.Article.Audience,
        d.Article.ProductIds,
        d.Article.GroupIds,
        d.Article.Views,
        d.Article.Helpful,
        d.Article.Unhelpful,
        d.Article.Revision,
        d.Article.CreatedAt,
        d.Article.UpdatedAt,
        d.Article.PublishedAt,
        texts = d.Texts.ToDictionary(t => t.Language, t => new { t.Title, t.Summary, t.Body, t.Tags }),
    };

    // ---- Categorías (de la base de conocimiento y de las descargas) ----

    private static void MapCategories(RouteGroupBuilder api, string path, CategoryKind kind, string scope)
    {
        api.MapGet(path, async (CategoryService categories, CancellationToken ct) =>
            Results.Ok((await categories.List(kind, ct)).Select(AsCategory)))
            .WithMetadata(new RequireScope($"{scope}:read"));

        api.MapGet($"{path}/{{id:long}}", async (long id, CategoryService categories, CancellationToken ct) =>
            await categories.Get(kind, id, ct) is { } row ? Results.Ok(AsCategory(row)) : NotFound("la categoría", id))
            .WithMetadata(new RequireScope($"{scope}:read"));

        api.MapPost(path, async (CategoryRequest r, CategoryService categories, CancellationToken ct) =>
        {
            var id = await categories.Create(kind, ToInput(r), ct);
            return Results.Created($"/api/v1{path}/{id}", new { id });
        }).WithMetadata(new RequireScope($"{scope}:write"));

        api.MapPut($"{path}/{{id:long}}", async (long id, CategoryRequest r, CategoryService categories, CancellationToken ct) =>
        {
            await categories.Update(kind, id, ToInput(r), ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope($"{scope}:write"));

        api.MapDelete($"{path}/{{id:long}}", async (long id, CategoryService categories, CancellationToken ct) =>
        {
            await categories.Delete(kind, id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope($"{scope}:write"));
    }

    private static CategoryInput ToInput(CategoryRequest r) =>
        new(r.ParentId, r.Slug, r.SortOrder, r.Hidden, r.Texts ?? new Dictionary<string, ContentText>(), r.Revision);

    private static object AsCategory(CategoryRow r) => new
    {
        r.Category.Id,
        r.Category.ParentId,
        r.Category.Slug,
        r.Category.SortOrder,
        r.Category.Hidden,
        r.Texts,
        Items = r.Items,
        r.Category.Revision,
        r.Category.CreatedAt,
        r.Category.UpdatedAt,
    };

    // ---- Novedades ----

    private static void MapAnnouncements(RouteGroupBuilder api)
    {
        api.MapGet("/announcements", async (string? status, int? limit, AnnouncementService announcements, CancellationToken ct) =>
        {
            if (!TryState(status, out var state))
            {
                return UnknownState(status);
            }

            return Results.Ok((await announcements.List(state, limit ?? 200, ct)).Select(AsAnnouncement));
        }).WithMetadata(new RequireScope("announcements:read"));

        api.MapGet("/announcements/{id:long}", async (long id, AnnouncementService announcements, CancellationToken ct) =>
            await announcements.Get(id, ct) is { } row ? Results.Ok(AsAnnouncement(row)) : NotFound("la novedad", id))
            .WithMetadata(new RequireScope("announcements:read"));

        api.MapPost("/announcements", async (AnnouncementRequest r, AnnouncementService announcements, CancellationToken ct) =>
        {
            var id = await announcements.Create(ToInput(r), ct);
            return Results.Created($"/api/v1/announcements/{id}", new { id });
        }).WithMetadata(new RequireScope("announcements:write"));

        api.MapPut("/announcements/{id:long}", async (long id, AnnouncementRequest r, AnnouncementService announcements, CancellationToken ct) =>
        {
            await announcements.Update(id, ToInput(r), ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("announcements:write"));

        api.MapDelete("/announcements/{id:long}", async (long id, AnnouncementService announcements, CancellationToken ct) =>
        {
            await announcements.Delete(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("announcements:write"));

        // El cuerpo es opcional: sin él se publica ya.
        api.MapPost("/announcements/{id:long}/publish", async (long id, HttpRequest request, AnnouncementService announcements, CancellationToken ct) =>
        {
            PublishRequest? body;
            try
            {
                // Un cliente que manda el cuerpo sin largo conocido (chunked) también cuenta: se mira el tipo y no el largo.
                body = request.ContentLength != 0 && request.HasJsonContentType() ? await request.ReadFromJsonAsync<PublishRequest>(ct) : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "El cuerpo no es un JSON válido: usá {\"publish_at\": \"2026-11-05T12:00:00Z\"} o mandalo vacío.");
            }

            await announcements.Publish(id, body?.PublishAt, body?.Revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("announcements:write"));

        api.MapPost("/announcements/{id:long}/unpublish", async (long id, int? revision, AnnouncementService announcements, CancellationToken ct) =>
        {
            await announcements.Unpublish(id, revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("announcements:write"));

        api.MapPost("/announcements/{id:long}/archive", async (long id, int? revision, AnnouncementService announcements, CancellationToken ct) =>
        {
            await announcements.Archive(id, revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("announcements:write"));
    }

    private static AnnouncementInput ToInput(AnnouncementRequest r) =>
        new(r.Audience, r.ProductIds ?? [], r.GroupIds ?? [], r.Pinned, r.ExpiresAt, r.Texts ?? new Dictionary<string, ContentText>(), r.Revision);

    private static object AsAnnouncement(AnnouncementRow r) => new
    {
        r.Announcement.Id,
        r.Announcement.Status,
        r.Phase,
        r.Announcement.PublishAt,
        r.Announcement.ExpiresAt,
        r.Announcement.Pinned,
        r.Announcement.Audience,
        r.Announcement.ProductIds,
        r.Announcement.GroupIds,
        r.Texts,
        r.Announcement.Revision,
        r.Announcement.CreatedAt,
        r.Announcement.UpdatedAt,
    };

    // ---- Descargas ----

    private static void MapDownloads(RouteGroupBuilder api)
    {
        MapCategories(api, "/download-categories", CategoryKind.Downloads, "downloads");

        api.MapGet("/downloads", async ([FromQuery(Name = "category_id")] long? categoryId, string? status, int? limit, DownloadService downloads, CancellationToken ct) =>
        {
            if (!TryState(status, out var state))
            {
                return UnknownState(status);
            }

            return Results.Ok((await downloads.List(categoryId, state, limit ?? 500, ct)).Select(AsDownload));
        }).WithMetadata(new RequireScope("downloads:read"));

        api.MapGet("/downloads/{id:long}", async (long id, DownloadService downloads, CancellationToken ct) =>
            await downloads.Get(id, ct) is { } row ? Results.Ok(AsDownload(row)) : NotFound("la descarga", id))
            .WithMetadata(new RequireScope("downloads:read"));

        api.MapPost("/downloads", async (DownloadRequest r, DownloadService downloads, CancellationToken ct) =>
        {
            var id = await downloads.Create(ToInput(r), ct);
            return Results.Created($"/api/v1/downloads/{id}", new { id });
        }).WithMetadata(new RequireScope("downloads:write"));

        api.MapPut("/downloads/{id:long}", async (long id, DownloadRequest r, DownloadService downloads, CancellationToken ct) =>
        {
            await downloads.Update(id, ToInput(r), ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("downloads:write"));

        api.MapDelete("/downloads/{id:long}", async (long id, DownloadService downloads, CancellationToken ct) =>
        {
            await downloads.Delete(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("downloads:write"));

        // El archivo viaja en el cuerpo del pedido. El tope de 1 MiB del sitio se sube solo hasta el máximo de las descargas.
        api.MapPut("/downloads/{id:long}/file", async (long id, string? name, HttpContext http, DownloadService downloads, IOptions<ContentOptions> options, CancellationToken ct) =>
        {
            var max = options.Value.MaxDownloadBytes;
            if (http.Request.ContentLength > max)
            {
                return TooLarge(max);
            }

            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = max + 1;
            }

            try
            {
                var file = await downloads.Upload(id, name, http.Request.Body, ct);
                return Results.Ok(new { file.FileName, file.ContentType, file.Size, file.Sha256 });
            }
            catch (ContentRejectedException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
            }
        }).WithMetadata(new RequireScope("downloads:write"));

        // El staff baja el archivo sin sumar al contador.
        api.MapGet("/downloads/{id:long}/file", async (long id, HttpContext http, DownloadService downloads, CancellationToken ct) =>
            await downloads.OpenForStaff(id, ct) is { } file ? ContentFiles.Send(http, file) : NotFound("el archivo de la descarga", id))
            .WithMetadata(new RequireScope("downloads:read"));

        foreach (var (action, move) in new (string, Func<DownloadService, long, int?, CancellationToken, Task>)[]
        {
            ("publish", (d, id, revision, ct) => d.Publish(id, revision, ct)),
            ("unpublish", (d, id, revision, ct) => d.Unpublish(id, revision, ct)),
            ("archive", (d, id, revision, ct) => d.Archive(id, revision, ct)),
        })
        {
            api.MapPost($"/downloads/{{id:long}}/{action}", async (long id, int? revision, DownloadService downloads, CancellationToken ct) =>
            {
                await move(downloads, id, revision, ct);
                return Results.NoContent();
            }).WithMetadata(new RequireScope("downloads:write"));
        }
    }

    private static IResult TooLarge(long max) =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: string.Create(CultureInfo.InvariantCulture, $"El archivo pasa el tope de {max / (1024 * 1024)} MiB."));

    private static DownloadInput ToInput(DownloadRequest r) =>
        new(r.CategoryId, r.SortOrder, r.Audience, r.ProductIds ?? [], r.GroupIds ?? [], r.Texts ?? new Dictionary<string, ContentText>(), r.Revision);

    private static object AsDownload(DownloadRow r) => new
    {
        r.Download.Id,
        r.Download.CategoryId,
        r.Download.Status,
        r.Download.Audience,
        r.Download.ProductIds,
        r.Download.GroupIds,
        r.Download.SortOrder,
        r.Download.DownloadCount,
        File = r.Download.HasFile ? new { Name = r.Download.FileName, r.Download.ContentType, r.Download.Size, r.Download.Sha256 } : null,
        r.Texts,
        r.Download.Revision,
        r.Download.CreatedAt,
        r.Download.UpdatedAt,
    };

    // ---- Estado de la red ----

    private static void MapStatus(RouteGroupBuilder api)
    {
        api.MapGet("/status", async (string? language, StatusService status, CancellationToken ct) =>
            Results.Ok(await status.Overview(language, ct)))
            .WithMetadata(new RequireScope("status:read"));

        api.MapGet("/status/components", async (StatusService status, CancellationToken ct) =>
            Results.Ok((await status.Components(ct)).Select(AsComponent)))
            .WithMetadata(new RequireScope("status:read"));

        api.MapPost("/status/components", async (ComponentRequest r, StatusService status, CancellationToken ct) =>
        {
            var id = await status.CreateComponent(ToInput(r), ct);
            return Results.Created($"/api/v1/status/components/{id}", new { id });
        }).WithMetadata(new RequireScope("status:write"));

        api.MapPut("/status/components/{id:long}", async (long id, ComponentRequest r, StatusService status, CancellationToken ct) =>
        {
            await status.UpdateComponent(id, ToInput(r), ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));

        api.MapDelete("/status/components/{id:long}", async (long id, StatusService status, CancellationToken ct) =>
        {
            await status.DeleteComponent(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));

        // Los incidentes y mantenimientos vigentes de esos productos, para la tarjeta del servicio y el formulario de ticket.
        api.MapGet("/status/issues", async ([FromQuery(Name = "product_id")] long[]? productIds, string? language, StatusService status, CancellationToken ct) =>
            Results.Ok(await status.IssuesForProducts(productIds ?? [], language, ct)))
            .WithMetadata(new RequireScope("status:read"));

        api.MapGet("/status/incidents", async ([FromQuery(Name = "open")] bool? openOnly, int? limit, string? language, StatusService status, CancellationToken ct) =>
            Results.Ok(await status.List(StatusEventKind.Incident, openOnly ?? false, limit ?? 100, language, ct)))
            .WithMetadata(new RequireScope("status:read"));

        api.MapGet("/status/incidents/{id:long}", async (long id, string? language, StatusService status, CancellationToken ct) =>
            await status.Event(id, language, includeHidden: true, ct: ct) is { Kind: StatusEventKind.Incident } view ? Results.Ok(view) : NotFound("el incidente", id))
            .WithMetadata(new RequireScope("status:read"));

        api.MapPost("/status/incidents", async (IncidentRequest r, StatusService status, CancellationToken ct) =>
        {
            var id = await status.OpenIncident(
                new IncidentInput(
                    r.Title ?? new Dictionary<string, ContentText>(),
                    r.Message ?? new Dictionary<string, ContentText>(),
                    [.. (r.Components ?? []).Select(c => new ComponentImpact(c.ComponentId, c.Impact))],
                    r.State ?? StatusEventState.Investigating,
                    r.StartedAt),
                ct);
            return Results.Created($"/api/v1/status/incidents/{id}", new { id });
        }).WithMetadata(new RequireScope("status:write"));

        api.MapPost("/status/incidents/{id:long}/updates", async (long id, StatusUpdateRequest r, StatusService status, CancellationToken ct) =>
        {
            await status.PostUpdate(id, ToInput(r), ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));

        api.MapGet("/status/maintenances", async ([FromQuery(Name = "open")] bool? openOnly, int? limit, string? language, StatusService status, CancellationToken ct) =>
            Results.Ok(await status.List(StatusEventKind.Maintenance, openOnly ?? false, limit ?? 100, language, ct)))
            .WithMetadata(new RequireScope("status:read"));

        api.MapGet("/status/maintenances/{id:long}", async (long id, string? language, StatusService status, CancellationToken ct) =>
            await status.Event(id, language, includeHidden: true, ct: ct) is { Kind: StatusEventKind.Maintenance } view ? Results.Ok(view) : NotFound("el mantenimiento", id))
            .WithMetadata(new RequireScope("status:read"));

        api.MapPost("/status/maintenances", async (MaintenanceRequest r, StatusService status, CancellationToken ct) =>
        {
            var id = await status.ScheduleMaintenance(new MaintenanceInput(r.Title ?? new Dictionary<string, ContentText>(), r.ComponentIds ?? [], r.StartsAt, r.EndsAt), ct);
            return Results.Created($"/api/v1/status/maintenances/{id}", new { id });
        }).WithMetadata(new RequireScope("status:write"));

        api.MapPut("/status/maintenances/{id:long}", async (long id, RescheduleRequest r, StatusService status, CancellationToken ct) =>
        {
            await status.Reschedule(id, r.StartsAt, r.EndsAt, r.Revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));

        api.MapPost("/status/maintenances/{id:long}/updates", async (long id, StatusUpdateRequest r, StatusService status, CancellationToken ct) =>
        {
            await status.PostUpdate(id, ToInput(r), ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));

        api.MapPost("/status/maintenances/{id:long}/cancel", async (long id, int? revision, StatusService status, CancellationToken ct) =>
        {
            await status.Cancel(id, revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));

        api.MapPost("/status/maintenances/{id:long}/complete", async (long id, int? revision, StatusService status, CancellationToken ct) =>
        {
            await status.Complete(id, revision, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));

        // Sin los tokens: el enlace de baja de cada casilla no sale del sistema.
        api.MapGet("/status/subscribers", async (bool? confirmed, int? limit, SubscriptionService subscribers, CancellationToken ct) =>
            Results.Ok(await subscribers.List(confirmed, limit ?? 500, ct)))
            .WithMetadata(new RequireScope("status:read"));

        api.MapDelete("/status/subscribers/{id:long}", async (long id, SubscriptionService subscribers, CancellationToken ct) =>
        {
            await subscribers.Remove(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("status:write"));
    }

    private static ComponentInput ToInput(ComponentRequest r) =>
        new(r.ParentId, r.Kind, r.Slug, r.SortOrder, r.Hidden, r.ProductIds ?? [], r.Texts ?? new Dictionary<string, ContentText>(), r.Revision);

    private static StatusUpdateInput ToInput(StatusUpdateRequest r) =>
        new(r.Message ?? new Dictionary<string, ContentText>(), r.State, r.Components?.Select(c => new ComponentImpact(c.ComponentId, c.Impact)).ToList(), r.Revision);

    private static object AsComponent(ComponentRow r) => new
    {
        r.Component.Id,
        r.Component.ParentId,
        r.Component.Kind,
        r.Component.Slug,
        r.Component.SortOrder,
        r.Component.Hidden,
        r.Component.ProductIds,
        r.Texts,
        r.Status,
        r.Component.Revision,
        r.Component.CreatedAt,
        r.Component.UpdatedAt,
    };

    // ---- Comunes ----

    private static bool TryState(string? text, out PublishState? state)
    {
        state = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (Enum.TryParse<PublishState>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            state = parsed;
            return true;
        }

        return false;
    }

    private static IResult UnknownState(string? text) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Estado desconocido: {text}. Los válidos son draft, published y archived.");

    private static IResult NotFound(string what, long id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: string.Create(CultureInfo.InvariantCulture, $"No existe {what} {id}."));
}

/// <summary>Cómo sale un archivo de una descarga: siempre como adjunto y con el tipo fijo que corresponde a su extensión.</summary>
internal static class ContentFiles
{
    public static IResult Send(HttpContext http, DownloadFileStream file)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(file);
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.AcceptRanges = "none";
        http.Response.ContentLength = file.Size;
        return Results.Stream(file.Content, file.ContentType, file.FileName);
    }
}
