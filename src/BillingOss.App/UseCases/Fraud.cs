using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>Quién hace un pedido, para evaluar el fraude: la IP, el navegador y el usuario del portal si se sabe.</summary>
public sealed record FraudContext(IPAddress? Ip, string? UserAgent = null, long? UserId = null)
{
    /// <summary>
    /// El de la sesión del portal: el actor es un cliente con usuario. Lo que llega sin sesión, el staff y la API sin
    /// IP del cliente no se evalúan.
    /// </summary>
    public static FraudContext? FromActor(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Kind == ActorKind.Customer && actor.Id.StartsWith("user:", StringComparison.Ordinal)
            && long.TryParse(actor.Id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? new FraudContext(actor.Ip, null, userId)
            : null;
    }
}

/// <summary>
/// Evaluación de fraude al confirmar un pedido (diseno/fraude-y-acceso.md, sección 3.2). Corre dentro de la transacción
/// de <see cref="OrderService.PlaceOrder"/>, sin red: las reglas propias mandan a revisión enseguida, y si hace falta el
/// módulo queda pendiente con su trabajo. En los dos casos el pedido queda retenido.
/// </summary>
public sealed class FraudScreening(BillingDbContext db, SecuritySettingsStore settings, AccessGuard guard, JobQueue jobs, AuditLog audit, TimeProvider time)
{
    public async Task<FraudCheck?> Start(Order order, Account account, FraudContext? context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(account);
        context ??= FraudContext.FromActor(Actor.Current);
        if (context is null)
        {
            return null;
        }

        var s = await settings.Get(ct).ConfigureAwait(false);
        var user = context.UserId is { } id
            ? await db.Set<PortalUser>().AsNoTracking().Where(u => u.Id == id).Select(u => new { u.Email, u.EmailConfirmed }).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var reasons = await guard.BlockReasons(context.Ip, [account.Email, user?.Email], ct).ConfigureAwait(false);
        if (s.RequireVerifiedEmail && user is { EmailConfirmed: false })
        {
            reasons.Add(new FraudReason("email.unverified", "Quien hizo el pedido no verificó su correo."));
        }

        var useModule = reasons.Count == 0 && s.FraudEnabled && s.FraudInstanceId is not null
            && !(s.FraudSkipExisting && await db.Services.AnyAsync(sv => sv.AccountId == account.Id && (sv.Status == ServiceStatus.Active || sv.Status == ServiceStatus.Suspended), ct).ConfigureAwait(false));
        if (reasons.Count == 0 && !useModule)
        {
            return null;
        }

        var check = new FraudCheck(order.Id, account.Id, reasons, context.Ip is null ? null : Networks.Normalize(context.Ip), context.UserId, useModule ? s.FraudInstanceId : null, time.GetUtcNow());
        db.Set<FraudCheck>().Add(check);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var entity = string.Create(CultureInfo.InvariantCulture, $"order:{order.Id}");
        if (useModule)
        {
            await jobs.Enqueue(JobKinds.FraudScreen, new FraudScreenJob(order.Id, context.UserAgent), entity, $"{JobKinds.FraudScreen}:{order.Id}", maxAttempts: FraudScreenHandler.MaxAttempts, ct: ct).ConfigureAwait(false);
        }
        else
        {
            await audit.Record("fraud.review", entity, new { reasons = reasons.Select(r => r.Code) }, ct).ConfigureAwait(false);
        }

        return check;
    }
}

public sealed record FraudScreenJob(long OrderId, string? UserAgent);

/// <summary>
/// El resultado de una decisión. Al rechazar, las notas de crédito de las facturas impagas del pedido (puede tener una por
/// modo de carga) y lo ya pagado, que queda para que el staff decida si lo devuelve.
/// </summary>
public sealed record FraudDecision(long OrderId, ScreeningStatus Status, IReadOnlyList<long> CreditNoteIds, decimal PaidAmount);

/// <summary>
/// Decisiones sobre un pedido retenido, con el pedido bloqueado como la traba de <see cref="BillingCore.StartProvisioning"/>:
/// el resultado del módulo, la aprobación y el rechazo del staff. Liberar aprovisiona como el pedido, con el alta de
/// cada producto: lo de alta al pedir, y lo que se activa al pagar si ya estaba pago o era de total cero.
/// </summary>
public sealed class FraudReviewService(BillingDbContext db, BillingCore core, CreditService credit, AuditLog audit, TimeProvider time)
{
    /// <summary>Lo que devolvió el módulo, o su error. Si el staff ya decidió, queda anotado y no cambia la decisión.</summary>
    public async Task Evaluated(long orderId, decimal? score, IReadOnlyList<FraudReason> reasons, string? externalId, string? error, bool review, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "orders.orders", [orderId], ct, "NO KEY UPDATE").ConfigureAwait(false);
        var check = await db.Set<FraudCheck>().SingleAsync(f => f.OrderId == orderId, ct).ConfigureAwait(false);
        var decided = check.Status;
        var passed = check.Evaluated(score, reasons, externalId, error, review, time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (passed)
        {
            await Release(orderId, ct).ConfigureAwait(false);
        }

        await audit.Record("fraud.screened", string.Create(CultureInfo.InvariantCulture, $"order:{orderId}"),
            new { score, status = check.Status.ToString(), already_decided = decided != ScreeningStatus.Pending, reasons = reasons.Select(r => r.Code), error = error is not null }, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Aprueba o rechaza un pedido retenido. Repetir la misma decisión no hace nada. Rechazar cancela el pedido y sus
    /// servicios pendientes, y anula con una nota de crédito cada factura del pedido que no tiene nada pagado. Una que
    /// tiene algo pagado queda como está: puede ser plata de una tarjeta robada y no tiene que quedar como saldo a favor.
    /// </summary>
    public async Task<FraudDecision> Decide(long orderId, bool approve, string? note, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "orders.orders", [orderId], ct, "NO KEY UPDATE").ConfigureAwait(false);
        var check = await db.Set<FraudCheck>().SingleOrDefaultAsync(f => f.OrderId == orderId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"El pedido {orderId} no tiene evaluación de fraude.");
        var order = await db.Orders.SingleAsync(o => o.Id == orderId, ct).ConfigureAwait(false);
        var changed = check.Decide(approve, Actor.Current.Id, note, time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        IReadOnlyList<long> creditNotes = [];
        var paid = 0m;
        if (changed)
        {
            if (approve)
            {
                await Release(orderId, ct).ConfigureAwait(false);
            }
            else
            {
                (creditNotes, paid) = await Reject(order, check.Note, ct).ConfigureAwait(false);
            }

            await audit.Record(approve ? "fraud.approved" : "fraud.rejected", string.Create(CultureInfo.InvariantCulture, $"order:{orderId}"),
                new { credit_notes = creditNotes, paid }, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Las notas de crédito del rechazo se numeran al confirmar.
        await core.Commit(tx, ct).ConfigureAwait(false);
        return new FraudDecision(orderId, check.Status, creditNotes, paid);
    }

    /// <summary>
    /// Lo facturado y lo pagado de cada pedido, con todas sus facturas: un pedido que mezcla modos de carga tiene una por
    /// modo. Se cuentan las facturas que tienen renglones de los servicios del pedido.
    /// </summary>
    public async Task<Dictionary<long, (decimal Total, decimal Paid)>> Amounts(IReadOnlyCollection<long> orderIds, CancellationToken ct = default)
    {
        var rows = await (from l in db.Set<OrderLine>()
                          where orderIds.Contains(l.OrderId) && l.ServiceId != null
                          join dl in db.DocumentLines on l.ServiceId equals dl.ServiceId
                          join d in db.Documents on dl.DocumentId equals d.Id
                          where d.Kind == DocumentKind.Invoice
                          select new { l.OrderId, d.Id, d.Total, d.Allocated }).Distinct().ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(r => r.OrderId).ToDictionary(g => g.Key, g => (g.Sum(r => r.Total), g.Sum(r => r.Allocated)));
    }

    /// <summary>
    /// Aprovisiona cada servicio pendiente del pedido con la regla del alta de su producto, la misma del pedido: el de
    /// alta al pedir sale enseguida, el que acepta el staff espera que acepte el pedido y el resto sale si sus facturas
    /// están pagas o si no tiene ninguna (total cero). Lo que falta pagar se aprovisiona al pagarse, como siempre: un
    /// pedido con una factura por modo de carga no espera a las dos.
    /// </summary>
    private async Task Release(long orderId, CancellationToken ct)
    {
        var services = await Services(orderId, ct).ConfigureAwait(false);
        var productIds = services.Select(s => s.ProductId).Distinct().ToList();
        var activations = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Activation, ct).ConfigureAwait(false);
        foreach (var service in services)
        {
            var release = activations[service.ProductId] switch
            {
                ProductActivation.OnOrder => true,
                ProductActivation.Manual => false,
                _ => !await (from dl in db.DocumentLines
                             join d in db.Documents on dl.DocumentId equals d.Id
                             where dl.ServiceId == service.Id && d.Kind == DocumentKind.Invoice && d.Total - d.Credited - d.Allocated > 0
                             select d.Id).AnyAsync(ct).ConfigureAwait(false),
            };
            if (release)
            {
                await core.StartProvisioning(service, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<(IReadOnlyList<long> CreditNotes, decimal Paid)> Reject(Order order, string? note, CancellationToken ct)
    {
        order.Status = OrderStatus.Cancelled;
        var now = time.GetUtcNow();
        var services = await Services(order.Id, ct).ConfigureAwait(false);
        var serviceIds = services.Select(s => s.Id).ToList();
        foreach (var service in services)
        {
            service.MoveTo(ServiceStatus.Cancelled, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var invoices = await (from d in db.Documents.AsNoTracking()
                              where d.Kind == DocumentKind.Invoice && db.DocumentLines.Any(l => l.DocumentId == d.Id && serviceIds.Contains(l.ServiceId!.Value))
                              orderby d.Id
                              select new { d.Id, d.Allocated }).ToListAsync(ct).ConfigureAwait(false);
        var reason = "Pedido rechazado en la revisión de fraude" + (note is null ? string.Empty : $": {note}");
        var notes = new List<long>();
        foreach (var invoice in invoices.Where(i => i.Allocated == 0m))
        {
            if (await credit.Credit(invoice.Id, null, reason is { Length: > 200 } ? reason[..200] : reason, ct).ConfigureAwait(false) is { } result)
            {
                notes.Add(result.Note.Id);
            }
        }

        return (notes, invoices.Sum(i => i.Allocated));
    }

    private Task<List<Service>> Services(long orderId, CancellationToken ct) =>
        (from s in db.Services
         join l in db.Set<OrderLine>() on s.OrderLineId equals l.Id
         where l.OrderId == orderId && s.Status == ServiceStatus.Pending
         select s).ToListAsync(ct);
}

/// <summary>
/// Trabajo que le pide la evaluación al módulo de fraude (sección 3.3). Llama fuera de toda transacción y decide con el
/// pedido bloqueado. Un error reintentable vuelve a la cola, y uno definitivo o el último intento aplican el ajuste
/// FraudReviewOnError.
/// </summary>
public sealed class FraudScreenHandler(BillingDbContext db, ModuleRegistry registry, ModuleClient client, FraudReviewService review, SecuritySettingsStore settings) : IJobHandler
{
    public const int MaxAttempts = 5;

    public string Kind => JobKinds.FraudScreen;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = JsonSerializer.Deserialize<FraudScreenJob>(job.Payload)!;
        var check = await db.Set<FraudCheck>().AsNoTracking().SingleOrDefaultAsync(f => f.OrderId == payload.OrderId, ct).ConfigureAwait(false);
        if (check is not { Status: ScreeningStatus.Pending, ModuleInstanceId: { } instanceId })
        {
            return JobResult.Done;
        }

        FraudEvaluation evaluation;
        string? error = null;
        try
        {
            evaluation = await Evaluate(check, instanceId, payload.UserAgent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RetryLaterException or PermanentFailureException)
        {
            if (ex is RetryLaterException && job.Attempts < job.MaxAttempts)
            {
                throw;
            }

            error = ex.Message;
            evaluation = new FraudEvaluation(null, [new FraudReason("fraud.module_error", "El módulo de fraude no pudo evaluar el pedido.")], null);
        }

        var s = await settings.Get(ct).ConfigureAwait(false);
        var toReview = error is not null ? s.FraudReviewOnError : evaluation.Score >= s.FraudReviewScore;
        await review.Evaluated(check.OrderId, evaluation.Score, evaluation.Reasons, evaluation.ExternalId, error, toReview, ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    private async Task<FraudEvaluation> Evaluate(FraudCheck check, long instanceId, string? userAgent, CancellationToken ct)
    {
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        if (module.InCore || module.Manifest["kinds"] is not JsonArray kinds || !kinds.Any(k => k?.GetValue<string>() == "fraud"))
        {
            throw new PermanentFailureException($"La instancia {instanceId} no es de un módulo de fraude.");
        }

        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == check.OrderId, ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == order.AccountId, ct).ConfigureAwait(false);
        var tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == account.Id && t.ValidTo == null, ct).ConfigureAwait(false);
        // Todas las facturas del pedido: puede tener una por modo de carga.
        var total = (await review.Amounts([order.Id], ct).ConfigureAwait(false)).GetValueOrDefault(order.Id).Total;
        var productIds = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct).ConfigureAwait(false);

        var input = new JsonObject
        {
            ["order"] = new JsonObject
            {
                ["id"] = order.Id.ToString(CultureInfo.InvariantCulture),
                ["placed_at"] = order.PlacedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["amount"] = new JsonObject { ["value"] = total.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = order.Currency.Code },
                ["items"] = new JsonArray([.. order.Lines.Select(l => (JsonNode)new JsonObject
                {
                    ["product_id"] = l.ProductId.ToString(CultureInfo.InvariantCulture),
                    ["name"] = products.GetValueOrDefault(l.ProductId),
                    ["quantity"] = l.Quantity,
                })]),
            },
            ["request"] = new JsonObject { ["ip"] = check.Ip?.ToString(), ["user_agent"] = userAgent },
        };
        var context = new JsonObject
        {
            ["account"] = new JsonObject
            {
                ["id"] = account.Id.ToString(CultureInfo.InvariantCulture),
                ["country"] = tax?.Country,
                ["language"] = account.Language,
                ["created_at"] = account.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            },
        };
        var scopes = module.Manifest["data_scopes"] is JsonArray s ? s.Select(x => x?.GetValue<string>()).ToHashSet() : [];
        if (scopes.Contains("account.contact"))
        {
            context["contact"] = new JsonObject { ["name"] = account.Name, ["email"] = account.Email, ["phone"] = account.Phone };
        }

        if (scopes.Contains("account.address") && tax is not null)
        {
            context["address"] = new JsonObject { ["street"] = tax.Street, ["city"] = tax.City, ["province"] = tax.Province, ["postal_code"] = tax.PostalCode, ["country"] = tax.Country };
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"order-{order.Id}-evaluate");
        var result = await client.Call(module.Endpoint, "/v0/fraud/evaluate", new ModuleRequest(module.Data, context, input), key, module.Timeout("evaluate"), ct).ConfigureAwait(false);
        if (result.Error is { } e)
        {
            throw ErrorCodes.IsRetryable(e.Code)
                ? new RetryLaterException($"{e.Code}: {e.Message}", e.RetryAfter is { } after ? TimeSpan.FromSeconds(after) : null)
                : new PermanentFailureException($"{e.Code}: {e.Message}");
        }

        if (result.Response!.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        if (result.Response.Status == ModuleResponse.PendingStatus)
        {
            throw new RetryLaterException("La evaluación sigue en curso en el módulo.", TimeSpan.FromSeconds(result.Response.RetryAfter ?? 15));
        }

        return FraudEvaluation.Parse(result.Response.Result) ?? throw new PermanentFailureException("El módulo de fraude devolvió un puntaje inválido.");
    }
}

/// <summary>Lo que devuelve un módulo de fraude, ya validado (sección 9.6 del protocolo).</summary>
public sealed partial record FraudEvaluation(decimal? Score, IReadOnlyList<FraudReason> Reasons, string? ExternalId)
{
    public const int MaxReasons = 20;

    /// <summary>
    /// Null si el puntaje no es un decimal de 0 a 100, en texto o en número. Los motivos con un código inválido se
    /// descartan, y el texto se corta en 300 caracteres. Desconfianza en la respuesta (sección 1 del protocolo).
    /// </summary>
    public static FraudEvaluation? Parse(JsonNode? result)
    {
        if (result is not JsonObject body || body["score"] is not JsonValue raw)
        {
            return null;
        }

        var text = raw.TryGetValue<string>(out var str) ? str : raw.TryGetValue<decimal>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score) || score > 100m)
        {
            return null;
        }

        var reasons = new List<FraudReason>();
        if (body["reasons"] is JsonArray list)
        {
            foreach (var item in list.OfType<JsonObject>())
            {
                if (item["code"] is JsonValue c && c.TryGetValue<string>(out var code) && Code().IsMatch(code)
                    && item["message"] is JsonValue m && m.TryGetValue<string>(out var message))
                {
                    reasons.Add(new FraudReason(code, message.Length > 300 ? message[..300] : message));
                }

                if (reasons.Count == MaxReasons)
                {
                    break;
                }
            }
        }

        var external = body["external_id"] is JsonValue x && x.TryGetValue<string>(out var id) ? id : null;
        return new FraudEvaluation(decimal.Round(score, 2, MidpointRounding.AwayFromZero), reasons, external is { Length: > 100 } ? external[..100] : external);
    }

    [GeneratedRegex("^[a-z0-9._-]{1,64}$")]
    private static partial Regex Code();
}
