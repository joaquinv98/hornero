using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using BillingOss.Modules.Hosting;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Form = System.Collections.Generic.Dictionary<string, Microsoft.Extensions.Primitives.StringValues>;

namespace BillingOss.Module.DirectAdmin;

/// <summary>Configuración de la instancia (config_schema del manifiesto).</summary>
internal sealed class DirectAdminConfig : PanelConfig
{
    public string? ApiUser { get; init; }

    public string? ApiKey { get; init; }

    public string? IpAddress { get; init; }
}

/// <summary>
/// Un usuario de DirectAdmin visto por su API de comandos: https://{host}:{port}/CMD_API_..., por GET para leer y por POST
/// con el formulario en el cuerpo para cambiar, con autenticación básica del administrador y su clave de acceso. Las
/// respuestas vienen codificadas como formulario: los cambios traen <c>error</c> en 0 o en 1 con <c>text</c> y
/// <c>details</c>, y las lecturas traen sus datos o una lista en <c>list[]</c>. DirectAdmin no tiene dónde guardar una
/// marca: la marca es el usuario, que sale del uuid del servicio.
/// </summary>
internal sealed class DirectAdminPanel(PanelTransport transport, PanelEndpoint endpoint, string apiUser, string apiKey, string ip, IReadOnlyList<string?> hidden) : IHostingPanel
{
    private const string ListKey = "list[]";

    /// <summary>Textos con los que DirectAdmin rechaza la clave, la IP o un comando que la clave no puede correr.</summary>
    private static readonly string[] DeniedTexts = ["access denied", "do not have access", "not allowed", "unauthorized", "invalid login", "blacklisted"];

    public string UsernamePrefix => endpoint.UsernamePrefix;

    public Uri LoginUrl => endpoint.Url("/");

    public bool KeepsMarker => false;

    private IEnumerable<string?> Secrets => hidden.Append(apiKey);

    public static DirectAdminPanel Open(ModuleCall call, Func<string?, HttpClient> http, Budget budget, ILogger logger, IReadOnlyList<string?> hidden)
    {
        var config = Settings.Read<DirectAdminConfig>(call);
        var bad = new List<string>();
        var endpoint = Settings.Endpoint(config, 2222, bad);

        // ':' separa la clave en la autenticación básica y '|' es el "entrar como" del acceso directo.
        var user = config.ApiUser ?? "admin";
        if (!Settings.IsToken(user) || user.Contains(':', StringComparison.Ordinal) || user.Contains('|', StringComparison.Ordinal))
        {
            bad.Add("api_user");
        }

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
            : new DirectAdminPanel(new PanelTransport(http(endpoint.Fingerprint), budget, logger, "DirectAdmin"), endpoint, user, config.ApiKey!, config.IpAddress!, hidden);
    }

    public async Task<IReadOnlyList<string>> Plans() => List(await Query("CMD_API_PACKAGES_USER").ConfigureAwait(false), "CMD_API_PACKAGES_USER");

    /// <summary>
    /// El usuario exacto en la lista de todos los usuarios. Ausente es una lista que DirectAdmin devolvió bien y que no lo
    /// tiene. El dominio es el principal del usuario, el que se creó con él.
    /// </summary>
    public async Task<AccountState> Read(PanelAccount account)
    {
        var users = List(await Query("CMD_API_SHOW_ALL_USERS").ConfigureAwait(false), "CMD_API_SHOW_ALL_USERS");
        if (!users.Contains(account.Username, StringComparer.OrdinalIgnoreCase))
        {
            return AccountState.Absent;
        }

        var config = await Query("CMD_API_SHOW_USER_CONFIG", ("user", account.Username)).ConfigureAwait(false);
        var suspended = Value(config, "suspended");
        return Value(config, "domain") is not { Length: > 0 } domain || suspended is not ("yes" or "no")
            ? throw Unreadable("CMD_API_SHOW_USER_CONFIG")
            : new AccountState
            {
                Exists = true,
                OwnsDomain = string.Equals(Names.Domain(domain), account.Domain, StringComparison.OrdinalIgnoreCase),
                Plan = Value(config, "package"),
                Suspended = suspended == "yes",
            };
    }

    /// <summary>El dueño de cualquier dominio del servidor, principal o adicional, de la lista completa de dominios y dueños.</summary>
    public async Task<string?> DomainOwner(string domain)
    {
        var owners = await Query("CMD_API_DOMAIN_OWNERS").ConfigureAwait(false);

        // Cada clave es un dominio y su valor el usuario. Una respuesta con otra forma no prueba que el dominio esté libre.
        if (owners.Any(o => Names.Domain(o.Key) is null || !Names.IsUsername(o.Value.ToString())))
        {
            throw Unreadable("CMD_API_DOMAIN_OWNERS");
        }

        return owners.Where(o => Names.Domain(o.Key) == domain).Select(o => o.Value.ToString()).FirstOrDefault();
    }

    public Task<Ack> Create(NewAccount account) => Mutate(
        "CMD_API_ACCOUNT_USER",
        ("action", "create"),
        ("add", "Submit"),
        ("username", account.Username),
        ("email", account.Email),
        ("passwd", account.Password),
        ("passwd2", account.Password),
        ("domain", account.Domain),
        ("package", account.Plan),
        ("ip", ip),
        ("notify", "no"));

    public Task<Ack> Suspend(PanelAccount account) =>
        Mutate("CMD_API_SELECT_USERS", ("location", "CMD_SELECT_USERS"), ("suspend", "Suspend"), ("select0", account.Username));

    public Task<Ack> Unsuspend(PanelAccount account) =>
        Mutate("CMD_API_SELECT_USERS", ("location", "CMD_SELECT_USERS"), ("suspend", "Unsuspend"), ("select0", account.Username));

    public Task<Ack> Delete(PanelAccount account) =>
        Mutate("CMD_API_SELECT_USERS", ("confirmed", "Confirm"), ("delete", "yes"), ("select0", account.Username));

    public Task<Ack> ChangePlan(PanelAccount account, string plan) =>
        Mutate("CMD_API_MODIFY_USER", ("action", "package"), ("user", account.Username), ("package", plan));

    public Task<Ack> ChangePassword(PanelAccount account, string password) =>
        Mutate("CMD_API_USER_PASSWD", ("username", account.Username), ("passwd", password), ("passwd2", password));

    /// <summary>
    /// Una URL de un solo uso, que el administrador pide entrando como el usuario ("admin|usuario"). La URL lleva la
    /// sesión: nunca va a los logs.
    /// </summary>
    public async Task<SsoLink> Sso(PanelAccount account, string? clientIp)
    {
        var data = await Read("CMD_API_LOGIN_KEYS", HttpMethod.Post, account.Username, [("action", "create"), ("type", "one_time_url")]).ConfigureAwait(false);
        var text = Value(data, "url") ?? Value(data, "details");
        return Uri.TryCreate(text, UriKind.Absolute, out var url) ? new SsoLink(url, null) : throw Unreadable("CMD_API_LOGIN_KEYS");
    }

    /// <summary>El disco y la transferencia del mes de CMD_API_SHOW_USER_USAGE, que DirectAdmin da en MB con decimales.</summary>
    public async Task<PanelUsage> Usage(PanelAccount account)
    {
        var usage = await Query("CMD_API_SHOW_USER_USAGE", ("user", account.Username)).ConfigureAwait(false);
        return new PanelUsage(JsonRead.Mebibytes(Value(usage, "quota")), JsonRead.Mebibytes(Value(usage, "bandwidth")));
    }

    /// <summary>Una lectura por GET, con los parámetros en la URL. Ninguno es un secreto.</summary>
    private Task<Form> Query(string command, params (string Name, string Value)[] args) => Read(command, HttpMethod.Get, null, args);

    /// <summary>Una lectura. Lo que no es una respuesta de DirectAdmin, o un error, no prueba nada: no disponible.</summary>
    private async Task<Form> Read(string command, HttpMethod method, string? loginAs, (string Name, string Value)[] args)
    {
        var reply = await transport.Read(command, () => Request(command, method, loginAs, args)).ConfigureAwait(false);
        var data = Parse(reply);
        if (data is null || reply.Status is < 200 or >= 300)
        {
            throw Unreadable(command);
        }

        if (Value(data, "error") == "1")
        {
            var text = Describe(reply.Status, data);
            throw Fail.Unavailable($"DirectAdmin respondió un error a {command}: {text}", "upstream_error", text);
        }

        return data;
    }

    /// <summary>
    /// Un cambio. Solo error en 0 es éxito, pero ni ese ni el error alcanzan: después se vuelve a leer. Un 5xx o un cuerpo
    /// que no es de DirectAdmin pueden venir de algo intermedio con el comando todavía corriendo: quedan sin respuesta.
    /// </summary>
    private async Task<Ack> Mutate(string command, params (string Name, string Value)[] args)
    {
        var reply = await transport.Mutate(command, () => Request(command, HttpMethod.Post, null, args)).ConfigureAwait(false);
        if (reply is null)
        {
            return Ack.NoReply;
        }

        var data = Parse(reply);
        return (data is null ? null : Value(data, "error")) switch
        {
            "0" when reply.Status is >= 200 and < 300 => Ack.Done,
            "1" when reply.Status < 500 => Ack.Refused(Describe(reply.Status, data!)),
            _ => Ack.NoReply,
        };
    }

    private HttpRequestMessage Request(string command, HttpMethod method, string? loginAs, (string Name, string Value)[] args)
    {
        var url = endpoint.Url("/" + command);
        var pairs = args.Select(a => KeyValuePair.Create(a.Name, (string?)a.Value));
        var request = method == HttpMethod.Get
            ? new HttpRequestMessage(method, new Uri(QueryHelpers.AddQueryString(url.AbsoluteUri, pairs)))
            : new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(pairs) };
        var user = loginAs is null ? apiUser : $"{apiUser}|{loginAs}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{apiKey}")));
        return request;
    }

    /// <summary>
    /// Los datos de una respuesta codificada como formulario. Null si es HTML: la página de login de DirectAdmin o algo
    /// intermedio. Un rechazo de la clave, de la IP o de un comando es configuración, también en un cambio: no llegó a correr.
    /// </summary>
    private Form? Parse(PanelReply reply)
    {
        if (reply.Status is 401 or 403)
        {
            throw Denied(Upstream.Describe(reply.Status, null, Secrets));
        }

        var body = reply.Body.Trim();
        if (body.StartsWith('<'))
        {
            return null;
        }

        var data = QueryHelpers.ParseQuery(body);
        var text = $"{Value(data, "text")} {Value(data, "details")}";
        return Value(data, "error") == "1" && DeniedTexts.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))
            ? throw Denied(Describe(reply.Status, data))
            : data;
    }

    /// <summary>El texto de un error de DirectAdmin: el título y el detalle, cortos y sin secretos.</summary>
    private string Describe(int status, Form data) =>
        Upstream.Describe(status, string.Join(": ", new[] { Value(data, "text"), Value(data, "details") }.Where(t => !string.IsNullOrWhiteSpace(t))), Secrets);

    private static ModuleException Denied(string upstream) => Fail.InvalidConfig(
        ["api_user", "api_key"],
        "DirectAdmin rechazó el pedido a la API. Revisar el usuario administrador, la clave de acceso, sus comandos permitidos y que la IP del módulo no esté bloqueada.",
        upstream);

    private static ModuleException Unreadable(string command) =>
        Fail.Unavailable(string.Create(CultureInfo.InvariantCulture, $"DirectAdmin respondió algo que no se puede leer a {command}."), "unreadable");

    /// <summary>Una lista en list[]. Vacía es un cuerpo vacío. Otra clave dice que no es la respuesta esperada.</summary>
    private static string[] List(Form data, string command) =>
        data.Keys.All(k => k == ListKey) ? [.. data.GetValueOrDefault(ListKey).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)] : throw Unreadable(command);

    private static string? Value(Form data, string key) => data.TryGetValue(key, out var value) ? value.ToString() : null;
}
