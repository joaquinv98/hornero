using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Pterodactyl;

// Entradas del sobre. Todo es opcional a propósito: lo que falta lo reporta la validación con su ruta, en vez de
// dejar que el deserializador falle sin decir qué campo era.

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class PanelConfig
{
    public string? PanelUrl { get; init; }

    public string? ApplicationKey { get; init; }
}

/// <summary>El panel ya validado: la raíz de la Application API, la del navegador y la clave.</summary>
internal sealed record Panel(Uri Api, Uri Web, string Key)
{
    public static Panel From(ModuleCall call)
    {
        PanelConfig config;
        try
        {
            config = call.Config<PanelConfig>();
        }
        catch (JsonException)
        {
            throw Fail.InvalidConfig(["config"]);
        }

        // Una clave con espacios o caracteres de control casi siempre es un error al copiarla.
        if (config.ApplicationKey is not { Length: > 0 } key || !key.All(c => c is > ' ' and < '\u007f'))
        {
            throw Fail.InvalidConfig(["application_key"], "La application_key de la instancia falta o tiene espacios o caracteres que no van.");
        }

        // Solo https, sin usuario, query ni fragmento: la clave viaja en cada pedido a esta URL.
        if (!Uri.TryCreate(config.PanelUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            throw Fail.InvalidConfig(["panel_url"], "La panel_url tiene que ser una URL https sin usuario, query ni fragmento.");
        }

        var root = url.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return new Panel(new Uri(root + "api/application/"), new Uri(root), key);
    }
}

/// <summary>Contexto de provisioning (sección 8 del protocolo).</summary>
internal sealed class CallContext
{
    public ServiceContext? Service { get; init; }

    public AccountContext? Account { get; init; }

    public static CallContext Read(ModuleCall call)
    {
        try
        {
            return call.Context<CallContext>();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidInput([$"context{ex.Path?.TrimStart('$')}"]);
        }
    }

    /// <summary>El ID del servicio. Es el external_id de los servers importados y de los creados sin uuid.</summary>
    public long ServiceId => Service?.Id is long id && id > 0 ? id : throw Fail.InvalidInput(["context.service.id"]);

    /// <summary>El uuid estable del servicio (context.service.uuid), o null mientras el núcleo no lo mande.</summary>
    public string? ServiceUuid => Service?.Uuid is not { } text
        ? null
        : Guid.TryParseExact(text, "D", out var uuid) && uuid != Guid.Empty ? uuid.ToString("D") : throw Fail.InvalidInput(["context.service.uuid"]);

    /// <summary>
    /// La marca externa de un server nuevo (sección 6 del protocolo), su external_id: el uuid del servicio, que a
    /// diferencia del ID no choca con el de otra instalación que comparte el panel. Sin uuid, el ID.
    /// </summary>
    public string Mark => ServiceUuid ?? ServiceId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Las marcas con que se busca el server del servicio: la de un server nuevo y el ID de los importados.</summary>
    public string[] Marks => ServiceUuid is { } uuid ? [uuid, ServiceId.ToString(CultureInfo.InvariantCulture)] : [ServiceId.ToString(CultureInfo.InvariantCulture)];
}

internal sealed class ServiceContext
{
    public long? Id { get; init; }

    public string? Uuid { get; init; }

    public JsonObject? ExternalIds { get; init; }

    public JsonObject? Params { get; init; }

    public OrderContext? Order { get; init; }
}

internal sealed class OrderContext
{
    public Dictionary<string, JsonNode?>? Variables { get; init; }
}

/// <summary>La cuenta. Nombre y correo llegan porque el manifiesto pide account.contact.</summary>
internal sealed class AccountContext
{
    public long? Id { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }

    public string? Language { get; init; }

    public bool English => Language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Entrada de run_action.</summary>
internal sealed class ActionInput
{
    public string? Action { get; init; }

    public Dictionary<string, string?>? Credentials { get; init; }
}

/// <summary>Código y mensaje de un error del panel.</summary>
internal sealed record Upstream(string Code, string Detail);

/// <summary>Armado de los errores del protocolo con los detalles que usa este módulo.</summary>
internal static class Fail
{
    public static ModuleException InvalidInput(IReadOnlyCollection<string> fields, string? message = null) => new(
        ErrorCodes.InvalidInput,
        message ?? $"Faltan datos o son inválidos: {string.Join(", ", fields)}.",
        details: new JsonObject { ["fields"] = Strings(fields) });

    public static ModuleException InvalidConfig(IReadOnlyCollection<string> fields, string? message = null, int? httpStatus = null)
    {
        var details = new JsonObject { ["fields"] = Strings(fields) };
        if (httpStatus is not null)
        {
            details["http_status"] = httpStatus;
        }

        return new(ErrorCodes.InvalidConfig, message ?? $"La configuración de la instancia no sirve: {string.Join(", ", fields)}.", details: details);
    }

    /// <summary>El panel no respondió y el pedido seguro no se ejecutó, o fue una lectura: repetir no hace daño.</summary>
    public static ModuleException Unavailable(string message, int retryAfter = 30) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    /// <summary>Resultado incierto: el pedido pudo haberse ejecutado, así que se repite con la misma clave.</summary>
    public static ModuleException Unknown(string message, string reason) => new(
        ErrorCodes.OutcomeUnknown, message, retryAfter: 30, details: new JsonObject { ["reason"] = reason });

    /// <summary>El panel rechazó por sus reglas. details.upstream.reason dice por qué, como pide PTR-14.</summary>
    public static ModuleException Rejected(string reason, string message, JsonObject? upstream = null)
    {
        upstream ??= [];
        upstream["reason"] = reason;
        return new(ErrorCodes.Rejected, message, details: new JsonObject { ["upstream"] = upstream });
    }

    public static ModuleException NotFound(string message) => new(ErrorCodes.NotFound, message);

    /// <summary>Una respuesta del panel que no tiene la forma esperada. Reintentable, y nunca se toma como ausencia.</summary>
    public static ModuleException Malformed(string what) => new(ErrorCodes.Internal, $"El panel devolvió una respuesta que no se entiende ({what}).");

    /// <summary>Los campos de un invalid_input, para decidir qué hacer con un 422 del panel.</summary>
    public static IEnumerable<string> Fields(ModuleException ex) =>
        ex.Details?["fields"] is JsonArray fields ? fields.Select(f => f.Text() ?? string.Empty) : [];

    public static int? HttpStatus(ModuleException ex) => ex.Details?["upstream"]?["http_status"].Int() ?? ex.Details?["http_status"].Int();

    public static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}

internal static class Inputs
{
    /// <summary>Lee la entrada de la operación. Un tipo mal puesto sale como invalid_input con la ruta, nunca con el valor.</summary>
    public static T Read<T>(ModuleCall call)
    {
        try
        {
            return call.Input<T>();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidInput([$"input{ex.Path?.TrimStart('$')}"]);
        }
    }
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

    public static int? Int(this JsonNode? node) =>
        int.TryParse(node.Text(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static bool? Bool(this JsonNode? node) => node.Text() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };
}
