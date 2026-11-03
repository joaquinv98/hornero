using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace BillingOss.Module.DirectAdmin.Tests;

/// <summary>Un comando que el módulo mandó al DirectAdmin falso, con el usuario y la clave de la autenticación básica.</summary>
internal sealed record DirectAdminCall(string Method, string Command, string Query, IReadOnlyDictionary<string, string> Args, string? User, string? Key)
{
    public string Arg(string name) => Args.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>Un usuario del DirectAdmin falso. Uso y topes en MB, como los devuelve DirectAdmin.</summary>
internal sealed class DirectAdminUser
{
    public string Domain { get; set; } = "example.test";

    public string Package { get; set; } = "basico";

    public bool Suspended { get; set; }

    public string Password { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public List<string> ExtraDomains { get; } = [];

    public string DiskUsed { get; set; } = "120.4";

    public string DiskLimit { get; set; } = "1000";

    public string BandwidthUsed { get; set; } = "35.2";

    public string BandwidthLimit { get; set; } = "unlimited";
}

/// <summary>
/// DirectAdmin falso: simula los comandos CMD_API_ que usa el módulo sobre usuarios en memoria, con la forma de sus
/// respuestas codificadas como formulario (error, text y details en los cambios, list[] en las listas). Cada test puede
/// pisar la respuesta de un comando o cortar la conexión, y anota todo lo que recibe.
/// </summary>
internal sealed class FakeDirectAdmin : HttpMessageHandler
{
    public const string Host = "da.example.test";

    /// <summary>Inventada: no sirve contra ningún DirectAdmin.</summary>
    public const string Key = "CLAVEDEACCESOINVENTADA0123456789";

    public const string Ip = "192.0.2.20";

    public static readonly string[] Packages = ["basico", "pro", "Plan Empresa"];

    private static readonly string[] Reads =
        ["CMD_API_PACKAGES_USER", "CMD_API_SHOW_ALL_USERS", "CMD_API_SHOW_USER_CONFIG", "CMD_API_SHOW_USER_USAGE", "CMD_API_DOMAIN_OWNERS", "CMD_API_LOGIN_KEYS"];

    private readonly Dictionary<string, Func<DirectAdminCall, Func<(int Status, string Body)>, (int Status, string Body)>> overrides = new(StringComparer.Ordinal);
    private readonly List<DirectAdminCall> calls = [];

    public Dictionary<string, DirectAdminUser> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DirectAdminCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public string[] Commands => [.. Calls.Select(c => c.Command)];

    public string[] Mutations => [.. Commands.Where(c => !Reads.Contains(c))];

    public DirectAdminCall Last(string command) => Calls.Last(c => c.Command == command);

    public FakeDirectAdmin With(string user, string domain = "example.test", string package = "basico", bool suspended = false)
    {
        Users[user] = new DirectAdminUser { Domain = domain, Package = package, Suspended = suspended };
        return this;
    }

    public FakeDirectAdmin On(string command, Func<DirectAdminCall, Func<(int Status, string Body)>, (int Status, string Body)> respond)
    {
        overrides[command] = respond;
        return this;
    }

    public FakeDirectAdmin On(string command, int status, string body) => On(command, (_, _) => (status, body));

    /// <summary>Un corte de conexión después de mandar el comando. Con apply, DirectAdmin llegó a ejecutarlo.</summary>
    public FakeDirectAdmin CutAfter(string command, bool apply) => On(command, (_, run) =>
    {
        if (apply)
        {
            run();
        }

        throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
    });

    /// <summary>Una respuesta codificada como formulario, como la de DirectAdmin.</summary>
    public static string Form(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => $"{(p.Key == "list[]" ? p.Key : Uri.EscapeDataString(p.Key))}={Uri.EscapeDataString(p.Value)}"));

    public static string Error(string text, string details) => Form(("error", "1"), ("text", text), ("details", details));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var query = request.RequestUri!.Query;
        var args = QueryHelpers.ParseQuery(request.Method == HttpMethod.Get ? query : body).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        string? user = null;
        string? key = null;
        if (request.Headers.Authorization is { Scheme: "Basic", Parameter: { } parameter })
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(parameter)).Split(':', 2);
            (user, key) = (pair[0], pair.Length > 1 ? pair[1] : null);
        }

        var call = new DirectAdminCall(request.Method.Method, request.RequestUri.AbsolutePath.TrimStart('/'), query, args, user, key);
        lock (calls)
        {
            calls.Add(call);
        }

        var (status, text) = overrides.TryGetValue(call.Command, out var respond) ? respond(call, () => Default(call)) : Default(call);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    }

    private (int Status, string Body) Default(DirectAdminCall call)
    {
        lock (Users)
        {
            return Run(call);
        }
    }

    private (int Status, string Body) Run(DirectAdminCall call)
    {
        if (call.Key != Key || call.User is null || call.User.Split('|')[0] != "admin")
        {
            return (401, "<html><head><title>DirectAdmin Login</title></head></html>");
        }

        // El "entrar como" vale solo para pedir el acceso directo de un usuario que existe.
        var loginAs = call.User.Contains('|', StringComparison.Ordinal) ? call.User.Split('|')[1] : null;
        if (loginAs is not null && (call.Command != "CMD_API_LOGIN_KEYS" || !Users.ContainsKey(loginAs)))
        {
            return (401, "<html><head><title>DirectAdmin Login</title></head></html>");
        }

        var reading = Reads.Contains(call.Command) && call.Command != "CMD_API_LOGIN_KEYS";
        if (reading != (call.Method == "GET"))
        {
            return (200, Error("Cannot Execute Your Request", "Método equivocado para este comando en el DirectAdmin falso."));
        }

        var name = call.Arg("user");
        var target = Users.GetValueOrDefault(name);
        var missing = Error("Cannot Execute Your Request", $"User {name} does not exist");
        switch (call.Command)
        {
            case "CMD_API_PACKAGES_USER":
                return (200, Form([.. Packages.Select(p => ("list[]", p))]));
            case "CMD_API_SHOW_ALL_USERS":
                return (200, Form([.. Users.Keys.Select(u => ("list[]", u))]));
            case "CMD_API_SHOW_USER_CONFIG":
                return target is null
                    ? (200, missing)
                    : (200, Form(
                        ("username", name),
                        ("domain", target.Domain),
                        ("package", target.Package),
                        ("suspended", target.Suspended ? "yes" : "no"),
                        ("email", target.Email),
                        ("ip", Ip),
                        ("quota", target.DiskLimit),
                        ("bandwidth", target.BandwidthLimit),
                        ("creator", "admin")));
            case "CMD_API_SHOW_USER_USAGE":
                return target is null ? (200, missing) : (200, Form(("bandwidth", target.BandwidthUsed), ("quota", target.DiskUsed), ("nemails", "2")));
            case "CMD_API_DOMAIN_OWNERS":
                return (200, Form([.. Users.SelectMany(u => u.Value.ExtraDomains.Prepend(u.Value.Domain).Select(d => (d, u.Key)))]));
            case "CMD_API_ACCOUNT_USER":
                return (200, Create(call));
            case "CMD_API_SELECT_USERS":
                var selected = call.Arg("select0");
                if (!Users.TryGetValue(selected, out var user))
                {
                    return (200, Error("Cannot Execute Your Request", $"User {selected} does not exist"));
                }

                if (call.Arg("delete") == "yes" && call.Arg("confirmed") == "Confirm")
                {
                    Users.Remove(selected);
                    return (200, Form(("error", "0"), ("text", "Users deleted"), ("details", $"{selected}<br>")));
                }

                if (call.Arg("location") == "CMD_SELECT_USERS" && call.Arg("suspend") is "Suspend" or "Unsuspend")
                {
                    user.Suspended = call.Arg("suspend") == "Suspend";
                    return (200, Form(("error", "0"), ("text", "Success"), ("details", "Users modified")));
                }

                return (200, Error("Cannot Execute Your Request", "Nothing to do"));
            case "CMD_API_MODIFY_USER":
                var modified = call.Arg("user");
                if (!Users.TryGetValue(modified, out var owner) || call.Arg("action") != "package")
                {
                    return (200, Error("Cannot Execute Your Request", $"User {modified} does not exist"));
                }

                if (!Packages.Contains(call.Arg("package")))
                {
                    return (200, Error("Cannot Execute Your Request", $"Package {call.Arg("package")} does not exist"));
                }

                owner.Package = call.Arg("package");
                return (200, Form(("error", "0"), ("text", "Package changed")));
            case "CMD_API_USER_PASSWD":
                if (!Users.TryGetValue(call.Arg("username"), out var account))
                {
                    return (200, Error("Cannot Execute Your Request", "User does not exist"));
                }

                if (call.Arg("passwd") != call.Arg("passwd2") || call.Arg("passwd").Length < 8)
                {
                    return (200, Error("Cannot Execute Your Request", "Passwords do not match or are too short"));
                }

                account.Password = call.Arg("passwd");
                return (200, Form(("error", "0"), ("text", "Password Changed")));
            case "CMD_API_LOGIN_KEYS":
                return call.Arg("action") == "create" && call.Arg("type") == "one_time_url"
                    ? (200, Form(("error", "0"), ("text", "Login URL created"), ("details", $"https://{Host}:2222/api/login/url?key=CLAVEUNICAINVENTADA{loginAs}")))
                    : (200, Error("Cannot Execute Your Request", "Unsupported login key request"));
            default:
                return (200, Error("Cannot Execute Your Request", $"Unknown command {call.Command}"));
        }
    }

    private string Create(DirectAdminCall call)
    {
        var name = call.Arg("username");
        var domain = call.Arg("domain");
        if (call.Arg("action") != "create" || call.Arg("add") != "Submit")
        {
            return Error("Cannot Execute Your Request", "Missing action");
        }

        if (Users.ContainsKey(name))
        {
            return Error("Unable to Create User", $"User {name} already exists");
        }

        if (Users.Values.Any(u => u.Domain == domain || u.ExtraDomains.Contains(domain)))
        {
            return Error("Unable to Create User", $"Domain {domain} already exists on the server");
        }

        if (!Packages.Contains(call.Arg("package")))
        {
            return Error("Unable to Create User", $"Package {call.Arg("package")} does not exist");
        }

        if (call.Arg("ip") != Ip)
        {
            return Error("Unable to Create User", "That IP is not available");
        }

        if (name.Length is 0 or > 10 || !char.IsAsciiLetterLower(name[0]) || call.Arg("passwd") != call.Arg("passwd2"))
        {
            return Error("Unable to Create User", "Invalid username or password");
        }

        Users[name] = new DirectAdminUser { Domain = domain, Package = call.Arg("package"), Password = call.Arg("passwd"), Email = call.Arg("email") };
        return Form(("error", "0"), ("text", "User Created"), ("details", $"User {name} created"));
    }
}
