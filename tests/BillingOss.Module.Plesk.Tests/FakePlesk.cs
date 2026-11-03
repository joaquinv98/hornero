using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace BillingOss.Module.Plesk.Tests;

/// <summary>Un paquete que el módulo mandó al Plesk falso, con la ruta y el encabezado de la clave.</summary>
internal sealed record PleskCall(string Method, string Path, XElement Packet, string? Key, string ContentType)
{
    /// <summary>Las operaciones del paquete, como "customer/get".</summary>
    public string[] Operations => [.. Packet.Elements().Select(o => $"{o.Name}/{o.Elements().First().Name}")];

    /// <summary>La primera operación del paquete con ese nombre, por ejemplo "webspace/add".</summary>
    public XElement Operation(string name) =>
        Packet.Elements().Select(o => o.Elements().First()).First(a => $"{a.Parent!.Name}/{a.Name}" == name);
}

/// <summary>Un cliente del Plesk falso.</summary>
internal sealed class PleskCustomer
{
    public long Id { get; init; }

    public string Name { get; set; } = "Cliente";

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string? ExternalId { get; set; }
}

/// <summary>Una suscripción del Plesk falso. Owner es el login del cliente, o admin.</summary>
internal sealed class PleskWebspace
{
    public long Id { get; init; }

    public string Owner { get; set; } = "admin";

    public string PlanGuid { get; set; } = FakePlesk.Guid("basico");

    public int Status { get; set; }

    public string FtpLogin { get; set; } = string.Empty;

    public string FtpPassword { get; set; } = string.Empty;

    public string Ip { get; set; } = FakePlesk.Ip;

    public long RealSize { get; set; } = 120L * 1024 * 1024;

    public long Traffic { get; set; } = 35L * 1024 * 1024;

    public long DiskLimit { get; set; } = 1000L * 1024 * 1024;

    public long TrafficLimit { get; set; } = -1;
}

/// <summary>
/// Plesk falso: simula las operaciones de la API XML que usa el módulo sobre clientes, suscripciones, planes y sitios en
/// memoria, con la forma de sus paquetes de respuesta (status, errcode y errtext por resultado, y el error de todo el
/// paquete en system). Cada test puede pisar la respuesta de un paquete que lleva una operación o cortar la conexión, y
/// anota todo lo que recibe.
/// </summary>
internal sealed class FakePlesk : HttpMessageHandler
{
    public const string Host = "plesk.example.test";

    /// <summary>Inventada: no sirve contra ningún Plesk.</summary>
    public const string Key = "CLAVEINVENTADADEPRUEBA0123456789abcdef";

    public const string Ip = "192.0.2.10";

    public static readonly string[] Plans = ["basico", "pro", "Plan Empresa"];

    private readonly Dictionary<string, Func<PleskCall, Func<(int Status, string Body)>, (int Status, string Body)>> overrides = new(StringComparer.Ordinal);
    private readonly List<PleskCall> calls = [];
    private long nextId = 100;

    public Dictionary<string, PleskCustomer> Customers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, PleskWebspace> Webspaces { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dominios adicionales de alguna suscripción.</summary>
    public HashSet<string> Sites { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>La IP del navegador de la última sesión creada, ya decodificada.</summary>
    public string? LastUserIp { get; private set; }

    public IReadOnlyList<PleskCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public string[] Operations => [.. Calls.SelectMany(c => c.Operations)];

    public string[] Mutations => [.. Operations.Where(o => !o.EndsWith("/get", StringComparison.Ordinal) && o != "server/create_session")];

    public static string Guid(string plan) => $"guid-{plan.Replace(' ', '-').ToLowerInvariant()}";

    public PleskCall Last(string operation) => Calls.Last(c => c.Operations.Contains(operation));

    /// <summary>Un cliente con una suscripción. Con dominio vacío, el cliente solo, como un alta cortada a la mitad.</summary>
    public FakePlesk With(string login, string domain = "example.test", string plan = "basico", bool suspended = false, string? marker = null)
    {
        Customers[login] = new PleskCustomer { Id = ++nextId, ExternalId = marker };
        return domain.Length == 0 ? this : Subscription(login, domain, plan, suspended);
    }

    public FakePlesk Subscription(string owner, string domain, string plan = "basico", bool suspended = false)
    {
        Webspaces[domain] = new PleskWebspace { Id = ++nextId, Owner = owner, PlanGuid = Guid(plan), Status = suspended ? 16 : 0, FtpLogin = owner };
        return this;
    }

    /// <summary>Pisa la respuesta de los paquetes que llevan esa operación. La función recibe la respuesta normal, por si la quiere aplicar.</summary>
    public FakePlesk On(string operation, Func<PleskCall, Func<(int Status, string Body)>, (int Status, string Body)> respond)
    {
        overrides[operation] = respond;
        return this;
    }

    public FakePlesk On(string operation, int status, string body) => On(operation, (_, _) => (status, body));

    /// <summary>Un corte de conexión después de mandar el paquete. Con apply, Plesk llegó a ejecutarlo.</summary>
    public FakePlesk CutAfter(string operation, bool apply) => On(operation, (_, run) =>
    {
        if (apply)
        {
            run();
        }

        throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
    });

    /// <summary>Un paquete de respuesta con un resultado por operación, como ("customer/add", Ok()).</summary>
    public static string Reply(params (string Operation, XElement Result)[] results) => new XElement(
        "packet",
        new XAttribute("version", "1.6.9.1"),
        results.Select(r => new XElement(r.Operation.Split('/')[0], new XElement(r.Operation.Split('/')[1], r.Result)))).ToString();

    public static XElement Ok(params object?[] content) => new("result", new XElement("status", "ok"), content);

    public static XElement Error(int code, string text, string? filterId = null) => new(
        "result",
        new XElement("status", "error"),
        new XElement("errcode", code),
        new XElement("errtext", text),
        filterId is null ? null : new XElement("filter-id", filterId));

    /// <summary>El error de todo el paquete, como el de una clave rechazada.</summary>
    public static string SystemError(int code, string text) => new XElement(
        "packet",
        new XAttribute("version", "1.6.9.1"),
        new XElement("system", new XElement("status", "error"), new XElement("errcode", code), new XElement("errtext", text))).ToString();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var call = new PleskCall(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            XElement.Parse(body),
            request.Headers.TryGetValues("KEY", out var key) ? key.Single() : null,
            request.Content.Headers.ContentType?.MediaType ?? string.Empty);
        lock (calls)
        {
            calls.Add(call);
        }

        var respond = call.Operations.Select(o => overrides.GetValueOrDefault(o)).FirstOrDefault(r => r is not null);
        var (status, text) = respond is not null ? respond(call, () => Default(call)) : Default(call);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };
    }

    private (int Status, string Body) Default(PleskCall call)
    {
        lock (Customers)
        {
            return (200, Run(call));
        }
    }

    private string Run(PleskCall call)
    {
        if (call.Key != Key)
        {
            return SystemError(1001, "Authentication failed - wrong secret key or usage of not allowed IP address.");
        }

        if (call.Method != "POST" || call.Path != "/enterprise/control/agent.php" || call.ContentType != "text/xml" || call.Packet.Name != "packet")
        {
            return SystemError(1014, "Parser error: el Plesk falso solo atiende paquetes por POST a agent.php.");
        }

        var response = new XElement("packet", new XAttribute("version", "1.6.9.1"));
        foreach (var operation in call.Packet.Elements())
        {
            var action = operation.Elements().First();
            response.Add(new XElement(operation.Name, new XElement(action.Name, Results(operation.Name.LocalName, action))));
        }

        return response.ToString();
    }

    private IEnumerable<XElement> Results(string op, XElement action) => (op, action.Name.LocalName) switch
    {
        ("customer", "get") => CustomerGet(action),
        ("customer", "add") => [CustomerAdd(action)],
        ("customer", "set") => [CustomerSet(action)],
        ("customer", "del") => [CustomerDel(action)],
        ("webspace", "get") => WebspaceGet(action),
        ("webspace", "add") => [WebspaceAdd(action)],
        ("webspace", "set") => [WebspaceSet(action)],
        ("webspace", "del") => [WebspaceDel(action)],
        ("webspace", "switch-subscription") => [Switch(action)],
        ("service-plan", "get") => Plans.Select((p, i) => Ok(new XElement("id", i + 1), new XElement("name", p), new XElement("guid", Guid(p)), new XElement("external-id"))),
        ("site", "get") => [SiteGet(Value(action, "filter", "name") ?? string.Empty)],
        ("server", "create_session") => [Session(action)],
        _ => [Error(1017, $"Operación no soportada por el Plesk falso: {op}/{action.Name}")],
    };

    private IEnumerable<XElement> CustomerGet(XElement action)
    {
        var login = Value(action, "filter", "login");
        var id = Value(action, "filter", "id");
        var found = Customers.Where(c => login is not null ? string.Equals(c.Key, login, StringComparison.OrdinalIgnoreCase) : Text(c.Value.Id) == id).ToList();
        return found.Count == 0
            ? [Error(1013, "client does not exist", login ?? id)]
            : found.Select(c => Ok(
                new XElement("filter-id", login ?? id),
                new XElement("id", c.Value.Id),
                new XElement("data", new XElement(
                    "gen_info",
                    new XElement("cr_date", "2026-10-01"),
                    new XElement("pname", c.Value.Name),
                    new XElement("login", c.Key),
                    new XElement("status", 0),
                    new XElement("email", c.Value.Email),
                    new XElement("guid", $"guid-cliente-{c.Value.Id}"),
                    new XElement("external-id", c.Value.ExternalId ?? string.Empty)))));
    }

    private XElement CustomerAdd(XElement action)
    {
        var info = action.Element("gen_info")!;
        var login = info.Element("login")?.Value ?? string.Empty;
        var marker = info.Element("external-id")?.Value;
        if (Customers.ContainsKey(login))
        {
            return Error(1007, $"Client with login {login} already exists.");
        }

        if (marker is { Length: > 0 } && Customers.Values.Any(c => c.ExternalId == marker))
        {
            return Error(1007, "Client with such external-id already exists.");
        }

        if (string.IsNullOrEmpty(info.Element("pname")?.Value) || info.Element("passwd")?.Value is not { Length: >= 8 })
        {
            return Error(1019, "Invalid value of pname or passwd.");
        }

        var customer = new PleskCustomer
        {
            Id = ++nextId,
            Name = info.Element("pname")!.Value,
            Email = info.Element("email")?.Value ?? string.Empty,
            Password = info.Element("passwd")!.Value,
            ExternalId = marker,
        };
        Customers[login] = customer;
        return Ok(new XElement("id", customer.Id), new XElement("guid", $"guid-cliente-{customer.Id}"));
    }

    private XElement CustomerSet(XElement action)
    {
        var login = Value(action, "filter", "login") ?? string.Empty;
        if (!Customers.TryGetValue(login, out var customer))
        {
            return Error(1013, "client does not exist", login);
        }

        if (action.Element("values")?.Element("gen_info")?.Element("passwd")?.Value is { } password)
        {
            customer.Password = password;
        }

        return Ok(new XElement("filter-id", login), new XElement("id", customer.Id));
    }

    /// <summary>Borra el cliente y todas sus suscripciones.</summary>
    private XElement CustomerDel(XElement action)
    {
        var login = Value(action, "filter", "login") ?? string.Empty;
        if (!Customers.Remove(login, out var customer))
        {
            return Error(1013, "client does not exist", login);
        }

        foreach (var name in Webspaces.Where(w => string.Equals(w.Value.Owner, login, StringComparison.OrdinalIgnoreCase)).Select(w => w.Key).ToList())
        {
            Webspaces.Remove(name);
        }

        return Ok(new XElement("filter-id", login), new XElement("id", customer.Id));
    }

    private IEnumerable<XElement> WebspaceGet(XElement action)
    {
        var name = Value(action, "filter", "name");
        var owner = Value(action, "filter", "owner-login");
        var found = Webspaces
            .Where(w => name is not null ? string.Equals(w.Key, name, StringComparison.OrdinalIgnoreCase) : string.Equals(w.Value.Owner, owner, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (found.Count == 0)
        {
            return [Error(1013, "webspace does not exist", name ?? owner)];
        }

        var dataset = action.Element("dataset")?.Elements().Select(e => e.Name.LocalName).ToHashSet(StringComparer.Ordinal) ?? [];
        return found.Select(w => Ok(new XElement("filter-id", name ?? owner), new XElement("id", w.Value.Id), new XElement("data", Data(w.Key, w.Value, dataset))));
    }

    private IEnumerable<XElement> Data(string name, PleskWebspace webspace, HashSet<string> dataset)
    {
        if (dataset.Contains("gen_info"))
        {
            yield return new XElement(
                "gen_info",
                new XElement("cr_date", "2026-10-01"),
                new XElement("name", name),
                new XElement("ascii-name", name),
                new XElement("status", webspace.Status),
                new XElement("real_size", webspace.RealSize),
                new XElement("owner-id", Customers.TryGetValue(webspace.Owner, out var customer) ? customer.Id : 1),
                new XElement("dns_ip_address", webspace.Ip),
                new XElement("htype", "vrt_hst"),
                new XElement("guid", $"guid-suscripcion-{webspace.Id}"));
        }

        if (dataset.Contains("subscriptions"))
        {
            yield return new XElement("subscriptions", new XElement(
                "subscription",
                new XElement("locked", "true"),
                new XElement("synchronized", "true"),
                new XElement("plan", new XElement("plan-guid", webspace.PlanGuid))));
        }

        if (dataset.Contains("stat"))
        {
            yield return new XElement("stat", new XElement("traffic", webspace.Traffic), new XElement("subdom", 0), new XElement("box", 2));
        }

        if (dataset.Contains("limits"))
        {
            yield return new XElement(
                "limits",
                new XElement("overuse", "normal"),
                new XElement("limit", new XElement("name", "disk_space"), new XElement("value", webspace.DiskLimit)),
                new XElement("limit", new XElement("name", "max_traffic"), new XElement("value", webspace.TrafficLimit)));
        }
    }

    private XElement WebspaceAdd(XElement action)
    {
        var setup = action.Element("gen_setup")!;
        var name = setup.Element("name")?.Value ?? string.Empty;
        var owner = setup.Element("owner-login")?.Value ?? string.Empty;
        var hosting = action.Element("hosting")?.Element("vrt_hst");
        string? Property(string property) => hosting?.Elements("property").FirstOrDefault(p => p.Element("name")?.Value == property)?.Element("value")?.Value;
        var plan = action.Element("plan-name")?.Value;
        if (!Customers.ContainsKey(owner))
        {
            return Error(1015, $"Owner {owner} not found.");
        }

        if (Webspaces.ContainsKey(name) || Sites.Contains(name))
        {
            return Error(1007, $"Domain {name} already exists.");
        }

        if (plan is null || !Plans.Contains(plan))
        {
            return Error(1013, $"Service plan {plan} does not exist.");
        }

        if (setup.Element("ip_address")?.Value != Ip || hosting?.Element("ip_address")?.Value != Ip)
        {
            return Error(1018, "IP address not found.");
        }

        var ftp = Property("ftp_login") ?? string.Empty;
        if (ftp.Length == 0 || Webspaces.Values.Any(w => w.FtpLogin == ftp && !string.Equals(w.Owner, owner, StringComparison.OrdinalIgnoreCase)))
        {
            return Error(1007, $"System user {ftp} already exists.");
        }

        var webspace = new PleskWebspace
        {
            Id = ++nextId,
            Owner = owner,
            PlanGuid = Guid(plan),
            FtpLogin = ftp,
            FtpPassword = Property("ftp_password") ?? string.Empty,
            Status = int.Parse(setup.Element("status")?.Value ?? "0", CultureInfo.InvariantCulture),
        };
        Webspaces[name] = webspace;
        return Ok(new XElement("id", webspace.Id), new XElement("guid", $"guid-suscripcion-{webspace.Id}"));
    }

    private XElement WebspaceSet(XElement action)
    {
        var name = Value(action, "filter", "name") ?? string.Empty;
        if (!Webspaces.TryGetValue(name, out var webspace))
        {
            return Error(1013, "webspace does not exist", name);
        }

        var values = action.Element("values");
        if (values?.Element("gen_setup")?.Element("status")?.Value is { } status)
        {
            webspace.Status = int.Parse(status, CultureInfo.InvariantCulture);
        }

        var password = values?.Element("hosting")?.Element("vrt_hst")?.Elements("property")
            .FirstOrDefault(p => p.Element("name")?.Value == "ftp_password")?.Element("value")?.Value;
        if (password is not null)
        {
            webspace.FtpPassword = password;
        }

        return Ok(new XElement("filter-id", name), new XElement("id", webspace.Id));
    }

    private XElement WebspaceDel(XElement action)
    {
        var name = Value(action, "filter", "name") ?? string.Empty;
        return Webspaces.Remove(name, out var webspace)
            ? Ok(new XElement("filter-id", name), new XElement("id", webspace.Id))
            : Error(1013, "webspace does not exist", name);
    }

    private XElement Switch(XElement action)
    {
        var name = Value(action, "filter", "name") ?? string.Empty;
        var guid = action.Element("plan-guid")?.Value;
        if (!Webspaces.TryGetValue(name, out var webspace))
        {
            return Error(1013, "webspace does not exist", name);
        }

        if (guid is null || !Plans.Any(p => Guid(p) == guid))
        {
            return Error(1013, $"Service plan {guid} does not exist.");
        }

        webspace.PlanGuid = guid;
        return Ok(new XElement("filter-id", name), new XElement("id", webspace.Id));
    }

    private XElement SiteGet(string name) => Sites.Contains(name)
        ? Ok(new XElement("filter-id", name), new XElement("id", 7), new XElement("data", new XElement("gen_info", new XElement("name", name), new XElement("webspace-id", 1))))
        : Error(1013, "site does not exist", name);

    private XElement Session(XElement action)
    {
        var login = action.Element("login")?.Value ?? string.Empty;
        var ip = action.Element("data")?.Element("user_ip")?.Value;
        if (!Customers.ContainsKey(login) || ip is null)
        {
            return Error(1013, "client does not exist");
        }

        LastUserIp = Encoding.ASCII.GetString(Convert.FromBase64String(ip));
        return Ok(new XElement("id", "SESIONINVENTADA0123456789abcdef"));
    }

    private static string? Value(XElement action, string parent, string name) => action.Element(parent)?.Element(name)?.Value;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
