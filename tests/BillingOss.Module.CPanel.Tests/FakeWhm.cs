using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace BillingOss.Module.CPanel.Tests;

/// <summary>Una función que el módulo llamó en el WHM falso.</summary>
internal sealed record WhmCall(string Method, string Function, string Query, IReadOnlyDictionary<string, string> Args, string? Authorization)
{
    public string Arg(string name) => Args.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>Una cuenta de cPanel del WHM falso.</summary>
internal sealed class WhmAccount
{
    public string Domain { get; set; } = "example.test";

    public string Plan { get; set; } = "basico";

    public bool Suspended { get; set; }

    public string Password { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DiskUsed { get; set; } = "120M";

    public string DiskLimit { get; set; } = "1000M";

    public long BandwidthBytes { get; set; } = 35L * 1024 * 1024;

    public long BandwidthLimit { get; set; }
}

/// <summary>
/// WHM falso: simula las funciones de la API 1 que usa el módulo sobre cuentas en memoria, con la forma de sus
/// respuestas (metadata.result, metadata.reason y data). Cada test puede pisar la respuesta de una función o cortar la
/// conexión, y anota todo lo que recibe.
/// </summary>
internal sealed class FakeWhm : HttpMessageHandler
{
    public const string Host = "whm.example.test";

    /// <summary>Inventado: no sirve contra ningún WHM.</summary>
    public const string Token = "TOKENINVENTADODEPRUEBA0123456789ABCDEF";

    public static readonly string[] Packages = ["basico", "pro", "Plan Empresa"];

    private static readonly string[] Reads = ["listpkgs", "listaccts", "getdomainowner", "showbw", "create_user_session"];

    private readonly Dictionary<string, Func<WhmCall, Func<(int Status, string Body)>, (int Status, string Body)>> overrides = new(StringComparer.Ordinal);
    private readonly List<WhmCall> calls = [];

    public Dictionary<string, WhmAccount> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WhmCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public string[] Functions => Calls.Select(c => c.Function).ToArray();

    public string[] Mutations => Functions.Where(f => !Reads.Contains(f)).ToArray();

    public WhmCall Last(string function) => Calls.Last(c => c.Function == function);

    public FakeWhm With(string user, string domain = "example.test", string plan = "basico", bool suspended = false)
    {
        Accounts[user] = new WhmAccount { Domain = domain, Plan = plan, Suspended = suspended };
        return this;
    }

    public FakeWhm On(string function, Func<WhmCall, Func<(int Status, string Body)>, (int Status, string Body)> respond)
    {
        overrides[function] = respond;
        return this;
    }

    public FakeWhm On(string function, int status, string body) => On(function, (_, _) => (status, body));

    /// <summary>Un corte de conexión después de mandar el pedido. Con apply, WHM llegó a ejecutarlo.</summary>
    public FakeWhm CutAfter(string function, bool apply) => On(function, (_, run) =>
    {
        if (apply)
        {
            run();
        }

        throw new HttpRequestException(HttpRequestError.ResponseEnded, "La conexión se cortó");
    });

    public static string Result(string function, int result, string reason, JsonObject? data = null) => new JsonObject
    {
        ["metadata"] = new JsonObject { ["version"] = 1, ["command"] = function, ["result"] = result, ["reason"] = reason },
        ["data"] = data,
    }.ToJsonString();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var call = new WhmCall(
            request.Method.Method,
            request.RequestUri!.AbsolutePath.Replace("/json-api/", string.Empty, StringComparison.Ordinal),
            request.RequestUri.Query,
            QueryHelpers.ParseQuery(body).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal),
            request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null);
        lock (calls)
        {
            calls.Add(call);
        }

        var (status, text) = overrides.TryGetValue(call.Function, out var respond) ? respond(call, () => Default(call)) : Default(call);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private (int Status, string Body) Default(WhmCall call)
    {
        lock (Accounts)
        {
            return Run(call);
        }
    }

    private (int Status, string Body) Run(WhmCall call)
    {
        if (call.Authorization != $"whm root:{Token}")
        {
            return (403, """{"cpanelresult":{"apiversion":"1","error":"Access denied","data":{"reason":"Access denied","result":"0"},"type":"text"}}""");
        }

        if (call.Method != "POST" || call.Query != "?api.version=1")
        {
            return (200, Result(call.Function, 0, "Este WHM falso solo atiende la API 1 por POST."));
        }

        var f = call.Function;
        var user = call.Arg("user");
        var account = Accounts.GetValueOrDefault(user);
        var missing = Result(f, 0, $"The user “{user}” does not exist.");
        switch (f)
        {
            case "listpkgs":
                return (200, Result(f, 1, "OK", new JsonObject { ["pkg"] = new JsonArray(Packages.Select(p => (JsonNode)new JsonObject { ["name"] = p }).ToArray()) }));
            case "listaccts":
                var exact = call.Arg("searchtype") == "user" && call.Arg("searchmethod") == "exact";
                var rows = Accounts.Where(a => !exact || string.Equals(a.Key, call.Arg("search"), StringComparison.OrdinalIgnoreCase))
                    .Select(a => (JsonNode)new JsonObject
                    {
                        ["user"] = a.Key,
                        ["domain"] = a.Value.Domain,
                        ["plan"] = a.Value.Plan,
                        ["suspended"] = a.Value.Suspended ? 1 : 0,
                        ["diskused"] = a.Value.DiskUsed,
                        ["disklimit"] = a.Value.DiskLimit,
                        ["email"] = a.Value.Email,
                    });
                return (200, Result(f, 1, "OK", new JsonObject { ["acct"] = new JsonArray(rows.ToArray()) }));
            case "getdomainowner":
                var owner = Accounts.FirstOrDefault(a => string.Equals(a.Value.Domain, call.Arg("domain"), StringComparison.OrdinalIgnoreCase)).Key;
                return (200, Result(f, 1, "OK", new JsonObject { ["user"] = owner }));
            case "createacct":
                var name = call.Arg("username");
                if (Accounts.ContainsKey(name))
                {
                    return (200, Result(f, 0, $"Sorry, a group for that username already exists. ({name})"));
                }

                if (Accounts.Values.Any(a => a.Domain == call.Arg("domain")))
                {
                    return (200, Result(f, 0, $"The domain “{call.Arg("domain")}” already exists in the userdata."));
                }

                if (!Packages.Contains(call.Arg("plan")))
                {
                    return (200, Result(f, 0, $"The package “{call.Arg("plan")}” does not exist."));
                }

                if (name.Length is 0 or > 16 || !char.IsAsciiLetterLower(name[0]) || name.StartsWith("test", StringComparison.Ordinal))
                {
                    return (200, Result(f, 0, "Invalid username."));
                }

                Accounts[name] = new WhmAccount { Domain = call.Arg("domain"), Plan = call.Arg("plan"), Password = call.Arg("password"), Email = call.Arg("contactemail") };
                return (200, Result(f, 1, "Account Creation Ok", new JsonObject { ["package"] = call.Arg("plan") }));
            case "suspendacct" or "unsuspendacct":
                if (account is null)
                {
                    return (200, missing);
                }

                account.Suspended = f == "suspendacct";
                return (200, Result(f, 1, "OK"));
            case "removeacct":
                // La API 1 pide username: con el user de la versión vieja no borra nada.
                return Accounts.Remove(call.Arg("username")) ? (200, Result(f, 1, "OK")) : (200, Result(f, 0, "Missing or unknown username."));
            case "changepackage":
                if (account is null)
                {
                    return (200, missing);
                }

                if (!Packages.Contains(call.Arg("pkg")))
                {
                    return (200, Result(f, 0, $"Specified package “{call.Arg("pkg")}” does not exist."));
                }

                account.Plan = call.Arg("pkg");
                return (200, Result(f, 1, "OK"));
            case "passwd":
                if (account is null)
                {
                    return (200, missing);
                }

                account.Password = call.Arg("password");
                return (200, Result(f, 1, "Password changed"));
            case "create_user_session":
                return account is null || call.Arg("service") != "cpaneld"
                    ? (200, missing)
                    : (200, Result(f, 1, "Created session", new JsonObject
                    {
                        ["url"] = $"https://{Host}:2083/cpsess0123456789/login/?session={user}%3aSESIONINVENTADA",
                        ["session"] = $"{user}:SESIONINVENTADA",
                        ["expires"] = 1793232000,
                        ["service"] = "cpaneld",
                    }));
            case "showbw":
                var usage = Accounts.Where(a => call.Arg("searchtype") == "user" && a.Key.Contains(call.Arg("search"), StringComparison.OrdinalIgnoreCase))
                    .Select(a => (JsonNode)new JsonObject { ["user"] = a.Key, ["totalbytes"] = a.Value.BandwidthBytes, ["limit"] = a.Value.BandwidthLimit });
                return (200, Result(f, 1, "OK", new JsonObject { ["month"] = 10, ["year"] = 2026, ["acct"] = new JsonArray(usage.ToArray()) }));
            default:
                return (200, Result(f, 0, $"Unknown app ({f}) requested for this version (1) of the API."));
        }
    }
}
