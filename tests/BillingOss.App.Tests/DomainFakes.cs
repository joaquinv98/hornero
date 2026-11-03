using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// RDAP falso: el bootstrap de IANA y los registros de .ar y .com, con fallas a pedido. Un nombre que no tiene da 404
/// en HTML, como NIC Argentina.
/// </summary>
internal sealed class FakeRdap : HttpMessageHandler
{
    public const string Bootstrap = "https://iana.example/rdap/dns.json";

    private int requests;

    public ConcurrentDictionary<string, DateOnly> Registered { get; } = new(StringComparer.Ordinal);

    /// <summary>Cuántas consultas de dominio contesta con 500 antes de andar. Negativo: todas.</summary>
    public int Failures { get; set; }

    /// <summary>El registro no contesta nunca: cada consulta espera hasta que la cancelan, como NIC Argentina caído.</summary>
    public bool Hang { get; set; }

    public int Requests => requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        if (url.AbsoluteUri == Bootstrap)
        {
            return Json("""{"services":[[["ar"],["https://rdap.nic.example/rdap/"]],[["com","net"],["https://rdap.verisign.example/com/v1/"]]]}""");
        }

        Interlocked.Increment(ref requests);
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Failures != 0)
        {
            Failures--;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        var name = url.AbsolutePath[(url.AbsolutePath.LastIndexOf("/domain/", StringComparison.Ordinal) + 8)..];
        if (Registered.TryGetValue(name, out var expires))
        {
            var at = expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Json($$"""
                {"objectClassName":"domain","ldhName":"{{name}}","status":["active"],
                 "events":[{"eventAction":"registration","eventDate":"2020-01-01T00:00:00Z"},{"eventAction":"expiration","eventDate":"{{at}}T00:00:00Z"}]}
                """);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html><body>No encontrado</body></html>", Encoding.UTF8, "text/html") };
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/rdap+json") };
}

/// <summary>Los avisos de dominios, con la misma regla que la cola de correo: una clave, un aviso.</summary>
internal sealed class CapturedDomainMail : UseCases.IDomainMail
{
    public ConcurrentDictionary<string, (UseCases.DomainNotice Notice, UseCases.DomainNoticeData Data)> Sent { get; } = new(StringComparer.Ordinal);

    public int Requests { get; private set; }

    public Task Send(UseCases.DomainNotice notice, UseCases.DomainNoticeData data, string dedupKey, CancellationToken ct)
    {
        Requests++;
        Sent.TryAdd(dedupKey, (notice, data));
        return Task.CompletedTask;
    }
}

/// <summary>Resolvedor DNS falso por UDP: NOERROR para los nombres delegados, NXDOMAIN para el resto, o mudo.</summary>
internal sealed class FakeDns : IDisposable
{
    private readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource stop = new();

    public FakeDns() => _ = Task.Run(Serve);

    public ConcurrentDictionary<string, bool> Delegated { get; } = new(StringComparer.Ordinal);

    public bool Silent { get; set; }

    public string Address => $"127.0.0.1:{((IPEndPoint)udp.Client.LocalEndPoint!).Port}";

    public void Dispose()
    {
        stop.Cancel();
        udp.Dispose();
        stop.Dispose();
    }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (Silent)
            {
                continue;
            }

            var query = received.Buffer;
            var labels = new List<string>();
            for (var i = 12; i < query.Length && query[i] != 0; i += query[i] + 1)
            {
                labels.Add(Encoding.ASCII.GetString(query, i + 1, query[i]));
            }

            var reply = (byte[])query.Clone();
            reply[2] = 0x81;
            reply[3] = (byte)(Delegated.ContainsKey(string.Join('.', labels)) ? 0x80 : 0x83);
            await udp.SendAsync(reply, received.RemoteEndPoint);
        }
    }
}

/// <summary>
/// Un registrar falso que habla el tipo registrar del protocolo con la SDK, sobre un registro en memoria. Sirve para
/// probar el núcleo de punta a punta: pedido, pago, registro, transferencia, operaciones y sincronización.
/// </summary>
internal sealed class FakeRegistrarModule
{
    public ConcurrentDictionary<string, FakeEntry> Domains { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Operation, JsonObject Input, JsonObject Context)> Calls { get; } = new();

    /// <summary>Nombres registrados en otro lado: no se pueden registrar y sí transferir, como traer.com.</summary>
    public HashSet<string> Taken { get; } = new(StringComparer.Ordinal) { "tomado.com", "traer.com" };

    /// <summary>Nombres libres que el registro vende como premium, con otro precio.</summary>
    public HashSet<string> Premium { get; } = new(StringComparer.Ordinal);

    /// <summary>Las transferencias quedan en curso hasta que la prueba las suelta.</summary>
    public bool TransfersPending { get; set; } = true;

    public DateOnly Today { get; set; } = new(2026, 11, 2);

    /// <summary>
    /// Lo que necesita una prueba de punta a punta: .com a la venta en pesos en la instancia (15.000 el registro,
    /// 16.000 la renovación y 14.000 la transferencia, y 29.000 a dos años) y la cuenta con el titular completo.
    /// </summary>
    public static async Task Seed(BillingDbContext db, long instanceId, long accountId)
    {
        db.TldPrices.Add(new TldPrice(".com", instanceId, Currency.Ars, 1, new DateOnly(2026, 1, 1)) { Register = 15000m, Renew = 16000m, Transfer = 14000m });
        db.TldPrices.Add(new TldPrice(".com", instanceId, Currency.Ars, 2, new DateOnly(2026, 1, 1)) { Register = 29000m, Renew = 31000m });
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        account.Phone = "+54 11 5555-1234";
        var tax = await db.TaxIdentities.SingleAsync(t => t.AccountId == accountId);
        (tax.Street, tax.City, tax.Province, tax.PostalCode) = ("Av. Siempre Viva 742", "Buenos Aires", "CABA", "C1000");
        await db.SaveChangesAsync();
    }

    public static JsonObject Manifest(params string[] capabilities) => new()
    {
        ["id"] = "registrar-falso",
        ["kinds"] = new JsonArray("registrar"),
        ["capabilities"] = new JsonArray([.. capabilities.Prepend("registrar.core").Select(c => (JsonNode)c)]),
    };

    public static IReadOnlyList<string> AllCapabilities { get; } =
        ["registrar.transfer", "registrar.auth_code", "registrar.lock", "registrar.contacts", "registrar.pricing", "registrar.dns", "registrar.email_forwarding", "registrar.privacy"];

    /// <summary>Sin el registrar falla la lectura de la zona: el núcleo lo muestra sin romper la página.</summary>
    public bool ReadsFail { get; set; }

    /// <summary>El registrar no contesta la disponibilidad: el núcleo sigue sin saberla.</summary>
    public bool ChecksFail { get; set; }

    /// <summary>El registrar rechaza las renovaciones, por ejemplo sin saldo del revendedor: la operación queda fallida.</summary>
    public bool RenewsFail { get; set; }

    public ModuleDefinition Definition(params string[] capabilities) =>
        new ModuleDefinition(Manifest(capabilities.Length > 0 ? capabilities : [.. AllCapabilities]))
            .Operation("registrar", "check", c => ChecksFail
                ? throw new ModuleException(ErrorCodes.UpstreamUnavailable, "El registrar no responde.", 30)
                : Done(c, Check(c.Request.Input)), mutating: false)
            .Operation("registrar", "register", c =>
            {
                var name = c.Request.Input["name"]!.ToString();
                if (Taken.Contains(name))
                {
                    throw new ModuleException(ErrorCodes.Rejected, $"{name} está tomado.", details: new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "taken" } });
                }

                var entry = Domains.GetOrAdd(name, _ => new FakeEntry { Expires = Today.AddYears(c.Request.Input["years"]!.GetValue<int>()) });
                entry.Nameservers = [.. (c.Request.Input["nameservers"] as JsonArray ?? []).Select(n => n!.ToString())];
                entry.Privacy = c.Request.Input["privacy"]?.GetValue<bool>() == true;
                return Done(c, entry.State(name));
            })
            .Operation("registrar", "transfer", c =>
            {
                var name = c.Request.Input["name"]!.ToString();
                if (c.Request.Input["auth_code"]?.ToString() != "EPP-CORRECTO")
                {
                    throw new ModuleException(ErrorCodes.Rejected, "Código EPP inválido.", details: new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "invalid_auth_code" } });
                }

                Domains.GetOrAdd(name, _ => new FakeEntry { Expires = Today.AddMonths(4).AddYears(1), Status = "pending_transfer" });
                // Se consulta cada hora: la prueba mueve el reloj para que vuelva a correr.
                return TransfersPending
                    ? Task.FromResult(Record(c, ModuleResponse.Pending($"transfer-{name.Replace('.', '_')}", retryAfter: 3600)))
                    : Done(c, Release(name));
            })
            .Operation("registrar", "renew", c =>
            {
                if (RenewsFail)
                {
                    throw new ModuleException(ErrorCodes.Rejected, "Sin saldo en la cuenta del revendedor.", details: new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "insufficient_funds" } });
                }

                var entry = Domains[Name(c)];
                entry.Expires = entry.Expires.AddYears(c.Request.Input["years"]!.GetValue<int>());
                return Done(c, new JsonObject { ["expires_on"] = Iso(entry.Expires) });
            })
            .Operation("registrar", "get_domain", c => Done(c, Domains.TryGetValue(Name(c), out var e) ? e.State(Name(c)) : new JsonObject { ["status"] = "not_found" }), mutating: false)
            .Operation("registrar", "set_nameservers", c =>
            {
                Domains[Name(c)].Nameservers = [.. c.Request.Input["nameservers"]!.AsArray().Select(n => n!.ToString())];
                return Done(c, new JsonObject { ["nameservers"] = c.Request.Input["nameservers"]!.DeepClone() });
            })
            .Operation("registrar", "set_lock", c =>
            {
                Domains[Name(c)].Locked = c.Request.Input["locked"]!.GetValue<bool>();
                return Done(c, new JsonObject { ["locked"] = Domains[Name(c)].Locked });
            })
            .Operation("registrar", "get_auth_code", c => Done(c, new JsonObject { ["secrets"] = new JsonObject { ["auth_code"] = "EPP-DEL-REGISTRAR" } }), mutating: false)
            .Operation("registrar", "set_contacts", c =>
            {
                Domains[Name(c)].RegistrantEmail = c.Request.Context["contacts"]!["registrant"]!["email"]!.ToString();
                return Done(c, []);
            })
            .Operation("registrar", "get_prices", c => Done(c, new JsonObject
            {
                ["prices"] = new JsonArray(
                    new JsonObject { ["tld"] = ".com", ["currency"] = "USD", ["years"] = 1, ["register"] = "9.50", ["renew"] = "10.25", ["transfer"] = "9.75" },
                    new JsonObject { ["tld"] = ".net", ["currency"] = "USD", ["years"] = 1, ["register"] = "11.00", ["renew"] = null, ["transfer"] = "11.50" }),
            }), mutating: false)
            .Operation("registrar", "get_dns", c => ReadsFail
                ? throw new ModuleException(ErrorCodes.UpstreamUnavailable, "El registrar no responde.", 30)
                : Done(c, new JsonObject { ["records"] = JsonSerializer.SerializeToNode(Domains[Name(c)].Records, ModuleJson.Options) }), mutating: false)
            .Operation("registrar", "set_dns", c =>
            {
                Domains[Name(c)].Records = c.Request.Input["records"].Deserialize<List<DnsRecord>>(ModuleJson.Options)!;
                return Done(c, new JsonObject { ["records"] = c.Request.Input["records"]!.DeepClone() });
            })
            .Operation("registrar", "get_email_forwards", c => Done(c, new JsonObject { ["forwards"] = JsonSerializer.SerializeToNode(Domains[Name(c)].Forwards, ModuleJson.Options) }), mutating: false)
            .Operation("registrar", "set_email_forwards", c =>
            {
                Domains[Name(c)].Forwards = c.Request.Input["forwards"].Deserialize<List<EmailForward>>(ModuleJson.Options)!;
                return Done(c, new JsonObject { ["forwards"] = c.Request.Input["forwards"]!.DeepClone() });
            })
            .Operation("registrar", "set_privacy", c =>
            {
                Domains[Name(c)].Privacy = c.Request.Input["enabled"]!.GetValue<bool>();
                return Done(c, new JsonObject { ["enabled"] = Domains[Name(c)].Privacy });
            })
            .Poll((c, id) =>
            {
                var name = Name(c);
                return Domains.TryGetValue(name, out var e) && e.Status == "active" ? Done(c, e.State(name)) : Task.FromResult(ModuleResponse.Pending(id, retryAfter: 3600));
            })
            .InstanceTest(c => Done(c, new JsonObject { ["ok"] = true }));

    /// <summary>El registro de origen soltó la transferencia.</summary>
    public JsonObject Release(string name)
    {
        var entry = Domains[name];
        entry.Status = "active";
        return entry.State(name);
    }

    private static string Name(ModuleCall c) => c.Request.Context["domain"]!["name"]!.ToString();

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private JsonObject Check(JsonObject input) => new()
    {
        ["results"] = new JsonArray([.. input["names"]!.AsArray().Select(n => (JsonNode)new JsonObject
        {
            ["name"] = n!.ToString(),
            ["available"] = !Taken.Contains(n.ToString()) && !Domains.ContainsKey(n.ToString()),
            ["premium"] = Premium.Contains(n.ToString()),
        })]),
    };

    private Task<ModuleResponse> Done(ModuleCall c, JsonObject result) => Task.FromResult(Record(c, ModuleResponse.Completed(result)));

    private ModuleResponse Record(ModuleCall c, ModuleResponse response)
    {
        Calls.Enqueue((c.Operation, c.Request.Input.DeepClone().AsObject(), c.Request.Context.DeepClone().AsObject()));
        return response;
    }

    internal sealed class FakeEntry
    {
        public DateOnly Expires { get; set; }

        public string Status { get; set; } = "active";

        public List<string> Nameservers { get; set; } = [];

        public bool Locked { get; set; }

        public string? RegistrantEmail { get; set; }

        public List<DnsRecord> Records { get; set; } = [];

        public List<EmailForward> Forwards { get; set; } = [];

        public bool Privacy { get; set; }

        public JsonObject State(string name) => new()
        {
            ["status"] = Status,
            ["expires_on"] = Iso(Expires),
            ["nameservers"] = new JsonArray([.. Nameservers.Select(n => (JsonNode)n)]),
            ["locked"] = Locked,
            ["privacy"] = Privacy,
            ["external_ids"] = new JsonObject { ["registrar_id"] = "R-" + name },
        };
    }
}
