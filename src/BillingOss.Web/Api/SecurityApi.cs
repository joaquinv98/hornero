using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

public sealed record ReviewRequest(string Decision, string? Note = null);

public sealed record BlockRequest(string Kind, string Value, string Reason, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Revisión de pedidos retenidos por fraude y listas de bloqueo por la API propia (diseno/fraude-y-acceso.md, sección
/// 10). Usan los mismos casos de uso que el admin.
/// </summary>
internal static class SecurityApi
{
    public static void MapSecurityApi(this RouteGroupBuilder api)
    {
        api.MapGet("/orders/review", async (BillingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Set<FraudCheck>().AsNoTracking()
                    .Where(c => c.Status == ScreeningStatus.Pending || c.Status == ScreeningStatus.Review)
                    .OrderBy(c => c.CreatedAt).Take(500).ToListAsync(ct))
                .Select(c => new { c.OrderId, c.AccountId, c.Status, c.Score, reasons = c.ReasonList, ip = c.Ip?.ToString(), c.Error, c.CreatedAt })))
            .WithMetadata(new RequireScope("orders:review"));

        api.MapPost("/orders/{id:long}/review", async (long id, ReviewRequest r, FraudReviewService review, CancellationToken ct) =>
        {
            if (r.Decision is not ("approve" or "reject"))
            {
                return Results.Problem(statusCode: 400, title: "La decisión es approve o reject.");
            }

            var decision = await review.Decide(id, r.Decision == "approve", r.Note, ct);
            return Results.Ok(new { order_id = decision.OrderId, decision.Status, credit_note_ids = decision.CreditNoteIds, paid_amount = decision.PaidAmount });
        }).WithMetadata(new RequireScope("orders:review"));

        api.MapGet("/security/blocklist", async (BillingDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            return Results.Ok(await db.Set<BlockedEntry>().AsNoTracking().Where(e => e.ExpiresAt == null || e.ExpiresAt > now).OrderBy(e => e.Id)
                .Select(e => new { e.Id, e.Kind, e.Value, e.Reason, e.Source, e.CreatedBy, e.CreatedAt, e.ExpiresAt }).ToListAsync(ct));
        }).WithMetadata(new RequireScope("security:read"));

        api.MapPost("/security/blocklist", async (BlockRequest r, AccessGuard guard, CancellationToken ct) =>
        {
            if (!Enum.TryParse<BlockKind>(r.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                return Results.Problem(statusCode: 400, title: "El tipo es ip, email o domain.");
            }

            var id = await guard.Block(kind, r.Value, r.Reason, r.ExpiresAt, BlockSource.Api, ct);
            return Results.Created($"/api/v1/security/blocklist/{id}", new { id });
        }).WithMetadata(new RequireScope("security:write"));

        api.MapDelete("/security/blocklist/{id:long}", async (long id, AccessGuard guard, CancellationToken ct) =>
            await guard.Unblock(id, ct) ? Results.NoContent() : Results.Problem(statusCode: 404, title: $"No existe la entrada {id}."))
            .WithMetadata(new RequireScope("security:write"));
    }
}
