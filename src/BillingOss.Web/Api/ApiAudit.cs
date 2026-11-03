using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;

namespace BillingOss.Web.Api;

/// <summary>
/// Auditoría de las APIs (D-15): cada pedido con efecto de una credencial válida queda con su actor, la ruta, el
/// estado HTTP y los IDs de la respuesta. Nunca el cuerpo: puede traer datos personales o una credencial nueva.
/// </summary>
internal static class ApiAudit
{
    /// <summary>Filtro para la API propia: va después de autenticar y antes de traducir los errores.</summary>
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
        {
            return await next(ctx);
        }

        object? result = null;
        try
        {
            result = await next(ctx);
            return result;
        }
        finally
        {
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value;
            await Record(http, $"{http.Request.Method} {route}", result);
        }
    }

    /// <param name="target">Identificadores del pedido que nombran sobre qué se actuó, por ejemplo los del formulario de la API compatible.</param>
    public static Task Record(HttpContext http, string action, object? result, JsonObject? target = null) =>
        http.RequestServices.GetRequiredService<AuditLog>().TryRecord(action, http.Request.Path.Value, new
        {
            status = result switch
            {
                null => StatusCodes.Status500InternalServerError,
                IStatusCodeHttpResult { StatusCode: { } code } => code,
                _ => StatusCodes.Status200OK,
            },
            outcome = Outcome(result),
            target,
            ids = Ids(result),
        });

    /// <summary>El campo result de la API compatible: un error de negocio sale con estado 200 y result en error.</summary>
    private static string? Outcome(object? result) =>
        result is IValueHttpResult { Value: JsonObject body } && body["result"] is JsonValue value && value.TryGetValue<string>(out var outcome) ? outcome : null;

    /// <summary>Solo los campos de la respuesta que son identificadores ("id", "OrderId", "serviceids").</summary>
    private static JsonObject? Ids(object? result)
    {
        if (result is not IValueHttpResult { Value: { } value } || JsonSerializer.SerializeToNode(value) is not JsonObject body)
        {
            return null;
        }

        var ids = new JsonObject();
        foreach (var (name, node) in body)
        {
            if (name.EndsWith("id", StringComparison.OrdinalIgnoreCase) || name.EndsWith("ids", StringComparison.OrdinalIgnoreCase))
            {
                ids[name] = node?.DeepClone();
            }
        }

        return ids.Count > 0 ? ids : null;
    }
}
