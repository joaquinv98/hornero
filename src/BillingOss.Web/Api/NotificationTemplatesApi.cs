using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

public sealed record TemplateRequest(string Subject, string Body);

/// <summary>
/// Plantillas de los avisos, editables por el proveedor (D-13). Una edición reemplaza a la de fábrica, y borrarla
/// vuelve a la de fábrica. Se valida la sintaxis al guardar.
/// </summary>
internal static class NotificationTemplatesApi
{
    public static void MapNotificationTemplates(this RouteGroupBuilder api)
    {
        api.MapGet("/notifications/templates", async (BillingDbContext db, CancellationToken ct) =>
        {
            var custom = await db.Set<Template>().AsNoTracking().ToListAsync(ct);
            return Results.Ok(TemplateRenderer.Keys.SelectMany(key => TemplateRenderer.Cultures.Select(culture =>
            {
                var edited = custom.Find(t => t.Key == key && t.Culture == culture);
                var (subject, body) = edited is null ? TemplateRenderer.Default(key, culture) : (edited.Subject, edited.Body);
                return new { key, culture, customized = edited is not null, subject, body };
            })));
        }).WithMetadata(new RequireScope("templates:read"));

        api.MapPut("/notifications/templates/{key}/{culture}", async (string key, string culture, TemplateRequest r, BillingDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (!TemplateRenderer.Exists(key, culture))
            {
                return Results.Problem(statusCode: 404, title: $"No existe la plantilla {key} en {culture}.");
            }

            if ((TemplateRenderer.Validate(r.Subject) ?? TemplateRenderer.Validate(r.Body)) is { } error)
            {
                return Results.Problem(statusCode: 422, title: $"La plantilla tiene un error: {error}");
            }

            var template = await db.Set<Template>().SingleOrDefaultAsync(t => t.Key == key && t.Culture == culture, ct);
            if (template is null)
            {
                template = new Template { Key = key, Culture = culture };
                db.Add(template);
            }

            template.Subject = r.Subject;
            template.Body = r.Body;
            template.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("templates:write"));

        api.MapDelete("/notifications/templates/{key}/{culture}", async (string key, string culture, BillingDbContext db, CancellationToken ct) =>
        {
            await db.Set<Template>().Where(t => t.Key == key && t.Culture == culture).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("templates:write"));
    }
}
