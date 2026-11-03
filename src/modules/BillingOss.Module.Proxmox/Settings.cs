using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Proxmox;

/// <summary>Una entrada del pool de red de la instancia: la MAC con su IP pública, su bridge y la reserva DHCP.</summary>
internal sealed record NetEntry(string Mac, string PublicIp, string Bridge, int? Mtu, string? PrivateIp);

internal sealed record VmidRange(int From, int To);

/// <summary>Configuración de la instancia (config_schema). Se lee entera en cada llamada: el módulo no guarda nada.</summary>
internal sealed class InstanceConfig
{
    public const string DefaultBridge = "vmbr1";

    public required Uri ApiUrl { get; init; }

    public required string TokenId { get; init; }

    public required string TokenSecret { get; init; }

    public X509Certificate2Collection? Ca { get; init; }

    public required IReadOnlyList<NetEntry> Pool { get; init; }

    public Uri? ConsoleOrigin { get; init; }

    public string? ConsoleSecret { get; init; }

    public required string ConsoleServerKey { get; init; }

    public bool LegacyAclCleanup { get; init; }

    public required IReadOnlyList<VmidRange> ReservedVmids { get; init; }

    public bool IsReserved(int vmid) => ReservedVmids.Any(r => vmid >= r.From && vmid <= r.To);

    public static InstanceConfig Read(JsonObject config)
    {
        var bad = new List<string>();
        var apiUrl = Rules.HttpsOrigin(config["api_url"].Text());
        if (apiUrl is null)
        {
            bad.Add("api_url");
        }

        var tokenId = config["token_id"].Text();
        if (tokenId is null || !Rules.TokenId().IsMatch(tokenId))
        {
            bad.Add("token_id");
        }

        var tokenSecret = config["token_secret"].Text();
        if (string.IsNullOrEmpty(tokenSecret) || tokenSecret.Length > 512 || tokenSecret.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '='))
        {
            bad.Add("token_secret");
        }

        X509Certificate2Collection? ca = null;
        var caPem = config["ca_pem"].Text();
        if (!string.IsNullOrWhiteSpace(caPem))
        {
            try
            {
                ca = [];
                ca.ImportFromPem(caPem);
                if (ca.Count == 0)
                {
                    bad.Add("ca_pem");
                }
            }
            catch (CryptographicException)
            {
                bad.Add("ca_pem");
            }
        }

        var pool = ReadPool(config["network_pool"], bad);

        Uri? consoleOrigin = null;
        string? consoleSecret = null;
        if (config["console_origin"].Text() is { Length: > 0 } origin)
        {
            consoleOrigin = Rules.HttpsOrigin(origin);
            if (consoleOrigin is null)
            {
                bad.Add("console_origin");
            }

            // El gateway exige al menos 32 bytes y usa el texto literal como clave del HMAC.
            consoleSecret = config["console_secret"].Text();
            if (consoleSecret is null || Encoding.UTF8.GetByteCount(consoleSecret) < 32)
            {
                bad.Add("console_secret");
            }
        }

        var serverKey = config["console_server_key"].Text() ?? "6";
        if (!Rules.Identifier().IsMatch(serverKey))
        {
            bad.Add("console_server_key");
        }

        var legacyAcl = config["legacy_acl_cleanup"] switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => Fail(bad, "legacy_acl_cleanup"),
        };

        var reserved = new List<VmidRange>();
        if (config["reserved_vmids"] is JsonArray ranges)
        {
            foreach (var range in ranges)
            {
                var from = range.Field("from").Int();
                var to = range.Field("to").Int();
                if (from is null || to is null || from < Pve.MinVmid || to > Pve.MaxVmid || from > to)
                {
                    bad.Add("reserved_vmids");
                    break;
                }

                reserved.Add(new VmidRange(from.Value, to.Value));
            }
        }
        else if (config["reserved_vmids"] is not null)
        {
            bad.Add("reserved_vmids");
        }
        else
        {
            // El mismo valor por defecto del esquema, por si el núcleo no lo completa: el rango habitual de las plantillas.
            reserved.Add(new VmidRange(9000, 9019));
        }

        if (bad.Count > 0)
        {
            throw Errors.Config("invalid_field", $"La configuración de la instancia no sirve: {string.Join(", ", bad.Distinct())}.", bad.Distinct());
        }

        return new InstanceConfig
        {
            ApiUrl = apiUrl!,
            TokenId = tokenId!,
            TokenSecret = tokenSecret!,
            Ca = ca,
            Pool = pool,
            ConsoleOrigin = consoleOrigin,
            ConsoleSecret = consoleSecret,
            ConsoleServerKey = serverKey,
            LegacyAclCleanup = legacyAcl,
            ReservedVmids = reserved,
        };
    }

    /// <summary>
    /// El pool en orden. Una línea inválida o repetida invalida la configuración entera: una MAC o una IP mal cargada
    /// que llegue al ipset deja al VPS sin red.
    /// </summary>
    private static List<NetEntry> ReadPool(JsonNode? node, List<string> bad)
    {
        var pool = new List<NetEntry>();
        if (node is null)
        {
            return pool;
        }

        if (node is not JsonArray lines)
        {
            bad.Add("network_pool");
            return pool;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var mac = line.Field("mac").Text()?.Trim().ToLowerInvariant();
            var ip = line.Field("public_ip").Text()?.Trim();
            var bridge = line.Field("bridge").Text()?.Trim() ?? DefaultBridge;
            var mtuNode = line.Field("mtu");
            var mtu = mtuNode.Int();
            var privateIp = line.Field("private_ip").Text()?.Trim();
            if (privateIp is { Length: 0 })
            {
                privateIp = null;
            }

            if (mac is null || !Rules.Mac().IsMatch(mac)
                || ip is null || !Rules.IsIp(ip)
                || !Rules.Bridge().IsMatch(bridge)
                || (mtuNode is not null && (mtu is null || mtu < 576 || mtu > 9000))
                || (privateIp is not null && !Rules.IsIp(privateIp)))
            {
                bad.Add($"network_pool[{i}]");
                continue;
            }

            pool.Add(new NetEntry(mac, ip, bridge, mtu, privateIp));
        }

        if (pool.DistinctBy(e => e.Mac).Count() != pool.Count || pool.DistinctBy(e => e.PublicIp).Count() != pool.Count)
        {
            bad.Add("network_pool");
        }

        return pool;
    }

    private static bool Fail(List<string> bad, string field)
    {
        bad.Add(field);
        return false;
    }
}

internal sealed record Minimum(int? Cpu, int? RamMb, int? DiskGb);

/// <summary>
/// Parámetros del producto ya resueltos con las opciones del cliente (product_schema). Solo template_id, cores,
/// memory_mb, disk_gb y rate_mb_s pueden venir de opciones: lo controla el núcleo al resolverlas.
/// </summary>
internal sealed class ProductParams
{
    public required int TemplateId { get; init; }

    public required string Node { get; init; }

    public required int Cores { get; init; }

    public required int MemoryMb { get; init; }

    public required int DiskGb { get; init; }

    public required decimal RateMbS { get; init; }

    public bool ConsoleEnabled { get; init; }

    public string HostnameSuffix { get; init; } = string.Empty;

    public string IsolationGroupPrefix { get; init; } = string.Empty;

    public IReadOnlyDictionary<int, Minimum> OsMinimum { get; init; } = new Dictionary<int, Minimum>();

    public string? ConsolePool { get; init; }

    public string Rate => RateMbS.ToString(CultureInfo.InvariantCulture);

    public static ProductParams Read(JsonNode? node)
    {
        var fields = new JsonObject();
        int Positive(string name, int min = 1)
        {
            var value = node.Field(name).Int();
            if (value is null || value < min)
            {
                fields[name] = new JsonObject { ["reason"] = "invalid" };
                return 0;
            }

            return value.Value;
        }

        var template = Positive("template_id", Pve.MinVmid);
        var nodeName = node.Field("node").Text();
        if (nodeName is null || !Rules.Node().IsMatch(nodeName))
        {
            fields["node"] = new JsonObject { ["reason"] = "invalid" };
        }

        var cores = Positive("cores");
        var memory = Positive("memory_mb");
        var disk = Positive("disk_gb");
        var rate = node.Field("rate_mb_s").Dec();
        if (rate is null or <= 0 or > 100_000)
        {
            fields["rate_mb_s"] = new JsonObject { ["reason"] = "invalid" };
        }

        var consolePool = node.Field("console_pool").Text()?.Trim();
        if (consolePool is { Length: 0 })
        {
            consolePool = null;
        }

        if (consolePool is not null && !Rules.Node().IsMatch(consolePool))
        {
            fields["console_pool"] = new JsonObject { ["reason"] = "invalid" };
        }

        var prefix = node.Field("isolation_group_prefix").Text()?.Trim() ?? string.Empty;
        if (prefix.Length > 0 && !Rules.GroupPrefix().IsMatch(prefix))
        {
            fields["isolation_group_prefix"] = new JsonObject { ["reason"] = "invalid" };
        }

        var suffix = node.Field("hostname_suffix").Text() ?? string.Empty;
        if (suffix.Length > 200 || suffix.Any(char.IsControl))
        {
            fields["hostname_suffix"] = new JsonObject { ["reason"] = "invalid" };
        }

        var minimum = ReadPolicy(node.Field("os_minimum_policy"), fields);

        if (fields.Count > 0)
        {
            throw Errors.Input(fields);
        }

        return new ProductParams
        {
            TemplateId = template,
            Node = nodeName!,
            Cores = cores,
            MemoryMb = memory,
            DiskGb = disk,
            RateMbS = rate!.Value,
            ConsoleEnabled = node.Field("console_enabled").Bool(),
            HostnameSuffix = suffix,
            IsolationGroupPrefix = prefix,
            OsMinimum = minimum,
            ConsolePool = consolePool,
        };
    }

    /// <summary>Los mínimos del sistema operativo elegido que no se cumplen, o null. Las claves van en details.fields.</summary>
    public JsonObject? Violations()
    {
        if (!OsMinimum.TryGetValue(TemplateId, out var min))
        {
            return null;
        }

        var fields = new JsonObject();
        if (Cores < min.Cpu)
        {
            fields["cores"] = new JsonObject { ["reason"] = "os_minimum", ["min"] = min.Cpu };
        }

        if (MemoryMb < min.RamMb)
        {
            fields["memory_mb"] = new JsonObject { ["reason"] = "os_minimum", ["min"] = min.RamMb };
        }

        if (DiskGb < min.DiskGb)
        {
            fields["disk_gb"] = new JsonObject { ["reason"] = "os_minimum", ["min"] = min.DiskGb };
        }

        return fields.Count > 0 ? fields : null;
    }

    /// <summary>
    /// Política de mínimos por plantilla: un objeto o el mismo objeto como texto JSON, que es como venía del sistema
    /// anterior. Las claves son VMID de plantilla y los valores cpu, ramMB y diskGB enteros positivos.
    /// </summary>
    private static Dictionary<int, Minimum> ReadPolicy(JsonNode? node, JsonObject fields)
    {
        var result = new Dictionary<int, Minimum>();
        if (node is JsonValue text && text.TryGetValue<string>(out var raw))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return result;
            }

            try
            {
                node = JsonNode.Parse(raw);
            }
            catch (System.Text.Json.JsonException)
            {
                node = text;
            }
        }

        if (node is null)
        {
            return result;
        }

        if (node is not JsonObject policy)
        {
            fields["os_minimum_policy"] = new JsonObject { ["reason"] = "invalid" };
            return result;
        }

        foreach (var (key, value) in policy)
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var templateId)
                || value is not JsonObject limits
                || limits.Any(l => l.Key is not ("cpu" or "ramMB" or "diskGB") || l.Value.Int() is not > 0))
            {
                fields["os_minimum_policy"] = new JsonObject { ["reason"] = "invalid" };
                return result;
            }

            result[templateId] = new Minimum(limits["cpu"].Int(), limits["ramMB"].Int(), limits["diskGB"].Int());
        }

        return result;
    }
}

/// <summary>El contexto que manda el núcleo: servicio, sus IDs externos y parámetros, y la cuenta.</summary>
internal sealed class ServiceCtx
{
    public required long ServiceId { get; init; }

    /// <summary>
    /// El uuid estable del servicio (context.service.uuid), que va en la marca de la VM. A diferencia del ID, no choca
    /// con el de otra instalación que comparte el cluster. Null mientras el núcleo no lo mande.
    /// </summary>
    public string? Uuid { get; init; }

    public required string Status { get; init; }

    public required IReadOnlyDictionary<string, string> Ids { get; init; }

    public JsonNode? Params { get; init; }

    public required long AccountId { get; init; }

    public required string Language { get; init; }

    public bool Spanish => !Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public string? Id(string name) => Ids.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>El VMID de external_ids, si el alta terminó o el servicio vino importado.</summary>
    public int? Vmid => Id("vmid") is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
        && v is >= Pve.MinVmid and <= Pve.MaxVmid ? v : null;

    public static ServiceCtx Read(JsonObject context)
    {
        var service = context["service"];
        var account = context["account"];
        var serviceId = service.Field("id").Long();
        var accountId = account.Field("id").Long();
        if (serviceId is null or < 1 || accountId is null or < 1)
        {
            throw new ModuleException(ErrorCodes.InvalidInput, "El contexto no trae el servicio o la cuenta.",
                details: new JsonObject { ["fields"] = new JsonObject { ["context"] = new JsonObject { ["reason"] = "invalid" } } });
        }

        string? uuid = null;
        if (service.Field("uuid").Text() is { } uuidText)
        {
            uuid = Guid.TryParseExact(uuidText, "D", out var parsed) && parsed != Guid.Empty
                ? parsed.ToString("D")
                : throw new ModuleException(ErrorCodes.InvalidInput, "El uuid del servicio no es válido.",
                    details: new JsonObject { ["fields"] = new JsonObject { ["service.uuid"] = new JsonObject { ["reason"] = "invalid" } } });
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (service.Field("external_ids") is JsonObject external)
        {
            foreach (var (key, value) in external)
            {
                if (value.Text() is { } text)
                {
                    ids[key] = text;
                }
            }
        }

        return new ServiceCtx
        {
            ServiceId = serviceId.Value,
            Uuid = uuid,
            Status = service.Field("status").Text() ?? string.Empty,
            Ids = ids,
            Params = service.Field("params"),
            AccountId = accountId.Value,
            Language = account.Field("language").Text() ?? "es",
        };
    }
}

/// <summary>Formatos que valida el módulo, los mismos del módulo anterior y de la API de Proxmox.</summary>
internal static partial class Rules
{
    [GeneratedRegex("^[0-9a-f]{2}(:[0-9a-f]{2}){5}$")]
    public static partial Regex Mac();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_.-]{0,14}$")]
    public static partial Regex Bridge();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$")]
    public static partial Regex Node();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public static partial Regex Identifier();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,17}$")]
    public static partial Regex GroupPrefix();

    /// <summary>usuario@realm!nombre, como lo muestra Proxmox al crear el token.</summary>
    [GeneratedRegex(@"^[^\s=!@:]+@[A-Za-z][A-Za-z0-9._-]*![A-Za-z][A-Za-z0-9._-]*$")]
    public static partial Regex TokenId();

    /// <summary>IPv4 con cuatro octetos o IPv6. IPAddress.TryParse solo acepta cosas como "1.2.3", por eso el control extra.</summary>
    public static bool IsIp(string text) =>
        IPAddress.TryParse(text, out var ip)
        && (ip.AddressFamily == AddressFamily.InterNetworkV6 ? text.Contains(':', StringComparison.Ordinal) : ip.ToString() == text);

    /// <summary>Origen https sin usuario, ruta, query ni fragmento, por ejemplo https://pve.example.com:8006.</summary>
    public static Uri? HttpsOrigin(string? text)
    {
        if (text is null || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '?' or '#')
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/")
        {
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority));
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

    public static decimal? Dec(this JsonNode? node) =>
        decimal.TryParse(node.Text(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static long? Long(this JsonNode? node) =>
        long.TryParse(node.Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static int? Int(this JsonNode? node) =>
        int.TryParse(node.Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Booleano del esquema, o los valores con que venía del sistema anterior ("on", "1").</summary>
    public static bool Bool(this JsonNode? node) => node.Text()?.ToLowerInvariant() is "true" or "on" or "1" or "yes";
}
