using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.MercadoPago;

/// <summary>Las operaciones del módulo. Cada una valida su entrada, llama a Mercado Pago y arma la respuesta del protocolo.</summary>
internal sealed class MercadoPagoOperations(MercadoPagoApi api)
{
    /// <summary>Pagos por página de list_payments. Con unos 400 bytes por pago queda muy lejos del límite de 1 MiB.</summary>
    private const int PageSize = 100;

    private static readonly string[] ReturnResults = ["success", "pending", "failure"];

    /// <summary>Un ts firmado más viejo que esto respecto de cuándo llegó el aviso no vale: una notificación capturada no se repite para siempre.</summary>
    private static readonly TimeSpan WebhookMaxAge = TimeSpan.FromHours(24);

    // ---- Operaciones ----

    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var token = AccessToken(call);
        _ = WebhookSecret(call);
        var account = await api.Get("/users/me", token, call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(new JsonObject
        {
            ["account_id"] = account.Field("id").Text(),
            ["site_id"] = account.Field("site_id").Text(),

            // Un token TEST- es de prueba. Uno APP_USR- puede ser de producción o de un usuario de prueba: el ambiente
            // lo elige el staff en el asistente.
            ["livemode"] = token.StartsWith("TEST-", StringComparison.Ordinal) ? false : null,
        });
    }

    public async Task<ModuleResponse> CreateCheckout(ModuleCall call)
    {
        var preference = Preference(Inputs.Read<CheckoutInput>(call));
        var token = AccessToken(call);

        // No se corta cuando el núcleo se desconecta: con el pedido ya enviado, abandonarlo dejaría el resultado
        // sin guardar. Si se termina, la SDK lo registra por la clave de idempotencia y el reintento lo recupera.
        var response = await api.Post("/checkout/preferences", token, preference, null, CancellationToken.None).ConfigureAwait(false);

        // La redirección la sigue el navegador del cliente: solo https (sección 9.1 del protocolo).
        var url = response.Field("init_point").Text();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw Fail.Unavailable("Mercado Pago no devolvió una URL de checkout https.");
        }

        return ModuleResponse.Completed(new JsonObject { ["redirect_url"] = url });
    }

    public async Task<ModuleResponse> GetPayment(ModuleCall call)
    {
        var id = PaymentId(Inputs.Read<PaymentRef>(call).PaymentExternalId);
        var payment = await api.Get($"/v1/payments/{id}", AccessToken(call), call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(PaymentMapper.ToResult(payment));
    }

    /// <summary>
    /// Verifica la firma y normaliza el evento. Una firma que no vale no es un error: es valid false y ningún evento.
    /// De las notificaciones que no son de pagos se verifica la firma y nada más, porque no hay un pago que consultar.
    /// </summary>
    public Task<ModuleResponse> VerifyWebhook(ModuleCall call)
    {
        var input = Inputs.Read<WebhookInput>(call);
        var secret = WebhookSecret(call);
        var headers = Pairs(input.Headers);
        var query = Pairs(input.Query);
        var dataId = query.GetValueOrDefault("data.id");

        if (!WebhookSignature.TryVerify(secret, headers.GetValueOrDefault("x-signature"), headers.GetValueOrDefault("x-request-id"), dataId, out var ts))
        {
            return Verified(false);
        }

        // Con received_at (el núcleo manda cuándo llegó el aviso) hay ventana: el ts firmado tiene que ser de las
        // últimas 24 horas. Sin él no hay contra qué medir, y queda la unicidad por evento del núcleo.
        if (input.ReceivedAt is { } receivedAt && !WebhookSignature.IsFresh(ts, receivedAt, WebhookMaxAge))
        {
            return Verified(false);
        }

        JsonNode? body = null;
        try
        {
            body = JsonNode.Parse(Convert.FromBase64String(input.BodyBase64 ?? string.Empty));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Con la firma buena y el cuerpo ilegible alcanza lo firmado: el data.id y el type de la URL.
        }

        var type = body.Field("type").Text() ?? query.GetValueOrDefault("type");

        // Un cambio en una suscripción (la aprobó, la pausó o se dio de baja por rechazos). El núcleo la consulta con
        // get_subscription: el aviso solo dice cuál. Todo sale de lo firmado, el data.id de la URL y el ts: un cuerpo que
        // habla de otra suscripción lo tocaron, y repetir el aviso con otro id en el cuerpo no da otro evento.
        if (type == "subscription_preapproval")
        {
            var bodySubscription = body.Field("data").Field("id").Text();
            if (dataId is not null && bodySubscription is not null && !string.Equals(dataId, bodySubscription, StringComparison.OrdinalIgnoreCase))
            {
                return Verified(false);
            }

            return dataId is { Length: > 0 and <= 64 } && dataId.All(char.IsAsciiLetterOrDigit)
                ? Verified(true, new JsonObject
                {
                    ["event_id"] = $"subscription:{dataId}:{ts}",
                    ["type"] = "subscription.updated",
                    ["subscription_external_id"] = dataId,
                })
                : Verified(true);
        }

        if (type != "payment")
        {
            return Verified(true);
        }

        // La firma cubre el data.id de la URL y no el cuerpo: si el cuerpo habla de otro pago, lo tocaron.
        var bodyId = body.Field("data").Field("id").Text();
        if (dataId is not null && bodyId is not null && !string.Equals(dataId, bodyId, StringComparison.OrdinalIgnoreCase))
        {
            return Verified(false);
        }

        var paymentId = dataId ?? bodyId;
        if (paymentId is not { Length: > 0 and <= 20 } || !paymentId.All(char.IsAsciiDigit))
        {
            return Verified(true);
        }

        // Solo lo firmado identifica el evento: el pago y el ts. El id del cuerpo no está firmado, así que una
        // notificación capturada y repetida con otro id no es un evento nuevo.
        return Verified(true, new JsonObject
        {
            ["event_id"] = $"{paymentId}:{ts}",
            ["type"] = body.Field("action").Text() ?? "payment",
            ["payment_external_id"] = paymentId,
        });
    }

    public async Task<ModuleResponse> Refund(ModuleCall call)
    {
        var input = Inputs.Read<RefundInput>(call);
        var id = PaymentId(input.PaymentExternalId);
        var partial = input.Amount;
        if (partial is not null && (!IsCurrency(partial.Currency) || !IsPrice(partial.Value)))
        {
            throw Fail.InvalidInput(["amount"]);
        }

        var token = AccessToken(call);

        // Mercado Pago reembolsa en la moneda del pago y no la pide: un monto pensado en otra moneda movería la plata equivocada.
        var payment = await api.Get($"/v1/payments/{id}", token, call.Aborted).ConfigureAwait(false);
        var currency = payment.Field("currency_id").Text() ?? throw Fail.Unavailable("Mercado Pago devolvió un pago sin currency_id.");
        if (partial is not null && partial.Currency != currency)
        {
            throw Fail.InvalidInput(["amount.currency"], $"El pago es en {currency} y el reembolso se pidió en {partial.Currency}.");
        }

        // Sin amount es un reembolso total. La clave del núcleo viaja como X-Idempotency-Key: repetir el pedido no repite el reembolso.
        var body = new JsonObject();
        if (partial is not null)
        {
            body["amount"] = partial.Value;
        }

        var refund = await api.Post($"/v1/payments/{id}/refunds", token, body, call.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);

        var refunded = refund.Field("amount").Dec() ?? partial?.Value
            ?? throw Fail.Unknown("Mercado Pago no informó el monto reembolsado.", "unexpected_response");
        var gatewayStatus = refund.Field("status").Text();
        return ModuleResponse.Completed(new JsonObject
        {
            ["refund_id"] = refund.Field("id").Text(),
            ["payment_external_id"] = id,
            ["status"] = gatewayStatus == "approved" ? "approved" : "pending",
            ["amount"] = Fmt.Money(refunded, currency),
            ["gateway_status"] = gatewayStatus,
        });
    }

    public async Task<ModuleResponse> ListPayments(ModuleCall call)
    {
        var input = Inputs.Read<ListInput>(call);
        if (input.From is not { } from || input.To is not { } to || from >= to)
        {
            throw Fail.InvalidInput(["from", "to"]);
        }

        var offset = input.Offset ?? 0;
        if (offset < 0)
        {
            throw Fail.InvalidInput(["offset"]);
        }

        var range = input.By switch
        {
            null or "created" => "date_created",
            "approved" => "date_approved",
            "updated" => "date_last_updated",
            _ => throw Fail.InvalidInput(["by"]),
        };

        // Se ordena por id, que no se repite: con la fecha, pagos del mismo instante podrían saltarse entre páginas.
        var query = $"sort=id&criteria=asc&range={range}&begin_date={QueryInstant(from)}&end_date={QueryInstant(to)}&limit={PageSize}&offset={offset}";
        var page = await api.Get($"/v1/payments/search?{query}", AccessToken(call), call.Aborted).ConfigureAwait(false);
        if (page.Field("results") is not JsonArray results)
        {
            throw Fail.Unavailable("Mercado Pago devolvió una búsqueda sin results.");
        }

        var payments = new JsonArray(results.Select(p => (JsonNode?)PaymentMapper.ToResult(p)).ToArray());
        var total = (int?)page.Field("paging").Field("total").Dec() ?? offset + results.Count;
        var next = results.Count > 0 && offset + results.Count < total ? offset + results.Count : (int?)null;
        return ModuleResponse.Completed(new JsonObject
        {
            ["payments"] = payments,
            ["total"] = total,
            ["offset"] = offset,
            ["next_offset"] = next,
        });
    }

    /// <summary>
    /// Crea una suscripción sin plan (preapproval) pendiente de aprobación: el cliente la aprueba en la URL que vuelve
    /// y desde ahí Mercado Pago cobra solo cada período. La referencia del núcleo viaja como external_reference.
    /// </summary>
    public async Task<ModuleResponse> CreateSubscription(ModuleCall call)
    {
        var input = Inputs.Read<SubscriptionInput>(call);
        var email = Inputs.Context<SubscriptionContext>(call)?.Account?.Contact?.Email;
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(input.SubscriptionId) || input.SubscriptionId.Length > 256)
        {
            bad.Add("subscription_id");
        }

        if (!IsCurrency(input.Amount?.Currency) || !IsPrice(input.Amount?.Value))
        {
            bad.Add("amount");
        }

        var frequency = input.Interval switch { "month" => "months", "day" => "days", _ => null };
        if (frequency is null)
        {
            bad.Add("interval");
        }

        if (input.IntervalCount is not (>= 1 and <= 12))
        {
            bad.Add("interval_count");
        }

        if (input.StartDate is null)
        {
            bad.Add("start_date");
        }

        if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 250)
        {
            bad.Add("reason");
        }

        if (input.BackUrl is null || !IsWebUrl(input.BackUrl))
        {
            bad.Add("back_url");
        }

        if (email is null || !System.Net.Mail.MailAddress.TryCreate(email, out _))
        {
            bad.Add("context.account.contact.email");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }

        var body = new JsonObject
        {
            ["reason"] = input.Reason,
            ["external_reference"] = input.SubscriptionId,
            ["payer_email"] = email,
            ["auto_recurring"] = new JsonObject
            {
                ["frequency"] = input.IntervalCount,
                ["frequency_type"] = frequency,

                // Al mediodía UTC, así la fecha no cambia de día en ninguna zona de Latinoamérica.
                ["start_date"] = input.StartDate!.Value.ToString("yyyy-MM-dd'T12:00:00.000Z'", CultureInfo.InvariantCulture),
                ["transaction_amount"] = input.Amount!.Value,
                ["currency_id"] = input.Amount.Currency,
            },
            ["back_url"] = input.BackUrl,
            ["status"] = "pending",
        };

        var created = await api.Post("/preapproval", AccessToken(call), body, call.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        var url = created.Field("init_point").Text();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw Fail.Unknown("Mercado Pago no devolvió una URL https para aprobar la suscripción.", "unexpected_response");
        }

        var result = Subscription(created);
        result["redirect_url"] = url;
        return ModuleResponse.Completed(result);
    }

    public async Task<ModuleResponse> GetSubscription(ModuleCall call)
    {
        var id = SubscriptionId(Inputs.Read<SubscriptionRef>(call).SubscriptionExternalId);
        var subscription = await api.Get($"/preapproval/{id}", AccessToken(call), call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(Subscription(subscription));
    }

    /// <summary>Da de baja la suscripción. Repetirla sobre una ya cancelada da lo mismo.</summary>
    public async Task<ModuleResponse> CancelSubscription(ModuleCall call)
    {
        var id = SubscriptionId(Inputs.Read<SubscriptionRef>(call).SubscriptionExternalId);
        var token = AccessToken(call);
        var current = await api.Get($"/preapproval/{id}", token, call.Aborted).ConfigureAwait(false);
        if (current.Field("status").Text() == "cancelled")
        {
            return ModuleResponse.Completed(Subscription(current));
        }

        var updated = await api.Put($"/preapproval/{id}", token, new JsonObject { ["status"] = "cancelled" }, call.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        return ModuleResponse.Completed(Subscription(updated));
    }

    /// <summary>Cambia el monto de los próximos cobros (payment.subscription_update), en la moneda de la suscripción.</summary>
    public async Task<ModuleResponse> UpdateSubscription(ModuleCall call)
    {
        var input = Inputs.Read<SubscriptionUpdate>(call);
        var id = SubscriptionId(input.SubscriptionExternalId);
        if (!IsCurrency(input.Amount?.Currency) || !IsPrice(input.Amount?.Value))
        {
            throw Fail.InvalidInput(["amount"]);
        }

        var body = new JsonObject
        {
            ["auto_recurring"] = new JsonObject { ["transaction_amount"] = input.Amount!.Value, ["currency_id"] = input.Amount.Currency },
        };
        var updated = await api.Put($"/preapproval/{id}", AccessToken(call), body, call.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        return ModuleResponse.Completed(Subscription(updated));
    }

    // ---- Entradas ----

    /// <summary>
    /// La suscripción de Mercado Pago (preapproval) con la forma del protocolo. Un estado que no conocemos no se
    /// adivina.
    /// </summary>
    private static JsonObject Subscription(JsonNode? preapproval)
    {
        var id = preapproval.Field("id").Text() ?? throw Fail.Unavailable("Mercado Pago devolvió una suscripción sin id.");
        var recurring = preapproval.Field("auto_recurring");
        var currency = recurring.Field("currency_id").Text();
        var amount = recurring.Field("transaction_amount").Dec();
        var gatewayStatus = preapproval.Field("status").Text();
        return new JsonObject
        {
            ["subscription_external_id"] = id,
            ["status"] = gatewayStatus switch
            {
                "pending" => "pending",
                "authorized" => "active",
                "paused" => "paused",
                "cancelled" => "cancelled",
                _ => throw Fail.Unavailable($"Mercado Pago devolvió un estado de suscripción que el módulo no conoce: {gatewayStatus}."),
            },
            ["amount"] = amount is { } value && currency is not null ? Fmt.Money(value, currency) : null,
            ["external_reference"] = preapproval.Field("external_reference").Text(),
            ["next_charge_at"] = Fmt.Instant(preapproval.Field("next_payment_date")),
            ["gateway_status"] = gatewayStatus,
        };
    }

    private static string SubscriptionId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit)
            ? id
            : throw Fail.InvalidInput(["subscription_external_id"]);

    /// <summary>
    /// El cuerpo de la preferencia de Checkout Pro. El cliente paga la suma de los ítems, así que se exige que sea el
    /// monto del intento: si no, el núcleo cobraría una cosa y registraría otra.
    /// </summary>
    private static JsonObject Preference(CheckoutInput input)
    {
        var bad = new List<string>();
        var attempt = input.AttemptId;
        if (string.IsNullOrWhiteSpace(attempt) || attempt.Length > 256)
        {
            bad.Add("attempt_id");
        }

        var currency = input.Amount?.Currency;
        var amount = input.Amount?.Value;
        if (!IsCurrency(currency))
        {
            bad.Add("amount.currency");
        }

        if (!IsPrice(amount))
        {
            bad.Add("amount.value");
        }

        var items = new JsonArray();
        var total = 0m;
        if (input.Items is not { Count: > 0 })
        {
            bad.Add("items");
        }

        foreach (var (i, item) in (input.Items ?? []).Index())
        {
            if (item is null)
            {
                bad.Add($"items[{i}]");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Title))
            {
                bad.Add($"items[{i}].title");
            }

            var quantityOk = item.Quantity is >= 1;
            var priceOk = IsPrice(item.UnitPrice);
            if (!quantityOk)
            {
                bad.Add($"items[{i}].quantity");
            }

            if (!priceOk)
            {
                bad.Add($"items[{i}].unit_price");
            }

            // Solo se suma lo válido: los topes de IsPrice son lo que evita el desborde.
            if (quantityOk && priceOk)
            {
                total += item.Quantity!.Value * item.UnitPrice!.Value;
            }

            var json = new JsonObject
            {
                ["title"] = item.Title,
                ["quantity"] = item.Quantity,
                ["currency_id"] = currency,
                ["unit_price"] = item.UnitPrice,
            };
            if (item.Id is not null)
            {
                json["id"] = item.Id;
            }

            if (item.Description is not null)
            {
                json["description"] = item.Description;
            }

            items.Add(json);
        }

        var back = new JsonObject();
        foreach (var result in ReturnResults)
        {
            if (input.ReturnUrls?.GetValueOrDefault(result) is not { } url)
            {
                continue;
            }

            if (IsWebUrl(url))
            {
                back[result] = url;
            }
            else
            {
                bad.Add($"return_urls.{result}");
            }
        }

        if (input.NotificationUrl is not null && !IsWebUrl(input.NotificationUrl))
        {
            bad.Add("notification_url");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }

        if (total != amount)
        {
            throw Fail.InvalidInput(["items"], $"La suma de los ítems ({Fmt.Amount(total)}) no es el monto del intento ({Fmt.Amount(amount!.Value)}).");
        }

        var body = new JsonObject { ["items"] = items, ["external_reference"] = attempt };
        if (back.Count > 0)
        {
            body["back_urls"] = back;

            // Con la URL de éxito, Mercado Pago devuelve solo al cliente a esa página cuando el pago se aprueba.
            if (back["success"] is not null)
            {
                body["auto_return"] = "approved";
            }
        }

        if (input.NotificationUrl is not null)
        {
            body["notification_url"] = input.NotificationUrl;
        }

        return body;
    }

    private static string PaymentId(string? id) =>
        id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit)
            ? id
            : throw Fail.InvalidInput(["payment_external_id"]);

    private static bool IsCurrency(string? code) => code is { Length: 3 } && code.All(char.IsAsciiLetterUpper);

    /// <summary>
    /// Un precio o un monto: mayor que cero, con los 2 decimales que acepta Mercado Pago y con un tope que deja la
    /// suma de los ítems lejos del desborde de decimal.
    /// </summary>
    private static bool IsPrice(decimal? value) => value is > 0m and < 1_000_000_000_000m && decimal.Round(value.Value, 2) == value.Value;

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http";

    /// <summary>Una fecha para la búsqueda: ISO 8601 en UTC con milésimas, escapada para la URL.</summary>
    private static string QueryInstant(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

    // ---- Configuración y sobre ----

    private static MercadoPagoConfig Config(ModuleCall call)
    {
        try
        {
            return call.Config<MercadoPagoConfig>();
        }
        catch (JsonException)
        {
            throw Fail.InvalidConfig(["config"]);
        }
    }

    private static string AccessToken(ModuleCall call) => Credential(Config(call).AccessToken, "access_token");

    private static string WebhookSecret(ModuleCall call) => Credential(Config(call).WebhookSecret, "webhook_secret");

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

    /// <summary>Encabezados o parámetros del pedido original, con el nombre en cualquier caso. Cada valor es un texto o una lista de textos.</summary>
    private static Dictionary<string, string> Pairs(JsonNode? node)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj)
            {
                if (First(value) is { } text)
                {
                    pairs[name] = text;
                }
            }
        }

        return pairs;
    }

    private static string? First(JsonNode? value) => value is JsonArray { Count: > 0 } list ? First(list[0]) : value.Text();
}
