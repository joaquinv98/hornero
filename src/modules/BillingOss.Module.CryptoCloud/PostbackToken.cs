using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BillingOss.Module.CryptoCloud;

/// <summary>
/// Firma de los avisos de pago (postback) de CryptoCloud: el campo token es un JWT HS256 firmado con la secret_key
/// del proyecto. Las claims no están documentadas y el ejemplo oficial solo trae un id numérico y exp, así que la
/// firma prueba que el aviso salió de CryptoCloud pero no cubre la factura del cuerpo.
/// </summary>
internal static class PostbackToken
{
    /// <summary>
    /// Cuánto después de exp todavía vale un token. CryptoCloud lo vence a los 5 minutos, pero el núcleo verifica
    /// después de recibir, con un trabajo que puede demorar. Siete días alcanzan para los reintentos y acotan lo
    /// que sirve un token que se filtre.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromDays(7);

    /// <summary>
    /// Verifica el token y devuelve su firma, o null si no vale. Solo acepta HS256, así un token con alg none u
    /// otro algoritmo no pasa. Un exp más viejo que la tolerancia no vale. Sin exp, alcanza con la firma.
    /// </summary>
    public static byte[]? Verify(string secretKey, string? token, DateTimeOffset now)
    {
        if (token?.Split('.') is not [var header, var payload, var signature] || header.Length == 0 || payload.Length == 0)
        {
            return null;
        }

        try
        {
            if (Part(header).Field("alg").Text() != "HS256")
            {
                return null;
            }

            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes($"{header}.{payload}"));
            var actual = Base64Url.DecodeFromChars(signature);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return null;
            }

            // Las claims se leen recién con la firma comprobada.
            return Part(payload).Field("exp").Dec() is { } exp && exp < (now - Tolerance).ToUnixTimeSeconds() ? null : actual;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Una parte del JWT como JSON. Se pasa por texto para que bytes que no son UTF-8 se reemplacen en vez de tirar.</summary>
    private static JsonNode? Part(string part) => JsonNode.Parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(part)));
}
