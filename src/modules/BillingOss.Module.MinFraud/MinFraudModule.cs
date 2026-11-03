using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.MinFraud;

/// <summary>
/// Módulo de fraude (sección 9.6 del protocolo) para MaxMind minFraud, de ejemplo del tipo fraud. Arma el pedido con la
/// forma de la documentación pública de minFraud v2.0 y lo probamos solo contra un MaxMind falso. La prueba contra el
/// servicio real está escrita y queda inconclusa sin credenciales.
/// </summary>
internal static class MinFraudModule
{
    public static ModuleDefinition Create(HttpClient http, ILogger logger)
    {
        var client = new MinFraudClient(http, logger);
        return new ModuleDefinition(Manifest())
            .Operation("fraud", "evaluate", call => Evaluate(client, call))
            .InstanceTest(call => Test(client, call));
    }

    public static JsonObject Manifest() => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = "minfraud",
        ["name"] = "MaxMind minFraud",
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("fraud"),
        ["capabilities"] = new JsonArray("fraud.core"),
        ["config_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["account_id", "license_key"],
              "properties": {
                "account_id": { "type": "string", "pattern": "^[0-9]{1,10}$", "description": "Account ID de MaxMind" },
                "license_key": { "type": "string", "x-secret": true, "description": "License key de MaxMind con permiso para minFraud" },
                "service": { "type": "string", "enum": ["score", "insights", "factors"], "default": "factors", "description": "Factors trae los motivos del puntaje" },
                "environment": { "type": "string", "enum": ["production", "sandbox"], "default": "production" }
              }
            }
            """),

        // El correo viaja como MD5 y el domicilio solo con su permiso (sección 8 del protocolo).
        ["data_scopes"] = new JsonArray("account.contact", "account.address"),
        ["timeouts"] = new JsonObject { ["evaluate"] = 15 },
        ["max_in_flight"] = 8,
    };

    /// <summary>Una IP de la documentación de MaxMind, para probar las credenciales. Cuesta una consulta.</summary>
    internal const string TestIp = "81.2.69.160";

    private static async Task<ModuleResponse> Test(MinFraudClient client, ModuleCall call)
    {
        var config = MinFraudConfig.From(call.Request.Instance.Config);
        var response = await client.Score(config, "score", new JsonObject { ["device"] = new JsonObject { ["ip_address"] = TestIp } }, call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(new JsonObject { ["queries_remaining"] = response["queries_remaining"]?.DeepClone() });
    }

    private static async Task<ModuleResponse> Evaluate(MinFraudClient client, ModuleCall call)
    {
        var config = MinFraudConfig.From(call.Request.Instance.Config);
        var request = Transaction(call.Request.Input, call.Request.Context);
        var response = await client.Score(config, config.Service, request, call.Aborted).ConfigureAwait(false);
        return ModuleResponse.Completed(Result(response));
    }

    /// <summary>El pedido de minFraud a partir de la entrada y el contexto del protocolo. Lo que no llegó no se manda.</summary>
    internal static JsonObject Transaction(JsonObject input, JsonObject context)
    {
        var order = input["order"] as JsonObject ?? throw Invalid("order", "Falta el pedido.");
        var request = input["request"] as JsonObject;
        var account = context["account"] as JsonObject;
        var contact = context["contact"] as JsonObject;
        var address = context["address"] as JsonObject;

        var body = new JsonObject();
        var device = new JsonObject();
        if (Text(request?["ip"]) is { } ip)
        {
            device["ip_address"] = IPAddress.TryParse(ip, out _) ? ip : throw Invalid("request.ip", "La IP no es válida.");
        }

        if (Text(request?["user_agent"]) is { } agent)
        {
            device["user_agent"] = agent.Length > 512 ? agent[..512] : agent;
        }

        if (device.Count > 0)
        {
            body["device"] = device;
        }

        body["event"] = new JsonObject
        {
            ["transaction_id"] = Text(order["id"]) ?? throw Invalid("order.id", "Falta el ID del pedido."),
            ["time"] = Text(order["placed_at"]),
            ["type"] = "purchase",
        };
        if (Text(account?["id"]) is { } accountId)
        {
            body["account"] = new JsonObject { ["user_id"] = accountId };
        }

        if (Text(contact?["email"])?.Trim().ToLowerInvariant() is { Length: > 0 } email && email.Contains('@', StringComparison.Ordinal))
        {
            // minFraud acepta el MD5 del correo en minúsculas: el correo en claro no sale del núcleo.
#pragma warning disable CA5351 // MD5 es el formato que pide minFraud para el correo, no se usa como protección.
            var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(email)));
#pragma warning restore CA5351
            body["email"] = new JsonObject { ["address"] = md5, ["domain"] = email[(email.LastIndexOf('@') + 1)..] };
        }

        var billing = new JsonObject();
        if ((Text(address?["country"]) ?? Text(account?["country"])) is { Length: 2 } country)
        {
            billing["country"] = country.ToUpperInvariant();
        }

        if (address is not null)
        {
            Copy(address, "street", billing, "address");
            Copy(address, "city", billing, "city");
            Copy(address, "postal_code", billing, "postal");
        }

        if (contact is not null)
        {
            Copy(contact, "phone", billing, "phone_number");
        }

        if (billing.Count > 0)
        {
            body["billing"] = billing;
        }

        if (order["amount"] is JsonObject amount && decimal.TryParse(Text(amount["value"]), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            body["order"] = new JsonObject { ["amount"] = value, ["currency"] = Text(amount["currency"]) };
        }

        if (order["items"] is JsonArray items && items.Count > 0)
        {
            body["shopping_cart"] = new JsonArray([.. items.OfType<JsonObject>().Select(i => (JsonNode)new JsonObject
            {
                ["item_id"] = Text(i["product_id"]),
                ["quantity"] = i["quantity"]?.DeepClone(),
            })]);
        }

        return body;
    }

    /// <summary>
    /// El resultado del protocolo: el puntaje de minFraud (0,01 a 99, la probabilidad de fraude en por ciento) y los
    /// motivos que trae según el servicio. Factors trae los motivos del puntaje, Insights algunos datos de la IP, el
    /// correo y el domicilio, y Score solo el riesgo de la IP.
    /// </summary>
    internal static JsonObject Result(JsonNode response)
    {
        var score = response["risk_score"] is JsonValue raw && raw.TryGetValue<decimal>(out var s) ? s
            : throw new ModuleException(ErrorCodes.UpstreamUnavailable, "minFraud respondió sin risk_score.");
        var reasons = new List<(string Code, string Message)>();
        foreach (var group in response["risk_score_reasons"] as JsonArray ?? [])
        {
            foreach (var reason in group?["reasons"] as JsonArray ?? [])
            {
                Add("minfraud", Text(reason?["code"]), Text(reason?["reason"]));
            }
        }

        foreach (var reason in response["ip_address"]?["risk_reasons"] as JsonArray ?? [])
        {
            Add("ip", Text(reason?["code"]), Text(reason?["reason"]));
        }

        if (Flag(response["ip_address"]?["traits"]?["is_anonymous"]))
        {
            Add("ip", "anonymous", "La IP es de un servicio de anonimato, VPN o proxy.");
        }

        if (Flag(response["email"]?["is_disposable"]))
        {
            Add("email", "disposable", "El correo es de un servicio de casillas descartables.");
        }

        if (Flag(response["email"]?["is_high_risk"]))
        {
            Add("email", "high_risk", "MaxMind marca el correo como de alto riesgo.");
        }

        if (response["billing_address"]?["is_in_ip_country"] is JsonValue inCountry && inCountry.TryGetValue<bool>(out var same) && !same)
        {
            Add("billing", "not_in_ip_country", "El país del domicilio no es el de la IP.");
        }

        if (reasons.Count == 0 && response["ip_address"]?["risk"] is JsonValue ipRisk && ipRisk.TryGetValue<decimal>(out var risk) && risk >= 10m)
        {
            Add("ip", "risk", string.Create(CultureInfo.InvariantCulture, $"Riesgo de la IP: {risk:0.##}."));
        }

        return new JsonObject
        {
            ["score"] = decimal.Round(Math.Clamp(score, 0m, 100m), 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture),
            ["reasons"] = new JsonArray([.. reasons.DistinctBy(r => r.Code).Take(20).Select(r => (JsonNode)new JsonObject { ["code"] = r.Code, ["message"] = r.Message })]),
            ["external_id"] = Text(response["id"]),
        };

        void Add(string prefix, string? code, string? message)
        {
            if (code is { Length: > 0 })
            {
                var clean = new string([.. code.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
                var full = $"{prefix}.{clean}";
                reasons.Add((full.Length > 64 ? full[..64] : full, message is { Length: > 300 } ? message[..300] : message ?? code));
            }
        }
    }

    private static void Copy(JsonObject from, string name, JsonObject to, string target)
    {
        if (Text(from[name]) is { Length: > 0 } value)
        {
            to[target] = value;
        }
    }

    private static bool Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string? Text(JsonNode? node) => node is JsonValue v ? v.TryGetValue<string>(out var s) ? s : v.ToJsonString() : null;

    private static ModuleException Invalid(string field, string message) =>
        new(ErrorCodes.InvalidInput, message, details: new JsonObject { ["fields"] = new JsonArray(field) });
}

/// <summary>La configuración de la instancia, validada.</summary>
internal sealed record MinFraudConfig(string AccountId, string LicenseKey, string Service, Uri BaseUrl)
{
    /// <summary>Fijas a propósito: la license key nunca viaja a un destino que alguien configure.</summary>
    public static readonly Uri Production = new("https://minfraud.maxmind.com/minfraud/v2.0/");

    public static readonly Uri Sandbox = new("https://sandbox.maxmind.com/minfraud/v2.0/");

    public static MinFraudConfig From(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var account = config["account_id"]?.GetValue<string>();
        var key = config["license_key"]?.GetValue<string>();
        var service = config["service"]?.GetValue<string>() ?? "factors";
        var environment = config["environment"]?.GetValue<string>() ?? "production";
        var bad = new List<string>();
        if (account is not { Length: > 0 and <= 10 } || !account.All(char.IsAsciiDigit))
        {
            bad.Add("account_id");
        }

        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
        {
            bad.Add("license_key");
        }

        if (service is not ("score" or "insights" or "factors"))
        {
            bad.Add("service");
        }

        if (environment is not ("production" or "sandbox"))
        {
            bad.Add("environment");
        }

        if (bad.Count > 0)
        {
            throw new ModuleException(ErrorCodes.InvalidConfig, $"Configuración inválida: {string.Join(", ", bad)}.", details: new JsonObject { ["fields"] = new JsonArray([.. bad.Select(b => (JsonNode)b)]) });
        }

        return new MinFraudConfig(account!, key!, service, environment == "sandbox" ? Sandbox : Production);
    }
}

/// <summary>Cliente mínimo de minFraud v2.0 con HttpClient: autenticación básica con el account ID y la license key.</summary>
internal sealed class MinFraudClient(HttpClient http, ILogger logger)
{
    public async Task<JsonNode> Score(MinFraudConfig config, string service, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(config.BaseUrl, service));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.AccountId}:{config.LicenseKey}")));
        request.Headers.UserAgent.ParseAdd("BillingOss-MinFraud/0.1");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        int status;
        string text;
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Una evaluación no cambia nada afuera: repetirla es seguro, a lo sumo cuesta otra consulta.
            logger.LogWarning("minFraud sin respuesta: {Type}", ex.GetType().Name);
            throw new ModuleException(ErrorCodes.UpstreamUnavailable, "minFraud no respondió.", 30);
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            json = null;
        }

        if (status is >= 200 and < 300 && json is not null)
        {
            return json;
        }

        var code = Clean(json?["code"]?.ToString(), config.LicenseKey);
        var message = Clean(json?["error"]?.ToString(), config.LicenseKey);
        logger.LogWarning("minFraud respondió HTTP {Status} {Code}", status, code);
        var details = new JsonObject { ["upstream"] = new JsonObject { ["status"] = status, ["code"] = code, ["message"] = message } };
        throw status switch
        {
            401 => new ModuleException(ErrorCodes.InvalidConfig, "MaxMind rechazó el account ID o la license key.", details: details),
            403 => new ModuleException(ErrorCodes.InvalidConfig, "La cuenta de MaxMind no tiene permiso para ese servicio de minFraud.", details: details),
            402 => new ModuleException(ErrorCodes.Rejected, "La cuenta de MaxMind no tiene fondos para más consultas.", details: details),
            400 => new ModuleException(ErrorCodes.InvalidInput, $"minFraud rechazó el pedido: {code}.", details: details),
            429 => new ModuleException(ErrorCodes.RateLimited, "minFraud limitó los pedidos.", 30),
            _ => new ModuleException(ErrorCodes.UpstreamUnavailable, $"minFraud respondió HTTP {status}.", 30, details),
        };
    }

    /// <summary>Lo que devuelve MaxMind nunca arrastra la license key, y se corta en 300 caracteres.</summary>
    private static string Clean(string? text, string secret)
    {
        var clean = (text ?? string.Empty).Replace(secret, "***", StringComparison.Ordinal);
        return clean.Length <= 300 ? clean : clean[..300];
    }
}
