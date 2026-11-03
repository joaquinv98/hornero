using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Registrars;

/// <summary>
/// ResellerClub, sobre la plataforma LogicBoxes (la misma API usan otras marcas de revendedores). API HTTP con
/// parámetros en la URL y respuesta JSON, autenticada con auth-userid y api-key.
/// Cada registro crea un contacto con los datos del titular, dentro del cliente de la configuración.
/// </summary>
internal sealed class ResellerClub(RegistrarHttp http) : Registrar
{
    public override string Id => "resellerclub";

    public override string Name => "ResellerClub";

    /// <summary>
    /// Sin precios: la lista de costos viene por clave de producto y no por TLD, y se cargan por CSV. El reenvío de
    /// correo es un producto aparte con su propia orden, así que tampoco se declara.
    /// </summary>
    public override IReadOnlyList<string> Extras => ["registrar.transfer", "registrar.auth_code", "registrar.lock", "registrar.contacts", "registrar.dns", "registrar.privacy"];

    /// <summary>Cómo se llama cada tipo de registro en las rutas de LogicBoxes (add-ipv4-record, delete-mx-record).</summary>
    private static readonly Dictionary<string, string> RecordPaths = new(StringComparer.Ordinal)
    {
        ["A"] = "ipv4", ["AAAA"] = "ipv6", ["CNAME"] = "cname", ["MX"] = "mx", ["TXT"] = "txt",
    };

    public override JsonObject ConfigSchema => (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "required": ["reseller_id", "api_key", "customer_id"],
          "properties": {
            "reseller_id": { "type": "string", "description": "auth-userid del revendedor" },
            "api_key": { "type": "string", "x-secret": true },
            "customer_id": { "type": "string", "description": "Cliente de ResellerClub que queda como dueño de los dominios" },
            "sandbox": { "type": "boolean", "description": "Usar el ambiente de prueba test.httpapi.com" }
          }
        }
        """)!;

    public override async Task Test(Session s) =>
        await Call(s, HttpMethod.Get, "domains/available.json", [("domain-name", "billing-oss-prueba"), ("tlds", "com")], mutating: false).ConfigureAwait(false);

    public override async Task<IReadOnlyList<Availability>> Check(Session s, IReadOnlyList<DomainName> names)
    {
        var results = new List<Availability>();
        foreach (var group in names.GroupBy(n => n.Label))
        {
            var args = new List<(string, string)> { ("domain-name", group.Key) };
            args.AddRange(group.Select(n => ("tlds", n.Extension)));
            var json = await Call(s, HttpMethod.Get, "domains/available.json", args, mutating: false).ConfigureAwait(false);
            results.AddRange(group.Select(n => new Availability(n, json[n.Name]?["status"]?.ToString() switch
            {
                "available" => true,
                "regthroughus" or "regthroughothers" => false,
                _ => null,
            })));
        }

        return results;
    }

    public override async Task<DomainState> Register(Session s, RegisterOrder order)
    {
        var contact = await AddContact(s, order.Registrant).ConfigureAwait(false);
        var args = Order(s, order.Name, order.Years, contact);
        args.AddRange(order.Nameservers.Select(n => ("ns", n)));
        args.Add(("protect-privacy", order.Privacy ? "true" : "false"));
        var json = await Call(s, HttpMethod.Post, "domains/register.json", args, mutating: true).ConfigureAwait(false);
        Action(json, "taken");
        return await Info(s, new DomainRef(order.Name, [])).ConfigureAwait(false) is { Status: "active" } state ? state : new DomainState("pending", null, order.Nameservers, null, []);
    }

    /// <summary>LogicBoxes pide el vencimiento actual exacto como lo tiene él: sale de los detalles, no del núcleo.</summary>
    public override async Task<DateOnly> Renew(Session s, DomainRef domain, int years, DateOnly currentExpiry)
    {
        var details = await Details(s, domain.Name).ConfigureAwait(false) ?? throw Fail.NotFound($"ResellerClub no tiene {domain.Name}.");
        var json = await Call(
            s,
            HttpMethod.Post,
            "domains/renew.json",
            [("order-id", details["orderid"]!.ToString()), ("years", Years(years)), ("exp-date", details["endtime"]!.ToString()), ("invoice-option", "NoInvoice")],
            mutating: true).ConfigureAwait(false);
        Action(json, null);
        return (await Details(s, domain.Name).ConfigureAwait(false) is { } after ? EndTime(after) : null) ?? currentExpiry.AddYears(years);
    }

    public override async Task<DomainState> Info(Session s, DomainRef domain)
    {
        if (await Details(s, domain.Name).ConfigureAwait(false) is not { } d)
        {
            return DomainState.NotFound;
        }

        var nameservers = Enumerable.Range(1, 13).Select(i => d[$"ns{i}"]?.ToString()).OfType<string>().Select(n => n.ToLowerInvariant()).ToList();
        var flags = (d["orderstatus"] as JsonArray ?? []).Select(f => f?.ToString()).ToList();
        var status = d["currentstatus"]?.ToString() switch
        {
            "Active" => EndTime(d) is { } end && end < DateOnly.FromDateTime(DateTime.UtcNow) ? "expired" : "active",
            "Deleted" or "Archived" => "transferred_away",
            "InActive" or "Pending" => "pending",
            _ => "unknown",
        };
        return new DomainState(
            status,
            EndTime(d),
            nameservers,
            flags.Contains("transferlock"),
            new JsonObject { ["order_id"] = d["orderid"]?.ToString() },
            d["isprivacyprotected"]?.ToString() is { Length: > 0 } privacy ? privacy.Equals("true", StringComparison.OrdinalIgnoreCase) : null);
    }

    /// <summary>La zona del DNS administrado de LogicBoxes, que se busca por tipo y de a 50.</summary>
    public override async Task<IReadOnlyList<DnsRecord>> Dns(Session s, DomainRef domain)
    {
        var zone = domain.Name.Name;
        var records = new List<DnsRecord>();
        foreach (var type in DnsRecords.Types)
        {
            for (var page = 1; ; page++)
            {
                var json = await Call(s, HttpMethod.Get, "dns/manage/search-records.json", [("domain-name", zone), ("type", type), ("no-of-records", "50"), ("page-no", page.ToString(CultureInfo.InvariantCulture))], mutating: false).ConfigureAwait(false);
                var rows = json.Where(p => p.Key.All(char.IsAsciiDigit)).Select(p => p.Value).OfType<JsonObject>().ToList();
                records.AddRange(rows.Select(r => DnsRecords.Read(
                    zone,
                    r["host"]?.ToString(),
                    r["type"]?.ToString() ?? type,
                    r["value"]?.ToString(),
                    int.TryParse(r["timetolive"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var ttl) ? ttl : null,
                    int.TryParse(r["priority"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var priority) ? priority : null)).OfType<DnsRecord>());
                if (rows.Count < 50)
                {
                    break;
                }
            }
        }

        return records;
    }

    /// <summary>Prende el DNS administrado de la orden y saca y agrega de a un registro.</summary>
    public override async Task SetDns(Session s, DomainRef domain, IReadOnlyList<DnsRecord> records)
    {
        var zone = domain.Name.Name;
        await ActivateDns(s, await OrderId(s, domain).ConfigureAwait(false)).ConfigureAwait(false);
        var (remove, add) = Diff(await Dns(s, domain).ConfigureAwait(false), records);
        foreach (var r in remove)
        {
            Action(await Call(s, HttpMethod.Post, $"dns/manage/delete-{RecordPaths[r.Type]}-record.json", [("domain-name", zone), ("host", Host(r)), ("value", r.Value)], mutating: true).ConfigureAwait(false), null);
        }

        foreach (var r in add)
        {
            List<(string, string)> args = [("domain-name", zone), ("host", Host(r)), ("value", r.Value), ("ttl", r.Ttl.ToString(CultureInfo.InvariantCulture))];
            if (r.Priority is { } priority)
            {
                args.Add(("priority", priority.ToString(CultureInfo.InvariantCulture)));
            }

            Action(await Call(s, HttpMethod.Post, $"dns/manage/add-{RecordPaths[r.Type]}-record.json", args, mutating: true).ConfigureAwait(false), null);
        }
    }

    public override async Task SetPrivacy(Session s, DomainRef domain, bool enabled, string? registrantEmail) =>
        Action(await Call(
            s,
            HttpMethod.Post,
            "domains/modify-privacy-protection.json",
            [("order-id", await OrderId(s, domain).ConfigureAwait(false)), ("protect-privacy", enabled ? "true" : "false"), ("reason", "Pedido del titular")],
            mutating: true).ConfigureAwait(false), null);

    /// <summary>LogicBoxes dice "@" con el host vacío.</summary>
    private static string Host(DnsRecord r) => r.Name == "@" ? string.Empty : r.Name;

    /// <summary>
    /// Prende el DNS administrado de la orden. Responde en XML y no dice si ya estaba prendido, así que es a mejor
    /// esfuerzo: si no quedó prendido, lo dicen los cambios de registros que siguen.
    /// </summary>
    private async Task ActivateDns(Session s, string orderId)
    {
        using var request = Request(s, HttpMethod.Post, "dns/activate.xml", [("order-id", orderId)]);
        try
        {
            await http.Send(Name, request, mutating: true, s.Ct).ConfigureAwait(false);
        }
        catch (ModuleException e) when (e.Code is ErrorCodes.OutcomeUnknown or ErrorCodes.UpstreamUnavailable)
        {
            // Lo que no llegó se ve en los registros: si el servicio no se prendió, el primer cambio falla.
        }
    }

    public override async Task SetNameservers(Session s, DomainRef domain, IReadOnlyList<string> nameservers)
    {
        var args = new List<(string, string)> { ("order-id", await OrderId(s, domain).ConfigureAwait(false)) };
        args.AddRange(nameservers.Select(n => ("ns", n)));
        Action(await Call(s, HttpMethod.Post, "domains/modify-ns.json", args, mutating: true).ConfigureAwait(false), null);
    }

    public override async Task<DomainState> Transfer(Session s, TransferOrder order)
    {
        var contact = await AddContact(s, order.Registrant).ConfigureAwait(false);
        var args = Order(s, order.Name, order.Years, contact);
        args.RemoveAll(a => a.Item1 == "years");
        args.Add(("auth-code", order.AuthCode));
        args.AddRange(order.Nameservers.Select(n => ("ns", n)));
        Action(await Call(s, HttpMethod.Post, "domains/transfer.json", args, mutating: true).ConfigureAwait(false), "invalid_auth_code");
        return new DomainState("pending_transfer", null, order.Nameservers, null, []);
    }

    public override async Task SetLock(Session s, DomainRef domain, bool locked) =>
        Action(await Call(s, HttpMethod.Post, locked ? "domains/enable-theft-protection.json" : "domains/disable-theft-protection.json", [("order-id", await OrderId(s, domain).ConfigureAwait(false))], mutating: true).ConfigureAwait(false), null);

    public override async Task<string> AuthCode(Session s, DomainRef domain) =>
        (await Details(s, domain.Name).ConfigureAwait(false))?["domsecret"]?.ToString() is { Length: > 0 } code
            ? code
            : throw Fail.NotFound($"ResellerClub no dio el código de {domain.Name}.");

    public override async Task SetContacts(Session s, DomainRef domain, Contact registrant)
    {
        var contact = await AddContact(s, registrant).ConfigureAwait(false);
        Action(await Call(
            s,
            HttpMethod.Post,
            "domains/modify-contact.json",
            [("order-id", await OrderId(s, domain).ConfigureAwait(false)), ("reg-contact-id", contact), ("admin-contact-id", contact), ("tech-contact-id", contact), ("billing-contact-id", contact)],
            mutating: true).ConfigureAwait(false), null);
    }

    /// <summary>Detalles por nombre, o null si el dominio no está en la cuenta.</summary>
    private async Task<JsonObject?> Details(Session s, DomainName name)
    {
        try
        {
            return await Call(s, HttpMethod.Get, "domains/details-by-name.json", [("domain-name", name.Name), ("options", "All")], mutating: false).ConfigureAwait(false);
        }
        catch (ModuleException e) when (e.Code == ErrorCodes.NotFound)
        {
            return null;
        }
    }

    private async Task<string> OrderId(Session s, DomainRef domain) =>
        domain.Id("order_id") ?? (await Details(s, domain.Name).ConfigureAwait(false))?["orderid"]?.ToString() ?? throw Fail.NotFound($"ResellerClub no tiene {domain.Name}.");

    /// <summary>Un contacto nuevo con los datos del titular. Devuelve su ID.</summary>
    private async Task<string> AddContact(Session s, Contact c)
    {
        var (cc, number) = c.SplitPhone();
        List<(string, string)> args =
        [
            ("name", $"{c.FirstName} {c.LastName}"), ("company", c.Company is { Length: > 0 } company ? company : "N/A"), ("email", c.Email),
            ("address-line-1", c.Address1), ("city", c.City), ("state", c.Region), ("country", c.Country), ("zipcode", c.PostalCode),
            ("phone-cc", cc), ("phone", number), ("customer-id", s.Text("customer_id")), ("type", "Contact"),
        ];
        if (c.Address2 is { Length: > 0 } line2)
        {
            args.Add(("address-line-2", line2));
        }

        var node = await Send(s, HttpMethod.Post, "contacts/add.json", args, mutating: true).ConfigureAwait(false);
        return node is JsonValue v && v.ToString() is { Length: > 0 } id && id.All(char.IsAsciiDigit)
            ? id
            : throw Fail.Unknown("ResellerClub no devolvió el ID del contacto.");
    }

    private static List<(string, string)> Order(Session s, DomainName name, int years, string contact) =>
    [
        ("domain-name", name.Name), ("years", Years(years)), ("customer-id", s.Text("customer_id")),
        ("reg-contact-id", contact), ("admin-contact-id", contact), ("tech-contact-id", contact), ("billing-contact-id", contact),
        ("invoice-option", "NoInvoice"),
    ];

    /// <summary>Una acción con efecto: actionstatus Failed es un rechazo, con el motivo propio si se conoce.</summary>
    private static void Action(JsonObject json, string? reason)
    {
        if (json["actionstatus"]?.ToString() is "Failed" or "Error")
        {
            throw Fail.Rejected($"ResellerClub: {Fail.Short(json["actionstatusdesc"]?.ToString())}", reason);
        }
    }

    private async Task<JsonObject> Call(Session s, HttpMethod method, string path, List<(string, string)> args, bool mutating) =>
        await Send(s, method, path, args, mutating).ConfigureAwait(false) as JsonObject
            ?? throw (mutating ? Fail.Unknown("ResellerClub respondió algo inesperado. Puede haberse procesado.") : Fail.Unavailable("ResellerClub respondió algo inesperado."));

    /// <summary>Los parámetros van en la URL, como pide LogicBoxes. Las URLs nunca se registran en los logs.</summary>
    private static HttpRequestMessage Request(Session s, HttpMethod method, string path, List<(string, string)> args)
    {
        var all = new List<(string, string)> { ("auth-userid", s.Text("reseller_id")), ("api-key", s.Text("api_key")) };
        all.AddRange(args);
        var query = string.Join('&', all.Select(a => $"{Uri.EscapeDataString(a.Item1)}={Uri.EscapeDataString(a.Item2)}"));
        var host = s.Flag("sandbox") ? "https://test.httpapi.com/api/" : "https://httpapi.com/api/";
        return new HttpRequestMessage(method, new Uri(new Uri(host), $"{path}?{query}"));
    }

    private async Task<JsonNode?> Send(Session s, HttpMethod method, string path, List<(string, string)> args, bool mutating)
    {
        using var request = Request(s, method, path, args);
        var (status, body, _) = await http.Send(Name, request, mutating, s.Ct).ConfigureAwait(false);
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
        }

        if (json is JsonObject o && (o["status"]?.ToString() is { } st && st.Equals("ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            var message = Fail.Short((o["message"] ?? o["error"])?.ToString());
            var text = message.ToLowerInvariant();
            throw text switch
            {
                _ when text.Contains("auth", StringComparison.Ordinal) && !text.Contains("auth-code", StringComparison.Ordinal) || text.Contains("access denied", StringComparison.Ordinal)
                    || text.Contains("api-key", StringComparison.Ordinal) => Fail.Config($"ResellerClub rechazó las credenciales o la IP: {message}"),
                _ when text.Contains("doesn't exist", StringComparison.Ordinal) || text.Contains("does not exist", StringComparison.Ordinal)
                    || text.Contains("not found", StringComparison.Ordinal) => Fail.NotFound($"ResellerClub: {message}"),
                _ when text.Contains("already", StringComparison.Ordinal) || text.Contains("not available", StringComparison.Ordinal) => Fail.Rejected($"ResellerClub: {message}", "taken"),
                _ when text.Contains("auth-code", StringComparison.Ordinal) || text.Contains("authcode", StringComparison.Ordinal) => Fail.Rejected($"ResellerClub: {message}", "invalid_auth_code"),
                _ => Fail.Rejected($"ResellerClub: {message}", null),
            };
        }

        if (status >= 400 || json is null)
        {
            throw json is null && status < 400
                ? mutating ? Fail.Unknown("ResellerClub respondió algo que no es JSON. Puede haberse procesado.") : Fail.Unavailable("ResellerClub respondió algo que no es JSON.")
                : RegistrarHttp.Status(Name, status, mutating);
        }

        return json;
    }

    private static DateOnly? EndTime(JsonObject details) =>
        long.TryParse(details["endtime"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime)
            : null;

    private static string Years(int years) => years.ToString(CultureInfo.InvariantCulture);
}
