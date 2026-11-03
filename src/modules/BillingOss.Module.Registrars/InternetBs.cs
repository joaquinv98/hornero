using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Registrars;

/// <summary>
/// Internet.bs, registrar de TLD genéricos. API HTTP con formulario y respuesta JSON
/// (ResponseFormat=JSON), autenticada con ApiKey y Password. El ambiente de prueba es testapi.internet.bs.
/// </summary>
internal sealed class InternetBs(RegistrarHttp http) : Registrar
{
    private static readonly string[] Roles = ["Registrant", "Admin", "Technical", "Billing"];

    public override string Id => "internetbs";

    public override string Name => "Internet.bs";

    public override IReadOnlyList<string> Extras =>
        ["registrar.transfer", "registrar.auth_code", "registrar.lock", "registrar.contacts", "registrar.pricing", "registrar.dns", "registrar.email_forwarding", "registrar.privacy"];

    public override JsonObject ConfigSchema => (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "required": ["api_key", "password"],
          "properties": {
            "api_key": { "type": "string", "x-secret": true, "description": "ApiKey de la cuenta de revendedor" },
            "password": { "type": "string", "x-secret": true, "description": "Password de la API" },
            "sandbox": { "type": "boolean", "description": "Usar el ambiente de prueba testapi.internet.bs" }
          }
        }
        """)!;

    public override async Task Test(Session s) => await Call(s, "Account/Balance/Get", [], mutating: false).ConfigureAwait(false);

    public override async Task<IReadOnlyList<Availability>> Check(Session s, IReadOnlyList<DomainName> names)
    {
        var results = new List<Availability>();
        foreach (var name in names)
        {
            var json = await Call(s, "Domain/Check", new() { ["Domain"] = name.Name }, mutating: false).ConfigureAwait(false);
            results.Add(new Availability(name, json["status"]?.ToString() switch
            {
                "AVAILABLE" => true,
                "UNAVAILABLE" => false,
                _ => null,
            }));
        }

        return results;
    }

    public override async Task<DomainState> Register(Session s, RegisterOrder order)
    {
        var args = Contacts(order.Registrant);
        args["Domain"] = order.Name.Name;
        args["Period"] = Period(order.Years);
        args["Ns_list"] = string.Join(',', order.Nameservers);
        args["privateWhois"] = order.Privacy ? "FULL" : "DISABLE";
        var json = await Call(s, "Domain/Create", args, mutating: true).ConfigureAwait(false);
        if (Product(json, "status") == "PENDING" || json["status"]?.ToString() == "PENDING")
        {
            return new DomainState("pending", null, order.Nameservers, null, []);
        }

        return new DomainState("active", Date(Product(json, "expiration"), "yyyy/MM/dd", "yyyy-MM-dd"), order.Nameservers, null, []);
    }

    public override async Task<DateOnly> Renew(Session s, DomainRef domain, int years, DateOnly currentExpiry)
    {
        var json = await Call(s, "Domain/Renew", new() { ["Domain"] = domain.Name.Name, ["Period"] = Period(years) }, mutating: true).ConfigureAwait(false);
        return Date(Product(json, "newexpiration") ?? Product(json, "expiration"), "yyyy/MM/dd", "yyyy-MM-dd") ?? currentExpiry.AddYears(years);
    }

    public override async Task<DomainState> Info(Session s, DomainRef domain)
    {
        JsonObject json;
        try
        {
            json = await Call(s, "Domain/Info", new() { ["Domain"] = domain.Name.Name }, mutating: false).ConfigureAwait(false);
        }
        catch (BillingOss.Modules.Sdk.ModuleException e) when (e.Code == BillingOss.Modules.Protocol.ErrorCodes.NotFound)
        {
            return DomainState.NotFound;
        }

        var status = json["domainstatus"]?.ToString()?.ToUpperInvariant() ?? string.Empty;
        return new DomainState(
            status switch
            {
                _ when status.Contains("TRANSFER", StringComparison.Ordinal) => "pending_transfer",
                _ when status.Contains("REDEMPTION", StringComparison.Ordinal) => "redemption",
                _ when status.Contains("EXPIRED", StringComparison.Ordinal) => "expired",
                "REGISTERED" or "ACTIVE" or "OK" => "active",
                _ when status.Contains("PENDING", StringComparison.Ordinal) => "pending",
                _ => "unknown",
            },
            Date(json["expirationdate"]?.ToString(), "yyyy/MM/dd", "yyyy-MM-dd"),
            Nameservers(json),
            json["registrarlock"]?.ToString() is { } lockText ? lockText.Equals("ENABLED", StringComparison.OrdinalIgnoreCase) : null,
            [],
            json["privatewhois"]?.ToString() is { } privacy ? !privacy.Equals("DISABLED", StringComparison.OrdinalIgnoreCase) : null);
    }

    /// <summary>La zona en los servidores DNS de Internet.bs, que la administran si el dominio los usa.</summary>
    public override async Task<IReadOnlyList<DnsRecord>> Dns(Session s, DomainRef domain)
    {
        var json = await Call(s, "Domain/DnsRecord/List", new() { ["Domain"] = domain.Name.Name }, mutating: false).ConfigureAwait(false);
        return [.. Rows(json, "records").Select(r => Record(domain.Name.Name, r)).OfType<DnsRecord>()];
    }

    public override async Task SetDns(Session s, DomainRef domain, IReadOnlyList<DnsRecord> records)
    {
        var zone = domain.Name.Name;
        var (remove, add) = Diff(await Dns(s, domain).ConfigureAwait(false), records);
        foreach (var r in remove)
        {
            await Call(s, "Domain/DnsRecord/Remove", new() { ["FullRecordName"] = DnsRecords.FullName(zone, r.Name), ["Type"] = r.Type, ["Value"] = r.Value }, mutating: true).ConfigureAwait(false);
        }

        foreach (var r in add)
        {
            var args = new Dictionary<string, string>
            {
                ["FullRecordName"] = DnsRecords.FullName(zone, r.Name),
                ["Type"] = r.Type,
                ["Value"] = r.Value,
                ["Ttl"] = r.Ttl.ToString(CultureInfo.InvariantCulture),
            };
            if (r.Priority is { } priority)
            {
                args["Priority"] = priority.ToString(CultureInfo.InvariantCulture);
            }

            await Call(s, "Domain/DnsRecord/Add", args, mutating: true).ConfigureAwait(false);
        }
    }

    public override async Task<IReadOnlyList<EmailForward>> Forwards(Session s, DomainRef domain)
    {
        var json = await Call(s, "Domain/EmailForward/List", new() { ["Domain"] = domain.Name.Name }, mutating: false).ConfigureAwait(false);
        return [.. Rows(json, "rules").Select(r => ReadForward(domain.Name.Name, r["source"]?.ToString(), r["destination"]?.ToString())).OfType<EmailForward>()];
    }

    public override async Task SetForwards(Session s, DomainRef domain, IReadOnlyList<EmailForward> forwards)
    {
        var zone = domain.Name.Name;
        var (remove, add) = Diff(await Forwards(s, domain).ConfigureAwait(false), forwards);
        foreach (var f in remove)
        {
            await Call(s, "Domain/EmailForward/Remove", new() { ["Source"] = $"{f.Mailbox}@{zone}" }, mutating: true).ConfigureAwait(false);
        }

        foreach (var f in add)
        {
            await Call(s, "Domain/EmailForward/Add", new() { ["Source"] = $"{f.Mailbox}@{zone}", ["Destination"] = f.To }, mutating: true).ConfigureAwait(false);
        }
    }

    public override async Task SetPrivacy(Session s, DomainRef domain, bool enabled, string? registrantEmail)
    {
        try
        {
            await Call(
                s,
                enabled ? "Domain/PrivateWhois/Enable" : "Domain/PrivateWhois/Disable",
                enabled ? new() { ["Domain"] = domain.Name.Name, ["type"] = "FULL" } : new() { ["Domain"] = domain.Name.Name },
                mutating: true).ConfigureAwait(false);
        }
        catch (BillingOss.Modules.Sdk.ModuleException e) when (e.Code == BillingOss.Modules.Protocol.ErrorCodes.Rejected && e.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            // Ya estaba como se pide: el pedido se cumple igual.
        }
    }

    public override async Task SetNameservers(Session s, DomainRef domain, IReadOnlyList<string> nameservers) =>
        await Call(s, "Domain/Update", new() { ["Domain"] = domain.Name.Name, ["Ns_list"] = string.Join(',', nameservers) }, mutating: true).ConfigureAwait(false);

    public override async Task<DomainState> Transfer(Session s, TransferOrder order)
    {
        var args = Contacts(order.Registrant);
        args["Domain"] = order.Name.Name;
        args["AuthInfo"] = order.AuthCode;
        if (order.Nameservers.Count > 0)
        {
            args["Ns_list"] = string.Join(',', order.Nameservers);
        }

        await Call(s, "Domain/Transfer/Initiate", args, mutating: true).ConfigureAwait(false);
        return new DomainState("pending_transfer", null, order.Nameservers, null, []);
    }

    public override async Task SetLock(Session s, DomainRef domain, bool locked) =>
        await Call(s, locked ? "Domain/RegistrarLock/Enable" : "Domain/RegistrarLock/Disable", new() { ["Domain"] = domain.Name.Name }, mutating: true).ConfigureAwait(false);

    public override async Task<string> AuthCode(Session s, DomainRef domain)
    {
        var json = await Call(s, "Domain/Info", new() { ["Domain"] = domain.Name.Name }, mutating: false).ConfigureAwait(false);
        return json["transferauthinfo"]?.ToString() is { Length: > 0 } code
            ? code
            : throw Fail.Rejected($"Internet.bs no dio el código de {domain.Name}: el dominio puede estar bloqueado.", "locked");
    }

    public override async Task SetContacts(Session s, DomainRef domain, Contact registrant)
    {
        var args = Contacts(registrant);
        args["Domain"] = domain.Name.Name;
        await Call(s, "Domain/Update", args, mutating: true).ConfigureAwait(false);
    }

    /// <summary>Lista de precios de la cuenta: una fila por TLD y operación, por un año.</summary>
    public override async Task<IReadOnlyList<CostPrice>> Prices(Session s, string? currency)
    {
        var args = new Dictionary<string, string> { ["version"] = "2" };
        if (currency is { Length: 3 })
        {
            args["Currency"] = currency;
        }

        var json = await Call(s, "Account/PriceList/Get", args, mutating: false).ConfigureAwait(false);
        var rows = new Dictionary<(string Tld, string Currency), CostPrice>();
        foreach (var p in (json["product"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var tld = (p["name"] ?? p["tld"])?.ToString()?.Trim().TrimStart('.').ToLowerInvariant();
            var cur = p["currency"]?.ToString() ?? currency ?? "USD";
            if (string.IsNullOrEmpty(tld) || Money(p["price"]) is not { } price)
            {
                continue;
            }

            var row = rows.GetValueOrDefault((tld, cur)) ?? new CostPrice(tld, cur, 1, null, null, null);
            rows[(tld, cur)] = p["type"]?.ToString()?.ToLowerInvariant() switch
            {
                "registration" or "register" => row with { Register = price },
                "renewal" or "renew" => row with { Renew = price },
                "transfer" => row with { Transfer = price },
                _ => row,
            };
        }

        return [.. rows.Values];
    }

    /// <summary>Un comando de la API. status FAILURE se traduce al error del protocolo que corresponde.</summary>
    private async Task<JsonObject> Call(Session s, string command, Dictionary<string, string> args, bool mutating)
    {
        var form = new Dictionary<string, string>(args)
        {
            ["ApiKey"] = s.Text("api_key"),
            ["Password"] = s.Text("password"),
            ["ResponseFormat"] = "JSON",
        };
        var host = s.Flag("sandbox") ? "https://testapi.internet.bs/" : "https://api.internet.bs/";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(host), command)) { Content = new FormUrlEncodedContent(form) };
        var (status, body, _) = await http.Send(Name, request, mutating, s.Ct).ConfigureAwait(false);
        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
        }

        if (json is null)
        {
            throw status >= 400 ? RegistrarHttp.Status(Name, status, mutating) : mutating
                ? Fail.Unknown("Internet.bs respondió algo que no es JSON. Puede haberse procesado.")
                : Fail.Unavailable("Internet.bs respondió algo que no es JSON.");
        }

        if (json["status"]?.ToString() != "FAILURE")
        {
            return status < 400 ? json : throw RegistrarHttp.Status(Name, status, mutating);
        }

        var message = Fail.Short(json["message"]?.ToString());
        var code = json["code"]?.ToString();
        var text = message.ToLowerInvariant();
        throw text switch
        {
            _ when text.Contains("password", StringComparison.Ordinal) || text.Contains("api key", StringComparison.Ordinal)
                || text.Contains("apikey", StringComparison.Ordinal) || text.Contains("not allowed", StringComparison.Ordinal)
                || text.Contains("authentication", StringComparison.Ordinal) => Fail.Config($"Internet.bs rechazó las credenciales o la IP: {message}"),
            _ when text.Contains("too many", StringComparison.Ordinal) => Fail.Limited($"Internet.bs limitó los pedidos: {message}", 60),
            _ when text.Contains("not found", StringComparison.Ordinal) || text.Contains("does not exist", StringComparison.Ordinal)
                || text.Contains("not exist", StringComparison.Ordinal) => Fail.NotFound($"Internet.bs: {message}"),
            _ when text.Contains("not available", StringComparison.Ordinal) || text.Contains("already registered", StringComparison.Ordinal)
                || text.Contains("unavailable", StringComparison.Ordinal) => Fail.Rejected($"Internet.bs: {message}", "taken", code),
            _ when text.Contains("auth", StringComparison.Ordinal) && command.Contains("Transfer", StringComparison.Ordinal) => Fail.Rejected($"Internet.bs: {message}", "invalid_auth_code", code),
            _ => Fail.Rejected($"Internet.bs: {message}", null, code),
        };
    }

    /// <summary>Los cuatro contactos con los datos del titular, como los pide Internet.bs.</summary>
    private static Dictionary<string, string> Contacts(Contact c)
    {
        var args = new Dictionary<string, string>();
        foreach (var role in Roles)
        {
            args[$"{role}_FirstName"] = c.FirstName;
            args[$"{role}_LastName"] = c.LastName;
            args[$"{role}_Email"] = c.Email;
            args[$"{role}_PhoneNumber"] = c.Phone;
            args[$"{role}_Street"] = c.Address1;
            args[$"{role}_City"] = c.City;
            args[$"{role}_CountryCode"] = c.Country;
            args[$"{role}_PostalCode"] = c.PostalCode;
            if (c.Address2 is { Length: > 0 } street2)
            {
                args[$"{role}_Street2"] = street2;
            }

            if (c.Company is { Length: > 0 } company)
            {
                args[$"{role}_Organization"] = company;
            }
        }

        return args;
    }

    private static string Period(int years) => string.Create(CultureInfo.InvariantCulture, $"{years}Y");

    /// <summary>Dato del primer producto de la respuesta, en lista o aplanado como product_0_campo.</summary>
    private static string? Product(JsonObject json, string field) =>
        (json["product"] as JsonArray)?.FirstOrDefault()?[field]?.ToString() ?? json[$"product_0_{field}"]?.ToString() ?? json[field]?.ToString();

    private static List<string> Nameservers(JsonObject json) =>
        json["nameserver"] is JsonArray list
            ? [.. list.Select(n => n!.ToString().ToLowerInvariant())]
            : [.. Enumerable.Range(0, 13).Select(i => json[$"nameserver_{i}"]?.ToString()).OfType<string>().Select(n => n.ToLowerInvariant())];

    /// <summary>Las filas de una lista, que llega como arreglo o aplanada como records_0_name, records_0_type.</summary>
    private static List<JsonObject> Rows(JsonObject json, string key)
    {
        if (json[key] is JsonArray list)
        {
            return [.. list.OfType<JsonObject>()];
        }

        var rows = new List<JsonObject>();
        for (var i = 0; ; i++)
        {
            var prefix = string.Create(CultureInfo.InvariantCulture, $"{key}_{i}_");
            var row = new JsonObject();
            foreach (var (k, v) in json.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                row[k[prefix.Length..]] = v?.DeepClone();
            }

            if (row.Count == 0)
            {
                return rows;
            }

            rows.Add(row);
        }
    }

    /// <summary>Un registro de la lista. Un MX sin prioridad aparte la trae adelante del valor ("10 mx.ejemplo.com").</summary>
    private static DnsRecord? Record(string zone, JsonObject r)
    {
        var type = r["type"]?.ToString()?.ToUpperInvariant();
        var value = r["value"]?.ToString() ?? string.Empty;
        int? priority = int.TryParse(r["priority"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
        if (type == "MX" && priority is null && value.Split(' ', 2) is [var pref, var host] && int.TryParse(pref, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            (priority, value) = (parsed, host);
        }

        return DnsRecords.Read(zone, r["name"]?.ToString(), type, value, int.TryParse(r["ttl"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var ttl) ? ttl : null, priority);
    }
}
