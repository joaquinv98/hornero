using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

public sealed record AttributionRequest(long AccountId, string Code);

public sealed record PayoutPaidRequest(string Reference);

/// <summary>Programa de afiliados en la API propia (sección 9 de diseno/afiliados.md).</summary>
internal static class AffiliatesApi
{
    public static void MapAffiliatesApi(this RouteGroupBuilder api)
    {
        api.MapGet("/affiliates", async (string? status, BillingDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<Affiliate>().AsNoTracking();
            if (status is not null)
            {
                if (!Enum.TryParse<AffiliateStatus>(status, true, out var parsed))
                {
                    return Results.Problem(statusCode: 400, title: $"Estado desconocido: {status}.");
                }

                query = query.Where(a => a.Status == parsed);
            }

            var affiliates = await query.OrderBy(a => a.Id).Take(1000).ToListAsync(ct);
            var ids = affiliates.Select(a => a.Id).ToList();
            var balances = await AffiliateData.Balances(db, ids, ct);
            var clicks = await db.Set<AffiliateClick>().Where(c => ids.Contains(c.AffiliateId)).GroupBy(c => c.AffiliateId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            var referrals = await db.Set<AffiliateReferral>().Where(r => ids.Contains(r.AffiliateId)).GroupBy(r => r.AffiliateId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            return Results.Ok(affiliates.Select(a => new
            {
                a.Id,
                a.AccountId,
                a.Code,
                a.Status,
                a.CreatedAt,
                clicks = clicks.GetValueOrDefault(a.Id),
                referrals = referrals.GetValueOrDefault(a.Id),
                balances = Balances(balances.GetValueOrDefault(a.Id)),
            }));
        }).WithMetadata(new RequireScope("affiliates:read"));

        api.MapGet("/affiliates/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
        {
            var a = await db.Set<Affiliate>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (a is null)
            {
                return Results.Problem(statusCode: 404, title: $"No existe el afiliado {id}.");
            }

            var balances = (await AffiliateData.Balances(db, [id], ct)).GetValueOrDefault(id);
            var commissions = await db.Set<AffiliateCommission>().AsNoTracking().Where(c => c.AffiliateId == id).OrderByDescending(c => c.Id).Take(500)
                .Select(c => new { c.Id, c.ServiceId, c.DocumentId, currency = c.Currency.Code, c.Base, c.Kind, c.Rate, c.Amount, c.Reversed, c.Status, c.ReleaseOn, c.CreatedAt, c.Imported })
                .ToListAsync(ct);
            var payouts = await db.Set<AffiliatePayout>().AsNoTracking().Where(p => p.AffiliateId == id).OrderByDescending(p => p.Id).Take(500)
                .Select(p => new { p.Id, p.Method, currency = p.Currency.Code, p.Amount, p.Status, p.Reference, p.Reason, p.RequestedAt, p.DecidedAt })
                .ToListAsync(ct);
            var referrals = await db.Set<AffiliateReferral>().AsNoTracking().Where(r => r.AffiliateId == id).OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.ServiceId, r.AccountId, r.OrderId, r.Source, r.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(new { a.Id, a.AccountId, a.Code, a.Status, a.CreatedAt, a.DecidedAt, balances = Balances(balances), referrals, commissions, payouts });
        }).WithMetadata(new RequireScope("affiliates:read"));

        api.MapPost("/affiliates/{id:long}/{action}", async (long id, string action, AffiliateService affiliates, CancellationToken ct) =>
        {
            AffiliateStatus? target = action switch
            {
                "approve" or "reactivate" => AffiliateStatus.Active,
                "reject" => AffiliateStatus.Rejected,
                "suspend" => AffiliateStatus.Suspended,
                _ => null,
            };
            if (target is not { } status)
            {
                return Results.Problem(statusCode: 404, title: $"Acción desconocida: {action}.");
            }

            await affiliates.Decide(id, status, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("affiliates:write"));

        // Una tienda que no corre en el portal informa de qué afiliado vino la cuenta, con el código del enlace.
        api.MapPost("/affiliates/attributions", async (AttributionRequest r, AffiliateService affiliates, CancellationToken ct) =>
            Results.Ok(new { attributed = await affiliates.AttributeByCode(r.AccountId, r.Code, ct) }))
            .WithMetadata(new RequireScope("affiliates:write"));

        api.MapGet("/affiliates/payouts", async (string? status, BillingDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<AffiliatePayout>().AsNoTracking();
            if (status is not null)
            {
                if (!Enum.TryParse<PayoutStatus>(status, true, out var parsed))
                {
                    return Results.Problem(statusCode: 400, title: $"Estado desconocido: {status}.");
                }

                query = query.Where(p => p.Status == parsed);
            }

            return Results.Ok(await query.OrderBy(p => p.Id).Take(1000)
                .Select(p => new { p.Id, p.AffiliateId, p.Method, currency = p.Currency.Code, p.Amount, p.Status, p.Details, p.Reference, p.Reason, p.RequestedAt, p.DecidedAt })
                .ToListAsync(ct));
        }).WithMetadata(new RequireScope("affiliates:read"));

        api.MapPost("/affiliates/payouts/{id:long}/paid", async (long id, PayoutPaidRequest r, AffiliateService affiliates, CancellationToken ct) =>
        {
            await affiliates.MarkPayoutPaid(id, r.Reference, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("affiliates:write"));

        api.MapPost("/affiliates/payouts/{id:long}/reject", async (long id, RejectRequest r, AffiliateService affiliates, CancellationToken ct) =>
        {
            await affiliates.RejectPayout(id, r.Reason, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("affiliates:write"));
    }

    private static IEnumerable<object> Balances(IReadOnlyList<AffiliateBalance>? balances) =>
        (balances ?? []).Select(b => new { currency = b.Currency.Code, available = b.Available, held = b.Held, withdrawn = b.Withdrawn });
}
