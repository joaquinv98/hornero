using System.Globalization;
using System.Text.Json;
using BillingOss.Updater;
using BillingOss.Updates;
using Npgsql;

// billing-oss-actualizador: el servicio (run) y la consola con el mismo motor (sección 7.3 de D-20).
const string Usage = """
    Uso: billing-oss-actualizador [--config ARCHIVO] COMANDO
      run                                 Servicio de systemd: una vuelta cada TickInterval.
      check [--file CANAL --signature SIG] Baja y verifica el canal ahora, o carga uno bajado a mano.
      status                              Versión, canal, nodos y pedido en curso.
      plan X.Y.Z                          Camino desde la versión instalada.
      apply X.Y.Z                         Deja el pedido y sigue su avance. Puede volver por debajo de un parche de seguridad.
      block X.Y.Z | unblock X.Y.Z         Bloquea o libera una versión en esta instalación.
    Configuración por defecto: /etc/billing-oss/actualizador.json.
    """;

var argv = args.ToList();
var configFile = "/etc/billing-oss/actualizador.json";
if (argv.IndexOf("--config") is var c and >= 0 && c + 1 < argv.Count)
{
    configFile = argv[c + 1];
    argv.RemoveRange(c, 2);
}

if (argv.Count == 0 || argv[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return argv.Count == 0 ? 2 : 0;
}

void Log(string text) => Console.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {text}");

try
{
    var options = UpdaterOptions.Load(configFile);
    await using var db = NpgsqlDataSource.Create(options.ResolveConnectionString());
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var host = new ComposeHost(options, ComposeHost.Processes(options.ComposeDirectory, TimeSpan.FromMinutes(30)));
    var engine = new UpdaterEngine(options, db, host, http, TimeProvider.System, Log);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
    var ct = stop.Token;

    switch (argv[0])
    {
        case "run":
            Log($"Actualizador {UpdaterEngine.Own.Text()} en el nodo {options.Node}.");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (await engine.Tick(ct))
                    {
                        Log("Binario nuevo: systemd arranca el actualizador otra vez.");
                        return 0;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return 0;
                }
                catch (Exception ex)
                {
                    // Sin base, con un paso que falló afuera del motor o con un dato raro del canal: el servicio sigue y
                    // se reintenta en la próxima vuelta. Caerse acá reiniciaría al actualizador en todos los nodos.
                    Log("Vuelta con error: " + ex.Message);
                }

                await Task.Delay(options.TickInterval, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            return 0;

        case "check":
            var file = Option(argv, "--file");
            var problem = file is null
                ? await engine.Check(null, null, ct)
                : await engine.Check(await File.ReadAllBytesAsync(file, ct), await File.ReadAllTextAsync(Option(argv, "--signature") ?? file + ".sig", ct), ct);
            Console.WriteLine(problem ?? "Canal verificado.");
            return problem is null ? 0 : 1;

        case "status":
            var state = await engine.State(ct);
            var channel = UpdaterEngine.Channel(state);
            Console.WriteLine($"Instalación: {state.Version ?? "sin versión registrada"}. Canal: {(channel is null ? "sin verificar" : $"{channel.Name}, serial {state.Serial}, vence {channel.Expires:yyyy-MM-dd HH:mm}Z")}.");
            Console.WriteLine($"Último chequeo: {state.CheckedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "nunca"}. Bloqueadas: {(state.Blocked.Length == 0 ? "ninguna" : string.Join(", ", state.Blocked))}.");
            foreach (var node in (await engine.Nodes(ct)).Values)
            {
                Console.WriteLine($"  {node.Name}: {node.Version ?? "?"} {(node.Fresh ? "encendido" : "sin señales")} {node.Status} {node.Detail}");
            }

            if (await engine.Active(ct) is { } active)
            {
                Console.WriteLine($"Pedido {active.Id}: {active.Version}, {active.Status}, {active.Phase ?? "-"}, paso {active.Step}.");
            }

            return 0;

        case "plan" when argv.Count == 2:
            var plan = await engine.Preview(argv[1], "console", ct);
            Console.WriteLine($"{string.Join(" > ", plan.Path)} en {string.Join(", ", plan.Nodes)}{(plan.Back ? ", vuelta atrás del binario, sin migraciones" : string.Empty)}.");
            return 0;

        case "apply" when argv.Count == 2:
            _ = await engine.Preview(argv[1], "console", ct);
            var id = await engine.RequestFromConsole(argv[1], $"console:{Environment.UserName}@{options.Node}", ct);
            Console.WriteLine($"Pedido {id}. El servicio lo aplica; acá se ve el avance.");
            var shown = 0;
            while (true)
            {
                await using var cmd = db.CreateCommand("SELECT status, progress::text, error FROM updates.requests WHERE id = $1");
                cmd.Parameters.Add(new NpgsqlParameter { Value = id });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                var status = r.GetString(0);
                var progress = JsonSerializer.Deserialize<JsonElement[]>(r.GetString(1)) ?? [];
                foreach (var step in progress.Skip(shown))
                {
                    Console.WriteLine("  " + step.GetRawText());
                }

                shown = progress.Length;
                if (status is "done" or "failed" or "rejected")
                {
                    Console.WriteLine(status + (r.IsDBNull(2) ? string.Empty : ": " + r.GetString(2)));
                    return status == "done" ? 0 : 1;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

        case "block" or "unblock" when argv.Count == 2:
            var version = Versions.Parse(argv[1]).Text();
            await using (var cmd = db.CreateCommand(argv[0] == "block"
                ? "UPDATE updates.state SET blocked_versions = ARRAY(SELECT DISTINCT unnest(blocked_versions || ARRAY[$1]) ORDER BY 1) WHERE id = 1"
                : "UPDATE updates.state SET blocked_versions = array_remove(blocked_versions, $1) WHERE id = 1"))
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = version });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            Console.WriteLine($"{version}: {(argv[0] == "block" ? "bloqueada" : "liberada")}.");
            return 0;

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (UpdateException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string? Option(List<string> argv, string name) => argv.IndexOf(name) is var i and >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
