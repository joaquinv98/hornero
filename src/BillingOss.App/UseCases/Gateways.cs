using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.UseCases;

public sealed record CheckoutStarted(string Reference, string RedirectUrl);

/// <summary>
/// Lo que el cliente acepta antes de guardar un medio o prender un débito (sección 4.5 del diseño de cobros): el texto
/// exacto que vio, en su idioma, y su navegador. Quién y desde qué IP salen del actor del pedido.
/// </summary>
public sealed record ConsentInput(string Text, string Culture, string? UserAgent = null)
{
    /// <summary>La constancia que se guarda con el medio, mientras exista, y en la auditoría. JSON.</summary>
    public string Record(DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Text);
        var actor = Actor.Current;
        return new JsonObject
        {
            ["at"] = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["actor"] = actor.Id,
            ["ip"] = actor.Ip?.ToString(),
            ["user_agent"] = UserAgent is { Length: > 300 } ua ? ua[..300] : UserAgent,
            ["culture"] = Culture,
            ["text"] = Text,
            ["text_sha256"] = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Text))),
        }.ToJsonString();
    }
}

/// <summary>
/// Primer paso de la recepción de webhooks (sección 9.3 del protocolo): guarda lo que llegó, sin ninguna unicidad,
/// y encola su verificación. No necesita módulos, así que atiende aunque el proceso no tenga llave de secretos.
/// </summary>
public sealed class WebhookInbox(BillingDbContext db, JobQueue jobs, TimeProvider time)
{
    /// <summary>Devuelve false si la instancia no existe o no es de un módulo de cobros que recibe avisos.</summary>
    public async Task<bool> Receive(long instanceId, JsonObject headers, JsonObject query, byte[] body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(query);
        if (!await GatewayPayments.Declares(db, instanceId, "payment.webhooks", ct).ConfigureAwait(false))
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var receipt = new WebhookReceipt
        {
            ModuleInstanceId = instanceId,
            Headers = headers.ToJsonString(),
            Query = query.ToJsonString(),
            Body = body,
            ReceivedAt = time.GetUtcNow(),
        };
        db.WebhookReceipts.Add(receipt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.WebhookVerify, new { receipt_id = receipt.Id }, ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// Cobro por pasarela (sección 8 del modelo de dominio y 9.3 del protocolo): intento y checkout, verificación de
/// webhooks, consulta del pago y conciliación. Un webhook nunca da algo por pagado: el pago se le pide al módulo y
/// se valida contra el intento. Además guarda el medio que el cliente pidió guardar y registra los cobros de las
/// suscripciones propias (sección 4.5 del diseño de cobros).
/// </summary>
public sealed class GatewayPayments(
    BillingDbContext db,
    ModuleRegistry registry,
    ModuleClient client,
    JobQueue jobs,
    PaymentService payments,
    BillingCore core,
    ExchangeRateService rates,
    AuditLog audit,
    TimeProvider time,
    ILogger<GatewayPayments> logger)
{
    /// <summary>Canal de los pagos de una instancia: con el ID externo forma la clave única del pago.</summary>
    public static string Channel(long instanceId) => string.Create(CultureInfo.InvariantCulture, $"module:{instanceId}");

    /// <summary>
    /// La instancia es de un módulo de cobros que declara la capacidad (sección 9.3 del protocolo). Así ni un aviso
    /// anónimo ni un checkout hacen que el núcleo llame firmado, con toda la configuración, a un módulo de otro tipo.
    /// </summary>
    public static Task<bool> Declares(BillingDbContext db, long instanceId, string capability, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM platform.module_instances i
                JOIN platform.module_installations m ON m.id = i.installation_id
                WHERE i.id = {instanceId}
                  AND m.manifest -> 'kinds' @> '["payment"]'::jsonb
                  AND m.manifest -> 'capabilities' @> jsonb_build_array({capability}::text)) AS "Value"
            """).SingleAsync(ct);
    }

    /// <summary>La instancia de un canal de pasarela, o null si el pago entró por otro lado.</summary>
    public static long? InstanceOf(string channel) =>
        channel.StartsWith("module:", StringComparison.Ordinal) && long.TryParse(channel.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>Clave de entidad de los trabajos de un pago: consulta y reembolso corren en orden y de a uno.</summary>
    public static string PaymentEntity(long instanceId, string externalId) =>
        string.Create(CultureInfo.InvariantCulture, $"payment:{instanceId}:{externalId}");

    /// <summary>
    /// En qué moneda cobra una pasarela documentos de una moneda: la pedida, o la de los documentos si la cobra. Si el
    /// manifiesto lista sus monedas ("currencies") y la de los documentos no está, la primera de la lista, con la
    /// cotización vigente. Sin lista, la pasarela cobra lo que le pidan.
    /// </summary>
    public static Currency ChargeCurrency(JsonObject manifest, Currency documents, Currency? requested = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var listed = manifest["currencies"] is JsonArray list ? list.Select(c => c?.GetValue<string>()).OfType<string>().ToList() : null;
        bool Charges(Currency c) => listed is null || listed.Contains("*") || listed.Contains(c.Code);
        if (requested is { } wanted)
        {
            return Charges(wanted) ? wanted : throw new ValidationException($"La pasarela no cobra en {wanted}.");
        }

        if (Charges(documents))
        {
            return documents;
        }

        return listed!.FirstOrDefault() is { } first ? new Currency(first) : throw new ValidationException("La pasarela no dice en qué moneda cobra.");
    }

    /// <summary>Clave de entidad de los trabajos de un medio guardado.</summary>
    public static string MethodEntity(long instanceId, string methodExternalId) =>
        string.Create(CultureInfo.InvariantCulture, $"method:{instanceId}:{methodExternalId}");

    /// <summary>Clave de entidad de los trabajos de una suscripción.</summary>
    public static string SubscriptionEntity(long subscriptionId) =>
        string.Create(CultureInfo.InvariantCulture, $"subscription:{subscriptionId}");

    /// <summary>Referencia pública de un intento o de una suscripción: no se puede adivinar.</summary>
    public static string NewReference() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Hay un cobro automático abierto sobre la factura: mientras dure, no se cobra por otro medio (sección 4.5 del
    /// diseño de cobros).
    /// </summary>
    public static Task<bool> AutoChargeInFlight(BillingDbContext db, long documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.PaymentAttempts.AnyAsync(a => a.Status == AttemptStatus.Open && a.Initiator == AttemptInitiator.Merchant && a.DocumentIds.Contains(documentId), ct);
    }

    /// <summary>Abre un intento por las facturas y prepara el cobro. El estado de las facturas se comprueba antes de cobrar.</summary>
    public Task<CheckoutStarted> Checkout(long accountId, IReadOnlyList<long> documentIds, long instanceId, CancellationToken ct = default) =>
        Checkout(accountId, documentIds, instanceId, null, null, ct);

    /// <summary>
    /// Abre un intento por una o varias facturas de la misma moneda y prepara el cobro. El estado de las facturas se
    /// comprueba antes de cobrar. Si la pasarela cobra en otra moneda (<paramref name="currency"/>, o la primera que lista
    /// si no cobra la de las facturas), el monto se convierte con la cotización vigente, que queda congelada en el intento
    /// con su fuente: el pago se imputa con ella. Con <paramref name="saveMethod"/> el cliente pide además guardar el medio
    /// para el débito automático (payment.saved_methods), que cobra cada factura en su moneda, así que va sin conversión.
    /// El consentimiento se guarda antes de redirigir.
    /// </summary>
    public async Task<CheckoutStarted> Checkout(long accountId, IReadOnlyList<long> documentIds, long instanceId, string? currency, ConsentInput? saveMethod, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (!await Declares(db, instanceId, "payment.checkout", ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe la instancia de cobro {instanceId}.");
        }

        var publicUrl = core.Options.PublicUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Falta Billing:PublicUrl para armar las URLs de retorno y de aviso.");
        var ids = documentIds.Distinct().ToList();
        var docs = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)).OrderBy(d => d.DueDate).ThenBy(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0 || docs.Count != ids.Count || docs.Any(d => d.AccountId != accountId || d.Kind == DocumentKind.CreditNote || !d.IsIssued || d.Pending <= 0m))
        {
            throw new ValidationException("Hay facturas que no son de la cuenta, no están emitidas o no tienen saldo.");
        }

        if (docs.Select(d => d.Currency).Distinct().Count() != 1)
        {
            throw new ValidationException("Las facturas de un intento tienen que estar en la misma moneda.");
        }

        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var documents = docs[0].Currency;
        var charge = ChargeCurrency(module.Manifest, documents, currency is null ? null : InvoiceService.ParseCurrency(currency));
        var setup = await EnsureOffers(instanceId, charge, ct).ConfigureAwait(false);
        if (saveMethod is not null && (charge != documents || setup is not { AutoCharge: true } || !await Declares(db, instanceId, "payment.saved_methods", ct).ConfigureAwait(false)))
        {
            throw new ValidationException("La pasarela no ofrece guardar el medio para el débito automático.");
        }

        if (saveMethod is not null && await AutoChargeService.HasSubscription(db, accountId, ct).ConfigureAwait(false))
        {
            throw new ValidationException("La cuenta ya tiene el débito automático por suscripción: hay que desactivarlo antes de guardar un medio para el débito.");
        }

        var conversion = charge == documents ? null : await rates.Require(documents, charge, ct).ConfigureAwait(false);

        // Cada renglón convertido en forma acumulada: suman exactamente el monto del intento.
        var prices = new List<decimal>();
        var cumulative = 0m;
        foreach (var d in docs)
        {
            var before = conversion?.Apply(cumulative) ?? cumulative;
            cumulative += d.Pending;
            prices.Add((conversion?.Apply(cumulative) ?? cumulative) - before);
        }

        var now = time.GetUtcNow();
        var attempt = new PaymentAttempt(accountId, instanceId, charge, prices.Sum(), [.. docs.Select(d => d.Id)], NewReference(), now);
        if (conversion is not null)
        {
            attempt.ConvertedWith(conversion.Rate.Id, conversion.Factor);
        }

        if (saveMethod is not null)
        {
            attempt.SaveMethodWith(saveMethod.Record(now));
        }

        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            // Con la misma traba de las facturas que toma el débito al abrir su intento: el pago del cliente y el cobro
            // automático no se cruzan aunque corran a la vez en dos nodos.
            await Sql.LockRows(db, "billing.documents", ids, ct).ConfigureAwait(false);
            foreach (var doc in docs)
            {
                if (await AutoChargeInFlight(db, doc.Id, ct).ConfigureAwait(false))
                {
                    throw new ValidationException("Estamos cobrando una de las facturas con el medio guardado: esperá el resultado antes de pagarla de otra forma.");
                }
            }

            db.PaymentAttempts.Add(attempt);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (saveMethod is not null)
            {
                await audit.Record("payments.consent", string.Create(CultureInfo.InvariantCulture, $"account:{accountId}"), new { attempt = attempt.Id, instance = instanceId, kind = "saved_method" }, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        JsonArray items = [.. docs.Select((d, i) => (JsonNode)Item(string.Create(CultureInfo.InvariantCulture, $"doc-{d.Id}"), string.Create(CultureInfo.InvariantCulture, $"Factura {d.Number}"), prices[i]))];
        return await Start(module, attempt, publicUrl, items, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Carga de saldo a favor desde el portal (sección 6.3 del modelo de dominio): un intento sin facturas, por un monto
    /// en una moneda que la pasarela cobra y dentro de los límites de Billing:TopUp. El pago entra entero como saldo a
    /// favor y no lleva comprobante fiscal: lo lleva la factura que lo consume.
    /// </summary>
    public async Task<CheckoutStarted> TopUp(long accountId, string currency, decimal amount, long instanceId, CancellationToken ct = default)
    {
        var code = InvoiceService.ParseCurrency(currency);
        var limits = TopUpLimits(core.Options, code) ?? throw new ValidationException($"No se carga saldo en {code}.");
        if (amount <= 0m || amount < limits.Min || amount > limits.Max || !Rounding.HasAtMostDecimals(amount))
        {
            throw new ValidationException(string.Create(CultureInfo.InvariantCulture, $"La carga en {code} va de {limits.Min:0.00} a {limits.Max:0.00}, con dos decimales como máximo."));
        }

        if (!await Declares(db, instanceId, "payment.checkout", ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe la instancia de cobro {instanceId}.");
        }

        var publicUrl = core.Options.PublicUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Falta Billing:PublicUrl para armar las URLs de retorno y de aviso.");
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        }

        await EnsureOffers(instanceId, code, ct).ConfigureAwait(false);

        // Sin conversión: el saldo queda en la moneda que se cobró, así que la pasarela tiene que cobrar en la pedida.
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        ChargeCurrency(module.Manifest, code, code);
        var attempt = new PaymentAttempt(accountId, instanceId, code, amount, [], NewReference(), time.GetUtcNow());
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await Start(module, attempt, publicUrl, [Item("credit", "Carga de saldo", amount)], ct).ConfigureAwait(false);
    }

    /// <summary>Los límites de la carga de saldo en una moneda, o null si en esa moneda no se carga.</summary>
    public static TopUpLimits? TopUpLimits(BillingOptions options, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.TopUp.FirstOrDefault(p => string.Equals(p.Key, currency.Code, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static JsonObject Item(string id, string title, decimal price) => new()
    {
        ["id"] = id,
        ["title"] = title,
        ["quantity"] = 1,
        ["unit_price"] = price.ToString("0.00", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Una instancia que pasó por el asistente cobra solo si está activa y en las monedas que se le activaron. Devuelve su
    /// configuración, o null si no pasó por el asistente.
    /// </summary>
    private async Task<GatewaySetup?> EnsureOffers(long instanceId, Currency currency, CancellationToken ct)
    {
        var setup = await db.GatewaySetups.AsNoTracking().SingleOrDefaultAsync(s => s.ModuleInstanceId == instanceId, ct).ConfigureAwait(false);
        if (setup is not null && !setup.Offers(currency))
        {
            throw new ValidationException($"La pasarela no está activa para cobrar en {currency.Code}.");
        }

        return setup;
    }

    /// <summary>
    /// Pide el checkout al módulo con la clave del intento y el contexto de la cuenta, y valida la URL a la que va el
    /// navegador. Si el cliente pidió guardar el medio, va save_method y el cliente que la pasarela ya conoce de la cuenta.
    /// </summary>
    private async Task<CheckoutStarted> Start(ResolvedInstance module, PaymentAttempt attempt, string publicUrl, JsonArray items, CancellationToken ct)
    {
        var accountId = attempt.AccountId ?? throw new InvalidOperationException("Un checkout necesita la cuenta que paga.");
        var back = $"{publicUrl}/pay/return/{attempt.Reference}";
        var input = new JsonObject
        {
            ["attempt_id"] = attempt.Reference,
            ["amount"] = Money(attempt.Amount, attempt.Currency),
            ["items"] = items,
            ["return_urls"] = new JsonObject { ["success"] = back, ["pending"] = back, ["failure"] = back },
            ["notification_url"] = string.Create(CultureInfo.InvariantCulture, $"{publicUrl}/webhooks/{attempt.ModuleInstanceId}"),
        };
        if (attempt.SaveMethod)
        {
            input["save_method"] = true;
            var customer = await db.SavedPaymentMethods.AsNoTracking().Where(m => m.AccountId == accountId && m.ModuleInstanceId == attempt.ModuleInstanceId)
                .OrderByDescending(m => m.Id).Select(m => m.CustomerExternalId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (customer is not null)
            {
                input["customer_external_id"] = customer;
            }
        }

        var context = await Context(accountId, module, contact: false, ct).ConfigureAwait(false);
        var result = await Call(module, "create_checkout", input, string.Create(CultureInfo.InvariantCulture, $"checkout-{attempt.Id}"), ct, context).ConfigureAwait(false);
        var redirect = Redirect(module, result);

        // El ID del checkout, si la pasarela lo da: con él el barrido pregunta por el pago mientras el intento sigue abierto.
        var checkoutId = result["checkout_external_id"]?.GetValue<string>();
        if (checkoutId is { Length: > 200 })
        {
            throw new PermanentFailureException("La pasarela devolvió un checkout_external_id de más de 200 caracteres.");
        }

        attempt.RedirectUrl = redirect;
        attempt.CheckoutExternalId = checkoutId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new CheckoutStarted(attempt.Reference, redirect);
    }

    /// <summary>
    /// Segundo paso de la recepción: el módulo verifica y los eventos válidos se guardan con unicidad por instancia e ID.
    /// Un aviso inválido se queda sin cuerpo: cualquiera que conozca la URL puede mandar basura, y la retención borra el
    /// recibo a los Workers:KeepDone días.
    /// </summary>
    public async Task Verify(long receiptId, CancellationToken ct = default)
    {
        var receipt = await db.WebhookReceipts.SingleAsync(r => r.Id == receiptId, ct).ConfigureAwait(false);
        if (receipt.Status != ReceiptStatus.Received)
        {
            return;
        }

        var module = await registry.Resolve(receipt.ModuleInstanceId, ct).ConfigureAwait(false);

        // received_at es la hora en que llegó al núcleo: con ella el módulo acota la edad de lo firmado aunque la
        // verificación corra mucho después, y un aviso capturado no sirve días más tarde.
        var result = await Call(module, "verify_webhook", new JsonObject
        {
            ["headers"] = JsonNode.Parse(receipt.Headers),
            ["query"] = JsonNode.Parse(receipt.Query),
            ["body_base64"] = Convert.ToBase64String(receipt.Body),
            ["received_at"] = Iso(receipt.ReceivedAt),
        }, null, ct).ConfigureAwait(false);

        var now = time.GetUtcNow();
        var valid = result["valid"]?.GetValue<bool>() == true;
        var instance = receipt.ModuleInstanceId;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        receipt.Status = valid ? ReceiptStatus.Verified : ReceiptStatus.Invalid;
        receipt.ProcessedAt = now;
        if (!valid)
        {
            receipt.Body = [];
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var e in valid ? result["events"]?.AsArray() ?? [] : [])
        {
            var eventId = e!["event_id"]!.GetValue<string>();
            var external = e["payment_external_id"]?.GetValue<string>();
            var method = e["method_external_id"]?.GetValue<string>();
            var subscription = e["subscription_external_id"]?.GetValue<string>();
            var type = e["type"]?.GetValue<string>() ?? string.Empty;
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO payments.gateway_events (module_instance_id, event_id, type, payment_external_id, receipt_id, created_at)
                VALUES ({instance}, {eventId}, {type}, {external}, {receipt.Id}, {now})
                ON CONFLICT (module_instance_id, event_id) DO NOTHING
                """, ct).ConfigureAwait(false);
            if (inserted != 1)
            {
                continue;
            }

            var dedup = string.Create(CultureInfo.InvariantCulture, $"{instance}:event:{eventId}");
            if (external is not null)
            {
                await jobs.Enqueue(JobKinds.PaymentSync, new { instance_id = instance, payment_external_id = external }, PaymentEntity(instance, external), $"{JobKinds.PaymentSync}:{dedup}", ct: ct).ConfigureAwait(false);
            }
            else if (method is not null)
            {
                await jobs.Enqueue(JobKinds.MethodSync, new { instance_id = instance, method_external_id = method }, MethodEntity(instance, method), $"{JobKinds.MethodSync}:{dedup}", ct: ct).ConfigureAwait(false);
            }
            else if (subscription is not null
                && await db.GatewaySubscriptions.AsNoTracking().Where(s => s.ModuleInstanceId == instance && s.ExternalId == subscription).Select(s => (long?)s.Id).SingleOrDefaultAsync(ct).ConfigureAwait(false) is { } own)
            {
                // Solo las propias, con la misma clave de entidad que la baja y el cambio de monto: corren de a uno.
                await jobs.Enqueue(JobKinds.SubscriptionSync, new { subscription_id = own }, SubscriptionEntity(own), $"{JobKinds.SubscriptionSync}:{dedup}", ct: ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cada cuánto vuelve a consultar un pago avisado cuando la configuración de la instancia está rota: con los intentos
    /// del trabajo, unos dos días para que el staff la arregle.
    /// </summary>
    public static TimeSpan ConfigRetry { get; } = TimeSpan.FromHours(6);

    /// <summary>Pide el pago al módulo y lo registra contra su intento.</summary>
    public async Task Sync(long instanceId, string externalId, CancellationToken ct = default)
    {
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var payment = await Query(module, "get_payment", new JsonObject { ["payment_external_id"] = externalId }, ct).ConfigureAwait(false)
            ?? throw new RetryLaterException($"La configuración de la instancia {instanceId} no deja consultar el pago {externalId}.", ConfigRetry);
        await Apply(instanceId, payment, ct).ConfigureAwait(false);
    }

    /// <summary>Registra el pago que devolvió un cobro sin el cliente presente (charge_saved), con la misma validación.</summary>
    internal Task Record(long instanceId, JsonNode payment, CancellationToken ct) => Apply(instanceId, payment, ct);

    /// <summary>
    /// Pide a la pasarela la plata de un reembolso del staff. La clave de idempotencia es la del reembolso: repetir el
    /// pedido no devuelve dos veces. Si la pasarela lo rechaza, por ejemplo porque venció su plazo, o en el último
    /// intento sigue sin respuesta cierta, el reembolso falla y el importe sigue como saldo a favor. Si en realidad la
    /// plata salió, la próxima consulta del pago lo encuentra devuelto y lo cierra.
    /// </summary>
    public async Task SendRefund(long refundId, bool lastAttempt, CancellationToken ct = default)
    {
        var refund = await db.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId, ct).ConfigureAwait(false);
        if (refund.Status != RefundStatus.Requested)
        {
            return;
        }

        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == refund.PaymentId, ct).ConfigureAwait(false);
        if (payment.Status == PaymentStatus.ChargedBack || refund.Amount > payment.Kept)
        {
            await payments.FailRefund(refundId, "El pago ya había vuelto al pagador por otra vía.", ct).ConfigureAwait(false);
            return;
        }

        JsonObject result;
        try
        {
            var module = await registry.Resolve(InstanceOf(payment.Channel)!.Value, ct).ConfigureAwait(false);
            result = await Call(module, "refund", new JsonObject
            {
                ["payment_external_id"] = payment.ExternalId,
                ["amount"] = Money(refund.Amount, payment.Currency),
            }, string.Create(CultureInfo.InvariantCulture, $"refund-{refund.Id}"), ct).ConfigureAwait(false);
        }
        catch (PermanentFailureException ex)
        {
            await payments.FailRefund(refundId, ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (RetryLaterException ex) when (lastAttempt)
        {
            await payments.FailRefund(refundId, $"Sin respuesta cierta de la pasarela: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        await payments.SettleRefund(refundId, result["refund_id"]?.GetValue<string>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Concilia los pagos que cambiaron en el rango: encuentra lo que no llegó por webhook. Sigue las páginas por
    /// posición o, si el módulo pagina con cursor como Stripe, por cursor. Devuelve cuántos no tienen intento ni
    /// suscripción propios. En modo de prueba (<paramref name="dryRun"/>) solo cuenta y no escribe nada.
    /// </summary>
    public async Task<int> Reconcile(long instanceId, DateTimeOffset from, DateTimeOffset to, bool dryRun = false, CancellationToken ct = default)
    {
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        var unmatched = 0;
        int? offset = 0;
        string? cursor = null;
        do
        {
            var input = new JsonObject { ["from"] = Iso(from), ["to"] = Iso(to), ["by"] = "updated" };
            if (cursor is not null)
            {
                input["cursor"] = cursor;
            }
            else
            {
                input["offset"] = offset;
            }

            // Con la configuración rota no se reintenta: la conciliación de la hora siguiente vuelve a probar.
            if (await Query(module, "list_payments", input, ct).ConfigureAwait(false) is not { } page)
            {
                return unmatched;
            }

            foreach (var p in page["payments"]?.AsArray() ?? [])
            {
                if (dryRun)
                {
                    unmatched += await Owner(instanceId, p!, ct).ConfigureAwait(false) is (null, null) ? 1 : 0;
                }
                else if (!await Apply(instanceId, p!, ct).ConfigureAwait(false))
                {
                    unmatched++;
                }
            }

            cursor = page["next_cursor"]?.GetValue<string>();
            offset = page["next_offset"]?.GetValue<int>();
        }
        while (cursor is not null || offset is not null);

        return unmatched;
    }

    /// <summary>
    /// De quién es un pago: su intento, por la referencia, o si no tiene, la suscripción propia que lo cobró. Sin ninguno
    /// de los dos el pago no es de este sistema.
    /// </summary>
    private async Task<(PaymentAttempt? Attempt, GatewaySubscription? Subscription)> Owner(long instanceId, JsonNode p, CancellationToken ct)
    {
        var reference = p["external_reference"]?.GetValue<string>();
        var attempt = reference is null
            ? null
            : await db.PaymentAttempts.SingleOrDefaultAsync(a => a.Reference == reference && a.ModuleInstanceId == instanceId, ct).ConfigureAwait(false);
        return attempt is not null
            ? (attempt, null)
            : (null, await OwnSubscription(instanceId, p["subscription_external_id"]?.GetValue<string>(), reference, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Registra un pago del módulo validado contra su intento. Sin intento propio no se registra: puede ser de otro
    /// sistema que usa la misma cuenta de la pasarela, o un cobro posterior a una restauración. Queda en
    /// payments.unmatched, a la vista del staff y con alerta, y devuelve false. La excepción es el cobro de una
    /// suscripción propia, aunque el débito por suscripción esté apagado. Si el monto o la moneda no coinciden con el
    /// intento, entra a saldo a favor y no se imputa a sus facturas. Un pago de prueba del asistente solo se mira.
    /// </summary>
    private async Task<bool> Apply(long instanceId, JsonNode p, CancellationToken ct)
    {
        var externalId = p["payment_external_id"]!.GetValue<string>();
        var reference = p["external_reference"]?.GetValue<string>();
        var gatewayStatus = p["status"]?.GetValue<string>();
        var (attempt, subscription) = await Owner(instanceId, p, ct).ConfigureAwait(false);
        if (attempt is { IsTest: true })
        {
            // Pago de prueba: no es un pago, no toca facturas, saldo ni comprobantes.
            attempt.Observe(gatewayStatus ?? "unknown", time.GetUtcNow());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }

        // Un pago del ambiente de prueba en una instancia de producción no es plata: con credenciales de prueba cargadas
        // por error, un cliente pagaría con una tarjeta de prueba y se le darían por pagadas las facturas.
        if (p["livemode"] is JsonValue lm && lm.TryGetValue<bool>(out var live) && !live
            && await db.GatewaySetups.AsNoTracking().AnyAsync(s => s.ModuleInstanceId == instanceId && s.Environment == GatewaySetupService.Live, ct).ConfigureAwait(false))
        {
            logger.LogError("El pago {ExternalId} de la instancia de producción {InstanceId} es del ambiente de prueba: no se registra. Revisar las credenciales en el asistente de cobros.", externalId, instanceId);
            return true;
        }

        var accountId = attempt?.AccountId ?? subscription?.AccountId;
        if (accountId is null)
        {
            logger.LogWarning("Pago {ExternalId} de la instancia {InstanceId} sin intento propio: queda sin casar.", externalId, instanceId);
            var seen = time.GetUtcNow();
            var unmatchedStatus = gatewayStatus ?? string.Empty;
            var unmatchedAmount = decimal.Parse(p["amount"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var unmatchedCurrency = p["amount"]!["currency"]!.GetValue<string>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO payments.unmatched (module_instance_id, payment_external_id, external_reference, status, amount, currency, seen_at)
                VALUES ({instanceId}, {externalId}, {reference}, {unmatchedStatus}, {unmatchedAmount}, {unmatchedCurrency}, {seen})
                ON CONFLICT (module_instance_id, payment_external_id) DO UPDATE
                  SET status = EXCLUDED.status, amount = EXCLUDED.amount, seen_at = EXCLUDED.seen_at
                  WHERE payments.unmatched.resolved_at IS NULL
                """, ct).ConfigureAwait(false);
            return false;
        }

        var status = Status(gatewayStatus);
        var amount = decimal.Parse(p["amount"]!["value"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var currency = new Currency(p["amount"]!["currency"]!.GetValue<string>());
        var fee = p["fee"]?["value"] is JsonNode f ? decimal.Parse(f.GetValue<string>(), CultureInfo.InvariantCulture) : 0m;
        // El protocolo no obliga a informar lo devuelto: un pago reembolsado sin ese dato se devolvió entero.
        var refunded = p["refunded"]?["value"] is JsonNode r ? decimal.Parse(r.GetValue<string>(), CultureInfo.InvariantCulture)
            : status == PaymentStatus.Refunded ? amount
            : (decimal?)null;
        if (refunded is null && status == PaymentStatus.PartiallyRefunded)
        {
            logger.LogWarning("El pago {ExternalId} está reembolsado en parte y el módulo no dice cuánto: lo resuelve el staff.", externalId);
        }

        // El cobro de una suscripción va primero a las facturas de los servicios mensuales que cubre, después a las demás
        // impagas de la cuenta en su moneda, y lo que sobra a saldo a favor. El de un intento, a sus facturas si coincide.
        IReadOnlyList<long>? documents = null;
        if (attempt is not null)
        {
            var matches = amount == attempt.Amount && currency == attempt.Currency;
            if (!matches)
            {
                logger.LogWarning("El pago {ExternalId} no coincide con el intento {Reference}: entra a saldo a favor.", externalId, reference);
            }

            documents = matches ? attempt.DocumentIds : [];
        }
        else if (subscription is not null)
        {
            documents = await SubscriptionService.ChargeTargets(db, subscription.AccountId, currency, ct).ConfigureAwait(false);
        }

        if (status == PaymentStatus.Disputed)
        {
            logger.LogWarning("El pago {ExternalId} está en disputa: si se pierde, el contracargo revierte sus imputaciones.", externalId);
        }

        var paymentId = await payments.Record(new PaymentInput(accountId.Value, Channel(instanceId), externalId, currency, amount, status, documents, fee, refunded, attempt?.Rate), ct).ConfigureAwait(false);
        var automatic = attempt?.Initiator == AttemptInitiator.Merchant || subscription is not null;
        if (automatic && status is PaymentStatus.Disputed or PaymentStatus.ChargedBack)
        {
            await StopAutoCharge(accountId.Value, subscription, externalId, ct).ConfigureAwait(false);
        }

        if (attempt is null || status == PaymentStatus.Pending)
        {
            return true;
        }

        // Si Record reintentó por un pago registrado a la vez, limpió el contexto: el intento se vuelve a leer.
        var attemptId = attempt.Id;
        attempt = await db.PaymentAttempts.SingleAsync(a => a.Id == attemptId, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (status == PaymentStatus.Rejected)
        {
            // Un cobro automático rechazado deja que el débito decida otro día. El del cliente sigue abierto: puede
            // reintentar en la pasarela con el mismo intento. Si el débito ya lo había cerrado (el rechazo de charge_saved
            // dice si se reintenta, y el aviso del mismo rechazo no), no se pisa lo que decidió.
            if (attempt.Initiator == AttemptInitiator.Merchant && attempt.Status == AttemptStatus.Open)
            {
                attempt.Fail($"rejected:{p["gateway_status_detail"]?.GetValue<string>()}", "later", now);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return true;
        }

        if (attempt.Status != AttemptStatus.Paid)
        {
            attempt.MarkPaid(paymentId, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (attempt.SaveMethod && p["saved_method"] is JsonObject saved)
        {
            await RememberMethod(instanceId, attempt, saved, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// El pago dejó guardado el medio que el cliente pidió guardar: queda con la constancia de su consentimiento y pasa
    /// a ser el del débito automático de la cuenta, en una sola transacción. Solo se guarda lo que se muestra.
    /// </summary>
    private async Task RememberMethod(long instanceId, PaymentAttempt attempt, JsonObject saved, CancellationToken ct)
    {
        var methodId = saved["method_external_id"]?.GetValue<string>();
        var customerId = saved["customer_external_id"]?.GetValue<string>();
        var accountId = attempt.AccountId!.Value;
        if (string.IsNullOrWhiteSpace(methodId) || string.IsNullOrWhiteSpace(customerId))
        {
            logger.LogWarning("El pago del intento {Reference} guardó un medio sin sus IDs: no se puede usar para el débito.", attempt.Reference);
            return;
        }

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        if (await db.SavedPaymentMethods.AnyAsync(m => m.ModuleInstanceId == instanceId && m.MethodExternalId == methodId, ct).ConfigureAwait(false))
        {
            return;
        }

        var method = new SavedPaymentMethod(accountId, instanceId, customerId, methodId, attempt.Consent!, now);
        method.Describe(saved["type"]?.GetValue<string>(), saved["brand"]?.GetValue<string>(), saved["last4"]?.GetValue<string>(), Int(saved["exp_month"]), Int(saved["exp_year"]), now);
        if (method.ExpiredOn(core.Today()))
        {
            method.Expire(now);
        }
        else if (await AutoChargeService.HasSubscription(db, accountId, ct).ConfigureAwait(false))
        {
            // La cuenta prendió la suscripción mientras pagaba: el medio queda guardado sin el débito, que es uno de los dos.
            logger.LogInformation("El medio del intento {Reference} queda guardado sin el débito: la cuenta tiene una suscripción vigente.", attempt.Reference);
        }
        else
        {
            // Uno solo por cuenta: primero se apaga el anterior, así el índice único nunca ve dos.
            await db.SavedPaymentMethods.Where(m => m.AccountId == accountId && m.AutoCharge)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.AutoCharge, false).SetProperty(m => m.UpdatedAt, now), ct).ConfigureAwait(false);
            method.UseForAutoCharge(true, now);
        }

        db.SavedPaymentMethods.Add(method);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.method_saved", string.Create(CultureInfo.InvariantCulture, $"account:{accountId}"), new { method = method.Id, instance = instanceId, auto_charge = method.AutoCharge }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    }

    /// <summary>
    /// Un débito automático en disputa o contracargado apaga el débito de la cuenta y avisa al staff: casi siempre el
    /// cliente desconoció el cargo. Si vino de una suscripción, se da de baja.
    /// </summary>
    private async Task StopAutoCharge(long accountId, GatewaySubscription? subscription, string externalId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var stopped = await db.SavedPaymentMethods.Where(m => m.AccountId == accountId && m.AutoCharge)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.AutoCharge, false).SetProperty(m => m.UpdatedAt, now), ct).ConfigureAwait(false);
        if (subscription is { Status: not SubscriptionStatus.Cancelled })
        {
            await jobs.Enqueue(JobKinds.SubscriptionCancel, new { subscription_id = subscription.Id }, SubscriptionEntity(subscription.Id), string.Create(CultureInfo.InvariantCulture, $"{JobKinds.SubscriptionCancel}:{subscription.Id}"), ct: ct).ConfigureAwait(false);
            stopped++;
        }

        if (stopped > 0)
        {
            logger.LogWarning("El débito automático {ExternalId} de la cuenta {AccountId} está en disputa: se apagó el débito de la cuenta.", externalId, accountId);
            await audit.Record("payments.autocharge_stopped", string.Create(CultureInfo.InvariantCulture, $"account:{accountId}"), new { payment = externalId, reason = "dispute" }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// La suscripción propia de un cobro sin intento: por el ID que la pasarela le puso o, si el pago no lo trae, por la
    /// referencia que le puso el núcleo al crearla y que la pasarela copia en cada cobro (Mercado Pago, external_reference).
    /// Siempre, también con Payments:SubscriptionsEnabled apagado: la bandera corta ofrecerlas y cambiarlas, no la plata
    /// que la pasarela ya cobró por una vigente, que si no quedaría sin registrar y la cuenta en mora.
    /// </summary>
    private Task<GatewaySubscription?> OwnSubscription(long instanceId, string? externalId, string? reference, CancellationToken ct) =>
        externalId is null && reference is null
            ? Task.FromResult<GatewaySubscription?>(null)
            : db.GatewaySubscriptions.AsNoTracking()
                .Where(s => s.ModuleInstanceId == instanceId && ((externalId != null && s.ExternalId == externalId) || (reference != null && s.Reference == reference)))
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync(ct);

    private static PaymentStatus Status(string? status) => status switch
    {
        "approved" => PaymentStatus.Approved,
        "pending" => PaymentStatus.Pending,
        "rejected" => PaymentStatus.Rejected,
        "disputed" => PaymentStatus.Disputed,
        "charged_back" => PaymentStatus.ChargedBack,
        "partially_refunded" => PaymentStatus.PartiallyRefunded,
        "refunded" => PaymentStatus.Refunded,
        _ => throw new RetryLaterException($"Estado de pago desconocido: {status}."),
    };

    internal static JsonObject Money(decimal value, Currency currency) =>
        new() { ["value"] = value.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = currency.Code };

    internal static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>El navegador sigue esta URL: solo https y a un host de la instancia (sección 9.1 del protocolo).</summary>
    internal static string Redirect(ResolvedInstance module, JsonObject result, string field = "redirect_url")
    {
        ArgumentNullException.ThrowIfNull(module);
        var redirect = result[field]?.GetValue<string>();
        var hosts = JsonSerializer.Deserialize<string[]>(module.Instance.BrowserHosts) ?? [];
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new PermanentFailureException("La pasarela devolvió una URL que no es https o no está en los hosts permitidos de la instancia.");
        }

        return redirect!;
    }

    /// <summary>
    /// Contexto de cobros (sección 8 del protocolo): la cuenta con su ID e idioma. Nombre y correo van solo si el
    /// módulo pidió account.contact y la operación los necesita.
    /// </summary>
    internal async Task<JsonObject> Context(long accountId, ResolvedInstance module, bool contact, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId, ct).ConfigureAwait(false);
        var node = new JsonObject { ["id"] = accountId.ToString(CultureInfo.InvariantCulture), ["language"] = account.Language };
        if (contact && module.Manifest["data_scopes"] is JsonArray scopes && scopes.Any(s => s?.GetValue<string>() == "account.contact"))
        {
            node["contact"] = new JsonObject { ["name"] = account.Name, ["email"] = account.Email };
        }

        return new JsonObject { ["account"] = node };
    }

    /// <summary>Llama a una ruta del módulo y guarda el estado de la instancia si vino. El error vuelve tal cual.</summary>
    internal async Task<ModuleCallResult> Send(ResolvedInstance module, string path, string operation, JsonObject input, string? key, JsonObject? context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(module);
        var result = await client.Call(module.Endpoint, path, new ModuleRequest(module.Data, context ?? [], input), key, module.Timeout(operation), ct).ConfigureAwait(false);
        if (result.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Una operación de cobros. Un error reintentable vuelve a la cola y uno definitivo deja el trabajo muerto.</summary>
    internal async Task<JsonObject> Call(ResolvedInstance module, string operation, JsonObject input, string? key, CancellationToken ct, JsonObject? context = null)
    {
        var result = await Send(module, $"/v0/payment/{operation}", operation, input, key, context, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw Failure(error);
        }

        return result.Response?.Result as JsonObject ?? [];
    }

    /// <summary>
    /// Una consulta de pagos (get_payment o list_payments). Con la configuración de la instancia rota (una clave revocada
    /// o sin un permiso: invalid_config) devuelve null en vez de matar el trabajo, porque el aviso ya está guardado y
    /// solo se perdió la consulta. Pasarelas de pago muestra la instancia con el error, como una prueba de conexión
    /// fallida, y queda en la auditoría.
    /// </summary>
    private async Task<JsonObject?> Query(ResolvedInstance module, string operation, JsonObject input, CancellationToken ct)
    {
        var result = await Send(module, $"/v0/payment/{operation}", operation, input, null, null, ct).ConfigureAwait(false);
        if (result.Error is { Code: ErrorCodes.InvalidConfig } broken)
        {
            var now = time.GetUtcNow();
            var text = $"La consulta de pagos ({operation}) falló por la configuración: {broken.Message} Revisar las credenciales y volver a probar la conexión.";
            var message = text.Length > 2000 ? text[..2000] : text;
            await db.GatewaySetups.Where(s => s.ModuleInstanceId == module.Instance.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastTestOk, false).SetProperty(x => x.LastTestMessage, message).SetProperty(x => x.UpdatedAt, now), ct).ConfigureAwait(false);
            await audit.Record("payments.gateway_config_error", string.Create(CultureInfo.InvariantCulture, $"module_instance:{module.Instance.Id}"), new { operation, broken.Message }, ct).ConfigureAwait(false);
            logger.LogError("La instancia {InstanceId} no puede consultar pagos ({Operation}) por su configuración: {Message}", module.Instance.Id, operation, broken.Message);
            return null;
        }

        if (result.Error is { } error)
        {
            throw Failure(error);
        }

        return result.Response?.Result as JsonObject ?? [];
    }

    /// <summary>Un error reintentable vuelve a la cola y uno definitivo, o con details.auto_retry en false, deja el trabajo muerto.</summary>
    internal static Exception Failure(ModuleError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ProtocolServiceModules.Retryable(error)
            ? new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
            : new PermanentFailureException($"{error.Code}: {error.Message}", error.Code);
    }
}

/// <summary>
/// Barrido de los intentos abiertos que tienen ID de checkout: pregunta por su pago aunque el aviso de la pasarela no
/// haya llegado, mientras el checkout todavía se puede cobrar (PayPal da seis horas para aprobar y capturar una orden).
/// Un intento sin pago no es una falla, y uno que falla no frena a los demás.
/// </summary>
public sealed class CheckoutSweepHandler(BillingDbContext db, GatewayPayments gateways, TimeProvider time, ILogger<CheckoutSweepHandler> logger) : IJobHandler
{
    public static TimeSpan Window { get; } = TimeSpan.FromHours(6);

    public static TimeSpan Every { get; } = TimeSpan.FromMinutes(15);

    public string Kind => JobKinds.CheckoutSweep;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var since = time.GetUtcNow() - Window;
        var open = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.Status == AttemptStatus.Open && a.CheckoutExternalId != null && a.CreatedAt >= since)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.ModuleInstanceId, a.CheckoutExternalId })
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);
        var errors = await Batch.Each(open.Keys.Order(), async id =>
        {
            try
            {
                await gateways.Sync(open[id].ModuleInstanceId, open[id].CheckoutExternalId!, ct).ConfigureAwait(false);
            }
            catch (PermanentFailureException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                // Todavía no hay pago para ese checkout: se vuelve a preguntar en el próximo barrido.
            }
        }, logger, "la consulta del checkout del intento").ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new AggregateException("El barrido de checkouts terminó con fallas.", errors);
        }

        return JobResult.Done;
    }
}

public sealed class WebhookVerifyHandler(GatewayPayments gateways) : IJobHandler
{
    public string Kind => JobKinds.WebhookVerify;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await gateways.Verify(JsonDocument.Parse(job.Payload).RootElement.GetProperty("receipt_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class PaymentRefundHandler(GatewayPayments gateways) : IJobHandler
{
    public string Kind => JobKinds.PaymentRefund;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await gateways.SendRefund(JsonDocument.Parse(job.Payload).RootElement.GetProperty("refund_id").GetInt64(), job.Attempts >= job.MaxAttempts, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class PaymentSyncHandler(GatewayPayments gateways) : IJobHandler
{
    public string Kind => JobKinds.PaymentSync;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = JsonDocument.Parse(job.Payload).RootElement;
        await gateways.Sync(payload.GetProperty("instance_id").GetInt64(), payload.GetProperty("payment_external_id").GetString()!, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>Conciliación de una pasarela: los pagos que cambiaron en los dos días anteriores a la corrida.</summary>
public sealed class PaymentReconcileHandler(GatewayPayments gateways, TimeProvider time) : IJobHandler
{
    public static TimeSpan Window { get; } = TimeSpan.FromDays(2);

    public string Kind => JobKinds.PaymentReconcile;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var now = time.GetUtcNow();
        await gateways.Reconcile(JsonDocument.Parse(job.Payload).RootElement.GetProperty("instance_id").GetInt64(), now - Window, now, ct: ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}
