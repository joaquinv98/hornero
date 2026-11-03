using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Shared;
using BillingOss.Domain.Usage;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

public sealed record UsageMetricRequest(string? Name, string? Unit, UsageAggregation? Aggregation, decimal Included, decimal Step = 1m, UsageCycle Cycle = UsageCycle.Renewal, IReadOnlyList<int>? AlertPercents = null, bool Active = true);

public sealed record UsagePriceRequest(string Currency, decimal UnitPrice, DateOnly ValidFrom);

/// <summary>Uso medido (sección 9 de diseno/uso-medido.md): el uso de un servicio y las métricas de un producto.</summary>
internal static class UsageApi
{
    public static void MapUsage(this RouteGroupBuilder api)
    {
        api.MapGet("/services/{id:long}/usage", async (long id, UsageQueries usage, CancellationToken ct) =>
            await usage.ForService(id, ct) is { } view
                ? Results.Ok(new
                {
                    view.ServiceId,
                    view.AccountId,
                    currency = view.Currency.Code,
                    metrics = view.Metrics.Select(m => new { m.Code, m.Name, m.Unit, m.Aggregation, m.Cycle, m.Included, m.Used, m.Percent, m.Since, m.LastSample, m.Excess, m.UnitPrice, m.Estimated }),
                    charges = view.Charges.Select(c => new { c.Code, c.Start, c.End, c.Measured, c.Included, c.Excess, c.UnitPrice, c.Amount, c.DocumentId }),
                })
                : Results.Problem(statusCode: 404, title: $"No existe el servicio {id}."))
            .WithMetadata(new RequireScope("services:read"));

        api.MapGet("/products/{id:long}/usage-metrics", async (long id, BillingDbContext db, CancellationToken ct) =>
        {
            if (!await db.Products.AnyAsync(p => p.Id == id, ct))
            {
                return Results.Problem(statusCode: 404, title: $"No existe el producto {id}.");
            }

            var metrics = await db.Set<UsageMetric>().AsNoTracking().Where(m => m.ProductId == id).OrderBy(m => m.Id).ToListAsync(ct);
            var ids = metrics.Select(m => m.Id).ToList();
            var prices = await db.Set<UsageMetricPrice>().AsNoTracking().Where(p => ids.Contains(p.MetricId)).OrderBy(p => p.ValidFrom).ToListAsync(ct);
            return Results.Ok(metrics.Select(m => new
            {
                m.Code,
                m.Name,
                m.Unit,
                m.Kind,
                m.Aggregation,
                m.Included,
                m.Step,
                m.Cycle,
                m.AlertPercents,
                m.Active,
                prices = prices.Where(p => p.MetricId == m.Id).Select(p => new { currency = p.Currency.Code, p.UnitPrice, p.ValidFrom }),
            }));
        }).WithMetadata(new RequireScope("catalog:read"));

        api.MapPut("/products/{id:long}/usage-metrics/{code}", async (long id, string code, UsageMetricRequest r, UsageCatalog catalog, CancellationToken ct) =>
        {
            var m = await catalog.Save(id, code, new UsageMetricInput(r.Name, r.Unit, r.Aggregation, r.Included, r.Step, r.Cycle, r.AlertPercents, r.Active), ct);
            return Results.Ok(new { m.Code, m.Name, m.Unit, m.Kind, m.Aggregation, m.Included, m.Step, m.Cycle, m.AlertPercents, m.Active });
        }).WithMetadata(new RequireScope("catalog:write"));

        api.MapPost("/products/{id:long}/usage-metrics/{code}/prices", async (long id, string code, UsagePriceRequest r, UsageCatalog catalog, CancellationToken ct) =>
        {
            var price = await catalog.AddPrice(id, code, new Currency(r.Currency), r.UnitPrice, r.ValidFrom, ct);
            return Results.Created($"/api/v1/products/{id}/usage-metrics", new { currency = price.Currency.Code, price.UnitPrice, price.ValidFrom });
        }).WithMetadata(new RequireScope("catalog:write"));
    }
}
