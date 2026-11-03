using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Proxmox;

/// <summary>Una VM o un contenedor del inventario del cluster.</summary>
internal sealed record VmRef(int Vmid, string Node, string Type, string Name, string Status, bool Template, string Lock)
{
    public bool Running => Status == "running";
}

/// <summary>
/// Quién tiene una MAC en el cluster. Una VM prendida o con onboot ya ganó la MAC y no la suelta, y la reserva de una
/// reinstalación tampoco.
/// </summary>
internal sealed record MacHolder(string Mac, int Vmid, bool Committed);

/// <summary>
/// Cliente de la API de Proxmox VE con una sola política para todo pedido: solo HTTPS con certificado y nombre
/// validados, sin redirecciones, cuerpo form-urlencoded, solo JSON con data y sin errors, y rutas armadas con nodo
/// y VMID validados. Nunca loguea cuerpos ni el token.
/// </summary>
internal sealed partial class Pve(InstanceConfig config, HttpClient http, ILogger logger, TimeProvider time, TimeSpan pollInterval)
{
    public const int MinVmid = 100;
    public const int MaxVmid = 999_999_999;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public TimeProvider Time => time;

    public static string VmPath(string node, int vmid, string type = "qemu") =>
        Rules.Node().IsMatch(node) && vmid is >= MinVmid and <= MaxVmid && type is "qemu" or "lxc"
            ? $"/nodes/{node}/{type}/{vmid.ToString(CultureInfo.InvariantCulture)}"
            : throw Errors.Unavailable("Proxmox devolvió un nodo o un VMID inválido.");

    public Task<JsonNode?> Get(string path, string? query = null, TimeSpan? timeout = null) =>
        Send(HttpMethod.Get, path, query, null, timeout);

    public Task<JsonNode?> Post(string path, params (string Key, string Value)[] form) =>
        Send(HttpMethod.Post, path, null, form, null);

    public Task<JsonNode?> Put(string path, params (string Key, string Value)[] form) =>
        Send(HttpMethod.Put, path, null, form, null);

    public Task<JsonNode?> Delete(string path) => Send(HttpMethod.Delete, path, null, null, null);

    // ---- Inventario ----

    /// <summary>Todas las VMs y contenedores. Una entrada sin nodo o VMID válido aborta: el inventario tiene que estar completo.</summary>
    public async Task<List<VmRef>> Resources()
    {
        if (await Get("/cluster/resources", "type=vm").ConfigureAwait(false) is not JsonArray items)
        {
            throw Errors.Unavailable("Proxmox devolvió un inventario inválido.");
        }

        var list = new List<VmRef>(items.Count);
        foreach (var item in items)
        {
            var type = item.Field("type").Text();
            var vmid = item.Field("vmid").Int();
            var node = item.Field("node").Text();
            if (type is not ("qemu" or "lxc") || vmid is null or < MinVmid or > MaxVmid || node is null || !Rules.Node().IsMatch(node))
            {
                throw Errors.Unavailable("El inventario del cluster vino incompleto.");
            }

            list.Add(new VmRef(vmid.Value, node, type, item.Field("name").Text() ?? string.Empty,
                item.Field("status").Text() ?? string.Empty, item.Field("template").Text() is "1" or "true", item.Field("lock").Text() ?? string.Empty));
        }

        return list;
    }

    /// <summary>
    /// Proxmox filtra en silencio las VMs que el token no ve. Sin VM.Audit heredado en /vms, una VM ajena parece
    /// libre y una VM viva parece borrada: por eso se exige antes de asignar red o de dar algo por inexistente.
    /// </summary>
    public async Task RequireAudit()
    {
        var permissions = await Get("/access/permissions", "path=%2Fvms").ConfigureAwait(false);
        if (permissions.Field("/vms").Field("VM.Audit").Text() != "1")
        {
            throw Errors.Config("vm_audit_missing", "El token necesita VM.Audit heredado en /vms para ver el cluster completo.", ["token_id"]);
        }
    }

    public async Task<JsonObject> Config(VmRef vm) =>
        await Get($"{VmPath(vm.Node, vm.Vmid, vm.Type)}/config").ConfigureAwait(false) as JsonObject
        ?? throw Errors.Unavailable("Proxmox devolvió una configuración de VM inválida.");

    /// <summary>Las MACs de todas las NICs de todas las VMs y contenedores, menos las VMs indicadas.</summary>
    public async Task<List<MacHolder>> Macs(IReadOnlyList<VmRef> resources, params int[] exclude)
    {
        await RequireAudit().ConfigureAwait(false);
        var holders = new ConcurrentBag<MacHolder>();
        await Parallel.ForEachAsync(
            resources.Where(r => !exclude.Contains(r.Vmid)),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (vm, _) =>
            {
                var config = await Config(vm).ConfigureAwait(false);
                var committed = vm.Running || config["onboot"].Text() == "1"
                    || VmConfig.Description(config).StartsWith(VmConfig.ReservePrefix, StringComparison.Ordinal);
                foreach (var (key, value) in config)
                {
                    if (VmConfig.NetKey().IsMatch(key))
                    {
                        var mac = VmConfig.MacOf(value.Text()) ?? throw Errors.Unavailable("No se pudo leer la MAC de una VM del cluster. No se asigna red.");
                        holders.Add(new MacHolder(mac, vm.Vmid, committed));
                    }
                }
            }).ConfigureAwait(false);
        return [.. holders];
    }

    // ---- Tareas ----

    /// <summary>
    /// Espera una tarea de Proxmox hasta el plazo. Devuelve false si sigue corriendo al llegar al plazo, y una
    /// tarea que terminó mal es un error. El nodo se toma del UPID.
    /// </summary>
    public async Task<bool> Wait(JsonNode? task, DateTimeOffset deadline, string what) =>
        await Finish(task, deadline, what).ConfigureAwait(false) is not null;

    /// <summary>
    /// Como <see cref="Wait"/>, pero devuelve el exitstatus de una tarea que terminó bien (OK o WARNINGS), o null si
    /// sigue corriendo al llegar al plazo. Proxmox revisa el lock de la VM dentro de la tarea y no en el pedido: una
    /// tarea que no lo consiguió (un backup, una migración u otra tarea de la VM) es conflict, no un rechazo.
    /// </summary>
    public async Task<string?> Finish(JsonNode? task, DateTimeOffset deadline, string what)
    {
        var upid = task.Text();
        var match = upid is null ? null : Upid().Match(upid);
        if (match is not { Success: true })
        {
            throw Errors.Unknown($"Proxmox no devolvió una tarea válida para {what}.");
        }

        var path = $"/nodes/{match.Groups[1].Value}/tasks/{Uri.EscapeDataString(upid!)}/status";
        while (true)
        {
            var status = await Get(path).ConfigureAwait(false);
            switch (status.Field("status").Text())
            {
                case "stopped":
                    var exit = status.Field("exitstatus").Text() ?? string.Empty;
                    if (exit == "OK" || exit.StartsWith("WARNINGS", StringComparison.Ordinal))
                    {
                        return exit;
                    }

                    if (Locked(exit))
                    {
                        throw Errors.Locked($"La VM tiene un lock u otra tarea en curso en Proxmox ({what}).");
                    }

                    throw Errors.Rejected("task_failed", $"La tarea de Proxmox ({what}) terminó con error.", new JsonObject { ["exitstatus"] = Scrub(exit) });
                case "running":
                    if (time.GetUtcNow() >= deadline)
                    {
                        return null;
                    }

                    await Task.Delay(pollInterval, time).ConfigureAwait(false);
                    break;
                default:
                    throw Errors.Unavailable("Proxmox devolvió un estado de tarea inválido.");
            }
        }
    }

    /// <summary>El texto con el que Proxmox avisa que la VM está bloqueada: "VM is locked (backup)" o "can't lock file ... got timeout".</summary>
    public static bool Locked(string? text) =>
        text is not null && (text.Contains("is locked", StringComparison.OrdinalIgnoreCase) || text.Contains("can't lock file", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// La tarea de la VM que sigue corriendo en Proxmox, si hay una: por ejemplo la de un intento anterior que se cortó
    /// después del pedido. Sin Sys.Audit en el nodo Proxmox lista solo las del token, que son las que importan acá. Las de
    /// otros (un backup, una migración) dejan un lock en la configuración, que se mira aparte.
    /// </summary>
    public async Task<(JsonNode Upid, string Type)?> ActiveTask(VmRef vm)
    {
        _ = VmPath(vm.Node, vm.Vmid, vm.Type);
        var vmid = vm.Vmid.ToString(CultureInfo.InvariantCulture);
        if (await Get($"/nodes/{vm.Node}/tasks", $"vmid={vmid}&source=active").ConfigureAwait(false) is not JsonArray tasks)
        {
            throw Errors.Unavailable("Proxmox devolvió una lista de tareas inválida.");
        }

        foreach (var task in tasks)
        {
            if (task.Field("upid").Text() is { } upid && Upid().Match(upid) is { Success: true } match && match.Groups[3].Value == vmid)
            {
                return (JsonValue.Create(upid), match.Groups[2].Value);
            }
        }

        return null;
    }

    /// <summary>Si el texto es un UPID de una tarea de ese tipo sobre esa VM.</summary>
    public static bool IsTask(string upid, string type, string vmid) =>
        Upid().Match(upid) is { Success: true } match && match.Groups[2].Value == type && match.Groups[3].Value == vmid;

    /// <summary>Algunos PUT devuelven una tarea según la versión de Proxmox y otros nada: si hay tarea, se espera.</summary>
    public Task<bool> WaitIfTask(JsonNode? result, DateTimeOffset deadline, string what) =>
        result.Text() is { } text && text.StartsWith("UPID:", StringComparison.Ordinal) ? Wait(result, deadline, what) : Task.FromResult(true);

    public Task Pause() => Task.Delay(pollInterval, time);

    // ---- Transporte ----

    private async Task<JsonNode?> Send(HttpMethod method, string path, string? query, (string Key, string Value)[]? form, TimeSpan? timeout)
    {
        if (!SafePath(path))
        {
            throw new InvalidOperationException("Ruta de Proxmox inválida.");
        }

        var mutating = method != HttpMethod.Get;
        using var request = new HttpRequestMessage(method, new Uri(config.ApiUrl, "/api2/json" + path + (query is null ? string.Empty : "?" + query)));
        request.Headers.TryAddWithoutValidation("Authorization", "PVEAPIToken=" + config.TokenId + "=" + config.TokenSecret);
        request.Headers.Accept.ParseAdd("application/json");
        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            request.Content = new FormUrlEncodedContent((form ?? []).Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        }

        using var cts = new CancellationTokenSource(timeout ?? CallTimeout, time);
        int status;
        string reason;
        string text;
        try
        {
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            reason = Scrub(response.ReasonPhrase);
            text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (Untrusted(ex))
        {
            throw Errors.Config("tls_untrusted", "El certificado de Proxmox no es de confianza o no coincide con el nombre de api_url.", ["api_url", "ca_pem"]);
        }
        catch (HttpRequestException ex)
        {
            // En la etapa de conexión el pedido seguro no salió.
            var sent = ex.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError);
            logger.LogWarning("Proxmox sin respuesta en {Method} {Path}: {Error}", method, path, ex.HttpRequestError);
            throw sent && mutating ? Errors.Unknown("Se cortó la conexión con Proxmox después de enviar el pedido.") : Errors.Unavailable("Proxmox no responde.");
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            // Venció el tiempo para conectar del handler, que avisa con una cancelación y no con un error de conexión:
            // el pedido seguro no salió.
            logger.LogWarning("Proxmox sin conexión a tiempo en {Method} {Path}", method, path);
            throw Errors.Unavailable("No se pudo conectar con Proxmox a tiempo.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Proxmox no respondió a tiempo en {Method} {Path}", method, path);
            throw mutating ? Errors.Unknown("Proxmox no respondió a tiempo después de enviar el pedido.") : Errors.Unavailable("Proxmox no respondió a tiempo.");
        }

        logger.Log(mutating ? LogLevel.Information : LogLevel.Debug, "Proxmox {Method} {Path} {Status}", method, path, status);
        if (status is >= 200 and < 300)
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject body && body.ContainsKey("data") && body["errors"] is null or JsonObject { Count: 0 } or JsonArray { Count: 0 })
                {
                    return body["data"];
                }
            }
            catch (JsonException)
            {
            }

            throw mutating
                ? Errors.Unknown("Proxmox respondió sin el JSON esperado después de un cambio.")
                : Errors.Unavailable("Proxmox respondió sin el JSON esperado.");
        }

        throw Failure(status, reason, mutating, $"{method} {path}");
    }

    /// <summary>Convierte un error HTTP de Proxmox en un error del protocolo. Proxmox pone el motivo en la línea de estado.</summary>
    private ModuleException Failure(int status, string reason, bool mutating, string what)
    {
        logger.LogWarning("Proxmox respondió HTTP {Status} a {What}", status, what);
        var upstream = new JsonObject { ["http_status"] = status, ["message"] = reason };
        return status switch
        {
            401 => Errors.Config("token_rejected", "Proxmox rechazó el token (HTTP 401).", ["token_id", "token_secret"]),
            403 => Errors.Config("permission_denied", $"Al token le falta un permiso en Proxmox (HTTP 403). {reason}".Trim(), ["token_id"]),
            _ when reason.Contains("lock", StringComparison.OrdinalIgnoreCase) => Errors.Locked("La VM tiene otra tarea en curso en Proxmox."),
            >= 500 when mutating => Errors.Unknown($"Proxmox respondió HTTP {status} a un cambio. {reason}".Trim()),
            >= 500 => Errors.Unavailable($"Proxmox respondió HTTP {status}."),
            _ when status < 400 => mutating ? Errors.Unknown($"Proxmox respondió HTTP {status} a un cambio.") : Errors.Unavailable($"Proxmox respondió HTTP {status}."),
            _ => Errors.Rejected("upstream_rejected", $"Proxmox rechazó el pedido (HTTP {status}).", upstream),
        };
    }

    /// <summary>Tapa el token y las IPs en un texto de Proxmox antes de devolverlo o loguearlo.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.Replace(config.TokenSecret, "***", StringComparison.Ordinal);
        text = Ipv4().Replace(text, "x.x.x.x");
        text = Ipv6().Replace(text, "x:x");
        return text.Length <= 200 ? text.Trim() : text[..200].Trim() + "...";
    }

    private static bool Untrusted(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rutas armadas por el módulo: sin "..", sin "//" y con cada segmento decodificado limpio.</summary>
    private static bool SafePath(string path) =>
        PathChars().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal) && !path.Contains("//", StringComparison.Ordinal)
        && path.Split('/').Skip(1).All(s => Uri.UnescapeDataString(s) is { Length: > 0 } d && !d.Any(c => c is '/' or '\\' or '%' || char.IsControl(c) || c == ' '));

    [GeneratedRegex("^/[A-Za-z0-9_/%!@.:-]+$")]
    private static partial Regex PathChars();

    // pid, pstart y starttime como upid_decode de Proxmox: pstart pasa a 9 dígitos con más de 497 días de uptime.
    [GeneratedRegex("^UPID:([A-Za-z0-9][A-Za-z0-9_.-]*):[0-9A-Fa-f]{8}:[0-9A-Fa-f]{8,9}:[0-9A-Fa-f]{8}:([A-Za-z0-9_]+):([^:/\\\\]*):[^:/\\\\\\s]+:$")]
    private static partial Regex Upid();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"\b[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{0,4}){2,7}\b")]
    private static partial Regex Ipv6();
}

/// <summary>Lectura de la configuración de una VM de Proxmox.</summary>
internal static partial class VmConfig
{
    [GeneratedRegex(@"^net\d+$")]
    public static partial Regex NetKey();

    [GeneratedRegex(@"(?:^|,)(?:hwaddr|macaddr|[A-Za-z][A-Za-z0-9_-]*)=([0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5})(?:,|$)")]
    private static partial Regex NetMac();

    [GeneratedRegex(@"(?:^|,)size=(\d+(?:\.\d+)?)([KMGT])(?:,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DiskSize();

    /// <summary>La MAC de un valor netN, de VM (virtio=MAC) o de contenedor (hwaddr=MAC), en minúsculas.</summary>
    public static string? MacOf(string? value) =>
        value is not null && NetMac().Match(value) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>Un parámetro de un valor netN, por ejemplo bridge o mtu.</summary>
    public static string? NetParam(string? value, string name) =>
        value?.Split(',').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == name)?[1];

    /// <summary>Tamaño de scsi0 en GB, o null si no se puede leer.</summary>
    public static decimal? DiskGb(JsonObject config)
    {
        if (DiskSize().Match(config["scsi0"].Text() ?? string.Empty) is not { Success: true } match)
        {
            return null;
        }

        var value = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return char.ToUpperInvariant(match.Groups[2].Value[0]) switch
        {
            'K' => value / 1048576,
            'M' => value / 1024,
            'G' => value,
            _ => value * 1024,
        };
    }

    public static string Description(JsonObject config) => (config["description"].Text() ?? string.Empty).TrimEnd();

    /// <summary>El comienzo de la descripción de una reserva de MAC (ver Operations.Reserve).</summary>
    public const string ReservePrefix = "billing-oss-reserva:";
}

/// <summary>Armado de los errores del protocolo con los detalles que usa este módulo.</summary>
internal static class Errors
{
    public static ModuleException Config(string reason, string message, IEnumerable<string>? fields = null) => new(
        ErrorCodes.InvalidConfig,
        message,
        details: new JsonObject
        {
            ["reason"] = reason,
            ["fields"] = new JsonArray((fields ?? []).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        });

    /// <summary>Parámetros inválidos: details.fields es un objeto con el motivo de cada campo, para que el núcleo arme su texto.</summary>
    public static ModuleException Input(JsonObject fields, string? message = null) => new(
        ErrorCodes.InvalidInput,
        message ?? $"Parámetros inválidos: {string.Join(", ", fields.Select(f => f.Key))}.",
        details: new JsonObject { ["fields"] = fields });

    public static ModuleException Input(string field, string reason, int? min = null)
    {
        var detail = new JsonObject { ["reason"] = reason };
        if (min is not null)
        {
            detail["min"] = min;
        }

        return Input(new JsonObject { [field] = detail });
    }

    /// <summary>Estado transitorio: otra tarea en curso, el alta sin terminar o una espera. Se reintenta.</summary>
    public static ModuleException Busy(string reason, string message, int retryAfter = 15) =>
        new(ErrorCodes.Conflict, message, retryAfter, new JsonObject { ["reason"] = reason });

    /// <summary>La VM tiene un lock (backup, migración, clon) u otra tarea: se reintenta, más espaciado porque un backup tarda.</summary>
    public static ModuleException Locked(string message) => Busy("vm_locked", message, 60);

    /// <summary>Inconsistencia que tiene que mirar el staff. No se reintenta ni se toca la VM.</summary>
    public static ModuleException Review(string reason, string message) =>
        new(ErrorCodes.ManualReview, message, details: new JsonObject { ["reason"] = reason });

    public static ModuleException Rejected(string reason, string message, JsonObject? upstream = null)
    {
        var details = new JsonObject { ["reason"] = reason };
        if (upstream is not null)
        {
            details["upstream"] = upstream;
        }

        return new(ErrorCodes.Rejected, message, details: details);
    }

    /// <summary>Proxmox no respondió y el pedido seguro no se ejecutó, o era una consulta.</summary>
    public static ModuleException Unavailable(string message, int retryAfter = 30) => new(ErrorCodes.UpstreamUnavailable, message, retryAfter);

    /// <summary>El pedido pudo haberse ejecutado. Se repite con la misma clave: cada paso mira el estado antes de actuar.</summary>
    public static ModuleException Unknown(string message) =>
        new(ErrorCodes.OutcomeUnknown, message, 30, new JsonObject { ["reason"] = "no_response" });
}

/// <summary>El cliente HTTP hacia Proxmox, con la validación del certificado contra el CA de la instancia si lo tiene.</summary>
internal static class Transport
{
    /// <summary>
    /// Un handler por llamada al módulo: la configuración llega en cada llamada, así que el CA también. Sin ca_pem vale
    /// el almacén del sistema. Con ca_pem vale solo ese CA, como CURLOPT_CAINFO en el módulo anterior. El nombre del
    /// certificado se valida siempre.
    /// </summary>
    public static HttpMessageHandler Handler(X509Certificate2Collection? ca) => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) => Trusted(ca, certificate as X509Certificate2, chain, errors),
        },
    };

    private static bool Trusted(X509Certificate2Collection? ca, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (ca is null)
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
        {
            return false;
        }

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.CustomTrustStore.AddRange(ca);
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return custom.Build(certificate);
    }
}
