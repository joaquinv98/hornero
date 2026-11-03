using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Registrars;

/// <summary>
/// Namecheap, API XML por HTTP. Cada pedido lleva ApiUser, ApiKey, UserName y ClientIp, la IP que la cuenta tiene
/// habilitada. La API no da el código de transferencia: se pide desde el panel de Namecheap.
/// </summary>
internal sealed class Namecheap(RegistrarHttp http) : Registrar
{
    private static readonly string[] Roles = ["Registrant", "Tech", "Admin", "AuxBilling"];

    public override string Id => "namecheap";

    public override string Name => "Namecheap";

    /// <summary>Sin registrar.auth_code: la API no da el código de transferencia.</summary>
    public override IReadOnlyList<string> Extras =>
        ["registrar.transfer", "registrar.lock", "registrar.contacts", "registrar.pricing", "registrar.dns", "registrar.email_forwarding", "registrar.privacy"];

    public override JsonObject ConfigSchema => (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "required": ["api_user", "api_key", "username", "client_ip"],
          "properties": {
            "api_user": { "type": "string" },
            "api_key": { "type": "string", "x-secret": true },
            "username": { "type": "string", "description": "Usuario sobre el que se opera, casi siempre el mismo que api_user" },
            "client_ip": { "type": "string", "description": "IP de salida del módulo, habilitada en la cuenta de Namecheap" },
            "sandbox": { "type": "boolean", "description": "Usar el ambiente de prueba api.sandbox.namecheap.com" }
          }
        }
        """)!;

    public override async Task Test(Session s) => await Call(s, "namecheap.users.getBalances", [], mutating: false).ConfigureAwait(false);

    public override async Task<IReadOnlyList<Availability>> Check(Session s, IReadOnlyList<DomainName> names)
    {
        var response = await Call(s, "namecheap.domains.check", new() { ["DomainList"] = string.Join(',', names.Select(n => n.Name)) }, mutating: false).ConfigureAwait(false);
        var results = response.Descendants().Where(e => e.Name.LocalName == "DomainCheckResult")
            .ToDictionary(e => ((string?)e.Attribute("Domain") ?? string.Empty).ToLowerInvariant(), StringComparer.Ordinal);
        return [.. names.Select(n =>
        {
            if (!results.TryGetValue(n.Name, out var r))
            {
                return new Availability(n, null, Reason: "no_answer");
            }

            var premium = Bool(r, "IsPremiumName");
            return new Availability(n, Bool(r, "Available"), premium, premium ? Decimal(r, "PremiumRegistrationPrice") : null, premium ? "USD" : null);
        })];
    }

    public override async Task<DomainState> Register(Session s, RegisterOrder order)
    {
        var args = Contacts(order.Registrant);
        args["DomainName"] = order.Name.Name;
        args["Years"] = Number(order.Years);
        args["Nameservers"] = string.Join(',', order.Nameservers);
        args["AddFreeWhoisguard"] = order.Privacy ? "yes" : "no";
        args["WGEnabled"] = order.Privacy ? "yes" : "no";
        var response = await Call(s, "namecheap.domains.create", args, mutating: true).ConfigureAwait(false);
        var result = Element(response, "DomainCreateResult");
        if (!Bool(result, "Registered"))
        {
            throw Fail.Rejected($"Namecheap no registró {order.Name}.", "failed");
        }

        var ids = new JsonObject { ["domain_id"] = (string?)result?.Attribute("DomainID") };
        return await Info(s, new DomainRef(order.Name, ids)).ConfigureAwait(false) is { Status: "active" } state
            ? state with { ExternalIds = ids }
            : new DomainState("pending", null, order.Nameservers, null, ids);
    }

    public override async Task<DateOnly> Renew(Session s, DomainRef domain, int years, DateOnly currentExpiry)
    {
        var response = await Call(s, "namecheap.domains.renew", new() { ["DomainName"] = domain.Name.Name, ["Years"] = Number(years) }, mutating: true).ConfigureAwait(false);
        var expired = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "ExpiredDate")?.Value;
        return Date(expired, "MM/dd/yyyy", "M/d/yyyy") ?? currentExpiry.AddYears(years);
    }

    public override async Task<DomainState> Info(Session s, DomainRef domain)
    {
        XElement response;
        try
        {
            response = await Call(s, "namecheap.domains.getInfo", new() { ["DomainName"] = domain.Name.Name }, mutating: false).ConfigureAwait(false);
        }
        catch (ModuleException e) when (e.Code == ErrorCodes.NotFound)
        {
            return DomainState.NotFound;
        }

        var info = Element(response, "DomainGetInfoResult");
        var lockResponse = await Call(s, "namecheap.domains.getRegistrarLock", new() { ["DomainName"] = domain.Name.Name }, mutating: false).ConfigureAwait(false);
        var expired = info?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ExpiredDate")?.Value;
        var nameservers = info?.Descendants().Where(e => e.Name.LocalName == "Nameserver").Select(e => e.Value.Trim().ToLowerInvariant()).ToList() ?? [];
        var guard = info?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Whoisguard");
        return new DomainState(
            ((string?)info?.Attribute("Status"))?.ToLowerInvariant() switch
            {
                "ok" or "locked" => "active",
                "expired" => "expired",
                _ => "unknown",
            },
            Date(expired, "MM/dd/yyyy", "M/d/yyyy"),
            nameservers,
            Bool(Element(lockResponse, "DomainGetRegistrarLockResult"), "RegistrarLockStatus"),
            new JsonObject { ["domain_id"] = (string?)info?.Attribute("ID") },
            guard is null ? null : Bool(guard, "Enabled"));
    }

    /// <summary>Los hosts de la zona en el DNS de Namecheap (BasicDNS), de los tipos que se administran.</summary>
    public override async Task<IReadOnlyList<DnsRecord>> Dns(Session s, DomainRef domain) =>
        [.. (await Hosts(s, domain).ConfigureAwait(false)).Hosts.Select(h => Record(domain.Name.Name, h)).OfType<DnsRecord>()];

    /// <summary>
    /// setHosts reemplaza todos los hosts: se mandan los pedidos más los de tipos que no se administran, como estaban.
    /// Con registros MX propios el correo pasa a MX, y sin ellos deja de serlo.
    /// </summary>
    public override async Task SetDns(Session s, DomainRef domain, IReadOnlyList<DnsRecord> records)
    {
        var (hosts, emailType) = await Hosts(s, domain).ConfigureAwait(false);
        var mail = records.Any(r => r.Type == "MX") ? "MX" : emailType == "MX" ? "NONE" : emailType;
        if (Diff(hosts.Select(h => Record(domain.Name.Name, h)).OfType<DnsRecord>(), records) is ({ Count: 0 }, { Count: 0 }) && mail == emailType)
        {
            return;
        }

        var kept = hosts.Where(h => Record(domain.Name.Name, h) is null)
            .Select(h => ((string?)h.Attribute("Name") ?? "@", (string?)h.Attribute("Type") ?? string.Empty, (string?)h.Attribute("Address") ?? string.Empty, (string?)h.Attribute("TTL") ?? "1800", (string?)h.Attribute("MXPref")));
        var wanted = records.Select(r => (r.Name, r.Type, r.Value, r.Ttl.ToString(CultureInfo.InvariantCulture), r.Priority?.ToString(CultureInfo.InvariantCulture)));
        await SetHosts(s, domain, [.. wanted.Concat(kept)], mail).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<EmailForward>> Forwards(Session s, DomainRef domain)
    {
        var response = await Call(s, "namecheap.domains.dns.getEmailForwarding", new() { ["DomainName"] = domain.Name.Name }, mutating: false).ConfigureAwait(false);
        return [.. response.Descendants().Where(e => e.Name.LocalName == "Forward")
            .Select(e => ReadForward(domain.Name.Name, (string?)e.Attribute("mailbox"), e.Value)).OfType<EmailForward>()];
    }

    /// <summary>
    /// Reemplaza los reenvíos. Para que el correo llegue, el dominio tiene que estar en modo reenvío (FWD): si no tiene
    /// registros MX propios, se lo pone en ese modo.
    /// </summary>
    public override async Task SetForwards(Session s, DomainRef domain, IReadOnlyList<EmailForward> forwards)
    {
        var args = new Dictionary<string, string> { ["DomainName"] = domain.Name.Name };
        for (var i = 0; i < forwards.Count; i++)
        {
            args[$"MailBox{i + 1}"] = forwards[i].Mailbox;
            args[$"ForwardTo{i + 1}"] = forwards[i].To;
        }

        var response = await Call(s, "namecheap.domains.dns.setEmailForwarding", args, mutating: true).ConfigureAwait(false);
        if (!Bool(Element(response, "DomainDNSSetEmailForwardingResult"), "IsSuccess"))
        {
            throw Fail.Rejected($"Namecheap no cambió los reenvíos de {domain.Name}.", "failed");
        }

        var (hosts, emailType) = await Hosts(s, domain).ConfigureAwait(false);
        if (forwards.Count > 0 && emailType != "FWD" && !hosts.Any(h => (string?)h.Attribute("Type") == "MX"))
        {
            var all = hosts.Select(h => ((string?)h.Attribute("Name") ?? "@", (string?)h.Attribute("Type") ?? string.Empty, (string?)h.Attribute("Address") ?? string.Empty, (string?)h.Attribute("TTL") ?? "1800", (string?)h.Attribute("MXPref")));
            await SetHosts(s, domain, [.. all], "FWD").ConfigureAwait(false);
        }
    }

    /// <summary>La protección de Namecheap va por el ID de su suscripción, que sale de getInfo, y reenvía al correo del titular.</summary>
    public override async Task SetPrivacy(Session s, DomainRef domain, bool enabled, string? registrantEmail)
    {
        var info = Element(await Call(s, "namecheap.domains.getInfo", new() { ["DomainName"] = domain.Name.Name }, mutating: false).ConfigureAwait(false), "DomainGetInfoResult");
        var guard = info?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Whoisguard");
        var id = guard?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ID")?.Value.Trim();
        if (string.IsNullOrEmpty(id) || id == "0")
        {
            throw Fail.Rejected($"{domain.Name} no tiene la protección de identidad de Namecheap.", "no_privacy_subscription");
        }

        if (Bool(guard, "Enabled") == enabled)
        {
            return;
        }

        var args = new Dictionary<string, string> { ["WhoisguardID"] = id };
        if (enabled)
        {
            args["ForwardedToEmail"] = registrantEmail is { Length: > 0 } email ? email : throw Fail.Input("Falta el correo del titular, al que reenvía la protección.", "contacts.registrant.email");
        }

        var response = await Call(s, enabled ? "namecheap.whoisguard.enable" : "namecheap.whoisguard.disable", args, mutating: true).ConfigureAwait(false);
        if (!Bool(Element(response, enabled ? "WhoisguardEnableResult" : "WhoisguardDisableResult"), "IsSuccess"))
        {
            throw Fail.Rejected($"Namecheap no cambió la protección de {domain.Name}.", "failed");
        }
    }

    private async Task<(List<XElement> Hosts, string EmailType)> Hosts(Session s, DomainRef domain)
    {
        var response = await Call(s, "namecheap.domains.dns.getHosts", new() { ["SLD"] = domain.Name.Label, ["TLD"] = domain.Name.Extension }, mutating: false).ConfigureAwait(false);
        var result = Element(response, "DomainDNSGetHostsResult");
        return ([.. result?.Elements().Where(e => e.Name.LocalName == "host") ?? []], ((string?)result?.Attribute("EmailType") ?? "NONE").ToUpperInvariant());
    }

    private async Task SetHosts(Session s, DomainRef domain, IReadOnlyList<(string Name, string Type, string Address, string Ttl, string? MxPref)> hosts, string emailType)
    {
        var args = new Dictionary<string, string> { ["SLD"] = domain.Name.Label, ["TLD"] = domain.Name.Extension, ["EmailType"] = emailType };
        for (var i = 0; i < hosts.Count; i++)
        {
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            (args["HostName" + n], args["RecordType" + n], args["Address" + n], args["TTL" + n]) = (hosts[i].Name, hosts[i].Type, hosts[i].Address, hosts[i].Ttl);
            if (hosts[i].MxPref is { } pref)
            {
                args["MXPref" + n] = pref;
            }
        }

        var response = await Call(s, "namecheap.domains.dns.setHosts", args, mutating: true).ConfigureAwait(false);
        if (!Bool(Element(response, "DomainDNSSetHostsResult"), "IsSuccess"))
        {
            throw Fail.Rejected($"Namecheap no cambió la zona de {domain.Name}.", "failed");
        }
    }

    private static DnsRecord? Record(string zone, XElement host) => DnsRecords.Read(
        zone,
        (string?)host.Attribute("Name"),
        (string?)host.Attribute("Type"),
        (string?)host.Attribute("Address"),
        int.TryParse((string?)host.Attribute("TTL"), NumberStyles.None, CultureInfo.InvariantCulture, out var ttl) ? ttl : null,
        int.TryParse((string?)host.Attribute("MXPref"), NumberStyles.None, CultureInfo.InvariantCulture, out var pref) ? pref : null);

    public override async Task SetNameservers(Session s, DomainRef domain, IReadOnlyList<string> nameservers)
    {
        var response = await Call(s, "namecheap.domains.dns.setCustom", new() { ["SLD"] = domain.Name.Label, ["TLD"] = domain.Name.Extension, ["Nameservers"] = string.Join(',', nameservers) }, mutating: true).ConfigureAwait(false);
        if (!Bool(Element(response, "DomainDNSSetCustomResult"), "Updated"))
        {
            throw Fail.Rejected($"Namecheap no cambió los nameservers de {domain.Name}.", "failed");
        }
    }

    public override async Task<DomainState> Transfer(Session s, TransferOrder order)
    {
        // Namecheap transfiere por un año, que suma al vencimiento.
        var response = await Call(s, "namecheap.domains.transfer.create", new() { ["DomainName"] = order.Name.Name, ["Years"] = "1", ["EPPCode"] = order.AuthCode }, mutating: true).ConfigureAwait(false);
        var result = Element(response, "DomainTransferCreateResult");
        return Bool(result, "Transfer")
            ? new DomainState("pending_transfer", null, order.Nameservers, null, new JsonObject { ["transfer_id"] = (string?)result?.Attribute("TransferID") })
            : throw Fail.Rejected($"Namecheap no aceptó la transferencia de {order.Name}.", "invalid_auth_code");
    }

    public override async Task SetLock(Session s, DomainRef domain, bool locked)
    {
        var response = await Call(s, "namecheap.domains.setRegistrarLock", new() { ["DomainName"] = domain.Name.Name, ["LockAction"] = locked ? "LOCK" : "UNLOCK" }, mutating: true).ConfigureAwait(false);
        if (!Bool(Element(response, "DomainSetRegistrarLockResult"), "IsSuccess"))
        {
            throw Fail.Rejected($"Namecheap no cambió el bloqueo de {domain.Name}.", "failed");
        }
    }

    public override async Task SetContacts(Session s, DomainRef domain, Contact registrant)
    {
        var args = Contacts(registrant);
        args["DomainName"] = domain.Name.Name;
        var response = await Call(s, "namecheap.domains.setContacts", args, mutating: true).ConfigureAwait(false);
        if (!Bool(Element(response, "DomainSetContactResult"), "IsSuccess"))
        {
            throw Fail.Rejected($"Namecheap no cambió los contactos de {domain.Name}.", "failed");
        }
    }

    /// <summary>Precios de la cuenta por TLD y años, en dólares, de las categorías REGISTER, RENEW y TRANSFER.</summary>
    public override async Task<IReadOnlyList<CostPrice>> Prices(Session s, string? currency)
    {
        var response = await Call(s, "namecheap.users.getPricing", new() { ["ProductType"] = "DOMAIN" }, mutating: false).ConfigureAwait(false);
        var rows = new Dictionary<(string Tld, int Years), CostPrice>();
        foreach (var category in response.Descendants().Where(e => e.Name.LocalName == "ProductCategory"))
        {
            var operation = ((string?)category.Attribute("Name"))?.ToUpperInvariant();
            foreach (var product in category.Elements().Where(e => e.Name.LocalName == "Product"))
            {
                var tld = ((string?)product.Attribute("Name"))?.ToLowerInvariant();
                foreach (var price in product.Elements().Where(e => e.Name.LocalName == "Price" && ((string?)e.Attribute("DurationType"))?.ToUpperInvariant() == "YEAR"))
                {
                    if (tld is null || !int.TryParse((string?)price.Attribute("Duration"), NumberStyles.None, CultureInfo.InvariantCulture, out var years) || years is < 1 or > 10)
                    {
                        continue;
                    }

                    var value = Decimal(price, "YourPrice") ?? Decimal(price, "Price");
                    var row = rows.GetValueOrDefault((tld, years)) ?? new CostPrice(tld, (string?)price.Attribute("Currency") ?? "USD", years, null, null, null);
                    rows[(tld, years)] = operation switch
                    {
                        "REGISTER" => row with { Register = value },
                        "RENEW" => row with { Renew = value },
                        "TRANSFER" => row with { Transfer = value },
                        _ => row,
                    };
                }
            }
        }

        return [.. rows.Values];
    }

    /// <summary>Un comando. Las lecturas van por GET y lo que cambia algo por POST, con los parámetros en el formulario.</summary>
    private async Task<XElement> Call(Session s, string command, Dictionary<string, string> args, bool mutating)
    {
        var all = new Dictionary<string, string>(args)
        {
            ["ApiUser"] = s.Text("api_user"),
            ["ApiKey"] = s.Text("api_key"),
            ["UserName"] = s.Text("username"),
            ["ClientIp"] = s.Text("client_ip"),
            ["Command"] = command,
        };
        var url = new Uri(s.Flag("sandbox") ? "https://api.sandbox.namecheap.com/xml.response" : "https://api.namecheap.com/xml.response");
        using var request = mutating
            ? new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(all) }
            : new HttpRequestMessage(HttpMethod.Get, new Uri(url + "?" + string.Join('&', all.Select(a => $"{Uri.EscapeDataString(a.Key)}={Uri.EscapeDataString(a.Value)}"))));
        var (status, body, _) = await http.Send(Name, request, mutating, s.Ct).ConfigureAwait(false);
        XElement root;
        try
        {
            root = XElement.Parse(body);
        }
        catch (XmlException)
        {
            throw status >= 400 ? RegistrarHttp.Status(Name, status, mutating) : mutating
                ? Fail.Unknown("Namecheap respondió algo que no es XML. Puede haberse procesado.")
                : Fail.Unavailable("Namecheap respondió algo que no es XML.");
        }

        if (((string?)root.Attribute("Status"))?.Equals("OK", StringComparison.OrdinalIgnoreCase) == true)
        {
            return root.Elements().FirstOrDefault(e => e.Name.LocalName == "CommandResponse") ?? root;
        }

        var error = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Error");
        var number = (string?)error?.Attribute("Number") ?? string.Empty;
        var message = Fail.Short(error?.Value);
        var lower = message.ToLowerInvariant();
        throw number switch
        {
            _ when number.StartsWith("1010", StringComparison.Ordinal) || number.StartsWith("1011", StringComparison.Ordinal) => Fail.Config($"Namecheap rechazó las credenciales o la IP: {message}"),
            "2019166" or "2016166" => Fail.NotFound($"Namecheap: {message}"),
            _ when lower.Contains("too many", StringComparison.Ordinal) => Fail.Limited($"Namecheap limitó los pedidos: {message}", 60),
            _ when lower.Contains("not available", StringComparison.Ordinal) || lower.Contains("already registered", StringComparison.Ordinal)
                || lower.Contains("is unavailable", StringComparison.Ordinal) => Fail.Rejected($"Namecheap: {message}", "taken", number),
            _ when lower.Contains("not found", StringComparison.Ordinal) => Fail.NotFound($"Namecheap: {message}"),
            _ when command.Contains("transfer", StringComparison.Ordinal) && lower.Contains("epp", StringComparison.Ordinal) => Fail.Rejected($"Namecheap: {message}", "invalid_auth_code", number),
            _ => Fail.Rejected($"Namecheap: {message}", null, number),
        };
    }

    /// <summary>Los cuatro contactos que exige Namecheap, todos con los datos del titular.</summary>
    private static Dictionary<string, string> Contacts(Contact c)
    {
        var phone = c.Phone;
        var args = new Dictionary<string, string>();
        foreach (var role in Roles)
        {
            args[$"{role}FirstName"] = c.FirstName;
            args[$"{role}LastName"] = c.LastName;
            args[$"{role}Address1"] = c.Address1;
            args[$"{role}City"] = c.City;
            args[$"{role}StateProvince"] = c.Region;
            args[$"{role}PostalCode"] = c.PostalCode;
            args[$"{role}Country"] = c.Country;
            args[$"{role}Phone"] = phone;
            args[$"{role}EmailAddress"] = c.Email;
            if (c.Address2 is { Length: > 0 } line2)
            {
                args[$"{role}Address2"] = line2;
            }

            if (c.Company is { Length: > 0 } company)
            {
                args[$"{role}OrganizationName"] = company;
            }
        }

        return args;
    }

    private static XElement? Element(XElement response, string name) => response.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == name);

    private static bool Bool(XElement? e, string attribute) => ((string?)e?.Attribute(attribute))?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static decimal? Decimal(XElement? e, string attribute) =>
        decimal.TryParse((string?)e?.Attribute(attribute), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
