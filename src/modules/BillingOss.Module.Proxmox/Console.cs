using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Proxmox;

/// <summary>
/// Contrato del gateway de consola noVNC, sin cambios para no redesplegarlo: un grant firmado con HMAC-SHA256 que
/// vale 60 segundos, y una revocación por servicio antes de suspender, dar de baja o reinstalar.
/// </summary>
internal static class ConsoleGateway
{
    public const string Audience = "proxmox-console";
    public const string RevokeAudience = "proxmox-console-revoke";
    public const int Lifetime = 60;

    /// <summary>
    /// base64url del JSON, un punto y base64url del HMAC-SHA256 de ese segmento. La clave es el texto literal del
    /// secreto en UTF-8, no su decodificación.
    /// </summary>
    public static string Grant(string secret, string server, long serviceId, string? node, int? vmid, long iat)
    {
        var payload = new JsonObject
        {
            ["v"] = 1,
            ["aud"] = node is null ? RevokeAudience : Audience,
            ["server"] = server,
            ["sub"] = serviceId,
        };
        if (node is not null)
        {
            payload["node"] = node;
            payload["vmid"] = vmid;
        }

        payload["iat"] = iat;
        payload["exp"] = iat + Lifetime;
        payload["jti"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var segment = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(segment));
        return segment + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>
    /// Cierra las consolas abiertas del servicio. Sin gateway configurado no hay nada que revocar. Si está configurado y
    /// no confirma, la operación falla antes de tocar la VM: una consola abierta de un servicio suspendido es un acceso
    /// que no tiene que existir.
    /// </summary>
    public static async Task Revoke(HttpClient gateway, InstanceConfig config, long serviceId, TimeProvider time, ILogger logger)
    {
        if (config.ConsoleOrigin is not { } origin)
        {
            return;
        }

        var grant = Grant(config.ConsoleSecret!, config.ConsoleServerKey, serviceId, null, null, time.GetUtcNow().ToUnixTimeSeconds());
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            using var response = await gateway.PostAsJsonAsync(new Uri(origin, "/api/revoke"), new { grant }, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            logger.LogWarning("El gateway de consola respondió HTTP {Status} a la revocación", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("El gateway de consola no respondió a la revocación: {Type}", ex.GetType().Name);
        }

        throw Errors.Unavailable("No se pudo revocar la consola del servicio. No se tocó la VM.", 15);
    }
}
