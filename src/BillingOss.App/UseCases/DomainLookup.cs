using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

/// <summary>
/// Lo que se sabe de un nombre en el registro. Registered nulo es que no se pudo saber, y Reason dice por qué:
/// rdap_unavailable, not_delegated (el DNS no lo conoce, puede estar libre o registrado sin delegar), lookup_disabled o
/// no_rdap_server.
/// </summary>
public sealed record RegistryAnswer(bool? Registered, string Source, DateOnly? ExpiresOn = null, IReadOnlyList<string>? Statuses = null, string? Reason = null);

/// <summary>El transporte HTTP de las consultas RDAP. Las pruebas lo reemplazan por un registro falso.</summary>
public sealed class RdapHandler(HttpMessageHandler inner) : DelegatingHandler(inner);

/// <summary>
/// Consulta de un nombre en su registro por RDAP (RFC 9082 y 9083), con reintentos y el DNS como alternativa. Nunca da
/// por libre un nombre por una falla: solo un 404 del registro dice que no existe, aunque venga en HTML como en NIC
/// Argentina. Si el RDAP no contesta, el DNS confirma los nombres delegados. La lista de servidores de IANA se guarda
/// en memoria un día: es una copia que se vuelve a pedir, no un estado.
/// </summary>
public sealed class DomainLookup : IDisposable
{
    private static readonly TimeSpan BootstrapLife = TimeSpan.FromDays(1);
    private static readonly TimeSpan BootstrapRetry = TimeSpan.FromMinutes(5);

    private readonly IOptions<BillingOptions> options;
    private readonly ILogger<DomainLookup> logger;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, Uri> bootstrap = new(StringComparer.Ordinal);
    private DateTimeOffset bootstrapUntil = DateTimeOffset.MinValue;

    public DomainLookup(IOptions<BillingOptions> options, ILogger<DomainLookup> logger, RdapHandler? handler = null)
    {
        this.options = options;
        this.logger = logger;
        http = new HttpClient(handler ?? (HttpMessageHandler)new SocketsHttpHandler { MaxAutomaticRedirections = 3, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BillingOss-Rdap/0.1");
    }

    private DomainLookupOptions Settings => options.Value.DomainLookup;

    public async Task<RegistryAnswer> Lookup(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Settings.Enabled)
        {
            return new RegistryAnswer(null, "none", Reason: "lookup_disabled");
        }

        var rdap = await Rdap(name, ct).ConfigureAwait(false);
        if (rdap.Registered is not null)
        {
            return rdap;
        }

        var dns = await Dns(name, ct).ConfigureAwait(false);
        return dns.Registered is not null || dns.Reason == "not_delegated" ? dns : rdap;
    }

    public void Dispose()
    {
        http.Dispose();
        gate.Dispose();
    }

    private async Task<RegistryAnswer> Rdap(string name, CancellationToken ct)
    {
        var server = await Server(name, ct).ConfigureAwait(false);
        if (server is null)
        {
            return new RegistryAnswer(null, "rdap", Reason: "no_rdap_server");
        }

        var url = new Uri(server, "domain/" + name);
        var attempts = Math.Max(1, Settings.Attempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Settings.RetryDelayMilliseconds * (1 << (attempt - 2))), ct).ConfigureAwait(false);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Settings.TimeoutSeconds)));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("application/rdap+json");
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status == 404)
                {
                    // El código alcanza: NIC Argentina contesta el 404 en HTML.
                    return new RegistryAnswer(false, "rdap");
                }

                if (status == 200)
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (Parse(body) is { } found)
                    {
                        return found;
                    }

                    // Un 200 que no es un dominio en JSON es la página de un intermediario: se reintenta.
                    continue;
                }

                if (status is not (429 or >= 500))
                {
                    return new RegistryAnswer(null, "rdap", Reason: string.Create(CultureInfo.InvariantCulture, $"rdap_{status}"));
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }

        logger.LogWarning("RDAP de {Server} sin respuesta útil para {Name} en {Attempts} intentos", server.Host, name, attempts);
        return new RegistryAnswer(null, "rdap", Reason: "rdap_unavailable");
    }

    /// <summary>Un objeto de dominio de RDAP: existe, con su vencimiento y sus estados.</summary>
    private static RegistryAnswer? Parse(string body)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (json?["objectClassName"]?.ToString() != "domain")
        {
            return null;
        }

        var expires = (json["events"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => e["eventAction"]?.ToString() == "expiration")
            .Select(e => DateTimeOffset.TryParse(e["eventDate"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? DateOnly.FromDateTime(at.UtcDateTime) : (DateOnly?)null)
            .FirstOrDefault(d => d is not null);
        var statuses = (json["status"] as JsonArray ?? []).Select(s => s?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList();
        return new RegistryAnswer(true, "rdap", expires, statuses);
    }

    /// <summary>El servidor del sufijo más largo que tenga uno: "com.ar" antes que "ar". Primero los configurados.</summary>
    private async Task<Uri?> Server(string name, CancellationToken ct)
    {
        var labels = name.Split('.');
        var suffixes = Enumerable.Range(1, labels.Length - 1).Select(i => string.Join('.', labels[i..])).ToList();
        foreach (var suffix in suffixes)
        {
            if (Settings.Servers.TryGetValue(suffix, out var configured) && Uri.TryCreate(Slash(configured), UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        var map = await Bootstrap(ct).ConfigureAwait(false);
        return suffixes.Select(s => map.GetValueOrDefault(s)).FirstOrDefault(u => u is not null);
    }

    private async Task<Dictionary<string, Uri>> Bootstrap(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < bootstrapUntil)
        {
            return bootstrap;
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < bootstrapUntil)
            {
                return bootstrap;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Settings.TimeoutSeconds) * 2));
                var json = JsonNode.Parse(await http.GetStringAsync(new Uri(Settings.BootstrapUrl), cts.Token).ConfigureAwait(false));
                var map = new Dictionary<string, Uri>(StringComparer.Ordinal);
                foreach (var service in (json?["services"] as JsonArray ?? []).OfType<JsonArray>().Where(s => s.Count >= 2))
                {
                    var urls = (service[1] as JsonArray ?? []).Select(u => u?.ToString() ?? string.Empty).ToList();
                    var https = urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
                    if (https is null || !Uri.TryCreate(Slash(https), UriKind.Absolute, out var uri))
                    {
                        continue;
                    }

                    foreach (var tld in (service[0] as JsonArray ?? []).Select(t => t?.ToString().ToLowerInvariant()).Where(t => t is { Length: > 0 }))
                    {
                        map[tld!] = uri;
                    }
                }

                bootstrap = map;
                bootstrapUntil = DateTimeOffset.UtcNow + BootstrapLife;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("No se pudo leer la lista de servidores RDAP de IANA: {Error}", ex.Message);
                bootstrapUntil = DateTimeOffset.UtcNow + BootstrapRetry;
            }

            return bootstrap;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// La alternativa: una consulta de NS. Un nombre que el DNS conoce está registrado. Uno que no conoce (NXDOMAIN)
    /// puede estar libre o registrado sin delegar, así que no se afirma nada.
    /// </summary>
    private async Task<RegistryAnswer> Dns(string name, CancellationToken ct)
    {
        foreach (var resolver in Resolvers().Take(3))
        {
            switch (await Query(resolver, name, ct).ConfigureAwait(false))
            {
                case 0:
                    return new RegistryAnswer(true, "dns");
                case 3:
                    return new RegistryAnswer(null, "dns", Reason: "not_delegated");
            }
        }

        return new RegistryAnswer(null, "dns", Reason: "dns_unavailable");
    }

    private IEnumerable<IPEndPoint> Resolvers()
    {
        if (Settings.DnsResolvers.Count > 0)
        {
            foreach (var text in Settings.DnsResolvers)
            {
                if (IPEndPoint.TryParse(text, out var endpoint))
                {
                    yield return endpoint.Port == 0 ? new IPEndPoint(endpoint.Address, 53) : endpoint;
                }
            }

            yield break;
        }

        foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct())
        {
            yield return new IPEndPoint(address, 53);
        }
    }

    /// <summary>Código de respuesta de una consulta NS por UDP (RFC 1035), o nulo si no contestó a tiempo.</summary>
    private static async Task<int?> Query(IPEndPoint resolver, string name, CancellationToken ct)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue);
        var query = new List<byte>(name.Length + 18) { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }

        query.AddRange([0, 0, 2, 0, 1]);
        using var udp = new UdpClient(resolver.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await udp.SendAsync(query.ToArray(), resolver, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var b = reply.Buffer;
                if (b.Length >= 12 && BinaryPrimitives.ReadUInt16BigEndian(b) == id && (b[2] & 0x80) != 0)
                {
                    return b[3] & 0x0F;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static string Slash(string url) => url.EndsWith('/') ? url : url + "/";
}
