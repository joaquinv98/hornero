using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.CryptoCloud;

/// <summary>Las operaciones del módulo. Cada una valida su entrada, llama a CryptoCloud y arma la respuesta del protocolo.</summary>
internal sealed class CryptoCloudOperations(CryptoCloudApi api, ILogger logger)
{
    /// <summary>Tope propio de invoice_hours: 30 días. CryptoCloud no documenta uno.</summary>
    public const int MaxInvoiceHours = 720;

    /// <summary>Facturas por página de list_payments: el máximo de CryptoCloud. Con unos 2 KB cada una queda lejos de 1 MiB.</summary>
    private const int PageSize = 100;

    /// <summary>Vida de la factura cuando la instancia no la fija: el ejemplo de la documentación usa 24 horas.</summary>
    public const int DefaultInvoiceHours = 24;

    private static readonly string[] InvoiceStatuses = ["created", "paid", "partial", "overpaid", "canceled"];

    // ---- Operaciones ----

    /// <summary>Valida la configuración y la api_key con la consulta de saldos, que no tiene efectos. No devuelve los saldos.</summary>
    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var config = Config(call);
        _ = ShopId(config);
        _ = SecretKey(config);
        _ = InvoiceHours(config);
        await api.Post("merchant/wallet/balance/all", ApiKey(config), null, mutating: false, call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(new JsonObject());
    }

    /// <summary>
    /// Crea la factura de cobro y devuelve su link. Solo en USD: CryptoCloud informa las comisiones en USD y el núcleo
    /// las toma en la moneda del pago. Las URLs de retorno y de aviso son del proyecto en CryptoCloud, no de cada factura.
    /// </summary>
    public async Task<ModuleResponse> CreateCheckout(ModuleCall call)
    {
        var input = Inputs.Read<CheckoutInput>(call);
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(input.AttemptId) || input.AttemptId.Length > 256)
        {
            bad.Add("attempt_id");
        }

        var currency = input.Amount?.Currency;
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
        {
            bad.Add("amount.currency");
        }

        var amount = input.Amount?.Value;
        if (amount is not (> 0m and < 1_000_000_000_000m) || decimal.Round(amount.Value, 2) != amount)
        {
            bad.Add("amount.value");
        }

        if (bad.Count > 0)
        {
            throw Fail.InvalidInput(bad);
        }

        if (currency != "USD")
        {
            throw Fail.InvalidInput(["amount.currency"], $"Este módulo cobra solo en USD y el intento es en {currency}.");
        }

        var config = Config(call);
        var body = new JsonObject
        {
            ["shop_id"] = ShopId(config),
            ["amount"] = amount,
            ["currency"] = currency,
            ["order_id"] = input.AttemptId,
        };

        // La vida va siempre: así no depende de lo que tenga el proyecto y list_payments sabe cuánto mirar para atrás.
        var hours = InvoiceHours(config) ?? DefaultInvoiceHours;
        body["add_fields"] = new JsonObject { ["time_to_pay"] = new JsonObject { ["hours"] = hours, ["minutes"] = 0 } };

        // No se corta cuando el núcleo se desconecta: con el pedido ya enviado, abandonarlo dejaría el resultado
        // sin guardar. Si se termina, la SDK lo registra por la clave de idempotencia y el reintento lo recupera.
        var created = await api.Post("invoice/create", ApiKey(config), body, mutating: true, CancellationToken.None).ConfigureAwait(false);
        var invoice = created.Field("result");

        // El link lo sigue el navegador del cliente: solo https (sección 9.1 del protocolo). Con un 2xx la factura
        // pudo haberse creado aunque la respuesta no sirva, así que el resultado es incierto.
        var link = invoice.Field("link").Text();
        var uuid = Inputs.InvoiceId(invoice.Field("uuid").Text());
        if (uuid is null || link is null || !Uri.TryCreate(link, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw Fail.Unknown("CryptoCloud no devolvió el uuid y un link https de la factura. Puede haberse creado.", "unexpected_response");
        }

        return ModuleResponse.Completed(new JsonObject { ["redirect_url"] = link, ["checkout_external_id"] = uuid });
    }

    public async Task<ModuleResponse> GetPayment(ModuleCall call)
    {
        var id = Inputs.InvoiceId(Inputs.Read<PaymentRef>(call).PaymentExternalId) ?? throw Fail.InvalidInput(["payment_external_id"]);
        var config = Config(call);
        var body = new JsonObject { ["uuids"] = new JsonArray(id) };
        var found = await api.Post("invoice/merchant/info", ApiKey(config), body, mutating: false, call.Aborted).ConfigureAwait(false);

        // Una respuesta sin lista no prueba que la factura no exista: not_found es definitivo para el núcleo.
        if (found.Field("result") is not JsonArray list)
        {
            throw Fail.Unavailable("CryptoCloud devolvió la consulta de facturas sin result.");
        }

        var invoice = list.FirstOrDefault(i => string.Equals(Inputs.InvoiceId(i.Field("uuid").Text()), id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModuleException(ErrorCodes.NotFound, $"CryptoCloud no tiene la factura {id}.");
        return ModuleResponse.Completed(InvoiceMapper.ToResult(invoice, AllowTestInvoices(config)));
    }

    /// <summary>
    /// Verifica la firma del aviso de pago y normaliza el evento. Una firma que no vale no es un error: es valid false
    /// y ningún evento. El aviso llega en JSON o como formulario, según la configuración del proyecto.
    /// </summary>
    public Task<ModuleResponse> VerifyWebhook(ModuleCall call)
    {
        var input = Inputs.Read<WebhookInput>(call);
        var secret = SecretKey(Config(call));
        var postback = Postback(input.BodyBase64);
        var signature = PostbackToken.Verify(secret, postback.Field("token").Text(), DateTimeOffset.UtcNow);
        if (signature is null)
        {
            return Verified(false);
        }

        // La firma no cubre el cuerpo: la factura sale de ahí y el núcleo confirma siempre con get_payment.
        var info = postback.Field("invoice_info");
        var id = Inputs.InvoiceId(postback.Field("invoice_id").Text() ?? info.Field("uuid").Text());
        if (id is null)
        {
            return Verified(true);
        }

        // El ID del evento es la factura más un hash de la firma, que CryptoCloud genera nueva en cada aviso. Como la
        // firma no cubre el cuerpo, un ID armado solo con datos del cuerpo dejaría que un token filtrado ocupara de
        // antemano el ID de un aviso real. Así, repetir un token da siempre IDs que no son de avisos futuros.
        // Solo se documenta el aviso de pago confirmado: sin el estado de la factura (en formulario) el tipo es paid.
        var state = info.Field("status").Text() is { } s && InvoiceStatuses.Contains(s) ? s : "paid";
        return Verified(true, new JsonObject
        {
            ["event_id"] = $"{id}:{Convert.ToHexStringLower(SHA256.HashData(signature))[..16]}",
            ["type"] = $"invoice.{state}",
            ["payment_external_id"] = id,
        });
    }

    /// <summary>
    /// Facturas de un rango. CryptoCloud filtra por días enteros (dd.mm.yyyy) y por la fecha de creación, así que el
    /// rango se amplía a días UTC completos. Para approved y updated empieza antes, la vida más larga que puede tener
    /// una factura (MaxInvoiceHours): lo que se pagó o cambió en el rango se creó a lo sumo ese tiempo antes, aunque
    /// invoice_hours haya bajado después. Puede traer facturas de más, nunca de menos.
    /// </summary>
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

        var config = Config(call);
        var start = input.By switch
        {
            null or "created" => from,
            "approved" or "updated" => from.AddHours(-MaxInvoiceHours),
            _ => throw Fail.InvalidInput(["by"]),
        };

        // limit es la posición del último registro, no la cantidad: offset 100 y limit 200 traen del 101 al 200.
        var body = new JsonObject
        {
            ["start"] = Day(start),
            ["end"] = Day(to.AddTicks(-1)),
            ["offset"] = offset,
            ["limit"] = offset + PageSize,
        };
        var page = await api.Post("invoice/merchant/list", ApiKey(config), body, mutating: false, call.Aborted).ConfigureAwait(false);
        if (page.Field("result") is not JsonArray results)
        {
            throw Fail.Unavailable("CryptoCloud devolvió una lista sin result.");
        }

        // ponytail: con by updated cada página mira 30 días para atrás. Con el volumen de cobros en cripto no pesa. Si
        // crece, ampliar por invoice_hours y guardar la vida de cada factura.
        var allowTest = AllowTestInvoices(config);
        var payments = new JsonArray();
        foreach (var invoice in results)
        {
            try
            {
                payments.Add(InvoiceMapper.ToResult(invoice, allowTest));
            }
            catch (ModuleException ex)
            {
                // Una factura que no se puede leer, quizás de otro sistema que usa el mismo proyecto, no frena la
                // conciliación del resto. get_payment sí falla con ella. El total y la paginación no cambian.
                logger.LogWarning("list_payments omitió la factura {Uuid}: {Message}", Inputs.InvoiceId(invoice.Field("uuid").Text()) ?? "(sin uuid)", ex.Message);
            }
        }

        var total = (int?)page.Field("all_count").Dec() ?? offset + results.Count;
        var next = results.Count > 0 && offset + results.Count < total ? offset + results.Count : (int?)null;
        return ModuleResponse.Completed(new JsonObject
        {
            ["payments"] = payments,
            ["total"] = total,
            ["offset"] = offset,
            ["next_offset"] = next,
        });
    }

    // ---- Entradas ----

    /// <summary>El cuerpo del aviso como objeto: JSON, o formulario con cada campo como texto. Null si no se puede leer.</summary>
    private static JsonObject? Postback(string? base64)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(base64 ?? string.Empty));
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject json)
            {
                return json;
            }
        }
        catch (JsonException)
        {
        }

        var form = new JsonObject();
        foreach (var (name, value) in QueryHelpers.ParseQuery(text))
        {
            form[name] = value.ToString();
        }

        return form;
    }

    private static string Day(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    // ---- Configuración y sobre ----

    private static CryptoCloudConfig Config(ModuleCall call)
    {
        try
        {
            return call.Config<CryptoCloudConfig>();
        }
        catch (JsonException)
        {
            throw Fail.InvalidConfig(["config"]);
        }
    }

    private static string ShopId(CryptoCloudConfig config) => Credential(config.ShopId, "shop_id");

    private static string ApiKey(CryptoCloudConfig config) => Credential(config.ApiKey, "api_key");

    private static string SecretKey(CryptoCloudConfig config) => Credential(config.SecretKey, "secret_key");

    private static bool AllowTestInvoices(CryptoCloudConfig config) => config.AllowTestInvoices ?? false;

    private static int? InvoiceHours(CryptoCloudConfig config) =>
        config.InvoiceHours is null or (>= 1 and <= MaxInvoiceHours)
            ? config.InvoiceHours
            : throw Fail.InvalidConfig(["invoice_hours"], $"invoice_hours va de 1 a {MaxInvoiceHours}.");

    /// <summary>Un dato de acceso tiene que estar y no llevar espacios ni caracteres de control, que casi siempre son un error al copiarlo.</summary>
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
}
