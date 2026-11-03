using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Registrars;

/// <summary>
/// OpenProvider, API REST v1beta con JSON. Se entra con usuario y contraseña y el token que devuelve se guarda como
/// estado de la instancia (sección 6 del protocolo): el núcleo lo guarda cifrado y lo devuelve en cada llamada. Cada
/// registro crea un handle de cliente con los datos del titular.
/// </summary>
internal sealed class OpenProvider(RegistrarHttp http) : Registrar
{
    /// <summary>OpenProvider da tokens por 48 horas: se renuevan antes, y ante un 401 se vuelve a entrar una vez.</summary>
    private static readonly TimeSpan TokenLife = TimeSpan.FromHours(24);

    public override string Id => "openprovider";

    public override string Name => "OpenProvider";

    /// <summary>Sin reenvío de correo: OpenProvider no lo tiene en su API de dominios.</summary>
    public override IReadOnlyList<string> Extras => ["registrar.transfer", "registrar.auth_code", "registrar.lock", "registrar.contacts", "registrar.pricing", "registrar.dns", "registrar.privacy"];

    public override JsonObject ConfigSchema => (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "required": ["username", "password"],
          "properties": {
            "username": { "type": "string" },
            "password": { "type": "string", "x-secret": true },
            "sandbox": { "type": "boolean", "description": "Usar el ambiente de prueba api.sandbox.openprovider.nl" }
          }
        }
        """)!;

    public override async Task Test(Session s)
    {
        s.NewState = null;
        await Login(s).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<Availability>> Check(Session s, IReadOnlyList<DomainName> names)
    {
        var body = new JsonObject
        {
            ["domains"] = new JsonArray([.. names.Select(n => (JsonNode)Domain(n))]),
            ["with_price"] = true,
        };
        var data = await Call(s, HttpMethod.Post, "domains/check", body, mutating: false).ConfigureAwait(false);
        var results = (data["results"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(r => r["domain"]?.ToString() ?? string.Empty, StringComparer.Ordinal);
        return [.. names.Select(n => results.TryGetValue(n.Name, out var r)
            ? new Availability(
                n,
                r["status"]?.ToString() switch { "free" => true, "active" or "reserved" => false, _ => null },
                r["is_premium"]?.GetValueKind() == JsonValueKind.True,
                r["is_premium"]?.GetValueKind() == JsonValueKind.True ? Money(r["premium"]?["price"]?["create"]) ?? Money(r["price"]?["product"]?["price"]) : null,
                r["price"]?["product"]?["currency"]?.ToString())
            : new Availability(n, null, Reason: "no_answer"))];
    }

    public override async Task<DomainState> Register(Session s, RegisterOrder order)
    {
        var handle = await Handle(s, order.Registrant).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["domain"] = Domain(order.Name),
            ["period"] = order.Years,
            ["owner_handle"] = handle,
            ["admin_handle"] = handle,
            ["tech_handle"] = handle,
            ["billing_handle"] = handle,
            ["name_servers"] = Servers(order.Nameservers),
            ["autorenew"] = "off",
            ["is_private_whois_enabled"] = order.Privacy,
        };
        var data = await Call(s, HttpMethod.Post, "domains", body, mutating: true).ConfigureAwait(false);
        return State(data, order.Nameservers);
    }

    public override async Task<DateOnly> Renew(Session s, DomainRef domain, int years, DateOnly currentExpiry)
    {
        var id = await DomainId(s, domain).ConfigureAwait(false);
        await Call(s, HttpMethod.Post, $"domains/{id}/renew", new JsonObject { ["domain"] = Domain(domain.Name), ["period"] = years }, mutating: true).ConfigureAwait(false);
        var after = await Call(s, HttpMethod.Get, $"domains/{id}", null, mutating: false).ConfigureAwait(false);
        return Date(after["expiration_date"]?.ToString()) ?? currentExpiry.AddYears(years);
    }

    public override async Task<DomainState> Info(Session s, DomainRef domain)
    {
        var found = await Find(s, domain.Name).ConfigureAwait(false);
        return found is null ? DomainState.NotFound : State(found, null);
    }

    public override async Task SetNameservers(Session s, DomainRef domain, IReadOnlyList<string> nameservers) =>
        await Call(s, HttpMethod.Put, $"domains/{await DomainId(s, domain).ConfigureAwait(false)}", new JsonObject { ["name_servers"] = Servers(nameservers) }, mutating: true).ConfigureAwait(false);

    public override async Task<DomainState> Transfer(Session s, TransferOrder order)
    {
        var handle = await Handle(s, order.Registrant).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["domain"] = Domain(order.Name),
            ["auth_code"] = order.AuthCode,
            ["period"] = order.Years,
            ["owner_handle"] = handle,
            ["admin_handle"] = handle,
            ["tech_handle"] = handle,
            ["billing_handle"] = handle,
            ["autorenew"] = "off",
        };
        if (order.Nameservers.Count > 0)
        {
            body["name_servers"] = Servers(order.Nameservers);
        }

        var data = await Call(s, HttpMethod.Post, "domains/transfer", body, mutating: true).ConfigureAwait(false);
        return State(data, order.Nameservers) is { Status: "active" } done ? done : new DomainState("pending_transfer", null, order.Nameservers, null, Ids(data));
    }

    public override async Task SetLock(Session s, DomainRef domain, bool locked) =>
        await Call(s, HttpMethod.Put, $"domains/{await DomainId(s, domain).ConfigureAwait(false)}", new JsonObject { ["is_locked"] = locked }, mutating: true).ConfigureAwait(false);

    public override async Task<string> AuthCode(Session s, DomainRef domain)
    {
        var data = await Call(s, HttpMethod.Get, $"domains/{await DomainId(s, domain).ConfigureAwait(false)}/authcode", null, mutating: false).ConfigureAwait(false);
        return data["auth_code"]?.ToString() is { Length: > 0 } code ? code : throw Fail.NotFound($"OpenProvider no dio el código de {domain.Name}.");
    }

    public override async Task SetContacts(Session s, DomainRef domain, Contact registrant)
    {
        var handle = await Handle(s, registrant).ConfigureAwait(false);
        var body = new JsonObject { ["owner_handle"] = handle, ["admin_handle"] = handle, ["tech_handle"] = handle, ["billing_handle"] = handle };
        await Call(s, HttpMethod.Put, $"domains/{await DomainId(s, domain).ConfigureAwait(false)}", body, mutating: true).ConfigureAwait(false);
    }

    /// <summary>La zona en el DNS de OpenProvider. Si todavía no existe, está vacía.</summary>
    public override async Task<IReadOnlyList<DnsRecord>> Dns(Session s, DomainRef domain) =>
        await Zone(s, domain.Name).ConfigureAwait(false) is { } records ? records : [];

    /// <summary>Un solo pedido con lo que se agrega y lo que se saca. Si la zona no existe, se crea con los registros.</summary>
    public override async Task SetDns(Session s, DomainRef domain, IReadOnlyList<DnsRecord> records)
    {
        var zone = domain.Name.Name;
        if (await Zone(s, domain.Name).ConfigureAwait(false) is not { } current)
        {
            var created = new JsonObject
            {
                ["domain"] = Domain(domain.Name),
                ["type"] = "master",
                ["records"] = new JsonArray([.. records.Select(r => (JsonNode)Wire(r))]),
            };
            await Call(s, HttpMethod.Post, "dns/zones", created, mutating: true).ConfigureAwait(false);
            return;
        }

        var (remove, add) = Diff(current, records);
        if (remove.Count + add.Count == 0)
        {
            return;
        }

        var body = new JsonObject
        {
            ["name"] = zone,
            ["records"] = new JsonObject
            {
                ["add"] = new JsonArray([.. add.Select(r => (JsonNode)Wire(r))]),
                ["remove"] = new JsonArray([.. remove.Select(r => (JsonNode)Wire(r))]),
            },
        };
        await Call(s, HttpMethod.Put, $"dns/zones/{Uri.EscapeDataString(zone)}", body, mutating: true).ConfigureAwait(false);
    }

    public override async Task SetPrivacy(Session s, DomainRef domain, bool enabled, string? registrantEmail) =>
        await Call(s, HttpMethod.Put, $"domains/{await DomainId(s, domain).ConfigureAwait(false)}", new JsonObject { ["is_private_whois_enabled"] = enabled }, mutating: true).ConfigureAwait(false);

    /// <summary>Precios de revendedor por TLD, para un año.</summary>
    public override async Task<IReadOnlyList<CostPrice>> Prices(Session s, string? currency)
    {
        var data = await Call(s, HttpMethod.Get, "tlds?with_price=true&limit=1000", null, mutating: false).ConfigureAwait(false);
        var rows = new List<CostPrice>();
        foreach (var t in (data["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var prices = t["prices"];
            var cur = prices?["create_price"]?["reseller"]?["currency"]?.ToString() ?? currency ?? "USD";
            if (t["name"]?.ToString() is { Length: > 0 } tld)
            {
                rows.Add(new CostPrice(tld.ToLowerInvariant(), cur, 1, Money(prices?["create_price"]?["reseller"]?["price"]), Money(prices?["renew_price"]?["reseller"]?["price"]), Money(prices?["transfer_price"]?["reseller"]?["price"])));
            }
        }

        return rows;
    }

    private static DomainState State(JsonObject d, IReadOnlyList<string>? nameservers) =>
        new(
            d["status"]?.ToString() switch
            {
                "ACT" => "active",
                "REQ" or "PEN" or "SCH" => "pending",
                "FAI" => "failed",
                "DEL" => "transferred_away",
                _ => "unknown",
            },
            Date(d["expiration_date"]?.ToString()),
            d["name_servers"] is JsonArray ns ? [.. ns.Select(n => n?["name"]?.ToString()).OfType<string>()] : nameservers ?? [],
            d["is_locked"] is JsonValue locked && locked.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? locked.GetValue<bool>() : null,
            Ids(d),
            d["is_private_whois_enabled"] is JsonValue privacy && privacy.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? privacy.GetValue<bool>() : null);

    private static JsonObject Ids(JsonObject d) => d["id"] is { } id ? new JsonObject { ["domain_id"] = id.ToString() } : [];

    /// <summary>Los registros de la zona, o null si la zona no existe.</summary>
    private async Task<List<DnsRecord>?> Zone(Session s, DomainName name)
    {
        JsonObject data;
        try
        {
            data = await Call(s, HttpMethod.Get, $"dns/zones/{Uri.EscapeDataString(name.Name)}?with_records=true", null, mutating: false).ConfigureAwait(false);
        }
        catch (ModuleException e) when (e.Code == ErrorCodes.NotFound)
        {
            return null;
        }

        return [.. (data["records"] as JsonArray ?? []).OfType<JsonObject>().Select(r => DnsRecords.Read(
            name.Name,
            r["name"]?.ToString(),
            r["type"]?.ToString(),
            r["value"]?.ToString(),
            r["ttl"] is JsonValue ttl && ttl.TryGetValue<int>(out var t) ? t : null,
            r["prio"] is JsonValue prio && prio.TryGetValue<int>(out var p) ? p : null)).OfType<DnsRecord>()];
    }

    /// <summary>Un registro como lo pide OpenProvider: nombre relativo, vacío para la zona, y la prioridad en prio.</summary>
    private static JsonObject Wire(DnsRecord r)
    {
        var json = new JsonObject { ["name"] = r.Name == "@" ? string.Empty : r.Name, ["type"] = r.Type, ["value"] = r.Value, ["ttl"] = r.Ttl };
        if (r.Priority is { } priority)
        {
            json["prio"] = priority;
        }

        return json;
    }

    private async Task<string> DomainId(Session s, DomainRef domain) =>
        domain.Id("domain_id") ?? (await Find(s, domain.Name).ConfigureAwait(false))?["id"]?.ToString() ?? throw Fail.NotFound($"OpenProvider no tiene {domain.Name}.");

    private async Task<JsonObject?> Find(Session s, DomainName name)
    {
        var data = await Call(s, HttpMethod.Get, $"domains?full_name={Uri.EscapeDataString(name.Name)}&limit=1", null, mutating: false).ConfigureAwait(false);
        return (data["results"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
    }

    /// <summary>Un handle de cliente con los datos del titular. El número de calle va aparte si el domicilio lo trae al final.</summary>
    private async Task<string> Handle(Session s, Contact c)
    {
        var (cc, number) = c.SplitPhone();
        var words = c.Address1.Trim().Split(' ');
        var hasNumber = words.Length > 1 && words[^1].Any(char.IsAsciiDigit);

        // ponytail: área de 2 dígitos para todos los países. OpenProvider concatena las partes y el registro ve el número entero.
        var body = new JsonObject
        {
            ["name"] = new JsonObject { ["first_name"] = c.FirstName, ["last_name"] = c.LastName },
            ["company_name"] = c.Company,
            ["email"] = c.Email,
            ["phone"] = new JsonObject { ["country_code"] = "+" + cc, ["area_code"] = number.Length > 6 ? number[..2] : string.Empty, ["subscriber_number"] = number.Length > 6 ? number[2..] : number },
            ["address"] = new JsonObject
            {
                ["street"] = hasNumber ? string.Join(' ', words[..^1]) : c.Address1,
                ["number"] = hasNumber ? words[^1] : string.Empty,
                ["city"] = c.City,
                ["state"] = c.Region,
                ["zipcode"] = c.PostalCode,
                ["country"] = c.Country,
            },
        };
        var data = await Call(s, HttpMethod.Post, "customers", body, mutating: true).ConfigureAwait(false);
        return data["handle"]?.ToString() is { Length: > 0 } handle ? handle : throw Fail.Unknown("OpenProvider no devolvió el handle del contacto.");
    }

    private static JsonObject Domain(DomainName name) => new() { ["name"] = name.Label, ["extension"] = name.Extension };

    private static JsonArray Servers(IEnumerable<string> nameservers) => new([.. nameservers.Select(n => (JsonNode)new JsonObject { ["name"] = n })]);

    private string Base(Session s) => s.Flag("sandbox") ? "https://api.sandbox.openprovider.nl/v1beta/" : "https://api.openprovider.eu/v1beta/";

    /// <summary>El token guardado si sigue vigente, o uno nuevo, que queda como estado nuevo de la instancia.</summary>
    private async Task<string> Token(Session s)
    {
        var state = s.NewState ?? s.State;
        if (state?["token"]?.ToString() is { Length: > 0 } token
            && DateTimeOffset.TryParse(state["obtained_at"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            && DateTimeOffset.UtcNow - at < TokenLife)
        {
            return token;
        }

        return await Login(s).ConfigureAwait(false);
    }

    private async Task<string> Login(Session s)
    {
        var body = new JsonObject { ["username"] = s.Text("username"), ["password"] = s.Text("password"), ["ip"] = "0.0.0.0" };
        var data = await Raw(s, HttpMethod.Post, "auth/login", body, null, mutating: false).ConfigureAwait(false);
        var token = data["token"]?.ToString() is { Length: > 0 } t ? t : throw Fail.Config("OpenProvider no devolvió un token.");
        s.NewState = new JsonObject { ["token"] = token, ["obtained_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        return token;
    }

    private async Task<JsonObject> Call(Session s, HttpMethod method, string path, JsonObject? body, bool mutating)
    {
        try
        {
            return await Raw(s, method, path, body, await Token(s).ConfigureAwait(false), mutating).ConfigureAwait(false);
        }
        catch (ModuleException e) when (e.Code == ErrorCodes.InvalidConfig && (s.NewState ?? s.State)?["token"] is not null && e.Details?["expired_token"] is not null)
        {
            // El token guardado venció antes de tiempo: se entra de nuevo una sola vez.
            s.NewState = null;
            return await Raw(s, method, path, body, await Login(s).ConfigureAwait(false), mutating).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject> Raw(Session s, HttpMethod method, string path, JsonObject? body, string? token, bool mutating)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(Base(s)), path));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var (status, text, _) = await http.Send(Name, request, mutating, s.Ct).ConfigureAwait(false);
        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
        }

        var code = json?["code"]?.ToString();
        if (status < 400 && code is null or "0")
        {
            return json?["data"] as JsonObject ?? [];
        }

        var message = Fail.Short(json?["desc"]?.ToString());
        if (status == 401 || code == "196")
        {
            // Credenciales o token: el detalle avisa que puede ser un token vencido.
            throw new ModuleException(ErrorCodes.InvalidConfig, $"OpenProvider rechazó la autenticación. {message}", details: new JsonObject { ["expired_token"] = token is not null });
        }

        if (json is null)
        {
            throw RegistrarHttp.Status(Name, status, mutating);
        }

        var lower = message.ToLowerInvariant();
        throw lower switch
        {
            _ when lower.Contains("not found", StringComparison.Ordinal) || lower.Contains("does not exist", StringComparison.Ordinal) => Fail.NotFound($"OpenProvider: {message}"),
            _ when lower.Contains("already exists", StringComparison.Ordinal) || lower.Contains("not available", StringComparison.Ordinal)
                || lower.Contains("not free", StringComparison.Ordinal) => Fail.Rejected($"OpenProvider: {message}", "taken", code),
            _ when lower.Contains("auth", StringComparison.Ordinal) && path.Contains("transfer", StringComparison.Ordinal) => Fail.Rejected($"OpenProvider: {message}", "invalid_auth_code", code),
            _ when status >= 500 => mutating ? Fail.Unknown($"OpenProvider respondió HTTP {status}. Puede haberse procesado.") : Fail.Unavailable($"OpenProvider respondió HTTP {status}."),
            _ => Fail.Rejected($"OpenProvider: {message}", null, code),
        };
    }
}
