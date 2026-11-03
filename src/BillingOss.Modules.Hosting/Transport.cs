using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace BillingOss.Modules.Hosting;

/// <summary>Los clientes HTTP hacia los paneles, uno por huella de certificado.</summary>
public static class PanelHttp
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    /// <summary>
    /// Con la huella fija en la validación, una conexión validada con una huella nunca la usa otra configuración. Es un
    /// caché de conexiones: no guarda nada de los pedidos.
    /// </summary>
    public static HttpClient For(string? fingerprint) => Clients.GetOrAdd(fingerprint ?? string.Empty, Build);

    /// <summary>
    /// TLS siempre validado: con la CA del sistema, o con la huella SHA-256 del certificado si la instancia la fija. No hay
    /// opción para desactivarlo.
    /// </summary>
    public static HttpClient Build(string fingerprint) => new(new SocketsHttpHandler
    {
        // Sin redirecciones: un 30x mandaría las credenciales del panel a otro destino.
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
public sealed class Budget(TimeProvider time, TimeSpan total)
{
    private readonly long started = time.GetTimestamp();

    public TimeSpan Remaining => total - time.GetElapsedTime(started);
}

/// <summary>Se terminó el tiempo de la operación. Cada operación decide si eso es pending, incierto o no disponible.</summary>
public sealed class OutOfTimeException() : Exception("Se terminó el tiempo de la operación.");

/// <summary>Lo que respondió el panel. El cuerpo se conserva aunque el HTTP no sea 2xx: ahí suele venir el texto del error.</summary>
public sealed record PanelReply(int Status, string Body);

/// <summary>
/// El pedido a un panel con el plazo de la operación. Distingue lo que no salió (no hay conexión: no disponible), lo que
/// salió y no tuvo respuesta (incierto en un cambio) y lo que se quedó sin tiempo. Los logs llevan solo el nombre de la
/// función del panel y el código HTTP, nunca los argumentos ni el cuerpo.
/// </summary>
public sealed class PanelTransport(HttpClient http, Budget budget, ILogger logger, string panel)
{
    /// <summary>Plazo de cada lectura.</summary>
    public static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(30);

    /// <summary>Lo mínimo que tiene que quedar para mandar un cambio.</summary>
    public static readonly TimeSpan MutatorRoom = TimeSpan.FromSeconds(20);

    /// <summary>Lo que se reserva para releer después de un cambio. El cambio tiene todo lo demás, porque una baja grande tarda.</summary>
    public static readonly TimeSpan ReadRoom = TimeSpan.FromSeconds(10);

    /// <summary>Una lectura. Un corte se puede repetir sin riesgo: sale como no disponible.</summary>
    public async Task<PanelReply> Read(string function, Func<HttpRequestMessage> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await Send(function, request, ReadLimit).ConfigureAwait(false);
        }
        catch (CutException cut)
        {
            throw Fail.Unavailable($"{panel} no respondió a {function} ({cut.Message}).", "no_response");
        }
    }

    /// <summary>Un cambio. Null si salió y no hubo respuesta: el panel pudo haberlo aplicado.</summary>
    public async Task<PanelReply?> Mutate(string function, Func<HttpRequestMessage> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRoom();
        try
        {
            return await Send(function, request, budget.Remaining - ReadRoom).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CutException or OutOfTimeException)
        {
            return null;
        }
    }

    /// <summary>Si queda tiempo para mandar un cambio. Si no, la operación no cambió nada.</summary>
    public void EnsureRoom()
    {
        if (budget.Remaining < MutatorRoom)
        {
            throw new OutOfTimeException();
        }
    }

    private async Task<PanelReply> Send(string function, Func<HttpRequestMessage> build, TimeSpan limit)
    {
        var remaining = budget.Remaining;
        if (remaining <= TimeSpan.Zero || limit <= TimeSpan.Zero)
        {
            throw new OutOfTimeException();
        }

        var byBudget = remaining <= limit;
        using var timeout = new CancellationTokenSource(byBudget ? remaining : limit);
        using var request = build();
        int status;
        string body;
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        {
            // Errores de la etapa de conexión: el pedido seguro no salió.
            logger.LogWarning("{Panel} {Function}: sin conexión ({Error})", panel, function, ex.HttpRequestError);
            if (ex.InnerException is AuthenticationException)
            {
                throw Fail.InvalidConfig(
                    ["tls_fingerprint"],
                    $"El certificado de {panel} no es de una CA confiable ni coincide con tls_fingerprint.");
            }

            throw Fail.Unavailable($"No se pudo conectar con {panel} ({ex.HttpRequestError}).", "no_connection");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{Panel} {Function}: conexión cortada ({Error})", panel, function, ex.HttpRequestError);
            throw new CutException("conexión cortada");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("{Panel} {Function}: sin respuesta a tiempo", panel, function);
            throw byBudget ? new OutOfTimeException() : new CutException("sin respuesta a tiempo");
        }
        catch (OperationCanceledException)
        {
            // Venció el tiempo para conectar del handler: el pedido no salió.
            logger.LogWarning("{Panel} {Function}: no conectó a tiempo", panel, function);
            throw Fail.Unavailable($"No se pudo conectar con {panel} a tiempo.", "no_connection");
        }

        logger.LogInformation("{Panel} {Function}: HTTP {Status}", panel, function, status);

        // Las redirecciones no se siguen: no se sabe si la función corrió.
        if (status is >= 300 and < 400)
        {
            throw new CutException(string.Create(CultureInfo.InvariantCulture, $"redirección HTTP {status}"));
        }

        return new PanelReply(status, body);
    }

    /// <summary>El pedido salió y no hubo respuesta.</summary>
    private sealed class CutException(string message) : Exception(message);
}
