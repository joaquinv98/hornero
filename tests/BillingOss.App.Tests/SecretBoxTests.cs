using System.Security.Cryptography;
using BillingOss.App.Platform;

namespace BillingOss.App.Tests;

[TestClass]
public sealed class SecretBoxTests
{
    private static SecretBox Box(params int[] versions) =>
        new(versions.ToDictionary(v => v, _ => RandomNumberGenerator.GetBytes(32)), versions.Max());

    [TestMethod]
    public void IdaYVuelta()
    {
        var box = Box(1);
        var blob = box.Encrypt("token-de-proxmox", "platform.module_instances", "secrets", 7);

        Assert.AreEqual("token-de-proxmox", box.Decrypt(blob, "platform.module_instances", "secrets", 7));
    }

    [TestMethod]
    public void UnCifradoCopiadoAOtraFilaNoSeLee()
    {
        var box = Box(1);
        var blob = box.Encrypt("secreto", "platform.module_instances", "secrets", 7);

        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => box.Decrypt(blob, "platform.module_instances", "secrets", 8));
        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => box.Decrypt(blob, "platform.module_instances", "state", 7));
    }

    [TestMethod]
    public void UnSecretoAtadoAUnDestinoNoSeLeeParaOtro()
    {
        var box = Box(1);
        var blob = box.Encrypt("firma", "platform.module_installations", "signing_secret", 7, "https://modulo.example.net/");

        Assert.AreEqual("firma", box.Decrypt(blob, "platform.module_installations", "signing_secret", 7, "https://modulo.example.net/"));
        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => box.Decrypt(blob, "platform.module_installations", "signing_secret", 7, "https://otro.example.net/"));
        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => box.Decrypt(blob, "platform.module_installations", "signing_secret", 7));
    }

    [TestMethod]
    public void RotacionLeeLoViejoYCifraConLaNueva()
    {
        var keys = new Dictionary<int, byte[]> { [1] = RandomNumberGenerator.GetBytes(32) };
        var old = new SecretBox(keys, 1).Encrypt("secreto", "t", "c", 1);
        keys[2] = RandomNumberGenerator.GetBytes(32);
        var box = new SecretBox(keys, 2);

        var rewrapped = box.Rewrap(old, "t", "c", 1);

        Assert.AreEqual(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(rewrapped));
        Assert.AreEqual("secreto", box.Decrypt(rewrapped, "t", "c", 1));
    }

    [TestMethod]
    public void ArchivoDeLlaves()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path, ["# llaves de prueba", SecretBox.NewKeyLine(1), SecretBox.NewKeyLine(2)]);
            var box = SecretBox.FromFile(path);

            Assert.AreEqual("x", box.Decrypt(box.Encrypt("x", "t", "c", 1), "t", "c", 1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
