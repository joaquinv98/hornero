using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.PayPal;

/// <summary>
/// Las operaciones del módulo. Cada una valida su entrada, llama a PayPal y arma la respuesta del protocolo.
/// <para>
/// Con Orders v2 la plata se mueve recién al capturar la orden que el cliente aprobó, y el protocolo v0 no tiene ese
/// paso. Lo hace get_payment: el webhook CHECKOUT.ORDER.APPROVED da un evento con "order:" más el ID de la orden, el
/// núcleo pide ese pago y el módulo captura. Capturar es seguro de repetir: lleva un PayPal-Request-Id fijo por orden y
/// PayPal no captura dos veces la misma orden. Desde ahí el pago es la captura, con su ID.
/// </para>
/// </summary>
internal sealed class PayPalOperations(PayPalApi api, ILogger logger)
{
    /// <summary>Marca de las órdenes de este módulo, en reference_id. Solo se capturan las que la tienen.</summary>
    public const string Marker = "billing-oss";

    /// <summary>Prefijo del ID externo de una orden aprobada que todavía hay que capturar.</summary>
    public const string OrderPrefix = "order:";

    /// <summary>Transacciones por página de list_payments. Cada captura que aparece se consulta aparte.</summary>
    public const int PageSize = 20;

    /// <summary>Eventos que tiene que tener el webhook: sin el primero nadie captura, y los otros cambian el estado de un pago cobrado.</summary>
    internal static readonly string[] RequiredEvents =
        ["CHECKOUT.ORDER.APPROVED", "PAYMENT.CAPTURE.COMPLETED", "PAYMENT.CAPTURE.REFUNDED", "PAYMENT.CAPTURE.REVERSED"];

    // ---- Operaciones ----

    /// <summary>Valida las credenciales con el token y lee el webhook configurado, que tiene que tener los eventos que se usan.</summary>
    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var access = Access(call);
        var webhookId = WebhookId(call);
        JsonNode webhook;
        try
        {
            webhook = await api.Get(access, $"/v1/notifications/webhooks/{webhookId}", call.Aborted).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            throw Fail.InvalidConfig(["webhook_id"], "El webhook_id no existe en la app de PayPal de estas credenciales.");
        }

        var events = webhook.Field("event_types").Items().Select(e => e.Field("name").Text()).OfType<string>().ToList();
        var missing = events.Contains("*") ? [] : RequiredEvents.Except(events).ToList();
        if (missing.Count > 0)
        {
            throw Fail.InvalidConfig(["webhook_id"], $"Al webhook de PayPal le faltan eventos: {string.Join(", ", missing)}.");
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["environment"] = access.BaseUrl == PayPalApi.Live ? "live" : "sandbox",

            // El asistente de cobros del núcleo compara el ambiente con el de la instancia.
            ["livemode"] = access.BaseUrl == PayPalApi.Live,
            ["webhook_url"] = webhook.Field("url").Text(),
            ["event_types"] = new JsonArray(events.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
        });
    }

    public async Task<ModuleResponse> CreateCheckout(ModuleCall call)
    {
        var input = Inputs.Read<CheckoutInput>(call);
        var order = Order(input);
        var access = Access(call);

        // El PayPal-Request-Id sale del intento, que es aleatorio: no choca con otro sistema que use la misma cuenta y una
        // repetición devuelve la misma orden. No se corta cuando el núcleo se desconecta: con el pedido ya enviado,
        // abandonarlo dejaría el resultado sin guardar.
        var created = await api.Post(access, "/v2/checkout/orders", order.ToJsonString(), $"checkout-{input.AttemptId}", true, CancellationToken.None)
            .ConfigureAwait(false);

        // La redirección la sigue el navegador del cliente: solo https (sección 9.1 del protocolo).
        var url = Links.Href(created, "payer-action") ?? Links.Href(created, "approve");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw Fail.Unknown("PayPal no devolvió una URL https para aprobar la orden.", "unexpected_response");
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["redirect_url"] = url,
            ["checkout_external_id"] = created.Field("id").Text(),
        });
    }

    /// <summary>
    /// El pago de una captura, o el de una orden ("order:" más su ID) que se captura si está aprobada. Las dos formas
    /// devuelven la captura con su ID como payment_external_id.
    /// </summary>
    public async Task<ModuleResponse> GetPayment(ModuleCall call)
    {
        var external = Inputs.Read<PaymentRef>(call).PaymentExternalId;
        var access = Access(call);
        if (external is not null && external.StartsWith(OrderPrefix, StringComparison.Ordinal))
        {
            return ModuleResponse.Completed(await FromOrder(access, IdOf(external[OrderPrefix.Length..]), call.Aborted).ConfigureAwait(false));
        }

        var id = IdOf(external);
        var capture = await api.Get(access, $"/v2/payments/captures/{id}", call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(await FromCapture(access, capture, call.Aborted).ConfigureAwait(false));
    }

    /// <summary>
    /// Verifica la firma con la API de PayPal y normaliza el evento. Una firma que no vale no es un error: es valid false
    /// y ningún evento. De los eventos que no son de órdenes, capturas o disputas se verifica la firma y nada más.
    /// </summary>
    public async Task<ModuleResponse> VerifyWebhook(ModuleCall call)
    {
        var input = Inputs.Read<WebhookInput>(call);
        var access = Access(call);
        var webhookId = WebhookId(call);
        var headers = Pairs(input.Headers);
        var transmission = new JsonObject
        {
            ["auth_algo"] = Header(headers, "paypal-auth-algo", 100, alphanumeric: true),
            ["cert_url"] = Header(headers, "paypal-cert-url", 500),
            ["transmission_id"] = Header(headers, "paypal-transmission-id", 50),
            ["transmission_sig"] = Header(headers, "paypal-transmission-sig", 500),
            ["transmission_time"] = Header(headers, "paypal-transmission-time", 100),
            ["webhook_id"] = webhookId,
        };
        // Los formatos que exige la verificación se comprueban acá: lo que no los cumple no es de PayPal. Así un 4xx de
        // PayPal es un problema nuestro (el webhook_id, la app o el pedido) y sale como error, no como una firma falsa
        // que el núcleo descartaría para siempre.
        if (transmission.Any(p => p.Value is null)
            || !Uri.TryCreate(transmission["cert_url"]!.Text(), UriKind.Absolute, out var cert) || cert.Scheme != Uri.UriSchemeHttps
            || !DateTimeOffset.TryParse(transmission["transmission_time"]!.Text(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !IsTransmissionToken(transmission["transmission_id"]!.Text()!)
            || !IsTransmissionToken(transmission["transmission_sig"]!.Text()!)
            || RawEvent(input.BodyBase64) is not { } received)
        {
            return Verified(false);
        }

        // PayPal pide el evento exactamente como llegó: se pega el cuerpo crudo, nunca se vuelve a serializar. Ya se
        // comprobó que es un único objeto JSON, así que no puede escaparse del campo. Una firma falsa da 200 con FAILURE.
        var (raw, @event) = received;
        var head = transmission.ToJsonString();
        var json = $"{head[..^1]},\"webhook_event\":{raw}}}";
        JsonNode verification;
        try
        {
            verification = await api.Post(access, "/v1/notifications/verify-webhook-signature", json, null, false, call.Aborted).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ex.Code is ErrorCodes.Rejected or ErrorCodes.InvalidConfig)
        {
            // Mientras dure, ningún aviso se procesa, ni la aprobación que dispara la captura: es para el staff.
            logger.LogError("PayPal rechazó la verificación de un webhook ({Code}). Revisar webhook_id y la app: los avisos quedan sin procesar.", ex.Code);
            throw;
        }

        if (verification.Field("verification_status").Text() != "SUCCESS")
        {
            return Verified(false);
        }

        var eventId = @event.Field("id").Text();
        var type = @event.Field("event_type").Text() ?? string.Empty;
        if (string.IsNullOrEmpty(eventId))
        {
            return Verified(true);
        }

        var payments = Payments(type, @event.Field("resource"));
        return Verified(true, payments.Select(p => new JsonObject
        {
            // Una disputa puede tocar varios pagos: cada uno es un evento aparte para la unicidad del núcleo.
            ["event_id"] = payments.Count == 1 ? eventId : $"{eventId}:{p}",
            ["type"] = type,
            ["payment_external_id"] = p,
        }));
    }

    public async Task<ModuleResponse> Refund(ModuleCall call)
    {
        var input = Inputs.Read<RefundInput>(call);
        var id = IdOf(input.PaymentExternalId);
        var partial = input.Amount;
        if (partial is not null && (Currencies.Decimals(partial.Currency) is not { } decimals || !Currencies.IsAmount(partial.Value, decimals)))
        {
            throw Fail.InvalidInput(["amount"]);
        }

        var access = Access(call);

        // Sin amount PayPal reembolsa lo que queda sin reembolsar. Con amount, la moneda va en el pedido y PayPal rechaza
        // la que no es la de la captura, así que no hace falta consultarla antes. La clave del núcleo viaja como
        // PayPal-Request-Id, junto con la captura: repetir el pedido no repite el reembolso.
        var body = partial is null ? "{}" : new JsonObject { ["amount"] = Currencies.Money(partial.Value!.Value, partial.Currency!) }.ToJsonString();
        var refund = await api.Post(access, $"/v2/payments/captures/{id}/refund", body, $"refund-{id}-{call.IdempotencyKey}", true, CancellationToken.None)
            .ConfigureAwait(false);

        var currency = refund.Field("amount").Field("currency_code").Text() ?? partial?.Currency
            ?? throw Fail.Unknown("PayPal no informó la moneda del reembolso.", "unexpected_response");
        var refunded = refund.Field("amount").Field("value").Dec() ?? partial?.Value
            ?? throw Fail.Unknown("PayPal no informó el monto reembolsado.", "unexpected_response");
        var breakdown = refund.Field("seller_payable_breakdown");
        var gatewayStatus = refund.Field("status").Text();

        // Un reembolso que PayPal no completó no movió plata: es el mismo rechazo que un 422, y status queda en approved o
        // pending como en el contrato de Mercado Pago.
        if (gatewayStatus is "CANCELLED" or "FAILED")
        {
            throw new ModuleException(ErrorCodes.Rejected, $"PayPal no completó el reembolso ({gatewayStatus}).", details: new JsonObject
            {
                ["upstream"] = new JsonArray(new JsonObject { ["code"] = gatewayStatus, ["message"] = refund.Field("status_details").Field("reason").Text() }),
                ["refund_id"] = refund.Field("id").Text(),
                ["gateway_status"] = gatewayStatus,
            });
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["refund_id"] = refund.Field("id").Text(),
            ["payment_external_id"] = id,
            ["status"] = gatewayStatus == "COMPLETED" ? "approved" : "pending",
            ["amount"] = Fmt.Money(refunded, currency),
            ["fee_refunded"] = SameCurrency(breakdown.Field("paypal_fee"), currency),
            ["total_refunded"] = SameCurrency(breakdown.Field("total_refunded_amount"), currency),
            ["gateway_status"] = gatewayStatus,
        });
    }

    /// <summary>
    /// Las capturas que tuvieron movimiento en el rango, con su estado actual. Busca las transacciones (Transaction search)
    /// y consulta cada captura que aparece: un cobro por su propio ID, y un reembolso, una reversión o un contracargo por
    /// la captura a la que se refiere. Así un reembolso de un pago viejo aparece en el rango en que se hizo.
    /// </summary>
    public async Task<ModuleResponse> ListPayments(ModuleCall call)
    {
        var input = Inputs.Read<ListInput>(call);
        if (input.From is not { } from || input.To is not { } to || from >= to)
        {
            throw Fail.InvalidInput(["from", "to"]);
        }

        if (to - from > TimeSpan.FromDays(31))
        {
            throw Fail.InvalidInput(["from", "to"], "PayPal busca transacciones en rangos de hasta 31 días.");
        }

        var offset = input.Offset ?? 0;
        if (offset < 0 || offset % PageSize != 0)
        {
            throw Fail.InvalidInput(["offset"]);
        }

        // PayPal solo filtra por la fecha de la transacción. Un cambio en un pago es una transacción nueva, así que las
        // tres formas dan lo mismo.
        if (input.By is not (null or "created" or "approved" or "updated"))
        {
            throw Fail.InvalidInput(["by"]);
        }

        var access = Access(call);
        var page = offset / PageSize + 1;
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"start_date={QueryInstant(from)}&end_date={QueryInstant(to)}&fields=transaction_info&page_size={PageSize}&page={page}");
        var search = await api.Get(access, $"/v1/reporting/transactions?{query}", call.Aborted).ConfigureAwait(false);
        if (search.Field("transaction_details") is not JsonArray details)
        {
            throw Fail.Unavailable("PayPal devolvió una búsqueda sin transaction_details.");
        }

        var payments = new JsonArray();
        foreach (var captureId in details.Select(d => Touched(d.Field("transaction_info"))).OfType<string>().Distinct())
        {
            try
            {
                var capture = await api.Get(access, $"/v2/payments/captures/{captureId}", call.Aborted).ConfigureAwait(false);
                payments.Add(await FromCapture(access, capture, call.Aborted).ConfigureAwait(false));
            }
            catch (ModuleException ex) when (ex.Code is ErrorCodes.NotFound or ErrorCodes.Rejected)
            {
                // Una transacción que no es una captura de la API de órdenes (404), o una captura de otra app de la
                // cuenta que esta no puede leer (403 PERMISSION_DENIED). PayPalApi ya dejó en el log la ruta con el ID.
                // Si cortara la página, la conciliación fallaría en cada corrida mientras siga en la ventana.
            }
        }

        var total = (int?)search.Field("total_items").Dec() ?? offset + details.Count;
        var pages = (int?)search.Field("total_pages").Dec() ?? page;
        return ModuleResponse.Completed(new JsonObject
        {
            ["payments"] = payments,
            ["total"] = total,
            ["offset"] = offset,
            ["next_offset"] = page < pages && details.Count > 0 ? offset + PageSize : null,
        });
    }

    // ---- Órdenes y capturas ----

    /// <summary>La orden aprobada se captura, la que ya se capturó se lee, y las demás todavía no tienen un pago.</summary>
    private async Task<JsonObject> FromOrder(PayPalAccess access, string orderId, CancellationToken ct)
    {
        var order = await api.Get(access, $"/v2/checkout/orders/{orderId}", ct).ConfigureAwait(false);
        var unit = order.Field("purchase_units").Items().FirstOrDefault();

        // Otro sistema puede usar la misma cuenta de PayPal: sus órdenes las captura él.
        if (unit.Field("reference_id").Text() != Marker)
        {
            throw new ModuleException(ErrorCodes.NotFound, $"La orden {orderId} no es de billing-oss.");
        }

        var status = order.Field("status").Text();
        if (status == "APPROVED")
        {
            order = await Capture(access, orderId, ct).ConfigureAwait(false);
            unit = order.Field("purchase_units").Items().FirstOrDefault();
        }
        else if (status != "COMPLETED")
        {
            throw new ModuleException(ErrorCodes.NotFound, $"La orden {orderId} está {status}: no tiene un pago.");
        }

        // PayPal puede contestar la captura en diferido (202): la orden todavía no la muestra y se vuelve a pedir.
        var capture = unit.Field("payments").Field("captures").Items().LastOrDefault()
            ?? throw Fail.Unavailable($"La captura de la orden {orderId} sigue en proceso en PayPal.");
        var id = capture.Field("id").Text() ?? throw Fail.Unavailable("PayPal devolvió una captura sin id.");
        return PaymentMapper.ToResult(capture, unit.Field("custom_id").Text(), PaymentMapper.Refunded(order, id));
    }

    /// <summary>
    /// POST /v2/checkout/orders/{id}/capture con un PayPal-Request-Id fijo por orden. Si otra réplica ya la capturó,
    /// PayPal contesta ORDER_ALREADY_CAPTURED y se lee la orden.
    /// </summary>
    private async Task<JsonNode> Capture(PayPalAccess access, string orderId, CancellationToken ct)
    {
        try
        {
            // No se corta si el núcleo se desconecta: es plata, y el resultado tiene que quedar en PayPal.
            return await api.Post(access, $"/v2/checkout/orders/{orderId}/capture", "{}", $"capture-{orderId}", true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ModuleException ex) when (Fail.Has(ex, "ORDER_ALREADY_CAPTURED"))
        {
            return await api.Get(access, $"/v2/checkout/orders/{orderId}", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// La captura leída sola. Si tiene un reembolso parcial, lo reembolsado sale de su orden. Si cobró plata, se mira si
    /// tiene disputas, que la captura sola no muestra.
    /// </summary>
    private async Task<JsonObject> FromCapture(PayPalAccess access, JsonNode capture, CancellationToken ct)
    {
        var id = capture.Field("id").Text() ?? string.Empty;
        var gatewayStatus = capture.Field("status").Text();
        decimal? refunded = null;
        if (gatewayStatus == "PARTIALLY_REFUNDED"
            && capture.Field("supplementary_data").Field("related_ids").Field("order_id").Text() is { } orderId
            && Ids.IsValid(orderId))
        {
            try
            {
                var order = await api.Get(access, $"/v2/checkout/orders/{orderId}", ct).ConfigureAwait(false);
                refunded = PaymentMapper.Refunded(order, id);
            }
            catch (ModuleException ex) when (ex.Code is ErrorCodes.NotFound or ErrorCodes.Rejected)
            {
                // La orden es de otra app de la cuenta: lo reembolsado queda sin informar, igual que sin orden.
            }
        }

        var payment = PaymentMapper.ToResult(capture, null, refunded);
        if (gatewayStatus is "COMPLETED" or "PENDING" or "PARTIALLY_REFUNDED" or "REFUNDED"
            && await DisputeStatus(access, id, ct).ConfigureAwait(false) is { } disputed)
        {
            payment["status"] = disputed;
        }

        return payment;
    }

    /// <summary>
    /// El estado que le da a la captura una disputa (GET /v1/customer/disputes), o null si no tiene. Con una disputa
    /// abierta la captura puede seguir COMPLETED, y con una perdida queda REFUNDED como un reembolso cualquiera. Por eso:
    /// una abierta da disputed, y una que PayPal resolvió a favor del comprador da charged_back. El resultado de una
    /// disputa resuelta solo está en la disputa completa. Si la app no tiene la función de disputas, no se informa.
    /// </summary>
    private async Task<string?> DisputeStatus(PayPalAccess access, string captureId, CancellationToken ct)
    {
        try
        {
            var search = await api.Get(access, $"/v1/customer/disputes?disputed_transaction_id={Uri.EscapeDataString(captureId)}", ct).ConfigureAwait(false);
            var disputes = search.Field("items").Items().ToList();
            if (disputes.Any(d => d.Field("status").Text() is { } s && s != "RESOLVED"))
            {
                return "disputed";
            }

            foreach (var disputeId in disputes.Select(d => d.Field("dispute_id").Text()).OfType<string>())
            {
                // Los IDs de disputa son letras, dígitos y guiones (PP-D-27803).
                if (disputeId.Length is 0 or > 255 || !disputeId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    continue;
                }

                var dispute = await api.Get(access, $"/v1/customer/disputes/{disputeId}", ct).ConfigureAwait(false);
                if (dispute.Field("dispute_outcome").Field("outcome_code").Text() is "RESOLVED_BUYER_FAVOUR" or "ACCEPTED")
                {
                    return "charged_back";
                }
            }

            return null;
        }
        catch (ModuleException ex) when (ex.Code is ErrorCodes.InvalidConfig or ErrorCodes.Rejected or ErrorCodes.NotFound)
        {
            // La app sin la función de disputas (403) o una disputa que no puede leer. PayPalApi ya dejó el aviso en el
            // log: se informa la captura con su propio estado.
            return null;
        }
    }

    /// <summary>
    /// Los pagos de un evento verificado. Una orden aprobada de este módulo da "order:" y su ID, para que el núcleo pida
    /// el pago y se capture. Una captura da su ID. Un reembolso o una reversión, la captura de su enlace up. Una
    /// disputa, las capturas que reclama.
    /// </summary>
    private static List<string> Payments(string type, JsonNode? resource)
    {
        var id = resource.Field("id").Text();
        IEnumerable<string?> ids = type switch
        {
            "CHECKOUT.ORDER.APPROVED" when resource.Field("purchase_units").Items().FirstOrDefault().Field("reference_id").Text() == Marker
                => Ids.IsValid(id) ? [OrderPrefix + id] : [],
            "PAYMENT.CAPTURE.REFUNDED" or "PAYMENT.CAPTURE.REVERSED" => [Links.CaptureOf(resource)],
            _ when type.StartsWith("PAYMENT.CAPTURE.", StringComparison.Ordinal) => [id],
            _ when type.StartsWith("CUSTOMER.DISPUTE.", StringComparison.Ordinal) =>
                resource.Field("disputed_transactions").Items().Select(t => t.Field("seller_transaction_id").Text()),
            _ => [],
        };
        return ids.OfType<string>().Where(i => i.StartsWith(OrderPrefix, StringComparison.Ordinal) || Ids.IsValid(i)).Distinct().ToList();
    }

    /// <summary>
    /// La captura que tocó una transacción de la búsqueda. Un cobro por la API de pagos (T0006) o por el botón (T0007),
    /// con monto positivo, es la captura. Los demás T00xx son cobros de otro tipo de la cuenta compartida. Un reembolso o
    /// una reversión (T11xx) y un ajuste o contracargo (T12xx) apuntan a la captura en paypal_reference_id.
    /// </summary>
    private static string? Touched(JsonNode? info)
    {
        var code = info.Field("transaction_event_code").Text() ?? string.Empty;
        var id = code is "T0006" or "T0007" && info.Field("transaction_amount").Field("value").Dec() > 0m
            ? info.Field("transaction_id").Text()
            : (code.StartsWith("T11", StringComparison.Ordinal) || code.StartsWith("T12", StringComparison.Ordinal))
              && info.Field("paypal_reference_id_type").Text() == "TXN"
                ? info.Field("paypal_reference_id").Text()
                : null;
        return Ids.IsValid(id) ? id : null;
    }

    // ---- Entradas ----

    /// <summary>
    /// El cuerpo de la orden. El cliente paga la suma de los ítems, así que se exige que sea el monto del intento: si no,
    /// el núcleo cobraría una cosa y registraría otra.
    /// </summary>
    private static JsonObject Order(CheckoutInput input)
    {
        var bad = new List<string>();
        var attempt = input.AttemptId;

        // Viaja como custom_id, invoice_id (hasta 127 y único en la cuenta) y en el PayPal-Request-Id.
        if (attempt is not { Length: > 0 and <= 127 } || !attempt.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            bad.Add("attempt_id");
        }

        var currency = input.Amount?.Currency;
        var amount = input.Amount?.Value;
        var decimals = Currencies.Decimals(currency);
        if (decimals is null)
        {
            bad.Add("amount.currency");
        }

        if (!Currencies.IsAmount(amount, decimals ?? 2))
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
            var priceOk = Currencies.IsAmount(item.UnitPrice, decimals ?? 2);
            if (!quantityOk)
            {
                bad.Add($"items[{i}].quantity");
            }

            if (!priceOk)
            {
                bad.Add($"items[{i}].unit_price");
            }

            // Solo se suma lo válido: los topes de IsAmount son lo que evita el desborde.
            if (quantityOk && priceOk)
            {
                total += item.Quantity!.Value * item.UnitPrice!.Value;
            }

            if (bad.Count == 0)
            {
                var json = new JsonObject
                {
                    ["name"] = Fmt.Truncate(item.Title!, 127),
                    ["quantity"] = item.Quantity!.Value.ToString(CultureInfo.InvariantCulture),
                    ["unit_amount"] = Currencies.Money(item.UnitPrice!.Value, currency!),
                    ["category"] = "DIGITAL_GOODS",
                };
                if (item.Id is not null)
                {
                    json["sku"] = Fmt.Truncate(item.Id, 127);
                }

                if (item.Description is not null)
                {
                    json["description"] = Fmt.Truncate(item.Description, 2048);
                }

                items.Add(json);
            }
        }

        // Sin la URL de vuelta el cliente queda en PayPal después de aprobar. La de cancelación es la de falla, si viene.
        var back = input.ReturnUrls?.GetValueOrDefault("success");
        var cancel = input.ReturnUrls?.GetValueOrDefault("failure") ?? back;
        if (back is null || !IsWebUrl(back))
        {
            bad.Add("return_urls.success");
        }
        else if (!IsWebUrl(cancel!))
        {
            bad.Add("return_urls.failure");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }

        if (total != amount)
        {
            throw Fail.InvalidInput(["items"], $"La suma de los ítems ({Fmt.Amount(total)}) no es el monto del intento ({Fmt.Amount(amount!.Value)}).");
        }

        var money = Currencies.Money(amount!.Value, currency!);
        money["breakdown"] = new JsonObject { ["item_total"] = Currencies.Money(amount.Value, currency!) };
        return new JsonObject
        {
            ["intent"] = "CAPTURE",
            ["purchase_units"] = new JsonArray(new JsonObject
            {
                ["reference_id"] = Marker,
                ["custom_id"] = attempt,
                ["invoice_id"] = attempt,
                ["description"] = Fmt.Truncate(string.Join(", ", input.Items!.Select(i => i.Title)), 127),
                ["amount"] = money,
                ["items"] = items,
            }),
            ["payment_source"] = new JsonObject
            {
                ["paypal"] = new JsonObject
                {
                    ["experience_context"] = new JsonObject
                    {
                        ["return_url"] = back,
                        ["cancel_url"] = cancel,
                        ["user_action"] = "PAY_NOW",
                        ["shipping_preference"] = "NO_SHIPPING",
                    },
                },
            },
        };
    }

    /// <summary>El ID de una captura u orden. Va en la ruta de la API, así que solo letras y dígitos.</summary>
    private static string IdOf(string? id) => Ids.IsValid(id) ? id! : throw Fail.InvalidInput(["payment_external_id"]);

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http";

    /// <summary>Una fecha para la búsqueda: RFC 3339 en UTC con segundos, escapada para la URL.</summary>
    private static string QueryInstant(DateTimeOffset value) => Uri.EscapeDataString(Fmt.Instant(value));

    /// <summary>Un money de PayPal en la moneda dada, o null si falta o viene en otra.</summary>
    private static JsonObject? SameCurrency(JsonNode? money, string currency) =>
        money.Field("currency_code").Text() == currency && money.Field("value").Dec() is { } value ? Fmt.Money(value, currency) : null;

    /// <summary>El cuerpo del webhook, si es un único objeto JSON en UTF-8 válido. Count lee las propiedades acá, dentro del try.</summary>
    private static (string Raw, JsonObject Event)? RawEvent(string? base64)
    {
        try
        {
            var raw = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64 ?? string.Empty));
            return JsonNode.Parse(raw) is JsonObject { Count: > 0 } @event ? (raw, @event) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    // ---- Configuración y sobre ----

    private static PayPalConfig Config(ModuleCall call)
    {
        try
        {
            return call.Config<PayPalConfig>();
        }
        catch (JsonException)
        {
            throw Fail.InvalidConfig(["config"]);
        }
    }

    private static PayPalAccess Access(ModuleCall call)
    {
        var config = Config(call);
        var baseUrl = config.Environment switch
        {
            "live" => PayPalApi.Live,
            "sandbox" => PayPalApi.Sandbox,
            _ => throw Fail.InvalidConfig(["environment"], "El environment de la instancia tiene que ser live o sandbox."),
        };
        return new PayPalAccess(baseUrl, Credential(config.ClientId, "client_id"), Credential(config.ClientSecret, "client_secret"));
    }

    private static string WebhookId(ModuleCall call) =>
        Config(call).WebhookId is { Length: > 0 and <= 50 } id && id.All(char.IsAsciiLetterOrDigit)
            ? id
            : throw Fail.InvalidConfig(["webhook_id"], "El webhook_id de la instancia falta o no tiene la forma de un ID de PayPal.");

    /// <summary>Una credencial tiene que estar y no llevar espacios ni caracteres de control, que casi siempre son un error al copiarla.</summary>
    private static string Credential(string? value, string field) =>
        value is { Length: > 0 } && value.All(c => c is > ' ' and < '\u007f')
            ? value
            : throw Fail.InvalidConfig([field], $"El {field} de la instancia falta o tiene espacios o caracteres que no van.");

    /// <summary>Un encabezado de PayPal, si está y entra en lo que acepta la verificación.</summary>
    private static string? Header(Dictionary<string, string> headers, string name, int max, bool alphanumeric = false) =>
        headers.GetValueOrDefault(name) is { Length: > 0 } value && value.Length <= max
        && value.All(c => alphanumeric ? char.IsAsciiLetterOrDigit(c) : c is > ' ' and < '\u007f')
            ? value
            : null;

    /// <summary>
    /// El patrón de PayPal para el ID y la firma de la transmisión (^(?!\d+$)\w+\S+): empieza con una letra, un dígito o
    /// un guion bajo, tiene al menos dos caracteres y no son todos dígitos. Header ya descartó los espacios.
    /// </summary>
    private static bool IsTransmissionToken(string value) =>
        value.Length >= 2 && (char.IsAsciiLetterOrDigit(value[0]) || value[0] == '_') && !value.All(char.IsAsciiDigit);

    private static ModuleResponse Verified(bool valid, IEnumerable<JsonObject>? events = null) =>
        ModuleResponse.Completed(new JsonObject { ["valid"] = valid, ["events"] = new JsonArray([.. events ?? []]) });

    /// <summary>Encabezados del pedido original, con el nombre en cualquier caso. Cada valor es un texto o una lista de textos.</summary>
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
