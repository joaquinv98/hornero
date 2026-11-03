using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BillingOss.Module.Stripe;

/// <summary>
/// Firma Stripe-Signature de los avisos de Stripe. El encabezado trae t (segundos Unix) y una o más firmas v1, cada
/// una el HMAC-SHA256 en hexadecimal, con el secreto del destino (whsec_...) como clave, del texto t, un punto y el
/// cuerpo crudo. Mientras se rota el secreto Stripe firma con los dos, así que alcanza con que una coincida. Los otros
/// esquemas (v0 y los de prueba) se ignoran.
/// </summary>
internal static class WebhookSignature
{
    /// <summary>La ventana que sugiere Stripe contra repeticiones, medida contra la hora en que el aviso llegó al núcleo.</summary>
    public static TimeSpan Tolerance { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifica la firma. Con <paramref name="receivedAt"/> la marca tiene que estar a 5 minutos de la recepción. Sin
    /// ella no hay ventana: el núcleo verifica en un trabajo que puede demorar, y lo protegen la unicidad por ID de
    /// evento y la consulta del pago.
    /// </summary>
    public static bool Verify(string secret, string? header, byte[] body, DateTimeOffset? receivedAt)
    {
        string? t = null;
        var signatures = new List<string>();
        foreach (var part in (header ?? string.Empty).Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair is ["t", var value])
            {
                t = value;
            }
            else if (pair is ["v1", var signature] && signature.Length > 0)
            {
                signatures.Add(signature.ToLowerInvariant());
            }
        }

        if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || signatures.Count == 0)
        {
            return false;
        }

        if (receivedAt is { } at && (at - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        var signed = new byte[t!.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(t, signed);
        signed[t.Length] = (byte)'.';
        body.CopyTo(signed, t.Length + 1);
        var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed)));
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s)));
    }
}
