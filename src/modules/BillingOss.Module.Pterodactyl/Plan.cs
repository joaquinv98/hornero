using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl;

/// <summary>Parámetros del producto tal como llegan en context.service.params (product_schema, PTR-02).</summary>
internal sealed class ProductParams
{
    public int? NestId { get; init; }

    public int? EggId { get; init; }

    public int? LocationId { get; init; }

    public int? MemoryMb { get; init; }

    public int? DiskMb { get; init; }

    public int? CpuPercent { get; init; }

    public int? SwapMb { get; init; }

    public int? IoWeight { get; init; }

    public bool? OomDisabled { get; init; }

    public int? Databases { get; init; }

    public int? Backups { get; init; }

    public int? AllocationLimit { get; init; }

    public string? ServerName { get; init; }

    public string? Startup { get; init; }

    public Dictionary<string, JsonNode?>? Variables { get; init; }

    public ExtraPortsParams? ExtraPorts { get; init; }

    public List<OrderField?>? OrderFields { get; init; }
}

internal sealed class ExtraPortsParams
{
    public int? Count { get; init; }

    public bool? Contiguous { get; init; }

    public Dictionary<string, int>? Variables { get; init; }
}

/// <summary>Un dato que completa el cliente al contratar (PTR-04). Key es una env_variable, un nombre del egg o egg_id.</summary>
internal sealed class OrderField
{
    public string? Key { get; init; }

    public JsonNode? Label { get; init; }

    public bool? Required { get; init; }

    public bool? Secret { get; init; }

    public List<string>? Options { get; init; }
}

/// <summary>Puertos adicionales (PTR-07). Variables lleva el desplazamiento de cada variable desde el puerto principal.</summary>
internal sealed record PortsPlan(int Count, bool Contiguous, IReadOnlyDictionary<string, int> Variables);

/// <summary>Lo que eligió el cliente en el pedido, solo de los campos que declara el producto.</summary>
internal sealed record OrderValues(int? EggId, IReadOnlyDictionary<string, string> Variables);

/// <summary>Los parámetros ya validados. Un opcional vacío vale 0, como en el módulo de hoy.</summary>
internal sealed record Plan(
    int NestId,
    int EggId,
    int LocationId,
    int MemoryMb,
    int DiskMb,
    int CpuPercent,
    int SwapMb,
    int IoWeight,
    bool OomDisabled,
    int Databases,
    int Backups,
    int AllocationLimit,
    string? ServerName,
    string? Startup,
    IReadOnlyDictionary<string, string> Variables,
    PortsPlan? Ports,
    IReadOnlyList<OrderField> OrderFields)
{
    /// <summary>Con puertos adicionales el tope de allocations cubre al menos todos los del alta, como hoy en DayZ.</summary>
    public int Allocations => Ports is null ? AllocationLimit : Math.Max(AllocationLimit, Ports.Count + 1);

    public static Plan From(JsonObject? node)
    {
        ProductParams p;
        try
        {
            p = node?.Deserialize<ProductParams>(ModuleJson.Options) ?? new ProductParams();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidInput([$"params{ex.Path?.TrimStart('$')}"]);
        }

        var bad = new List<string>();
        int Need(int? value, string name, int min)
        {
            if (value is null || value < min)
            {
                bad.Add($"params.{name}");
            }

            return value ?? 0;
        }

        int Optional(int? value, string name, int min, int max = int.MaxValue, int fallback = 0)
        {
            if (value is { } v && (v < min || v > max))
            {
                bad.Add($"params.{name}");
            }

            return value ?? fallback;
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in p.Variables ?? [])
        {
            // Texto, número o booleano. Un null es como si no estuviera.
            if (value is JsonValue && value.Text() is { } text)
            {
                variables[name] = text;
            }
            else if (value is not null)
            {
                bad.Add($"params.variables.{name}");
            }
        }

        PortsPlan? ports = null;
        if (p.ExtraPorts is { } extra)
        {
            var count = extra.Count ?? 0;
            var offsets = extra.Variables ?? [];
            if (count is < 1 or > 20)
            {
                bad.Add("params.extra_ports.count");
            }

            foreach (var (name, offset) in offsets)
            {
                if (string.IsNullOrWhiteSpace(name) || offset < 1 || offset > count)
                {
                    bad.Add($"params.extra_ports.variables.{name}");
                }
            }

            if (offsets.Values.Distinct().Count() != offsets.Count)
            {
                bad.Add("params.extra_ports.variables");
            }

            ports = new PortsPlan(count, extra.Contiguous ?? false, offsets);
        }

        var fields = new List<OrderField>();
        foreach (var (i, field) in (p.OrderFields ?? []).Index())
        {
            if (field?.Key is not { Length: > 0 } key || fields.Any(f => f.Key == key))
            {
                bad.Add($"params.order_fields[{i}].key");
                continue;
            }

            fields.Add(field);
        }

        var plan = new Plan(
            Need(p.NestId, "nest_id", 1),
            Need(p.EggId, "egg_id", 1),
            Need(p.LocationId, "location_id", 1),
            Need(p.MemoryMb, "memory_mb", 0),
            Need(p.DiskMb, "disk_mb", 0),
            Need(p.CpuPercent, "cpu_percent", 0),
            Optional(p.SwapMb, "swap_mb", 0),
            Optional(p.IoWeight, "io_weight", 10, 1000, 500),
            p.OomDisabled ?? false,
            Optional(p.Databases, "databases", 0),
            Optional(p.Backups, "backups", 0),
            Optional(p.AllocationLimit, "allocation_limit", 0),
            string.IsNullOrWhiteSpace(p.ServerName) ? null : p.ServerName.Trim(),
            string.IsNullOrWhiteSpace(p.Startup) ? null : p.Startup,
            variables,
            ports,
            fields);

        return bad.Count > 0 ? throw Fail.InvalidInput(bad) : plan;
    }

    /// <summary>
    /// Los datos del pedido que declara el producto, para crear el server. Lo que no declara se ignora, y un valor
    /// fuera de sus opciones o un obligatorio que falta no pasan: así un pedido armado a mano no elige un egg o un valor
    /// que el producto no ofrece. Solo se controla al crear: en un server que ya existe los datos ya se aplicaron.
    /// </summary>
    public OrderValues Order(OrderContext? order)
    {
        var given = order?.Variables ?? [];
        var bad = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int? egg = null;
        foreach (var field in OrderFields)
        {
            var key = field.Key!;
            var value = given.GetValueOrDefault(key).Text();
            if (string.IsNullOrEmpty(value))
            {
                if (field.Required == true)
                {
                    bad.Add($"order.variables.{key}");
                }

                continue;
            }

            if (value.Length > 1024 || (field.Options is { Count: > 0 } options && !options.Contains(value)))
            {
                bad.Add($"order.variables.{key}");
            }
            else if (key == "egg_id")
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    egg = id;
                }
                else
                {
                    bad.Add("order.variables.egg_id");
                }
            }
            else
            {
                values[key] = value;
            }
        }

        return bad.Count > 0 ? throw Fail.InvalidInput(bad) : new OrderValues(egg, values);
    }

    /// <summary>
    /// El egg que eligió el cliente al contratar, para el cambio de plan y el estado. No se controla contra las
    /// opciones actuales: el valor ya se validó al contratar, y sacar una opción del producto no puede trabar a los
    /// servicios que la eligieron. Egg en null si el producto no lo declara o el pedido no lo trae. Bad si no es un ID.
    /// </summary>
    public (int? Egg, bool Bad) StoredEgg(OrderContext? order)
    {
        if (OrderFields.All(f => f.Key != "egg_id") || order?.Variables?.GetValueOrDefault("egg_id").Text() is not { Length: > 0 } value)
        {
            return (null, false);
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? (id, false) : (null, true);
    }
}

/// <summary>
/// El paquete deseado de un server, como lo compara change_plan y get_status (PTR-10). Las variables salen de
/// los parámetros y, si no están, del valor actual del server o del valor por defecto del egg. Los datos del
/// pedido se aplican solo al crear: así un cambio de plan no pisa lo que el cliente cambió en el panel. Los textos
/// van recortados, como los guarda el panel (su middleware TrimStrings): si no, la relectura nunca coincidiría.
/// </summary>
internal sealed record Package(
    int Memory,
    int Swap,
    int Disk,
    int Io,
    int Cpu,
    bool OomDisabled,
    int Databases,
    int Allocations,
    int Backups,
    int Nest,
    int Egg,
    string Startup,
    string Image,
    IReadOnlyDictionary<string, string> Environment)
{
    public static Package Desired(Plan plan, PanelEgg egg, PanelServer server)
    {
        // Con otro egg va su startup y su imagen, salvo que el egg nuevo ofrezca la actual. Con el mismo, se conservan.
        var sameEgg = egg.Id == server.Egg;
        var startup = (plan.Startup ?? (sameEgg ? server.Startup : egg.Startup)).Trim();
        var image = (sameEgg || egg.Images.Contains(server.Image) ? server.Image : egg.DefaultImage).Trim();
        var environment = egg.Variables.ToDictionary(
            v => v.Env,
            v => (Lookup(plan.Variables, v) ?? server.Environment.GetValueOrDefault(v.Env) ?? v.Default).Trim(),
            StringComparer.Ordinal);
        return new Package(
            plan.MemoryMb, plan.SwapMb, plan.DiskMb, plan.IoWeight, plan.CpuPercent, plan.OomDisabled,
            plan.Databases, plan.Allocations, plan.Backups, plan.NestId, egg.Id, startup, image, environment);
    }

    /// <summary>Las variables del alta: primero el pedido, después los parámetros y por último el valor por defecto del egg.</summary>
    public static Dictionary<string, string> CreateEnvironment(Plan plan, PanelEgg egg, OrderValues order) =>
        egg.Variables.ToDictionary(
            v => v.Env,
            v => (Lookup(order.Variables, v) ?? Lookup(plan.Variables, v) ?? v.Default).Trim(),
            StringComparer.Ordinal);

    /// <summary>Una variable del egg se busca por su env_variable o por su nombre visible, como hoy.</summary>
    public static string? Lookup(IReadOnlyDictionary<string, string> values, EggVariable variable) =>
        values.GetValueOrDefault(variable.Env) ?? values.GetValueOrDefault(variable.Name);

    /// <summary>Nombres de lo que difiere, nunca valores: una variable puede ser secreta.</summary>
    public List<string> Differences(PanelServer server)
    {
        var list = new List<string>();
        void Check(bool same, string name)
        {
            if (!same)
            {
                list.Add(name);
            }
        }

        Check(server.Memory == Memory, "memory_mb");
        Check(server.Swap == Swap, "swap_mb");
        Check(server.Disk == Disk, "disk_mb");
        Check(server.Io == Io, "io_weight");
        Check(server.Cpu == Cpu, "cpu_percent");
        Check(server.OomDisabled == OomDisabled, "oom_disabled");
        Check(server.Databases == Databases, "databases");
        Check(server.AllocationLimit == Allocations, "allocation_limit");
        Check(server.Backups == Backups, "backups");
        Check(server.Nest == Nest, "nest_id");
        Check(server.Egg == Egg, "egg_id");
        Check(server.Startup.Trim() == Startup, "startup");
        Check(server.Image.Trim() == Image, "image");
        foreach (var (name, value) in Environment)
        {
            // Vacío y null son lo mismo: el panel convierte el texto vacío en null.
            Check((server.Environment.GetValueOrDefault(name) ?? string.Empty).Trim() == value, $"variables.{name}");
        }

        return list;
    }
}

/// <summary>Elección de puertos libres para el alta multipuerto (PTR-07).</summary>
internal static class Ports
{
    /// <summary>
    /// Elige el puerto principal y los adicionales en una misma IP. Contiguos: un bloque de puertos seguidos.
    /// Si no, el principal y los siguientes libres. Devuelve la lista ordenada por puerto, así el desplazamiento
    /// k de una variable es el elemento k. Sin lugar devuelve null.
    /// </summary>
    public static IReadOnlyList<Alloc>? Pick(IEnumerable<Alloc> allocations, PortsPlan plan, long seed)
    {
        // Bloques que no se pisan: cada uno empieza después de donde termina el anterior. Con la misma lista de
        // libres, dos altas eligen el mismo bloque o bloques sin puertos en común, nunca dos que se solapan a medias.
        var candidates = new List<Alloc[]>();
        foreach (var ip in allocations.Where(a => !a.Assigned).GroupBy(a => a.Ip).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ports = ip.OrderBy(a => a.Port).ToArray();
            for (var i = 0; i + plan.Count < ports.Length; i++)
            {
                var block = ports[i..(i + plan.Count + 1)];
                if (!plan.Contiguous || block[^1].Port - block[0].Port == plan.Count)
                {
                    candidates.Add(block);
                    i += plan.Count;
                }
            }
        }

        // ponytail: sin lock entre réplicas. Cada servicio elige otro bloque (hash multiplicativo del ID), y si igual
        // dos altas eligen el mismo, el panel rechaza la segunda con un 422 y se vuelve a elegir. Con muchas altas
        // multipuerto simultáneas en un nodo casi lleno, un lease en el núcleo.
        return candidates.Count == 0 ? null : candidates[(int)(unchecked((ulong)seed * 2654435761UL) % (ulong)candidates.Count)];
    }

    /// <summary>El valor de cada variable de puerto para una lista ordenada como la que devuelve Pick.</summary>
    public static Dictionary<string, string> Assign(IReadOnlyList<Alloc> block, PortsPlan plan) =>
        plan.Variables.ToDictionary(v => v.Key, v => block[v.Value].Port.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
}
