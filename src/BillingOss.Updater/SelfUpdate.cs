using System.Net;
using System.Security.Cryptography;
using BillingOss.Updates;

namespace BillingOss.Updater;

/// <summary>
/// El actualizador se actualiza antes que nada (sección 7.3), con el binario del campo updater del canal verificado: lo
/// baja, compara el SHA-256, guarda el anterior y lo reemplaza. Al salir, systemd lo arranca de nuevo, y si el nuevo no
/// arranca, la unidad de OnFailure= vuelve al anterior (deploy/actualizador).
/// </summary>
public static class SelfUpdate
{
    /// <summary>Tope de lo que se baja: el ejecutable autocontenido pesa unas decenas de MB.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    public static bool IsNewer(UpdaterBinary? binary, Version own) =>
        binary is not null && Versions.TryParse(binary.Version, out var offered) && offered > own;

    /// <returns>Si reemplazó el binario y hay que reiniciar.</returns>
    public static async Task<bool> Apply(UpdaterBinary binary, string binaryPath, HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(http);
        if (binary.Url.Scheme != Uri.UriSchemeHttps && !(binary.Url.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(binary.Url.Host, out var ip) && IPAddress.IsLoopback(ip)))
        {
            throw new UpdateException("El binario del actualizador se baja solo por HTTPS.");
        }

        var expected = binary.Sha256.Trim().ToLowerInvariant();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
        {
            throw new UpdateException("El canal trae un SHA-256 inválido para el actualizador.");
        }

        var next = binaryPath + ".nuevo";
        try
        {
            using (var response = await http.GetAsync(binary.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes)
                {
                    throw new UpdateException("El binario del actualizador es demasiado grande.");
                }

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = PrivateFile.Create(next, executable: true);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        throw new UpdateException("El binario del actualizador es demasiado grande.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            await using (var check = File.OpenRead(next))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
                if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected)))
                {
                    throw new UpdateException($"El binario del actualizador {binary.Version} no coincide con el SHA-256 del canal.");
                }
            }

            // El anterior queda al lado: es el que restaura la unidad de vuelta atrás.
            File.Copy(binaryPath, binaryPath + ".anterior", overwrite: true);
            File.Move(next, binaryPath, overwrite: true);
            return true;
        }
        finally
        {
            File.Delete(next);
        }
    }
}
