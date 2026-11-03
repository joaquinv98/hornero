using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Hestia;

/// <summary>Los clientes HTTP hacia Hestia, uno por huella de certificado.</summary>
internal static class HestiaHttp
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    /// <summary>
    /// Con la huella fija en la validación, una conexión validada con una huella nunca la usa otra configuración. Es
    /// un caché de conexiones: no guarda nada de los pedidos.
    /// </summary>
    public static HttpClient For(string? fingerprint) => Clients.GetOrAdd(fingerprint ?? string.Empty, Build);

    /// <summary>
    /// TLS siempre validado: con la CA del sistema, o con la huella SHA-256 del certificado si la instancia la fija. No
    /// hay opción para desactivarlo.
    /// </summary>
    public static HttpClient Build(string fingerprint) => new(new SocketsHttpHandler
    {
        // Sin redirecciones: un 30x mandaría la access key a otro destino.
        AllowAutoRedirect = false,
        UseCookies = false,

        // El tiempo para conectar es corto porque ahí el pedido todavía no salió.
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None
                || (fingerprint.Length > 0
                    && certificate is not null
                    && Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)) == fingerprint),
        },
    })
    {
        // El plazo lo pone cada llamada.
        Timeout = Timeout.InfiniteTimeSpan,

        // Un tope a lo que devuelve el panel o algo intermedio. Pasarlo es un corte, como cualquier respuesta rota.
        MaxResponseContentBufferSize = 16 * 1024 * 1024,
    };
}

/// <summary>El tiempo que el módulo se da para una operación: lo que espera el núcleo menos un margen para contestar.</summary>
internal sealed class Budget(TimeProvider time, TimeSpan total)
{
    private readonly long started = time.GetTimestamp();

    public TimeSpan Remaining => total - time.GetElapsedTime(started);
}

/// <summary>Se terminó el tiempo de la operación. Cada operación decide si eso es pending, incierto o no disponible.</summary>
internal sealed class OutOfTime() : Exception("Se terminó el tiempo de la operación.");

/// <summary>Hestia contestó con un error que no es de autenticación. El texto ya viene sin secretos.</summary>
internal sealed class HestiaRefusal(int status, string text, bool blank) : Exception(text)
{
    /// <summary>El código HTTP. Hestia pasa ahí el código de salida del comando, por ejemplo 404 si algo no existe.</summary>
    public int Status { get; } = status;

    public string Text { get; } = text;

    /// <summary>El cuerpo vino vacío: el comando terminó con error sin imprimir nada.</summary>
    public bool Blank { get; } = blank;
}

/// <summary>Lo que pasó con un mutador. El estado posterior es el que decide si la operación terminó.</summary>
internal sealed record Ack(bool Ok, bool Lost, string? Refusal)
{
    public static readonly Ack Done = new(true, false, null);

    /// <summary>Se mandó y no hubo respuesta: pudo haberse ejecutado.</summary>
    public static readonly Ack NoReply = new(false, true, null);

    public static Ack Refused(string text) => new(false, false, text);
}

/// <summary>
/// Cliente de la API de Hestia: POST a https://{host}:{port}/api/ con el cuerpo en application/x-www-form-urlencoded,
/// hash ACCESS_KEY:SECRET_KEY, cmd y arg1 a argN. Las consultas piden json y los mutadores contestan OK.
/// </summary>
internal sealed class HestiaClient(HttpClient http, Settings settings, Budget budget, ILogger logger, IReadOnlyList<string?> hidden)
{
    /// <summary>Plazo de cada lectura.</summary>
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(30);

    /// <summary>Lo mínimo que tiene que quedar para mandar un mutador.</summary>
    private static readonly TimeSpan MutatorRoom = TimeSpan.FromSeconds(30);

    /// <summary>Lo que se reserva para releer después de un mutador. El mutador tiene todo lo demás, porque un borrado grande tarda.</summary>
    private static readonly TimeSpan ReadRoom = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Textos con los que Hestia rechaza la autenticación, la IP o un comando fuera del perfil de la access key. Solo se
    /// miran en las lecturas: en un mutador cuentan el 401 y el 403.
    /// </summary>
    private static readonly string[] AuthTexts =
    [
        "authentication failed", "ip is not allowed", "not allowed to connect", "permission to run", "api is disabled", "api has been disabled",
    ];

    public Settings Settings => settings;

    /// <summary>Una consulta v-list-* con formato json. Un texto de error de Hestia sale como <see cref="HestiaRefusal"/>.</summary>
    public async Task<JsonNode> List(string command, params string[] args)
    {
        var (status, text) = await Read(command, [.. args, "json"]).ConfigureAwait(false);
        if (status is >= 200 and < 300 && !IsErrorText(text))
        {
            return Parse(text) ?? throw Fail.Unavailable($"Hestia respondió algo que no es JSON a {command}.", "unreadable");
        }

        throw Refusal(status, text);
    }

    /// <summary>Una consulta que contesta texto, como v-search-domain-owner.</summary>
    public async Task<string> Text(string command, params string[] args)
    {
        var (status, text) = await Read(command, args).ConfigureAwait(false);
        return status is >= 200 and < 300 && !IsErrorText(text) ? text.Trim() : throw Refusal(status, text);
    }

    /// <summary>Si queda tiempo para mandar un mutador. Si no, la operación no cambió nada.</summary>
    public void EnsureRoom()
    {
        if (budget.Remaining < MutatorRoom)
        {
            throw new OutOfTime();
        }
    }

    /// <summary>
    /// Un comando que cambia algo. Solo el texto OK exacto es éxito, pero ni el OK ni el error alcanzan: después se vuelve
    /// a leer el estado. Si no se pudo conectar, el pedido no salió y se avisa como no disponible. Es rechazo solo un
    /// texto de error de Hestia con un código menor a 500: un 5xx o un cuerpo que no es de Hestia pueden venir de algo
    /// intermedio con el comando todavía corriendo, así que quedan como sin respuesta.
    /// </summary>
    public async Task<Ack> Mutate(string command, params string[] args)
    {
        try
        {
            var (status, text) = await Send(command, args, budget.Remaining - ReadRoom).ConfigureAwait(false);
            if (status is >= 200 and < 300 && text.Trim() == "OK")
            {
                return Ack.Done;
            }

            return status < 500 && IsErrorText(text) ? Ack.Refused(Describe(status, text)) : Ack.NoReply;
        }
        catch (Exception ex) when (ex is Cut or OutOfTime)
        {
            return Ack.NoReply;
        }
    }

    private async Task<(int Status, string Text)> Read(string command, string[] args)
    {
        (int Status, string Text) answer;
        try
        {
            answer = await Send(command, args, CallLimit).ConfigureAwait(false);
        }
        catch (Cut cut)
        {
            // Una lectura cortada se repite sin riesgo.
            throw Fail.Unavailable($"Hestia no respondió a {command} ({cut.Message}).", "no_response");
        }

        // Una lectura no imprime esos textos, así que son de la API. En un mutador pueden ser la salida del comando, por
        // ejemplo un "not allowed to connect" de la base: ahí cuentan solo el 401 y el 403 y lo demás lo decide la relectura.
        // Los textos se miran solo en un error, porque un JSON bueno puede traer esas palabras en un nombre de dominio.
        if (IsErrorText(answer.Text) && AuthTexts.Any(t => answer.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            throw AuthRejected(answer.Status, answer.Text);
        }

        return answer;
    }

    private async Task<(int Status, string Text)> Send(string command, string[] args, TimeSpan limit)
    {
        var remaining = budget.Remaining;
        if (remaining <= TimeSpan.Zero || limit <= TimeSpan.Zero)
        {
            throw new OutOfTime();
        }

        var byBudget = remaining <= limit;
        using var timeout = new CancellationTokenSource(byBudget ? remaining : limit);
        var form = new List<KeyValuePair<string, string>>
        {
            new("hash", $"{settings.AccessKeyId}:{settings.SecretKey}"),
            new("returncode", "no"),
            new("cmd", command),
        };
        form.AddRange(args.Select((value, i) => KeyValuePair.Create(string.Create(CultureInfo.InvariantCulture, $"arg{i + 1}"), value)));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl) { Content = new FormUrlEncodedContent(form) };

        int status;
        string text;
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;

            // El cuerpo se conserva aunque el HTTP no sea 2xx: Hestia manda ahí el texto del error.
            text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        {
            // Errores de la etapa de conexión: el pedido seguro no salió.
            logger.LogWarning("Hestia {Command}: sin conexión ({Error})", command, ex.HttpRequestError);
            if (ex.InnerException is AuthenticationException)
            {
                throw Fail.InvalidConfig(
                    ["tls_fingerprint"],
                    "El certificado del puerto de la API de Hestia no es de una CA confiable ni coincide con tls_fingerprint.");
            }

            throw Fail.Unavailable($"No se pudo conectar con Hestia ({ex.HttpRequestError}).", "no_connection");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Hestia {Command}: conexión cortada ({Error})", command, ex.HttpRequestError);
            throw new Cut("conexión cortada");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Hestia {Command}: sin respuesta a tiempo", command);
            throw byBudget ? new OutOfTime() : new Cut("sin respuesta a tiempo");
        }
        catch (OperationCanceledException)
        {
            // Venció el tiempo para conectar del handler: el pedido no salió.
            logger.LogWarning("Hestia {Command}: no conectó a tiempo", command);
            throw Fail.Unavailable("No se pudo conectar con Hestia a tiempo.", "no_connection");
        }

        logger.LogInformation("Hestia {Command}: HTTP {Status}", command, status);
        if (status is 401 or 403)
        {
            throw AuthRejected(status, text);
        }

        // Las redirecciones no se siguen: no se sabe si el comando corrió.
        if (status is >= 300 and < 400)
        {
            throw new Cut(string.Create(CultureInfo.InvariantCulture, $"redirección HTTP {status}"));
        }

        return (status, text);
    }

    private HestiaRefusal Refusal(int status, string text) => new(status, Describe(status, text), string.IsNullOrWhiteSpace(text));

    private ModuleException AuthRejected(int status, string text) => Fail.InvalidConfig(
        ["access_key_id", "secret_key"],
        "Hestia rechazó el pedido a la API. Revisar la access key y su perfil de comandos, y que la IP del módulo esté en la lista de IPs permitidas de la API.",
        Describe(status, text));

    private static bool IsErrorText(string text) => text.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>El texto de Hestia, corto y sin la secret key ni las contraseñas de la llamada.</summary>
    private string Describe(int status, string text)
    {
        foreach (var secret in hidden.Append(settings.SecretKey))
        {
            if (!string.IsNullOrEmpty(secret))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"HTTP {status} sin texto");
        }

        return text.Length <= 300 ? text : text[..300] + "...";
    }

    /// <summary>El pedido salió y no hubo respuesta.</summary>
    private sealed class Cut(string message) : Exception(message);
}
