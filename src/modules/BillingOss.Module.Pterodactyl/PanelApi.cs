using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Pterodactyl;

/// <summary>
/// Cliente mínimo de la Application API de Pterodactyl, con HttpClient y System.Text.Json. Convierte las
/// respuestas en los errores del protocolo (PTR-14). Registra solo método, ruta sin query, código y duración:
/// nunca cuerpos, que llevan variables secretas y el correo del cliente.
/// </summary>
internal sealed class PanelApi(HttpClient http, ILogger logger)
{
    /// <summary>
    /// Una lectura. Devuelve null si el panel contesta su propio 404: solo eso es ausencia, cualquier otra falla es un
    /// error.
    /// </summary>
    public async Task<JsonNode?> Get(Panel panel, string path, CancellationToken ct)
    {
        var (status, json) = await Send(panel, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return status == 404 ? null : json ?? throw Fail.Malformed($"GET {Route(path)} sin cuerpo");
    }

    /// <summary>Todas las páginas de un listado. Falla cerrado: un listado incompleto nunca se usa para elegir.</summary>
    public async Task<List<JsonNode>> List(Panel panel, string path, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        for (var page = 1; page <= 100; page++)
        {
            var json = await Get(panel, $"{path}{separator}per_page=200&page={page}", ct).ConfigureAwait(false)
                ?? throw Fail.NotFound($"El panel no tiene {Route(path)}.");
            var last = json.Field("meta").Field("pagination").Field("total_pages").Int();
            if (json.Field("data") is not JsonArray data || last is null)
            {
                throw Fail.Malformed($"listado {Route(path)} sin paginación");
            }

            all.AddRange(data.Select(row => row.Field("attributes") ?? throw Fail.Malformed($"fila de {Route(path)}")));
            if (page >= last)
            {
                return all;
            }
        }

        throw Fail.Unavailable($"El listado {Route(path)} tiene más de 100 páginas.");
    }

    /// <summary>
    /// Un pedido. Devuelve el código y el JSON para los 2xx y el 404, y convierte el resto en un error del protocolo.
    /// Los pedidos que cambian algo conviene mandarlos sin cancelación: abandonarlos ya enviados deja el resultado
    /// sin saber.
    /// </summary>
    public async Task<(int Status, JsonNode? Json)> Send(Panel panel, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var mutating = method != HttpMethod.Get;
        var route = Route(path);
        using var request = new HttpRequestMessage(method, new Uri(panel.Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", panel.Key);
        request.Headers.Accept.ParseAdd("application/vnd.pterodactyl.v1+json");
        request.Headers.UserAgent.ParseAdd("BillingOss-Pterodactyl/0.1");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var started = Stopwatch.GetTimestamp();
        int status;
        int? retryAfter;
        string text;
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;
            retryAfter = (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds;
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Errores de la etapa de conexión: ahí el pedido seguro no salió.
            var sent = ex.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError);
            logger.LogWarning("Panel {Method} {Path} sin respuesta ({Error})", method.Method, route, ex.HttpRequestError);
            throw Cut($"No se pudo comunicar con el panel en {method.Method} {route}.", sent && mutating);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Panel {Method} {Path} sin respuesta (plazo vencido)", method.Method, route);
            throw Cut($"El panel no respondió a tiempo en {method.Method} {route}.", mutating);
        }

        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation("Panel {Method} {Path} {Status} en {Elapsed} ms", method.Method, route, status, elapsed);

        var json = Parse(text);
        if (status == 404 && !Errors(json, panel.Key).Any(e => e.Code == "NotFoundHttpException"))
        {
            // Un 404 sin el error del panel lo dio otro: el proxy, un vhost por defecto o la panel_url con otro prefijo.
            // El pedido no llegó al panel y no dice nada de la ausencia del server (PTR-11 y PTR-14).
            logger.LogWarning("Panel {Method} {Path} dio HTTP 404 sin el error del panel", method.Method, route);
            throw Fail.Unavailable($"{method.Method} {route} dio HTTP 404 sin el error del panel. Revisar el proxy y la panel_url.");
        }

        if (status is >= 200 and < 300 or 404)
        {
            // 204 no trae cuerpo. Cualquier otro 2xx tiene que traer JSON.
            return json is null && status is not (204 or 404) && text.Length > 0
                ? throw Fail.Malformed($"{method.Method} {route} con HTTP {status} sin JSON")
                : (status, json);
        }

        throw Failure(status, json, panel.Key, retryAfter, mutating, $"{method.Method} {route}");
    }

    /// <summary>La ruta sin query, para logs y mensajes: la query de users lleva el correo del cliente.</summary>
    public static string Route(string path) => path.Split('?')[0];

    private static ModuleException Cut(string message, bool uncertain) =>
        uncertain ? Fail.Unknown($"{message} Puede haberse ejecutado.", "no_response") : Fail.Unavailable(message);

    /// <summary>Convierte un error HTTP del panel en el error del protocolo (sección 10 y PTR-14).</summary>
    private ModuleException Failure(int status, JsonNode? body, string key, int? retryAfter, bool mutating, string what)
    {
        var errors = Errors(body, key);
        logger.LogWarning("Panel respondió HTTP {Status} a {What}: {Codes}", status, what, string.Join(",", errors.Select(e => e.Code)));
        var upstream = new JsonObject
        {
            ["http_status"] = status,
            ["codes"] = Fail.Strings(errors.Select(e => e.Code)),
        };
        var detail = string.Join(" ", errors.Select(e => e.Detail).Where(d => d.Length > 0));
        return status switch
        {
            401 or 403 => Fail.InvalidConfig(
                ["application_key"],
                $"El panel rechazó la application_key (HTTP {status}) en {what}. Revisar la clave y sus permisos.",
                status),

            // Los valores nunca van al mensaje: pueden ser secretos. Solo los campos que el panel marcó.
            422 => Fail.InvalidInput(SourceFields(body), $"El panel rechazó los datos de {what}."),
            429 => new ModuleException(ErrorCodes.RateLimited, "El panel limitó los pedidos.", retryAfter is > 0 ? retryAfter : 60),
            409 => new ModuleException(ErrorCodes.Conflict, $"El panel no puede hacer {what} ahora. {detail}".Trim(), 30, new JsonObject { ["upstream"] = upstream }),

            // 502 y 504 los da el proxy delante del panel cuando este tarda: el pedido pudo haberse ejecutado. Con
            // DaemonConnectionException los da el panel porque Wings no respondió, y ahí no aplicó el cambio o lo deshizo.
            502 or 504 when mutating && errors.All(e => e.Code != "DaemonConnectionException") =>
                Fail.Unknown($"El panel no respondió a tiempo (HTTP {status}) en {what}. Puede haberse ejecutado.", "gateway_timeout"),
            >= 500 => Fail.Unavailable($"El panel respondió HTTP {status} a {what}."),

            // Sin redirecciones: llevarían la clave a otro destino. Casi siempre es la panel_url mal escrita.
            < 400 => Fail.InvalidConfig(["panel_url"], $"El panel redirigió {what} (HTTP {status}). Revisar la panel_url.", status),
            _ => new ModuleException(ErrorCodes.Rejected, $"El panel rechazó {what} (HTTP {status}). {detail}".Trim(), details: new JsonObject { ["upstream"] = upstream }),
        };
    }

    /// <summary>Los errores del panel: errors[] con code, detail y meta.source_field. La clave se tapa si el panel la repite.</summary>
    private static List<Upstream> Errors(JsonNode? body, string key) =>
        body.Field("errors") is JsonArray errors
            ? errors.Select(e => new Upstream(Clean(e.Field("code").Text(), key), Clean(e.Field("detail").Text(), key))).ToList()
            : [];

    /// <summary>Los campos de un 422, por ejemplo external_id o allocation.default. Nunca los valores.</summary>
    private static List<string> SourceFields(JsonNode? body)
    {
        var fields = (body.Field("errors") as JsonArray ?? [])
            .Select(e => e.Field("meta").Field("source_field").Text())
            .OfType<string>()
            .Where(f => f.Length is > 0 and <= 200)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return fields.Count > 0 ? fields : ["input"];
    }

    private static string Clean(string? text, string key)
    {
        text = text?.Replace(key, "***", StringComparison.Ordinal).Trim() ?? string.Empty;
        return text.Length <= 300 ? text : text[..300] + "...";
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Una allocation del panel: IP, alias y puerto.</summary>
internal sealed record Alloc(int Id, string Ip, string? Alias, int Port, bool Assigned)
{
    public static Alloc Parse(JsonNode? a) => new(
        a.Field("id").Int() ?? throw Fail.Malformed("allocation sin id"),
        a.Field("ip").Text() ?? throw Fail.Malformed("allocation sin ip"),
        a.Field("alias").Text(),
        a.Field("port").Int() ?? throw Fail.Malformed("allocation sin puerto"),
        a.Field("assigned").Bool() ?? false);

    /// <summary>La dirección que ve el cliente: el alias si la IP es 0.0.0.0, y si no la IP, con el puerto.</summary>
    public string Address => Ip == "0.0.0.0" && !string.IsNullOrEmpty(Alias)
        ? $"{Alias}:{Port.ToString(CultureInfo.InvariantCulture)}"
        : $"{Ip}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

internal sealed record EggVariable(string Name, string Env, string Default);

internal sealed record PanelEgg(int Id, string Startup, IReadOnlyList<string> Images, IReadOnlyList<EggVariable> Variables)
{
    /// <summary>La imagen por defecto del egg es la primera de docker_images.</summary>
    public string DefaultImage => Images.Count > 0 ? Images[0] : string.Empty;

    public static PanelEgg Parse(JsonNode json)
    {
        var a = json.Field("attributes");
        var images = (a.Field("docker_images") as JsonObject)?.Select(i => i.Value.Text()).OfType<string>().ToList() ?? [];
        if (images.Count == 0 && a.Field("docker_image").Text() is { Length: > 0 } single)
        {
            images.Add(single);
        }

        var variables = (a.Field("relationships").Field("variables").Field("data") as JsonArray ?? [])
            .Select(v => v.Field("attributes"))
            .Select(v => new EggVariable(
                v.Field("name").Text() ?? string.Empty,
                v.Field("env_variable").Text() ?? throw Fail.Malformed("variable de egg sin env_variable"),
                v.Field("default_value").Text() ?? string.Empty))
            .ToList();
        return new PanelEgg(a.Field("id").Int() ?? throw Fail.Malformed("egg sin id"), a.Field("startup").Text() ?? string.Empty, images, variables);
    }
}

/// <summary>Un server del panel, leído con include=allocations.</summary>
internal sealed record PanelServer(
    int Id,
    string? ExternalId,
    string Identifier,
    string? Status,
    bool Suspended,
    int User,
    int Node,
    int Allocation,
    int Nest,
    int Egg,
    int Memory,
    int Swap,
    int Disk,
    int Io,
    int Cpu,
    string? Threads,
    bool OomDisabled,
    int? Databases,
    int? AllocationLimit,
    int? Backups,
    string Startup,
    string Image,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<Alloc> Allocations)
{
    public bool Installing => Status == "installing";

    public bool InstallFailed => Status is "install_failed" or "reinstall_failed";

    public bool IsSuspended => Status == "suspended" || Suspended;

    public Alloc? Primary => Allocations.FirstOrDefault(a => a.Id == Allocation);

    /// <summary>Estado observado para el núcleo (sección 9.1 del protocolo).</summary>
    public string Observed => IsSuspended ? "suspended" : InstallFailed ? "unknown" : "active";

    /// <summary>Los IDs externos que el núcleo guarda en el servicio y manda de vuelta en cada llamada.</summary>
    public JsonObject Ids()
    {
        var ids = new JsonObject
        {
            ["server_id"] = Id.ToString(CultureInfo.InvariantCulture),
            ["identifier"] = Identifier,
            ["user_id"] = User.ToString(CultureInfo.InvariantCulture),
            ["node_id"] = Node.ToString(CultureInfo.InvariantCulture),
            ["allocation_id"] = Allocation.ToString(CultureInfo.InvariantCulture),
        };
        if (Primary is { } primary)
        {
            ids["address"] = primary.Address;
        }

        return ids;
    }

    public static PanelServer Parse(JsonNode? json)
    {
        var a = json.Field("attributes") ?? throw Fail.Malformed("server sin attributes");
        var limits = a.Field("limits");
        var features = a.Field("feature_limits");
        var container = a.Field("container");
        int Need(JsonNode? node, string what) => node.Int() ?? throw Fail.Malformed($"server sin {what}");
        var environment = (container.Field("environment") as JsonObject ?? [])
            .ToDictionary(e => e.Key, e => e.Value.Text(), StringComparer.Ordinal);

        // Sin permiso de lectura de allocations el panel contesta null_resource en vez de la lista, sin dar error
        // (2026-06-30): leerlo como una lista vacía dejaba el alta sin dirección.
        var relation = a.Field("relationships").Field("allocations");
        if (relation is not null && relation.Field("data") is not JsonArray)
        {
            throw new ModuleException(
                ErrorCodes.InvalidConfig,
                "La application_key no tiene permiso de lectura sobre allocations: el panel no devolvió las del server.",
                details: new JsonObject { ["fields"] = Fail.Strings(["application_key"]), ["missing"] = Fail.Strings(["allocations"]) });
        }

        var allocations = (relation.Field("data") as JsonArray ?? [])
            .Select(r => Alloc.Parse(r.Field("attributes")))
            .ToList();
        return new PanelServer(
            Need(a.Field("id"), "id"),
            a.Field("external_id").Text(),
            a.Field("identifier").Text() ?? string.Empty,
            a.Field("status").Text(),
            a.Field("suspended").Bool() ?? false,
            Need(a.Field("user"), "user"),
            Need(a.Field("node"), "node"),
            Need(a.Field("allocation"), "allocation"),
            Need(a.Field("nest"), "nest"),
            Need(a.Field("egg"), "egg"),
            Need(limits.Field("memory"), "memory"),
            Need(limits.Field("swap"), "swap"),
            Need(limits.Field("disk"), "disk"),
            Need(limits.Field("io"), "io"),
            Need(limits.Field("cpu"), "cpu"),
            limits.Field("threads").Text(),
            limits.Field("oom_disabled").Bool() ?? false,
            features.Field("databases").Int(),
            features.Field("allocations").Int(),
            features.Field("backups").Int(),
            container.Field("startup_command").Text() ?? string.Empty,
            container.Field("image").Text() ?? string.Empty,
            environment,
            allocations);
    }
}
