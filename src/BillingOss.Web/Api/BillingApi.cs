using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

public sealed record CreditAdjustmentRequest(string Currency, decimal Amount, string Reason);

public sealed record ExchangeRateRequest(string Currency, decimal Rate, DateOnly? Date = null);

public sealed record DocumentsRequest(IReadOnlyList<long> DocumentIds);

public sealed record ItemsRequest(IReadOnlyList<long>? ItemIds = null);

public sealed record ConvertQuoteRequest(QuoteTarget To);

public sealed record ExemptionsRequest(bool NoLateFees, bool NoReminders);

public sealed record TopUpRequest(string Currency, decimal Amount, long InstanceId);

/// <summary>
/// Facturación avanzada en la API propia (D-11): facturas con renglones propios, saldo a favor, ítems facturables,
/// presupuestos, recargos y cotizaciones. Los mismos casos de uso que el admin.
/// </summary>
internal static class BillingApi
{
    public static void MapBillingApi(this RouteGroupBuilder api)
    {
        api.MapPost("/invoices", async (ManualInvoiceInput r, InvoiceService invoices, CancellationToken ct) =>
        {
            var id = await invoices.CreateManual(r, ct);
            return Results.Created($"/api/v1/documents/{id}", new { id });
        }).WithMetadata(new RequireScope("documents:write"));

        api.MapPost("/invoices/{id:long}/issue", async (long id, InvoiceService invoices, CancellationToken ct) =>
        {
            await invoices.Issue(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("documents:write"));

        // Solo borradores manuales: un documento emitido no se borra (D-11).
        api.MapDelete("/invoices/{id:long}", async (long id, InvoiceService invoices, CancellationToken ct) =>
        {
            await invoices.Discard(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("documents:write"));

        api.MapGet("/accounts/{id:long}/credit", async (long id, BillingDbContext db, CancellationToken ct) =>
        {
            if (!await db.Accounts.AnyAsync(a => a.Id == id, ct))
            {
                return Results.Problem(statusCode: 404, title: $"No existe la cuenta {id}.");
            }

            var balances = await db.CreditBalances.AsNoTracking().Where(b => b.AccountId == id).OrderBy(b => b.Currency).ToListAsync(ct);
            var entries = await db.CreditEntries.AsNoTracking().Where(e => e.AccountId == id).OrderByDescending(e => e.Id).Take(500).ToListAsync(ct);
            return Results.Ok(new
            {
                balances = balances.Select(b => new { currency = b.Currency.Code, b.Balance }),
                entries = entries.Select(e => new { e.Id, currency = e.Currency.Code, e.Amount, e.Reason, e.PaymentId, e.DocumentId, e.Actor, e.CreatedAt }),
            });
        }).WithMetadata(new RequireScope("payments:read"));

        api.MapPost("/accounts/{id:long}/credit/adjustments", async (long id, CreditAdjustmentRequest r, InvoiceService invoices, CancellationToken ct) =>
        {
            var entryId = await invoices.AdjustCredit(id, r.Currency, r.Amount, r.Reason, ct);
            return Results.Created($"/api/v1/accounts/{id}/credit", new { id = entryId });
        }).WithMetadata(new RequireScope("credit:write"));

        api.MapPost("/accounts/{id:long}/credit/apply", async (long id, DocumentsRequest r, InvoiceService invoices, CancellationToken ct) =>
            Results.Ok(await invoices.ApplyCredit(id, r.DocumentIds ?? [], ct)))
            .WithMetadata(new RequireScope("payments:write"));

        api.MapGet("/accounts/{id:long}/billable-items", async (long id, BillingDbContext db, CancellationToken ct) =>
            Results.Ok(await db.BillableItems.AsNoTracking().Where(i => i.AccountId == id).OrderByDescending(i => i.Id).Take(500)
                .Select(i => new { i.Id, i.Description, i.Quantity, i.UnitPrice, currency = i.Currency.Code, i.Treatment, i.ServiceId, i.InvoiceOn, i.Status, i.DocumentId, i.CreatedAt })
                .ToListAsync(ct)))
            .WithMetadata(new RequireScope("documents:read"));

        api.MapPost("/accounts/{id:long}/billable-items", async (long id, BillableItemInput r, BillableItemService items, CancellationToken ct) =>
        {
            var itemId = await items.Add(id, r, ct);
            return Results.Created($"/api/v1/accounts/{id}/billable-items", new { id = itemId });
        }).WithMetadata(new RequireScope("documents:write"));

        // Sin IDs factura todos los pendientes de la cuenta, aunque su fecha sea posterior.
        api.MapPost("/accounts/{id:long}/billable-items/invoice", async (long id, ItemsRequest? r, BillableItemService items, CancellationToken ct) =>
            Results.Ok(new { document_ids = await items.InvoiceNow(id, r?.ItemIds, ct) }))
            .WithMetadata(new RequireScope("documents:write"));

        api.MapPost("/billable-items/{id:long}/cancel", async (long id, BillableItemService items, CancellationToken ct) =>
        {
            await items.Cancel(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("documents:write"));

        // El recargo por mora a pedido del staff, sin esperar a la corrida: el mismo que esta, una sola vez por factura.
        api.MapPost("/invoices/{id:long}/late-fee", async (long id, DunningService dunning, CancellationToken ct) =>
            await dunning.ChargeLateFee(id, ct) is { } noteId
                ? Results.Created($"/api/v1/documents/{noteId}", new { id = noteId })
                : Results.Problem(statusCode: 422, title: "No corresponde un recargo: la factura no venció, no se debe, ya tiene el suyo, la cuenta está exenta o el recargo da cero."))
            .WithMetadata(new RequireScope("documents:write"));

        api.MapPost("/accounts/{id:long}/billing-exemptions", async (long id, ExemptionsRequest r, InvoiceService invoices, CancellationToken ct) =>
        {
            await invoices.SetExemptions(id, r.NoLateFees, r.NoReminders, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("accounts:write"));

        // Carga de saldo a favor por una pasarela: devuelve la URL a la que va el navegador del cliente.
        api.MapPost("/accounts/{id:long}/credit/top-up", async (long id, TopUpRequest r, [Microsoft.AspNetCore.Mvc.FromServices] GatewayPayments gateways, CancellationToken ct) =>
            Results.Ok(await gateways.TopUp(id, r.Currency, r.Amount, r.InstanceId, ct)))
            .WithMetadata(new RequireScope("payments:write"));

        api.MapGet("/accounts/{id:long}/quotes", async (long id, BillingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Quotes.AsNoTracking().Where(q => q.AccountId == id).OrderByDescending(q => q.Id).Take(500).ToListAsync(ct)).Select(Quote)))
            .WithMetadata(new RequireScope("documents:read"));

        api.MapGet("/quotes/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
            await db.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, ct) is { } q
                ? Results.Ok(Quote(q))
                : Results.Problem(statusCode: 404, title: $"No existe el presupuesto {id}."))
            .WithMetadata(new RequireScope("documents:read"));

        api.MapPost("/quotes", async (QuoteInput r, QuoteService quotes, CancellationToken ct) =>
        {
            var id = await quotes.Create(r, ct);
            return Results.Created($"/api/v1/quotes/{id}", new { id });
        }).WithMetadata(new RequireScope("documents:write"));

        // Un presupuesto no es un documento emitido: abierto, se reemplaza entero.
        api.MapPut("/quotes/{id:long}", async (long id, QuoteInput r, QuoteService quotes, CancellationToken ct) =>
        {
            await quotes.Update(id, r, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("documents:write"));

        api.MapDelete("/quotes/{id:long}", async (long id, QuoteService quotes, CancellationToken ct) =>
        {
            await quotes.Delete(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("documents:write"));

        api.MapPost("/quotes/{id:long}/send", async (long id, QuoteService quotes, CancellationToken ct) =>
        {
            await quotes.Send(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("documents:write"));

        api.MapPost("/quotes/{id:long}/decline", async (long id, QuoteService quotes, CancellationToken ct) =>
        {
            await quotes.Decline(id, ct: ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("documents:write"));

        api.MapPost("/quotes/{id:long}/convert", async (long id, ConvertQuoteRequest r, QuoteService quotes, CancellationToken ct) =>
            Results.Ok(await quotes.Convert(id, r.To, ct)))
            .WithMetadata(new RequireScope("documents:write"));

        // Un borrador nuevo con lo mismo, también desde uno cerrado.
        api.MapPost("/quotes/{id:long}/duplicate", async (long id, QuoteService quotes, CancellationToken ct) =>
        {
            var copy = await quotes.Duplicate(id, ct);
            return Results.Created($"/api/v1/quotes/{copy}", new { id = copy });
        }).WithMetadata(new RequireScope("documents:write"));

        // Cotizaciones: cualquier llave las lee. Cargar una a mano o pedir la del banco pide rates:write.
        api.MapGet("/exchange-rates", async (string? currency, ExchangeRateService rates, CancellationToken ct) =>
        {
            var code = InvoiceService.ParseCurrency(currency ?? "USD");
            var current = code == rates.Local ? null : await rates.Current(code, rates.Local, ct);
            return Results.Ok(new { current = current is null ? null : Rate(current.Rate), recent = (await rates.Recent(30, ct)).Select(Rate) });
        });

        api.MapPost("/exchange-rates", async (ExchangeRateRequest r, ExchangeRateService rates, CancellationToken ct) =>
        {
            var id = await rates.AddManual(r.Currency, r.Rate, r.Date, ct);
            return Results.Created("/api/v1/exchange-rates", new { id });
        }).WithMetadata(new RequireScope("rates:write"));

        api.MapPost("/exchange-rates/refresh", async (JobQueue jobs, TimeProvider time, CancellationToken ct) =>
        {
            await jobs.Enqueue(JobKinds.ExchangeRates, new { manual = true }, ExchangeRateService.Entity, $"{JobKinds.ExchangeRates}:{time.GetUtcNow():yyyy-MM-dd'T'HH:mm}", maxAttempts: 3, ct: ct);
            return Results.Accepted("/api/v1/exchange-rates");
        }).WithMetadata(new RequireScope("rates:write"));
    }

    private static object Quote(Quote q) => new
    {
        q.Id,
        q.AccountId,
        q.Subject,
        currency = q.Currency.Code,
        q.ValidUntil,
        q.Status,
        q.Total,
        q.Notes,
        lines = q.Lines,
        q.SentAt,
        q.ClosedAt,
        q.DocumentId,
        q.OrderId,
        q.CreatedAt,
    };

    private static object Rate(ExchangeRate r) => new { r.Id, currency = r.Currency.Code, quote = r.Quote.Code, r.Rate, r.Date, r.Source, r.Actor, r.CreatedAt };
}
