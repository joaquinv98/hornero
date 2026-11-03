using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>Una VM o plantilla del cluster falso.</summary>
internal sealed class FakeVm
{
    public required int Vmid { get; init; }

    public required string Node { get; set; }

    public string Type { get; init; } = "qemu";

    public string Status { get; set; } = "stopped";

    public Dictionary<string, string> Config { get; set; } = [];

    /// <summary>La contraseña de cloud-init, que Proxmox nunca devuelve en la configuración.</summary>
    public string? CiPassword { get; set; }

    public Dictionary<string, string> Firewall { get; } = [];

    public Dictionary<string, List<string>> IpSets { get; } = [];

    public List<Dictionary<string, string>> Rules { get; } = [];

    public string? Pool { get; set; }

    public string Name => Config.GetValueOrDefault("name") ?? string.Empty;

    public bool Template => Config.GetValueOrDefault("template") == "1";

    public string? Mac => Config.GetValueOrDefault("net0")?.Split(',')[0].Split('=')[1].ToLowerInvariant();

    public decimal DiskGb => decimal.Parse(Config["scsi0"].Split("size=")[1].TrimEnd('G'), CultureInfo.InvariantCulture);
}

/// <summary>
/// Proxmox VE falso por HTTPS de verdad, en loopback y con un certificado autofirmado que se genera en cada prueba.
/// Responde con las formas de la API pública de Proxmox (envoltorio data, tareas por UPID, motivo del error en la línea
/// de estado) y anota cada pedido. Atiende también la revocación del gateway de consola.
/// </summary>
internal sealed class FakePve : IAsyncDisposable
{
    public const string TokenId = "billing@pve!modulo";

    private readonly object gate = new();
    private readonly Dictionary<string, FakeTask> tasks = [];
    private readonly List<string> calls = [];
    private readonly WebApplication app;
    private int taskCounter;

    private FakePve(WebApplication app, X509Certificate2 certificate)
    {
        this.app = app;
        Certificate = certificate;
    }

    /// <summary>Secreto del token, inventado en cada prueba: sirve de testigo para ver que no se filtre.</summary>
    public string Secret { get; } = "token-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Secreto del gateway, también inventado en cada prueba.</summary>
    public string ConsoleSecret { get; } = "consola-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public X509Certificate2 Certificate { get; }

    public string CaPem => Certificate.ExportCertificatePem();

    public X509Certificate2Collection Ca => [Certificate];

    public Uri Origin { get; private set; } = null!;

    public Dictionary<int, FakeVm> Vms { get; } = [];

    public HashSet<string> Pools { get; } = ["vps-console"];

    public HashSet<string> Groups { get; } = ["vps-vmbr2", "vps-vmbr1"];

    public Dictionary<string, HashSet<string>> Bridges { get; } = new()
    {
        ["pve1"] = ["vmbr0", "vmbr2", "vmbr1"],
        ["pve2"] = ["vmbr0", "vmbr2", "vmbr1"],
    };

    public HashSet<string> Users { get; } = [];

    /// <summary>
    /// Un pvestatd que no publica, trabado o con atraso: el inventario trae las VMs sin nombre y con template en 0,
    /// aunque la configuración diga otra cosa.
    /// </summary>
    public bool StaleInventory { get; set; }

    /// <summary>
    /// Un clon que ya copió el primer disco (un efidisk o el cloud-init en ide2): la configuración tiene el nombre, la
    /// marca y la MAC nueva, todavía con lock clone. Sin esto el clon en curso tiene la configuración temporal de Proxmox.
    /// </summary>
    public bool CloneConfigEarly { get; set; }

    public List<string> Acl { get; } = [];

    /// <summary>Si el token puede quitar ACL (Permissions.Modify en /vms) y ver los usuarios (Sys.Audit en /access/groups).</summary>
    public bool AclAllowed { get; set; } = true;

    public List<JsonObject> Revoked { get; } = [];

    public bool VmAudit { get; set; } = true;

    public bool GatewayDown { get; set; }

    /// <summary>Lo que tarda cada tipo de tarea (qmclone, qmstart). Con mucho tiempo queda en curso hasta <see cref="ReleaseTasks"/>.</summary>
    public Dictionary<string, TimeSpan> Durations { get; } = [];

    /// <summary>Si un ipset de VM tiene que fallar al cargar una entrada.</summary>
    public bool FailIpSetEntry { get; set; }

    /// <summary>Intercepta un pedido ("PUT /nodes/pve1/qemu/7001/config") antes de atenderlo. Devuelve true si ya respondió.</summary>
    public Func<HttpContext, string, Task<bool>>? Hook { get; set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public int Count(string route) => Calls.Count(c => c == route);

    public static async Task<FakePve> Start()
    {
        var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        var app = builder.Build();
        var pve = new FakePve(app, certificate);
        app.Run(pve.Handle);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        pve.Origin = new Uri(address);
        pve.AddTemplate(9001, "pve1", "debian-13", 10);
        pve.AddTemplate(9002, "pve1", "windows-2025", 40, ostype: "win11");
        return pve;
    }

    public FakeVm AddTemplate(int vmid, string node, string name, int diskGb, string ostype = "l26") => Add(new FakeVm
    {
        Vmid = vmid,
        Node = node,
        Config = new()
        {
            ["name"] = name,
            ["template"] = "1",
            ["ostype"] = ostype,
            ["cores"] = "1",
            ["memory"] = "1024",
            ["onboot"] = "1",
            ["scsi0"] = $"local-zfs:base-{vmid}-disk-0,size={diskGb}G",
            ["net0"] = "virtio=BC:24:11:00:00:01,bridge=vmbr0",
        },
    });

    /// <summary>Una VM cualquiera del cluster, por ejemplo la de otro servicio o de otro sistema.</summary>
    public FakeVm AddVm(int vmid, string name, string? mac = null, string node = "pve1", string status = "running", string? description = null, bool onboot = true)
    {
        var vm = new FakeVm
        {
            Vmid = vmid,
            Node = node,
            Status = status,
            Config = new()
            {
                ["name"] = name,
                ["ostype"] = "l26",
                ["cores"] = "1",
                ["memory"] = "1024",
                ["onboot"] = onboot ? "1" : "0",
                ["scsi0"] = $"local-zfs:vm-{vmid}-disk-0,size=10G",
                ["net0"] = $"virtio={(mac ?? RandomMac()).ToUpperInvariant()},bridge=vmbr2,firewall=1",
            },
        };
        if (description is not null)
        {
            vm.Config["description"] = description;
        }

        return Add(vm);
    }

    public FakeVm Add(FakeVm vm)
    {
        lock (gate)
        {
            Vms[vm.Vmid] = vm;
        }

        return vm;
    }

    public FakeVm? Vm(int vmid)
    {
        lock (gate)
        {
            Tick();
            return Vms.GetValueOrDefault(vmid);
        }
    }

    /// <summary>Termina ya todas las tareas en curso.</summary>
    public void ReleaseTasks()
    {
        lock (gate)
        {
            foreach (var task in tasks.Values)
            {
                task.DoneAt = DateTimeOffset.MinValue;
            }

            Tick();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        Certificate.Dispose();
    }

    /// <summary>Certificado autofirmado para 127.0.0.1 y localhost. La clave vive solo en memoria durante la prueba.</summary>
    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        // SChannel en Windows no usa claves efímeras: se pasa por PKCS#12 para que Kestrel la pueda usar.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    private static string RandomMac() =>
        "bc:24:11:" + string.Join(':', RandomNumberGenerator.GetBytes(3).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    // ---- Atención de pedidos ----

    private async Task Handle(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (path == "/api/revoke")
        {
            await Revoke(ctx);
            return;
        }

        if (!path.StartsWith("/api2/json/", StringComparison.Ordinal))
        {
            await Reply(ctx, 404, "Not found", null);
            return;
        }

        if (ctx.Request.Headers.Authorization.ToString() != "PVEAPIToken=" + TokenId + "=" + Secret)
        {
            await Reply(ctx, 401, "authentication failure", null);
            return;
        }

        var route = $"{ctx.Request.Method} {path["/api2/json".Length..]}";
        lock (calls)
        {
            calls.Add(route);
        }

        if (Hook is not null && await Hook(ctx, route))
        {
            return;
        }

        var form = ctx.Request.HasFormContentType ? (await ctx.Request.ReadFormAsync()).ToDictionary(f => f.Key, f => f.Value.ToString()) : [];
        (int Status, string Reason, JsonNode? Data) result;
        lock (gate)
        {
            Tick();
            try
            {
                result = (200, "OK", Route(ctx.Request.Method, path["/api2/json/".Length..].Split('/'), ctx.Request.Query, form));
            }
            catch (PveError error)
            {
                result = (error.Status, error.Message, null);
            }
        }

        await Reply(ctx, result.Status, result.Reason, result.Data);
    }

    public static async Task Reply(HttpContext ctx, int status, string reason, JsonNode? data)
    {
        ctx.Response.StatusCode = status;
        ctx.Features.Get<IHttpResponseFeature>()!.ReasonPhrase = reason;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(new JsonObject { ["data"] = data }.ToJsonString());
    }

    private sealed class PveError(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private JsonNode? Route(string method, string[] s, IQueryCollection query, Dictionary<string, string> form)
    {
        switch (s)
        {
            case ["cluster", "resources"] when method == "GET":
                return new JsonArray(Vms.Values.OrderBy(v => v.Vmid).Select(v =>
                {
                    var item = new JsonObject
                    {
                        ["id"] = $"{v.Type}/{v.Vmid}",
                        ["type"] = v.Type,
                        ["vmid"] = v.Vmid,
                        ["node"] = v.Node,
                        ["status"] = v.Status,
                        ["template"] = v.Template && !StaleInventory ? 1 : 0,
                    };
                    if (!StaleInventory && v.Config.TryGetValue("name", out var name))
                    {
                        item["name"] = name;
                    }

                    if (v.Config.TryGetValue("lock", out var lockName))
                    {
                        item["lock"] = lockName;
                    }

                    return (JsonNode?)item;
                }).ToArray());
            case ["access", "permissions"] when method == "GET" && query["path"] == "/access/groups":
                return new JsonObject { ["/access/groups"] = AclAllowed ? new JsonObject { ["Sys.Audit"] = 1 } : new JsonObject() };
            case ["access", "permissions"] when method == "GET" && query["path"] == "/vms":
                var privileges = new JsonObject { ["VM.PowerMgmt"] = 1 };
                if (AclAllowed)
                {
                    privileges["Permissions.Modify"] = 1;
                }

                if (VmAudit)
                {
                    foreach (var p in new[] { "VM.Audit", "VM.Allocate", "VM.Clone", "VM.Config.CPU", "VM.Config.Memory", "VM.Config.Disk", "VM.Config.Network", "VM.Config.Cloudinit", "VM.Config.Options" })
                    {
                        privileges[p] = 1;
                    }
                }

                return new JsonObject { ["/vms"] = privileges };
            case ["access", "users"] when method == "GET":
                return new JsonArray(Users.Select(u => (JsonNode?)new JsonObject { ["userid"] = u }).ToArray());
            case ["access", "acl"] when method == "PUT":
                if (!AclAllowed)
                {
                    throw new PveError(403, "Permission check failed (/vms/7001, Permissions.Modify)");
                }

                Acl.Add($"{(form.GetValueOrDefault("delete") == "1" ? "delete" : "add")} {form["path"]} {form["users"]} {form["roles"]}");
                return null;
            case ["pools"] when method == "GET":
                return new JsonArray(Pools.Select(p => (JsonNode?)new JsonObject { ["poolid"] = p }).ToArray());
            case ["cluster", "firewall", "groups"] when method == "GET":
                return new JsonArray(Groups.Select(g => (JsonNode?)new JsonObject { ["group"] = g }).ToArray());
            case ["nodes", var node, "network"] when method == "GET":
                return new JsonArray(Bridges[node].Select(b => (JsonNode?)new JsonObject { ["iface"] = b, ["type"] = "bridge" }).ToArray());
            case ["nodes", var node, "tasks"] when method == "GET" && query["source"] == "active":
                return new JsonArray(tasks.Where(t => !t.Value.Done && t.Key.Split(':')[1] == node && t.Key.Split(':')[6] == query["vmid"])
                    .Select(t => (JsonNode?)new JsonObject { ["upid"] = t.Key, ["type"] = t.Key.Split(':')[5], ["id"] = t.Key.Split(':')[6] })
                    .ToArray());
            case ["nodes", _, "tasks", var upid, "status"] when method == "GET":
                var task = tasks.GetValueOrDefault(upid) ?? throw new PveError(500, "no such task");
                return task.Done
                    ? new JsonObject { ["status"] = "stopped", ["exitstatus"] = task.Exit, ["upid"] = upid }
                    : new JsonObject { ["status"] = "running", ["upid"] = upid };
            case ["nodes", var node, "qemu"] when method == "POST":
                // Una VM nueva sin discos, como la reserva de MAC de una reinstalación.
                var created = int.Parse(form["vmid"], CultureInfo.InvariantCulture);
                if (Vms.ContainsKey(created))
                {
                    throw new PveError(500, $"unable to create VM {created}: config file already exists");
                }

                Vms[created] = new FakeVm
                {
                    Vmid = created,
                    Node = node,
                    Config = form.Where(f => f.Key != "vmid").ToDictionary(f => f.Key, f => f.Value),
                };
                return NewTask(node, "qmcreate", created, TimeSpan.Zero, _ => { });
            case ["nodes", var node, var type, var id, .. var rest] when type is "qemu" or "lxc":
                return VmRoute(method, node, int.Parse(id, CultureInfo.InvariantCulture), rest, form);
            default:
                throw new PveError(501, $"Method '{method} /{string.Join('/', s)}' not implemented");
        }
    }

    private JsonNode? VmRoute(string method, string node, int vmid, string[] rest, Dictionary<string, string> form)
    {
        var vm = Vms.GetValueOrDefault(vmid);
        if (vm is null || vm.Node != node)
        {
            throw new PveError(500, $"Configuration file 'nodes/{node}/qemu-server/{vmid}.conf' does not exist");
        }

        var locked = vm.Config.TryGetValue("lock", out var lockName);

        // Como Proxmox: una segunda tarea sobre la VM espera el lock de su archivo de configuración y falla por tiempo.
        var busy = tasks.Any(t => !t.Value.Done && t.Key.Split(':')[6] == vmid.ToString(CultureInfo.InvariantCulture));
        var contended = $"can't lock file '/var/lock/qemu-server/lock-{vmid}.conf' - got timeout";
        void NotLocked()
        {
            if (locked)
            {
                throw new PveError(500, $"VM {vmid} is locked ({lockName})");
            }
        }

        switch (rest)
        {
            case [] when method == "DELETE":
                // Como Proxmox: el lock se revisa dentro de la tarea, no en el pedido.
                if (locked)
                {
                    return NewTask(node, "qmdestroy", vmid, TimeSpan.Zero, _ => { }, $"VM {vmid} is locked ({lockName})");
                }

                if (busy)
                {
                    return NewTask(node, "qmdestroy", vmid, TimeSpan.Zero, _ => { }, contended);
                }

                if (vm.Status == "running")
                {
                    throw new PveError(500, $"VM {vmid} is running - destroy failed");
                }

                // Proxmox no pone lock en la configuración mientras borra los discos.
                return NewTask(node, "qmdestroy", vmid, TimeSpan.Zero, ok =>
                {
                    if (ok)
                    {
                        Vms.Remove(vmid);
                    }
                }, DestroyWarnings ? "WARNINGS: 1" : null);
            case ["config"] when method == "GET":
                var config = new JsonObject();
                foreach (var (k, v) in vm.Config)
                {
                    config[k] = v;
                }

                if (vm.CiPassword is not null)
                {
                    config["cipassword"] = "**********";
                }

                config["digest"] = "0123456789abcdef";
                return config;
            case ["config"] when method == "PUT":
                NotLocked();
                foreach (var (k, v) in form)
                {
                    if (k == "delete")
                    {
                        foreach (var key in v.Split(','))
                        {
                            vm.Config.Remove(key);
                        }
                    }
                    else if (k == "cipassword")
                    {
                        vm.CiPassword = v;
                    }
                    else
                    {
                        vm.Config[k] = v;
                    }
                }

                return null;
            case ["clone"] when method == "POST":
                if (!vm.Template)
                {
                    throw new PveError(500, "clone of a non-template is not used in these tests");
                }

                var newid = int.Parse(form["newid"], CultureInfo.InvariantCulture);
                if (Vms.ContainsKey(newid))
                {
                    throw new PveError(500, $"unable to create VM {newid}: config file already exists");
                }

                // Como Proxmox: primero la configuración temporal ("# qmclone temporary file", que se lee como descripción)
                // con lock clone, después de cada disco la configuración nueva con lock clone, y al final sin lock.
                var clone = new FakeVm
                {
                    Vmid = newid,
                    Node = form.GetValueOrDefault("target") ?? node,
                    Config = new() { ["description"] = "qmclone temporary file", ["lock"] = "clone" },
                };
                var mac = RandomMac().ToUpperInvariant();
                Dictionary<string, string> Cloned()
                {
                    var config = vm.Config.Where(c => c.Key != "template").ToDictionary(c => c.Key, c => c.Value);
                    config["scsi0"] = $"local-zfs:vm-{newid}-disk-0,size={vm.DiskGb.ToString(CultureInfo.InvariantCulture)}G";
                    config["net0"] = $"virtio={mac},bridge=vmbr0";
                    config["name"] = form["name"];
                    config.Remove("description");
                    if (form.TryGetValue("description", out var description))
                    {
                        config["description"] = description;
                    }

                    return config;
                }

                if (CloneConfigEarly)
                {
                    clone.Config = Cloned();
                    clone.Config["lock"] = "clone";
                }

                Vms[newid] = clone;
                return NewTask(node, "qmclone", vmid, TimeSpan.Zero, ok =>
                {
                    if (!ok)
                    {
                        Vms.Remove(newid);
                        return;
                    }

                    clone.Config = Cloned();
                    clone.Pool = form.GetValueOrDefault("pool");
                });
            case ["resize"] when method == "PUT":
                NotLocked();
                var size = decimal.Parse(form["size"].TrimEnd('G'), CultureInfo.InvariantCulture);
                if (size < vm.DiskGb)
                {
                    throw new PveError(500, "shrinking disks is not supported");
                }

                return NewTask(node, "resize", vmid, TimeSpan.Zero, ok => vm.Config["scsi0"] = vm.Config["scsi0"].Split(",size=")[0] + $",size={form["size"]}");
            case ["status", "current"] when method == "GET":
                return new JsonObject
                {
                    ["status"] = vm.Status,
                    ["vmid"] = vmid,
                    ["cpu"] = 0.0525,
                    ["mem"] = 536870912L,
                    ["maxmem"] = long.Parse(vm.Config.GetValueOrDefault("memory") ?? "1024", CultureInfo.InvariantCulture) * 1048576,
                };
            case ["status", var verb] when method == "POST":
                // Como Proxmox: start, stop y reboot contestan el UPID y la tarea falla adentro si la VM tiene lock.
                if (locked)
                {
                    return NewTask(node, "qm" + verb, vmid, TimeSpan.Zero, _ => { }, $"VM {vmid} is locked ({lockName})");
                }

                if (busy)
                {
                    return NewTask(node, "qm" + verb, vmid, TimeSpan.Zero, _ => { }, contended);
                }

                switch (verb)
                {
                    case "start" when vm.Status == "running":
                        throw new PveError(500, $"VM {vmid} already running");
                    case "reboot" when vm.Status != "running":
                        throw new PveError(500, $"VM {vmid} not running");
                }

                return NewTask(node, "qm" + verb, vmid, TimeSpan.Zero, ok => vm.Status = verb == "stop" ? "stopped" : "running");
            case ["firewall", "options"] when method == "GET":
                return new JsonObject(vm.Firewall.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)JsonValue.Create(f.Value))));
            case ["firewall", "options"] when method == "PUT":
                foreach (var (k, v) in form)
                {
                    vm.Firewall[k] = v;
                }

                return null;
            case ["firewall", "ipset"] when method == "GET":
                return new JsonArray(vm.IpSets.Keys.Select(n => (JsonNode?)new JsonObject { ["name"] = n }).ToArray());
            case ["firewall", "ipset"] when method == "POST":
                if (!vm.IpSets.TryAdd(form["name"], []))
                {
                    throw new PveError(500, "ipset already exists");
                }

                return null;
            case ["firewall", "ipset", var name] when method == "GET":
                return new JsonArray(vm.IpSets[name].Select(c => (JsonNode?)new JsonObject { ["cidr"] = c }).ToArray());
            case ["firewall", "ipset", var name] when method == "POST":
                if (FailIpSetEntry)
                {
                    throw new PveError(500, "ipset error");
                }

                vm.IpSets[name].Add(form["cidr"]);
                return null;
            case ["firewall", "rules"] when method == "GET":
                return new JsonArray(vm.Rules.Select((r, i) =>
                {
                    var rule = new JsonObject { ["pos"] = i };
                    foreach (var (k, v) in r)
                    {
                        rule[k] = v;
                    }

                    return (JsonNode?)rule;
                }).ToArray());
            case ["firewall", "rules"] when method == "POST":
                vm.Rules.Add(new Dictionary<string, string>(form));
                return null;
            case ["firewall", "rules", var pos] when method == "PUT":
                foreach (var (k, v) in form)
                {
                    vm.Rules[int.Parse(pos, CultureInfo.InvariantCulture)][k] = v;
                }

                return null;
            default:
                throw new PveError(501, $"Method '{method} {string.Join('/', rest)}' not implemented");
        }
    }

    // ---- Tareas ----

    private sealed class FakeTask
    {
        public required DateTimeOffset DoneAt { get; set; }

        public required Action<bool> OnDone { get; init; }

        public string Exit { get; set; } = "OK";

        public bool EffectEvenIfFailed { get; set; }

        public bool Done { get; set; }
    }

    /// <summary>Hace que la próxima tarea de ese tipo termine con error, como un clon que falla.</summary>
    public string? FailNextTask { get; set; }

    /// <summary>Si la tarea que falla con <see cref="FailNextTask"/> igual hace su efecto, como un borrado que hizo otra tarea.</summary>
    public bool FailNextTaskAfterEffect { get; set; }

    /// <summary>El exitstatus de la tarea que falla con <see cref="FailNextTask"/>.</summary>
    public string FailNextTaskExit { get; set; } = "command failed: exit code 1";

    /// <summary>Un borrado que no pudo sacar un disco: Proxmox borra la configuración igual y la tarea termina con WARNINGS.</summary>
    public bool DestroyWarnings { get; set; }

    /// <summary>Un nodo con más de 497 días encendido: el pstart del UPID tiene 9 dígitos.</summary>
    public bool LongUptime { get; set; }

    private string NewTask(string node, string type, int id, TimeSpan duration, Action<bool> onDone, string? exit = null)
    {
        var n = Interlocked.Increment(ref taskCounter);
        var pstart = LongUptime ? (0x100000000L + n).ToString("X9", CultureInfo.InvariantCulture) : n.ToString("X8", CultureInfo.InvariantCulture);
        var upid = string.Create(CultureInfo.InvariantCulture, $"UPID:{node}:{n:X8}:{pstart}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds():X8}:{type}:{id}:{TokenId}:");
        var task = new FakeTask { DoneAt = DateTimeOffset.UtcNow + Durations.GetValueOrDefault(type, duration), OnDone = onDone };
        if (exit is not null)
        {
            task.Exit = exit;
        }
        else if (FailNextTask == type)
        {
            task.Exit = FailNextTaskExit;
            task.EffectEvenIfFailed = FailNextTaskAfterEffect;
            FailNextTask = null;
        }

        tasks[upid] = task;
        Tick();
        return upid;
    }

    private void Tick()
    {
        foreach (var task in tasks.Values.Where(t => !t.Done && t.DoneAt <= DateTimeOffset.UtcNow).ToList())
        {
            task.Done = true;
            task.OnDone(task.Exit == "OK" || task.Exit.StartsWith("WARNINGS", StringComparison.Ordinal) || task.EffectEvenIfFailed);
        }
    }

    // ---- Gateway de consola ----

    private async Task Revoke(HttpContext ctx)
    {
        if (GatewayDown)
        {
            await Reply(ctx, 503, "Service Unavailable", null);
            return;
        }

        var body = await JsonNode.ParseAsync(ctx.Request.Body);
        var grant = Grants.Verify(body?["grant"]?.GetValue<string>(), ConsoleSecret, "proxmox-console-revoke");
        if (grant is null)
        {
            await Reply(ctx, 403, "Forbidden", null);
            return;
        }

        lock (gate)
        {
            Revoked.Add(grant);
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("""{"ok":true}""");
    }
}

/// <summary>
/// Las reglas de verifyGrant del gateway de consola (console-gateway/server.mjs), repetidas en C# para comprobar los
/// grants del módulo: formato, HMAC con el texto literal del secreto, audiencia, tiempos, jti y destino.
/// </summary>
internal static class Grants
{
    public static JsonObject? Verify(string? token, string secret, string audience)
    {
        if (token is null || token.Length > 4096 || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]{43}$"))
        {
            return null;
        }

        var parts = token.Split('.');
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(Base64Url.DecodeFromChars(parts[1]), expected))
        {
            return null;
        }

        var data = JsonNode.Parse(Base64Url.DecodeFromChars(parts[0]))!.AsObject();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var iat = data["iat"]!.GetValue<long>();
        var exp = data["exp"]!.GetValue<long>();
        if (data["v"]!.GetValue<int>() != 1 || data["aud"]!.GetValue<string>() != audience
            || data["sub"]!.GetValue<long>() < 1 || iat > now || exp <= now || exp - iat > 60
            || !System.Text.RegularExpressions.Regex.IsMatch(data["jti"]!.GetValue<string>(), "^[a-f0-9]{32,128}$")
            || !System.Text.RegularExpressions.Regex.IsMatch(data["server"]!.GetValue<string>(), "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
        {
            return null;
        }

        if (audience == "proxmox-console" && (data["node"] is null || data["vmid"]!.GetValue<int>() < 100))
        {
            return null;
        }

        return data;
    }
}
