using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

/// <summary>
/// Catálogo y cambios de servicios en la API propia (D-11, diseno/catalogo.md): leer y editar grupos, productos con sus
/// precios, grupos de opciones, cupones y paquetes, y pedir, calcular y cancelar cambios de plan o de opciones.
/// </summary>
internal static class CatalogApi
{
    public static void MapCatalog(this RouteGroupBuilder api)
    {
        api.MapGet("/product-groups", async (BillingDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ProductGroups.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToListAsync(ct)))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapPost("/product-groups", async (ProductGroupInput r, CatalogService catalog, CancellationToken ct) =>
        {
            var id = await catalog.SaveGroup(null, r, ct);
            return Results.Created($"/api/v1/product-groups/{id}", new { id });
        }).WithMetadata(new RequireScope("catalog:write"));

        api.MapPut("/product-groups/{id:long}", async (long id, ProductGroupInput r, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(new { id = await catalog.SaveGroup(id, r, ct) }))
            .WithMetadata(new RequireScope("catalog:write"));

        api.MapGet("/products", async (BillingDbContext db, BillingCore core, CancellationToken ct) =>
            Results.Ok(await CatalogQueries.Products(db, core.Today(), null, ct)))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapGet("/products/{id:long}", async (long id, BillingDbContext db, BillingCore core, CancellationToken ct) =>
            (await CatalogQueries.Products(db, core.Today(), [id], ct)).SingleOrDefault() is { } product
                ? Results.Ok(product)
                : Results.Problem(statusCode: 404, title: $"No existe el producto {id}."))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapPost("/products", async (ProductInput r, CatalogService catalog, CancellationToken ct) =>
        {
            var id = await catalog.SaveProduct(null, r, ct);
            return Results.Created($"/api/v1/products/{id}", new { id });
        }).WithMetadata(new RequireScope("catalog:write"));

        // Duplica un producto o un grupo de opciones: el nombre es opcional, la copia del producto queda oculta.
        api.MapPost("/products/{id:long}/copy", async (long id, CopyRequest? r, CatalogService catalog, CancellationToken ct) =>
        {
            var copy = await catalog.CopyProduct(id, r?.Name, ct);
            return Results.Created($"/api/v1/products/{copy}", new { id = copy });
        }).WithMetadata(new RequireScope("catalog:write"));

        api.MapPost("/option-groups/{id:long}/copy", async (long id, CopyRequest? r, CatalogService catalog, CancellationToken ct) =>
        {
            var copy = await catalog.CopyOptionGroup(id, r?.Name, ct);
            return Results.Created($"/api/v1/option-groups/{copy}", new { id = copy });
        }).WithMetadata(new RequireScope("catalog:write"));

        api.MapPut("/products/{id:long}", async (long id, ProductInput r, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(new { id = await catalog.SaveProduct(id, r, ct) }))
            .WithMetadata(new RequireScope("catalog:write"));

        api.MapGet("/option-groups", async (BillingDbContext db, CancellationToken ct) =>
            Results.Ok(await CatalogQueries.OptionGroups(db, null, ct)))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapGet("/option-groups/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
            (await CatalogQueries.OptionGroups(db, [id], ct)).SingleOrDefault() is { } group
                ? Results.Ok(group)
                : Results.Problem(statusCode: 404, title: $"No existe el grupo de opciones {id}."))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapPost("/option-groups", async (OptionGroupInput r, CatalogService catalog, CancellationToken ct) =>
        {
            var id = await catalog.SaveOptionGroup(null, r, ct);
            return Results.Created($"/api/v1/option-groups/{id}", new { id });
        }).WithMetadata(new RequireScope("catalog:write"));

        api.MapPut("/option-groups/{id:long}", async (long id, OptionGroupInput r, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(new { id = await catalog.SaveOptionGroup(id, r, ct) }))
            .WithMetadata(new RequireScope("catalog:write"));

        api.MapGet("/coupons", async (BillingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Coupons.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct)).Select(c => new
            {
                c.Id,
                c.Code,
                c.Kind,
                c.Value,
                currency = c.Currency?.Code,
                c.Recurring,
                c.RecurringCycles,
                c.ProductIds,
                c.Cycles,
                c.StartsOn,
                c.ExpiresOn,
                c.MaxUses,
                c.Uses,
                c.OncePerAccount,
                c.NewAccountsOnly,
                c.ExistingAccountsOnly,
                c.OncePerOrder,
                c.Active,
                c.Notes,
            })))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapPost("/coupons", async (CouponInput r, CatalogService catalog, CancellationToken ct) =>
        {
            var id = await catalog.SaveCoupon(null, r, ct);
            return Results.Created($"/api/v1/coupons/{id}", new { id });
        }).WithMetadata(new RequireScope("catalog:write"));

        api.MapPut("/coupons/{id:long}", async (long id, CouponInput r, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(new { id = await catalog.SaveCoupon(id, r, ct) }))
            .WithMetadata(new RequireScope("catalog:write"));

        api.MapGet("/bundles", async (BillingDbContext db, BillingCore core, CancellationToken ct) =>
            Results.Ok(await CatalogQueries.Bundles(db, core.Today(), null, ct)))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapGet("/bundles/{id:long}", async (long id, BillingDbContext db, BillingCore core, CancellationToken ct) =>
            (await CatalogQueries.Bundles(db, core.Today(), [id], ct)).SingleOrDefault() is { } bundle
                ? Results.Ok(bundle)
                : Results.Problem(statusCode: 404, title: $"No existe el paquete {id}."))
            .WithMetadata(new RequireScope("catalog:read"));

        api.MapPost("/bundles", async (BundleInput r, CatalogService catalog, CancellationToken ct) =>
        {
            var id = await catalog.SaveBundle(null, r, ct);
            return Results.Created($"/api/v1/bundles/{id}", new { id });
        }).WithMetadata(new RequireScope("catalog:write"));

        api.MapPut("/bundles/{id:long}", async (long id, BundleInput r, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(new { id = await catalog.SaveBundle(id, r, ct) }))
            .WithMetadata(new RequireScope("catalog:write"));

        // El servicio con lo elegido de cada opción y su cambio pendiente, si lo tiene.
        api.MapGet("/services/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
        {
            var s = await db.Services.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null)
            {
                return Results.Problem(statusCode: 404, title: $"No existe el servicio {id}.");
            }

            var options = await CatalogQueries.Chosen(db, id, ct);
            var pending = await db.ServiceChanges.AsNoTracking().Where(c => c.ServiceId == id && c.Status == ServiceChangeStatus.Pending).Select(c => (long?)c.Id).SingleOrDefaultAsync(ct);
            return Results.Ok(new
            {
                s.Id,
                s.AccountId,
                s.ProductId,
                s.ParentServiceId,
                s.Label,
                s.Status,
                cycle = s.Cycle.Code,
                currency = s.Currency.Code,
                s.UnitPrice,
                s.Quantity,
                // Un descuento que se terminó ya no se muestra: no va en lo que se factura.
                discount_percent = s.DiscountCyclesLeft == 0 ? null : s.DiscountPercent,
                discount_amount = s.DiscountCyclesLeft == 0 ? null : s.DiscountAmount,
                discount_cycles_left = s.DiscountCyclesLeft == 0 ? null : s.DiscountCyclesLeft,
                s.CouponId,
                s.TrialEndsOn,
                s.NextDueDate,
                s.PaidThrough,
                options = options.Select(o => new { option_id = o.Option.Id, option = o.Option.Name, value_id = o.Value.Id, value = o.Value.Name, o.Quantity }),
                pending_change_id = pending,
            });
        }).WithMetadata(new RequireScope("services:read"));

        // Calcula el cambio sin hacerlo: precio nuevo y lo que se cobraría o acreditaría hoy.
        api.MapPost("/services/{id:long}/changes/preview", async (long id, ChangeRequest r, ServiceChangeService changes, CancellationToken ct) =>
            Results.Ok(await changes.Preview(id, r, ct: ct)))
            .WithMetadata(new RequireScope("services:read"));

        api.MapPost("/services/{id:long}/changes", async (long id, ChangeRequest r, ServiceChangeService changes, CancellationToken ct) =>
        {
            var result = await changes.Request(id, r, ct: ct);
            return Results.Created($"/api/v1/service-changes/{result.ChangeId}", result);
        }).WithMetadata(new RequireScope("services:write"));

        api.MapGet("/service-changes/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
            await db.ServiceChanges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } c
                ? Results.Ok(new { c.Id, c.ServiceId, c.Status, c.FromProductId, c.ToProductId, cycle = c.Cycle.Code, c.FromUnitPrice, c.ToUnitPrice, c.Amount, c.DocumentId, c.CreatedAt, c.AppliedAt })
                : Results.Problem(statusCode: 404, title: $"No existe el cambio {id}."))
            .WithMetadata(new RequireScope("services:read"));

        api.MapPost("/service-changes/{id:long}/cancel", async (long id, ServiceChangeService changes, CancellationToken ct) =>
        {
            await changes.Cancel(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("services:write"));
    }
}

/// <summary>El nombre de una copia del catálogo. Sin nombre lleva el del original con "(copia)".</summary>
internal sealed record CopyRequest(string? Name = null);
