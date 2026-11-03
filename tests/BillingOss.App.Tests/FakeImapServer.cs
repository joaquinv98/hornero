using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace BillingOss.App.Tests;

/// <summary>
/// Servidor IMAP de mentira en proceso, con lo justo para MailKit: LOGIN, LIST, EXAMINE y SELECT, UID FETCH de
/// tamaños, mensajes y encabezados, y LOGOUT. Una sola casilla (INBOX). Guarda los comandos que recibe para que la
/// prueba vea que nadie marca ni borra nada.
/// </summary>
internal sealed class FakeImapServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly Lock gate = new();
    private readonly List<(uint Uid, byte[] Raw)> messages = [];
    private uint nextUid = 1;

    public FakeImapServer(string user, string password)
    {
        User = user;
        Password = password;
        listener.Start();
        loop = Accept();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public string User { get; }

    public string Password { get; }

    public uint UidValidity { get; private set; } = 1;

    /// <summary>Los comandos recibidos, en mayúsculas y sin argumentos, salvo el de UID ("UID FETCH", "UID STORE").</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public uint Add(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var raw = new MemoryStream();
        message.WriteTo(raw);
        lock (gate)
        {
            messages.Add((nextUid, raw.ToArray()));
            return nextUid++;
        }
    }

    /// <summary>La casilla se rearma, como al migrar de servidor: UIDVALIDITY nuevo y UIDs desde 1.</summary>
    public void Renumber(uint validity)
    {
        lock (gate)
        {
            UidValidity = validity;
            nextUid = 1;
            for (var i = 0; i < messages.Count; i++)
            {
                messages[i] = (nextUid++, messages[i].Raw);
            }
        }
    }

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
        using var reader = new StreamReader(stream, Encoding.ASCII);
        async Task Send(string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"));

        try
        {
            await Send("* OK [CAPABILITY IMAP4rev1] IMAP de prueba listo");
            while (await reader.ReadLineAsync() is { } line)
            {
                var parts = line.Split(' ', 3);
                var tag = parts[0];
                var command = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
                var rest = parts.Length > 2 ? parts[2] : string.Empty;
                Commands.Enqueue(command == "UID" ? "UID " + rest.Split(' ')[0].ToUpperInvariant() : command);
                switch (command)
                {
                    case "CAPABILITY":
                        await Send("* CAPABILITY IMAP4rev1");
                        await Send($"{tag} OK CAPABILITY listo");
                        break;
                    case "LOGIN":
                        var args = await Arguments(rest, reader, Send);
                        await Send(args is [var user, var password] && user == User && password == Password
                            ? $"{tag} OK [CAPABILITY IMAP4rev1] Adentro"
                            : $"{tag} NO [AUTHENTICATIONFAILED] Usuario o clave incorrectos");
                        break;
                    case "LIST" or "LSUB":
                        await Send(rest.EndsWith("\"\"", StringComparison.Ordinal) ? $"* {command} (\\Noselect) \"/\" \"\"" : $"* {command} (\\HasNoChildren) \"/\" INBOX");
                        await Send($"{tag} OK {command} listo");
                        break;
                    case "EXAMINE" or "SELECT":
                        await Mailbox(tag, command, Send);
                        break;
                    case "UID" when rest.StartsWith("FETCH ", StringComparison.OrdinalIgnoreCase):
                        await Fetch(tag, rest[6..], stream, Send);
                        break;
                    case "LOGOUT":
                        await Send("* BYE Chau");
                        await Send($"{tag} OK LOGOUT listo");
                        return;
                    case "NOOP" or "STORE" or "EXPUNGE" or "UID" or "CLOSE" or "UNSELECT":
                        await Send($"{tag} OK {command} listo");
                        break;
                    default:
                        await Send($"{tag} BAD No entiendo {command}");
                        break;
                }
            }
        }
        catch (IOException)
        {
            // El cliente cortó.
        }
    }

    private async Task Mailbox(string tag, string command, Func<string, Task> send)
    {
        int count;
        uint validity;
        uint next;
        lock (gate)
        {
            (count, validity, next) = (messages.Count, UidValidity, nextUid);
        }

        await send($"* {count} EXISTS");
        await send("* 0 RECENT");
        await send($"* OK [UIDVALIDITY {validity}] UIDs válidos");
        await send($"* OK [UIDNEXT {next}] Próximo UID");
        await send("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
        await send("* OK [PERMANENTFLAGS ()] Sin marcas permanentes");
        await send($"{tag} OK [{(command == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] {command} listo");
    }

    private async Task Fetch(string tag, string rest, NetworkStream stream, Func<string, Task> send)
    {
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        var set = rest[..space];
        var items = rest[(space + 1)..].ToUpperInvariant();
        List<(int Seq, uint Uid, byte[] Raw)> selected;
        lock (gate)
        {
            var max = messages.Count == 0 ? 0u : messages[^1].Uid;
            selected = messages.Select((m, i) => (Seq: i + 1, m.Uid, m.Raw)).Where(m => InSet(set, m.Uid, max)).ToList();
        }

        foreach (var (seq, uid, raw) in selected)
        {
            if (items.Contains("BODY.PEEK[HEADER]", StringComparison.Ordinal))
            {
                var end = IndexOf(raw, "\r\n\r\n"u8);
                await Literal(stream, $"* {seq} FETCH (UID {uid} BODY[HEADER] ", end < 0 ? raw : raw[..(end + 4)]);
            }
            else if (items.Contains("BODY.PEEK[]", StringComparison.Ordinal))
            {
                await Literal(stream, $"* {seq} FETCH (UID {uid} BODY[] ", raw);
            }
            else
            {
                await send(string.Create(CultureInfo.InvariantCulture, $"* {seq} FETCH (UID {uid} RFC822.SIZE {raw.Length})"));
            }
        }

        await send($"{tag} OK FETCH listo");
    }

    private static async Task Literal(NetworkStream stream, string prefix, byte[] content)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{prefix}{{{content.Length}}}\r\n")));
        await stream.WriteAsync(content);
        await stream.WriteAsync(")\r\n"u8.ToArray());
    }

    /// <summary>"1:*", "4", "2:3,7" o "5:4294967295". En "n:*" con n mayor que el último, entra el último (RFC 3501).</summary>
    private static bool InSet(string set, uint uid, uint max) => set.Split(',').Any(part =>
    {
        var bounds = part.Split(':').Select(b => b == "*" ? max : uint.Parse(b, CultureInfo.InvariantCulture)).ToArray();
        var (low, high) = bounds.Length == 1 ? (bounds[0], bounds[0]) : (Math.Min(bounds[0], bounds[1]), Math.Max(bounds[0], bounds[1]));
        return uid >= low && uid <= high;
    });

    /// <summary>Átomos, cadenas entre comillas y literales {n}, que MailKit usa si la clave tiene caracteres raros.</summary>
    private static async Task<List<string>> Arguments(string text, StreamReader reader, Func<string, Task> send)
    {
        var args = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            switch (text[i])
            {
                case ' ':
                    i++;
                    break;
                case '"':
                    var value = new StringBuilder();
                    for (i++; i < text.Length && text[i] != '"'; i++)
                    {
                        value.Append(text[i] == '\\' ? text[++i] : text[i]);
                    }

                    args.Add(value.ToString());
                    i++;
                    break;
                case '{':
                    var spec = text[(i + 1)..text.IndexOf('}', i)];
                    if (!spec.EndsWith('+'))
                    {
                        await send("+ Seguí");
                    }

                    var buffer = new char[int.Parse(spec.TrimEnd('+'), CultureInfo.InvariantCulture)];
                    await reader.ReadBlockAsync(buffer);
                    args.Add(new string(buffer));
                    text = await reader.ReadLineAsync() ?? string.Empty;
                    i = 0;
                    break;
                default:
                    var end = text.IndexOf(' ', i);
                    args.Add(end < 0 ? text[i..] : text[i..end]);
                    i = end < 0 ? text.Length : end;
                    break;
            }
        }

        return args;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
}
