using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BillingOss.App.Platform;

/// <summary>
/// Cifrado por campo con AES-256-GCM (D-09). Cada valor va atado a su tabla, columna y fila como dato asociado,
/// así un cifrado copiado a otra fila no se puede leer. Un secreto que viaja a un destino (los de los módulos) se
/// ata además a ese destino: cambiarlo exige volver a cargar el secreto, nadie lo manda a otro lado sin conocerlo.
/// Formato: versión de llave (4 bytes), nonce (12), tag (16) y texto cifrado. En v0 la llave sale de un archivo
/// fuera de la base y de sus backups, con una copia aparte en la bóveda del proveedor. Las versiones viejas se
/// conservan para leer lo cifrado con ellas.
/// </summary>
public sealed class SecretBox
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 4 + NonceSize + TagSize;

    private readonly Dictionary<int, byte[]> keys;
    private readonly int current;

    public SecretBox(IReadOnlyDictionary<int, byte[]> keys, int currentVersion)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.TryGetValue(currentVersion, out var k) || k.Length != 32)
        {
            throw new ArgumentException("Falta la llave vigente o no tiene 32 bytes.", nameof(keys));
        }

        this.keys = keys.ToDictionary();
        current = currentVersion;
    }

    /// <summary>
    /// Lee las llaves de un archivo con una línea por versión: "número:base64". La de número mayor es la vigente.
    /// </summary>
    public static SecretBox FromFile(string path)
    {
        var keys = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .Select(l => l.Split(':', 2))
            .ToDictionary(p => int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p => Convert.FromBase64String(p[1].Trim()));
        return new SecretBox(keys, keys.Keys.Max());
    }

    /// <summary>Una llave nueva de 32 bytes, en el formato del archivo.</summary>
    public static string NewKeyLine(int version) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{version}:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}");

    /// <param name="destination">Destino al que viaja el secreto, si viaja: queda en el dato asociado.</param>
    public byte[] Encrypt(string plaintext, string table, string column, long rowId, string? destination = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[HeaderSize + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(blob, current);
        var nonce = blob.AsSpan(4, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(keys[current], TagSize);
        aes.Encrypt(nonce, data, blob.AsSpan(HeaderSize), blob.AsSpan(4 + NonceSize, TagSize), Aad(table, column, rowId, current, destination));
        return blob;
    }

    public string Decrypt(byte[] blob, string table, string column, long rowId, string? destination = null)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < HeaderSize)
        {
            throw new CryptographicException("Cifrado truncado.");
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(blob);
        if (!keys.TryGetValue(version, out var key))
        {
            throw new CryptographicException($"No está la llave versión {version}.");
        }

        var plain = new byte[blob.Length - HeaderSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(blob.AsSpan(4, NonceSize), blob.AsSpan(HeaderSize), blob.AsSpan(4 + NonceSize, TagSize), plain, Aad(table, column, rowId, version, destination));
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Vuelve a cifrar con la llave vigente, para la rotación en segundo plano.</summary>
    public byte[] Rewrap(byte[] blob, string table, string column, long rowId, string? destination = null) =>
        Encrypt(Decrypt(blob, table, column, rowId, destination), table, column, rowId, destination);

    private static byte[] Aad(string table, string column, long rowId, int version, string? destination) =>
        Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{table}|{column}|{rowId}|{version}{(destination is null ? string.Empty : "|" + destination)}"));
}
