using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Registrars;

/// <summary>
/// Las operaciones del tipo registrar (sección 9.2 del protocolo) sobre un <see cref="Registrar"/>: valida la entrada,
/// arma las respuestas y resuelve lo que es igual para todos, como la marca de idempotencia del registro y la consulta
/// de las transferencias en curso.
/// </summary>
internal static class RegistrarModule
{
    public const int MaxNames = 50;

    /// <summary>Una transferencia tarda hasta una semana: se consulta cada hora.</summary>
    public const int TransferPollSeconds = 3600;

    public const int RegisterPollSeconds = 300;

    public static ModuleDefinition Create(string registrar, HttpClient http, ILogger logger)
    {
        var client = new RegistrarHttp(http, logger);
        Registrar adapter = registrar switch
        {
            "internetbs" => new InternetBs(client),
            "resellerclub" => new ResellerClub(client),
            "openprovider" => new OpenProvider(client),
            "namecheap" => new Namecheap(client),
            _ => throw new InvalidOperationException($"Registrar desconocido: {registrar}. Puede ser internetbs, resellerclub, openprovider o namecheap."),
        };
        return Create(adapter);
    }

    public static ModuleDefinition Create(Registrar r) =>
        new ModuleDefinition(Manifest(r))
            .Operation("registrar", "check", c => Check(r, c), mutating: false)
            .Operation("registrar", "get_domain", c => GetDomain(r, c), mutating: false)
            .Operation("registrar", "get_auth_code", c => AuthCode(r, c), mutating: false)
            .Operation("registrar", "get_prices", c => Prices(r, c), mutating: false)
            .Operation("registrar", "register", c => Register(r, c))
            .Operation("registrar", "transfer", c => Transfer(r, c))
            .Operation("registrar", "renew", c => Renew(r, c))
            .Operation("registrar", "set_nameservers", c => SetNameservers(r, c))
            .Operation("registrar", "set_lock", c => SetLock(r, c))
            .Operation("registrar", "set_contacts", c => SetContacts(r, c))
            .Operation("registrar", "get_dns", c => GetDns(r, c), mutating: false)
            .Operation("registrar", "set_dns", c => SetDns(r, c))
            .Operation("registrar", "get_email_forwards", c => GetForwards(r, c), mutating: false)
            .Operation("registrar", "set_email_forwards", c => SetForwards(r, c))
            .Operation("registrar", "set_privacy", c => SetPrivacy(r, c))
            .InstanceTest(c => Test(r, c))
            .Poll((c, id) => Poll(r, c, id));

    public static JsonObject Manifest(Registrar r) => new()
    {
        ["protocol"] = new JsonArray("0"),
        ["id"] = r.Id,
        ["name"] = r.Name,
        ["version"] = "0.1.0",
        ["vendor"] = "Hornero",
        ["license"] = "Apache-2.0",
        ["kinds"] = new JsonArray("registrar"),
        ["capabilities"] = new JsonArray([.. r.Extras.Prepend("registrar.core").Append("instance_state").Distinct().Select(c => (JsonNode)c)]),
        ["config_schema"] = r.ConfigSchema.DeepClone(),
        ["data_scopes"] = new JsonArray(),
        // Cambiar la zona puede ser un pedido por registro en los registrars que no la reemplazan entera.
        ["timeouts"] = new JsonObject { ["register"] = 30, ["transfer"] = 30, ["renew"] = 30, ["set_dns"] = 120, ["set_email_forwards"] = 60 },

        // Una transferencia la decide el registro de origen, hasta en 5 días hábiles. Un registro no inmediato, en horas.
        ["deadlines"] = new JsonObject { ["transfer"] = 14 * 86400, ["register"] = 3 * 86400 },
        ["max_in_flight"] = 4,
    };

    private static async Task<ModuleResponse> Test(Registrar r, ModuleCall call)
    {
        var s = new Session(call);
        await r.Test(s).ConfigureAwait(false);
        return Done(s, new JsonObject { ["ok"] = true });
    }

    private static async Task<ModuleResponse> Check(Registrar r, ModuleCall call)
    {
        var raw = call.Request.Input["names"] as JsonArray ?? [];
        if (raw.Count is 0 or > MaxNames)
        {
            throw Fail.Input($"names lleva entre 1 y {MaxNames} nombres.", "names");
        }

        var names = raw.Select(n => Name(n?.ToString(), "names")).Distinct().ToList();
        var s = new Session(call);
        var results = new JsonArray();
        foreach (var a in await r.Check(s, names).ConfigureAwait(false))
        {
            var row = new JsonObject { ["name"] = a.Name.Name, ["available"] = a.Available, ["premium"] = a.Premium };
            if (a.PremiumPrice is { } price)
            {
                row["price"] = new JsonObject { ["value"] = price.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = a.Currency };
            }

            if (a.Reason is not null)
            {
                row["reason"] = a.Reason;
            }

            results.Add(row);
        }

        return Done(s, new JsonObject { ["results"] = results });
    }

    /// <summary>
    /// Registro con marca de idempotencia (punto 4 de la sección 6): si el registrar dice que el nombre está tomado,
    /// el dominio ya figura en la cuenta y vence justo lo que pide el pedido contado desde hoy, es la respuesta de un
    /// registro anterior que se perdió. La cuenta del revendedor es de todos sus clientes: un dominio de otro cliente que
    /// ya está en ella vence otro día y sigue tomado.
    /// </summary>
    private static async Task<ModuleResponse> Register(Registrar r, ModuleCall call)
    {
        var input = call.Request.Input;
        var name = Name(input["name"]?.ToString(), "name");
        var order = new RegisterOrder(name, Years(input), Nameservers(input, required: true), Registrant(call), input["privacy"]?.GetValueKind() == JsonValueKind.True);
        var s = new Session(call);
        DomainState state;
        try
        {
            state = await r.Register(s, order).ConfigureAwait(false);
        }
        catch (ModuleException e) when (e.Code == ErrorCodes.Rejected && Reason(e) == "taken")
        {
            state = await r.Info(s, new DomainRef(name, [])).ConfigureAwait(false);
            // Un día de margen: el registro cuenta el vencimiento en su huso y la repetición puede llegar pasada la medianoche.
            var expected = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(order.Years);
            if (state.Status is not ("active" or "pending") || state.ExpiresOn is not { } expires || Math.Abs(expires.DayNumber - expected.DayNumber) > 1)
            {
                throw;
            }
        }

        return state.Status == "pending"
            ? Pending(s, "register", name, RegisterPollSeconds)
            : Done(s, Result(state));
    }

    private static async Task<ModuleResponse> Transfer(Registrar r, ModuleCall call)
    {
        var input = call.Request.Input;
        var name = Name(input["name"]?.ToString(), "name");
        var code = input["auth_code"]?.ToString();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 255)
        {
            throw Fail.Input("Falta el código de autorización (EPP) del dominio.", "auth_code");
        }

        var order = new TransferOrder(name, code.Trim(), input["years"] is null ? 1 : Years(input), Nameservers(input, required: false), Registrant(call));
        var s = new Session(call);
        var state = await r.Transfer(s, order).ConfigureAwait(false);
        return state.Status == "active" ? Done(s, Result(state)) : Pending(s, "transfer", name, TransferPollSeconds);
    }

    private static async Task<ModuleResponse> Renew(Registrar r, ModuleCall call)
    {
        var input = call.Request.Input;
        if (!DateOnly.TryParseExact(input["current_expiry"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var current))
        {
            throw Fail.Input("Falta current_expiry, el vencimiento que se renueva.", "current_expiry");
        }

        var s = new Session(call);
        var domain = Domain(call);
        var years = Years(input);

        // Marca externa de la renovación (punto 4 de la sección 6): si el registro ya vence después del período pedido,
        // una llamada anterior renovó y se perdió su respuesta. Así no se renueva dos veces aunque falte el registro.
        if ((await r.Info(s, domain).ConfigureAwait(false)).ExpiresOn is { } known && known >= current.AddYears(years))
        {
            return Done(s, new JsonObject { ["expires_on"] = Iso(known) });
        }

        var expires = await r.Renew(s, domain, years, current).ConfigureAwait(false);
        return Done(s, new JsonObject { ["expires_on"] = Iso(expires) });
    }

    private static async Task<ModuleResponse> GetDomain(Registrar r, ModuleCall call)
    {
        var s = new Session(call);
        var state = await r.Info(s, Domain(call)).ConfigureAwait(false);
        var result = Result(state);
        result["status"] = state.Status;
        result["source"] = "registrar";
        if (state.Locked is { } locked)
        {
            result["locked"] = locked;
        }

        if (state.Privacy is { } privacy)
        {
            result["privacy"] = privacy;
        }

        return Done(s, result);
    }

    private static async Task<ModuleResponse> GetDns(Registrar r, ModuleCall call)
    {
        var s = new Session(call);
        var records = await r.Dns(s, Domain(call)).ConfigureAwait(false);
        return Done(s, new JsonObject { ["records"] = JsonSerializer.SerializeToNode(Sorted(records), ModuleJson.Options) });
    }

    /// <summary>La zona entera: lo que no viene se saca. Así repetir el pedido deja lo mismo (sección 9.2).</summary>
    private static async Task<ModuleResponse> SetDns(Registrar r, ModuleCall call)
    {
        var domain = Domain(call);
        var raw = call.Request.Input["records"] as JsonArray ?? throw Fail.Input("Faltan los registros (records).", "records");
        List<DnsRecord> typed;
        try
        {
            typed = raw.Deserialize<List<DnsRecord>>(ModuleJson.Options) ?? [];
        }
        catch (JsonException)
        {
            throw Fail.Input("Los registros no tienen la forma del protocolo.", "records");
        }

        if (!DnsRecords.TryNormalize(domain.Name.Name, typed, out var records, out var error))
        {
            throw Fail.Input(error, "records");
        }

        var s = new Session(call);
        await r.SetDns(s, domain, records).ConfigureAwait(false);
        return Done(s, new JsonObject { ["records"] = JsonSerializer.SerializeToNode(records, ModuleJson.Options) });
    }

    private static async Task<ModuleResponse> GetForwards(Registrar r, ModuleCall call)
    {
        var s = new Session(call);
        var forwards = await r.Forwards(s, Domain(call)).ConfigureAwait(false);
        return Done(s, new JsonObject { ["forwards"] = JsonSerializer.SerializeToNode(forwards.OrderBy(f => f.Mailbox, StringComparer.Ordinal).ToList(), ModuleJson.Options) });
    }

    private static async Task<ModuleResponse> SetForwards(Registrar r, ModuleCall call)
    {
        var raw = call.Request.Input["forwards"] as JsonArray ?? throw Fail.Input("Faltan los reenvíos (forwards).", "forwards");
        List<EmailForward> typed;
        try
        {
            typed = raw.Deserialize<List<EmailForward>>(ModuleJson.Options) ?? [];
        }
        catch (JsonException)
        {
            throw Fail.Input("Los reenvíos no tienen la forma del protocolo.", "forwards");
        }

        if (!EmailForwards.TryNormalize(typed, out var forwards, out var error))
        {
            throw Fail.Input(error, "forwards");
        }

        var s = new Session(call);
        await r.SetForwards(s, Domain(call), forwards).ConfigureAwait(false);
        return Done(s, new JsonObject { ["forwards"] = JsonSerializer.SerializeToNode(forwards, ModuleJson.Options) });
    }

    private static async Task<ModuleResponse> SetPrivacy(Registrar r, ModuleCall call)
    {
        if (call.Request.Input["enabled"] is not JsonValue v || v.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Fail.Input("enabled tiene que ser true o false.", "enabled");
        }

        var enabled = v.GetValue<bool>();
        var s = new Session(call);
        await r.SetPrivacy(s, Domain(call), enabled, call.Request.Context["contacts"]?["registrant"]?["email"]?.ToString()).ConfigureAwait(false);
        return Done(s, new JsonObject { ["enabled"] = enabled });
    }

    private static List<DnsRecord> Sorted(IEnumerable<DnsRecord> records) =>
        [.. records.Distinct().OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Value, StringComparer.Ordinal)];

    private static async Task<ModuleResponse> SetNameservers(Registrar r, ModuleCall call)
    {
        var nameservers = Nameservers(call.Request.Input, required: true);
        var s = new Session(call);
        await r.SetNameservers(s, Domain(call), nameservers).ConfigureAwait(false);
        return Done(s, new JsonObject { ["nameservers"] = Array(nameservers) });
    }

    private static async Task<ModuleResponse> SetLock(Registrar r, ModuleCall call)
    {
        if (call.Request.Input["locked"] is not JsonValue v || v.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Fail.Input("locked tiene que ser true o false.", "locked");
        }

        var locked = v.GetValue<bool>();
        var s = new Session(call);
        await r.SetLock(s, Domain(call), locked).ConfigureAwait(false);
        return Done(s, new JsonObject { ["locked"] = locked });
    }

    /// <summary>El código va en result.secrets: la SDK lo oculta en los logs y el núcleo no lo guarda (sección 6).</summary>
    private static async Task<ModuleResponse> AuthCode(Registrar r, ModuleCall call)
    {
        var s = new Session(call);
        var code = await r.AuthCode(s, Domain(call)).ConfigureAwait(false);
        return Done(s, new JsonObject { ["secrets"] = new JsonObject { ["auth_code"] = code } });
    }

    private static async Task<ModuleResponse> SetContacts(Registrar r, ModuleCall call)
    {
        var registrant = Registrant(call);
        var s = new Session(call);
        await r.SetContacts(s, Domain(call), registrant).ConfigureAwait(false);
        return Done(s, new JsonObject());
    }

    private static async Task<ModuleResponse> Prices(Registrar r, ModuleCall call)
    {
        var s = new Session(call);
        var rows = new JsonArray();
        foreach (var p in await r.Prices(s, call.Request.Input["currency"]?.ToString()).ConfigureAwait(false))
        {
            rows.Add(new JsonObject
            {
                ["tld"] = "." + p.Tld.TrimStart('.'),
                ["currency"] = p.Currency,
                ["years"] = p.Years,
                ["register"] = Decimal(p.Register),
                ["renew"] = Decimal(p.Renew),
                ["transfer"] = Decimal(p.Transfer),
            });
        }

        return Done(s, new JsonObject { ["prices"] = rows });
    }

    /// <summary>
    /// Consulta de un registro o una transferencia en curso. El módulo no guarda nada: el dominio sale del contexto
    /// que manda el núcleo y la operación termina cuando aparece activo en la cuenta.
    /// </summary>
    private static async Task<ModuleResponse> Poll(Registrar r, ModuleCall call, string operationId)
    {
        var kind = operationId.Split('-')[0];
        if (kind is not ("register" or "transfer"))
        {
            throw Fail.NotFound($"No existe la operación {operationId}.");
        }

        var s = new Session(call);
        var state = await r.Info(s, Domain(call)).ConfigureAwait(false);
        return state.Status switch
        {
            "active" => Done(s, Result(state)),
            "failed" or "transferred_away" => throw Fail.Rejected(
                kind == "transfer" ? $"La transferencia de {Domain(call).Name} no se completó." : $"El registro de {Domain(call).Name} no se completó.",
                "failed"),
            _ => Pending(s, kind, Domain(call).Name, kind == "transfer" ? TransferPollSeconds : RegisterPollSeconds),
        };
    }

    private static ModuleResponse Done(Session s, JsonObject result) => ModuleResponse.Completed(result, s.NewState);

    /// <summary>El ID de la operación solo dice qué se espera: el dominio llega en el contexto de cada consulta.</summary>
    private static ModuleResponse Pending(Session s, string kind, DomainName name, int retryAfter) =>
        new(ModuleResponse.PendingStatus, InstanceState: s.NewState, OperationId: $"{kind}-{name.Name.Replace('.', '_')}", RetryAfter: retryAfter);

    private static JsonObject Result(DomainState state)
    {
        var result = new JsonObject
        {
            ["status"] = state.Status,
            ["nameservers"] = Array(state.Nameservers),
            ["external_ids"] = state.ExternalIds.DeepClone(),
        };
        if (state.ExpiresOn is { } expires)
        {
            result["expires_on"] = Iso(expires);
        }

        return result;
    }

    private static DomainRef Domain(ModuleCall call)
    {
        var domain = call.Request.Context["domain"] as JsonObject ?? throw Fail.Input("Falta context.domain.", "context.domain");
        return new DomainRef(Name(domain["name"]?.ToString(), "context.domain.name"), domain["external_ids"] as JsonObject ?? []);
    }

    private static DomainName Name(string? text, string field) =>
        DomainNames.TryNormalize(text, out var name) ? new DomainName(name) : throw Fail.Input($"{text} no es un nombre de dominio válido.", field);

    private static int Years(JsonObject input) =>
        input["years"] is JsonValue v && v.TryGetValue<int>(out var years) && years is >= 1 and <= 10
            ? years
            : throw Fail.Input("years va de 1 a 10.", "years");

    /// <summary>Entre 2 y 13 nombres de host válidos, sin repetir. Sin required, puede faltar.</summary>
    private static List<string> Nameservers(JsonObject input, bool required)
    {
        if (input["nameservers"] is not JsonArray list)
        {
            return required ? throw Fail.Input("Faltan los nameservers.", "nameservers") : [];
        }

        var hosts = list.Select(n => DomainNames.TryNormalize(n?.ToString(), out var host) ? host : throw Fail.Input($"{n} no es un nameserver válido.", "nameservers"))
            .Distinct().ToList();
        return hosts.Count is >= 2 and <= 13 ? hosts : throw Fail.Input("Van entre 2 y 13 nameservers.", "nameservers");
    }

    /// <summary>El titular de context.contacts.registrant, con los datos que pide cualquier registro.</summary>
    private static Contact Registrant(ModuleCall call)
    {
        if (call.Request.Context["contacts"]?["registrant"] is not JsonObject node)
        {
            throw Fail.Input("Falta el titular en context.contacts.registrant.", "contacts.registrant");
        }

        string[] required = ["first_name", "last_name", "email", "phone", "address1", "city", "postal_code", "country"];
        var missing = required.Where(f => string.IsNullOrWhiteSpace(node[f]?.ToString())).Select(f => "contacts.registrant." + f).ToArray();
        if (missing.Length > 0)
        {
            throw Fail.Input("Faltan datos del titular del dominio.", missing);
        }

        if (node["phone"]!.ToString() is not ['+', .. var digits] || !digits.Contains('.', StringComparison.Ordinal))
        {
            throw Fail.Input("El teléfono va como +código.número, por ejemplo +54.1155551234.", "contacts.registrant.phone");
        }

        return node.Deserialize<Contact>(ModuleJson.Options)!;
    }

    private static string? Reason(ModuleException e) => e.Details?["upstream"]?["reason"]?.ToString();

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)i)]);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Decimal(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture);
}
