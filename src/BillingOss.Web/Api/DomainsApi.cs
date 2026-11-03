using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

public sealed record DomainCheckRequest(IReadOnlyList<string> Names, string Currency = "ARS");

public sealed record DomainOrderRequest(long AccountId, string Name, int Years = 1, IReadOnlyList<string>? Nameservers = null, string? AuthCode = null, JsonObject? Registrant = null, string? PaymentMethod = null, bool Privacy = false, bool DnsManagement = false, bool EmailForwarding = false);

public sealed record DnsRequest(IReadOnlyList<DnsRecord>? Records);

public sealed record ForwardsRequest(IReadOnlyList<EmailForward>? Forwards);

public sealed record PrivacyRequest(bool Enabled);

public sealed record OptionRequest(string Option, bool Enabled);

public sealed record OptionPriceRequest(string Option, string Currency, decimal Price, DateOnly ValidFrom);

/// <summary>Las opciones del dominio que se ofrecen en la extensión, por su nombre: dns, email_forwarding y privacy.</summary>
public sealed record TldOptionsRequest(string Tld, IReadOnlyList<string> Offered);

public sealed record NameserversRequest(IReadOnlyList<string> Nameservers);

public sealed record LockRequest(bool Locked);

public sealed record AutoRenewRequest(bool AutoRenew);

public sealed record ContactsRequest(JsonObject Registrant);

public sealed record RetryRequest(string? AuthCode = null);

public sealed record DomainCancelRequest(string Reason);

public sealed record TldPriceRequest(string Tld, long InstanceId, string Currency, int Years, decimal? Register, decimal? Renew, decimal? Transfer, DateOnly ValidFrom);

public sealed record TldCsvRequest(long InstanceId, string Currency, string Csv, DateOnly? ValidFrom = null, bool Apply = false);

public sealed record TldImportRequest(long InstanceId, string Currency, decimal ExchangeRate = 1m, decimal MarginPercent = 0m, decimal TaxPercent = 0m, decimal RoundTo = 1m, DateOnly? ValidFrom = null, bool Apply = false);

/// <summary>
/// Dominios y precios por TLD en la API propia (D-11). Usa los mismos casos de uso que el portal y el admin. Lo que va
/// al registrar se encola y responde 202: el resultado se ve después en el dominio y en sus operaciones. El código EPP
/// nunca sale por la API.
/// </summary>
internal static class DomainsApi
{
    public static void MapDomains(this RouteGroupBuilder api)
    {
        // Por estado o cuenta: el staff ve acá, por ejemplo, los que vencieron sin renovar.
        api.MapGet("/domains", async (string? status, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "account_id")] long? accountId, BillingDbContext db, CancellationToken ct) =>
        {
            var query = db.Domains.AsNoTracking();
            if (status is not null)
            {
                if (!Enum.TryParse<DomainStatus>(status, true, out var parsed))
                {
                    return Results.Problem(statusCode: 400, title: $"Estado desconocido: {status}.");
                }

                query = query.Where(d => d.Status == parsed);
            }

            if (accountId is { } account)
            {
                query = query.Where(d => d.AccountId == account);
            }

            return Results.Ok(await query.OrderBy(d => d.Id)
                .Select(d => new { d.Id, d.AccountId, d.Name, d.Status, d.ExpiresOn, d.PaidThrough, d.NextDueDate, d.AutoRenew, d.RegistrarInstanceId, currency = d.Currency.Code, d.RecurringAmount })
                .ToListAsync(ct));
        }).WithMetadata(new RequireScope("domains:read"));

        api.MapGet("/domains/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
        {
            var d = await db.Domains.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (d is null)
            {
                return Results.Problem(statusCode: 404, title: $"No existe el dominio {id}.");
            }

            var operations = (await db.DomainOperations.AsNoTracking().Where(o => o.DomainId == id).OrderBy(o => o.Id).ToListAsync(ct))
                .Select(o => new { o.Id, o.Operation, o.Status, o.CreatedAt, o.FinishedAt, o.LastError, in_progress = o.PendingOperation != null });
            return Results.Ok(new
            {
                d.Id,
                d.AccountId,
                d.Name,
                d.Status,
                d.RegistryStatus,
                d.RegisteredOn,
                d.ExpiresOn,
                d.PaidThrough,
                d.NextDueDate,
                d.Years,
                d.AutoRenew,
                d.Locked,
                d.SyncedAt,
                d.RegistrarInstanceId,
                currency = d.Currency.Code,
                d.RecurringAmount,
                nameservers = JsonNode.Parse(d.Nameservers),
                has_auth_code = d.AuthCode is not null,
                options = DomainOptions.All.Where(d.HasOption),
                option_prices = JsonNode.Parse(d.OptionPrices),
                operations,
            });
        }).WithMetadata(new RequireScope("domains:read"));

        api.MapPost("/domains/check", async (DomainCheckRequest r, DomainSearch search, CancellationToken ct) =>
            Results.Ok((await search.Check(r.Names ?? [], new Currency(r.Currency), ct))
                .Select(a => new { a.Name, a.Available, a.Source, a.Reason, a.Premium, price = a.Offer is { } o ? new { o.Register, o.Renew, o.Transfer, currency = o.Currency.Code, years = o.Years } : null })))
            .WithMetadata(new RequireScope("domains:read"));

        api.MapPost("/domains/register", async (DomainOrderRequest r, DomainOrderService orders, CancellationToken ct) =>
            Placed(await orders.Register(r.AccountId, Order(r), ct)))
            .WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/transfer", async (DomainOrderRequest r, DomainOrderService orders, CancellationToken ct) =>
            Placed(await orders.Transfer(r.AccountId, Order(r), ct)))
            .WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/nameservers", async (long id, NameserversRequest r, DomainService domains, CancellationToken ct) =>
        {
            await domains.SetNameservers(id, r.Nameservers ?? [], ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/lock", async (long id, LockRequest r, DomainService domains, CancellationToken ct) =>
        {
            await domains.SetLock(id, r.Locked, ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        // Pide el código al registrar. Queda cifrado en el dominio y se muestra en el portal o el admin, con auditoría.
        api.MapPost("/domains/{id:long}/auth-code", async (long id, DomainService domains, CancellationToken ct) =>
        {
            await domains.RequestAuthCode(id, ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/contacts", async (long id, ContactsRequest r, DomainService domains, CancellationToken ct) =>
        {
            await domains.SetContacts(id, r.Registrant ?? [], ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/auto-renew", async (long id, AutoRenewRequest r, DomainService domains, CancellationToken ct) =>
        {
            await domains.SetAutoRenew(id, r.AutoRenew, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/renew", async (long id, DomainService domains, CancellationToken ct) =>
            Results.Ok(new { invoice_id = await domains.RenewNow(id, ct) }))
            .WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/retry", async (long id, RetryRequest? r, DomainService domains, CancellationToken ct) =>
        {
            await domains.Retry(id, r?.AuthCode, ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/cancel", async (long id, DomainCancelRequest r, DomainService domains, CancellationToken ct) =>
        {
            await domains.CancelOrder(id, r.Reason, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("domains:write"));

        api.MapPost("/domains/{id:long}/sync", async (long id, DomainService domains, CancellationToken ct) =>
        {
            await domains.SyncNow(id, ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        // La zona y los reenvíos se leen del registrar en el momento y se cambian enteros, por la cola.
        api.MapGet("/domains/{id:long}/dns", async (long id, DomainService domains, CancellationToken ct) =>
            Results.Ok(new { records = await domains.Dns(id, ct) }))
            .WithMetadata(new RequireScope("domains:read"));

        api.MapPut("/domains/{id:long}/dns", async (long id, DnsRequest r, DomainService domains, CancellationToken ct) =>
        {
            await domains.SetDns(id, r.Records ?? [], ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        api.MapGet("/domains/{id:long}/email-forwards", async (long id, DomainService domains, CancellationToken ct) =>
            Results.Ok(new { forwards = await domains.Forwards(id, ct) }))
            .WithMetadata(new RequireScope("domains:read"));

        api.MapPut("/domains/{id:long}/email-forwards", async (long id, ForwardsRequest r, DomainService domains, CancellationToken ct) =>
        {
            await domains.SetForwards(id, r.Forwards ?? [], ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("domains:write"));

        // Con precio, prender la protección la contrata: devuelve la factura y se pide al registrar cuando se paga.
        api.MapPost("/domains/{id:long}/privacy", async (long id, PrivacyRequest r, DomainService domains, CancellationToken ct) =>
            await domains.SetPrivacy(id, r.Enabled, ct) is { } invoice ? Results.Ok(new { invoice_id = invoice }) : Results.Accepted())
            .WithMetadata(new RequireScope("domains:write"));

        // Cualquier opción (dns, email_forwarding o privacy): sin precio se prende sola, con precio devuelve la factura.
        api.MapPost("/domains/{id:long}/options", async (long id, OptionRequest r, DomainService domains, CancellationToken ct) =>
            await domains.SetOption(id, r.Option ?? string.Empty, r.Enabled, ct) is { } invoice ? Results.Ok(new { invoice_id = invoice }) : Results.Accepted())
            .WithMetadata(new RequireScope("domains:write"));

        api.MapGet("/domain-option-prices", async (string? currency, TldPricing pricing, CancellationToken ct) =>
            Results.Ok(await pricing.OptionPrices(new Currency(currency ?? "ARS"), ct)))
            .WithMetadata(new RequireScope("domains:read"));

        api.MapPut("/domain-option-prices", async (OptionPriceRequest r, TldPricing pricing, CancellationToken ct) =>
        {
            await pricing.SetOptionPrice(r.Option ?? string.Empty, new Currency(r.Currency), r.Price, r.ValidFrom, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("pricing:write"));

        // Qué opciones se ofrecen en cada extensión. Las que no figuran ofrecen todas.
        api.MapGet("/tld-options", async (TldPricing pricing, CancellationToken ct) =>
            Results.Ok((await pricing.OptionsByTld(ct)).Select(t => new { t.Tld, offered = DomainOptions.All.Where(t.Offers) })))
            .WithMetadata(new RequireScope("domains:read"));

        api.MapPut("/tld-options", async (TldOptionsRequest r, TldPricing pricing, CancellationToken ct) =>
        {
            await pricing.SetTldOptions(r.Tld ?? string.Empty, r.Offered ?? [], ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("pricing:write"));

        api.MapGet("/tld-prices", async (string? currency, TldPricing pricing, CancellationToken ct) =>
            Results.Ok((await pricing.Current(new Currency(currency ?? "ARS"), ct))
                .Select(o => new { o.Tld, instance_id = o.RegistrarInstanceId, currency = o.Currency.Code, o.Years, o.Register, o.Renew, o.Transfer, o.ValidFrom })))
            .WithMetadata(new RequireScope("domains:read"));

        api.MapPut("/tld-prices", async (TldPriceRequest r, TldPricing pricing, CancellationToken ct) =>
        {
            await pricing.Set(r.Tld, r.InstanceId, new Currency(r.Currency), r.Years, r.Register, r.Renew, r.Transfer, r.ValidFrom, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("pricing:write"));

        // Sin apply devuelve la propuesta para revisarla. Con apply la guarda desde valid_from (hoy si no viene).
        api.MapPost("/tld-prices/import", async (TldImportRequest r, TldPricing pricing, BillingCore core, CancellationToken ct) =>
        {
            var rule = new PriceRule(new Currency(r.Currency), r.ExchangeRate, r.MarginPercent, r.TaxPercent, r.RoundTo);
            return Results.Ok(await pricing.ImportFromRegistrar(r.InstanceId, rule, r.ValidFrom ?? core.Today(), r.Apply, ct));
        }).WithMetadata(new RequireScope("pricing:write"));

        // Lo mismo desde un CSV, para los registrars que no publican costos.
        api.MapPost("/tld-prices/csv", async (TldCsvRequest r, TldPricing pricing, BillingCore core, CancellationToken ct) =>
            Results.Ok(await pricing.ImportCsv(r.InstanceId, new Currency(r.Currency), r.Csv, r.ValidFrom ?? core.Today(), r.Apply, ct)))
            .WithMetadata(new RequireScope("pricing:write"));
    }

    private static DomainOrder Order(DomainOrderRequest r) => new(r.Name, r.Years, r.Nameservers, r.AuthCode, r.Registrant, r.PaymentMethod, r.Privacy, r.DnsManagement, r.EmailForwarding);

    private static IResult Placed(PlacedDomainOrder placed) =>
        Results.Created($"/api/v1/domains/{placed.DomainId}", new { domain_id = placed.DomainId, invoice_id = placed.InvoiceId });
}
