using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Stripe;

/// <summary>Las operaciones del módulo. Cada una valida su entrada, llama a Stripe y arma la respuesta del protocolo.</summary>
internal sealed class StripeOperations(StripeApi api, TimeProvider time)
{
    /// <summary>
    /// Los eventos que el destino de avisos tiene que tener en Stripe. El asistente los muestra y instance/test
    /// comprueba que estén.
    /// </summary>
    public static readonly string[] WebhookEvents =
    [
        "checkout.session.completed", "checkout.session.async_payment_succeeded", "checkout.session.async_payment_failed", "checkout.session.expired",
        "payment_intent.succeeded", "payment_intent.payment_failed", "payment_intent.processing", "payment_intent.canceled",
        "charge.refunded", "refund.created", "refund.updated", "refund.failed",
        "charge.dispute.created", "charge.dispute.updated", "charge.dispute.closed", "charge.dispute.funds_withdrawn", "charge.dispute.funds_reinstated",
        "payment_method.detached", "payment_method.automatically_updated",
    ];

    /// <summary>
    /// Lo que la clave tiene que poder leer además del saldo, porque alguna operación del módulo lo consulta. Una clave
    /// restringida a la que le falte uno pasaría la prueba, se activaría, y después get_payment o list_payments
    /// recibirían 403 y dejarían muertos los trabajos de los avisos y de la conciliación. Se prueba con un listado de
    /// uno. La escritura la prueba el pago de prueba del asistente.
    /// </summary>
    public static readonly (string Path, string Resource)[] Reads =
    [
        ("/v1/payment_intents", "PaymentIntents"),
        ("/v1/charges", "Charges"),
        ("/v1/disputes", "Disputes"),
        ("/v1/customers", "Customers"),
        ("/v1/payment_methods", "PaymentMethods"),
        ("/v1/refunds", "Refunds"),
        ("/v1/checkout/sessions", "Checkout Sessions"),
        ("/v1/balance_transactions", "Balance transactions"),
    ];

    /// <summary>Stripe admite hasta 100 líneas por sesión: con más, va una sola por el total.</summary>
    private const int MaxLines = 100;

    /// <summary>Movimientos de saldo por página de list_payments: cada uno puede llevar dos consultas más.</summary>
    private const int PageSize = 20;

    /// <summary>Rechazos que no vale la pena reintentar: la tarjeta ya no sirve o el banco pidió no insistir.</summary>
    private static readonly HashSet<string> Never = new(StringComparer.Ordinal)
    {
        "expired_card", "incorrect_number", "invalid_number", "invalid_expiry_month", "invalid_expiry_year", "card_not_supported",
        "currency_not_supported", "lost_card", "stolen_card", "pickup_card", "restricted_card", "fraudulent", "merchant_blacklist",
        "revocation_of_authorization", "revocation_of_all_authorizations", "stop_payment_order", "security_violation",
        "transaction_not_allowed", "invalid_account", "new_account_information_available", "do_not_try_again",
    };

    // ---- Operaciones ----

    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var key = ApiKey(call);
        _ = WebhookSecret(call);
        var input = Inputs.Read<TestInput>(call);
        var balance = await api.Get("/v1/balance", key, call.Aborted).ConfigureAwait(false);
        foreach (var (path, resource) in Reads)
        {
            try
            {
                await api.Get(path, key, call.Aborted, ("limit", "1")).ConfigureAwait(false);
            }
            catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidConfig)
            {
                throw Fail.InvalidConfig(["api_key"], $"A la clave le falta el permiso de lectura de {resource} (GET {path}), que usan las operaciones del módulo. {ex.Message}");
            }
            catch (ModuleException ex) when (ex.Code is ErrorCodes.InvalidInput or ErrorCodes.NotFound or ErrorCodes.Rejected)
            {
                // Stripe contestó, así que el permiso está: un parámetro que pida la versión de la API no es asunto de
                // esta prueba.
            }
        }

        var warnings = new JsonArray();
        if (key.StartsWith("sk_", StringComparison.Ordinal))
        {
            warnings.Add("La clave es secreta (sk_): Stripe recomienda una clave restringida (rk_) con los permisos justos.");
        }

        var result = new JsonObject
        {
            ["livemode"] = balance.Field("livemode").Bool() ?? key.Contains("_live_", StringComparison.Ordinal),
            ["warnings"] = warnings,
        };

        if (input.WebhookUrl is { Length: > 0 } url)
        {
            var endpoints = await api.Get("/v1/webhook_endpoints", key, call.Aborted, ("limit", "100")).ConfigureAwait(false);
            var endpoint = (endpoints.Field("data") as JsonArray ?? [])
                .FirstOrDefault(e => e.Field("url").Text() == url && e.Field("status").Text() == "enabled");
            if (endpoint is null)
            {
                throw Fail.InvalidConfig(["webhook_secret"], $"En Stripe no hay un destino de avisos activo hacia {url}. Crearlo con los eventos que muestra el asistente y copiar su secreto.");
            }

            var enabled = (endpoint.Field("enabled_events") as JsonArray ?? []).Select(e => e.Text()).OfType<string>().ToList();
            var missing = enabled.Contains("*") ? [] : WebhookEvents.Except(enabled, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                throw Fail.InvalidConfig(["webhook_secret"], $"Al destino de avisos de Stripe le faltan eventos: {string.Join(", ", missing)}.");
            }

            result["webhook_url"] = url;
            result["event_types"] = Fail.Strings(enabled);
        }

        return ModuleResponse.Completed(result);
    }

    public async Task<ModuleResponse> CreateCheckout(ModuleCall call)
    {
        var input = Inputs.Read<CheckoutInput>(call);
        var context = Inputs.Context(call);
        var form = Session(input, context);
        var key = ApiKey(call);

        // Con medio guardado hace falta un cliente en Stripe. Si el núcleo no tiene uno para esta cuenta, se crea
        // sin datos personales, con el ID de la cuenta en la metadata.
        var customer = input.CustomerExternalId;
        if (customer is not null && !IsId(customer, "cus_"))
        {
            throw Fail.InvalidInput(["customer_external_id"]);
        }

        if (input.SaveMethod == true && customer is null)
        {
            var fields = new List<(string, string)>();
            if (context.Account?.Id is { Length: > 0 } accountId)
            {
                fields.Add(("metadata[account_id]", accountId));
            }

            var created = await api.Post("/v1/customers", key, fields, $"{call.IdempotencyKey}:customer", CancellationToken.None).ConfigureAwait(false);
            customer = created.Field("id").Text() ?? throw Fail.Unknown("Stripe creó un cliente sin id.", "unexpected_response");
        }

        if (customer is not null)
        {
            form.Add(("customer", customer));
        }

        // No se corta cuando el núcleo se desconecta: con el pedido ya enviado, abandonarlo dejaría el resultado sin
        // guardar. La clave del núcleo viaja a Stripe tal cual.
        var session = await api.Post("/v1/checkout/sessions", key, form, call.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);

        // La redirección la sigue el navegador del cliente: solo https (sección 9.1 del protocolo).
        var url = session.Field("url").Text();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw Fail.Unavailable("Stripe no devolvió una URL de checkout https.");
        }

        // Sin checkout_external_id: el núcleo lo pasaría a get_payment, que acepta el PaymentIntent y no la sesión. Un pago
        // cuyo aviso no llegó lo encuentra la conciliación por los movimientos de saldo.
        var result = new JsonObject { ["redirect_url"] = url };
        if (customer is not null)
        {
            result["external_ids"] = new JsonObject { ["customer"] = customer };
        }

        return ModuleResponse.Completed(result);
    }

    public async Task<ModuleResponse> GetPayment(ModuleCall call)
    {
        var id = IntentId(Inputs.Read<PaymentRef>(call).PaymentExternalId);
        var (intent, disputes) = await Intent(id, ApiKey(call), call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(PaymentIntents.ToResult(intent, disputes));
    }

    /// <summary>
    /// Verifica la firma y normaliza el evento. Una firma que no vale no es un error: es valid false y ningún evento.
    /// Los eventos que no hablan de un pago ni de un medio se verifican y no dan eventos.
    /// </summary>
    public Task<ModuleResponse> VerifyWebhook(ModuleCall call)
    {
        var input = Inputs.Read<WebhookInput>(call);
        var secret = WebhookSecret(call);
        byte[] body;
        try
        {
            body = Convert.FromBase64String(input.BodyBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return Verified(false);
        }

        if (!WebhookSignature.Verify(secret, Header(input.Headers, "stripe-signature"), body, input.ReceivedAt))
        {
            return Verified(false);
        }

        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Verified(true);
        }

        var eventId = payload.Field("id").Text();
        var type = payload.Field("type").Text() ?? string.Empty;
        var data = payload.Field("data").Field("object");
        if (eventId is null)
        {
            return Verified(true);
        }

        JsonObject? normalized = type switch
        {
            _ when type.StartsWith("checkout.session.", StringComparison.Ordinal) && data.Field("payment_intent").Id() is { } pi =>
                new JsonObject { ["payment_external_id"] = pi },
            "payment_intent.succeeded" or "payment_intent.payment_failed" or "payment_intent.processing" or "payment_intent.canceled" when data.Field("id").Text() is { } pi =>
                new JsonObject { ["payment_external_id"] = pi },
            _ when (type is "charge.refunded" || type.StartsWith("refund.", StringComparison.Ordinal) || type.StartsWith("charge.dispute.", StringComparison.Ordinal))
                && data.Field("payment_intent").Id() is { } pi =>
                new JsonObject { ["payment_external_id"] = pi },
            "payment_method.detached" when data.Field("id").Text() is { } pm =>
                new JsonObject { ["method_external_id"] = pm, ["type"] = "method.deleted" },
            "payment_method.automatically_updated" when data.Field("id").Text() is { } pm =>
                new JsonObject { ["method_external_id"] = pm, ["type"] = "method.updated" },
            _ => null,
        };

        if (normalized is null)
        {
            return Verified(true);
        }

        normalized["event_id"] = eventId;
        normalized["type"] ??= type;
        return Verified(true, normalized);
    }

    public async Task<ModuleResponse> Refund(ModuleCall call)
    {
        var input = Inputs.Read<RefundInput>(call);
        var id = IntentId(input.PaymentExternalId);
        var partial = input.Amount;
        if (partial is not null && (!Currencies.IsCode(partial.Currency) || partial.Value is not > 0m))
        {
            throw Fail.InvalidInput(["amount"]);
        }

        var key = ApiKey(call);

        // Stripe reembolsa en la moneda del pago: un monto pensado en otra moneda movería la plata equivocada.
        var intent = await api.Get($"/v1/payment_intents/{id}", key, call.Aborted).ConfigureAwait(false);
        var currency = Currencies.Upper(intent.Field("currency").Text() ?? throw Fail.Unavailable("Stripe devolvió un pago sin currency."));
        var form = new List<(string, string)> { ("payment_intent", id) };
        if (partial is not null)
        {
            if (partial.Currency != currency)
            {
                throw Fail.InvalidInput(["amount.currency"], $"El pago es en {currency} y el reembolso se pidió en {partial.Currency}.");
            }

            var minor = Currencies.ToMinor(partial.Value!.Value, currency) ?? throw Fail.InvalidInput(["amount.value"], $"El monto tiene más decimales de los que admite {currency}.");
            form.Add(("amount", minor.ToString(CultureInfo.InvariantCulture)));
        }

        // Sin amount es un reembolso total. La clave del núcleo viaja como Idempotency-Key: repetir no devuelve dos veces.
        var refund = await api.Post("/v1/refunds", key, form, call.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        var gatewayStatus = refund.Field("status").Text();
        if (gatewayStatus is "failed" or "canceled")
        {
            throw new ModuleException(ErrorCodes.Rejected, $"Stripe no hizo el reembolso ({gatewayStatus}): la plata no salió.", details: new JsonObject
            {
                ["upstream"] = new JsonArray(new JsonObject { ["code"] = refund.Field("failure_reason").Text() ?? gatewayStatus, ["message"] = gatewayStatus }),
            });
        }

        var amount = refund.Field("amount").Long() ?? throw Fail.Unknown("Stripe no informó el monto reembolsado.", "unexpected_response");
        return ModuleResponse.Completed(new JsonObject
        {
            ["refund_id"] = refund.Field("id").Text(),
            ["payment_external_id"] = id,
            ["status"] = gatewayStatus == "succeeded" ? "approved" : "pending",
            ["amount"] = Fmt.Money(Currencies.FromMinor(amount, currency), currency),
            ["gateway_status"] = gatewayStatus,
        });
    }

    /// <summary>
    /// Concilia por movimientos de saldo: cobros, reembolsos y disputas llevan a su PaymentIntent, que se informa con
    /// la forma de get_payment. Stripe pagina con cursor, que vuelve en next_cursor.
    /// </summary>
    public async Task<ModuleResponse> ListPayments(ModuleCall call)
    {
        var input = Inputs.Read<ListInput>(call);
        if (input.From is not { } from || input.To is not { } to || from >= to)
        {
            throw Fail.InvalidInput(["from", "to"]);
        }

        if (input.Cursor is { } cursor && !IsId(cursor, "txn_"))
        {
            throw Fail.InvalidInput(["cursor"]);
        }

        var key = ApiKey(call);
        var query = new List<(string, string)>
        {
            ("created[gte]", from.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("created[lt]", to.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("expand[]", "data.source"),
        };
        if (input.Cursor is not null)
        {
            query.Add(("starting_after", input.Cursor));
        }

        var page = await api.Get("/v1/balance_transactions", key, call.Aborted, [.. query]).ConfigureAwait(false);
        if (page.Field("data") is not JsonArray movements)
        {
            throw Fail.Unavailable("Stripe devolvió movimientos sin data.");
        }

        var intents = movements
            .Where(m => m.Field("type").Text() is "charge" or "payment" or "refund" or "payment_refund" or "payment_failure_refund" or "adjustment")
            .Select(m => m.Field("source").Field("payment_intent").Id())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var payments = new JsonArray();
        foreach (var id in intents)
        {
            var (intent, disputes) = await Intent(id, key, call.Aborted).ConfigureAwait(false);
            payments.Add(PaymentIntents.ToResult(intent, disputes));
        }

        var more = page.Field("has_more").Bool() == true && movements.Count > 0;
        return ModuleResponse.Completed(new JsonObject
        {
            ["payments"] = payments,
            ["next_cursor"] = more ? movements[^1].Field("id").Text() : null,
        });
    }

    /// <summary>
    /// Cobra sin el cliente presente con un medio guardado (payment.saved_methods). Antes de cobrar busca un cobro de
    /// este intento entre los del cliente, que el listado devuelve enseguida: si existe, devuelve ese. Así un reintento
    /// sin respuesta cierta nunca cobra dos veces, aunque se haya perdido el registro de idempotencia.
    /// </summary>
    public async Task<ModuleResponse> ChargeSaved(ModuleCall call)
    {
        var input = Inputs.Read<ChargeInput>(call);
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(input.AttemptId) || input.AttemptId.Length > 200)
        {
            bad.Add("attempt_id");
        }

        var currency = input.Amount?.Currency;
        long? minor = Currencies.IsCode(currency) && input.Amount?.Value is > 0m and var value ? Currencies.ToMinor(value, currency!) : null;
        if (minor is null)
        {
            bad.Add("amount");
        }

        if (!IsId(input.CustomerExternalId, "cus_"))
        {
            bad.Add("customer_external_id");
        }

        if (!IsId(input.MethodExternalId, "pm_"))
        {
            bad.Add("method_external_id");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }

        var key = ApiKey(call);
        if (await Previous(key, input, call.Aborted).ConfigureAwait(false) is { } existing)
        {
            return Existing(existing);
        }

        var form = new List<(string, string)>
        {
            ("amount", minor!.Value.ToString(CultureInfo.InvariantCulture)),
            ("currency", Currencies.Lower(currency!)),
            ("customer", input.CustomerExternalId!),
            ("payment_method", input.MethodExternalId!),
            ("off_session", "true"),
            ("confirm", "true"),
            ("metadata[attempt_id]", input.AttemptId!),

            // Marca el cobro sin el cliente: si se rechaza, get_payment lo informa rechazado y no pendiente.
            ("metadata[off_session]", "true"),
            ("expand[]", "latest_charge"),
        };
        if (input.Description is { Length: > 0 } description)
        {
            form.Add(("description", description.Length <= 1000 ? description : description[..1000]));
        }

        try
        {
            return ModuleResponse.Completed(PaymentIntents.ToResult(await Charge(key, form, call.IdempotencyKey).ConfigureAwait(false), []));
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.OutcomeUnknown && ex.Details?["reason"].Text() == "upstream_error")
        {
            // Stripe guarda también los errores 500 bajo la clave: repetir con la misma devolvería el mismo error hasta que
            // la olvide. Si el cobro no existe, se pide con una clave derivada de la hora, así cada reintento posterior
            // tiene otra oportunidad. Buscar antes de pedir es lo que evita cobrar dos veces.
            if (await Previous(key, input, CancellationToken.None).ConfigureAwait(false) is { } created)
            {
                return Existing(created);
            }

            var derived = string.Create(CultureInfo.InvariantCulture, $"{call.IdempotencyKey}:{time.GetUtcNow():yyyyMMddHH}");
            return ModuleResponse.Completed(PaymentIntents.ToResult(await Charge(key, form, derived).ConfigureAwait(false), []));
        }
    }

    /// <summary>El PaymentIntent de este intento entre los del cliente de los últimos 3 días. El listado, a diferencia de la búsqueda, es inmediato.</summary>
    private async Task<JsonNode?> Previous(string key, ChargeInput input, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-3).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var previous = await api.Get("/v1/payment_intents", key, ct, ("customer", input.CustomerExternalId!), ("created[gte]", since), ("limit", "100")).ConfigureAwait(false);
        return (previous.Field("data") as JsonArray ?? []).FirstOrDefault(p => p.Field("metadata").Field("attempt_id").Text() == input.AttemptId);
    }

    /// <summary>Lo que pasó con un cobro anterior del intento: el pago, o el rechazo con lo que dice si se puede reintentar.</summary>
    private static ModuleResponse Existing(JsonNode existing)
    {
        if (existing.Field("status").Text() == "requires_payment_method" && existing.Field("last_payment_error") is JsonObject error)
        {
            throw Declined(new JsonObject
            {
                ["upstream"] = new JsonArray(new JsonObject { ["code"] = error.Field("code").Text(), ["message"] = error.Field("message").Text() }),
                ["decline_code"] = error.Field("decline_code").Text(),
                ["advice_code"] = error.Field("advice_code").Text(),
                ["payment_external_id"] = existing.Field("id").Text(),
            });
        }

        return ModuleResponse.Completed(PaymentIntents.ToResult(existing, []));
    }

    private async Task<JsonNode> Charge(string key, List<(string, string)> form, string? idempotencyKey)
    {
        try
        {
            return await api.Post("/v1/payment_intents", key, form, idempotencyKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.Rejected && ex.Details is { } details)
        {
            throw Declined(details);
        }
    }

    public async Task<ModuleResponse> GetMethod(ModuleCall call)
    {
        var id = MethodId(Inputs.Read<MethodRef>(call).MethodExternalId);
        try
        {
            var method = await api.Get($"/v1/payment_methods/{id}", ApiKey(call), call.Aborted).ConfigureAwait(false);
            var customer = method.Field("customer").Id();
            var result = PaymentIntents.Method(method, customer);
            result["status"] = customer is null ? "deleted" : "active";
            return ModuleResponse.Completed(result);
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            return ModuleResponse.Completed(new JsonObject { ["method_external_id"] = id, ["status"] = "deleted" });
        }
    }

    /// <summary>Quita el medio del cliente en Stripe. Si ya no estaba, también es éxito.</summary>
    public async Task<ModuleResponse> DeleteMethod(ModuleCall call)
    {
        var id = MethodId(Inputs.Read<MethodRef>(call).MethodExternalId);
        var key = ApiKey(call);
        try
        {
            var method = await api.Get($"/v1/payment_methods/{id}", key, call.Aborted).ConfigureAwait(false);
            if (method.Field("customer").Id() is not null)
            {
                await api.Post($"/v1/payment_methods/{id}/detach", key, [], call.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            // Ya no existe: no queda nada que borrar.
        }

        return ModuleResponse.Completed(new JsonObject { ["method_external_id"] = id, ["deleted"] = true });
    }

    // ---- Armado y lectura ----

    /// <summary>
    /// El cuerpo de la sesión de Checkout. El cliente paga la suma de las líneas, así que se exige que sea el monto
    /// del intento: si no, el núcleo cobraría una cosa y registraría otra. Sin precios adaptativos ni impuestos
    /// automáticos, para que moneda y monto sean los del intento.
    /// </summary>
    private static List<(string, string)> Session(CheckoutInput input, PaymentContext context)
    {
        var bad = new List<string>();
        var attempt = input.AttemptId;
        if (string.IsNullOrWhiteSpace(attempt) || attempt.Length > 200)
        {
            bad.Add("attempt_id");
        }

        var currency = input.Amount?.Currency;
        if (!Currencies.IsCode(currency))
        {
            bad.Add("amount.currency");
        }

        long? total = currency is not null && input.Amount?.Value is > 0m and var value ? Currencies.ToMinor(value, currency) : null;
        if (total is null)
        {
            bad.Add("amount.value");
        }

        if (input.Items is not { Count: > 0 })
        {
            bad.Add("items");
        }

        var lines = new List<(string Title, string? Description, int Quantity, long Unit)>();
        long sum = 0;
        foreach (var (i, item) in (input.Items ?? []).Index())
        {
            if (item is null)
            {
                bad.Add($"items[{i}]");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 250)
            {
                bad.Add($"items[{i}].title");
            }

            if (item.Quantity is not >= 1)
            {
                bad.Add($"items[{i}].quantity");
            }

            long? unit = currency is not null && item.UnitPrice is > 0m and var price ? Currencies.ToMinor(price, currency) : null;
            if (unit is null)
            {
                bad.Add($"items[{i}].unit_price");
            }

            if (unit is { } u && item.Quantity is >= 1 and var quantity && item.Title is { Length: > 0 and <= 250 } title)
            {
                lines.Add((title, item.Description, quantity, u));
                sum += quantity * u;
            }
        }

        var success = input.ReturnUrls?.GetValueOrDefault("success");
        var failure = input.ReturnUrls?.GetValueOrDefault("failure");
        if (success is null || !IsWebUrl(success))
        {
            bad.Add("return_urls.success");
        }

        if (failure is not null && !IsWebUrl(failure))
        {
            bad.Add("return_urls.failure");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }

        if (sum != total)
        {
            throw Fail.InvalidInput(["items"], $"La suma de los ítems no es el monto del intento ({Fmt.Amount(input.Amount!.Value!.Value)} {currency}).");
        }

        var form = new List<(string, string)>
        {
            ("mode", "payment"),
            ("client_reference_id", attempt!),
            ("metadata[attempt_id]", attempt!),
            ("payment_intent_data[metadata][attempt_id]", attempt!),
            ("success_url", success!),
            ("cancel_url", failure ?? success!),
            ("locale", Locale(input.Language ?? context.Account?.Language)),
            ("adaptive_pricing[enabled]", "false"),
        };

        if (lines.Count > MaxLines)
        {
            lines = [("Total", null, 1, total!.Value)];
        }

        foreach (var (i, line) in lines.Index())
        {
            var prefix = string.Create(CultureInfo.InvariantCulture, $"line_items[{i}]");
            form.Add(($"{prefix}[quantity]", line.Quantity.ToString(CultureInfo.InvariantCulture)));
            form.Add(($"{prefix}[price_data][currency]", Currencies.Lower(currency!)));
            form.Add(($"{prefix}[price_data][unit_amount]", line.Unit.ToString(CultureInfo.InvariantCulture)));
            form.Add(($"{prefix}[price_data][product_data][name]", line.Title));
            if (line.Description is { Length: > 0 } description)
            {
                form.Add(($"{prefix}[price_data][product_data][description]", description));
            }
        }

        // Guardar el medio para cobros iniciados por el comercio: solo tarjetas. Checkout le avisa al cliente que el
        // medio queda guardado. El consentimiento propio lo juntó el núcleo antes de redirigir.
        if (input.SaveMethod == true)
        {
            form.Add(("payment_intent_data[setup_future_usage]", "off_session"));
            form.Add(("payment_method_types[0]", "card"));
        }

        return form;
    }

    private async Task<(JsonNode Intent, IReadOnlyList<string> Disputes)> Intent(string id, string key, CancellationToken ct)
    {
        var intent = await api.Get($"/v1/payment_intents/{id}", key, ct, ("expand[]", "latest_charge.balance_transaction"), ("expand[]", "payment_method")).ConfigureAwait(false);
        var disputes = await api.Get("/v1/disputes", key, ct, ("payment_intent", id), ("limit", "100")).ConfigureAwait(false);
        return (intent, (disputes.Field("data") as JsonArray ?? []).Select(d => d.Field("status").Text()).OfType<string>().ToList());
    }

    /// <summary>
    /// Un rechazo de la tarjeta, con lo que el núcleo necesita para decidir: later reprograma, never pide otro medio y
    /// customer_action manda al cliente a pagar. El consejo del emisor (advice_code) manda sobre el código.
    /// </summary>
    private static ModuleException Declined(JsonObject details)
    {
        var code = details["upstream"]?[0]?["code"].Text();
        var decline = details["decline_code"].Text();
        details["retry"] = details["advice_code"].Text() switch
        {
            "do_not_try_again" => "never",
            "confirm_card_data" => "customer_action",
            "try_again_later" => "later",
            _ when code == "authentication_required" || decline == "authentication_required" => "customer_action",
            _ when (code is not null && Never.Contains(code)) || (decline is not null && Never.Contains(decline)) => "never",
            _ => "later",
        };
        return new ModuleException(ErrorCodes.Rejected, "Stripe rechazó el cobro con el medio guardado.", details: details);
    }

    private static string Locale(string? language) => language switch
    {
        not null when language.StartsWith("es", StringComparison.OrdinalIgnoreCase) => "es-419",
        not null when language.StartsWith("en", StringComparison.OrdinalIgnoreCase) => "en",
        _ => "auto",
    };

    private static string IntentId(string? id) => IsId(id, "pi_") ? id! : throw Fail.InvalidInput(["payment_external_id"]);

    private static string MethodId(string? id) => IsId(id, "pm_") ? id! : throw Fail.InvalidInput(["method_external_id"]);

    /// <summary>Un ID de Stripe: su prefijo y después solo letras, dígitos y guion bajo, sin nada que se cuele en la ruta.</summary>
    private static bool IsId(string? id, string prefix) =>
        id is { Length: > 3 and <= 255 } && id.StartsWith(prefix, StringComparison.Ordinal) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http";

    // ---- Configuración y sobre ----

    private static StripeConfig Config(ModuleCall call)
    {
        try
        {
            return call.Config<StripeConfig>();
        }
        catch (JsonException)
        {
            throw Fail.InvalidConfig(["config"]);
        }
    }

    /// <summary>Clave secreta o restringida, de prueba o de producción. El prefijo dice el ambiente.</summary>
    private static string ApiKey(ModuleCall call)
    {
        var key = Credential(Config(call).ApiKey, "api_key");
        return key.StartsWith("rk_live_", StringComparison.Ordinal) || key.StartsWith("rk_test_", StringComparison.Ordinal)
            || key.StartsWith("sk_live_", StringComparison.Ordinal) || key.StartsWith("sk_test_", StringComparison.Ordinal)
            ? key
            : throw Fail.InvalidConfig(["api_key"], "La api_key tiene que ser una clave restringida (rk_) o secreta (sk_) de Stripe.");
    }

    private static string WebhookSecret(ModuleCall call)
    {
        var secret = Credential(Config(call).WebhookSecret, "webhook_secret");
        return secret.StartsWith("whsec_", StringComparison.Ordinal)
            ? secret
            : throw Fail.InvalidConfig(["webhook_secret"], "El webhook_secret es el secreto del destino de avisos, el que empieza con whsec_.");
    }

    /// <summary>Un secreto tiene que estar y no llevar espacios ni caracteres de control, que casi siempre son un error al copiarlo.</summary>
    private static string Credential(string? value, string field) =>
        value is { Length: > 0 } && value.All(c => c is > ' ' and < '\u007f')
            ? value
            : throw Fail.InvalidConfig([field], $"El {field} de la instancia falta o tiene espacios o caracteres que no van.");

    private static Task<ModuleResponse> Verified(bool valid, JsonObject? @event = null)
    {
        var events = new JsonArray();
        if (@event is not null)
        {
            events.Add(@event);
        }

        return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["valid"] = valid, ["events"] = events }));
    }

    /// <summary>Un encabezado del pedido original, con el nombre en cualquier caso. Cada valor es un texto o una lista de textos.</summary>
    private static string? Header(JsonNode? headers, string name)
    {
        if (headers is not JsonObject obj)
        {
            return null;
        }

        var value = obj.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return value is JsonArray { Count: > 0 } list ? list[0].Text() : value.Text();
    }
}
