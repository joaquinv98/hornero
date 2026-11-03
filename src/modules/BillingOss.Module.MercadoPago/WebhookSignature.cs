using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BillingOss.Module.MercadoPago;

/// <summary>
/// Firma x-signature de los webhooks de Mercado Pago. El encabezado trae ts y v1. v1 es el HMAC-SHA256 en
/// hexadecimal, con la clave secreta de la aplicación, de un texto armado con el data.id de la URL, el encabezado
/// x-request-id y ts, cada uno terminado en punto y coma. Lo que no llegó se quita del texto. La firma no cubre el
/// cuerpo, así que de ahí no se puede confiar en nada que no coincida con lo firmado.
/// </summary>
internal static class WebhookSignature
{
    /// <summary>
    /// Verifica la firma y devuelve el ts firmado. La ventana de tiempo va aparte, en <see cref="IsFresh"/>, contra
    /// cuándo llegó el aviso al núcleo: la verificación corre después de recibir, con trabajos que pueden demorar.
    /// </summary>
    public static bool TryVerify(string secret, string? signature, string? requestId, string? dataId, out string ts)
    {
        string? signedTs = null;
        string? v1 = null;
        foreach (var part in (signature ?? string.Empty).Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair is [var name, var value])
            {
                if (name == "ts")
                {
                    signedTs = value;
                }
                else if (name == "v1")
                {
                    v1 = value;
                }
            }
        }

        ts = signedTs ?? string.Empty;
        if (string.IsNullOrEmpty(signedTs) || string.IsNullOrEmpty(v1))
        {
            return false;
        }

        // Si el id es alfanumérico va en minúsculas, como pide Mercado Pago.
        var text = new StringBuilder();
        if (!string.IsNullOrEmpty(dataId))
        {
            text.Append("id:").Append(dataId.ToLowerInvariant()).Append(';');
        }

        if (!string.IsNullOrEmpty(requestId))
        {
            text.Append("request-id:").Append(requestId).Append(';');
        }

        text.Append("ts:").Append(signedTs).Append(';');

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text.ToString())));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(v1.ToLowerInvariant()));
    }

    /// <summary>El ts firmado no es anterior a <paramref name="receivedAt"/> en más de <paramref name="maxAge"/>.</summary>
    public static bool IsFresh(string ts, DateTimeOffset receivedAt, TimeSpan maxAge)
    {
        // Mercado Pago documenta el ts en segundos Unix y en la práctica también llega en milisegundos.
        if (!long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 253_402_300_799_999)
        {
            return false;
        }

        var signedAt = value >= 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
        return receivedAt - signedAt <= maxAge;
    }
}
