using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.CPanel;

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class WhmConfig : PanelConfig
{
    public string? ApiUser { get; init; }

    public string? ApiToken { get; init; }
}

/// <summary>
/// Una cuenta de cPanel vista por la API 1 de WHM: POST a https://{host}:{port}/json-api/{función}?api.version=1 con
/// los parámetros en el cuerpo y el encabezado <c>Authorization: whm USUARIO:TOKEN</c>. Cada respuesta trae
/// <c>metadata.result</c> (1 o 0) y <c>metadata.reason</c>. cPanel no tiene dónde guardar una marca: la marca es el
/// usuario, que sale del uuid del servicio.
/// </summary>
internal sealed class WhmPanel(PanelTransport transport, PanelEndpoint endpoint, string apiUser, string apiToken, IReadOnlyList<string?> hidden) : IHostingPanel
{
    /// <summary>El puerto de cPanel con TLS, el del login del cliente y el del acceso directo.</summary>
    public const int CpanelPort = 2083;

    /// <summary>Textos con los que WHM rechaza el token o un privilegio que el token no tiene.</summary>
    private static readonly string[] DeniedTexts = ["access denied", "permission", "not allowed", "invalid api token"];

    public string UsernamePrefix => endpoint.UsernamePrefix;

    public Uri LoginUrl => endpoint.Url("/", CpanelPort);

    public bool KeepsMarker => false;

    public static WhmPanel Open(ModuleCall call, Func<string?, HttpClient> http, Budget budget, ILogger logger, IReadOnlyList<string?> hidden)
    {
        var config = Settings.Read<WhmConfig>(call);
        var bad = new List<string>();
        var endpoint = Settings.Endpoint(config, 2087, bad);

        // cPanel no acepta usuarios que empiezan con test.
        if (endpoint.UsernamePrefix.StartsWith("test", StringComparison.Ordinal))
        {
            bad.Add("username_prefix");
        }

        var user = config.ApiUser ?? "root";
        if (!Settings.IsToken(user) || user.Contains(':', StringComparison.Ordinal))
        {
            bad.Add("api_user");
        }

        if (!Settings.IsToken(config.ApiToken))
        {
            bad.Add("api_token");
        }

        return bad.Count > 0
            ? throw Fail.InvalidConfig(bad)
            : new WhmPanel(new PanelTransport(http(endpoint.Fingerprint), budget, logger, "WHM"), endpoint, user, config.ApiToken!, hidden);
    }

    public async Task<IReadOnlyList<string>> Plans() =>
        await Query("listpkgs").ConfigureAwait(false) is { } data && data["pkg"] is JsonArray packages
            ? [.. packages.Select(p => p.Field("name").Text()).OfType<string>()]
            : throw Unreadable("listpkgs");

    /// <summary>
    /// La cuenta por su usuario exacto en listaccts. Ausente es una lista que WHM devolvió bien y que no la tiene. El
    /// dominio es el principal de la cuenta, el que se creó con ella.
    /// </summary>
    public async Task<AccountState> Read(PanelAccount account)
    {
        var row = await Account(account.Username).ConfigureAwait(false);
        return row is null
            ? AccountState.Absent
            : new AccountState
            {
                Exists = true,
                OwnsDomain = string.Equals(row["domain"].Text(), account.Domain, StringComparison.OrdinalIgnoreCase),
                Plan = row["plan"].Text(),
                Suspended = row["suspended"].Text() is "1" or "true",
            };
    }

    /// <summary>El dueño de cualquier dominio del servidor: principal, adicional, estacionado o subdominio.</summary>
    public async Task<string?> DomainOwner(string domain)
    {
        var owner = (await Query("getdomainowner", ("domain", domain)).ConfigureAwait(false))["user"].Text();
        return string.IsNullOrEmpty(owner) ? null : Names.IsUsername(owner) ? owner : throw Unreadable("getdomainowner");
    }

    public Task<Ack> Create(NewAccount account) => Mutate(
        "createacct",
        ("username", account.Username),
        ("domain", account.Domain),
        ("plan", account.Plan),
        ("password", account.Password),
        ("contactemail", account.Email));

    public Task<Ack> Suspend(PanelAccount account) => Mutate("suspendacct", ("user", account.Username));

    public Task<Ack> Unsuspend(PanelAccount account) => Mutate("unsuspendacct", ("user", account.Username));

    public Task<Ack> Delete(PanelAccount account) => Mutate("removeacct", ("username", account.Username));

    public Task<Ack> ChangePlan(PanelAccount account, string plan) => Mutate("changepackage", ("user", account.Username), ("pkg", plan));

    public Task<Ack> ChangePassword(PanelAccount account, string password) => Mutate("passwd", ("user", account.Username), ("password", password));

    /// <summary>Una sesión de cPanel de un solo uso. La URL lleva la sesión: nunca va a los logs.</summary>
    public async Task<SsoLink> Sso(PanelAccount account, string? clientIp)
    {
        var data = await Query("create_user_session", ("user", account.Username), ("service", "cpaneld")).ConfigureAwait(false);
        if (!Uri.TryCreate(data["url"].Text(), UriKind.Absolute, out var url))
        {
            throw Unreadable("create_user_session");
        }

        return new SsoLink(url, data["expires"].Long() is { } expires ? DateTimeOffset.FromUnixTimeSeconds(expires) : null);
    }

    /// <summary>El disco de listaccts y la transferencia del mes de showbw. WHM da el disco en MiB con una M y la transferencia en bytes.</summary>
    public async Task<PanelUsage> Usage(PanelAccount account)
    {
        var row = await Account(account.Username).ConfigureAwait(false) ?? throw Fail.NotFound("La cuenta no existe en WHM.");
        var bandwidth = await Query("showbw", ("searchtype", "user"), ("search", account.Username)).ConfigureAwait(false);
        var usage = (bandwidth["acct"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(r => string.Equals(r["user"].Text(), account.Username, StringComparison.OrdinalIgnoreCase));
        return new PanelUsage(
            JsonRead.Mebibytes(row["diskused"].Text()),
            usage?["totalbytes"].Long() is { } bytes and >= 0 ? bytes / JsonRead.MiB : null);
    }

    private async Task<JsonObject?> Account(string username)
    {
        var data = await Query("listaccts", ("searchtype", "user"), ("search", username), ("searchmethod", "exact")).ConfigureAwait(false);
        return data["acct"] is JsonArray accounts && accounts.All(a => a is JsonObject)
            ? accounts.OfType<JsonObject>().FirstOrDefault(a => string.Equals(a["user"].Text(), username, StringComparison.OrdinalIgnoreCase))
            : throw Unreadable("listaccts");
    }

    /// <summary>Una lectura. Lo que no es un resultado 1 con datos no prueba nada: no disponible.</summary>
    private async Task<JsonObject> Query(string function, params (string Name, string Value)[] args)
    {
        var reply = await transport.Read(function, () => Request(function, args)).ConfigureAwait(false);
        var (result, reason, data) = Parse(reply);
        if (result == 1 && reply.Status is >= 200 and < 300)
        {
            return data ?? [];
        }

        throw result is null
            ? Unreadable(function)
            : Fail.Unavailable($"WHM respondió un error a {function}: {reason}", "upstream_error", reason);
    }

    /// <summary>
    /// Un cambio. Solo un resultado 1 es éxito, pero ni ese ni el error alcanzan: después se vuelve a leer. Un 5xx o un
    /// cuerpo que no es de WHM pueden venir de algo intermedio con la función todavía corriendo: quedan sin respuesta.
    /// </summary>
    private async Task<Ack> Mutate(string function, params (string Name, string Value)[] args)
    {
        var reply = await transport.Mutate(function, () => Request(function, args)).ConfigureAwait(false);
        if (reply is null)
        {
            return Ack.NoReply;
        }

        var (result, reason, _) = Parse(reply);
        return result switch
        {
            1 when reply.Status is >= 200 and < 300 => Ack.Done,
            0 when reply.Status < 500 => Ack.Refused(reason ?? "WHM rechazó el cambio sin texto"),
            _ => Ack.NoReply,
        };
    }

    private HttpRequestMessage Request(string function, (string Name, string Value)[] args)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder(endpoint.Url($"/json-api/{function}")) { Query = "api.version=1" }.Uri)
        {
            Content = new FormUrlEncodedContent(args.Select(a => KeyValuePair.Create(a.Name, a.Value))),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"whm {apiUser}:{apiToken}");
        return request;
    }

    /// <summary>
    /// El resultado, el texto y los datos de una respuesta. Un rechazo del token o de un privilegio es configuración,
    /// también en un cambio: no llegó a correr.
    /// </summary>
    private (int? Result, string? Reason, JsonObject? Data) Parse(PanelReply reply)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(reply.Body);
        }
        catch (JsonException)
        {
            body = null;
        }

        var reason = body.Field("metadata").Field("reason").Text() ?? body.Field("cpanelresult").Field("error").Text();
        var described = Upstream.Describe(reply.Status, reason ?? (body is null ? reply.Body : null), hidden.Append(apiToken));
        var result = body.Field("metadata").Field("result").Long() is { } r ? (int)r : (int?)null;
        if (reply.Status is 401 or 403 || (result != 1 && reason is not null && DeniedTexts.Any(t => reason.Contains(t, StringComparison.OrdinalIgnoreCase))))
        {
            throw Fail.InvalidConfig(
                ["api_user", "api_token"],
                "WHM rechazó el pedido a la API. Revisar el token, sus privilegios y que la IP del módulo esté permitida en WHM.",
                described);
        }

        return (result, described, body.Field("data") as JsonObject);
    }

    private static ModuleException Unreadable(string function) =>
        Fail.Unavailable(string.Create(CultureInfo.InvariantCulture, $"WHM respondió algo que no se puede leer a {function}."), "unreadable");
}
