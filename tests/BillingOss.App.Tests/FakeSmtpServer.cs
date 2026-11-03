using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace BillingOss.App.Tests;

/// <summary>Servidor SMTP de mentira en proceso: guarda lo que recibe y falla a pedido.</summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public FakeSmtpServer()
    {
        listener.Start();
        loop = Accept();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public ConcurrentQueue<MimeMessage> Messages { get; } = new();

    /// <summary>Usuarios que se autenticaron.</summary>
    public ConcurrentQueue<string> Users { get; } = new();

    /// <summary>Contesta 421 al saludo, como un servidor caído o saturado.</summary>
    public bool Down { get; set; }

    /// <summary>Rechaza para siempre (550) las casillas que contienen este texto.</summary>
    public string? Reject { get; set; }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await loop;
        stop.Dispose();
    }

    private async Task Accept()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        if (Down)
        {
            await writer.WriteLineAsync("421 4.3.2 Servicio no disponible");
            return;
        }

        await writer.WriteLineAsync("220 smtp.example.com ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            var parts = line.Split(' ');
            switch (parts[0].ToUpperInvariant())
            {
                case "EHLO":
                    await writer.WriteAsync("250-smtp.example.com\r\n250 AUTH PLAIN\r\n");
                    break;
                case "AUTH":
                    var response = parts.Length > 2 ? parts[2] : await Challenge(reader, writer);
                    Users.Enqueue(Encoding.UTF8.GetString(Convert.FromBase64String(response)).Split('\0')[1]);
                    await writer.WriteLineAsync("235 2.7.0 Autenticado");
                    break;
                case "RCPT":
                    await writer.WriteLineAsync(Reject is not null && line.Contains(Reject, StringComparison.OrdinalIgnoreCase) ? "550 5.1.1 No existe la casilla" : "250 2.1.5 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 Mandá el mensaje");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } l && l != ".")
                    {
                        data.Append(l.StartsWith("..", StringComparison.Ordinal) ? l[1..] : l).Append("\r\n");
                    }

                    using (var raw = new MemoryStream(Encoding.UTF8.GetBytes(data.ToString())))
                    {
                        Messages.Enqueue(await MimeMessage.LoadAsync(raw));
                    }

                    await writer.WriteLineAsync("250 2.0.0 Recibido");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 2.0.0 Chau");
                    return;
                default:
                    await writer.WriteLineAsync("250 2.0.0 OK");
                    break;
            }
        }
    }

    private static async Task<string> Challenge(StreamReader reader, StreamWriter writer)
    {
        await writer.WriteLineAsync("334 ");
        return await reader.ReadLineAsync() ?? string.Empty;
    }
}
