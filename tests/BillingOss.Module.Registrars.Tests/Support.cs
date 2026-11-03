using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace BillingOss.Module.Registrars.Tests;

/// <summary>Un dominio en la cuenta del revendedor, en el registrar falso.</summary>
internal sealed class FakeDomain
{
    public required string Name { get; init; }

    public long Id { get; init; }

    public DateOnly Expires { get; set; }

    public List<string> Nameservers { get; set; } = ["ns1.viejo.example", "ns2.viejo.example"];

    public bool Locked { get; set; } = true;

    public string AuthCode { get; set; } = "EPP-del-dominio";

    /// <summary>active o pending_transfer.</summary>
    public string Status { get; set; } = "active";

    /// <summary>Correo del titular, para ver que set_contacts llegó.</summary>
    public string? RegistrantEmail { get; set; }

    /// <summary>La zona en el DNS del registrar, con los nombres relativos. Puede tener tipos que no se administran.</summary>
    public List<DnsRecord> Records { get; set; } = [];

    /// <summary>OpenProvider crea la zona aparte del dominio.</summary>
    public bool ZoneExists { get; set; }

    public List<EmailForward> Forwards { get; set; } = [];

    public bool Privacy { get; set; }

    /// <summary>Namecheap: si el dominio tiene la suscripción de protección y en qué modo está el correo.</summary>
    public bool PrivacySubscription { get; set; } = true;

    public string EmailType { get; set; } = "NONE";

    public bool RemoveRecord(string name, string type, string value) =>
        Records.RemoveAll(r => r.Name == name && r.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && r.Value == value) > 0;
}

/// <summary>
/// El registro que comparten los cuatro registrars falsos: qué dominios tiene la cuenta, cuáles están tomados por
/// otros y cuáles se pueden traer con su código. Cada falso traduce su API a este estado.
/// </summary>
internal sealed class FakeRegistry
{
    public const string ValidTransferCode = "EPP-VALIDO-123";

    private long nextId = 5000;

    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public Dictionary<string, FakeDomain> Mine { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Taken { get; } = new(StringComparer.Ordinal) { "tomado.com", "tomado.net" };

    public HashSet<string> Transferable { get; } = new(StringComparer.Ordinal) { "traer.com" };

    public int Registrations { get; set; }

    /// <summary>Cuántos cambios de zona o de reenvíos recibió: repetir un pedido igual no tiene que sumar.</summary>
    public int Writes { get; set; }

    /// <summary>Sin conexión: el pedido nunca sale.</summary>
    public bool Down { get; set; }

    /// <summary>El pedido sale y la respuesta no llega.</summary>
    public bool Hang { get; set; }

    public static IReadOnlyList<(string Tld, decimal Register, decimal Renew, decimal Transfer)> Prices { get; } =
        [("com", 9.50m, 10.25m, 9.75m), ("net", 11m, 12m, 11.50m), ("com.ar", 20m, 20m, 20m)];

    public bool Available(string name) => !Mine.ContainsKey(name) && !Taken.Contains(name);

    public FakeDomain Add(string name, DateOnly expires, string status = "active")
    {
        var domain = new FakeDomain { Name = name, Id = nextId++, Expires = expires, Status = status };
        Mine[name] = domain;
        return domain;
    }

    public FakeDomain Register(string name, int years, IEnumerable<string> nameservers)
    {
        Registrations++;
        var domain = Add(name, Today.AddYears(years));
        domain.Nameservers = [.. nameservers];
        domain.Locked = false;
        return domain;
    }

    /// <summary>El registro de origen soltó los dominios en transferencia: quedan activos por un año más.</summary>
    public void CompleteTransfers()
    {
        foreach (var d in Mine.Values.Where(d => d.Status == "pending_transfer"))
        {
            d.Status = "active";
            d.Expires = Today.AddYears(1);
        }
    }

    public FakeDomain? Find(long id) => Mine.Values.FirstOrDefault(d => d.Id == id);
}

/// <summary>Un registrar falso: verifica credenciales, simula cortes y anota cuántos pedidos recibió.</summary>
internal abstract class FakeRegistrar(FakeRegistry registry) : HttpMessageHandler
{
    public const string Secret = "secreto-de-prueba";

    public FakeRegistry Registry { get; } = registry;

    public int Requests { get; private set; }

    public abstract string Id { get; }

    /// <summary>Configuración de la instancia con las credenciales que este falso acepta.</summary>
    public abstract JsonObject Config(string secret = Secret);

    protected static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    protected static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    protected static async Task<Dictionary<string, string>> Form(HttpRequestMessage request)
    {
        var text = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
        var form = QueryHelpers.ParseQuery(text).Concat(QueryHelpers.ParseQuery(request.RequestUri!.Query));
        return form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
    }

    protected abstract Task<HttpResponseMessage> Handle(HttpRequestMessage request);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (Registry.Down)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Sin conexión con el registrar falso.");
        }

        if (Registry.Hang)
        {
            // Lo que ve el módulo cuando vence el plazo de su HttpClient con el pedido ya enviado.
            throw new TaskCanceledException("El registrar falso no respondió.", new TimeoutException());
        }

        lock (Registry)
        {
            return Handle(request).GetAwaiter().GetResult();
        }
    }
}

/// <summary>Internet.bs falso: comandos por formulario y respuesta JSON con status.</summary>
internal sealed class FakeInternetBs(FakeRegistry registry) : FakeRegistrar(registry)
{
    public override string Id => "internetbs";

    public override JsonObject Config(string secret = Secret) => new() { ["api_key"] = "clave-de-api", ["password"] = secret };

    private static string Slash(DateOnly d) => d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);

    protected override async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
    {
        var f = await Form(request);
        if (f.GetValueOrDefault("Password") != Secret || f.GetValueOrDefault("ApiKey") != "clave-de-api")
        {
            return Failure("Invalid API key or password", "100002");
        }

        var name = f.GetValueOrDefault("Domain") ?? string.Empty;
        var mine = Registry.Mine.GetValueOrDefault(name);
        switch (request.RequestUri!.AbsolutePath.TrimStart('/'))
        {
            case "Account/Balance/Get":
                return Json(new JsonObject { ["status"] = "SUCCESS", ["balance"] = new JsonArray(new JsonObject { ["amount"] = "100.00", ["currency"] = "USD" }) });
            case "Domain/Check":
                return Json(new JsonObject { ["status"] = Registry.Available(name) ? "AVAILABLE" : "UNAVAILABLE", ["domain"] = name });
            case "Domain/Create":
                if (!Registry.Available(name))
                {
                    return Failure("Domain not available for registration", "107002");
                }

                var created = Registry.Register(name, int.Parse(f["Period"].TrimEnd('Y'), CultureInfo.InvariantCulture), f["Ns_list"].Split(','));
                created.RegistrantEmail = f.GetValueOrDefault("Registrant_Email");
                return Json(new JsonObject { ["status"] = "SUCCESS", ["product"] = new JsonArray(new JsonObject { ["status"] = "SUCCESS", ["domain"] = name, ["expiration"] = Slash(created.Expires) }) });
            case "Domain/Renew":
                if (mine is null)
                {
                    return Failure("Domain not found", "100005");
                }

                mine.Expires = mine.Expires.AddYears(int.Parse(f["Period"].TrimEnd('Y'), CultureInfo.InvariantCulture));
                return Json(new JsonObject { ["status"] = "SUCCESS", ["product"] = new JsonArray(new JsonObject { ["status"] = "SUCCESS", ["newexpiration"] = Slash(mine.Expires) }) });
            case "Domain/Info":
                if (mine is null)
                {
                    return Failure("Domain not found", "100005");
                }

                return Json(new JsonObject
                {
                    ["status"] = "SUCCESS",
                    ["domain"] = name,
                    ["domainstatus"] = mine.Status == "pending_transfer" ? "PENDING TRANSFER" : "REGISTERED",
                    ["expirationdate"] = Slash(mine.Expires),
                    ["registrarlock"] = mine.Locked ? "ENABLED" : "DISABLED",
                    ["nameserver"] = new JsonArray([.. mine.Nameservers.Select(n => (JsonNode)n)]),
                    ["transferauthinfo"] = mine.AuthCode,
                    ["privatewhois"] = mine.Privacy ? "FULL" : "DISABLED",
                });
            case "Domain/DnsRecord/List":
                // Nombres completos, y el MX con la prioridad adelante del valor.
                return Json(new JsonObject
                {
                    ["status"] = "SUCCESS",
                    ["total_records"] = mine!.Records.Count.ToString(CultureInfo.InvariantCulture),
                    ["records"] = new JsonArray([.. mine.Records.Select(r => (JsonNode)new JsonObject
                    {
                        ["name"] = r.Name == "@" ? name : $"{r.Name}.{name}",
                        ["type"] = r.Type,
                        ["value"] = r.Type == "MX" ? $"{r.Priority} {r.Value}" : r.Value,
                        ["ttl"] = r.Ttl.ToString(CultureInfo.InvariantCulture),
                    })]),
                });
            case "Domain/DnsRecord/Add" or "Domain/DnsRecord/Remove":
                var full = f["FullRecordName"];
                var zone = Registry.Mine.Keys.Single(z => full == z || full.EndsWith("." + z, StringComparison.Ordinal));
                var relative = full == zone ? "@" : full[..^(zone.Length + 1)];
                Registry.Writes++;
                if (request.RequestUri.AbsolutePath.EndsWith("Remove", StringComparison.Ordinal))
                {
                    return Registry.Mine[zone].RemoveRecord(relative, f["Type"], f["Value"]) ? Json(new JsonObject { ["status"] = "SUCCESS" }) : Failure("Record not found", "100005");
                }

                Registry.Mine[zone].Records.Add(new DnsRecord(relative, f["Type"], f["Value"], int.Parse(f["Ttl"], CultureInfo.InvariantCulture), f.TryGetValue("Priority", out var p) ? int.Parse(p, CultureInfo.InvariantCulture) : null));
                return Json(new JsonObject { ["status"] = "SUCCESS" });
            case "Domain/EmailForward/List":
                // Aplanada, como la respuesta en texto: rules_0_source, rules_0_destination.
                var rules = new JsonObject { ["status"] = "SUCCESS", ["total_rules"] = mine!.Forwards.Count.ToString(CultureInfo.InvariantCulture) };
                for (var i = 0; i < mine.Forwards.Count; i++)
                {
                    rules[$"rules_{i}_source"] = $"{mine.Forwards[i].Mailbox}@{name}";
                    rules[$"rules_{i}_destination"] = mine.Forwards[i].To;
                }

                return Json(rules);
            case "Domain/EmailForward/Add" or "Domain/EmailForward/Remove":
                var source = f["Source"].Split('@');
                var owner = Registry.Mine[source[1]];
                Registry.Writes++;
                if (request.RequestUri.AbsolutePath.EndsWith("Remove", StringComparison.Ordinal))
                {
                    owner.Forwards.RemoveAll(x => x.Mailbox == source[0]);
                }
                else if (owner.Forwards.Any(x => x.Mailbox == source[0]))
                {
                    return Failure("Email forward already exists", "100006");
                }
                else
                {
                    owner.Forwards.Add(new EmailForward(source[0], f["Destination"]));
                }

                return Json(new JsonObject { ["status"] = "SUCCESS" });
            case "Domain/PrivateWhois/Enable" or "Domain/PrivateWhois/Disable":
                var enable = request.RequestUri.AbsolutePath.EndsWith("Enable", StringComparison.Ordinal);
                if (mine!.Privacy == enable)
                {
                    return Failure($"Private whois is already {(enable ? "enabled" : "disabled")}", "100007");
                }

                mine.Privacy = enable;
                return Json(new JsonObject { ["status"] = "SUCCESS" });
            case "Domain/Update":
                if (mine is null)
                {
                    return Failure("Domain not found", "100005");
                }

                if (f.TryGetValue("Ns_list", out var ns))
                {
                    mine.Nameservers = [.. ns.Split(',')];
                }

                if (f.TryGetValue("Registrant_Email", out var email))
                {
                    mine.RegistrantEmail = email;
                }

                return Json(new JsonObject { ["status"] = "SUCCESS" });
            case "Domain/RegistrarLock/Enable" or "Domain/RegistrarLock/Disable":
                mine!.Locked = request.RequestUri.AbsolutePath.EndsWith("Enable", StringComparison.Ordinal);
                return Json(new JsonObject { ["status"] = "SUCCESS" });
            case "Domain/Transfer/Initiate":
                if (!Registry.Transferable.Contains(name) || f.GetValueOrDefault("AuthInfo") != FakeRegistry.ValidTransferCode)
                {
                    return Failure("Invalid transfer auth code", "107210");
                }

                Registry.Add(name, Registry.Today, "pending_transfer").RegistrantEmail = f.GetValueOrDefault("Registrant_Email");
                return Json(new JsonObject { ["status"] = "PENDING", ["product"] = new JsonArray(new JsonObject { ["status"] = "PENDING", ["domain"] = name }) });
            case "Account/PriceList/Get":
                var rows = new JsonArray();
                foreach (var (tld, register, renew, transfer) in FakeRegistry.Prices)
                {
                    rows.Add(Row(tld, "registration", register));
                    rows.Add(Row(tld, "renewal", renew));
                    rows.Add(Row(tld, "transfer", transfer));
                }

                return Json(new JsonObject { ["status"] = "SUCCESS", ["product"] = rows });
            default:
                return Failure("Unknown command", "100000");
        }
    }

    private static JsonObject Row(string tld, string type, decimal price) =>
        new() { ["name"] = "." + tld, ["type"] = type, ["price"] = price.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = "USD" };

    private static HttpResponseMessage Failure(string message, string code) =>
        Json(new JsonObject { ["status"] = "FAILURE", ["message"] = message, ["code"] = code });
}

/// <summary>ResellerClub (LogicBoxes) falso: parámetros en la URL, errores con status ERROR y HTTP 500.</summary>
internal sealed class FakeResellerClub(FakeRegistry registry) : FakeRegistrar(registry)
{
    private int nextContact = 7000;

    public override string Id => "resellerclub";

    public Dictionary<string, string> Contacts { get; } = [];

    public override JsonObject Config(string secret = Secret) => new() { ["reseller_id"] = "12345", ["api_key"] = secret, ["customer_id"] = "777" };

    protected override async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
    {
        var q = new Query(QueryHelpers.ParseQuery(request.RequestUri!.Query));
        if (q["auth-userid"] != "12345" || q["api-key"] != Secret)
        {
            return Error("Access Denied: You are not authorized to perform this action");
        }

        await Task.CompletedTask;
        var name = q["domain-name"].ToString();
        var mine = Registry.Mine.GetValueOrDefault(name) ?? (long.TryParse(q["order-id"], CultureInfo.InvariantCulture, out var id) ? Registry.Find(id) : null);
        switch (request.RequestUri.AbsolutePath["/api/".Length..])
        {
            case "domains/available.json":
                var answer = new JsonObject();
                foreach (var tld in q["tlds"])
                {
                    var full = $"{name}.{tld}";
                    answer[full] = new JsonObject { ["classkey"] = "domcno", ["status"] = Registry.Mine.ContainsKey(full) ? "regthroughus" : Registry.Available(full) ? "available" : "regthroughothers" };
                }

                return Json(answer);
            case "contacts/add.json":
                var contact = (nextContact++).ToString(CultureInfo.InvariantCulture);
                Contacts[contact] = q["email"].ToString();
                return Json(JsonValue.Create(int.Parse(contact, CultureInfo.InvariantCulture)));
            case "domains/register.json":
                if (!Registry.Available(name))
                {
                    return Error($"Domain already registered: {name}");
                }

                var created = Registry.Register(name, int.Parse(q["years"]!, CultureInfo.InvariantCulture), q["ns"].Select(n => n!));
                created.RegistrantEmail = Contacts.GetValueOrDefault(q["reg-contact-id"].ToString());
                return Json(new JsonObject { ["actiontypedesc"] = $"Registration of {name}", ["entityid"] = created.Id.ToString(CultureInfo.InvariantCulture), ["actionstatus"] = "Success", ["status"] = "Success" });
            case "domains/details-by-name.json":
                if (mine is null)
                {
                    return Error($"Website doesn't exist for {name}");
                }

                var details = new JsonObject
                {
                    ["orderid"] = mine.Id.ToString(CultureInfo.InvariantCulture),
                    ["domainname"] = mine.Name,
                    ["currentstatus"] = mine.Status == "pending_transfer" ? "InActive" : "Active",
                    ["endtime"] = Epoch(mine.Expires),
                    ["domsecret"] = mine.AuthCode,
                    ["orderstatus"] = mine.Locked ? new JsonArray("transferlock") : new JsonArray(),
                    ["noOfNameServers"] = mine.Nameservers.Count.ToString(CultureInfo.InvariantCulture),
                    ["isprivacyprotected"] = mine.Privacy ? "true" : "false",
                };
                for (var i = 0; i < mine.Nameservers.Count; i++)
                {
                    details[$"ns{i + 1}"] = mine.Nameservers[i];
                }

                return Json(details);
            case "domains/renew.json":
                if (q["exp-date"] != Epoch(mine!.Expires))
                {
                    return Error("exp-date does not match the current expiry");
                }

                mine.Expires = mine.Expires.AddYears(int.Parse(q["years"]!, CultureInfo.InvariantCulture));
                return Action();
            case "domains/modify-ns.json":
                mine!.Nameservers = [.. q["ns"].Select(n => n!)];
                return Action();
            case "domains/enable-theft-protection.json" or "domains/disable-theft-protection.json":
                mine!.Locked = request.RequestUri.AbsolutePath.Contains("enable", StringComparison.Ordinal);
                return Action();
            case "domains/transfer.json":
                if (!Registry.Transferable.Contains(name) || q["auth-code"] != FakeRegistry.ValidTransferCode)
                {
                    return Error("Invalid auth-code for the domain");
                }

                Registry.Add(name, Registry.Today, "pending_transfer");
                return Json(new JsonObject { ["actionstatus"] = "PendingExecution", ["status"] = "Success" });
            case "domains/modify-contact.json":
                mine!.RegistrantEmail = Contacts.GetValueOrDefault(q["reg-contact-id"].ToString());
                return Action();
            case "domains/modify-privacy-protection.json":
                mine!.Privacy = q["protect-privacy"] == "true";
                return Action();
            case "dns/activate.xml":
                // LogicBoxes contesta en XML: el módulo solo mira el código HTTP.
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<hashtable><entry><string>status</string><string>Success</string></entry></hashtable>", Encoding.UTF8, "text/xml") };
            case "dns/manage/search-records.json":
                // Uno por tipo y por página. El host de la zona viene con el nombre del dominio.
                var page = mine!.Records.Where(r => r.Type == q["type"]).Skip((int.Parse(q["page-no"]!, CultureInfo.InvariantCulture) - 1) * 50).Take(50).ToList();
                var found = new JsonObject { ["recsonpage"] = page.Count.ToString(CultureInfo.InvariantCulture), ["recsindb"] = page.Count.ToString(CultureInfo.InvariantCulture) };
                for (var i = 0; i < page.Count; i++)
                {
                    var row = new JsonObject
                    {
                        ["timetolive"] = page[i].Ttl.ToString(CultureInfo.InvariantCulture),
                        ["status"] = "Active",
                        ["type"] = page[i].Type,
                        ["host"] = page[i].Name == "@" ? name : page[i].Name,
                        ["value"] = page[i].Value,
                    };
                    if (page[i].Priority is { } priority)
                    {
                        row["priority"] = priority.ToString(CultureInfo.InvariantCulture);
                    }

                    found[(i + 1).ToString(CultureInfo.InvariantCulture)] = row;
                }

                return Json(found);
            case var path when path.StartsWith("dns/manage/add-", StringComparison.Ordinal) || path.StartsWith("dns/manage/delete-", StringComparison.Ordinal):
                var type = path.Split('-')[1] switch { "ipv4" => "A", "ipv6" => "AAAA", "cname" => "CNAME", "mx" => "MX", _ => "TXT" };
                var host = q["host"].ToString() is { Length: > 0 } h ? h : "@";
                Registry.Writes++;
                if (path.StartsWith("dns/manage/delete-", StringComparison.Ordinal))
                {
                    return mine!.RemoveRecord(host, type, q["value"]!) ? Action() : Error("Record doesn't exist");
                }

                mine!.Records.Add(new DnsRecord(host, type, q["value"]!, int.Parse(q["ttl"]!, CultureInfo.InvariantCulture), q["priority"].Count > 0 ? int.Parse(q["priority"]!, CultureInfo.InvariantCulture) : null));
                return Action();
            default:
                return Error("Unknown API call");
        }
    }

    private static string Epoch(DateOnly date) => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static HttpResponseMessage Action() => Json(new JsonObject { ["actionstatus"] = "Success", ["status"] = "Success" });

    private static HttpResponseMessage Error(string message) =>
        Json(new JsonObject { ["status"] = "ERROR", ["message"] = message }, HttpStatusCode.InternalServerError);
}

/// <summary>OpenProvider falso: REST con JSON, login con token y handles de cliente.</summary>
internal sealed class FakeOpenProvider(FakeRegistry registry) : FakeRegistrar(registry)
{
    private readonly HashSet<string> tokens = [];
    private readonly Dictionary<string, string> handles = [];

    public override string Id => "openprovider";

    public int Logins { get; private set; }

    public override JsonObject Config(string secret = Secret) => new() { ["username"] = "usuario", ["password"] = secret };

    /// <summary>Los tokens emitidos dejan de valer, como si hubieran vencido.</summary>
    public void ExpireTokens() => tokens.Clear();

    protected override async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath["/v1beta/".Length..];
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync());
        if (path == "auth/login")
        {
            if (body?["username"]?.ToString() != "usuario" || body["password"]?.ToString() != Secret)
            {
                return Json(new JsonObject { ["code"] = 196, ["desc"] = "Authentication/Authorization Failed" }, HttpStatusCode.Unauthorized);
            }

            var token = $"tok-{++Logins}";
            tokens.Add(token);
            return Ok(new JsonObject { ["token"] = token, ["reseller_id"] = 1 });
        }

        if (request.Headers.Authorization?.Parameter is not { } presented || !tokens.Contains(presented))
        {
            return Json(new JsonObject { ["code"] = 196, ["desc"] = "Authentication/Authorization Failed" }, HttpStatusCode.Unauthorized);
        }

        var q = new Query(QueryHelpers.ParseQuery(request.RequestUri.Query));
        var segments = path.Split('/');
        var domain = segments is ["domains", var idText, ..] && long.TryParse(idText, CultureInfo.InvariantCulture, out var id) ? Registry.Find(id) : null;
        switch (request.Method.Method, segments)
        {
            case ("POST", ["domains", "check"]):
                var results = new JsonArray();
                foreach (var d in body!["domains"]!.AsArray())
                {
                    var full = $"{d!["name"]}.{d["extension"]}";
                    results.Add(new JsonObject { ["domain"] = full, ["status"] = Registry.Available(full) ? "free" : "active", ["is_premium"] = false });
                }

                return Ok(new JsonObject { ["results"] = results });
            case ("POST", ["customers"]):
                var handle = $"XX{handles.Count + 1:000000}-AR";
                handles[handle] = body!["email"]!.ToString();
                return Ok(new JsonObject { ["handle"] = handle });
            case ("POST", ["domains"]):
                var name = $"{body!["domain"]!["name"]}.{body["domain"]!["extension"]}";
                if (!Registry.Available(name))
                {
                    return Json(new JsonObject { ["code"] = 346, ["desc"] = "Domain already exists" }, HttpStatusCode.BadRequest);
                }

                var created = Registry.Register(name, body["period"]!.GetValue<int>(), body["name_servers"]!.AsArray().Select(n => n!["name"]!.ToString()));
                created.RegistrantEmail = handles.GetValueOrDefault(body["owner_handle"]!.ToString());
                return Ok(Domain(created));
            case ("GET", ["domains"]):
                var found = Registry.Mine.GetValueOrDefault(q["full_name"].ToString());
                return Ok(new JsonObject { ["results"] = found is null ? new JsonArray() : new JsonArray(Domain(found)), ["total"] = found is null ? 0 : 1 });
            case ("GET", ["domains", _]) when domain is not null:
                return Ok(Domain(domain));
            case ("POST", ["domains", _, "renew"]) when domain is not null:
                domain.Expires = domain.Expires.AddYears(body!["period"]!.GetValue<int>());
                return Ok(new JsonObject { ["status"] = "ACT" });
            case ("PUT", ["domains", _]) when domain is not null:
                if (body!["name_servers"] is JsonArray ns)
                {
                    domain.Nameservers = [.. ns.Select(n => n!["name"]!.ToString())];
                }

                if (body["is_locked"] is JsonValue locked)
                {
                    domain.Locked = locked.GetValue<bool>();
                }

                if (body["owner_handle"] is { } owner)
                {
                    domain.RegistrantEmail = handles.GetValueOrDefault(owner.ToString());
                }

                if (body["is_private_whois_enabled"] is JsonValue privacy)
                {
                    domain.Privacy = privacy.GetValue<bool>();
                }

                return Ok(new JsonObject { ["success"] = true });
            case ("GET", ["dns", "zones", var zoneName]) when Registry.Mine.TryGetValue(zoneName, out var withZone) && withZone.ZoneExists:
                // Nombres completos, con los NS y el SOA que pone OpenProvider y no se administran.
                var listed = new JsonArray(
                    new JsonObject { ["name"] = zoneName, ["type"] = "NS", ["value"] = "ns1.openprovider.nl", ["ttl"] = 86400 },
                    new JsonObject { ["name"] = zoneName, ["type"] = "SOA", ["value"] = "ns1.openprovider.nl dns@openprovider.eu 1 10800 3600 604800 3600", ["ttl"] = 86400 });
                foreach (var r in withZone.Records)
                {
                    var row = new JsonObject { ["name"] = r.Name == "@" ? zoneName : $"{r.Name}.{zoneName}", ["type"] = r.Type, ["value"] = r.Value, ["ttl"] = r.Ttl };
                    if (r.Priority is { } prio)
                    {
                        row["prio"] = prio;
                    }

                    listed.Add(row);
                }

                return Ok(new JsonObject { ["id"] = withZone.Id, ["name"] = zoneName, ["records"] = listed });
            case ("POST", ["dns", "zones"]):
                var target = Registry.Mine[$"{body!["domain"]!["name"]}.{body["domain"]!["extension"]}"];
                if (target.ZoneExists)
                {
                    return Json(new JsonObject { ["code"] = 817, ["desc"] = "Zone already exists" }, HttpStatusCode.BadRequest);
                }

                (target.ZoneExists, target.Records) = (true, [.. body["records"]!.AsArray().Select(r => Read(r!))]);
                Registry.Writes++;
                return Ok(new JsonObject { ["success"] = true });
            case ("PUT", ["dns", "zones", var changed]) when Registry.Mine.TryGetValue(changed, out var edited) && edited.ZoneExists:
                foreach (var r in body!["records"]!["remove"]!.AsArray())
                {
                    edited.RemoveRecord(Read(r!).Name, r!["type"]!.ToString(), r["value"]!.ToString());
                }

                edited.Records.AddRange(body["records"]!["add"]!.AsArray().Select(r => Read(r!)));
                Registry.Writes++;
                return Ok(new JsonObject { ["success"] = true });
            case ("GET", ["domains", _, "authcode"]) when domain is not null:
                return Ok(new JsonObject { ["auth_code"] = domain.AuthCode, ["type"] = "internal" });
            case ("POST", ["domains", "transfer"]):
                var incoming = $"{body!["domain"]!["name"]}.{body["domain"]!["extension"]}";
                if (!Registry.Transferable.Contains(incoming) || body["auth_code"]?.ToString() != FakeRegistry.ValidTransferCode)
                {
                    return Json(new JsonObject { ["code"] = 399, ["desc"] = "Wrong auth code for transfer" }, HttpStatusCode.BadRequest);
                }

                var pending = Registry.Add(incoming, Registry.Today, "pending_transfer");
                return Ok(new JsonObject { ["id"] = pending.Id, ["status"] = "REQ" });
            case ("GET", ["tlds"]):
                var tlds = new JsonArray();
                foreach (var (tld, register, renew, transfer) in FakeRegistry.Prices)
                {
                    tlds.Add(new JsonObject
                    {
                        ["name"] = tld,
                        ["prices"] = new JsonObject
                        {
                            ["create_price"] = Price(register),
                            ["renew_price"] = Price(renew),
                            ["transfer_price"] = Price(transfer),
                        },
                    });
                }

                return Ok(new JsonObject { ["results"] = tlds });
            default:
                return Json(new JsonObject { ["code"] = 320, ["desc"] = "Domain not found" }, HttpStatusCode.NotFound);
        }
    }

    private static JsonObject Price(decimal value) => new() { ["reseller"] = new JsonObject { ["price"] = value, ["currency"] = "USD" } };

    /// <summary>Un registro como lo manda el módulo: nombre relativo y vacío para la zona.</summary>
    private static DnsRecord Read(JsonNode r) => new(
        r["name"]!.ToString() is { Length: > 0 } n ? n : "@",
        r["type"]!.ToString(),
        r["value"]!.ToString(),
        r["ttl"]!.GetValue<int>(),
        r["prio"]?.GetValue<int>());

    private static JsonObject Domain(FakeDomain d) => new()
    {
        ["id"] = d.Id,
        ["domain"] = new JsonObject { ["name"] = d.Name.Split('.')[0], ["extension"] = d.Name[(d.Name.IndexOf('.', StringComparison.Ordinal) + 1)..] },
        ["status"] = d.Status == "pending_transfer" ? "REQ" : "ACT",
        ["expiration_date"] = $"{Iso(d.Expires)} 10:00:00",
        ["is_locked"] = d.Locked,
        ["is_private_whois_enabled"] = d.Privacy,
        ["name_servers"] = new JsonArray([.. d.Nameservers.Select(n => (JsonNode)new JsonObject { ["name"] = n })]),
    };

    private static HttpResponseMessage Ok(JsonObject data) => Json(new JsonObject { ["code"] = 0, ["desc"] = string.Empty, ["data"] = data });
}

/// <summary>Namecheap falso: XML con el espacio de nombres de su API.</summary>
internal sealed class FakeNamecheap(FakeRegistry registry) : FakeRegistrar(registry)
{
    private static readonly string Ns = "http://api.namecheap.com/xml.response";

    public override string Id => "namecheap";

    public override JsonObject Config(string secret = Secret) =>
        new() { ["api_user"] = "usuario", ["api_key"] = secret, ["username"] = "usuario", ["client_ip"] = "203.0.113.10" };

    private static string Us(DateOnly d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

    protected override async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
    {
        var f = await Form(request);
        if (f.GetValueOrDefault("ApiKey") != Secret || f.GetValueOrDefault("ApiUser") != "usuario" || f.GetValueOrDefault("ClientIp") != "203.0.113.10")
        {
            return Error("1011102", "Parameter APIKey is invalid");
        }

        var name = f.GetValueOrDefault("DomainName") ?? string.Empty;
        var mine = Registry.Mine.GetValueOrDefault(name);
        switch (f["Command"])
        {
            case "namecheap.users.getBalances":
                return Ok("namecheap.users.getBalances", "<UserGetBalancesResult Currency=\"USD\" AvailableBalance=\"100.00\" />");
            case "namecheap.domains.check":
                var results = string.Concat(f["DomainList"].Split(',').Select(d => $"<DomainCheckResult Domain=\"{d}\" Available=\"{(Registry.Available(d) ? "true" : "false")}\" IsPremiumName=\"false\" PremiumRegistrationPrice=\"0\" />"));
                return Ok("namecheap.domains.check", results);
            case "namecheap.domains.create":
                if (!Registry.Available(name))
                {
                    return Error("2033409", $"Domain {name} is not available for registration");
                }

                var created = Registry.Register(name, int.Parse(f["Years"], CultureInfo.InvariantCulture), f["Nameservers"].Split(','));
                created.RegistrantEmail = f.GetValueOrDefault("RegistrantEmailAddress");
                return Ok("namecheap.domains.create", $"<DomainCreateResult Domain=\"{name}\" Registered=\"true\" ChargedAmount=\"9.50\" DomainID=\"{created.Id}\" OrderID=\"1\" TransactionID=\"2\" />");
            case "namecheap.domains.getInfo":
                if (mine is null || mine.Status == "pending_transfer")
                {
                    return Error("2019166", "Domain not found");
                }

                var servers = string.Concat(mine.Nameservers.Select(n => $"<Nameserver>{n}</Nameserver>"));
                var guard = $"<Whoisguard Enabled=\"{(mine.Privacy ? "True" : "False")}\"><ID>{(mine.PrivacySubscription ? mine.Id : 0)}</ID></Whoisguard>";
                return Ok("namecheap.domains.getInfo", $"<DomainGetInfoResult Status=\"Ok\" ID=\"{mine.Id}\" DomainName=\"{name}\" IsOwner=\"true\"><DomainDetails><CreatedDate>01/01/2020</CreatedDate><ExpiredDate>{Us(mine.Expires)}</ExpiredDate></DomainDetails>{guard}<DnsDetails ProviderType=\"CUSTOM\" IsUsingOurDNS=\"false\">{servers}</DnsDetails></DomainGetInfoResult>");
            case "namecheap.domains.dns.getHosts":
                var zone = Registry.Mine[$"{f["SLD"]}.{f["TLD"]}"];
                var hosts = string.Concat(zone.Records.Select((r, i) =>
                    $"<host HostId=\"{i + 1}\" Name=\"{r.Name}\" Type=\"{r.Type}\" Address=\"{System.Security.SecurityElement.Escape(r.Value)}\" MXPref=\"{r.Priority ?? 10}\" TTL=\"{r.Ttl}\" />"));
                return Ok("namecheap.domains.dns.getHosts", $"<DomainDNSGetHostsResult Domain=\"{zone.Name}\" EmailType=\"{zone.EmailType}\" IsUsingOurDNS=\"true\">{hosts}</DomainDNSGetHostsResult>");
            case "namecheap.domains.dns.setHosts":
                var target = Registry.Mine[$"{f["SLD"]}.{f["TLD"]}"];
                target.Records = [.. Enumerable.Range(1, 200).TakeWhile(i => f.ContainsKey($"HostName{i}")).Select(i => new DnsRecord(
                    f[$"HostName{i}"], f[$"RecordType{i}"], f[$"Address{i}"], int.Parse(f[$"TTL{i}"], CultureInfo.InvariantCulture),
                    f[$"RecordType{i}"] == "MX" ? int.Parse(f[$"MXPref{i}"], CultureInfo.InvariantCulture) : null))];
                target.EmailType = f["EmailType"];
                Registry.Writes++;
                return Ok("namecheap.domains.dns.setHosts", $"<DomainDNSSetHostsResult Domain=\"{target.Name}\" IsSuccess=\"true\" />");
            case "namecheap.domains.dns.getEmailForwarding":
                var forwards = string.Concat(mine!.Forwards.Select(x => $"<Forward mailbox=\"{x.Mailbox}\">{x.To}</Forward>"));
                return Ok("namecheap.domains.dns.getEmailForwarding", $"<DomainDNSGetEmailForwardingResult Domain=\"{name}\">{forwards}</DomainDNSGetEmailForwardingResult>");
            case "namecheap.domains.dns.setEmailForwarding":
                mine!.Forwards = [.. Enumerable.Range(1, 100).TakeWhile(i => f.ContainsKey($"MailBox{i}")).Select(i => new EmailForward(f[$"MailBox{i}"], f[$"ForwardTo{i}"]))];
                Registry.Writes++;
                return Ok("namecheap.domains.dns.setEmailForwarding", $"<DomainDNSSetEmailForwardingResult Domain=\"{name}\" IsSuccess=\"true\" />");
            case "namecheap.whoisguard.enable" or "namecheap.whoisguard.disable":
                var guarded = Registry.Find(long.Parse(f["WhoisguardID"], CultureInfo.InvariantCulture))!;
                var on = f["Command"].EndsWith("enable", StringComparison.Ordinal);
                if (on && !f.ContainsKey("ForwardedToEmail"))
                {
                    return Error("2011331", "Parameter ForwardedToEmail is missing");
                }

                guarded.Privacy = on;
                return Ok(f["Command"], $"<Whoisguard{(on ? "Enable" : "Disable")}Result DomainName=\"{guarded.Name}\" IsSuccess=\"true\" />");
            case "namecheap.domains.getRegistrarLock":
                return Ok("namecheap.domains.getRegistrarLock", $"<DomainGetRegistrarLockResult Domain=\"{name}\" RegistrarLockStatus=\"{(mine!.Locked ? "true" : "false")}\" />");
            case "namecheap.domains.renew":
                mine!.Expires = mine.Expires.AddYears(int.Parse(f["Years"], CultureInfo.InvariantCulture));
                return Ok("namecheap.domains.renew", $"<DomainRenewResult DomainName=\"{name}\" DomainID=\"{mine.Id}\" Renew=\"true\"><DomainDetails><ExpiredDate>{Us(mine.Expires)}</ExpiredDate></DomainDetails></DomainRenewResult>");
            case "namecheap.domains.dns.setCustom":
                var full = $"{f["SLD"]}.{f["TLD"]}";
                Registry.Mine[full].Nameservers = [.. f["Nameservers"].Split(',')];
                return Ok("namecheap.domains.dns.setCustom", $"<DomainDNSSetCustomResult Domain=\"{full}\" Updated=\"true\" />");
            case "namecheap.domains.setRegistrarLock":
                mine!.Locked = f["LockAction"] == "LOCK";
                return Ok("namecheap.domains.setRegistrarLock", $"<DomainSetRegistrarLockResult Domain=\"{name}\" IsSuccess=\"true\" />");
            case "namecheap.domains.transfer.create":
                if (!Registry.Transferable.Contains(name) || f.GetValueOrDefault("EPPCode") != FakeRegistry.ValidTransferCode)
                {
                    return Error("2030166", "EPP code is invalid");
                }

                var pending = Registry.Add(name, Registry.Today, "pending_transfer");
                return Ok("namecheap.domains.transfer.create", $"<DomainTransferCreateResult DomainName=\"{name}\" Transfer=\"true\" TransferID=\"{pending.Id}\" StatusID=\"-1\" />");
            case "namecheap.domains.setContacts":
                mine!.RegistrantEmail = f.GetValueOrDefault("RegistrantEmailAddress");
                return Ok("namecheap.domains.setContacts", $"<DomainSetContactResult Domain=\"{name}\" IsSuccess=\"true\" />");
            case "namecheap.users.getPricing":
                var categories = new StringBuilder("<UserGetPricingResult><ProductType Name=\"DOMAIN\">");
                foreach (var (category, pick) in new (string, Func<(string Tld, decimal Register, decimal Renew, decimal Transfer), decimal>)[] { ("REGISTER", p => p.Register), ("RENEW", p => p.Renew), ("TRANSFER", p => p.Transfer) })
                {
                    categories.Append(CultureInfo.InvariantCulture, $"<ProductCategory Name=\"{category}\">");
                    foreach (var p in FakeRegistry.Prices)
                    {
                        categories.Append(CultureInfo.InvariantCulture, $"<Product Name=\"{p.Tld}\"><Price Duration=\"1\" DurationType=\"YEAR\" YourPrice=\"{pick(p).ToString("0.00", CultureInfo.InvariantCulture)}\" Currency=\"USD\" /></Product>");
                    }

                    categories.Append("</ProductCategory>");
                }

                categories.Append("</ProductType></UserGetPricingResult>");
                return Ok("namecheap.users.getPricing", categories.ToString());
            default:
                return Error("1010900", "Unknown command");
        }
    }

    private static HttpResponseMessage Ok(string command, string inner) => Xml($"<ApiResponse Status=\"OK\" xmlns=\"{Ns}\"><Errors /><RequestedCommand>{command}</RequestedCommand><CommandResponse Type=\"{command}\">{inner}</CommandResponse></ApiResponse>");

    private static HttpResponseMessage Error(string number, string message) =>
        Xml($"<ApiResponse Status=\"ERROR\" xmlns=\"{Ns}\"><Errors><Error Number=\"{number}\">{message}</Error></Errors></ApiResponse>");

    private static HttpResponseMessage Xml(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };
}

/// <summary>
/// El módulo en un TestServer con un registrar falso del otro lado. Firma como el núcleo y, como él, guarda el
/// instance_state de cada respuesta y lo manda en la llamada siguiente.
/// </summary>
internal sealed class RegistrarHost : IAsyncDisposable
{
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-firma");

    private readonly WebApplication app;
    private readonly string db;

    private RegistrarHost(WebApplication app, string db, FakeRegistrar fake)
    {
        this.app = app;
        this.db = db;
        Fake = fake;
        Client = app.GetTestClient();
    }

    public FakeRegistrar Fake { get; }

    public FakeRegistry Registry => Fake.Registry;

    public HttpClient Client { get; }

    public JsonNode? State { get; set; }

    public static FakeRegistrar NewFake(string registrar) => registrar switch
    {
        "internetbs" => new FakeInternetBs(new FakeRegistry()),
        "resellerclub" => new FakeResellerClub(new FakeRegistry()),
        "openprovider" => new FakeOpenProvider(new FakeRegistry()),
        "namecheap" => new FakeNamecheap(new FakeRegistry()),
        _ => throw new ArgumentException(registrar),
    };

    public static async Task<RegistrarHost> Start(string registrar, FakeRegistrar? fake = null, HttpClient? http = null)
    {
        fake ??= NewFake(registrar);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var db = Path.Combine(Path.GetTempPath(), $"reg-idem-{Guid.NewGuid():N}.db");
        app.MapModule(
            RegistrarModule.Create(registrar, http ?? new HttpClient(fake), NullLogger.Instance),
            new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = db });
        await app.StartAsync();
        return new RegistrarHost(app, db, fake);
    }

    /// <summary>Contexto del tipo registrar: el dominio y el titular, como lo arma el núcleo.</summary>
    public static JsonObject Context(string domain = "ejemplo.com", JsonObject? externalIds = null, JsonObject? registrant = null) => new()
    {
        ["domain"] = new JsonObject { ["id"] = 1, ["name"] = domain, ["external_ids"] = externalIds ?? [] },
        ["account"] = new JsonObject { ["id"] = 1, ["language"] = "es" },
        ["contacts"] = new JsonObject { ["registrant"] = registrant ?? Registrant() },
    };

    public static JsonObject Registrant(string email = "titular@example.com") => new()
    {
        ["first_name"] = "Ana",
        ["last_name"] = "Gómez",
        ["company"] = "Gómez SRL",
        ["email"] = email,
        ["phone"] = "+54.1155551234",
        ["address1"] = "Av. Siempre Viva 742",
        ["city"] = "Buenos Aires",
        ["state"] = "CABA",
        ["postal_code"] = "C1000",
        ["country"] = "AR",
    };

    public static string Code(JsonNode? body) => body?["error"]?["code"]?.ToString() ?? string.Empty;

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(string path, JsonObject? input = null, string? key = null, JsonObject? config = null, JsonObject? context = null)
    {
        var envelope = new JsonObject
        {
            ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = (config ?? Fake.Config()).DeepClone(), ["state"] = State?.DeepClone() },
            ["context"] = (context ?? Context()).DeepClone(),
            ["input"] = (input ?? []).DeepClone(),
        };
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(path == "/v0/manifest" ? HttpMethod.Get : HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (path != "/v0/manifest")
        {
            request.Content = new ByteArrayContent(bytes);
        }
        else
        {
            bytes = [];
        }

        request.Headers.Add(ModuleHeaders.RequestId, requestId);
        request.Headers.Add(ModuleHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        if (key is not null)
        {
            request.Headers.Add(ModuleHeaders.IdempotencyKey, key);
        }

        request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(Secret, timestamp, request.Method.Method, path, requestId, key, bytes));
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        if (body?["instance_state"] is { } state)
        {
            State = state.DeepClone();
        }

        return (response.StatusCode, body);
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        Client.Dispose();
        try
        {
            File.Delete(db);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Parámetros de la URL: uno que falta da vacío, como en un servidor real.</summary>
internal sealed class Query(Dictionary<string, StringValues> values)
{
    public StringValues this[string key] => values.GetValueOrDefault(key);
}
