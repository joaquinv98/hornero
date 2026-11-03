using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.TacticalRmm;

/// <summary>
/// Configuración de la instancia (config_schema del manifiesto), ya validada. Es una clase y no un record a
/// propósito: el ToString de un record imprimiría la API key.
/// </summary>
internal sealed partial class TacticalSettings(Uri apiUrl, string apiKey, string prefix, string siteName, int deployExpiryDays)
{
    public Uri ApiUrl { get; } = apiUrl;

    public string ApiKey { get; } = apiKey;

    public string Prefix { get; } = prefix;

    public string SiteName { get; } = siteName;

    public int DeployExpiryDays { get; } = deployExpiryDays;

    /// <summary>
    /// Lee y valida la configuración. Lo que falla sale como invalid_config con los campos, nunca con los valores.
    /// La URL tiene que ser https y sin usuario, query ni fragmento: la API key viaja a ese destino.
    /// </summary>
    public static TacticalSettings From(ModuleCall call)
    {
        var config = call.Request.Instance.Config;
        var bad = new List<string>();

        Uri? api = null;
        if (config.Field("api_url").Text() is not { } url
            || !Uri.TryCreate(url, UriKind.Absolute, out api)
            || api.Scheme != Uri.UriSchemeHttps
            || api.UserInfo.Length > 0
            || api.Query.Length > 0
            || api.Fragment.Length > 0)
        {
            bad.Add("api_url");
        }

        var key = config.Field("api_key").Text();
        if (string.IsNullOrEmpty(key) || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            bad.Add("api_key");
        }

        // Sin valor por defecto: dos instalaciones de billing-oss contra el mismo Tactical (producción y staging, por
        // ejemplo) comparten IDs de servicio, y con el mismo prefijo una adoptaría los clientes de la otra.
        var prefix = config.Field("client_name_prefix").Text();
        if (prefix is null || !PrefixPattern().IsMatch(prefix))
        {
            bad.Add("client_name_prefix");
        }

        var site = config.Field("site_name").Text() ?? "OficinaPrincipal";
        if (!SitePattern().IsMatch(site))
        {
            bad.Add("site_name");
        }

        var days = config.Field("deploy_expiry_days") is { } node ? node.Int() : 7;
        if (days is not (>= 1 and <= 30))
        {
            bad.Add("deploy_expiry_days");
        }

        if (bad.Count > 0)
        {
            throw Fail.Config(bad, $"La configuración de la instancia no sirve: {string.Join(", ", bad)}.");
        }

        // Con barra final, para que las rutas relativas se sumen a la ruta base y no la reemplacen.
        var root = new Uri(api!.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        return new TacticalSettings(root, key!, prefix!, site, days!.Value);
    }

    /// <summary>La marca del alta: el cliente de Tactical se llama prefijo más ID del servicio, sin datos personales.</summary>
    public string ClientName(long serviceId) => Prefix + serviceId.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,16}$")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("^[A-Za-z0-9 _-]{1,64}$")]
    private static partial Regex SitePattern();
}

/// <summary>El servicio del contexto (sección 8 del protocolo): ID, estado, IDs externos, parámetros e idioma.</summary>
internal sealed record ServiceRef(long? Id, string? Status, JsonObject ExternalIds, JsonObject Params, bool English)
{
    public static ServiceRef From(ModuleCall call)
    {
        var context = call.Request.Context;
        var service = context.Field("service");
        return new ServiceRef(
            service.Field("id").Long(),
            service.Field("status").Text(),
            service.Field("external_ids") as JsonObject ?? [],
            service.Field("params") as JsonObject ?? [],
            context.Field("account").Field("language").Text()?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true);
    }

    public long ServiceId() => Id is > 0 ? Id.Value : throw Fail.Input(["service.id"]);

    public long ClientId() => Positive(ExternalIds.Field("client_id")) ?? throw Fail.Input(["external_ids.client_id"]);

    public long SiteId() => Positive(ExternalIds.Field("site_id")) ?? throw Fail.Input(["external_ids.site_id"]);

    /// <summary>
    /// El nombre que tiene que tener en Tactical el cliente del servicio: el que devolvió el alta o cargó el
    /// importador en external_ids.client_name, y si no viene, la marca del alta.
    /// </summary>
    public string ClientName(TacticalSettings settings) =>
        ExternalIds.Field("client_name").Text() is { Length: > 0 } name ? name : settings.ClientName(ServiceId());

    /// <summary>El límite de agentes del plan, o null si el servicio no lo trae.</summary>
    public int? AgentLimit => Params.Field("agent_limit").Int() is > 0 and var limit ? limit : null;

    /// <summary>
    /// Los parámetros a validar o aplicar: los de input.params si vienen (cambio de plan, validación de un pedido)
    /// y si no los del servicio.
    /// </summary>
    public static int RequiredLimit(ModuleCall call, ServiceRef service)
    {
        var node = call.Request.Input.Field("params") is JsonObject requested ? requested.Field("agent_limit") : service.Params.Field("agent_limit");
        return Positive(node) is { } limit and <= int.MaxValue ? (int)limit : throw Fail.Input(["params.agent_limit"]);
    }

    /// <summary>Un ID de Tactical: entero positivo, como número o como texto. Nunca entra en una ruta sin pasar por acá.</summary>
    public static long? Positive(JsonNode? node) => node.Long() is > 0 and var value ? value : null;
}

/// <summary>Armado de los errores del protocolo con los detalles que usa este módulo.</summary>
internal static class Fail
{
    public static ModuleException Input(IReadOnlyCollection<string> fields, string? message = null, JsonObject? extra = null)
    {
        var details = extra ?? [];
        details["fields"] = Strings(fields);
        return new(ErrorCodes.InvalidInput, message ?? $"Faltan datos o son inválidos: {string.Join(", ", fields)}.", details: details);
    }

    public static ModuleException Config(IReadOnlyCollection<string> fields, string message, string? cause = null, JsonObject? extra = null)
    {
        var details = extra ?? [];
        details["fields"] = Strings(fields);
        if (cause is not null)
        {
            details["cause"] = cause;
        }

        return new(ErrorCodes.InvalidConfig, message, details: details);
    }

    /// <summary>Tactical no respondió y el pedido seguro no se ejecutó, o fue una consulta: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, int retryAfter = 30) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    /// <summary>Resultado incierto: el pedido pudo haberse ejecutado, así que el núcleo repite con la misma clave.</summary>
    public static ModuleException Unknown(string message, string reason) =>
        new(ErrorCodes.OutcomeUnknown, message, retryAfter: 30, details: new JsonObject { ["reason"] = reason });

    public static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}

/// <summary>Lectura tolerante de JSON que no controlamos: lo que no es del tipo esperado da null en vez de una excepción.</summary>
internal static class JsonRead
{
    public static JsonNode? Field(this JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    /// <summary>El texto de un valor. Los números y los booleanos salen tal cual vinieron.</summary>
    public static string? Text(this JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    public static long? Long(this JsonNode? node) =>
        long.TryParse(node.Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static int? Int(this JsonNode? node) =>
        int.TryParse(node.Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static bool IsTrue(this JsonNode? node) => node?.GetValueKind() == JsonValueKind.True;
}
