using BillingOss.App.UseCases;

namespace BillingOss.Web.Api;

/// <summary>La confirmación de la adopción: el código de autorización que el fisco informa para el número propuesto.</summary>
public sealed record AdoptRequest(string AuthorizationCode);

public sealed record SeriesAdjustRequest(long LastNumber, string Reason, DateOnly? LastDate = null);

/// <summary>
/// Operaciones del staff sobre la emisión fiscal (sección 7.1 del modelo de dominio), con los permisos fiscal:read y
/// fiscal:write. Las que cambian algo quedan en la auditoría con sus datos.
/// </summary>
internal static class FiscalApi
{
    public static void MapFiscal(this RouteGroupBuilder api)
    {
        api.MapGet("/fiscal/emissions/{documentId:long}", async (long documentId, [Microsoft.AspNetCore.Mvc.FromServices] FiscalStaffService staff, CancellationToken ct) =>
            Results.Ok(await staff.Show(documentId, ct)))
            .WithMetadata(new RequireScope("fiscal:read"));

        api.MapPost("/fiscal/emissions/{documentId:long}/adopt", async (long documentId, AdoptRequest r, [Microsoft.AspNetCore.Mvc.FromServices] FiscalStaffService staff, CancellationToken ct) =>
        {
            var fiscal = await staff.Adopt(documentId, r.AuthorizationCode, ct);
            return Results.Ok(new { fiscal.DocumentId, fiscal.PointOfSale, fiscal.TypeCode, fiscal.Number, fiscal.Date, fiscal.AuthorizationCode, fiscal.AuthorizationDue });
        }).WithMetadata(new RequireScope("fiscal:write"));

        api.MapPost("/fiscal/emissions/{documentId:long}/retry", async (long documentId, [Microsoft.AspNetCore.Mvc.FromServices] FiscalStaffService staff, CancellationToken ct) =>
            Results.Accepted(value: new { status = await staff.Retry(documentId, ct) }))
            .WithMetadata(new RequireScope("fiscal:write"));

        api.MapGet("/fiscal/series", async ([Microsoft.AspNetCore.Mvc.FromServices] FiscalStaffService staff, CancellationToken ct) => Results.Ok(await staff.Series(ct)))
            .WithMetadata(new RequireScope("fiscal:read"));

        api.MapPost("/fiscal/series/{pointOfSale:int}/{typeCode:int}", async (int pointOfSale, int typeCode, SeriesAdjustRequest r, [Microsoft.AspNetCore.Mvc.FromServices] FiscalStaffService staff, CancellationToken ct) =>
            Results.Ok(await staff.AdjustSeries(pointOfSale, typeCode, r.LastNumber, r.LastDate, r.Reason, ct)))
            .WithMetadata(new RequireScope("fiscal:write"));
    }
}
