using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using BillingOss.App.Documents;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

/// <summary>Permiso que exige un endpoint de la API propia. Sin permiso, el endpoint pide igual una llave válida.</summary>
internal sealed record RequireScope(string? Scope);

/// <summary>
/// Tope de pedidos por API key (D-11), en memoria de cada nodo web. Lo usan la API propia y la compatible después
/// de autenticar.
/// </summary>
internal sealed class ApiKeyLimiter : IDisposable
{
    public const int DefaultPerMinute = 600;

    // La partición lleva el tope: si cambia en la llave, arranca un cupo nuevo en vez de seguir con el viejo.
    private readonly PartitionedRateLimiter<ApiKey> limiter = PartitionedRateLimiter.Create<ApiKey, (long, int)>(key =>
    {
        var limit = key.RequestsPerMinute ?? DefaultPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter((key.Id, limit), _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) });
    });

    public bool TryAcquire(ApiKey key)
    {
        using var lease = limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}

public sealed record AccountRequest(string Name, string Email, string Currency, string Language = "es", string? Phone = null, string? CompanyName = null, TaxIdentityInput? Tax = null);

/// <param name="ClientIp">IP de quien compra: con ella el pedido pasa por la evaluación de fraude, como uno del portal.</param>
public sealed record OrderRequest(long AccountId, IReadOnlyList<OrderItem> Items, string? PaymentMethod = null, string? CouponCode = null, long? BundleId = null, string? ClientIp = null);

public sealed record PaymentRequest(long AccountId, string Method, string ExternalId, string Currency, decimal Amount, IReadOnlyList<long>? DocumentIds = null, decimal? Rate = null);

public sealed record CheckoutRequest(long AccountId, IReadOnlyList<long> DocumentIds, long InstanceId, string? Currency = null);

public sealed record RefundRequest(long InvoiceId, IReadOnlyList<CreditLine> Lines, string Reason);
public sealed record RejectRequest(string Reason);

/// <summary>
/// Cuerpo opcional de una operación del staff: reason es el motivo de la suspensión, que queda en el servicio, e input
/// va al módulo en el input del sobre.
/// </summary>
public sealed record ServiceOperationRequest(string? Reason = null, JsonObject? Input = null);

/// <summary>Cuerpo opcional de una acción del manifiesto: sus parámetros.</summary>
public sealed record ServiceActionRequest(JsonObject? Params = null);

/// <summary>Cuerpo opcional de la baja a fin de período: sin fecha es el próximo vencimiento.</summary>
public sealed record CancelRequest(DateOnly? At = null);

/// <summary>Marcas del servicio que el staff cambia a mano. Lo que no viene no se toca.</summary>
public sealed record ServicePatch(bool? NoAutoSuspend = null);

/// <summary>
/// API propia (D-11): REST con JSON en /api/v1, por casos de uso, con API keys, Problem Details e Idempotency-Key en
/// los POST que crean pedidos. Los pagos ya son idempotentes por método e ID externo.
/// </summary>
internal static class ApiV1
{
    public const string KeyItem = "billingoss.apikey";

    /// <summary>Política del tope por IP de la API propia y la compatible.</summary>
    public const string ClientLimiter = "api";

    public static void MapApiV1(this WebApplication app)
    {
        // La llave se valida en un middleware, después del tope por IP y antes de leer el cuerpo: en minimal APIs el
        // cuerpo se lee antes de los filtros de endpoint.
        app.Use(Authenticate);
        var api = app.MapGroup("/api/v1").WithMetadata(new RequireScope(null)).RequireRateLimiting(ClientLimiter).AddEndpointFilter(ApiAudit.Filter).AddEndpointFilter(MapErrors);

        api.MapPost("/accounts", async (AccountRequest r, AccountService accounts, CancellationToken ct) =>
        {
            var id = await accounts.Create(new AccountInput(r.Name, r.Email, new Currency(r.Currency), r.Language, r.Phone, r.CompanyName, r.Tax), ct);
            return Results.Created($"/api/v1/accounts/{id}", new { id });
        }).WithMetadata(new RequireScope("accounts:write"));

        api.MapGet("/accounts/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
            await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } a
                ? Results.Ok(new { a.Id, a.Name, a.CompanyName, a.Email, a.Phone, a.Language, currency = a.Currency.Code, a.Status })
                : Results.Problem(statusCode: 404, title: $"No existe la cuenta {id}."))
            .WithMetadata(new RequireScope("accounts:read"));

        api.MapGet("/accounts/{id:long}/services", async (long id, BillingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Services.AsNoTracking().Where(s => s.AccountId == id).OrderBy(s => s.Id).ToListAsync(ct))

                // Con precio pactado en otra moneda, unit_price es el de la última factura y la moneda pactada va aparte.
                .Select(s => new { s.Id, s.ProductId, s.Label, s.Status, s.NextDueDate, currency = s.Currency.Code, s.UnitPrice, s.Quantity, s.CancelAt, s.SuspendReason, s.NoAutoSuspend, index_currency = s.IndexCurrency?.Code, s.IndexedUnitPrice })))
            .WithMetadata(new RequireScope("services:read"));

        api.MapGet("/accounts/{id:long}/documents", async (long id, BillingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Documents.AsNoTracking().Where(d => d.AccountId == id && d.IssueDate != null).OrderBy(d => d.Id).ToListAsync(ct))
                .Select(d => new { d.Id, d.Kind, d.Number, d.IssueDate, d.DueDate, currency = d.Currency.Code, d.Total, d.Pending, d.Status })))
            .WithMetadata(new RequireScope("documents:read"));

        // Vuelve a facturar lo que liberó la anulación automática de impagas, aunque el saldo no lo cubra.
        api.MapPost("/accounts/{id:long}/reissue-released", async (long id, BillingRunService billing, CancellationToken ct) =>
            Results.Ok(new { issued = await billing.ReissueReleased(id, onlyIfCovered: false, ct) }))
            .WithMetadata(new RequireScope("documents:write"));

        api.MapGet("/documents/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
        {
            var d = await db.Documents.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (d is null)
            {
                return Results.Problem(statusCode: 404, title: $"No existe el documento {id}.");
            }

            var fiscal = await db.FiscalDocuments.AsNoTracking().Where(f => f.DocumentId == id).Select(f => new { f.TypeCode, f.PointOfSale, f.Number, f.Date, f.AuthorizationCode, f.AuthorizationDue }).ToListAsync(ct);
            return Results.Ok(new
            {
                d.Id,
                d.Kind,
                d.Number,
                d.AccountId,
                currency = d.Currency.Code,
                d.IssueDate,
                d.DueDate,
                d.NetTaxed,
                d.Exempt,
                d.NotTaxed,
                d.Tax,
                d.Total,
                d.Allocated,
                d.Credited,
                d.Pending,
                d.Status,
                lines = d.Lines.Select(l => new { l.Id, l.Kind, l.Description, l.Quantity, l.UnitPrice, l.Amount, l.Rate, l.Net, tax = l.LineTax, l.ServiceId }),
                fiscal,
            });
        }).WithMetadata(new RequireScope("documents:read"));

        api.MapGet("/documents/{id:long}/pdf", async (long id, HttpContext http, DocumentPdfService pdfs, CancellationToken ct) =>
            await pdfs.Get(id, ct) is { } pdf
                ? Portal.PortalDocuments.File(http, pdf)
                : Results.Problem(statusCode: 404, title: $"No existe el documento emitido {id}."))
            .WithMetadata(new RequireScope("documents:read"));

        // La Idempotency-Key vale por API key: la misma clave desde otra llave es otro pedido, y con otro cuerpo da 422.
        api.MapPost("/orders", async (OrderRequest r, HttpContext http, OrderService orders, CancellationToken ct) =>
        {
            var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
            FraudContext? fraud = null;
            if (r.ClientIp is { } clientIp)
            {
                fraud = System.Net.IPAddress.TryParse(clientIp, out var ip) ? new FraudContext(ip) : throw new ArgumentException("client_ip no es una IP.");
            }

            var placed = await orders.PlaceOrder(r.AccountId, r.Items, ct, key, r.PaymentMethod, r.CouponCode, r.BundleId, apiKeyId: ((ApiKey)http.Items[KeyItem]!).Id, fraud: fraud);
            return Results.Created($"/api/v1/orders/{placed.OrderId}", placed);
        }).WithMetadata(new RequireScope("orders:write"));

        // El mismo pedido calculado sin confirmarlo: líneas, descuentos, altas y la factura que saldría hoy.
        api.MapPost("/orders/quote", async (OrderRequest r, OrderService orders, CancellationToken ct) =>
            Results.Ok(await orders.Quote(r.AccountId, r.Items, r.CouponCode, r.BundleId, ct)))
            .WithMetadata(new RequireScope("orders:write"));

        api.MapPost("/payments", async (PaymentRequest r, PaymentService payments, CancellationToken ct) =>
        {
            var id = await payments.Record(new PaymentInput(r.AccountId, $"manual:{r.Method}", r.ExternalId, new Currency(r.Currency), r.Amount, PaymentStatus.Approved, r.DocumentIds, Rate: r.Rate), ct);
            return Results.Ok(new { id });
        }).WithMetadata(new RequireScope("payments:write"));

        // Reembolso del staff (sección 12.4 del modelo): la nota de crédito sale ya, la plata la pide un trabajo.
        api.MapPost("/payments/{id:long}/refunds", async (long id, RefundRequest r, PaymentService payments, CancellationToken ct) =>
        {
            var refundId = await payments.RequestRefund(id, r.InvoiceId, r.Lines, r.Reason, ct);
            return Results.Accepted($"/api/v1/refunds/{refundId}", new { id = refundId });
        }).WithMetadata(new RequireScope("payments:refund"));

        api.MapGet("/refunds/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
            await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } r
                ? Results.Ok(new { r.Id, r.PaymentId, r.Origin, r.Amount, r.Status, r.CreditNoteId, r.ExternalId, r.Uncovered, r.Error, r.CreatedAt, r.UpdatedAt })
                : Results.Problem(statusCode: 404, title: $"No existe el reembolso {id}."))
            .WithMetadata(new RequireScope("payments:read"));

        api.MapPost("/payments/checkout", async (CheckoutRequest r, [Microsoft.AspNetCore.Mvc.FromServices] GatewayPayments gateways, CancellationToken ct) =>
            Results.Ok(await gateways.Checkout(r.AccountId, r.DocumentIds, r.InstanceId, r.Currency, null, ct)))
            .WithMetadata(new RequireScope("payments:write"));

        // Extracto del banco en CSV, con el mapa de columnas de la cuenta (sección 4.6 del diseño de cobros). Cada crédito
        // entra una sola vez, con la referencia del banco como ID externo: reimportar no duplica.
        api.MapPost("/transfer-accounts/{id:long}/statements", async (long id, HttpRequest request, TransferService transfers, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8);
            return Results.Ok(await transfers.Import(id, await reader.ReadToEndAsync(ct), ct));
        }).WithMetadata(new RequireScope("payments:write"));

        // Operaciones del staff sobre un servicio: suspend, unsuspend, terminate y create (reintento de un alta fallida).
        // El cuerpo es opcional: reason es el motivo de la suspensión, que un pago no levanta, e input va al módulo, por
        // ejemplo {"input": {"force": true}} en una baja que no sale sola.
        api.MapPost("/services/{id:long}/{operation}", async (long id, string operation, [Microsoft.AspNetCore.Mvc.FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ServiceOperationRequest? r, ServiceAdmin services, CancellationToken ct) =>
        {
            await services.Request(id, operation, r?.Input, r?.Reason, ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("services:write"));

        // Acciones del manifiesto del módulo (reiniciar, reinstalar, cambiar la contraseña): solo las que admiten staff.
        api.MapPost("/services/{id:long}/actions/{action}", async (long id, string action, [Microsoft.AspNetCore.Mvc.FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ServiceActionRequest? r, ServiceAdmin services, CancellationToken ct) =>
        {
            await services.RunAction(id, action, r?.Params, "staff", ct);
            return Results.Accepted();
        }).WithMetadata(new RequireScope("services:write"));

        // Baja a fin de período y su anulación.
        api.MapPost("/services/{id:long}/cancel", async (long id, CancelRequest? r, ServiceAdmin services, CancellationToken ct) =>
            Results.Ok(new { cancel_at = await services.RequestCancellation(id, r?.At, ct) }))
            .WithMetadata(new RequireScope("services:write"));

        api.MapDelete("/services/{id:long}/cancel", async (long id, ServiceAdmin services, CancellationToken ct) =>
        {
            await services.KeepService(id, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("services:write"));

        api.MapPatch("/services/{id:long}", async (long id, ServicePatch r, ServiceAdmin services, CancellationToken ct) =>
        {
            if (r.NoAutoSuspend is { } exempt)
            {
                await services.SetNoAutoSuspend(id, exempt, ct);
            }

            return Results.NoContent();
        }).WithMetadata(new RequireScope("services:write"));

        api.MapNotificationTemplates();
        api.MapCatalog();
        api.MapFiscal();
        api.MapUpdates();
        api.MapSecurityApi();
        api.MapTickets();
        api.MapUsage();
        api.MapAffiliatesApi();
        api.MapContentApi();

        // Admin de las tareas del staff que abre el módulo manual. Cerrar una reprograma el trabajo que la espera.
        api.MapGet("/staff-tasks", async (BillingDbContext db, CancellationToken ct) =>
            Results.Ok((await db.StaffTasks.AsNoTracking().Where(t => t.Status == StaffTaskStatus.Open).OrderBy(t => t.Id).ToListAsync(ct))
                .Select(t => new { t.Id, t.ModuleInstanceId, t.Kind, t.Operation, t.Subject, t.CreatedAt, details = JsonNode.Parse(t.Details), input = JsonNode.Parse(t.Input) })))
            .WithMetadata(new RequireScope("tasks:read"));

        api.MapPost("/staff-tasks/{id:long}/complete", async (long id, StaffTaskClose r, StaffTaskService tasks, CancellationToken ct) =>
        {
            await tasks.Complete(id, r, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("tasks:write"));

        api.MapPost("/staff-tasks/{id:long}/reject", async (long id, RejectRequest r, StaffTaskService tasks, CancellationToken ct) =>
        {
            await tasks.Reject(id, r.Reason, ct);
            return Results.NoContent();
        }).WithMetadata(new RequireScope("tasks:write"));

        api.MapDomains();

        // Facturación avanzada: facturas manuales, saldo a favor, ítems, presupuestos, recargos y cotizaciones.
        api.MapBillingApi();
    }

    /// <summary>Los permisos que exige algún endpoint, también los de otros archivos: los que se ofrecen al emitir una llave.</summary>
    public static IReadOnlyList<string> Scopes(EndpointDataSource endpoints) =>
        [.. endpoints.Endpoints.Select(e => e.Metadata.GetMetadata<RequireScope>()?.Scope).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Todo endpoint del grupo lleva RequireScope: el del grupo, sin permiso, o el propio, que va último.</summary>
    private static async Task Authenticate(HttpContext http, RequestDelegate next)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<RequireScope>() is not { } required)
        {
            await next(http);
            return;
        }

        var header = http.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
        if (!ApiKeyService.TryParseToken(presented, out var prefix, out var secret))
        {
            await Results.Problem(statusCode: 401, title: "Falta la API key.").ExecuteAsync(http);
            return;
        }

        var key = await http.RequestServices.GetRequiredService<ApiKeyService>().Authenticate(prefix, secret, http.Connection.RemoteIpAddress, http.RequestAborted);
        var denied = key switch
        {
            null => Results.Problem(statusCode: 401, title: "API key inválida."),
            _ when required.Scope is not null && !key.Allows(required.Scope) => Results.Problem(statusCode: 403, title: $"La API key no tiene el permiso {required.Scope}."),
            _ when !http.RequestServices.GetRequiredService<ApiKeyLimiter>().TryAcquire(key) => Results.Problem(statusCode: 429, title: "La API key pasó su tope de pedidos por minuto."),
            _ => null,
        };
        if (denied is not null)
        {
            await denied.ExecuteAsync(http);
            return;
        }

        http.Items[KeyItem] = key;
        Actor.Current = new Actor(ActorKind.Api, ApiKeyService.Describe(key!), http.Connection.RemoteIpAddress);
        await next(http);
    }

    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (Exception ex) when (Status(ex) is { } status)
        {
            return Results.Problem(statusCode: status, title: ex.Message);
        }
    }

    /// <summary>El código HTTP de un error propio. Los demás siguen al manejador de excepciones: log y 500 con texto fijo.</summary>
    private static int? Status(Exception ex) => !PublicErrors.IsOwn(ex) ? null : ex switch
    {
        ValidationException => 422,
        Updates.UpdateException => 409,
        KeyNotFoundException => 404,
        InvalidOperationException => 409,
        PermanentFailureException => 502,
        RetryLaterException => 503,
        ArgumentException => 400,
        _ => null,
    };
}

/// <summary>
/// Qué texto de una excepción puede ver un integrador. Solo el de las que lanza el código de billing-oss, que está
/// escrito para mostrarse. Las del framework, la base o la red pueden traer detalles internos, así que no se
/// atrapan: el manejador de excepciones las deja en el log y responde un texto fijo.
/// </summary>
public static class PublicErrors
{
    /// <summary>La lanzó código propio: el primer marco de su pila es de un ensamblado BillingOss.</summary>
    public static bool IsOwn(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.TargetSite?.DeclaringType?.Assembly.GetName().Name?.StartsWith("BillingOss.", StringComparison.Ordinal) == true;
    }
}
