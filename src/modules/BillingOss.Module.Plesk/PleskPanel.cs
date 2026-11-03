using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Plesk;

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class PleskConfig : PanelConfig
{
    public string? ApiKey { get; init; }

    public string? IpAddress { get; init; }
}

/// <summary>
/// Una cuenta de hosting en Plesk vista por su API XML: POST a https://{host}:{port}/enterprise/control/agent.php con un
/// paquete (<c>packet</c>) de una o más operaciones y el encabezado <c>KEY</c> con la clave secreta. Cada operación vuelve
/// con sus resultados: <c>status</c> ok, o error con <c>errcode</c> y <c>errtext</c>. La cuenta es un cliente de Plesk con
/// el usuario del servicio como login, dueño de una suscripción con el dominio del servicio. La marca es el uuid del
/// servicio en el <c>external-id</c> del cliente.
/// </summary>
internal sealed class PleskPanel(PanelTransport transport, PanelEndpoint endpoint, string apiKey, string ip, IReadOnlyList<string?> hidden) : IHostingPanel
{
    /// <summary>El objeto del filtro no existe: es la ausencia probada.</summary>
    private const int NotFound = 1013;

    /// <summary>La suscripción suspendida por el administrador. 0 es activa.</summary>
    private const int SuspendedByAdmin = 16;

    /// <summary>Autenticación rechazada (clave inválida o creada para otra IP) y permiso denegado: es la configuración.</summary>
    private static readonly int[] DeniedCodes = [1001, 1006];

    public string UsernamePrefix => endpoint.UsernamePrefix;

    public Uri LoginUrl => endpoint.Url("/");

    public bool KeepsMarker => true;

    private IEnumerable<string?> Secrets => hidden.Append(apiKey);

    public static PleskPanel Open(ModuleCall call, Func<string?, HttpClient> http, Budget budget, ILogger logger, IReadOnlyList<string?> hidden)
    {
        var config = Settings.Read<PleskConfig>(call);
        var bad = new List<string>();
        var endpoint = Settings.Endpoint(config, 8443, bad);
        if (!Settings.IsToken(config.ApiKey))
        {
            bad.Add("api_key");
        }

        if (!Settings.IsIPv4(config.IpAddress))
        {
            bad.Add("ip_address");
        }

        return bad.Count > 0
            ? throw Fail.InvalidConfig(bad)
            : new PleskPanel(new PanelTransport(http(endpoint.Fingerprint), budget, logger, "Plesk"), endpoint, config.ApiKey!, config.IpAddress!, hidden);
    }

    public async Task<IReadOnlyList<string>> Plans() =>
        [.. ServicePlans(await Query("service-plan/get", ListPlans()).ConfigureAwait(false)).Select(p => p.Name)];

    /// <summary>
    /// La cuenta: el cliente por su login, sus suscripciones y los planes, en un solo paquete. Con la suscripción del dominio
    /// es la cuenta del servicio. Un cliente sin ninguna suscripción es un alta a medias o un cliente vacío, y la marca dice
    /// de quién es. Un cliente con otras suscripciones y sin la del dominio es de otros servicios: para este no está.
    /// </summary>
    public async Task<AccountState> Read(PanelAccount account)
    {
        var snapshot = await Load(account.Username).ConfigureAwait(false);
        if (snapshot.Customer is not { } customer)
        {
            return AccountState.Absent;
        }

        var subscription = snapshot.Subscriptions.FirstOrDefault(s => Same(s.Domain, account.Domain));
        if (subscription is null)
        {
            return snapshot.Subscriptions.Count == 0 ? new AccountState { Exists = true, Marker = customer.Marker } : AccountState.Absent;
        }

        return new AccountState
        {
            Exists = true,
            OwnsDomain = true,
            Plan = snapshot.Plans.FirstOrDefault(p => subscription.PlanGuids.Contains(p.Guid, StringComparer.OrdinalIgnoreCase))?.Name,
            Suspended = subscription.Status != 0,
            Marker = customer.Marker,
        };
    }

    /// <summary>
    /// El login del cliente dueño de la suscripción del dominio. Si es del administrador o de un revendedor, o si el dominio
    /// es un sitio adicional de otra suscripción, devuelve una marca que no es un login: el dominio está ocupado.
    /// </summary>
    public async Task<string?> DomainOwner(string domain)
    {
        var packet = await Query(
            "webspace/get+site/get",
            Op("webspace", "get", Filter("name", domain), Dataset("gen_info")),
            Op("site", "get", Filter("name", domain), Dataset("gen_info"))).ConfigureAwait(false);
        if (Found(packet, "webspace", "get").FirstOrDefault() is { } subscription)
        {
            var owner = Info(subscription)?.Element("owner-id")?.Value;
            if (!long.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw Unreadable("webspace/get");
            }

            var customer = Found(await Query("customer/get", Op("customer", "get", Filter("id", owner!), Dataset("gen_info"))).ConfigureAwait(false), "customer", "get");
            return customer.Count == 0 ? $"#{owner}"
                : Info(customer[0])?.Element("login")?.Value is { Length: > 0 } login ? login
                : throw Unreadable("customer/get");
        }

        return Found(packet, "site", "get").Count > 0 ? "#site" : null;
    }

    /// <summary>
    /// El cliente con la marca y su suscripción en un solo paquete, o solo la suscripción si el cliente quedó de un alta
    /// anterior de este servicio. Antes de mandar nada controla el plan: un plan que no existe dejaría un cliente sin
    /// suscripción.
    /// </summary>
    public async Task<Ack> Create(NewAccount account)
    {
        var snapshot = await Load(account.Username).ConfigureAwait(false);
        PlanGuid(snapshot.Plans, account.Plan);
        var operations = new List<XElement>();
        if (snapshot.Customer is null)
        {
            operations.Add(Op("customer", "add", new XElement(
                "gen_info",
                new XElement("pname", account.Name ?? account.Username),
                new XElement("login", account.Username),
                new XElement("passwd", account.Password),
                new XElement("status", 0),
                new XElement("email", account.Email),
                new XElement("external-id", account.Marker))));
        }
        else if (!Same(snapshot.Customer.Marker, account.Marker) || snapshot.Subscriptions.Count > 0)
        {
            throw Fail.Rejected($"El cliente {account.Username} ya existe en Plesk y no es un alta a medias de este servicio. No se creó nada.", "user_taken", null);
        }

        operations.Add(Op(
            "webspace",
            "add",
            new XElement(
                "gen_setup",
                new XElement("name", account.Domain),
                new XElement("owner-login", account.Username),
                new XElement("htype", "vrt_hst"),
                new XElement("ip_address", ip),
                new XElement("status", 0)),
            new XElement("hosting", new XElement(
                "vrt_hst",
                Property("ftp_login", account.Username),
                Property("ftp_password", account.Password),
                new XElement("ip_address", ip))),
            new XElement("plan-name", account.Plan)));
        return await Mutate(snapshot.Customer is null ? "customer/add+webspace/add" : "webspace/add", [.. operations]).ConfigureAwait(false);
    }

    /// <summary>Suspende la suscripción, no el cliente: un cliente importado puede tener otras de otros servicios.</summary>
    public Task<Ack> Suspend(PanelAccount account) => SetStatus(account, SuspendedByAdmin);

    public Task<Ack> Unsuspend(PanelAccount account) => SetStatus(account, 0);

    /// <summary>Borra el cliente con su suscripción. Si el cliente tiene otras, que son de otros servicios, borra solo la del dominio.</summary>
    public async Task<Ack> Delete(PanelAccount account)
    {
        var snapshot = await Load(account.Username).ConfigureAwait(false);
        return snapshot.Subscriptions.Any(s => !Same(s.Domain, account.Domain))
            ? await Mutate("webspace/del", Op("webspace", "del", Filter("name", account.Domain))).ConfigureAwait(false)
            : await Mutate("customer/del", Op("customer", "del", Filter("login", account.Username))).ConfigureAwait(false);
    }

    /// <summary>Pasa la suscripción a otro plan de servicio, que Plesk pide por su GUID.</summary>
    public async Task<Ack> ChangePlan(PanelAccount account, string plan)
    {
        var guid = PlanGuid(ServicePlans(await Query("service-plan/get", ListPlans()).ConfigureAwait(false)), plan);
        return await Mutate(
            "webspace/switch-subscription",
            Op("webspace", "switch-subscription", Filter("name", account.Domain), new XElement("plan-guid", guid))).ConfigureAwait(false);
    }

    /// <summary>La contraseña del cliente para entrar al panel y la del usuario de FTP de la suscripción, en un solo paquete.</summary>
    public Task<Ack> ChangePassword(PanelAccount account, string password) => Mutate(
        "customer/set+webspace/set",
        Op("customer", "set", Filter("login", account.Username), new XElement("values", new XElement("gen_info", new XElement("passwd", password)))),
        Op("webspace", "set", Filter("name", account.Domain), new XElement("values", new XElement("hosting", new XElement("vrt_hst", Property("ftp_password", password))))));

    /// <summary>
    /// Una sesión del cliente, atada a la IP del navegador, que el navegador abre en rsession_init.php. La URL lleva la
    /// sesión: nunca va a los logs.
    /// </summary>
    public async Task<SsoLink> Sso(PanelAccount account, string? clientIp)
    {
        if (clientIp is null)
        {
            throw Fail.InvalidInput(["client_ip"], "Plesk ata la sesión a la IP del navegador: el núcleo tiene que mandar input.client_ip.");
        }

        var packet = await Query("server/create_session", Op(
            "server",
            "create_session",
            new XElement("login", account.Username),
            new XElement("data", new XElement("user_ip", Convert.ToBase64String(Encoding.ASCII.GetBytes(clientIp))), new XElement("source_server", string.Empty)))).ConfigureAwait(false);
        var session = Found(packet, "server", "create_session").FirstOrDefault()?.Element("id")?.Value;
        return string.IsNullOrEmpty(session)
            ? throw Unreadable("server/create_session")
            : new SsoLink(new UriBuilder(endpoint.Url("/enterprise/rsession_init.php")) { Query = "PLESKSESSID=" + Uri.EscapeDataString(session) }.Uri, null);
    }

    /// <summary>El disco (real_size) y la transferencia del mes (stat/traffic), que Plesk da en bytes.</summary>
    public async Task<PanelUsage> Usage(PanelAccount account)
    {
        var packet = await Query("webspace/get", Op("webspace", "get", Filter("name", account.Domain), Dataset("gen_info", "stat"))).ConfigureAwait(false);
        var data = Found(packet, "webspace", "get").FirstOrDefault()?.Element("data") ?? throw Fail.NotFound("La suscripción no existe en Plesk.");
        return new PanelUsage(
            Bytes(data.Element("gen_info")?.Element("real_size")) / JsonRead.MiB,
            Bytes(data.Element("stat")?.Element("traffic")) / JsonRead.MiB);
    }

    private Task<Ack> SetStatus(PanelAccount account, int status) => Mutate(
        "webspace/set",
        Op("webspace", "set", Filter("name", account.Domain), new XElement("values", new XElement("gen_setup", new XElement("status", status)))));

    /// <summary>El cliente, sus suscripciones y los planes. Las suscripciones solo cuentan si el cliente existe.</summary>
    private async Task<Snapshot> Load(string username)
    {
        var packet = await Query(
            "customer/get+webspace/get+service-plan/get",
            Op("customer", "get", Filter("login", username), Dataset("gen_info")),
            Op("webspace", "get", Filter("owner-login", username), Dataset("gen_info", "subscriptions")),
            ListPlans()).ConfigureAwait(false);
        var plans = ServicePlans(packet);
        var customer = Found(packet, "customer", "get").FirstOrDefault();
        if (customer is null)
        {
            return new Snapshot(null, [], plans);
        }

        var subscriptions = Found(packet, "webspace", "get").Select(result =>
        {
            var info = Info(result);
            var name = info?.Element("ascii-name")?.Value is { Length: > 0 } ascii ? ascii : info?.Element("name")?.Value;
            return name is null || !int.TryParse(info!.Element("status")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
                ? throw Unreadable("webspace/get")
                : new Subscription(
                    Names.Domain(name) ?? name,
                    status,
                    [.. result.Element("data")!.Elements("subscriptions").Elements("subscription").Elements("plan").Elements("plan-guid").Select(g => g.Value)]);
        }).ToList();
        return new Snapshot(new Customer(Info(customer)?.Element("external-id")?.Value is { Length: > 0 } marker ? marker : null), subscriptions, plans);
    }

    /// <summary>Los planes de servicio del dueño de la clave, con nombre y GUID.</summary>
    private List<ServicePlan> ServicePlans(XElement packet) =>
        [.. Found(packet, "service-plan", "get").Select(r => r.Element("name")?.Value is { Length: > 0 } name && r.Element("guid")?.Value is { Length: > 0 } guid
            ? new ServicePlan(name, guid)
            : throw Unreadable("service-plan/get"))];

    /// <summary>Una lectura. Lo que no es un paquete de Plesk no prueba nada: no disponible.</summary>
    private async Task<XElement> Query(string function, params XElement[] operations)
    {
        var reply = await transport.Read(function, () => Request(operations)).ConfigureAwait(false);
        var (packet, error) = Parse(reply);
        if (packet is null || reply.Status is < 200 or >= 300)
        {
            throw Unreadable(function);
        }

        return error is null ? packet : throw Fail.Unavailable($"Plesk respondió un error a {function}: {error}", "upstream_error", error);
    }

    /// <summary>
    /// Un cambio. Solo un paquete con todas sus operaciones en ok es éxito, pero ni ese ni el error alcanzan: después se
    /// vuelve a leer. Un 5xx, un cuerpo que no es de Plesk o un paquete sin los resultados pueden venir de algo intermedio
    /// con el pedido todavía corriendo: quedan sin respuesta.
    /// </summary>
    private async Task<Ack> Mutate(string function, params XElement[] operations)
    {
        var reply = await transport.Mutate(function, () => Request(operations)).ConfigureAwait(false);
        if (reply is null)
        {
            return Ack.NoReply;
        }

        var (packet, error) = Parse(reply);
        if (packet is null || reply.Status >= 500)
        {
            return Ack.NoReply;
        }

        if (error is not null)
        {
            // Plesk rechazó el paquete entero: no corrió ninguna operación.
            return Ack.Refused(error);
        }

        var results = new List<XElement>();
        foreach (var operation in operations)
        {
            var found = packet.Element(operation.Name)?.Element(operation.Elements().First().Name)?.Elements("result").ToList();
            if (found is not { Count: > 0 })
            {
                return Ack.NoReply;
            }

            results.AddRange(found);
        }

        var failed = results.FirstOrDefault(r => !Ok(r));
        if (failed is null)
        {
            return reply.Status is >= 200 and < 300 ? Ack.Done : Ack.NoReply;
        }

        return IsDenied(failed) ? throw Denied(Describe(reply.Status, failed)) : Ack.Refused(Describe(reply.Status, failed));
    }

    /// <summary>
    /// Los resultados en ok de una operación de lectura del paquete. Vacío si Plesk dice que el objeto no existe. Otro error
    /// no prueba nada: no disponible, o configuración si es un rechazo de la clave o de un permiso.
    /// </summary>
    private List<XElement> Found(XElement packet, string op, string action)
    {
        var results = packet.Element(op)?.Element(action)?.Elements("result").ToList() ?? throw Unreadable($"{op}/{action}");
        foreach (var result in results.Where(r => !Ok(r)))
        {
            if (result.Element("status")?.Value != "error")
            {
                throw Unreadable($"{op}/{action}");
            }

            if (Code(result) == NotFound)
            {
                continue;
            }

            var text = Describe(200, result);
            throw IsDenied(result) ? Denied(text) : Fail.Unavailable($"Plesk respondió un error a {op}/{action}: {text}", "upstream_error", text);
        }

        return [.. results.Where(Ok)];
    }

    /// <summary>
    /// El paquete de una respuesta y el error de todo el paquete si lo hubo. Null si no es un paquete de Plesk. Un rechazo de
    /// la clave es configuración, también en un cambio: no llegó a correr. Sin DTD: un paquete de Plesk nunca la trae.
    /// </summary>
    private (XElement? Packet, string? Error) Parse(PanelReply reply)
    {
        if (reply.Status is 401 or 403)
        {
            throw Denied(Upstream.Describe(reply.Status, null, Secrets));
        }

        XElement packet;
        try
        {
            using var reader = XmlReader.Create(new StringReader(reply.Body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            packet = XElement.Load(reader);
        }
        catch (XmlException)
        {
            return (null, null);
        }

        if (packet.Name != "packet")
        {
            return (null, null);
        }

        if (packet.Element("system") is not { } system || Ok(system))
        {
            return (packet, null);
        }

        var error = Describe(reply.Status, system);
        return IsDenied(system) ? throw Denied(error) : (packet, error);
    }

    private HttpRequestMessage Request(XElement[] operations)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url("/enterprise/control/agent.php"))
        {
            Content = new StringContent(
                """<?xml version="1.0" encoding="UTF-8"?>""" + new XElement("packet", operations).ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8,
                "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("KEY", apiKey);
        return request;
    }

    /// <summary>El texto de un error de Plesk con su código, corto y sin secretos.</summary>
    private string Describe(int status, XElement error) => Upstream.Describe(
        status,
        string.Create(CultureInfo.InvariantCulture, $"{error.Element("errtext")?.Value} ({error.Element("errcode")?.Value})"),
        Secrets);

    private static ModuleException Denied(string upstream) => Fail.InvalidConfig(
        ["api_key"],
        "Plesk rechazó el pedido a la API. Revisar la clave secreta, que sea del administrador y que esté creada para la IP de salida del módulo.",
        upstream);

    private static ModuleException Unreadable(string function) =>
        Fail.Unavailable(string.Create(CultureInfo.InvariantCulture, $"Plesk respondió algo que no se puede leer a {function}."), "unreadable");

    private static string PlanGuid(IEnumerable<ServicePlan> plans, string plan) =>
        plans.FirstOrDefault(p => p.Name == plan)?.Guid ?? throw Fail.InvalidConfig(["plan"], $"El plan {plan} no existe en Plesk.");

    private static long? Bytes(XElement? node) =>
        long.TryParse(node?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    private static int? Code(XElement result) =>
        int.TryParse(result.Element("errcode")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;

    private static bool IsDenied(XElement result) => Code(result) is { } code && DeniedCodes.Contains(code);

    private static bool Ok(XElement result) => result.Element("status")?.Value == "ok";

    private static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static XElement? Info(XElement? result) => result?.Element("data")?.Element("gen_info");

    private static XElement Op(string op, string action, params object[] content) => new(op, new XElement(action, content));

    private static XElement Filter(string name, string value) => new("filter", new XElement(name, value));

    private static XElement Dataset(params string[] names) => new("dataset", names.Select(n => new XElement(n)));

    private static XElement Property(string name, string value) => new("property", new XElement("name", name), new XElement("value", value));

    private static XElement ListPlans() => Op("service-plan", "get", new XElement("filter"));

    private sealed record Customer(string? Marker);

    /// <param name="Domain">El nombre de la suscripción en ASCII y minúsculas.</param>
    /// <param name="Status">0 es activa. Cualquier otro valor es suspendida, por el administrador o por otra causa.</param>
    private sealed record Subscription(string Domain, int Status, IReadOnlyList<string> PlanGuids);

    private sealed record ServicePlan(string Name, string Guid);

    private sealed record Snapshot(Customer? Customer, IReadOnlyList<Subscription> Subscriptions, IReadOnlyList<ServicePlan> Plans);
}
