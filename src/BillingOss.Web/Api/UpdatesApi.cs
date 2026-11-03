using BillingOss.App.Platform;
using BillingOss.Updates;

namespace BillingOss.Web.Api;

public sealed record UpdateRequestBody(string Version);

/// <summary>
/// Versiones y pedidos de actualización (D-20, sección 7.2). Lo mismo que muestra el panel. El pedido solo deja la
/// versión: el actualizador del host la vuelve a validar contra el canal verificado antes de tocar nada.
/// </summary>
internal static class UpdatesApi
{
    public static void MapUpdates(this RouteGroupBuilder api)
    {
        api.MapGet("/updates", async (UpdateService updates, TimeProvider time, CancellationToken ct) =>
        {
            var o = await updates.Overview(time.GetUtcNow(), ct);
            return Results.Ok(new
            {
                state = o.Summary.State,
                reason = o.Summary.Reason,
                installed = o.Summary.Installed.Text(),
                checked_at = o.CheckedAt,
                check_error = o.CheckError,
                cves = o.Summary.Cves.Select(c => c.CveId),
                postgres_behind = o.Summary.PostgresBehind,
                offered = o.Offered.Select(r => new
                {
                    version = r.Release.ReleaseVersion,
                    r.Release.ReleaseDate,
                    r.Release.Security,
                    r.Release.Severity,
                    cves = r.Release.CveList.Select(c => c.CveId),
                    r.Release.RequiresWindow,
                    r.Release.Notes,
                    r.Path,
                    r.Blocked,
                }),
                nodes = o.Nodes.Select(n => new { n.Name, n.Version, n.UpdaterVersion, n.SeenAt, n.Status }),
                requests = o.Requests.Select(Describe),
            });
        }).WithMetadata(new RequireScope("updates:read"));

        api.MapPost("/updates/requests", async (UpdateRequestBody r, UpdateService updates, CancellationToken ct) =>
        {
            var id = await updates.Request(r.Version, ct);
            return Results.Accepted($"/api/v1/updates/requests/{id}", new { id });
        }).WithMetadata(new RequireScope("updates:write"));

        api.MapGet("/updates/requests/{id:long}", async (long id, UpdateService updates, CancellationToken ct) =>
            await updates.Find(id, ct) is { } request
                ? Results.Ok(Describe(request))
                : Results.Problem(statusCode: 404, title: $"No existe el pedido {id}."))
            .WithMetadata(new RequireScope("updates:read"));
    }

    private static object Describe(UpdateRequest r) => new
    {
        r.Id,
        r.Version,
        r.Origin,
        r.RequestedBy,
        r.RequestedAt,
        r.Status,
        r.Phase,
        r.FromVersion,
        r.StartedAt,
        r.FinishedAt,
        r.Error,
        progress = UpdateService.Events(r),
    };
}
